using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

internal static class CodexSessionCatalog
{
    public static IEnumerable<SessionInfo> Enrich(string home, IEnumerable<SessionInfo> sessions)
    {
        var result = sessions.ToDictionary(s => s.Id);
        string root = Path.Combine(home, "sessions");
        if (!Directory.Exists(root)) return result.Values;
        // Only metadata from this thread's own rollout, never a child review rollout.
        foreach (string path in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length < 36 || !result.TryGetValue(name[^36..], out var session)) continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                // Metadata is the first JSONL record; never read the full long history.
                var chars = new char[65536]; int length = 0, value;
                while (length < chars.Length && (value = reader.Read()) >= 0 && value != '\n') chars[length++] = (char)value;
                var metadata = JsonNode.Parse(new string(chars, 0, length));
                if (metadata?["type"]?.ToString() != "session_meta" || metadata["payload"]?["id"]?.ToString() != session.Id) continue;
                if (metadata["payload"]?["source"] is JsonObject source && source["subagent"] is not null)
                { result.Remove(session.Id); continue; }
                string cwd = metadata["payload"]?["cwd"]?.ToString() ?? session.Cwd;
                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                result[session.Id] = session with { Cwd = cwd, UpdatedAt = (modified > session.SortTime ? modified : session.SortTime).ToString("O") };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return result.Values;
    }
}
