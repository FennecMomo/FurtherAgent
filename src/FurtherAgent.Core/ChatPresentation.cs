using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FurtherAgent.Core;

/// <summary>Shared display projection. Never changes the text submitted to an Agent.</summary>
public static class ChatPresentation
{
    public static ChatEntry Display(ChatEntry entry)
    {
        if (entry.Role is not ("user" or "补充指令")) return entry;
        const string marker = "\n\n[FurtherAgent 文件工作区]\n";
        int start = entry.Text.LastIndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return entry;
        string footer = entry.Text[(start + marker.Length)..];
        const string instruction = "如需向用户交付文件，请把最终文件写入以下输出目录的顶层（普通文件，不使用符号链接，每文件最多 100 MiB），并在回复中告知文件名：\n";
        int output = footer.IndexOf(instruction, StringComparison.Ordinal);
        if (output < 0) return entry;
        try
        {
            string? path = JsonNode.Parse(footer[(output + instruction.Length)..])?.GetValue<string>();
            if (path is null || !Regex.IsMatch(path.Replace('\\', '/'), @"/[a-fA-F0-9]{64}/outbox$")) return entry;
            string attachments = footer[..output];
            if (attachments.Length > 0 && !attachments.StartsWith("用户上传的文件已存到此机器，可按任务需要读取：\n", StringComparison.Ordinal)) return entry;
            var names = attachments.Split('\n').Skip(1).Where(x => x.Length > 0)
                .Select(x => JsonNode.Parse(x)!.GetValue<string>().Replace('\\', '/').Split('/')[^1])
                .Select(x => x.Contains("__") ? x[(x.IndexOf("__", StringComparison.Ordinal) + 2)..] : x).ToArray();
            return entry with { Text = entry.Text[..start] + (names.Length == 0 ? "" : "\n\n附件：" + string.Join("、", names)) };
        }
        catch { return entry; } // Unrecognized user text must remain intact.
    }

    public static string Kind(ChatEntry entry) => entry.Role switch
    {
        "user" or "补充指令" or "你" => "user",
        "assistant" when entry.Text.Length == 0 && entry.Detail is not null => "tool",
        "assistant" or "Hermes · 增量" or "Hermes · 进度" or "Codex" => "assistant",
        "推理摘要" => "reasoning",
        "工具" => "tool",
        var role when role.Contains("reasoning", StringComparison.OrdinalIgnoreCase) => "reasoning",
        var role when role.StartsWith("tool", StringComparison.OrdinalIgnoreCase) => "tool",
        _ => "system"
    };
}
