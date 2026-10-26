using System.IO;
using System.Windows;
using System.Windows.Threading;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private readonly MailboxVault mailboxVault = new(Path.Combine(Path.GetDirectoryName(SettingsFile)!, "mailboxes-v1"));
    private readonly Dictionary<string, DesktopMailboxSession> mailboxSessions = [];
    private DispatcherTimer? mailboxTimer;
    private MailboxWindow? mailboxWindow;
    private bool pollingMailboxes;

    private void InitializeMailbox()
    {
        foreach (var saved in mailboxVault.Load()) AddMailboxSession(saved);
        mailboxTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(12) };
        mailboxTimer.Tick += async (_, _) => await PollMailboxesAsync();
        mailboxTimer.Start();
        _ = PollMailboxesAsync();
    }

    internal IReadOnlyList<SavedConnection> MailboxProfiles()
    {
        List<SavedConnection> profiles;
        try { profiles = connectionStore.List().ToList(); }
        catch (Exception) { profiles = mailboxSessions.Values.Select(s => new SavedConnection(s.State.Server, s.State.Binding.Code, s.State.Name, false)).ToList(); }
        if (activeDevice is { } device && !profiles.Any(p => MailboxVault.Key(p.Server, p.Code) == MailboxVault.Key(device.Server, device.Code)))
            profiles.Add(new(device.Server, device.Code, device.Name, false));
        return profiles;
    }

    internal DesktopMailboxSession? FindMailbox(SavedConnection profile) => mailboxSessions.GetValueOrDefault(MailboxVault.Key(profile.Server, profile.Code));

    internal async Task<DesktopMailboxSession> BindMailboxAsync(SavedConnection profile)
    {
        var binding = await MailboxClient.PairAsync(profile.Server, profile.Code);
        string key = MailboxVault.Key(profile.Server, profile.Code);
        var previous = mailboxSessions.GetValueOrDefault(key);
        var state = previous is null ? new MailboxLocalState(profile.Server, profile.Name, binding, [], [])
            : previous.State with { Binding = binding, Name = profile.Name };
        mailboxVault.Save(state);
        previous?.Dispose();
        return AddMailboxSession(state);
    }

    private DesktopMailboxSession AddMailboxSession(MailboxLocalState saved)
    {
        var session = new DesktopMailboxSession(mailboxVault, saved);
        mailboxSessions[session.Key] = session;
        session.Changed += UpdateMailboxBadge;
        session.Received += message => ShowMailboxToast(session, message);
        return session;
    }

    private async Task PollMailboxesAsync()
    {
        if (pollingMailboxes || closing || exitRequested) return;
        pollingMailboxes = true;
        try
        {
            foreach (var session in mailboxSessions.Values.ToArray())
            {
                if (closing || exitRequested) break;
                await session.RefreshAsync();
            }
            UpdateMailboxBadge();
        }
        finally { pollingMailboxes = false; }
    }

    private void UpdateMailboxBadge()
    {
        int unread = mailboxSessions.Values.Sum(s => s.State.ServerUnread ?? s.State.Messages.Count(m => m.Sender == "agent" && m.Sequence > s.State.ControllerReadThrough));
        MailboxButton.Content = unread == 0 ? "留言" : $"留言 · {unread}";
    }

    private void Mailbox_Click(object sender, RoutedEventArgs e) => OpenMailbox();
    private void OpenMailbox(SavedConnection? profile = null)
    {
        if (closing || exitRequested) return;
        if (mailboxWindow is null)
        {
            mailboxWindow = new MailboxWindow(this);
            mailboxWindow.Closed += (_, _) => mailboxWindow = null;
        }
        if (profile is not null) mailboxWindow.RefreshProfiles(profile);
        else if (!mailboxWindow.IsVisible) mailboxWindow.RefreshProfiles(null);
        mailboxWindow.Show();
        if (mailboxWindow.WindowState == WindowState.Minimized) mailboxWindow.WindowState = WindowState.Normal;
        mailboxWindow.Activate();
    }

    private void ShowMailboxToast(DesktopMailboxSession session, MailboxMessage message)
    {
        if (closing || exitRequested) return;
        bool viewing = mailboxWindow is { IsVisible: true, IsActive: true } window && window.ActiveSession == session;
        if (viewing) return;
        string preview = message.Text.Replace('\r', ' ').Replace('\n', ' ');
        var toast = new CompletionToastWindow(session.State.Name + " · 新留言", preview, "收到新留言", "点击打开留言");
        completionToasts.Add(toast);
        toast.Loaded += (_, _) => PositionCompletionToasts();
        toast.Closed += (_, _) => { completionToasts.Remove(toast); PositionCompletionToasts(); };
        toast.OpenRequested += (_, _) =>
        {
            var profile = MailboxProfiles().FirstOrDefault(p => MailboxVault.Key(p.Server, p.Code) == session.Key);
            if (profile is not null) OpenMailbox(profile);
            toast.Close();
        };
        int capacity = Math.Max(1, (int)((SystemParameters.WorkArea.Height - 32) / 122));
        while (completionToasts.Count > capacity) { var oldest = completionToasts[0]; completionToasts.RemoveAt(0); oldest.Close(); }
        if (completionServices is { } services) services.ShowToast(toast); else toast.Show();
    }

    private void CloseMailboxes()
    {
        mailboxTimer?.Stop();
        mailboxWindow?.Close(); mailboxWindow = null;
        foreach (var session in mailboxSessions.Values) session.Dispose();
        mailboxSessions.Clear();
    }
}
