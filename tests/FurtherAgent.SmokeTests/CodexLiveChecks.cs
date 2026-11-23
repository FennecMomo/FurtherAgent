using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexLiveChecks
{
    private const string Id = "018924dc-4bac-754f-ad02-33ff2d87f128";
    private const string Turn = "fixture-turn";

    public static async Task Run(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var portPicker = new TcpListener(IPAddress.Loopback, 0);
        portPicker.Start();
        int port = ((IPEndPoint)portPicker.LocalEndpoint).Port;
        portPicker.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        await using var controller = new CodexLiveSession(Id);
        var streamed = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderReady = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<SessionView>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<SessionCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += view =>
        {
            if (view.Messages.Any(message => message.Text == "第一段第二段")) streamed.TrySetResult(view);
            if (view.Messages.Any(message => message.Text == "旧回复")) olderReady.TrySetResult(view);
            if (view.Prompts.Count > 0) approval.TrySetResult(view);
        };
        controller.Completed += item => finished.TrySetResult(item);
        var attach = controller.AttachAsync($"ws://127.0.0.1:{port}/", ct);
        var web = await (await server.GetContextAsync().WaitAsync(ct)).AcceptWebSocketAsync(null);
        using var socket = web.WebSocket;
        async Task<JsonNode> ReadAsync()
        {
            using var message = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (true)
            {
                var part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Fixture socket closed.");
                message.Write(buffer, 0, part.Count);
                if (!part.EndOfMessage) continue;
                message.Position = 0;
                return JsonNode.Parse(message)!;
            }
        }
        Task WriteAsync(object value) => socket.SendAsync(Encoding.UTF8.GetBytes(Json.Node(value).ToJsonString(Json.Options)).AsMemory(),
            WebSocketMessageType.Text, true, ct).AsTask();
        Task ReplyAsync(JsonNode request, object result) => WriteAsync(new { id = request["id"], result });
        Task NotifyAsync(string method, object parameters) => WriteAsync(new { method, @params = parameters });

        var init = await ReadAsync();
        check(init["method"]?.ToString() == "initialize", "shared Codex endpoint uses native initialization");
        await ReplyAsync(init, new { });
        var initialized = await ReadAsync();
        var resume = await ReadAsync();
        check(initialized["method"]?.ToString() == "initialized" && resume["method"]?.ToString() == "thread/resume" &&
            resume["params"]?["threadId"]?.ToString() == Id, "shared endpoint subscribes to the exact saved Codex thread");
        await ReplyAsync(resume, new { thread = new { id = Id, name = "已有会话", status = new { type = "idle" }, turns = Array.Empty<object>() },
            initialTurnsPage = new { data = Array.Empty<object>(), nextCursor = "older" }, model = "fixture", modelProvider = "openai" });
        var ready = await attach;
        check(ready.Id == Id && ready.CanSend && ready.CanSteer == true && ready.CanStop == false, "shared thread reports exact live capabilities");
        var older = await ReadAsync();
        check(ready.Messages.Count == 0 && older["method"]?.ToString() == "thread/turns/list" &&
            older["params"]?["cursor"]?.ToString() == "older", "shared attach returns the first page before hydrating older turns");
        await ReplyAsync(older, new { data = new[] { new { id = "old-turn", status = "completed",
            items = new[] { new { id = "old-reply", type = "agentMessage", text = "旧回复" } } } } });
        await olderReady.Task.WaitAsync(ct);
        check(!finished.Task.IsCompleted, "hydrating older completed turns does not emit a completion notification");

        var submit = controller.SendAsync("开始这一轮", ct);
        var start = await ReadAsync();
        check(start["method"]?.ToString() == "turn/start" && start["params"]?["threadId"]?.ToString() == Id, "idle Codex input starts a turn on the existing server");
        object turn = new { id = Turn, status = "inProgress", items = Array.Empty<object>() };
        await ReplyAsync(start, new { turn });
        await NotifyAsync("turn/started", new { threadId = Id, turn });
        await NotifyAsync("item/agentMessage/delta", new { threadId = Id, turnId = Turn, itemId = "answer", delta = "第一段" });
        await NotifyAsync("item/agentMessage/delta", new { threadId = Id, turnId = Turn, itemId = "answer", delta = "第二段" });
        await submit;
        var active = await streamed.Task.WaitAsync(ct);
        check(active.TurnId == Turn && active.CanStop == true && active.Messages.Last().Text == "第一段第二段",
            "native deltas update the live reply and retain the active turn identity");

        var steer = controller.SendAsync("补充指令", ct);
        var follow = await ReadAsync();
        check(follow["method"]?.ToString() == "turn/steer" && follow["params"]?["expectedTurnId"]?.ToString() == Turn,
            "active Codex input steers the exact current turn");
        await ReplyAsync(follow, new { turnId = Turn });
        await steer;

        await WriteAsync(new
        {
            id = 804, method = CodexPermissionRequests.Kind,
            @params = new { threadId = Id, turnId = Turn, reason = "fixture",
                permissions = new { network = new { enabled = true } } }
        });
        var prompted = await approval.Task.WaitAsync(ct);
        var answer = controller.RespondAsync(prompted.Prompts.Single().Id, "acceptForSession", null, ct);
        var response = await ReadAsync();
        check(response["id"]?.ToString() == "804" && response["result"]?["scope"]?.ToString() == "session" &&
            response["result"]?["permissions"]?["network"]?["enabled"]?.GetValue<bool>() == true,
            "live native permission reply retains the request ID, requested scope and explicit session grant");
        await answer;
        await NotifyAsync("serverRequest/resolved", new { threadId = Id, requestId = 804 });

        var stop = controller.InterruptAsync(ct);
        var interrupt = await ReadAsync();
        check(interrupt["method"]?.ToString() == "turn/interrupt" && interrupt["params"]?["turnId"]?.ToString() == Turn,
            "stop targets the native active turn");
        await ReplyAsync(interrupt, new { });
        await stop;
        await NotifyAsync("turn/completed", new { threadId = Id, turn = new { id = Turn, status = "completed", items = Array.Empty<object>() } });
        var completion = await finished.Task.WaitAsync(ct);
        check(completion.SessionId == Id && completion.TurnId == Turn, "completion notification uses the authoritative live turn event");

        var configure = controller.ConfigurePermissionsAsync(":workspace", ct);
        var settings = await ReadAsync();
        check(settings["method"]?.ToString() == "thread/settings/update" &&
            settings["params"]?["threadId"]?.ToString() == Id &&
            settings["params"]?["permissions"]?.ToString() == ":workspace" &&
            settings["params"]?["approvalPolicy"]?.ToString() == "on-request",
            "permission settings change is scoped to the original idle thread");
        await ReplyAsync(settings, new { });
        await configure;
    }
}
