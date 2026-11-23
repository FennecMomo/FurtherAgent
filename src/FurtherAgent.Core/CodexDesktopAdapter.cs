using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// Desktop IPC (v11/v13) or the native queue for versions without that pipe.
// Never starts a second writer or edits Codex history/configuration directly.
public sealed class CodexDesktopAdapter : IAgentAdapter
{
    private const string DesktopPipeName = "codex-ipc";
    private readonly string home;
    private readonly NamedPipeClientStream pipe = new(".", DesktopPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private string clientId = "initializing-client";
    private string? sessionId, owner;
    private JsonNode? state;
    private long revision;
    private readonly SemaphoreSlim answerWrites = new(1, 1);
    private readonly HashSet<string> submittedQuestions = [];
    private Task? reader;
    private TaskCompletionSource<SessionView>? snapshot;
    private SessionView? savedView;
    private volatile CodexQueuedSession? savedQueue;
    private volatile bool observed = true;
    private sealed class DesktopPipeUnavailable(Exception? cause = null)
        : IOException("无法连接 Codex 桌面。请在 B 上以同一 Windows 用户打开 Codex。", cause);
    public AgentInfo Info => new("codex", "Codex 桌面", true, "桌面实时接口与原生消息队列 · 支持远程工作空间和空会话", true, true, CanWorkspaces: true);
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted;
    public void SetObserved(bool value)
    {
        observed = value;
        savedQueue?.SetObserved(value);
    }
    public CodexDesktopAdapter(string? codexHome = null) => home = codexHome ??
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken ct) => new CodexWorkspaces(home).ListAsync(ct);
    public Task<WorkspaceDirectories> BrowseDirectoriesAsync(string path, CancellationToken ct) => Task.FromResult(CodexWorkspaces.Browse(path, ct));
    public Task<WorkspaceInfo> AddWorkspaceAsync(string id, string path, string folder, CancellationToken ct) => new CodexWorkspaces(home).AddAsync(id, path, folder, ct);
    public Task<SessionInfo> CreateAsync(string id, string title, string cwd, CancellationToken ct) => new CodexWorkspaces(home).CreateAsync(id, title, cwd, ct);

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct)
    {
        var file = Path.Combine(home, "session_index.jsonl");
        if (!File.Exists(file)) throw new IOException("没有找到 Codex 会话索引。请先在此 Windows 用户下打开 Codex 并创建会话。");
        var entries = new Dictionary<string, SessionInfo>();
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(stream);
        while (await r.ReadLineAsync(ct) is { } line)
        {
            try
            {
                var item = JsonNode.Parse(line);
                var id = item?["id"]?.GetValue<string>();
                if (id is not null) entries[id] = new(id, Text(item!["thread_name"]), Text(item["updated_at"]));
            }
            catch (System.Text.Json.JsonException) { /* Concurrent partial last line. */ }
        }
        return CodexSessionCatalog.Enrich(home, entries.Values).OrderByDescending(x => x.SortTime).ThenBy(x => x.Id).Take(300).ToArray();
    }
    private async Task ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reader is not null)
        {
            if (reader.IsCompleted) throw new IOException("Codex 桌面连接已断开，请重新连接远端设备。");
            return;
        }
        if (IsDesktopPipeMissing()) throw new DesktopPipeUnavailable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { await pipe.ConnectAsync(timeout.Token); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (OperationCanceledException or IOException or UnauthorizedAccessException))
        { throw new DesktopPipeUnavailable(ex); }
        reader = ReadLoopAsync();
        var result = await RequestAsync("initialize", new { clientType = "furtheragent" }, 0, ct, targeted: false);
        clientId = result["result"]!["clientId"]!.GetValue<string>();
    }

    private static bool IsDesktopPipeMissing()
    {
        if (!OperatingSystem.IsWindows()) return false;
        // Newer desktops do not always start this listener. A known-absent pipe can
        // use stored history immediately, while an inconclusive check still tries IPC.
        try { return !Directory.EnumerateFiles(@"\\.\pipe\", DesktopPipeName).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("会话 ID 无效。");
        if (savedQueue is { } existing && existing.View.Id == id) return existing.AttachCached(ct);
        try { await ConnectAsync(ct); }
        catch (DesktopPipeUnavailable) when (OperatingSystem.IsWindows())
        {
            await using var server = await CodexAppServer.OpenAsync(home, ct);
            var view = await CodexQueuedSession.ReadAsync(server, id, ct);
            ct.ThrowIfCancellationRequested();
            var previous = savedQueue;
            savedQueue = null;
            if (previous is not null) await previous.DisposeAsync();
            lock (gate) savedView = view;
            if (view.CanSend)
            {
                var queue = new CodexQueuedSession(view, OpenMetadataAsync, snapshot => Changed?.Invoke(snapshot));
                savedQueue = queue;
                queue.SetObserved(observed);
            }
            else CodexThreadActivation.Open(CodexThreadActivation.ThreadUrl(id));
            return view;
        }
        var disabled = savedQueue;
        savedQueue = null;
        if (disabled is not null) await disabled.DisposeAsync();
        lock (gate) savedView = null;
        string discoveredOwner = await CodexThreadActivation.ActivateAsync(id, CodexThreadActivation.Open, async token =>
        {
            var discovered = await RequestAsync("thread-owner-discovery", new { hostId = "local", conversationId = id },
                1, token, false, timeoutMs: 2000, allowMissingOwner: true);
            return Text(discovered["resultType"]) == "error" ? null
                : discovered["handledByClientId"]?.GetValue<string>() ?? throw new IOException("桌面返回的会话宿主无效。");
        }, ct);
        lock (gate)
        {
            if (sessionId == id && owner == discoveredOwner && state is not null && reader?.IsCompleted == false)
                return ToView(state);
        }
        if (sessionId is not null) await FollowAsync(false, ct);
        lock (gate)
        {
            sessionId = id; owner = discoveredOwner; state = null; revision = 0;
            snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        await FollowAsync(true, ct);
        try { return await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (TimeoutException) { throw new IOException("桌面会话已打开，但未及时收到快照，请稍后重新选择该会话。"); }
    }

    private async Task<ICodexMetadata> OpenMetadataAsync(CancellationToken ct) => await CodexAppServer.OpenAsync(home, ct);

    private Task FollowAsync(bool following, CancellationToken ct) => WriteAsync(Json.Node(new
    {
        type = "broadcast", method = "thread-stream-following-changed", sourceClientId = clientId, version = 1,
        targetClientIds = owner is null ? null : new[] { owner },
        @params = new { conversationId = sessionId, hostId = "local", following }
    }), ct);
    private async Task<JsonNode> RequestAsync(string method, object args, int version, CancellationToken ct, bool targeted = true,
        int timeoutMs = 25000, bool allowMissingOwner = false)
    {
        string id = Guid.NewGuid().ToString();
        var source = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = source;
        try
        {
            await WriteAsync(Json.Node(new { type = "request", requestId = id, sourceClientId = clientId,
                method, @params = args, version, targetClientId = targeted ? owner : null, timeoutMs }), ct);
            var reply = await source.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 3000), ct);
            if (Text(reply["resultType"]) != "success" &&
                !(allowMissingOwner && Text(reply["resultType"]) == "error" && Text(reply["error"]) == "no-client-found"))
                throw CodexSubmissionReceipt.RequestError(Text(reply["error"]));
            return reply;
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task WriteAsync(JsonNode node, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try { await Frames.WriteAsync(pipe, node, ct); }
        finally { writes.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var m = await Frames.ReadDesktopAsync(pipe, lifetime.Token);
                switch (Text(m["type"]))
                {
                    case "response":
                        if (pending.TryGetValue(Text(m["requestId"]), out var source)) source.TrySetResult(m);
                        break;
                    case "client-discovery-request":
                        await WriteAsync(Json.Node(new { type = "client-discovery-response", requestId = Text(m["requestId"]), response = new { canHandle = false } }), lifetime.Token);
                        break;
                    case "broadcast": await BroadcastAsync(m); break;
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var source in pending.Values) source.TrySetException(new IOException("Codex 桌面连接已断开。", ex));
            snapshot?.TrySetException(ex);
            if (!lifetime.IsCancellationRequested) Faulted?.Invoke("Codex 桌面连接已断开：" + ex.Message);
        }
    }
    private async Task BroadcastAsync(JsonNode m)
    {
        var p = m["params"];
        if (Text(p?["conversationId"]) != sessionId || Text(p?["hostId"]) != "local") return;
        if (m["targetClientIds"] is JsonArray targets && !targets.Any(t => Text(t) == clientId)) return;
        if (Text(m["method"]) == "thread-stream-following-status-requested") { await FollowAsync(true, lifetime.Token); return; }
        if (Text(m["method"]) != "thread-stream-state-changed" || Text(m["sourceClientId"]) != owner) return;
        if (m["version"]?.GetValue<int>() is not (11 or 13)) throw new IOException("不兼容的 Codex 桌面流协议版本，需要更新适配器。");
        var change = p!["change"]!;
        bool resync = false;
        SessionView? view = null;
        lock (gate)
        {
            if (Text(change["type"]) == "snapshot") state = change["conversationState"]!.DeepClone();
            else if (Text(change["type"]) == "patches" && state is not null && change["baseRevision"]!.GetValue<long>() == revision)
            {
                try { StatePatches.Apply(state, change["patches"]!.AsArray()); }
                catch { state = null; resync = true; }
            }
            else resync = true;
            if (!resync && state is not null)
            {
                revision = change["revision"]!.GetValue<long>();
                view = ToView(state);
            }
        }
        if (resync) { await FollowAsync(false, lifetime.Token); await FollowAsync(true, lifetime.Token); return; }
        if (view is not null) { snapshot?.TrySetResult(view); Changed?.Invoke(view); }
    }
    public async Task<AgentSettings> SettingsAsync(CancellationToken ct)
    {
        JsonNode value;
        lock (gate)
        {
            if ((savedQueue?.View ?? savedView) is { } view)
                return new(view.Model ?? "", "", view.Mode ?? "default", [], false, false, "请在 Codex 桌面修改会话设置。");
            value = (state ?? throw new IOException("请先打开会话。")).DeepClone();
        }
        string model = Text(value["latestCollaborationMode"]?["settings"]?["model"] ?? value["latestModel"]);
        string provider = Text(value["modelProvider"]);
        string effort = Text(value["latestCollaborationMode"]?["settings"]?["reasoning_effort"] ?? value["latestReasoningEffort"]);
        await using var metadata = await CodexAppServer.OpenAsync(home, ct);
        List<ModelOption> models = [];
        string detail = "读取远端 Codex 模型目录。空闲时修改，后续轮次生效。", usage;
        try { models = await CodexSettingsCatalog.ModelsAsync(metadata, provider, ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
        { detail = "模型列表读取失败，请刷新重试：" + ex.Message; }
        try { usage = CodexSettingsCatalog.Usage(await metadata.CallAsync("account/rateLimits/read", new { }, ct)); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
        { usage = "套餐限额暂不可用：" + ex.Message; }
        return new(model, provider, Text(value["latestCollaborationMode"]?["mode"]) is "plan" ? "plan" : "default",
            models, models.Count > 0, true, detail, effort, models.Any(m => m.ReasoningEfforts?.Count > 0), usage, true, CodexPermissions.Current(value), true);
    }
    public Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct) => ConfigureAsync(model, provider, mode, null, ct);
    public async Task ConfigureAsync(string model, string provider, string mode, string? reasoningEffort, CancellationToken ct)
    {
        lock (gate) _ = RequireLiveState();
        reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();
        await using var metadata = await CodexAppServer.OpenAsync(home, ct);
        var models = await CodexSettingsCatalog.ModelsAsync(metadata, provider, ct);
        var selected = models.SingleOrDefault(m => m.Id == model.Trim()) ?? throw new ArgumentException("该模型不在远端可用目录中，请刷新后选择。");
        if (reasoningEffort is not null && selected.ReasoningEfforts?.Contains(reasoningEffort) != true)
            throw new ArgumentException("该模型不支持所选思考强度，请刷新后选择。");
        JsonNode collaboration;
        string id;
        lock (gate)
        {
            var value = RequireLiveState();
            if (ToView(value) is { } view && (view.Status == "active" || view.TurnId is not null)) throw new IOException("请等待当前任务结束后修改模型、模式或思考强度。");
            if (string.IsNullOrWhiteSpace(model) || mode is not ("default" or "plan")) throw new ArgumentException("模型或模式无效。");
            id = sessionId!;
            collaboration = value["latestCollaborationMode"]?.DeepClone() ?? throw new IOException("桌面快照缺少协作模式，请升级适配器。");
            collaboration["mode"] = mode;
            collaboration["settings"]!["model"] = model.Trim();
            string previousEffort = Text(collaboration["settings"]!["reasoning_effort"]);
            reasoningEffort = CodexSettingsCatalog.SelectEffort(selected, reasoningEffort, previousEffort);
            collaboration["settings"]!["reasoning_effort"] = reasoningEffort;
            // Let Codex apply its native mode instructions, rather than copying instructions from a different mode.
            collaboration["settings"]!["developer_instructions"] = null;
        }
        var result = await RequestAsync("thread-follower-update-thread-settings", new { conversationId = id,
            threadSettings = new { model = model.Trim(), effort = reasoningEffort, collaborationMode = collaboration }, activeTurnId = (string?)null }, 2, ct);
        if (result["result"]?["applied"]?.GetValue<bool>() != true) throw new IOException("桌面未应用会话设置。");
    }
    public async Task ConfigurePermissionsAsync(string permission, CancellationToken ct)
    {
        var settings = CodexPermissions.Build(permission);
        string id;
        lock (gate)
        {
            var view = ToView(RequireLiveState());
            if (view.Status == "active" || view.TurnId is not null) throw new IOException("请等待当前任务结束后修改权限。");
            id = sessionId!;
        }
        var result = await RequestAsync("thread-follower-update-thread-settings", new { conversationId = id,
            threadSettings = settings, activeTurnId = (string?)null }, 2, ct);
        if (result["result"]?["applied"]?.GetValue<bool>() != true) throw new IOException("桌面未应用会话权限。");
    }
    public Task SendAsync(string text, CancellationToken ct) => SendMessageAsync(text, false, ct);
    private async Task SendMessageAsync(string text, bool questionReply, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100000) throw new ArgumentException("消息不能为空或超过 100000 字符。");
        if (!questionReply && savedQueue is { } queue)
        {
            await queue.SendAsync(text, ct);
            return;
        }
        SessionView view;
        lock (gate) view = ToView(RequireLiveState());
        var request = CodexMessageSubmission.Build(view, text, questionReply);
        string messageId = Text(request.Parameters["clientUserMessageId"] ?? request.Parameters["turnStart"]?["request"]?["clientUserMessageId"]);
        await CodexSubmissionReceipt.WaitAsync(
            RequestAsync(request.Method, request.Parameters, request.Version, ct),
            () => { lock (gate) return sessionId == view.Id && CodexSubmissionReceipt.Contains(state, messageId); }, ct);
    }
    public async Task InterruptAsync(CancellationToken ct)
    {
        SessionView view;
        lock (gate) view = ToView(RequireLiveState());
        if (view.Status != "active" || view.TurnId is null) throw new IOException("没有可停止的活动任务。");
        await RequestAsync("thread-follower-interrupt-turn", new { conversationId = sessionId, mode = "user-stop", expectedTurnId = view.TurnId }, 4, ct);
    }
    public async Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct)
    {
        lock (gate) _ = RequireLiveState();
        if (requestId.StartsWith("codex-recovery:", StringComparison.Ordinal))
        {
            if (decision != "continue") throw new ArgumentException("无效恢复操作。");
            await answerWrites.WaitAsync(ct);
            try
            {
                lock (gate)
                {
                    var view = ToView(RequireLiveState());
                    if (submittedQuestions.Contains(requestId) || view.TurnId is not null ||
                        !view.Prompts.Any(p => p.Id == requestId && p.Kind == CodexRecovery.Kind))
                        throw new IOException("该任务状态已变化，请刷新后再操作。");
                    // Reserve before submission: an uncertain receipt must not replay this action.
                    submittedQuestions.Add(requestId);
                }
                await SendMessageAsync(CodexRecovery.ContinueText, false, ct);
            }
            finally { answerWrites.Release(); }
            return;
        }
        if (requestId.StartsWith("codex-async:", StringComparison.Ordinal))
        {
            await answerWrites.WaitAsync(ct);
            try
            {
                PendingPrompt prompt;
                lock (gate) prompt = !submittedQuestions.Contains(requestId)
                    ? ToView(RequireLiveState()).Prompts.SingleOrDefault(p => p.Id == requestId)
                        ?? throw new IOException("该问题已回答或失效，请刷新。")
                    : throw new IOException("该问题的回答已提交，请等待同步。");
                await SendMessageAsync(CodexQuestions.Reply(prompt, response), true, ct);
                lock (gate) submittedQuestions.Add(requestId);
            }
            finally { answerWrites.Release(); }
            return;
        }
        JsonNode request;
        lock (gate)
            request = state?["requests"]?.AsArray().FirstOrDefault(r => Text(r?["id"]) == requestId)?.DeepClone()
                ?? throw new IOException("该确认已经处理或失效，请刷新状态。");
        string method = Text(request["method"]);
        string follower = method switch
        {
            "item/commandExecution/requestApproval" => "thread-follower-command-approval-decision",
            "item/fileChange/requestApproval" => "thread-follower-file-approval-decision",
            "item/tool/requestUserInput" => "thread-follower-submit-user-input",
            CodexPermissionRequests.Kind => "thread-follower-permissions-request-approval-response",
            _ => throw new IOException("第一版暂不支持此类确认，请在 B 的 Codex 桌面处理。")
        };
        if (method is not ("item/tool/requestUserInput" or CodexPermissionRequests.Kind) && decision is not ("accept" or "decline" or "cancel")) throw new ArgumentException("无效审批决定。");
        var args = new JsonObject { ["conversationId"] = sessionId, ["requestId"] = request["id"]!.DeepClone() };
        if (method == "item/tool/requestUserInput") args["response"] = response?.DeepClone() ?? throw new ArgumentException("需要回答内容。");
        else if (method == CodexPermissionRequests.Kind) args["response"] = CodexPermissionRequests.BuildResponse(request["params"], decision);
        else args["decision"] = decision;
        await RequestAsync(follower, args, 1, ct);
    }
    private JsonNode RequireLiveState()
    {
        if (savedQueue is not null) throw new IOException("请在 Codex 桌面执行此会话操作。");
        if (savedView is { } view) throw new IOException(view.StatusDetail ?? "当前会话仅可查看已保存的历史。");
        return state ?? throw new IOException("请先选择会话。");
    }

    public static SessionView ToView(JsonNode s)
    {
        var turns = s["turnHistory"]?["history"]?["entitiesByKey"] is JsonObject entities
            ? entities.Select(p => p.Value!).OrderBy(t => t["turnStartedAtMs"]?.GetValue<double>() ?? 0).ToArray()
            : s["turns"]?.AsArray().Select(t => t!).ToArray() ?? [];
        var messages = new List<ChatEntry>();
        var ordinaryIds = turns.SelectMany(t => t["items"]?.AsArray() ?? []).Where(i => Text(i?["type"]) == "userMessage")
            .SelectMany(i => new[] { Text(i?["id"]), Text(i?["clientId"]), Text(i?["clientUserMessageId"]) }).Where(id => id.Length > 0).ToHashSet();
        // Keep projected history on the host. Controllers receive bounded pages.
        foreach (var t in turns.Reverse())
        {
            foreach (var item in (t["items"]?.AsArray() ?? []).Reverse())
            {
                string kind = Text(item?["type"]);
                if (kind == "steeringUserMessage" && (Text(item?["status"]) != "accepted"
                    || ordinaryIds.Contains(Text(item?["serverUserMessageId"])) || ordinaryIds.Contains(Text(item?["clientUserMessageId"])))) continue;
                string content = kind switch
                {
                    "userMessage" => CodexQuestions.DisplayReply(string.Join("\n", item?["content"]?.AsArray().Where(c => Text(c?["type"]) == "text").Select(c => Text(c?["text"])) ?? [])),
                    "steeringUserMessage" => CodexQuestions.DisplayReply(string.Join("\n", item?["input"]?.AsArray().Where(c => Text(c?["type"]) == "text").Select(c => Text(c?["text"])) ?? [])),
                    "agentMessage" => Text(item?["text"]),
                    "error" => CodexRecovery.ErrorText(item),
                    "reasoning" => Text(item?["summary"] ?? item?["text"] ?? item?["content"]),
                    "commandExecution" => Text(item?["command"]) + "\n" + Text(item?["aggregatedOutput"]),
                    "mcpToolCall" => Text(item?["server"]) + "/" + Text(item?["tool"]) + " · " + Text(item?["status"]),
                    "fileChange" => "文件修改 · " + Text(item?["status"]) + "\n" + string.Join("\n", item?["changes"]?.AsArray().Select(c => Text(c?["path"])) ?? []),
                    "contextCompaction" => "上下文已压缩，完整对话记录仍可向上翻阅。",
                    _ => ""
                };
                if (!string.IsNullOrWhiteSpace(content))
                {
                    string? detail = kind is "userMessage" or "steeringUserMessage" or "agentMessage" or "contextCompaction" ? null : item?.ToJsonString();
                    if (detail?.Length > 20000) detail = detail[..20000] + "\n[详情截断]";
                    var entry = new ChatEntry(kind is "userMessage" or "steeringUserMessage" ? "你" : kind == "agentMessage" ? "Codex" : kind == "reasoning" ? "推理摘要" : kind is "contextCompaction" or "error" ? "系统" : "工具", content.Length > 20000 ? content[..20000] + "\n[输出截断，请在桌面查看全文]" : content, detail);
                    messages.Add(entry);
                }
            }
        }
        messages.Reverse();
        var prompts = (s["requests"]?.AsArray() ?? []).Select(r => Text(r?["method"]) == CodexPermissionRequests.Kind
            ? CodexPermissionRequests.Pending(Text(r?["id"]), r?["params"])
            : new PendingPrompt(Text(r?["id"]), Text(r?["method"]), Text(r?["method"]),
                r?["params"]?.ToJsonString() ?? "", r?["params"]?.DeepClone())).ToList();
        prompts.AddRange(CodexQuestions.Pending(turns));
        var latest = turns.LastOrDefault();
        var last = Text(latest?["status"]) == "inProgress" ? latest : null;
        string runtime = Text(s["threadRuntimeStatus"]?["type"]);
        var recovery = CodexRecovery.Pending(latest, runtime);
        if (recovery is not null) prompts.Add(recovery);
        string detailText = CodexRecovery.ErrorText(latest?["error"]);
        return new(Text(s["id"]), Text(s["title"]), Text(s["cwd"]), runtime == "systemError" ? "error" : runtime,
            last is null ? null : Text(last["turnId"]), messages, prompts,
            StatusDetail: recovery?.Detail ?? (detailText.Length == 0 ? null : detailText),
            Model: Text(s["latestCollaborationMode"]?["settings"]?["model"] ?? s["latestModel"]), Mode: Text(s["latestCollaborationMode"]?["mode"]));
    }
    internal static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var text) ? text : n?.ToJsonString() ?? "";
    public async ValueTask DisposeAsync()
    {
        if (pipe.IsConnected && sessionId is not null)
            try { using var timeout = new CancellationTokenSource(1000); await FollowAsync(false, timeout.Token); } catch { }
        lifetime.Cancel(); pipe.Dispose();
        if (reader is not null) await reader;
        var queue = savedQueue;
        savedQueue = null;
        if (queue is not null) await queue.DisposeAsync();
    }
}

public static class StatePatches
{
    public static void Apply(JsonNode root, JsonArray patches)
    {
        foreach (var p in patches)
        {
            var path = p!["path"]!.AsArray();
            if (path.Count == 0) throw new IOException("不支持替换状态根节点。");
            JsonNode parent = root;
            for (int i = 0; i < path.Count - 1; i++) parent = Child(parent, path[i]!) ?? throw new IOException("补丁路径不存在。");
            string op = p["op"]!.GetValue<string>();
            if (op is not ("add" or "replace" or "remove")) throw new IOException("未知补丁操作。");
            var value = p["value"]?.DeepClone();
            if (parent is JsonArray array)
            {
                if (path[^1]!.ToString() == "length")
                {
                    int count = value!.GetValue<int>();
                    if (count < 0 || count > array.Count) throw new IOException("无效数组长度。");
                    while (array.Count > count) array.RemoveAt(array.Count - 1);
                }
                else
                {
                    int index = path[^1]!.GetValue<int>();
                    if (op == "remove") array.RemoveAt(index);
                    else if (op == "add") array.Insert(index, value);
                    else array[index] = value;
                }
            }
            else if (parent is JsonObject obj)
            {
                string key = path[^1]!.GetValue<string>();
                if (op == "remove") obj.Remove(key); else obj[key] = value;
            }
            else throw new IOException("补丁父节点无效。");
        }
    }
    private static JsonNode? Child(JsonNode node, JsonNode key) => node is JsonArray a ? a[key.GetValue<int>()] : node[key.GetValue<string>()];
}
