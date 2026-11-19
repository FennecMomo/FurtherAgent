using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FurtherAgent.Core;

namespace FurtherAgent.Client;

public record SavedConnection(string Server, string Code, string Name, bool AutoConnect);

/// <summary>User-scoped state, independent of the versioned executable directory.</summary>
public sealed class SavedConnectionStore(string path)
{
    private record Stored(string Server, string ProtectedCode, string Name, bool AutoConnect);
    private List<Stored> Read()
    {
        if (!File.Exists(path)) return [];
        string json = File.ReadAllText(path);
        return json.TrimStart().StartsWith('[')
            ? JsonSerializer.Deserialize<List<Stored>>(json) ?? throw new IOException("连接记录为空。")
            : [JsonSerializer.Deserialize<Stored>(json) ?? throw new IOException("连接记录为空。")];
    }
    private static string Normalize(string server) => new Uri(server.Trim().TrimEnd('/') + '/').AbsoluteUri;
    private static string NormalizeCode(string code) => code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
    internal static bool IsSameRelay(string first, string second) => first.TrimEnd('/') == second.TrimEnd('/') ||
        (RelayEndpoint.IsBuiltIn(first) && RelayEndpoint.IsBuiltIn(second));
    private static SavedConnection Decode(Stored stored)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedCode), Encoding.UTF8.GetBytes(stored.Server), DataProtectionScope.CurrentUser);
        return new(stored.Server, Encoding.UTF8.GetString(bytes), stored.Name, stored.AutoConnect);
    }
    public IReadOnlyList<SavedConnection> List() => Read().Select(Decode).ToList();
    public IReadOnlyList<SavedConnection> List(string server)
    {
        string endpoint = Normalize(server);
        return Read().Where(s => IsSameRelay(s.Server, endpoint)).Select(s => Decode(s) with { Server = endpoint })
            .Reverse().DistinctBy(s => NormalizeCode(s.Code)).Reverse().ToList();
    }
    public SavedConnection? Load(string server)
    {
        var entries = List(server);
        return entries.LastOrDefault(s => s.AutoConnect) ?? entries.LastOrDefault();
    }
    public void Save(SavedConnection connection)
    {
        string server = Normalize(connection.Server);
        var entries = Read();
        entries.RemoveAll(s => IsSameRelay(s.Server, server) && NormalizeCode(Decode(s).Code) == NormalizeCode(connection.Code));
        if (connection.AutoConnect)
            entries = entries.Select(s => IsSameRelay(s.Server, server) ? s with { AutoConnect = false } : s).ToList();
        string encrypted = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(connection.Code), Encoding.UTF8.GetBytes(server), DataProtectionScope.CurrentUser));
        entries.Add(new Stored(server, encrypted, connection.Name, connection.AutoConnect));
        Write(entries);
    }
    private void Write(List<Stored> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Forget(SavedConnection connection)
    {
        string server = Normalize(connection.Server);
        var entries = Read();
        entries.RemoveAll(s => IsSameRelay(s.Server, server) && NormalizeCode(Decode(s).Code) == NormalizeCode(connection.Code));
        Write(entries);
    }
    public void Forget(string server)
    {
        string endpoint = Normalize(server);
        var entries = Read();
        if (entries.RemoveAll(s => IsSameRelay(s.Server, endpoint)) > 0) Write(entries);
    }
    public void Forget() { if (File.Exists(path)) File.Delete(path); }
}
