using System.Text.Json.Nodes;
using FurtherAgent.Core;

namespace FurtherAgent.Cli;

// Explicit Agent tools: none of these commands call DSH/Hermes send or create a model turn.
internal static class MailboxCli
{
    private const string Help = """
FurtherAgent.Cli mailbox accounts
FurtherAgent.Cli mailbox setup --server URL --name NAME --identity-file EXISTING_HOST_IDENTITY
FurtherAgent.Cli mailbox status|read|reply|mark-read --account-file ACCOUNT_JSON [options]
Account files are written by an upgraded persistent FurtherAgent Host; accounts lists their paths.
read: defaults to messages after the Agent's read cursor, --after SEQUENCE OR --before SEQUENCE, --limit 1..50, optional --mark-read
reply: --text TEXT OR --text-file PATH; optional --id GUID (reuse this ID after an uncertain result)
mark-read: --through SEQUENCE
These commands only use the independent persistent mailbox. Reading counts never marks a message read.
""";
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        void Emit(string kind, object value) => output.WriteLine(Json.Node(new { type = kind, time = DateTimeOffset.UtcNow, data = value }).ToJsonString());
        try
        {
            if (args.Length == 0 || args.Contains("--help")) { await output.WriteLineAsync(Help); return 0; }
            string command = args[0];
            if (command == "accounts")
            {
                var accounts = new List<object>();
                if (Directory.Exists(MailboxAgentAccount.DirectoryPath))
                    foreach (var path in Directory.EnumerateFiles(MailboxAgentAccount.DirectoryPath, "*.json"))
                        try { var a = MailboxAgentAccount.Read(path); accounts.Add(new { a.Name, a.Server, accountFile = path }); } catch (IOException) { }
                Emit("mailbox-accounts", accounts); return 0;
            }
            if (command is not ("setup" or "status" or "read" or "reply" or "mark-read")) throw new ArgumentException("未知留言命令。" + Help);
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            bool markRead = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--mark-read") { markRead = true; continue; }
                if (!args[i].StartsWith("--") || i + 1 >= args.Length || !options.TryAdd(args[i][2..], args[++i])) throw new ArgumentException("留言命令参数无效。" + Help);
            }
            if (options.Keys.Any(k => k is not ("account-file" or "text" or "text-file" or "id" or "before" or "after" or "limit" or "through" or "server" or "name" or "identity-file"))) throw new ArgumentException("未知留言参数。" + Help);
            string Required(string name) => options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException("缺少 --" + name);
            long? Number(string name) => options.TryGetValue(name, out var value) ? long.TryParse(value, out var n) && n >= 0 ? n : throw new ArgumentException("无效 --" + name) : null;
            if (command == "setup")
            {
                string identity = Path.GetFullPath(Required("identity-file"));
                if (!File.Exists(identity)) throw new IOException("请指定已有接入端身份文件；不会自动创建或更换设备身份。");
                var uri = new Uri(Required("server").TrimEnd('/') + '/');
                if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    throw new ArgumentException("请提供不含凭据的 HTTPS 中继地址。");
                _ = DeviceIdentity.LoadOrCreate(identity);
                string accountFile = MailboxAgentAccount.Register(Required("name"), uri.AbsoluteUri, identity);
                Emit("mailbox-account", new { accountFile }); return 0;
            }
            var account = MailboxAgentAccount.Read(Required("account-file"));
            using var client = account.Open();
            if (command == "status")
            {
                var status = await client.StatusAsync(ct);
                Emit("mailbox-status", new { status.Name, status.Unread, status.LatestSequence, status.ControllerReadThrough, status.AgentReadThrough });
            }
            else if (command == "read")
            {
                int limit = (int)(Number("limit") ?? 40);
                long? before = Number("before"), after = Number("after");
                // Explicit collection starts at the unread cursor so a long backlog is never skipped.
                if (before is null && after is null) after = (await client.StatusAsync(ct)).AgentReadThrough;
                var page = await client.FetchAsync(before, after, limit, ct);
                Emit("mailbox-messages", new { page.Name, page.Messages, page.LatestSequence, page.ControllerReadThrough, page.AgentReadThrough, page.HasOlder, page.HasMore });
                if (markRead && page.Messages.Count > 0) await client.MarkReadAsync(page.Messages[^1].Sequence, ct);
            }
            else if (command == "reply")
            {
                if (options.ContainsKey("text") == options.ContainsKey("text-file")) throw new ArgumentException("请提供 --text 或 --text-file 其中一个。");
                string text = options.TryGetValue("text-file", out var file) ? await File.ReadAllTextAsync(file, ct) : Required("text");
                string id = options.GetValueOrDefault("id", Guid.NewGuid().ToString("N"));
                if (!Guid.TryParse(id, out var parsed)) throw new ArgumentException("--id 需要 GUID。");
                id = parsed.ToString("N"); Emit("mailbox-message-id", new { id });
                Emit("mailbox-saved", await client.SendAsync(text, id, ct));
            }
            else await client.MarkReadAsync(Number("through") ?? throw new ArgumentException("缺少 --through"), ct);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await error.WriteLineAsync("操作已取消；结果未知的发送请使用原消息 ID 核对。"); return 130; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or ArgumentException or System.Text.Json.JsonException or NotSupportedException or OperationCanceledException)
        { await error.WriteLineAsync(Json.Node(new { type = "mailbox-error", message = ex.Message }).ToJsonString()); return 1; }
    }
}
