using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace FurtherAgent.Core;

public sealed class DeviceHost : IAsyncDisposable
{
    private readonly RegistryClient registry;
    private readonly Func<IAgentAdapter> factory;
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
    { this.registry = registry; this.factory = factory ?? (() => new CodexDesktopAdapter()); files = new(() => new FileTransferStore(filesRoot)); this.persistentIdentity = persistentIdentity; }

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
        Lease ??= await registry.PostAsync<DeviceLease>("devices", new DeviceRegistration(name, identitySecret), timeout.Token);
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
                        if (tasks.Count >= 2) throw new IOException("中继会话数量异常。");
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
            await using var adapter = factory();
            await using var peer = new RpcPeer(socket);
            using var pumping = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int incremental = 0;
            bool lastIncremental = false;
            var sync = new SessionSyncEncoder();
            var updates = Channel.CreateBounded<SessionView>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            adapter.Changed += view => updates.Writer.TryWrite(view);
            adapter.Faulted += message => { Status?.Invoke(message); socket.Abort(); };
            string? attachedSession = null;
            SessionView WithFiles(SessionView view)
            {
                if (adapter.Info.Id != "hermes") return view;
                try { return view with { OutputFiles = files.Value.List(view.Id).Files.Where(f => f.Id.StartsWith("outbox/", StringComparison.Ordinal)).ToList() }; }
                catch (IOException) { return view with { StatusDetail = view.StatusDetail + " · 输出文件列表暂不可用，请打开会话文件查看。" }; }
            }
            async Task<JsonNode> Attach(string id, CancellationToken token)
            {
                var view = WithFiles(await adapter.AttachAsync(id, token)); attachedSession = view.Id; return Json.Node(view);
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
                "session-sync" => Json.Node(new { incremental = Interlocked.Exchange(ref incremental, 1) >= 0, version = 1 }),
                "agents" => Json.Node(new[] { adapter.Info with { CanFiles = adapter.Info.Id != "dsh" || adapter.Info.CanFiles, CanThumbnails = adapter.Info.Id == "hermes" } }),
                "sessions" => Json.Node(await adapter.ListAsync(token)),
                "attach" => await Attach(args!["id"]!.GetValue<string>(), token),
                "send-files" => await SendFiles(args, token),
                _ when method.StartsWith("files/", StringComparison.Ordinal) => await FileOperation(method, args, token),
                "create" => Json.Node(await adapter.CreateAsync(args!["id"]!.GetValue<string>(), args["title"]?.ToString() ?? "新会话", token)),
                "settings" => Json.Node(await adapter.SettingsAsync(token)),
                "archive" => await Execute(() => adapter.ArchiveAsync(FileSession(args), args!["archived"]!.GetValue<bool>(), token)),
                "configure" => await Execute(() => adapter.ConfigureAsync(args!["model"]?.ToString() ?? "", args["provider"]?.ToString() ?? "", args["mode"]?.ToString() ?? "default", token)),
                "send" => await Execute(() => adapter.SendAsync(args!["text"]!.GetValue<string>(), args["delivery"]?.ToString() ?? "auto", token)),
                "interrupt-send" => await Execute(() => adapter.InterruptAndSendAsync(args!["turnId"]!.GetValue<string>(), args["text"]!.GetValue<string>(), token)),
                "interrupt" => await Execute(() => adapter.InterruptAsync(token)),
                "respond" => await Execute(() => adapter.RespondAsync(args!["id"]!.GetValue<string>(), args["decision"]?.GetValue<string>() ?? "", args["response"], token)),
                _ => throw new IOException("未知操作。")
            };
            peer.Start();
            Status?.Invoke("远程端已通过服务端中继连接。");
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var view in updates.Reader.ReadAllAsync(pumping.Token))
                    {
                        bool mode = Volatile.Read(ref incremental) == 1;
                        if (mode != lastIncremental) { sync = new SessionSyncEncoder(); lastIncremental = mode; }
                        var enriched = WithFiles(view);
                        var update = sync.Encode(enriched);
                        if (update is not null)
                            await peer.NotifyAsync(mode ? "session-update" : "session", mode ? (object)update : enriched);
                        await Task.Delay(500, pumping.Token);
                    }
                }
                catch (Exception) { socket.Abort(); }
            });
            await peer.Completion!;
            updates.Writer.TryComplete(); pumping.Cancel(); await pump;
            Status?.Invoke("远程端已断开，授权码仍可用于连接。");
        }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Status?.Invoke("远程会话连接结束：" + ex.Message); }
    }
    private static async Task<JsonNode?> Execute(Func<Task> operation) { await operation(); return Json.Node(new { ok = true }); }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); control?.Abort();
        if (supervisor is not null) await supervisor;
        if (files.IsValueCreated) files.Value.Dispose();
        if (control is not null) await control.DisposeAsync();
        if (Lease is not null)
            try { await registry.PostAsync<JsonNode>("unregister", new LeaseProof(Lease.Code, Lease.Secret)); } catch { }
        identityLock?.Dispose();
    }
}

public sealed class RemoteDevice(bool autoReconnect = true) : IAsyncDisposable
{
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
        await OpenAsync(registry, code, ct);
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
            ConnectionChanged?.Invoke(false, autoReconnect ? "中继连接已断开，正在重连…" : "中继连接已断开。");
            if (!autoReconnect) return;
            int attempt = 0;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Min(attempt++, 4)))), lifetime.Token);
                    await OpenAsync(registry, code, lifetime.Token);
                    ReconnectCount++;
                    ConnectionChanged?.Invoke(true, "中继连接已恢复。");
                    Reconnected?.Invoke();
                    break;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    ConnectionChanged?.Invoke(false, "授权码已失效，请获取 B 的新授权码后重新连接。"); return;
                }
                catch (Exception ex) { ConnectionChanged?.Invoke(false, "正在重连：" + ex.Message); }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); socket?.Abort();
        if (supervisor is not null) await supervisor;
        if (Peer is not null) await Peer.DisposeAsync();
        if (socket is not null) await socket.DisposeAsync();
    }
}
