using System.Security.Cryptography;
using System.Text;

namespace FurtherAgent.Core;

public static class DeviceIdentity
{
    public static string Code(string secret)
    {
        if (secret.Length != 64 || !secret.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid device identity.");
        return LegacyCode(secret)[..8];
    }
    public static string LegacyCode(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("FurtherAgent/device/v1/" + secret.ToUpperInvariant())))[..32];
    public static string DisplayCode(string code) => code.Length == 8 ? code[..4] + "-" + code[4..] : code;
    public static string DefaultPath(string server, string name)
    {
        if (Environment.GetEnvironmentVariable("FURTHERAGENT_IDENTITY_FILE") is { Length: > 0 } configured) return Path.GetFullPath(configured);
        string root = Environment.GetEnvironmentVariable("FURTHERAGENT_IDENTITY_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "identities");
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\n" + name)));
        return Path.Combine(root, scope + ".identity");
    }
    public static FileStream Lock(string path) => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public static void Reset(string path)
    {
        _ = LoadOrCreate(path);
        using var held = Lock(path);
        File.Delete(path); // Explicit reset while no host owns this identity.
        _ = LoadOrCreate(path);
    }
    public static string LoadOrCreate(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(dir))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (File.Exists(path)) return Read(path);
        // Never expose a partially-written identity to a second starting host.
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            { byte[] bytes = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))); stream.Write(bytes); stream.Flush(true); }
            try { File.Move(temp, path); } catch (IOException) when (File.Exists(path)) { }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return Read(path);
    }
    private static string Read(string path)
    {
        string secret = File.ReadAllText(path).Trim().ToUpperInvariant();
        _ = Code(secret); // Corruption must not silently rotate the user's pairing code.
        return secret;
    }
}
