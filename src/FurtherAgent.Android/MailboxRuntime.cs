using ConnectionProfile = FurtherAgent.Mobile.ConnectionProfile;
using System.Text.Json;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

// Independent of DSH sessions and Activity lifetime. A mailbox never submits an Agent turn.
internal static class MailboxRuntime
{
    internal sealed record Pending(string Id, string Text, DateTimeOffset CreatedAt);
    private sealed record Cached(List<MailboxMessage> Messages, List<Pending> Outbox, long AgentReadThrough,
        long ControllerReadThrough, long NotifiedThrough, bool HasOlder, long ReceivedThrough = 0, int? ServerUnread = null);
    internal sealed class Entry(ConnectionProfile profile, MailboxBinding binding)
    {
        internal ConnectionProfile Profile { get; } = profile;
        internal MailboxClient Client { get; set; } = new(profile.Server, binding);
        internal List<MailboxMessage> Messages { get; } = [];
        internal List<Pending> Outbox { get; } = [];
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal long AgentReadThrough, ControllerReadThrough, NotifiedThrough, ReceivedThrough;
        internal bool HasOlder, Initialized;
        internal string? Error;
        internal int? ServerUnread;
        internal int Unread => ServerUnread ?? Messages.Count(m => m.Sender == "agent" && m.Sequence > ControllerReadThrough);
    }
    private static readonly Dictionary<string, Entry> entries = [];
    private static readonly SemaphoreSlim opening = new(1, 1), polling = new(1, 1);
    internal static event Action<string>? Changed;
    internal static string? ViewingProfile { get; set; }
    internal static int TotalUnread => entries.Values.Sum(e => e.Unread);
    internal static bool HasBindings => Preferences.Get("mailbox-has-bindings", false);
    private static string Key(string id) => "mailbox-binding-v1." + id;
    private static string CachePath(string id) => Path.Combine(FileSystem.AppDataDirectory, "mailboxes", id + ".json");
    internal static string DraftKey(string id) => "mailbox-draft-v1." + id;

    internal static async Task<Entry?> OpenAsync(ConnectionProfile profile, bool pair)
    {
        await opening.WaitAsync();
        try
        {
            if (entries.TryGetValue(profile.Id, out var existing)) return existing;
            MailboxBinding? binding = null;
            try
            {
                string? json = await SecureStorage.GetAsync(Key(profile.Id));
                if (json is not null) binding = JsonSerializer.Deserialize<MailboxBinding>(json, Json.Options);
            }
            catch { /* The original connection remains available for explicit re-binding. */ }
            if (binding is null)
            {
                if (!pair) return null;
                binding = await MailboxClient.PairAsync(profile.Server, profile.Code);
                await SecureStorage.SetAsync(Key(profile.Id), JsonSerializer.Serialize(binding, Json.Options));
            }
            var entry = new Entry(profile, binding);
            try
            {
                var saved = JsonSerializer.Deserialize<Cached>(await File.ReadAllTextAsync(CachePath(profile.Id)), Json.Options);
                if (saved is not null)
                {
                    entry.Messages.AddRange(saved.Messages); entry.Outbox.AddRange(saved.Outbox);
                    entry.AgentReadThrough = saved.AgentReadThrough; entry.ControllerReadThrough = saved.ControllerReadThrough;
                    entry.NotifiedThrough = saved.NotifiedThrough; entry.HasOlder = saved.HasOlder; entry.ReceivedThrough = saved.ReceivedThrough; entry.ServerUnread = saved.ServerUnread;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            entries[profile.Id] = entry;
            Preferences.Set("mailbox-has-bindings", true);
            try { ConnectionService.Start(); } catch { /* Foreground polling still works if Android refuses service start. */ }
            return entry;
        }
        finally { opening.Release(); }
    }

    internal static async Task<Entry> RebindAsync(ConnectionProfile profile)
    {
        var binding = await MailboxClient.PairAsync(profile.Server, profile.Code);
        await SecureStorage.SetAsync(Key(profile.Id), JsonSerializer.Serialize(binding, Json.Options));
        var entry = await OpenAsync(profile, false) ?? throw new IOException("留言绑定未能保存，请重试。");
        await entry.Gate.WaitAsync();
        try { entry.Client.Dispose(); entry.Client = new(profile.Server, binding); entry.Error = null; }
        finally { entry.Gate.Release(); }
        return entry;
    }

    internal static async Task PollAllAsync()
    {
        if (!await polling.WaitAsync(0)) return;
        try
        {
            var saved = await SavedConnections.LoadAsync();
            bool found = false;
            foreach (var profile in saved.Connections)
            {
                try
                {
                    var entry = await OpenAsync(profile, false);
                    if (entry is not null) { found = true; await RefreshAsync(entry); }
                }
                catch { /* A failed mailbox does not stop other saved connections. */ }
            }
            Preferences.Set("mailbox-has-bindings", found);
        }
        catch { /* SecureStorage may be unavailable before the device is unlocked. */ }
        finally { polling.Release(); }
    }

    internal static async Task RefreshAsync(Entry entry)
    {
        if (!await entry.Gate.WaitAsync(0)) return;
        try
        {
            await FlushCoreAsync(entry);
            long after = entry.ReceivedThrough;
            var page = await entry.Client.FetchAsync(after: after > 0 ? after : null);
            if (!entry.Initialized && after == 0) entry.HasOlder = page.HasOlder;
            Apply(entry, page); entry.Initialized = true;
            // Never jump to LatestSequence while a bounded after-page still has more data.
            for (int i = 0; page.HasMore && page.Messages.Count > 0 && i < 3; i++)
            {
                page = await entry.Client.FetchAsync(after: page.Messages[^1].Sequence);
                Apply(entry, page);
            }
            entry.Error = null;
            var unseen = entry.Messages.Where(m => m.Sender == "agent" && m.Sequence > Math.Max(entry.NotifiedThrough, entry.ControllerReadThrough)).ToArray();
            if (unseen.Length > 0)
            {
                entry.NotifiedThrough = unseen[^1].Sequence;
                if (!(CompletionNotifications.Foreground && ViewingProfile == entry.Profile.Id))
                    CompletionNotifications.ShowMailbox(entry.Profile.Id, entry.Profile.Name, unseen[^1].Text);
            }
            await SaveAsync(entry);
        }
        catch (Exception ex)
        {
            entry.Error = ex is NotSupportedException ? "中继尚未启用留言，请先升级服务器。" : "留言暂未同步：" + ex.Message;
            await SaveAsync(entry);
        }
        finally { entry.Gate.Release(); Notify(entry); }
    }

    internal static async Task LoadOlderAsync(Entry entry)
    {
        await entry.Gate.WaitAsync();
        try
        {
            var first = entry.Messages.FirstOrDefault();
            if (first is null) return;
            var page = await entry.Client.FetchAsync(before: first.Sequence);
            Apply(entry, page); entry.HasOlder = page.HasOlder; entry.Error = null;
            await SaveAsync(entry);
        }
        finally { entry.Gate.Release(); Notify(entry); }
    }

    internal static async Task QueueAsync(Entry entry, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 8000) throw new IOException("每条留言最多 8000 字，请分条发送。");
        await entry.Gate.WaitAsync();
        try
        {
            entry.Outbox.Add(new(Guid.NewGuid().ToString("N"), text, DateTimeOffset.UtcNow));
            await SaveAsync(entry); // Keep the same id across connection loss or application restart.
            try { await FlushCoreAsync(entry); entry.Error = null; }
            catch (Exception ex) { entry.Error = "留言已保存在手机，等待重试：" + ex.Message; }
            await SaveAsync(entry);
        }
        finally { entry.Gate.Release(); Notify(entry); }
    }

    internal static async Task MarkReadAsync(Entry entry, long through)
    {
        if (through <= entry.ControllerReadThrough || through <= 0) return;
        await entry.Gate.WaitAsync();
        try
        {
            through = Math.Min(through, entry.ReceivedThrough);
            if (through <= entry.ControllerReadThrough) return;
            await entry.Client.MarkReadAsync(through);
            entry.ControllerReadThrough = Math.Max(entry.ControllerReadThrough, through);
            var status = await entry.Client.StatusAsync();
            entry.ServerUnread = status.Unread;
            entry.AgentReadThrough = status.AgentReadThrough;
            entry.ControllerReadThrough = Math.Max(entry.ControllerReadThrough, status.ControllerReadThrough);
            await SaveAsync(entry);
        }
        finally { entry.Gate.Release(); Notify(entry); }
    }

    private static async Task FlushCoreAsync(Entry entry)
    {
        foreach (var pending in entry.Outbox.ToArray())
        {
            var message = await entry.Client.SendAsync(pending.Text, pending.Id);
            Merge(entry, [message]); entry.Outbox.Remove(pending);
            await SaveAsync(entry);
        }
    }
    private static void Apply(Entry entry, FurtherAgent.Core.MailboxPage page)
    {
        Merge(entry, page.Messages);
        entry.ReceivedThrough = Math.Max(entry.ReceivedThrough, page.Messages.LastOrDefault()?.Sequence ?? 0);
        entry.AgentReadThrough = page.AgentReadThrough;
        entry.ServerUnread = page.Unread;
        entry.ControllerReadThrough = Math.Max(entry.ControllerReadThrough, page.ControllerReadThrough);
    }
    private static void Merge(Entry entry, IEnumerable<MailboxMessage> messages)
    {
        var merged = entry.Messages.Concat(messages).GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.Sequence).ToArray();
        entry.Messages.Clear(); entry.Messages.AddRange(merged);
    }
    private static async Task SaveAsync(Entry entry)
    {
        string path = CachePath(entry.Profile.Id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Cached(entry.Messages, entry.Outbox,
            entry.AgentReadThrough, entry.ControllerReadThrough, entry.NotifiedThrough, entry.HasOlder, entry.ReceivedThrough, entry.ServerUnread), Json.Options));
        File.Move(temporary, path, true);
    }
    private static void Notify(Entry entry) => MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke(entry.Profile.Id));
}
