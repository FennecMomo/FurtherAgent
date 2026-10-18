using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

// Offline adapter contracts. These requests never reach a DSH process or create a native session.
internal static class DshAdapterChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        string[] capabilities = ["create", "model", "plan", "watch", "files", "workspaces", "respond", "reasoning", "permissions"];
        var prompt = new PendingPrompt("question-1", "item/tool/requestUserInput", "Question", "Choose", Json.Node(new { questions = new[] { new { id = "q", question = "Choose?" } } }));
        string text = "initial";
        long revision = 10;
        bool failWatch = false, failSend = true, failInterruptSend = true;
        string[] acceptedRequestIds = [];
        int watchCalls = 0;
        JsonNode Snapshot(string id) => Json.Node(new
        {
            id, title = "Test", cwd = "C:/workspace", status = "idle", turnId = "turn-1",
            messages = new[] { new ChatEntry("assistant", text) }, prompts = new[] { prompt },
            model = "model-1", provider = "provider-1", reasoningEffort = "high", mode = "default", canChangeMode = true,
            permission = "ask", canChangePermissions = true,
            cursor = 9, streamRevision = revision, acceptedRequestIds,
        });
        await using var bridge = await FakeBridge.Start(async request =>
        {
            string method = request["method"]!.ToString();
            var args = request["args"];
            if (method == "watch")
            {
                Interlocked.Increment(ref watchCalls);
                await Task.Delay(35, ct);
                if (failWatch) throw new IOException("temporary bridge failure");
                return Snapshot(args!["id"]!.ToString());
            }
            return method switch
            {
                "info" => Json.Node(new { protocol = 1, mode = "desktop", hostVersion = "test-only", capabilities }),
                "snapshot" => Snapshot(args!["id"]!.ToString()),
                "list" => Json.Node(new { items = new object[]
                {
                    new { sessionId = "old", title = "Old", cwd = "C:/old", updatedAt = 1000L },
                    new { sessionId = "child", title = "Child", origin = "subagent", updatedAt = 4000L },
                    new { sessionId = "child-object", title = "Child", origin = new { kind = "subagent" }, updatedAt = 4000L },
                    new { sessionId = "new", projections = new { values = new { title = new { title = "Newest" } } }, header = new { cwd = "C:/new" }, updatedAt = 3000L },
                } }),
                "create" => Json.Node(new { sessionId = args!["id"]!.ToString(), title = args["title"]!.ToString(), cwd = args["cwd"]!.ToString() }),
                "workspaces" => Json.Node(new[] { new WorkspaceInfo("workspace-1", "Project", "C:/workspace") }),
                "browseDirectories" => Json.Node(new WorkspaceDirectories("C:/workspace", "C:/", [new("Child", "C:/workspace/Child")])),
                "addWorkspace" => Json.Node(new WorkspaceInfo(args!["id"]!.ToString(), "Added", args["path"]!.ToString())),
                "settings" => Json.Node(new
                {
                    catalog = new { groups = new[] { new { id = "provider-1", models = new[] { new { id = "model-1", label = "Native model", reasoningEfforts = new[] { "low", "high" }, defaultReasoningEffort = "high" } } } } },
                    permissionOptions = new[] { new PermissionOption("ask", "Ask"), new PermissionOption("allow", "Allow") },
                    view = Snapshot("session-1"),
                }),
                "send" when failSend => throw new IOException("acknowledgement unavailable"),
                "interruptSend" when failInterruptSend => throw new IOException("interrupt acknowledgement unavailable"),
                "configure" or "configurePermissions" or "respond" or "fileWorkspace" or "send" or "interruptSend" or "observe" => Json.Node(new { accepted = true }),
                _ => throw new IOException("Unexpected fake method: " + method),
            };
        });
        await using var adapter = new DshAdapter(bridge.Descriptor);
        await adapter.ProbeAsync(ct);
        check(adapter.Info.CanFiles && adapter.Info.CanWorkspaces && !adapter.Info.CanThumbnails, "DSH advertises only negotiated file/workspace/thumbnail capabilities");
        var sessions = await adapter.ListAsync(ct);
        check(sessions.Select(x => x.Id).SequenceEqual(["new", "old"]), "DSH roster hides both native subagent origin shapes and sorts by recent activity");
        check(sessions[0].Cwd == "C:/new" && sessions[0].Title == "Newest", "DSH roster preserves native workspace and title projections");
        var created = await adapter.CreateAsync("created", "Created", "C:/workspace", ct);
        check(created.Cwd == "C:/workspace" && created.Id == "created", "DSH create forwards and retains selected workspace");
        check((await adapter.WorkspacesAsync(ct)).Single().Id == "workspace-1", "DSH uses native workspace roster DTO");
        check((await adapter.BrowseDirectoriesAsync("C:/workspace", ct)).Directories.Single().Name == "Child", "DSH browses directories through scoped bridge method");
        check((await adapter.AddWorkspaceAsync("added", "C:/added", "", ct)).Path == "C:/added", "DSH adds workspace through scoped bridge method");
        await adapter.AttachAsync("session-1", ct);
        var settings = await adapter.SettingsAsync(ct);
        check(settings.CanChangeReasoning && settings.ReasoningEffort == "high" && settings.Models.Single().ReasoningEfforts!.SequenceEqual(["low", "high"]), "DSH native reasoning catalogue reaches shared settings");
        check(settings.RequireModelSelection && settings.CanChangePermissions && settings.PermissionOptions?.Select(p => p.Value).SequenceEqual(["ask", "allow"]) == true,
            "DSH requires catalogue model selection and exposes native permission choices");
        await adapter.ConfigurePermissionsAsync("ask", ct);
        check(bridge.Requests.Last(x => x["method"]!.ToString() == "configurePermissions")["args"]?["permission"]?.ToString() == "ask", "DSH permission change targets native session command");
        adapter.SetObserved(true);
        await adapter.ConfigureAsync("model-1", "provider-1", "plan", "low", ct);
        check(bridge.Requests.Last(x => x["method"]!.ToString() == "configure")["args"]?["reasoningEffort"]?.ToString() == "low", "DSH reasoning change is forwarded without synthetic model prompt");
        await adapter.SetFileWorkspaceAsync("C:/outbox", ct);
        var fileArgs = bridge.Requests.Last(x => x["method"]!.ToString() == "fileWorkspace")["args"];
        check(fileArgs?["id"]?.ToString() == "session-1" && fileArgs["outbox"]?.ToString() == "C:/outbox", "DSH file workspace is bound to current native session");
        await adapter.RespondAsync("question-1", "", Json.Node(new { answers = new { q = "Yes" } }), ct);
        var responseArgs = bridge.Requests.Last(x => x["method"]!.ToString() == "respond")["args"];
        check(responseArgs?["id"]?.ToString() == "session-1" && responseArgs["requestId"]?.ToString() == "question-1", "DSH question response carries both session and native prompt identity");
        bool duplicate = false, unknown = false;
        try { await adapter.RespondAsync("question-1", "", null, ct); } catch (IOException) { duplicate = true; }
        try { await adapter.RespondAsync("not-pending", "accept", null, ct); } catch (IOException) { unknown = true; }
        check(duplicate && unknown && bridge.Requests.Count(x => x["method"]!.ToString() == "respond") == 1, "DSH rejects duplicate and stale responses before RPC");
        bool uncertain = false, alternateRejected = false;
        try { await adapter.SendAsync("same message", "queue", ct); } catch (IOException) { uncertain = true; }
        try { await adapter.SendAsync("different message", "queue", ct); } catch (IOException) { alternateRejected = true; }
        failSend = false;
        await adapter.SendAsync("same message", "steer", ct);
        var sends = bridge.Requests.Where(x => x["method"]!.ToString() == "send").Select(x => x["args"]!).ToArray();
        check(uncertain && alternateRejected && sends.Length == 2 && sends[0]["requestId"]!.ToString() == sends[1]["requestId"]!.ToString() && sends[1]["mode"]!.ToString() == "queue", "DSH uncertain submission retains original request identity and delivery mode");
        try { await adapter.InterruptAndSendAsync("turn-1", "replacement", ct); } catch (IOException) { }
        failInterruptSend = false;
        await adapter.InterruptAndSendAsync("turn-2", "replacement", ct);
        var replacements = bridge.Requests.Where(x => x["method"]!.ToString() == "interruptSend").Select(x => x["args"]!).ToArray();
        check(replacements.Length == 2 && replacements[0]["requestId"]!.ToString() == replacements[1]["requestId"]!.ToString()
            && replacements[1]["turnId"]!.ToString() == "turn-1", "DSH uncertain interrupt replacement cannot target a newer turn on retry");
        failSend = true;
        try { await adapter.SendAsync("receipt-confirmed", "queue", ct); } catch (IOException) { }
        acceptedRequestIds = [bridge.Requests.Last(x => x["method"]!.ToString() == "send")["args"]!["requestId"]!.ToString()];
        await adapter.AttachAsync("session-1", ct);
        failSend = false;
        int beforeReceiptRetry = bridge.Requests.Count(x => x["method"]!.ToString() == "send");
        await adapter.SendAsync("receipt-confirmed", "steer", ct);
        check(bridge.Requests.Count(x => x["method"]!.ToString() == "send") == beforeReceiptRetry, "DSH history-confirmed unknown receipt consumes same-text retry without duplicate native submission");
        failSend = true;
        try { await adapter.SendAsync("another confirmed message", "queue", ct); } catch (IOException) { }
        acceptedRequestIds = [bridge.Requests.Last(x => x["method"]!.ToString() == "send")["args"]!["requestId"]!.ToString()];
        failSend = false;
        await adapter.SendAsync("a genuinely different message", "queue", ct);
        check(bridge.Requests.Last(x => x["method"]!.ToString() == "send")["args"]!["text"]!.ToString() == "a genuinely different message", "DSH confirmed receipt permits a different message without locking the session");
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Faulted += _ => fault.TrySetResult();
        adapter.Changed += view => { if (view.Messages.LastOrDefault()?.Text == "restored") restored.TrySetResult(); };
        int before = watchCalls;
        failWatch = true;
        await fault.Task.WaitAsync(ct);
        revision = 0; text = "restored"; failWatch = false;
        await restored.Task.WaitAsync(ct);
        int submittedBeforeOutage = bridge.Requests.Count(x => x["method"]!.ToString() == "send");
        check(watchCalls > before && submittedBeforeOutage == beforeReceiptRetry + 2, "DSH transient failure restores same-session read subscription after stream revision reset without resending");
        check(bridge.Requests.Any(x => x["method"]!.ToString() == "observe" && x["args"]?["observed"]?.GetValue<bool>() == true)
            && bridge.Requests.Any(x => x["method"]!.ToString() == "watch" && x["args"]?["observed"]?.GetValue<bool>() == true), "DSH approval observation follows actual attached viewers and renews through watch");
        string observer = bridge.Requests.Last(x => x["method"]!.ToString() == "observe")["args"]!["observerId"]!.ToString();
        await adapter.AttachAsync("session-2", ct);
        using (var releaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            releaseDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            while (!bridge.Requests.Any(x => x["method"]!.ToString() == "observe" && x["args"]?["id"]?.ToString() == "session-2" && x["args"]?["observed"]?.GetValue<bool>() == true))
                await Task.Delay(20, releaseDeadline.Token);
        }
        check(bridge.Requests.Any(x => x["method"]!.ToString() == "observe" && x["args"]?["id"]?.ToString() == "session-1"
            && x["args"]?["observed"]?.GetValue<bool>() == false && x["args"]?["observerId"]?.ToString() == observer), "DSH selection switch releases prior session lease only for this observer");
        capabilities = [];
        await adapter.ProbeAsync(ct);
        check(!adapter.Info.CanFiles && !adapter.Info.CanWorkspaces && !(await adapter.SettingsAsync(ct)).CanChangeReasoning, "DSH old plugins remain capability gated");
    }

    private sealed class FakeBridge : IAsyncDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private readonly string directory;
        public string Descriptor { get; }
        public ConcurrentQueue<JsonNode> Requests { get; } = new();
        private FakeBridge(HttpListener listener, string directory, string descriptor, Func<JsonNode, Task<JsonNode?>> handler)
        {
            this.listener = listener; this.directory = directory; Descriptor = descriptor;
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    HttpListenerContext? context = null;
                    try
                    {
                        context = await listener.GetContextAsync().WaitAsync(stop.Token);
                        using var reader = new StreamReader(context.Request.InputStream);
                        var request = JsonNode.Parse(await reader.ReadToEndAsync(stop.Token))!;
                        Requests.Enqueue(request);
                        if (context.Request.Headers["Authorization"] != "Bearer test-only") throw new IOException("Unexpected authentication");
                        var result = await handler(request);
                        byte[] bytes = Encoding.UTF8.GetBytes(new JsonObject { ["result"] = result }.ToJsonString());
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes, stop.Token);
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested && context is not null)
                    {
                        try
                        {
                            context.Response.StatusCode = 400;
                            byte[] bytes = Encoding.UTF8.GetBytes(Json.Node(new { error = new { code = "test/transient", message = ex.Message } }).ToJsonString());
                            await context.Response.OutputStream.WriteAsync(bytes, stop.Token);
                        }
                        catch (Exception) { /* The cancelled watch may already have closed its socket. */ }
                    }
                    catch (Exception) when (stop.IsCancellationRequested) { break; }
                    finally { try { context?.Response.Close(); } catch (Exception) { } }
                }
            });
        }
        public static async Task<FakeBridge> Start(Func<JsonNode, Task<JsonNode?>> handler)
        {
            var portFinder = new TcpListener(IPAddress.Loopback, 0); portFinder.Start();
            int port = ((IPEndPoint)portFinder.LocalEndpoint).Port; portFinder.Stop();
            string endpoint = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener(); listener.Prefixes.Add(endpoint); listener.Start();
            string directory = Path.Combine(Path.GetTempPath(), "fa-dsh-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string descriptor = Path.Combine(directory, "desktop-test.json");
            await File.WriteAllTextAsync(descriptor, Json.Node(new { protocol = 1, pid = Environment.ProcessId, instanceId = "test-only", endpoint, token = "test-only" }).ToJsonString());
            return new(listener, directory, descriptor, handler);
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Close(); await loop;
            File.Delete(Descriptor); Directory.Delete(directory); stop.Dispose();
        }
    }
}
