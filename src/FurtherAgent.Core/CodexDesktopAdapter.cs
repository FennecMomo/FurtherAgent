using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// Experimental desktop IPC profile, observed in Codex Desktop 26.1002.7124.0.
// Never launches another writer or edits Codex history/configuration.
public sealed class CodexDesktopAdapter : IAgentAdapter
{
    private readonly string home;
    private readonly NamedPipeClientStream pipe = new(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private string clientId = "initializing-client";
    private string? sessionId, owner;
    private JsonNode? state;
    private long revision;
    private Task? reader;
    private TaskCompletionSource<SessionView>? snapshot;
    public AgentInfo Info => new("codex", "Codex 桌面", true, "实验性桌面 IPC · 保留原会话与工具 · 需在 B 打开 Codex");
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted;
    public CodexDesktopAdapter(string? codexHome = null) => home = codexHome ??
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct)
    {
        var file = Path.Combine(home, "session_index.jsonl");
        if (!File.Exists(file)) throw new IOException("没有找到 Codex 会话索引。请先在此 Windows 用户下打开 Codex 并创建会话。");
        var entries = new Dictionary<string, SessionInfo>();
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(stream);
        while (await r.ReadLineAsync(ct) is { } line)
        {
            try
            {
                var item = JsonNode.Parse(line);
                var id = item?["id"]?.GetValue<string>();
                if (id is not null) entries[id] = new(id, Text(item!["thread_name"]), Text(item["updated_at"]));
            }
            catch (System.Text.Json.JsonException) { /* Concurrent partial last line. */ }
        }
        return entries.Values.OrderByDescending(x => x.UpdatedAt).Take(300).ToArray();
    }
    private async Task ConnectAsync(CancellationToken ct)
    {
        if (reader is not null)
        {
            if (reader.IsCompleted) throw new IOException("Codex 桌面连接已断开，请重新连接远端设备。");
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { await pipe.ConnectAsync(timeout.Token); }
        catch (Exception ex) { throw new IOException("无法连接 Codex 桌面。请在 B 上以同一 Windows 用户打开 Codex。", ex); }
        reader = ReadLoopAsync();
        var result = await RequestAsync("initialize", new { clientType = "furtheragent" }, 0, ct, targeted: false);
        clientId = result["result"]!["clientId"]!.GetValue<string>();
    }
    public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("会话 ID 无效。");
        await ConnectAsync(ct);
        if (sessionId is not null) await FollowAsync(false, ct);
        lock (gate)
        {
            sessionId = id; owner = null; state = null; revision = 0;
            snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var discovered = await RequestAsync("thread-owner-discovery", new { hostId = "local", conversationId = id }, 1, ct, false);
        owner = discovered["handledByClientId"]?.GetValue<string>() ?? throw new IOException("找不到会话宿主。");
        await FollowAsync(true, ct);
        try { return await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (TimeoutException) { throw new IOException("未收到桌面会话快照。请在 B 的 Codex 中打开该会话，再重新选择。"); }
    }
    private Task FollowAsync(bool following, CancellationToken ct) => WriteAsync(Json.Node(new
    {
        type = "broadcast", method = "thread-stream-following-changed", sourceClientId = clientId, version = 1,
        targetClientIds = owner is null ? null : new[] { owner },
        @params = new { conversationId = sessionId, hostId = "local", following }
    }), ct);
    private async Task<JsonNode> RequestAsync(string method, object args, int version, CancellationToken ct, bool targeted = true)
    {
        string id = Guid.NewGuid().ToString();
        var source = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = source;
        try
        {
            await WriteAsync(Json.Node(new { type = "request", requestId = id, sourceClientId = clientId,
                method, @params = args, version, targetClientId = targeted ? owner : null, timeoutMs = 25000 }), ct);
            var reply = await source.Task.WaitAsync(TimeSpan.FromSeconds(28), ct);
            if (Text(reply["resultType"]) != "success")
                throw new IOException($"Codex 桌面拒绝请求：{Text(reply["error"])}。请确认会话已在 B 桌面打开，且桌面版本兼容。");
            return reply;
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task WriteAsync(JsonNode node, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try { await Frames.WriteAsync(pipe, node, ct); }
        finally { writes.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var m = await Frames.ReadAsync(pipe, lifetime.Token);
                switch (Text(m["type"]))
                {
                    case "response":
                        if (pending.TryGetValue(Text(m["requestId"]), out var source)) source.TrySetResult(m);
                        break;
                    case "client-discovery-request":
                        await WriteAsync(Json.Node(new { type = "client-discovery-response", requestId = Text(m["requestId"]), response = new { canHandle = false } }), lifetime.Token);
                        break;
                    case "broadcast": await BroadcastAsync(m); break;
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var source in pending.Values) source.TrySetException(new IOException("Codex 桌面连接已断开。", ex));
            snapshot?.TrySetException(ex);
            if (!lifetime.IsCancellationRequested) Faulted?.Invoke("Codex 桌面连接已断开：" + ex.Message);
        }
    }
    private async Task BroadcastAsync(JsonNode m)
    {
        var p = m["params"];
        if (Text(p?["conversationId"]) != sessionId || Text(p?["hostId"]) != "local") return;
        if (m["targetClientIds"] is JsonArray targets && !targets.Any(t => Text(t) == clientId)) return;
        if (Text(m["method"]) == "thread-stream-following-status-requested") { await FollowAsync(true, lifetime.Token); return; }
        if (Text(m["method"]) != "thread-stream-state-changed" || Text(m["sourceClientId"]) != owner) return;
        if (m["version"]?.GetValue<int>() != 11) throw new IOException("不兼容的 Codex 桌面流协议版本，需要更新适配器。");
        var change = p!["change"]!;
        bool resync = false;
        SessionView? view = null;
        lock (gate)
        {
            if (Text(change["type"]) == "snapshot") state = change["conversationState"]!.DeepClone();
            else if (Text(change["type"]) == "patches" && state is not null && change["baseRevision"]!.GetValue<long>() == revision)
            {
                try { StatePatches.Apply(state, change["patches"]!.AsArray()); }
                catch { state = null; resync = true; }
            }
            else resync = true;
            if (!resync && state is not null)
            {
                revision = change["revision"]!.GetValue<long>();
                view = ToView(state);
            }
        }
        if (resync) { await FollowAsync(false, lifetime.Token); await FollowAsync(true, lifetime.Token); return; }
        if (view is not null) { snapshot?.TrySetResult(view); Changed?.Invoke(view); }
    }
    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100000) throw new ArgumentException("消息不能为空或超过 100000 字符。");
        SessionView view;
        lock (gate) view = ToView(state ?? throw new IOException("请先选择会话。"));
        var input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } };
        var messageId = Guid.NewGuid().ToString();
        if (view.Status == "active")
            await RequestAsync("thread-follower-steer-turn", new { conversationId = sessionId, input,
                restoreMessage = new { id = messageId, text }, clientUserMessageId = messageId, attachments = Array.Empty<object>() }, 1, ct);
        else
            await RequestAsync("thread-follower-start-turn", new { conversationId = sessionId,
                turnStart = new { request = new { threadId = sessionId, input, clientUserMessageId = messageId, turnTrigger = "composer" },
                    context = new { inheritThreadSettings = true, attachments = Array.Empty<object>(), commentAttachments = Array.Empty<object>() } } }, 2, ct);
    }
    public async Task InterruptAsync(CancellationToken ct)
    {
        SessionView view;
        lock (gate) view = ToView(state ?? throw new IOException("请先选择会话。"));
        if (view.Status != "active" || view.TurnId is null) throw new IOException("没有可停止的活动任务。");
        await RequestAsync("thread-follower-interrupt-turn", new { conversationId = sessionId, mode = "user-stop", expectedTurnId = view.TurnId }, 4, ct);
    }
    public async Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct)
    {
        JsonNode request;
        lock (gate)
            request = state?["requests"]?.AsArray().FirstOrDefault(r => Text(r?["id"]) == requestId)?.DeepClone()
                ?? throw new IOException("该确认已经处理或失效，请刷新状态。");
        string method = Text(request["method"]);
        string follower = method switch
        {
            "item/commandExecution/requestApproval" => "thread-follower-command-approval-decision",
            "item/fileChange/requestApproval" => "thread-follower-file-approval-decision",
            "item/tool/requestUserInput" => "thread-follower-submit-user-input",
            _ => throw new IOException("第一版暂不支持此类确认，请在 B 的 Codex 桌面处理。")
        };
        if (method != "item/tool/requestUserInput" && decision is not ("accept" or "decline" or "cancel")) throw new ArgumentException("无效审批决定。");
        var args = new JsonObject { ["conversationId"] = sessionId, ["requestId"] = request["id"]!.DeepClone() };
        if (method == "item/tool/requestUserInput") args["response"] = response?.DeepClone() ?? throw new ArgumentException("需要回答内容。");
        else args["decision"] = decision;
        await RequestAsync(follower, args, 1, ct);
    }
    public static SessionView ToView(JsonNode s)
    {
        var turns = s["turnHistory"]?["history"]?["entitiesByKey"] is JsonObject entities
            ? entities.Select(p => p.Value!).OrderBy(t => t["turnStartedAtMs"]?.GetValue<double>() ?? 0).ToArray()
            : s["turns"]?.AsArray().Select(t => t!).ToArray() ?? [];
        var messages = new List<ChatEntry>();
        foreach (var t in turns)
        {
            foreach (var item in t["items"]?.AsArray() ?? [])
            {
                string kind = Text(item?["type"]);
                string content = kind switch
                {
                    "userMessage" => string.Join("\n", item?["content"]?.AsArray().Where(c => Text(c?["type"]) == "text").Select(c => Text(c?["text"])) ?? []),
                    "agentMessage" => Text(item?["text"]),
                    "commandExecution" => Text(item?["command"]) + "\n" + Text(item?["aggregatedOutput"]),
                    "mcpToolCall" => Text(item?["server"]) + "/" + Text(item?["tool"]) + " · " + Text(item?["status"]),
                    "fileChange" => "文件修改 · " + Text(item?["status"]) + "\n" + string.Join("\n", item?["changes"]?.AsArray().Select(c => Text(c?["path"])) ?? []),
                    _ => ""
                };
                if (!string.IsNullOrWhiteSpace(content)) messages.Add(new(kind == "userMessage" ? "你" : kind == "agentMessage" ? "Codex" : "工具", content.Length > 20000 ? content[..20000] + "\n[输出截断，请在桌面查看全文]" : content));
            }
        }
        var prompts = (s["requests"]?.AsArray() ?? []).Select(r => new PendingPrompt(Text(r?["id"]), Text(r?["method"]),
            Text(r?["method"]), r?["params"]?.ToJsonString() ?? "", r?["params"]?.DeepClone())).ToList();
        var last = turns.LastOrDefault(t => Text(t["status"]) == "inProgress");
        return new(Text(s["id"]), Text(s["title"]), Text(s["cwd"]), Text(s["threadRuntimeStatus"]?["type"]),
            last is null ? null : Text(last["turnId"]), messages.TakeLast(250).ToList(), prompts);
    }
    internal static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var text) ? text : n?.ToJsonString() ?? "";
    public async ValueTask DisposeAsync()
    {
        if (pipe.IsConnected && sessionId is not null)
            try { using var timeout = new CancellationTokenSource(1000); await FollowAsync(false, timeout.Token); } catch { }
        lifetime.Cancel(); pipe.Dispose();
        if (reader is not null) await reader;
    }
}

public static class StatePatches
{
    public static void Apply(JsonNode root, JsonArray patches)
    {
        foreach (var p in patches)
        {
            var path = p!["path"]!.AsArray();
            if (path.Count == 0) throw new IOException("不支持替换状态根节点。");
            JsonNode parent = root;
            for (int i = 0; i < path.Count - 1; i++) parent = Child(parent, path[i]!) ?? throw new IOException("补丁路径不存在。");
            string op = p["op"]!.GetValue<string>();
            if (op is not ("add" or "replace" or "remove")) throw new IOException("未知补丁操作。");
            var value = p["value"]?.DeepClone();
            if (parent is JsonArray array)
            {
                if (path[^1]!.ToString() == "length")
                {
                    int count = value!.GetValue<int>();
                    if (count < 0 || count > array.Count) throw new IOException("无效数组长度。");
                    while (array.Count > count) array.RemoveAt(array.Count - 1);
                }
                else
                {
                    int index = path[^1]!.GetValue<int>();
                    if (op == "remove") array.RemoveAt(index);
                    else if (op == "add") array.Insert(index, value);
                    else array[index] = value;
                }
            }
            else if (parent is JsonObject obj)
            {
                string key = path[^1]!.GetValue<string>();
                if (op == "remove") obj.Remove(key); else obj[key] = value;
            }
            else throw new IOException("补丁父节点无效。");
        }
    }
    private static JsonNode? Child(JsonNode node, JsonNode key) => node is JsonArray a ? a[key.GetValue<int>()] : node[key.GetValue<string>()];
}
