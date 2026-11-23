using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexQueueChecks
{
    private const string ThreadId = "018924dc-4bac-754f-ad02-33ff2d87f128";
    private const string MessageId = "018924dc-4bac-754f-ad02-33ff2d87f129";
    private const string QueuedId = "018924dc-4bac-754f-ad02-33ff2d87f130";
    private const string Message = "继续原会话。\n请保留这段换行。";

    internal static async Task Run(Action<bool, string> check)
    {
        var bridge = new Metadata(method => method switch
        {
            "thread/read" => SavedThread(),
            "thread/turns/list" => TurnPage(SavedTurn()),
            "thread/queue/list" => Json.Node(new { data = new[] { QueuedSubmission(MessageId) } }),
            "thread/queue/add" => Json.Node(new { queuedSubmission = QueuedSubmission(MessageId) }),
            _ => throw new InvalidOperationException("Unexpected Codex method: " + method)
        });
        var initial = await CodexQueuedSession.ReadInitialAsync(bridge, ThreadId, default);
        var attached = initial.View;
        check(attached.Id == ThreadId && attached.Title == "Saved Codex thread" &&
            attached.Messages.Single().Text == "原会话中的回复" && attached.CanSend,
            "Codex queue support enables text input while preserving the selected thread and history");
        check(attached.Status == "queued" && attached.PendingMessages == 1 && attached.CanStop == false && attached.TurnId is null &&
            attached.Prompts.Count == 0,
            "Codex queue view reports pending input without a live turn or approval controls");
        check(bridge.Calls.Count == 3 &&
            bridge.Calls.Select(call => call.Method).ToHashSet().SetEquals(["thread/read", "thread/turns/list", "thread/queue/list"]) &&
            bridge.Calls[0].Parameters["includeTurns"]?.GetValue<bool>() == false &&
            bridge.Calls[1].Parameters["limit"]?.GetValue<int>() == CodexQueuedHistory.PageSize &&
            bridge.Calls.All(call => call.Parameters["threadId"]?.ToString() == ThreadId),
            "Codex queue attachment reads a bounded first page for the selected thread without resuming it");

        bridge.Calls.Clear();
        await CodexQueuedSession.EnqueueAsync(bridge, ThreadId, Message, MessageId, default);
        var request = bridge.Calls.Single();
        check(request.Method == "thread/queue/add" && request.Parameters["threadId"]?.ToString() == ThreadId &&
            request.Parameters["clientUserMessageId"]?.ToString() == MessageId &&
            request.Parameters["input"] is JsonArray { Count: 1 } input &&
            input[0]?["type"]?.ToString() == "text" && input[0]?["text"]?.ToString() == Message,
            "Codex submission queues exact text and unique ID in the selected thread exactly once");

        var older = new Metadata(method => method switch
        {
            "thread/read" => SavedThread(),
            "thread/queue/list" or "thread/turns/list" => throw new CodexAppServerRequestException(method, "Method not found", -32601),
            _ => throw new InvalidOperationException("Unexpected Codex method: " + method)
        });
        var readOnly = await CodexQueuedSession.ReadAsync(older, ThreadId, default);
        check(readOnly.Id == ThreadId && readOnly.Status == "readOnly" && !readOnly.CanSend &&
            readOnly.CanStop == false && readOnly.Messages.Single().Text == "原会话中的回复" &&
            older.Calls.Any(call => call.Method == "thread/read" && call.Parameters["includeTurns"]?.GetValue<bool>() == true),
            "legacy Codex without native paging and queue falls back to the original full read-only history");

        var wrongReceipt = new Metadata(method => method == "thread/queue/add"
            ? Json.Node(new { queuedSubmission = QueuedSubmission(Guid.NewGuid().ToString("D")) })
            : throw new InvalidOperationException("Unexpected Codex method: " + method));
        bool rejected = false;
        try { await CodexQueuedSession.EnqueueAsync(wrongReceipt, ThreadId, Message, MessageId, default); }
        catch (IOException) { rejected = true; }
        check(rejected && wrongReceipt.Calls.Count == 1,
            "a mismatched Codex queue receipt fails without issuing another add");

        await CheckCachedAttachAsync(initial, check);
        await CheckSendDuringRefreshAsync(initial, check);
        await CheckSavedStateAsync(check);
        await CheckOlderExpansionAsync(check);
        await CheckWatchedTurnAfterLeaveAsync(initial, check);
    }

    private static async Task CheckCachedAttachAsync(CodexQueuedSession.Initial initial, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extraOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0;
        initial = initial with { View = initial.View with { Status = "idle", PendingMessages = 0 } };
        const string refreshedReply = "刷新后的回复";
        await using var session = new CodexQueuedSession(initial, async ct =>
        {
            if (Interlocked.Increment(ref opens) > 1) extraOpen.TrySetResult();
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new Metadata(method => method switch
            {
                "thread/read" => SavedThread(),
                "thread/turns/list" => TurnPage(SavedTurn(refreshedReply)),
                "thread/queue/list" => Json.Node(new { data = Array.Empty<object>() }),
                _ => throw new InvalidOperationException("Unexpected Codex method: " + method)
            });
        }, view => refreshed.TrySetResult(view));

        var cached = await Task.Run(() => session.AttachCached(deadline.Token)).WaitAsync(deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        check(cached.Id == ThreadId && cached.Messages.Single().Text == "原会话中的回复" && cached.CanSend &&
            !refreshed.Task.IsCompleted,
            "reopening Codex immediately returns the cached history and keeps sending enabled during blocked metadata refresh");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => session.AttachCached(deadline.Token))))
            .WaitAsync(deadline.Token);

        release.SetResult();
        var latest = await refreshed.Task.WaitAsync(deadline.Token);
        check(latest.Id == ThreadId && latest.Messages.Single().Text == refreshedReply && latest.CanSend,
            "Codex publishes refreshed history and send capability for the same thread");
        await Task.WhenAny(extraOpen.Task, Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token));
        check(Volatile.Read(ref opens) == 1, "repeated Codex attachments share a single outstanding metadata refresh");
    }

    private static async Task CheckSendDuringRefreshAsync(CodexQueuedSession.Initial initial, Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<SessionCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0, notices = 0, sends = 0;
        string? submissionId = null;
        await using var session = new CodexQueuedSession(initial, async ct =>
        {
            int index = Interlocked.Increment(ref opens);
            if (index == 1)
            {
                reading.TrySetResult();
                await allowRead.Task.WaitAsync(ct);
            }
            if (index == 2)
                return new Metadata((method, args) =>
                {
                    if (method != "thread/queue/add") throw new InvalidOperationException(method);
                    Interlocked.Increment(ref sends);
                    submissionId = args["clientUserMessageId"]!.GetValue<string>();
                    return Json.Node(new { queuedSubmission = QueuedSubmission(submissionId) });
                });
            return new Metadata(method => method switch
            {
                "thread/read" => SavedThread(),
                "thread/turns/list" => index == 1 ? TurnPage(SavedTurn("过时的读取")) :
                    TurnPage(SavedTurn("快速完成", "fast-turn", submissionId), SavedTurn()),
                "thread/queue/list" when index == 3 => throw new CodexAppServerRequestException(method, "queue busy", -32000),
                "thread/queue/list" => Json.Node(new { data = Array.Empty<object>() }),
                _ => throw new InvalidOperationException(method)
            });
        }, _ => { }, notice => { Interlocked.Increment(ref notices); completed.TrySetResult(notice); }, _ => { });

        session.AttachCached(timeout.Token);
        await reading.Task.WaitAsync(timeout.Token);
        await session.SendAsync(Message, timeout.Token).WaitAsync(timeout.Token);
        check(!allowRead.Task.IsCompleted && Volatile.Read(ref sends) == 1 && session.View.Status == "queued",
            "native input can be acknowledged while an independent history refresh remains blocked");

        allowRead.SetResult();
        var notice = await completed.Task.WaitAsync(timeout.Token);
        check(notice.TurnId == "fast-turn" && session.View.CanSend && session.View.Messages.Last().Text == "快速完成",
            "a fast terminal turn publishes its persisted completion despite a transient queue error and no active sample");
        session.AttachCached(timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(650), timeout.Token);
        check(Volatile.Read(ref notices) == 1 && session.View.Messages.Any(item => item.Text == "原会话中的回复"),
            "reopening neither duplicates a completed-turn notice nor drops resident older history");
    }

    private static async Task CheckSavedStateAsync(Action<bool, string> check)
    {
        var metadata = SavedThread();
        metadata["thread"]!["turns"]![0]!["status"] = "interrupted";
        var bridge = new Metadata(method => method switch
        {
            "thread/read" => (JsonObject)metadata.DeepClone(),
            "thread/turns/list" => TurnPage(metadata["thread"]!["turns"]![0]!.DeepClone()),
            "thread/queue/list" => Json.Node(new { data = Array.Empty<object>() }),
            _ => throw new InvalidOperationException(method)
        });
        var view = await CodexQueuedSession.ReadAsync(bridge, ThreadId, default);
        check(view.Status == "codexStatePending" && view.CanSend && view.CanStop == false,
            "storage-only interruption without a terminal timestamp does not claim Codex has stopped");
        var turn = metadata["thread"]!["turns"]![0]!;
        turn["status"] = "failed";
        turn["error"] = Json.Node(new { codexErrorInfo = "rateLimitExceeded", message = "fixture rate limited" });
        view = await CodexQueuedSession.ReadAsync(bridge, ThreadId, default);
        check(view.Status == "error" && view.Prompts.Single().Id == "codex-recovery:old-turn",
            "modern App Server turn IDs and rate-limit codes restore the manual continue action");
    }

    private static async Task CheckOlderExpansionAsync(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        int notifications = 0;
        var newest = SavedTurn("最新正文", "latest");
        var requests = new Metadata((method, args) => method switch
        {
            "thread/read" => SavedThread(),
            "thread/turns/list" when args["cursor"]?.GetValue<string>() == "older-cursor" => TurnPage(
                Enumerable.Range(1, 10).Reverse().Select(i => SavedTurn($"较早正文 {i}", $"older-{i}")).ToArray()),
            "thread/turns/list" => new JsonObject
            {
                ["data"] = new JsonArray(newest.DeepClone()), ["nextCursor"] = "older-cursor"
            },
            "thread/queue/list" => Json.Node(new { data = Array.Empty<object>() }),
            _ => throw new InvalidOperationException(method)
        });
        var seed = await CodexQueuedSession.ReadInitialAsync(requests, ThreadId, timeout.Token);
        check(seed.View.Messages.Single().Text == "最新正文" && seed.History.HasOlder &&
            requests.Calls.All(call => call.Parameters["cursor"] is null),
            "cold Codex attach returns newest history without fetching any older native page");

        var expanded = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new CodexQueuedSession(seed, _ => Task.FromResult<ICodexMetadata>(requests),
            value => expanded.TrySetResult(value), _ => Interlocked.Increment(ref notifications));
        var history = await expanded.Task.WaitAsync(timeout.Token);
        var latest = HistoryPaging.Latest(history);
        var earlier = HistoryPaging.Page(history, latest.History!.Start, latest.History.Revision);
        check(history.Messages.Count == 11 && history.Messages[0].Text == "较早正文 1" &&
            earlier.Total == 11 && earlier.End == latest.History.Start &&
            earlier.Messages[0].Text == "较早正文 1" && Volatile.Read(ref notifications) == 0,
            "background expansion restores the controller's exact older page without notifying historical completed turns");
    }

    private static async Task CheckWatchedTurnAfterLeaveAsync(CodexQueuedSession.Initial initial, Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var detected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<SessionCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        int observations = 0;
        initial = initial with { View = initial.View with { Status = "idle", PendingMessages = 0 } };
        await using var session = new CodexQueuedSession(initial, _ =>
        {
            int index = Interlocked.Increment(ref observations);
            var external = SavedTurn("B 侧回复", "b-turn");
            external["status"] = index == 1 ? "interrupted" : "completed";
            return Task.FromResult<ICodexMetadata>(new Metadata(method => method switch
            {
                "thread/read" => SavedThread(),
                "thread/turns/list" => TurnPage(external, SavedTurn()),
                "thread/queue/list" => Json.Node(new { data = Array.Empty<object>() }),
                _ => throw new InvalidOperationException(method)
            }));
        }, snapshot =>
        {
            if (snapshot.Status == "codexStatePending") detected.TrySetResult();
        }, notice => completed.TrySetResult(notice));

        session.SetObserved(true);
        session.AttachCached(timeout.Token);
        await detected.Task.WaitAsync(timeout.Token);
        session.SetObserved(false);
        var notice = await completed.Task.WaitAsync(timeout.Token);
        check(notice.TurnId == "b-turn" && session.View.Status == "idle",
            "a detected turn begun directly in Codex continues monitoring after the last viewer leaves");
    }

    private static JsonNode TurnPage(params JsonNode[] turns) => new JsonObject
    { ["data"] = new JsonArray(turns), ["nextCursor"] = null };

    private static JsonNode SavedThread(string reply = "原会话中的回复") => Json.Node(new
    {
        thread = new
        {
            id = ThreadId, name = "Saved Codex thread", cwd = "E:/fixture", status = new { type = "notLoaded" },
            turns = new[] { SavedTurn(reply) }
        }
    });

    private static JsonNode SavedTurn(string reply = "原会话中的回复", string id = "old-turn", string? submitted = null)
    {
        var items = new JsonArray();
        if (submitted is not null) items.Add(Json.Node(new
        {
            type = "userMessage", id = "input", clientId = submitted, content = new[] { new { type = "text", text = Message } }
        }));
        items.Add(Json.Node(new { type = "agentMessage", id = "answer", text = reply }));
        return new JsonObject { ["id"] = id, ["status"] = "completed", ["items"] = items };
    }

    private static object QueuedSubmission(string clientUserMessageId) => new
    {
        id = QueuedId, clientUserMessageId,
        input = new[] { new { type = "text", text = Message, textElements = Array.Empty<object>() } }
    };

    private sealed class Metadata(Func<string, JsonNode, JsonNode> result) : ICodexMetadata
    {
        public Metadata(Func<string, JsonNode> result) : this((method, _) => result(method)) { }
        public List<(string Method, JsonNode Parameters)> Calls { get; } = [];

        public Task<JsonNode> CallAsync(string method, object args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var parameters = Json.Node(args);
            Calls.Add((method, parameters));
            return Task.FromResult(result(method, parameters));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
