using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

static class FileChecks
{
    public static async Task RunAsync(RegistryClient registry, Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "fa-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var adapter = new FileAgent();
        await using var host = new DeviceHost(registry, () => adapter, Path.Combine(root, "host"));
        await host.StartAsync("file fixture");
        await using var remote = new RemoteDevice(false);
        await remote.ConnectAsync(registry, host.Lease!.Code);
        var peer = remote.Peer;
        async Task Reject(Func<Task> action, string message)
        { bool rejected = false; try { await action(); } catch { rejected = true; } check(rejected, message); }
        await Reject(() => { using var invalid = new FileTransferStore(Path.GetPathRoot(root)!); return Task.CompletedTask; }, "file store rejects disk root configuration");
        check(Json.Read<AgentInfo[]>(await peer.CallAsync("agents", null)).Single().CanFiles, "host advertises file capability independently of Agent");
        var client = new FileTransferClient(peer);
        await Reject(async () => await client.ListAsync("test"), "files require attached session");
        await peer.CallAsync("attach", new { id = "test" });
        byte[] content = RandomNumberGenerator.GetBytes(FileTransferStore.ChunkBytes * 3 + 43);
        string source = Path.Combine(root, "附件 sample.bin"); await File.WriteAllBytesAsync(source, content);
        var uploaded = await client.UploadAsync("test", source);
        var workspace = await client.ListAsync("test");
        check(workspace.Files.Single().Name == "附件 sample.bin" && uploaded.Sha256 == Convert.ToHexString(SHA256.HashData(content)), "multi-chunk upload preserves Unicode filename and SHA256");
        await client.SendAsync("test", "请读取", [uploaded.Id]);
        check(adapter.Text.Contains("用户上传的文件：") && adapter.Text.Contains("inbox") && !adapter.Text.Contains("文件工作区") && !adapter.Text.Contains(workspace.Outbox) && !adapter.Text.Contains(source), "attachment send includes only host-local input paths without output instructions");
        string legacy = "请读取\n\n[FurtherAgent 文件工作区]\n用户上传的文件已存到此机器，可按任务需要读取：\n" +
            Json.Node(Path.Combine(Path.GetDirectoryName(workspace.Outbox)!, uploaded.Id.Replace('/', Path.DirectorySeparatorChar))).ToJsonString() + "\n" +
            FileTransferStore.OutputInstruction + Json.Node(workspace.Outbox).ToJsonString();
        foreach (string role in new[] { "user", "补充指令", "你" })
            check(ChatPresentation.Display(new(role, legacy)).Text == "请读取\n\n附件：附件 sample.bin", "legacy workspace metadata hidden for " + role);
        await client.SendAsync("test", "原文\n保留换行", []);
        check(adapter.Text == "原文\n保留换行", "plain message reaches Agent unchanged without workspace instructions");
        check(ChatPresentation.Display(new("assistant", adapter.Text)).Text == adapter.Text, "assistant content stays intact");
        check(ChatPresentation.Display(new("你", "说明\n\n[FurtherAgent 文件工作区]\n用户原文")).Text.EndsWith("用户原文"), "user-authored workspace marker is preserved");
        string generated = Path.Combine(workspace.Outbox, "result.txt");
        await File.WriteAllTextAsync(generated, "Agent fixture output 中文");
        workspace = await client.ListAsync("test");
        var output = workspace.Files.Single(f => f.Id == "outbox/result.txt");
        var refreshed = Json.Read<SessionView>(await peer.CallAsync("attach", new { id = "test" }));
        check(refreshed.OutputFiles?.Any(f => f.Id == output.Id) == true, "non-Hermes agents include output files in session snapshots");
        string destination = Path.Combine(root, "download.txt"); await client.DownloadAsync("test", output.Id, destination);
        check(await File.ReadAllTextAsync(destination) == "Agent fixture output 中文", "Agent-produced file lists and downloads through relay");
        await Reject(async () => await client.DownloadAsync("test", output.Id, destination), "download never overwrites existing local file");
        string roundtrip = Path.Combine(root, "roundtrip.bin"); await client.DownloadAsync("test", uploaded.Id, roundtrip);
        check((await File.ReadAllBytesAsync(roundtrip)).SequenceEqual(content), "multi-chunk binary download matches uploaded bytes");
        string changing = Path.Combine(workspace.Outbox, "changing.bin"); await File.WriteAllBytesAsync(changing, content);
        string changedTarget = Path.Combine(root, "changed.bin"); bool modified = false;
        await Reject(async () => await client.DownloadAsync("test", "outbox/changing.bin", changedTarget, new InlineProgress(p =>
        {
            if (!modified) { modified = true; var changedBytes = (byte[])content.Clone(); changedBytes[^1] ^= 255; File.WriteAllBytes(changing, changedBytes); }
        })), "download detects file modified between chunks");
        check(!File.Exists(changedTarget) && !Directory.EnumerateFiles(root, "changed.bin.*.part").Any(), "failed download removes staging and never publishes corrupt output");
        using var midCancel = new CancellationTokenSource();
        await Reject(async () => await client.DownloadAsync("test", uploaded.Id, Path.Combine(root, "cancelled.bin"), new InlineProgress(_ => midCancel.Cancel()), midCancel.Token), "mid-transfer cancellation stops download");
        check(!Directory.EnumerateFiles(root, "cancelled.bin*").Any(), "cancelled download removes partial file");
        await Reject(async () => await client.ListAsync("other"), "file RPC rejects mismatched selected session");
        await Reject(async () => await peer.CallAsync("files/stat", new { session = "test", id = "outbox/../../secret" }), "file path traversal is rejected");
        await Reject(async () => await peer.CallAsync("files/begin", new { session = "test", name = "../escape", size = 0, sha256 = new string('0', 64) }), "upload path traversal is rejected");
        await Reject(async () => await peer.CallAsync("files/begin", new { session = "test", name = "huge", size = FileTransferStore.MaxFileBytes + 1, sha256 = new string('0', 64) }), "oversized upload rejected before allocation");
        var begin = await peer.CallAsync("files/begin", new { session = "test", name = "bad", size = 1, sha256 = new string('0', 64) });
        string transferId = begin!["id"]!.ToString();
        await Reject(async () => await peer.CallAsync("files/chunk", new { session = "test", id = transferId, offset = 1, data = "YQ==" }), "out-of-order chunk rejected");
        await peer.CallAsync("files/chunk", new { session = "test", id = transferId, offset = 0, data = "YQ==" });
        await Reject(async () => await peer.CallAsync("files/commit", new { session = "test", id = transferId }), "bad checksum cannot publish uploaded file");
        await peer.CallAsync("files/abort", new { session = "test", id = transferId });
        check(!(await client.ListAsync("test")).Files.Any(f => f.Name == "bad"), "aborted staging file stays invisible to Agent file list");
        string empty = Path.Combine(root, "empty.txt"); await File.WriteAllTextAsync(empty, "");
        var zero = await client.UploadAsync("test", empty); await client.DownloadAsync("test", zero.Id, Path.Combine(root, "empty-copy.txt"));
        check(zero.Size == 0, "empty file upload and download verify empty SHA256");
        await peer.CallAsync("attach", new { id = "other" });
        await Reject(async () => await client.DownloadAsync("other", uploaded.Id, Path.Combine(root, "cross-session.bin")), "file IDs do not cross session workspaces");
        await peer.CallAsync("attach", new { id = "test" });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Reject(async () => await client.UploadAsync("test", source, ct: cancelled.Token), "cancelled transfer terminates without publication");
        await remote.DisposeAsync(); await Task.Delay(200);
        await using var again = new RemoteDevice(false); await again.ConnectAsync(registry, host.Lease.Code);
        await again.Peer.CallAsync("attach", new { id = "test" });
        check((await new FileTransferClient(again.Peer).ListAsync("test")).Files.Any(f => f.Id == uploaded.Id), "completed files survive controller reconnection");
        // Own fixture files only; keep the bounded test directory on failure for diagnostics.
        await again.DisposeAsync(); await host.DisposeAsync(); Directory.Delete(root, true);
    }
    private sealed class InlineProgress(Action<FileProgress> action) : IProgress<FileProgress>
    { public void Report(FileProgress value) => action(value); }
    private sealed class FileAgent : IAgentAdapter
    {
        public string Text = "";
        public AgentInfo Info => new("fixture", "File fixture", true, "test");
        public event Action<SessionView>? Changed { add { } remove { } }
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(new SessionView(id, id, "", "idle", null, [], []));
        public Task SendAsync(string text, CancellationToken ct) { Text = text; return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
