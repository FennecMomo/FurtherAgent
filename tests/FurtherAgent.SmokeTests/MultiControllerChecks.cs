using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

internal static class MultiControllerChecks
{
    public static async Task RunAsync(RegistryClient registry, Action<bool, string> check)
    {
        var state = new State();
        string root = Path.Combine(Path.GetTempPath(), "fa-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var host = new DeviceHost(registry, () => new Adapter(state), Path.Combine(root, "files"));
        await host.StartAsync("multi-controller fixture");
        await using var a = new RemoteDevice();
        await using var b = new RemoteDevice(false);
        await using var c = new RemoteDevice(false);
        await Task.WhenAll(a.ConnectAsync(registry, host.Lease!.Code), b.ConnectAsync(registry, DeviceIdentity.DisplayCode(host.Lease.Code).ToLowerInvariant()), c.ConnectAsync(registry, host.Lease.Code));
        check(a.IsConnected && b.IsConnected && c.IsConnected, "three controllers pair concurrently, formatted short code accepted");
        var seenA = new ConcurrentQueue<SessionView>(); var seenB = new ConcurrentQueue<SessionView>();
        a.Notification += (method, node) => { if (method == "session") seenA.Enqueue(Json.Read<SessionView>(node)); };
        b.Notification += (method, node) => { if (method == "session") seenB.Enqueue(Json.Read<SessionView>(node)); };
        await a.Peer.CallAsync("attach", new { id = "one" });
        await b.Peer.CallAsync("attach", new { id = "one" });
        await c.Peer.CallAsync("attach", new { id = "two" });
        await Task.WhenAll(a.Peer.CallAsync("send", new { text = "desktop" }), b.Peer.CallAsync("send", new { text = "phone" }));
        await Until(() => seenA.Any(v => v.Messages.Count == 2) && seenB.Any(v => v.Messages.Count == 2));
        check(state.Messages["one"].Count == 2 && state.MaxConcurrent == 1, "same-session sends execute once each and serialize acceptance");
        check(seenA.Last().Messages.Select(m => m.Text).SequenceEqual(seenB.Last().Messages.Select(m => m.Text)), "both controllers receive identical ordered conversation updates");
        await c.Peer.CallAsync("send", new { text = "other-session" });
        check(state.Messages["two"].Single().Text == "other-session" && state.Messages["one"].Count == 2, "third controller selection cannot redirect another controller send");
        await b.Peer.CallAsync("attach", new { id = "two" });
        await Task.Delay(600); seenB.Clear();
        await a.Peer.CallAsync("send", new { text = "still-one" }); await Task.Delay(650);
        check(seenB.All(v => v.Id == "two"), "switching subscription excludes previous session updates");
        await b.Peer.CallAsync("attach", new { id = "one" });
        async Task<bool> Respond(RemoteDevice client)
        { try { await client.Peer.CallAsync("respond", new { id = "approve", decision = "accept" }); return true; } catch (IOException) { return false; } }
        var decisions = await Task.WhenAll(Respond(a), Respond(b));
        check(decisions.Count(x => x) == 1 && state.Decisions == 1, "simultaneous approvals reach Agent only once");
        await Until(() => seenA.Any(v => v.Prompts.Count == 0) && seenB.Any(v => v.Prompts.Count == 0));
        check(true, "approval removal broadcasts to both viewers");
        // Adapter deliberately returns stale approval; host must keep the consumed ID suppressed.
        var refreshed = Json.Read<SessionView>(await b.Peer.CallAsync("attach", new { id = "one" }));
        check(refreshed.Prompts.Count == 0 && !await Respond(b), "stale backend snapshot cannot resurrect consumed approval");
        await a.Peer.CallAsync("configure", new { model = "shared-model", provider = "fixture", mode = "plan" });
        await Until(() => seenB.Any(v => v.Model == "shared-model"));
        check(true, "model changes synchronize across controllers");
        await b.Peer.CallAsync("interrupt", null);
        await Until(() => seenA.Any(v => v.Status == "stopped"));
        check(state.Stops == 1, "either controller can stop the shared run");
        string sourceA = Path.Combine(root, "desktop.txt"), sourceB = Path.Combine(root, "phone.txt");
        await File.WriteAllTextAsync(sourceA, "desktop attachment"); await File.WriteAllTextAsync(sourceB, "phone attachment");
        var filesA = new FileTransferClient(a.Peer); var filesB = new FileTransferClient(b.Peer);
        var uploaded = await Task.WhenAll(filesA.UploadAsync("one", sourceA), filesB.UploadAsync("one", sourceB));
        var listed = await filesA.ListAsync("one");
        check(uploaded.All(f => listed.Files.Any(x => x.Id == f.Id)), "concurrent controller uploads share session files without corrupting upload state");
        string download = Path.Combine(root, "download.txt");
        await filesB.DownloadAsync("one", uploaded[0].Id, download);
        check(await File.ReadAllTextAsync(download) == "desktop attachment", "one controller can download the other controller attachment");
        await b.DisposeAsync();
        await a.Peer.CallAsync("send", new { text = "after-phone-left" });
        check(a.IsConnected && c.IsConnected && state.Disposed == 0, "disconnecting one controller preserves other connections and owned adapters");
        int sent = state.Messages["one"].Count;
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Reconnected += () => restored.TrySetResult(); await a.Peer.DisposeAsync();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(12));
        var recovered = Json.Read<SessionView>(await a.Peer.CallAsync("attach", new { id = "one" }));
        check(recovered.Messages.Count == sent && c.IsConnected, "reconnect preserves shared run state, never replays send or disrupts another controller");
        await host.DisposeAsync(); await Until(() => !a.IsConnected && !c.IsConnected);
        check(state.Disposed >= 3, "stopping sharing closes all controllers and disposes host-owned adapters");

        string secret = new('A', 64), shortCode = DeviceIdentity.Code(secret);
        var lease = await registry.PostAsync<DeviceLease>("devices", new DeviceRegistration("short identity", secret, 8));
        check(lease.Code == shortCode && shortCode.Length == 8, "stable identity derives eight-character code without changing secret");
        await using var control = await registry.OpenSocketAsync("host", default);
        await control.WriteAsync(Json.Node(new { code = lease.Code, secret }), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = await control.ReadAsync(timeout.Token);
        var legacy = await registry.PostAsync<PairResult>("pair", new PairRequest(DeviceIdentity.LegacyCode(secret)));
        check(legacy.Name == "short identity", "saved 32-character legacy code remains a pairing alias after upgrade");
        await registry.PostAsync<JsonNode>("unregister", new LeaseProof(lease.Code, secret));
    }
    private static async Task Until(Func<bool> predicate)
    { using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!predicate()) await Task.Delay(50, ct.Token); }
    private sealed class State
    {
        public ConcurrentDictionary<string, List<ChatEntry>> Messages = new();
        public int Decisions, Stops, Disposed, Active, MaxConcurrent;
    }
    private sealed class Adapter(State state) : IAgentAdapter
    {
        private string id = "", status = "running", model = "initial";
        public AgentInfo Info => new("fixture", "Shared fixture", true, "", true, true);
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted { add { } remove { } }
        private SessionView View() => new(id, id, "", status, "turn", state.Messages[id].ToList(), [new("approve", "test", "Approve", "", null)], Model: model);
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("one", "one", ""), new("two", "two", "")]);
        public Task<SessionView> AttachAsync(string value, CancellationToken ct) { id = value; state.Messages.TryAdd(id, []); return Task.FromResult(View()); }
        public async Task SendAsync(string text, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref state.Active); state.MaxConcurrent = Math.Max(state.MaxConcurrent, active);
            try { await Task.Delay(60, ct); state.Messages[id].Add(new("user", text)); Changed?.Invoke(View()); }
            finally { Interlocked.Decrement(ref state.Active); }
        }
        public Task InterruptAsync(CancellationToken ct) { state.Stops++; status = "stopped"; Changed?.Invoke(View()); return Task.CompletedTask; }
        public Task ConfigureAsync(string value, string provider, string mode, CancellationToken ct) { model = value; Changed?.Invoke(View()); return Task.CompletedTask; }
        public Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct) { Interlocked.Increment(ref state.Decisions); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref state.Disposed); return ValueTask.CompletedTask; }
    }
}
