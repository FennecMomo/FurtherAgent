using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public static class CodexSubmissionReceipt
{
    public static bool Contains(JsonNode? state, string clientId)
    {
        if (state is null || string.IsNullOrEmpty(clientId)) return false;
        IEnumerable<JsonNode?> turns = state["turnHistory"]?["history"]?["entitiesByKey"] is JsonObject entities
            ? entities.Select(p => p.Value) : state["turns"]?.AsArray() ?? [];
        return turns.SelectMany(t => t?["items"]?.AsArray() ?? []).Any(i =>
            i?["type"]?.ToString() == "userMessage" && (i["clientId"]?.ToString() == clientId || i["clientUserMessageId"]?.ToString() == clientId)
            || i?["type"]?.ToString() == "steeringUserMessage" && i["status"]?.ToString() == "accepted" && i["clientUserMessageId"]?.ToString() == clientId);
    }

    // A persisted native echo is also a receipt. Never resend after a slow or lost IPC reply.
    public static Exception RequestError(string error) => error.EndsWith("-timeout", StringComparison.Ordinal)
        ? new TimeoutException($"Codex 桌面确认超时（{error}），不代表拒收；发送结果待确认。")
        : new IOException($"Codex 桌面请求未成功：{error}。");

    public static async Task WaitAsync(Task acknowledgement, Func<bool> observed, CancellationToken ct,
        TimeSpan? reconciliationWindow = null)
    {
        try
        {
            while (!acknowledgement.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                if (observed()) return;
                await Task.WhenAny(acknowledgement, Task.Delay(100, ct));
            }
            if (observed()) return;
            try { await acknowledgement; }
            catch (TimeoutException ex)
            {
                // Native start-turn can time out before its accepted message reaches the stream.
                // Keep the original send pending so both clients use their normal draft cleanup.
                // Stay below the clients' three-minute RPC deadline and never submit a second time.
                long deadline = Environment.TickCount64 + (long)(reconciliationWindow ?? TimeSpan.FromMinutes(2)).TotalMilliseconds;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (observed()) return;
                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) break;
                    await Task.Delay((int)Math.Min(100, remaining), ct);
                }
                throw new IOException("远端确认超时，发送结果待确认，不代表 B 端拒收。草稿已保留；原请求可能仍在执行，请先核对会话，勿重复发送。", ex);
            }
        }
        finally
        {
            // Observe any late failure even when the accepted message arrived first.
            _ = acknowledgement.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
