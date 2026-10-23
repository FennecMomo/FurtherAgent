using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class CompletionChecks
{
    public static async Task MobileSmoke(Action<bool, string> check)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            string url = $"http://127.0.0.1:{port}";
            using var registry = new RegistryClient(url);
            for (int i = 0; ; i++)
            {
                try { await registry.CheckHealthAsync(default); break; }
                catch { if (i >= 40) throw; await Task.Delay(100); }
            }
            var adapters = new ConcurrentDictionary<string, Adapter>();
            await using var host = new DeviceHost(registry, () => new Adapter(adapters), enableLan: false);
            await host.StartAsync("mobile completion fixture");
            await using var mobile = new MobileSession(a => a());
            var received = new ConcurrentQueue<SessionCompletion>();
            mobile.Completed += received.Enqueue;
            await mobile.ConnectAsync(url, host.Lease!.Code);
            await mobile.SelectAsync("a");
            await adapters["a"].SendAsync("fixture", default);
            await mobile.SelectAsync("b");
            adapters["a"].Emit("idle");
            await Until(() => received.Count == 1);
            check(received.Single().SessionId == "a" && mobile.View?.Id == "b",
                "mobile delivers background session completion without switching or mixing the current view");
            adapters["a"].Emit("idle"); adapters["b"].Emit("idle");
            await Task.Delay(250);
            check(received.Count == 1, "duplicate terminal snapshots and old idle history do not alert again");
            await adapters["b"].SendAsync("fixture", default);
            adapters["b"].Emit("idle");
            await Until(() => received.Count == 2);
            check(received.Last().SessionId == "b", "mobile exposes selected-session completion for native foreground/background policy");
            await mobile.DisconnectAsync();
            await adapters["b"].SendAsync("fixture", default); adapters["b"].Emit("idle");
            await Task.Delay(150);
            check(received.Count == 2, "disconnected mobile does not deliver stale completion events");
        }
        finally { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } }
    }

    public static void States(Action<bool, string> check)
    {
        SessionView View(string status, string? turn = "one") => new("a", "任务 A", "", status, turn, [], []);
        var tracker = new SessionCompletionTracker();
        check(tracker.Observe(View("idle")) is null, "opening old completed history does not notify");
        tracker.Observe(View("active"));
        check(tracker.Observe(View("unknown")) is null && tracker.Observe(View("waiting_for_approval")) is null,
            "unknown state and approval waiting are not completion");
        check(tracker.Observe(View("idle") with { Prompts = [new("p", "question", "", "", null)] }) is null,
            "an unanswered prompt prevents completion notification");
        var first = tracker.Observe(View("idle", null));
        check(first is { SessionId: "a", TurnId: "one" } && tracker.Observe(View("idle")) is null, "running to idle emits once and retains active turn identity");
        tracker.Observe(View("active"));
        check(tracker.Observe(View("idle")) is null, "replayed active and terminal snapshots cannot duplicate a completed turn");
        tracker.Observe(View("running", "two"));
        check(tracker.Observe(View("completed", "two")) is { } second && second.EventId != first!.EventId, "a subsequent turn gets a separate completion event");
        foreach (string status in new[] { "failed", "error", "cancelled", "stopped", "interrupted" })
        {
            tracker.Observe(View("active", status));
            check(tracker.Observe(View(status, status)) is null && tracker.Observe(View("idle", status)) is null, status + " does not become successful completion");
        }
        tracker.Observe(View("active", "manual")); tracker.SuppressStoppedRun();
        check(tracker.Observe(View("idle", null)) is null, "manual interrupt returning idle does not notify success");
        tracker.Observe(View("active", "manual"));
        check(tracker.Observe(View("idle", null)) is null, "replaying a manually stopped turn cannot report success");
        tracker.Observe(View("active", "after-stop"));
        check(tracker.Observe(View("idle", null)) is not null, "a new run after an interrupt can notify");
        var noIds = new SessionCompletionTracker();
        noIds.Observe(View("running", null)); var one = noIds.Observe(View("idle", null));
        noIds.Observe(View("running", null)); var two = noIds.Observe(View("idle", null));
        check(one is not null && two is not null && one.EventId != two.EventId, "adapters without turn IDs still support distinct running cycles");
        noIds.SuppressStoppedRun(); noIds.Observe(View("active", "new"));
        check(noIds.Observe(View("idle", null)) is not null, "a stale stop request while idle cannot suppress the next run");
    }

    public static async Task Relay(RegistryClient registry, Action<bool, string> check)
    {
        var adapters = new ConcurrentDictionary<string, Adapter>();
        string root = Path.Combine(Path.GetTempPath(), "fa-completion-" + Guid.NewGuid().ToString("N"));
        await using var host = new DeviceHost(registry, () => new Adapter(adapters), root);
        try
        {
            await host.StartAsync("completion fixture");
            await using var remote = new RemoteDevice(); await remote.ConnectAsync(registry, host.Lease!.Code);
            var received = new ConcurrentQueue<SessionCompletion>(); var views = new ConcurrentQueue<string>();
            remote.Notification += (method, node) =>
            {
                if (method == "session-completed") received.Enqueue(Json.Read<SessionCompletion>(node));
                if (method == "session") views.Enqueue(Json.Read<SessionView>(node).Id);
            };
            await remote.Peer.CallAsync("attach", new { id = "a" });
            await remote.Peer.CallAsync("send", new { text = "start a" });
            await remote.Peer.CallAsync("attach", new { id = "b" });
            adapters["a"].Emit("idle");
            await Until(() => received.Count == 1);
            check(received.Single().SessionId == "a", "real relay delivers completion after switching away from the running session");
            adapters["a"].Emit("idle"); adapters["b"].Emit("idle");
            await Task.Delay(650);
            check(received.Count == 1 && views.Last() == "b", "completion is independent of selected snapshots and old idle history does not notify");
            await remote.Peer.CallAsync("send", new { text = "start b" });
            await remote.Peer.CallAsync("interrupt", null);
            await Task.Delay(150);
            check(received.Count == 1, "host suppresses manual stop even when the adapter reports idle");
            await remote.Peer.CallAsync("send", new { text = "new b" }); adapters["b"].Emit("idle");
            await Until(() => received.Count == 2);
            check(received.Last().SessionId == "b", "selected session completion reaches the same notification transport");
        }
        finally { await host.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(50, timeout.Token);
    }
    private sealed class Adapter(ConcurrentDictionary<string, Adapter> adapters) : IAgentAdapter
    {
        private string id = "", status = "idle";
        private int run;
        public AgentInfo Info => new("fixture", "completion", true, "isolated");
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted { add { } remove { } }
        private SessionView View => new(id, "任务 " + id, "", status, "turn-" + run, [], []);
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([]);
        public Task<SessionView> AttachAsync(string value, CancellationToken ct) { id = value; adapters[id] = this; return Task.FromResult(View); }
        public void Emit(string value) { status = value; Changed?.Invoke(View); }
        public Task SendAsync(string text, CancellationToken ct) { run++; Emit("active"); return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct) { Emit("idle"); return Task.CompletedTask; }
        public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
