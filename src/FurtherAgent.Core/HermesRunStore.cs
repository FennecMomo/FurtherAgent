using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

internal sealed record HermesRunState(string Session, string? Run, string Status, string? PendingText,
    string? PendingKey, JsonObject? PendingBody, DateTimeOffset? PendingAt, bool ReplaySupported,
    string? ReasoningEffort, string? Usage);

// One journal per authenticated endpoint and session; never store the API credential.
internal sealed class HermesRunStore(string root, string address, string credential)
{
    private readonly string directory = Path.Combine(Path.GetFullPath(root), Hash(address.TrimEnd('/') + "\n" + credential));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string PathFor(string session) => Path.Combine(directory, Hash(session) + ".json");
    public HermesRunState? Read(string session)
    {
        string path = PathFor(session);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Hermes 运行记录不能使用符号链接。");
        var state = JsonSerializer.Deserialize<HermesRunState>(File.ReadAllText(path), Json.Options);
        if (state?.Session != session || state.PendingKey is not null &&
            (string.IsNullOrEmpty(state.PendingText) || state.PendingBody?["session_id"]?.ToString() != session
             || state.PendingBody?["input"]?.ToString() != state.PendingText || state.PendingAt is null))
            throw new IOException("Hermes 运行记录损坏，未覆盖原记录。");
        return state;
    }
    public void Write(HermesRunState state)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Hermes 运行记录目录不能使用符号链接。");
        string path = PathFor(state.Session), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options))
            { JsonSerializer.Serialize(file, state, Json.Options); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
