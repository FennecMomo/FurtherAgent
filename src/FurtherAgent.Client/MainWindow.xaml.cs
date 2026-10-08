using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow : Window
{
    private DeviceHost? host;
    private RegistryClient? hostRegistry, remoteRegistry;
    private RemoteDevice? remote;
    private IReadOnlyList<SessionInfo> sessions = [];
    private SessionView? current;
    private string? selectedId, promptSignature;
    private int selectionVersion;
    private bool closing, actionBusy;
    public MainWindow()
    {
        InitializeComponent();
        ServerUrl.Text = Environment.GetEnvironmentVariable("FURTHERAGENT_SERVER") ?? ServerUrl.Text;
    }
    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        ShareButton.IsEnabled = false;
        try
        {
            if (host is not null) { await host.DisposeAsync(); host = null; hostRegistry?.Dispose(); LocalCode.Text = "尚未开启共享"; CopyCodeButton.IsEnabled = false; HostStatus.Text = "共享已停止"; ShareButton.Content = "开启共享"; }
            else
            {
                hostRegistry = new RegistryClient(ServerUrl.Text);
                host = new DeviceHost(hostRegistry);
                host.Status += message => Dispatcher.BeginInvoke(() => HostStatus.Text = message);
                host.CodeChanged += code => Dispatcher.BeginInvoke(() => { LocalCode.Text = code; CopyCodeButton.IsEnabled = true; });
                try { await host.StartAsync(Environment.MachineName); }
                catch { await host.DisposeAsync(); host = null; hostRegistry.Dispose(); throw; }
                LocalCode.Text = host.Lease!.Code;
                CopyCodeButton.IsEnabled = true;
                HostStatus.Text = "等待连接。请将上方授权码发送给 A。";
                ShareButton.Content = "停止共享";
            }
        }
        catch (Exception ex) { Error(ex); }
        finally { ShareButton.IsEnabled = true; }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            await DisconnectAsync();
            remoteRegistry = new RegistryClient(ServerUrl.Text);
            remote = new RemoteDevice();
            RemoteStatus.Text = "正在通过授权码建立中继连接…";
            await remote.ConnectAsync(remoteRegistry, RemoteCode.Text.Trim());
            var connected = remote;
            remote.Notification += (method, node) =>
            {
                if (method == "session" && node is not null)
                { var view = Json.Read<SessionView>(node); Dispatcher.BeginInvoke(() => { if (remote == connected) Render(view); }); }
            };
            remote.ConnectionChanged += (online, message) => Dispatcher.BeginInvoke(() =>
            {
                if (remote != connected) return;
                if (!online) current = null;
                RemoteStatus.Text = message; SetSessionEnabled(false); RefreshButton.IsEnabled = online;
                StatusBar.Text = message + " 重连会恢复会话显示；不会自动重发消息或确认操作。";
            });
            remote.Reconnected += () => Dispatcher.BeginInvoke(async () => await RestoreSessionAsync(connected));
            var agents = Json.Read<AgentInfo[]>(await remote.Peer.CallAsync("agents", null));
            if (!agents.Any(a => a.Id == "codex")) throw new IOException("远端没有可用 Codex 适配器。");
            RemoteStatus.Text = $"已通过服务端连接 · {remote.Device!.Name}";
            DisconnectButton.IsEnabled = RefreshButton.IsEnabled = true;
            await RefreshAsync();
            StatusBar.Text = "设备连接成功，请选择会话。Codex 桌面适配属于实验性接口。";
        }
        catch (Exception ex) { await DisconnectAsync(); Error(ex); RemoteStatus.Text = "连接失败。请检查中继服务地址、授权码及 B 是否已开启共享。"; }
        finally { ConnectButton.IsEnabled = true; }
    }
    private void CopyCode_Click(object sender, RoutedEventArgs e)
    { try { Clipboard.SetText(LocalCode.Text); StatusBar.Text = "授权码已复制。"; } catch (Exception ex) { Error(ex); } }
    private async Task RestoreSessionAsync(RemoteDevice connection)
    {
        if (remote != connection) return;
        int version = selectionVersion;
        string? id = selectedId;
        try
        {
            await RefreshAsync();
            if (id is not null)
            {
                var view = Json.Read<SessionView>(await connection.Peer.CallAsync("attach", new { id }));
                if (remote == connection && version == selectionVersion) Render(view);
            }
        }
        catch (Exception ex) { if (remote == connection) Error(ex); }
    }
    private async Task DisconnectAsync()
    {
        selectionVersion++; selectedId = null; current = null; promptSignature = null;
        var previous = remote; remote = null;
        if (previous is not null) await previous.DisposeAsync();
        remoteRegistry?.Dispose(); remoteRegistry = null;
        sessions = []; SessionList.ItemsSource = null;
        DisconnectButton.IsEnabled = RefreshButton.IsEnabled = false; SetSessionEnabled(false);
        PromptBorder.Visibility = Visibility.Collapsed;
        RemoteStatus.Text = "尚未连接"; SessionTitle.Text = "选择远端会话，继续工作";
        SessionMeta.Text = "连接后显示原桌面会话的消息与执行状态"; Transcript.Text = "远程会话的消息将在这里显示。";
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e) { await DisconnectAsync(); }
    private async Task RefreshAsync()
    {
        if (remote is null) return;
        sessions = Json.Read<List<SessionInfo>>(await remote.Peer.CallAsync("sessions", null));
        Filter();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    { try { await RefreshAsync(); } catch (Exception ex) { Error(ex); } }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (SessionList is not null) Filter(); }
    private void Filter()
    {
        var selected = selectedId;
        var filtered = sessions.Where(s => s.Title.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        SessionList.ItemsSource = filtered;
        SessionList.SelectedItem = filtered.FirstOrDefault(s => s.Id == selected);
    }
    private async void Session_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (remote is null || SessionList.SelectedItem is not SessionInfo session || session.Id == selectedId) return;
        int version = ++selectionVersion;
        selectedId = session.Id; current = null; promptSignature = null; SetSessionEnabled(false);
        PromptBorder.Visibility = Visibility.Collapsed; SessionTitle.Text = session.Title; SessionMeta.Text = "正在连接原桌面会话…";
        try
        {
            var view = Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id = session.Id }));
            if (version != selectionVersion) return;
            Render(view); StatusBar.Text = "已接入原桌面会话。发送指令会在 B 的原项目中执行。";
        }
        catch (Exception ex)
        {
            if (version != selectionVersion) return;
            selectedId = null; SessionMeta.Text = "会话连接失败，请在 B 桌面打开它，再重新选择"; Error(ex);
        }
    }
    private void Render(SessionView view)
    {
        if (view.Id != selectedId) return;
        current = view; SessionTitle.Text = string.IsNullOrWhiteSpace(view.Title) ? SessionTitle.Text : view.Title;
        SessionMeta.Text = $"{view.Cwd}\n{(view.Status == "active" ? "执行中" : "等待指令")} · 桌面实时同步";
        var builder = new StringBuilder();
        foreach (var message in view.Messages) builder.AppendLine($"{message.Role}\n{message.Text}\n");
        var content = builder.ToString();
        if (content != Transcript.Text)
        {
            bool atBottom = Transcript.VerticalOffset + Transcript.ViewportHeight >= Transcript.ExtentHeight - 30;
            double previous = Transcript.VerticalOffset;
            Transcript.Text = content;
            if (atBottom) Transcript.ScrollToEnd(); else Transcript.ScrollToVerticalOffset(previous);
        }
        SetSessionEnabled(!actionBusy && remote?.IsConnected == true);
        var signature = string.Join("|", view.Prompts.Select(p => p.Id + p.Kind));
        if (signature != promptSignature) { promptSignature = signature; RenderPrompts(view.Prompts); }
    }
    private void SetSessionEnabled(bool enabled)
    {
        Composer.IsEnabled = SendButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled && current?.Status == "active";
    }
    private async Task ActionAsync(Func<RpcPeer, Task> action)
    {
        if (remote is null || actionBusy) return;
        actionBusy = true; SetSessionEnabled(false);
        try { await action(remote.Peer); StatusBar.Text = "操作已由 Codex 桌面接收，结果以会话状态为准。"; }
        catch (Exception ex) { Error(ex); }
        finally { actionBusy = false; SetSessionEnabled(remote is not null && current is not null && remote.Peer.Completion?.IsCompleted == false); }
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        string text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        await ActionAsync(async peer => { await peer.CallAsync("send", new { text }); if (Composer.Text == text) Composer.Clear(); });
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ActionAsync(async peer => { await peer.CallAsync("interrupt", null); });
    private void RenderPrompts(List<PendingPrompt> prompts)
    {
        PromptPanel.Children.Clear(); PromptBorder.Visibility = prompts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var prompt in prompts)
        {
            PromptPanel.Children.Add(new TextBlock { Text = "Codex 需要你的确认", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            if (prompt.Kind == "item/tool/requestUserInput" && prompt.Payload?["questions"] is JsonArray questions)
            {
                var answers = new Dictionary<string, TextBox>();
                foreach (var q in questions)
                {
                    PromptPanel.Children.Add(new TextBlock { Text = q?["question"]?.ToString(), TextWrapping = TextWrapping.Wrap });
                    if (q?["options"] is JsonArray options)
                        PromptPanel.Children.Add(new TextBlock { Text = string.Join("\n", options.Select(o => $"• {o?["label"]}：{o?["description"]}")), TextWrapping = TextWrapping.Wrap });
                    var answer = new TextBox(); PromptPanel.Children.Add(answer); answers[q!["id"]!.ToString()] = answer;
                }
                var button = new Button { Content = "提交回答" };
                button.Click += async (_, _) => await ActionAsync(async peer =>
                {
                    if (answers.Values.Any(a => string.IsNullOrWhiteSpace(a.Text))) throw new ArgumentException("请填写每个问题的回答。");
                    await peer.CallAsync("respond", new { id = prompt.Id, decision = "", response = new { answers = answers.ToDictionary(p => p.Key, p => new { answers = new[] { p.Value.Text } }) } });
                });
                PromptPanel.Children.Add(button);
            }
            else if (prompt.Kind is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
            {
                PromptPanel.Children.Add(new TextBlock { Text = prompt.Kind.Contains("commandExecution") ? "执行命令" : "修改文件", FontWeight = FontWeights.SemiBold });
                PromptPanel.Children.Add(new TextBlock { Text = prompt.Payload?["command"]?.ToString() ?? prompt.Payload?["reason"]?.ToString() ?? prompt.Detail, TextWrapping = TextWrapping.Wrap });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var (label, decision) in new[] { ("允许本次", "accept"), ("拒绝", "decline") })
                {
                    var button = new Button { Content = label };
                    button.Click += async (_, _) => await ActionAsync(async peer => { await peer.CallAsync("respond", new { id = prompt.Id, decision }); });
                    buttons.Children.Add(button);
                }
                PromptPanel.Children.Add(buttons);
            }
            else PromptPanel.Children.Add(new TextBlock { Text = "此类交互请在 B 的 Codex 桌面处理：" + prompt.Kind, TextWrapping = TextWrapping.Wrap });
        }
    }
    private void Error(Exception ex) => StatusBar.Text = "操作失败：" + ex.Message;
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; IsEnabled = false;
        await DisconnectAsync();
        if (host is not null) await host.DisposeAsync();
        hostRegistry?.Dispose(); closing = true; Close();
    }
}
