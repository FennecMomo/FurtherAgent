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
    private readonly object observationGate = new();
    private readonly string observerId = Guid.NewGuid().ToString("N");
    private long observerRevision;
    private Task observationUpdate = Task.CompletedTask;
    private string? leasedSession;
    private bool observed;
    private string? selected;
    private CancellationTokenSource? watchLifetime;
    private long selectionGeneration, cursor = -1, streamRevision;
    private string instanceMode = "desktop";
    private string runtimeVersion = "unknown";
    private HashSet<string> capabilities = [];
    private SessionView? current;
    private string? fingerprint;
    private string? pendingText, pendingId, pendingSession;
    private string? pendingDelivery, pendingMethod, pendingTurnId;
    private bool pendingAccepted;
    private readonly HashSet<(string Session, string Request)> submittedPrompts = [];
    public AgentInfo Info => new("dsh", instanceMode == "background" ? "DSH · 独立后台" : "DSH · 桌面", IsAvailable,
        $"DSH {runtimeVersion} · 同一 Host 原生会话服务；查看历史不取得会话写入权。" + (instanceMode == "background" ? "独立后台需单独配置模型账号，模型目录不代表凭据已配置。" : ""), CanCreate: capabilities.Contains("create"), CanConfigure: capabilities.Contains("model"), CanArchive: capabilities.Contains("archive"),
        CanChooseDelivery: capabilities.Contains("delivery"), CanInterruptSend: capabilities.Contains("interrupt-send"),
        CanFiles: capabilities.Contains("files"), CanThumbnails: capabilities.Contains("thumbnails"), CanWorkspaces: capabilities.Contains("workspaces"));
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
        return (data?["items"]?.AsArray() ?? [])
            .Where(x => (x?["origin"] is JsonObject origin ? origin["kind"]?.ToString() : x?["origin"]?.ToString()) != "subagent")
            .Select(x =>
            {
                string id = x!["sessionId"]!.ToString();
                var title = x["projections"]?["values"]?["title"] ?? x["title"];
                return new SessionInfo(id, (title is JsonObject value ? value["title"] : title)?.ToString() ?? id,
                    x["updatedAt"]?.ToString() ?? "", x["cwd"]?.ToString() ?? x["header"]?["cwd"]?.ToString() ?? "");
            }).OrderByDescending(x => x.SortTime).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }
    public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            bool leaveConfirmedSession = pendingId is not null && pendingSession != id;
            if (leaveConfirmedSession && !pendingAccepted) throw new IOException("上次发送结果未知，请先核对原会话。");
            watchLifetime?.Cancel(); selectionGeneration++;
            cursor = -1; streamRevision = 0;
            var view = await ReadSnapshot(id, ct);
            if (leaveConfirmedSession) ClearPending();
            current = view; fingerprint = Json.Node(view).ToJsonString();
            lock (observationGate)
            {
                selected = id; observerRevision++;
                observationUpdate = UpdateObservationAsync(observationUpdate);
            }
            return view;
        }
        finally { gate.Release(); }
    }
    public Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct) => CreateAsync(id, title, "", ct);
    public async Task<SessionInfo> CreateAsync(string id, string title, string cwd, CancellationToken ct)
    {
        if (cwd.Length > 0 && !capabilities.Contains("workspaces")) throw new NotSupportedException("此 DSH 插件不支持指定工作空间，请升级插件。");
        var result = await Call("create", new { id, title, cwd }, ct);
        return new(result!["sessionId"]!.ToString(), result["title"]?.ToString() ?? title, DateTimeOffset.UtcNow.ToString("O"), result["cwd"]?.ToString() ?? cwd);
    }
    public async Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken ct)
    {
        RequireCapability("workspaces", "工作空间");
        return Json.Read<WorkspaceInfo[]>(await Call("workspaces", null, ct));
    }
    public async Task<WorkspaceDirectories> BrowseDirectoriesAsync(string path, CancellationToken ct)
    {
        RequireCapability("workspaces", "浏览工作空间目录");
        return Json.Read<WorkspaceDirectories>(await Call("browseDirectories", new { path }, ct));
    }
    public async Task<WorkspaceInfo> AddWorkspaceAsync(string id, string path, string folder, CancellationToken ct)
    {
        RequireCapability("workspaces", "新增工作空间");
        return Json.Read<WorkspaceInfo>(await Call("addWorkspace", new { id, path, folder }, ct));
    }
    private void RequireCapability(string capability, string action)
    {
        if (!capabilities.Contains(capability)) throw new NotSupportedException($"此 DSH 插件尚未开放{action}，请升级插件。");
    }
    public async Task SetFileWorkspaceAsync(string outbox, CancellationToken ct)
    {
        RequireCapability("files", "文件传输");
        _ = await Call("fileWorkspace", new { id = selected ?? throw new IOException("请先选择会话。"), outbox }, ct);
    }
    public void SetObserved(bool value)
    {
        lock (observationGate)
        {
            observed = value; observerRevision++;
            observationUpdate = UpdateObservationAsync(observationUpdate);
        }
        // A long poll made before this change must not keep its old viewer lease.
        // Cancelling it also starts a new watch immediately with the new revision.
        try { watchLifetime?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task UpdateObservationAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
            string? id; bool active; long observedRevision;
            lock (observationGate) { id = selected; active = observed; observedRevision = observerRevision; }
            if (!capabilities.Contains("respond") || lifetime.IsCancellationRequested) return;
            if (leasedSession is { } old && (old != id || !active))
            {
                _ = await Call("observe", new { id = old, observed = false, observerId, observerRevision = observedRevision }, lifetime.Token);
                leasedSession = null;
            }
            if (id is not null)
            {
                // The bridge may accept this lease even if its acknowledgement is lost.
                if (active) leasedSession = id;
                _ = await Call("observe", new { id, observed = active, observerId, observerRevision = observedRevision }, lifetime.Token);
            }
        }
        catch (Exception) { /* Read-only watch renews the lease after a transient connection loss. */ }
    }
    private async Task<SessionView> ReadSnapshot(string id, CancellationToken ct)
    {
        var node = await Call("snapshot", new { id }, ct);
        return ParseSnapshot(id, node);
    }
    private SessionView ParseSnapshot(string id, JsonNode? node)
    {
        var view = Json.Read<SessionView>(node);
        if (view.Id != id) throw new IOException("DSH 返回了其他会话的快照，已拒绝切换。");
        cursor = node?["cursor"]?.GetValue<long>() ?? -1;
        streamRevision = node?["streamRevision"]?.GetValue<long>() ?? 0;
        if (pendingId is not null && id == pendingSession && (node?["acceptedRequestIds"]?.AsArray() ?? []).Any(x => x?.ToString() == pendingId))
            pendingAccepted = true;
        return view;
    }
    private void ClearPending()
    {
        pendingId = pendingText = pendingDelivery = pendingMethod = pendingSession = pendingTurnId = null;
        pendingAccepted = false;
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
            if (pendingId is not null && !pendingAccepted && (pendingText != text || pendingMethod != method))
                current = await ReadSnapshot(selected, ct);
            if (pendingAccepted)
            {
                bool confirmsOriginal = pendingText == text;
                ClearPending();
                // The first acknowledgement was lost, but native history proves receipt.
                // A retry confirms that submission rather than creating a second request.
                if (confirmsOriginal) return;
            }
            if (pendingId is not null && (pendingText != text || pendingMethod != method)) throw new IOException("上次发送结果未知，请先核对原消息，不能提交另一条。");
            if (pendingId is null) current = await ReadSnapshot(selected, ct);
            pendingDelivery ??= delivery == "auto" ? current?.Status == "active" ? "steer" : "queue" : delivery;
            pendingId ??= Guid.NewGuid().ToString("N"); pendingText = text;
            pendingMethod = method; pendingSession = selected;
            pendingTurnId ??= turnId;
            try { await Call(method, new { id = selected, text, requestId = pendingId, mode = pendingDelivery, turnId = pendingTurnId }, ct); }
            catch (BridgeException ex) when (ex.Code == "bridge/not-submitted")
            { ClearPending(); throw; }
            ClearPending();
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
                var efforts = (model?["reasoningEfforts"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (name.Length > 0 && id.Length > 0) models.Add(new(id, name, model?["label"]?.ToString() ?? name + " / " + id,
                    efforts, model?["defaultReasoningEffort"]?.ToString()));
            }
        }
        return new(view?["model"]?.ToString() ?? catalog?["default"]?["model"]?.ToString() ?? "",
            view?["provider"]?.ToString() ?? catalog?["default"]?["provider"]?.ToString() ?? "",
            view?["mode"]?.ToString() ?? "default", models, models.Count > 0, capabilities.Contains("plan") && view?["canChangeMode"]?.GetValue<bool>() == true,
            models.Count == 0 ? "当前 DSH 实例尚无可用模型，请为该实例单独配置模型账号；独立后台不复用桌面账号。"
                : "DSH 原生设置：修改模型也会更新 DSH 的默认模型；执行中的 Plan 变更在下一执行步骤生效。" + (!capabilities.Contains("respond") ? "此插件尚未开放远程审批和提问，请在所属 DSH 入口处理。" : "审批和提问通过所属 DSH Host 回应。") + (instanceMode == "background" ? "独立后台需单独配置账号；能列出模型不代表已配置凭据。" : ""),
            view?["reasoningEffort"]?.ToString(), capabilities.Contains("reasoning") && models.Any(m => m.ReasoningEfforts?.Count > 0), RequireModelSelection: true,
            Permission: view?["permission"]?.ToString(), CanChangePermissions: capabilities.Contains("permissions") && view?["canChangePermissions"]?.GetValue<bool>() == true,
            PermissionOptions: settings?["permissionOptions"] is JsonArray permissions ? Json.Read<List<PermissionOption>>(permissions) : null);
    }
    public Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct) => ConfigureAsync(model, provider, mode, null, ct);
    public async Task ConfigureAsync(string model, string provider, string mode, string? reasoningEffort, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (pendingId is not null && !pendingAccepted) throw new IOException("上次发送结果未知，请先核对原消息再修改设置。");
            if (!string.IsNullOrEmpty(reasoningEffort)) RequireCapability("reasoning", "思考强度设置");
            _ = await Call("configure", new { id = selected ?? throw new IOException("请先选择会话。"), provider, model, mode, reasoningEffort }, ct);
        }
        finally { gate.Release(); }
    }
    public async Task ArchiveAsync(string id, bool archived, CancellationToken ct)
    {
        if (pendingId is not null && !pendingAccepted) throw new IOException("上次发送结果未知，暂不能归档。");
        _ = await Call("archive", new { id, archived }, ct);
    }
    public async Task ConfigurePermissionsAsync(string permission, CancellationToken ct)
    {
        RequireCapability("permissions", "会话权限设置");
        await gate.WaitAsync(ct);
        try
        {
            if (pendingId is not null && !pendingAccepted) throw new IOException("上次发送结果未知，请先核对原消息再修改权限。");
            _ = await Call("configurePermissions", new { id = selected ?? throw new IOException("请先选择会话。"), permission }, ct);
        }
        finally { gate.Release(); }
    }
    public async Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct)
    {
        RequireCapability("respond", "远程审批和提问");
        await gate.WaitAsync(ct);
        try
        {
            string id = selected ?? throw new IOException("请先选择会话。");
            current = await ReadSnapshot(id, ct);
            if (current.Prompts.All(p => p.Id != requestId) || !submittedPrompts.Add((id, requestId)))
                throw new IOException("此确认已处理、已失效或正在提交，请刷新会话。");
            // Reserve before I/O. An uncertain acknowledgement must not submit an approval twice.
            _ = await Call("respond", new { id, requestId, decision, response }, ct);
        }
        finally { gate.Release(); }
    }
    private async Task MonitorAsync()
    {
        try
        {
            bool interrupted = false;
            int failures = 0;
            while (!lifetime.IsCancellationRequested)
            {
                string? id; long generation; object watchArgs;
                using var watching = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    id = selected; generation = selectionGeneration;
                    lock (observationGate)
                        watchArgs = new { id, cursor, streamRevision, status = current?.Status, pendingMessages = current?.PendingMessages, observed, observerId, observerRevision };
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
                        // Each selection generation has only one sequential watcher. Counter
                        // eviction or plugin reload may reset a revision without an I/O failure.
                        current = ParseSnapshot(id, node);
                        interrupted = false; failures = 0;
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
                        {
                            if (!interrupted) Faulted?.Invoke("DSH 连接中断，正在恢复只读订阅，未重发操作：" + ex.Message);
                            interrupted = true;
                        }
                    }
                    finally { gate.Release(); }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 1 << Math.Min(failures++, 4))), lifetime.Token);
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
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); await monitor;
        Task update; lock (observationGate) update = observationUpdate;
        await update;
        http.Dispose(); gate.Dispose(); lifetime.Dispose();
    }
}
