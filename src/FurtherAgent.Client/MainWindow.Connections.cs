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
        public string Label => Name + " · " + Remote.ConnectionKind + " · " + AgentIndicator.From(Remote.IsConnected, Remote.IsReconnecting, View).Label;
        public async ValueTask DisposeAsync()
        {
            try { if (Heartbeat is not null) await Heartbeat.DisposeAsync(); }
            finally { try { await Remote.DisposeAsync(); } finally { Registry.Dispose(); } }
        }
    }
    private readonly DeviceConnectionPool<DeviceContext> deviceConnections = new();
    private DeviceContext? activeDevice;
    private bool updatingDevicePicker;
    private string devicePickerSignature = "";
    private bool CanSwitchDevice => !actionBusy && transferCancellation is null && !workspaceBusy && !loadingSettings;

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
                    var view = Json.Read<SessionView>(node);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (activeDevice == added && selectedId == view.Id)
                        { added.View = view; lock (snapshotGate) pendingSnapshot = (added.Remote, view); }
                        else if (activeDevice != added && added.SelectedId == view.Id) added.View = view;
                    });
                };
                added.Remote.ConnectionChanged += (online, message) => Dispatcher.BeginInvoke(() =>
                {
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
        try
        {
            try { await OperationGroups.NegotiateAsync(device.Remote.Peer); } catch (IOException) { /* Legacy history fallback. */ }
            device.Sessions = Json.Read<List<SessionInfo>>(await device.Remote.Peer.CallAsync("sessions", null));
            if (activeDevice == device ? selectedId != id || version != selectionVersion : device.SelectedId != id) return;
            var view = id is null ? null : Json.Read<SessionView>(await device.Remote.Peer.CallAsync("attach", new { id }));
            if (activeDevice == device)
            {
                if (version != selectionVersion) return;
                sessions = device.Sessions; Filter();
                if (view is not null) Render(view);
            }
            else if (device.SelectedId == id) device.View = view;
        }
        catch (Exception ex) { if (activeDevice == device) Error(ex); }
    }
    private void SaveActiveDevice()
    {
        if (activeDevice is not { } device) return;
        SaveDraft();
        device.SelectedId = selectedId; device.View = History.Latest ?? current;
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
        AttachmentSummary.Text = attachedFiles.Count == 0 ? "" : "待发送：" + string.Join("、", attachedFiles.Select(f => f.Name));
        SearchBox.Text = device.Search; Filter();
        WorkspaceButton.Visibility = agent?.CanWorkspaces == true ? Visibility.Visible : Visibility.Collapsed;
        AgentPicker.Items.Clear(); AgentPicker.Items.Add(agent?.Name ?? "Agent"); AgentPicker.SelectedIndex = 0;
        DisconnectButton.IsEnabled = true; RefreshButton.IsEnabled = remote.IsConnected;
        SetSessionEnabled(false); RemoteStatus.Text = "当前设备 · " + device.Name + " · " + device.Remote.ConnectionKind;
        SessionTitle.Text = "选择远端会话，继续工作"; SessionMeta.Text = "其他设备保持连接，可在左侧随时切换。";
        EmptyTitle.Text = "选择会话，继续工作"; EmptyDescription.Text = "从左侧打开已有会话，或点击「新建会话」。";
        EmptyConnect.Visibility = Visibility.Collapsed; EmptyState.Visibility = Visibility.Visible;
        if (current is not null) Render(current);
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
