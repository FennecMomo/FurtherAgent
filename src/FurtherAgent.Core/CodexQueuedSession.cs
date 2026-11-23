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
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task watcher;
    private volatile bool observed;
    private volatile SessionView view;
    private bool wakeFailed;
    private long followUntil;
    private int refreshRequested;

    public SessionView View => view;

    public CodexQueuedSession(SessionView initial, Func<CancellationToken, Task<ICodexMetadata>> open, Action<SessionView> changed)
    {
        id = initial.Id;
        view = initial;
        this.open = open;
        this.changed = changed;
        watcher = WatchAsync(lifetime.Token);
    }

    internal static async Task<SessionView> ReadAsync(ICodexMetadata server, string id, CancellationToken ct)
    {
        var thread = await CodexSavedSession.ReadThreadAsync(server, id, ct);
        var saved = CodexSavedSession.ToView(thread) with { Id = id };
        (JsonArray Items, bool HasMore) queued;
        try { queued = await ReadQueueAsync(server, id, 1, ct); }
        catch (CodexAppServerRequestException)
        {
            return saved with { StatusDetail = "当前 Codex 运行时无法读取原生消息队列。请在 Codex 桌面继续会话。" };
        }

        var latest = thread["turns"]?.AsArray().LastOrDefault();
        string turnStatus = CodexDesktopAdapter.Text(latest?["status"]);
        string status = turnStatus == "inProgress" ? "active" : queued.Items.Count > 0 ? "inputQueued" : turnStatus switch
        {
            "failed" => "error",
            "interrupted" => "interrupted",
            _ => "idle"
        };
        string detail = queued.Items.Count > 0
            ? turnStatus == "interrupted" ? "消息已进入 Codex 队列。若队列因上一次停止而暂停，可在 Codex 桌面继续。"
                : "有消息在 Codex 队列中等待处理。"
            : CodexRecovery.ErrorText(latest?["error"]);
        if (string.IsNullOrEmpty(detail)) detail = "支持向原会话发送消息。停止和审批请在 Codex 桌面操作。";
        return saved with
        {
            Status = status,
            TurnId = status == "active" ? CodexDesktopAdapter.Text(latest?["id"]) : null,
            StatusDetail = detail,
            CanSend = true,
            PendingMessages = queued.HasMore ? null : queued.Items.Count,
            Model = CodexDesktopAdapter.Text(thread["model"])
        };
    }

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
        if (value) Signal();
    }

    public SessionView AttachCached(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref refreshRequested, 1) == 0) Signal();
        return view;
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        await operations.WaitAsync(ct);
        try
        {
            if (!view.CanSend) throw new IOException("Codex 消息队列暂不可用，请刷新会话。");
            string messageId = Guid.NewGuid().ToString("D");
            bool attempted = false;
            try
            {
                await using var server = await open(ct);
                attempted = true;
                await EnqueueAsync(server, id, text, messageId, ct);
            }
            catch (Exception ex) when (attempted && !ct.IsCancellationRequested && ex is not CodexAppServerRequestException &&
                ex is (IOException or OperationCanceledException or JsonException))
            {
                Follow();
                if (!await HasReceiptAsync(messageId, ct))
                    throw new TimeoutException("Codex 队列回执未确认。请先核对会话或 Codex 队列，避免重复发送。", ex);
            }

            wakeFailed = false;
            try { CodexThreadActivation.Open(CodexThreadActivation.WakeUrl(id)); }
            catch (IOException) { wakeFailed = true; }
            Follow();
            Publish(view with { Status = view.Status == "active" ? "active" : "inputQueued", PendingMessages = null,
                StatusDetail = "消息已进入 Codex 队列，等待原会话处理。" });
        }
        finally { operations.Release(); }
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

    private void Follow()
    {
        Volatile.Write(ref followUntil, Environment.TickCount64 + 30_000);
        Signal();
    }

    private void Signal()
    {
        try { wake.Release(); }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { }
    }

    private bool ShouldWatch => view.CanSend && (Environment.TickCount64 < Volatile.Read(ref followUntil) ||
        (observed && view.Status is ("inputQueued" or "active")));

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                if (Volatile.Read(ref refreshRequested) == 0 && !ShouldWatch)
                {
                    await wake.WaitAsync(ct);
                    continue;
                }

                // A switch requests a fresh snapshot, but the attach reply carries
                // the cached one immediately. Coalesce switches during one read.
                if (Volatile.Read(ref refreshRequested) == 0)
                    await wake.WaitAsync(TimeSpan.FromSeconds(5), ct);
                else
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                await operations.WaitAsync(ct);
                bool requested = Volatile.Read(ref refreshRequested) != 0;
                try
                {
                    if (!requested && !ShouldWatch) continue;
                    await using var server = await open(ct);
                    Publish(await ReadAsync(server, id, ct));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is (IOException or OperationCanceledException or JsonException))
                {
                    Publish(view with { StatusDetail = ShouldWatch
                        ? "Codex 历史同步暂未完成，将自动重试。"
                        : "暂时无法同步 Codex 历史，正在显示已缓存的会话。" });
                }
                finally
                {
                    if (requested) Volatile.Write(ref refreshRequested, 0);
                    operations.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void Publish(SessionView next)
    {
        var previous = view;
        if (next.PendingMessages == 0) wakeFailed = false;
        if (wakeFailed && next.Status == "inputQueued")
            next = next with { StatusDetail = "消息已进入 Codex 队列。请在接入端的 Codex 桌面打开原会话处理。" };
        next = next with
        {
            Prompts = previous.Prompts,
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
        operations.Dispose();
    }
}
