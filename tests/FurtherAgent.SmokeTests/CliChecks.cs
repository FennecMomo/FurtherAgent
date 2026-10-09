using System.Text.Json.Nodes;
using FurtherAgent.Core;
using FurtherAgent.Cli;

static class CliChecks
{
    public static async Task RunAsync(RegistryClient registry, string address, Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = timeout.Token;
        string testRoot = Path.Combine(Path.GetTempPath(), "fa-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var api = new HermesFixture();
        await using var adapter = new HermesAdapter("http://127.0.0.1:8642", "test-only-secret", api);
        await adapter.ProbeAsync(ct);
        await using var host = new DeviceHost(registry, () => new BorrowedAgentAdapter(adapter), Path.Combine(testRoot, "host"));
        await host.StartAsync("CLI fixture", ct);
        string codeFile = Path.Combine(Path.GetTempPath(), "fa-cli-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(codeFile, host.Lease!.Code, ct);
        try
        {
            async Task<(int Exit, string Output, string Error)> Run(params string[] arguments)
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                int exit = await CliRunner.RunAsync([.. arguments, "--server", address, "--code-file", codeFile], output, error, ct);
                await Task.Delay(180, ct); // Allow the previous controller's relay socket to finish closing.
                check(!(output + error.ToString()).Contains(host.Lease.Code), "CLI never emits authorization code");
                foreach (string line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)) _ = JsonNode.Parse(line);
                return (exit, output.ToString(), error.ToString());
            }
            var result = await Run("probe", "--seconds", "1");
            check(result.Exit == 0 && result.Output.Contains("heartbeat") && result.Output.Contains("session-count"), "CLI headless probe traverses real relay and returns JSON Lines");
            result = await Run("send", "--session", "weixin-session", "--text", "x", "--text-file", "missing");
            check(result.Exit == 1 && api.Submits == 0, "CLI rejects conflicting send arguments before mutation");
            string id = Guid.NewGuid().ToString("N");
            result = await Run("create", "--request-id", id, "--title", "CLI test");
            var retry = await Run("create", "--request-id", id, "--title", "CLI test");
            check(result.Exit == 0 && retry.Exit == 0 && api.Created == 1, "CLI explicit create retry preserves one session");
            result = await Run("settings", "--session", "weixin-session");
            check(result.Exit == 0 && result.Output.Contains("model-b"), "CLI reads remote model settings");
            result = await Run("configure", "--session", "weixin-session", "--model", "model-b", "--provider", "test");
            check(result.Exit == 0 && api.ModelUpdates == 1, "CLI applies remote session model setting");
            string local = Path.Combine(testRoot, "sample.txt"), downloaded = Path.Combine(testRoot, "copy.txt");
            await File.WriteAllTextAsync(local, "CLI 文件传输验证", ct);
            result = await Run("upload", "--session", "weixin-session", "--path", local);
            string fileId = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!)
                .Single(node => node["type"]!.ToString() == "uploaded")["data"]!["id"]!.ToString();
            check(result.Exit == 0, "CLI upload uses shared transfer service");
            result = await Run("files", "--session", "weixin-session");
            check(result.Exit == 0 && result.Output.Contains(fileId), "CLI lists uploaded file IDs");
            result = await Run("download", "--session", "weixin-session", "--id", fileId, "--output", downloaded);
            check(result.Exit == 0 && await File.ReadAllTextAsync(downloaded, ct) == "CLI 文件传输验证", "CLI download uses shared integrity-checked transfer");
            result = await Run("send", "--session", "weixin-session", "--text", "continue", "--file-id", fileId);
            check(result.Exit == 1 && api.Submits == 1, "CLI reports uncertain send without automatic replay");
            result = await Run("send", "--session", "weixin-session", "--text", "continue", "--file-id", fileId, "--seconds", "1");
            check(result.Exit == 0 && api.Submits == 2 && api.RunCount == 1 && result.Output.Contains("accepted"), "CLI explicit retry recovers same owned run");
            result = await Run("watch", "--session", "weixin-session", "--seconds", "1");
            check(result.Exit == 0 && result.Output.Contains("session") && result.Output.Contains("observation-ended"), "CLI watches an existing session without sending");
            result = await Run("stop", "--session", "weixin-session");
            check(result.Exit == 0 && api.Stops == 1, "CLI stops owned run through relay");
        }
        finally { await host.DisposeAsync(); File.Delete(codeFile); Directory.Delete(testRoot, true); }
    }
}
