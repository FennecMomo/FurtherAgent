using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// Connect to an explicitly shared App Server. This transport never starts a
// Codex process and never opens a second writer to the desktop's data.
internal sealed class CodexLiveConnection : ICodexMetadata
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> pending = new();
    private Task? reader;
    private long sequence;

    public event Action<string, JsonNode?, JsonNode?>? Received;
    public event Action<string>? Faulted;

    public async Task OpenAsync(string endpoint, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss") || !uri.IsLoopback ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new IOException("共享 Codex App Server 必须使用回环 WebSocket 地址。");
        socket.Options.Proxy = null;
        if (Environment.GetEnvironmentVariable("FURTHERAGENT_CODEX_APP_SERVER_TOKEN_FILE") is { Length: > 0 } file)
        {
            string token = (await File.ReadAllTextAsync(file, ct)).Trim();
            if (token.Length == 0 || token.Any(char.IsWhiteSpace)) throw new IOException("共享 Codex App Server 令牌文件格式无效。");
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await socket.ConnectAsync(uri, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new IOException("连接本机共享 Codex App Server 超时。"); }
        reader = ReadAsync(lifetime.Token);
        await CallAsync("initialize", new { clientInfo = new { name = "furtheragent", title = "FurtherAgent", version = "0.17.0" },
            capabilities = new { experimentalApi = true } }, ct);
        await WriteAsync(Json.Node(new { method = "initialized" }), ct);
    }

    public async Task<JsonNode> CallAsync(string method, object args, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        long id = Interlocked.Increment(ref sequence);
        var reply = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        try
        {
            await WriteAsync(Json.Node(new { id, method, @params = args }), deadline.Token);
            var received = await reply.Task.WaitAsync(deadline.Token);
            if (received["error"] is { } error)
                throw new CodexAppServerRequestException(method, CodexDesktopAdapter.Text(error["message"]), error["code"]?.GetValue<int>());
            return received["result"] ?? throw new IOException("共享 Codex App Server 返回了空结果。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException($"等待 Codex {method} 回执超时。"); }
        finally { pending.TryRemove(id, out _); }
    }

    public Task RespondAsync(JsonNode requestId, JsonNode result, CancellationToken ct) => WriteAsync(new JsonObject
    {
        ["id"] = requestId.DeepClone(), ["result"] = result.DeepClone()
    }, ct);

    private async Task WriteAsync(JsonNode message, CancellationToken ct)
    {
        byte[] data = Encoding.UTF8.GetBytes(message.ToJsonString(Json.Options));
        await writer.WaitAsync(ct);
        try { await socket.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, ct); }
        catch (WebSocketException ex) { throw new IOException("共享 Codex App Server 已断开。", ex); }
        finally { writer.Release(); }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            byte[] buffer = new byte[32 * 1024];
            using var message = new MemoryStream();
            while (!ct.IsCancellationRequested)
            {
                var part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (part.MessageType == WebSocketMessageType.Close) throw new IOException("共享 Codex App Server 已断开。");
                if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > Frames.MaxBytes)
                    throw new IOException("共享 Codex App Server 返回了无效消息。");
                message.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage) continue;

                message.Position = 0;
                var value = JsonNode.Parse(message) ?? throw new IOException("共享 Codex App Server 返回了空消息。");
                message.SetLength(0);
                if (value["method"] is JsonValue method)
                    Received?.Invoke(method.GetValue<string>(), value["params"], value["id"]);
                else if (value["id"] is JsonValue response && response.TryGetValue<long>(out long id) &&
                    pending.TryGetValue(id, out var reply))
                    reply.TrySetResult(value);
            }
        }
        catch (Exception ex)
        {
            foreach (var reply in pending.Values)
                reply.TrySetException(new IOException("共享 Codex App Server 已断开。", ex));
            if (!ct.IsCancellationRequested) Faulted?.Invoke(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        socket.Abort();
        if (reader is not null) await reader;
        socket.Dispose();
        lifetime.Dispose();
        writer.Dispose();
    }
}
