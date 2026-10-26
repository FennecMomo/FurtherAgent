using FurtherAgent.Core;

namespace FurtherAgent.AndroidApp;

public sealed partial class MainPage
{
    private Button deviceButton = null!;
    private Button mailboxButton = null!;
    private Grid drawerLayer = null!;
    private Border drawerPanel = null!;
    private VerticalStackLayout drawerItems = null!;
    private SearchBar drawerSearch = null!;
    private Button drawerDevice = null!;
    private readonly HashSet<string> collapsedWorkspaces = [];

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
        drawerSearch = new SearchBar { Placeholder = "搜索会话", FontSize = 14 };
        drawerSearch.TextChanged += (_, _) => RenderDrawer();
        drawerItems = new VerticalStackLayout { Spacing = 2 };
        mailboxButton = TextAction("留言", () => OpenMailboxAsync());
        var top = new VerticalStackLayout { Spacing = 8, Children = { drawerDevice, drawerSearch,
            TextAction("新建会话", NewConversationAsync), mailboxButton } };
        var bottom = new VerticalStackLayout { Children = {
            TextAction("刷新会话", async () => { await Act(session.RefreshAsync); RenderDrawer(); }),
            TextAction("连接管理", async () => { CloseDrawer(); await OpenConnectionAsync(); }),
            TextAction("设置与更多", MoreAsync) } };
        var content = new Grid { Padding = new Thickness(12, 12, 12, 16), RowSpacing = 12,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        content.Add(top); content.Add(new ScrollView { Content = drawerItems }, 0, 1); content.Add(bottom, 0, 2);
        drawerPanel = new Border { HorizontalOptions = LayoutOptions.Start, StrokeThickness = 0, Content = content };
        drawerPanel.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#101C17"));
        drawerLayer.Add(drawerPanel); root.Add(drawerLayer);
        root.SizeChanged += (_, _) => drawerPanel.WidthRequest = Math.Min(360, Math.Max(240, root.Width * .86));
        return root;
    }
    private void OpenDrawer() { RenderDrawer(); drawerLayer.IsVisible = true; }
    private void CloseDrawer() => drawerLayer.IsVisible = false;
    protected override bool OnBackButtonPressed()
    {
        if (drawerLayer.IsVisible) { CloseDrawer(); return true; }
        return base.OnBackButtonPressed();
    }
    private void RenderDrawer()
    {
        UpdateMailboxBadge();
        drawerDevice.Text = $"{session.DeviceName} · {session.Indicator.Label} ▾";
        drawerItems.Children.Clear();
        string query = drawerSearch.Text?.Trim() ?? "";
        var groups = session.Sessions.Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Cwd.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.SortTime).GroupBy(s => s.Cwd);
        foreach (var group in groups)
        {
            string key = (activeConnection ?? "") + "|" + group.Key;
            bool folded = query.Length == 0 && collapsedWorkspaces.Contains(key);
            string name = string.IsNullOrEmpty(group.Key) ? "最近" : group.Key.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
            var header = TextAction($"{name}  {(folded ? "›" : "⌄")}", () =>
            { if (!collapsedWorkspaces.Add(key)) collapsedWorkspaces.Remove(key); RenderDrawer(); return Task.CompletedTask; });
            header.FontAttributes = FontAttributes.Bold; header.HorizontalOptions = LayoutOptions.Fill;
            AlignStart(header);
            SemanticProperties.SetDescription(header, group.Key + (folded ? "，展开工作空间" : "，折叠工作空间"));
            drawerItems.Children.Add(header);
            if (folded) continue;
            foreach (var item in group)
            {
                var row = TextAction(item.Title, async () =>
                {
                    if (interactionBusy || session.Busy || connectingDevice) return;
                    await Act(() => SelectAsync(item.Id)); CloseDrawer();
                });
                row.HorizontalOptions = LayoutOptions.Fill; row.Margin = new Thickness(12, 0, 0, 0);
                AlignStart(row);
                if (item.Id == session.View?.Id) row.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#E7F1EB"), Color.FromArgb("#264637"));
                drawerItems.Children.Add(row);
            }
        }
        if (drawerItems.Children.Count == 0) drawerItems.Children.Add(Ui.Text(session.Connected ? "没有匹配的会话" : "连接设备后查看会话", 14));
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
        CloseDrawer(); await Act(async () => { SaveDraft(); await session.CreateAsync(); OnChanged(); });
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
