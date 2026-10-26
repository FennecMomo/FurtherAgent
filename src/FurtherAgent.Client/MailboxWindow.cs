using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

// A separate inbox: every action here targets mailbox HTTP endpoints, never Agent send/steer.
internal sealed class MailboxWindow : Window
{
    private readonly MainWindow owner;
    private readonly ComboBox profiles = new() { DisplayMemberPath = nameof(SavedConnection.Name), MinWidth = 190, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
    private readonly StackPanel messages = new();
    private readonly ScrollViewer scroll;
    private readonly TextBox composer = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 180,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10), FontSize = 14 };
    private readonly Button send = new() { Content = "发送留言", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button older = new() { Content = "加载更早留言", Margin = new Thickness(0, 6, 0, 12), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button bind = new() { Content = "启用留言", Padding = new Thickness(10, 5, 10, 5) };
    private bool editingDraft, busy, closed, markingRead;
    private string fingerprint = "";
    public DesktopMailboxSession? ActiveSession { get; private set; }

    public MailboxWindow(MainWindow owner)
    {
        this.owner = owner;
        Title = "留言 · FurtherAgent"; Width = 820; Height = 780; MinWidth = 560; MinHeight = 500;
        FontFamily = new FontFamily("Microsoft YaHei UI"); Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "留言", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 20, 0) });
        var refresh = new Button { Content = "刷新", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(refresh, Dock.Right); header.Children.Add(refresh);
        DockPanel.SetDock(bind, Dock.Right); header.Children.Add(bind); header.Children.Add(profiles);
        root.Children.Add(header);
        var meta = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        meta.Children.Add(new TextBlock { Text = "先留在这里，等对方方便时再查看和回复。", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) });
        meta.Children.Add(state); Grid.SetRow(meta, 1); root.Children.Add(meta);
        var history = new StackPanel(); history.Children.Add(older); history.Children.Add(messages);
        scroll = new ScrollViewer { Content = history, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var input = new DockPanel(); DockPanel.SetDock(send, Dock.Right); input.Children.Add(send); input.Children.Add(composer); footer.Children.Add(input);
        footer.Children.Add(new TextBlock { Text = "Ctrl+Enter 发送 · Enter 换行 · 支持系统听写输入", FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) });
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        bind.Click += async (_, _) => await RunAsync(BindAsync);
        refresh.Click += async (_, _) => await RunAsync(async () => { if (ActiveSession is { } session) await session.RefreshAsync(); else await BindAsync(); });
        older.Click += async (_, _) => await RunAsync(async () =>
        {
            if (ActiveSession is { } session)
            {
                double height = scroll.ExtentHeight, offset = scroll.VerticalOffset;
                await session.LoadOlderAsync();
                await Dispatcher.InvokeAsync(() => scroll.ScrollToVerticalOffset(offset + scroll.ExtentHeight - height), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        });
        send.Click += async (_, _) => await SendAsync();
        composer.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && send.IsEnabled) { e.Handled = true; await SendAsync(); } };
        composer.TextChanged += (_, _) => { if (!editingDraft && ActiveSession is { } session) session.SetDraft(composer.Text); };
        profiles.SelectionChanged += async (_, _) => await SelectProfileAsync();
        Activated += async (_, _) => await MarkVisibleReadAsync();
        Closed += (_, _) => { closed = true; if (ActiveSession is { } session) session.Changed -= OnChanged; };
        RefreshProfiles(null);
        RefreshActions();
    }

    public void RefreshProfiles(SavedConnection? preferred)
    {
        var choices = owner.MailboxProfiles();
        string? currentKey = preferred is null && profiles.SelectedItem is SavedConnection selected ? MailboxVault.Key(selected.Server, selected.Code)
            : preferred is null ? null : MailboxVault.Key(preferred.Server, preferred.Code);
        profiles.ItemsSource = choices;
        profiles.SelectedItem = choices.FirstOrDefault(p => MailboxVault.Key(p.Server, p.Code) == currentKey)
            ?? choices.FirstOrDefault(p => p.Name.Contains("DSH", StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
    }

    private async Task SelectProfileAsync()
    {
        if (ActiveSession is { } previous) previous.Changed -= OnChanged;
        ActiveSession = profiles.SelectedItem is SavedConnection profile ? owner.FindMailbox(profile) : null;
        if (ActiveSession is { } session) session.Changed += OnChanged;
        editingDraft = true; composer.Text = ActiveSession?.State.Draft ?? ""; editingDraft = false;
        fingerprint = ""; Render();
        if (ActiveSession is { } current) await RunAsync(current.RefreshAsync);
    }

    private async Task BindAsync()
    {
        if (profiles.SelectedItem is not SavedConnection profile) throw new InvalidOperationException("先在 FurtherAgent 保存一个连接，再来启用留言。");
        state.Text = "正在绑定留言收件箱…";
        var session = await owner.BindMailboxAsync(profile);
        if (ActiveSession is { } old) old.Changed -= OnChanged;
        ActiveSession = session; session.Changed += OnChanged;
        editingDraft = true; composer.Text = session.State.Draft; editingDraft = false;
        fingerprint = ""; Render(); await session.RefreshAsync();
    }

    private async Task SendAsync()
    {
        if (ActiveSession is not { } session || string.IsNullOrWhiteSpace(composer.Text)) return;
        string text = composer.Text;
        if (text.Length > 8000) { state.Text = "每条留言最多 8000 字符，请拆成几条发送。"; return; }
        await RunAsync(async () =>
        {
            editingDraft = true; composer.Clear(); editingDraft = false;
            await session.SendAsync(text);
            scroll.ScrollToEnd();
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || closed) return;
        busy = true; RefreshActions();
        try { await action(); }
        catch (Exception ex) { if (!closed) state.Text = ex.Message; }
        finally { busy = false; RefreshActions(); }
    }

    private void OnChanged()
    {
        if (closed) return;
        Render(); _ = MarkVisibleReadAsync();
    }

    private async Task MarkVisibleReadAsync()
    {
        if (closed || markingRead || !IsVisible || !IsActive || WindowState == WindowState.Minimized || ActiveSession is not { } session) return;
        markingRead = true;
        try { await session.MarkReadAsync(); }
        catch (Exception ex) { if (!closed) state.Text = ex.Message; }
        finally { markingRead = false; }
    }

    private void Render()
    {
        RefreshActions();
        if (ActiveSession is not { } session)
        {
            messages.Children.Clear(); older.Visibility = Visibility.Collapsed;
            state.Text = profiles.SelectedItem is null ? "没有保存的连接。" : "首次启用时请让家里的接入端在线；绑定后，对方离线也可以留言。";
            return;
        }
        var saved = session.State;
        state.Text = session.LastError ?? (saved.Pending.Count > 0 ? $"{saved.Pending.Count} 条待发送，网络恢复后会继续投递。" : "留言已保存到服务器，等待对方方便时查收。");
        older.Visibility = saved.HasOlder ? Visibility.Visible : Visibility.Collapsed;
        string next = Json.Node(new { saved.Messages, saved.Pending, saved.AgentReadThrough }).ToJsonString();
        if (next == fingerprint) return;
        fingerprint = next;
        bool bottom = scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 50;
        messages.Children.Clear();
        foreach (var item in saved.Messages)
        {
            bool mine = item.Sender == "controller";
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = (mine ? "你" : "对方") + " · " + item.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(mine ? 60 : 0, 5, 0, 4) });
            var bubble = new ChatMessageView { DataContext = new ChatEntry(mine ? "user" : "assistant", item.Text) };
            VisualTheme.SetIsCodex(bubble, true); row.Children.Add(bubble);
            if (mine) row.Children.Add(new TextBlock { Text = item.Sequence <= saved.AgentReadThrough ? "对方已读" : "已存入留言箱", FontSize = 11,
                Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -5, 12, 8) });
            messages.Children.Add(row);
        }
        foreach (var pending in saved.Pending)
        {
            messages.Children.Add(new ChatMessageView { DataContext = new ChatEntry("user", pending.Text) });
            messages.Children.Add(new TextBlock { Text = "待发送 · 正在等待服务器确认", Foreground = Brushes.DarkOrange, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -5, 12, 8) });
        }
        if (saved.Messages.Count == 0 && saved.Pending.Count == 0)
            messages.Children.Add(new TextBlock { Text = "写下第一条留言吧。", HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(0, 70, 0, 0) });
        if (bottom) Dispatcher.BeginInvoke(() => scroll.ScrollToEnd());
    }

    private void RefreshActions()
    {
        send.IsEnabled = !busy && ActiveSession is not null;
        composer.IsEnabled = ActiveSession is not null && !busy;
        older.IsEnabled = !busy && ActiveSession is not null;
        bind.IsEnabled = !busy && profiles.SelectedItem is not null;
        bind.Content = ActiveSession is null ? "启用留言" : "重新绑定";
        profiles.IsEnabled = !busy;
    }
}
