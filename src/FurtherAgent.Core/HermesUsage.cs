using System.Globalization;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public sealed partial class HermesAdapter
{
    private void ReadUsage(JsonNode result)
    {
        if (result["usage"] is not JsonObject counts) return;
        var values = new List<string>();
        foreach (var (key, label) in new[] { ("input_tokens", "输入"), ("output_tokens", "输出"),
            ("total_tokens", "总计"), ("cache_read_tokens", "缓存读取"), ("cache_write_tokens", "缓存写入") })
            if (long.TryParse(counts[key]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
                values.Add($"{label} {value:N0}");
        if (values.Count == 0) return;
        string actualProvider = result["runtime"]?["provider"]?.ToString() ?? "";
        string actualModel = result["runtime"]?["model"]?.ToString() ?? "";
        usage = "最近一次运行用量（tokens）：" + string.Join(" · ", values)
            + (actualModel.Length > 0 ? $"\n实际模型：{actualProvider} / {actualModel}" : "");
    }
}
