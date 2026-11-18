using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace FurtherAgent.Core;

// Each logical peer has its own RPC queue, history cursor and delta decoder. Only
// the physical peer owns the socket, ordered writes, liveness and reconnection.
internal sealed class SessionChannelMultiplexer : IAsyncDisposable
{
    internal const int MaxChannels = 10;
    private readonly RpcPeer owner;
    private readonly Func<IConnectionTransport, CancellationToken, Task>? serve;
    private readonly ConcurrentDictionary<string, ChannelTransport> channels = new();
    private readonly ConcurrentDictionary<string, Task> workers = new();
    private readonly HashSet<string> earlyCloses = [];
    private readonly Queue<string> earlyCloseOrder = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private bool accepting = true;
    private bool closed;
    private int disposed;
    internal int ActiveCount => channels.Count;

    internal SessionChannelMultiplexer(RpcPeer owner, Func<IConnectionTransport, CancellationToken, Task>? serve = null)
    {
        this.owner = owner; this.serve = serve;
        owner.Notification += Receive;
        owner.Closed += Closed;
    }

    private void Closed(string _) => CloseAll();
    private void CloseAll()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true; accepting = false;
            lifetime.Cancel();
            foreach (var channel in channels.Values) channel.Close(false);
        }
    }
    internal bool TryRetireIfEmpty(Func<bool> redirect)
    {
        lock (gate)
        {
            if (!accepting || channels.Count > 0 || !redirect()) return false;
            accepting = false;
            return true;
        }
    }
    private ChannelTransport Add(string id)
    {
        lock (gate)
        {
            if (!accepting) throw new IOException("设备连接已更新，请重试会话通道。");
            if (channels.Count >= MaxChannels) throw new IOException("最多同时打开 10 个迷你会话，请先关闭至少一个迷你窗口。");
            var channel = new ChannelTransport(this, id);
            if (!channels.TryAdd(id, channel)) throw new IOException("会话通道已存在。");
            return channel;
        }
    }
    internal async Task<IConnectionTransport> OpenAsync(CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N");
        var channel = Add(id);
        try
        {
            await owner.CallChannelAsync("session-channel/open", new { id }, ct);
            ct.ThrowIfCancellationRequested();
            return channel;
        }
        catch { channel.Close(true); throw; }
    }
    internal Task<JsonNode?> AcceptAsync(JsonNode? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string id = args?["id"]?.ToString() ?? "";
        if (serve is null || id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new IOException("会话通道无效。");
        lock (gate)
        {
            if (earlyCloses.Remove(id)) throw new IOException("会话通道已关闭。");
            var channel = Add(id);
            var channelToken = channel.ClosedToken;
            var worker = Task.Run(async () =>
            {
                try { await serve(channel, channelToken); }
                finally { channel.Close(true); await channel.DisposeClosedLifetimeAsync(); }
            });
            workers[id] = worker;
            _ = worker.ContinueWith(_ => workers.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return Task.FromResult<JsonNode?>(Json.Node(new { ok = true }));
    }
    private void Receive(string method, JsonNode? args)
    {
        if (!method.StartsWith("session-channel/", StringComparison.Ordinal)) return;
        string id = args?["id"]?.ToString() ?? "";
        if (method == "session-channel/close")
        {
            lock (gate)
            {
                if (channels.TryGetValue(id, out var channel)) channel.Close(false);
                // The physical reader can receive close while the earlier open
                // RPC is still queued behind a root-session operation.
                else if (serve is not null && !closed && id.Length == 32 && id.All(Uri.IsHexDigit) && earlyCloses.Add(id))
                {
                    earlyCloseOrder.Enqueue(id);
                    while (earlyCloseOrder.Count > 128) earlyCloses.Remove(earlyCloseOrder.Dequeue());
                }
            }
        }
        else if (method == "session-channel/frame" && channels.TryGetValue(id, out var channel) && args?["frame"] is { } frame)
            channel.Deliver(frame.DeepClone());
    }
    private async Task SendCloseAsync(string id)
    {
        try { await owner.NotifyAsync("session-channel/close", new { id }, lifetime.Token); }
        catch (Exception) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        owner.Notification -= Receive; owner.Closed -= Closed;
        CloseAll();
        await Task.WhenAll(workers.Values);
        lifetime.Dispose();
    }

    private sealed class ChannelTransport(SessionChannelMultiplexer mux, string id) : IConnectionTransport
    {
        private readonly Channel<JsonNode> incoming = Channel.CreateBounded<JsonNode>(64);
        private readonly CancellationTokenSource? closedLifetime = mux.serve is null ? null : new();
        private readonly TaskCompletionSource closeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int closed;
        internal CancellationToken ClosedToken => closedLifetime?.Token ?? CancellationToken.None;
        internal async ValueTask DisposeClosedLifetimeAsync()
        {
            await closeFinished.Task;
            closedLifetime?.Dispose();
        }
        public long ReceivedBytes => 0;
        public long SentBytes => 0;
        internal void Deliver(JsonNode frame)
        {
            if (!incoming.Writer.TryWrite(frame)) Close(true);
        }
        internal void Close(bool notify)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try
            {
                mux.channels.TryRemove(id, out _);
                incoming.Writer.TryComplete(new IOException("会话通道已关闭。"));
                closedLifetime?.Cancel();
                if (notify && !mux.lifetime.IsCancellationRequested) _ = mux.SendCloseAsync(id);
            }
            finally { closeFinished.TrySetResult(); }
        }
        public Task<JsonNode> ReadAsync(CancellationToken ct) => incoming.Reader.ReadAsync(ct).AsTask();
        public Task WriteAsync(JsonNode message, CancellationToken ct)
        {
            if (Volatile.Read(ref closed) != 0) throw new IOException("会话通道已关闭。");
            return mux.owner.NotifyAsync("session-channel/frame", new JsonObject { ["id"] = id, ["frame"] = message.DeepClone() }, ct);
        }
        public void Abort() => Close(true);
        public ValueTask DisposeAsync() { Close(true); return ValueTask.CompletedTask; }
    }
}
