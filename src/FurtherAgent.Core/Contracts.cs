using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, Options)!;
    public static T Read<T>(JsonNode? value) => value!.Deserialize<T>(Options)!;
}

public record DeviceRegistration(string Name);
public record DeviceLease(string Code, string Secret, DateTimeOffset ExpiresAt);
public record LeaseProof(string Code, string Secret);
public record PairRequest(string Code);
public record PairResult(string Name, string Ticket);
public record AgentInfo(string Id, string Name, bool Available, string Detail, bool CanCreate = false, bool CanConfigure = false, bool CanFiles = false, bool CanArchive = false);
public record SessionInfo(string Id, string Title, string UpdatedAt)
{
    public string DisplayTime => double.TryParse(UpdatedAt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var epoch) && epoch > 0 && epoch < 253402300799
        ? DateTimeOffset.FromUnixTimeSeconds((long)epoch).ToLocalTime().ToString("MM-dd HH:mm")
        : DateTimeOffset.TryParse(UpdatedAt, out var date) ? date.ToLocalTime().ToString("MM-dd HH:mm") : UpdatedAt;
}
public record ModelOption(string Id, string Provider, string Label);
public record AgentSettings(string Model, string Provider, string Mode, List<ModelOption> Models, bool CanChangeModel, bool CanChangeMode, string Detail);
public record ChatEntry(string Role, string Text, string? Detail = null);
public record PendingPrompt(string Id, string Kind, string Title, string Detail, JsonNode? Payload);
public record SessionView(string Id, string Title, string Cwd, string Status, string? TurnId,
    List<ChatEntry> Messages, List<PendingPrompt> Prompts,
    string? StatusDetail = null, bool CanSend = true, bool? CanStop = null, string? Model = null, string? Mode = null);

public interface IAgentAdapter : IAsyncDisposable
{
    AgentInfo Info { get; }
    event Action<SessionView>? Changed;
    event Action<string>? Faulted;
    Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct);
    Task<SessionView> AttachAsync(string id, CancellationToken ct);
    Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct) => throw new NotSupportedException("此适配器暂不支持远程新建会话，请先在远端创建后刷新。");
    Task<AgentSettings> SettingsAsync(CancellationToken ct) => throw new NotSupportedException("请升级远端接入端以使用会话设置。");
    Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct) => throw new NotSupportedException("此适配器不支持修改会话设置。");
    Task ArchiveAsync(string id, bool archived, CancellationToken ct) => throw new NotSupportedException("此适配器未接通原生归档。");
    Task SendAsync(string text, CancellationToken ct);
    Task InterruptAsync(CancellationToken ct);
    Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct);
}
