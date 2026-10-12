using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// Keep the follower request and its restore payload together: Codex reads the
// restore context even when no attachments or workspace overrides are supplied.
public static class CodexMessageSubmission
{
    public sealed record Request(string Method, int Version, JsonNode Parameters);

    public static Request Build(SessionView view, string text, bool questionReply = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100000)
            throw new ArgumentException("消息不能为空或超过 100000 字符。");
        string messageId = Guid.NewGuid().ToString();
        string trigger = questionReply ? "send_user_message_async_question" : "composer";
        var input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } };
        var restoreMessage = new
        {
            id = messageId, text, cwd = view.Cwd, createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            context = new
            {
                prompt = text, turnTrigger = trigger,
                addedFiles = Array.Empty<object>(), fileAttachments = Array.Empty<object>(),
                imageAttachments = Array.Empty<object>(), commentAttachments = Array.Empty<object>(),
                workspaceRoots = new[] { view.Cwd }, ideContext = (object?)null
            }
        };
        // An in-progress turn is stronger evidence than the coarse runtime badge,
        // which can say idle/waiting while a tool is waiting for user input.
        if (!string.IsNullOrEmpty(view.TurnId) || view.Status == "active")
            return new("thread-follower-steer-turn", 1, Json.Node(new
            {
                conversationId = view.Id, input, restoreMessage,
                clientUserMessageId = messageId, attachments = Array.Empty<object>()
            }));
        return new("thread-follower-start-turn", 2, Json.Node(new
        {
            conversationId = view.Id,
            turnStart = new
            {
                request = new { threadId = view.Id, input, clientUserMessageId = messageId, turnTrigger = trigger },
                context = new { inheritThreadSettings = true, restoreMessage, attachments = Array.Empty<object>(), commentAttachments = Array.Empty<object>() }
            }
        }));
    }
}
