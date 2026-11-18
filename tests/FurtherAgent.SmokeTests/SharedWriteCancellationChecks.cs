using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FurtherAgent.Core;

internal static class SharedWriteCancellationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var transport = new PausedTransport();
        await using var owner = new RpcPeer(transport);
        owner.Start();
        await using var mux = new SessionChannelMultiplexer(owner);
        await using var child = await mux.OpenAsync(default);

        using (var cancel = new CancellationTokenSource())
        {
            var pause = transport.PauseNext("session-channel/frame");
            Task write = child.WriteAsync(Json.Node(new { method = "first-frame" }), cancel.Token);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            bool unaffected = !pause.WriteToken.IsCancellationRequested && !write.IsCompleted;
            pause.Release.TrySetResult();
            await write.WaitAsync(TimeSpan.FromSeconds(3));
            await owner.NotifyAsync("main-still-usable", null);
            check(unaffected && transport.Messages.Any(message => message["args"]?["frame"]?["method"]?.ToString() == "first-frame"),
                "cancelling a child during a physical frame write finishes the frame without cancelling the shared transport");
        }

        var blocker = transport.PauseNext("parent-blocker");
        Task blocked = owner.NotifyAsync("parent-blocker", null);
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        int attempts = transport.WriteAttempts;
        using (var cancel = new CancellationTokenSource())
        {
            Task queuedFrame = child.WriteAsync(Json.Node(new { method = "cancelled-queued-frame" }), cancel.Token);
            Task queuedOpen = owner.CallChannelAsync("session-channel/open", new { id = "cancelled-queued-open" }, cancel.Token);
            cancel.Cancel();
            await Cancelled(queuedFrame);
            await Cancelled(queuedOpen);
            check(transport.WriteAttempts == attempts && !owner.HasPendingCalls,
                "a cancelled queued child frame and channel-open request never reach the physical writer");
        }
        blocker.Release.TrySetResult();
        await blocked.WaitAsync(TimeSpan.FromSeconds(3));

        using (var cancel = new CancellationTokenSource())
        {
            var pause = transport.PauseNext("session-channel/open");
            Task opening = owner.CallChannelAsync("session-channel/open", new { id = "midwrite-open" }, cancel.Token);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            bool unaffected = !pause.WriteToken.IsCancellationRequested;
            pause.Release.TrySetResult();
            await Cancelled(opening);
            await owner.NotifyAsync("main-after-cancelled-open", null);
            check(unaffected && transport.Messages.Any(message => message["args"]?["id"]?.ToString() == "midwrite-open"),
                "cancelling channel-open during its physical write preserves the complete request and only cancels the caller's wait");
        }

        var startupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int startupAttempts = 0;
        await using (var serving = new SessionChannelMultiplexer(owner, async (_, ct) =>
        {
            Interlocked.Increment(ref startupAttempts);
            startupEntered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { startupCancelled.TrySetResult(); }
        }))
        {
            string id = Guid.NewGuid().ToString("N");
            await serving.AcceptAsync(Json.Node(new { id }), default);
            await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await transport.ReceiveAsync(Json.Node(new { method = "session-channel/close", args = new { id } }));
            await startupCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await owner.NotifyAsync("main-after-cancelled-startup", null);
            check(owner.Completion?.IsCompleted == false,
                "closing a channel during host startup cancels its pending initialization without closing the shared owner");

            string cancelledId = Guid.NewGuid().ToString("N");
            var closeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveClose(string method, JsonNode? args)
            {
                if (method == "session-channel/close" && args?["id"]?.ToString() == cancelledId) closeObserved.TrySetResult();
            }
            owner.Notification += ObserveClose;
            try
            {
                await transport.ReceiveAsync(Json.Node(new { method = "session-channel/close", args = new { id = cancelledId } }));
                await closeObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
                bool rejected = false;
                try { await serving.AcceptAsync(Json.Node(new { id = cancelledId }), default); }
                catch (IOException) { rejected = true; }
                await owner.NotifyAsync("main-after-close-before-open", null);
                check(rejected && Volatile.Read(ref startupAttempts) == 1 && owner.Completion?.IsCompleted == false,
                    "a close received before a queued channel-open prevents an orphan startup without affecting the owner");
            }
            finally { owner.Notification -= ObserveClose; }
        }

        var shutdown = transport.PauseNext("session-channel/frame");
        Task finalWrite = child.WriteAsync(Json.Node(new { method = "parent-shutdown" }), default);
        await shutdown.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Cancelled(finalWrite);
        check(shutdown.WriteToken.IsCancellationRequested,
            "closing the physical owner still cancels an in-progress channel frame write");
    }

    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation did not stop the queued operation.");
    }

    private sealed class WritePause(string method)
    {
        public string Method { get; } = method;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken WriteToken;
    }

    private sealed class PausedTransport : IJsonTransport
    {
        private readonly Channel<JsonNode> incoming = Channel.CreateUnbounded<JsonNode>();
        private WritePause? pause;
        public ConcurrentQueue<JsonNode> Messages { get; } = new();
        public int WriteAttempts;
        public WritePause PauseNext(string method)
        {
            var next = new WritePause(method);
            if (Interlocked.CompareExchange(ref pause, next, null) is not null) throw new InvalidOperationException("A write is already paused.");
            return next;
        }
        public Task<JsonNode> ReadAsync(CancellationToken ct) => incoming.Reader.ReadAsync(ct).AsTask();
        public Task ReceiveAsync(JsonNode message) => incoming.Writer.WriteAsync(message).AsTask();
        public async Task WriteAsync(JsonNode message, CancellationToken ct)
        {
            Interlocked.Increment(ref WriteAttempts);
            var current = Volatile.Read(ref pause);
            if (current is not null && message["method"]?.ToString() == current.Method &&
                Interlocked.CompareExchange(ref pause, null, current) == current)
            {
                current.WriteToken = ct;
                current.Entered.TrySetResult();
                await current.Release.Task.WaitAsync(ct);
            }
            Messages.Enqueue(message.DeepClone());
            if (message["id"] is { } id)
                await incoming.Writer.WriteAsync(new JsonObject { ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["ok"] = true } }, ct);
        }
        public ValueTask DisposeAsync() { incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
