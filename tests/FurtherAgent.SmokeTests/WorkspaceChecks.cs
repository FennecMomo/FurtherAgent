using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class WorkspaceChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "fa-workspaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var metadata = new Metadata();
            var service = new CodexWorkspaces(root, _ => Task.FromResult<ICodexMetadata>(metadata));
            var added = await service.AddAsync(Guid.NewGuid().ToString("N"), root, "中文 space", default);
            check(Directory.Exists(added.Path) && metadata.Projects.Count == 1, "remote Unicode folder is registered as native project");
            var existing = await service.AddAsync(Guid.NewGuid().ToString("N"), added.Path, "", default);
            check(existing == added && metadata.Projects.Count == 1, "adding existing remote path reuses native workspace");
            var listing = CodexWorkspaces.Browse(root, default);
            check(listing.Directories.Any(d => d.Path == added.Path) && listing.Parent == Directory.GetParent(root)!.FullName, "directory browse returns children and parent paths");
            foreach (var invalid in new[] { "../escape", "..", "a/b", "a\\b", ".", "bad." })
            {
                bool rejected = false;
                try { CodexWorkspaces.ChildPath(root, invalid); } catch (ArgumentException) { rejected = true; }
                check(rejected, "new folder rejects traversal or invalid component: " + invalid);
            }
            string id = Guid.NewGuid().ToString("N");
            var first = await service.CreateAsync(id, "空会话", added.Path, default);
            var recovered = await new CodexWorkspaces(root, _ => Task.FromResult<ICodexMetadata>(metadata)).CreateAsync(id, "空会话", added.Path, default);
            check(first.Id == recovered.Id && first.Cwd == added.Path && metadata.Starts == 1, "retry after restart reuses persisted thread ID and cwd");
            check(metadata.LastStart?["projectId"]?.ToString() == added.Id && metadata.LastStart?["ephemeral"]?.GetValue<bool>() == false && metadata.Turns == 0, "empty thread persists project membership without starting a turn");
            bool rejectedChange = false;
            try { await service.CreateAsync(id, "changed", root, default); } catch (IOException) { rejectedChange = true; }
            check(rejectedChange && metadata.Starts == 1, "request ID rejects changed title or workspace");
            metadata.LoseStartReply = true; string unknown = Guid.NewGuid().ToString("N");
            try { await service.CreateAsync(unknown, "unknown", added.Path, default); } catch (IOException) { }
            metadata.LoseStartReply = false; bool unknownRejected = false;
            try { await service.CreateAsync(unknown, "unknown", added.Path, default); } catch (IOException ex) { unknownRejected = ex.Message.Contains("结果未知"); }
            check(unknownRejected && metadata.Starts == 2, "uncertain creation is never replayed automatically");
            metadata.FailNaming = true; string interrupted = Guid.NewGuid().ToString("N");
            try { await service.CreateAsync(interrupted, "recover", added.Path, default); } catch (IOException) { }
            metadata.FailNaming = false;
            await service.CreateAsync(interrupted, "recover", added.Path, default);
            check(metadata.Starts == 3, "failure after creation recovers naming without duplicate thread");
            var other = new Metadata { FailListing = true }; bool failed = false;
            try { await new CodexWorkspaces(root, _ => Task.FromResult<ICodexMetadata>(other)).AddAsync(Guid.NewGuid().ToString("N"), root, "should-not-exist", default); } catch (IOException) { failed = true; }
            check(failed && !Directory.Exists(Path.Combine(root, "should-not-exist")), "unsupported Codex does not create orphan directory");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Metadata : ICodexMetadata
    {
        internal List<JsonNode> Projects = [];
        internal Dictionary<string, JsonNode> Threads = [];
        internal int Starts, Turns;
        internal bool LoseStartReply, FailNaming, FailListing;
        internal JsonNode? LastStart;
        public Task<JsonNode> CallAsync(string method, object args, CancellationToken ct)
        {
            var a = Json.Node(args);
            if (method == "project/list")
            {
                if (FailListing) throw new IOException("unsupported");
                return Task.FromResult(Json.Node(new { data = Projects, nextCursor = (string?)null }));
            }
            if (method == "project/create")
            {
                var project = Json.Node(new { id = Guid.NewGuid().ToString(), name = a["name"]!.ToString(), roots = a["roots"]!.DeepClone() });
                Projects.Add(project); return Task.FromResult(Json.Node(new { project }));
            }
            if (method == "thread/start")
            {
                Starts++; LastStart = a;
                var thread = Json.Node(new { id = Guid.NewGuid().ToString(), cwd = a["cwd"]!.ToString(), updatedAt = 1791768000 });
                Threads[thread["id"]!.ToString()] = thread;
                if (LoseStartReply) throw new IOException("lost reply");
                return Task.FromResult(Json.Node(new { thread }));
            }
            if (method == "thread/name/set")
            { if (FailNaming) throw new IOException("naming failed"); return Task.FromResult(Json.Node(new { })); }
            if (method == "thread/read") return Task.FromResult(Json.Node(new { thread = Threads[a["threadId"]!.ToString()] }));
            if (method == "turn/start") Turns++;
            throw new InvalidOperationException(method);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
