using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

public sealed partial class MainPage
{
    private bool openingMailbox;
    private void UpdateMailboxBadge()
    {
        if (mailboxButton is null) return;
        int count = MailboxRuntime.TotalUnread;
        string label = count > 0 ? $"留言 · {count} 条未读" : "留言";
        if (mailboxButton.Text != label) mailboxButton.Text = label;
    }
    internal async Task OpenMailboxAsync(string? profileId = null)
    {
        if (closed || openingMailbox) return;
        openingMailbox = true;
        try
        {
            var saved = await SavedConnections.LoadAsync();
            if (saved.Connections.Count == 0)
            {
                await DisplayAlertAsync("留言", "先在连接管理中保存家里的设备。首次绑定留言箱时接入端需在线，之后对方离线也能留言。", "知道了");
                return;
            }
            var selected = saved.Connections.FirstOrDefault(p => p.Id == profileId);
            if (selected is null)
            {
                var labels = saved.Connections.Select((p, i) => $"{i + 1}. {p.Name}").ToArray();
                string choice = await DisplayActionSheetAsync("选择留言对象", "取消", null, labels);
                int index = Array.IndexOf(labels, choice); if (index < 0) return;
                selected = saved.Connections[index];
            }
            CloseDrawer();
            await Navigation.PushAsync(new MailboxPage(selected));
        }
        catch (Exception ex) { await ErrorAsync(ex); }
        finally { openingMailbox = false; }
    }

    internal async Task OpenPendingMailboxAsync()
    {
        if (CompletionNotifications.PendingMailbox is not { } id || !CompletionNotifications.Foreground ||
            closed || suspended || openingMailbox || Navigation.ModalStack.Count > 0) return;
        if (MailboxRuntime.ViewingProfile == id)
        { CompletionNotifications.ClearMailboxPending(id); return; }
        // This route uses the saved mailbox capability even when its realtime Host is offline.
        CompletionNotifications.ClearMailboxPending(id);
        await OpenMailboxAsync(id);
    }
}
