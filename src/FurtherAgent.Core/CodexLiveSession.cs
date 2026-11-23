using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// A subscription to the same App Server as Codex Desktop. All mutations are
// addressed to that server's single owner and to the exact selected thread.
internal sealed class CodexLiveSession : IAsyncDisposable
{
    private readonly CodexLiveConnection connection = new();
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly string id;
    private readonly string threadId;
    private readonly Dictionary<string, (JsonNode Id, PendingPrompt Prompt)> requests = [];
    private readonly List<(string Method, JsonNode Args, JsonNode? Request)> early = [];
    private readonly HashSet<string> completed = [];
    private JsonObject? thread;
    private JsonObject settings = [];
    private Task? earlierHistory;
    private int deltaPending;

    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted;
    public event Action<SessionCompletion>? Completed;

    public CodexLiveSession(string id)
    {
        this.id = id;
        threadId = Guid.Parse(id).ToString("D");
        connection.Received += Receive;
        connection.Faulted += error => Faulted?.Invoke(error);
    }

    public async Task<SessionView> AttachAsync(string endpoint, CancellationToken ct)
    {
        lock (gate)
        {
            if (thread is not null) return View();
        }
        await connection.OpenAsync(endpoint, ct);
        var reply = await connection.CallAsync("thread/resume", new { threadId, excludeTurns = true,
            initialTurnsPage = new { limit = 16, sortDirection = "desc", itemsView = "full" } }, ct);
        if (reply["thread"] is not JsonObject snapshot || Text(snapshot["id"]) != threadId)
            throw new IOException("共享 Codex App Server 返回了不同的会话。");
        var page = reply["initialTurnsPage"];
        if (page?["data"] is JsonArray recent)
            snapshot["turns"] = new JsonArray(recent.Reverse().Select(turn => turn?.DeepClone()).ToArray());
        else snapshot = await CodexSavedSession.ReadThreadAsync(connection, id, ct);
        lock (gate)
        {
            thread = (JsonObject)snapshot.DeepClone();
            thread["turns"] ??= new JsonArray();
            settings = (JsonObject)reply.DeepClone();
            settings.Remove("thread");
            foreach (var update in early) Apply(update.Method, update.Args, update.Request, bootstrap: true);
            early.Clear();
            if (Text(page?["nextCursor"]) is { Length: > 0 } cursor)
                earlierHistory = ReadEarlierAsync(cursor, lifetime.Token);
            return View();
        }
    }

    private async Task ReadEarlierAsync(string cursor, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            var turns = new List<JsonNode>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            while (cursor.Length > 0 && cursors.Add(cursor))
            {
                var page = await connection.CallAsync("thread/turns/list",
                    new { threadId, cursor, limit = 64, sortDirection = "desc", itemsView = "full" }, ct);
                if (page["data"] is not JsonArray entries) throw new IOException("Codex 返回了无效的历史页。");
                turns.AddRange(entries.OfType<JsonObject>());
                cursor = Text(page["nextCursor"]);
            }
            lock (gate)
            {
                int index = 0;
                foreach (var turn in turns.AsEnumerable().Reverse())
                    if (FindTurn(Text(turn["id"])) is null) Turns.Insert(index++, turn.DeepClone());
                if (index > 0) Changed?.Invoke(View());
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            lock (gate)
            {
                if (!ct.IsCancellationRequested)
                    Changed?.Invoke(View() with { StatusDetail = "较早历史暂时无法读取，请重新连接后再试。" });
            }
        }
    }

    private static string Text(JsonNode? node) => CodexDesktopAdapter.Text(node);
    private JsonArray Turns => thread!["turns"]!.AsArray();
    private JsonNode? FindTurn(string turnId) => Turns.LastOrDefault(t => Text(t?["id"]) == turnId);
    private JsonNode? ActiveTurn => Turns.LastOrDefault(t => Text(t?["status"]) == "inProgress");
    private string ActiveTurnId => Text(ActiveTurn?["id"]);

    private SessionView View()
    {
        if (thread is null) throw new IOException("请先打开 Codex 会话。");
        var projected = CodexDesktopAdapter.ToView(thread);
        var latest = Turns.LastOrDefault();
        string runtime = Text(thread["status"]?["type"]);
        string status = ActiveTurn is not null || runtime == "active" ? "active"
            : runtime == "systemError" || Text(latest?["status"]) == "failed" ? "error" : "idle";
        string title = Text(thread["name"]);
        if (title.Length == 0) title = Text(thread["preview"]);
        var prompts = projected.Prompts.Concat(requests.Values.Select(value => value.Prompt)).DistinctBy(p => p.Id).ToList();
        var recovery = CodexRecovery.Pending(latest, status);
        if (recovery is not null && prompts.All(p => p.Id != recovery.Id)) prompts.Add(recovery);
        string detail = CodexRecovery.ErrorText(latest?["error"]);
        return projected with
        {
            Id = id, Title = title.Length == 0 ? "Codex 会话" : title, Status = status,
            Model = Text(settings["model"] ?? thread["model"]), Mode = Text(settings["collaborationMode"]?["mode"]),
            TurnId = ActiveTurnId is { Length: > 0 } activeId ? activeId : null,
            CanSend = (status != "active" || ActiveTurnId.Length > 0) && thread["canAcceptDirectInput"]?.GetValue<bool>() != false,
            CanStop = ActiveTurnId.Length > 0, CanSteer = true, CanConfigure = true,
            Prompts = prompts, StatusDetail = detail.Length == 0 ? null : detail
        };
    }

    private void Receive(string method, JsonNode? args, JsonNode? request)
    {
        if (args is not JsonObject || Text(args["threadId"]) != threadId) return;
        lock (gate)
        {
            if (thread is null)
            {
                early.Add((method, args, request));
                return;
            }
            if (!Apply(method, args, request, bootstrap: false)) return;
            if (method.EndsWith("/delta", StringComparison.Ordinal))
            {
                if (Interlocked.Exchange(ref deltaPending, 1) == 0) _ = PublishDeltaAsync();
            }
            else Changed?.Invoke(View());
        }
    }

    private async Task PublishDeltaAsync()
    {
        try
        {
            await Task.Delay(120, lifetime.Token);
            lock (gate)
            {
                Interlocked.Exchange(ref deltaPending, 0);
                if (!lifetime.IsCancellationRequested) Changed?.Invoke(View());
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private bool Apply(string method, JsonNode args, JsonNode? request, bool bootstrap)
    {
        if (request is not null)
        {
            string key = RequestKey(request);
            var prompt = method == CodexPermissionRequests.Kind ? CodexPermissionRequests.Pending(key, args)
                : new PendingPrompt(key, method, method, args.ToJsonString(), args.DeepClone());
            requests[key] = (request.DeepClone(), prompt);
            return true;
        }

        switch (method)
        {
            case "serverRequest/resolved":
                return args["requestId"] is { } resolved && requests.Remove(RequestKey(resolved));
            case "thread/status/changed":
                thread!["status"] = args["status"]?.DeepClone();
                return true;
            case "thread/name/updated":
                thread!["name"] = args["threadName"]?.DeepClone();
                return true;
            case "thread/settings/updated":
                if (args["threadSettings"] is not JsonObject values) return false;
                foreach (var (key, value) in values) settings[key] = value?.DeepClone();
                return true;
            case "turn/started":
            case "turn/completed":
                if (args["turn"] is not JsonObject nativeTurn) return false;
                string turnId = Text(nativeTurn["id"]);
                if (turnId.Length == 0) return false;
                var previous = FindTurn(turnId);
                if (method == "turn/started" && previous is not null) return false;
                var turn = (JsonObject)nativeTurn.DeepClone();
                if (previous?["items"] is JsonArray existing && turn["items"] is not JsonArray { Count: > 0 })
                    turn["items"] = existing.DeepClone();
                turn["items"] ??= new JsonArray();
                if (previous is null) Turns.Add(turn);
                else Turns[Turns.IndexOf(previous)] = turn;
                thread!["status"] = new JsonObject { ["type"] = ActiveTurn is null ? "idle" : "active" };
                if (method == "turn/completed")
                {
                    foreach (string key in requests.Where(p => Text(p.Value.Prompt.Payload?["turnId"]) == turnId).Select(p => p.Key).ToArray())
                        requests.Remove(key);
                    if (!bootstrap && Text(turn["status"]) == "completed" && completed.Add(turnId))
                    {
                        string title = Text(thread!["name"] ?? thread["preview"]);
                        Completed?.Invoke(new SessionCompletion(Guid.NewGuid().ToString("N"), id, title, turnId));
                    }
                }
                return true;
            case "item/started":
            case "item/completed":
                if (args["item"] is not JsonObject item || FindTurn(Text(args["turnId"]))?["items"] is not JsonArray items) return false;
                var existingItem = items.FirstOrDefault(i => Text(i?["id"]) == Text(item["id"]));
                if (existingItem is null) items.Add(item.DeepClone());
                else if (method == "item/completed") items[items.IndexOf(existingItem)] = item.DeepClone();
                return true;
            case "item/agentMessage/delta":
                return !bootstrap && ApplyDelta(args, "agentMessage", "text");
            case "item/commandExecution/outputDelta":
                return !bootstrap && ApplyDelta(args, "commandExecution", "aggregatedOutput");
            case "error":
                if (FindTurn(Text(args["turnId"])) is not JsonObject failed) return false;
                failed["error"] = args["error"]?.DeepClone();
                return true;
            default:
                return false;
        }
    }

    private bool ApplyDelta(JsonNode args, string type, string field)
    {
        if (FindTurn(Text(args["turnId"])) is not JsonObject turn || Text(turn["status"]) != "inProgress" ||
            turn["items"] is not JsonArray items) return false;
        string itemId = Text(args["itemId"]);
        if (itemId.Length == 0) return false;
        var item = items.OfType<JsonObject>().FirstOrDefault(i => Text(i["id"]) == itemId);
        if (item is null)
        {
            item = new JsonObject { ["id"] = itemId, ["type"] = type };
            items.Add(item);
        }
        item[field] = Text(item[field]) + Text(args["delta"]);
        return true;
    }

    public async Task SendAsync(string text, CancellationToken ct, bool questionReply = false)
    {
        SessionView view;
        lock (gate) view = View();
        if (!view.CanSend) throw new IOException("当前 Codex 会话暂不可发送，请等待状态同步。");
        string messageId = Guid.NewGuid().ToString("D");
        var input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } };
        string method = view.TurnId is null ? "turn/start" : "turn/steer";
        object args = view.TurnId is null
            ? new { threadId, input, clientUserMessageId = messageId,
                turnTrigger = questionReply ? "send_user_message_async_question" : "composer" }
            : new { threadId, input, clientUserMessageId = messageId, expectedTurnId = view.TurnId };
        try
        {
            await CodexSubmissionReceipt.WaitAsync(connection.CallAsync(method, args, ct),
                () => { lock (gate) return CodexSubmissionReceipt.Contains(thread, messageId); }, ct);
        }
        catch (IOException ex) when (ex is not CodexAppServerRequestException and not SubmissionUncertainException && !ct.IsCancellationRequested)
        {
            throw new SubmissionUncertainException("Codex 发送回执未确认。请先核对原会话，避免重复提交。", ex);
        }
    }

    public Task InterruptAsync(CancellationToken ct)
    {
        string turnId;
        lock (gate) turnId = ActiveTurnId;
        if (turnId.Length == 0) throw new IOException("没有可停止的活动任务。");
        return connection.CallAsync("turn/interrupt", new { threadId, turnId }, ct);
    }

    public async Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct)
    {
        (JsonNode Id, PendingPrompt Prompt) request;
        lock (gate)
        {
            if (!requests.TryGetValue(requestId, out request))
                request = (null!, View().Prompts.SingleOrDefault(p => p.Id == requestId)
                    ?? throw new IOException("此请求已处理或失效，请刷新。"));
        }

        if (request.Prompt.Kind == CodexRecovery.Kind)
        {
            if (decision != "continue") throw new ArgumentException("无效的恢复操作。");
            await SendAsync(CodexRecovery.ContinueText, ct);
        }
        else if (request.Prompt.Kind == CodexQuestions.AsyncKind)
            await SendAsync(CodexQuestions.Reply(request.Prompt, response), ct, questionReply: true);
        else
        {
            var result = request.Prompt.Kind switch
            {
                CodexPermissionRequests.Kind => CodexPermissionRequests.BuildResponse(request.Prompt.Payload, decision),
                "item/tool/requestUserInput" or "tool/requestUserInput" =>
                    response?["answers"] is JsonObject ? response : throw new ArgumentException("请回答待处理的问题。"),
                "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" when
                    decision is "accept" or "acceptForSession" or "decline" or "cancel" => Json.Node(new { decision }),
                _ => throw new IOException("请在 Codex 桌面处理此请求。")
            };
            await connection.RespondAsync(request.Id, result, ct);
        }
    }

    public async Task<AgentSettings> SettingsAsync(CancellationToken ct)
    {
        JsonObject current;
        lock (gate) current = (JsonObject)settings.DeepClone();
        string model = Text(current["model"]), provider = Text(current["modelProvider"]);
        string mode = Text(current["collaborationMode"]?["mode"]) is "plan" ? "plan" : "default";
        string effort = Text(current["effort"] ?? current["reasoningEffort"] ?? current["collaborationMode"]?["settings"]?["reasoning_effort"]);
        var models = await CodexSettingsCatalog.ModelsAsync(connection, provider, ct);
        string? usage;
        try { usage = CodexSettingsCatalog.Usage(await connection.CallAsync("account/rateLimits/read", new { }, ct)); }
        catch (CodexAppServerRequestException) { usage = "套餐限额暂不可读。"; }
        return new AgentSettings(model, provider, mode, models, models.Count > 0, true,
            "修改将应用于原 Codex 会话的后续轮次。", effort, models.Any(m => m.ReasoningEfforts?.Count > 0), usage,
            true, CodexPermissions.Current(Json.Node(new { latestThreadSettings = current })), true);
    }

    public async Task ConfigureAsync(string model, string provider, string mode, string? effort, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model) || mode is not ("default" or "plan")) throw new ArgumentException("模型或模式无效。");
        string previous;
        lock (gate)
        {
            RequireIdle();
            previous = Text(settings["effort"] ?? settings["reasoningEffort"]);
        }
        var models = await CodexSettingsCatalog.ModelsAsync(connection, provider, ct);
        var selected = models.FirstOrDefault(m => m.Id == model.Trim())
            ?? throw new IOException("该模型不在当前 Codex 模型列表。");
        string? reasoning = CodexSettingsCatalog.SelectEffort(selected, effort, previous);
        var collaboration = new { mode, settings = new { model = selected.Id, reasoning_effort = reasoning, developer_instructions = (string?)null } };
        lock (gate) RequireIdle();
        await connection.CallAsync("thread/settings/update", new { threadId, model = selected.Id, effort = reasoning, collaborationMode = collaboration }, ct);
    }

    public Task ConfigurePermissionsAsync(string permission, CancellationToken ct)
    {
        lock (gate) RequireIdle();
        var settings = CodexPermissions.Build(permission);
        return connection.CallAsync("thread/settings/update", new { threadId, permissions = permission,
            approvalPolicy = settings["approvalPolicy"] }, ct);
    }

    private void RequireIdle()
    {
        if (View().Status == "active") throw new IOException("请等待当前任务结束后修改会话设置。");
    }

    private static string RequestKey(JsonNode request) => "codex-live:" + request.ToJsonString();

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (earlierHistory is not null) await earlierHistory;
        await connection.DisposeAsync();
        lifetime.Dispose();
    }
}
