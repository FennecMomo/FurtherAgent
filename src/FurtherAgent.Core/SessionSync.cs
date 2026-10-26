using System.Text.Json;
using System.Text.Json.Serialization;

namespace FurtherAgent.Core;

// Connection-local baseline. Every new connection starts with a full snapshot.
public sealed record SessionTextAppend(int Length, string Text);
public sealed record SessionUpdate(long Revision, long BaseRevision, int Keep, SessionView View,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionTextAppend? Append = null);
public sealed class SessionSyncEncoder(bool textDeltas = false)
{
    private SessionView? previous;
    private string? fingerprint;
    private long revision;
    public SessionUpdate? Encode(SessionView view)
    {
        string next = Json.Node(view).ToJsonString();
        if (next == fingerprint) return null;
        int keep = 0;
        bool reset = previous?.Id != view.Id;
        if (!reset)
            while (keep < previous!.Messages.Count && keep < view.Messages.Count && previous.Messages[keep] == view.Messages[keep]) keep++;
        SessionTextAppend? append = null;
        if (textDeltas && !reset && keep < previous!.Messages.Count && keep < view.Messages.Count)
        {
            var before = previous.Messages[keep];
            var after = view.Messages[keep];
            if (before.Role == after.Role && before.Detail == after.Detail && before.Operations is null && after.Operations is null &&
                after.Text.Length > before.Text.Length && after.Text.StartsWith(before.Text, StringComparison.Ordinal) &&
                !SplitsSurrogate(before.Text, after.Text[before.Text.Length..]))
            {
                var candidate = new SessionTextAppend(before.Text.Length, after.Text[before.Text.Length..]);
                // Compare the removed message with its replacement envelope, leaving a margin for Keep/comma changes.
                if (JsonSerializer.SerializeToUtf8Bytes(candidate, Json.Options).Length + 16 <
                    JsonSerializer.SerializeToUtf8Bytes(after, Json.Options).Length)
                { append = candidate; keep++; }
            }
        }
        long baseline = reset ? 0 : revision;
        previous = view with { Messages = view.Messages.ToList() };
        fingerprint = next;
        return new(++revision, baseline, keep, view with { Messages = view.Messages.Skip(keep).ToList() }, append);
    }

    internal static bool SplitsSurrogate(string prefix, string suffix) => prefix.Length > 0 && suffix.Length > 0 &&
        char.IsHighSurrogate(prefix[^1]) && char.IsLowSurrogate(suffix[0]);
}
public sealed class SessionSyncDecoder
{
    private SessionView? previous;
    private long revision;
    public SessionView Apply(SessionUpdate update)
    {
        if (update.Revision <= revision || update.Keep < 0 ||
            (update.BaseRevision == 0 ? update.Keep != 0 :
                update.BaseRevision != revision || previous?.Id != update.View.Id || update.Keep > previous.Messages.Count))
            throw new IOException($"会话增量基线不匹配（本地 {revision}，远端 {update.BaseRevision}→{update.Revision}，保留 {update.Keep}/{previous?.Messages.Count ?? 0}），请重新连接读取完整历史；不会重发操作。");
        var messages = update.BaseRevision == 0 ? new List<ChatEntry>() : previous!.Messages.Take(update.Keep).ToList();
        if (update.Append is { } append)
        {
            if (update.BaseRevision == 0 || messages.Count == 0 || append.Length < 0 || string.IsNullOrEmpty(append.Text) ||
                messages[^1].Text.Length != append.Length || messages[^1].Operations is not null ||
                SessionSyncEncoder.SplitsSurrogate(messages[^1].Text, append.Text))
                throw new IOException("文本增量基线不匹配，请重新连接读取完整历史；不会重发操作。");
            messages[^1] = messages[^1] with { Text = messages[^1].Text + append.Text };
        }
        messages.AddRange(update.View.Messages);
        previous = update.View with { Messages = messages }; revision = update.Revision;
        return previous;
    }
}
