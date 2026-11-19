using FurtherAgent.Core;
using FurtherAgent.Mobile;
using ConnectionProfile = FurtherAgent.Mobile.ConnectionProfile;

namespace FurtherAgent.AndroidApp;

internal static class SavedConnections
{
    private const string Key = "connection-profiles-v1";
    private static readonly ConnectionProfiles Store = new(() => SecureStorage.GetAsync(Key), json => SecureStorage.SetAsync(Key, json));
    private static readonly SemaphoreSlim migration = new(1, 1);
    public static async Task<ConnectionProfileState> LoadAsync()
    {
        await migration.WaitAsync();
        try
        {
            // Keep the original record until the encrypted replacement is successfully persisted.
            if (await SecureStorage.GetAsync(Key) is null && await SecureStorage.GetAsync("pairing-code") is { Length: > 0 } code)
            {
                string server = Preferences.Get("server-url", RelayEndpoint.LegacyAddress);
                if (RelayEndpoint.IsBuiltIn(server))
                {
                    await Store.SaveAsync("原有连接", server, code);
                    SecureStorage.Remove("pairing-code");
                }
            }
            return (await Store.LoadAsync()).ForBuiltInRelay();
        }
        finally { migration.Release(); }
    }

    // Keep previous profile IDs so drafts, preferences and encrypted mailbox bindings still open.
    public static ConnectionProfile? FindForSave(ConnectionProfileState saved, string code, string? replaceId) =>
        saved.Connections.FirstOrDefault(p => p.Id == replaceId)
        ?? saved.Connections.FirstOrDefault(p => p.Id == ConnectionProfiles.Identity(p.Server, code));

    public static Task<ConnectionProfile> SaveAsync(string name, string code, ConnectionProfile? existing, string? replaceId) =>
        Store.SaveAsync(name, existing?.Server ?? RelayEndpoint.Address, code, replaceId == existing?.Id ? replaceId : null);

    public static Task RemoveAsync(string id) => Store.RemoveAsync(id);
}
