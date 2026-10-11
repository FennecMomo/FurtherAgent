using FurtherAgent.Core;
using FurtherAgent.Mobile;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class PagingChecks
{
    public static async Task RunLive(string url, string codeFile, string id, Action<bool, string> check)
    {
        await using var mobile = new MobileSession(a => a());
        await mobile.ConnectAsync(url, (await File.ReadAllTextAsync(codeFile)).Trim());
        await mobile.SelectAsync(id);
        check(mobile.View?.Id == id && mobile.View.Messages.Count <= 8 && mobile.HasOlder, "live relay returns only target's latest page");
        int start = mobile.View!.History!.Start;
        await mobile.LoadOlderAsync();
        check(mobile.View.History!.Start < start && mobile.View.Messages.Count <= 16, "live relay serves earlier page through actual mobile controller");
        Console.WriteLine($"LIVE MOBILE: {mobile.View.Messages.Count} entries; workspace={mobile.View.Cwd}; no messages sent.");
    }
    public static async Task RunAsync(RegistryClient registry, string url, Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "furtheragent-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new HistoryCache(root);
            var adapter = new Adapter();
            await using var host = new DeviceHost(registry, () => adapter);
            await host.StartAsync("Paged history fixture");
            await using var mobile = new MobileSession(a => a(), cache);
            await mobile.ConnectAsync(url, host.Lease!.Code);
            check(mobile.Sessions[0].Id == "two" && mobile.Sessions[1].Cwd == "D:/MaidTown", "mixed ISO and epoch timestamps sort newest first with workspace");
            await mobile.SelectAsync("one");
            check(mobile.View!.Messages.Count == 8 && mobile.View.Messages[0].Text == "record 92", "mobile attaches only newest eight records");
            await mobile.LoadOlderAsync();
            check(mobile.View.Messages.Count == 16 && mobile.View.Messages[0].Text == "record 84", "upward pagination prepends next eight records in order");
            for (int i = 0; i < 4; i++) await mobile.LoadOlderAsync();
            check(mobile.View.Messages.Count == 32 && mobile.View.History!.Start == 52, "long scrolling keeps a bounded 32-record render window");
            mobile.ShowLatest();
            await mobile.SelectAsync("one");
            var pageFiles = Directory.GetFiles(root, "*.json");
            check(pageFiles.Length >= 5, "loaded history persists in local page cache");
            var h = mobile.View!.History!;
            string scope = url.TrimEnd('/').ToLowerInvariant() + "\n" + host.Lease!.Code;
            check(await new HistoryCache(root).ReadPageAsync(scope, "one", h.Revision, h.Start) is not null,
                "fresh cache instance reuses stored page without downloading its body");
            check(await cache.ReadPageAsync(scope + "other-device", "one", h.Revision, h.Start) is null, "cache isolates paired devices");
            await mobile.LoadOlderAsync();
            adapter.Rewrite();
            for (int i = 0; i < 30; i++) { await Task.Delay(100); if (mobile.View?.Title == "rewritten") break; }
            await mobile.LoadOlderAsync();
            check(!mobile.ReadingOlder && mobile.View!.Messages.Count == 8 && mobile.View.Messages[^1].Text == "new 11",
                "compaction-like rewrite invalidates old cursor without mixing histories");
            await mobile.ClearCacheAsync();
            check(Directory.GetFiles(root, "*.json").Length == 0 && mobile.View is not null, "clear cache preserves active conversation");
            string html = Transcript.Render(new("large", "", "", "idle", null,
                Enumerable.Range(0, 100).Select(_ => new ChatEntry("系统", new string('[', 100000), new string('x', 100000))).ToList(), []));
            check(html.Length < 400000 && !html.Contains("<script>"), "huge compaction/tool data renders as bounded plain text");
            var compact = CodexDesktopAdapter.ToView(JsonNode.Parse("""{"turns":[{"items":[{"type":"contextCompaction","summary":"internal compressed context"}]}]}""")!);
            check(compact.Messages.Single().Text.Contains("上下文已压缩") && compact.Messages[0].Detail is null, "compaction becomes a small status entry without injecting internal payload");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Adapter : IAgentAdapter
    {
        private SessionView view = new("one", "paged", "D:/MaidTown", "idle", null, Enumerable.Range(0, 100).Select(i => new ChatEntry("assistant", "record " + i)).ToList(), []);
        public AgentInfo Info => new("fixture", "Fixture", true, "");
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted { add { } remove { } }
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([
            new("one", "one", "2026-10-01T00:00:00Z", "D:/MaidTown"), new("two", "two", "1791763200000", "D:/Other")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(view);
        public void Rewrite() { view = view with { Title = "rewritten", Messages = Enumerable.Range(0, 12).Select(i => new ChatEntry("assistant", "new " + i)).ToList() }; Changed?.Invoke(view); }
        public Task SendAsync(string text, CancellationToken ct) => throw new NotSupportedException();
        public Task InterruptAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
