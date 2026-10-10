using FurtherAgent.Core;

internal static class DshDiscoveryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "fa-dsh-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string old = Path.Combine(directory, "desktop-old.json"), other = Path.Combine(directory, "desktop-other.json"), next = Path.Combine(directory, "desktop-next.json");
        void Descriptor(string path, string identity) => File.WriteAllText(path, Json.Node(new { protocol = 1, pid = Environment.ProcessId, instanceId = identity, endpoint = "http://127.0.0.1:1", token = "test-only" }).ToJsonString());
        try
        {
            Descriptor(old, "original-profile");
            await using var adapter = new DshAdapter(old, followDesktopRestarts: true);
            check(adapter.Info.Available, "DSH discovery recognizes initial instance identity");
            File.Delete(old);
            Descriptor(other, "other-profile");
            check(!adapter.Info.Available, "DSH does not replace offline desktop with a different profile");
            Descriptor(next, "original-profile");
            check(adapter.Info.Available, "DSH follows a restarted descriptor only for the same profile identity");
            File.Delete(next);
            check(!adapter.Info.Available, "DSH reports offline without starting a competing runtime");
        }
        finally { File.Delete(old); File.Delete(other); File.Delete(next); Directory.Delete(directory); }
    }
}
