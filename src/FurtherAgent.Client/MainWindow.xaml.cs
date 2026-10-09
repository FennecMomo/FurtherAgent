using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
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
    private bool closing, actionBusy, loadingSettings;
    private System.Windows.Forms.NotifyIcon? tray;
    private bool exitRequested;
    private AgentInfo? agent;
    private string? createRequestId, modelSession;
    private string transcriptText = "";
    private CancellationTokenSource? transferCancellation;
    private readonly List<RemoteFile> attachedFiles = [];
    private string? filesSession;
    private void ClearAttachments() { attachedFiles.Clear(); AttachmentSummary.Text = ""; }

    private readonly Dictionary<string, string> drafts = [];
    private static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "settings.json");
    private readonly SavedConnectionStore connectionStore = new(Path.Combine(Path.GetDirectoryName(SettingsFile)!, "connection.json"));
    public MainWindow()
    {
        InitializeComponent();
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/FurtherAgent.ico")).Stream)
        using (var icon = new System.Drawing.Icon(stream))
            tray = new System.Windows.Forms.NotifyIcon { Icon = (System.Drawing.Icon)icon.Clone(), Text = "FurtherAgent", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开 FurtherAgent", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => { exitRequested = true; Close(); }));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        Application.Current.SessionEnding += (_, _) => { closing = true; DisposeTray(); };
        try
        {
            if (File.Exists(SettingsFile)) ServerUrl.Text = JsonNode.Parse(File.ReadAllText(SettingsFile))?["serverUrl"]?.ToString() ?? ServerUrl.Text;
        }
        catch { StatusBar.Text = "保存的地址无法读取，请在设置中重新保存。"; }
        ServerUrl.Text = Environment.GetEnvironmentVariable("FURTHERAGENT_SERVER") ?? ServerUrl.Text;
        try
        {
            var saved = connectionStore.Load(ServerUrl.Text);
            if (saved is not null)
            {
                RemoteCode.Text = saved.Code;
                RemoteStatus.Text = "已记住设备 · " + saved.Name;
                if (saved.AutoConnect) Loaded += (_, _) => Connect_Click(this, new RoutedEventArgs());
            }
        }
        catch { StatusBar.Text = "已保存连接无法解密，请重新输入授权码连接；原记录未删除。"; }
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
                host = new DeviceHost(hostRegistry, persistentIdentity: true);
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
        if (!ConnectButton.IsEnabled) return;
        string server = ServerUrl.Text.Trim(), code = RemoteCode.Text.Trim();
        ConnectButton.IsEnabled = false; ConnectError.Text = "";
        try
        {
            await DisconnectAsync();
            remoteRegistry = new RegistryClient(server);
            remote = new RemoteDevice();
            RemoteStatus.Text = "正在通过授权码建立中继连接…";
            await remote.ConnectAsync(remoteRegistry, code);
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
            agent = agents.FirstOrDefault(a => a.Available) ?? throw new IOException("远端没有可用适配器。");
            AgentPicker.Items.Clear(); AgentPicker.Items.Add(agent.Name); AgentPicker.SelectedIndex = 0;
            RemoteStatus.Text = $"已通过服务端连接 · {remote.Device!.Name}";
            DisconnectButton.IsEnabled = RefreshButton.IsEnabled = true;
            await RefreshAsync();
            EmptyTitle.Text = "选择会话，继续工作"; EmptyDescription.Text = "从左侧打开已有会话，或点击「新建会话」。"; EmptyConnect.Visibility = Visibility.Collapsed;
            StatusBar.Text = agent.Detail; ConnectOverlay.Visibility = Visibility.Collapsed; ShowConversations();
            try
            {
                SaveServerAddress(server);
                connectionStore.Save(new(server, code, remote.Device!.Name, true));
            }
            catch { StatusBar.Text += " 连接已建立，但本机连接记录保存失败，下次启动需重新输入。"; }
        }
        catch (Exception ex) { await DisconnectAsync(); Error(ex); ConnectError.Text = ex.Message; RemoteStatus.Text = "连接失败。请检查中继服务地址、授权码及 B 是否已开启共享。"; }
        finally { ConnectButton.IsEnabled = true; }
    }
    private void CopyCode_Click(object sender, RoutedEventArgs e)
    { try { Clipboard.SetText(LocalCode.Text); StatusBar.Text = "授权码已复制。"; } catch (Exception ex) { Error(ex); } }
    private async void ResetPairing_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string server = hostRegistry?.ServerIdentity ?? new Uri(ServerUrl.Text.TrimEnd('/') + '/').AbsoluteUri;
            if (host is not null) { await host.DisposeAsync(); host = null; hostRegistry?.Dispose(); }
            DeviceIdentity.Reset(DeviceIdentity.DefaultPath(server, Environment.MachineName));
            LocalCode.Text = "身份已重置，下次开启共享后显示新码"; CopyCodeButton.IsEnabled = false;
            ShareButton.Content = "开启共享"; HostStatus.Text = "共享已停止，旧配对码已失效。";
        }
        catch (Exception ex) { Error(ex); }
    }
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
        transferCancellation?.Cancel(); ClearAttachments();
        SaveDraft(); agent = null; ModelOverlay.Visibility = Visibility.Collapsed;
        selectionVersion++; selectedId = null; current = null; promptSignature = null;
        var previous = remote; remote = null;
        if (previous is not null) await previous.DisposeAsync();
        remoteRegistry?.Dispose(); remoteRegistry = null;
        sessions = []; SessionList.ItemsSource = null;
        DisconnectButton.IsEnabled = RefreshButton.IsEnabled = false; SetSessionEnabled(false);
        PromptBorder.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = "你的 Agent，随时继续。"; EmptyDescription.Text = "连接设备，选择已有会话或新建会话。"; EmptyConnect.Visibility = Visibility.Visible;
        RemoteStatus.Text = "尚未连接"; SessionTitle.Text = "选择远端会话，继续工作";
        SessionMeta.Text = "连接后显示远端会话历史与可用的运行状态"; Messages.ItemsSource = null; transcriptText = ""; Composer.Clear(); EmptyState.Visibility = Visibility.Visible;
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = connectionStore.Load(remoteRegistry?.ServerIdentity ?? ServerUrl.Text);
            if (saved is not null) connectionStore.Save(saved with { AutoConnect = false });
        }
        catch { StatusBar.Text = "无法保存自动连接设置，请使用忘记已保存连接。"; }
        await DisconnectAsync();
    }
    private void ForgetConnection_Click(object sender, RoutedEventArgs e)
    {
        try { connectionStore.Forget(); RemoteCode.Clear(); StatusBar.Text = "已忘记保存的连接；当前连接不受影响，下次启动不会自动连接。"; }
        catch (Exception ex) { Error(ex); }
    }
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
        ClearAttachments(); SaveDraft(); ShowConversations(); ModelOverlay.Visibility = Visibility.Collapsed;
        int version = ++selectionVersion;
        selectedId = session.Id; Composer.Text = drafts.GetValueOrDefault(session.Id, ""); current = null; promptSignature = null; SetSessionEnabled(false);
        PromptBorder.Visibility = Visibility.Collapsed; SessionTitle.Text = session.Title; SessionMeta.Text = "正在读取远端会话…";
        try
        {
            var view = Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id = session.Id }));
            if (version != selectionVersion) return;
            Render(view); StatusBar.Text = view.StatusDetail ?? "已接入原桌面会话。发送指令会在 B 的原项目中执行。";
        }
        catch (Exception ex)
        {
            if (version != selectionVersion) return;
            selectedId = null; SessionMeta.Text = "会话连接失败，请检查远端状态或重新选择原会话"; Error(ex);
        }
    }
    private void Render(SessionView view)
    {
        if (view.Id != selectedId) return;
        current = view; ModelButton.Content = string.IsNullOrWhiteSpace(view.Model) ? "模型与模式" : (view.Mode == "plan" ? "Plan" : "Build") + " · " + view.Model;
        ModelButton.ToolTip = "模型与模式 · " + (view.Model ?? "打开会话后设置，远端需支持此功能"); SessionTitle.Text = string.IsNullOrWhiteSpace(view.Title) ? SessionTitle.Text : view.Title;
        SessionMeta.Text = $"{view.Cwd}\n" + (view.StatusDetail ?? $"{(view.Status == "active" ? "执行中" : "等待指令")} · 桌面实时同步");
        var builder = new StringBuilder();
        foreach (var message in view.Messages.Select(ChatPresentation.Display)) builder.AppendLine($"{message.Role}\n{message.Text}\n{message.Detail}\n");
        var content = builder.ToString();
        if (content != transcriptText)
        {
            bool atBottom = ChatScroll.VerticalOffset + ChatScroll.ViewportHeight >= ChatScroll.ExtentHeight - 30;
            double previous = ChatScroll.VerticalOffset;
            transcriptText = content; Messages.ItemsSource = view.Messages;
            if (atBottom) ChatScroll.ScrollToEnd(); else ChatScroll.ScrollToVerticalOffset(previous);
        }
        EmptyTitle.Text = "新会话已就绪"; EmptyDescription.Text = "在下方输入第一条消息，开始这段对话。"; EmptyConnect.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = view.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSessionEnabled(!actionBusy && remote?.IsConnected == true);
        var signature = string.Join("|", view.Prompts.Select(p => p.Id + p.Kind));
        if (signature != promptSignature) { promptSignature = signature; RenderPrompts(view.Prompts); }
    }
    private void SetSessionEnabled(bool enabled)
    {
        Composer.IsEnabled = SendButton.IsEnabled = enabled && current is not null && current.CanSend;
        UploadButton.IsEnabled = FilesButton.IsEnabled = enabled && current is not null && agent?.CanFiles == true;
        ModelButton.IsEnabled = enabled && current is not null && agent?.CanConfigure == true;
        ArchiveButton.IsEnabled = enabled && current is not null && agent?.CanArchive == true && current.Status is not ("active" or "running");
        SendButton.Content = current?.Status is "active" or "running" ? "追加指令" : "发送";
        StopButton.IsEnabled = enabled && (current?.CanStop ?? current?.Status == "active");
    }
    private async Task ActionAsync(Func<RpcPeer, Task> action)
    {
        if (remote is null || actionBusy) return;
        actionBusy = true; SessionList.IsEnabled = NewButton.IsEnabled = false; SetSessionEnabled(false);
        try { await action(remote.Peer); StatusBar.Text = "操作已由远端 Agent 接收，结果以会话状态为准。"; }
        catch (Exception ex) { Error(ex); }
        finally { actionBusy = false; SessionList.IsEnabled = NewButton.IsEnabled = true; SetSessionEnabled(remote is not null && current is not null && remote.Peer.Completion?.IsCompleted == false); }
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        string text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text) && attachedFiles.Count == 0) return;
        var ids = attachedFiles.Select(f => f.Id).ToArray(); string id = current!.Id;
        await ActionAsync(async peer =>
        {
            if (agent?.CanFiles == true) await new FileTransferClient(peer).SendAsync(id, text, ids);
            else await peer.CallAsync("send", new { text });
            if (Composer.Text == text) Composer.Clear(); ClearAttachments();
        });
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ActionAsync(async peer => { await peer.CallAsync("interrupt", null); });
    private void RenderPrompts(List<PendingPrompt> prompts)
    {
        PromptPanel.Children.Clear(); PromptBorder.Visibility = prompts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var prompt in prompts)
        {
            PromptPanel.Children.Add(new TextBlock { Text = "Agent 需要你的确认", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
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
            else if (prompt.Kind is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "hermes/approval")
            {
                PromptPanel.Children.Add(new TextBlock { Text = prompt.Kind == "hermes/approval" ? "Hermes 工具权限" : prompt.Kind.Contains("commandExecution") ? "执行命令" : "修改文件", FontWeight = FontWeights.SemiBold });
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
    private void SaveDraft() { if (selectedId is not null) drafts[selectedId] = Composer.Text; }
    private void ShowConversations() { SettingsPage.Visibility = Visibility.Collapsed; ConversationPage.Visibility = Visibility.Visible; }
    private void Conversations_Click(object sender, RoutedEventArgs e) => ShowConversations();
    private void Settings_Click(object sender, RoutedEventArgs e)
    { CloseOverlays(); ConversationPage.Visibility = Visibility.Collapsed; SettingsPage.Visibility = Visibility.Visible; }
    private void CloseOverlays() { ConnectOverlay.Visibility = NewOverlay.Visibility = ModelOverlay.Visibility = FilesOverlay.Visibility = Visibility.Collapsed; }
    private void CloseOverlay_Click(object sender, RoutedEventArgs e) { if (!actionBusy) CloseOverlays(); }
    private void OpenConnect_Click(object sender, RoutedEventArgs e)
    { CloseOverlays(); ConnectServer.Text = "中继：" + ServerUrl.Text.Trim(); ConnectError.Text = ""; ConnectOverlay.Visibility = Visibility.Visible; RemoteCode.Focus(); }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var validation = new RegistryClient(ServerUrl.Text.Trim());
            SaveServerAddress(ServerUrl.Text.Trim());
            RemoteCode.Text = connectionStore.Load(ServerUrl.Text)?.Code ?? "";
            StatusBar.Text = "服务端地址已保存，下次连接生效。";
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Composer_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && SendButton.IsEnabled) { e.Handled = true; Send_Click(sender, e); } }
    private void CopyChat_Click(object sender, RoutedEventArgs e)
    { try { if (transcriptText.Length > 0) { Clipboard.SetText(transcriptText); StatusBar.Text = "对话已复制。"; } } catch (Exception ex) { Error(ex); } }
    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (remote?.IsConnected != true) { OpenConnect_Click(sender, e); return; }
        CloseOverlays(); NewOverlay.Visibility = Visibility.Visible; NewError.Text = "";
        NewHint.Text = agent?.CanCreate == true ? "创建空会话，打开后发送第一条消息。" : "此版本远端不支持新建。Hermes 请升级至 0.3.0 接入端；Codex 请在远端桌面新建，再刷新会话列表。";
        CreateButton.IsEnabled = NewTitle.IsEnabled = agent?.CanCreate == true;
        createRequestId ??= Guid.NewGuid().ToString("N");
    }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || actionBusy || string.IsNullOrWhiteSpace(NewTitle.Text)) return;
        var connection = remote; CreateButton.IsEnabled = false; NewError.Text = "";
        actionBusy = true; SessionList.IsEnabled = false; SetSessionEnabled(false);
        try
        {
            var result = Json.Read<SessionInfo>(await connection.Peer.CallAsync("create", new { id = createRequestId, title = NewTitle.Text.Trim() }));
            if (remote != connection) return;
            createRequestId = null; SaveDraft(); current = null; promptSignature = null; selectedId = result.Id; selectionVersion++; Composer.Clear();
            sessions = new[] { result }.Concat(sessions.Where(x => x.Id != result.Id)).ToArray(); SearchBox.Clear(); Filter();
            Render(Json.Read<SessionView>(await connection.Peer.CallAsync("attach", new { id = result.Id })));
            CloseOverlays(); ShowConversations(); StatusBar.Text = "新会话已创建，可以发送第一条消息。";
        }
        catch (Exception ex) { if (current is null) selectedId = null; createRequestId ??= Guid.NewGuid().ToString("N"); NewError.Text = ex.Message; Error(ex); }
        finally { actionBusy = false; CreateButton.IsEnabled = true; SessionList.IsEnabled = true; SetSessionEnabled(remote?.IsConnected == true && current is not null); }
    }
    private async void Model_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || current is null) return;
        var connection = remote; string id = current.Id;
        CloseOverlays(); ModelOverlay.Visibility = Visibility.Visible; ModelError.Text = ""; ModelHint.Text = "正在读取远端设置…";
        ApplyModelButton.IsEnabled = false; ModelPicker.IsEnabled = ProviderText.IsEnabled = ModePicker.IsEnabled = false;
        try
        {
            var settings = Json.Read<AgentSettings>(await connection.Peer.CallAsync("settings", null));
            if (remote != connection || selectedId != id) return;
            loadingSettings = true; modelSession = id;
            ModelPicker.ItemsSource = settings.Models; ModelPicker.Text = settings.Model; ProviderText.Text = settings.Provider;
            ModePicker.SelectedIndex = settings.Mode == "plan" ? 1 : 0;
            ModelPicker.IsEnabled = settings.CanChangeModel; ProviderText.IsEnabled = settings.CanChangeModel && agent?.Id == "hermes";
            ModePicker.IsEnabled = settings.CanChangeMode; ModelHint.Text = settings.Detail;
            ApplyModelButton.IsEnabled = settings.CanChangeModel || settings.CanChangeMode;
        }
        catch (Exception ex) { ModelError.Text = ex.Message; }
        finally { loadingSettings = false; }
    }
    private void ModelSelection_Changed(object sender, SelectionChangedEventArgs e)
    { if (!loadingSettings && ModelPicker.SelectedItem is ModelOption model) ProviderText.Text = model.Provider; }
    private async void ApplyModel_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || current?.Id != modelSession || actionBusy) return;
        ApplyModelButton.IsEnabled = false; ModelError.Text = "";
        await ActionAsync(async peer =>
        {
            try
            {
                await peer.CallAsync("configure", new { model = ModelPicker.Text.Trim(), provider = ProviderText.Text.Trim(), mode = (ModePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "default" });
                ModelButton.Content = (ModePicker.SelectedIndex == 1 ? "Plan" : "Build") + " · " + ModelPicker.Text.Trim();
                CloseOverlays();
            }
            catch (Exception ex) { ModelError.Text = ex.Message; throw; }
        });
        ApplyModelButton.IsEnabled = true;
    }
    private void ClearFiles_Click(object sender, RoutedEventArgs e) { if (!actionBusy) ClearAttachments(); }
    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => transferCancellation?.Cancel();
    private async Task TransferAsync(Func<FileTransferClient, IProgress<FileProgress>, CancellationToken, Task> action)
    {
        if (remote is null || actionBusy) return;
        var connection = remote;
        using var cancel = new CancellationTokenSource(); transferCancellation = cancel;
        CancelTransferButton.Visibility = Visibility.Visible;
        await ActionAsync(async peer =>
        {
            var progress = new Progress<FileProgress>(p => { if (remote == connection) StatusBar.Text = $"文件传输：{p.Completed / 1024:N0} / {p.Total / 1024:N0} KiB"; });
            await action(new FileTransferClient(peer), progress, cancel.Token);
        });
        transferCancellation = null; CancelTransferButton.Visibility = Visibility.Collapsed;
    }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (current is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "添加文件（单文件最多 100 MiB）" };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileNames.Length + attachedFiles.Count > 20) { StatusBar.Text = "每条消息最多 20 个附件。"; return; }
        string id = current.Id;
        await TransferAsync(async (client, progress, ct) =>
        {
            foreach (var file in dialog.FileNames)
            {
                var uploaded = await client.UploadAsync(id, file, progress, ct);
                if (current?.Id != id) return;
                attachedFiles.Add(uploaded); AttachmentSummary.Text = "待发送：" + string.Join("、", attachedFiles.Select(f => f.Name));
            }
        });
    }
    private async void Files_Click(object sender, RoutedEventArgs e)
    {
        if (current is null || remote is null) return;
        string id = current.Id; var connection = remote;
        try
        {
            var workspace = await new FileTransferClient(connection.Peer).ListAsync(id);
            if (remote != connection || current?.Id != id) return;
            filesSession = id; RemoteFiles.ItemsSource = workspace.Files; FilesStatus.Text = $"共 {workspace.Files.Count} 个文件。输出目录：{workspace.Outbox}";
            CloseOverlays(); FilesOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFiles.SelectedItem is not RemoteFile file || current?.Id != filesSession) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = file.Name, Title = "保存文件（请选择不存在的文件名）", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        string id = filesSession!; CloseOverlays();
        await TransferAsync(async (client, progress, ct) => { await client.DownloadAsync(id, file.Id, dialog.FileName, progress, ct); });
    }
    private static void SaveServerAddress(string server)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        string temporary = SettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { serverUrl = server }));
        File.Move(temporary, SettingsFile, true);
    }
    private async void PasteImage_Click(object sender, RoutedEventArgs e)
    {
        if (current is null || remote is null || actionBusy || agent?.CanFiles != true) return;
        string path = Path.Combine(Path.GetTempPath(), "FurtherAgent-image-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var bitmap = Clipboard.GetImage();
            if (bitmap is null) { StatusBar.Text = "剪贴板中没有图片。"; return; }
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 40000000) throw new IOException("图片超过 4000 万像素，请先缩小。");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
            string id = current.Id;
            await TransferAsync(async (client, progress, ct) =>
            {
                var uploaded = await client.UploadAsync(id, path, progress, ct);
                attachedFiles.Add(uploaded); AttachmentSummary.Text = "待发附件：" + string.Join("、", attachedFiles.Select(f => f.Name));
            });
        }
        catch (Exception ex) { Error(ex); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private async void PreviewImage_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFiles.SelectedItem is not RemoteFile file || current?.Id != filesSession || actionBusy) return;
        if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(file.Name).ToLowerInvariant()))
        { FilesStatus.Text = "此格式不支持图片预览，请下载后打开。"; return; }
        if (file.Size > 10 * 1024 * 1024) { FilesStatus.Text = "图片预览限制为 10 MiB，请下载原文件。"; return; }
        string path = Path.Combine(Path.GetTempPath(), "FurtherAgent-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            await TransferAsync(async (client, progress, ct) =>
            {
                await client.DownloadAsync(filesSession!, file.Id, path, progress, ct);
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                using (var input = File.OpenRead(path))
                {
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(input, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.OnDemand);
                    var frame = decoder.Frames[0];
                    if ((long)frame.PixelWidth * frame.PixelHeight > 40000000) throw new IOException("预览图片超过 4000 万像素，请下载查看。");
                    int width = Math.Min(1600, frame.PixelWidth); input.Position = 0;
                    bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = width; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
                }
                var picture = new Image { Source = bitmap, Stretch = System.Windows.Media.Stretch.Uniform };
                new Window { Owner = this, Title = file.Name + " · 图片预览", Width = 800, Height = 600, Content = picture }.Show();
            });
        }
        catch (Exception ex) { Error(ex); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private void Error(Exception ex) => StatusBar.Text = "操作失败：" + ex.Message;
    private string ArchiveIndexPath => Path.Combine(Path.GetDirectoryName(SettingsFile)!, "archived-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ServerUrl.Text.TrimEnd('/') + "|" + remote?.Device?.Name))) + ".json");
    private List<SessionInfo> ArchiveIndex()
    {
        if (!File.Exists(ArchiveIndexPath)) return [];
        return JsonSerializer.Deserialize<List<SessionInfo>>(File.ReadAllText(ArchiveIndexPath), Json.Options) ?? [];
    }
    private void SaveArchiveIndex(List<SessionInfo> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ArchiveIndexPath)!);
        File.WriteAllText(ArchiveIndexPath + ".tmp", JsonSerializer.Serialize(rows, Json.Options));
        File.Move(ArchiveIndexPath + ".tmp", ArchiveIndexPath, true);
    }
    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (current is null || agent?.CanArchive != true || actionBusy) return;
        string id = current.Id;
        await ActionAsync(async peer =>
        {
            // Save the ID before PATCH so even an uncertain response remains recoverable.
            var rows = ArchiveIndex(); rows.RemoveAll(x => x.Id == id);
            rows.Add(new(id, current.Title, DateTimeOffset.Now.ToString("O"))); SaveArchiveIndex(rows);
            await peer.CallAsync("archive", new { session = id, archived = true });
            selectedId = null; current = null; Messages.ItemsSource = null; transcriptText = "";
            ClearAttachments(); Composer.Clear(); await RefreshAsync();
            EmptyTitle.Text = "会话已归档"; EmptyDescription.Text = "可通过左侧「恢复归档」找回，会话内容未删除。"; EmptyState.Visibility = Visibility.Visible;
        });
    }
    private void RestoreArchive_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || agent?.CanArchive != true || actionBusy) { StatusBar.Text = "请连接支持原生归档的接入端。"; return; }
        try
        {
            var picker = new ComboBox { IsEditable = true, ItemsSource = ArchiveIndex().Select(s => s.Id).ToArray(), Margin = new Thickness(0, 10, 0, 10) };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "选择本机记录的会话 ID，或粘贴远端会话 ID。\nHermes 当前接口不能枚举所有已归档会话。", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(picker); var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
            var button = new Button { Content = "恢复会话" }; panel.Children.Add(button);
            var dialog = new Window { Owner = this, Title = "恢复归档", Width = 600, SizeToContent = SizeToContent.Height, Content = panel };
            button.Click += async (_, _) =>
            {
                string id = picker.Text.Trim(); if (id.Length == 0 || remote is null || actionBusy) return;
                button.IsEnabled = false;
                await ActionAsync(async peer =>
                {
                    SaveDraft(); ClearAttachments(); selectedId = null; current = null; Messages.ItemsSource = null; transcriptText = "";
                    await peer.CallAsync("attach", new { id });
                    await peer.CallAsync("archive", new { session = id, archived = false });
                    var rows = ArchiveIndex(); rows.RemoveAll(x => x.Id == id); SaveArchiveIndex(rows);
                    selectedId = null; current = null; await RefreshAsync(); dialog.Close();
                });
                error.Text = StatusBar.Text; button.IsEnabled = true;
            };
            dialog.ShowDialog();
        }
        catch (Exception ex) { Error(ex); }
    }
    public void RestoreFromTray()
    {
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void DisposeTray()
    {
        if (tray is null) return;
        tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Icon?.Dispose(); tray.Dispose(); tray = null;
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (!exitRequested) { Hide(); return; }
        if (!IsEnabled) return;
        IsEnabled = false;
        try
        {
            transferCancellation?.Cancel();
            await DisconnectAsync();
            if (host is not null) await host.DisposeAsync();
            hostRegistry?.Dispose();
        }
        finally { DisposeTray(); closing = true; Application.Current.Shutdown(); }
    }
}
