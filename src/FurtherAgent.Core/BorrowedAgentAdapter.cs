using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// A controller connection borrows the host-owned adapter; losing the relay must not close Hermes SSE.
public sealed class BorrowedAgentAdapter : IAgentAdapter
{
    private readonly IAgentAdapter inner;
    public BorrowedAgentAdapter(IAgentAdapter inner)
    { this.inner = inner; inner.Changed += OnChanged; inner.Faulted += OnFaulted; }
    public AgentInfo Info => inner.Info;
    public event Action<SessionView>? Changed;
    public event Action<string>? Faulted;
    private void OnChanged(SessionView view) => Changed?.Invoke(view);
    private void OnFaulted(string message) => Faulted?.Invoke(message);
    public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
    public Task<SessionView> AttachAsync(string id, CancellationToken ct) => inner.AttachAsync(id, ct);
    public Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct) => inner.CreateAsync(id, title, ct);
    public Task<SessionInfo> CreateAsync(string id, string title, string cwd, CancellationToken ct) => inner.CreateAsync(id, title, cwd, ct);
    public Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken ct) => inner.WorkspacesAsync(ct);
    public Task<WorkspaceDirectories> BrowseDirectoriesAsync(string path, CancellationToken ct) => inner.BrowseDirectoriesAsync(path, ct);
    public Task<WorkspaceInfo> AddWorkspaceAsync(string id, string path, string folder, CancellationToken ct) => inner.AddWorkspaceAsync(id, path, folder, ct);
    public Task<AgentSettings> SettingsAsync(CancellationToken ct) => inner.SettingsAsync(ct);
    public Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct) => inner.ConfigureAsync(model, provider, mode, ct);
    public Task ConfigureAsync(string model, string provider, string mode, string? reasoningEffort, CancellationToken ct) => inner.ConfigureAsync(model, provider, mode, reasoningEffort, ct);
    public Task ArchiveAsync(string id, bool archived, CancellationToken ct) => inner.ArchiveAsync(id, archived, ct);
    public Task SendAsync(string text, CancellationToken ct) => inner.SendAsync(text, ct);
    public Task SendAsync(string text, string delivery, CancellationToken ct) => inner.SendAsync(text, delivery, ct);
    public Task InterruptAndSendAsync(string turnId, string text, CancellationToken ct) => inner.InterruptAndSendAsync(turnId, text, ct);
    public Task InterruptAsync(CancellationToken ct) => inner.InterruptAsync(ct);
    public Task RespondAsync(string id, string decision, JsonNode? response, CancellationToken ct) => inner.RespondAsync(id, decision, response, ct);
    public ValueTask DisposeAsync()
    { inner.Changed -= OnChanged; inner.Faulted -= OnFaulted; return ValueTask.CompletedTask; }
}
