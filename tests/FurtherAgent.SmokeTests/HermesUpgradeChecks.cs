using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class HermesUpgradeChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "fa-hermes-recovery-" + Guid.NewGuid().ToString("N"));
        var state = new State { LoseAcceptance = true };
        HermesAdapter Open() => new("http://127.0.0.1:8642", "upgrade-fixture-secret", new Api(state), root);
        try
        {
            await using (var first = Open())
            {
                await first.ProbeAsync();
                var view = await first.AttachAsync("session", default);
                check(view.Messages.Count == 620 && view.Messages[0].Text == "message-0" && view.Messages[^1].Text == "message-619",
                    "Hermes backend offsets expose history beyond 250 and 500 rows in chronological order");
                var settings = await first.SettingsAsync(default);
                check(settings.RequireModelSelection && settings.Models[0].ReasoningEfforts!.Contains("ultra")
                    && settings.Models[1].ReasoningEfforts is null, "Hermes settings show reasoning only on declared capable models");
                await first.ConfigureAsync("reasoner", "fixture", "default", "high", default);
                check((await first.SettingsAsync(default)).ReasoningEffort == "high", "Hermes reasoning setting is verified from persisted remote session lock");
                bool rejected = false;
                try { await first.ConfigureAsync("plain", "fixture", "default", "high", default); } catch (ArgumentException) { rejected = true; }
                check(rejected, "unsupported Hermes model effort is rejected before configuration mutation");
                state.IgnoreModelOptions = true; bool ignoredSetting = false;
                try { await first.ConfigureAsync("reasoner", "fixture", "default", "ultra", default); } catch (IOException) { ignoredSetting = true; }
                check(ignoredSetting, "Hermes silently ignored reasoning settings are not reported as saved");
                state.IgnoreModelOptions = false;
                await first.SetFileWorkspaceAsync(Path.Combine(root, "outbox"), default);
                try { await first.SendAsync("exact message", default); } catch (HttpRequestException) { }
                check(state.CreatedRuns == 1, "fixture loses acknowledgement only after accepting one run");
            }
            string intent = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Single();
            check(!File.ReadAllText(intent).Contains("upgrade-fixture-secret"), "durable Hermes journal never contains the API credential");
            await using (var resumed = Open())
            {
                await resumed.ProbeAsync(); await resumed.AttachAsync("session", default);
                check(state.Submits == 1, "Host restart restores uncertain intent without automatically resubmitting");
                await resumed.SetFileWorkspaceAsync(Path.Combine(root, "changed-outbox"), default);
                await resumed.SendAsync("exact message", default);
                check(state.CreatedRuns == 1 && state.Submits == 2 && state.Bodies[0] == state.Bodies[1],
                    "explicit retry after Host restart reuses exact idempotency key and frozen context payload");
                var body = JsonNode.Parse(state.Bodies[1])!;
                check(body["input"]!.ToString() == "exact message" && body["instructions"]!.ToString().Contains("outbox")
                    && !body["input"]!.ToString().Contains("文件工作区"), "output delivery context never pollutes user message text");
            }
            await using (var running = Open())
            {
                await running.ProbeAsync();
                var restored = await running.AttachAsync("session", default);
                check(restored.TurnId == "run-1" && restored.CanStop == true && state.Submits == 2,
                    "Host restart queries and restores ownership of original run without creating a new run");
                state.LoseStatus = true;
                await Until(async () => (await running.AttachAsync("session", default)).Status == "unknown");
                check(!(await running.AttachAsync("session", default)).CanSend, "unavailable Hermes backend does not show stale execution as healthy");
                state.LoseStatus = false; state.Status = "completed";
                await Until(async () => (await running.AttachAsync("session", default)).Status == "completed");
                string usage = (await running.SettingsAsync(default)).Usage!;
                check(usage.Contains("123") && usage.Contains("实际模型：fixture / fallback-model") && usage.Contains("未提供统一余额接口"),
                    "Hermes reports real run token usage and fallback model without inventing account quota");
            }
            var streamState = new State { Stream = true, HistoryCount = 1 };
            await using (var streaming = new HermesAdapter("http://127.0.0.1:8642", "upgrade-fixture-secret", new Api(streamState)))
            {
                await streaming.ProbeAsync(); await streaming.AttachAsync("session", default);
                await streaming.SendAsync("stream", default);
                await Until(async () => (await streaming.AttachAsync("session", default)).Messages.Any(m => m.Text == "AB"));
                check(streamState.LastEventId == "1" && streamState.Streams == 2,
                    "Hermes resumes sequenced SSE from Last-Event-ID and ignores duplicate replay deltas");
                var streamView = await streaming.AttachAsync("session", default);
                check(streamView.StatusDetail!.Contains("部分事件已过期") && streamView.Prompts.Single().Kind == "hermes/clarify-unavailable",
                    "expired event replay and unsupported native clarification remain explicitly visible");
                bool answerBlocked = false;
                try { await streaming.RespondAsync(streamView.Prompts.Single().Id, "accept", null, default); } catch (IOException) { answerBlocked = true; }
                check(answerBlocked, "Hermes clarification is never misrouted as an approval or ordinary chat message");
                streamState.Status = "failed";
                await Until(async () => (await streaming.AttachAsync("session", default)).Status == "failed");
                check((await streaming.AttachAsync("session", default)).Prompts.Count == 0,
                    "terminal Hermes failure clears outstanding clarification notice");
            }
            await using (var large = new HermesAdapter("http://127.0.0.1:8642", "fixture", new Api(new State { HistoryCount = 250, LargeHistory = true })))
            {
                var view = await large.AttachAsync("session", default);
                check(view.Messages.Count > 0 && view.Messages.Count < 250 && view.Messages[^1].Text.StartsWith("message-249 ")
                    && view.StatusDetail!.Contains("历史达到"), "oversized Hermes history retains the newest bounded suffix instead of an empty view");
            }
            await using (var legacy = new HermesAdapter("http://127.0.0.1:8642", "fixture", new Api(new State { LegacyHistory = true })))
            {
                var view = await legacy.AttachAsync("session", default);
                check(view.Messages.Count == 250 && view.StatusDetail!.Contains("历史达到"), "legacy Hermes without pagination metadata retains latest history with a visible limit");
            }
            // Corrupt journal is never silently discarded, which could create a duplicate run.
            File.WriteAllText(intent, "broken-json");
            await using (var corrupt = Open())
            {
                await corrupt.ProbeAsync(); bool rejected = false;
                try { await corrupt.AttachAsync("session", default); } catch (System.Text.Json.JsonException) { rejected = true; }
                bool sendBlocked = false;
                try { await corrupt.SendAsync("must not run", default); } catch (IOException) { sendBlocked = true; }
                await Task.Delay(2200);
                check(rejected && sendBlocked && File.ReadAllText(intent) == "broken-json", "corrupt Hermes recovery record blocks later sends and background writes");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!await condition()) await Task.Delay(150, timeout.Token);
    }
    private sealed class State
    {
        public bool LoseAcceptance, LoseStatus, Stream, IgnoreModelOptions, LargeHistory, LegacyHistory;
        public int Submits, CreatedRuns, Streams, HistoryCount = 620;
        public string Status = "running";
        public string? Key, LastEventId;
        public JsonNode? ModelLock;
        public List<string> Bodies = [];
    }
    private sealed class Api(State state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
            if (path == "/v1/capabilities") return Reply(new { features = new { session_resources = true, run_submission = true,
                run_status = true, run_events_sse = state.Stream, run_stop = true, runs_idempotency = new { supported = true }, model_options = true, session_model_lock = true } });
            if (path == "/api/model/options") return Reply(new { providers = new[] { new { slug = "fixture", models = new[] { "reasoner", "plain" },
                capabilities = new { reasoner = new { reasoning = true, can_disable_reasoning = true }, plain = new { reasoning = false } } } } });
            if (path.EndsWith("/model")) { state.ModelLock = body; if (state.IgnoreModelOptions) state.ModelLock!.AsObject().Remove("model_options"); return Reply(new { ok = true }); }
            if (path.EndsWith("/messages"))
            {
                int offset = int.Parse(request.RequestUri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("offset=")).Split('=')[1]);
                int end = Math.Max(0, state.HistoryCount - offset), start = Math.Max(0, end - 250);
                return Reply(new { data = Enumerable.Range(start, end - start).Select(i => new { role = "assistant", content = "message-" + i + (state.LargeHistory ? " " + new string('x', 15000) : "") }),
                    pagination = state.LegacyHistory ? null : new { offset, returned = end - start, order = "latest" } });
            }
            if (path == "/api/sessions/session") return Reply(new { session = new { title = "fixture", model_config = new { browser_model_lock = state.ModelLock } } });
            if (path == "/v1/runs")
            {
                state.Submits++; state.Bodies.Add(body!.ToJsonString());
                string key = request.Headers.GetValues("Idempotency-Key").Single();
                if (state.Key != key) { state.Key = key; state.CreatedRuns++; }
                if (state.LoseAcceptance) { state.LoseAcceptance = false; throw new HttpRequestException("lost acceptance"); }
                return Reply(new { run_id = "run-1", status = state.Status });
            }
            if (path == "/v1/runs/run-1") return state.LoseStatus ? new(HttpStatusCode.NotFound) : Reply(new { status = state.Status,
                usage = new { input_tokens = 123, output_tokens = 45, total_tokens = 168, cache_read_tokens = 50 }, runtime = new { provider = "fixture", model = "fallback-model" } });
            if (path.EndsWith("/events"))
            {
                state.Streams++;
                state.LastEventId = request.Headers.TryGetValues("Last-Event-ID", out var ids) ? ids.Single() : null;
                string events = "id: 1\nevent: message.delta\ndata: {\"delta\":\"A\"}\n\n";
                if (state.Streams > 1) events += "event: replay.truncated\ndata: {\"oldest_retained_seq\":2}\n\n"
                    + "id: 2\nevent: message.delta\ndata: {\"delta\":\"B\"}\n\n"
                    + "id: 3\nevent: tool.started\ndata: {\"tool\":\"clarify\",\"preview\":\"Which option?\"}\n\n";
                return new(HttpStatusCode.OK) { Content = state.Streams == 1 ? new StringContent(events, Encoding.UTF8, "text/event-stream")
                    : new StreamContent(new HeldStream(Encoding.UTF8.GetBytes(events))) };
            }
            throw new Exception("Unexpected upgrade fixture path: " + path);
        }
        private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(Json.Node(body).ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
