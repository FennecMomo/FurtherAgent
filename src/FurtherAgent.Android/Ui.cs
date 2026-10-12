namespace FurtherAgent.AndroidApp;

internal static class Ui
{
    public static readonly Color Green = Color.FromArgb("#183F34");
    public static readonly Color Muted = Color.FromArgb("#718278");
    public static Label Text(string text, double size = 14, bool bold = false) => new()
    { Text = text, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None, TextColor = Green };
    public static Button Button(string text, Func<Task> click, bool primary = false)
    {
        var button = new Button { Text = "", CornerRadius = 10, HeightRequest = 44, WidthRequest = 44,
            MinimumWidthRequest = 44, MinimumHeightRequest = 44, HorizontalOptions = LayoutOptions.Start,
            Padding = new Thickness(10), BackgroundColor = primary ? Green : Color.FromArgb("#E7EEE8"),
            TextColor = primary ? Colors.White : Green };
        SetAction(button, text, primary);
        button.Clicked += async (_, _) => await click();
        return button;
    }
    public static ImageSource Icon(string action, bool primary = false)
    {
        string name = action switch
        {
            "会话" => "chats", "连接" or "连接并查看会话" => "link", "发送 ↑" => "send",
            "停止" or "结束" => "stop", "附件" => "attach", "模型" => "model", "语音" => "mic",
            "核对" => "check", "等待…" => "clock", "取消" or "取消下载" => "close", "刷新" => "refresh",
            "新建会话" => "plus", "归档当前" => "archive", "保存" or "应用到此会话" => "save",
            "语音设置" => "settings", "清理会话缓存" => "broom", "查看离线界面预览" => "eye",
            "断开当前连接" => "unlink", "忘记保存的连接" or "删除语音密钥" => "trash",
            _ => "check"
        };
        return ImageSource.FromFile($"ic_{name}{(primary ? "_white" : "")}.png");
    }
    public static void SetAction(Button button, string action, bool primary = false)
    {
        button.Text = ""; button.ImageSource = Icon(action, primary);
        SemanticProperties.SetDescription(button, action);
        ToolTipProperties.SetText(button, action);
    }
    public static void Theme(Page page)
    {
        page.SetAppThemeColor(Page.BackgroundColorProperty, Color.FromArgb("#F6F8F5"), Color.FromArgb("#101C17"));
        if (page is ContentPage content) content.SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.All);
    }
    public static void TextTheme(Label label) => label.SetAppThemeColor(Label.TextColorProperty, Green, Color.FromArgb("#E4ECE6"));
}
