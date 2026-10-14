using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class OperationHistoryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var messages = new List<ChatEntry>();
        for (int i = 0; i < 40; i++)
        {
            messages.Add(new(i % 2 == 0 ? "你" : "Codex", "正文 " + i));
            for (int j = 0; j < 70; j++) messages.Add(new("工具", $"操作 {i}/{j}", new string('中', 3000)));
        }
        var view = new SessionView("operations", "", "", "idle", null, messages, []);
        var latest = HistoryPaging.Page(view, includeOperations: true, conversationBudget: true);
        check(latest.Messages.Count(HistoryPaging.IsConversation) == 8 && latest.Messages.Last(HistoryPaging.IsConversation).Text == "正文 39", "thousands of tool records do not displace the latest eight prose messages");
        check(System.Text.Encoding.UTF8.GetByteCount(Json.Node(latest).ToJsonString()) < HistoryPaging.MaxPageBytes, "grouped tool payload stays within transport budget");
        check(latest.Messages.Where(m => m.Operations is not null).All(m => m.Text.StartsWith("70 项") && m.Text.Contains("最近")), "operation counts remain accurate and bounded details disclose truncation");
        check(latest.Messages.Last().Operations!.Last().Text == "操作 39/69", "bounded operation details retain the most recent tools");
        var all = latest.Messages.ToList(); var page = latest;
        while (page.Start > 0)
        {
            var older = HistoryPaging.Page(view, page.Start, page.Revision, true, true);
            check(older.End == page.Start && older.Start + older.Messages.Count == older.End, "grouped history cursor remains contiguous");
            all.InsertRange(0, older.Messages); page = older;
        }
        check(all.Where(HistoryPaging.IsConversation).Select(m => m.Text).SequenceEqual(Enumerable.Range(0,40).Select(i => "正文 " + i)), "all prose loads in order without loss or duplication");
        check(all.Count(m => m.Operations is not null) == 40, "operation groups are neither duplicated nor skipped across pages");
        var window = OperationGroups.TakeConversations(all, 32);
        check(window.Count(HistoryPaging.IsConversation) == 32 && window.Count > 32, "mobile retention budget counts prose rather than operation summaries");
        string html = Transcript.Render(view with { Messages = window, OutputFiles = [new("outbox/test", "报告.txt", 1024)] });
        check(html.Contains("正文 31") && !html.Contains("正文 32") && html.Contains("<details class='operations'"), "mobile renders every retained prose item and collapsed operation groups");
        check(html.Contains("<details id='session-files'><summary>会话文件 · 1 个") && !html.Contains(" open") && html.Contains("attachment:"), "mobile files default to one collapsed row and retain download links");
        check(HistoryPaging.Page(view).Messages.All(HistoryPaging.IsConversation), "legacy v1 clients retain prose-only history");
        var v2 = HistoryPaging.Page(view, includeOperations: true);
        check(v2.Messages.Count == 8 && v2.Messages.All(m => m.Operations is null), "legacy v2 clients retain the existing flat schema");
        var toolsOnly = HistoryPaging.Page(view with { Messages = messages.Where(m => !HistoryPaging.IsConversation(m)).ToList() }, includeOperations:true, conversationBudget:true);
        check(toolsOnly.Start == 0 && toolsOnly.Messages.Count == 1, "tool-only sessions stay visible and bounded");
    }
}
