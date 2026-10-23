using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class ConnectionRecoveryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        // A long agent operation must not prevent the transport from answering a ping.
        var transport = new ProbeTransport();
        await using (var peer = new RpcPeer(transport))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.Handler = async (_, _, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return null; };
            peer.Start();
            await transport.Input.Writer.WriteAsync(Json.Node(new { id = "send", method = "send" }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await transport.Input.Writer.WriteAsync(Json.Node(new { id = "ping", method = "transport-ping" }));
            var pong = await transport.Output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            check(pong["id"]?.ToString() == "ping" && pong["result"]?.GetValue<bool>() == true,
                "transport heartbeat answers while an agent operation is pending");
            release.TrySetResult();
        }

        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
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
            await using var host = new DeviceHost(registry, () => new TestAdapter(), enableLan: false);
            await host.StartAsync("Connection recovery smoke");
            await using var gateway = new BlackholeGateway(port);
            await using var mobile = new MobileSession(a => a());
            await mobile.ConnectAsync(gateway.Url, host.Lease!.Code);
            await mobile.SelectAsync("test");
            await mobile.ResumeAsync();
            check(mobile.Connected && mobile.View?.Id == "test", "mobile resume retains the live connection and selected session");
            gateway.Drop = true;
            await Until(() => !mobile.Connected, 14);
            check(!mobile.Connected, "heartbeat detects a silent network blackhole without waiting for manual reconnect");
            gateway.Drop = false;
            await Until(() => mobile.Connected && !mobile.Busy, 45);
            await mobile.ResumeAsync();
            check(mobile.Connected && mobile.View?.Id == "test" && TestAdapter.Sends == 0,
                "network recovery automatically restores the session without replaying messages");
            await mobile.DisconnectAsync();
            await mobile.ResumeAsync();
            check(!mobile.HasConnection && !mobile.Connected, "foreground recovery respects explicit disconnect");
        }
        finally { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } }
    }

    private static async Task Until(Func<bool> condition, int seconds)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(100, stop.Token);
    }

    private sealed class ProbeTransport : IJsonTransport
    {
        public Channel<JsonNode> Input { get; } = Channel.CreateUnbounded<JsonNode>();
        public Channel<JsonNode> Output { get; } = Channel.CreateUnbounded<JsonNode>();
        public async Task<JsonNode> ReadAsync(CancellationToken ct) => await Input.Reader.ReadAsync(ct);
        public async Task WriteAsync(JsonNode message, CancellationToken ct) => await Output.Writer.WriteAsync(message, ct);
        public ValueTask DisposeAsync() { Input.Writer.TryComplete(); Output.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    // Loopback-only network interruption; never changes the user's NIC, proxy or VPN.
    private sealed class BlackholeGateway : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> tunnels = [];
        private readonly Task accepting;
        public volatile bool Drop;
        public string Url { get; }
        public BlackholeGateway(int targetPort)
        {
            listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            accepting = AcceptAsync(targetPort);
        }
        private async Task AcceptAsync(int targetPort)
        {
            try { while (!stop.IsCancellationRequested) tunnels.Add(TunnelAsync(await listener.AcceptTcpClientAsync(stop.Token), targetPort)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        private async Task TunnelAsync(TcpClient incoming, int targetPort)
        {
            using (incoming)
            using (var outgoing = new TcpClient())
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                try
                {
                    await outgoing.ConnectAsync(IPAddress.Loopback, targetPort, lifetime.Token);
                    var upload = CopyAsync(incoming.GetStream(), outgoing.GetStream(), lifetime.Token);
                    var download = CopyAsync(outgoing.GetStream(), incoming.GetStream(), lifetime.Token);
                    await Task.WhenAny(upload, download); lifetime.Cancel();
                    await Task.WhenAll(upload, download);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
            }
        }
        private async Task CopyAsync(Stream source, Stream destination, CancellationToken ct)
        {
            byte[] buffer = new byte[16384];
            while (await source.ReadAsync(buffer, ct) is int count && count > 0)
                if (!Drop) await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); await accepting; listener.Stop(); await Task.WhenAll(tunnels); stop.Dispose();
        }
    }
}
