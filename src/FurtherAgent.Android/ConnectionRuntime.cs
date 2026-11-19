using FurtherAgent.Core;
using FurtherAgent.Mobile;
using ConnectionProfile = FurtherAgent.Mobile.ConnectionProfile;

namespace FurtherAgent.AndroidApp;

// Application/process lifetime, independent of Activity and WebView recreation.
// All entry points run on the MAUI main dispatcher; the pool serializes additions/removals.
internal static class ConnectionRuntime
{
    public static DeviceConnectionPool<MobileSession> Connections { get; } = new();
    public static event Action? Changed;
    private static readonly SemaphoreSlim operations = new(1, 1);
    public static MobileSession NewSession() => new(action => MainThread.BeginInvokeOnMainThread(action),
        new HistoryCache(Path.Combine(FileSystem.AppDataDirectory, "history-cache-v1")));

    private static Task<MobileSession> OpenCoreAsync(string profileServer, string code) => Connections.OpenAsync(profileServer, code, async () =>
    {
        var added = NewSession();
        string id = DeviceConnectionPool<MobileSession>.Identity(profileServer, code);
        added.Completed += completion =>
        {
            if (Connections.Entries.TryGetValue(id, out var current) && ReferenceEquals(current, added))
                CompletionNotifications.Show(id, added.DeviceName, completion);
        };
        try { await added.ConnectAsync(RelayEndpoint.Address, code, profileServer); }
        catch { await added.DisposeAsync(); throw; }
        added.Changed += () => Changed?.Invoke();
        return added;
    });

    public static async Task<(ConnectionProfile Profile, MobileSession Session)> ConnectAsync(string code, string? name, string? replaceId,
        bool? resumeOnLaunch = null)
    {
        await operations.WaitAsync();
        try
        {
            var saved = await SavedConnections.LoadAsync();
            var existing = SavedConnections.FindForSave(saved, code, replaceId);
            var target = await OpenCoreAsync(existing?.Server ?? RelayEndpoint.Address, code);
            var profile = await SavedConnections.SaveAsync(string.IsNullOrWhiteSpace(name) ? target.DeviceName : name,
                code, existing, replaceId);
            if (resumeOnLaunch is bool resume)
                Preferences.Set("resume-connection." + profile.Id, resume);
            return (profile, target);
        }
        catch
        {
            if (Connections.Entries.Count == 0)
            {
                var saved = await SavedConnections.LoadAsync();
                if (!MailboxRuntime.HasBindings && !saved.Connections.Any(p => Preferences.Get("resume-connection." +
                        DeviceConnectionPool<MobileSession>.Identity(p.Server, p.Code), true))) ConnectionService.Stop();
            }
            throw;
        }
        finally { operations.Release(); Changed?.Invoke(); }
    }

    public static async Task DisconnectAsync(string id)
    {
        await operations.WaitAsync();
        try
        {
            Preferences.Set("resume-connection." + id, false);
            await Connections.CloseAsync(id);
            var saved = await SavedConnections.LoadAsync();
            if (Connections.Entries.Count == 0 && !MailboxRuntime.HasBindings && !saved.Connections.Any(p => Preferences.Get(
                    "resume-connection." + DeviceConnectionPool<MobileSession>.Identity(p.Server, p.Code), true)))
                ConnectionService.Stop();
        }
        finally { operations.Release(); Changed?.Invoke(); }
    }

    public static async Task RestoreAsync()
    {
        if (!await operations.WaitAsync(0)) return;
        try
        {
            var saved = await SavedConnections.LoadAsync();
            if (Connections.Entries.Count == 0 && !MailboxRuntime.HasBindings && !saved.Connections.Any(p => Preferences.Get(
                    "resume-connection." + DeviceConnectionPool<MobileSession>.Identity(p.Server, p.Code), true)))
            {
                ConnectionService.Stop();
                return;
            }
            foreach (var profile in saved.Connections)
            {
                string id = DeviceConnectionPool<MobileSession>.Identity(profile.Server, profile.Code);
                if (Connections.Entries.ContainsKey(id) || !Preferences.Get("resume-connection." + id, true)) continue;
                try
                {
                    var target = await OpenCoreAsync(profile.Server, profile.Code);
                    string thread = Preferences.Get("last-session." + id, "");
                    if (thread.Length > 0 && target.Sessions.Any(s => s.Id == thread)) await target.SelectAsync(thread);
                }
                catch { /* Offline profiles remain eligible for the next service/foreground retry. */ }
            }
        }
        finally { operations.Release(); Changed?.Invoke(); }
    }

    public static Task CheckConnectionsAsync() => Task.WhenAll(Connections.Entries.Values.Select(s => s.ResumeAsync()));
}
