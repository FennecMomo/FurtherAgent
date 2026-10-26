using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private readonly ConversationHistory fallbackHistory = new();
    private ConversationHistory History => activeDevice?.History ?? fallbackHistory;
    private bool historyBusy, restoringScroll;
    private void InitializeHistory()
    {
        ChatScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(ChatHistoryWheel), true);
        ChatScroll.ScrollChanged += async (_, e) =>
        {
            if (!restoringScroll && e.VerticalChange < 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && ChatScroll.VerticalOffset <= 50)
                await LoadOlderHistoryAsync();
        };
    }
    private void UpdateHistoryActions()
    {
        bool same = current is not null && History.Displayed?.Id == selectedId;
        OlderHistoryButton.Visibility = same && History.HasOlder ? Visibility.Visible : Visibility.Collapsed;
        OlderHistoryButton.IsEnabled = !historyBusy && remote?.IsConnected == true;
        OlderHistoryButton.Content = historyBusy ? "正在加载更早记录…" : "加载更早记录";
        LatestHistoryButton.Visibility = same && History.ReadingOlder ? Visibility.Visible : Visibility.Collapsed;
    }
    private static DependencyObject? HistoryParent(DependencyObject item) => item is FrameworkContentElement content
        ? content.Parent : item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : null;
    private async void ChatHistoryWheel(object sender, MouseWheelEventArgs e)
    {
        // RichTextBox consumes wheel events even when its own scrollbars are disabled.
        // Preserve real nested tool/code scrolling until that viewer reaches its boundary.
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != ChatScroll; node = HistoryParent(node))
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0 &&
                (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight)) return;
        e.Handled = true;
        ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset - e.Delta);
        if (e.Delta > 0 && ChatScroll.VerticalOffset <= 50) await LoadOlderHistoryAsync();
    }
    private async void OlderHistory_Click(object sender, RoutedEventArgs e) => await LoadOlderHistoryAsync();
    private void LatestHistory_Click(object sender, RoutedEventArgs e)
    {
        if (History.ShowLatest() is { } view && view.Id == selectedId)
        { RenderSnapshot(view, true); ChatScroll.UpdateLayout(); ChatScroll.ScrollToEnd(); }
    }
    private async Task LoadOlderHistoryAsync()
    {
        if (historyBusy || restoringScroll || remote?.IsConnected != true || current?.Id != selectedId ||
            History.Displayed is not { History: { Start: > 0 } cursor } view || view.Id != selectedId) return;
        var connection = remote; var reader = History; int version = selectionVersion;
        historyBusy = true; UpdateHistoryActions();
        try
        {
            if (reader.Latest?.History?.Revision != cursor.Revision)
                throw new IOException("会话历史已更新，请点击「返回最新」后重新加载；当前阅读位置已保留。");
            var page = await OperationGroups.LoadBatchAsync(view.Id, cursor, async before =>
            {
                if (remote != connection || version != selectionVersion || selectedId != view.Id) throw new OperationCanceledException();
                return Json.Read<HistoryPage>(await SessionHistory.ReadAsync(connection.Peer, "history", new { session = view.Id, before, revision = cursor.Revision }));
            });
            if (remote != connection || version != selectionVersion || selectedId != view.Id || History != reader) return;
            PrependHistory(page);
        }
        catch (Exception ex)
        {
            if (remote == connection && version == selectionVersion) StatusBar.Text = "历史加载未完成：" + ex.Message;
        }
        finally { historyBusy = false; UpdateHistoryActions(); }
    }
    private void PrependHistory(HistoryPage page)
    {
        // Anchor an existing message, rather than the total extent: the bounded window
        // can discard its tail and async images may change sizes below the viewport.
        var anchor = Messages.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        double anchorY = anchor?.TranslatePoint(new Point(), ChatScroll).Y ?? 0;
        double offset = ChatScroll.VerticalOffset, extent = ChatScroll.ExtentHeight;
        var view = History.Prepend(page);
        restoringScroll = true;
        try
        {
            RenderSnapshot(view, true); ChatScroll.UpdateLayout();
            var moved = Messages.ItemContainerGenerator.ContainerFromIndex(OperationGroups.Group(page.Messages).Count) as FrameworkElement;
            double delta = moved is null ? ChatScroll.ExtentHeight - extent : moved.TranslatePoint(new Point(), ChatScroll).Y - anchorY;
            ChatScroll.ScrollToVerticalOffset(offset + delta); ChatScroll.UpdateLayout();
        }
        finally { restoringScroll = false; }
    }
}
