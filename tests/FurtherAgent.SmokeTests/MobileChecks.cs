using System.Text.Json.Nodes;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class MobileChecks
{
    public static async Task RunAsync(RegistryClient registry, string url, Action<bool, string> check)
    {
        var adapter = new Adapter();
        string filesRoot = Path.Combine(Path.GetTempPath(), "fa-mobile-files-" + Guid.NewGuid().ToString("N"));
        await using var host = new DeviceHost(registry, () => adapter, filesRoot);
        await host.StartAsync("Android controller test");
        await using var mobile = new MobileSession(a => a());
        await mobile.ConnectAsync(url, host.Lease!.Code);
        check(mobile.Connected && mobile.Sessions.Count == 2, "mobile pairs and lists sessions through real relay");
        await mobile.SelectAsync("one");
        check(mobile.View?.Id == "one", "mobile opens remote session");
        await mobile.GetSettingsAsync();
        check(mobile.Settings?.ReasoningEffort == "high", "composer gets actual remote model and reasoning settings");
        await mobile.ConfigureAsync("other", "fixture", "default", "low");
        check(mobile.Settings is { Model: "other", ReasoningEffort: "low" }, "composer settings refresh after acknowledged configuration");
        check(mobile.View!.Messages.Any(m => m.Role == "工具" && m.Detail == "fixture detail"), "v2 negotiation transports operation details through the real relay");
        await mobile.ResumeAsync();
        check(mobile.Connected && mobile.View?.Id == "one" && adapter.Sends == 0,
            "foreground resume retains selected session without submitting a message");
        var running = mobile.View! with { Prompts = [] };
        check(AgentIndicator.From(false, false, running) is { Label: "已断连", Color: "#DC2626" }
            && AgentIndicator.From(false, true, running).Label == "连接中", "connection state overrides stale executing snapshot");
        check(AgentIndicator.From(true, false, running).Label == "执行中"
            && AgentIndicator.From(true, false, running with { Status = "idle" }).Label == "空闲"
            && AgentIndicator.From(true, false, mobile.View).Label == "等待确认"
            && AgentIndicator.From(true, false, running with { Status = "unknown" }).Label == "状态未知",
            "both clients distinguish executing, idle, approval and unknown agent states");
        using (var files = new FileTransferStore(filesRoot))
        {
            string output = Path.Combine(files.List("one").Outbox, "new-output.txt");
            await File.WriteAllTextAsync(output, "created after attach");
            var listed = await mobile.ListOutputFilesAsync();
            check(listed.Any(f => f.Name == "new-output.txt") && mobile.View!.OutputFiles!.Count == 1,
                "mobile refreshes files created after attach without a conversation update");
            File.Delete(output);
            check((await mobile.ListOutputFilesAsync()).Count == 0, "mobile file refresh removes stale outputs");
        }
        await mobile.SendAsync("手机中文 🚀", []);
        for (int i = 0; i < 50 && mobile.View?.Messages.Any(m => m.Text.StartsWith("手机中文 🚀", StringComparison.Ordinal)) != true; i++) await Task.Delay(100);
        check(adapter.Sends == 1 && mobile.View?.Messages.Any(m => m.Text.StartsWith("手机中文 🚀", StringComparison.Ordinal)) == true, "mobile receives real relay incremental update after send");
        adapter.Emit("unselected", "must not appear"); await Task.Delay(600);
        check(mobile.View?.Id == "one" && mobile.View.Messages.All(m => m.Text != "must not appear"), "mobile ignores updates from unselected session");
        bool rejected = false;
        try { await mobile.SelectAsync("locked"); } catch { rejected = true; }
        check(rejected && mobile.View?.Id == "one", "rejected mobile switch preserves previous selection");
        check(mobile.Settings?.Model == "other", "rejected session switch preserves current model settings");
        adapter.FailAfterAccept = true;
        try { await mobile.SendAsync("accepted but lost response", []); } catch { }
        check(mobile.SendUncertain && adapter.Sends == 2, "mobile retains uncertain send state");
        await mobile.ResumeAsync();
        check(mobile.SendUncertain && adapter.Sends == 2, "foreground resume never replays an uncertain submission");
        var transport = (RemoteDevice)typeof(MobileSession).GetField("remote", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mobile)!;
        int attachedBeforeDisconnect = adapter.Attaches;
        await transport.Peer.DisposeAsync();
        await mobile.ResumeAsync();
        for (int i = 0; i < 100 && (!mobile.Connected || adapter.Attaches == attachedBeforeDisconnect); i++) await Task.Delay(100);
        check(mobile.Connected && mobile.View?.Id == "one" && adapter.Attaches > attachedBeforeDisconnect
            && mobile.SendUncertain && adapter.Sends == 2,
            "foreground resume recovers a broken relay connection and original session without replaying uncertain send");
        await mobile.RefreshAsync();
        try { await mobile.SendAsync("no replay", []); } catch { }
        check(adapter.Sends == 2 && mobile.SendUncertain, "refresh does not replay and uncertain send blocks duplicate submit");
        mobile.AcknowledgeUncertain(); adapter.FailAfterAccept = false;
        await mobile.SendAsync("explicit next action", []);
        check(adapter.Sends == 3, "mobile only resumes submission after explicit acknowledgement");
        await mobile.StopAsync(); check(adapter.Stops == 1, "mobile stops controllable run");
        await mobile.RespondAsync("approve", "decline"); check(adapter.Decisions == 1, "mobile forwards exact approval decision");
        bool invalidPrompt = false;
        try { await mobile.RespondAsync("stale", "accept"); } catch { invalidPrompt = true; }
        check(invalidPrompt && adapter.Decisions == 1, "mobile rejects stale approval ID");
        await mobile.CreateAsync(); check(mobile.View?.Id == "created", "mobile creates and attaches supported session");
        check(mobile.Settings is null, "new session never inherits prior session reasoning label");
        await mobile.ArchiveAsync(); check(mobile.View is null && adapter.Archives == 1, "mobile archives without deleting history");
        await mobile.DisconnectAsync();
        check(!mobile.Connected && mobile.View is null, "mobile explicit disconnect clears active view");
        await mobile.ResumeAsync();
        check(!mobile.HasConnection && !mobile.Connected, "foreground resume respects explicit disconnect");
        string html = Transcript.Render(Transcript.Preview with { Messages = [new("assistant", "<script>alert(1)</script>\n\n[bad](javascript:alert(1)) [copy](fa-copy://0) [good](https://example.com)\n\n![remote](https://example.com/tracker.png)\n\n<javascript:alert(1)>\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```cs\nConsole.WriteLine(1);\n```")] });
        check(!html.Contains("<script>") && !html.Contains("href=\"javascript:") && !html.Contains("href=\"fa-copy://0\">copy") && !html.Contains("<img"), "mobile Markdown blocks HTML, executable links, injected copy links and remote images");
        check(html.Contains("<table>") && html.Contains("<pre><code") && html.Contains("href=\"https://example.com\""), "mobile Markdown retains tables, code and safe external links");
        string demo = Transcript.Render(Transcript.Preview);
        string attachment = Transcript.Render(Transcript.Preview with { Messages = [new("assistant", "[package](attachment:app.apk)")] });
        check(attachment.Contains("href=\"attachment:app.apk\""), "mobile attachment links reach native download handler");
        var fileCard = Transcript.Render(Transcript.Preview with { OutputFiles = [new("outbox/资料 #1.txt", "资料 #1.txt", 4096)] });
        check(fileCard.Contains("会话文件") && fileCard.Contains("attachment:" + Uri.EscapeDataString("资料 #1.txt")), "mobile renders downloadable file cards with encoded Unicode filenames");
        check(demo.Contains("离线界面预览") && demo.Contains("class='operations'"), "mobile preview retains conversation and groups tool records");
        Directory.Delete(filesRoot, true);
    }
    private sealed class Adapter : IAgentAdapter
    {
        private string selected = "one";
        private string last = "initial";
        public int Sends, Stops, Decisions, Archives, Attaches;
        public bool FailAfterAccept;
        public AgentInfo Info => new("mobile-test", "Mobile test", true, "isolated fixture", true, true, CanArchive: true);
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("one", "One", ""), new("locked", "Locked", "")]);
        private SessionView View(string id, string text) => new(id, id, "", "active", "run", [new("工具", "fixture tool", "fixture detail"), new("assistant", text)], [new("approve", "hermes/approval", "Test", "", null)], CanStop: true);
        public void Emit(string id, string text) => Changed?.Invoke(View(id, text));
        public Task<SessionView> AttachAsync(string id, CancellationToken ct)
        { if (id == "locked") throw new IOException("running elsewhere"); Attaches++; selected = id; return Task.FromResult(View(id, last)); }
        public Task SendAsync(string text, CancellationToken ct)
        { Sends++; last = text; Emit(selected, text); if (FailAfterAccept) throw new IOException("simulated lost acknowledgement"); return Task.CompletedTask; }
        public Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct) => Task.FromResult(new SessionInfo("created", title, ""));
        public Task InterruptAsync(CancellationToken ct) { Stops++; return Task.CompletedTask; }
        public Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct) { Decisions++; return Task.CompletedTask; }
        public Task ArchiveAsync(string id, bool archived, CancellationToken ct) { Archives++; return Task.CompletedTask; }
        public Task<AgentSettings> SettingsAsync(CancellationToken ct) => Task.FromResult(new AgentSettings("fixture", "fixture", "default", [], true, false, "", "high", true));
        public Task ConfigureAsync(string model, string provider, string mode, string? reasoningEffort, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
