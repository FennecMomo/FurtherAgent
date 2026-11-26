using System.Text.Json;

namespace FurtherAgent.Core;

internal static class AntigravityTranscript
{
    private const int MaxReadBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 500;
    private const int MaxTextLength = 30000;
    private const string UserRequestStart = "<USER_REQUEST>";
    private const string UserRequestEnd = "</USER_REQUEST>";

    internal static async Task<List<ChatEntry>> ReadAsync(string appDataRoot, string conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(conversationId, "D", out var id)) return [];
        string path = PathFor(appDataRoot, id.ToString("D"));

        try
        {
            // The desktop can append or atomically replace this log while a Controller reads it.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
            long length = stream.Length;
            int window = (int)Math.Min(length, MaxReadBytes + 1);
            if (window == 0) return [];
            var bytes = new byte[window];
            stream.Position = length - window;
            int count = await stream.ReadAtLeastAsync(bytes, window, false, ct);

            return ReadTail(bytes.AsMemory(0, count), truncated: length > window, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    internal static string PathFor(string appDataRoot, string id) =>
        Path.Combine(appDataRoot, "brain", id, ".system_generated", "logs", "transcript.jsonl");

    private static List<ChatEntry> ReadTail(ReadOnlyMemory<byte> bytes, bool truncated, CancellationToken ct)
    {
        var result = new List<ChatEntry>();
        int end = bytes.Length;
        while (end > 0 && result.Count < MaxEntries)
        {
            ct.ThrowIfCancellationRequested();
            if (bytes.Span[end - 1] == (byte)'\n') end--;
            int previousNewLine = bytes.Span[..end].LastIndexOf((byte)'\n');
            if (previousNewLine < 0 && truncated) break;
            int start = previousNewLine + 1;
            if (start == 0 && bytes.Span.StartsWith("\uFEFF"u8)) start = 3;
            if (start < end) ParseLine(bytes[start..end], result);
            end = previousNewLine + 1;
        }
        result.Reverse();
        return result;
    }

    private static void ParseLine(ReadOnlyMemory<byte> line, List<ChatEntry> target)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var step = document.RootElement;
            string kind = StringField(step, "type");
            if (kind is not ("USER_INPUT" or "PLANNER_RESPONSE")) return;

            string content = StringField(step, "content");
            if (kind == "USER_INPUT")
            {
                content = UserText(content);
                if (!string.IsNullOrWhiteSpace(content)) target.Add(new ChatEntry("user", Limit(content)));
                return;
            }

            // Read prose and tool names only. Tool payloads, arguments and internal reasoning can be large.
            if (step.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                var tools = calls.EnumerateArray().Take(12).Select(call => StringField(call, "name"))
                    .Where(name => name.Length is > 0 and <= 80 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (tools.Length > 0) target.Add(new ChatEntry("tool", "调用工具 " + string.Join("、", tools)));
            }
            if (target.Count < MaxEntries && !string.IsNullOrWhiteSpace(content)) target.Add(new ChatEntry("assistant", Limit(content)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { /* The producer may still be writing the last record. */ }
    }

    private static string StringField(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "" : "";

    private static string UserText(string value)
    {
        var text = value.AsSpan().TrimStart();
        if (!text.StartsWith(UserRequestStart, StringComparison.Ordinal)) return value;
        text = text[UserRequestStart.Length..];
        int stop = text.LastIndexOf(UserRequestEnd, StringComparison.Ordinal);
        return stop >= 0 ? text[..stop].Trim("\r\n".AsSpan()).ToString() : "";
    }

    private static string Limit(string text)
    {
        if (text.Length <= MaxTextLength) return text;
        int length = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return text[..length] + "\n[内容已截断]";
    }
}
