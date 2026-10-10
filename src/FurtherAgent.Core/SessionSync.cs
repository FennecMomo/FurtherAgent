namespace FurtherAgent.Core;

// Connection-local baseline. Every new connection starts with a full snapshot.
public sealed record SessionUpdate(long Revision, long BaseRevision, int Keep, SessionView View);
public sealed class SessionSyncEncoder
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
        long baseline = reset ? 0 : revision;
        previous = view with { Messages = view.Messages.ToList() };
        fingerprint = next;
        return new(++revision, baseline, keep, view with { Messages = view.Messages.Skip(keep).ToList() });
    }
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
        messages.AddRange(update.View.Messages);
        previous = update.View with { Messages = messages }; revision = update.Revision;
        return previous;
    }
}
