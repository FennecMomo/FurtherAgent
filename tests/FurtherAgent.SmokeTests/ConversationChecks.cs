using FurtherAgent.Core;
using FurtherAgent.Mobile;
using System.Text.Json;

internal static class ConversationChecks
{
    public static void Run(Action<bool, string> check)
    {
        string home = Path.Combine(Path.GetTempPath(), "fa-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        try
        {
            string ordinary = Guid.NewGuid().ToString(), guardian = Guid.NewGuid().ToString(), worker = Guid.NewGuid().ToString();
            void Metadata(string id, object source) => File.WriteAllText(Path.Combine(home, "sessions", $"rollout-{id}.jsonl"),
                Json.Node(new { type = "session_meta", payload = new { id, cwd = "D:/project", source } }).ToJsonString() + "\n");
            Metadata(ordinary, "cli"); Metadata(guardian, new { subagent = new { other = "guardian" } }); Metadata(worker, new { subagent = new { thread_spawn = new { parent_thread_id = ordinary } } });
            var listed = CodexSessionCatalog.Enrich(home, [new(ordinary, "Guardian review", ""), new(guardian, "renamed review", ""), new(worker, "worker", "")]).ToList();
            check(listed.Count == 1 && listed[0].Id == ordinary && listed[0].Cwd == "D:/project", "catalog filters internal source metadata, not ordinary user titles");
        }
        finally { Directory.Delete(home, true); }
        var entries = new List<ChatEntry>();
        for (int i = 0; i < 12; i++)
        {
            entries.Add(new("user", "question " + i));
            entries.Add(new("工具", new string('x', 100000), "private tool details"));
            entries.Add(new("推理摘要", "reasoning payload"));
            entries.Add(new("assistant", "answer " + i, "extra process detail"));
        }
        var source = new SessionView("s", "s", "", "active", "turn", entries, [new("approve", "command", "Approval", "decision", null)]);
        var latest = HistoryPaging.Latest(source);
        check(latest.Messages.Count == 8 && latest.History?.Total == 24 && latest.Messages.All(HistoryPaging.IsConversation), "eight-record page counts only human and assistant messages");
        check(latest.Messages.All(m => m.Detail is null) && JsonSerializer.SerializeToUtf8Bytes(latest, Json.Options).Length < 4096,
            "tool bodies and process details are removed before network serialization");
        check(latest.Prompts.Count == 1 && latest.TurnId == "turn", "conversation filtering preserves approvals and running state");
        var toolOnlyChange = source with { Messages = entries.Select(m => m.Role == "工具" ? m with { Text = "new tool output" } : m).ToList() };
        check(HistoryPaging.Latest(toolOnlyChange).History!.Revision == latest.History!.Revision, "tool-only updates do not invalidate conversation cache");
        var previous = HistoryPaging.Page(source, latest.History!.Start, latest.History.Revision);
        check(previous.Messages.Count == 8 && previous.Messages[0].Text == "question 4", "filtered history paginates without losing conversation messages");
        var html = Transcript.Render(source);
        check(!html.Contains("private tool details") && !html.Contains("reasoning payload") && html.Contains("分析任务与规划步骤") && html.Contains("class='operations'") && !html.Contains("extra process detail"), "process entries are available inside collapsed groups without leaking assistant detail");
    }
}
