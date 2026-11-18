using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class SharedConnectionChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        await SharedWriteCancellationChecks.Run(check);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string fixture = Path.Combine(root, "artifacts", "checks", "shared-connection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = fixture
        };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["FURTHERAGENT_MAILBOX_ROOT"] = Path.Combine(fixture, "mailboxes");
        using var server = Process.Start(start)!;
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            using var hostRegistry = new RegistryClient($"http://127.0.0.1:{port}");
            await Until(async () =>
            {
                try { await hostRegistry.CheckHealthAsync(default); return true; }
                catch { return false; }
            });
            var state = new SessionState();
            await using var host = new DeviceHost(hostRegistry, () => new SessionAdapter(state), Path.Combine(fixture, "files"), enableLan: false);
            int physicalControls = 0;
            host.Status += message =>
            {
                if (message == "远程端已通过服务端中继连接。") Interlocked.Increment(ref physicalControls);
            };
            await host.StartAsync("Shared connection fixture");
            await using var gateway = new DisconnectGateway(port);
            using var registry = new RegistryClient(gateway.Url);
            await using var owner = new RemoteDevice();
            await owner.ConnectAsync(registry, host.Lease!.Code);
            var main = new Subscription(() => owner.Peer, "main");
            owner.Notification += main.OnNotification;
            owner.Reconnected += main.Restore;
            await main.AttachAsync();

            await using var alpha = new RemoteSessionConnection(owner, registry, host.Lease.Code);
            await using var beta = new RemoteSessionConnection(owner, registry, host.Lease.Code);
            var first = new Subscription(() => alpha.Peer, "alpha");
            var second = new Subscription(() => beta.Peer, "beta");
            alpha.Notification += first.OnNotification;
            beta.Notification += second.OnNotification;
            alpha.Reconnected += first.Restore;
            beta.Reconnected += second.Restore;
            await alpha.ConnectAsync();
            await beta.ConnectAsync();
            await first.AttachAsync();
            await second.AttachAsync();
            check(alpha.IsSharedConnection && beta.IsSharedConnection && Volatile.Read(ref physicalControls) == 1,
                "main and two session channels use a single physical relay control");
            check(main.View?.Id == "main" && first.View?.Id == "alpha" && second.View?.Id == "beta",
                "logical channels attach independent sessions without changing the main selection");

            var cursor = first.View!.History!;
            var earlier = Json.Read<HistoryPage>(await SessionHistory.ReadAsync(alpha.Peer, "history",
                new { session = "alpha", revision = cursor.Revision, before = cursor.Start }));
            check(cursor.Start == 4 && earlier.Session == "alpha" && earlier.Start == 0 && earlier.End == cursor.Start &&
                earlier.Messages.All(entry => entry.Text.StartsWith("alpha-", StringComparison.Ordinal)),
                "each channel keeps its own paged history source and revision");

            string warm = state.Text("alpha") + " warm";
            state.Publish("alpha", warm);
            await Until(() => first.LastText == warm);
            long before = owner.ReceivedBytes;
            string append = warm + " 新增片段🙂";
            state.Publish("alpha", append);
            await Until(() => first.LastText == append);
            check(owner.ReceivedBytes - before < 4096 && main.LastText == state.Text("main") && second.LastText == state.Text("beta"),
                "channel notifications reconstruct small text deltas without leaking into other sessions");
            await alpha.Peer.CallAsync("send", new { text = "one deliberate submission" });
            check(state.Submissions.Count == 1 && state.Submissions.Single().Session == "alpha",
                "a mutating request is routed only to its channel's attached session");

            Task<JsonNode?> waiting = alpha.Peer.CallAsync("settings", null);
            await state.SettingsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.Peer.CallAsync("agents", null).WaitAsync(TimeSpan.FromSeconds(3));
            var betaAgain = Json.Read<SessionView>(await SessionHistory.ReadAsync(beta.Peer, "attach", new { id = "beta" })
                .WaitAsync(TimeSpan.FromSeconds(3)));
            check(betaAgain.Id == "beta" && owner.IsConnected,
                "a blocked session operation does not block the main channel or another session");
            await alpha.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("closed channel unexpectedly completed its blocked operation"); }
            catch (IOException) { }
            check(owner.IsConnected && beta.IsConnected && await owner.CheckConnectionAsync(),
                "closing a channel cancels its pending work and keeps the shared physical connection alive");

            var previousOwner = owner.Peer;
            var previousBeta = beta.Peer;
            int ownerOffline = 0, betaOffline = 0;
            owner.ConnectionChanged += (online, _) => { if (!online) Interlocked.Increment(ref ownerOffline); };
            beta.ConnectionChanged += (online, _) => { if (!online) Interlocked.Increment(ref betaOffline); };
            gateway.CloseConnections();
            await Until(() => Volatile.Read(ref ownerOffline) > 0 && Volatile.Read(ref betaOffline) > 0);
            state.Publish("beta", state.Text("beta") + " generated during disconnect");
            await Until(() => owner.Peer != previousOwner && beta.IsConnected && beta.Peer != previousBeta &&
                main.View?.Id == "main" && second.LastText == state.Text("beta"), 35);
            check(main.Error is null && second.Error is null && beta.IsSharedConnection && owner.IsConnected && beta.IsNetworkConnected &&
                Volatile.Read(ref physicalControls) == 2,
                "physical reconnect rebinds the surviving session channel and restores history generated while offline");
            check(state.Submissions.Count == 1,
                "shared connection recovery does not replay a previously submitted mutation");
            string afterReconnect = state.Text("beta") + " new baseline";
            state.Publish("beta", afterReconnect);
            await Until(() => second.LastText == afterReconnect);
            before = owner.ReceivedBytes;
            string resumed = afterReconnect + " resumed delta";
            state.Publish("beta", resumed);
            await Until(() => second.LastText == resumed);
            check(owner.ReceivedBytes - before < 4096,
                "per-channel text deltas resume after a fresh reconnect baseline");
            await owner.DisposeAsync();
            await Until(() => !beta.IsNetworkConnected && !beta.IsConnected);
            check(!beta.IsNetworkConnected && !beta.IsConnected,
                "disposing the owning device makes all surviving channel connection states offline");
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
        }
    }

    private static Task Until(Func<bool> condition, int seconds = 10) => Until(() => Task.FromResult(condition()), seconds);
    private static async Task Until(Func<Task<bool>> condition, int seconds = 10)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await condition()) await Task.Delay(40, stop.Token);
    }

    private sealed class Subscription(Func<RpcPeer> peer, string session)
    {
        private SessionView? view;
        public SessionView? View => Volatile.Read(ref view);
        public string? LastText => View?.Messages.LastOrDefault()?.Text;
        public Exception? Error;
        public void OnNotification(string method, JsonNode? node)
        {
            if (method == "session" && node is not null) Volatile.Write(ref view, Json.Read<SessionView>(node));
        }
        public async Task AttachAsync()
        {
            var current = peer();
            await OperationGroups.NegotiateAsync(current);
            Volatile.Write(ref view, Json.Read<SessionView>(await SessionHistory.ReadAsync(current, "attach", new { id = session })));
        }
        public async void Restore()
        {
            try { await AttachAsync(); }
            catch (Exception ex) { Error = ex; }
        }
    }

    private sealed class SessionState
    {
        private readonly ConcurrentDictionary<string, string> texts = new();
        private readonly ConcurrentDictionary<SessionAdapter, byte> adapters = new();
        public ConcurrentQueue<(string Session, string Text)> Submissions { get; } = new();
        public TaskCompletionSource SettingsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text(string id) => texts.GetOrAdd(id, _ => id + "-" + new string('x', 12000));
        public void Add(SessionAdapter adapter) => adapters.TryAdd(adapter, 0);
        public void Remove(SessionAdapter adapter) => adapters.TryRemove(adapter, out _);
        public void Publish(string id, string text)
        {
            texts[id] = text;
            foreach (var adapter in adapters.Keys) adapter.Publish(id);
        }
        public SessionView View(string id) => new(id, id, "", "active", "run-" + id,
            Enumerable.Range(0, 11).Select(i => new ChatEntry(i % 2 == 0 ? "user" : "assistant", id + "-history-" + i))
                .Append(new ChatEntry("assistant", Text(id))).ToList(), []);
    }

    private sealed class SessionAdapter : IAgentAdapter
    {
        private readonly SessionState state;
        private string? selected;
        public SessionAdapter(SessionState state) { this.state = state; state.Add(this); }
        public AgentInfo Info => new("test", "Shared connection fixture", true, "loopback fixture");
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([new("main", "main", ""), new("alpha", "alpha", ""), new("beta", "beta", "")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct)
        { selected = id; return Task.FromResult(state.View(id)); }
        public void Publish(string id) { if (selected == id) Changed?.Invoke(state.View(id)); }
        public Task SendAsync(string text, CancellationToken ct)
        { state.Submissions.Enqueue((selected!, text)); return Task.CompletedTask; }
        public async Task<AgentSettings> SettingsAsync(CancellationToken ct)
        {
            if (selected == "alpha")
            {
                state.SettingsEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new("", "", "default", [], false, false, "fixture");
        }
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() { state.Remove(this); return ValueTask.CompletedTask; }
    }

    // Interrupt only fixture loopback sockets; leave the user's network configuration untouched.
    private sealed class DisconnectGateway : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
        private readonly ConcurrentBag<Task> tunnels = [];
        private readonly Task accepting;
        public string Url { get; }
        public DisconnectGateway(int targetPort)
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            accepting = AcceptAsync(targetPort);
        }
        public void CloseConnections() { foreach (var client in clients.Keys) client.Dispose(); }
        private async Task AcceptAsync(int targetPort)
        {
            try { while (!stop.IsCancellationRequested) tunnels.Add(TunnelAsync(await listener.AcceptTcpClientAsync(stop.Token), targetPort)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        private async Task TunnelAsync(TcpClient incoming, int targetPort)
        {
            clients.TryAdd(incoming, 0);
            using (incoming)
            using (var outgoing = new TcpClient())
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                try
                {
                    await outgoing.ConnectAsync(IPAddress.Loopback, targetPort, lifetime.Token);
                    var upload = incoming.GetStream().CopyToAsync(outgoing.GetStream(), lifetime.Token);
                    var download = outgoing.GetStream().CopyToAsync(incoming.GetStream(), lifetime.Token);
                    await Task.WhenAny(upload, download);
                    lifetime.Cancel();
                    await Task.WhenAll(upload, download);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                finally { clients.TryRemove(incoming, out _); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            CloseConnections();
            await accepting;
            listener.Stop();
            await Task.WhenAll(tunnels);
            stop.Dispose();
        }
    }
}
