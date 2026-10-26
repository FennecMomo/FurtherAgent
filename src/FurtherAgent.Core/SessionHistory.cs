using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// History reads may need extra time on a slow relay. Mutations retain their own deadlines.
public static class SessionHistory
{
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);

    public static async Task<JsonNode?> ReadAsync(RpcPeer peer, string method, object? args, CancellationToken ct = default)
    {
        if (method is not ("attach" or "history")) throw new ArgumentException("仅支持读取会话历史。", nameof(method));
        try { return await peer.CallAsync(method, args, ct, ReadTimeout); }
        catch (TimeoutException ex)
        { throw new IOException("会话历史读取超时（已等待 120 秒）。请刷新重试；若接入端较旧，请同时更新接入端以启用小页读取。", ex); }
    }
}
