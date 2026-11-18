using FurtherAgent.Core;

namespace FurtherAgent.AndroidApp;

public sealed partial class MainPage
{
    private Button deviceButton = null!;
    private Button mailboxButton = null!;
    private Grid drawerLayer = null!;
    private Border drawerPanel = null!;
    private CollectionView drawerItems = null!;
    private SearchBar drawerSearch = null!;
    private Button drawerDevice = null!;
    private IReadOnlyList<DrawerRow> drawerRows = [];
    private IReadOnlyList<SessionInfo>? drawerCatalog;
    private (string? Connection, string? Selected, string Query) drawerSelection;
    private readonly HashSet<string> collapsedWorkspaces = [];

    private abstract record DrawerRow;
    private sealed record WorkspaceRow(string Key, string Path, string Title, bool Folded) : DrawerRow;
    private sealed record ConversationRow(SessionInfo Session, bool Active) : DrawerRow;
    private sealed class DrawerTemplates(DataTemplate workspace, DataTemplate conversation) : DataTemplateSelector
    {
        protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
            item is WorkspaceRow ? workspace : conversation;
    }

    private static Button TextAction(string text, Func<Task> action)
    {
        var button = new Button { Text = text, BackgroundColor = Colors.Transparent, FontSize = 14,
            Padding = new Thickness(8, 0), MinimumHeightRequest = 44, CornerRadius = 12 };
        button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#202428"), Colors.White);
        Ui.AddFeedback(button);
        button.Clicked += async (_, _) => await action();
        return button;
    }
    private static Button StyleModelButton(Button button)
    {
        button.ImageSource = null; button.Text = "模型 · 思考强度 ▾";
        button.WidthRequest = -1; button.HorizontalOptions = LayoutOptions.Fill;
        button.BackgroundColor = Colors.Transparent; button.Padding = new Thickness(0); button.FontSize = 12;
        button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#202428"), Color.FromArgb("#E4ECE6"));
        AlignStart(button);
        return button;
    }
    private static void AlignStart(Button button) => button.HandlerChanged += (_, _) =>
    {
        if (button.Handler?.PlatformView is global::Android.Widget.Button native)
        {
            native.Gravity = global::Android.Views.GravityFlags.Start | global::Android.Views.GravityFlags.CenterVertical;
            native.SetSingleLine(true);
            native.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        }
    };
    private View CreateDrawerHost(View chat)
    {
        var root = new Grid(); root.Add(chat);
        drawerLayer = new Grid { IsVisible = false, ZIndex = 10 };
        var shade = new BoxView { Color = Color.FromArgb("#66000000") };
        var dismiss = new TapGestureRecognizer(); dismiss.Tapped += (_, _) => CloseDrawer(); shade.GestureRecognizers.Add(dismiss);
        drawerLayer.Add(shade);
        drawerDevice = TextAction("连接设备 ▾", async () => { await SwitchDeviceAsync(); if (drawerLayer.IsVisible) RenderDrawer(); });
        drawerDevice.HorizontalOptions = LayoutOptions.Fill;
        drawerDevice.FontAttributes = FontAttributes.Bold;
        drawerDevice.Padding = new Thickness(12, 0);
        drawerDevice.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#EAF1EC"), Color.FromArgb("#233A30"));
        AlignStart(drawerDevice);
        drawerSearch = new SearchBar { Placeholder = "搜索标题或工作区", FontSize = 14, HeightRequest = 48 };
        drawerSearch.SetAppThemeColor(SearchBar.TextColorProperty, Color.FromArgb("#202428"), Color.FromArgb("#E4ECE6"));
        drawerSearch.SetAppThemeColor(SearchBar.PlaceholderColorProperty, Color.FromArgb("#63756B"), Color.FromArgb("#ACBCB3"));
        drawerSearch.TextChanged += (_, _) => RenderDrawer();
        drawerItems = new CollectionView { SelectionMode = SelectionMode.None, ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemTemplate = new DrawerTemplates(new DataTemplate(CreateWorkspaceRow), new DataTemplate(CreateConversationRow)) };
        mailboxButton = TextAction("留言", () => OpenMailboxAsync());
        var shortcuts = new Grid { ColumnSpacing = 8, ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)] };
        var create = TextAction("新建会话", NewConversationAsync);
        create.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#E7F1EB"), Color.FromArgb("#264637"));
        shortcuts.Add(create); shortcuts.Add(mailboxButton, 1);
        var top = new VerticalStackLayout { Spacing = 6, Children = { drawerDevice, drawerSearch, shortcuts } };
        var refresh = Ui.Button("刷新", async () => { await Act(session.RefreshAsync); RenderDrawer(); });
        var bottom = new Grid { ColumnSpacing = 4, ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Star)] };
        bottom.Add(refresh);
        bottom.Add(TextAction("连接管理", async () => { CloseDrawer(); await OpenConnectionAsync(); }), 1);
        bottom.Add(TextAction("设置与更多", MoreAsync), 2);
        drawerSearch.Focused += (_, _) => bottom.IsVisible = false;
        drawerSearch.Unfocused += (_, _) => bottom.IsVisible = true;
        var content = new Grid { Padding = new Thickness(12, 12, 12, 16), RowSpacing = 6,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        content.Add(top); content.Add(drawerItems, 0, 1); content.Add(bottom, 0, 2);
        drawerPanel = new Border { HorizontalOptions = LayoutOptions.Start, StrokeThickness = 0, Content = content };
        drawerPanel.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#101C17"));
        drawerLayer.Add(drawerPanel); root.Add(drawerLayer);
        root.SizeChanged += (_, _) =>
        {
            if (root.Width > 0) drawerPanel.WidthRequest = Math.Min(372, root.Width * .9);
        };
        return root;
    }
    private void OpenDrawer() { RenderDrawer(); drawerLayer.IsVisible = true; }
    internal void ShowSessionDrawer() => OpenDrawer();
    private void CloseDrawer() { drawerSearch.Unfocus(); drawerLayer.IsVisible = false; }
    protected override bool OnBackButtonPressed()
    {
        if (drawerLayer.IsVisible) { CloseDrawer(); return true; }
        return base.OnBackButtonPressed();
    }
    private void RenderDrawer()
    {
        UpdateMailboxBadge();
        string device = session.HasConnection ? $"{connectionNames.GetValueOrDefault(activeConnection ?? "", session.DeviceName)} · {session.Indicator.Label} ▾" : "连接设备 ▾";
        if (drawerDevice.Text != device) drawerDevice.Text = device;
        string query = drawerSearch.Text?.Trim() ?? "";
        var selection = (activeConnection, session.View?.Id, query);
        string empty = !session.Connected ? "连接设备后查看会话" : query.Length == 0 ? "还没有会话" : "没有匹配的会话";
        if (drawerItems.EmptyView is not Label hint || hint.Text != empty)
        {
            var emptyLabel = Ui.Text(empty, 14);
            emptyLabel.Margin = new Thickness(12, 18);
            Ui.TextTheme(emptyLabel);
            drawerItems.EmptyView = emptyLabel;
        }
        if (ReferenceEquals(drawerCatalog, session.Sessions) && selection == drawerSelection) return;
        drawerCatalog = session.Sessions;
        drawerSelection = selection;
        var nextRows = new List<DrawerRow>();
        var groups = session.Sessions.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Cwd.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.SortTime).ThenBy(s => s.Id).GroupBy(s => s.Cwd);
        foreach (var group in groups)
        {
            string key = (activeConnection ?? "") + "|" + group.Key;
            bool folded = query.Length == 0 && collapsedWorkspaces.Contains(key);
            string name = string.IsNullOrEmpty(group.Key) ? "最近" : group.Key.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
            nextRows.Add(new WorkspaceRow(key, group.Key, name, folded));
            if (folded) continue;
            foreach (var item in group)
                nextRows.Add(new ConversationRow(item, item.Id == session.View?.Id));
        }
        if (!drawerRows.SequenceEqual(nextRows)) drawerItems.ItemsSource = drawerRows = nextRows;
    }
    private View CreateWorkspaceRow()
    {
        Button header = null!;
        header = TextAction("", () =>
        {
            if (header.BindingContext is WorkspaceRow row && string.IsNullOrWhiteSpace(drawerSearch.Text))
            {
                if (!collapsedWorkspaces.Add(row.Key)) collapsedWorkspaces.Remove(row.Key);
                drawerCatalog = null;
                RenderDrawer();
            }
            return Task.CompletedTask;
        });
        header.HorizontalOptions = LayoutOptions.Fill;
        header.FontAttributes = FontAttributes.Bold;
        header.FontSize = 12;
        header.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#52685C"), Color.FromArgb("#ACBCB3"));
        AlignStart(header);
        header.BindingContextChanged += (_, _) =>
        {
            if (header.BindingContext is not WorkspaceRow row) return;
            header.Text = $"{(row.Folded ? "›" : "⌄")}   {row.Title}";
            SemanticProperties.SetDescription(header, $"{row.Title}，{(row.Folded ? "展开" : "折叠")}工作区");
            SemanticProperties.SetHint(header, row.Path);
            ToolTipProperties.SetText(header, row.Path);
        };
        return header;
    }
    private View CreateConversationRow()
    {
        Button button = null!;
        button = TextAction("", async () =>
        {
            if (button.BindingContext is not ConversationRow row || interactionBusy || session.Busy || connectingDevice) return;
            await Act(async () => { await SelectAsync(row.Session.Id); CloseDrawer(); });
        });
        button.HorizontalOptions = LayoutOptions.Fill;
        button.MinimumHeightRequest = 48;
        AlignStart(button);
        var time = Ui.Text("", 11);
        time.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#52685C"), Color.FromArgb("#ACBCB3"));
        time.HorizontalOptions = LayoutOptions.End;
        time.VerticalTextAlignment = TextAlignment.Center;
        time.InputTransparent = true;
        time.Margin = new Thickness(0, 0, 9, 0);
        time.MaximumWidthRequest = 76;
        time.LineBreakMode = LineBreakMode.TailTruncation;
        time.MaxLines = 1;
        var item = new Grid { Margin = new Thickness(8, 0, 0, 2) };
        item.Add(button); item.Add(time);
        button.BindingContextChanged += (_, _) =>
        {
            if (button.BindingContext is not ConversationRow row) return;
            button.Text = row.Session.Title;
            time.Text = row.Session.DisplayTime;
            button.Padding = new Thickness(9, 0, string.IsNullOrWhiteSpace(time.Text) ? 9 : 88, 0);
            button.FontAttributes = row.Active ? FontAttributes.Bold : FontAttributes.None;
            button.SetAppThemeColor(BackgroundColorProperty, row.Active ? Color.FromArgb("#E7F1EB") : Colors.Transparent,
                row.Active ? Color.FromArgb("#264637") : Colors.Transparent);
            SemanticProperties.SetDescription(button, $"{row.Session.Title}，{time.Text}{(row.Active ? "，当前会话" : "")}");
        };
        return item;
    }
    private async Task SwitchDeviceAsync()
    {
        if (interactionBusy || session.Busy || connectingDevice) return;
        var entries = connections.Entries.ToArray();
        var labels = entries.Select((e, i) => $"{i + 1}. {connectionNames.GetValueOrDefault(e.Key, e.Value.DeviceName)} · {e.Value.Indicator.Label}").ToArray();
        string choice = await DisplayActionSheetAsync("切换连接端", "取消", null, [.. labels, "管理连接…"]);
        if (choice == "管理连接…") { CloseDrawer(); await OpenConnectionAsync(); return; }
        int index = Array.IndexOf(labels, choice);
        if (index >= 0) ActivateConnection(entries[index].Key, entries[index].Value);
    }
    private async Task NewConversationAsync()
    {
        if (!session.Connected) { CloseDrawer(); await OpenConnectionAsync(); return; }
        if (session.Agent?.CanCreate != true || session.Busy || interactionBusy) return;
        await Act(async () => { SaveDraft(); await session.CreateAsync(); OnChanged(); CloseDrawer(); });
    }
    private async Task MoreAsync()
    {
        string choice = await DisplayActionSheetAsync("设置与更多", "取消", null,
            "模型、思考强度与限额", "语音设置", "检查更新", "会话详情", "归档当前会话", "清理会话缓存");
        if (choice == "取消" || choice is null) return;
        CloseDrawer();
        if (choice == "模型、思考强度与限额") await SettingsAsync();
        else if (choice == "语音设置") await Navigation.PushAsync(new SpeechSettingsPage());
        else if (choice == "检查更新") await Navigation.PushAsync(new UpdatePage());
        else if (choice == "会话详情") await DisplayAlertAsync("会话详情", $"{session.View?.Title}\n{session.View?.Cwd}\n{session.DeviceName}\n{session.Heartbeat}\n{session.Traffic}\n{session.Status}", "关闭");
        else if (choice == "清理会话缓存") await Act(session.ClearCacheAsync);
        else if (choice == "归档当前会话" && session.View is { } view && session.Agent?.CanArchive == true &&
            await DisplayAlertAsync("归档会话", view.Title, "归档", "取消")) await Act(session.ArchiveAsync);
    }
    private void UpdateStatusVisibility()
    {
        string text = status.Text ?? "";
        status.IsVisible = recognizingSpeech || downloadCancel is not null || session.SendUncertain ||
            backgroundServiceError is not null || new[] { "失败", "异常", "未知", "超时", "无法", "缓存", "正在", "上传", "下载", "取消", "重试" }.Any(text.Contains);
    }
}
