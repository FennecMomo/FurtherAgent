using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public static class CodexPermissions
{
    public static readonly string[] Values = [":read-only", ":workspace", ":danger-full-access"];
    public static readonly string[] Labels = ["只读", "工作区写入 · 按需确认", "完全访问 · 无需确认"];

    public static JsonNode Build(string permission)
    {
        if (!Values.Contains(permission)) throw new ArgumentException("请选择受支持的会话权限。");
        return Json.Node(new { permissions = permission, activePermissionProfile = new { id = permission, extends = (string?)null },
            approvalPolicy = permission == ":workspace" ? "on-request" : "never" });
    }

    public static string Current(JsonNode state)
    {
        var latest = state["latestThreadSettings"];
        var settings = latest?["activePermissionProfile"] is not null || latest?["permissions"] is not null || latest?["sandboxPolicy"] is not null
            ? latest : state["currentPermissions"];
        string? profile = settings?["activePermissionProfile"]?["id"]?.ToString() ?? settings?["permissions"]?.ToString();
        string? sandbox = settings?["sandboxPolicy"]?["type"]?.ToString() ?? settings?["sandbox"]?["type"]?.ToString();
        string? value = profile ?? sandbox switch { "readOnly" => Values[0], "workspaceWrite" => Values[1], "dangerFullAccess" => Values[2], _ => null };
        int index = Array.IndexOf(Values, value);
        if (index < 0) return value is null ? "未知 · 请在 Codex 核对" : "自定义 · " + value;
        string? approval = settings?["approvalPolicy"]?.ToString();
        string expected = value == Values[1] ? "on-request" : "never";
        return approval is not null && approval != expected ? "自定义 · " + Labels[index] + " / " + approval : value!;
    }
}
