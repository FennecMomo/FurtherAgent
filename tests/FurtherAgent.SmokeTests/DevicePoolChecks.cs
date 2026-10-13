using System.Text.Json.Nodes;
using FurtherAgent.Core;
using FurtherAgent.Mobile;

internal static class DevicePoolChecks
{
    public static async Task RunAsync(RegistryClient registry, string url, Action<bool, string> check)
    {
        var a = new Adapter("A"); var b = new Adapter("B");
        string root = Path.Combine(Path.GetTempPath(), "fa-device-pool-" + Guid.NewGuid().ToString("N"));
        await using var hostA = new DeviceHost(registry, () => new BorrowedAgentAdapter(a), Path.Combine(root, "a"));
        await using var hostB = new DeviceHost(registry, () => new BorrowedAgentAdapter(b), Path.Combine(root, "b"));
        await hostA.StartAsync("device A"); await hostB.StartAsync("device B");
        await using var pool = new DeviceConnectionPool<MobileSession>();
        int opened = 0;
        Task<MobileSession> Open(string code) => pool.OpenAsync(url, code, async () =>
        {
            opened++; var session = new MobileSession(action => action());
            try { await session.ConnectAsync(url, code); return session; }
            catch { await session.DisposeAsync(); throw; }
        });
        try
        {
            var first = await Open(hostA.Lease!.Code); await first.SelectAsync("same-id");
            var second = await Open(hostB.Lease!.Code); await second.SelectAsync("same-id");
            check(first.Connected && second.Connected && pool.Entries.Count == 2, "one client pool keeps two devices connected through the real relay");
            var same = await Open(DeviceIdentity.DisplayCode(hostA.Lease.Code).ToLowerInvariant());
            check(ReferenceEquals(first, same) && opened == 2 && first.View!.Messages[0].Text == "A",
                "switching back reuses the exact transport and selected session without pairing again");
            a.Emit("A background update"); b.Emit("B foreground update");
            await Until(() => first.View!.Messages[0].Text == "A background update" && second.View!.Messages[0].Text == "B foreground update");
            check(true, "background device continues receiving updates without crossing identical session IDs");
            a.FailAfterAccept = true;
            try { await first.SendAsync("uncertain A", []); } catch (IOException) { }
            await second.SendAsync("only B", []);
            check(first.SendUncertain && !second.SendUncertain && a.Sends == 1 && b.Sends == 1,
                "uncertain sends and subsequent commands are isolated per device");
            await Task.WhenAll(first.ResumeAsync(), second.ResumeAsync());
            check(a.Sends == 1 && b.Sends == 1 && first.SendUncertain, "resuming every connection never replays a submission");
            bool rejected = false;
            try { await pool.OpenAsync(url, "bad-code", () => Task.FromException<MobileSession>(new IOException("fixture failed"))); }
            catch (IOException) { rejected = true; }
            check(rejected && pool.Entries.Count == 2 && first.Connected && second.Connected,
                "failed device addition preserves all established connections");
            var restored = await Task.WhenAll(Open(hostA.Lease.Code), Open(hostA.Lease.Code));
            check(restored.All(s => ReferenceEquals(s, first)) && opened == 2, "concurrent requests for the same device cannot create duplicate controllers");
            await pool.CloseAsync(DeviceConnectionPool<MobileSession>.Identity(url, hostA.Lease.Code));
            check(!first.HasConnection && second.Connected && pool.Entries.Count == 1 && a.Stops == 0 && b.Stops == 0,
                "manual device disconnect closes only its connection and never stops remote tasks");
            await second.SendAsync("B still usable", []);
            check(b.Sends == 2, "remaining device stays usable after another disconnects");
            await pool.DisposeAsync();
            check(!second.HasConnection && pool.Entries.Count == 0 && b.Stops == 0,
                "application shutdown closes every controller without submitting stop commands");
            bool disposed = false;
            try { await Open(hostA.Lease.Code); } catch (ObjectDisposedException) { disposed = true; }
            check(disposed, "connection pool cannot resurrect connections after application shutdown");
        }
        finally
        {
            await hostA.DisposeAsync(); await hostB.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(100, timeout.Token);
    }
    private sealed class Adapter : IAgentAdapter
    {
        private readonly string name;
        private string text;
        public Adapter(string name) { this.name = name; text = name; }
        public int Sends, Stops;
        public bool FailAfterAccept;
        public AgentInfo Info => new("pool-fixture", name, true, "isolated test");
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted { add { } remove { } }
        private SessionView View => new("same-id", name, name, "active", "run", [new("assistant", text)], [], CanStop: true);
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("same-id", name, "")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) => Task.FromResult(View);
        public void Emit(string value) { text = value; Changed?.Invoke(View); }
        public Task SendAsync(string value, CancellationToken ct) { Sends++; Emit(value); if (FailAfterAccept) throw new IOException("accepted without acknowledgement"); return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct) { Stops++; return Task.CompletedTask; }
        public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
