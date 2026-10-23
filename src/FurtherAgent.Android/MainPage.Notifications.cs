using FurtherAgent.Core;
using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

public sealed partial class MainPage
{
    private bool openingCompletion;
    private DateTime nextCompletionAttempt;
    private CompletionNotifications.Target? attemptedCompletion;

    internal bool IsViewingCompletion(string connectionId, string sessionId) =>
        !closed && !suspended && !preview && !openingCompletion
        && activeConnection == connectionId && session.View?.Id == sessionId
        && Navigation.NavigationStack.LastOrDefault() == this && Navigation.ModalStack.Count == 0
        && !drawerLayer.IsVisible;

    private async Task OpenPendingCompletionAsync()
    {
        if (CompletionNotifications.Pending is not { } pending || !CompletionNotifications.Foreground
            || closed || suspended || openingCompletion || interactionBusy || connectingDevice
            || restoringConnections || session.Busy || Navigation.ModalStack.Count > 0
            || ReferenceEquals(attemptedCompletion, pending) && DateTime.UtcNow < nextCompletionAttempt) return;
        attemptedCompletion = pending;
        openingCompletion = true; interactionBusy = true; RefreshButtons();
        try
        {
            if (!connections.Entries.TryGetValue(pending.ConnectionId, out var target))
            {
                var saved = await SavedConnections.LoadAsync();
                var profile = saved.Connections.FirstOrDefault(p => DeviceConnectionPool<MobileSession>.Identity(p.Server, p.Code) == pending.ConnectionId);
                if (profile is null)
                {
                    CompletionNotifications.Clear(pending);
                    status.Text = "通知对应的设备连接已移除，请在连接管理中重新连接。";
                    return;
                }
                // Tapping a notification is an explicit request to reopen this saved connection.
                StartConnectionService();
                await ConnectionRuntime.ConnectAsync(profile.Server, profile.Code, profile.Name, null);
                connectionNames[pending.ConnectionId] = profile.Name;
                if (!connections.Entries.TryGetValue(pending.ConnectionId, out target)) return;
            }
            if (!target.Connected || target.Busy)
            {
                nextCompletionAttempt = DateTime.UtcNow.AddSeconds(5);
                status.Text = "正在等待通知对应的设备连接恢复…";
                return;
            }
            if (!ReferenceEquals(CompletionNotifications.Pending, pending) || suspended || closed) return;
            SaveDraft();
            ActivateConnection(pending.ConnectionId, target);
            await Navigation.PopToRootAsync(false);
            CloseDrawer();
            await SelectAsync(pending.SessionId);
            CompletionNotifications.Clear(pending);
            nextCompletionAttempt = DateTime.MinValue;
        }
        catch
        {
            nextCompletionAttempt = DateTime.UtcNow.AddSeconds(20);
            status.Text = "暂时无法打开通知对应的会话，连接恢复后会自动重试。";
        }
        finally { openingCompletion = false; interactionBusy = false; RefreshButtons(); }
    }
}
