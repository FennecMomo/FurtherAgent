using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FurtherAgent.Core;

internal static class AntigravityChecks
{
    private const string OriginalId = "07e5d133-c88c-4df2-8ffa-40cc4962fd54";
    private const string OlderId = "b84947c4-3cd8-4581-b7ea-6f02e9b6b682";
    private const string CreatedId = "99958bd5-e286-4e03-999a-acf3f782def7";

    internal static async Task Run(Action<bool, string> check)
    {
        string dataRoot = Directory.CreateTempSubdirectory("fa-antigravity-").FullName;
        var logDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "brain", OriginalId, ".system_generated", "logs"));
        string transcript = Path.Combine(logDirectory.FullName, "transcript.jsonl");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        try
        {
            await File.WriteAllLinesAsync(transcript, new[]
            {
                Line("USER_INPUT", "<USER_REQUEST>\n先前的问题\n</USER_REQUEST>unrelated runtime data"),
                Json.Node(new
                {
                    type = "PLANNER_RESPONSE", content = "先前的回复",
                    tool_calls = new[] { new { name = "read_file", arguments = "opaque payload" } }
                }).ToJsonString(),
                "{\"type\":\"PLANNER_RESPONSE\",\"content\":"
            }, ct);

            var desktop = new DesktopFixture();
            var seen = Channel.CreateUnbounded<SessionView>();
            var sent = new List<(string Id, string Text)>();
            await using var adapter = new AntigravityDesktopAdapter("127.0.0.1:29391", "fake-sidecar-token", dataRoot,
                "fixture-project", (id, text, _) => { sent.Add((id, text)); return Task.CompletedTask; }, desktop);
            adapter.Changed += view => seen.Writer.TryWrite(view);

            var probe = await adapter.ProbeAsync(ct);
            check(probe?["backend"]?.ToString() == "antigravity", "Antigravity probes the product service on the sidecar loopback contract");
            var listed = await adapter.ListAsync(ct);
            check(desktop.Cursors.SequenceEqual(new[] { "", "desktop cursor-2" }) &&
                listed.Select(item => item.Id).SequenceEqual(new[] { OriginalId, OlderId }),
                "Antigravity honors native pagination and retains the authoritative desktop IDs");

            var created = await adapter.CreateAsync("controller-form-id", "原生新会话", ct);
            check(created.Id == CreatedId && desktop.CreateBody?["title"]?.ToString() == "原生新会话" &&
                desktop.CreateBody?["projectId"]?.ToString() == "fixture-project" && desktop.CreateBody?["id"] is null,
                "Antigravity does not reuse controller draft IDs when creating a desktop conversation");
            await adapter.ArchiveAsync(OriginalId, true, ct);
            check(desktop.UpdateBody?["conversationId"]?.ToString() == OriginalId &&
                desktop.UpdateBody?["annotations"]?["archived"]?.GetValue<bool>() == true,
                "Antigravity archives the original desktop conversation by ID");

            var attached = await adapter.AttachAsync(OriginalId, ct);
            var conversation = attached.Messages.Where(HistoryPaging.IsConversation).Select(message => message.Text).ToArray();
            check(attached.Status == "idle" && attached.CanSend && !attached.Prompts.Any() &&
                conversation.SequenceEqual(new[] { "先前的问题", "先前的回复" }),
                "Antigravity restores chat from complete native transcript records");
            check(attached.Messages.Single(message => message.Role == "tool").Text.Contains("read_file", StringComparison.Ordinal) &&
                attached.Messages.All(message => !message.Text.Contains("opaque payload", StringComparison.Ordinal)) &&
                !adapter.Info.CanFiles && !adapter.Info.CanConfigure && attached.CanStop == false && attached.CanSteer == false,
                "Antigravity exposes readable tool names and accurately reports supported controls");

            adapter.SetObserved(true);
            const string text = "--title=原样\n检查引号 \" & 保留空行\n\n🙂";
            await adapter.SendAsync(text, ct);
            var pending = await WaitForStateAsync(seen.Reader, "antigravityPending", ct);
            check(sent.SequenceEqual(new[] { (OriginalId, text) }) && !pending.CanSend,
                "Antigravity submits literal input once to the selected original conversation and waits for acknowledgement");

            desktop.RunState = "RUN_STATE_RUNNING";
            var running = await WaitForStateAsync(seen.Reader, "running", ct);
            check(!running.CanSend && running.TurnId is { Length: > 0 },
                "Antigravity polling detects the native active turn and keeps concurrent send disabled");

            await File.AppendAllLinesAsync(transcript, new[] { Line("USER_INPUT", text), Line("PLANNER_RESPONSE", "新回复已保存") }, ct);
            desktop.RunState = "RUN_STATE_IDLE";
            var idle = await WaitForStateAsync(seen.Reader, "idle", ct);
            check(idle.CanSend && idle.Messages[^1].Text == "新回复已保存" && idle.TurnId is null,
                "Antigravity polling publishes the finished transcript and restores input in the same original conversation");
        }
        finally
        {
            File.Delete(transcript);
            for (DirectoryInfo? path = logDirectory; path is not null && path.FullName != dataRoot; path = path.Parent) path.Delete();
            Directory.Delete(dataRoot);
        }
    }

    private static string Line(string type, string content) => Json.Node(new { type, content }).ToJsonString();

    private static async Task<SessionView> WaitForStateAsync(ChannelReader<SessionView> source, string status, CancellationToken ct)
    {
        while (true)
        {
            var view = await source.ReadAsync(ct);
            if (view.Status == status) return view;
        }
    }

    private sealed class DesktopFixture : HttpMessageHandler
    {
        internal List<string> Cursors { get; } = [];
        internal JsonObject? CreateBody { get; private set; }
        internal JsonObject? UpdateBody { get; private set; }
        internal volatile string RunState = "RUN_STATE_IDLE";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string service = "/product.v1.ConversationService/";
            if (request.RequestUri is not { IsLoopback: true, Port: 29391 } uri || request.Method != HttpMethod.Post ||
                !uri.AbsolutePath.StartsWith(service, StringComparison.Ordinal) ||
                request.Headers.GetValues("Connect-Protocol-Version").Single() != "1" ||
                request.Headers.GetValues("x-antigravity-csrf-token").Single() != "fake-sidecar-token")
                throw new InvalidOperationException("Wrong Antigravity sidecar endpoint or header.");
            var args = await request.Content!.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Missing Antigravity request body.");

            object body = uri.AbsolutePath[service.Length..] switch
            {
                "ListConversations" => Page(args),
                "GetConversation" when args["conversationId"]?.ToString() == OriginalId => new
                {
                    conversation = Conversation(OriginalId, "桌面原会话", "2026-11-26T10:30:00Z", RunState)
                },
                "CreateConversation" => Create(args),
                "UpdateConversation" => Update(args),
                _ => throw new InvalidOperationException("Unexpected Antigravity product call.")
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }

        private object Page(JsonObject args)
        {
            var first = Conversation(OriginalId, "桌面原会话", "2026-11-26T10:30:00Z");
            if (args["pageSize"]?.GetValue<int>() == 1) return new { conversations = new[] { first } };
            if (args["pageSize"]?.GetValue<int>() != 100) throw new InvalidOperationException("Unexpected Antigravity page size.");
            string cursor = args["pageToken"]?.ToString() ?? "";
            Cursors.Add(cursor);
            return cursor switch
            {
                "" => new { conversations = new[] { first }, nextPageToken = "desktop cursor-2" },
                "desktop cursor-2" => new { conversations = new[] { first, Conversation(OlderId, "另一桌面会话", "2026-11-25T09:00:00Z") } },
                _ => throw new InvalidOperationException("Unknown Antigravity cursor.")
            };
        }

        private object Create(JsonObject args)
        {
            CreateBody = args;
            return new { conversation = Conversation(CreatedId, "原生新会话", "2026-11-26T10:35:00Z") };
        }

        private object Update(JsonObject args)
        {
            UpdateBody = args;
            return new { };
        }

        private static object Conversation(string id, string title, string time, string runState = "RUN_STATE_IDLE") => new
        {
            id, annotations = new { title }, state = new { updateTime = time, runState }
        };
    }
}
