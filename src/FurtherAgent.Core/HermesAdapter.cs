using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

/// <summary>Hermes native API bridge. Controls runs submitted by this host, including its persisted runs.</summary>
public sealed partial class HermesAdapter : IAgentAdapter
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task monitor;
    private readonly List<Task> streams = [];
    private JsonNode? features;
    private readonly HermesRunStore? runStore;
    private JsonObject? pendingBody;
    private DateTimeOffset? pendingAt;
    private string? reasoningEffort, usage;
    private bool replaySupported;
    private bool replayGap;
    private long lastSequence = -1;
    private string? lastSavedState;
    private DateTimeOffset nextHistoryRead;
    private string? outbox;
    private List<ModelOption> modelCatalog = [];
    private string? historyFingerprint;
    private bool historyLimited;
    private string? historyScope;
    private List<ChatEntry>? historyCache;
    private bool recoveryReady;
    private volatile bool observed = true;
    public void SetObserved(bool value) => observed = value;
    private string? session, run, pendingText, pendingKey;
    private string modelName = "";
    private string title = "", status = "unknown", notice = "";
    private bool streamLost, unavailable;
    private int disposed;
    private List<ChatEntry> history = [], progress = [];
    private readonly Dictionary<string, PendingPrompt> prompts = [];
    private PendingPrompt? unsupportedQuestion;
    public AgentInfo Info => new("hermes", "Hermes Agent", true,
        "原生 HTTP API；可继续既有会话，只能控制本接入端发起的运行。其他入口状态不可见。", true, true, CanArchive: true);
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted { add { } remove { } }

    public HermesAdapter(string address, string key, HttpMessageHandler? handler = null, string? stateDirectory = null)
    {
        var uri = new Uri(address.TrimEnd('/') + "/");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Hermes 必须使用 HTTPS 或本机回环 HTTP。");
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Hermes 地址不能包含凭据、查询串或片段。");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("未配置 Hermes API key。");
        http = new HttpClient(handler ?? DirectNetwork.CreateHandler(allowAutoRedirect: false))
        { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        if (stateDirectory is not null) runStore = new HermesRunStore(stateDirectory, uri.AbsoluteUri, key.Trim());
        monitor = MonitorAsync();
    }
    private bool Supports(string name) => features?[name]?.GetValue<bool>() == true;
    public async Task<JsonNode> ProbeAsync(CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "v1/capabilities", null, ct);
        features = result["features"]?.DeepClone() ?? throw new IOException("Hermes capabilities 缺少 features。");
        foreach (string required in new[] { "session_resources", "run_submission", "run_status" })
            if (!Supports(required)) throw new IOException($"Hermes 未声明必需能力：{required}");
        if (features["runs_idempotency"]?["supported"]?.GetValue<bool>() != true)
            throw new IOException("Hermes 未声明提交幂等保护，不启用远程运行。");
        return result;
    }
    private async Task<JsonNode> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct, string? key = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(Json.Node(body).ToJsonString(), Encoding.UTF8, "application/json");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Hermes HTTP {(int)response.StatusCode} ({method} {path.Split('?')[0]})", null, response.StatusCode);
        await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, timeout.Token);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token)) ?? throw new IOException("Hermes 返回空 JSON。");
    }
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct)
    {
        if (features is null) await ProbeAsync(ct);
        var result = new Dictionary<string, SessionInfo>();
        for (int offset = 0; offset < 1000; offset += 200)
        {
            var page = await RequestAsync(HttpMethod.Get, $"api/sessions?limit=200&offset={offset}", null, ct);
            foreach (var row in page["data"]?.AsArray() ?? throw new IOException("Hermes 会话列表缺少 data。"))
            {
                if (row?["id"]?.ToString() is not { Length: > 0 } id) continue;
                result[id] = new(id, row["title"]?.ToString() is { Length: > 0 } t ? t : id,
                    row["last_active"]?.ToString() ?? row["updated_at"]?.ToString() ?? "", row["cwd"]?.ToString() ?? row["working_directory"]?.ToString() ?? "");
            }
            if (page["has_more"]?.GetValue<bool>() != true) break;
        }
        return result.Values.ToArray();
    }
    public async Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("新会话请求 ID 无效。");
        await gate.WaitAsync(ct);
        try
        {
            EnsureIdle();
            string sessionId = "furtheragent_" + id;
            JsonNode result;
            try { result = await RequestAsync(HttpMethod.Post, "api/sessions", new { id = sessionId, title }, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            { result = await RequestAsync(HttpMethod.Get, $"api/sessions/{sessionId}", null, ct); }
            var row = result["session"] ?? throw new IOException("创建回复缺少 session。");
            return new(row["id"]?.ToString() ?? sessionId, row["title"]?.ToString() ?? title, DateTimeOffset.Now.ToString("O"));
        }
        finally { gate.Release(); }
    }
    private void EnsureIdle()
    {
        if (Busy || pendingKey is not null || unavailable || session is not null && !recoveryReady) throw new IOException("请先处理当前运行或结果未知的提交，再新建会话或修改模型。");
    }
    public async Task ArchiveAsync(string id, bool archived, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureIdle();
            if (session != id) throw new IOException("请先打开对应会话再归档或恢复。");
            await RequestAsync(HttpMethod.Patch, $"api/sessions/{Segment(id)}", new { archived }, ct);
            var check = await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(id)}", null, ct);
            if (check["session"]?["archived"]?.GetValue<bool>() != archived)
                throw new IOException("归档状态未确认，请刷新后核查；不要以删除代替归档。");
        }
        finally { gate.Release(); }
    }
    public async Task<AgentSettings> SettingsAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (features is null) await ProbeAsync(ct);
            var options = Supports("model_options") ? await RequestAsync(HttpMethod.Get, "api/model/options?include_unconfigured=false", null, ct) : null;
            var models = new List<ModelOption>();
            foreach (var provider in options?["providers"]?.AsArray() ?? [])
            {
                string slug = provider?["slug"]?.ToString() ?? "";
                var excluded = provider?["unavailable_models"]?.AsArray().Select(x => x?.ToString()).ToHashSet() ?? [];
                foreach (var m in provider?["models"]?.AsArray() ?? [])
                {
                    string name = m is JsonObject ? m["id"]?.ToString() ?? "" : m?.ToString() ?? "";
                    if (name.Length > 0 && !excluded.Contains(name))
                    {
                        var capability = provider?["capabilities"]?[name];
                        List<string>? efforts = capability?["reasoning"]?.GetValue<bool>() == true
                            ? ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"] : null;
                        if (capability?["can_disable_reasoning"]?.GetValue<bool>() == true) efforts?.Insert(0, "none");
                        models.Add(new(name, slug, $"{slug} / {name}", efforts));
                    }
                }
            }
            var meta = session is null ? null : await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(session)}", null, ct);
            var row = meta?["session"];
            JsonNode? config = row?["model_config"];
            if (config is JsonValue && config.ToString().StartsWith('{')) config = JsonNode.Parse(config.ToString());
            var locked = config?["browser_model_lock"];
            modelCatalog = models;
            reasoningEffort = locked?["model_options"]?["reasoning"]?["enabled"]?.GetValue<bool>() == false ? "none"
                : locked?["model_options"]?["reasoning"]?["effort"]?.ToString()
                ?? locked?["model_options"]?["reasoning_effort"]?.ToString();
            return new(locked?["model"]?.ToString() ?? row?["model"]?.ToString() ?? options?["model"]?.ToString() ?? "",
                locked?["provider"]?.ToString() ?? options?["provider"]?.ToString() ?? "", "default", models,
                Supports("session_model_lock") && models.Count > 0, false,
                "模型和思考设置作用于后续运行。思考档位由 Hermes 转换为模型支持的参数；此接口未提供原生 Plan 或普通 Question 回答接口。",
                reasoningEffort, models.Any(m => m.ReasoningEfforts?.Count > 0),
                (usage ?? "尚无本接入端运行的用量记录。") + "\n套餐余额：当前 Hermes 接入未提供统一余额接口。", RequireModelSelection: true);
        }
        finally { gate.Release(); }
    }
    public Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct) => ConfigureAsync(model, provider, mode, null, ct);
    public async Task ConfigureAsync(string model, string provider, string mode, string? effort, CancellationToken ct)
    {
        await SettingsAsync(ct);
        await gate.WaitAsync(ct);
        try
        {
            EnsureIdle();
            if (session is null) throw new IOException("请先选择会话。");
            if (!Supports("session_model_lock") || mode != "default") throw new NotSupportedException("Hermes 不支持此设置或执行模式。");
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("请选择模型和提供商。");
            var selectedModel = modelCatalog.FirstOrDefault(m => m.Id == model && m.Provider == provider)
                ?? throw new ArgumentException("模型不在远端可用目录中，请刷新后选择。");
            if (effort is not null && selectedModel.ReasoningEfforts?.Contains(effort) != true)
                throw new ArgumentException("此模型未声明可用的思考设置。");
            var body = new JsonObject { ["model"] = model, ["provider"] = provider };
            if (effort is not null) body["model_options"] = new JsonObject { ["reasoning"] = new JsonObject { ["enabled"] = effort != "none", ["effort"] = effort } };
            await RequestAsync(HttpMethod.Post, $"api/sessions/{Segment(session)}/model", body, ct);
            if (effort is not null)
            {
                var metadata = await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(session)}", null, ct);
                JsonNode? config = metadata["session"]?["model_config"];
                if (config is JsonValue && config.ToString().StartsWith('{')) config = JsonNode.Parse(config.ToString());
                var actual = config?["browser_model_lock"]?["model_options"]?["reasoning"];
                if (effort == "none" ? actual?["enabled"]?.GetValue<bool>() != false : actual?["effort"]?.ToString() != effort)
                    throw new IOException("Hermes 未确认思考设置已保存；模型可能已变更，请刷新核对。不会自动重试。");
            }
            modelName = model; reasoningEffort = effort; Publish();
        }
        finally { gate.Release(); }
    }
    private bool Busy => run is not null && !Terminal(status);
    private static bool Terminal(string value) => value is "completed" or "failed" or "cancelled" or "interrupted";
    private static string Segment(string value) => Uri.EscapeDataString(value);
    public async Task SetFileWorkspaceAsync(string path, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { outbox = Path.GetFullPath(path); }
        finally { gate.Release(); }
    }
    private void SaveState()
    {
        if (runStore is null || session is null || !recoveryReady) return;
        var state = new HermesRunState(session, run, status, pendingText, pendingKey, pendingBody, pendingAt, replaySupported, reasoningEffort, usage);
        string fingerprint = Json.Node(state).ToJsonString();
        if (fingerprint == lastSavedState) return;
        runStore.Write(state); lastSavedState = fingerprint;
    }
    private async Task RestoreRunAsync(CancellationToken ct)
    {
        var saved = runStore?.Read(session!);
        if (saved is null) return;
        run = saved.Run; status = saved.Status; pendingText = saved.PendingText; pendingKey = saved.PendingKey;
        pendingBody = saved.PendingBody; pendingAt = saved.PendingAt; replaySupported = saved.ReplaySupported;
        reasoningEffort = saved.ReasoningEffort; usage = saved.Usage;
        if (run is null || Terminal(status)) return;
        try
        {
            var result = await RequestAsync(HttpMethod.Get, $"v1/runs/{Segment(run)}", null, ct);
            status = result["status"]?.ToString() ?? "unknown";
            ReadUsage(result);
            if (status == "waiting_for_approval" && result["approval"] is { } approval) Approval(approval);
            if (Busy && replaySupported && Supports("run_events_sse")) streams.Add(StreamAsync(run));
            unavailable = false;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        { unavailable = true; notice = "原运行暂时无法确认，已保留恢复记录；不会重新提交任务。"; }
    }
    public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (session != id && (Busy || pendingKey is not null))
                throw new IOException("本接入端仍有运行或待确认提交，请先回到原会话处理，再切换会话。");
            var metadata = await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(id)}", null, ct);
            var messages = await ReadHistoryAsync(id, ct);
            bool changedSession = session != id;
            if (changedSession)
            { run = null; status = "unknown"; progress.Clear(); prompts.Clear(); unsupportedQuestion = null; notice = ""; streamLost = unavailable = replaySupported = replayGap = false; lastSequence = -1; lastSavedState = null; recoveryReady = false; outbox = null; usage = null; reasoningEffort = null; }
            modelName = metadata["session"]?["model"]?.ToString() ?? "";
            session = id; title = metadata["session"]?["title"]?.ToString() ?? id;
            if (!recoveryReady) { history = messages; await RestoreRunAsync(ct); recoveryReady = true; }
            else if (!Busy) { history = messages; unavailable = false; if (Terminal(status)) replayGap = false; }
            nextHistoryRead = DateTimeOffset.UtcNow.AddSeconds(15);
            return View();
        }
        finally { gate.Release(); }
    }
    private async Task<List<ChatEntry>> ReadHistoryAsync(string id, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30)); ct = budget.Token;
        var result = new List<ChatEntry>();
        var pages = new HashSet<string>();
        string? newest = null;
        int characters = 0;
        bool limited = false;
        // Backend offsets page beyond the former 250-row window. Bound source reads as well as UI pages.
        for (int offset = 0; offset < 8000; offset += 250)
        {
            var page = await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(id)}/messages?limit=250&offset={offset}&order=latest&inline_images=false", null, ct);
            var rows = page["data"]?.AsArray() ?? throw new IOException("Hermes 历史缺少 data。");
            string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToJsonString())));
            if (offset == 0)
            {
                newest = fingerprint;
                if (historyScope == id && newest == historyFingerprint && historyCache is not null) return historyCache.ToList();
            }
            if (rows.Count > 0 && !pages.Add(fingerprint)) throw new IOException("Hermes 未推进历史分页，已停止读取以避免重复记录。");
            var entries = rows.Where(m => m is not null).Select(m => new ChatEntry(m!["role"]?.ToString() ?? "message", Content(m["content"]),
                m["tool_calls"] is not null || m["role"]?.ToString() == "tool" ? Content(m) : null))
                .Where(m => m.Text.Length != 0 || m.Detail is not null).ToList();
            int pageCharacters = entries.Sum(m => m.Text.Length + (m.Detail?.Length ?? 0));
            if (characters + pageCharacters > 2_000_000)
            {
                // Preserve the newest suffix even when the first page alone exceeds the display budget.
                int start = entries.Count;
                while (start > 0)
                {
                    var entry = entries[start - 1];
                    int length = entry.Text.Length + (entry.Detail?.Length ?? 0);
                    if (characters + length > 2_000_000) break;
                    characters += length; start--;
                }
                result.InsertRange(0, entries.Skip(start)); limited = true; break;
            }
            characters += pageCharacters;
            result.InsertRange(0, entries);
            if (rows.Count < 250) break;
            if (page["pagination"]?["offset"]?.GetValue<int>() != offset)
            { limited = true; break; } // Old versions without a pagination contract retain their latest window.
            limited = offset == 7750;
        }
        if (pages.Count > 1)
        {
            var verify = await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(id)}/messages?limit=250&offset=0&order=latest&inline_images=false", null, ct);
            string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verify["data"]!.ToJsonString())));
            if (fingerprint != newest) throw new IOException("Hermes 历史在分页期间发生变化，请刷新后重试；未合并不一致记录。");
        }
        historyScope = id; historyFingerprint = newest; historyLimited = limited; historyCache = result.ToList();
        return result;
    }
    private static string Content(JsonNode? node)
    {
        string value = node is JsonArray a ? string.Join("\n", a.Select(p => p?["text"]?.ToString() ?? "[非文本内容]")) : node?.ToString() ?? "";
        return value.Length <= 20000 ? value : value[..20000] + "\n[显示已截断]";
    }
    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("消息不能为空。");
        await gate.WaitAsync(ct);
        try
        {
            if (session is null) throw new IOException("请先选择会话。");
            if (!recoveryReady) throw new IOException("运行记录尚未恢复，不能提交新任务。请先重新打开原会话核对记录。");
            if (unavailable) throw new IOException("Hermes 状态尚未恢复，不能提交新指令。");
            if (features is null) await ProbeAsync(ct);
            if (Busy)
            {
                if (status != "running" || !Supports("run_steer") || unavailable)
                    throw new IOException("当前运行不可追加，请等待或停止本接入端的运行。");
                await RequestAsync(HttpMethod.Post, $"v1/runs/{Segment(run!)}/steer", new { input = text }, ct);
                progress.Add(new("补充指令", text)); Publish(); return;
            }
            if (pendingKey is not null && pendingText != text)
                throw new IOException("上次提交结果未知。请原样重试上次消息，以同一幂等键找回运行。");
            if (pendingAt is { } created && DateTimeOffset.UtcNow - created > TimeSpan.FromHours(24))
                throw new IOException("待确认提交已超过幂等保留期限，请先在 Hermes 核对；不会用过期记录再次提交。");
            pendingKey ??= Guid.NewGuid().ToString("N"); pendingText = text;
            pendingAt ??= DateTimeOffset.UtcNow;
            if (pendingBody is null)
            {
                pendingBody = new JsonObject { ["session_id"] = session, ["input"] = text };
                if (outbox is not null) pendingBody["instructions"] = FileTransferStore.OutputInstruction + Json.Node(outbox).ToJsonString();
            }
            SaveState(); // Durable intent must precede the only mutating request.
            try
            {
                var result = await RequestAsync(HttpMethod.Post, "v1/runs", pendingBody, ct, pendingKey);
                run = result["run_id"]?.ToString() ?? throw new IOException("Hermes 未返回 run_id。");
                status = result["status"]?.ToString() ?? "queued";
                pendingKey = pendingText = null; pendingBody = null; pendingAt = null; prompts.Clear(); unsupportedQuestion = null; progress = [new("user", text)];
                lastSequence = -1; replaySupported = false; replayGap = false;
                streamLost = false; unavailable = false; notice = "";
                streams.RemoveAll(t => t.IsCompleted);
                if (Supports("run_events_sse")) streams.Add(StreamAsync(run));
                Publish();
            }
            catch
            {
                notice = pendingKey is not null
                    ? "提交结果未确认；请原样重试，接入端将沿用同一幂等键。"
                    : "运行已接收，但本地状态未能保存或发布；请先核对当前运行，不要重复提交。";
                Publish(false); throw;
            }
        }
        finally { gate.Release(); }
    }
    public async Task InterruptAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!Busy || !Supports("run_stop") || unavailable) throw new IOException("没有可停止的本接入端运行。");
            await RequestAsync(HttpMethod.Post, $"v1/runs/{Segment(run!)}/stop", new { }, ct);
            status = "stopping"; Publish();
        }
        finally { gate.Release(); }
    }
    public async Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!Busy || unavailable || !Supports("run_approval_response") || !prompts.TryGetValue(requestId, out var prompt))
                throw new IOException("审批已失效或不属于本接入端运行。");
            string choice = decision switch { "accept" => "once", "decline" => "deny", _ => throw new IOException("只支持允许本次或拒绝。") };
            if (prompt.Payload?["choices"] is JsonArray choices && !choices.Any(c => c?.ToString() == choice))
                throw new IOException("Hermes 未允许此审批选项。");
            await RequestAsync(HttpMethod.Post, $"v1/runs/{Segment(run!)}/approval", new { choice, request_id = requestId }, ct);
            prompts.Remove(requestId); Publish();
        }
        finally { gate.Release(); }
    }
    private void Approval(JsonNode data)
    {
        if (data["request_id"]?.ToString() is not { Length: > 0 } id) { notice = "审批缺少 request_id，无法安全代答。"; return; }
        prompts[id] = new(id, "hermes/approval", "Hermes 权限审批", Content(data["command"] ?? data["description"] ?? data["reason"]), data.DeepClone());
    }
    private async Task StreamAsync(string runId)
    {
        // Legacy streams without event sequence IDs remain poll-only after a disconnect.
        await Task.Yield();
        for (int attempt = 0; attempt < 6 && !lifetime.IsCancellationRequested; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/runs/{Segment(runId)}/events");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (replaySupported && lastSequence >= 0) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(lifetime.Token));
                string eventName = "", eventId = ""; var data = new StringBuilder();
                while (!lifetime.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(75));
                    string? line = await reader.ReadLineAsync(idle.Token);
                    if (line is null) break;
                    if (line.Length == 0)
                    {
                        if (data.Length > 0)
                        {
                            var value = JsonNode.Parse(data.ToString())!;
                            await gate.WaitAsync(lifetime.Token);
                            try
                            {
                                if (run != runId || Terminal(status)) return;
                                string name = value["event"]?.ToString() ?? eventName;
                                if (long.TryParse(value["seq"]?.ToString() ?? eventId, out long sequence) && sequence >= 0)
                                {
                                    replaySupported = true;
                                    if (sequence <= lastSequence) { data.Clear(); eventName = eventId = ""; continue; }
                                    lastSequence = sequence;
                                }
                                streamLost = false;
                                if (name == "replay.truncated")
                                { replayGap = true; }
                                if (name == "message.delta")
                                {
                                    string delta = value["delta"]?.ToString() ?? "";
                                    if (progress.LastOrDefault()?.Role == "Hermes · 增量")
                                        progress[^1] = new("Hermes · 增量", Content(JsonValue.Create(progress[^1].Text + delta)));
                                    else progress.Add(new("Hermes · 增量", Content(JsonValue.Create(delta))));
                                }
                                else if (name == "message.interim" && value["already_streamed"]?.GetValue<bool>() != true)
                                    progress.Add(new("Hermes · 进度", Content(value["text"])));
                                else if (name.StartsWith("tool.") || name.Contains("reasoning", StringComparison.OrdinalIgnoreCase))
                                    progress.Add(new(name, Content(value["tool"] ?? value["name"]) + " " + Content(value["preview"] ?? value["text"]), Content(value)));
                                else if (name == "approval.request") { Approval(value); status = "waiting_for_approval"; }
                                else if (name == "approval.responded" && value["request_id"] is { } id) prompts.Remove(id.ToString());
                                if (name is "run.completed" or "run.failed" or "run.cancelled" or "run.interrupted")
                                { status = name[4..]; ReadUsage(value); nextHistoryRead = DateTimeOffset.MinValue; prompts.Clear(); unsupportedQuestion = null; }
                                if ((value["tool"] ?? value["name"])?.ToString() == "clarify")
                                {
                                    if (name == "tool.started") unsupportedQuestion = new(runId + ":clarify", "hermes/clarify-unavailable",
                                        "Hermes 正在请求澄清", Content(value["preview"]) + "\n当前 Hermes HTTP 接口未提供普通问题的回答通道，请在 Hermes 原入口处理。", null);
                                    else if (name == "tool.completed") unsupportedQuestion = null;
                                }
                                if (progress.Count > 250) progress.RemoveRange(0, progress.Count - 250);
                                Publish();
                                if (name is "run.completed" or "run.failed" or "run.cancelled" or "run.interrupted") return;
                            }
                            finally { gate.Release(); }
                        }
                        data.Clear(); eventName = eventId = "";
                    }
                    else if (line.StartsWith("event:")) eventName = line[6..].Trim();
                    else if (line.StartsWith("id:")) eventId = line[3..].Trim();
                    else if (line.StartsWith("data:")) data.AppendLine(line[5..].TrimStart());
                    if (data.Length > 1024 * 1024) throw new IOException("Hermes SSE 事件过大。");
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception) { /* Status polling below remains authoritative. Do not expose response bodies or credentials. */ }
            await gate.WaitAsync(lifetime.Token);
            try
            {
                if (run != runId || Terminal(status)) return;
                streamLost = true; Publish();
                if (!replaySupported) return;
            }
            finally { gate.Release(); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt))), lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        }
    }
    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    if (session is null || !recoveryReady) continue;
                    if (Busy)
                    {
                        var result = await RequestAsync(HttpMethod.Get, $"v1/runs/{Segment(run!)}", null, lifetime.Token);
                        status = result["status"]?.ToString() ?? "unknown";
                        ReadUsage(result);
                        if (Terminal(status)) { nextHistoryRead = DateTimeOffset.MinValue; unsupportedQuestion = null; }
                        if (status == "waiting_for_approval" && result["approval"] is { } approval) Approval(approval);
                        else prompts.Clear();
                    }
                    if (!Busy && pendingKey is null && observed && DateTimeOffset.UtcNow >= nextHistoryRead)
                    { history = await ReadHistoryAsync(session, lifetime.Token); nextHistoryRead = DateTimeOffset.UtcNow.AddSeconds(30); if (Terminal(status)) replayGap = false; progress.RemoveAll(m => m.Detail is null || m.Role.StartsWith("tool.", StringComparison.Ordinal)); }
                    unavailable = false; notice = ""; Publish();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    unavailable = true;
                    notice = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                        ? "运行状态已丢失或过期，无法判定是否完成；不会重发消息，请在 Hermes 核实。"
                        : "Hermes 状态暂不可达；不会重发消息。";
                    Publish(false);
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private SessionView View()
    {
        string detail = run is null ? "仅查看历史；微信等其他入口是否正在执行不可见。发送会在原会话排队开启新一轮。"
            : $"FurtherAgent 发起的运行：{status}。其他入口的运行不可控制。";
        if (streamLost) detail += " 事件流已断，已降级状态轮询；增量可能缺失。";
        if (replayGap) detail += " 部分事件已过期，实时输出可能缺失；完成后将用会话历史核对最终结果。";
        if (pendingKey is not null) detail += " 上次提交结果未知，请原样重试找回运行。";
        if (notice.Length != 0) detail += " " + notice;
        if (historyLimited) detail += " 历史达到本次读取上限，较早内容请在 Hermes 查看。";
        string projected = unavailable || pendingKey is not null ? "unknown" : run is null ? "unknown" : status switch
        {
            "running" => "active", "started" or "queued" => "queued", "stopping" => "stopping",
            "waiting_for_approval" => "waiting_for_approval", "completed" => "completed",
            "failed" => "failed", "cancelled" or "interrupted" => "stopped", _ => "unknown"
        };
        return new(session!, title, "Hermes 网关工作目录（由服务器配置）", projected, run,
            history.Concat(progress).Select(ChatPresentation.Display).ToList(),
            unsupportedQuestion is null ? prompts.Values.ToList() : [.. prompts.Values, unsupportedQuestion], detail,
            recoveryReady && !unavailable && (!Busy || status == "running" && Supports("run_steer")),
            !unavailable && Busy && Supports("run_stop") && status != "stopping", modelName, "default");
    }
    private string? lastPublished;
    private void Publish(bool persist = true)
    {
        if (session is null) return;
        if (persist) SaveState();
        var view = View(); string fingerprint = Json.Node(view).ToJsonString();
        if (fingerprint == lastPublished) return;
        lastPublished = fingerprint; Changed?.Invoke(view);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        try { await monitor; await Task.WhenAll(streams); } catch (OperationCanceledException) { }
        http.Dispose(); lifetime.Dispose(); gate.Dispose();
    }
}
