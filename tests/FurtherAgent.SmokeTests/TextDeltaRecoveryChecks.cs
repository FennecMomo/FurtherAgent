using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class TextDeltaRecoveryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string fixture = Path.Combine(Path.GetTempPath(), "FurtherAgent-text-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            using var registry = new RegistryClient($"http://127.0.0.1:{port}");
            for (int i = 0; ; i++)
            {
                try { await registry.CheckHealthAsync(default); break; }
                catch { if (i >= 40) throw; await Task.Delay(100); }
            }
            var state = new StreamingState();
            await using var host = new DeviceHost(registry, () => new StreamingAdapter(state), Path.Combine(fixture, "files"), enableLan: false);
            await host.StartAsync("Text delta recovery smoke");
            await using var gateway = new DisconnectGateway(port);
            using var desktopRegistry = new RegistryClient(gateway.Url);
            await using var desktop = new RemoteDevice();
            SessionView? desktopView = null;
            Exception? desktopRecoveryError = null;
            desktop.Notification += (method, node) =>
            {
                if (method == "session" && node is not null) Volatile.Write(ref desktopView, Json.Read<SessionView>(node));
            };
            desktop.Reconnected += () => _ = RestoreDesktopAsync();
            async Task RestoreDesktopAsync()
            {
                try { Volatile.Write(ref desktopView, Json.Read<SessionView>(await desktop.Peer.CallAsync("attach", new { id = "text-stream" }))); }
                catch (Exception ex) { desktopRecoveryError = ex; }
            }
            await desktop.ConnectAsync(desktopRegistry, host.Lease!.Code);
            Volatile.Write(ref desktopView, Json.Read<SessionView>(await desktop.Peer.CallAsync("attach", new { id = "text-stream" })));
            await using var mobile = new MobileSession(a => a());
            await mobile.ConnectAsync(gateway.Url, host.Lease.Code);
            await mobile.SelectAsync("text-stream");

            // The first notification has an independent full baseline even after attach.
            string warm = state.Text + " first notification";
            state.Publish(warm);
            try { await Until(() => Text(Volatile.Read(ref desktopView)) == warm && Text(mobile.View) == warm); }
            catch
            {
                Console.WriteLine($"Warmup state: desktopConnected={desktop.IsConnected}, desktopLength={Text(Volatile.Read(ref desktopView))?.Length}, mobileConnected={mobile.Connected}, mobileLength={Text(mobile.View)?.Length}, expectedLength={warm.Length}, mobileStatus={mobile.Status}");
                throw;
            }
            long receivedBefore = desktop.ReceivedBytes;
            string growing = warm + " 新增片段🙂";
            state.Publish(growing);
            await Until(() => Text(Volatile.Read(ref desktopView)) == growing && Text(mobile.View) == growing);
            long appendBytes = desktop.ReceivedBytes - receivedBefore;
            check(appendBytes < 4096, "negotiated v2 sends a small append instead of repeating the long reply");
            check(Text(Volatile.Read(ref desktopView)) == growing && Text(mobile.View) == growing,
                "desktop RemoteDevice and MobileSession reconstruct identical streaming text");

            await CheckLegacyClientAsync(registry, host.Lease.Code, state, check);
            await Until(() => Text(Volatile.Read(ref desktopView)) == state.Text && Text(mobile.View) == state.Text);

            var previousPeer = desktop.Peer;
            int disconnects = 0;
            desktop.ConnectionChanged += (connected, _) => { if (!connected) Interlocked.Increment(ref disconnects); };
            gateway.CloseConnections();
            await Until(() => Volatile.Read(ref disconnects) > 0);
            string gap = state.Text + " disconnected gap";
            state.Publish(gap);
            await Until(() => desktop.ReconnectCount > 0 && desktop.Peer != previousPeer &&
                Text(Volatile.Read(ref desktopView)) == gap && mobile.Connected && Text(mobile.View) == gap, 40);
            check(desktopRecoveryError is null && Text(Volatile.Read(ref desktopView)) == gap && Text(mobile.View) == gap,
                "reconnection restores the complete reply including text generated while disconnected");
            string resumed = gap + " resumed append";
            state.Publish(resumed);
            await Until(() => Text(Volatile.Read(ref desktopView)) == resumed && Text(mobile.View) == resumed);
            receivedBefore = desktop.ReceivedBytes;
            string final = resumed + " final append";
            state.Publish(final);
            await Until(() => Text(Volatile.Read(ref desktopView)) == final && Text(mobile.View) == final);
            check(desktop.ReceivedBytes - receivedBefore < 4096, "text deltas resume after a new full reconnect baseline");
            check(state.Sends == 0, "connection recovery never replays send operations");
            Console.WriteLine($"Loopback append payload: {appendBytes:N0} bytes for a {warm.Length:N0}-character existing reply");
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            Directory.Delete(fixture, true);
        }
    }

    private static async Task CheckLegacyClientAsync(RegistryClient registry, string code, StreamingState state, Action<bool, string> check)
    {
        var pair = await registry.PostAsync<PairResult>("pair", new PairRequest(code), default);
        await using var transport = await registry.OpenSocketAsync("control", default);
        await transport.WriteAsync(Json.Node(new { ticket = pair.Ticket }), default);
        var accepted = await transport.ReadLimitedAsync(4096, default);
        if (accepted["ok"]?.GetValue<bool>() != true) throw new IOException("Legacy fixture connection refused.");
        await using var peer = new RpcPeer(transport);
        var decoder = new SessionSyncDecoder();
        SessionView? observed = null;
        bool appendSeen = false;
        peer.Notification += (method, node) =>
        {
            if (method != "session-update" || node is null) return;
            appendSeen |= node["append"] is not null;
            Volatile.Write(ref observed, decoder.Apply(Json.Read<SessionUpdate>(node)));
        };
        peer.Start();
        var negotiation = await peer.CallAsync("session-sync", new { version = 1 });
        await peer.CallAsync("attach", new { id = "text-stream" });
        string first = state.Text + " legacy baseline";
        state.Publish(first);
        await Until(() => Text(Volatile.Read(ref observed)) == first);
        long before = transport.ReceivedBytes;
        string second = first + " legacy growing";
        state.Publish(second);
        await Until(() => Text(Volatile.Read(ref observed)) == second);
        check(negotiation?["version"]?.GetValue<int>() == 1 && !appendSeen && transport.ReceivedBytes - before > 30000,
            "v1 clients negotiate legacy whole-message updates on the new host");
    }

    private static string? Text(SessionView? view) => view?.Messages.LastOrDefault()?.Text;
    private static async Task Until(Func<bool> condition, int seconds = 8)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(50, stop.Token);
    }

    private sealed class StreamingState
    {
        private readonly object gate = new();
        private readonly List<StreamingAdapter> adapters = [];
        private string text = new('文', 12000);
        public int Sends;
        public string Text { get { lock (gate) return text; } }
        public void Add(StreamingAdapter adapter) { lock (gate) adapters.Add(adapter); }
        public void Remove(StreamingAdapter adapter) { lock (gate) adapters.Remove(adapter); }
        public void Publish(string value)
        {
            StreamingAdapter[] targets;
            lock (gate) { text = value; targets = adapters.ToArray(); }
            foreach (var adapter in targets) adapter.Publish();
        }
        public SessionView View => new("text-stream", "Text stream", "", "active", "run", [new("assistant", Text)], []);
    }
    private sealed class StreamingAdapter : IAgentAdapter
    {
        private readonly StreamingState state;
        private bool attached;
        public StreamingAdapter(StreamingState state) { this.state = state; state.Add(this); }
        public AgentInfo Info => new("test", "Text recovery fixture", true, "Loopback test only");
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted { add { } remove { } }
        public void Publish() { if (attached) Changed?.Invoke(state.View); }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("text-stream", "Text stream", "2026-10-26")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) { attached = true; return Task.FromResult(state.View); }
        public Task SendAsync(string text, CancellationToken ct) { Interlocked.Increment(ref state.Sends); return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() { state.Remove(this); return ValueTask.CompletedTask; }
    }

    // Only the fixture clients' loopback connections pass through this gateway.
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
            listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
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
                    await Task.WhenAny(upload, download); lifetime.Cancel(); await Task.WhenAll(upload, download);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                finally { clients.TryRemove(incoming, out _); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); CloseConnections(); await accepting; listener.Stop(); await Task.WhenAll(tunnels); stop.Dispose();
        }
    }
}
