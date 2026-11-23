using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class CodexSavedSessionChecks
{
    private const string Id = "018924dc-4bac-754f-ad02-33ff2d87f128";

    internal static async Task Run(Action<bool, string> check)
    {
        var history = new Metadata(_ => JsonNode.Parse($$"""
            {
              "thread": {
                "id":"{{Id}}", "name":"Saved Codex thread", "preview":"First message", "cwd":"E:/fixture",
                "status":{"type":"active"},
                "turns":[{"id":"turn-one","status":"inProgress","items":[
                  {"type":"userMessage","content":[{"type":"text","text":"请核对文件"}]},
                  {"type":"commandExecution","command":"demo","aggregatedOutput":"文件输出"},
                  {"type":"agentMessage","id":"answer","text":"请看 [结果](outbox/report.md)",
                    "questions":[{"title":"是否继续？"}]}
                ]}]
              }
            }
            """)!);
        var view = await CodexSavedSession.ReadAsync(history, Id, default);
        check(history.Calls.SequenceEqual([("thread/read", Id, true)]), "stored Codex attach uses only the read-history request");
        check(view.Id == Id && view.Title == "Saved Codex thread" && view.Cwd == "E:/fixture" &&
            view.Messages.Select(message => message.Role).SequenceEqual(["你", "工具", "Codex"]) &&
            view.Messages[1].Text.Contains("文件输出") && view.Messages[2].Text.Contains("outbox/report.md"),
            "stored Codex history preserves chronological messages, output and attachment references");
        check(view.Status == "readOnly" && !view.CanSend && view.CanStop == false && view.TurnId is null && view.Prompts.Count == 0,
            "stored Codex history never claims live actions or pending desktop prompts");

        var empty = new Metadata(includeTurns => includeTurns
            ? throw new IOException($"thread {Id} is not materialized yet; includeTurns is unavailable before first user message")
            : Json.Node(new { thread = new { id = Id, preview = "Empty Codex thread", cwd = "E:/fixture" } }));
        view = await CodexSavedSession.ReadAsync(empty, Id, default);
        check(empty.Calls.SequenceEqual([("thread/read", Id, true), ("thread/read", Id, false)]) &&
            view.Title == "Empty Codex thread" && view.Messages.Count == 0,
            "unmaterialized Codex threads fall back to reading their summary");

        var wrongThread = new Metadata(_ => Json.Node(new { thread = new { id = Guid.NewGuid() } }));
        try
        {
            _ = await CodexSavedSession.ReadAsync(wrongThread, Id, default);
            check(false, "stored Codex attach rejects a response for a different thread");
        }
        catch (IOException)
        {
            check(wrongThread.Calls.Count == 1, "stored Codex attach rejects a response for a different thread");
        }
    }

    private sealed class Metadata(Func<bool, JsonNode> result) : ICodexMetadata
    {
        internal List<(string Method, string Id, bool IncludeTurns)> Calls { get; } = [];

        public Task<JsonNode> CallAsync(string method, object args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var parameters = Json.Node(args);
            bool includeTurns = parameters["includeTurns"]!.GetValue<bool>();
            Calls.Add((method, parameters["threadId"]!.ToString(), includeTurns));
            return Task.FromResult(result(includeTurns));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
