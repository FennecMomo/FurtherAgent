using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace FurtherAgent.Core;

public sealed class DeviceHost : IAsyncDisposable
{
    private readonly RegistryClient registry;
    private readonly Func<IAgentAdapter> factory;
    private readonly SharedAgentHub agents;
    private readonly CancellationTokenSource lifetime = new();
    private Task? supervisor;
    private WebSocketTransport? control;
    private string name = "";
    private readonly Lazy<FileTransferStore> files;
    private readonly bool persistentIdentity;
    private string? identitySecret;
    private string? identityPath;
    private LanHost? lan;
    private bool registered;
    private int disposed;
    public int LanPort => lan?.Port ?? 0;
    private FileStream? identityLock;
    public DeviceLease? Lease { get; private set; }
    public event Action<string>? Status;
    public event Action<string>? CodeChanged;
    public DeviceHost(RegistryClient registry, Func<IAgentAdapter>? factory = null, string? filesRoot = null, bool persistentIdentity = false, bool enableLan = true)
    { this.registry = registry; this.factory = factory ?? (() => new CodexDesktopAdapter()); agents = new(this.factory); files = new(() => new FileTransferStore(filesRoot)); this.persistentIdentity = persistentIdentity; this.enableLan = enableLan; }

    private readonly bool enableLan;
    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        this.name = name;
        if (persistentIdentity)
        {
            string path = DeviceIdentity.DefaultPath(registry.ServerIdentity, name);
            identityPath = path;
            _ = DeviceIdentity.LoadOrCreate(path);
            identityLock = DeviceIdentity.Lock(path);
            identitySecret = DeviceIdentity.LoadOrCreate(path);
            if (enableLan && Environment.GetEnvironmentVariable("FURTHERAGENT_LAN") != "0")
            {
                try { lan = new LanHost(name, path, identitySecret, ServePeerAsync); lan.Start(); Status?.Invoke($"局域网接入已启用，端口 {lan.Port}。"); }
                catch (Exception ex) { if (lan is not null) await lan.DisposeAsync(); lan = null; Status?.Invoke("局域网接入暂不可用：" + ex.Message); }
            }
        }
        try { control = await OpenControlAsync(ct); }
        catch (Exception ex) when (lan is not null && !ct.IsCancellationRequested)
        {
            Lease = new DeviceLease(DeviceIdentity.Code(identitySecret!), identitySecret!, DateTimeOffset.MaxValue);
            CodeChanged?.Invoke(Lease.Code); Status?.Invoke("局域网接入就绪，中继暂不可达：" + ex.Message);
        }
        supervisor = SuperviseAsync(control);
    }
    public async Task SetLanEnabledAsync(bool enabled)
    {
        if (!enabled) { var previous = lan; lan = null; if (previous is not null) await previous.DisposeAsync(); return; }
        if (lan is not null) return;
        if (identityPath is null || identitySecret is null) throw new IOException("局域网接入需要固定设备身份。");
        var candidate = new LanHost(name, identityPath, identitySecret, ServePeerAsync);
        try { candidate.Start(); lan = candidate; }
        catch { await candidate.DisposeAsync(); throw; }
    }
    private async Task<WebSocketTransport> OpenControlAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        if (!registered) { Lease = await registry.PostAsync<DeviceLease>("devices", new DeviceRegistration(name, identitySecret, 8), timeout.Token); registered = true; }
        if (identitySecret is not null && (Lease!.Code != DeviceIdentity.Code(identitySecret) || Lease.Secret != identitySecret))
            throw new IOException("中继尚不支持固定配对码，请升级中继；设备身份已保留，不会退回随机码。");
        var socket = await registry.OpenSocketAsync("host", timeout.Token);
        try
        {
            await socket.WriteAsync(Json.Node(new { code = Lease!.Code, secret = Lease.Secret }), timeout.Token);
            var response = await socket.ReadLimitedAsync(4096, timeout.Token);
            if (response["ok"]?.GetValue<bool>() != true)
            {
                if (response["resetLease"]?.GetValue<bool>() == true) { Lease = null; registered = false; }
                throw new IOException(response["error"]?.ToString() ?? "设备中继连接失败。");
            }
            CodeChanged?.Invoke(Lease.Code);
            return socket;
        }
        catch { await socket.DisposeAsync(); throw; }
    }
    private async Task SuperviseAsync(WebSocketTransport? socket)
    {
        while (!lifetime.IsCancellationRequested)
        {
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var tasks = new List<Task>();
            try
            {
                socket ??= control = await OpenControlAsync(cycle.Token);
                using var stop = cycle.Token.Register(socket.Abort);
                while (true)
                {
                    var message = await socket.ReadLimitedAsync(4096, cycle.Token);
                    if (message["type"]?.ToString() == "connect" && message["sessionId"] is { } id)
                    {
                        tasks.RemoveAll(t => t.IsCompleted);
                        if (tasks.Count >= 16) throw new IOException("中继会话数量异常。");
                        tasks.Add(ServeAsync(id.GetValue<string>(), cycle.Token));
                    }
                    else throw new IOException("未知的中继控制消息。");
                }
            }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) Status?.Invoke("服务连接中断，正在重连：" + ex.Message); }
            finally
            {
                cycle.Cancel(); if (socket is not null) await socket.DisposeAsync();
                await Task.WhenAll(tasks);
            }
            int attempt = 0;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Min(attempt++, 4)))), lifetime.Token);
                    socket = control = await OpenControlAsync(lifetime.Token);
                    Status?.Invoke("已恢复中继连接，等待远程端连接。");
                    break;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception ex) { Status?.Invoke("正在重连中继服务：" + ex.Message); }
            }
        }
    }
    private async Task ServeAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await using var socket = await registry.OpenSocketAsync("join", timeout.Token);
            await socket.WriteAsync(Json.Node(new { code = Lease!.Code, secret = Lease.Secret, sessionId }), timeout.Token);
            var hello = await socket.ReadLimitedAsync(4096, timeout.Token);
            if (hello["ok"]?.GetValue<bool>() != true) throw new IOException(hello["error"]?.ToString() ?? "连接授权失败。");
            await ServePeerAsync(socket, ct);
        }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Status?.Invoke("远程会话连接结束：" + ex.Message); }
    }
    private async Task ServePeerAsync(IConnectionTransport socket, CancellationToken ct)
    {
        try
        {
            using var stop = ct.Register(socket.Abort);
            await using var adapter = await agents.ConnectAsync(ct);
            await using var peer = new RpcPeer(socket);
            using var pumping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int incremental = 0;
            int paged = 0;
            SessionView? historySource = null;
            int lastIncremental = 0;
            var sync = new SessionSyncEncoder();
            var updates = Channel.CreateBounded<SessionView>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            var completions = Channel.CreateBounded<SessionCompletion>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });
            adapter.Completed += completion => { if (!completions.Writer.TryWrite(completion)) socket.Abort(); };
            adapter.Changed += view => updates.Writer.TryWrite(view);
            adapter.Faulted += message => { Status?.Invoke(message); socket.Abort(); };
            string? attachedSession = null;
            SessionView WithFiles(SessionView view)
            {
                view = OperationSummary.ForTransport(view);
                if (adapter.Info.Id == "dsh" && !adapter.Info.CanFiles) return view;
                try { return view with { OutputFiles = files.Value.List(view.Id).Files.Where(f => f.Id.StartsWith("outbox/", StringComparison.Ordinal)).ToList() }; }
                catch (IOException) { return view with { StatusDetail = view.StatusDetail + " · 输出文件列表暂不可用，请打开会话文件查看。" }; }
            }
            async Task<JsonNode> Attach(string id, CancellationToken token)
            {
                var view = WithFiles(await adapter.AttachAsync(id, token));
                if (adapter.Info.Id == "hermes" || adapter.Info.Id == "dsh" && adapter.Info.CanFiles)
                    await adapter.SetFileWorkspaceAsync(files.Value.List(id).Outbox, token);
                historySource = view; attachedSession = view.Id;
                return Json.Node(Volatile.Read(ref paged) > 0 || adapter.Info.Id == "codex" ? HistoryPaging.Latest(view, Volatile.Read(ref paged) >= 2, Volatile.Read(ref paged) >= 3) : view);
            }
            string FileSession(JsonNode? args)
            {
                string id = args?["session"]?.ToString() ?? "";
                if (id.Length == 0 || id != attachedSession) throw new IOException("请先打开对应会话再操作文件。");
                return id;
            }
            async Task<JsonNode?> FileOperation(string method, JsonNode? args, CancellationToken token)
            {
                if (adapter.Info.Id == "dsh" && !adapter.Info.CanFiles) throw new NotSupportedException("当前 DSH 实例尚未开放文件传输。");
                _ = FileSession(args); return await files.Value.HandleAsync(method, args!, token);
            }
            async Task<JsonNode?> SendFiles(JsonNode? args, CancellationToken token)
            {
                if (adapter.Info.Id == "dsh" && !adapter.Info.CanFiles) throw new NotSupportedException("当前 DSH 实例尚未开放文件传输。");
                string id = FileSession(args), text = args!["text"]?.ToString() ?? "";
                if (text.Length > 100000) throw new ArgumentException("消息过长。");
                var ids = args["files"]?.AsArray().Select(x => x!.ToString()).ToArray() ?? [];
                if (string.IsNullOrWhiteSpace(text) && ids.Length == 0) throw new ArgumentException("消息不能为空。");
                string composed = await files.Value.ComposeMessageAsync(id, text, ids, token);
                await adapter.SendAsync(composed, token); return Json.Node(new { ok = true });
            }
            peer.Handler = async (method, args, token) => method switch
            {
                "history-mode" => Json.Node(new { enabled = Interlocked.Exchange(ref paged, args?["version"]?.GetValue<int>() is 3 ? 3 : args?["version"]?.GetValue<int>() is 2 ? 2 : 1) >= 0, version = Volatile.Read(ref paged) }),
                "history" => Json.Node(HistoryPaging.Page(historySource is { } history && history.Id == FileSession(args) ? history : throw new IOException("请先打开会话。"),
                    args!["before"]!.GetValue<int>(), args["revision"]!.GetValue<string>(), Volatile.Read(ref paged) >= 2, Volatile.Read(ref paged) >= 3)),
                "session-sync" => Json.Node(new { incremental = Interlocked.Exchange(ref incremental, args?["version"]?.GetValue<int>() >= 2 ? 2 : 1) >= 0, version = Volatile.Read(ref incremental), transportPing = true }),
                "lan-info" => lan is null ? null : Json.Node(lan.Pairing),
                "agents" => Json.Node(new[] { adapter.Info with { CanFiles = adapter.Info.Id != "dsh" || adapter.Info.CanFiles, CanThumbnails = adapter.Info.Id is "hermes" or "codex" || adapter.Info.CanThumbnails } }),
                "sessions" => Json.Node(await adapter.ListAsync(token)),
                "attach" => await Attach(args!["id"]!.GetValue<string>(), token),
                "send-files" => await SendFiles(args, token),
                _ when method.StartsWith("files/", StringComparison.Ordinal) => await FileOperation(method, args, token),
                "create" => Json.Node(await adapter.CreateAsync(args!["id"]!.GetValue<string>(), args["title"]?.ToString() ?? "新会话", args["cwd"]?.ToString() ?? "", token)),
                "workspaces/list" => Json.Node(await adapter.WorkspacesAsync(token)),
                "workspaces/browse" => Json.Node(await adapter.BrowseDirectoriesAsync(args?["path"]?.ToString() ?? "", token)),
                "workspaces/add" => Json.Node(await adapter.AddWorkspaceAsync(args!["id"]!.GetValue<string>(), args["path"]!.GetValue<string>(), args["folder"]?.ToString() ?? "", token)),
                "settings" => Json.Node(await adapter.SettingsAsync(token)),
                "archive" => await Execute(() => adapter.ArchiveAsync(FileSession(args), args!["archived"]!.GetValue<bool>(), token)),
                "configure-permissions" => await Execute(() => adapter.ConfigurePermissionsAsync(args?["permission"]?.ToString() ?? "", token)),
                "configure" => await Execute(() => adapter.ConfigureAsync(args!["model"]?.ToString() ?? "", args["provider"]?.ToString() ?? "", args["mode"]?.ToString() ?? "default", args["reasoningEffort"]?.ToString(), token)),
                "send" => await Execute(() => adapter.SendAsync(args!["text"]!.GetValue<string>(), args["delivery"]?.ToString() ?? "auto", token)),
                "interrupt-send" => await Execute(() => adapter.InterruptAndSendAsync(args!["turnId"]!.GetValue<string>(), args["text"]!.GetValue<string>(), token)),
                "interrupt" => await Execute(() => adapter.InterruptAsync(token)),
                "respond" => await Execute(() => adapter.RespondAsync(args!["id"]!.GetValue<string>(), args["decision"]?.GetValue<string>() ?? "", args["response"], token)),
                _ => throw new IOException("未知操作。")
            };
            peer.Start();
            var completionPump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var completion in completions.Reader.ReadAllAsync(pumping.Token))
                        await peer.NotifyAsync("session-completed", completion);
                }
                catch (Exception) { socket.Abort(); }
            });
            Status?.Invoke(socket is LanTransport ? "控制端已通过局域网直连。" : "远程端已通过服务端中继连接。");
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var view in updates.Reader.ReadAllAsync(pumping.Token))
                    {
                        if (view.Id != attachedSession) continue;
                        int mode = Volatile.Read(ref incremental);
                        if (mode != lastIncremental) { sync = new SessionSyncEncoder(textDeltas: mode >= 2); lastIncremental = mode; }
                        var enriched = WithFiles(view);
                        historySource = enriched;
                        if (Volatile.Read(ref paged) > 0 || adapter.Info.Id == "codex") enriched = HistoryPaging.Latest(enriched, Volatile.Read(ref paged) >= 2, Volatile.Read(ref paged) >= 3);
                        var update = sync.Encode(enriched);
                        if (update is not null)
                            await peer.NotifyAsync(mode > 0 ? "session-update" : "session", mode > 0 ? (object)update : enriched);
                        await Task.Delay(500, pumping.Token);
                    }
                }
                catch (Exception) { socket.Abort(); }
            });
            await peer.Completion!;
            updates.Writer.TryComplete(); completions.Writer.TryComplete(); pumping.Cancel(); await Task.WhenAll(pump, completionPump);
            Status?.Invoke("远程端已断开，授权码仍可用于连接。");
        }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Status?.Invoke("连接结束：" + ex.Message); }
    }
    private static async Task<JsonNode?> Execute(Func<Task> operation) { await operation(); return Json.Node(new { ok = true }); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); control?.Abort();
        if (supervisor is not null) await supervisor;
        if (lan is not null) await lan.DisposeAsync();
        await agents.DisposeAsync();
        if (files.IsValueCreated) files.Value.Dispose();
        if (control is not null) await control.DisposeAsync();
        if (Lease is not null && registered)
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await registry.PostAsync<JsonNode>("unregister", new LeaseProof(Lease.Code, Lease.Secret), timeout.Token); } catch { }
        identityLock?.Dispose();
    }
}

public sealed class RemoteDevice(bool autoReconnect = true) : IAsyncDisposable
{
    private readonly SemaphoreSlim reconnectWake = new(0, 1);
    public bool IsReconnecting { get; private set; }
    public void WakeReconnect()
    {
        if (lifetime.IsCancellationRequested) return;
        try { reconnectWake.Release(); } catch (SemaphoreFullException) { }
    }
    public async Task<bool> CheckConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConnected) { WakeReconnect(); return false; }
        var checkedPeer = Peer;
        // Legacy hosts serialize agents behind sends. Do not mistake a long operation
        // for a dead socket; their WebSocket keepalive still detects transport failure.
        if (!checkedPeer.SupportsTransportPing && checkedPeer.HasPendingCalls) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await checkedPeer.CallAsync(checkedPeer.SupportsTransportPing ? "transport-ping" : "agents", null, timeout.Token);
            return ReferenceEquals(checkedPeer, Peer) && IsConnected;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
            or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
            if (!checkedPeer.SupportsTransportPing && checkedPeer.HasPendingCalls && IsConnected) return true;
            if (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested && ReferenceEquals(checkedPeer, Peer))
            { socket?.Abort(); WakeReconnect(); }
            return false;
        }
    }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public DateTimeOffset? DisconnectedAt { get; private set; }
    public int ReconnectCount { get; private set; }
    public long ReceivedBytes => socket?.ReceivedBytes ?? 0;
    public long SentBytes => socket?.SentBytes ?? 0;
    private readonly CancellationTokenSource lifetime = new();
    private IConnectionTransport? socket;
    private Task? supervisor;
    public string ConnectionKind { get; private set; } = "服务器中继";
    private LanPairing? pairing;
    private string? attached;
    private int historyMode;
    private long selectionRevision;
    public RpcPeer Peer { get; private set; } = null!;
    public PairResult? Device { get; private set; }
    public bool IsConnected => Peer is not null && Peer.Completion?.IsCompleted == false;
    public event Action<string, JsonNode?>? Notification;
    public event Action<bool, string>? ConnectionChanged;
    public event Action? Reconnected;
    public async Task ConnectAsync(RegistryClient registry, string code, CancellationToken ct = default)
    {
        IsReconnecting = true;
        try { await OpenAsync(registry, code, ct); }
        finally { IsReconnecting = false; }
        supervisor = SuperviseAsync(registry, code);
    }
    private RpcPeer NewPeer(IConnectionTransport connection)
    {
        var peer = new RpcPeer(connection); var sync = new SessionSyncDecoder();
        peer.Notification += (method, node) =>
        {
            if (method == "session-update")
            {
                var view = sync.Apply(Json.Read<SessionUpdate>(node));
                if (Peer == peer) Notification?.Invoke("session", Json.Node(view));
            }
            else if (Peer == peer) Notification?.Invoke(method, node);
        };
        peer.CallSucceeded += (method, args) =>
        {
            if (Peer != peer) return;
            if (method == "attach") { attached = Json.Node(args!)["id"]?.ToString(); Interlocked.Increment(ref selectionRevision); }
            else if (method == "history-mode") { historyMode = Json.Node(args!)["version"]?.GetValue<int>() ?? 1; Interlocked.Increment(ref selectionRevision); }
        };
        peer.Start(); return peer;
    }
    private async Task OpenAsync(RegistryClient registry, string code, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        pairing ??= LanTrustStore.Load(registry.ServerIdentity, code);
        IConnectionTransport? direct = null;
        if (pairing is not null)
            try { direct = await LanHost.ConnectAsync(pairing, timeout.Token); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException && !timeout.IsCancellationRequested) { }
        if (direct is not null)
        {
            socket = direct; Device = new PairResult(pairing!.Name, ""); ConnectionKind = "局域网直连";
            Peer = NewPeer(direct); await EnableSync(Peer, timeout.Token); ConnectedAt = DateTimeOffset.UtcNow; return;
        }
        Device = await registry.PostAsync<PairResult>("pair", new PairRequest(code), timeout.Token);
        var connection = await registry.OpenSocketAsync("control", timeout.Token);
        try
        {
            await connection.WriteAsync(Json.Node(new { ticket = Device.Ticket }), timeout.Token);
            var response = await connection.ReadLimitedAsync(4096, timeout.Token);
            if (response["ok"]?.GetValue<bool>() != true) throw new IOException(response["error"]?.ToString() ?? "中继连接失败。");
            socket = connection; ConnectionKind = "服务器中继"; Peer = NewPeer(connection); await EnableSync(Peer, timeout.Token);
            try
            {
                var info = await Peer.CallAsync("lan-info", null, timeout.Token, TimeSpan.FromSeconds(5));
                if (info is not null) { pairing = Json.Read<LanPairing>(info); LanTrustStore.Save(registry.ServerIdentity, code, pairing); }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or TimeoutException) { }
            await TryUpgradeAsync(timeout.Token);
            ConnectedAt = DateTimeOffset.UtcNow;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static async Task EnableSync(RpcPeer peer, CancellationToken ct)
    {
        try
        {
            var capabilities = await peer.CallAsync("session-sync", new { version = 2 }, ct);
            peer.SupportsTransportPing = capabilities?["transportPing"]?.GetValue<bool>() == true;
        }
        catch (IOException) when (peer.Completion?.IsCompleted == false) { }
    }
    private async Task<bool> TryUpgradeAsync(CancellationToken ct)
    {
        if (pairing is null || ConnectionKind == "局域网直连" || Peer.HasPendingCalls || !IsConnected) return false;
        var previous = Peer; var previousSocket = socket; long revision = Interlocked.Read(ref selectionRevision);
        IConnectionTransport? direct = null; RpcPeer? candidate = null;
        try
        {
            direct = await LanHost.ConnectAsync(pairing, ct); if (direct is null) return false;
            candidate = NewPeer(direct); await EnableSync(candidate, ct);
            if (historyMode > 0) await candidate.CallAsync("history-mode", new { version = historyMode }, ct);
            if (attached is { } id) await candidate.CallAsync("attach", new { id }, ct);
            if (!previous.TryRedirectTo(candidate, () => Peer == previous && Interlocked.Read(ref selectionRevision) == revision)) return false;
            socket = direct; Peer = candidate; ConnectionKind = "局域网直连";
            direct = null; candidate = null;
            await previous.DisposeAsync(); if (previousSocket is not null) await previousSocket.DisposeAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or AuthenticationException or OperationCanceledException or ObjectDisposedException) { return false; }
        finally { if (candidate is not null) await candidate.DisposeAsync(); if (direct is not null) await direct.DisposeAsync(); }
    }
    private async Task SuperviseAsync(RegistryClient registry, string code)
    {
        while (!lifetime.IsCancellationRequested)
        {
            var observed = Peer;
            var done = await Task.WhenAny(observed.Completion!, Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token));
            if (lifetime.IsCancellationRequested) return;
            if (Peer != observed) continue;
            if (done != observed.Completion)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); probe.CancelAfter(TimeSpan.FromSeconds(8));
                try { if (await TryUpgradeAsync(probe.Token)) { ConnectionChanged?.Invoke(true, "已切换为局域网直连。"); Reconnected?.Invoke(); } }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                if (ConnectionKind == "局域网直连" && !Peer.HasPendingCalls) await CheckConnectionAsync();
                continue;
            }
            DisconnectedAt = DateTimeOffset.UtcNow;
            await Peer.DisposeAsync();
            IsReconnecting = autoReconnect;
            ConnectionChanged?.Invoke(false, autoReconnect ? "连接已断开，正在选择局域网或中继…" : "连接已断开。");
            if (!autoReconnect) return;
            int attempt = 0;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await reconnectWake.WaitAsync(TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Min(attempt++, 4)))), lifetime.Token);
                    await OpenAsync(registry, code, lifetime.Token);
                    ReconnectCount++;
                    IsReconnecting = false;
                    ConnectionChanged?.Invoke(true, "已恢复连接 · " + ConnectionKind);
                    Reconnected?.Invoke();
                    break;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // A fixed identity is temporarily absent while its host is offline.
                    // Keep retrying; only an explicit disconnect should stop recovery.
                    ConnectionChanged?.Invoke(false, "接入端暂时离线或授权码无效，等待设备重新上线…");
                }
                catch (Exception ex) { ConnectionChanged?.Invoke(false, "正在重连：" + ex.Message); }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        IsReconnecting = false;
        lifetime.Cancel(); socket?.Abort();
        if (supervisor is not null) await supervisor;
        if (Peer is not null) await Peer.DisposeAsync();
        if (socket is not null) await socket.DisposeAsync();
    }
}
