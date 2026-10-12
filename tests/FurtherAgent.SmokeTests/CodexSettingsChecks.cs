using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexSettingsChecks
{
    private sealed class Metadata(params string[] pages) : ICodexMetadata
    {
        internal int Calls;
        public Task<JsonNode> CallAsync(string method, object args, CancellationToken ct)
        {
            if (method != "model/list") throw new Exception("unexpected mutation");
            return Task.FromResult(JsonNode.Parse(pages[Calls++])!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal static async Task Run(Action<bool, string> check)
    {
        await using var metadata = new Metadata(
            """{"data":[{"id":"alias","model":"live-model","displayName":"Live","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"ultra"}],"defaultReasoningEffort":"low"},{"model":"hidden","hidden":true}],"nextCursor":"page2"}""",
            """{"data":[{"model":"live-model"},{"model":"second","supportedReasoningEfforts":[{"reasoningEffort":"medium"}]}],"nextCursor":null}""");
        var models = await CodexSettingsCatalog.ModelsAsync(metadata, "openai", default);
        check(metadata.Calls == 2 && models.Count == 2 && models[0].Id == "live-model", "remote catalog follows pagination, ignores hidden models and deduplicates by usable model slug");
        check(models[0].ReasoningEfforts!.SequenceEqual(new[] { "low", "ultra" }), "reasoning choices come from model metadata without a hardcoded enum");
        check(CodexSettingsCatalog.SelectEffort(models[0], null, "ultra") == "ultra", "old clients preserve a supported current reasoning effort");
        check(CodexSettingsCatalog.SelectEffort(models[0], null, "high") == "low", "changing model replaces unsupported old effort with new model default");
        bool rejected = false;
        try { CodexSettingsCatalog.SelectEffort(models[0], "high", "low"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "unsupported reasoning effort is rejected on the host");
        var usage = CodexSettingsCatalog.Usage(JsonNode.Parse("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"one":{"planType":"pro","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1792319772}},"two":{"secondary":{"usedPercent":120,"windowDurationMins":10080}}}}""")!);
        check(usage.Contains("剩余 75%") && usage.Contains("5 小时") && usage.Contains("7 天") && usage.Contains("剩余 0%") && !usage.Contains("剩余 1%"), "all limit buckets use remaining percentage and actual window length with legacy fallback excluded");
        check(CodexSettingsCatalog.Usage(JsonNode.Parse("""{"rateLimits":{"primary":{"usedPercent":null}}}""")!).Contains("剩余比例未知"), "missing quota is unknown instead of falsely showing 100 percent remaining");
        check(CodexSettingsCatalog.Usage(JsonNode.Parse("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":10}}}""")!).Contains("剩余 90%"), "empty multi-bucket response falls back to legacy quota");
        await using var broken = new Metadata("""{"data":[],"nextCursor":"same"}""", """{"data":[],"nextCursor":"same"}""");
        rejected = false;
        try { await CodexSettingsCatalog.ModelsAsync(broken, "", default); } catch (IOException) { rejected = true; }
        check(rejected, "repeated model cursor cannot hang settings indefinitely");
        var old = Json.Read<AgentSettings>(JsonNode.Parse("""{"model":"old","provider":"","mode":"default","models":[],"canChangeModel":true,"canChangeMode":true,"detail":"old host"}"""));
        check(!old.CanChangeReasoning && !old.RequireModelSelection && old.Usage is null, "old settings payload stays readable without inventing new host capabilities");
    }
    internal static async Task Live(string threadId)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var registry = new RegistryClient("https://furtheragent.fennecmomo.top");
        await using var host = new DeviceHost(registry, () => new CodexDesktopAdapter());
        await host.StartAsync("Settings 自动验证", stop.Token);
        await using var remote = new RemoteDevice();
        await remote.ConnectAsync(registry, host.Lease!.Code, stop.Token);
        await remote.Peer.CallAsync("attach", new { id = threadId }, stop.Token);
        var settings = Json.Read<AgentSettings>(await remote.Peer.CallAsync("settings", null, stop.Token));
        if (settings.Models.Count == 0 || !settings.CanChangeReasoning || settings.Usage is null || !settings.Usage.Contains("剩余"))
            throw new Exception("live settings incomplete: " + settings.Detail + " / " + settings.Usage);
        Console.WriteLine($"PASS: public relay returned {settings.Models.Count} models, reasoning choices and account usage from the Codex host");
        Console.WriteLine($"Current model: {settings.Model}; effort: {settings.ReasoningEffort}");
        Console.WriteLine(settings.Usage);
    }
    internal static async Task LiveConfigure()
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string? original = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        string? createdId = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using (var metadata = await CodexAppServer.OpenAsync(home, timeout.Token))
            {
                var created = await metadata.CallAsync("thread/start", new { cwd = Environment.CurrentDirectory, ephemeral = false, historyMode = "legacy" }, timeout.Token);
                createdId = created["thread"]!["id"]!.ToString();
                await metadata.CallAsync("thread/name/set", new { threadId = createdId, name = "FurtherAgent 设置自动验证（无消息）" }, timeout.Token);
            }
            await using var adapter = new CodexDesktopAdapter(home);
            await adapter.AttachAsync(createdId, timeout.Token);
            var before = await adapter.SettingsAsync(timeout.Token);
            var model = before.Models.First(m => m.ReasoningEfforts?.Count > 1 && m.Id != before.Model);
            var effort = model.ReasoningEfforts!.Last();
            await adapter.ConfigureAsync(model.Id, model.Provider, "plan", effort, timeout.Token);
            var after = await adapter.SettingsAsync(timeout.Token);
            if (after.Model != model.Id || after.Mode != "plan" || after.ReasoningEffort != effort)
                throw new Exception($"Native readback mismatch: {after.Model}/{after.Mode}/{after.ReasoningEffort}");
            Console.WriteLine($"PASS: native IPC readback confirmed {after.Model}, {after.Mode}, {after.ReasoningEffort}");
            await adapter.ConfigureAsync(before.Model, before.Provider, before.Mode, before.ReasoningEffort, timeout.Token);
            var restored = await adapter.SettingsAsync(timeout.Token);
            if (restored.Model != before.Model || restored.Mode != before.Mode) throw new Exception("Native restore mismatch");
            Console.WriteLine($"PASS: native IPC model/mode/effort written and read back ({model.Id}, plan, {effort}); original model/mode restored; no turns sent");
        }
        finally
        {
            if (createdId is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var metadata = await CodexAppServer.OpenAsync(home, cleanup.Token);
                try
                {
                    await metadata.CallAsync("thread/archive", new { threadId = createdId }, cleanup.Token);
                    Console.WriteLine("Archived newly-created settings fixture: " + createdId);
                }
                catch (IOException ex) { Console.WriteLine("Fixture requires desktop archive: " + createdId + "; " + ex.Message); }
            }
            if (original is not null) CodexThreadActivation.Open(CodexThreadActivation.ThreadUrl(original));
        }
    }
}
