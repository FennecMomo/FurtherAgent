using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

internal sealed class SpeechSettingsPage : ContentPage
{
    internal const string KeyName = "speech-api-key";
    internal const string WorkspaceName = "speech-workspace";
    public SpeechSettingsPage()
    {
        Title = "语音设置"; Ui.Theme(this);
        var key = new Entry { Placeholder = "填写或替换北京地域 API Key", IsPassword = true, IsTextPredictionEnabled = false, AutomationId = "speech-key" };
        var workspace = new Entry { Placeholder = "选填：业务空间 ID", Text = Preferences.Get(WorkspaceName, ""), IsTextPredictionEnabled = false };
        var status = Ui.Text("", 12);
        var stack = new VerticalStackLayout { Padding = 24, Spacing = 18, Children = {
            Ui.Text("实时语音输入", 25, true),
            Ui.Text("使用阿里云百炼 · Fun-ASR（北京）。说话时逐步出字，点击结束后可编辑再发送。单次最长 3 分钟。"),
            Ui.Text("录音会直接发送到阿里云识别，按录音时长计费。手机不保存录音，密钥仅保存于本机安全存储。"),
            Ui.Text("API Key", 12, true), key, Ui.Text("业务空间", 12, true), workspace,
            Ui.Text("默认使用北京公共接入地址；填写业务空间 ID 可使用专属接入地址。密钥须属于北京地域并有 Fun-ASR 调用权限。", 12),
            Ui.Button("保存", async () => {
                try {
                    _ = FunAsrClient.Endpoint(workspace.Text ?? "");
                    string value = key.Text?.Trim() ?? "";
                    if (value.Length == 0 && string.IsNullOrWhiteSpace(await SecureStorage.GetAsync(KeyName)))
                        throw new InvalidOperationException("请先填写 API Key。");
                    if (value.Any(char.IsWhiteSpace)) throw new InvalidOperationException("API Key 不能包含空白字符。");
                    if (value.Length > 0) await SecureStorage.SetAsync(KeyName, value);
                    Preferences.Set(WorkspaceName, workspace.Text?.Trim() ?? ""); key.Text = "";
                    status.Text = "已保存。返回会话点击语音即可开始。";
                } catch { status.Text = "保存失败，请检查密钥和业务空间 ID 后重试。"; }
            }, true),
            Ui.Button("删除语音密钥", async () => {
                if (!await DisplayAlertAsync("删除密钥", "删除本机语音识别密钥？", "删除", "取消")) return;
                SecureStorage.Remove(KeyName); key.Text = ""; status.Text = "已删除，需要重新配置后才能识别。";
            }), status } };
        foreach (var label in stack.Children.OfType<Label>()) Ui.TextTheme(label);
        Content = new ScrollView { Content = stack };
        Loaded += async (_, _) => {
            try { status.Text = string.IsNullOrWhiteSpace(await SecureStorage.GetAsync(KeyName)) ? "尚未配置语音密钥" : "已有密钥；留空表示保留。"; }
            catch { status.Text = "无法读取已存密钥，请重新保存。"; }
        };
    }
}
