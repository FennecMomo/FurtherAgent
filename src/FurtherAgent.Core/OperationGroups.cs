using System.Text.Json;

namespace FurtherAgent.Core;

public static class OperationGroups
{
    public static List<ChatEntry> Group(IEnumerable<ChatEntry> source)
    {
        var result = new List<ChatEntry>();
        var operations = new List<ChatEntry>();
        int count = 0, bytes = 0;
        void Flush()
        {
            if (count == 0) return;
            string note = operations.Count < count ? $"（仅展示最近 {operations.Count} 项详情）" : "";
            result.Add(new("工具", $"{count} 项操作记录{note}", Operations: operations));
            operations = []; count = bytes = 0;
        }
        foreach (var entry in source)
        {
            if (HistoryPaging.IsConversation(entry) || entry.Operations is not null)
            { Flush(); result.Add(entry); continue; }
            count++;
            static string Clip(string text) => text.Length <= 1000 ? text : text[..1000] + "\n[操作详情截断]";
            var detail = entry with { Text = Clip(entry.Text), Detail = entry.Detail is null ? null : Clip(entry.Detail) };
            int size = JsonSerializer.SerializeToUtf8Bytes(detail, Json.Options).Length;
            while (operations.Count > 0 && bytes + size > 16000)
            {
                bytes -= JsonSerializer.SerializeToUtf8Bytes(operations[0], Json.Options).Length;
                operations.RemoveAt(0);
            }
            operations.Add(detail); bytes += size;
        }
        Flush(); return result;
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
