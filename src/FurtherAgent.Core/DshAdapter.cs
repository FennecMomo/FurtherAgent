using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

/// <summary>Connects to a plugin inside the selected DSH Host; never launches or resumes sessions to read them.</summary>
public sealed class DshAdapter : IAgentAdapter
{
    private readonly string descriptorPath;
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Task monitor;
    private string? selected;
    private string instanceMode = "desktop";
    private string runtimeVersion = "unknown";
    private HashSet<string> capabilities = [];
    private SessionView? current;
    private string? fingerprint;
    private string? pendingText, pendingId;
    public AgentInfo Info => new("dsh", instanceMode == "background" ? "DSH · 独立后台" : "DSH · 桌面", true,
        $"DSH {runtimeVersion} · 同一 Host 原生会话服务；查看历史不取得会话写入权。", CanCreate: capabilities.Contains("create"), CanConfigure: capabilities.Contains("model"), CanArchive: capabilities.Contains("archive"));
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted;
    public DshAdapter(string descriptorPath) { this.descriptorPath = Path.GetFullPath(descriptorPath); monitor = MonitorAsync(); }
    public static string InstanceDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "dsh", "instances");
    public static string FindDescriptor(string mode)
    {
        var matches = Directory.Exists(InstanceDirectory) ? Directory.GetFiles(InstanceDirectory, mode + "-*.json").Where(path =>
        {
            try { var d = JsonNode.Parse(File.ReadAllText(path))!; return !System.Diagnostics.Process.GetProcessById(d["pid"]!.GetValue<int>()).HasExited; }
            catch { return false; }
        }).ToArray() : [];
        return matches.Length switch { 1 => matches[0], 0 => throw new IOException("未发现 DSH 适配插件，请先启用对应模式的插件。"), _ => throw new IOException("发现多个 DSH 实例，请显式指定连接描述文件。") };
    }
    private async Task<JsonNode?> Call(string method, object? args, CancellationToken ct)
    {
        var d = JsonNode.Parse(await File.ReadAllTextAsync(descriptorPath, ct))!;
        var endpoint = new Uri(d["endpoint"]!.GetValue<string>());
        if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.UserInfo.Length != 0 || d["protocol"]?.GetValue<int>() != 1)
            throw new IOException("无效的 DSH 本机连接描述。");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "rpc"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", d["token"]!.GetValue<string>());
        request.Content = JsonContent.Create(new { method, args }, options: Json.Options);
        using var response = await http.SendAsync(request, ct);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode || result?["error"] is not null)
            throw new IOException(result?["error"]?["message"]?.ToString() ?? $"DSH HTTP {(int)response.StatusCode}");
        return result?["result"]?.DeepClone();
    }
    public async Task<JsonNode?> ProbeAsync(CancellationToken ct = default)
    {
        var info = await Call("info", null, ct);
        if (info?["protocol"]?.GetValue<int>() != 1) throw new IOException("DSH 插件协议不兼容。");
        instanceMode = info["mode"]?.ToString() ?? "desktop";
        runtimeVersion = info["hostVersion"]?.ToString() ?? "unknown";
        capabilities = (info["capabilities"]?.AsArray() ?? []).Select(x => x!.ToString()).ToHashSet(StringComparer.Ordinal);
        return info;
    }
    public async Task ShutdownBackgroundAsync(CancellationToken ct = default) => _ = await Call("shutdown", null, ct);
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct)
    {
        var data = await Call("list", null, ct);
        return (data?["items"]?.AsArray() ?? []).Where(x => x?["origin"]?.ToString() != "subagent")
            .Select(x => new SessionInfo(x!["sessionId"]!.ToString(), x["title"]?.ToString() ?? x["sessionId"]!.ToString(),
                DateTimeOffset.FromUnixTimeMilliseconds(x["updatedAt"]?.GetValue<long>() ?? 0).ToString("O"))).ToArray();
    }
    public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (pendingId is not null && selected != id) throw new IOException("上次发送结果未知，请先核对原会话。");
            var view = Json.Read<SessionView>(await Call("snapshot", new { id }, ct));
            selected = id; current = view; fingerprint = Json.Node(view).ToJsonString(); return view;
        }
        finally { gate.Release(); }
    }
    public async Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct)
    {
        var result = await Call("create", new { id, title }, ct);
        return new(result!["sessionId"]!.ToString(), title, DateTimeOffset.UtcNow.ToString("O"));
    }
    public async Task SendAsync(string text, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (selected is null) throw new IOException("请先选择会话。");
            if (pendingId is not null && pendingText != text) throw new IOException("上次发送结果未知，请先核对原消息，不能提交另一条。");
            pendingId ??= Guid.NewGuid().ToString("N"); pendingText = text;
            await Call("send", new { id = selected, text, requestId = pendingId, mode = current?.Status == "active" ? "steer" : "queue" }, ct);
            pendingId = pendingText = null;
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
            "DSH 原生设置：修改模型也会更新 DSH 的默认模型；执行中的 Plan 变更在下一执行步骤生效。");
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
                await Task.Delay(1000, lifetime.Token);
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    if (selected is null) continue;
                    current = Json.Read<SessionView>(await Call("snapshot", new { id = selected }, lifetime.Token));
                    string next = Json.Node(current).ToJsonString();
                    if (next != fingerprint) { fingerprint = next; Changed?.Invoke(current); }
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested)
                { Faulted?.Invoke("DSH 连接中断，未重发操作：" + ex.Message); selected = null; }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await monitor; http.Dispose(); gate.Dispose(); lifetime.Dispose(); }
}
