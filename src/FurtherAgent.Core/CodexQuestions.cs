using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public static class CodexQuestions
{
    public const string AsyncKind = "codex/asyncUserInput";
    private const string Start = "<send_user_message_question_reply>", End = "</send_user_message_question_reply>";
    public static List<PendingPrompt> Pending(JsonNode[] turns)
    {
        var answered = new HashSet<string>();
        foreach (var turn in turns)
        foreach (var item in turn["items"]?.AsArray() ?? [])
        {
            string type = Text(item?["type"]);
            if (type != "userMessage" && !(type == "steeringUserMessage" && Text(item?["status"]) == "accepted")) continue;
            var content = (item?[type == "userMessage" ? "content" : "input"] as JsonArray);
            if (content?.Count != 1) continue;
            foreach (var reply in Replies(Text(content[0]?["text"]))) answered.Add(Text(reply?["questionItemId"]));
        }
        var prompts = new List<PendingPrompt>();
        // A later turn supersedes unanswered questions from old history.
        foreach (var item in turns.LastOrDefault()?["items"]?.AsArray() ?? [])
        {
            if (Text(item?["type"]) != "agentMessage" || item?["questions"] is not JsonArray questions || string.IsNullOrEmpty(Text(item["id"]))) continue;
            var normalized = new JsonArray();
            for (int i = 0; i < questions.Count; i++)
            {
                string id = JsonSerializer.Serialize(new object[] { "request_user_input_async", Text(item["id"]), i });
                if (answered.Contains(id)) continue;
                var q = questions[i]; var options = new JsonArray();
                foreach (var option in q?["options"] as JsonArray ?? [])
                    options.Add(option is JsonValue ? new JsonObject { ["label"] = Text(option) } : option?.DeepClone());
                string title = Text(q?["title"] ?? q?["question"]);
                if (string.IsNullOrWhiteSpace(title)) continue;
                normalized.Add(new JsonObject { ["id"] = id, ["question"] = title, ["options"] = options });
            }
            if (normalized.Count > 0) prompts.Add(new PendingPrompt("codex-async:" + Text(item["id"]), AsyncKind,
                "Agent 需要你的回答", "", new JsonObject { ["questions"] = normalized }));
        }
        return prompts;
    }
    public static string Reply(PendingPrompt prompt, JsonNode? response)
    {
        var replies = new JsonArray();
        foreach (var q in prompt.Payload!["questions"]!.AsArray())
        {
            string id = Text(q!["id"]);
            var values = response?["answers"]?[id]?["answers"] as JsonArray;
            if (values?.Count != 1 || string.IsNullOrWhiteSpace(Text(values[0]))) throw new ArgumentException("请回答每个问题。");
            replies.Add(new JsonObject { ["questionItemId"] = id, ["question"] = Text(q["question"]), ["answer"] = Text(values[0]) });
        }
        return Start + "\n" + replies.ToJsonString() + "\n" + End;
    }
    public static string DisplayReply(string text)
    {
        var replies = Replies(text);
        return replies.Count == 0 ? text : string.Join("\n\n", replies.Select(r => Text(r?["question"]) + "\n回答：" + Text(r?["answer"])));
    }
    private static JsonArray Replies(string text)
    {
        text = text.Trim();
        if (!text.StartsWith(Start, StringComparison.Ordinal) || !text.EndsWith(End, StringComparison.Ordinal)) return [];
        try
        {
            var node = JsonNode.Parse(text[Start.Length..^End.Length]);
            var list = node is JsonArray a ? a : new JsonArray(node);
            return list.Count > 0 && list.All(r => r is JsonObject && r["questionItemId"] is JsonValue && r["question"] is JsonValue && r["answer"] is JsonValue) ? list : [];
        }
        catch (JsonException) { return []; }
    }
    private static string Text(JsonNode? node) => CodexDesktopAdapter.Text(node);
}
