using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FurtherAgent.Client;

public record SavedConnection(string Server, string Code, string Name, bool AutoConnect);

/// <summary>User-scoped state, independent of the versioned executable directory.</summary>
public sealed class SavedConnectionStore(string path)
{
    private record Stored(string Server, string ProtectedCode, string Name, bool AutoConnect);
    private static string Normalize(string server) => new Uri(server.Trim().TrimEnd('/') + '/').AbsoluteUri;
    public SavedConnection? Load(string server)
    {
        if (!File.Exists(path)) return null;
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new IOException("连接记录为空。");
        if (stored.Server != Normalize(server)) return null;
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedCode), Encoding.UTF8.GetBytes(stored.Server), DataProtectionScope.CurrentUser);
        return new(stored.Server, Encoding.UTF8.GetString(bytes), stored.Name, stored.AutoConnect);
    }
    public void Save(SavedConnection connection)
    {
        string server = Normalize(connection.Server);
        string encrypted = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(connection.Code), Encoding.UTF8.GetBytes(server), DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored(server, encrypted, connection.Name, connection.AutoConnect)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Forget() { if (File.Exists(path)) File.Delete(path); }
}
