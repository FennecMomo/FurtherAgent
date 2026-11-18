using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class ConnectionOpenChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        await ExpectFailureAsync(HttpStatusCode.Conflict, "B 暂时离线，正在等待重连。", RelayBehavior.AbortBeforeHello, true,
            "a saved device whose host is temporarily offline can be retried", check);
        await ExpectFailureAsync(HttpStatusCode.Conflict, "控制端数量达到上限；旧版接入端需升级后才能多端连接。", RelayBehavior.AbortBeforeHello, false,
            "a controller-capacity rejection is not retried", check);
        await ExpectFailureAsync(HttpStatusCode.NotFound, "授权码不存在或已过期。", RelayBehavior.AbortBeforeHello, false,
            "a rejected pairing code is not retried", check);
        await ExpectFailureAsync(HttpStatusCode.OK, null, RelayBehavior.AbortBeforeHello, true,
            "an abrupt close before the relay hello is a retryable connection failure", check);
        await ExpectFailureAsync(HttpStatusCode.OK, null, RelayBehavior.AbortDuringLanInfo, true,
            "a relay lost during optional LAN discovery is not returned as connected", check);

        await WithRelayAsync(HttpStatusCode.OK, null, RelayBehavior.LegacyWithoutLanInfo, async (registry, ct) =>
        {
            await using var remote = new RemoteDevice(autoReconnect: false);
            await remote.ConnectAsync(registry, "fixture-only", ct);
            var result = await remote.Peer.CallAsync("agents", null, ct);
            check(remote.IsConnected && result is JsonArray,
                "a live legacy relay can still connect when it rejects optional LAN discovery");
        });
    }

    private enum RelayBehavior { AbortBeforeHello, AbortDuringLanInfo, LegacyWithoutLanInfo }

    private static Task ExpectFailureAsync(HttpStatusCode pairStatus, string? reason, RelayBehavior relay, bool canRetry,
        string name, Action<bool, string> check) => WithRelayAsync(pairStatus, reason, relay, async (registry, ct) =>
    {
        await using var remote = new RemoteDevice(autoReconnect: false);
        try
        {
            await remote.ConnectAsync(registry, "fixture-only", ct);
            check(false, name);
        }
        catch (ConnectionOpenException ex)
        {
            check(ex.CanRetry == canRetry && !remote.IsConnected && remote.Peer is null, name);
        }
    });

    private static async Task WithRelayAsync(HttpStatusCode pairStatus, string? reason, RelayBehavior relay,
        Func<RegistryClient, CancellationToken, Task> use)
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        string address = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(address);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var registry = new RegistryClient(address);
        var serving = ServeAsync(listener, pairStatus, reason, relay, deadline.Token);
        try { await use(registry, deadline.Token); }
        finally
        {
            deadline.Cancel();
            try { await serving; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    private static async Task ServeAsync(HttpListener listener, HttpStatusCode pairStatus, string? reason,
        RelayBehavior relay, CancellationToken ct)
    {
        var pair = await listener.GetContextAsync().WaitAsync(ct);
        RequirePath(pair, "/pair");
        using (var response = pair.Response)
        {
            response.StatusCode = (int)pairStatus;
            response.ContentType = "application/json";
            var body = pairStatus == HttpStatusCode.OK ? Json.Node(new PairResult("fixture", "fixture-ticket")) : JsonValue.Create(reason)!;
            await System.Text.Json.JsonSerializer.SerializeAsync(response.OutputStream, body, Json.Options, ct);
        }
        if (pairStatus != HttpStatusCode.OK) return;

        var control = await listener.GetContextAsync().WaitAsync(ct);
        RequirePath(control, "/control");
        await using var socket = new WebSocketTransport((await control.AcceptWebSocketAsync(null).WaitAsync(ct)).WebSocket);
        var ticket = await socket.ReadLimitedAsync(4096, ct);
        if (ticket["ticket"]?.ToString() != "fixture-ticket") throw new IOException("Wrong test pairing ticket.");
        if (relay == RelayBehavior.AbortBeforeHello) { socket.Abort(); return; }

        await socket.WriteAsync(Json.Node(new { ok = true, protocol = 2 }), ct);
        var sync = await ReadMethodAsync(socket, "session-sync", ct);
        await socket.WriteAsync(new JsonObject
        {
            ["id"] = sync["id"]!.DeepClone(),
            ["result"] = Json.Node(new { transportPing = true, sessionChannels = 1 })
        }, ct);
        var lan = await ReadMethodAsync(socket, "lan-info", ct);
        if (relay == RelayBehavior.AbortDuringLanInfo) { socket.Abort(); return; }
        await socket.WriteAsync(new JsonObject { ["id"] = lan["id"]!.DeepClone(), ["error"] = "未知方法" }, ct);
        var agents = await ReadMethodAsync(socket, "agents", ct);
        await socket.WriteAsync(new JsonObject { ["id"] = agents["id"]!.DeepClone(), ["result"] = new JsonArray() }, ct);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static void RequirePath(HttpListenerContext context, string path)
    {
        if (context.Request.Url?.AbsolutePath != path) throw new IOException($"Expected test request to {path}.");
    }

    private static async Task<JsonNode> ReadMethodAsync(WebSocketTransport socket, string method, CancellationToken ct)
    {
        var request = await socket.ReadAsync(ct);
        if (request["method"]?.ToString() != method || request["id"] is null) throw new IOException($"Expected test RPC {method}.");
        return request;
    }
}
