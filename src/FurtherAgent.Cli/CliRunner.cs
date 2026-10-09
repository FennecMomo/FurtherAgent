using System.Text.Json.Nodes;
using FurtherAgent.Core;

namespace FurtherAgent.Cli;

public static class CliRunner
{
    private const string Help = """
FurtherAgent.Cli <command> [options]
Commands: probe, agents, sessions, read, create, settings, configure, send, interrupt-send, watch, stop, respond, files, upload, download, archive, unarchive
Connection: --server URL (or FURTHERAGENT_SERVER); --code-file PATH
            Authorization code is read from FURTHERAGENT_CODE or FURTHERAGENT_CODE_FILE if no file option is supplied.
Session:    --session ID (required for read/settings/configure/send/watch/stop/respond)
Create:     --request-id 32_HEX_GUID --title TITLE (both optional; generated ID printed before submission)
Send:       --text TEXT or --text-file PATH; optional --file PATH or --file-id ID; --seconds N
Files:      files --session ID; upload --session ID --path PATH; download --session ID --id FILE_ID --output PATH
Watch:      --seconds N (1..600, default 15)
Archive:    archive/unarchive --session ID (native backend support required; no delete)
Configure:  --model ID --provider ID --mode default|plan (mode defaults to default)
Respond:    --request-id ID --decision accept|decline OR --response-file JSON_PATH
All successful commands emit JSON Lines. No automatic reconnection or mutation replay.
Exit codes: 0 success, 1 failure, 130 cancelled. A send success confirms acceptance, not completion.
This CLI does not read or modify WPF settings and never opens a desktop window.
""";
    private static readonly HashSet<string> Commands = ["probe", "agents", "sessions", "read", "create", "settings", "configure", "send", "interrupt-send", "watch", "stop", "respond", "files", "upload", "download", "archive", "unarchive"];
    private static readonly HashSet<string> Common = ["server", "code-file"];
    private static readonly Dictionary<string, string[]> CommandOptions = new()
    {
        ["probe"] = ["seconds"], ["agents"] = [], ["sessions"] = [], ["read"] = ["session"],
        ["create"] = ["request-id", "title"], ["settings"] = ["session"],
        ["interrupt-send"] = ["session", "text", "text-file", "seconds", "file", "file-id"],
        ["configure"] = ["session", "model", "provider", "mode"], ["send"] = ["session", "text", "text-file", "seconds", "file", "file-id"],
        ["files"] = ["session"], ["upload"] = ["session", "path"], ["download"] = ["session", "id", "output"],
        ["watch"] = ["session", "seconds"], ["stop"] = ["session"],
        ["archive"] = ["session"], ["unarchive"] = ["session"],
        ["respond"] = ["session", "request-id", "decision", "response-file"]
    };
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        string? code = null;
        object outputLock = new();
        void Emit(string type, object? data)
        {
            lock (outputLock) output.WriteLine(Json.Node(new { type, time = DateTimeOffset.UtcNow, data }).ToJsonString(Json.Options));
        }
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "help") { await output.WriteLineAsync(Help); return 0; }
            string command = args[0];
            if (!Commands.Contains(command)) throw new ArgumentException("Unknown command; use --help.");
            var options = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException("Options require --name value pairs.");
                string key = args[i][2..];
                if (!Common.Contains(key) && !CommandOptions[command].Contains(key)) throw new ArgumentException("Unsupported option for this command: " + key);
                if (!options.TryAdd(key, args[i + 1])) throw new ArgumentException("Duplicate option: " + key);
            }
            string Required(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException("Missing --" + name);
            string? session = CommandOptions[command].Contains("session") ? Required("session") : null;
            int seconds = command == "watch" ? 15 : 0;
            if (options.TryGetValue("seconds", out var duration) && (!int.TryParse(duration, out seconds) || seconds is < 0 or > 600))
                throw new ArgumentException("--seconds must be 0..600.");
            if (command == "watch" && seconds == 0) throw new ArgumentException("watch requires at least 1 second.");
            string? text = null, requestId = null;
            JsonNode? response = null;
            if (command is "send" or "interrupt-send")
            {
                if (options.ContainsKey("text") && options.ContainsKey("text-file")) throw new ArgumentException("Specify only one of --text or --text-file.");
                if (options.ContainsKey("file") && options.ContainsKey("file-id")) throw new ArgumentException("Specify only one of --file or --file-id.");
                text = options.TryGetValue("text-file", out var path) ? await File.ReadAllTextAsync(path, ct) : options.GetValueOrDefault("text") ?? (options.ContainsKey("file") || options.ContainsKey("file-id") ? "请查看所附文件。" : Required("text"));
                if (string.IsNullOrWhiteSpace(text) || text.Length > 100000) throw new ArgumentException("Message must contain 1..100000 characters.");
            }
            if (command == "upload") _ = Required("path");
            if (command == "download") { _ = Required("id"); _ = Required("output"); }
            if (command == "create")
            {
                requestId = options.GetValueOrDefault("request-id", Guid.NewGuid().ToString("N"));
                if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("Creation --request-id must be a 32-character GUID.");
            }
            if (command == "configure")
            {
                _ = Required("model");
                if (options.GetValueOrDefault("mode", "default") is not ("default" or "plan")) throw new ArgumentException("Mode must be default or plan.");
            }
            if (command == "respond")
            {
                requestId = Required("request-id");
                if (options.ContainsKey("decision") == options.ContainsKey("response-file")) throw new ArgumentException("Specify exactly one of --decision or --response-file.");
                if (options.TryGetValue("decision", out var decision) && decision is not ("accept" or "decline")) throw new ArgumentException("Decision must be accept or decline.");
                if (options.TryGetValue("response-file", out var path)) response = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) ?? throw new ArgumentException("Empty response JSON.");
            }
            string server = options.GetValueOrDefault("server") ?? Environment.GetEnvironmentVariable("FURTHERAGENT_SERVER")
                ?? "https://furtheragent.fennecmomo.top";
            string? codeFile = options.GetValueOrDefault("code-file") ?? Environment.GetEnvironmentVariable("FURTHERAGENT_CODE_FILE");
            code = codeFile is null ? Environment.GetEnvironmentVariable("FURTHERAGENT_CODE") : await File.ReadAllTextAsync(codeFile, ct);
            code = code?.Trim();
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Set FURTHERAGENT_CODE or use --code-file.");
            using var registry = new RegistryClient(server);
            registry.ConnectionRetry += (stage, attempt, exception) =>
            {
                var causes = new List<string>();
                for (Exception? current = exception; current is not null; current = current.InnerException)
                    causes.Add($"{current.GetType().Name}: {current.Message}");
                string detail = string.Join(" -> ", causes).Replace(code, "[REDACTED]", StringComparison.Ordinal);
                Emit("connection-retry", new { stage, attempt, nextAttempt = attempt + 1, detail });
            };
            await using var remote = new RemoteDevice(false);
            await remote.ConnectAsync(registry, code, ct);
            var peer = remote.Peer;
            Emit("connected", new { device = remote.Device!.Name });
            if (seconds > 0 && command is "send" or "interrupt-send" or "watch")
                remote.Notification += (method, node) => { if (method == "session" && node?["id"]?.ToString() == session) Emit("session", node); };
            async Task<JsonNode?> Call(string method, object? value) => await peer.CallAsync(method, value, ct);
            if (command is "probe" or "agents")
            {
                Emit("agents", await Call("agents", null));
                if (command == "probe") Emit("session-count", new { count = Json.Read<SessionInfo[]>(await Call("sessions", null)).Length });
            }
            else if (command == "sessions") Emit("sessions", await Call("sessions", null));
            else if (command == "create")
            {
                var agents = Json.Read<AgentInfo[]>(await Call("agents", null));
                if (!agents.Any(a => a.Available && a.CanCreate)) throw new NotSupportedException("Remote adapter does not advertise session creation. Upgrade the Hermes host or create on the Codex desktop.");
                Emit("create-request", new { requestId });
                Emit("created", await Call("create", new { id = requestId, title = options.GetValueOrDefault("title", "FurtherAgent 联调") }));
            }
            else
            {
                var view = await Call("attach", new { id = session });
                if (command is "read" or "watch") Emit("session", view);
                if (command == "settings") Emit("settings", await Call("settings", null));
                if (command is "archive" or "unarchive")
                {
                    var agents = Json.Read<AgentInfo[]>(await Call("agents", null));
                    if (!agents.Any(a => a.Available && a.CanArchive)) throw new NotSupportedException("Remote adapter does not advertise native archive support.");
                    Emit("accepted", await Call("archive", new { session, archived = command == "archive" }));
                }
                if (command is "files" or "upload" or "download" or "send" or "interrupt-send")
                {
                    var agents = Json.Read<AgentInfo[]>(await Call("agents", null));
                    bool supported = agents.Any(a => a.Available && a.CanFiles);
                    var transfer = new FileTransferClient(peer);
                    if (!supported && (command is not ("send" or "interrupt-send") || options.ContainsKey("file") || options.ContainsKey("file-id")))
                        throw new NotSupportedException("Remote host needs file transfer support (0.4.0+).");
                    if (command == "files") Emit("files", await transfer.ListAsync(session!, ct));
                    if (command == "upload") Emit("uploaded", await transfer.UploadAsync(session!, Required("path"), ct: ct));
                    if (command == "download") Emit("downloaded", await transfer.DownloadAsync(session!, Required("id"), Required("output"), ct: ct));
                    if (command is "send" or "interrupt-send")
                    {
                        var ids = new List<string>();
                        if (options.TryGetValue("file-id", out var fileId)) ids.Add(fileId);
                        if (options.TryGetValue("file", out var path))
                        {
                            var uploaded = await transfer.UploadAsync(session!, path, ct: ct); ids.Add(uploaded.Id);
                            Emit("uploaded", uploaded); // Retain this ID for an explicit retry; do not upload a second copy.
                        }
                        if (command == "interrupt-send")
                        {
                            await SessionActions.InterruptAndSendAsync(peer, agents.First(a => a.Available), Json.Read<SessionView>(view), text!, ids,
                                phase => Emit("phase", new { message = phase }), ct);
                            Emit("accepted", new { ok = true });
                        }
                        else if (supported) { await transfer.SendAsync(session!, text!, ids, ct); Emit("accepted", new { ok = true }); }
                        else Emit("accepted", await Call("send", new { text }));
                    }
                }
                if (command == "stop") Emit("accepted", await Call("interrupt", null));
                if (command == "respond") Emit("accepted", await Call("respond", new { id = requestId, decision = options.GetValueOrDefault("decision", ""), response }));
                if (command == "configure") Emit("accepted", await Call("configure", new { model = Required("model"), provider = options.GetValueOrDefault("provider", ""), mode = options.GetValueOrDefault("mode", "default") }));
            }
            if (seconds > 0)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                if (await Task.WhenAny(delay, peer.Completion!) != delay) throw new IOException("Relay disconnected during observation; no mutation was replayed.");
                await delay;
                if (command == "probe") Emit("heartbeat", new { connected = remote.IsConnected, agentCount = Json.Read<AgentInfo[]>(await Call("agents", null)).Length });
                Emit("observation-ended", new { seconds, connected = remote.IsConnected });
            }
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await error.WriteLineAsync("{\"type\":\"cancelled\"}"); return 130; }
        catch (Exception ex)
        {
            string message = ex.Message;
            if (!string.IsNullOrEmpty(code)) message = message.Replace(code, "[REDACTED]", StringComparison.Ordinal);
            await error.WriteLineAsync(Json.Node(new { type = "error", error = ex.GetType().Name, message }).ToJsonString(Json.Options));
            return 1;
        }
    }
}
