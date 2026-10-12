using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace FurtherAgent.Mobile;

public sealed record ConnectionProfile(string Id, string Name, string Server, string Code);
public sealed record ConnectionProfileState(List<ConnectionProfile> Connections, string? SelectedId);

// The platform supplies encrypted storage. A single document makes updates and migration atomic.
public sealed class ConnectionProfiles(Func<Task<string?>> read, Func<string, Task> write)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<ConnectionProfileState> LoadAsync() =>
        JsonSerializer.Deserialize<ConnectionProfileState>(await read() ?? "null") ?? new([], null);

    public static string Identity(string server, string code) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(new Uri(server.Trim().TrimEnd('/') + '/').AbsoluteUri + "\n" + code.Replace("-", "").Replace(" ", "").ToUpperInvariant())));

    public async Task<ConnectionProfile> SaveAsync(string name, string server, string code, string? replaceId = null)
    {
        server = new Uri(server.Trim().TrimEnd('/') + '/').AbsoluteUri;
        using var validation = new FurtherAgent.Core.RegistryClient(server);
        code = code.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("请输入设备授权码。");
        await gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            string id = Identity(server, code);
            var old = state.Connections.FirstOrDefault(p => p.Id == id);
            var profile = new ConnectionProfile(id, string.IsNullOrWhiteSpace(name) ? old?.Name ?? new Uri(server).Host : name.Trim(), server, code);
            state.Connections.RemoveAll(p => p.Id == id || p.Id == replaceId);
            state.Connections.Add(profile);
            await write(JsonSerializer.Serialize(state with { SelectedId = id }));
            return profile;
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            var state = await LoadAsync();
            state.Connections.RemoveAll(p => p.Id == id);
            await write(JsonSerializer.Serialize(state with { SelectedId = state.SelectedId == id ? null : state.SelectedId }));
        }
        finally { gate.Release(); }
    }
}
