using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FurtherAgent.Core;

internal static class DirectNetworkChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        var originalProxy = HttpClient.DefaultProxy;
        var brokenProxy = new BrokenProxy();
        try
        {
            // Process-local fixture only: no Windows/VPN settings are changed.
            HttpClient.DefaultProxy = brokenProxy;
            bool defaultFailed = false;
            using (var ordinary = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                try { await ordinary.GetAsync($"http://127.0.0.1:{port}/health"); }
                catch (Exception) { defaultFailed = true; }
            check(defaultFailed && brokenProxy.Calls > 0, "fixture rejects traffic inheriting the default proxy, including loopback");
            brokenProxy.Calls = 0;
            using var registry = new RegistryClient($"http://127.0.0.1:{port}");
            for (int i = 0; ; i++)
            {
                try { await registry.CheckHealthAsync(default); break; }
                catch { if (i >= 40) throw; await Task.Delay(100); }
            }
            check(brokenProxy.Calls == 0, "relay HTTP health succeeds without consulting the broken default proxy");
            await using var host = new DeviceHost(registry, () => new TestAdapter(), enableLan: false);
            await host.StartAsync("Direct network smoke");
            await using var remote = new RemoteDevice(false);
            await remote.ConnectAsync(registry, host.Lease!.Code);
            check(remote.IsConnected && await remote.CheckConnectionAsync() && brokenProxy.Calls == 0,
                "pairing, host/control WebSockets and RPC heartbeat stay direct without proxy fallback");
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
        }
    }

    private sealed class BrokenProxy : IWebProxy
    {
        public int Calls;
        public ICredentials? Credentials { get; set; }
        public bool IsBypassed(Uri host) { Interlocked.Increment(ref Calls); return false; }
        public Uri GetProxy(Uri destination) { Interlocked.Increment(ref Calls); return new Uri("http://127.0.0.1:1"); }
    }
}
