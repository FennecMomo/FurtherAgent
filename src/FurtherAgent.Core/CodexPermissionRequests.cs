using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public static class CodexPermissionRequests
{
    public const string Kind = "item/permissions/requestApproval";

    public static PendingPrompt Pending(string requestId, JsonNode? parameters)
    {
        var args = parameters as JsonObject;
        var details = new List<string>();
        string reason = args?["reason"] is { } reasonValue ? Text(reasonValue) : "";
        if (!string.IsNullOrWhiteSpace(reason)) details.Add(reason);
        if (args?["permissions"] is JsonObject permissions)
        {
            if (permissions["network"] is JsonObject network)
            {
                if (network["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out bool allowed))
                    details.Add("网络访问：" + (allowed ? "允许" : "不允许"));
                else if (network.ContainsKey("enabled")) details.Add("网络访问：" + Text(network["enabled"]));
                AddOther(details, network, ["enabled"], "网络");
            }
            else if (permissions["network"] is { } networkValue) details.Add("网络：" + Text(networkValue));
            if (permissions["fileSystem"] is JsonObject fileSystem)
            {
                AddPaths(details, fileSystem["read"], "文件读取");
                AddPaths(details, fileSystem["write"], "文件写入");
                if (fileSystem["entries"] is JsonArray entries)
                    foreach (var entry in entries)
                    {
                        if (entry is JsonObject rule)
                        {
                            string access = Text(rule["access"]);
                            string label = access switch { "read" => "文件读取", "write" => "文件写入", "deny" => "禁止访问", _ => "文件权限（" + access + "）" };
                            details.Add(label + "：" + Path(rule["path"]));
                            AddOther(details, rule, ["access", "path"], "文件规则");
                        }
                        else details.Add("文件规则：" + Text(entry));
                    }
                else if (fileSystem["entries"] is { } entriesValue) details.Add("文件规则：" + Text(entriesValue));
                AddOther(details, fileSystem, ["read", "write", "entries"], "文件系统");
            }
            else if (permissions["fileSystem"] is { } fileSystemValue) details.Add("文件系统：" + Text(fileSystemValue));
            AddOther(details, permissions, ["network", "fileSystem"], "其他权限");
            if (!HasContent(permissions)) details.Add("请求未提供访问权限。");
        }
        else details.Add("权限内容无法识别：" + Text(args?["permissions"]));
        details.Add("允许范围：当前轮次");
        return new(requestId, Kind, "Agent 请求访问权限", string.Join("\n", details), parameters?.DeepClone());
    }

    public static JsonObject BuildResponse(JsonNode? parameters, string decision)
    {
        if (decision is not ("accept" or "decline")) throw new ArgumentException("权限请求只支持允许本次或拒绝。", nameof(decision));
        if (decision == "decline") return new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" };
        if (parameters is not JsonObject args || args["permissions"] is not JsonObject permissions || !HasContent(permissions))
            throw new ArgumentException("权限请求内容缺失或无效，请刷新后再操作。", nameof(parameters));
        foreach (string field in new[] { "network", "fileSystem" })
            if (permissions[field] is { } value && value is not JsonObject)
                throw new ArgumentException("权限请求内容无效，请刷新后再操作。", nameof(parameters));
        if (permissions["network"] is JsonObject network && network["enabled"] is { } enabled &&
            (enabled is not JsonValue boolean || !boolean.TryGetValue<bool>(out _)))
            throw new ArgumentException("网络权限请求内容无效，请刷新后再操作。", nameof(parameters));
        if (permissions["fileSystem"] is JsonObject fileSystem)
            foreach (string field in new[] { "read", "write", "entries" })
                if (fileSystem[field] is { } paths && paths is not JsonArray)
                    throw new ArgumentException("文件权限请求内容无效，请刷新后再操作。", nameof(parameters));
        return new JsonObject { ["permissions"] = permissions.DeepClone(), ["scope"] = "turn" };
    }

    private static bool HasContent(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(pair => HasContent(pair.Value)),
        JsonArray list => list.Any(HasContent),
        JsonValue => true,
        _ => false
    };
    private static void AddOther(List<string> lines, JsonObject obj, string[] known, string label)
    {
        foreach (var pair in obj)
            if (!known.Contains(pair.Key)) lines.Add(label + "（" + pair.Key + "）：" + Text(pair.Value));
    }
    private static void AddPaths(List<string> lines, JsonNode? paths, string label)
    {
        if (paths is JsonArray list) foreach (var path in list) lines.Add(label + "：" + Path(path));
        else if (paths is not null) lines.Add(label + "：" + Text(paths));
    }
    private static string Path(JsonNode? path)
    {
        if (path is JsonObject obj && obj.Count == 2)
        {
            string type = Text(obj["type"]);
            if (type == "path" && obj["path"] is JsonValue) return Text(obj["path"]);
            if (type == "glob_pattern" && obj["pattern"] is JsonValue) return "匹配路径 " + Text(obj["pattern"]);
            if (type == "special" && obj["value"] is JsonObject special)
            {
                string kind = Text(special["kind"]);
                var known = kind == "project_roots" ? new[] { "kind", "subpath" } : ["kind"];
                if (special.All(pair => known.Contains(pair.Key)))
                {
                    string? label = kind switch
                    {
                        "root" => "全部文件（根目录）",
                        "minimal" => "基础系统路径（minimal）",
                        "project_roots" => "工作区目录",
                        "tmpdir" => "系统临时目录",
                        "slash_tmp" => "临时目录 /tmp",
                        _ => null
                    };
                    if (label is not null) return special["subpath"] is { } subpath ? label + "下的 " + Text(subpath) : label;
                }
            }
        }
        return Text(path);
    }
    private static string Text(JsonNode? node) => node is null ? "null" : CodexDesktopAdapter.Text(node);
}
