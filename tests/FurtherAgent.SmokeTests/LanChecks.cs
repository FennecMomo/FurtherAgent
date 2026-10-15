using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FurtherAgent.Core;

internal static class LanChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "fa-lan-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string? oldIdentity = Environment.GetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE"), oldTrust = Environment.GetEnvironmentVariable("FURTHERAGENT_LAN_TRUST_DIR");
        Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE", Path.Combine(directory, "identity"));
        Environment.SetEnvironmentVariable("FURTHERAGENT_LAN_TRUST_DIR", Path.Combine(directory, "trust"));
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            using var registry = new RegistryClient($"http://127.0.0.1:{port}");
            for (int i = 0; ; i++) { try { await registry.CheckHealthAsync(default); break; } catch { if (i > 30) throw; await Task.Delay(100); } }
            await using var host = new DeviceHost(registry, () => new TestAdapter(), Path.Combine(directory, "files"), persistentIdentity: true);
            host.Status += Console.WriteLine; await host.StartAsync("LAN fixture"); string code = host.Lease!.Code;
            await using var client = new RemoteDevice(); await client.ConnectAsync(registry, code);
            check(client.ConnectionKind == "局域网直连", "first trusted relay pairing upgrades to LAN");
            var trust = LanTrustStore.Load(registry.ServerIdentity, code);
            check(trust is not null && trust.Token.Length == 64, "relay authenticated LAN identity and token persist");
            if (OperatingSystem.IsWindows()) check(!File.ReadAllText(Directory.GetFiles(Path.Combine(directory, "trust")).Single()).Contains(trust!.Token), "Windows LAN credentials are protected with current-user DPAPI");
            await client.Peer.CallAsync("attach", new { id = "test" });
            int sends = TestAdapter.Sends; await client.Peer.CallAsync("send", new { text = "LAN request" });
            check(TestAdapter.Sends == sends + 1, "LAN uses existing adapter RPC without duplicate sends");
            await host.SetLanEnabledAsync(false);
            for (int i = 0; i < 150 && (!client.IsConnected || client.ConnectionKind != "服务器中继"); i++) await Task.Delay(100);
            check(client.IsConnected && client.ConnectionKind == "服务器中继", "LAN loss automatically falls back to relay");
            check(TestAdapter.Sends == sends + 1, "route recovery never replays a submitted message");
            await client.Peer.CallAsync("attach", new { id = "test" });
            var stale = client.Peer;
            await host.SetLanEnabledAsync(true);
            for (int i = 0; i < 230 && client.ConnectionKind != "局域网直连"; i++) await Task.Delay(100);
            check(client.ConnectionKind == "局域网直连", "idle relay connection automatically prefers returning LAN");
            check(await stale.CallAsync("agents", null) is not null, "calls captured before migration forward once to the live connection");
            server.Kill(true); await server.WaitForExitAsync();
            await using var offline = new RemoteDevice(false); await offline.ConnectAsync(registry, code);
            check(offline.ConnectionKind == "局域网直连" && offline.IsConnected, "cached LAN pairing connects with relay fully stopped");
            await offline.Peer.CallAsync("attach", new { id = "test" });
            var files = await offline.Peer.CallAsync("files/list", new { session = "test" });
            check(files is not null, "offline LAN retains existing file workspace protocol");
            var transfer = new FileTransferClient(offline.Peer);
            string input = Path.Combine(directory, "payload.bin"), output = Path.Combine(directory, "download.bin");
            byte[] data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(200000); await File.WriteAllBytesAsync(input, data);
            var upload = await transfer.UploadAsync("test", input); await transfer.DownloadAsync("test", upload.Id, output);
            check((await File.ReadAllBytesAsync(output)).SequenceEqual(data), "offline LAN uploads and downloads multiple verified file chunks");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            check(await LanHost.ConnectAsync(trust! with { Token = new string('0', 64) }, stop.Token) is null, "wrong LAN token rejected before RPC");
            check(await LanHost.ConnectAsync(trust! with { CertificateSha256 = new string('0', 64) }, default) is null, "discovery cannot replace pinned device identity");
            await offline.DisposeAsync(); await client.DisposeAsync(); await host.DisposeAsync();
            await using var offlineHost = new DeviceHost(registry, () => new TestAdapter(), persistentIdentity: true);
            await offlineHost.StartAsync("LAN fixture");
            await using var resumed = new RemoteDevice(false); await resumed.ConnectAsync(registry, code);
            check(resumed.IsConnected && offlineHost.Lease!.Code == code, "host starts offline with unchanged code and TLS identity");
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE", oldIdentity); Environment.SetEnvironmentVariable("FURTHERAGENT_LAN_TRUST_DIR", oldTrust);
            if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path");
            Directory.Delete(directory, true);
        }
    }
}
