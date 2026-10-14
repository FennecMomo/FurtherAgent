using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public static class OperationSummary
{
    public static ChatEntry Display(ChatEntry entry)
    {
        string summary = "执行了一项操作";
        JsonNode? data = null;
        try { if (entry.Detail is not null) data = JsonNode.Parse(entry.Detail) as JsonObject; } catch { }
        string type = data?["type"]?.ToString() ?? "";
        string status = data?["status"]?.ToString() ?? "";
        string Short(string value) => value.Replace('\r', ' ').Replace('\n', ' ') is { } clean && clean.Length > 160 ? clean[..160] + "…" : value.Replace('\r', ' ').Replace('\n', ' ');
        if (type == "mcpToolCall") summary = "调用工具 " + Short(data?["tool"]?.ToString() ?? "未知工具");
        else if (type == "commandExecution")
        {
            string command = data?["command"]?.ToString()?.Trim() ?? "";
            string program = command.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            summary = "执行终端命令" + (System.Text.RegularExpressions.Regex.IsMatch(program, @"^[a-zA-Z0-9_.-]{1,40}$") ? " " + program : "");
        }
        else if (data?["tool_calls"] is JsonArray calls)
            summary = "调用工具 " + string.Join("、", calls.OfType<JsonObject>().Select(call => Short(call["function"]?["name"]?.ToString() ?? call["name"]?.ToString() ?? "未知工具")));
        else if (data?["role"]?.ToString() == "tool") summary = "收到工具结果" + (data?["name"] is { } name ? " " + Short(name.ToString()) : "");
        else if (type == "fileChange" && data?["changes"] is JsonArray changes)
            summary = string.Join("；", changes.Select(change =>
            {
                string kind = change?["kind"] is JsonObject obj ? obj["type"]?.ToString() ?? "" : change?["kind"]?.ToString() ?? "";
                string verb = kind switch { "delete" => "删除文件", "add" => "新增文件", _ => "修改文件" };
                return verb + " " + Short(change?["path"]?.ToString() ?? "未知文件");
            }));
        else if (ChatPresentation.Kind(entry) == "reasoning") summary = "分析任务与规划步骤";
        else if (type == "contextCompaction" || entry.Text.StartsWith("上下文已压缩")) summary = "整理并压缩会话上下文";
        else if (entry.Detail is null && (entry.Text.StartsWith("收到工具结果") || entry.Text.StartsWith("调用工具 ") || entry.Text.StartsWith("执行终端命令") || entry.Text.StartsWith("删除文件 ") || entry.Text.StartsWith("新增文件 ") || entry.Text.StartsWith("修改文件 ") || entry.Text.StartsWith("分析任务") || entry.Text.StartsWith("整理并压缩"))) summary = entry.Text;
        else if (entry.Role.StartsWith("tool:", StringComparison.OrdinalIgnoreCase)) summary = "调用工具 " + Short(entry.Role[5..]);
        if (status is "failed" or "declined") summary += "（未成功）";
        else if (status is "inProgress" or "running") summary += "（进行中）";
        return new("工具", summary);
    }
}
