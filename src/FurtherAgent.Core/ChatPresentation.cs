using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FurtherAgent.Core;

/// <summary>Shared display projection. Never changes the text submitted to an Agent.</summary>
public static class ChatPresentation
{
    internal const string UploadFooter = "\n\n用户上传的文件：\n";

    internal static SessionView IncludeFiles(SessionView view, FileWorkspace workspace)
    {
        var output = workspace.Files.Where(file => file.Id.StartsWith("outbox/", StringComparison.Ordinal)).ToList();
        var inbox = workspace.Files.Where(file => file.Id.StartsWith("inbox/", StringComparison.Ordinal)).ToList();
        if (!inbox.Any(file => ImageThumbnail.Supported(file.Name)))
            return view with { OutputFiles = output };

        string inboxPath = Path.Combine(Path.GetDirectoryName(workspace.Outbox)!, "inbox");
        string PortablePath(string path) => path.Replace('\\', '/');
        var paths = inbox.ToDictionary(file => PortablePath(Path.Combine(inboxPath, file.Id[6..])),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var linked = new HashSet<string>(StringComparer.Ordinal);
        ChatEntry Project(ChatEntry entry)
        {
            if (Kind(entry) != "user") return entry;
            int footer = entry.Text.LastIndexOf(UploadFooter, StringComparison.Ordinal);
            if (footer < 0) return entry;
            string[] lines = entry.Text[(footer + UploadFooter.Length)..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length is 0 or > 20) return entry;
            var attachments = new List<RemoteFile>();
            foreach (string line in lines)
            {
                string? path;
                try { path = JsonNode.Parse(line) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null; }
                catch (System.Text.Json.JsonException) { return entry; }
                if (path is null || !paths.TryGetValue(PortablePath(path), out var file)) return entry;
                attachments.Add(file);
            }
            var images = attachments.Where(file => ImageThumbnail.Supported(file.Name)).DistinctBy(file => file.Id).ToList();
            if (images.Count == 0) return entry;
            foreach (var image in images)
                if (linked.Add(image.Id)) output.Add(image);
            string links = string.Join("\n\n", images.Select(file => $"![图片](attachment:{Uri.EscapeDataString(file.Id)})"));
            var otherNames = attachments.Where(file => !ImageThumbnail.Supported(file.Name)).Select(file => file.Name).ToArray();
            string names = otherNames.Length == 0 ? "" : "\n\n附件：" + string.Join("、", otherNames);
            return entry with { Text = entry.Text[..footer] + "\n\n" + links + names };
        }

        return view with { Messages = view.Messages.Select(Project).ToList(), OutputFiles = output };
    }

    public static ChatEntry Display(ChatEntry entry)
    {
        if (entry.Role is not ("user" or "补充指令" or "你")) return entry;
        const string marker = "\n\n[FurtherAgent 文件工作区]\n";
        int start = entry.Text.LastIndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return entry;
        string footer = entry.Text[(start + marker.Length)..];
        string instruction = FileTransferStore.OutputInstruction;
        int output = footer.IndexOf(instruction, StringComparison.Ordinal);
        if (output < 0)
        {
            instruction = "如需向用户交付文件，请把最终文件写入以下输出目录的顶层（普通文件，不使用符号链接，每文件最多 100 MiB），并在回复中告知文件名：\n";
            output = footer.IndexOf(instruction, StringComparison.Ordinal);
        }
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
