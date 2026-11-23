using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// The desktop keeps ownership of the live thread. This client only reads stored
// state and hands input to Codex's native persistent queue.
internal sealed class CodexQueuedSession : IAsyncDisposable
{
    private readonly string id;
    private readonly Func<CancellationToken, Task<ICodexMetadata>> open;
    private readonly Action<SessionView> changed;
    private readonly Action<SessionCompletion>? completed;
    private readonly Action<string> wakeThread;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sends = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task watcher;
    private volatile bool observed;
    private volatile SessionView view;
    private CodexQueuedHistory history;
    private bool? queueSupported;
    private readonly HashSet<string> pendingSubmissions = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedTurns = new(StringComparer.Ordinal);
    private readonly HashSet<string> observedActiveTurns = new(StringComparer.Ordinal);
    private bool wakeFailed;
    private int sendVersion;
    private int refreshRequested;

    public SessionView View => view;

    internal sealed record Initial(SessionView View, CodexQueuedHistory History, bool? QueueSupported);

    public CodexQueuedSession(Initial initial, Func<CancellationToken, Task<ICodexMetadata>> open, Action<SessionView> changed,
        Action<SessionCompletion>? completed = null, Action<string>? wakeThread = null)
    {
        id = initial.View.Id;
        view = initial.View;
        history = initial.History;
        queueSupported = initial.QueueSupported;
        foreach (var turn in history.Turns.Where(t => TurnState(t) == "completed"))
            if (CodexQueuedHistory.TurnId(turn) is { Length: > 0 } turnId) completedTurns.Add(turnId);
        this.open = open;
        this.changed = changed;
        this.completed = completed;
        this.wakeThread = wakeThread ?? CodexThreadActivation.Open;
        refreshRequested = history.HasOlder ? 1 : 0;
        watcher = WatchAsync(lifetime.Token);
    }

    internal static async Task<SessionView> ReadAsync(ICodexMetadata server, string id, CancellationToken ct)
        => (await ReadInitialAsync(server, id, ct)).View;

    internal static async Task<Initial> ReadInitialAsync(ICodexMetadata server, string id, CancellationToken ct)
        => await ReadSnapshotAsync(server, id, await CodexQueuedHistory.OpenAsync(server, id, ct), null, ct);

    private static async Task<Initial> ReadSnapshotAsync(ICodexMetadata server, string id, CodexQueuedHistory history,
        SessionView? previous, CancellationToken ct)
    {
        var thread = history.Thread;
        var saved = CodexSavedSession.ToView(thread) with
        {
            Id = id,
            CanConfigure = true,
            CanSteer = false,
            Model = CodexDesktopAdapter.Text(thread["model"]),
            Mode = CodexDesktopAdapter.Text(thread["collaborationMode"]?["mode"] ?? thread["mode"])
        };
        (JsonArray Items, bool HasMore) queued;
        try { queued = await ReadQueueAsync(server, id, 1, ct); }
        catch (CodexAppServerRequestException ex) when (ex.MethodNotFound)
        {
            return new(saved with { StatusDetail = "当前 Codex 运行时不提供原生消息队列。请在 Codex 桌面继续会话。" }, history, false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (IOException or OperationCanceledException or JsonException))
        {
            return new(saved with
            {
                Status = previous?.Status ?? "codexStatePending",
                StatusDetail = "暂时无法读取 Codex 消息队列，正在重试。",
                TurnId = previous?.TurnId,
                CanSend = previous?.CanSend == true,
                PendingMessages = previous?.PendingMessages,
                Prompts = previous?.Prompts ?? []
            }, history, null);
        }

        var latest = history.Turns.LastOrDefault();
        string turnStatus = TurnState(latest);
        bool active = CodexDesktopAdapter.Text(thread["status"]?["type"]) == "active";
        bool hasQueuedInput = queued.Items.Count > 0;
        string status = active ? "active" : hasQueuedInput ? "queued" : turnStatus switch
        {
            "failed" => "error",
            "interrupted" when HasTerminalTime(latest) => "interrupted",
            "inProgress" or "interrupted" => "codexStatePending",
            _ => "idle"
        };
        string detail = hasQueuedInput
            ? turnStatus == "interrupted" && HasTerminalTime(latest) ? "消息已进入 Codex 队列。若队列因上一次停止而暂停，可在 Codex 桌面继续。"
                : "有消息在 Codex 队列中等待处理。"
            : status == "codexStatePending" ? "正在核对原会话的执行结果。"
                : CodexRecovery.ErrorText(latest?["error"]);
        if (string.IsNullOrEmpty(detail)) detail = "支持向原会话发送消息。停止和审批请在 Codex 桌面操作。";
        var recovery = active || hasQueuedInput ? null : CodexRecovery.Pending(latest, status);
        return new(saved with
        {
            Status = status,
            TurnId = active ? CodexQueuedHistory.TurnId(latest) : null,
            StatusDetail = detail,
            CanSend = true,
            PendingMessages = queued.HasMore ? null : queued.Items.Count,
            Prompts = recovery is null ? [] : [recovery]
        }, history, true);
    }

    private static string TurnState(JsonNode? turn) => CodexDesktopAdapter.Text(turn?["status"]);
    private static bool HasTerminalTime(JsonNode? turn) => turn?["completedAt"] is not null;

    internal static async Task EnqueueAsync(ICodexMetadata server, string id, string text, string messageId, CancellationToken ct)
    {
        var result = await server.CallAsync("thread/queue/add", new
        {
            threadId = Guid.Parse(id).ToString("D"),
            clientUserMessageId = messageId,
            input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } }
        }, ct);
        var receipt = result["queuedSubmission"];
        if (CodexDesktopAdapter.Text(receipt?["clientUserMessageId"]) != messageId ||
            string.IsNullOrWhiteSpace(CodexDesktopAdapter.Text(receipt?["id"])))
            throw new IOException("Codex 返回的队列回执与本次消息不一致。");
    }

    private static async Task<(JsonArray Items, bool HasMore)> ReadQueueAsync(ICodexMetadata server, string id, int limit, CancellationToken ct)
    {
        var result = await server.CallAsync("thread/queue/list", new { threadId = Guid.Parse(id).ToString("D"), limit }, ct);
        if (result["data"] is not JsonArray items) throw new IOException("Codex 返回了无效的消息队列。");
        return (items, result["nextCursor"] is not null);
    }

    public void SetObserved(bool value)
    {
        observed = value;
        Signal();
    }

    public SessionView AttachCached(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref refreshRequested, 1) == 0) Signal();
        return view;
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        await sends.WaitAsync(ct);
        try
        {
            if (!view.CanSend) throw new IOException("Codex 消息队列暂不可用，请刷新会话。");
            string messageId = Guid.NewGuid().ToString("D");
            bool attempted = false;
            try
            {
                await using var server = await open(ct);
                lock (gate)
                {
                    pendingSubmissions.Add(messageId);
                    sendVersion++;
                }
                attempted = true;
                Signal();
                await EnqueueAsync(server, id, text, messageId, ct);
            }
            catch (CodexAppServerRequestException) when (attempted)
            {
                lock (gate)
                {
                    pendingSubmissions.Remove(messageId);
                    sendVersion++;
                }
                throw;
            }
            catch (Exception ex) when (attempted && !ct.IsCancellationRequested &&
                ex is (IOException or OperationCanceledException or JsonException))
            {
                if (!await HasReceiptAsync(messageId, ct))
                    throw new SubmissionUncertainException("Codex 队列回执未确认。请先核对会话或 Codex 队列，避免重复发送。", ex);
            }

            bool wakeSucceeded = true;
            try { wakeThread(CodexThreadActivation.WakeUrl(id)); }
            catch (IOException) { wakeSucceeded = false; }
            lock (gate)
            {
                sendVersion++;
                if (pendingSubmissions.Contains(messageId))
                {
                    wakeFailed = !wakeSucceeded;
                    PublishLocked(view with { Status = view.Status == "active" ? "active" : "queued", PendingMessages = null,
                        Prompts = [], StatusDetail = "消息已进入 Codex 队列，等待原会话处理。" });
                }
            }
            Signal();
        }
        finally { sends.Release(); }
    }

    private async Task<bool> HasReceiptAsync(string messageId, CancellationToken ct)
    {
        try
        {
            await using var server = await open(ct);
            var queued = await ReadQueueAsync(server, id, 100, ct);
            if (queued.Items.Any(item => CodexDesktopAdapter.Text(item?["clientUserMessageId"]) == messageId)) return true;
            return CodexSubmissionReceipt.Contains(await CodexSavedSession.ReadThreadAsync(server, id, ct), messageId);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (IOException or OperationCanceledException or JsonException))
        { return false; }
    }

    private void Signal()
    {
        try { wake.Release(); }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { }
    }

    private bool ShouldWatch()
    {
        lock (gate)
            return pendingSubmissions.Count > 0 || observedActiveTurns.Count > 0 ||
                (observed && (queueSupported != false || history.HasOlder));
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                if (Volatile.Read(ref refreshRequested) == 0 && !ShouldWatch())
                {
                    await wake.WaitAsync(ct);
                    continue;
                }

                // A switch requests a fresh snapshot, but the attach reply carries
                // the cached one immediately. Coalesce switches during one read.
                if (Volatile.Read(ref refreshRequested) == 0)
                    await wake.WaitAsync(TimeSpan.FromSeconds(observed ? 5 : 15), ct);
                else
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                bool requested = Volatile.Read(ref refreshRequested) != 0;
                int readVersion;
                SessionView previous;
                lock (gate) { readVersion = sendVersion; previous = view; }
                try
                {
                    if (!requested && !ShouldWatch()) continue;
                    await using var server = await open(ct);
                    var nextHistory = await history.RefreshAsync(server, id, ct);
                    var next = await ReadSnapshotAsync(server, id, nextHistory, previous, ct);
                    lock (gate)
                    {
                        if (readVersion != sendVersion) continue;
                        string lastTurn = CodexQueuedHistory.TurnId(history.Turns.LastOrDefault());
                        history = next.History;
                        queueSupported = next.QueueSupported;
                        PublishSnapshotLocked(next, lastTurn);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is (IOException or OperationCanceledException or JsonException))
                {
                    lock (gate)
                    {
                        if (readVersion != sendVersion) continue;
                        PublishLocked(view with { StatusDetail = ShouldWatch()
                            ? "Codex 历史同步暂未完成，将自动重试。"
                            : "暂时无法同步 Codex 历史，正在显示已缓存的会话。" });
                    }
                }
                finally
                {
                    if (requested) Volatile.Write(ref refreshRequested, 0);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void PublishSnapshotLocked(Initial next, string previousLastTurnId)
    {
        var notices = new List<SessionCompletion>();
        var latestTurn = next.History.Turns.LastOrDefault();
        string lastTurnId = CodexQueuedHistory.TurnId(latestTurn);
        if (observed && lastTurnId.Length > 0 &&
            (next.View.Status == "active" || lastTurnId != previousLastTurnId &&
                TurnState(latestTurn) is ("inProgress" or "interrupted") && !HasTerminalTime(latestTurn)))
            observedActiveTurns.Add(lastTurnId);
        foreach (var turn in next.History.Turns)
        {
            string status = TurnState(turn);
            string turnId = CodexQueuedHistory.TurnId(turn);
            if (status is "completed" or "failed" || status == "interrupted" && HasTerminalTime(turn))
            {
                observedActiveTurns.Remove(turnId);
                if (turn?["items"] is JsonArray items)
                    foreach (var item in items.Where(x => CodexDesktopAdapter.Text(x?["type"]) == "userMessage"))
                        pendingSubmissions.Remove(CodexDesktopAdapter.Text(item?["clientId"] ?? item?["clientUserMessageId"]));
            }
            if (status == "completed" && turnId.Length > 0 && completedTurns.Add(turnId) &&
                next.History.ExpandedOlderTurns?.Contains(turnId) != true)
                notices.Add(new($"codex:{id}:{turnId}", id, next.View.Title, turnId));
        }
        var snapshot = next.View;
        if (pendingSubmissions.Count > 0 && snapshot.PendingMessages == 0 && snapshot.Status is "idle" or "interrupted")
            snapshot = snapshot with { Status = "codexStatePending", StatusDetail = "已提交消息，正在核对原会话的执行结果。" };
        PublishLocked(snapshot);
        foreach (var notice in notices) completed?.Invoke(notice);
    }

    private void PublishLocked(SessionView next)
    {
        var previous = view;
        if (next.PendingMessages == 0) wakeFailed = false;
        if (wakeFailed && next.Status == "queued")
            next = next with { StatusDetail = "消息已进入 Codex 队列。请在接入端的 Codex 桌面打开原会话处理。" };
        next = next with
        {
            Prompts = previous.Prompts.SequenceEqual(next.Prompts) ? previous.Prompts : next.Prompts,
            Messages = previous.Messages.SequenceEqual(next.Messages) ? previous.Messages : next.Messages
        };
        view = next;
        if (previous != next) changed(next);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await watcher;
        lifetime.Dispose();
        wake.Dispose();
        sends.Dispose();
    }
}
