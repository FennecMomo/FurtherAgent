using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

static class HermesChecks
{
    public static async Task RunAsync(RegistryClient registry, Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = timeout.Token;
        var api = new HermesFixture();
        await using var adapter = new HermesAdapter("http://127.0.0.1:8642", "test-only-secret", api);
        await adapter.ProbeAsync(ct);
        await using var host = new DeviceHost(registry, () => new BorrowedAgentAdapter(adapter));
        await host.StartAsync("Hermes fixture", ct);
        await using var remote = new RemoteDevice(false);
        await remote.ConnectAsync(registry, host.Lease!.Code);
        var agents = Json.Read<AgentInfo[]>(await remote.Peer.CallAsync("agents", null));
        check(agents.Single().Id == "hermes", "Hermes agent discovery through relay");
        var sessions = Json.Read<SessionInfo[]>(await remote.Peer.CallAsync("sessions", null));
        check(sessions.Single().Id == "weixin-session", "Hermes existing session list");
        var view = Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id = "weixin-session" }));
        check(view.Messages.Single().Text == "微信历史" && view.Status == "unknown" && view.CanStop == false,
            "Hermes history does not claim ownership of Weixin running state");
        check(agents.Single().CanCreate && agents.Single().CanConfigure, "new host advertises create and settings capabilities");
        check(agents.Single().CanArchive, "Hermes advertises native archive capability");
        await remote.Peer.CallAsync("archive", new { session = "weixin-session", archived = true });
        check(api.Archived, "archive forwards boolean PATCH and verifies readback");
        await remote.Peer.CallAsync("archive", new { session = "weixin-session", archived = false });
        check(!api.Archived, "unarchive uses PATCH false without deleting history");
        bool crossArchive = false;
        try { await remote.Peer.CallAsync("archive", new { session = "different", archived = true }); } catch { crossArchive = true; }
        check(crossArchive && !api.Archived, "archive rejects session not attached by controller");
        var settings = Json.Read<AgentSettings>(await remote.Peer.CallAsync("settings", null));
        check(settings.Model == "model-a" && settings.Models.Single().Id == "model-b" && !settings.CanChangeMode,
            "Hermes configured model options parsed and unavailable models excluded");
        await remote.Peer.CallAsync("configure", new { model = "model-b", provider = "test", mode = "default" });
        check(api.ModelLock?["model"]?.ToString() == "model-b" && api.ModelLock?["provider"]?.ToString() == "test", "model change uses session model lock endpoint");
        bool planRejected = false;
        try { await remote.Peer.CallAsync("configure", new { model = "model-b", provider = "test", mode = "plan" }); } catch { planRejected = true; }
        check(planRejected && api.ModelUpdates == 1, "Hermes rejects unsupported Plan without writing settings");
        string createId = Guid.NewGuid().ToString("N");
        var created = Json.Read<SessionInfo>(await remote.Peer.CallAsync("create", new { id = createId, title = "测试会话" }));
        var recovered = Json.Read<SessionInfo>(await remote.Peer.CallAsync("create", new { id = createId, title = "测试会话" }));
        check(created.Id == recovered.Id && api.Created == 1 && api.Submits == 0, "stable create ID recovers existing empty session without running Agent");
        bool lostReply = false;
        try { await remote.Peer.CallAsync("send", new { text = "continue" }); } catch { lostReply = true; }
        check(lostReply, "ambiguous Hermes submit returns an error without automatic replay");
        bool blocked = false;
        try { await remote.Peer.CallAsync("send", new { text = "different" }); } catch { blocked = true; }
        bool settingsBlocked = false;
        try { await remote.Peer.CallAsync("configure", new { model = "other", provider = "test", mode = "default" }); } catch { settingsBlocked = true; }
        check(settingsBlocked && api.ModelUpdates == 1, "ambiguous submission prevents changing execution model");
        check(blocked && api.Submits == 1, "different message blocked until ambiguous submission reconciled");
        var changed = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.Notification += (method, node) =>
        { if (method == "session" && node is not null) { var v = Json.Read<SessionView>(node); if (v.Messages.Any(m => m.Text == "你好")) changed.TrySetResult(v); } };
        await remote.Peer.CallAsync("send", new { text = "continue" });
        view = await changed.Task.WaitAsync(ct);
        check(api.Submits == 2 && api.RunCount == 1 && api.LastBody?["session_id"]?.ToString() == "weixin-session",
            "same idempotency key recovers run after lost reply without duplicate execution");
        check(api.LastBody?.AsObject().Count == 2, "Hermes submit inherits server model settings without overriding provider");
        check(view.Messages.Any(m => m.Text == "你好"), "SSE multiline JSON and UTF-8 deltas reach relay controller");
        check(view.Messages.Any(m => m.Role == "tool.completed" && m.Detail?.Contains("duration") == true && m.Detail.Contains("error")), "tool event duration and error survive relay projection");
        check(view.Messages.Any(m => m.Role == "reasoning.available" && m.Text.Contains("public summary")), "provided reasoning event is retained");
        await Task.Delay(2400, ct);
        await remote.DisposeAsync();
        await Task.Delay(400, ct);
        await using var reconnect = new RemoteDevice(false);
        await reconnect.ConnectAsync(registry, host.Lease!.Code);
        view = Json.Read<SessionView>(await reconnect.Peer.CallAsync("attach", new { id = "weixin-session" }));
        check(view.TurnId == "run-one" && api.Streams == 1 && view.StatusDetail!.Contains("降级"),
            "relay reconnect retains run ownership; lost Hermes SSE uses polling without resubscription");
        check(view.Prompts.Single().Id == "approval-one", "Hermes approval recovered from polled run status");
        await reconnect.Peer.CallAsync("respond", new { id = "approval-one", decision = "accept" });
        check(api.Approval?["choice"]?.ToString() == "once" && api.Approval?["request_id"]?.ToString() == "approval-one",
            "approval targets exact request and only grants once");
        await Task.Delay(2200, ct);
        await reconnect.Peer.CallAsync("send", new { text = "steer" });
        check(api.Steer?["input"]?.ToString() == "steer" && api.Submits == 2, "active Hermes input steers existing run");
        await reconnect.Peer.CallAsync("interrupt", null);
        check(api.Stops == 1, "stop addresses owned Hermes run");
        api.LoseStatus = true;
        await Task.Delay(2300, ct);
        view = await adapter.AttachAsync("weixin-session", ct);
        // A fresh history read must not erase unknown run status or re-enable mutations.
        check(view.CanStop == false && view.CanSend == false, "lost run status remains fail-closed after reattach");
        bool unavailableRejected = false;
        try { await adapter.SendAsync("do not replay", ct); } catch (IOException) { unavailableRejected = true; }
        check(unavailableRejected && api.Submits == 2, "adapter rejects mutations while Hermes run status is unknown");

        var liveApi = new HermesFixture { KeepStream = true };
        await using var liveAdapter = new HermesAdapter("http://127.0.0.1:8642", "test-only-secret", liveApi);
        await liveAdapter.ProbeAsync(ct);
        var borrowed = new BorrowedAgentAdapter(liveAdapter);
        await borrowed.AttachAsync("weixin-session", ct);
        try { await borrowed.SendAsync("continue", ct); } catch (HttpRequestException) { }
        await borrowed.SendAsync("continue", ct);
        while (liveApi.LiveStream is null) await Task.Delay(25, ct);
        await borrowed.DisposeAsync();
        await Task.Delay(100, ct);
        check(!liveApi.LiveStream.Disposed, "disposing controller adapter keeps actual open Hermes SSE stream alive");
        await liveAdapter.DisposeAsync();
        check(liveApi.LiveStream.Disposed, "host shutdown closes owned Hermes SSE stream");
    }
}

sealed class HermesFixture : HttpMessageHandler
{
    public int Submits, RunCount, Streams, Stops, Created, ModelUpdates;
    public JsonNode? ModelLock;
    private string? createdId;
    public bool LoseStatus;
    public bool Archived;
    public bool KeepStream;
    public HeldStream? LiveStream;
    public JsonNode? LastBody, Approval, Steer;
    private string? key;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization?.ToString() != "Bearer test-only-secret") throw new Exception("missing auth");
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
        string path = request.RequestUri!.AbsolutePath;
        if (path == "/v1/capabilities") return Reply(new { features = new { session_resources = true, run_submission = true, run_status = true,
            model_options = true, session_model_lock = true, run_events_sse = true, run_stop = true, run_steer = true, run_approval_response = true, runs_idempotency = new { supported = true, durable = true } } });
        if (path == "/api/model/options") return Reply(new { model = "model-a", provider = "test", providers = new[] { new { slug = "test", models = new[] { "model-b", "unavailable" }, unavailable_models = new[] { "unavailable" } } } });
        if (path.EndsWith("/model")) { ModelLock = body; ModelUpdates++; return Reply(new { runtime = new { model = body?["model"]?.ToString() } }); }
        if (path == "/api/sessions" && request.Method == HttpMethod.Post)
        {
            if (createdId is not null) return new HttpResponseMessage(HttpStatusCode.Conflict);
            createdId = body?["id"]?.ToString(); Created++;
            return Reply(new { session = new { id = createdId, title = "测试会话" } });
        }
        if (path == "/api/sessions/" + createdId) return Reply(new { session = new { id = createdId, title = "测试会话" } });
        if (path == "/api/sessions") return Reply(new { data = new[] { new { id = "weixin-session", title = "微信会话" } }, has_more = false });
        if (path == "/api/sessions/weixin-session")
        {
            if (request.Method == HttpMethod.Patch) Archived = body!["archived"]!.GetValue<bool>();
            return Reply(new { session = new { title = "微信会话", archived = Archived } });
        }
        if (path.EndsWith("/messages")) return Reply(new { data = new[] { new { role = "user", content = "微信历史" } } });
        if (path == "/v1/runs")
        {
            Submits++; LastBody = body;
            string next = request.Headers.GetValues("Idempotency-Key").Single();
            if (key is null) { key = next; RunCount++; throw new HttpRequestException("simulated lost response after server accepted"); }
            if (key != next) { RunCount++; throw new Exception("duplicate run key"); }
            return Reply(new { run_id = "run-one", status = "queued" });
        }
        if (path.EndsWith("/events"))
        {
            Streams++;
            const string events = ": keepalive\n\ndata: {\"event\":\"tool.completed\",\"tool\":\"terminal\",\"duration\":0.5,\"error\":false}\n\ndata: {\"event\":\"reasoning.available\",\"text\":\"public summary\"}\n\nevent: message.delta\ndata: {\ndata: \"delta\":\"你好\"}\n\n";
            return new(HttpStatusCode.OK) { Content = KeepStream
                ? new StreamContent(LiveStream = new HeldStream(Encoding.UTF8.GetBytes(events)))
                : new StringContent(events, Encoding.UTF8, "text/event-stream") };
        }
        if (path == "/v1/runs/run-one") return LoseStatus ? new(HttpStatusCode.NotFound) : Reply(new
        {
            run_id = "run-one", status = Stops > 0 ? "stopping" : Approval is null ? "waiting_for_approval" : "running",
            approval = Approval is null ? new { request_id = "approval-one", command = "echo test", choices = new[] { "once", "deny" } } : null
        });
        if (path.EndsWith("/approval")) { Approval = body; return Reply(new { resolved = 1 }); }
        if (path.EndsWith("/steer")) { Steer = body; return Reply(new { accepted = true }); }
        if (path.EndsWith("/stop")) { Stops++; return Reply(new { status = "stopping" }); }
        throw new Exception("Unexpected fixture request: " + path);
    }
    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(Json.Node(value).ToJsonString(), Encoding.UTF8, "application/json") };
}

sealed class HeldStream(byte[] bytes) : MemoryStream(bytes)
{
    public bool Disposed;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int count = await base.ReadAsync(buffer, ct);
        if (count == 0) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return count;
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
