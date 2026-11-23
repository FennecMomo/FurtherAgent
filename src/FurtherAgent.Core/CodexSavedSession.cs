using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

internal static class CodexSavedSession
{
    internal static async Task<SessionView> ReadAsync(ICodexMetadata server, string id, CancellationToken ct)
        => ToView(await ReadThreadAsync(server, id, ct)) with { Id = id };

    internal static async Task<JsonObject> ReadThreadAsync(ICodexMetadata server, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var expected)) throw new ArgumentException("会话 ID 无效。", nameof(id));
        string threadId = expected.ToString("D");
        JsonNode result;
        try
        {
            result = await server.CallAsync("thread/read", new { threadId, includeTurns = true }, ct);
        }
        catch (IOException ex) when (ex.Message.Contains("is not materialized yet", StringComparison.Ordinal) &&
            ex.Message.Contains("includeTurns is unavailable before first user message", StringComparison.Ordinal))
        {
            // The runtime may know a newly created thread before its first saved turn.
            result = await server.CallAsync("thread/read", new { threadId, includeTurns = false }, ct);
        }

        if (result["thread"] is not JsonObject thread ||
            !Guid.TryParse(CodexDesktopAdapter.Text(thread["id"]), out var actual) || actual != expected)
            throw new IOException("Codex 返回的会话与所选会话不一致。");

        return thread;
    }

    internal static SessionView ToView(JsonNode thread)
    {
        string title = CodexDesktopAdapter.Text(thread["name"]);
        if (string.IsNullOrWhiteSpace(title)) title = CodexDesktopAdapter.Text(thread["preview"]);
        if (string.IsNullOrWhiteSpace(title)) title = "Codex 会话";

        // The same Codex item types are used by history reads and desktop snapshots.
        var view = CodexDesktopAdapter.ToView(thread);
        return view with
        {
            Title = title,
            Status = "readOnly",
            TurnId = null,
            Prompts = [],
            StatusDetail = "已读取 Codex 保存的会话历史。此会话可在 FurtherAgent 查看，操作请在 Codex 桌面完成。",
            CanSend = false,
            CanStop = false
        };
    }
}
