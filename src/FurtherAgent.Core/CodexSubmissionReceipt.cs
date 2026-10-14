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
    public static async Task WaitAsync(Task acknowledgement, Func<bool> observed, CancellationToken ct)
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
            await acknowledgement;
        }
        finally
        {
            // Observe any late failure even when the accepted message arrived first.
            _ = acknowledgement.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
