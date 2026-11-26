using System.Net.Sockets;
using System.Net.WebSockets;
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
    private readonly SemaphoreSlim publicPeerSlots = new(DeviceRegistration.SupportedMaxControllers);
    private readonly List<Task> publicPeers = [];
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
    public DeviceHost(RegistryClient registry, Func<IAgentAdapter>? factory = null, string? filesRoot = null, bool persistentIdentity = false, bool enableLan = true, bool? enableP2p = null)
    {
        this.registry = registry; this.factory = factory ?? (() => new CodexDesktopAdapter()); agents = new(this.factory); files = new(() => new FileTransferStore(filesRoot)); this.persistentIdentity = persistentIdentity; this.enableLan = enableLan;
        // Isolated HTTP relays require explicit opt-in, so local fixtures do not query public STUN.
        var relay = new Uri(registry.ServerIdentity);
        this.enableP2p = enableP2p ?? (relay.Scheme == Uri.UriSchemeHttps || !relay.IsLoopback);
    }

    private readonly bool enableLan;
    private readonly bool enableP2p;
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
            MailboxAgentAccount.Register(name, registry.ServerIdentity, path);
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
        if (!registered) { Lease = await registry.PostAsync<DeviceLease>("devices", new DeviceRegistration(name, identitySecret, DeviceRegistration.SupportedMaxControllers), timeout.Token); registered = true; }
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
    private Task ServePeerAsync(IConnectionTransport socket, CancellationToken ct) => ServeSessionPeerAsync(socket, ct, true);

    private async Task ServePublicPeerAsync(PublicPeerTransport transport, CancellationToken relayToken, Action finished)
    {
        try
        {
            await using (transport)
            {
                // A half-open offer belongs to this relay session. Once the
                // authenticated channel opens, only device shutdown owns it.
                using (var opening = CancellationTokenSource.CreateLinkedTokenSource(relayToken, lifetime.Token))
                {
                    opening.CancelAfter(TimeSpan.FromSeconds(15));
                    await transport.WaitReadyAsync(opening.Token);
                }
                await ServePeerAsync(transport, lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* A failed direct attempt leaves the relay available. */ }
        finally
        {
            publicPeerSlots.Release();
            finished();
        }
    }

    private async Task ServeSessionPeerAsync(IConnectionTransport socket, CancellationToken ct, bool physical)
    {
        try
        {
            using var stop = ct.Register(socket.Abort);
            await using var adapter = await agents.ConnectAsync(ct);
            await using var peer = new RpcPeer(socket);
            await using var channels = physical ? new SessionChannelMultiplexer(peer, (channel, token) => ServeSessionPeerAsync(channel, token, false)) : null;
            bool publicPeerAvailable = physical && socket is WebSocketTransport && enableP2p && PublicPeerTransport.Enabled;
            int publicOfferActive = 0;
            async Task<JsonNode> AcceptPublicOffer(JsonNode? args, CancellationToken token)
            {
                if (args is not JsonObject offer || offer["sdp"] is not JsonValue value ||
                    !value.TryGetValue<string>(out var sdp) || string.IsNullOrWhiteSpace(sdp) || sdp.Length > PublicPeerTransport.MaxDescriptionLength)
                    throw new IOException("点对点直连协商信息无效。");
                if (Interlocked.Exchange(ref publicOfferActive, 1) != 0)
                    throw new IOException("点对点直连正在协商。");
                if (!publicPeerSlots.Wait(0))
                {
                    Volatile.Write(ref publicOfferActive, 0);
                    throw new IOException("点对点直连已达到控制端数量上限。");
                }
                PublicPeerTransport? transport = null;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(6));
                    var response = await PublicPeerTransport.AcceptOfferAsync(sdp, deadline.Token);
                    transport = response.Transport;
                    if (response.Answer.Length > PublicPeerTransport.MaxDescriptionLength) throw new IOException("点对点直连协商信息过长。");
                    var reply = Json.Node(new { sdp = response.Answer });
                    var worker = ServePublicPeerAsync(transport, token, () => Volatile.Write(ref publicOfferActive, 0));
                    lock (publicPeers)
                    {
                        publicPeers.RemoveAll(task => task.IsCompleted);
                        publicPeers.Add(worker);
                    }
                    transport = null;
                    return reply;
                }
                catch
                {
                    try { if (transport is not null) await transport.DisposeAsync(); }
                    finally
                    {
                        publicPeerSlots.Release();
                        Volatile.Write(ref publicOfferActive, 0);
                    }
                    throw;
                }
            }
            using var pumping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int incremental = 0;
            int paged = 0;
            SessionView? historySource = null;
            int lastIncremental = 0;
            var sync = new SessionSyncEncoder();
            var updates = Channel.CreateBounded<SessionView>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            var completions = Channel.CreateBounded<SessionCompletion>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });
            // The root stream carries global completion notices once. Mini peers
            // only consume their selected session updates.
            if (physical) adapter.Completed += completion => { if (!completions.Writer.TryWrite(completion)) socket.Abort(); };
            adapter.Changed += view => updates.Writer.TryWrite(view);
            adapter.Faulted += message => { Status?.Invoke(message); socket.Abort(); };
            string? attachedSession = null;
            var chatImages = new ChatImageFiles();
            TaskCompletionSource? attaching = null;
            peer.ResponseSent += method =>
            {
                if (method == "attach") Interlocked.Exchange(ref attaching, null)?.TrySetResult();
            };
            bool SupportsFiles(AgentInfo info) => info.Id is not ("dsh" or "antigravity") || info.CanFiles;
            void RequireFileTransfers()
            {
                var info = adapter.Info;
                if (!SupportsFiles(info)) throw new NotSupportedException(info.Id == "dsh"
                    ? "当前 DSH 实例尚未开放文件传输。" : "当前 Antigravity 接入端尚未开放文件传输。");
            }
            bool UseHistoryPaging() => Volatile.Read(ref paged) > 0 || adapter.Info.Id is "codex" or "dsh" or "antigravity";
            SessionView WithFiles(SessionView view)
            {
                view = OperationSummary.ForTransport(view);
                if (!SupportsFiles(adapter.Info)) return view;
                try { view = ChatPresentation.IncludeFiles(view, files.Value.List(view.Id)); }
                catch (IOException) { view = view with { StatusDetail = view.StatusDetail + " · 输出文件列表暂不可用，请打开会话文件查看。" }; }
                return adapter.Info.Id == "codex" ? chatImages.Project(view) : view;
            }
            async Task<JsonNode> Attach(string id, CancellationToken token)
            {
                Volatile.Write(ref attaching, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var view = WithFiles(await adapter.AttachAsync(id, token));
                if (adapter.Info.Id == "hermes" || adapter.Info.Id == "dsh" && adapter.Info.CanFiles)
                    await adapter.SetFileWorkspaceAsync(files.Value.List(id).Outbox, token);
                historySource = view; attachedSession = view.Id;
                return Json.Node(UseHistoryPaging() ? HistoryPaging.Latest(view, Volatile.Read(ref paged) >= 2, Volatile.Read(ref paged) >= 3) : view);
            }
            string FileSession(JsonNode? args)
            {
                string id = args?["session"]?.ToString() ?? "";
                if (id.Length == 0 || id != attachedSession) throw new IOException("请先打开对应会话再操作文件。");
                return id;
            }
            async Task<JsonNode?> FileOperation(string method, JsonNode? args, CancellationToken token)
            {
                RequireFileTransfers();
                _ = FileSession(args); return await files.Value.HandleAsync(method, args!, chatImages, token);
            }
            async Task<JsonNode?> SendFiles(JsonNode? args, CancellationToken token)
            {
                RequireFileTransfers();
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
                "session-sync" => Json.Node(new { incremental = Interlocked.Exchange(ref incremental, args?["version"]?.GetValue<int>() >= 2 ? 2 : 1) >= 0, version = Volatile.Read(ref incremental), transportPing = true, sessionChannels = physical ? 1 : 0, publicP2p = publicPeerAvailable ? 1 : 0 }),
                "session-channel/open" when channels is not null => await channels.AcceptAsync(args, token),
                "p2p/offer" when publicPeerAvailable => await AcceptPublicOffer(args, token),
                "lan-info" => lan is null ? null : Json.Node(lan.Pairing),
                "agents" => Json.Node(new[] { adapter.Info with { CanFiles = SupportsFiles(adapter.Info), CanThumbnails = adapter.Info.Id is "hermes" or "codex" || adapter.Info.CanThumbnails } }),
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
            if (physical) Status?.Invoke(socket switch
            {
                LanTransport => "控制端已通过局域网直连。",
                PublicPeerTransport => "控制端已通过点对点直连。",
                _ => "远程端已通过服务端中继连接。"
            });
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var view in updates.Reader.ReadAllAsync(pumping.Token))
                    {
                        while (Volatile.Read(ref attaching) is { } pendingAttach)
                            await pendingAttach.Task.WaitAsync(pumping.Token);
                        if (view.Id != attachedSession) continue;
                        int mode = Volatile.Read(ref incremental);
                        if (mode != lastIncremental) { sync = new SessionSyncEncoder(textDeltas: mode >= 2); lastIncremental = mode; }
                        var enriched = WithFiles(view);
                        historySource = enriched;
                        if (UseHistoryPaging()) enriched = HistoryPaging.Latest(enriched, Volatile.Read(ref paged) >= 2, Volatile.Read(ref paged) >= 3);
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
            if (physical) Status?.Invoke("远程端已断开，授权码仍可用于连接。");
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
        Task[] pendingPublicPeers;
        lock (publicPeers) pendingPublicPeers = [.. publicPeers];
        await Task.WhenAll(pendingPublicPeers);
        publicPeerSlots.Dispose();
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
        var checkedSocket = socket;
        long receivedBefore = checkedSocket?.ReceivedBytes ?? 0;
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
            // A large JSON response must finish before the reader can dispatch a pong.
            // Receiving new fragments proves liveness; a stalled connection still fails the probe.
            if (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested && IsConnected &&
                ReferenceEquals(checkedPeer, Peer) && ReferenceEquals(checkedSocket, socket) &&
                (checkedSocket?.ReceivedBytes ?? 0) > receivedBefore) return true;
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
    private bool relaySupportsPublicPeer;
    private string? attached;
    private int historyMode;
    private long selectionRevision;
    public RpcPeer Peer { get; private set; } = null!;
    public PairResult? Device { get; private set; }
    public bool IsConnected => Peer is { IsClosed: false, Completion.IsCompleted: false };
    public bool SupportsSessionChannels => IsConnected && Peer.SupportsSessionChannels;
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
        peer.SessionChannels = new SessionChannelMultiplexer(peer);
        peer.Notification += (method, node) =>
        {
            if (method.StartsWith("session-channel/", StringComparison.Ordinal)) return;
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
        try { await OpenConnectionAsync(registry, code, ct); }
        catch
        {
            socket?.Abort();
            if (Peer is not null) await Peer.DisposeAsync();
            if (socket is not null) await socket.DisposeAsync();
            Peer = null!; socket = null;
            throw;
        }
    }
    private async Task OpenConnectionAsync(RegistryClient registry, string code, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string stage = "寻找局域网接入端";
        try
        {
            pairing ??= LanTrustStore.Load(registry.ServerIdentity, code);
            IConnectionTransport? direct = null;
            if (pairing is not null)
                try { direct = await LanHost.ConnectAsync(pairing, timeout.Token); }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException && !timeout.IsCancellationRequested) { }
            if (direct is not null)
            {
                stage = "局域网协议协商";
                socket = direct; Device = new PairResult(pairing!.Name, ""); ConnectionKind = "局域网直连";
                Peer = NewPeer(direct);
                await EnableSync(Peer, timeout.Token);
                if (!IsConnected) throw new IOException("局域网连接已关闭。");
                ConnectedAt = DateTimeOffset.UtcNow;
                return;
            }

            stage = "设备配对";
            Device = await registry.PostAsync<PairResult>("pair", new PairRequest(code), timeout.Token);
            stage = "建立中继 WebSocket";
            var connection = await registry.OpenSocketAsync("control", timeout.Token);
            socket = connection;
            stage = "等待接入端加入中继";
            await connection.WriteAsync(Json.Node(new { ticket = Device.Ticket }), timeout.Token);
            var response = await connection.ReadLimitedAsync(4096, timeout.Token);
            if (response["ok"]?.GetValue<bool>() != true)
            {
                string reason = response["error"]?.ToString() ?? "中继未确认控制连接。";
                throw new ConnectionOpenException(reason, reason.Contains("设备离线", StringComparison.Ordinal));
            }
            stage = "接入端协议协商";
            ConnectionKind = "服务器中继"; Peer = NewPeer(connection);
            relaySupportsPublicPeer = await EnableSync(Peer, timeout.Token);
            stage = "读取接入端直连信息";
            try
            {
                var info = await Peer.CallAsync("lan-info", null, timeout.Token, TimeSpan.FromSeconds(5));
                if (info is not null) { pairing = Json.Read<LanPairing>(info); LanTrustStore.Save(registry.ServerIdentity, code, pairing); }
            }
            catch (Exception ex) when (IsConnected && ex is IOException or JsonException or UnauthorizedAccessException or TimeoutException) { }
            await TryUpgradeLanAsync(timeout.Token);
            if (!IsConnected) throw new IOException("接入端连接已关闭。");
            ConnectedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested &&
            ex is not ConnectionOpenException && ex is HttpRequestException or WebSocketException or SocketException or IOException or JsonException or OperationCanceledException or TimeoutException)
        {
            throw ConnectionOpenException.During(stage, ex);
        }
    }
    private static async Task<bool> EnableSync(RpcPeer peer, CancellationToken ct)
    {
        try
        {
            var capabilities = await peer.CallAsync("session-sync", new { version = 2 }, ct);
            peer.SupportsTransportPing = capabilities?["transportPing"]?.GetValue<bool>() == true;
            peer.SupportsSessionChannels = capabilities?["sessionChannels"]?.GetValue<int>() >= 1;
            return capabilities?["publicP2p"] is JsonValue p2p && p2p.TryGetValue<int>(out int version) && version == 1;
        }
        catch (IOException) when (!peer.IsClosed && peer.Completion?.IsCompleted == false) { }
        return false;
    }

    private bool CanUpgrade() => IsConnected && !Peer.HasPendingCalls && Peer.SessionChannels?.ActiveCount == 0;

    private Task<bool> TryUpgradeLanAsync(CancellationToken ct) => pairing is { } trustedLan && ConnectionKind != "局域网直连"
        ? TryUpgradeAsync("局域网直连", (_, token) => LanHost.ConnectAsync(trustedLan, token), ct)
        : Task.FromResult(false);

    private static async Task<IConnectionTransport?> OpenPublicPeerAsync(RpcPeer relay, CancellationToken ct)
    {
        var (transport, offer) = await PublicPeerTransport.CreateOfferAsync(ct);
        try
        {
            if (offer.Length > PublicPeerTransport.MaxDescriptionLength) throw new IOException("点对点直连协商信息过长。");
            // Finishing this small signaling write keeps a cancelled probe from
            // interrupting the working relay socket and its other RPCs.
            var reply = await relay.CallChannelAsync("p2p/offer", new { sdp = offer }, ct);
            if (reply is not JsonObject answer || answer["sdp"] is not JsonValue value ||
                !value.TryGetValue<string>(out var sdp) || string.IsNullOrWhiteSpace(sdp) || sdp.Length > PublicPeerTransport.MaxDescriptionLength)
                throw new IOException("点对点直连协商应答无效。");
            await transport.ConnectAsync(sdp, ct);
            return transport;
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private async Task<bool> TryUpgradeAsync(string kind,
        Func<RpcPeer, CancellationToken, Task<IConnectionTransport?>> openDirect, CancellationToken ct)
    {
        if (!CanUpgrade()) return false;
        var previous = Peer;
        IConnectionTransport? direct = null; RpcPeer? candidate = null;
        try
        {
            direct = await openDirect(previous, ct);
            if (direct is null || !CanUpgrade()) return false;
            long revision = Interlocked.Read(ref selectionRevision);
            candidate = NewPeer(direct); await EnableSync(candidate, ct);
            if (direct is PublicPeerTransport)
            {
                if (!candidate.SupportsTransportPing) return false;
                var pong = await candidate.CallAsync("transport-ping", null, ct);
                if (pong is not JsonValue value || !value.TryGetValue<bool>(out bool ok) || !ok) return false;
            }
            if (historyMode > 0) await candidate.CallAsync("history-mode", new { version = historyMode }, ct);
            if (attached is { } id) await SessionHistory.ReadAsync(candidate, "attach", new { id }, ct);
            if (previous.SessionChannels?.TryRetireIfEmpty(() => previous.TryRedirectTo(candidate,
                () => !ct.IsCancellationRequested && Peer == previous && candidate.Completion?.IsCompleted == false &&
                    Interlocked.Read(ref selectionRevision) == revision)) != true) return false;
            socket = direct; Peer = candidate; ConnectionKind = kind;
            direct = null; candidate = null;
            await previous.DisposeAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or AuthenticationException or OperationCanceledException or ObjectDisposedException) { return false; }
        finally { if (candidate is not null) await candidate.DisposeAsync(); if (direct is not null) await direct.DisposeAsync(); }
    }

    private async Task SuperviseAsync(RegistryClient registry, string code)
    {
        // Publish the usable relay without waiting for the first public probe.
        await Task.Yield();
        long nextPublicProbe = 0;
        while (!lifetime.IsCancellationRequested)
        {
            if (relaySupportsPublicPeer && socket is WebSocketTransport && PublicPeerTransport.Enabled &&
                Environment.TickCount64 >= nextPublicProbe && CanUpgrade())
            {
                nextPublicProbe = Environment.TickCount64 + 30_000;
                using var publicProbe = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                publicProbe.CancelAfter(TimeSpan.FromSeconds(15));
                bool upgraded = false;
                try { upgraded = await TryUpgradeAsync("点对点直连", OpenPublicPeerAsync, publicProbe.Token); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* Retain the relay if optional transport setup fails. */ }
                if (upgraded && !lifetime.IsCancellationRequested)
                {
                    ConnectionChanged?.Invoke(true, "已切换为点对点直连。");
                    Reconnected?.Invoke();
                }
            }
            var observed = Peer;
            var done = await Task.WhenAny(observed.Completion!, Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token));
            if (lifetime.IsCancellationRequested) return;
            if (Peer != observed) continue;
            if (done != observed.Completion)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); probe.CancelAfter(TimeSpan.FromSeconds(8));
                try { if (await TryUpgradeLanAsync(probe.Token)) { ConnectionChanged?.Invoke(true, "已切换为局域网直连。"); Reconnected?.Invoke(); } }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                if (socket is not WebSocketTransport && !Peer.HasPendingCalls) await CheckConnectionAsync();
                continue;
            }
            DisconnectedAt = DateTimeOffset.UtcNow;
            IsReconnecting = autoReconnect;
            // Keep a usable relay long enough to recover when a previously
            // working direct path fails because UDP or the network changed.
            nextPublicProbe = socket is PublicPeerTransport ? Environment.TickCount64 + 30_000 : 0;
            ConnectionChanged?.Invoke(false, autoReconnect ? "连接已断开，正在重连…" : "连接已断开。");
            await Peer.DisposeAsync();
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
                catch (ConnectionOpenException ex) when (ex.InnerException is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
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
