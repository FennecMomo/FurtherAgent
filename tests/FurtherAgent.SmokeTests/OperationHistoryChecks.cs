using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class OperationHistoryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var raw = new SessionView("wire", "", "", "idle", null,
            [new("assistant", "visible-reply", "PRIVATE_DETAIL"),
             new("工具", "PRIVATE_OUTPUT", """{"type":"mcpToolCall","tool":"analysis","arguments":"PRIVATE_ARGS","result":"PRIVATE_RESULT"}"""),
             new("推理摘要", "PRIVATE_REASONING")], []);
        var clean = OperationSummary.ForTransport(raw);
        check(OperationSummary.Display(new("tool.completed", "PRIVATE_OUTPUT",
            """{"event":"tool.completed","tool":"terminal","result":"PRIVATE_RESULT"}""")).Text == "调用工具 terminal",
            "Hermes event retains only the tool name");
        foreach (var payload in new[] { Json.Node(clean).ToJsonString(),
            Json.Node(HistoryPaging.Latest(raw, true)).ToJsonString(),
            Json.Node(HistoryPaging.Latest(raw, true, true)).ToJsonString(),
            Json.Node(new SessionSyncEncoder().Encode(clean)!).ToJsonString() })
            check(!payload.Contains("PRIVATE_") && payload.Contains("visible-reply"),
                "outbound snapshot, v2/v3 history and incremental snapshot omit raw operation payloads");
        var encoder = new SessionSyncEncoder(); encoder.Encode(clean);
        var changed = raw with { Messages = raw.Messages.Select(m => m.Role == "工具" ? m with { Text = "PRIVATE_CHANGED_OUTPUT" } : m).ToList() };
        check(encoder.Encode(OperationSummary.ForTransport(changed)) is null,
            "tool output growth alone produces no incremental network update");
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
        check(latest.Messages.Where(m => m.Operations is not null).All(m => m.Text.StartsWith("70 项") && m.Operations!.Count == 70), "every operation retained as a compact summary");
        check(latest.Messages.Last().Operations!.All(m => m.Detail is null && m.Text == "执行了一项操作"), "operation summaries never leak raw details");
        int requests = 0;
        var legacy = HistoryPaging.Page(view, includeOperations: true);
        var batch = OperationGroups.LoadBatchAsync(view.Id, new(legacy.Revision, legacy.Start, legacy.Total), before =>
        {
            requests++;
            return Task.FromResult(HistoryPaging.Page(view, before, legacy.Revision, true));
        }).GetAwaiter().GetResult();
        check(requests == 2 && batch.Messages.Count(HistoryPaging.IsConversation) < 8 && batch.Start < legacy.Start,
            "one user action stops after two legacy tool-only pages and keeps its progress");
        check(batch.Messages.Count == batch.End - batch.Start, "legacy aggregation preserves all operations and raw cursors");
        var legacyAll = batch.Messages.Concat(legacy.Messages).ToList();
        int legacyBefore = batch.Start, legacyBatches = 0;
        while (legacyBefore > 0)
        {
            var remaining = OperationGroups.LoadBatchAsync(view.Id, new(legacy.Revision, legacyBefore, legacy.Total), before =>
                Task.FromResult(HistoryPaging.Page(view, before, legacy.Revision, true))).GetAwaiter().GetResult();
            if (remaining.End != legacyBefore || remaining.Start >= legacyBefore || ++legacyBatches > messages.Count)
                throw new Exception("Legacy history did not progress contiguously");
            legacyAll.InsertRange(0, remaining.Messages); legacyBefore = remaining.Start;
        }
        check(legacyAll.Count == legacy.Total && legacyAll.Where(HistoryPaging.IsConversation).Select(m => m.Text)
            .SequenceEqual(Enumerable.Range(0, 40).Select(i => "正文 " + i)),
            "repeated bounded legacy batches preserve all messages and operations without gaps");
        var deletion = OperationSummary.Display(new("工具", "SECRET OUTPUT", """{"type":"fileChange","changes":[{"path":"report.doc","kind":{"type":"delete"}}]}"""));
        check(deletion.Text == "删除文件 report.doc" && deletion.Detail is null, "structured deletion renders a human action without payload");
        var tool = OperationSummary.Display(new("工具", "SECRET", """{"type":"mcpToolCall","tool":"tool_analysis","arguments":{"secret":"hidden"}}"""));
        check(tool.Text == "调用工具 tool_analysis" && tool.Detail is null, "tool name is shown without arguments or output");
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
        var toolsOnlyView = view with { Messages = messages.Where(m => !HistoryPaging.IsConversation(m)).ToList() };
        var toolsOnly = HistoryPaging.Page(toolsOnlyView, includeOperations:true, conversationBudget:true);
        int toolCount = toolsOnly.Messages.Sum(m => m.Operations!.Count);
        while (toolsOnly.Start > 0)
        {
            var olderTools = HistoryPaging.Page(toolsOnlyView, toolsOnly.Start, toolsOnly.Revision, true, true);
            if (olderTools.Start >= toolsOnly.Start || olderTools.End != toolsOnly.Start) throw new Exception("Tool-only history did not progress");
            toolCount += olderTools.Messages.Sum(m => m.Operations!.Count); toolsOnly = olderTools;
        }
        check(toolCount == 2800, "tool-only sessions stay visible across bounded contiguous pages");
        var oversizedView = new SessionView("oversized", "", "", "idle", null,
            [new("assistant", "older reply"), .. Enumerable.Range(0, 256).Select(_ => new ChatEntry("tool:" + new string('中', 160), "output"))], []);
        var oversized = HistoryPaging.Page(oversizedView, includeOperations: true, conversationBudget: true);
        check(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(oversized.Messages, Json.Options).Length > HistoryPaging.MaxPageBytes &&
            oversized.Start < oversized.End && oversized.Messages.Count == 1 && oversized.Messages[0].Operations?.Count == 256,
            "an oversized operation group is returned alone instead of an empty non-progressing page");
        var beforeOversized = HistoryPaging.Page(oversizedView, oversized.Start, oversized.Revision, true, true);
        check(beforeOversized.End == oversized.Start && beforeOversized.Messages.Single().Text == "older reply",
            "the page after an oversized entry still reaches adjacent older history");
        int oversizedRequests = 0;
        var oversizedBatch = OperationGroups.LoadBatchAsync(oversizedView.Id, new(oversized.Revision, oversized.End, oversized.Total), before =>
        {
            oversizedRequests++;
            return Task.FromResult(HistoryPaging.Page(oversizedView, before, oversized.Revision, true, true));
        }).GetAwaiter().GetResult();
        check(oversizedRequests == 1 && oversizedBatch.Start == oversized.Start,
            "a batch reaching the byte target stops without requesting another page");
    }
}
