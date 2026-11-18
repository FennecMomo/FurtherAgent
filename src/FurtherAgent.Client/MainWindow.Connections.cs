using System.IO;
using System.Windows;
using System.Windows.Controls;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public partial class MainWindow
{
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
            (Remote.IsConnected && Recovery is not null ? "会话加载中" : AgentIndicator.From(Remote.IsConnected, Remote.IsReconnecting, View).Label);
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

    private async Task<DeviceContext> OpenDeviceAsync(string server, string code)
    {
        return await deviceConnections.OpenAsync(server, code, async () =>
        {
            var added = new DeviceContext(server, code);
            try
            {
                await added.Remote.ConnectAsync(added.Registry, code);
                try { await OperationGroups.NegotiateAsync(added.Remote.Peer); } catch (IOException) { /* Legacy hosts still provide their existing history. */ }
                added.Agent = Json.Read<AgentInfo[]>(await added.Remote.Peer.CallAsync("agents", null)).FirstOrDefault(a => a.Available)
                    ?? throw new IOException("远端没有可用适配器。");
                added.Sessions = Json.Read<List<SessionInfo>>(await added.Remote.Peer.CallAsync("sessions", null));
                if (added.Agent.CanWorkspaces)
                {
                    try { added.Workspaces = Json.Read<List<WorkspaceInfo>>(await added.Remote.Peer.CallAsync("workspaces/list", null)); }
                    catch { /* Session access remains usable when workspace discovery is unavailable. */ }
                }
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
        });
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
        bool Current() => !recovery.IsCancellationRequested && ReferenceEquals(device.Recovery, recovery) &&
            (activeDevice == device ? selectedId == id && selectionVersion == version : device.SelectedId == id);
        int attempt = 0;
        try
        {
            while (Current())
            {
                if (!device.Remote.IsConnected) return null;
                var peer = device.Remote.Peer;
                try
                {
                    try { await OperationGroups.NegotiateAsync(peer, recovery.Token); }
                    catch (IOException) when (device.Remote.IsConnected) { /* Legacy history fallback. */ }
                    var restoredSessions = refreshSessions
                        ? Json.Read<List<SessionInfo>>(await peer.CallAsync("sessions", null, recovery.Token)) : device.Sessions;
                    var view = id is null ? null : Json.Read<SessionView>(await SessionHistory.ReadAsync(peer, "attach", new { id }, recovery.Token));
                    if (!Current()) return null;
                    if (!ReferenceEquals(peer, device.Remote.Peer)) continue;
                    if (!device.Remote.IsConnected) return null;
                    device.Sessions = restoredSessions;
                    if (activeDevice == device)
                    {
                        RemoteStatus.Text = "已连接 · " + device.Name + " · " + device.Remote.ConnectionKind;
                        StatusBar.Text = id is null ? "设备连接已恢复。" : "会话已恢复。";
                    }
                    return view;
                }
                catch (OperationCanceledException) when (recovery.IsCancellationRequested) { return null; }
                catch (Exception ex)
                {
                    if (!Current() || !device.Remote.IsConnected) return null;
                    if (activeDevice == device)
                    {
                        SetSessionEnabled(false);
                        RemoteStatus.Text = "网络已连接 · 正在恢复会话";
                        SessionMeta.Text = "会话读取失败，等待自动重试";
                        StatusBar.Text = ex.Message;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 2 + attempt++ * 2)), recovery.Token);
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
