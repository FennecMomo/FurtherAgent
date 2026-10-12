using FurtherAgent.Mobile;

namespace FurtherAgent.AndroidApp;

internal static class SavedConnections
{
    private const string Key = "connection-profiles-v1";
    public static readonly ConnectionProfiles Store = new(() => SecureStorage.GetAsync(Key), json => SecureStorage.SetAsync(Key, json));
    private static readonly SemaphoreSlim migration = new(1, 1);
    public static async Task<ConnectionProfileState> LoadAsync()
    {
        await migration.WaitAsync();
        try
        {
            // Keep the original record until the encrypted replacement is successfully persisted.
            if (await SecureStorage.GetAsync(Key) is null && await SecureStorage.GetAsync("pairing-code") is { Length: > 0 } code)
                await Store.SaveAsync("原有连接", Preferences.Get("server-url", "https://furtheragent.fennecmomo.top"), code);
            var result = await Store.LoadAsync();
            SecureStorage.Remove("pairing-code");
            return result;
        }
        finally { migration.Release(); }
    }
}
