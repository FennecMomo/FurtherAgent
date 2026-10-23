using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FurtherAgent.Core;

// Records only completed, integrity-checked downloads; no credentials are persisted.
public sealed class DownloadedAttachments(string root)
{
    public static string Key(string connection, string session, RemoteFile file) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { connection, session, file.Id, file.Version, file.Size, file.Sha256 }))));

    public string? Find(string key)
    {
        try
        {
            var record = JsonSerializer.Deserialize<Entry>(File.ReadAllText(Path.Combine(root, key + ".json")));
            return record is not null && File.Exists(record.Path) && new FileInfo(record.Path).Length == record.Size ? record.Path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Remember(string key, string path)
    {
        var info = new FileInfo(path); // Called only after the transfer succeeds.
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, key + ".json"), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry(info.FullName, info.Length)));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string NewPath(string name)
    {
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string safeName = Path.GetFileName(name.Replace('\\', '/'));
        return Path.Combine(directory, string.IsNullOrWhiteSpace(safeName) || safeName is "." or ".." ? "attachment" : safeName);
    }

    private sealed record Entry(string Path, long Size);
}
