using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

internal sealed record MailboxPending(string Id, string Text, DateTimeOffset CreatedAt);
internal sealed record MailboxLocalState(string Server, string Name, MailboxBinding Binding,
    List<MailboxMessage> Messages, List<MailboxPending> Pending, string Draft = "", long PollAfter = 0,
    long LastNotified = 0, long ControllerReadThrough = 0, long AgentReadThrough = 0, bool HasOlder = false,
    int? ServerUnread = null, long ServerLatestSequence = 0);

// Separate from pairing credentials; tokens, drafts and local history all use current-user DPAPI.
internal sealed class MailboxVault(string root)
{
    public static string Key(string server, string code)
    {
        string normalized = code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (normalized.Length == 32) normalized = normalized[..8]; // Legacy full device code maps to the same mailbox.
        return DeviceConnectionPool<RemoteDevice>.Identity(server, normalized);
    }

    public IReadOnlyList<MailboxLocalState> Load()
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<MailboxLocalState>();
        foreach (string path in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                string key = Path.GetFileNameWithoutExtension(path);
                byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(path)),
                    Encoding.UTF8.GetBytes(key), DataProtectionScope.CurrentUser);
                var state = JsonSerializer.Deserialize<MailboxLocalState>(bytes, Json.Options);
                if (state is not null && Key(state.Server, state.Binding.Code) == key) result.Add(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException)
            { /* Keep an unreadable file intact; a fresh binding can be saved after the user opens it. */ }
        }
        return result;
    }

    public void Save(MailboxLocalState state)
    {
        Directory.CreateDirectory(root);
        string key = Key(state.Server, state.Binding.Code);
        byte[] bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(state, Json.Options),
            Encoding.UTF8.GetBytes(key), DataProtectionScope.CurrentUser);
        string path = Path.Combine(root, key + ".json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Convert.ToBase64String(bytes));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class DesktopMailboxSession : IDisposable
{
    private readonly MailboxVault vault;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Threading.DispatcherTimer draftTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool loaded;
    public MailboxLocalState State { get; private set; }
    public MailboxClient Client { get; }
    public string Key => MailboxVault.Key(State.Server, State.Binding.Code);
    public string? LastError { get; private set; }
    public event Action? Changed;
    public event Action<MailboxMessage>? Received;

    public DesktopMailboxSession(MailboxVault vault, MailboxLocalState state)
    {
        this.vault = vault; State = state; Client = new MailboxClient(state.Server, state.Binding); loaded = state.Messages.Count > 0;
        draftTimer.Tick += (_, _) =>
        {
            draftTimer.Stop();
            try { Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            { LastError = "留言草稿暂未保存：" + ex.Message; Changed?.Invoke(); }
        };
    }

    private void Save() { vault.Save(State); Changed?.Invoke(); }
    public void SetDraft(string draft) { State = State with { Draft = draft }; draftTimer.Stop(); draftTimer.Start(); }

    private void Merge(MailboxPage page)
    {
        var messages = State.Messages.Concat(page.Messages).GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.Sequence).ToList();
        State = State with { Messages = messages, ControllerReadThrough = page.ControllerReadThrough,
            AgentReadThrough = page.AgentReadThrough, ServerUnread = page.Unread ?? State.ServerUnread,
            ServerLatestSequence = Math.Max(State.ServerLatestSequence, page.LatestSequence) };
    }

    public async Task RefreshAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            LastError = null;
            await FlushCoreAsync();
            MailboxMessage? latestIncoming = null;
            for (int i = 0; i < 3; i++)
            {
                var page = await Client.FetchAsync(after: loaded ? State.PollAfter : null, ct: lifetime.Token);
                Merge(page);
                if (!loaded) State = State with { HasOlder = page.HasOlder };
                loaded = true;
                long fetched = page.Messages.Count == 0 ? State.PollAfter : page.Messages.Max(m => m.Sequence);
                State = State with { PollAfter = Math.Max(State.PollAfter, fetched) };
                latestIncoming = page.Messages.LastOrDefault(m => m.Sender == "agent" && m.Sequence > State.LastNotified) ?? latestIncoming;
                Save();
                if (!page.HasMore) break;
            }
            if (latestIncoming is not null)
            {
                State = State with { LastNotified = Math.Max(State.LastNotified, latestIncoming.Sequence) }; Save();
                if (latestIncoming.Sequence > State.ControllerReadThrough) Received?.Invoke(latestIncoming);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !lifetime.IsCancellationRequested)
        { LastError = ex.Message; Changed?.Invoke(); }
        catch (OperationCanceledException) { }
        finally { gate.Release(); }
    }

    public async Task LoadOlderAsync()
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            var page = await Client.FetchAsync(before: State.Messages.Count == 0 ? null : State.Messages.Min(m => m.Sequence), ct: lifetime.Token);
            Merge(page); State = State with { HasOlder = page.HasOlder }; Save();
        }
        finally { gate.Release(); }
    }

    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 8000) throw new IOException("每条留言最多 8000 字符，请拆成几条发送。");
        await gate.WaitAsync(lifetime.Token);
        try
        {
            State = State with { Pending = [.. State.Pending, new(Guid.NewGuid().ToString("N"), text, DateTimeOffset.UtcNow)], Draft = "" };
            Save();
            await FlushCoreAsync();
            LastError = null; Save();
        }
        catch (Exception ex) { LastError = ex.Message; Changed?.Invoke(); throw; }
        finally { gate.Release(); }
    }

    private async Task FlushCoreAsync()
    {
        foreach (var pending in State.Pending.ToArray())
        {
            var sent = await Client.SendAsync(pending.Text, pending.Id, lifetime.Token);
            var messages = State.Messages.Where(m => m.Id != sent.Id).Append(sent).OrderBy(m => m.Sequence).ToList();
            State = State with { Messages = messages, Pending = State.Pending.Where(p => p.Id != pending.Id).ToList() };
            // Do not advance PollAfter on send: unseen replies may precede this outgoing sequence.
            Save();
        }
    }

    public async Task MarkReadAsync()
    {
        if (State.Messages.Count == 0) return;
        await gate.WaitAsync(lifetime.Token);
        try
        {
            long through = State.Messages.Max(m => m.Sequence);
            if (through <= State.ControllerReadThrough) return;
            await Client.MarkReadAsync(through, lifetime.Token);
            int acknowledged = State.Messages.Count(m => m.Sender == "agent" && m.Sequence > State.ControllerReadThrough && m.Sequence <= through);
            int? remaining = through >= State.ServerLatestSequence ? 0 : State.ServerUnread is { } unread ? Math.Max(0, unread - acknowledged) : null;
            State = State with { ControllerReadThrough = Math.Max(State.ControllerReadThrough, through), ServerUnread = remaining }; Save();
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        draftTimer.Stop();
        try { vault.Save(State); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { LastError = ex.Message; }
        lifetime.Cancel(); Client.Dispose(); lifetime.Dispose();
    }
}
