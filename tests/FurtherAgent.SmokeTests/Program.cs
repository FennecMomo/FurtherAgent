using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

if (args.Contains("--mobile-ui-fixture")) { await MobileUiFixture.RunAsync(); return; }
if (args.Contains("--speech-checks")) { await SpeechChecks.Run(); return; }

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
if (args.Contains("--codex-history-checks")) { await CodexHistoryChecks.Run(Check); return; }
if (args.Contains("--codex-activation-checks")) { await CodexActivationChecks.Run(Check); return; }
if (args.Contains("--codex-activation-live")) { await CodexActivationChecks.RunLive(Check); return; }
if (args.Length == 2 && args[0] == "--codex-history-live") { await CodexHistoryChecks.RunLive(args[1], Check); return; }
if (args.Length == 4 && args[0] == "--paging-live") { await PagingChecks.RunLive(args[1], args[2], args[3], Check); return; }
async Task Reject(Func<Task> action, string name)
{
    bool rejected = false;
    try { await action(); } catch { rejected = true; }
    Check(rejected, name);
}
async Task Eventually(Func<bool> condition, int seconds = 15)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    while (!condition()) await Task.Delay(100, timeout.Token);
}
int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
if (args.Contains("--dsh-live"))
{
    await DshLiveChecks.Run(Check, args.Contains("--controls"));
    Console.WriteLine($"ALL {passed} LIVE DSH CHECKS PASSED");
    return;
}
await TlsRetryChecks.Run(Check);
await ConnectionProfileChecks.Run(Check);
ConversationChecks.Run(Check);
await CodexHistoryChecks.Run(Check);
await CodexActivationChecks.Run(Check);
await DshDiscoveryChecks.Run(Check);
SessionSyncChecks.Run(Check);
await ThumbnailChecks.Run(Check);
await RpcSchedulingChecks.Run(Check);
await InterruptSendChecks.Run(Check);
using (var stream = new MemoryStream())
{
    await Frames.WriteAsync(stream, Json.Node(new { text = "你好，Codex 🚀" }), default);
    stream.Position = 0;
    Check((await Frames.ReadAsync(stream, default))["text"]!.ToString() == "你好，Codex 🚀", "UTF-8 framed transport round trip");
}
await Reject(async () => await Frames.ReadAsync(new MemoryStream([255, 255, 255, 127]), default), "oversized frame rejected");
var state = JsonNode.Parse("""{"messages":[{"text":"old"}],"flag":true}""")!;
StatePatches.Apply(state, JsonNode.Parse("""[{"op":"replace","path":["messages",0,"text"],"value":"new"},{"op":"add","path":["messages",1],"value":{"text":"second"}},{"op":"remove","path":["flag"]}]""")!.AsArray());
Check(state["messages"]![0]!["text"]!.ToString() == "new" && state["messages"]!.AsArray().Count == 2 && state["flag"] is null, "desktop state patches");
await Reject(() => { _ = new RegistryClient("http://example.com"); return Task.CompletedTask; }, "public plaintext relay forbidden");
Check(typeof(PairResult).GetProperties().All(p => p.Name is not ("Host" or "Port" or "Fingerprint")), "pairing never exposes or requires a client endpoint");

string configuration = AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string serverDll = Path.Combine(root, $"src/FurtherAgent.Server/bin/{configuration}/net10.0/FurtherAgent.Server.dll");
int serverPort = FreePort();
Process StartServer()
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{serverPort}");
    var process = Process.Start(start) ?? throw new Exception("server startup failed");
    process.BeginOutputReadLine(); process.BeginErrorReadLine();
    return process;
}
var server = StartServer();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
async Task WaitForServer()
{
    bool ready = false;
    for (int i = 0; i < 40 && !server.HasExited; i++)
    {
        try { ready = (await http.GetAsync($"http://127.0.0.1:{serverPort}/health")).IsSuccessStatusCode; if (ready) break; } catch { }
        await Task.Delay(150);
    }
    Check(ready, "relay server ready");
}
try
{
    await WaitForServer();
    using var registry = new RegistryClient($"http://127.0.0.1:{serverPort}");
    await MobileChecks.RunAsync(registry, $"http://127.0.0.1:{serverPort}", Check);
    await FileChecks.RunAsync(registry, Check);
    await HermesChecks.RunAsync(registry, Check);
    await CliChecks.RunAsync(registry, $"http://127.0.0.1:{serverPort}", Check);
    // Isolate the extra pagination requests from the per-IP production rate limit.
    server.Kill(true); await server.WaitForExitAsync(); server.Dispose(); server = StartServer(); await WaitForServer();
    await PagingChecks.RunAsync(registry, $"http://127.0.0.1:{serverPort}", Check);
    await Reject(async () => await registry.PostAsync<PairResult>("pair", new PairRequest("INVALID")), "unknown authorization code rejected");
    await using (var host = new DeviceHost(registry, () => new TestAdapter()))
    {
        await host.StartAsync("Smoke B");
        string originalCode = host.Lease!.Code;
        Check(originalCode.Length == 8, "server issues eight-character authorization code");
        await Reject(async () => await registry.PostAsync<JsonNode>("unregister", new LeaseProof(originalCode, "wrong")), "device secret required to revoke sharing");
        await using (var secondHost = new DeviceHost(registry, () => new TestAdapter()))
        {
            await secondHost.StartAsync("Smoke C");
            Check(secondHost.Lease!.Code != originalCode, "active device authorization codes are unique");
        }
        // Use a ticket once, then try it again at the actual WebSocket authorization boundary.
        var pair = await registry.PostAsync<PairResult>("pair", new PairRequest(originalCode));
        await using (var raw = await registry.OpenSocketAsync("control", default))
        {
            await raw.WriteAsync(Json.Node(new { ticket = pair.Ticket }), default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Check((await raw.ReadAsync(timeout.Token))["ok"]?.GetValue<bool>() == true, "relay negotiates both outbound sockets");
            await using var replay = await registry.OpenSocketAsync("control", timeout.Token);
            await replay.WriteAsync(Json.Node(new { ticket = pair.Ticket }), timeout.Token);
            // Rejection can arrive as JSON or immediate socket close: the server aborts
            // rejected sockets, so delivery of its final error frame is not guaranteed.
            bool rejectedReplay;
            try { rejectedReplay = (await replay.ReadAsync(timeout.Token))["error"] is not null; }
            catch (System.Net.WebSockets.WebSocketException) { rejectedReplay = true; }
            Check(rejectedReplay, "pairing ticket cannot be replayed");
        }
        await Task.Delay(300);
        await using (var forged = await registry.OpenSocketAsync("join", default))
        {
            await forged.WriteAsync(Json.Node(new { code = originalCode, secret = "wrong", sessionId = "fake" }), default);
            using var timeout = new CancellationTokenSource(5000);
            bool rejectedJoin;
            try { rejectedJoin = (await forged.ReadAsync(timeout.Token))["error"] is not null; }
            catch (System.Net.WebSockets.WebSocketException) { rejectedJoin = true; }
            Check(rejectedJoin, "unauthorized B data socket rejected");
        }
        await using var remote = new RemoteDevice();
        await remote.ConnectAsync(registry, originalCode);
        Check(remote.Device?.Name == "Smoke B", "A connects to B using only the code and server");
        Check(Json.Read<AgentInfo[]>(await remote.Peer.CallAsync("agents", null)).Length == 1, "agent discovery through server relay");
        var sessions = Json.Read<SessionInfo[]>(await remote.Peer.CallAsync("sessions", null));
        var view = Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id = sessions[0].Id }));
        Check(view.Title == "Test session", "remote session selection");
        var update = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.Notification += (method, value) => { if (method == "session") update.TrySetResult(Json.Read<SessionView>(value)); };
        await remote.Peer.CallAsync("send", new { text = "transport-test" });
        var changed = await update.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(changed.Messages.Last().Text == "transport-test", "command and event traverse A→server→B→server→A (test adapter)");
        await remote.Peer.CallAsync("interrupt", null);
        await remote.Peer.CallAsync("respond", new { id = "test-prompt", decision = "decline" });
        Check(TestAdapter.Interrupts == 1 && TestAdapter.Decisions == 1, "interrupt and approval RPC routing (test adapter)");
        await using (var second = new RemoteDevice(false))
        { await second.ConnectAsync(registry, originalCode); Check(second.IsConnected && remote.IsConnected, "second controller coexists with first"); }

        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.Reconnected += () => restored.TrySetResult();
        await remote.Peer.DisposeAsync(); // Simulated A network loss; no OS network settings touched.
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Check(Json.Read<AgentInfo[]>(await remote.Peer.CallAsync("agents", null)).Length == 1, "A automatically reconnects through relay after transport loss");
        Check(TestAdapter.Sends == 1, "reconnection never replays a mutating request");
        var afterReconnect = Json.Read<SessionView>(await remote.Peer.CallAsync("attach", new { id = "test" }));
        Check(afterReconnect.Id == "test", "session snapshot can be reacquired after reconnect");

        await remote.DisposeAsync();
        // Restart only the server process created by this test. B must recover with a new lease.
        server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); server.Dispose();
        server = StartServer(); await WaitForServer();
        await Eventually(() => host.Lease?.Code is { } code && code != originalCode, 25);
        PairResult? recovered = null;
        for (int i = 0; i < 50 && recovered is null; i++)
        {
            try { recovered = await registry.PostAsync<PairResult>("pair", new PairRequest(host.Lease!.Code)); }
            catch { await Task.Delay(100); }
        }
        Check(recovered is not null, "B re-registers and reconnects automatically after server restart");
        await Reject(async () => await registry.PostAsync<PairResult>("pair", new PairRequest(originalCode)), "server restart invalidates old authorization code");
        await using var newRemote = new RemoteDevice(false);
        await newRemote.ConnectAsync(registry, host.Lease!.Code);
        await host.DisposeAsync();
        await Eventually(() => !newRemote.IsConnected);
        Check(true, "stop sharing closes the controller connection");
        await Reject(async () => await registry.PostAsync<PairResult>("pair", new PairRequest(host.Lease!.Code)), "stop sharing revokes authorization code");
    }
    string identityDir = Path.Combine(Path.GetTempPath(), "FurtherAgent-identity-test-" + Guid.NewGuid().ToString("N"));
    string? previousIdentityDir = Environment.GetEnvironmentVariable("FURTHERAGENT_IDENTITY_DIR");
    string? previousIdentityFile = Environment.GetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE");
    Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_DIR", identityDir);
    Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE", null);
    try
    {
        string path = DeviceIdentity.DefaultPath(registry.ServerIdentity, "Persistent test");
        string secret = DeviceIdentity.LoadOrCreate(path), fixedCode = DeviceIdentity.Code(secret);
        Check(secret == DeviceIdentity.LoadOrCreate(path) && fixedCode.Length == 8, "device identity persists and derives fixed pairing code");
        await using (var first = new DeviceHost(registry, () => new TestAdapter(), persistentIdentity: true))
        {
            await first.StartAsync("Persistent test");
            Check(first.Lease!.Code == fixedCode, "relay registers persistent device code");
            await Reject(() => { DeviceIdentity.Reset(path); return Task.CompletedTask; }, "active identity cannot be reset by second process");
            var wrong = await registry.PostAsync<DeviceLease>("devices", new DeviceRegistration("Persistent test", new string('A', 64)));
            Check(wrong.Code != fixedCode, "another identity cannot claim existing fixed code");
            await registry.PostAsync<JsonNode>("unregister", new LeaseProof(wrong.Code, wrong.Secret));
        }
        await using (var restarted = new DeviceHost(registry, () => new TestAdapter(), persistentIdentity: true))
        {
            await restarted.StartAsync("Persistent test");
            Check(restarted.Lease!.Code == fixedCode, "host restart retains pairing code");
            server.Kill(true); await server.WaitForExitAsync(); server.Dispose();
            server = StartServer(); await WaitForServer();
            PairResult? paired = null;
            for (int i = 0; i < 40 && paired is null; i++)
            {
                try { paired = await registry.PostAsync<PairResult>("pair", new PairRequest(fixedCode)); } catch { await Task.Delay(500); }
            }
            Check(paired is not null && restarted.Lease!.Code == fixedCode, "relay restart recovers same persistent code");
            await using var stableRemote = new RemoteDevice(false);
            await stableRemote.ConnectAsync(registry, fixedCode);
            Check(Json.Read<AgentInfo[]>(await stableRemote.Peer.CallAsync("agents", null)).Length == 1, "persistent code reconnects to actual host after relay restart");
        }
        DeviceIdentity.Reset(path);
        Check(DeviceIdentity.Code(DeviceIdentity.LoadOrCreate(path)) != fixedCode, "explicit offline reset rotates pairing code");
        await Reject(() => registry.PostAsync<DeviceLease>("devices", new DeviceRegistration("bad", "short")), "relay rejects malformed persistent identity");
        File.WriteAllText(path, "corrupt");
        await Reject(() => { _ = DeviceIdentity.LoadOrCreate(path); return Task.CompletedTask; }, "corrupt identity fails without silently changing code");
    }
    finally { Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_DIR", previousIdentityDir); Environment.SetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE", previousIdentityFile); }
    // Run after the restart checks so separate suites do not exhaust the real per-IP pairing limiter.
    await MultiControllerChecks.RunAsync(registry, Check);
    string? thread = null;
    for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--codex-thread") thread = args[i + 1];
    if (thread is not null)
    {
        await using var liveHost = new DeviceHost(registry);
        await liveHost.StartAsync("Live Codex B");
        await using var liveRemote = new RemoteDevice(false);
        await liveRemote.ConnectAsync(registry, liveHost.Lease!.Code);
        var list = Json.Read<SessionInfo[]>(await liveRemote.Peer.CallAsync("sessions", null));
        Check(list.Any(s => s.Id == thread), "real desktop session listed through relay");
        var live = Json.Read<SessionView>(await liveRemote.Peer.CallAsync("attach", new { id = thread }));
        Check(live.Id == thread && live.Messages.Count > 0, "real Codex snapshot traverses desktop→B→relay→A");
        Console.WriteLine($"LIVE: {live.Messages.Count} rendered items; status={live.Status}; no prompts or approvals changed.");
        if (args.Contains("--watch"))
        {
            string baseline = Json.Node(live).ToJsonString();
            var next = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
            liveRemote.Notification += (method, node) =>
            {
                if (method == "session" && node?.ToJsonString() != baseline) next.TrySetResult(Json.Read<SessionView>(node));
            };
            Console.WriteLine("WATCH: waiting for real desktop update (35s)");
            Check((await next.Task.WaitAsync(TimeSpan.FromSeconds(35))).Id == thread, "live desktop patches traverse server relay");
        }
    }
    Console.WriteLine($"ALL {passed} CHECKS PASSED");
}
finally
{
    if (!server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
    server.Dispose();
}

sealed class TestAdapter : IAgentAdapter
{
    public static int Sends, Interrupts, Decisions;
    public AgentInfo Info => new("test", "Test adapter", true, "Transport tests only; never shown in production UI");
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted { add { } remove { } }
    public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("test", "Test session", "2026-10-08")]);
    public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(View("attached"));
    private static SessionView View(string text) => new("test", "Test session", "test", "idle", null, [new("Codex", text)], [new("test-prompt", "test", "Test", "", null)]);
    public Task SendAsync(string text, CancellationToken ct) { Interlocked.Increment(ref Sends); Changed?.Invoke(View(text)); return Task.CompletedTask; }
    public Task InterruptAsync(CancellationToken ct) { Interlocked.Increment(ref Interrupts); return Task.CompletedTask; }
    public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) { Interlocked.Increment(ref Decisions); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
