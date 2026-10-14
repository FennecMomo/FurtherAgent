using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// Host-owned adapters survive controller disconnects. Each session has one run owner,
// while each controller has an independent selection and RPC response channel.
internal sealed class SharedAgentHub(Func<IAgentAdapter> factory) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> sessions = [];
    private readonly Lazy<IAgentAdapter> catalog = new(factory);
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private bool disposed;
    private event Action<SessionCompletion>? Completed;
    public async Task<IAgentAdapter> ConnectAsync(CancellationToken ct)
    {
        await Catalog(async a => { await Probe(a, ct); return true; }, ct);
        return new Controller(this);
    }
    private static async Task Probe(IAgentAdapter adapter, CancellationToken ct)
    {
        if (adapter is DshAdapter dsh) await dsh.ProbeAsync(ct);
        else if (adapter is HermesAdapter hermes) await hermes.ProbeAsync(ct);
    }
    private Entry Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("会话 ID 不能为空。");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessions.TryGetValue(id, out var entry)) return entry;
            // Never evict a running/uncertain adapter merely because a viewer leaves.
            if (sessions.Count >= 128) throw new IOException("接入端已保留 128 个会话，请在任务结束后重启接入端释放资源。");
            entry = new Entry(factory, id);
            entry.Completed += completion => Completed?.Invoke(completion);
            sessions.Add(id, entry); return entry;
        }
    }
    private async Task<T> Catalog<T>(Func<IAgentAdapter, Task<T>> action, CancellationToken ct)
    {
        await catalogGate.WaitAsync(ct);
        try { return await action(catalog.Value); } finally { catalogGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (gate) { if (disposed) return; disposed = true; entries = sessions.Values.ToArray(); }
        foreach (var entry in entries) await entry.Adapter.DisposeAsync();
        if (catalog.IsValueCreated) await catalog.Value.DisposeAsync();
    }
    private sealed class Entry
    {
        public IAgentAdapter Adapter { get; private set; } = null!;
        public SemaphoreSlim Operations { get; } = new(1, 1);
        public SessionView? View { get; private set; }
        public bool Initialized;
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted;
        public event Action<SessionCompletion>? Completed;
        public SessionCompletionTracker CompletionTracker { get; } = new();
        private readonly object stateGate = new();
        private readonly HashSet<string> answered = [];
        private readonly Func<IAgentAdapter> factory;
        private readonly string id;
        private volatile bool failed;
        private int observers;
        public void Observe(bool add)
        {
            lock (stateGate) { observers += add ? 1 : -1; Adapter.SetObserved(observers > 0); }
        }
        public Entry(Func<IAgentAdapter> factory, string id)
        {
            this.factory = factory; this.id = id; Open();
        }
        private void Open()
        {
            var adapter = Adapter = factory(); failed = false;
            adapter.SetObserved(observers > 0);
            adapter.Changed += view => { if (ReferenceEquals(Adapter, adapter) && view.Id == id) Publish(view); };
            adapter.Faulted += reason => { if (ReferenceEquals(Adapter, adapter)) { failed = true; Faulted?.Invoke(reason); } };
        }
        public async Task RecoverAsync()
        {
            // A broken desktop pipe cannot be reopened. Hermes run ownership and
            // DSH uncertain request IDs must remain on their existing adapters.
            if (!failed || Adapter is not CodexDesktopAdapter) return;
            await Adapter.DisposeAsync(); Open(); Initialized = false;
        }
        public SessionView Publish(SessionView view)
        {
            lock (stateGate)
            {
                View = view with { Prompts = view.Prompts.Where(p => !answered.Contains(p.Id)).ToList() };
                var completion = CompletionTracker.Observe(view);
                if (completion is not null) Completed?.Invoke(completion);
                Changed?.Invoke(View); return View;
            }
        }
        public void ReservePrompt(string id)
        {
            lock (stateGate)
            {
                if (View?.Prompts.Any(p => p.Id == id) != true || !answered.Add(id))
                    throw new IOException("此确认已处理、已失效或正在由另一端提交，请刷新会话。");
                Publish(View);
            }
        }
    }
    private sealed class Controller : IAgentAdapter
    {
        private readonly SharedAgentHub hub;
        public Controller(SharedAgentHub hub) { this.hub = hub; hub.Completed += OnCompleted; }
        public event Action<SessionCompletion>? Completed;
        private void OnCompleted(SessionCompletion completion) => Completed?.Invoke(completion);
        private Entry? selected;
        public AgentInfo Info => selected?.Adapter.Info ?? hub.catalog.Value.Info;
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted;
        private void OnChanged(SessionView view) => Changed?.Invoke(view);
        private void OnFaulted(string reason) => Faulted?.Invoke(reason);
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => hub.Catalog(a => a.ListAsync(ct), ct);
        public Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct) => hub.Catalog(a => a.CreateAsync(id, title, ct), ct);
        public Task<SessionInfo> CreateAsync(string id, string title, string cwd, CancellationToken ct) => hub.Catalog(a => a.CreateAsync(id, title, cwd, ct), ct);
        public Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken ct) => hub.Catalog(a => a.WorkspacesAsync(ct), ct);
        public Task<WorkspaceDirectories> BrowseDirectoriesAsync(string path, CancellationToken ct) => hub.Catalog(a => a.BrowseDirectoriesAsync(path, ct), ct);
        public Task<WorkspaceInfo> AddWorkspaceAsync(string id, string path, string folder, CancellationToken ct) => hub.Catalog(a => a.AddWorkspaceAsync(id, path, folder, ct), ct);
        public async Task<SessionView> AttachAsync(string id, CancellationToken ct)
        {
            var next = hub.Get(id);
            await next.Operations.WaitAsync(ct);
            try
            {
                await next.RecoverAsync();
                if (!next.Initialized) { await Probe(next.Adapter, ct); next.Initialized = true; }
                var view = next.Publish(await next.Adapter.AttachAsync(id, ct));
                if (!ReferenceEquals(selected, next))
                {
                    Unsubscribe(); selected = next;
                    next.Observe(true);
                    next.Changed += OnChanged; next.Faulted += OnFaulted;
                }
                return view;
            }
            finally { next.Operations.Release(); }
        }
        private async Task<T> Run<T>(Func<Entry, Task<T>> action, CancellationToken ct)
        {
            var entry = selected ?? throw new IOException("请先选择会话。");
            await entry.Operations.WaitAsync(ct);
            try { return await action(entry); } finally { entry.Operations.Release(); }
        }
        private Task Run(Func<Entry, Task> action, CancellationToken ct) => Run(async e => { await action(e); return true; }, ct);
        public Task<AgentSettings> SettingsAsync(CancellationToken ct) => Run(e => e.Adapter.SettingsAsync(ct), ct);
        public Task SetFileWorkspaceAsync(string outbox, CancellationToken ct) => Run(e => e.Adapter.SetFileWorkspaceAsync(outbox, ct), ct);
        public Task ConfigurePermissionsAsync(string permission, CancellationToken ct) => Run(e => e.Adapter.ConfigurePermissionsAsync(permission, ct), ct);
        public Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct) => Run(e => e.Adapter.ConfigureAsync(model, provider, mode, ct), ct);
        public Task ConfigureAsync(string model, string provider, string mode, string? reasoningEffort, CancellationToken ct) => Run(e => e.Adapter.ConfigureAsync(model, provider, mode, reasoningEffort, ct), ct);
        public Task ArchiveAsync(string id, bool archived, CancellationToken ct) => Run(e => e.Adapter.ArchiveAsync(id, archived, ct), ct);
        public Task SendAsync(string text, CancellationToken ct) => SendAsync(text, "auto", ct);
        public Task SendAsync(string text, string delivery, CancellationToken ct) => Run(e => e.Adapter.SendAsync(text, delivery, ct), ct);
        public Task InterruptAndSendAsync(string turnId, string text, CancellationToken ct) => Run(e =>
        { e.CompletionTracker.SuppressStoppedRun(); return e.Adapter.InterruptAndSendAsync(turnId, text, ct); }, ct);
        public Task InterruptAsync(CancellationToken ct) => Run(e =>
        { e.CompletionTracker.SuppressStoppedRun(); return e.Adapter.InterruptAsync(ct); }, ct);
        public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) => Run(async e =>
        {
            // Reserve before I/O: an uncertain acknowledgement must not cause double approval.
            e.ReservePrompt(id);
            await e.Adapter.RespondAsync(id, decision, response, ct);
        }, ct);
        private void Unsubscribe()
        {
            if (selected is not null) { selected.Changed -= OnChanged; selected.Faulted -= OnFaulted; selected.Observe(false); selected = null; }
        }
        public ValueTask DisposeAsync() { hub.Completed -= OnCompleted; Unsubscribe(); selected = null; return ValueTask.CompletedTask; }
    }
}
