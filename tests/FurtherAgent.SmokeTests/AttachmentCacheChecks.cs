using FurtherAgent.Core;

internal static class AttachmentCacheChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "FurtherAgent-cache-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new DownloadedAttachments(root);
            var file = new RemoteFile("outbox/result.txt", "result.txt", 3, Version: "v1");
            string key = DownloadedAttachments.Key("device-a", "session-a", file);
            string path = cache.NewPath(file.Name);
            File.WriteAllText(path, "abc");
            check(cache.Find(key) is null, "Unrecorded download is never reused");
            cache.Remember(key, path);
            check(new DownloadedAttachments(root).Find(key) == path, "Completed download survives cache recreation");
            check(cache.Find(DownloadedAttachments.Key("device-b", "session-a", file)) is null &&
                  cache.Find(DownloadedAttachments.Key("device-a", "session-b", file)) is null,
                  "Same filenames are isolated across connections and sessions");
            check(cache.Find(DownloadedAttachments.Key("device-a", "session-a", file with { Version = "v2" })) is null,
                  "New remote version requires a fresh download");
            File.WriteAllText(path, "truncated");
            check(cache.Find(key) is null, "Changed local size is not reused");
            File.Delete(path);
            check(cache.Find(key) is null, "Deleted local attachment requires a fresh download");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
