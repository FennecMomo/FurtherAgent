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
    public DeviceLease? Lease { get; private set; }
    public event Action<string>? Status;
    public event Action<string>? CodeChanged;
    public DeviceHost(RegistryClient registry, Func<IAgentAdapter>? factory = null)
    { this.registry = registry; this.factory = factory ?? (() => new CodexDesktopAdapter()); }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        this.name = name;
        control = await OpenControlAsync(ct);
        supervisor = SuperviseAsync(control);
    }
    private async Task<WebSocketTransport> OpenControlAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        Lease ??= await registry.PostAsync<DeviceLease>("devices", new DeviceRegistration(name), timeout.Token);
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
            var updates = Channel.CreateBounded<SessionView>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            adapter.Changed += view => updates.Writer.TryWrite(view);
            adapter.Faulted += message => { Status?.Invoke(message); socket.Abort(); };
            peer.Handler = async (method, args, token) => method switch
            {
                "agents" => Json.Node(new[] { adapter.Info }),
                "sessions" => Json.Node(await adapter.ListAsync(token)),
                "attach" => Json.Node(await adapter.AttachAsync(args!["id"]!.GetValue<string>(), token)),
                "send" => await Execute(() => adapter.SendAsync(args!["text"]!.GetValue<string>(), token)),
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
                    { await peer.NotifyAsync("session", view); await Task.Delay(100, pumping.Token); }
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
        if (control is not null) await control.DisposeAsync();
        if (Lease is not null)
            try { await registry.PostAsync<JsonNode>("unregister", new LeaseProof(Lease.Code, Lease.Secret)); } catch { }
    }
}

public sealed class RemoteDevice(bool autoReconnect = true) : IAsyncDisposable
{
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
            Peer.Notification += (method, node) => Notification?.Invoke(method, node);
            Peer.Start();
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private async Task SuperviseAsync(RegistryClient registry, string code)
    {
        while (!lifetime.IsCancellationRequested)
        {
            await Peer.Completion!;
            if (lifetime.IsCancellationRequested) return;
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
