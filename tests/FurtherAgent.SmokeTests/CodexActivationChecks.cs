using FurtherAgent.Core;
using System.IO.Pipes;
using System.Text.Json.Nodes;

internal static class CodexActivationChecks
{
    // Explicit opt-in: opens an existing desktop page, but never sends a turn.
    public static async Task RunLive(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        string? original = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        await using var adapter = new CodexDesktopAdapter();
        using var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ct);
        string client = "activation-test";
        async Task<JsonNode> Request(string method, object parameters, int version)
        {
            string id = Guid.NewGuid().ToString();
            await Frames.WriteAsync(pipe, Json.Node(new { type = "request", requestId = id, sourceClientId = client,
                method, @params = parameters, version, timeoutMs = 2000 }), ct);
            while (true)
            {
                var reply = await Frames.ReadDesktopAsync(pipe, ct);
                if (reply["type"]?.ToString() == "response" && reply["requestId"]?.ToString() == id) return reply;
                if (reply["type"]?.ToString() == "client-discovery-request")
                    await Frames.WriteAsync(pipe, Json.Node(new { type = "client-discovery-response", requestId = reply["requestId"]!.ToString(), response = new { canHandle = false } }), ct);
            }
        }
        var initialized = await Request("initialize", new { clientType = "furtheragent-activation-test" }, 0);
        client = initialized["result"]!["clientId"]!.ToString();
        string? target = null;
        foreach (var session in (await adapter.ListAsync(ct)).Where(s => s.Id != original && s.Title != "Guardian review").Take(10))
        {
            var before = await Request("thread-owner-discovery", new { hostId = "local", conversationId = session.Id }, 1);
            if (before["error"]?.ToString() == "no-client-found") { target = session.Id; break; }
        }
        check(target is not null, "live fixture has no loaded desktop owner before activation");
        pipe.Dispose(); // Do not leave an idle discovery participant while attaching.
        try
        {
            var view = await adapter.AttachAsync(target!, ct);
            check(view.Id == target, "previously unloaded live thread opens and returns matching snapshot");
            Console.WriteLine($"LIVE ACTIVATED: {target}; {view.Messages.Count} messages; no turns sent.");
        }
        finally { if (original is not null) CodexThreadActivation.Open(CodexThreadActivation.ThreadUrl(original)); }
    }
    public static async Task Run(Action<bool, string> check)
    {
        string id = Guid.NewGuid().ToString();
        int launches = 0, probes = 0;
        string? url = null;
        var owner = await CodexThreadActivation.ActivateAsync(id, value => { launches++; url = value; },
            _ => Task.FromResult(++probes < 3 ? null : "desktop-owner"), default, retryDelay: TimeSpan.Zero);
        check(owner == "desktop-owner" && launches == 1 && probes == 3 && url == $"codex://threads/{id}",
            "remote selection opens exact desktop thread once and waits for owner");
        int slowProbes = 0, slowLaunches = 0;
        owner = await CodexThreadActivation.ActivateAsync(id, _ => slowLaunches++, _ =>
        {
            if (++slowProbes == 1) throw new TimeoutException();
            return Task.FromResult<string?>("ready");
        }, default, retryDelay: TimeSpan.Zero);
        check(owner == "ready" && slowProbes == 2 && slowLaunches == 1, "slow owner discovery retries without reopening desktop");
        bool rejected = false;
        try { await CodexThreadActivation.ActivateAsync("bad?command=run", _ => launches++, _ => Task.FromResult<string?>("owner"), default); }
        catch (ArgumentException) { rejected = true; }
        check(rejected && launches == 1, "invalid thread ID cannot launch a URL");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        rejected = false;
        try { await CodexThreadActivation.ActivateAsync(id, _ => launches++, _ => Task.FromResult<string?>("owner"), cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected && launches == 1, "cancelled selection does not switch desktop");
        rejected = false;
        try { await CodexThreadActivation.ActivateAsync(id, _ => { }, _ => Task.FromResult<string?>(null), default,
            timeout: TimeSpan.FromMilliseconds(25), retryDelay: TimeSpan.FromMilliseconds(5)); }
        catch (IOException ex) { rejected = ex.Message.Contains("等待加载超时"); }
        check(rejected, "unavailable desktop owner produces bounded actionable timeout");
        probes = 0; rejected = false;
        try { await CodexThreadActivation.ActivateAsync(id, _ => { }, _ => { probes++; throw new IOException("incompatible"); }, default); }
        catch (IOException ex) { rejected = ex.Message == "incompatible"; }
        check(rejected && probes == 1, "protocol errors propagate without repeating navigation");
        probes = 0; rejected = false;
        try { await CodexThreadActivation.ActivateAsync(id, _ => throw new IOException("launch failed"),
            _ => { probes++; return Task.FromResult<string?>("owner"); }, default); }
        catch (IOException ex) { rejected = ex.Message == "launch failed"; }
        check(rejected && probes == 0, "failed navigation never attaches another owner");
    }
}
