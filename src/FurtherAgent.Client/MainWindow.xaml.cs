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
    private IAgentAdapter? localAdapter;
    private DshBackgroundRuntime? dshBackground;
    private string localHostName = Environment.MachineName;
    private RegistryClient? hostRegistry, remoteRegistry;
    private RemoteDevice? remote;
    private IReadOnlyList<SessionInfo> sessions = [];
    private SessionView? current;
    private string? selectedId, promptSignature;
    private int selectionVersion;
    private bool closing, actionBusy, loadingSettings;
    private System.Windows.Forms.NotifyIcon? tray;
    private bool exitRequested;
    private bool startSavedConnection;
    private string? connectionFailure;
    private CancellationTokenSource? startupRetry;
    private readonly object snapshotGate = new();
    private (RemoteDevice Connection, SessionView View)? pendingSnapshot;
    private readonly System.Windows.Threading.DispatcherTimer snapshotTimer;
    private DateTimeOffset? lastRenderedAt;
    private ConnectionHeartbeat? heartbeat;
    private AgentInfo? agent;
    private string? createRequestId, modelSession;
    private List<PermissionOption> modelPermissionOptions = [];
    private int modelSettingsGeneration;
    private string transcriptText = "";
    private CancellationTokenSource? transferCancellation;
    private List<RemoteFile> attachedFiles = [];
    private string? filesSession;
    private void ClearAttachments() { attachedFiles.Clear(); RenderAttachments(); }

    private Dictionary<string, string> drafts = [];
    private static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "settings.json");
    private readonly SavedConnectionStore connectionStore = new(Path.Combine(Path.GetDirectoryName(SettingsFile)!, "connection.json"));
    public MainWindow() : this(null) { }
    public MainWindow(CompletionNotificationServices? notificationServices)
    {
        completionServices = notificationServices;
        InitializeComponent();
        InitializeCompact();
        InitializeCompactHotkey();
        InitializeHistory();
        InitializeAttachments();
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        Title = "FurtherAgent · " + version; VersionLabel.Text = version + " · 远程工作空间";
        snapshotTimer = new(System.Windows.Threading.DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        snapshotTimer.Tick += (_, _) =>
        {
            RenderConnectionHealth();
            (RemoteDevice Connection, SessionView View)? next;
            lock (snapshotGate) { next = pendingSnapshot; pendingSnapshot = null; }
            if (next is { } update && remote == update.Connection && selectedId == update.View.Id) { Render(update.View); lastRenderedAt = DateTimeOffset.UtcNow; }
        };
        snapshotTimer.Start();
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/FurtherAgent.Client;component/Assets/FurtherAgent.ico")).Stream)
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
            if (File.Exists(SettingsFile))
            {
                var saved = JsonNode.Parse(File.ReadAllText(SettingsFile));
                ServerUrl.Text = saved?["serverUrl"]?.ToString() ?? ServerUrl.Text;
                LocalBackend.SelectedIndex = Math.Clamp(saved?["localBackend"]?.GetValue<int>() ?? 0, 0, 2);
                DshExecutable.Text = saved?["dshExecutable"]?.ToString() ?? DshExecutable.Text;
            }
        }
        catch { StatusBar.Text = "保存的地址无法读取，请在设置中重新保存。"; }
        InitializeDshExecutable();
        try { uiTheme = File.Exists(SettingsFile) ? VisualTheme.Normalize(JsonNode.Parse(File.ReadAllText(SettingsFile))?["uiTheme"]?.ToString()) : VisualTheme.Codex; }
        catch { uiTheme = VisualTheme.Codex; }
        ApplyTheme(uiTheme);
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        ServerUrl.Text = Environment.GetEnvironmentVariable("FURTHERAGENT_SERVER") ?? ServerUrl.Text;
        try
        {
            var saved = connectionStore.Load(ServerUrl.Text);
            if (saved is not null)
            {
                RemoteCode.Text = saved.Code;
                ConnectionName.Text = saved.Name;
                RemoteStatus.Text = "已记住设备 · " + saved.Name;
                startSavedConnection = saved.AutoConnect;
            }
        }
        catch (Exception ex) { connectionFailure = ex.GetType().Name; StatusBar.Text = "已保存连接无法解密，请重新输入授权码连接；原记录未删除。"; }
    }
    private void InitializeDshExecutable()
    {
        if (!string.IsNullOrWhiteSpace(DshExecutable.Text))
        {
            DshExecutableHint.Text = File.Exists(DshExecutable.Text.Trim()) ? "使用已保存的程序路径。" : "已保存的程序路径不可用，请重新选择 DeepSeek Harness.exe。";
            return;
        }
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                string path = Path.GetFullPath(value.Trim().Trim('"'));
                if (Path.GetFileName(path).Equals("DeepSeek Harness.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) candidates.Add(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException or IOException) { }
        }
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("DeepSeek Harness"))
        {
            using (process)
                try { AddCandidate(process.MainModule?.FileName); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            try
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\DeepSeek Harness.exe");
                AddCandidate(key?.GetValue(null) as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        AddCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DeepSeek Harness", "DeepSeek Harness.exe"));
        AddCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeepSeek Harness", "DeepSeek Harness.exe"));
        AddCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "DeepSeek Harness", "DeepSeek Harness.exe"));
        if (candidates.Count == 1)
        {
            DshExecutable.Text = candidates.Single();
            DshExecutableHint.Text = "已发现 DSH 程序；后台模式将使用此程序的运行时。";
        }
        else DshExecutableHint.Text = candidates.Count == 0 ? "未发现 DSH 程序；使用后台模式前请选择 DeepSeek Harness.exe。" : "发现多个 DSH 程序，请明确选择后台模式使用的版本。";
    }
    private void ChooseDshExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择 DeepSeek Harness 程序", Filter = "DeepSeek Harness|DeepSeek Harness.exe|可执行程序|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        DshExecutable.Text = dialog.FileName;
        DshExecutableHint.Text = "已选择程序；开启共享或保存设置时会记住此路径。";
    }
    public async void StartSavedConnection()
    {
        if (!startSavedConnection) return;
        startupRetry = new CancellationTokenSource();
        try
        {
            for (int attempt = 0; attempt < 4 && remote?.IsConnected != true; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(5 * attempt), startupRetry.Token);
                startupRetry.Token.ThrowIfCancellationRequested();
                if (!ConnectButton.IsEnabled) return;
                await ConnectAsync();
            }
        }
        catch (OperationCanceledException) { }
    }
    public object Control(string command)
    {
        if (command == "status") return new { success = true, processId = Environment.ProcessId,
            executable = Environment.ProcessPath, version = typeof(App).Assembly.GetName().Version?.ToString(3),
            ready = true, connected = remote?.IsConnected == true, device = remote?.Device?.Name,
            stateDirectory = Path.GetDirectoryName(SettingsFile), autoConnect = startSavedConnection, uiTheme,
            connecting = !ConnectButton.IsEnabled, error = connectionFailure, visible = IsVisible, connectedAt = remote?.ConnectedAt, disconnectedAt = remote?.DisconnectedAt, reconnectCount = remote?.ReconnectCount, lastReceivedAt = remote?.Peer?.LastReceivedAt, receivedMessages = remote?.Peer?.ReceivedMessages, lastRenderedAt,
            relayHeartbeat = heartbeat?.Relay, hostHeartbeat = heartbeat?.Host, activity = ActivityStatus.Text,
            receivedBytes = remote?.ReceivedBytes, sentBytes = remote?.SentBytes, miniWindows = miniWindows.Count };
        if (command != "shutdown") return new { success = false, error = "unknown_command" };
        if (actionBusy || preparingAttachment || transferCancellation is not null || attachedFiles.Count > 0 || !string.IsNullOrEmpty(Composer.Text) || drafts.Values.Any(t => !string.IsNullOrEmpty(t))
            || miniWindows.Values.Any(mini => mini.IsSending || mini.HasUnsentDraft)
            || deviceConnections.Entries.Values.Any(d => d.Attachments.Count > 0 || d.Drafts.Values.Any(t => !string.IsNullOrEmpty(t))))
            return new { success = false, error = "busy_or_unsent_draft: client left running" };
        startupRetry?.Cancel();
        _ = Task.Delay(200).ContinueWith(_ => Dispatcher.BeginInvoke(() => { exitRequested = true; Close(); }));
        return new { success = true };
    }
    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        ShareButton.IsEnabled = false;
        try
        {
            if (host is not null) { if (dshBackground is not null) { await dshBackground.DisposeAsync(); dshBackground = null; } await host.DisposeAsync(); host = null; hostRegistry?.Dispose(); await DisposeLocalAdapterAsync(); LocalBackend.IsEnabled = true; LocalCode.Text = "尚未开启共享"; CopyCodeButton.IsEnabled = false; HostStatus.Text = "共享已停止"; ShareButton.Content = "开启共享"; }
            else
            {
                hostRegistry = new RegistryClient(ServerUrl.Text);
                SaveServerAddress(ServerUrl.Text.Trim());
                localHostName = Environment.MachineName;
                Func<IAgentAdapter>? adapterFactory = null;
                if (LocalBackend.SelectedIndex > 0)
                {
                    string descriptor;
                    if (LocalBackend.SelectedIndex == 2)
                    {
                        dshBackground ??= await DshBackgroundRuntime.StartAsync(DshExecutable.Text);
                        descriptor = dshBackground.DescriptorPath;
                    }
                    else descriptor = DshAdapter.FindDescriptor("desktop");
                    var dsh = new DshAdapter(descriptor, followDesktopRestarts: LocalBackend.SelectedIndex == 1); localAdapter = dsh;
                    bool followRestarts = LocalBackend.SelectedIndex == 1;
                    adapterFactory = () => new DshAdapter(descriptor, followRestarts);
                    await dsh.ProbeAsync();
                    localHostName += LocalBackend.SelectedIndex == 2 ? " · DSH 后台" : " · DSH 桌面";
                }
                host = new DeviceHost(hostRegistry, adapterFactory, persistentIdentity: true);
                host.Status += message => Dispatcher.BeginInvoke(() => HostStatus.Text = message);
                host.CodeChanged += code => Dispatcher.BeginInvoke(() => { LocalCode.Text = DeviceIdentity.DisplayCode(code); CopyCodeButton.IsEnabled = true; });
                try { await host.StartAsync(localHostName); LocalBackend.IsEnabled = false; }
                catch { await host.DisposeAsync(); host = null; throw; }
                LocalCode.Text = DeviceIdentity.DisplayCode(host.Lease!.Code);
                CopyCodeButton.IsEnabled = true;
                HostStatus.Text = "等待连接。请将上方授权码发送给 A。";
                ShareButton.Content = "停止共享";
            }
        }
        catch (Exception ex)
        {
            if (host is null)
            {
                hostRegistry?.Dispose(); hostRegistry = null;
                try { await DisposeLocalAdapterAsync(); }
                catch (Exception cleanup) { Error(new IOException(ex.Message + "；清理状态：" + cleanup.Message)); return; }
                LocalBackend.IsEnabled = true;
            }
            Error(ex);
        }
        finally { ShareButton.IsEnabled = true; }
    }
    private async Task DisposeLocalAdapterAsync()
    {
        if (localAdapter is not null) { await localAdapter.DisposeAsync(); localAdapter = null; }
        if (dshBackground is not null) { await dshBackground.DisposeAsync(); dshBackground = null; }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        startupRetry?.Cancel();
        await ConnectAsync();
    }
    private async Task ConnectAsync()
    {
        if (!ConnectButton.IsEnabled || !CanSwitchDevice) return;
        string server = ServerUrl.Text.Trim(), code = RemoteCode.Text.Trim();
        ConnectButton.IsEnabled = false; ConnectError.Text = ""; connectionFailure = null;
        try
        {
            RemoteStatus.Text = "正在通过授权码建立中继连接…";
            var device = await OpenDeviceAsync(server, code);
            if (exitRequested) return;
            if (!string.IsNullOrWhiteSpace(ConnectionName.Text)) device.Name = ConnectionName.Text.Trim();
            ActivateDevice(device); UpdateDevicePicker();
            try
            {
                SaveServerAddress(server);
                connectionStore.Save(new(server, code, device.Name, true));
            }
            catch { StatusBar.Text += " 连接已建立，但本机连接记录保存失败，下次启动需重新输入。"; }
        }
        catch (Exception ex) { Error(ex); connectionFailure = ex.GetType().Name + ": " + (code.Length == 0 ? ex.Message : ex.Message.Replace(code, "[REDACTED]")); ConnectError.Text = ex.Message; RemoteStatus.Text = "新连接失败，已有连接保持。请检查地址、授权码及远端共享状态。"; }
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
            await DisposeLocalAdapterAsync(); LocalBackend.IsEnabled = true;
            DeviceIdentity.Reset(DeviceIdentity.DefaultPath(server, localHostName));
            LocalCode.Text = "身份已重置，下次开启共享后显示新码"; CopyCodeButton.IsEnabled = false;
            ShareButton.Content = "开启共享"; HostStatus.Text = "共享已停止，旧配对码已失效。";
        }
        catch (Exception ex) { Error(ex); }
    }
    private async Task DisconnectAsync()
    {
        var disconnected = activeDevice;
        if (disconnected is not null) CloseMinisForDevice(disconnected);
        SaveActiveDevice(); activeDevice = null; heartbeat = null;
        transferCancellation?.Cancel(); ClearAttachments();
        SaveDraft(); agent = null; ModelOverlay.Visibility = Visibility.Collapsed;
        selectionVersion++; selectedId = null; current = null; promptSignature = null;
        remote = null;
        if (disconnected is not null) await deviceConnections.CloseAsync(DeviceConnectionPool<DeviceContext>.Identity(disconnected.Server, disconnected.Code));
        remoteRegistry = null; drafts = []; attachedFiles = [];
        sessions = []; workspaces = []; workspaceGroups = []; SessionList.ItemsSource = null; WorkspaceButton.Visibility = Visibility.Collapsed;
        DisconnectButton.IsEnabled = RefreshButton.IsEnabled = false; SetSessionEnabled(false);
        PromptBorder.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = "你的 Agent，随时继续。"; EmptyDescription.Text = "连接设备，选择已有会话或新建会话。"; EmptyConnect.Visibility = Visibility.Visible;
        RemoteStatus.Text = "尚未连接"; SessionTitle.Text = "选择远端会话，继续工作";
        SessionMeta.Text = "连接后显示远端会话历史与可用的运行状态"; Messages.ItemsSource = null; transcriptText = ""; OutputFilesPanel.Children.Clear(); outputFilesFingerprint = renderedOutputFilesFingerprint = ""; Composer.Clear(); EmptyState.Visibility = Visibility.Visible;
        if (!exitRequested && deviceConnections.Entries.Values.LastOrDefault() is { } next) ActivateDevice(next);
        UpdateDevicePicker();
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        startupRetry?.Cancel();
        if (!CanSwitchDevice || !ConnectButton.IsEnabled) { StatusBar.Text = "请等待当前操作结束再断开设备。"; return; }
        try
        {
            var saved = activeDevice is null ? null : connectionStore.List().FirstOrDefault(s =>
                DeviceConnectionPool<DeviceContext>.Identity(s.Server, s.Code) == DeviceConnectionPool<DeviceContext>.Identity(activeDevice.Server, activeDevice.Code));
            if (saved is not null) connectionStore.Save(saved with { AutoConnect = false });
        }
        catch { StatusBar.Text = "无法保存自动连接设置，请使用忘记已保存连接。"; }
        await DisconnectAsync();
    }
    private void ForgetConnection_Click(object sender, RoutedEventArgs e)
    {
        startupRetry?.Cancel();
        try { connectionStore.Forget(); RemoteCode.Clear(); StatusBar.Text = "已忘记保存的连接；当前连接不受影响，下次启动不会自动连接。"; }
        catch (Exception ex) { Error(ex); }
    }
    private async Task RefreshAsync()
    {
        if (remote is null) return;
        var connection = remote;
        var loaded = Json.Read<List<SessionInfo>>(await connection.Peer.CallAsync("sessions", null));
        if (remote != connection) return;
        sessions = loaded;
        if (agent?.CanWorkspaces == true)
        {
            try { await LoadWorkspacesAsync(connection); }
            catch (Exception ex) { StatusBar.Text = "会话已加载；工作空间目录读取失败：" + ex.Message; }
        }
        Filter();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    { try { await RefreshAsync(); } catch (Exception ex) { Error(ex); } }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (SessionList is not null) Filter(); }
    private void Filter()
    {
        workspaceGroups = WorkspaceGroup.Build(sessions, workspaces, SearchBox.Text, selectedId, workspaceGroups);
        SessionList.ItemsSource = workspaceGroups;
    }
    private async void Session_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (remote is null || SessionList.SelectedItem is not WorkspaceSession row || row.Info.Id == selectedId) return;
        var connection = remote; var device = activeDevice;
        var session = row.Info;
        ClearAttachments(); SaveDraft(); ShowConversations(); ModelOverlay.Visibility = Visibility.Collapsed;
        int version = ++selectionVersion;
        selectedId = session.Id; Composer.Text = drafts.GetValueOrDefault(session.Id, ""); current = null; promptSignature = null; SetSessionEnabled(false);
        PromptBorder.Visibility = Visibility.Collapsed; SessionTitle.Text = session.Title; SessionMeta.Text = "正在读取远端会话…";
        try
        {
            var view = Json.Read<SessionView>(await connection.Peer.CallAsync("attach", new { id = session.Id }));
            if (version != selectionVersion)
            {
                if (device is not null && device != activeDevice && device.SelectedId == session.Id) device.View = view;
                else if (remote == connection && selectedId == session.Id) Render(view);
                return;
            }
            Render(view); StatusBar.Text = view.StatusDetail ?? "已接入原桌面会话。发送指令会在 B 的原项目中执行。";
        }
        catch (Exception ex)
        {
            if (version != selectionVersion) return;
            selectedId = null; SessionMeta.Text = "会话连接失败，请检查远端状态或重新选择原会话"; Error(ex);
        }
    }
    private void RenderConnectionHealth()
    {
        if (activeDevice is not null) activeDevice.View = History.Latest ?? current;
        UpdateDevicePicker();
        var indicator = AgentIndicator.From(remote?.IsConnected == true,
            !ConnectButton.IsEnabled || remote?.IsReconnecting == true, current);
        ConnectionStateLabel.Text = indicator.Label;
        ConnectionStateDot.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(indicator.Color)!;
        static string Describe(string label, HeartbeatState state)
        {
            string age = state.LastSuccess is { } last ? $" · {(int)Math.Max(0, (DateTimeOffset.UtcNow - last).TotalSeconds)} 秒前确认" : " · 尚无确认";
            return label + "：" + state.State + (state.Milliseconds is { } ms ? $" · {ms} ms" : "") + age;
        }
        RelayHealth.Text = heartbeat is { } h ? Describe("中继", h.Relay) : "中继：未连接";
        HostHealth.Text = heartbeat is { } b ? Describe("客户端 B", b.Host) : "客户端 B：未连接";
        static string Bytes(long value) => value >= 1048576 ? $"{value / 1048576d:F2} MiB" : $"{value / 1024d:F1} KiB";
        TrafficStatus.Text = remote is null ? "" : $"当前连接 ↑ {Bytes(remote.SentBytes)}  ↓ {Bytes(remote.ReceivedBytes)}";
        RelayHealth.Foreground = heartbeat?.Relay.State == "在线" ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.DarkGoldenrod;
        HostHealth.Foreground = heartbeat?.Host.State == "在线" ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.DarkGoldenrod;
        ActivityStatus.Text = !ConnectButton.IsEnabled ? "正在连接设备…"
            : remote?.IsConnected != true ? "连接未就绪"
            : transferCancellation is not null ? "正在传输文件…"
            : actionBusy ? operationPhase ?? "正在提交操作，等待远端确认…"
            : current is null ? (selectedId is null ? "已连接 · 请选择会话" : "正在加载会话…")
            : current.Prompts.Count > 0 ? "等待你的审批或回答"
            : current.Status is "active" or "running" ? "Agent 正在执行 / 生成回复…"
            : "会话已加载 · 等待指令（其他入口运行状态不可见）";
    }
    private void Render(SessionView view) => RenderSnapshot(view, false);
    private void RenderSnapshot(SessionView view, bool historyNavigation)
    {
        if (view.Id != selectedId) return;
        if (!historyNavigation) view = History.Accept(view);
        current = view; UpdateHistoryActions(); ModelButton.Content = string.IsNullOrWhiteSpace(view.Model) ? "模型与模式" : (view.Mode == "plan" ? "Plan" : "Build") + " · " + view.Model;
        RenderOutputFiles(view);
        ModelButton.ToolTip = "模型与模式 · " + (view.Model ?? "打开会话后设置，远端需支持此功能"); SessionTitle.Text = string.IsNullOrWhiteSpace(view.Title) ? SessionTitle.Text : view.Title;
        SessionMeta.Text = $"{view.Cwd}\n" + (view.StatusDetail ?? $"{(view.Status == "active" ? "执行中" : "等待指令")} · 桌面实时同步");
        var builder = new StringBuilder();
        foreach (var message in view.Messages.Select(ChatPresentation.Display)) builder.AppendLine($"{message.Role}\n{message.Text}\n{message.Detail}\n{(message.Operations is null ? "" : Json.Node(message.Operations).ToJsonString())}\n");
        var content = builder.ToString();
        if (content != transcriptText || renderedOutputFilesFingerprint != outputFilesFingerprint)
        {
            bool atBottom = ChatScroll.VerticalOffset + ChatScroll.ViewportHeight >= ChatScroll.ExtentHeight - 30;
            double previous = ChatScroll.VerticalOffset;
            transcriptText = content; renderedOutputFilesFingerprint = outputFilesFingerprint; Messages.ItemsSource = OperationGroups.Group(view.Messages);
            if (!historyNavigation) { if (atBottom && !History.ReadingOlder) ChatScroll.ScrollToEnd(); else ChatScroll.ScrollToVerticalOffset(previous); }
        }
        EmptyTitle.Text = "新会话已就绪"; EmptyDescription.Text = "在下方输入第一条消息，开始这段对话。"; EmptyConnect.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = view.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSessionEnabled(!actionBusy && remote?.IsConnected == true);
        var signature = string.Join("|", view.Prompts.Select(p => p.Id + p.Kind + p.Payload?.ToJsonString()));
        if (signature != promptSignature) { promptSignature = signature; RenderPrompts(view.Prompts); }
    }
    private readonly HashSet<string> expandedOperationGroups = [];
    internal string? OperationGroupKey(ChatEntry entry)
    {
        int index = Messages.Items.IndexOf(entry);
        return index < 0 || current is null ? null : $"{remote?.Device?.Name}|{current.Id}|{(current.History?.Start ?? 0) + index}";
    }
    internal bool OperationGroupExpanded(string? key) => key is not null && expandedOperationGroups.Contains(key);
    internal void SetOperationGroupExpanded(string? key, bool expanded)
    {
        if (key is null) return;
        if (expanded) { if (expandedOperationGroups.Count > 1024) expandedOperationGroups.Clear(); expandedOperationGroups.Add(key); }
        else expandedOperationGroups.Remove(key);
    }
    private string? operationPhase;
    private string outputFilesFingerprint = "";
    private string renderedOutputFilesFingerprint = "";
    private readonly Dictionary<string, Task<System.Windows.Media.Imaging.BitmapSource>> thumbnailCache = [];
    private readonly SemaphoreSlim thumbnailSlots = new(2);
    public FrameworkElement? CreateFileVisual(string reference)
    {
        if (current is null || remote is null) return null;
        var file = RemoteFileReferences.Resolve(current.OutputFiles, reference);
        return file is null ? null : CreateFileVisual(current.Id, file);
    }
    private FrameworkElement CreateFileVisual(string session, RemoteFile file)
    {
        var source = remote;
        var button = new Button { Content = file.Name, ToolTip = file.Name + " · 点击加载原图/保存文件", Padding = new Thickness(3), Margin = new Thickness(0,4,4,4), MaxWidth = 330 };
        System.Windows.Automation.AutomationProperties.SetName(button, file.Name + " · 查看图片或保存文件");
        bool picture = ImageThumbnail.Supported(file.Name);
        button.Click += async (_, _) =>
        {
            if (current?.Id != session || remote != source || actionBusy) return;
            if (picture) await PreviewRemoteFileAsync(session, file); else await SaveRemoteFileAsync(session, file);
        };
        if (picture && ImageThumbnail.CanRequest(agent) && remote is { } connection)
        {
            button.Content = "加载缩略图…";
            button.Loaded += async (_, _) =>
            {
                try
                {
                    string key = connection.GetHashCode() + "|" + session + "|" + file.Id + "|" + file.Version;
                    if (!thumbnailCache.TryGetValue(key, out var load))
                    {
                        if (thumbnailCache.Count >= 256) thumbnailCache.Remove(thumbnailCache.Keys.First());
                        thumbnailCache[key] = load = LoadThumbnailAsync(connection, session, file.Id);
                    }
                    var bitmap = await load;
                    if (button.IsLoaded && remote == source) button.Content = new Image { Source = bitmap, MaxWidth = 320, MaxHeight = 240, Stretch = System.Windows.Media.Stretch.Uniform };
                }
                catch
                {
                    string key = connection.GetHashCode() + "|" + session + "|" + file.Id + "|" + file.Version;
                    thumbnailCache.Remove(key);
                    button.Content = file.Name + "（缩略图不可用，点击查看原图）";
                }
            };
        }
        var menu = new ContextMenu();
        var saveItem = new MenuItem { Header = "另存为…" };
        saveItem.Click += async (_, _) => { if (remote == source && current?.Id == session && !actionBusy) await SaveRemoteFileAsync(session, file); };
        menu.Items.Add(saveItem); button.ContextMenu = menu;
        return button;
    }
    private async Task<System.Windows.Media.Imaging.BitmapSource> LoadThumbnailAsync(RemoteDevice connection, string session, string file)
    {
        await thumbnailSlots.WaitAsync();
        try
        {
            var thumbnail = await new FileTransferClient(connection.Peer).ThumbnailAsync(session, file);
            if (thumbnail.Data.Length > (ImageThumbnail.MaxEncodedBytes+2)/3*4) throw new IOException("缩略图数据超过限制。");
            using var stream = new MemoryStream(Convert.FromBase64String(thumbnail.Data));
            var probe = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.OnDemand).Frames[0];
            if (probe.PixelWidth > 320 || probe.PixelHeight > 240) throw new IOException("缩略图尺寸超过限制。");
            stream.Position = 0;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 320; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            return bitmap;
        }
        finally { thumbnailSlots.Release(); }
    }
    private void RenderOutputFiles(SessionView view)
    {
        var referenced = view.OutputFiles?.Any(f => ImageThumbnail.Supported(f.Name)) == true ? view.Messages.Select(ChatPresentation.Display)
            .Where(m => ChatPresentation.Kind(m) is "user" or "assistant")
            .SelectMany(m => MarkdownView.AttachmentReferences(m.Text.Length > 30000 ? m.Text[..30000] : m.Text))
            .Select(r => RemoteFileReferences.Resolve(view.OutputFiles, r)?.Id).OfType<string>().ToHashSet(StringComparer.Ordinal) : [];
        string signature = view.Id + "|" + Json.Node(view.OutputFiles ?? []).ToJsonString() + "|" + string.Join("|", referenced.Order());
        if (signature == outputFilesFingerprint) return;
        bool sameSession = outputFilesFingerprint.StartsWith(view.Id + "|", StringComparison.Ordinal);
        bool expanded = sameSession && OutputFilesPanel.Children.OfType<Expander>().FirstOrDefault()?.IsExpanded == true;
        outputFilesFingerprint = signature; OutputFilesPanel.Children.Clear();
        if (view.OutputFiles is not { Count: > 0 }) return;
        var pictures = view.OutputFiles.Where(f => f.Id.StartsWith("outbox/", StringComparison.Ordinal) && ImageThumbnail.Supported(f.Name) && !referenced.Contains(f.Id)).ToArray();
        if (pictures.Length > 0)
        {
            OutputFilesPanel.Children.Add(new TextBlock { Text = "会话图片 · 点击查看原图，右键另存为", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 6, 0, 2) });
            var gallery = new WrapPanel();
            foreach (var file in pictures) gallery.Children.Add(CreateFileVisual(view.Id, file));
            OutputFilesPanel.Children.Add(gallery);
        }
        var fileRows = new StackPanel();
        OutputFilesPanel.Children.Add(new Expander { Header = $"会话文件 · {view.OutputFiles.Count} 个", IsExpanded = expanded,
            Content = new ScrollViewer { Content = fileRows, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        foreach (var file in view.OutputFiles ?? [])
        {
            var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
            row.Children.Add(new TextBlock { Text = file.DisplayName, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) });
            var save = new Button { Content = "保存文件", Style = (Style)FindResource("Compact") };
            save.Click += async (_, _) => { if (current?.Id == view.Id && !actionBusy) await SaveRemoteFileAsync(view.Id, file); };
            row.Children.Add(save);
            if (ImageThumbnail.Supported(file.Name))
            {
                var preview = new Button { Content = "查看图片", Style = (Style)FindResource("Compact") };
                preview.Click += async (_, _) => { if (current?.Id == view.Id && !actionBusy) await PreviewRemoteFileAsync(view.Id, file); };
                row.Children.Add(preview);

            }
            fileRows.Children.Add(row);
        }
    }
    private string DeliveryMode => agent?.CanChooseDelivery != true ? "auto" : DeliveryPicker.SelectedIndex switch { 1 => "queue", 2 => "steer", _ => "auto" };
    private void Delivery_Changed(object sender, SelectionChangedEventArgs e)
    { if (IsLoaded) SetSessionEnabled(!actionBusy && remote?.IsConnected == true); }
    private void SetSessionEnabled(bool enabled)
    {
        enabled &= !preparingAttachment;
        UpdateHistoryActions();
        SendButton.IsEnabled = enabled && current is not null && current.CanSend;
        InterruptSendButton.IsEnabled = enabled && (agent?.Id == "hermes" || agent?.CanInterruptSend == true) && current?.CanStop == true && current.TurnId is not null;
        Composer.IsEnabled = SendButton.IsEnabled || InterruptSendButton.IsEnabled;
        PasteImageButton.IsEnabled = UploadButton.IsEnabled = FilesButton.IsEnabled = enabled && current is not null && agent?.CanFiles == true;
        ModelButton.IsEnabled = enabled && current is not null && agent?.CanConfigure == true;
        ArchiveButton.IsEnabled = enabled && current is not null && agent?.CanArchive == true && current.Status is not ("active" or "running");
        DeliveryPicker.Visibility = agent?.CanChooseDelivery == true ? Visibility.Visible : Visibility.Collapsed;
        DeliveryPicker.IsEnabled = enabled;
        SendButton.Content = DeliveryMode == "queue" ? "排队发送" : DeliveryMode == "steer" || current?.Status is "active" or "running" ? "追加指令" : "发送";
        StopButton.IsEnabled = enabled && (current?.CanStop ?? current?.Status == "active");
    }
    private async Task ActionAsync(Func<RpcPeer, Task> action)
    {
        if (remote is null || actionBusy) return;
        actionBusy = true; SessionList.IsEnabled = NewButton.IsEnabled = false; SetSessionEnabled(false);
        try { await action(remote.Peer); StatusBar.Text = "操作已由远端 Agent 接收，结果以会话状态为准。"; }
        catch (Exception ex) { Error(ex); }
        finally { operationPhase = null; actionBusy = false; SessionList.IsEnabled = NewButton.IsEnabled = true; SetSessionEnabled(remote is not null && current is not null && remote.Peer.Completion?.IsCompleted == false); }
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        string text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text) && attachedFiles.Count == 0) return;
        var ids = attachedFiles.Select(f => f.Id).ToArray(); string id = current!.Id;
        var connection = remote; var sentDrafts = drafts; var sentAttachments = attachedFiles;
        string delivery = DeliveryMode;
        await ActionAsync(async peer =>
        {
            StatusBar.Text = "正在提交消息，等待远端确认…";
            Task submission = agent?.CanFiles == true
                ? new FileTransferClient(peer).SendAsync(id, text, ids, responseTimeout: TimeSpan.FromMinutes(3))
                : peer.CallAsync("send", new { text, delivery }, responseTimeout: TimeSpan.FromMinutes(3));
            if (await Task.WhenAny(submission, Task.Delay(TimeSpan.FromSeconds(12))) != submission)
                StatusBar.Text = "远端确认较慢，仍在等待原请求；请勿重复发送。";
            try { await submission; }
            catch (TimeoutException)
            { throw new IOException("远端确认超时，发送结果未知，不代表发送失败。原请求可能仍在处理；程序没有重发，请先查看会话结果，不要重复提交。"); }
            if (sentDrafts.GetValueOrDefault(id) == text) sentDrafts[id] = "";
            sentAttachments.RemoveAll(f => ids.Contains(f.Id));
            if (remote == connection && selectedId == id)
            {
                if (Composer.Text == text) { Composer.Clear(); SaveDraft(); }
                RenderAttachments();
            }
        });
    }
    private async void InterruptSend_Click(object sender, RoutedEventArgs e)
    {
        if (current is null || agent is null) return;
        var target = current; var selectedAgent = agent; string text = Composer.Text;
        var connection = remote; var sentDrafts = drafts; var sentAttachments = attachedFiles; string id = target.Id;
        var ids = attachedFiles.Select(f => f.Id).ToArray();
        if (string.IsNullOrWhiteSpace(text) && ids.Length == 0) return;
        await ActionAsync(async peer =>
        {
            await SessionActions.InterruptAndSendAsync(peer, selectedAgent, target, text, ids,
                phase => { operationPhase = phase; StatusBar.Text = phase; RenderConnectionHealth(); });
            if (sentDrafts.GetValueOrDefault(id) == text) sentDrafts[id] = "";
            sentAttachments.RemoveAll(f => ids.Contains(f.Id));
            if (remote == connection && selectedId == id)
            {
                if (Composer.Text == text) { Composer.Clear(); SaveDraft(); }
                RenderAttachments();
            }
        });
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ActionAsync(async peer => { await peer.CallAsync("interrupt", null); });
    private void RenderPrompts(List<PendingPrompt> prompts)
    {
        PromptPanel.Children.Clear(); PromptBorder.Visibility = prompts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var prompt in prompts)
        {
            PromptPanel.Children.Add(new TextBlock { Text = prompt.Kind is "item/tool/requestUserInput" or CodexQuestions.AsyncKind ? "Agent 需要你的回答" : "Agent 需要你的确认", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            if (prompt.Kind == CodexRecovery.Kind)
            {
                PromptPanel.Children.Add(new TextBlock { Text = prompt.Title + "\n" + prompt.Detail, TextWrapping = TextWrapping.Wrap });
                var retry = new Button { Content = "继续任务（新一轮）" };
                retry.Click += async (_, _) => await ActionAsync(async peer => { await peer.CallAsync("respond", new { id = prompt.Id, decision = "continue" }); });
                PromptPanel.Children.Add(retry);
            }
            else if (prompt.Kind is "item/tool/requestUserInput" or CodexQuestions.AsyncKind && prompt.Payload?["questions"] is JsonArray questions)
            {
                var answers = new Dictionary<string, Func<string[]>>();
                foreach (var q in questions)
                {
                    string? header = q?["header"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(header))
                        PromptPanel.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                    PromptPanel.Children.Add(new TextBlock { Text = q?["question"]?.ToString(), TextWrapping = TextWrapping.Wrap });
                    string? detail = q?["detail"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(detail))
                    {
                        PromptPanel.Children.Add(new TextBlock { Text = "请阅读以下内容后回答：", FontSize = 12 });
                        PromptPanel.Children.Add(new TextBox { Text = detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                            MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
                    }
                    bool multiSelect = q?["multiSelect"]?.GetValue<bool>() == true || q?["multiple"]?.GetValue<bool>() == true;
                    var answer = new TextBox { ToolTip = multiSelect ? "勾选多个选项，也可以补充填写回答" : "点击选项，或直接输入你的回答", MaxLength = 4000 };
                    var checkedOptions = new List<CheckBox>();
                    if (q?["options"] is JsonArray options)
                    {
                        var choices = new WrapPanel();
                        foreach (var option in options)
                        {
                            string label = option?["label"]?.ToString() ?? "";
                            if (multiSelect)
                            {
                                var choice = new CheckBox { Content = label, ToolTip = option?["description"]?.ToString(), Margin = new Thickness(4, 8, 12, 8) };
                                checkedOptions.Add(choice); choices.Children.Add(choice);
                            }
                            else
                            {
                                var choice = new Button { Content = label, ToolTip = option?["description"]?.ToString(), Padding = new Thickness(10, 6, 10, 6) };
                                choice.Click += (_, _) => answer.Text = label;
                                choices.Children.Add(choice);
                            }
                        }
                        PromptPanel.Children.Add(choices);
                    }
                    PromptPanel.Children.Add(new TextBlock { Text = multiSelect ? "可多选，也可在下方补充填写。" : "可选择一个选项，也可在下方自由填写。", FontSize = 12 });
                    PromptPanel.Children.Add(answer);
                    answers[q!["id"]!.ToString()] = () =>
                    {
                        var values = checkedOptions.Where(choice => choice.IsChecked == true).Select(choice => choice.Content.ToString()!).ToList();
                        if (!string.IsNullOrWhiteSpace(answer.Text)) values.Add(answer.Text.Trim());
                        return values.Distinct(StringComparer.Ordinal).ToArray();
                    };
                }
                var button = new Button { Content = "提交回答" };
                button.Click += async (_, _) => await ActionAsync(async peer =>
                {
                    var values = answers.ToDictionary(item => item.Key, item => item.Value());
                    if (values.Values.Any(value => value.Length == 0)) throw new ArgumentException("请填写每个问题的回答。");
                    await peer.CallAsync("respond", new { id = prompt.Id, decision = "", response = new { answers = values.ToDictionary(p => p.Key, p => new { answers = p.Value }) } });
                });
                PromptPanel.Children.Add(button);
            }
            else if (prompt.Kind is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "hermes/approval" or "dsh/approval")
            {
                PromptPanel.Children.Add(new TextBlock { Text = prompt.Kind == "dsh/approval" ? "DSH 工具权限" : prompt.Kind == "hermes/approval" ? "Hermes 工具权限" : prompt.Kind.Contains("commandExecution") ? "执行命令" : "修改文件", FontWeight = FontWeights.SemiBold });
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
            else PromptPanel.Children.Add(new TextBlock { Text = prompt.Title + "\n" + prompt.Detail + "\n此类交互请在远端 Agent 的原入口处理。", TextWrapping = TextWrapping.Wrap });
        }
    }
    private void SaveDraft() { if (selectedId is not null) drafts[selectedId] = Composer.Text; }
    private void ShowConversations() { SettingsPage.Visibility = Visibility.Collapsed; ConversationPage.Visibility = Visibility.Visible; }
    private void Conversations_Click(object sender, RoutedEventArgs e) => ShowConversations();
    private void Settings_Click(object sender, RoutedEventArgs e)
    { CloseOverlays(); ConversationPage.Visibility = Visibility.Collapsed; SettingsPage.Visibility = Visibility.Visible; }
    private void CloseOverlays() { ConnectOverlay.Visibility = NewOverlay.Visibility = ModelOverlay.Visibility = FilesOverlay.Visibility = WorkspaceOverlay.Visibility = Visibility.Collapsed; }
    private void CloseOverlay_Click(object sender, RoutedEventArgs e) { if (!actionBusy) CloseOverlays(); }
    private void OpenConnect_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlays(); ConnectServer.Text = "中继：" + ServerUrl.Text.Trim(); ConnectError.Text = "";
        try { SavedConnectionsList.ItemsSource = connectionStore.List(); } catch (Exception ex) { Error(ex); }
        ConnectOverlay.Visibility = Visibility.Visible; RemoteCode.Focus();
    }
    private void SavedConnection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SavedConnectionsList.SelectedItem is not SavedConnection saved) return;
        ServerUrl.Text = saved.Server; RemoteCode.Text = saved.Code; ConnectionName.Text = saved.Name;
        ConnectServer.Text = "中继：" + saved.Server;
    }
    private void NewConnection_Click(object sender, RoutedEventArgs e)
    {
        SavedConnectionsList.SelectedIndex = -1;
        ConnectionName.Clear(); RemoteCode.Clear();
        ConnectServer.Text = "中继：" + ServerUrl.Text.Trim();
        ConnectError.Text = "正在新增连接。填写名称和授权码后保存，已有连接会保留。";
        ConnectionName.Focus();
    }
    private void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var validation = new RegistryClient(ServerUrl.Text.Trim());
            if (string.IsNullOrWhiteSpace(RemoteCode.Text)) throw new ArgumentException("请输入授权码。");
            connectionStore.Save(new(ServerUrl.Text.Trim(), RemoteCode.Text.Trim(), string.IsNullOrWhiteSpace(ConnectionName.Text) ? new Uri(ServerUrl.Text.Trim()).Host : ConnectionName.Text.Trim(), false));
            SavedConnectionsList.ItemsSource = connectionStore.List(); ConnectError.Text = "连接已保存。";
        }
        catch (Exception ex) { Error(ex); }
    }
    private void DeleteConnection_Click(object sender, RoutedEventArgs e)
    {
        if (SavedConnectionsList.SelectedItem is not SavedConnection selected) return;
        try { connectionStore.Forget(selected); SavedConnectionsList.ItemsSource = connectionStore.List(); RemoteCode.Clear(); ConnectionName.Clear(); }
        catch (Exception ex) { Error(ex); }
    }
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
    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (actionBusy) return;
        if (remote?.IsConnected != true) { OpenConnect_Click(sender, e); return; }
        CloseOverlays(); NewOverlay.Visibility = Visibility.Visible; NewError.Text = "";
        NewHint.Text = agent?.CanCreate == true ? (agent.CanWorkspaces ? "在所选远端工作空间创建空会话，打开后再发送第一条消息。" : "创建空会话，打开后再发送第一条消息。") : "此版本远端不支持新建，请升级接入端。";
        CreateButton.IsEnabled = NewTitle.IsEnabled = agent?.CanCreate == true;
        NewWorkspacePanel.Visibility = agent?.CanWorkspaces == true ? Visibility.Visible : Visibility.Collapsed;
        createRequestId = Guid.NewGuid().ToString("N"); NewWorkspace.IsEnabled = true;
        if (agent?.CanWorkspaces == true)
        {
            var connection = remote; CreateButton.IsEnabled = false;
            try
            {
                await LoadWorkspacesAsync(connection);
                if (remote != connection) return;
                FillWorkspacePicker(SessionList.SelectedItem is WorkspaceGroup group ? group.Path : sessions.FirstOrDefault(s => s.Id == selectedId)?.Cwd);
                Filter();
            }
            catch (Exception ex) { NewError.Text = ex.Message; }
            finally { CreateButton.IsEnabled = remote == connection && agent?.CanCreate == true; }
        }
    }
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || actionBusy || string.IsNullOrWhiteSpace(NewTitle.Text)) return;
        string cwd = agent?.CanWorkspaces == true ? (NewWorkspace.SelectedItem as WorkspaceInfo)?.Path ?? "" : "";
        if (agent?.CanWorkspaces == true && cwd.Length == 0) { NewError.Text = "请先选择或添加远端工作空间。"; return; }
        var connection = remote; CreateButton.IsEnabled = false; NewError.Text = "";
        NewTitle.IsEnabled = NewWorkspace.IsEnabled = false;
        actionBusy = true; SessionList.IsEnabled = false; SetSessionEnabled(false);
        try
        {
            var result = Json.Read<SessionInfo>(await connection.Peer.CallAsync("create", new { id = createRequestId, title = NewTitle.Text.Trim(), cwd }, responseTimeout: TimeSpan.FromSeconds(90)));
            if (remote != connection) return;
            createRequestId = null; SaveDraft(); current = null; promptSignature = null; selectedId = result.Id; selectionVersion++; Composer.Clear();
            foreach (var group in workspaceGroups.Where(g => WorkspaceGroup.Key(g.Path) == WorkspaceGroup.Key(result.Cwd))) group.IsExpanded = true;
            sessions = new[] { result }.Concat(sessions.Where(x => x.Id != result.Id)).ToArray(); SearchBox.Clear(); Filter();
            CloseOverlays(); ShowConversations(); StatusBar.Text = "新会话已创建，可以发送第一条消息。";
            try { Render(Json.Read<SessionView>(await connection.Peer.CallAsync("attach", new { id = result.Id }))); }
            catch (Exception ex) { selectedId = null; StatusBar.Text = "会话已创建，但打开失败。请在列表中重新选择该会话：" + ex.Message; }
        }
        catch (Exception ex) { if (current is null) selectedId = null; createRequestId ??= Guid.NewGuid().ToString("N"); NewError.Text = ex.Message; Error(ex); }
        finally { actionBusy = false; CreateButton.IsEnabled = true; SessionList.IsEnabled = true; SetSessionEnabled(remote?.IsConnected == true && current is not null); }
    }
    private async void Model_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || current is null) return;
        var connection = remote; string id = current.Id;
        int generation = ++modelSettingsGeneration;
        CloseOverlays(); ModelOverlay.Visibility = Visibility.Visible; ModelError.Text = ""; ModelHint.Text = "正在读取远端设置…";
        ApplyPermissionsButton.IsEnabled = false; PermissionPanel.Visibility = Visibility.Collapsed;
        ApplyModelButton.IsEnabled = false; ModelPicker.IsEnabled = ProviderText.IsEnabled = ModePicker.IsEnabled = ReasoningPicker.IsEnabled = false;
        UsageText.Text = "正在读取远端套餐限额…";
        try
        {
            var settings = Json.Read<AgentSettings>(await connection.Peer.CallAsync("settings", null, responseTimeout: TimeSpan.FromMinutes(1)));
            if (remote != connection || selectedId != id || generation != modelSettingsGeneration) return;
            ApplyModelSettings(settings, id);
        }
        catch (Exception ex) { if (generation == modelSettingsGeneration) { ModelError.Text = ex.Message; UsageText.Text = "未能获取远端限额，请刷新重试。"; } }
        finally { loadingSettings = false; }
    }
    private void ApplyModelSettings(AgentSettings settings, string id)
    {
            loadingSettings = true; modelSession = id;
            modelPermissionOptions = settings.PermissionOptions ?? (agent?.Id == "codex" ? CodexPermissions.Values.Select((value, index) => new PermissionOption(value, CodexPermissions.Labels[index])).ToList() : []);
            PermissionPanel.Visibility = settings.CanChangePermissions && modelPermissionOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PermissionPicker.ItemsSource = modelPermissionOptions;
            PermissionPicker.DisplayMemberPath = nameof(PermissionOption.Label);
            PermissionPicker.SelectedItem = modelPermissionOptions.FirstOrDefault(option => option.Value == settings.Permission);
            var permission = PermissionPicker.SelectedItem as PermissionOption;
            PermissionHint.Text = "当前：" + (permission?.Label ?? settings.Permission ?? "未知") + "。空闲时修改，后续轮次生效。" + (string.IsNullOrEmpty(permission?.Description) ? "" : "\n" + permission.Description);
            ApplyPermissionsButton.IsEnabled = settings.CanChangePermissions && modelPermissionOptions.Count > 0;
            ModelPicker.ItemsSource = settings.Models; ModelPicker.Text = settings.Model; ProviderText.Text = settings.Provider;
            ModelPicker.IsEditable = !settings.RequireModelSelection;
            ModelPicker.SelectedItem = settings.Models.FirstOrDefault(m => m.Id == settings.Model);
            ReasoningLabel.Visibility = ReasoningPicker.Visibility = settings.CanChangeReasoning ? Visibility.Visible : Visibility.Collapsed;
            UpdateReasoning(settings.ReasoningEffort);
            UsageText.Text = settings.Usage ?? "此接入端暂不提供套餐限额。";
            ModePicker.SelectedIndex = settings.Mode == "plan" ? 1 : 0;
            ModelPicker.IsEnabled = settings.CanChangeModel; ProviderText.IsEnabled = settings.CanChangeModel && agent?.Id == "hermes";
            ModePicker.IsEnabled = settings.CanChangeMode; ModelHint.Text = settings.Detail;
            ApplyModelButton.IsEnabled = (settings.CanChangeModel || settings.CanChangeMode) && (!settings.RequireModelSelection || ModelPicker.SelectedItem is ModelOption);
        loadingSettings = false;
    }
    private void ModelSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingSettings && ModelPicker.SelectedItem is ModelOption model)
        {
            ProviderText.Text = model.Provider; UpdateReasoning(ReasoningPicker.SelectedItem as string);
            ApplyModelButton.IsEnabled = true;
        }
    }
    private void UpdateReasoning(string? preferred)
    {
        var model = ModelPicker.SelectedItem as ModelOption;
        var efforts = model?.ReasoningEfforts ?? [];
        ReasoningPicker.ItemsSource = efforts;
        ReasoningPicker.SelectedItem = preferred is not null && efforts.Contains(preferred) ? preferred : model?.DefaultReasoningEffort;
        if (ReasoningPicker.SelectedIndex < 0 && efforts.Count > 0) ReasoningPicker.SelectedIndex = 0;
        ReasoningPicker.IsEnabled = efforts.Count > 0;
    }
    private async void ApplyModel_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || current?.Id != modelSession || actionBusy) return;
        ApplyModelButton.IsEnabled = false; ModelError.Text = "";
        await ActionAsync(async peer =>
        {
            try
            {
                await peer.CallAsync("configure", new { model = (ModelPicker.SelectedItem as ModelOption)?.Id ?? ModelPicker.Text.Trim(), provider = ProviderText.Text.Trim(), mode = (ModePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "default", reasoningEffort = ReasoningPicker.IsEnabled ? ReasoningPicker.SelectedItem as string : null });
                ModelButton.Content = (ModePicker.SelectedIndex == 1 ? "Plan" : "Build") + " · " + ModelPicker.Text.Trim();
                CloseOverlays();
            }
            catch (Exception ex) { ModelError.Text = ex.Message; throw; }
        });
        ApplyModelButton.IsEnabled = true;
    }
    private async void ApplyPermissions_Click(object sender, RoutedEventArgs e)
    {
        if (remote is null || current?.Id != modelSession || actionBusy || PermissionPicker.SelectedItem is not PermissionOption selectedPermission) return;
        string permission = selectedPermission.Value;
        ModelError.Text = "";
        await ActionAsync(async peer =>
        {
            try { await peer.CallAsync("configure-permissions", new { permission }); CloseOverlays(); }
            catch (Exception ex) { ModelError.Text = ex.Message; throw; }
        });
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
        if (!CanAttachFiles) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "添加文件（单文件最多 100 MiB）" };
        if (dialog.ShowDialog(this) != true) return;
        await AttachPathsAsync(dialog.FileNames);
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
        await SaveRemoteFileAsync(filesSession!, file);
    }
    private readonly DownloadedAttachments downloadedAttachments = new(Path.Combine(Path.GetDirectoryName(SettingsFile)!, "downloads"));
    private string AttachmentKey(string id, RemoteFile file) => DownloadedAttachments.Key(
        activeDevice is null ? "" : DeviceConnectionPool<DeviceContext>.Identity(activeDevice.Server, activeDevice.Code), id, file);
    private async Task SaveRemoteFileAsync(string id, RemoteFile file)
    {
        string key = AttachmentKey(id, file);
        if (downloadedAttachments.Find(key) is { } existing)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(existing) { UseShellExecute = true }); }
            catch (Exception ex) { Error(ex); }
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = file.Name, Title = "保存文件（请选择不存在的文件名）", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        CloseOverlays();
        await TransferAsync(async (client, progress, ct) => { await client.DownloadAsync(id, file.Id, dialog.FileName, progress, ct); downloadedAttachments.Remember(key, dialog.FileName); });
    }
    private void SaveServerAddress(string server)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        string temporary = SettingsFile + ".tmp";
        var saved = File.Exists(SettingsFile) ? JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject() : new JsonObject();
        saved["serverUrl"] = server; saved["localBackend"] = LocalBackend.SelectedIndex;
        saved["dshExecutable"] = DshExecutable.Text.Trim(); saved["uiTheme"] = uiTheme;
        File.WriteAllText(temporary, saved.ToJsonString(Json.Options));
        File.Move(temporary, SettingsFile, true);
    }
    private async void PasteImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } data || !TryAttachData(data)) StatusBar.Text = "剪贴板中没有图片或文件。";
            await attachmentPreparation;
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void PreviewImage_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFiles.SelectedItem is not RemoteFile file || current?.Id != filesSession || actionBusy) return;
        await PreviewRemoteFileAsync(filesSession!, file);
    }
    private async Task PreviewRemoteFileAsync(string id, RemoteFile file)
    {
        if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(file.Name).ToLowerInvariant()))
        { FilesStatus.Text = "此格式不支持图片预览，请下载后打开。"; return; }
        if (file.Size > 10 * 1024 * 1024) { FilesStatus.Text = "图片预览限制为 10 MiB，请下载原文件。"; return; }
        string key = AttachmentKey(id, file);
        string path = downloadedAttachments.Find(key) ?? downloadedAttachments.NewPath(file.Name);
        try
        {
            await TransferAsync(async (client, progress, ct) =>
            {
                if (!File.Exists(path))
                {
                    await client.DownloadAsync(id, file.Id, path, progress, ct);
                    downloadedAttachments.Remember(key, path);
                }
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
            selectedId = null; current = null; Messages.ItemsSource = null; transcriptText = ""; OutputFilesPanel.Children.Clear(); outputFilesFingerprint = renderedOutputFilesFingerprint = "";
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
            VisualTheme.Apply(dialog, uiTheme);
            dialog.SetResourceReference(FontFamilyProperty, "UIFont");
            dialog.SetResourceReference(FontSizeProperty, "UIFontSize");
            button.Click += async (_, _) =>
            {
                string id = picker.Text.Trim(); if (id.Length == 0 || remote is null || actionBusy) return;
                button.IsEnabled = false;
                await ActionAsync(async peer =>
                {
                    SaveDraft(); ClearAttachments(); selectedId = null; current = null; Messages.ItemsSource = null; transcriptText = ""; OutputFilesPanel.Children.Clear(); outputFilesFingerprint = renderedOutputFilesFingerprint = "";
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
        CloseCompletionToasts();
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
            try { await DisconnectAsync(); }
            finally { await deviceConnections.DisposeAsync(); }
            if (host is not null) await host.DisposeAsync();
            await DisposeLocalAdapterAsync();
            hostRegistry?.Dispose();
        }
        finally { snapshotTimer.Stop(); CloseAllMinis(); DisposeTray(); closing = true; Application.Current.Shutdown(); }
    }
}
