using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class MailboxChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FurtherAgent-mailbox-smoke-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(fixture);
        string url = $"http://127.0.0.1:{port}";
        string identity = Path.Combine(fixture, "home.identity");
        var previous = new Dictionary<string, string?>();
        foreach (string key in new[] { "FURTHERAGENT_IDENTITY_FILE", "FURTHERAGENT_MAILBOX_ACCOUNT_DIR", "FURTHERAGENT_MAILBOX_ROOT" })
            previous[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE", identity);
        Environment.SetEnvironmentVariable("FURTHERAGENT_MAILBOX_ACCOUNT_DIR", Path.Combine(fixture, "accounts"));
        Environment.SetEnvironmentVariable("FURTHERAGENT_MAILBOX_ROOT", Path.Combine(fixture, "server-mailboxes"));
        Process? server = null;
        DeviceHost? host = null;
        var calls = new PassiveAdapterCounters();
        try
        {
            server = await StartServer();
            using var registry = new RegistryClient(url);
            host = new DeviceHost(registry, () => { Interlocked.Increment(ref calls.Factories); return new PassiveAdapter(calls); },
                Path.Combine(fixture, "files"), persistentIdentity: true, enableLan: false);
            await host.StartAsync("Mailbox smoke home");
            var binding = await MailboxClient.PairAsync(url, host.Lease!.Code);
            using var controller = new MailboxClient(url, binding);
            string accountPath = Directory.EnumerateFiles(MailboxAgentAccount.DirectoryPath, "*.json").Single();
            using var agent = MailboxAgentAccount.Read(accountPath).Open();
            check(binding.Role == "controller" && (await controller.StatusAsync()).LatestSequence == 0,
                "online existing device pairing creates an independent persistent mailbox");

            string firstId = Guid.NewGuid().ToString("N");
            var first = await controller.SendAsync("我先留言，你有空再看。", firstId);
            var duplicate = await controller.SendAsync(first.Text, firstId);
            check(first.Sequence == duplicate.Sequence && first.Id == duplicate.Id && first.CreatedAt == duplicate.CreatedAt &&
                (await controller.FetchAsync()).Messages.Count == 1, "repeating an acknowledged mailbox message ID does not duplicate delivery");
            var agentPage = await agent.FetchAsync();
            check(agentPage.Messages.Single().Sender == "controller" && agentPage.Unread == 1 &&
                (await agent.StatusAsync()).Unread == 1 && agentPage.AgentReadThrough == 0,
                "fetching mailbox messages and counts does not mark them read");

            await controller.SendAsync("这是第二条，仍然先放在收件箱。", Guid.NewGuid().ToString("N"));
            for (int i = 0; i < 3; i++)
                await controller.SendAsync(new string('鲸', MailboxClient.MaxTextLength), Guid.NewGuid().ToString("N"));
            var bounded = await agent.FetchAsync(after: 0, limit: 50);
            check(bounded.HasMore && bounded.Messages.Count is > 0 and < 5 && bounded.Unread == 5 &&
                JsonSerializer.SerializeToUtf8Bytes(bounded.Messages, Json.Options).Length < 128 * 1024,
                "escaped Chinese messages above the old HTTP limit paginate within a bounded response");
            await agent.MarkReadAsync(5);
            check((await agent.StatusAsync()).Unread == 0 && (await controller.StatusAsync()).AgentReadThrough == 5,
                "explicit Agent read acknowledgement updates the shared read watermark");

            await host.DisposeAsync(); host = null;
            var offline = await controller.SendAsync("家里的接入端关闭了，这条也应当保存。", Guid.NewGuid().ToString("N"));
            var reply = await agent.SendAsync("我稍后看到了，回一封留言。", Guid.NewGuid().ToString("N"));
            check(offline.Sequence == 6 && reply.Sequence == 7 && reply.Sender == "agent" &&
                (await controller.StatusAsync()).Unread == 1 && (await agent.StatusAsync()).Unread == 1,
                "both directions work while the home Host is offline without starting an Agent turn");

            await StopServer(server); server.Dispose(); server = await StartServer();
            var persisted = await controller.FetchAsync();
            check(persisted.LatestSequence == 7 && persisted.AgentReadThrough == 5 && persisted.ControllerReadThrough == 0 &&
                persisted.Messages.Any(m => m.Id == reply.Id) && (await controller.StatusAsync()).Unread == 1,
                "server restart retains messages, existing controller binding and independent read watermarks");
            await controller.MarkReadAsync(7);
            check((await controller.StatusAsync()).Unread == 0 && (await agent.StatusAsync()).Unread == 1,
                "the user's acknowledgement does not mark the Agent's incoming messages read");

            var all = new List<MailboxMessage>(); long after = 0;
            while (true)
            {
                var page = await controller.FetchAsync(after: after, limit: 2);
                all.AddRange(page.Messages);
                if (page.Messages.Count > 0) after = page.Messages[^1].Sequence;
                if (!page.HasMore) break;
            }
            var latest = await controller.FetchAsync(limit: 2);
            var older = await controller.FetchAsync(before: latest.Messages[0].Sequence, limit: 2);
            check(all.Select(m => m.Sequence).SequenceEqual(Enumerable.Range(1, 7).Select(n => (long)n)) &&
                latest.Messages.Select(m => m.Sequence).SequenceEqual(new long[] { 6, 7 }) &&
                older.Messages.Select(m => m.Sequence).SequenceEqual(new long[] { 4, 5 }),
                "after polling and before history pages preserve message order without gaps or duplicates");

            var readOutput = await Cli("read", "--account-file", accountPath, "--after", "5", "--limit", "2");
            var readPage = Event(readOutput, "mailbox-messages");
            check(readPage["messages"]?.AsArray().Count == 2 && (await agent.StatusAsync()).Unread == 1,
                "Agent CLI reads the mailbox account while offline without implicitly acknowledging");
            var unreadOutput = await Cli("read", "--account-file", accountPath, "--limit", "1");
            check(Event(unreadOutput, "mailbox-messages")["messages"]?[0]?["sequence"]?.GetValue<long>() == 6,
                "Agent default read begins at its unread cursor instead of skipping to the newest page");
            var setupOutput = await Cli("setup", "--server", url, "--name", "Mailbox smoke home", "--identity-file", identity);
            check(File.Exists(Event(setupOutput, "mailbox-account")["accountFile"]!.ToString()),
                "Agent tool setup reuses the existing identity without requiring a running DSH session");
            string replyId = Guid.NewGuid().ToString("N");
            var replyOutput = await Cli("reply", "--account-file", accountPath, "--id", replyId, "--text", "小夜的延迟回信。");
            check(Event(replyOutput, "mailbox-saved")["sender"]?.ToString() == "agent" &&
                (await controller.FetchAsync()).Messages.Any(m => m.Id == replyId),
                "Agent CLI replies through the persisted mailbox without a DSH conversation send");
            await Cli("read", "--account-file", accountPath, "--after", "5", "--limit", "50", "--mark-read");
            check((await agent.StatusAsync()).Unread == 0 && (await controller.StatusAsync()).Unread == 1,
                "Agent CLI only acknowledges messages when explicitly requested");
            check(calls.Factories == 0 && calls.Attaches == 0 && calls.Sends == 0,
                "mailbox pairing, offline reads and replies never instantiate, attach or send to an Agent");
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            if (server is not null) { await StopServer(server); server.Dispose(); }
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fixture.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }

        async Task<Process> StartServer()
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture };
            start.ArgumentList.Add(Path.Combine(project, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
            start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
            var running = Process.Start(start)!; running.BeginOutputReadLine(); running.BeginErrorReadLine();
            try
            {
                using var probe = new RegistryClient(url);
                for (int i = 0; ; i++)
                {
                    try { await probe.CheckHealthAsync(default); return running; }
                    catch { if (i >= 40 || running.HasExited) throw; await Task.Delay(100); }
                }
            }
            catch { await StopServer(running); running.Dispose(); throw; }
        }
        static async Task StopServer(Process process)
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
        async Task<string> Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture };
            start.ArgumentList.Add(Path.Combine(project, "src/FurtherAgent.Cli/bin/Release/net10.0/FurtherAgent.Cli.dll"));
            start.ArgumentList.Add("mailbox");
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            if (process.ExitCode != 0) throw new IOException("Mailbox CLI fixture failed: " + await error);
            return await output;
        }
        static JsonNode Event(string output, string type) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).Single(node => node["type"]?.ToString() == type)["data"]!;
    }

    private sealed class PassiveAdapterCounters { public int Factories, Attaches, Sends; }
    private sealed class PassiveAdapter(PassiveAdapterCounters counters) : IAgentAdapter
    {
        public AgentInfo Info => new("test", "Mailbox passive fixture", true, "No model interaction");
        public event Action<SessionView>? Changed { add { } remove { } }
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) { Interlocked.Increment(ref counters.Attaches); throw new InvalidOperationException("Mailbox must not attach an Agent."); }
        public Task SendAsync(string text, CancellationToken ct) { Interlocked.Increment(ref counters.Sends); throw new InvalidOperationException("Mailbox must not send to an Agent."); }
        public Task InterruptAsync(CancellationToken ct) => throw new InvalidOperationException("Mailbox must not interrupt an Agent.");
        public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) => throw new InvalidOperationException("Mailbox must not answer an Agent prompt.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
