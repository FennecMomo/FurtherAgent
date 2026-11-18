using FurtherAgent.Mobile;
using Microsoft.Maui.Controls.Shapes;
using ConnectionProfile = FurtherAgent.Mobile.ConnectionProfile;

namespace FurtherAgent.AndroidApp;

internal sealed class ConnectionPage : ContentPage
{
    private const string DefaultServer = "https://furtheragent.fennecmomo.top";
    private readonly MainPage home;
    private readonly Entry name = new() { Placeholder = "选填，例如家里电脑", ReturnType = ReturnType.Next };
    private readonly Entry server = new() { Placeholder = DefaultServer, Keyboard = Keyboard.Url, ReturnType = ReturnType.Next };
    private readonly Entry code = new() { Placeholder = "输入远端显示的授权码", IsPassword = true, IsTextPredictionEnabled = false };
    private readonly Picker saved = new() { Title = "尚无保存的设备" };
    private readonly Label formTitle = Ui.Text("新增设备", 17, true);
    private readonly Label message = Ui.Text("", 13);
    private readonly Label maintenanceMessage = Ui.Text("", 13);
    private readonly Label notificationStatus = Ui.Text("", 12);
    private readonly ActivityIndicator loading = new() { WidthRequest = 20, HeightRequest = 20, IsVisible = false };
    private readonly Grid formStatus = new()
    {
        ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 8, IsVisible = false
    };
    private readonly Switch autoConnect = new() { IsToggled = true };
    private readonly List<View> controls = [];
    private readonly Button delete;
    private string? editing;
    private bool busy, populatingForm, loadedProfiles;

    public ConnectionPage(MainPage home)
    {
        this.home = home;
        Title = "连接设备";
        Ui.Theme(this);
        server.Text = Preferences.Get("server-url", DefaultServer);
        saved.ItemDisplayBinding = new Binding(nameof(ConnectionProfile.Name));
        saved.SelectedIndexChanged += (_, _) =>
        {
            if (populatingForm) return;
            ShowProfile(saved.SelectedItem as ConnectionProfile);
            ShowMessage(message, "");
        };
        autoConnect.Toggled += (_, _) =>
        {
            if (!populatingForm && editing is { } profileId)
                Preferences.Set("resume-connection." + profileId, autoConnect.IsToggled);
        };
        SemanticProperties.SetDescription(autoConnect, "启动时自动恢复连接");
        name.Completed += (_, _) => server.Focus();
        server.Completed += (_, _) => code.Focus();

        var add = Action("新增", "新建会话", () =>
        {
            saved.SelectedIndex = -1;
            ShowProfile(null);
            return Task.CompletedTask;
        }, message);
        var chooser = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        chooser.Add(saved);
        chooser.Add(add, 1);
        var savedCard = Card("已保存的设备", chooser);

        var remaining = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        var resumeLabel = Ui.Text("启动时自动恢复连接", 13);
        resumeLabel.VerticalOptions = LayoutOptions.Center;
        Ui.TextTheme(resumeLabel);
        remaining.Add(resumeLabel);
        remaining.Add(autoConnect, 1);

        var connect = Action("连接", "连接", ConnectAsync, message, "正在连接设备…", primary: true);
        connect.HorizontalOptions = LayoutOptions.Fill;
        var save = Action("保存", "保存", SaveAsync, message, "正在保存连接…");
        var formActions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        formActions.Add(connect);
        formActions.Add(save, 1);
        loading.SetAppThemeColor(ActivityIndicator.ColorProperty, Ui.Green, Color.FromArgb("#A6D9BC"));
        loading.VerticalOptions = LayoutOptions.Center;
        formStatus.Add(loading);
        formStatus.Add(message, 1);

        delete = Action("删除保存的设备", "忘记保存的连接", DeleteAsync, message);
        delete.IsVisible = false;
        Ui.TextTheme(formTitle);
        var formCard = Card(formTitle, Field("设备名称", name), Field("中继服务", server), Field("授权码", code), remaining,
            formActions, formStatus, delete);

        var services = Card("设备服务",
            Action("离线留言", "会话", () => home.OpenMailboxAsync(editing), maintenanceMessage),
            Action("语音设置", "语音设置", () => Navigation.PushAsync(new SpeechSettingsPage()), maintenanceMessage),
            Action("会话完成通知", "语音设置", () => { CompletionNotifications.OpenSettings(); return Task.CompletedTask; }, maintenanceMessage),
            notificationStatus);
        var maintenance = Card("应用与维护",
            Action("检查应用更新", "检查更新", () => Navigation.PushAsync(new UpdatePage()), maintenanceMessage),
            Action("清理会话缓存", "清理会话缓存", async () =>
            {
                await home.CurrentSession.ClearCacheAsync();
                ShowMessage(maintenanceMessage, "会话缓存已清理。");
            }, maintenanceMessage),
            Action("断开当前设备", "断开当前连接", async () =>
            {
                if (!home.CurrentSession.HasConnection)
                {
                    ShowMessage(maintenanceMessage, "当前没有可断开的设备。");
                    return;
                }
                await home.DisconnectCurrentAsync();
                ShowMessage(maintenanceMessage, "已断开当前设备。");
            }, maintenanceMessage),
            Action("离线界面预览", "查看离线界面预览", async () =>
            {
                await home.PreviewAsync();
                if (Navigation.NavigationStack.LastOrDefault() == this) await Navigation.PopAsync();
            }, maintenanceMessage),
            maintenanceMessage);
        Ui.TextTheme(notificationStatus);
        message.IsVisible = maintenanceMessage.IsVisible = false;
        controls.AddRange([saved, name, server, code, autoConnect]);
        UpdateControls();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16),
                Spacing = 14,
                Children = { savedCard, formCard, services, maintenance }
            }
        };
        Loaded += async (_, _) =>
        {
            if (loadedProfiles) return;
            await RunAsync(async () =>
            {
                await ReloadAsync();
                loadedProfiles = true;
            }, message, "正在读取保存的设备…", "无法读取保存的设备，请返回后重试。");
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshNotificationStatus();
    }

    internal void RefreshNotificationStatus() => notificationStatus.Text = CompletionNotifications.PermissionSummary;

    private Button Action(string text, string icon, Func<Task> execute, Label feedback, string? progress = null, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 14,
            FontAttributes = primary ? FontAttributes.Bold : FontAttributes.None,
            MinimumHeightRequest = 48,
            CornerRadius = 10,
            Padding = new Thickness(12, 6),
            HorizontalOptions = LayoutOptions.Start,
            ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 8)
        };
        var pale = Color.FromArgb("#E7EEE8");
        button.SetAppThemeColor(Button.BackgroundColorProperty, primary ? Ui.Green : Colors.Transparent,
            primary ? Color.FromArgb("#2A654D") : Colors.Transparent);
        button.SetAppThemeColor(Button.TextColorProperty, primary ? Colors.White : Ui.Green, primary ? Colors.White : pale);
        button.SetAppTheme(Button.ImageSourceProperty, Ui.Icon(icon, primary), Ui.Icon(icon, true));
        Ui.AddFeedback(button);
        button.Clicked += async (_, _) => await RunAsync(execute, feedback, progress);
        controls.Add(button);
        return button;
    }

    private static Border Card(string heading, params View[] content)
    {
        var label = Ui.Text(heading, 16, true);
        Ui.TextTheme(label);
        return Card(label, content);
    }

    private static Border Card(Label heading, params View[] content)
    {
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level2);
        var stack = new VerticalStackLayout { Spacing = 6, Children = { heading } };
        foreach (var view in content) stack.Children.Add(view);
        var card = new Border
        {
            Padding = new Thickness(12),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
            Content = stack
        };
        card.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F5F6F7"), Color.FromArgb("#182820"));
        return card;
    }

    private static VerticalStackLayout Field(string title, Entry entry)
    {
        var label = Ui.Text(title, 13, true);
        Ui.TextTheme(label);
        SemanticProperties.SetDescription(entry, title);
        entry.MinimumHeightRequest = 42;
        entry.SetAppThemeColor(Entry.TextColorProperty, Color.FromArgb("#202428"), Color.FromArgb("#E4ECE6"));
        entry.SetAppThemeColor(Entry.PlaceholderColorProperty, Color.FromArgb("#596660"), Color.FromArgb("#AFBFB5"));
        return new VerticalStackLayout { Spacing = 1, Children = { label, entry } };
    }

    private void ShowMessage(Label target, string text, bool isError = false)
    {
        target.Text = text;
        target.IsVisible = text.Length > 0;
        if (ReferenceEquals(target, message)) formStatus.IsVisible = target.IsVisible;
        if (isError) target.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#9E3028"), Color.FromArgb("#FFAFA7"));
        else Ui.TextTheme(target);
    }

    private void UpdateControls()
    {
        foreach (var control in controls) control.IsEnabled = !busy;
        saved.IsEnabled = !busy && saved.ItemsSource?.Count > 0;
        delete.IsVisible = editing is not null;
    }

    private async Task RunAsync(Func<Task> action, Label feedback, string? progress = null, string? error = null)
    {
        if (busy) return;
        busy = true;
        UpdateControls();
        loading.IsRunning = loading.IsVisible = progress is not null && ReferenceEquals(feedback, message);
        ShowMessage(feedback, progress ?? "");
        try
        {
            await action();
            if (progress is not null && feedback.Text == progress) ShowMessage(feedback, "");
        }
        catch (Exception ex)
        {
            string authorization = code.Text?.Trim() ?? "";
            string detail = error ?? (authorization.Length == 0 ? ex.Message : ex.Message.Replace(authorization, "[授权码]", StringComparison.Ordinal));
            ShowMessage(feedback, detail, isError: true);
        }
        finally
        {
            loading.IsRunning = loading.IsVisible = false;
            busy = false;
            UpdateControls();
        }
    }

    private void ShowProfile(ConnectionProfile? profile)
    {
        populatingForm = true;
        try
        {
            editing = profile?.Id;
            formTitle.Text = profile is null ? "新增设备" : "编辑设备";
            name.Text = profile?.Name ?? "";
            server.Text = profile?.Server ?? Preferences.Get("server-url", DefaultServer);
            code.Text = profile?.Code ?? "";
            autoConnect.IsToggled = profile is null || Preferences.Get("resume-connection." + profile.Id, true);
        }
        finally { populatingForm = false; }
        UpdateControls();
    }

    private async Task ReloadAsync(string? selected = null)
    {
        var state = await SavedConnections.LoadAsync();
        var profile = state.Connections.FirstOrDefault(p => p.Id == (selected ?? state.SelectedId));
        populatingForm = true;
        try
        {
            saved.ItemsSource = state.Connections;
            saved.Title = state.Connections.Count == 0 ? "尚无保存的设备" : "选择设备";
            saved.SelectedItem = profile;
        }
        finally { populatingForm = false; }
        ShowProfile(profile);
    }

    private async Task SaveAsync()
    {
        var (url, pairingCode) = ReadAddress();
        var profile = await SavedConnections.Store.SaveAsync(name.Text ?? "", url, pairingCode, editing);
        Preferences.Set("resume-connection." + profile.Id, autoConnect.IsToggled);
        await ReloadAsync(profile.Id);
        ShowMessage(message, "设备已保存。");
    }

    private async Task ConnectAsync()
    {
        var (url, pairingCode) = ReadAddress();
        await home.ConnectAsync(url, pairingCode, name.Text, editing, autoConnect.IsToggled);
        if (Navigation.NavigationStack.LastOrDefault() != this) return;
        await Navigation.PopAsync();
        if (home.CurrentSession.View is null) home.ShowSessionDrawer();
    }

    private async Task DeleteAsync()
    {
        if (editing is not { } profileId) return;
        if (!await DisplayAlertAsync("删除保存的设备", "当前连接和聊天草稿会保留。", "删除", "取消")) return;
        await SavedConnections.Store.RemoveAsync(profileId);
        await ReloadAsync();
        ShowMessage(message, "所选设备已从列表移除。");
    }

    private (string Url, string Code) ReadAddress()
    {
        string url = server.Text?.Trim() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || string.IsNullOrEmpty(address.Host))
            throw new ArgumentException("请输入完整的中继地址，例如 " + DefaultServer + "。");
        string pairingCode = code.Text?.Trim() ?? "";
        if (pairingCode.Length == 0) throw new ArgumentException("请输入设备授权码。");
        return (url, pairingCode);
    }
}
