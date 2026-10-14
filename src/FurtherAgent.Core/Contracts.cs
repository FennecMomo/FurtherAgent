using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, Options)!;
    public static T Read<T>(JsonNode? value) => value!.Deserialize<T>(Options)!;
}

public record DeviceRegistration(string Name, string? IdentitySecret = null, int MaxControllers = 1);
public record DeviceLease(string Code, string Secret, DateTimeOffset ExpiresAt);
public record LeaseProof(string Code, string Secret);
public record PairRequest(string Code);
public record PairResult(string Name, string Ticket);
public record AgentInfo(string Id, string Name, bool Available, string Detail, bool CanCreate = false, bool CanConfigure = false, bool CanFiles = false, bool CanArchive = false, bool CanChooseDelivery = false, bool CanInterruptSend = false, bool CanThumbnails = false, bool CanWorkspaces = false);
public record WorkspaceInfo(string Id, string Name, string Path)
{
    public string DisplayName => $"{Name} · {Path}";
}
public record WorkspaceDirectory(string Name, string Path);
public record WorkspaceDirectories(string Path, string? Parent, IReadOnlyList<WorkspaceDirectory> Directories);
public record SessionInfo(string Id, string Title, string UpdatedAt, string Cwd = "")
{
    public DateTimeOffset SortTime => ParseTime(UpdatedAt);
    public string Workspace => string.IsNullOrWhiteSpace(Cwd) ? "工作区未知" : Cwd;
    public static DateTimeOffset ParseTime(string text)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n))
        {
            if (n > 253402300799) n /= 1000;
            if (n >= 0 && n < 253402300799) return DateTimeOffset.FromUnixTimeMilliseconds((long)(n * 1000));
        }
        return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : DateTimeOffset.MinValue;
    }
    public string DisplayTime => SortTime == DateTimeOffset.MinValue ? UpdatedAt : SortTime.ToLocalTime().ToString("MM-dd HH:mm");
}
public record ModelOption(string Id, string Provider, string Label, List<string>? ReasoningEfforts = null, string? DefaultReasoningEffort = null);
public record AgentSettings(string Model, string Provider, string Mode, List<ModelOption> Models, bool CanChangeModel, bool CanChangeMode, string Detail,
    string? ReasoningEffort = null, bool CanChangeReasoning = false, string? Usage = null, bool RequireModelSelection = false, string? Permission = null, bool CanChangePermissions = false);
public record ChatEntry(string Role, string Text, string? Detail = null);
public record PendingPrompt(string Id, string Kind, string Title, string Detail, JsonNode? Payload);
public record SessionView(string Id, string Title, string Cwd, string Status, string? TurnId,
    List<ChatEntry> Messages, List<PendingPrompt> Prompts,
    string? StatusDetail = null, bool CanSend = true, bool? CanStop = null, string? Model = null, string? Mode = null, int? PendingMessages = null,
    List<RemoteFile>? OutputFiles = null, HistoryWindow? History = null);

public record HistoryWindow(string Revision, int Start, int Total);
public record HistoryPage(string Session, string Revision, int Start, int End, int Total, List<ChatEntry> Messages);

public interface IAgentAdapter : IAsyncDisposable
{
    AgentInfo Info { get; }
    event Action<SessionView>? Changed;
    event Action<string>? Faulted;
    event Action<SessionCompletion>? Completed { add { } remove { } }
    Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct);
    Task<SessionView> AttachAsync(string id, CancellationToken ct);
    Task SetFileWorkspaceAsync(string outbox, CancellationToken ct) => Task.CompletedTask;
    void SetObserved(bool observed) { }
    Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct) => throw new NotSupportedException("此适配器暂不支持远程新建会话，请先在远端创建后刷新。");
    Task<SessionInfo> CreateAsync(string id, string title, string cwd, CancellationToken ct) =>
        string.IsNullOrEmpty(cwd) ? CreateAsync(id, title, ct) : throw new NotSupportedException("此适配器不支持指定工作空间。");
    Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken ct) => throw new NotSupportedException("请升级 Codex 接入端以管理工作空间。");
    Task<WorkspaceDirectories> BrowseDirectoriesAsync(string path, CancellationToken ct) => throw new NotSupportedException("此适配器不支持浏览远端目录。");
    Task<WorkspaceInfo> AddWorkspaceAsync(string id, string path, string folder, CancellationToken ct) => throw new NotSupportedException("此适配器不支持新增工作空间。");
    Task<AgentSettings> SettingsAsync(CancellationToken ct) => throw new NotSupportedException("请升级远端接入端以使用会话设置。");
    Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct) => throw new NotSupportedException("此适配器不支持修改会话设置。");
    Task ConfigureAsync(string model, string provider, string mode, string? reasoningEffort, CancellationToken ct) =>
        string.IsNullOrEmpty(reasoningEffort) ? ConfigureAsync(model, provider, mode, ct) : throw new NotSupportedException("请升级接入端以配置思考强度。");
    Task ConfigurePermissionsAsync(string permission, CancellationToken ct) => throw new NotSupportedException("请升级 Codex 接入端以配置会话权限。");
    Task ArchiveAsync(string id, bool archived, CancellationToken ct) => throw new NotSupportedException("此适配器未接通原生归档。");
    Task SendAsync(string text, CancellationToken ct);
    Task SendAsync(string text, string delivery, CancellationToken ct) => delivery == "auto" ? SendAsync(text, ct) : throw new NotSupportedException("此适配器未开放发送方式选择。");
    Task InterruptAndSendAsync(string turnId, string text, CancellationToken ct) => throw new NotSupportedException("此适配器未开放原生打断后发送。");
    Task InterruptAsync(CancellationToken ct);
    Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct);
}
