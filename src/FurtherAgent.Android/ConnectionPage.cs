using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

internal sealed class ConnectionPage : ContentPage
{
    private readonly Entry server = new() { Placeholder = "https://你的中继服务", Keyboard = Keyboard.Url, ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private readonly Entry code = new() { Placeholder = "远端显示的授权码", IsPassword = true, IsTextPredictionEnabled = false };
    private readonly Label message = Ui.Text("", 12);
    private readonly Button connect;
    public ConnectionPage(MainPage home, MobileSession session)
    {
        Title = "连接设备"; Ui.Theme(this);
        server.Text = Preferences.Get("server-url", "https://furtheragent.fennecmomo.top");
        connect = Ui.Button("连接并查看会话", async () =>
        {
            connect!.IsEnabled = false; message.Text = "正在配对…";
            try
            {
                await home.ConnectAsync(server.Text?.Trim() ?? "", code.Text?.Trim() ?? "");
                await Navigation.PopAsync();
                await home.Navigation.PushAsync(new SessionsPage(home, session));
            }
            catch (Exception ex)
            {
                message.Text = string.IsNullOrWhiteSpace(code.Text) ? ex.Message : ex.Message.Replace(code.Text, "[授权码]", StringComparison.Ordinal);
            }
            finally { connect.IsEnabled = true; }
        }, true);
        var intro = Ui.Text("把工作带到手机上", 26, true);
        var detail = Ui.Text("在电脑或服务器开启 FurtherAgent 共享，将授权码填在这里。双方通过中继连接，无需配置设备 IP。", 14);
        var hint = Ui.Text("授权码加密保存在本机。关闭页面不会停止远端任务。每台远端设备当前只允许一个控制端；若提示已被占用，请先断开原控制端。", 12);
        var stack = new VerticalStackLayout { Padding = 24, Spacing = 18,
            Children = { intro, detail, Ui.Text("中继服务", 12, true), server, Ui.Text("设备授权码", 12, true), code,
                connect, message, hint,
                Ui.Button("查看离线界面预览", async () => { await home.PreviewAsync(); await Navigation.PopAsync(); }),
                Ui.Button("断开当前连接", async () => { await session.DisconnectAsync(); message.Text = "已断开，远端任务继续执行"; }),
                Ui.Button("忘记保存的连接", async () =>
                {
                    if (!await DisplayAlertAsync("忘记连接", "删除本机保存的授权码和地址？聊天草稿仍保留在本机。", "忘记", "取消")) return;
                    await session.DisconnectAsync(); SecureStorage.Remove("pairing-code"); Preferences.Remove("server-url"); code.Text = ""; message.Text = "连接资料已删除";
                }) } };
        foreach (var label in stack.Children.OfType<Label>()) Ui.TextTheme(label);
        Content = new ScrollView { Content = stack };
        Loaded += async (_, _) =>
        {
            try { code.Text = await SecureStorage.GetAsync("pairing-code") ?? ""; }
            catch { message.Text = "保存的凭据无法读取，请重新输入授权码。"; SecureStorage.Remove("pairing-code"); }
        };
    }
}
