using System.Windows;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public sealed record CompletionNotificationServices(Func<bool> IsForeground, Action<CompletionToastWindow> ShowToast, Action? ActivateWindow = null);

public partial class MainWindow
{
    private readonly CompletionNotificationServices? completionServices;
    private readonly List<CompletionToastWindow> completionToasts = [];
    private readonly HashSet<string> seenCompletions = [];
    private readonly Queue<string> completionOrder = new();

    private void HandleCompletion(DeviceContext device, SessionCompletion completion)
    {
        if (closing || exitRequested || !deviceConnections.Entries.Values.Contains(device)) return;
        string key = DeviceConnectionPool<DeviceContext>.Identity(device.Server, device.Code) + ":" + completion.EventId;
        if (!seenCompletions.Add(key)) return;
        completionOrder.Enqueue(key);
        if (completionOrder.Count > 512) seenCompletions.Remove(completionOrder.Dequeue());
        bool foreground = completionServices?.IsForeground() ?? (IsVisible && IsActive && WindowState != WindowState.Minimized);
        bool viewing = activeDevice == device && selectedId == completion.SessionId && ConversationPage.Visibility == Visibility.Visible
            && new[] { ConnectOverlay, NewOverlay, ModelOverlay, FilesOverlay, WorkspaceOverlay }.All(p => p.Visibility != Visibility.Visible);
        if (foreground && viewing) return;
        string title = string.IsNullOrWhiteSpace(completion.Title) ? "会话执行完成" : completion.Title;
        var toast = new CompletionToastWindow(title, device.Name + " · 执行完成");
        completionToasts.Add(toast);
        toast.Loaded += (_, _) => PositionCompletionToasts();
        toast.Closed += (_, _) => { completionToasts.Remove(toast); PositionCompletionToasts(); };
        toast.OpenRequested += async (_, _) =>
        {
            if (await OpenCompletedSessionAsync(device, completion)) toast.Close();
        };
        // Match ResourceManager's toast and stacking; bound bursts to the visible working area.
        int capacity = Math.Max(1, (int)((SystemParameters.WorkArea.Height - 32) / 122));
        while (completionToasts.Count > capacity)
        {
            var oldest = completionToasts[0]; completionToasts.RemoveAt(0); oldest.Close();
        }
        if (completionServices is { } services) services.ShowToast(toast); else toast.Show();
    }
    private void PositionCompletionToasts()
    {
        var workArea = SystemParameters.WorkArea;
        double bottom = workArea.Bottom - 16;
        foreach (var toast in completionToasts.Where(t => t.IsLoaded).Reverse())
        {
            toast.Left = workArea.Right - toast.ActualWidth - 16;
            toast.Top = Math.Max(workArea.Top + 16, bottom - toast.ActualHeight);
            bottom = toast.Top - 10;
        }
    }
    private async Task<bool> OpenCompletedSessionAsync(DeviceContext device, SessionCompletion completion)
    {
        if (closing || exitRequested) return false;
        if (completionServices?.ActivateWindow is { } activate) activate(); else RestoreFromTray();
        if (!CanSwitchDevice || !ConnectButton.IsEnabled)
        { StatusBar.Text = "当前操作尚未结束，请稍后打开完成的会话。"; return false; }
        if (!deviceConnections.Entries.Values.Contains(device))
        { StatusBar.Text = "该设备已经断开，请重新连接后打开会话。"; return false; }
        ActivateDevice(device);
        SaveDraft();
        if (selectedId != completion.SessionId) ClearAttachments();
        selectedId = completion.SessionId; current = null; promptSignature = null;
        Composer.Text = drafts.GetValueOrDefault(selectedId, "");
        int version = ++selectionVersion; SetSessionEnabled(false);
        SessionTitle.Text = completion.Title; SessionMeta.Text = "正在打开已完成的会话…";
        try
        {
            var view = Json.Read<SessionView>(await SessionHistory.ReadAsync(device.Remote.Peer, "attach", new { id = completion.SessionId }));
            if (activeDevice != device || version != selectionVersion) return false;
            Render(view); Filter(); return true;
        }
        catch (Exception ex)
        {
            if (activeDevice == device && version == selectionVersion) { selectedId = null; Error(ex); }
            return false;
        }
    }
    private void CloseCompletionToasts()
    {
        foreach (var toast in completionToasts.ToArray()) toast.Close();
        completionToasts.Clear();
    }
}
