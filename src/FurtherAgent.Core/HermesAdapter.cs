using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

/// <summary>Hermes native API bridge. Ownership is limited to runs submitted by this process.</summary>
public sealed class HermesAdapter : IAgentAdapter
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task monitor;
    private readonly List<Task> streams = [];
    private JsonNode? features;
    private string? session, run, pendingText, pendingKey;
    private string modelName = "";
    private string title = "", status = "unknown", notice = "";
    private bool streamLost, unavailable;
    private int disposed;
    private List<ChatEntry> history = [], progress = [];
    private readonly Dictionary<string, PendingPrompt> prompts = [];
    public AgentInfo Info => new("hermes", "Hermes Agent", true,
        "原生 HTTP API；可继续既有会话，只能控制本接入端发起的运行。其他入口状态不可见。", true, true, CanArchive: true);
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted { add { } remove { } }

    public HermesAdapter(string address, string key, HttpMessageHandler? handler = null)
    {
        var uri = new Uri(address.TrimEnd('/') + "/");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Hermes 必须使用 HTTPS 或本机回环 HTTP。");
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Hermes 地址不能包含凭据、查询串或片段。");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("未配置 Hermes API key。");
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
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
        if (Busy || pendingKey is not null || unavailable) throw new IOException("请先处理当前运行或结果未知的提交，再新建会话或修改模型。");
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
                    if (name.Length > 0 && !excluded.Contains(name)) models.Add(new(name, slug, $"{slug} / {name}"));
                }
            }
            var meta = session is null ? null : await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(session)}", null, ct);
            var row = meta?["session"];
            JsonNode? config = row?["model_config"];
            if (config is JsonValue && config.ToString().StartsWith('{')) config = JsonNode.Parse(config.ToString());
            var locked = config?["browser_model_lock"];
            return new(locked?["model"]?.ToString() ?? row?["model"]?.ToString() ?? options?["model"]?.ToString() ?? "",
                locked?["provider"]?.ToString() ?? options?["provider"]?.ToString() ?? "", "default", models,
                Supports("session_model_lock"), false, "模型修改作用于后续运行。Hermes 此接口未提供原生 Plan 模式；执行权限沿用服务器配置。");
        }
        finally { gate.Release(); }
    }
    public async Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureIdle();
            if (session is null) throw new IOException("请先选择会话。");
            if (!Supports("session_model_lock") || mode != "default") throw new NotSupportedException("Hermes 不支持此设置或执行模式。");
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("请选择模型和提供商。");
            await RequestAsync(HttpMethod.Post, $"api/sessions/{Segment(session)}/model", new { model, provider }, ct);
            modelName = model; Publish();
        }
        finally { gate.Release(); }
    }
    private bool Busy => run is not null && !Terminal(status);
    private static bool Terminal(string value) => value is "completed" or "failed" or "cancelled" or "interrupted";
    private static string Segment(string value) => Uri.EscapeDataString(value);
    public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (session != id && (Busy || pendingKey is not null))
                throw new IOException("本接入端仍有运行或待确认提交，请先回到原会话处理，再切换会话。");
            var metadata = await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(id)}", null, ct);
            var messages = await ReadHistoryAsync(id, ct);
            if (session != id)
            { run = null; status = "unknown"; progress.Clear(); prompts.Clear(); notice = ""; streamLost = false; }
            modelName = metadata["session"]?["model"]?.ToString() ?? "";
            session = id; title = metadata["session"]?["title"]?.ToString() ?? id;
            if (!Busy) { history = messages; unavailable = false; }
            return View();
        }
        finally { gate.Release(); }
    }
    private async Task<List<ChatEntry>> ReadHistoryAsync(string id, CancellationToken ct)
    {
        var result = await RequestAsync(HttpMethod.Get, $"api/sessions/{Segment(id)}/messages?limit=250&order=latest&inline_images=false", null, ct);
        return (result["data"]?.AsArray() ?? throw new IOException("Hermes 历史缺少 data。"))
            .Where(m => m is not null).Select(m => new ChatEntry(m!["role"]?.ToString() ?? "message", Content(m["content"]),
                m["tool_calls"] is not null || m["role"]?.ToString() == "tool" ? Content(m) : null))
            .Where(m => m.Text.Length != 0 || m.Detail is not null).ToList();
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
            pendingKey ??= Guid.NewGuid().ToString("N"); pendingText = text;
            try
            {
                var result = await RequestAsync(HttpMethod.Post, "v1/runs", new { session_id = session, input = pendingText }, ct, pendingKey);
                run = result["run_id"]?.ToString() ?? throw new IOException("Hermes 未返回 run_id。");
                status = result["status"]?.ToString() ?? "queued";
                pendingKey = pendingText = null; prompts.Clear(); progress = [new("user", text)];
                streamLost = false; unavailable = false; notice = "";
                streams.RemoveAll(t => t.IsCompleted);
                if (Supports("run_events_sse")) streams.Add(StreamAsync(run));
                Publish();
            }
            catch
            {
                notice = "提交结果未确认；请原样重试，接入端将沿用同一幂等键，不要重启接入端。";
                Publish(); throw;
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
        // Stream remains alive across controller/relay disconnects. Never attempt an SSE replay on 0.21.0.
        await Task.Yield();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/runs/{Segment(runId)}/events");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(lifetime.Token));
            string eventName = ""; var data = new StringBuilder();
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
                            if (progress.Count > 250) progress.RemoveRange(0, progress.Count - 250);
                            Publish();
                            if (name is "run.completed" or "run.failed" or "run.cancelled" or "run.interrupted") return;
                        }
                        finally { gate.Release(); }
                    }
                    data.Clear(); eventName = "";
                }
                else if (line.StartsWith("event:")) eventName = line[6..].Trim();
                else if (line.StartsWith("data:")) data.AppendLine(line[5..].TrimStart());
                if (data.Length > 1024 * 1024) throw new IOException("Hermes SSE 事件过大。");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception) { /* Status polling below remains authoritative. Do not expose response bodies or credentials. */ }
        await gate.WaitAsync(lifetime.Token);
        try { if (run == runId) { streamLost = true; Publish(); } }
        finally { gate.Release(); }
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
                    if (session is null) continue;
                    if (Busy)
                    {
                        var result = await RequestAsync(HttpMethod.Get, $"v1/runs/{Segment(run!)}", null, lifetime.Token);
                        status = result["status"]?.ToString() ?? "unknown";
                        if (status == "waiting_for_approval" && result["approval"] is { } approval) Approval(approval);
                        else prompts.Clear();
                    }
                    if (!Busy && pendingKey is null)
                    { history = await ReadHistoryAsync(session, lifetime.Token); progress.RemoveAll(m => m.Detail is null || m.Role.StartsWith("tool.", StringComparison.Ordinal)); }
                    unavailable = false; notice = ""; Publish();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    unavailable = true;
                    notice = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                        ? "运行状态已丢失或过期，无法判定是否完成；不会重发消息，请在 Hermes 核实。"
                        : "Hermes 状态暂不可达；不会重发消息。";
                    Publish();
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
        if (pendingKey is not null) detail += " 上次提交结果未知，请原样重试找回运行。";
        if (notice.Length != 0) detail += " " + notice;
        return new(session!, title, "Hermes 网关工作目录（由服务器配置）", Busy ? "active" : "unknown", run,
            history.Concat(progress).TakeLast(500).Select(ChatPresentation.Display).ToList(), prompts.Values.ToList(), detail,
            !unavailable && (!Busy || status == "running" && Supports("run_steer")),
            !unavailable && Busy && Supports("run_stop") && status != "stopping", modelName, "default");
    }
    private string? lastPublished;
    private void Publish()
    {
        if (session is null) return;
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
