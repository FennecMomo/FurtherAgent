using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

/// <summary>Connects to the original Antigravity App conversations from a managed sidecar.</summary>
public sealed class AntigravityDesktopAdapter : IAgentAdapter
{
    private const string Service = "product.v1.ConversationService/";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly HttpClient http;
    private readonly string csrfToken, dataRoot, projectId;
    private readonly Func<string, string, CancellationToken, Task> submit;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile bool observed;
    private SessionView? view;
    private Task? polling;
    private DateTimeOffset? submittedAt;
    private List<ChatEntry>? previousMessages;
    private (string Path, long Length, DateTime Modified)? historyStamp;
    private List<ChatEntry>? history;

    public AgentInfo Info { get; } = new("antigravity", "Antigravity", true, "原生桌面会话 · 历史由 Antigravity 本地记录提供", CanCreate: true, CanArchive: true);
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted { add { } remove { } }

    public AntigravityDesktopAdapter() : this(
        Environment.GetEnvironmentVariable("ANTIGRAVITY_LS_ADDRESS") ?? "",
        Environment.GetEnvironmentVariable("ANTIGRAVITY_CSRF_TOKEN") ?? "",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity"),
        Environment.GetEnvironmentVariable("ANTIGRAVITY_PROJECT_ID") ?? "", SendWithAgentApiAsync) { }

    internal AntigravityDesktopAdapter(string address, string csrfToken, string dataRoot, string projectId,
        Func<string, string, CancellationToken, Task> submit, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback ||
            endpoint.IsDefaultPort || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 ||
            endpoint.Fragment.Length > 0 || string.IsNullOrWhiteSpace(csrfToken))
            throw new IOException("请从 Antigravity 桌面的已启用 Sidecar 启动接入端，以获取本机原生会话接口。");
        this.csrfToken = csrfToken;
        this.dataRoot = dataRoot;
        this.projectId = projectId;
        this.submit = submit;
        http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<JsonNode?> ProbeAsync(CancellationToken ct)
    {
        _ = await RequestAsync("ListConversations", new { pageSize = 1 }, ct);
        return Json.Node(new { backend = "antigravity", desktop = true, canSend = true, history = "transcript.jsonl" });
    }

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct)
    {
        var sessions = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>();
        string token = "";
        do
        {
            var result = await RequestAsync("ListConversations", new { pageSize = 100, pageToken = token }, ct);
            foreach (var item in result["conversations"] as JsonArray ?? [])
            {
                if (item is JsonObject row && Guid.TryParse(Text(row["id"]), out var id))
                    sessions.TryAdd(id.ToString("D"), Session(row));
            }
            token = Text(result["nextPageToken"]);
        } while (token.Length > 0 && visited.Count < 9 && visited.Add(token));
        return sessions.Values.OrderByDescending(s => s.SortTime).ToArray();
    }

    public async Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct)
    {
        var request = new JsonObject { ["title"] = title };
        if (projectId.Length > 0) request["projectId"] = projectId;
        return Session(Conversation(await RequestAsync("CreateConversation", request, ct)));
    }

    public async Task ArchiveAsync(string id, bool archived, CancellationToken ct)
    {
        _ = await RequestAsync("UpdateConversation", new { conversationId = SessionId(id), annotations = new { archived } }, ct);
    }

    public void SetObserved(bool value) => observed = value;

    public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
    {
        id = SessionId(id);
        await gate.WaitAsync(ct);
        try
        {
            if (view is not null && view.Id != id) { view = null; submittedAt = null; previousMessages = null; }
            view = await ReadViewAsync(id, ct);
            polling ??= PollAsync();
            return view;
        }
        finally { gate.Release(); }
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("消息不能为空，请输入文字。");
        await gate.WaitAsync(ct);
        try
        {
            var current = view ?? throw new IOException("请先打开 Antigravity 会话。");
            var latest = await ReadViewAsync(current.Id, ct);
            Publish(latest);
            if (!latest.CanSend) throw new IOException("原会话正在处理任务或同步发送结果，请稍后重试。");
            try { await submit(latest.Id, text, ct); }
            catch (SubmissionUncertainException)
            {
                MarkSubmitted(latest, "Antigravity 消息回执待确认，请先核对桌面原会话。");
                throw;
            }
            MarkSubmitted(latest, "已提交到 Antigravity 原会话，正在同步状态。");
        }
        finally { gate.Release(); }
    }

    private void MarkSubmitted(SessionView latest, string message)
    {
        submittedAt = DateTimeOffset.UtcNow;
        previousMessages = latest.Messages;
        Publish(latest with { CanSend = false, Status = "antigravityPending", StatusDetail = message });
    }

    private async Task<SessionView> ReadViewAsync(string id, CancellationToken ct)
    {
        var row = Conversation(await RequestAsync("GetConversation", new { conversationId = id }, ct));
        var session = Session(row);
        if (session.Id != id) throw new IOException("Antigravity 返回的原会话与当前会话不一致。");
        var messages = await ReadHistoryAsync(id, ct);
        string status = Text(row["state"]?["runState"]) switch
        {
            "RUN_STATE_IDLE" => "idle",
            "RUN_STATE_RUNNING" => "running",
            "RUN_STATE_CANCELLING" => "stopping",
            _ => "antigravityPending"
        };
        string? detail = messages.Count == 0 ? "Antigravity 暂无可读的本地聊天记录。新回复出现后会继续同步。" : null;
        if (submittedAt is { } sent)
        {
            if (status is "running" or "stopping" || previousMessages?.SequenceEqual(messages) == false)
            { submittedAt = null; previousMessages = null; }
            else if (DateTimeOffset.UtcNow - sent > TimeSpan.FromSeconds(15))
            {
                submittedAt = null; previousMessages = null;
                detail = "尚未读到提交后的状态变化，请核对 Antigravity 桌面原会话。";
            }
            else { status = "antigravityPending"; detail = view?.StatusDetail; }
        }
        string? turn = status is "running" or "stopping" ? view?.TurnId ?? Guid.NewGuid().ToString("N") : null;
        return new(id, session.Title, session.Cwd, status, turn, messages, [], StatusDetail: detail,
            CanSend: status == "idle", CanStop: false, CanSteer: false);
    }

    private async Task<List<ChatEntry>> ReadHistoryAsync(string id, CancellationToken ct)
    {
        var file = new FileInfo(AntigravityTranscript.PathFor(dataRoot, id));
        if (!file.Exists) return view?.Id == id ? view.Messages : [];
        (string Path, long Length, DateTime Modified) stamp;
        try { stamp = (file.FullName, file.Length, file.LastWriteTimeUtc); }
        catch (FileNotFoundException) { return view?.Id == id ? view.Messages : []; }
        if (historyStamp == stamp && history is not null) return history;
        var read = await AntigravityTranscript.ReadAsync(dataRoot, id, ct);
        if (read.Count == 0 && view?.Id == id && view.Messages.Count > 0) return view.Messages;
        historyStamp = stamp;
        return history = read;
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        var ct = lifetime.Token;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (view is not { } current || (!observed && submittedAt is null && current.TurnId is null)) continue;
                    try { Publish(await ReadViewAsync(current.Id, ct)); }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is (IOException or HttpRequestException or OperationCanceledException or InvalidOperationException))
                    {
                        Publish(current with { CanSend = false, Status = "antigravityPending", StatusDetail = "暂时无法读取 Antigravity 原会话，正在重试。" });
                    }
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void Publish(SessionView next)
    {
        if (view is { } old && old with { Messages = next.Messages, Prompts = next.Prompts } == next && old.Messages.SequenceEqual(next.Messages)) return;
        view = next;
        Changed?.Invoke(next);
    }

    private async Task<JsonObject> RequestAsync(string method, object args, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Service + method) { Content = JsonContent.Create(args, options: Json.Options) };
        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Add("x-antigravity-csrf-token", csrfToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"Antigravity 桌面接口 {method} 返回 HTTP {(int)response.StatusCode}。");
        try { return (await response.Content.ReadFromJsonAsync<JsonObject>(Json.Options, ct)) ?? throw new IOException("Antigravity 桌面接口没有返回会话数据。"); }
        catch (JsonException ex) { throw new IOException("Antigravity 桌面接口返回了无法识别的会话数据。", ex); }
    }

    private static JsonObject Conversation(JsonObject result) => (result["conversation"] as JsonObject ?? result)
        is { } row && Guid.TryParse(Text(row["id"]), out _) ? row : throw new IOException("Antigravity 返回了无效的会话标识。");

    private static SessionInfo Session(JsonObject row)
    {
        string title = Text(row["annotations"]?["title"]);
        if (title.Length == 0) title = Text(row["state"]?["summary"]);
        return new(SessionId(Text(row["id"])), title.Length > 0 ? title : "Antigravity 会话", Text(row["state"]?["updateTime"]));
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static string SessionId(string id) => Guid.TryParse(id, out var value) ? value.ToString("D") : throw new ArgumentException("Antigravity 会话 ID 无效。");

    private static async Task SendWithAgentApiAsync(string id, string text, CancellationToken ct)
    {
        var installed = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Antigravity", "resources", "bin", "language_server.exe")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "bin", "agentapi");
        string executable = Environment.GetEnvironmentVariable("FURTHERAGENT_ANTIGRAVITY_EXE") ?? installed;
        if (!File.Exists(executable)) throw new IOException("找不到 Antigravity 的 agentapi。可在 Sidecar 配置中指定 FURTHERAGENT_ANTIGRAVITY_EXE。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        if (Path.GetFileNameWithoutExtension(executable).Equals("language_server", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add("agentapi");
        start.ArgumentList.Add("send-message"); start.ArgumentList.Add("--"); start.ArgumentList.Add(id); start.ArgumentList.Add(text);
        ct.ThrowIfCancellationRequested();
        using var proc = Process.Start(start) ?? throw new IOException("无法启动 Antigravity agentapi。");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException ex)
        {
            try { if (!proc.HasExited) proc.Kill(); } catch (Exception kill) when (kill is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new SubmissionUncertainException("Antigravity 发送回执未确认，请先核对桌面原会话。", ex);
        }
        if (proc.ExitCode != 0) throw new SubmissionUncertainException($"Antigravity agentapi 返回 {proc.ExitCode}，请先核对原会话再重试。");
        try
        {
            var result = JsonNode.Parse(await stdout) as JsonObject;
            if (result?["response"] is JsonObject && Text(result["error"]).Length == 0) return;
        }
        catch (JsonException) { }
        throw new SubmissionUncertainException("Antigravity agentapi 未返回成功回执，请先核对桌面原会话。");
    }

    public Task InterruptAsync(CancellationToken ct) => throw new NotSupportedException("请在 Antigravity 桌面停止当前任务。");
    public Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct) => throw new NotSupportedException("请在 Antigravity 桌面处理审批。");

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { if (polling is not null) await polling; }
        finally { http.Dispose(); gate.Dispose(); lifetime.Dispose(); }
    }
}
