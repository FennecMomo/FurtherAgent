using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

internal sealed class CodexWorkspaces(string home, Func<CancellationToken, Task<ICodexMetadata>>? connect = null)
{
    private async Task<ICodexMetadata> OpenAsync(CancellationToken ct) => connect is null ? await CodexAppServer.OpenAsync(home, ct) : await connect(ct);
    internal static string DirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择远端目录，或输入远端绝对路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("远端目录不存在或无权访问：" + full);
        return full;
    }
    internal static WorkspaceDirectories Browse(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(path)) return new("", null, DriveInfo.GetDrives().Where(d => d.IsReady)
            .Select(d => new WorkspaceDirectory(d.Name, d.RootDirectory.FullName)).ToArray());
        var full = DirectoryPath(path);
        var children = new List<WorkspaceDirectory>();
        foreach (var item in new DirectoryInfo(full).EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            // Never follow junctions while listing. Opening a selected path is explicit.
            if ((item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            children.Add(new(item.Name, item.FullName));
            if (children.Count > 2000) throw new IOException("此目录子文件夹超过 2000 个，请直接输入更具体的远端路径。");
        }
        return new(full, Directory.GetParent(full)?.FullName, children.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }
    internal static string ChildPath(string parent, string folder)
    {
        parent = DirectoryPath(parent);
        if (string.IsNullOrWhiteSpace(folder) || folder != folder.Trim() || folder is "." or ".." ||
            folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder.Contains('/') || folder.Contains('\\') || folder.EndsWith('.'))
            throw new ArgumentException("新文件夹名称无效，请只填写一个文件夹名。");
        var stem = folder.Split('.')[0].ToUpperInvariant();
        if (OperatingSystem.IsWindows() && (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3]))))
            throw new ArgumentException("不能使用 Windows 保留名称。");
        return Path.Combine(parent, folder);
    }
    internal async Task<IReadOnlyList<WorkspaceInfo>> ListAsync(CancellationToken ct)
    {
        await using var server = await OpenAsync(ct);
        return await ListAsync(server, ct);
    }
    private static async Task<IReadOnlyList<WorkspaceInfo>> ListAsync(ICodexMetadata server, CancellationToken ct)
    {
        var items = new List<WorkspaceInfo>(); string? cursor = null;
        do
        {
            var result = await server.CallAsync("project/list", new { limit = 100, cursor }, ct);
            foreach (var project in result["data"]!.AsArray())
                foreach (var root in project!["roots"]!.AsArray())
                    items.Add(new(project["id"]!.ToString(), project["name"]!.ToString(), root!["path"]!.ToString()));
            cursor = result["nextCursor"]?.ToString();
        } while (!string.IsNullOrEmpty(cursor));
        return items;
    }
    internal async Task<WorkspaceInfo> AddAsync(string id, string path, string folder, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("工作空间请求 ID 无效。");
        path = DirectoryPath(path);
        if (folder.Length > 0) path = ChildPath(path, folder);
        await using var server = await OpenAsync(ct);
        // Check protocol support before creating a directory. No parent trees are created implicitly.
        var projects = await ListAsync(server, ct);
        ct.ThrowIfCancellationRequested();
        if (folder.Length > 0) Directory.CreateDirectory(path);
        return await EnsureAsync(server, projects, path, ct);
    }
    private static async Task<WorkspaceInfo> EnsureAsync(ICodexMetadata server, IReadOnlyList<WorkspaceInfo> projects, string path, CancellationToken ct)
    {
        var found = projects.FirstOrDefault(p => SamePath(p.Path, path));
        if (found is not null) return found;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path)));
        var name = Path.GetFileName(path); if (string.IsNullOrEmpty(name)) name = path;
        var result = await server.CallAsync("project/create", new { idempotencyKey = "furtheragent-" + key, name, roots = new[] { new { path } } }, ct);
        var project = result["project"]!;
        return new(project["id"]!.ToString(), project["name"]!.ToString(), path);
    }
    internal static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal async Task<SessionInfo> CreateAsync(string id, string title, string cwd, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("新会话请求 ID 无效。");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) throw new ArgumentException("会话标题需为 1–200 个字符。");
        cwd = DirectoryPath(cwd);
        var root = Path.Combine(home, "furtheragent", "create-requests"); Directory.CreateDirectory(root);
        // Cross-process lock prevents two B hosts from replaying one creation request.
        await using var held = new FileStream(Path.Combine(root, id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string file = Path.Combine(root, id + ".json");
        JsonNode record;
        if (File.Exists(file))
        {
            record = JsonNode.Parse(await File.ReadAllTextAsync(file, ct))!;
            if (record["title"]?.ToString() != title || !SamePath(record["cwd"]!.ToString(), cwd)) throw new IOException("此创建请求的标题或工作目录已改变，请关闭后重新新建。");
            if (record["threadId"] is null) throw new IOException("上次创建结果未知，为避免重复创建已停止重试。请刷新远端会话列表核对，再发起新的创建。");
        }
        else record = Json.Node(new { title, cwd });
        await using var server = await OpenAsync(ct);
        if (record["threadId"] is null)
        {
            var project = await EnsureAsync(server, await ListAsync(server, ct), cwd, ct);
            await SaveAsync(file, record); // Pending marker is durable before issuing the non-idempotent request.
            var created = await server.CallAsync("thread/start", new { cwd, projectId = project.Id, ephemeral = false, historyMode = "legacy" }, ct);
            record["threadId"] = created["thread"]!["id"]!.ToString();
            await SaveAsync(file, record); // Even cancellation after a successful reply must preserve the ID.
        }
        var threadId = record["threadId"]!.ToString();
        await server.CallAsync("thread/name/set", new { threadId, name = title }, ct);
        var read = await server.CallAsync("thread/read", new { threadId, includeTurns = false }, ct);
        var thread = read["thread"]!;
        if (!SamePath(thread["cwd"]!.ToString(), cwd)) throw new IOException("Codex 返回的会话目录与所选工作空间不一致，已停止打开。");
        var result = new SessionInfo(threadId, title, thread["updatedAt"]!.ToString(), cwd);
        // Dispose closes the metadata process before the caller attaches through Desktop IPC.
        return result;
    }
    private static async Task SaveAsync(string file, JsonNode record)
    {
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, record.ToJsonString(Json.Options));
        File.Move(temp, file, true);
    }
}
