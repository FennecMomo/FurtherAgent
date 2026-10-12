using FurtherAgent.Mobile;
using ConnectionProfile = FurtherAgent.Mobile.ConnectionProfile;

namespace FurtherAgent.AndroidApp;

internal sealed class ConnectionPage : ContentPage
{
    private readonly Entry name = new() { Placeholder = "连接名称，例如办公电脑、家中电脑" };
    private readonly Entry server = new() { Placeholder = "https://你的中继服务", Keyboard = Keyboard.Url };
    private readonly Entry code = new() { Placeholder = "远端显示的授权码", IsPassword = true, IsTextPredictionEnabled = false };
    private readonly Picker saved = new() { Title = "选择已保存的连接" };
    private readonly Label message = Ui.Text("", 12);
    private string? editing;
    private bool busy;
    public ConnectionPage(MainPage home, MobileSession session)
    {
        Title = "连接设备"; Ui.Theme(this);
        server.Text = Preferences.Get("server-url", "https://furtheragent.fennecmomo.top");
        saved.ItemDisplayBinding = new Binding(nameof(ConnectionProfile.Name));
        saved.SelectedIndexChanged += (_, _) =>
        {
            if (saved.SelectedItem is not ConnectionProfile p) return;
            editing = p.Id; name.Text = p.Name; server.Text = p.Server; code.Text = p.Code;
        };
        Button Action(string text, Func<Task> action) => new() { Text = text, Command = new Command(async () =>
        {
            if (busy) return;
            busy = true; saved.IsEnabled = name.IsEnabled = server.IsEnabled = code.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { message.Text = string.IsNullOrWhiteSpace(code.Text) ? ex.Message : ex.Message.Replace(code.Text, "[授权码]", StringComparison.Ordinal); }
            finally { busy = false; saved.IsEnabled = name.IsEnabled = server.IsEnabled = code.IsEnabled = true; }
        }) };
        var stack = new VerticalStackLayout { Padding = 24, Spacing = 12, Children =
        {
            Ui.Text("已保存的连接", 22, true), saved,
            Action("新增连接", () => { saved.SelectedIndex = -1; editing = null; name.Text = ""; code.Text = ""; message.Text = "填写新设备的地址和授权码，已有连接会保留。"; return Task.CompletedTask; }),
            name, Ui.Text("中继服务", 12, true), server, Ui.Text("设备授权码", 12, true), code,
            Action("保存连接", async () =>
            {
                var p = await SavedConnections.Store.SaveAsync(name.Text ?? "", server.Text ?? "", code.Text ?? "", editing);
                await ReloadAsync(p.Id); message.Text = "已保存，可随时切换。";
            }),
            Action("连接并查看会话", async () =>
            {
                message.Text = "正在配对…";
                await home.ConnectAsync(server.Text?.Trim() ?? "", code.Text?.Trim() ?? "", name.Text, editing);
                await Navigation.PopAsync(); await home.Navigation.PushAsync(new SessionsPage(home, session));
            }),
            Action("删除所选连接", async () =>
            {
                if (editing is null) return;
                if (!await DisplayAlertAsync("删除连接", "仅删除所选设备的保存资料，当前连接与聊天草稿保留。", "删除", "取消")) return;
                await SavedConnections.Store.RemoveAsync(editing); editing = null; code.Text = name.Text = "";
                await ReloadAsync(); message.Text = "所选连接已删除。";
            }),
            message, Ui.Text("可保存多个设备，一次选择一个设备查看会话。切换后远端任务继续运行，草稿按设备和会话分别保留。", 12),
            Action("语音设置", () => Navigation.PushAsync(new SpeechSettingsPage())),
            Action("清理会话缓存", async () => { await session.ClearCacheAsync(); message.Text = "会话缓存已清理，草稿、附件和连接保留。"; }),
            Action("断开当前连接", async () => { await session.DisconnectAsync(); message.Text = "已断开，远端任务继续执行。"; }),
            Action("查看离线界面预览", async () => { await home.PreviewAsync(); await Navigation.PopAsync(); })
        } };
        foreach (var label in stack.Children.OfType<Label>()) Ui.TextTheme(label);
        Content = new ScrollView { Content = stack };
        Loaded += async (_, _) => { try { await ReloadAsync(); } catch { message.Text = "保存的连接无法读取，原资料未删除。"; } };
    }
    private async Task ReloadAsync(string? selected = null)
    {
        var state = await SavedConnections.LoadAsync();
        saved.ItemsSource = state.Connections;
        saved.SelectedItem = state.Connections.FirstOrDefault(p => p.Id == (selected ?? state.SelectedId));
    }
}
