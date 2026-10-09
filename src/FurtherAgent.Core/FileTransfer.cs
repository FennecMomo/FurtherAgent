using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public record RemoteFile(string Id, string Name, long Size, string Sha256 = "")
{ public string DisplayName => $"{(Id.StartsWith("outbox/", StringComparison.Ordinal) ? "Agent 输出" : "上传文件")} · {Name} · {Size:N0} bytes"; }
public record FileWorkspace(string Outbox, long MaxFileBytes, int ChunkBytes, List<RemoteFile> Files);
public record FileProgress(long Completed, long Total);

// Host-owned filesystem boundary. Clients address only files inside a hashed session workspace.
public sealed class FileTransferStore : IDisposable
{
    public const long MaxFileBytes = 100L * 1024 * 1024;
    public const int ChunkBytes = 128 * 1024;
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Upload> uploads = [];
    private sealed record Upload(string Session, string Name, long Size, string Hash, string Part, string Target)
    { public DateTime UpdatedAt { get; set; } = DateTime.UtcNow; }
    public FileTransferStore(string? root = null)
    {
        this.root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("FURTHERAGENT_FILES_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "files"));
        if (this.root.TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(this.root)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new ArgumentException("请为文件传输指定专用目录，不能使用磁盘根目录。");
        bool existing = Directory.Exists(this.root);
        Directory.CreateDirectory(this.root); NoLink(this.root);
        if (!existing && !OperatingSystem.IsWindows()) File.SetUnixFileMode(this.root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void NoLink(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("文件传输不允许符号链接或重解析点。"); }
    private string Workspace(string session)
    {
        if (string.IsNullOrWhiteSpace(session) || session.Length > 512) throw new ArgumentException("无效会话。");
        NoLink(root);
        string dir = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session))).ToLowerInvariant());
        Directory.CreateDirectory(dir); NoLink(dir);
        foreach (var name in new[] { "inbox", "outbox" }) { string child = Path.Combine(dir, name); Directory.CreateDirectory(child); NoLink(child); }
        // Only our abandoned staging files; completed files are retained until the operator removes them.
        foreach (var path in Directory.EnumerateFiles(dir, "*.part").Take(100))
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1) && !uploads.Values.Any(u => u.Part == path))
            { NoLink(path); File.Delete(path); }
        return dir;
    }
    private static string FileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny("/\\:*?\"<>|".ToCharArray()) >= 0 || name.Any(char.IsControl)) throw new ArgumentException("文件名无效。");
        return name;
    }
    private string Resolve(string session, string id)
    {
        var parts = id.Split('/');
        if (parts.Length != 2 || parts[0] is not ("inbox" or "outbox")) throw new ArgumentException("无效文件 ID。");
        string path = Path.Combine(Workspace(session), parts[0], FileName(parts[1]));
        NoLink(path);
        if (new FileInfo(path).Length > MaxFileBytes) throw new IOException("文件超过 100 MiB 上限。");
        return path;
    }
    public FileWorkspace List(string session)
    {
        string dir = Workspace(session);
        var files = new List<RemoteFile>();
        foreach (string area in new[] { "inbox", "outbox" })
            foreach (var path in Directory.EnumerateFiles(Path.Combine(dir, area)).Take(201))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var info = new FileInfo(path); if (info.Length > MaxFileBytes) continue;
                files.Add(new(area + "/" + info.Name, area == "inbox" && info.Name.Length > 34 ? info.Name[34..] : info.Name, info.Length));
                if (files.Count > 200) throw new IOException("会话文件过多，请在接入端清理后重试。");
            }
        return new(Path.Combine(dir, "outbox"), MaxFileBytes, ChunkBytes, files);
    }
    private static async Task<string> Hash(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    public async Task<JsonNode> HandleAsync(string method, JsonNode args, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await HandleCoreAsync(method, args, ct); }
        finally { gate.Release(); }
    }
    private async Task<JsonNode> HandleCoreAsync(string method, JsonNode args, CancellationToken ct)
    {
        foreach (var stale in uploads.Where(p => p.Value.UpdatedAt < DateTime.UtcNow.AddHours(-1)).ToArray())
        {
            _ = Workspace(stale.Value.Session); NoLink(stale.Value.Part); File.Delete(stale.Value.Part); uploads.Remove(stale.Key);
        }
        string session = args["session"]?.ToString() ?? throw new ArgumentException("缺少会话。");
        if (method == "files/list") return Json.Node(List(session));
        if (method == "files/begin")
        {
            string name = FileName(args["name"]!.ToString()); long size = args["size"]!.GetValue<long>(); string hash = args["sha256"]!.ToString();
            if (size < 0 || size > MaxFileBytes || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("文件大小或 SHA256 无效。");
            if (uploads.Count >= 8) throw new IOException("待完成上传过多，请取消旧上传或重启测试接入端。");
            var workspace = List(session);
            if (workspace.Files.Count >= 200 || workspace.Files.Sum(f => f.Size) + uploads.Values.Where(u => u.Session == session).Sum(u => u.Size) + size > 1024L * 1024 * 1024)
                throw new IOException("会话文件额度已满（最多 200 个 / 1 GiB），请在接入端清理。");
            string id = Guid.NewGuid().ToString("N"), dir = Workspace(session), target = Path.Combine(dir, "inbox", id + "__" + name), part = Path.Combine(dir, id + ".part");
            using (new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            uploads.Add(id, new(session, name, size, hash, part, target));
            return Json.Node(new { id, chunkBytes = ChunkBytes });
        }
        if (method is "files/chunk" or "files/commit" or "files/abort")
        {
            string id = args["id"]!.ToString();
            if (!uploads.TryGetValue(id, out var upload) || upload.Session != session) throw new IOException("上传不存在或不属于此会话。");
            upload.UpdatedAt = DateTime.UtcNow;
            _ = Workspace(session); NoLink(upload.Part);
            if (method == "files/abort") { File.Delete(upload.Part); uploads.Remove(id); return Json.Node(new { ok = true }); }
            if (method == "files/chunk")
            {
                string encoded = args["data"]!.ToString();
                if (encoded.Length > (ChunkBytes + 2) / 3 * 4) throw new IOException("文件分块过大。");
                byte[] bytes = Convert.FromBase64String(encoded); long offset = args["offset"]!.GetValue<long>();
                await using var stream = new FileStream(upload.Part, FileMode.Open, FileAccess.Write, FileShare.None, ChunkBytes, true);
                if (bytes.Length == 0 || bytes.Length > ChunkBytes || offset != stream.Length || offset > upload.Size - bytes.Length) throw new IOException("文件分块偏移或大小不匹配。");
                stream.Position = offset; await stream.WriteAsync(bytes, ct); return Json.Node(new { offset = stream.Position });
            }
            if (new FileInfo(upload.Part).Length != upload.Size || !string.Equals(await Hash(upload.Part, ct), upload.Hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件长度或 SHA256 校验失败，未交付给 Agent。");
            File.Move(upload.Part, upload.Target, false); uploads.Remove(id);
            return Json.Node(new RemoteFile("inbox/" + Path.GetFileName(upload.Target), upload.Name, upload.Size, upload.Hash));
        }
        string fileId = args["id"]!.ToString(); string file = Resolve(session, fileId);
        if (method == "files/stat") return Json.Node(new RemoteFile(fileId, Path.GetFileName(file), new FileInfo(file).Length, await Hash(file, ct)));
        if (method == "files/read")
        {
            long offset = args["offset"]!.GetValue<long>(); int count = args["count"]!.GetValue<int>();
            if (offset < 0 || count < 1 || count > ChunkBytes) throw new ArgumentException("读取范围无效。");
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, true);
            if (offset > stream.Length) throw new IOException("文件已变化，请重新下载。");
            stream.Position = offset; byte[] bytes = new byte[Math.Min(count, stream.Length - offset)];
            await stream.ReadExactlyAsync(bytes, ct); return Json.Node(new { data = Convert.ToBase64String(bytes) });
        }
        throw new IOException("未知文件操作。");
    }
    public async Task<string> ComposeMessageAsync(string session, string text, IEnumerable<string> ids, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return ComposeMessage(session, text, ids); }
        finally { gate.Release(); }
    }
    private string ComposeMessage(string session, string text, IEnumerable<string> ids)
    {
        var paths = ids.Take(21).Select(id => Resolve(session, id)).ToArray();
        if (paths.Length > 20) throw new ArgumentException("每条消息最多附加 20 个文件。");
        return text + "\n\n[FurtherAgent 文件工作区]\n" +
            (paths.Length == 0 ? "" : "用户上传的文件已存到此机器，可按任务需要读取：\n" + string.Join("\n", paths.Select(p => Json.Node(p).ToJsonString())) + "\n") +
            "如需向用户交付文件，请把最终文件写入以下输出目录的顶层（普通文件，不使用符号链接，每文件最多 100 MiB），并在回复中告知文件名：\n" + Json.Node(List(session).Outbox).ToJsonString();
    }
    public void Dispose()
    { foreach (var item in uploads.Values) { try { NoLink(item.Part); File.Delete(item.Part); } catch { } } uploads.Clear(); }
}

// Shared by WPF and CLI: transfer, integrity checks, cancellation and attachment submission.
public sealed class FileTransferClient(RpcPeer peer)
{
    public async Task<FileWorkspace> ListAsync(string session, CancellationToken ct = default) =>
        Json.Read<FileWorkspace>(await peer.CallAsync("files/list", new { session }, ct));
    public async Task<RemoteFile> UploadAsync(string session, string path, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferStore.ChunkBytes, true);
        if (stream.Length > FileTransferStore.MaxFileBytes) throw new IOException("文件超过 100 MiB 上限。");
        long size = stream.Length; string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); stream.Position = 0;
        var start = await peer.CallAsync("files/begin", new { session, name = Path.GetFileName(path), size, sha256 = hash }, ct);
        string id = start!["id"]!.ToString();
        try
        {
            byte[] buffer = new byte[FileTransferStore.ChunkBytes]; long offset = 0; int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await peer.CallAsync("files/chunk", new { session, id, offset, data = Convert.ToBase64String(buffer, 0, count) }, ct);
                offset += count; progress?.Report(new(offset, size));
            }
            return Json.Read<RemoteFile>(await peer.CallAsync("files/commit", new { session, id }, ct));
        }
        catch
        {
            try { using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await peer.CallAsync("files/abort", new { session, id }, abort.Token); } catch { }
            throw;
        }
    }
    public async Task<RemoteFile> DownloadAsync(string session, string id, string destination, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        string target = Path.GetFullPath(destination);
        if (File.Exists(target)) throw new IOException("目标文件已存在，请选择新名称。");
        var info = Json.Read<RemoteFile>(await peer.CallAsync("files/stat", new { session, id }, ct));
        if (info.Size < 0 || info.Size > FileTransferStore.MaxFileBytes || info.Sha256.Length != 64) throw new IOException("远端文件信息无效。");
        string part = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileTransferStore.ChunkBytes, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long offset = 0;
                while (offset < info.Size)
                {
                    var chunk = await peer.CallAsync("files/read", new { session, id, offset, count = (int)Math.Min(FileTransferStore.ChunkBytes, info.Size - offset) }, ct);
                    string data = chunk!["data"]!.ToString();
                    if (data.Length > (FileTransferStore.ChunkBytes + 2) / 3 * 4) throw new IOException("远端分块过大。");
                    byte[] bytes = Convert.FromBase64String(data);
                    if (bytes.Length == 0 || bytes.Length > Math.Min(FileTransferStore.ChunkBytes, info.Size - offset)) throw new IOException("远端文件已变化或分块无效。");
                    await stream.WriteAsync(bytes, ct); hash.AppendData(bytes); offset += bytes.Length; progress?.Report(new(offset, info.Size));
                }
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("下载 SHA256 校验失败，文件未交付。");
            }
            File.Move(part, target, false); return info;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
    public async Task SendAsync(string session, string text, IEnumerable<string> fileIds, CancellationToken ct = default, TimeSpan? responseTimeout = null) =>
        await peer.CallAsync("send-files", new { session, text, files = fileIds.ToArray() }, ct, responseTimeout);
}
