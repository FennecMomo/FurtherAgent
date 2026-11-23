using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
    private sealed record SessionLoadFailure(string? SessionId, string Phase, string Message);
    private sealed class DeviceContext(string server, string code) : IAsyncDisposable
    {
        public string Server { get; } = server;
        public string Code { get; } = code;
        public string Name = "";
        public RegistryClient Registry { get; } = new(server);
        public RemoteDevice Remote { get; } = new();
        public ConnectionHeartbeat? Heartbeat;
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationTokenSource? Recovery;
        public SessionLoadFailure? LoadFailure;
        public AgentInfo? Agent;
        public IReadOnlyList<SessionInfo> Sessions = [];
        public IReadOnlyList<WorkspaceInfo> Workspaces = [];
        public IReadOnlyList<WorkspaceGroup> Groups = [];
        public SessionView? View;
        public ConversationHistory History { get; } = new();
        public string? SelectedId, CreateRequestId, FilesSession;
        public string Search = "";
        public Dictionary<string, string> Drafts = [];
        public List<RemoteFile> Attachments = [];
        public string Label => Name + " · " + Remote.ConnectionKind + " · " +
            (Remote.IsConnected && LoadFailure is not null ? (Recovery is null ? "读取失败" : "读取失败 · 自动重试") :
                Remote.IsConnected && Recovery is not null ? "会话加载中" : AgentIndicator.From(Remote.IsConnected, Remote.IsReconnecting, View).Label);
        public async ValueTask DisposeAsync()
        {
            Lifetime.Cancel(); Recovery?.Cancel();
            try { if (Heartbeat is not null) await Heartbeat.DisposeAsync(); }
            finally { try { await Remote.DisposeAsync(); } finally { Registry.Dispose(); Lifetime.Dispose(); } }
        }
    }
    private readonly DeviceConnectionPool<DeviceContext> deviceConnections = new();
    private DeviceContext? activeDevice;
    private bool updatingDevicePicker;
    private string devicePickerSignature = "";
    private bool CanSwitchDevice => !actionBusy && !preparingAttachment && transferCancellation is null && !workspaceBusy && !loadingSettings;
    private SessionLoadFailure? CurrentSessionLoadFailure => activeDevice?.LoadFailure is { } failure && failure.SessionId == selectedId ? failure : null;

    private async Task<DeviceContext> OpenDeviceAsync(string server, string code)
    {
        CancellationToken ct = connectionAttempt?.Token ?? default;
        return await deviceConnections.OpenAsync(server, code, async () =>
        {
            int[] retryDelays = [1, 4, 12];
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { return await OpenDeviceOnceAsync(server, code, ct); }
                catch (ConnectionOpenException ex) when (ex.CanRetry && attempt < retryDelays.Length && !ct.IsCancellationRequested)
                {
                    int delay = retryDelays[attempt];
                    string message = ConnectionErrorMessage(ex, code);
                    RemoteStatus.Text = $"{message} {delay} 秒后自动重试（{attempt + 2}/{retryDelays.Length + 1}）。";
                    ConnectError.Text = RemoteStatus.Text;
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                }
            }
        });
    }

    private async Task<DeviceContext> OpenDeviceOnceAsync(string server, string code, CancellationToken ct)
    {
        var added = new DeviceContext(server, code);
        try
        {
            ConnectError.Text = "";
            RemoteStatus.Text = "正在连接设备…";
            await added.Remote.ConnectAsync(added.Registry, code, ct);
            await LoadInitialDeviceAsync(added, ct);
            ct.ThrowIfCancellationRequested();
            added.Heartbeat = new(added.Registry, added.Remote); added.Heartbeat.Start();
            added.Name = added.Remote.Device!.Name;
            added.Remote.Notification += (method, node) =>
            {
                if (method == "session-completed" && node is not null)
                {
                    var completion = Json.Read<SessionCompletion>(node);
                    Dispatcher.BeginInvoke(() => HandleCompletion(added, completion)); return;
                }
                if (method != "session" || node is null) return;
                var notificationPeer = added.Remote.Peer;
                var view = Json.Read<SessionView>(node);
                Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(notificationPeer, added.Remote.Peer)) return;
                    if (activeDevice == added && selectedId == view.Id)
                    { added.View = view; lock (snapshotGate) pendingSnapshot = (added.Remote, view); }
                    else if (activeDevice != added && added.SelectedId == view.Id) added.View = view;
                });
            };
            added.Remote.ConnectionChanged += (online, message) => Dispatcher.BeginInvoke(() =>
            {
                if (!online) added.Recovery?.Cancel();
                if (activeDevice != added) return;
                RemoteStatus.Text = message; SetSessionEnabled(false); RefreshButton.IsEnabled = online;
                StatusBar.Text = message + " 重连会恢复会话显示；不会自动重发消息或确认操作。";
            });
            added.Remote.Reconnected += () => Dispatcher.BeginInvoke(async () => await RestoreDeviceAsync(added));
            return added;
        }
        catch { await added.DisposeAsync(); throw; }
    }

    private static async Task LoadInitialDeviceAsync(DeviceContext device, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var peer = device.Remote.Peer;
            bool Current() => ReferenceEquals(peer, device.Remote.Peer) && device.Remote.IsConnected;
            if (!Current()) throw new ConnectionOpenException("读取设备信息前连接断开。", true);
            string phase = "协商历史记录格式";
            try
            {
                try { await OperationGroups.NegotiateAsync(peer, ct); }
                catch (IOException) when (Current()) { /* Older hosts can still serve their existing history format. */ }
                phase = "读取 Agent 信息";
                var agent = Json.Read<AgentInfo[]>(await peer.CallAsync("agents", null, ct)).FirstOrDefault(a => a.Available)
                    ?? throw new IOException("远端没有可用的 Agent。");
                phase = "读取会话列表";
                var sessions = Json.Read<List<SessionInfo>>(await peer.CallAsync("sessions", null, ct));
                IReadOnlyList<WorkspaceInfo> workspaces = [];
                if (agent.CanWorkspaces)
                {
                    phase = "读取工作区";
                    try { workspaces = Json.Read<List<WorkspaceInfo>>(await peer.CallAsync("workspaces/list", null, ct)); }
                    catch (Exception ex) when (Current() && ex is (IOException or TimeoutException or System.Text.Json.JsonException))
                    { /* A live peer can still serve sessions when optional workspace discovery fails. */ }
                }
                if (!Current()) throw new ConnectionOpenException($"{phase}时连接断开。", true);
                device.Agent = agent;
                device.Sessions = sessions;
                device.Workspaces = workspaces;
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A successful route change can replace the peer between metadata reads.
                // Refresh those reads on the new peer before starting a fresh connection.
                if (attempt == 0 && !ReferenceEquals(peer, device.Remote.Peer) && device.Remote.IsConnected) continue;
                if (ex is ConnectionOpenException) throw;
                bool transportLost = !Current() || ex is WebSocketException or SocketException ||
                    ex.InnerException is WebSocketException or SocketException;
                throw new ConnectionOpenException(transportLost ? $"{phase}时连接断开。" : $"{phase}失败：{ex.Message}", transportLost, ex);
            }
        }
    }
    private async Task RestoreDeviceAsync(DeviceContext device)
    {
        string? id = activeDevice == device ? selectedId : device.SelectedId;
        int version = selectionVersion;
        var view = await ReadDeviceSessionAsync(device, id, version, refreshSessions: true);
        if (activeDevice == device)
        {
            if (version != selectionVersion || selectedId != id) return;
            sessions = device.Sessions; Filter();
            if (view is not null) Render(view);
        }
        else if (view is not null && device.SelectedId == id) device.View = view;
    }
    private async Task<SessionView?> ReadDeviceSessionAsync(DeviceContext device, string? id, int version, bool refreshSessions)
    {
        if (device.Lifetime.IsCancellationRequested) return null;
        device.Recovery?.Cancel();
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(device.Lifetime.Token);
        device.Recovery = recovery;
        device.LoadFailure = null;
        bool Current() => !recovery.IsCancellationRequested && ReferenceEquals(device.Recovery, recovery) &&
            (activeDevice == device ? selectedId == id && selectionVersion == version : device.SelectedId == id);
        int attempt = 0;
        try
        {
            while (Current())
            {
                if (!device.Remote.IsConnected) return null;
                var peer = device.Remote.Peer;
                string phase = "协商会话历史格式";
                try
                {
                    try { await OperationGroups.NegotiateAsync(peer, recovery.Token); }
                    catch (IOException) when (device.Remote.IsConnected) { /* Legacy history fallback. */ }
                    phase = "读取会话列表";
                    var restoredSessions = refreshSessions
                        ? Json.Read<List<SessionInfo>>(await peer.CallAsync("sessions", null, recovery.Token)) : device.Sessions;
                    phase = "读取会话历史";
                    var view = id is null ? null : Json.Read<SessionView>(await SessionHistory.ReadAsync(peer, "attach", new { id }, recovery.Token));
                    if (!Current()) return null;
                    if (!ReferenceEquals(peer, device.Remote.Peer)) continue;
                    if (!device.Remote.IsConnected) return null;
                    device.Sessions = restoredSessions;
                    device.LoadFailure = null;
                    if (activeDevice == device)
                    {
                        RemoteStatus.Text = "已连接 · " + device.Name + " · " + device.Remote.ConnectionKind;
                        StatusBar.Text = view?.StatusDetail ?? (id is null ? "设备连接已恢复。" : "会话已恢复。");
                    }
                    return view;
                }
                catch (OperationCanceledException) when (recovery.IsCancellationRequested) { return null; }
                catch (Exception ex)
                {
                    if (!Current() || !device.Remote.IsConnected) return null;
                    if (!ReferenceEquals(peer, device.Remote.Peer)) continue;
                    string message = ConnectionErrorMessage(ex, device.Code);
                    device.LoadFailure = new(id, phase, message);
                    bool retry = ++attempt < 3 && ex is not TimeoutException && ex.InnerException is not TimeoutException;
                    int delay = 2 * attempt;
                    if (activeDevice == device)
                    {
                        SetSessionEnabled(false);
                        RemoteStatus.Text = retry ? "网络已连接 · 读取失败，等待重试" : "网络已连接 · 读取失败";
                        SessionMeta.Text = phase + "失败";
                        StatusBar.Text = $"{phase}失败：{message}" + (retry ? $"（{delay} 秒后重试）" : "（可点击刷新重试）");
                    }
                    if (!retry) return null;
                    await Task.Delay(TimeSpan.FromSeconds(delay), recovery.Token);
                }
            }
        }
        catch (OperationCanceledException) when (recovery.IsCancellationRequested) { }
        finally { if (ReferenceEquals(device.Recovery, recovery)) device.Recovery = null; }
        return null;
    }
    private void SaveActiveDevice()
    {
        if (activeDevice is not { } device) return;
        SaveDraft();
        device.SelectedId = selectedId;
        var latest = History.Latest ?? current;
        device.View = latest?.Id == selectedId ? latest : null;
        lock (snapshotGate)
        {
            if (pendingSnapshot is { } pending && pending.Connection == remote && pending.View.Id == selectedId) device.View = pending.View;
            pendingSnapshot = null;
        }
        device.Sessions = sessions; device.Workspaces = workspaces; device.Groups = workspaceGroups;
        device.Drafts = drafts; device.Attachments = attachedFiles; device.FilesSession = filesSession;
        device.CreateRequestId = createRequestId; device.Search = SearchBox.Text;
    }
    private void ActivateDevice(DeviceContext device)
    {
        if (activeDevice == device) { CloseOverlays(); ShowConversations(); return; }
        SaveActiveDevice(); CloseOverlays();
        selectionVersion++; modelSettingsGeneration++; browseVersion++;
        activeDevice = device; remote = device.Remote; remoteRegistry = device.Registry; heartbeat = device.Heartbeat; agent = device.Agent;
        selectedId = device.SelectedId; current = device.View; sessions = device.Sessions; workspaces = device.Workspaces; workspaceGroups = device.Groups;
        drafts = device.Drafts; attachedFiles = device.Attachments; filesSession = device.FilesSession; createRequestId = device.CreateRequestId;
        modelSession = null; promptSignature = null; transcriptText = ""; outputFilesFingerprint = renderedOutputFilesFingerprint = "";
        Messages.ItemsSource = null; OutputFilesPanel.Children.Clear(); PromptBorder.Visibility = Visibility.Collapsed;
        Composer.Text = selectedId is null ? "" : drafts.GetValueOrDefault(selectedId, "");
        RenderAttachments();
        SearchBox.Text = device.Search; Filter();
        WorkspaceButton.Visibility = agent?.CanWorkspaces == true ? Visibility.Visible : Visibility.Collapsed;
        AgentPicker.Items.Clear(); AgentPicker.Items.Add(agent?.Name ?? "Agent"); AgentPicker.SelectedIndex = 0;
        DisconnectButton.IsEnabled = true; RefreshButton.IsEnabled = remote.IsConnected;
        SetSessionEnabled(false); RemoteStatus.Text = "当前设备 · " + device.Name + " · " + device.Remote.ConnectionKind;
        SessionTitle.Text = "选择远端会话，继续工作"; SessionMeta.Text = "其他设备保持连接，可在左侧随时切换。";
        EmptyTitle.Text = "选择会话，继续工作"; EmptyDescription.Text = "从左侧打开已有会话，或点击「新建会话」。";
        EmptyConnect.Visibility = Visibility.Collapsed; EmptyState.Visibility = Visibility.Visible;
        if (current is not null && current.Id == selectedId) Render(current);
        else
        {
            current = null;
            if (selectedId is not null && device.Remote.IsConnected) _ = RestoreDeviceAsync(device);
        }
        StatusBar.Text = agent?.Detail ?? "已切换设备";
        UpdateDevicePicker(); ShowConversations();
    }
    private void UpdateDevicePicker()
    {
        if (ConnectedDevices is null) return;
        var devices = deviceConnections.Entries.Values.ToList();
        string signature = string.Join("|", devices.Select(d => DeviceConnectionPool<DeviceContext>.Identity(d.Server, d.Code) + d.Label))
            + "|" + (activeDevice is null ? "" : DeviceConnectionPool<DeviceContext>.Identity(activeDevice.Server, activeDevice.Code));
        if (signature == devicePickerSignature) return;
        devicePickerSignature = signature; updatingDevicePicker = true;
        try
        {
            ConnectedDevices.ItemsSource = devices; ConnectedDevices.SelectedItem = activeDevice;
            ConnectedDevices.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { updatingDevicePicker = false; }
    }
    private void ConnectedDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingDevicePicker || ConnectedDevices.SelectedItem is not DeviceContext device) return;
        if (!CanSwitchDevice || !ConnectButton.IsEnabled)
        { devicePickerSignature = ""; UpdateDevicePicker(); StatusBar.Text = "请等待当前操作结束再切换设备。"; return; }
        ActivateDevice(device);
    }
}
