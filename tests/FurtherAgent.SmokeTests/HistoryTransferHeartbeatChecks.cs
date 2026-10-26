using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class HistoryTransferHeartbeatChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string fixture = Path.Combine(Path.GetTempPath(), "FurtherAgent-history-heartbeat-" + Guid.NewGuid().ToString("N"));
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
            var adapter = new TransferAdapter();
            await using var host = new DeviceHost(registry, () => adapter, Path.Combine(fixture, "files"), enableLan: false);
            await host.StartAsync("History transfer heartbeat smoke");
            await using var gateway = new ThrottleGateway(port);
            using var clientRegistry = new RegistryClient(gateway.Url);
            await using var remote = new RemoteDevice(autoReconnect: false);
            await remote.ConnectAsync(clientRegistry, host.Lease!.Code);
            check(remote.Peer.SupportsTransportPing, "history transfer fixture negotiates a transport heartbeat");
            long receivedBefore = remote.ReceivedBytes;
            gateway.Throttle = true;
            var elapsed = Stopwatch.StartNew();
            var read = SessionHistory.ReadAsync(remote.Peer, "attach", new { id = "large-transfer" });
            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                while (remote.ReceivedBytes <= receivedBefore) await Task.Delay(20, wait.Token);
            var probe = Stopwatch.StartNew();
            bool alive = await remote.CheckConnectionAsync();
            probe.Stop();
            check(alive && remote.IsConnected && probe.Elapsed >= TimeSpan.FromSeconds(3.5) && !read.IsCompleted,
                "heartbeat timeout retains a live connection while large history fragments are still arriving");
            var result = Json.Read<SessionView>(await read.WaitAsync(TimeSpan.FromSeconds(8)));
            check(result.Id == "large-transfer" && result.Messages.Single().Text == TransferAdapter.Payload && remote.IsConnected,
                "slow history frame finishes intact after its delayed heartbeat response");
            check(adapter.Sends == 0 && remote.ReconnectCount == 0, "slow history transfer does not reconnect or replay business operations");
            Console.WriteLine($"Throttled history transfer: {TransferAdapter.Payload.Length:N0} characters in {elapsed.Elapsed.TotalSeconds:F1}s; probe waited {probe.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            Directory.Delete(fixture, true);
        }
    }

    private sealed class TransferAdapter : IAgentAdapter
    {
        public static readonly string Payload = "history-transfer:" + new string('x', 120 * 1024);
        public int Sends;
        public AgentInfo Info => new("test", "History transfer fixture", true, "Loopback test only");
        public event Action<SessionView>? Changed { add { } remove { } }
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("large-transfer", "History transfer", "2026-10-26")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(new SessionView("large-transfer", "History transfer", "", "idle", null, [new("assistant", Payload)], []));
        public Task SendAsync(string text, CancellationToken ct) { Interlocked.Increment(ref Sends); return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct) => throw new InvalidOperationException("History fixture never interrupts.");
        public Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct) => throw new InvalidOperationException("History fixture never responds to prompts.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Slow only the fixture's client download path. Host and registry remain local and unchanged.
    private sealed class ThrottleGateway : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> tunnels = [];
        private readonly Task accepting;
        public volatile bool Throttle;
        public string Url { get; }
        public ThrottleGateway(int port)
        {
            listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            accepting = AcceptAsync(port);
        }
        private async Task AcceptAsync(int port)
        {
            try { while (!stop.IsCancellationRequested) tunnels.Add(TunnelAsync(await listener.AcceptTcpClientAsync(stop.Token), port)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        private async Task TunnelAsync(TcpClient incoming, int port)
        {
            using (incoming)
            using (var outgoing = new TcpClient())
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                try
                {
                    await outgoing.ConnectAsync(IPAddress.Loopback, port, lifetime.Token);
                    var upload = incoming.GetStream().CopyToAsync(outgoing.GetStream(), lifetime.Token);
                    var download = DownloadAsync(outgoing.GetStream(), incoming.GetStream(), lifetime.Token);
                    await Task.WhenAny(upload, download); lifetime.Cancel(); await Task.WhenAll(upload, download);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        private async Task DownloadAsync(Stream source, Stream destination, CancellationToken ct)
        {
            byte[] buffer = new byte[4096]; int count;
            while ((count = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                if (Throttle) await Task.Delay(200, ct);
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); await accepting; listener.Stop(); await Task.WhenAll(tunnels); stop.Dispose();
        }
    }
}
