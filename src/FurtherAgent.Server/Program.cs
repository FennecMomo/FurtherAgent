using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using FurtherAgent.Core;
using FurtherAgent.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new MailboxStore());
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 90, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
var gate = new object();
var devices = new Dictionary<string, RelayDevice>();
var tickets = new Dictionary<string, Ticket>();
var mailboxes = app.Services.GetRequiredService<MailboxStore>();
string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
string Normalize(string? code) => (code ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
void Clean()
{
    var now = DateTimeOffset.UtcNow;
    foreach (var key in devices.Where(p => p.Value.Control is null && p.Value.Expires <= now).Select(p => p.Key).ToArray()) devices.Remove(key);
    foreach (var key in tickets.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) tickets.Remove(key);
}
bool Authorized(string? code, string? secret, out RelayDevice? device)
{
    Clean(); device = null;
    return code is not null && secret is not null && devices.TryGetValue(code, out device) &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(device.Secret), System.Text.Encoding.UTF8.GetBytes(secret));
}
async Task<WebSocketTransport?> Accept(HttpContext context)
{
    if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.ContainsKey("Origin"))
    { context.Response.StatusCode = 400; return null; }
    return new WebSocketTransport(await context.WebSockets.AcceptWebSocketAsync());
}
app.MapMailboxes(mailboxes, input =>
{
    lock (gate)
    {
        Clean(); string code = Normalize(input);
        if (code.Length == 32) code = devices.FirstOrDefault(p => p.Value.LegacyCode == code).Key ?? code;
        if (!devices.TryGetValue(code, out var device)) throw new MailboxException(503, "设备尚未在线配对，请先连接设备一次。");
        if (device.Control?.IsOpen != true) throw new MailboxException(503, "首次绑定留言箱需要设备在线，已有留言凭据可在设备离线时继续使用。");
        return new MailboxOnlineDevice(code, device.Name);
    }
});
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.9.1", mode = "relay", softwareVersion = typeof(MailboxStore).Assembly.GetName().Version?.ToString(3), mailboxVersion = 1, persistentMailbox = true }));
app.MapPost("/devices", (DeviceRegistration input) =>
{
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100) return Results.BadRequest("设备名称无效。");
    lock (gate)
    {
        Clean();
        if (input.IdentitySecret is { } identity)
        {
            string fixedCode;
            try { fixedCode = DeviceIdentity.Code(identity); } catch (ArgumentException) { return Results.BadRequest("设备身份无效。"); }
            identity = identity.ToUpperInvariant();
            try { mailboxes.RegisterDevice(fixedCode, input.Name, identity); }
            catch (MailboxException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { return Results.Json(new { error = "留言存储暂不可用，请稍后重试。" }, statusCode: 503); }
            if (devices.TryGetValue(fixedCode, out var existing))
            {
                if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(existing.Secret), System.Text.Encoding.ASCII.GetBytes(identity)))
                    return Results.Conflict("设备身份冲突。");
                return Results.Ok(new DeviceLease(fixedCode, existing.Secret, existing.Expires));
            }
            if (devices.Count >= 1000) return Results.StatusCode(503);
            var stable = new RelayDevice(input.Name, identity, DateTimeOffset.UtcNow.AddMinutes(2), Math.Clamp(input.MaxControllers, 1, 8), DeviceIdentity.LegacyCode(identity));
            devices.Add(fixedCode, stable);
            return Results.Ok(new DeviceLease(fixedCode, identity, stable.Expires));
        }
        if (devices.Count >= 1000) return Results.StatusCode(503);
        string code;
        do { code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)); } while (devices.ContainsKey(code));
        var device = new RelayDevice(input.Name, Token(), DateTimeOffset.UtcNow.AddMinutes(2), Math.Clamp(input.MaxControllers, 1, 8));
        try { mailboxes.RegisterDevice(code, input.Name, device.Secret); }
        catch (MailboxException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return Results.Json(new { error = "留言存储暂不可用，请稍后重试。" }, statusCode: 503); }
        devices.Add(code, device);
        return Results.Ok(new DeviceLease(code, device.Secret, device.Expires));
    }
});
app.MapPost("/unregister", (LeaseProof input) =>
{
    lock (gate)
    {
        if (!Authorized(input.Code, input.Secret, out var device)) return Results.Unauthorized();
        devices.Remove(input.Code);
        foreach (var key in tickets.Where(p => p.Value.Code == input.Code).Select(p => p.Key).ToArray()) tickets.Remove(key);
        device!.Control?.Abort(); foreach (var session in device.Sessions.Values) session.Stop();
        return Results.Ok(new { ok = true });
    }
});
app.MapPost("/pair", (PairRequest input) =>
{
    lock (gate)
    {
        Clean(); string code = Normalize(input.Code);
        if (code.Length == 32) code = devices.FirstOrDefault(p => p.Value.LegacyCode == code).Key ?? code;
        if (!devices.TryGetValue(code, out var device)) return Results.NotFound("授权码不存在或已过期。");
        if (device.Control?.IsOpen != true) return Results.Conflict("B 暂时离线，正在等待重连。");
        if (device.Sessions.Count >= device.MaxControllers) return Results.Conflict("控制端数量达到上限；旧版接入端需升级后才能多端连接。");
        if (tickets.Count >= 3000) return Results.StatusCode(503);
        string token = Token();
        tickets[token] = new Ticket(code, DateTimeOffset.UtcNow.AddSeconds(60));
        return Results.Ok(new PairResult(device.Name, token));
    }
});

// B's control channel: active while sharing. B never accepts an inbound connection.
app.Map("/host", async (HttpContext context) =>
{
    await using var socket = await Accept(context);
    if (socket is null) return;
    RelayDevice? device = null;
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(10));
    try
    {
        var hello = await socket.ReadLimitedAsync(4096, timeout.Token);
        string? error = null;
        bool resetLease = false;
        lock (gate)
        {
            if (!Authorized(hello["code"]?.GetValue<string>(), hello["secret"]?.GetValue<string>(), out device))
            { error = "授权已过期，请重新注册设备。"; resetLease = true; }
            else if (device!.Control is not null) { device = null; error = "该设备已有连接。"; }
            else device.Control = socket;
        }
        if (error is not null) { await socket.WriteAsync(Json.Node(new { error, resetLease }), timeout.Token); return; }
        await socket.WriteAsync(Json.Node(new { ok = true, protocol = 2 }), timeout.Token);
        while (true)
        {
            var message = await socket.ReadLimitedAsync(4096, context.RequestAborted);
            if (message["type"]?.GetValue<string>() != "ping") throw new IOException("未知控制消息。");
        }
    }
    catch (Exception) { /* Expected disconnect, invalid input or timeout. Never log credentials/payloads. */ }
    finally
    {
        lock (gate)
        {
            if (device is not null && ReferenceEquals(device.Control, socket))
            { device.Control = null; device.Expires = DateTimeOffset.UtcNow.AddMinutes(2); foreach (var active in device.Sessions.Values) active.Stop(); }
        }
    }
});

// B opens a second outbound socket for each authorized controller.
app.Map("/join", async (HttpContext context) =>
{
    await using var socket = await Accept(context);
    if (socket is null) return;
    RelaySession? session = null;
    bool joined = false;
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(10));
    try
    {
        var hello = await socket.ReadLimitedAsync(4096, timeout.Token);
        lock (gate)
        {
            if (Authorized(hello["code"]?.GetValue<string>(), hello["secret"]?.GetValue<string>(), out var device) &&
                device!.Sessions.TryGetValue(hello["sessionId"]?.GetValue<string>() ?? "", out var candidate) && candidate.Host is null)
            { session = candidate; candidate.Host = socket; joined = true; }
        }
        if (!joined) { await socket.WriteAsync(Json.Node(new { error = "中继会话授权无效。" }), timeout.Token); return; }
        session!.Joined.TrySetResult(socket);
        using var closed = context.RequestAborted.Register(session.Stop);
        await session.Done.Task;
    }
    catch (Exception) { if (joined) session?.Stop(); }
});

app.Map("/control", async (HttpContext context) =>
{
    await using var socket = await Accept(context);
    if (socket is null) return;
    RelaySession? session = null;
    RelayDevice? device = null;
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(15));
    try
    {
        var hello = await socket.ReadLimitedAsync(4096, timeout.Token);
        string? error = null;
        WebSocketTransport? control = null;
        lock (gate)
        {
            Clean(); var token = hello["ticket"]?.GetValue<string>() ?? "";
            if (!tickets.Remove(token, out var ticket) || !devices.TryGetValue(ticket.Code, out device) || device.Control?.IsOpen != true)
                error = "连接票据无效、已使用或设备离线。";
            else if (device.Sessions.Count >= device.MaxControllers) error = "控制端数量达到上限，请升级接入端或断开不用的连接。";
            else { session = new RelaySession(socket); device.Sessions.Add(session.Id, session); control = device.Control; }
        }
        if (error is not null) { await socket.WriteAsync(Json.Node(new { error }), timeout.Token); return; }
        await control!.WriteAsync(Json.Node(new { type = "connect", sessionId = session!.Id }), timeout.Token);
        var hostSocket = await session.Joined.Task.WaitAsync(timeout.Token);
        await hostSocket.WriteAsync(Json.Node(new { ok = true, protocol = 2 }), timeout.Token);
        await socket.WriteAsync(Json.Node(new { ok = true, protocol = 2 }), timeout.Token);
        using var stop = context.RequestAborted.Register(session.Stop);
        async Task Pump(WebSocketTransport from, WebSocketTransport to)
        {
            try { while (true) await to.WriteAsync(await from.ReadAsync(session.Lifetime.Token), session.Lifetime.Token); }
            finally { session.Stop(); }
        }
        await Task.WhenAll(Pump(socket, hostSocket), Pump(hostSocket, socket));
    }
    catch (Exception) { /* Both directions are cancelled below. */ }
    finally
    {
        if (session is not null)
        {
            session.Stop();
            lock (gate) { device?.Sessions.Remove(session.Id); }
            session.Done.TrySetResult();
        }
    }
});
app.Run();

sealed class RelayDevice(string name, string secret, DateTimeOffset expires, int maxControllers, string? legacyCode = null)
{
    public string Name { get; } = name;
    public string Secret { get; } = secret;
    public DateTimeOffset Expires { get; set; } = expires;
    public WebSocketTransport? Control { get; set; }
    public int MaxControllers { get; } = maxControllers;
    public string? LegacyCode { get; } = legacyCode;
    public Dictionary<string, RelaySession> Sessions { get; } = [];
}
sealed class RelaySession(WebSocketTransport controller)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public WebSocketTransport? Host { get; set; }
    public CancellationTokenSource Lifetime { get; } = new();
    public TaskCompletionSource<WebSocketTransport> Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Stop() { Lifetime.Cancel(); controller.Abort(); Host?.Abort(); Joined.TrySetCanceled(); }
}
sealed record Ticket(string Code, DateTimeOffset Expires);
