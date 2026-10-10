using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FurtherAgent.Cli;
using FurtherAgent.Core;

// Explicit opt-in: creates one dedicated session and submits one tool-free test prompt.
internal static class DshLiveChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        await using var adapter = new DshAdapter(DshAdapter.FindDescriptor("desktop"));
        var info = await adapter.ProbeAsync(ct);
        check(info?["mode"]?.ToString() == "desktop", "connected to existing desktop plugin");
        check(info?["hostVersion"]?.ToString() != "unknown", "actual Host runtime version is reported");
        string id = Guid.NewGuid().ToString("N");
        var created = await adapter.CreateAsync(id, "FurtherAgent DSH 端到端验收", ct);
        check(created.Id == id, "native same-Host session creation preserves request identity");
        var first = await adapter.AttachAsync(id, ct);
        await using var observer = new DshAdapter(DshAdapter.FindDescriptor("desktop"));
        await observer.ProbeAsync(ct);
        var second = await observer.AttachAsync(id, ct);
        check(first.Messages.Count == 0 && second.Messages.Count == 0, "two independent readers attach to a Host-owned session without writer-held");

        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string configuration = AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, $"src/FurtherAgent.Server/bin/{configuration}/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        using var server = Process.Start(start)!;
        server.BeginOutputReadLine(); server.BeginErrorReadLine();
        string? oldCode = Environment.GetEnvironmentVariable("FURTHERAGENT_CODE");
        string? oldFile = Environment.GetEnvironmentVariable("FURTHERAGENT_CODE_FILE");
        try
        {
            using var registry = new RegistryClient($"http://127.0.0.1:{port}");
            using var http = new HttpClient();
            for (int attempt = 0; ; attempt++)
            {
                try { if ((await http.GetAsync($"http://127.0.0.1:{port}/health", ct)).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                if (attempt == 40) throw new IOException("Test relay did not start");
                await Task.Delay(150, ct);
            }
            await using var host = new DeviceHost(registry, () => new BorrowedAgentAdapter(adapter));
            await host.StartAsync("DSH 专用联调", ct);
            Environment.SetEnvironmentVariable("FURTHERAGENT_CODE", host.Lease!.Code);
            Environment.SetEnvironmentVariable("FURTHERAGENT_CODE_FILE", null);
            async Task<string> Cli(params string[] arguments)
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                int exit = await CliRunner.RunAsync([.. arguments, "--server", $"http://127.0.0.1:{port}"], output, error, ct);
                if (exit != 0) throw new IOException(error.ToString());
                string result = output.ToString();
                check(!result.Contains(host.Lease.Code), "CLI never emits pairing secret");
                await Task.Delay(400, ct);
                return result;
            }
            check((await Cli("read", "--session", id)).Contains(id), "CLI reads desktop-owned session through actual relay");
            check((await Cli("settings", "--session", id)).Contains("deepseek-flash"), "CLI receives actual native model catalogue through shared adapter");
            var settings = await adapter.SettingsAsync(ct);
            await Cli("configure", "--session", id, "--model", settings.Model, "--provider", settings.Provider, "--mode", "plan");
            check((await adapter.AttachAsync(id, ct)).Mode == "plan", "native Plan selection reads back through same Host");
            await Cli("configure", "--session", id, "--model", settings.Model, "--provider", settings.Provider, "--mode", "default");
            check((await adapter.AttachAsync(id, ct)).Mode == "default", "native Build selection restores without submitting a turn");
            var observed = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
            observer.Changed += view => { if (view.Messages.Any(m => m.Role == "assistant" && m.Text.Contains("FA_DSH_RELAY_OK"))) observed.TrySetResult(view); };
            await Cli("send", "--session", id, "--text", "FurtherAgent 专用联调：不要调用工具、不要读写文件，只回复 FA_DSH_RELAY_OK。", "--seconds", "2");
            var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
            check(result.Messages.Count(m => m.Role == "user") == 1, "other reader receives same-Host update with exactly one user message");
            // Reconnect/read does not replay the message or create a second top-level Agent.
            await Cli("read", "--session", id);
            var final = await adapter.AttachAsync(id, ct);
            check(final.Messages.Count(m => m.Role == "user") == 1, "CLI reconnect does not resubmit the prompt");
            check(final.Messages.Count(m => m.Role == "assistant" && m.Text.Contains("FA_DSH_RELAY_OK")) == 1, "stream and committed assistant response are not duplicated");
            check(!final.Messages.Any(m => m.Role == "tool.call"), "dedicated live prompt executed no tools");
            await Cli("archive", "--session", id);
            check(!(await adapter.ListAsync(ct)).Any(s => s.Id == id), "native archive removes session from visible roster");
            await Cli("unarchive", "--session", id);
            check((await adapter.ListAsync(ct)).Any(s => s.Id == id), "native unarchive restores same session without deleting history");
            Console.WriteLine("Dedicated DSH test session: " + id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FURTHERAGENT_CODE", oldCode);
            Environment.SetEnvironmentVariable("FURTHERAGENT_CODE_FILE", oldFile);
            if (!server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
        }
    }
}
