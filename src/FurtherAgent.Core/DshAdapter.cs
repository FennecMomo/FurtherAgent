using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

/// <summary>Connects to a plugin inside the selected DSH Host; never launches or resumes sessions to read them.</summary>
public sealed class DshAdapter : IAgentAdapter
{
    private sealed class BridgeException(string message, string code) : IOException(message) { public string Code { get; } = code; }
    private string descriptorPath;
    private readonly bool followDesktopRestarts;
    private readonly object descriptorGate = new();
    private string? instanceId;
    private bool needsProbe;
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Task monitor;
    private string? selected;
    private CancellationTokenSource? watchLifetime;
    private long selectionGeneration, cursor = -1, streamRevision;
    private string instanceMode = "desktop";
    private string runtimeVersion = "unknown";
    private HashSet<string> capabilities = [];
    private SessionView? current;
    private string? fingerprint;
    private string? pendingText, pendingId, pendingSession;
    private string? pendingDelivery, pendingMethod;
    public AgentInfo Info => new("dsh", instanceMode == "background" ? "DSH · 独立后台" : "DSH · 桌面", IsAvailable,
        $"DSH {runtimeVersion} · 同一 Host 原生会话服务；查看历史不取得会话写入权。" + (instanceMode == "background" ? "独立后台需单独配置模型账号，模型目录不代表凭据已配置。" : ""), CanCreate: capabilities.Contains("create"), CanConfigure: capabilities.Contains("model"), CanArchive: capabilities.Contains("archive"),
        CanChooseDelivery: capabilities.Contains("delivery"), CanInterruptSend: capabilities.Contains("interrupt-send"));
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted;
    public DshAdapter(string descriptorPath, bool followDesktopRestarts = false)
    { this.descriptorPath = Path.GetFullPath(descriptorPath); this.followDesktopRestarts = followDesktopRestarts; monitor = MonitorAsync(); }
    private bool IsAvailable { get { try { _ = ResolveDescriptor(); return true; } catch { return false; } } }
    private static bool IsLive(JsonNode? descriptor)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(descriptor!["pid"]!.GetValue<int>()); return !process.HasExited; }
        catch { return false; }
    }
    private JsonNode ResolveDescriptor()
    {
        lock (descriptorGate)
        {
            JsonNode? Read(string path) { try { return JsonNode.Parse(File.ReadAllText(path)); } catch { return null; } }
            var currentDescriptor = Read(descriptorPath);
            if (IsLive(currentDescriptor))
            {
                string? actual = currentDescriptor?["instanceId"]?.ToString();
                if (instanceId is not null && actual != instanceId) throw new IOException("DSH 实例归属发生变化，拒绝自动切换。");
                instanceId ??= actual; return currentDescriptor!;
            }
            string directory = Path.GetDirectoryName(descriptorPath)!;
            if (followDesktopRestarts && instanceId is not null && Directory.Exists(directory))
            {
                var matches = Directory.GetFiles(directory, "desktop-*.json").Select(path => (path, data: Read(path)))
                    .Where(x => IsLive(x.data) && x.data?["instanceId"]?.ToString() == instanceId).ToArray();
                if (matches.Length == 1) { descriptorPath = matches[0].path; needsProbe = true; return matches[0].data!; }
            }
            throw new IOException("DSH 所属实例已离线；等待该实例重新启动，不会另起实例接管。");
        }
    }
    public static string InstanceDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "dsh", "instances");
    public static string FindDescriptor(string mode)
    {
        var matches = Directory.Exists(InstanceDirectory) ? Directory.GetFiles(InstanceDirectory, mode + "-*.json").Where(path =>
        {
            try { return IsLive(JsonNode.Parse(File.ReadAllText(path))); }
            catch { return false; }
        }).ToArray() : [];
        return matches.Length switch { 1 => matches[0], 0 => throw new IOException("未发现 DSH 适配插件，请先启用对应模式的插件。"), _ => throw new IOException("发现多个 DSH 实例，请显式指定连接描述文件。") };
    }
    private async Task<JsonNode?> Call(string method, object? args, CancellationToken ct)
    {
        var d = ResolveDescriptor();
        if (needsProbe && method != "info") { await ProbeAsync(ct); d = ResolveDescriptor(); }
        var endpoint = new Uri(d["endpoint"]!.GetValue<string>());
        if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.UserInfo.Length != 0 || d["protocol"]?.GetValue<int>() != 1)
            throw new IOException("无效的 DSH 本机连接描述。");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "rpc"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", d["token"]!.GetValue<string>());
        request.Content = JsonContent.Create(new { method, args }, options: Json.Options);
        using var response = await http.SendAsync(request, ct);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode || result?["error"] is not null)
            throw new BridgeException(result?["error"]?["message"]?.ToString() ?? $"DSH HTTP {(int)response.StatusCode}", result?["error"]?["code"]?.ToString() ?? "");
        return result?["result"]?.DeepClone();
    }
    public async Task<JsonNode?> ProbeAsync(CancellationToken ct = default)
    {
        var info = await Call("info", null, ct);
        if (info?["protocol"]?.GetValue<int>() != 1) throw new IOException("DSH 插件协议不兼容。");
        instanceMode = info["mode"]?.ToString() ?? "desktop";
        runtimeVersion = info["hostVersion"]?.ToString() ?? "unknown";
        capabilities = (info["capabilities"]?.AsArray() ?? []).Select(x => x!.ToString()).ToHashSet(StringComparer.Ordinal);
        needsProbe = false;
        return info;
    }
    public async Task ShutdownBackgroundAsync(CancellationToken ct = default) => _ = await Call("shutdown", null, ct);
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct)
    {
        var data = await Call("list", null, ct);
        return (data?["items"]?.AsArray() ?? []).Where(x => x?["origin"]?.ToString() != "subagent")
            .Select(x => new SessionInfo(x!["sessionId"]!.ToString(), x["projections"]?["values"]?["title"]?.ToString() ?? x["title"]?.ToString() ?? x["sessionId"]!.ToString(),
                DateTimeOffset.FromUnixTimeMilliseconds(x["updatedAt"]?.GetValue<long>() ?? 0).ToString("O"))).ToArray();
    }
    public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (pendingId is not null && pendingSession != id) throw new IOException("上次发送结果未知，请先核对原会话。");
            watchLifetime?.Cancel(); selectionGeneration++;
            cursor = -1; streamRevision = 0;
            var view = await ReadSnapshot(id, ct);
            selected = id; current = view; fingerprint = Json.Node(view).ToJsonString(); return view;
        }
        finally { gate.Release(); }
    }
    public async Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct)
    {
        var result = await Call("create", new { id, title }, ct);
        return new(result!["sessionId"]!.ToString(), title, DateTimeOffset.UtcNow.ToString("O"));
    }
    private async Task<SessionView> ReadSnapshot(string id, CancellationToken ct)
    {
        var node = await Call("snapshot", new { id }, ct);
        return ParseSnapshot(id, node);
    }
    private SessionView ParseSnapshot(string id, JsonNode? node)
    {
        cursor = node?["cursor"]?.GetValue<long>() ?? -1;
        streamRevision = node?["streamRevision"]?.GetValue<long>() ?? 0;
        if (pendingId is not null && id == pendingSession && (node?["acceptedRequestIds"]?.AsArray() ?? []).Any(x => x?.ToString() == pendingId))
            pendingId = pendingText = pendingDelivery = pendingMethod = pendingSession = null;
        return Json.Read<SessionView>(node);
    }
    public Task SendAsync(string text, CancellationToken ct) => SendAsync(text, "auto", ct);
    public Task SendAsync(string text, string delivery, CancellationToken ct) => SendMutation("send", text, delivery, null, ct);
    public Task InterruptAndSendAsync(string turnId, string text, CancellationToken ct) => SendMutation("interruptSend", text, "queue", turnId, ct);
    private async Task SendMutation(string method, string text, string delivery, string? turnId, CancellationToken ct)
    {
        if (delivery is not ("auto" or "queue" or "steer")) throw new ArgumentException("发送方式必须是 auto、queue 或 steer。");
        await gate.WaitAsync(ct);
        try
        {
            if (selected is null) throw new IOException("请先选择会话。");
            if (pendingId is not null && (pendingText != text || pendingMethod != method)) throw new IOException("上次发送结果未知，请先核对原消息，不能提交另一条。");
            if (pendingId is null) current = await ReadSnapshot(selected, ct);
            pendingDelivery ??= delivery == "auto" ? current?.Status == "active" ? "steer" : "queue" : delivery;
            pendingId ??= Guid.NewGuid().ToString("N"); pendingText = text;
            pendingMethod = method; pendingSession = selected;
            try { await Call(method, new { id = selected, text, requestId = pendingId, mode = pendingDelivery, turnId }, ct); }
            catch (BridgeException ex) when (ex.Code == "bridge/not-submitted")
            { pendingId = pendingText = pendingDelivery = pendingMethod = pendingSession = null; throw; }
            pendingId = pendingText = pendingDelivery = pendingMethod = pendingSession = null;
        }
        finally { gate.Release(); }
    }
    public async Task InterruptAsync(CancellationToken ct) => _ = await Call("stop", new { id = selected ?? throw new IOException("请先选择会话。") }, ct);
    public async Task<AgentSettings> SettingsAsync(CancellationToken ct)
    {
        var settings = await Call("settings", new { id = selected ?? throw new IOException("请先选择会话。") }, ct);
        var catalog = settings?["catalog"]; var view = settings?["view"]; var models = new List<ModelOption>();
        foreach (var provider in catalog?["groups"]?.AsArray() ?? [])
        {
            string name = provider?["id"]?.ToString() ?? provider?["provider"]?.ToString() ?? "";
            foreach (var model in provider?["models"]?.AsArray() ?? [])
            {
                string id = model?["id"]?.ToString() ?? model?["model"]?.ToString() ?? "";
                if (name.Length > 0 && id.Length > 0) models.Add(new(id, name, name + " / " + id));
            }
        }
        return new(view?["model"]?.ToString() ?? catalog?["default"]?["model"]?.ToString() ?? "",
            view?["provider"]?.ToString() ?? catalog?["default"]?["provider"]?.ToString() ?? "",
            view?["mode"]?.ToString() ?? "default", models, models.Count > 0, capabilities.Contains("plan") && view?["canChangeMode"]?.GetValue<bool>() == true,
            models.Count == 0 ? "当前 DSH 实例尚无可用模型，请为该实例单独配置模型账号；独立后台不复用桌面账号。"
                : "DSH 原生设置：修改模型也会更新 DSH 的默认模型；执行中的 Plan 变更在下一执行步骤生效。审批和提问需在所属 DSH 入口处理。" + (instanceMode == "background" ? "独立后台需单独配置账号；能列出模型不代表已配置凭据。" : ""));
    }
    public async Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct)
    {
        if (pendingId is not null) throw new IOException("上次发送结果未知，请先核对原消息再修改设置。");
        _ = await Call("configure", new { id = selected ?? throw new IOException("请先选择会话。"), provider, model, mode }, ct);
    }
    public async Task ArchiveAsync(string id, bool archived, CancellationToken ct)
    {
        if (pendingId is not null) throw new IOException("上次发送结果未知，暂不能归档。");
        _ = await Call("archive", new { id, archived }, ct);
    }
    public Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct) => throw new NotSupportedException("此 DSH 桥接版本尚未开放远程审批，请在所属 DSH 实例中处理。");
    private async Task MonitorAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                string? id; long generation; object watchArgs;
                using var watching = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    id = selected; generation = selectionGeneration;
                    watchArgs = new { id, cursor, streamRevision, status = current?.Status, pendingMessages = current?.PendingMessages };
                    watchLifetime = watching;
                }
                finally { gate.Release(); }
                try
                {
                    if (id is null) { await Task.Delay(200, watching.Token); continue; }
                    bool canWatch = capabilities.Contains("watch");
                    if (!canWatch) await Task.Delay(1000, watching.Token);
                    var node = await Call(canWatch ? "watch" : "snapshot", canWatch ? watchArgs : new { id }, watching.Token);
                    await gate.WaitAsync(watching.Token);
                    try
                    {
                        if (generation != selectionGeneration || selected != id) continue;
                        long nextCursor = node?["cursor"]?.GetValue<long>() ?? -1;
                        long nextRevision = node?["streamRevision"]?.GetValue<long>() ?? 0;
                        if (nextCursor < cursor || nextCursor == cursor && nextRevision < streamRevision) continue;
                        current = ParseSnapshot(id, node);
                        string next = Json.Node(current).ToJsonString();
                        if (next != fingerprint) { fingerprint = next; Changed?.Invoke(current); }
                    }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException) when (watching.IsCancellationRequested) { }
                catch (Exception ex) when (!lifetime.IsCancellationRequested)
                {
                    await gate.WaitAsync(lifetime.Token);
                    try
                    {
                        if (generation == selectionGeneration && selected == id)
                        { selected = null; Faulted?.Invoke("DSH 连接中断，未重发操作：" + ex.Message); }
                    }
                    finally { gate.Release(); }
                }
                finally
                {
                    await gate.WaitAsync();
                    try { if (watchLifetime == watching) watchLifetime = null; }
                    finally { gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await monitor; http.Dispose(); gate.Dispose(); lifetime.Dispose(); }
}
