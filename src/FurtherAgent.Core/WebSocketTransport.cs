using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// Each endpoint opens an outbound connection to the relay. No local listen port.
public sealed class WebSocketTransport(WebSocket socket) : IJsonTransport
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private int disposed;
    private long receivedBytes, sentBytes;
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    public long SentBytes => Interlocked.Read(ref sentBytes);
    public bool IsOpen => socket.State == WebSocketState.Open;
    public Task<JsonNode> ReadAsync(CancellationToken ct) => ReadLimitedAsync(Frames.MaxBytes, ct);
    public async Task<JsonNode> ReadLimitedAsync(int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk.AsMemory(), ct);
            Interlocked.Add(ref receivedBytes, result.Count);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("中继连接已关闭。");
            if (buffer.Length + result.Count > limit) throw new IOException("中继消息超过允许长度。");
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) return JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) ?? throw new IOException("空中继消息。");
        }
    }
    public async Task WriteAsync(JsonNode message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json.Options);
        if (bytes.Length > Frames.MaxBytes) throw new IOException("中继消息超过允许长度。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await writes.WaitAsync(timeout.Token);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token); Interlocked.Add(ref sentBytes, bytes.Length); }
        finally { writes.Release(); }
    }
    public void Abort() => socket.Abort();
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) { socket.Abort(); socket.Dispose(); }
        return ValueTask.CompletedTask;
    }
}
