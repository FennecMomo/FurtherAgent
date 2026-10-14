namespace FurtherAgent.Core;

public static class OperationGroups
{
    public static List<ChatEntry> Group(IEnumerable<ChatEntry> source)
    {
        var result = new List<ChatEntry>();
        var operations = new List<ChatEntry>();
        void Flush()
        {
            if (operations.Count == 0) return;
            result.Add(new("工具", $"{operations.Count} 项操作记录", Operations: operations));
            operations = [];
        }
        void Add(ChatEntry entry)
        {
            if (operations.Count == 256) Flush();
            operations.Add(OperationSummary.Display(entry));
        }
        foreach (var entry in source)
        {
            if (HistoryPaging.IsConversation(entry)) { Flush(); result.Add(entry); }
            else if (entry.Operations is { } nested) { foreach (var operation in nested) Add(operation); }
            else Add(entry);
        }
        Flush(); return result;
    }

    // A user-visible batch is eight prose entries even when an older host pages raw tools.
    public static async Task<HistoryPage> LoadBatchAsync(string session, HistoryWindow cursor,
        Func<int, Task<HistoryPage>> fetch)
    {
        var entries = new List<ChatEntry>(); int before = cursor.Start, count = 0;
        do
        {
            var page = await fetch(before);
            if (page.Session != session || page.Revision != cursor.Revision || page.End != before ||
                page.Start < 0 || page.Start >= before || page.Start + page.Messages.Count != page.End || page.Total != cursor.Total)
                throw new IOException("历史分页不匹配，请返回最新后重试。");
            entries.InsertRange(0, page.Messages);
            count += page.Messages.Count(HistoryPaging.IsConversation);
            before = page.Start;
        } while (before > 0 && count < HistoryPaging.BatchSize);
        return new(session, cursor.Revision, before, cursor.Start, cursor.Total, entries);
    }

    public static List<ChatEntry> TakeConversations(IEnumerable<ChatEntry> source, int limit)
    {
        var result = new List<ChatEntry>(); int count = 0;
        foreach (var entry in source)
        {
            if (HistoryPaging.IsConversation(entry) && ++count > limit) break;
            result.Add(entry);
        }
        return result;
    }

    public static async Task<bool> NegotiateAsync(RpcPeer peer)
    {
        var response = await peer.CallAsync("history-mode", new { version = 3 });
        if (response?["version"]?.GetValue<int>() != 3)
            response = await peer.CallAsync("history-mode", new { version = 2 });
        return response?["enabled"]?.GetValue<bool>() == true;
    }
}
