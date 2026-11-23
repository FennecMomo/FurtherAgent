using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// Native turns are ordered newest first on the wire; the shared history reader
// keeps a chronological baseline so A-side paging remains compatible.
internal sealed class CodexQueuedHistory(JsonObject thread, bool supportsPaging = true,
    string? olderCursor = null, IReadOnlySet<string>? expandedOlderTurns = null)
{
    internal const int PageSize = 16;
    internal JsonObject Thread { get; } = thread;
    internal JsonArray Turns => Thread["turns"] as JsonArray ?? [];
    internal bool HasOlder => !string.IsNullOrEmpty(olderCursor);
    internal IReadOnlySet<string>? ExpandedOlderTurns { get; } = expandedOlderTurns;

    internal static string TurnId(JsonNode? turn) => CodexDesktopAdapter.Text(turn?["id"] ?? turn?["turnId"]);

    internal static async Task<CodexQueuedHistory> OpenAsync(ICodexMetadata server, string id, CancellationToken ct)
    {
        try
        {
            var metadata = await CodexSavedSession.ReadMetadataAsync(server, id, ct);
            var page = await ReadPageAsync(server, id, null, ct);
            metadata["turns"] = new JsonArray(page.Items.Reverse().Select(turn => turn?.DeepClone()).ToArray());
            return new(metadata, olderCursor: page.Older);
        }
        catch (CodexAppServerRequestException ex) when (ex.MethodNotFound)
        {
            return new(await CodexSavedSession.ReadThreadAsync(server, id, ct), false);
        }
    }

    internal async Task<CodexQueuedHistory> RefreshAsync(ICodexMetadata server, string id, CancellationToken ct)
    {
        if (!supportsPaging) return new(await CodexSavedSession.ReadThreadAsync(server, id, ct), false);
        try
        {
            var baseline = HasOlder ? await ExpandAsync(server, id, ct) : this;
            var metadata = await CodexSavedSession.ReadMetadataAsync(server, id, ct);
            return new(await baseline.MergeAsync(server, metadata, id, ct),
                expandedOlderTurns: HasOlder ? baseline.ExpandedOlderTurns : null);
        }
        catch (CodexAppServerRequestException ex) when (ex.MethodNotFound)
        {
            var full = await CodexSavedSession.ReadThreadAsync(server, id, ct);
            string oldest = TurnId(Turns.FirstOrDefault());
            var older = HasOlder && oldest.Length > 0 && full["turns"] is JsonArray turns
                ? turns.TakeWhile(turn => TurnId(turn) != oldest).Select(TurnId).ToHashSet(StringComparer.Ordinal)
                : null;
            return new(full, false, expandedOlderTurns: older);
        }
    }

    private async Task<CodexQueuedHistory> ExpandAsync(ICodexMetadata server, string id, CancellationToken ct)
    {
        var newestToOldest = new List<JsonNode?>();
        var known = Turns.Select(TurnId).ToHashSet(StringComparer.Ordinal);
        var older = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var cursor = olderCursor;
        while (!string.IsNullOrEmpty(cursor))
        {
            if (!cursors.Add(cursor)) throw new IOException("Codex 会话分页游标重复。");
            var page = await ReadPageAsync(server, id, cursor, ct);
            foreach (var turn in page.Items)
            {
                string turnId = TurnId(turn);
                if (turnId.Length == 0) throw new IOException("Codex 会话分页缺少轮次 ID。");
                if (!known.Contains(turnId) && older.Add(turnId)) newestToOldest.Add(turn);
            }
            cursor = page.Older;
        }
        var expanded = (JsonObject)Thread.DeepClone();
        expanded["turns"] = new JsonArray(newestToOldest.AsEnumerable().Reverse().Concat(Turns)
            .Select(turn => turn?.DeepClone()).ToArray());
        return new(expanded, expandedOlderTurns: older);
    }

    private async Task<JsonObject> MergeAsync(ICodexMetadata server, JsonObject metadata, string id, CancellationToken ct)
    {
        var existing = Turns;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < existing.Count; i++)
        {
            string turnId = TurnId(existing[i]);
            if (turnId.Length == 0 || !indexes.TryAdd(turnId, i))
                return await CodexSavedSession.ReadThreadAsync(server, id, ct);
        }

        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var received = new List<JsonNode?>();
        var receivedIds = new HashSet<string>(StringComparer.Ordinal);
        int prefixLength = 0;
        while (true)
        {
            var page = await ReadPageAsync(server, id, cursor, ct);
            bool overlapping = false;
            foreach (var item in page.Items)
            {
                string turnId = TurnId(item);
                if (turnId.Length == 0) return await CodexSavedSession.ReadThreadAsync(server, id, ct);
                if (!receivedIds.Add(turnId)) continue;
                if (indexes.TryGetValue(turnId, out int index))
                {
                    prefixLength = index;
                    overlapping = true;
                }
                received.Add(item);
            }

            cursor = page.Older;
            if (overlapping)
            {
                // A cursor can stop inside the resident prefix. Keep that prefix and
                // replace its entire overlapping tail from Codex's newest snapshot.
                while (received.Count > 0 && !indexes.ContainsKey(TurnId(received[^1])))
                    received.RemoveAt(received.Count - 1);
                break;
            }
            if (string.IsNullOrEmpty(cursor)) break;
            if (!cursors.Add(cursor)) throw new IOException("Codex 会话分页游标重复。");
        }
        var turns = new JsonArray();
        foreach (var turn in existing.Take(prefixLength)) turns.Add(turn?.DeepClone());
        for (int i = received.Count - 1; i >= 0; i--) turns.Add(received[i]?.DeepClone());
        metadata["turns"] = turns;
        return metadata;
    }

    private static async Task<(JsonArray Items, string? Older)> ReadPageAsync(ICodexMetadata server, string id, string? cursor, CancellationToken ct)
    {
        var page = await server.CallAsync("thread/turns/list", new
        {
            threadId = Guid.Parse(id).ToString("D"), cursor, limit = PageSize, sortDirection = "desc", itemsView = "full"
        }, ct);
        if (page["data"] is not JsonArray items) throw new IOException("Codex 返回了无效的会话分页。");
        return (items, page["nextCursor"]?.GetValue<string>());
    }
}
