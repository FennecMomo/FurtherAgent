using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FurtherAgent.Core;

// Contains paths only. The existing host identity stays in its original private file.
public sealed record MailboxAgentAccount(string Name, string Server, string IdentityFile)
{
    public static string DirectoryPath => Environment.GetEnvironmentVariable("FURTHERAGENT_MAILBOX_ACCOUNT_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "mailbox-accounts");
    public static string Register(string name, string server, string identityFile)
    {
        Directory.CreateDirectory(DirectoryPath);
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\n" + name)));
        string target = Path.Combine(DirectoryPath, id + ".json"), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(new MailboxAgentAccount(name, server, Path.GetFullPath(identityFile)), Json.Options)); File.Move(temp, target, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return target;
    }
    public static MailboxAgentAccount Read(string path) => JsonSerializer.Deserialize<MailboxAgentAccount>(File.ReadAllText(path), Json.Options) ?? throw new IOException("留言账户描述为空。");
    public MailboxClient Open()
    {
        if (!File.Exists(IdentityFile)) throw new IOException("未找到已有接入端身份文件；请先启动并配置家里的 FurtherAgent 接入端。");
        string secret = DeviceIdentity.LoadOrCreate(IdentityFile);
        return new MailboxClient(Server, new(DeviceIdentity.Code(secret), Name, secret, "agent"));
    }
}
