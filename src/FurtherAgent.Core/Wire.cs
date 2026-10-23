using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace FurtherAgent.Core;

public static class Frames
{
    public const int MaxBytes = 32 * 1024 * 1024;
    public static Task<JsonNode> ReadAsync(Stream stream, CancellationToken ct) => ReadAsync(stream, MaxBytes, ct);
    // Only the local desktop pipe may carry full histories larger than relay messages.
    public static Task<JsonNode> ReadDesktopAsync(Stream stream, CancellationToken ct) => ReadAsync(stream, int.MaxValue, ct);
    private static async Task<JsonNode> ReadAsync(Stream stream, int limit, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > limit) throw new IOException("消息长度无效或超过 32 MiB。");
        using var frame = new FrameReadStream(stream, length);
        return await JsonNode.ParseAsync(frame, cancellationToken: ct) ?? throw new IOException("空消息。");
    }
    public static async Task WriteAsync(Stream stream, JsonNode message, CancellationToken ct)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, Json.Options);
        if (bytes.Length > MaxBytes) throw new IOException("消息超过 32 MiB。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}

// Expose exactly one frame to the JSON parser; never consume the following frame
// or allocate a buffer from an untrusted length header. Does not own the pipe.
internal sealed class FrameReadStream(Stream source, int length) : Stream
{
    private int remaining = length;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (remaining == 0 || buffer.Length == 0) return 0;
        int read = source.Read(buffer[..Math.Min(buffer.Length, remaining)]);
        if (read == 0) throw new EndOfStreamException("桌面消息未完整接收。");
        remaining -= read;
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (remaining == 0 || buffer.Length == 0) return 0;
        int read = await source.ReadAsync(buffer[..Math.Min(buffer.Length, remaining)], ct);
        if (read == 0) throw new EndOfStreamException("桌面消息未完整接收。");
        remaining -= read;
        return read;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// One reader and serialized writes per stream. Mutating requests are never replayed.
public sealed class RpcPeer(IJsonTransport transport) : IAsyncDisposable
{
    public RpcPeer(Stream stream) : this(new StreamTransport(stream)) { }
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<JsonNode> requests = Channel.CreateBounded<JsonNode>(64);
    private Task? handlers;
    private readonly object callGate = new();
    private RpcPeer? redirect;
    public bool HasPendingCalls => !pending.IsEmpty;
    internal bool SupportsTransportPing { get; set; }
    public event Action<string, object?>? CallSucceeded;
    // Atomically move future calls only after every submitted request has settled.
    internal bool TryRedirectTo(RpcPeer replacement, Func<bool> unchanged)
    {
        lock (callGate)
        {
            if (!pending.IsEmpty || lifetime.IsCancellationRequested || !unchanged()) return false;
            redirect = replacement; return true;
        }
    }
    public Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? Handler { get; set; }
    public event Action<string, JsonNode?>? Notification;
    public event Action<string>? Closed;
    public Task? Completion { get; private set; }
    private long lastReceivedTicks, receivedMessages;
    public DateTimeOffset? LastReceivedAt => Interlocked.Read(ref lastReceivedTicks) is var ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    public long ReceivedMessages => Interlocked.Read(ref receivedMessages);
    public void Start()
    {
        if (Completion is not null) throw new InvalidOperationException("连接读取器已经启动。");
        // A WPF caller must never bind the socket read/handler loops to its dispatcher.
        handlers = Task.Run(HandleLoopAsync);
        Completion = Task.Run(ReadLoopAsync);
    }
    public async Task<JsonNode?> CallAsync(string method, object? args, CancellationToken ct = default, TimeSpan? responseTimeout = null)
    {
        string id = Guid.NewGuid().ToString("N");
        var source = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RpcPeer? forwarded;
        lock (callGate)
        {
            forwarded = redirect;
            if (forwarded is null)
            {
                if (lifetime.IsCancellationRequested) throw new IOException("连接已断开。");
                pending[id] = source;
            }
        }
        if (forwarded is not null) return await forwarded.CallAsync(method, args, ct, responseTimeout);
        try
        {
            await WriteAsync(Json.Node(new { id, method, args }), ct);
            var result = await source.Task.WaitAsync(responseTimeout ?? TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
            CallSucceeded?.Invoke(method, args);
            return result;
        }
        finally { pending.TryRemove(id, out _); }
    }
    public Task NotifyAsync(string method, object? args) => WriteAsync(Json.Node(new { method, args }), lifetime.Token);
    private async Task WriteAsync(JsonNode message, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try { await transport.WriteAsync(message, ct); }
        finally { writes.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        string reason = "连接已关闭";
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var m = await transport.ReadAsync(lifetime.Token);
                Interlocked.Exchange(ref lastReceivedTicks, DateTimeOffset.UtcNow.Ticks);
                Interlocked.Increment(ref receivedMessages);
                string? id = m["id"]?.GetValue<string>();
                string? method = m["method"]?.GetValue<string>();
                if (method is null && id is not null)
                {
                    if (pending.TryGetValue(id, out var source))
                    {
                        if (m["error"] is { } error) source.TrySetException(new IOException(error.GetValue<string>()));
                        else source.TrySetResult(m["result"]?.DeepClone());
                    }
                }
                else if (method is not null && id is null) Notification?.Invoke(method, m["args"]);
                // Transport liveness is independent of serialized agent/file operations.
                else if (method == "transport-ping" && id is not null)
                    await WriteAsync(new JsonObject { ["id"] = id, ["result"] = true }, lifetime.Token);
                else if (method is not null && !requests.Writer.TryWrite(m)) throw new IOException("请求队列已满，请稍后重试。");
            }
        }
        catch (Exception ex) { reason = ex.Message; }
        finally
        {
            lifetime.Cancel();
            requests.Writer.TryComplete();
            foreach (var p in pending.Values) p.TrySetException(new IOException(reason));
            Closed?.Invoke(reason);
        }
    }
    private async Task HandleLoopAsync()
    {
        try
        {
            await foreach (var message in requests.Reader.ReadAllAsync(lifetime.Token))
                await HandleAsync(message["id"]!.GetValue<string>(), message["method"]!.GetValue<string>(), message["args"]);
        }
        catch (Exception)
        {
            lifetime.Cancel();
            await transport.DisposeAsync();
        }
    }
    private async Task HandleAsync(string id, string method, JsonNode? args)
    {
        JsonNode response;
        try
        {
            if (Handler is null) throw new InvalidOperationException("当前连接不接受请求。");
            var result = await Handler(method, args, lifetime.Token);
            response = new JsonObject { ["id"] = id, ["result"] = result };
        }
        catch (Exception ex) { response = Json.Node(new { id, error = ex.Message }); }
        await WriteAsync(response, lifetime.Token);
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await transport.DisposeAsync();
        if (Completion is not null) await Completion;
        if (handlers is not null) await handlers;
    }
}

public interface IJsonTransport : IAsyncDisposable
{
    Task<JsonNode> ReadAsync(CancellationToken ct);
    Task WriteAsync(JsonNode message, CancellationToken ct);
}
public interface IConnectionTransport : IJsonTransport
{
    void Abort();
    long ReceivedBytes { get; }
    long SentBytes { get; }
}

public sealed class StreamTransport(Stream stream) : IJsonTransport
{
    public Task<JsonNode> ReadAsync(CancellationToken ct) => Frames.ReadAsync(stream, ct);
    public Task WriteAsync(JsonNode message, CancellationToken ct) => Frames.WriteAsync(stream, message, ct);
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
