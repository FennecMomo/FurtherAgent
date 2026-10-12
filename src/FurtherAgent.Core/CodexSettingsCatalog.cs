using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

internal static class CodexSettingsCatalog
{
    internal static string? SelectEffort(ModelOption model, string? requested, string previous)
    {
        if (requested is not null && model.ReasoningEfforts?.Contains(requested) != true)
            throw new ArgumentException("该模型不支持所选思考强度，请刷新后选择。");
        return requested ?? (model.ReasoningEfforts?.Contains(previous) == true ? previous : model.DefaultReasoningEffort);
    }
    internal static async Task<List<ModelOption>> ModelsAsync(ICodexMetadata metadata, string provider, CancellationToken ct)
    {
        var models = new List<ModelOption>();
        var cursors = new HashSet<string>();
        string? cursor = null;
        do
        {
            var page = await metadata.CallAsync("model/list", new { cursor, limit = 100, includeHidden = false }, ct);
            foreach (var item in page["data"]?.AsArray() ?? [])
            {
                if (item?["hidden"]?.GetValue<bool>() == true) continue;
                string id = Text(item?["model"] ?? item?["id"]);
                if (id.Length == 0 || models.Any(m => m.Id == id)) continue;
                var efforts = (item?["supportedReasoningEfforts"]?.AsArray() ?? [])
                    .Select(e => Text(e?["reasoningEffort"])).Where(e => e.Length > 0).Distinct().ToList();
                models.Add(new(id, provider, Text(item?["displayName"]) is { Length: > 0 } label ? label : id,
                    efforts, Text(item?["defaultReasoningEffort"])));
            }
            cursor = Text(page["nextCursor"]);
            if (cursor.Length > 0 && !cursors.Add(cursor)) throw new IOException("Codex 模型目录返回了重复分页游标。");
        } while (!string.IsNullOrEmpty(cursor));
        return models;
    }

    internal static string Usage(JsonNode result)
    {
        var lines = new List<string> { "远端 Codex 账号限额 · " + DateTimeOffset.Now.ToString("MM-dd HH:mm") };
        var buckets = result["rateLimitsByLimitId"] is JsonObject map && map.Count > 0
            ? map.Select(p => (p.Key, Value: p.Value)).ToList()
            : new List<(string Key, JsonNode? Value)> { ("codex", result["rateLimits"]) };
        foreach (var (key, bucket) in buckets)
        {
            if (bucket is null) continue;
            lines.Add($"{(Text(bucket["limitName"]) is { Length: > 0 } name ? name : key)} · 套餐 {(Text(bucket["planType"]) is { Length: > 0 } plan ? plan : "未知")}");
            bool hasWindow = false;
            foreach (string slot in new[] { "primary", "secondary" })
            {
                if (bucket[slot] is not JsonObject window) continue;
                hasWindow = true;
                string duration = window["windowDurationMins"] is JsonValue minutes && minutes.TryGetValue<double>(out var m)
                    ? m >= 1440 && m % 1440 == 0 ? $"{m / 1440:g} 天" : m >= 60 && m % 60 == 0 ? $"{m / 60:g} 小时" : $"{m:g} 分钟"
                    : slot == "primary" ? "主要窗口" : "次要窗口";
                string remaining = window["usedPercent"] is JsonValue used && used.TryGetValue<double>(out var p) && double.IsFinite(p)
                    ? $"剩余 {Math.Clamp(100 - p, 0, 100):0.#}%" : "剩余比例未知";
                string reset = "重置时间未知";
                if (window["resetsAt"] is JsonValue time && time.TryGetValue<long>(out var seconds))
                    try { reset = "重置 " + DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("MM-dd HH:mm zzz"); } catch (ArgumentOutOfRangeException) { }
                lines.Add($"{duration}：{remaining} · {reset}");
            }
            if (!hasWindow) lines.Add("未返回限额窗口，不能据此视为无限额度。");
        }
        if (lines.Count == 1) lines.Add("远端未返回套餐限额（API Key 或当前账号可能不提供此数据）。");
        return string.Join("\n", lines);
    }
    private static string Text(JsonNode? value) => CodexDesktopAdapter.Text(value);
}
