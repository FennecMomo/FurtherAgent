using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexQueueChecks
{
    private const string ThreadId = "018924dc-4bac-754f-ad02-33ff2d87f128";
    private const string MessageId = "018924dc-4bac-754f-ad02-33ff2d87f129";
    private const string QueuedId = "018924dc-4bac-754f-ad02-33ff2d87f130";
    private const string Message = "继续原会话。\n请保留这段换行。";

    internal static async Task Run(Action<bool, string> check)
    {
        var bridge = new Metadata(method => method switch
        {
            "thread/read" => SavedThread(),
            "thread/queue/list" => Json.Node(new { data = new[] { QueuedSubmission(MessageId) } }),
            "thread/queue/add" => Json.Node(new { queuedSubmission = QueuedSubmission(MessageId) }),
            _ => throw new InvalidOperationException("Unexpected Codex method: " + method)
        });
        var attached = await CodexQueuedSession.ReadAsync(bridge, ThreadId, default);
        check(attached.Id == ThreadId && attached.Title == "Saved Codex thread" &&
            attached.Messages.Single().Text == "原会话中的回复" && attached.CanSend,
            "Codex queue support enables text input while preserving the selected thread and history");
        check(attached.PendingMessages == 1 && attached.CanStop == false && attached.TurnId is null &&
            attached.Prompts.Count == 0,
            "Codex queue view reports pending input without a live turn or approval controls");
        check(bridge.Calls.Count == 2 &&
            bridge.Calls.Select(call => call.Method).ToHashSet().SetEquals(["thread/read", "thread/queue/list"]) &&
            bridge.Calls.All(call => call.Parameters["threadId"]?.ToString() == ThreadId),
            "Codex queue attachment reads the selected thread without resuming it");

        bridge.Calls.Clear();
        await CodexQueuedSession.EnqueueAsync(bridge, ThreadId, Message, MessageId, default);
        var request = bridge.Calls.Single();
        check(request.Method == "thread/queue/add" && request.Parameters["threadId"]?.ToString() == ThreadId &&
            request.Parameters["clientUserMessageId"]?.ToString() == MessageId &&
            request.Parameters["input"] is JsonArray { Count: 1 } input &&
            input[0]?["type"]?.ToString() == "text" && input[0]?["text"]?.ToString() == Message,
            "Codex submission queues exact text and unique ID in the selected thread exactly once");

        var older = new Metadata(method => method switch
        {
            "thread/read" => SavedThread(),
            "thread/queue/list" => throw new CodexAppServerRequestException(method, "Method not found"),
            _ => throw new InvalidOperationException("Unexpected Codex method: " + method)
        });
        var readOnly = await CodexQueuedSession.ReadAsync(older, ThreadId, default);
        check(readOnly.Id == ThreadId && readOnly.Status == "readOnly" && !readOnly.CanSend &&
            readOnly.CanStop == false && readOnly.Messages.Single().Text == "原会话中的回复",
            "Codex runtimes without queue support still return the original read-only history");

        var wrongReceipt = new Metadata(method => method == "thread/queue/add"
            ? Json.Node(new { queuedSubmission = QueuedSubmission(Guid.NewGuid().ToString("D")) })
            : throw new InvalidOperationException("Unexpected Codex method: " + method));
        bool rejected = false;
        try { await CodexQueuedSession.EnqueueAsync(wrongReceipt, ThreadId, Message, MessageId, default); }
        catch (IOException) { rejected = true; }
        check(rejected && wrongReceipt.Calls.Count == 1,
            "a mismatched Codex queue receipt fails without issuing another add");
    }

    private static JsonNode SavedThread() => Json.Node(new
    {
        thread = new
        {
            id = ThreadId, name = "Saved Codex thread", cwd = "E:/fixture", status = new { type = "notLoaded" },
            turns = new[]
            {
                new
                {
                    id = "old-turn", status = "completed",
                    items = new[] { new { type = "agentMessage", id = "answer", text = "原会话中的回复" } }
                }
            }
        }
    });

    private static object QueuedSubmission(string clientUserMessageId) => new
    {
        id = QueuedId, clientUserMessageId,
        input = new[] { new { type = "text", text = Message, textElements = Array.Empty<object>() } }
    };

    private sealed class Metadata(Func<string, JsonNode> result) : ICodexMetadata
    {
        public List<(string Method, JsonNode Parameters)> Calls { get; } = [];

        public Task<JsonNode> CallAsync(string method, object args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((method, Json.Node(args)));
            return Task.FromResult(result(method));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
