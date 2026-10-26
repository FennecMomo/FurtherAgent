using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class SlowHistoryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string fixture = Path.Combine(Path.GetTempPath(), "FurtherAgent-slow-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            string url = $"http://127.0.0.1:{port}";
            using var registry = new RegistryClient(url);
            for (int i = 0; ; i++)
            {
                try { await registry.CheckHealthAsync(default); break; }
                catch { if (i >= 40) throw; await Task.Delay(100); }
            }
            var source = BuildHistory();
            int fullBytes = Encoding.UTF8.GetByteCount(Json.Node(source).ToJsonString());
            check(fullBytes > 600000, "DSH fixture starts with more than 600 KB of history");
            await using var host = new DeviceHost(registry, () => new HistoryAdapter(source), Path.Combine(fixture, "files"), enableLan: false);
            await host.StartAsync("Slow DSH history smoke");

            // Model a legacy client: no session-sync/history-mode call before attach.
            var pair = await registry.PostAsync<PairResult>("pair", new PairRequest(host.Lease!.Code), default);
            await using (var transport = await registry.OpenSocketAsync("control", default))
            {
                await transport.WriteAsync(Json.Node(new { ticket = pair.Ticket }), default);
                var accepted = await transport.ReadLimitedAsync(4096, default);
                if (accepted["ok"]?.GetValue<bool>() != true) throw new IOException("History fixture connection refused.");
                await using var legacy = new RpcPeer(transport); legacy.Start();
                var snapshot = Json.Read<SessionView>(await legacy.CallAsync("attach", new { id = source.Id }));
                int bytes = Encoding.UTF8.GetByteCount(Json.Node(snapshot).ToJsonString());
                check(snapshot.History is { Start: > 0 } && snapshot.Messages.Count(HistoryPaging.IsConversation) == 8 && bytes < 100000,
                    "unnegotiated DSH attach is bounded and carries an older-history cursor");
                check(Indices(snapshot).SequenceEqual(Enumerable.Range(92, 8)), "legacy DSH attach includes the latest eight prose entries");
            }

            await using (var desktop = new RemoteDevice(autoReconnect: false))
            {
                await desktop.ConnectAsync(registry, host.Lease.Code);
                await OperationGroups.NegotiateAsync(desktop.Peer);
                var latest = Json.Read<SessionView>(await SessionHistory.ReadAsync(desktop.Peer, "attach", new { id = source.Id }));
                var cursor = latest.History ?? throw new IOException("Desktop did not receive a history cursor.");
                var older = await OperationGroups.LoadBatchAsync(source.Id, cursor, async before =>
                    Json.Read<HistoryPage>(await SessionHistory.ReadAsync(desktop.Peer, "history", new { session = source.Id, revision = cursor.Revision, before })));
                check(older.End == cursor.Start && Indices(older.Messages.Concat(latest.Messages)).SequenceEqual(Enumerable.Range(84, 16)),
                    "desktop history cursor joins the latest and older pages without gaps or duplicates");
                check(latest.Messages.Any(m => m.Operations is not null), "desktop negotiated history retains compact operation groups");
            }

            await using (var mobile = new MobileSession(a => a()))
            {
                await mobile.ConnectAsync(url, host.Lease.Code);
                await mobile.SelectAsync(source.Id);
                check(mobile.HasOlder && mobile.View is { } latest && Indices(latest).SequenceEqual(Enumerable.Range(92, 8)),
                    "mobile opens a bounded latest DSH history page");
                await mobile.LoadOlderAsync();
                check(mobile.ReadingOlder && mobile.View is { } earlier && Indices(earlier).SequenceEqual(Enumerable.Range(84, 16)),
                    "mobile loads older DSH history without gaps or duplicates");
            }

            var cliStart = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture };
            cliStart.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Cli/bin/Release/net10.0/FurtherAgent.Cli.dll"));
            foreach (string argument in new[] { "read", "--server", url, "--session", source.Id }) cliStart.ArgumentList.Add(argument);
            cliStart.Environment["FURTHERAGENT_CODE"] = host.Lease.Code;
            cliStart.Environment.Remove("FURTHERAGENT_CODE_FILE");
            using var cli = Process.Start(cliStart)!;
            var stdout = cli.StandardOutput.ReadToEndAsync(); var stderr = cli.StandardError.ReadToEndAsync();
            try { await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { if (!cli.HasExited) { cli.Kill(true); await cli.WaitForExitAsync(); } }
            if (cli.ExitCode != 0) throw new IOException("CLI history fixture failed: " + (await stderr).Replace(host.Lease.Code, "[REDACTED]"));
            string output = await stdout;
            var cliView = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!)
                .Where(node => node["type"]?.ToString() == "session").Select(node => Json.Read<SessionView>(node["data"])).Single();
            check(cliView.History is { Start: > 0 } && Indices(cliView).SequenceEqual(Enumerable.Range(92, 8)) &&
                cliView.Messages.Any(m => m.Operations is not null) && Encoding.UTF8.GetByteCount(output) < 100000,
                "CLI read negotiates grouped history and emits a bounded latest DSH snapshot");
            Console.WriteLine($"History payload: source={fullBytes:N0} bytes; CLI latest={Encoding.UTF8.GetByteCount(output):N0} bytes");
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            Directory.Delete(fixture, true);
        }
    }

    private static SessionView BuildHistory()
    {
        var messages = new List<ChatEntry>();
        for (int i = 0; i < 100; i++)
        {
            messages.Add(new(i % 2 == 0 ? "user" : "assistant", $"entry-{i:D3}:" + new string('x', 8192)));
            messages.Add(new("工具", "trace", "{\"type\":\"commandExecution\",\"command\":\"fixture-only\"}"));
        }
        return new("slow-history", "Large DSH history fixture", "", "idle", null, messages, []);
    }
    private static IEnumerable<int> Indices(SessionView view) => Indices(view.Messages);
    private static IEnumerable<int> Indices(IEnumerable<ChatEntry> messages) => messages.Where(HistoryPaging.IsConversation).Select(m => int.Parse(m.Text.AsSpan(6, 3)));
    private sealed class HistoryAdapter(SessionView view) : IAgentAdapter
    {
        public AgentInfo Info => new("dsh", "History fixture", true, "Loopback test only");
        public event Action<SessionView>? Changed { add { } remove { } }
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new(view.Id, view.Title, "2026-10-26")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(view);
        public Task SendAsync(string text, CancellationToken ct) => throw new InvalidOperationException("History smoke never sends a message.");
        public Task InterruptAsync(CancellationToken ct) => throw new InvalidOperationException("History smoke never interrupts a task.");
        public Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct) => throw new InvalidOperationException("History smoke never responds to prompts.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
