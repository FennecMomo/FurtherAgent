using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace FurtherAgent.Core;

public static class HistoryPaging
{
    public const int BatchSize = 8;
    public const int MaxPageBytes = 1024 * 1024;
    private sealed record Index(string Revision, List<ChatEntry> Messages, int[] Sizes);
    private static readonly ConditionalWeakTable<SessionView, Index> Indexes = new();
    private static Index Get(SessionView view) => Indexes.GetValue(view, value =>
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var entries = value.Messages.Where(IsConversation).Select(m => m with { Text = Limit(m.Text), Detail = null }).ToList();
        var sizes = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entries[i], Json.Options);
            sizes[i] = bytes.Length + 1;
            hash.AppendData(bytes); hash.AppendData(new byte[] { 10 });
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), entries, sizes);
    });
    private static string Limit(string text) => text.Length <= 20000 ? text : text[..20000] + "\n[长内容已截断，请在电脑查看全文]";
    public static bool IsConversation(ChatEntry entry) => ChatPresentation.Kind(entry) is "user" or "assistant";
    public static HistoryPage Page(SessionView view, int? before = null, string? revision = null)
    {
        var index = Get(view);
        if (revision is not null && revision != index.Revision) throw new IOException("历史已更新，请返回最新记录后继续加载。");
        int end = before ?? index.Messages.Count;
        if (end < 0 || end > index.Messages.Count) throw new ArgumentException("历史游标无效。");
        int start = end, bytes = 2;
        while (start > 0 && end - start < BatchSize && bytes + index.Sizes[start - 1] < MaxPageBytes)
            bytes += index.Sizes[--start];
        return new(view.Id, index.Revision, start, end, index.Messages.Count, index.Messages.GetRange(start, end - start));
    }
    public static SessionView Latest(SessionView view)
    {
        var page = Page(view);
        return view with { Messages = page.Messages, History = new(page.Revision, page.Start, page.Total) };
    }
}
