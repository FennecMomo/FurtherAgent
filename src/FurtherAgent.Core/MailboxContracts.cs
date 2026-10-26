namespace FurtherAgent.Core;

public sealed record MailboxBinding(string Code, string Name, string Token, string Role = "controller");
public sealed record MailboxMessage(long Sequence, string Id, string Sender, string Text, DateTimeOffset CreatedAt);
public sealed record MailboxPage(string Code, string Name, List<MailboxMessage> Messages, long LatestSequence,
    long ControllerReadThrough, long AgentReadThrough, bool HasOlder, bool HasMore = false, int? Unread = null);
public sealed record MailboxStatus(string Code, string Name, long LatestSequence, long ControllerReadThrough,
    long AgentReadThrough, int Unread);
public sealed record MailboxSend(string Id, string Text);
public sealed record MailboxRead(long Through);
public sealed record MailboxPair(string Code);
