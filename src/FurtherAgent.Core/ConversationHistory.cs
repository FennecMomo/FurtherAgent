namespace FurtherAgent.Core;

/// <summary>Retains the reading window while live status and prompts continue to update.</summary>
public sealed class ConversationHistory
{
    public SessionView? Latest { get; private set; }
    public SessionView? Displayed { get; private set; }
    public bool ReadingOlder { get; private set; }
    public bool HasOlder => Displayed?.History?.Start > 0;
    public SessionView Accept(SessionView view)
    {
        if (Latest?.Id != view.Id) ReadingOlder = false;
        Latest = view;
        Displayed = ReadingOlder && Displayed is { } previous
            ? view with { Messages = previous.Messages, History = previous.History } : view;
        return Displayed;
    }
    public SessionView Prepend(HistoryPage page)
    {
        if (Displayed is not { History: { } history } view || Latest?.Id != view.Id ||
            Latest.History?.Revision != history.Revision)
            throw new IOException("会话历史已更新，请点击「返回最新」后重新加载；当前阅读位置已保留。");
        if (page.Session != view.Id || page.Revision != history.Revision || page.End != history.Start ||
            page.Start < 0 || page.Start >= page.End || page.Start + page.Messages.Count != page.End || page.Total != history.Total)
            throw new IOException("历史分页不匹配，请返回最新记录后重试。");
        ReadingOlder = true;
        Displayed = view with { Messages = page.Messages.Concat(view.Messages).Take(256).ToList(), History = history with { Start = page.Start } };
        return Displayed;
    }
    public SessionView? ShowLatest()
    {
        ReadingOlder = false; Displayed = Latest; return Displayed;
    }
    public void Clear() { Latest = Displayed = null; ReadingOlder = false; }
}
