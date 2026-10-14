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
    private FileStream? identityLock;
    public DeviceLease? Lease { get; private set; }
    public event Action<string>? Status;
    public event Action<string>? CodeChanged;
    public DeviceHost(RegistryClient registry, Func<IAgentAdapter>? factory = null, string? filesRoot = null, bool persistentIdentity = false)
    { this.registry = registry; this.factory = factory ?? (() => new CodexDesktopAdapter()); agents = new(this.factory); files = new(() => new FileTransferStore(filesRoot)); this.persistentIdentity = persistentIdentity; }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        this.name = name;
        if (persistentIdentity)
        {
            string path = DeviceIdentity.DefaultPath(registry.ServerIdentity, name);
            _ = DeviceIdentity.LoadOrCreate(path);
            identityLock = DeviceIdentity.Lock(path);
            identitySecret = DeviceIdentity.LoadOrCreate(path);
        }
        control = await OpenControlAsync(ct);
        supervisor = SuperviseAsync(control);
    }
    private async Task<WebSocketTransport> OpenControlAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        Lease ??= await registry.PostAsync<DeviceLease>("devices", new DeviceRegistration(name, identitySecret, 8), timeout.Token);
        if (identitySecret is not null && (Lease.Code != DeviceIdentity.Code(identitySecret) || Lease.Secret != identitySecret))
            throw new IOException("中继尚不支持固定配对码，请升级中继；设备身份已保留，不会退回随机码。");
        var socket = await registry.OpenSocketAsync("host", timeout.Token);
        try
        {
            await socket.WriteAsync(Json.Node(new { code = Lease.Code, secret = Lease.Secret }), timeout.Token);
            var response = await socket.ReadLimitedAsync(4096, timeout.Token);
            if (response["ok"]?.GetValue<bool>() != true)
            {
                if (response["resetLease"]?.GetValue<bool>() == true) Lease = null;
                throw new IOException(response["error"]?.ToString() ?? "设备中继连接失败。");
            }
            CodeChanged?.Invoke(Lease.Code);
            return socket;
        }
        catch { await socket.DisposeAsync(); throw; }
    }
    private async Task SuperviseAsync(WebSocketTransport socket)
    {
        while (!lifetime.IsCancellationRequested)
        {
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var tasks = new List<Task>();
            try
            {
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
                cycle.Cancel(); await socket.DisposeAsync();
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
            using var stop = ct.Register(socket.Abort);
            await using var adapter = await agents.ConnectAsync(ct);
            await using var peer = new RpcPeer(socket);
            using var pumping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int incremental = 0;
            int paged = 0;
            SessionView? historySource = null;
            bool lastIncremental = false;
            var sync = new SessionSyncEncoder();
            var updates = Channel.CreateBounded<SessionView>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            var completions = Channel.CreateBounded<SessionCompletion>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });
            adapter.Completed += completion => { if (!completions.Writer.TryWrite(completion)) socket.Abort(); };
            adapter.Changed += view => updates.Writer.TryWrite(view);
            adapter.Faulted += message => { Status?.Invoke(message); socket.Abort(); };
            string? attachedSession = null;
            SessionView WithFiles(SessionView view)
            {
                if (adapter.Info.Id == "dsh" && !adapter.Info.CanFiles) return view;
                try { return view with { OutputFiles = files.Value.List(view.Id).Files.Where(f => f.Id.StartsWith("outbox/", StringComparison.Ordinal)).ToList() }; }
                catch (IOException) { return view with { StatusDetail = view.StatusDetail + " · 输出文件列表暂不可用，请打开会话文件查看。" }; }
            }
            async Task<JsonNode> Attach(string id, CancellationToken token)
            {
                var view = WithFiles(await adapter.AttachAsync(id, token));
                if (adapter.Info.Id == "hermes") await adapter.SetFileWorkspaceAsync(files.Value.List(id).Outbox, token);
                historySource = view; attachedSession = view.Id;
                return Json.Node(Volatile.Read(ref paged) > 0 || adapter.Info.Id == "codex" ? HistoryPaging.Latest(view, Volatile.Read(ref paged) == 2) : view);
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
                "history-mode" => Json.Node(new { enabled = Interlocked.Exchange(ref paged, args?["version"]?.GetValue<int>() == 2 ? 2 : 1) >= 0, version = Volatile.Read(ref paged) }),
                "history" => Json.Node(HistoryPaging.Page(historySource is { } history && history.Id == FileSession(args) ? history : throw new IOException("请先打开会话。"),
                    args!["before"]!.GetValue<int>(), args["revision"]!.GetValue<string>(), Volatile.Read(ref paged) == 2)),
                "session-sync" => Json.Node(new { incremental = Interlocked.Exchange(ref incremental, 1) >= 0, version = 1 }),
                "agents" => Json.Node(new[] { adapter.Info with { CanFiles = adapter.Info.Id != "dsh" || adapter.Info.CanFiles, CanThumbnails = adapter.Info.Id == "hermes" } }),
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
            Status?.Invoke("远程端已通过服务端中继连接。");
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var view in updates.Reader.ReadAllAsync(pumping.Token))
                    {
                        if (view.Id != attachedSession) continue;
                        bool mode = Volatile.Read(ref incremental) == 1;
                        if (mode != lastIncremental) { sync = new SessionSyncEncoder(); lastIncremental = mode; }
                        var enriched = WithFiles(view);
                        historySource = enriched;
                        if (Volatile.Read(ref paged) > 0 || adapter.Info.Id == "codex") enriched = HistoryPaging.Latest(enriched, Volatile.Read(ref paged) == 2);
                        var update = sync.Encode(enriched);
                        if (update is not null)
                            await peer.NotifyAsync(mode ? "session-update" : "session", mode ? (object)update : enriched);
                        await Task.Delay(500, pumping.Token);
                    }
                }
                catch (Exception) { socket.Abort(); }
            });
            await peer.Completion!;
            updates.Writer.TryComplete(); completions.Writer.TryComplete(); pumping.Cancel(); await Task.WhenAll(pump, completionPump);
            Status?.Invoke("远程端已断开，授权码仍可用于连接。");
        }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Status?.Invoke("远程会话连接结束：" + ex.Message); }
    }
    private static async Task<JsonNode?> Execute(Func<Task> operation) { await operation(); return Json.Node(new { ok = true }); }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); control?.Abort();
        if (supervisor is not null) await supervisor;
        await agents.DisposeAsync();
        if (files.IsValueCreated) files.Value.Dispose();
        if (control is not null) await control.DisposeAsync();
        if (Lease is not null)
            try { await registry.PostAsync<JsonNode>("unregister", new LeaseProof(Lease.Code, Lease.Secret)); } catch { }
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
    public async Task<bool> CheckConnectionAsync()
    {
        if (!IsConnected) { WakeReconnect(); return false; }
        var checkedPeer = Peer;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await checkedPeer.CallAsync("agents", null, timeout.Token);
            return ReferenceEquals(checkedPeer, Peer) && IsConnected;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
            or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
            if (!lifetime.IsCancellationRequested && ReferenceEquals(checkedPeer, Peer))
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
    private WebSocketTransport? socket;
    private Task? supervisor;
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
    private async Task OpenAsync(RegistryClient registry, string code, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Device = await registry.PostAsync<PairResult>("pair", new PairRequest(code), timeout.Token);
        var connection = await registry.OpenSocketAsync("control", timeout.Token);
        try
        {
            await connection.WriteAsync(Json.Node(new { ticket = Device.Ticket }), timeout.Token);
            var response = await connection.ReadLimitedAsync(4096, timeout.Token);
            if (response["ok"]?.GetValue<bool>() != true) throw new IOException(response["error"]?.ToString() ?? "中继连接失败。");
            socket = connection;
            Peer = new RpcPeer(connection);
            var sync = new SessionSyncDecoder();
            Peer.Notification += (method, node) =>
            {
                if (method == "session-update")
                {
                    // Maintain the baseline even before a UI/CLI observer subscribes.
                    var view = sync.Apply(Json.Read<SessionUpdate>(node));
                    Notification?.Invoke("session", Json.Node(view));
                }
                else Notification?.Invoke(method, node);
            };
            Peer.Start();
            try { await Peer.CallAsync("session-sync", new { version = 1 }, timeout.Token); }
            catch (IOException) when (Peer.Completion?.IsCompleted == false) { /* Legacy Host sends full snapshots. */ }
            ConnectedAt = DateTimeOffset.UtcNow;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private async Task SuperviseAsync(RegistryClient registry, string code)
    {
        while (!lifetime.IsCancellationRequested)
        {
            await Peer.Completion!;
            if (lifetime.IsCancellationRequested) return;
            DisconnectedAt = DateTimeOffset.UtcNow;
            await Peer.DisposeAsync();
            IsReconnecting = autoReconnect;
            ConnectionChanged?.Invoke(false, autoReconnect ? "中继连接已断开，正在重连…" : "中继连接已断开。");
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
                    ConnectionChanged?.Invoke(true, "中继连接已恢复。");
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
