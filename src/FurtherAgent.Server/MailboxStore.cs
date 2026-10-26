using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FurtherAgent.Core;

namespace FurtherAgent.Server;

public sealed class MailboxException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

// A mailbox is independent of the Host's online lease and of every Agent session.
// Only completed, flushed message files are visible; metadata uses atomic replacement.
public sealed class MailboxStore : IDisposable
{
    public const int MaxTextLength = 8000;
    public const int PageBytes = 128 * 1024;
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Box> boxes = [];
    private readonly FileStream owner;
    private bool disposed;

    private sealed record Metadata(string Code, string Name, string OwnerHash, List<string> ControllerHashes,
        long ControllerReadThrough = 0, long AgentReadThrough = 0);
    private sealed class Box(Metadata metadata, string directory)
    {
        public Metadata Metadata = metadata;
        public string Directory { get; } = directory;
        public SortedDictionary<long, MailboxMessage> Messages { get; } = [];
        public Dictionary<string, MailboxMessage> ById { get; } = [];
        public long Latest => Messages.Count == 0 ? 0 : Messages.Keys.Last();
    }

    public MailboxStore(string? root = null)
    {
        this.root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("FURTHERAGENT_MAILBOX_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "mailboxes"));
        CreatePrivateDirectory(this.root);
        owner = new FileStream(Path.Combine(this.root, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static string NormalizeCode(string? code)
    {
        string value = (code ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (value.Length != 8 || !value.All(Uri.IsHexDigit)) throw new MailboxException(400, "留言箱设备码无效。");
        return value;
    }

    public void RegisterDevice(string code, string name, string identitySecret)
    {
        code = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || identitySecret.Length != 64 || !identitySecret.All(Uri.IsHexDigit))
            throw new MailboxException(400, "留言箱设备身份无效。");
        string hash = Hash(identitySecret.ToUpperInvariant());
        gate.Wait();
        try
        {
            var box = Find(code);
            if (box is not null)
            {
                if (!SameHash(box.Metadata.OwnerHash, hash)) throw new MailboxException(409, "留言箱设备身份冲突。");
                if (box.Metadata.Name != name)
                {
                    var changed = box.Metadata with { Name = name };
                    SaveMetadata(box.Directory, changed); box.Metadata = changed;
                }
                return;
            }
            string directory = Path.Combine(root, code);
            CreatePrivateDirectory(directory);
            CreatePrivateDirectory(Path.Combine(directory, "messages"));
            if (System.IO.Directory.EnumerateFiles(Path.Combine(directory, "messages"), "*.json").Any())
                throw new IOException("留言箱元数据缺失，已保留原消息。");
            var metadata = new Metadata(code, name, hash, []);
            SaveMetadata(directory, metadata);
            boxes.Add(code, new Box(metadata, directory));
        }
        finally { gate.Release(); }
    }

    public MailboxBinding PairController(string code)
    {
        code = NormalizeCode(code);
        gate.Wait();
        try
        {
            var box = Require(code);
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var metadata = box.Metadata with { ControllerHashes = [.. box.Metadata.ControllerHashes, Hash(token)] };
            SaveMetadata(box.Directory, metadata); box.Metadata = metadata;
            return new(code, metadata.Name, token);
        }
        finally { gate.Release(); }
    }

    public MailboxStatus Status(string code, string token)
    {
        code = NormalizeCode(code);
        gate.Wait();
        try { var box = Require(code); return Status(box, Authenticate(box, token)); }
        finally { gate.Release(); }
    }

    public MailboxPage Page(string code, string token, long? before = null, long? after = null, int limit = 40)
    {
        code = NormalizeCode(code);
        if (before is <= 0 || after is < 0 || before is not null && after is not null || limit < 1)
            throw new MailboxException(400, "留言分页参数无效。");
        limit = Math.Min(50, limit);
        gate.Wait();
        try
        {
            var box = Require(code); string role = Authenticate(box, token);
            IEnumerable<MailboxMessage> candidates = after is { } lower
                ? box.Messages.Values.Where(m => m.Sequence > lower)
                : box.Messages.Values.Reverse().Where(m => before is null || m.Sequence < before);
            List<MailboxMessage> messages = []; int bytes = 2;
            foreach (var message in candidates)
            {
                int size = JsonSerializer.SerializeToUtf8Bytes(message, Json.Options).Length + 1;
                if (messages.Count > 0 && (messages.Count >= limit || bytes + size > PageBytes)) break;
                messages.Add(message); bytes += size;
            }
            if (after is null) messages.Reverse();
            bool older = messages.Count > 0 && box.Messages.Keys.Any(sequence => sequence < messages[0].Sequence);
            bool more = after is not null && messages.Count > 0 && box.Messages.Keys.Any(sequence => sequence > messages[^1].Sequence);
            return new(code, box.Metadata.Name, messages, box.Latest, box.Metadata.ControllerReadThrough,
                box.Metadata.AgentReadThrough, older, more, Status(box, role).Unread);
        }
        finally { gate.Release(); }
    }

    public (MailboxMessage Message, bool Created) Send(string code, string token, MailboxSend input)
    {
        code = NormalizeCode(code);
        if (!Guid.TryParse(input.Id, out var id) || string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > MaxTextLength)
            throw new MailboxException(400, "留言需要唯一消息 ID 和 1–8000 字符正文。");
        string normalizedId = id.ToString("N");
        gate.Wait();
        try
        {
            var box = Require(code); string role = Authenticate(box, token);
            if (box.ById.TryGetValue(normalizedId, out var previous))
            {
                if (previous.Sender != role || previous.Text != input.Text)
                    throw new MailboxException(409, "同一消息 ID 已用于其他留言。");
                return (previous, false);
            }
            var message = new MailboxMessage(checked(box.Latest + 1), normalizedId, role, input.Text, DateTimeOffset.UtcNow);
            string path = Path.Combine(box.Directory, "messages", normalizedId + ".json");
            WriteAtomic(path, message);
            box.Messages.Add(message.Sequence, message); box.ById.Add(normalizedId, message);
            return (message, true);
        }
        finally { gate.Release(); }
    }

    public MailboxStatus Read(string code, string token, long through)
    {
        code = NormalizeCode(code);
        gate.Wait();
        try
        {
            var box = Require(code); string role = Authenticate(box, token);
            if (through < 0 || through > box.Latest) throw new MailboxException(400, "留言已读位置无效。");
            var metadata = role == "controller"
                ? box.Metadata with { ControllerReadThrough = Math.Max(box.Metadata.ControllerReadThrough, through) }
                : box.Metadata with { AgentReadThrough = Math.Max(box.Metadata.AgentReadThrough, through) };
            if (metadata != box.Metadata) { SaveMetadata(box.Directory, metadata); box.Metadata = metadata; }
            return Status(box, role);
        }
        finally { gate.Release(); }
    }

    private static MailboxStatus Status(Box box, string role)
    {
        long read = role == "controller" ? box.Metadata.ControllerReadThrough : box.Metadata.AgentReadThrough;
        return new(box.Metadata.Code, box.Metadata.Name, box.Latest, box.Metadata.ControllerReadThrough,
            box.Metadata.AgentReadThrough, box.Messages.Values.Count(m => m.Sender != role && m.Sequence > read));
    }
    private static string Authenticate(Box box, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new MailboxException(401, "留言箱需要已配对的凭据。");
        string hash = Hash(token);
        if (SameHash(box.Metadata.OwnerHash, hash)) return "agent";
        if (box.Metadata.ControllerHashes.Any(stored => SameHash(stored, hash))) return "controller";
        throw new MailboxException(401, "留言箱凭据无效。");
    }
    private Box Require(string code) => Find(code) ?? throw new MailboxException(404, "留言箱尚未建立，请在设备在线时配对一次。");
    private Box? Find(string code)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (boxes.TryGetValue(code, out var box)) return box;
        string directory = Path.Combine(root, code), metadataPath = Path.Combine(directory, "mailbox.json");
        if (!File.Exists(metadataPath)) return null;
        var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllBytes(metadataPath), Json.Options)
            ?? throw new IOException("留言箱元数据无法读取。");
        if (metadata.Code != code || metadata.OwnerHash.Length != 64 || metadata.ControllerHashes is null)
            throw new IOException("留言箱元数据无效。");
        box = new Box(metadata, directory);
        string messages = Path.Combine(directory, "messages");
        if (System.IO.Directory.Exists(messages))
            foreach (string path in System.IO.Directory.EnumerateFiles(messages, "*.json"))
            {
                var message = JsonSerializer.Deserialize<MailboxMessage>(File.ReadAllBytes(path), Json.Options)
                    ?? throw new IOException("留言记录无法读取。");
                if (message.Sequence < 1 || !Guid.TryParseExact(message.Id, "N", out _) ||
                    Path.GetFileNameWithoutExtension(path) != message.Id || message.Sender is not ("controller" or "agent") ||
                    string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > MaxTextLength ||
                    !box.Messages.TryAdd(message.Sequence, message) || !box.ById.TryAdd(message.Id, message))
                    throw new IOException("留言记录无效或序号重复，原文件已保留。");
            }
        if (metadata.ControllerReadThrough < 0 || metadata.AgentReadThrough < 0 ||
            metadata.ControllerReadThrough > box.Latest || metadata.AgentReadThrough > box.Latest)
            throw new IOException("留言已读记录无效。");
        boxes.Add(code, box); return box;
    }
    private static void SaveMetadata(string directory, Metadata metadata) => WriteAtomic(Path.Combine(directory, "mailbox.json"), metadata);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool SameHash(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
        else System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void WriteAtomic<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            { JsonSerializer.Serialize(stream, value, Json.Options); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; owner.Dispose(); gate.Dispose();
    }
}
