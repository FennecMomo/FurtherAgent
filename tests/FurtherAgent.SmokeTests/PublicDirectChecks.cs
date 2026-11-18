using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FurtherAgent.Core;

internal static class PublicDirectChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string directory = Path.Combine(root, "artifacts", "checks", "public-direct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int port = ReserveLoopbackPort();
        using var server = StartRelay(root, directory, port);
        string? oldStun = Environment.GetEnvironmentVariable("FURTHERAGENT_STUN");
        string? oldP2p = Environment.GetEnvironmentVariable("FURTHERAGENT_P2P");
        Environment.SetEnvironmentVariable("FURTHERAGENT_STUN", "off");
        Environment.SetEnvironmentVariable("FURTHERAGENT_P2P", "1");
        try
        {
            using var registry = new RegistryClient($"http://127.0.0.1:{port}");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            CancellationToken ct = deadline.Token;
            await WaitForRelayAsync(registry, ct);

            await using (var legacy = new DeviceHost(registry, () => new TestAdapter(),
                Path.Combine(directory, "legacy-files"), enableLan: false, enableP2p: false))
            {
                await legacy.StartAsync("Relay fixture without P2P", ct);
                await using var client = new RemoteDevice();
                await client.ConnectAsync(registry, legacy.Lease!.Code, ct);
                var session = Json.Read<SessionView>(await client.Peer.CallAsync("attach", new { id = "test" }, ct));
                check(session.Id == "test" && client.ConnectionKind == "服务器中继",
                    "a host without P2P capability continues serving sessions over the relay");
            }

            await using var host = new DeviceHost(registry, () => new TestAdapter(),
                Path.Combine(directory, "files"), enableLan: false, enableP2p: true);
            await host.StartAsync("Public direct fixture", ct);
            await using var peer = new RemoteDevice();
            await peer.ConnectAsync(registry, host.Lease!.Code, ct);
            await peer.Peer.CallAsync("attach", new { id = "test" }, ct);
            int sends = Volatile.Read(ref TestAdapter.Sends);
            await peer.Peer.CallAsync("send", new { text = "public direct fixture" }, ct);
            await WaitForDirectAsync(peer, ct);
            check(peer.IsConnected && peer.ConnectionKind == "点对点直连",
                "compatible peers negotiate a direct data connection using only local ICE candidates");
            check(Volatile.Read(ref TestAdapter.Sends) == sends + 1,
                "a change of transport does not replay a submitted message");

            await using (var reconnecting = new RemoteDevice())
            {
                await reconnecting.ConnectAsync(registry, host.Lease.Code, ct);
                await WaitForDirectAsync(reconnecting, ct);
                var direct = reconnecting.Peer;
                await direct.DisposeAsync();
                reconnecting.WakeReconnect();
                while (reconnecting.ReconnectCount == 0) await Task.Delay(100, ct);
                var restored = Json.Read<SessionView>(await reconnecting.Peer.CallAsync("attach", new { id = "test" }, ct));
                check(reconnecting.ConnectionKind == "服务器中继" && restored.Id == "test",
                    "after direct transport loss, a new relay connection can read the previous session");
            }

            string source = Path.Combine(directory, "payload.bin");
            string destination = Path.Combine(directory, "download.bin");
            byte[] expected = RandomNumberGenerator.GetBytes(390_241);
            await File.WriteAllBytesAsync(source, expected, ct);

            await StopRelayAsync(server);
            check(peer.IsConnected && await peer.CheckConnectionAsync(ct),
                "closing the coordination relay leaves the established direct data connection usable");
            var rootFiles = new FileTransferClient(peer.Peer);
            var uploaded = await rootFiles.UploadAsync("test", source, ct: ct);
            await using var mini = new RemoteSessionConnection(peer, registry, host.Lease.Code);
            await mini.ConnectAsync(ct);
            var miniView = Json.Read<SessionView>(await mini.Peer.CallAsync("attach", new { id = "test" }, ct));
            check(mini.IsSharedConnection && miniView.Id == "test",
                "a Mini session channel shares the direct transport after the relay stops");
            await new FileTransferClient(mini.Peer).DownloadAsync("test", uploaded.Id, destination, ct: ct);
            check((await File.ReadAllBytesAsync(destination, ct)).AsSpan().SequenceEqual(expected),
                "a direct connection transfers multiple full-size file chunks in both directions with the relay stopped");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FURTHERAGENT_STUN", oldStun);
            Environment.SetEnvironmentVariable("FURTHERAGENT_P2P", oldP2p);
            await StopRelayAsync(server);
        }
    }

    private static async Task WaitForDirectAsync(RemoteDevice peer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A foreground RPC can defer the first migration. Allow the background retry.
        timeout.CancelAfter(TimeSpan.FromSeconds(48));
        try
        {
            while (peer.ConnectionKind != "点对点直连") await Task.Delay(100, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The isolated peers did not upgrade from relay to public direct.");
        }
    }

    private static async Task WaitForRelayAsync(RegistryClient registry, CancellationToken ct)
    {
        while (true)
        {
            try { await registry.CheckHealthAsync(ct); return; }
            catch (HttpRequestException) { await Task.Delay(100, ct); }
        }
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Process StartRelay(string root, string directory, int port)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["FURTHERAGENT_MAILBOX_ROOT"] = Path.Combine(directory, "mailboxes");
        var server = Process.Start(start) ?? throw new IOException("Could not start isolated relay.");
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        return server;
    }

    private static async Task StopRelayAsync(Process server)
    {
        if (server.HasExited) return;
        server.Kill(entireProcessTree: true);
        await server.WaitForExitAsync();
    }
}
