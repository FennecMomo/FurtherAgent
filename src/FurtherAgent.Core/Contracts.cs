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
public record AgentInfo(string Id, string Name, bool Available, string Detail);
public record SessionInfo(string Id, string Title, string UpdatedAt);
public record ChatEntry(string Role, string Text);
public record PendingPrompt(string Id, string Kind, string Title, string Detail, JsonNode? Payload);
public record SessionView(string Id, string Title, string Cwd, string Status, string? TurnId,
    List<ChatEntry> Messages, List<PendingPrompt> Prompts);

public interface IAgentAdapter : IAsyncDisposable
{
    AgentInfo Info { get; }
    event Action<SessionView>? Changed;
    event Action<string>? Faulted;
    Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct);
    Task<SessionView> AttachAsync(string id, CancellationToken ct);
    Task SendAsync(string text, CancellationToken ct);
    Task InterruptAsync(CancellationToken ct);
    Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct);
}
