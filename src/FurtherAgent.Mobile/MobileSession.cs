using System.Text.Json.Nodes;
using FurtherAgent.Core;

namespace FurtherAgent.Mobile;

// All UI changes are marshalled by the application. No Windows settings or credentials are read.
public sealed class MobileSession(Action<Action> dispatch, HistoryCache? cache = null) : IAsyncDisposable
{
    private string cacheScope = "";
    private bool paging, readingOlder;
    private string? selecting;
    private SessionView? latest;
    public bool HasOlder => View?.History?.Start > 0;
    public bool ReadingOlder => readingOlder;
    private static IReadOnlyList<SessionInfo> Sorted(JsonNode? node) => Json.Read<SessionInfo[]>(node).OrderByDescending(s => s.SortTime).ThenBy(s => s.Id).ToArray();
    private void Accept(SessionView incoming, bool reset = false)
    {
        if (incoming.Id != View?.Id || incoming.Model is { } model && Settings is { } settings && settings.Model != model)
            Settings = null;
        // Older Codex hosts omit files in snapshots. Preserve the separately queried list.
        if (incoming.OutputFiles is null && View?.Id == incoming.Id)
            incoming = incoming with { OutputFiles = View.OutputFiles };
        // Even a legacy host must not hand megabytes of history to the WebView.
        if (incoming.History is null) incoming = HistoryPaging.Latest(incoming, includeOperations: true) with { History = null };
        latest = incoming;
        if (reset || !readingOlder) { View = incoming; readingOlder = false; }
        else if (View is { } current) View = incoming with { Messages = current.Messages, History = current.History };
        if (cache is not null) _ = cache.SaveLatestAsync(cacheScope, incoming);
    }
    private async Task NegotiateAsync(RpcPeer peer)
    {
        paging = false;
        try { paging = (await peer.CallAsync("history-mode", new { version = 2 }))?["enabled"]?.GetValue<bool>() == true; }
        catch (IOException) { /* Older hosts still work with a bounded local view. */ }
    }
    public Task ClearCacheAsync() => cache?.ClearAsync() ?? Task.CompletedTask;
    public void ShowLatest() { if (latest is not null) { View = latest; readingOlder = false; Notify(); } }
    public Task LoadOlderAsync() => Run(async () =>
    {
        if (!paging || View is not { History: { Start: > 0 } h } view) return;
        if (latest?.History?.Revision != h.Revision) { ShowLatest(); Status = "历史已更新，已返回最新记录，请重新上滑加载。"; return; }
        var page = cache is null ? null : await cache.ReadPageAsync(cacheScope, view.Id, h.Revision, h.Start);
        if (page is null || page.Session != view.Id || page.Revision != h.Revision || page.End != h.Start || page.Start + page.Messages.Count != page.End)
        {
            page = Json.Read<HistoryPage>(await Peer.CallAsync("history", new { session = view.Id, revision = h.Revision, before = h.Start }));
            if (page.Session != view.Id || page.Revision != h.Revision || page.End != h.Start || page.Start + page.Messages.Count != page.End) throw new IOException("历史分页不匹配，请刷新会话。");
            if (cache is not null) await cache.SavePageAsync(cacheScope, page);
        }
        if (View?.Id != view.Id || latest?.History?.Revision != h.Revision) { ShowLatest(); return; }
        readingOlder = true;
        View = view with { Messages = page.Messages.Concat(view.Messages).Take(32).ToList(), History = h with { Start = page.Start } };
    });
    private RegistryClient? registry;
    private RemoteDevice? remote;
    private ConnectionHeartbeat? heartbeat;
    private readonly SemaphoreSlim operation = new(1, 1);
    private long generation;
    private string? selected;
    private string? pendingCreate;
    public event Action? Changed;
    public AgentSettings? Settings { get; private set; }
    public AgentInfo? Agent { get; private set; }
    public SessionView? View { get; private set; }
    public IReadOnlyList<SessionInfo> Sessions { get; private set; } = [];
    public string Status { get; private set; } = "尚未连接";
    public string DeviceName => remote?.Device?.Name ?? "远程设备";
    public bool Connected => remote?.IsConnected == true;
    public bool Connecting => remote?.IsReconnecting == true || Busy && remote is not null && !Connected;
    public bool HasConnection => remote is not null;
    public AgentIndicator Indicator => AgentIndicator.From(Connected, Connecting, View);
    public bool Busy { get; private set; }
    public bool SendUncertain { get; private set; }
    public string Heartbeat => heartbeat is null ? Status : $"中继 {heartbeat.Relay.State} · 设备 {heartbeat.Host.State}";
    public string Traffic => remote is null ? "" : $"↓ {remote.ReceivedBytes / 1024d:N0} KB  ↑ {remote.SentBytes / 1024d:N0} KB";
    public void AcknowledgeUncertain() { SendUncertain = false; Notify(); }
    private void Notify() => Changed?.Invoke();
    private RpcPeer Peer => remote is { IsConnected: true } r ? r.Peer : throw new IOException("连接已断开，请等待恢复后再操作。");

    private async Task Run(Func<Task> action)
    {
        if (!await operation.WaitAsync(0)) throw new IOException("上一项操作仍在等待确认。");
        Busy = true; Notify();
        try { await action(); }
        finally { Busy = false; operation.Release(); Notify(); }
    }

    public Task ConnectAsync(string url, string code) => Run(async () =>
    {
        await CloseConnectionAsync();
        selected = null; View = null; Settings = null; Agent = null; Sessions = []; pendingCreate = null;
        latest = null; readingOlder = false;
        cacheScope = url.TrimEnd('/').ToLowerInvariant() + "\n" + code.Replace("-", "").Trim().ToUpperInvariant();
        SendUncertain = false;
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("请输入远端显示的授权码。");
        var owner = new RemoteDevice();
        var client = new RegistryClient(url);
        registry = client; remote = owner;
        Status = "正在连接中继…"; Notify();
        owner.Notification += (method, node) =>
        {
            if (method != "session" || node is null) return;
            var incoming = Json.Read<SessionView>(node);
            dispatch(() => { if (remote == owner && selecting is null && incoming.Id == selected) { Accept(incoming); Notify(); } });
        };
        owner.ConnectionChanged += (_, message) => dispatch(() =>
        {
            if (remote != owner) return;
            Status = message; Notify();
        });
        owner.Reconnected += () => dispatch(() => _ = RestoreAsync(owner));
        try
        {
            await owner.ConnectAsync(client, code.Trim());
            await NegotiateAsync(Peer);
            Agent = Json.Read<AgentInfo[]>(await Peer.CallAsync("agents", null)).FirstOrDefault(a => a.Available)
                ?? throw new IOException("远端尚无可用 Agent。");
            Sessions = Sorted(await Peer.CallAsync("sessions", null));
            heartbeat = new(client, owner); heartbeat.Start();
            Status = "已连接 · " + Agent.Name;
        }
        catch { await CloseConnectionAsync(); Status = "连接失败，请检查地址与授权码"; throw; }
    });

    private async Task RestoreAsync(RemoteDevice owner)
    {
        // Reconnection is read-only. Wait for the uncertain operation to settle first.
        await operation.WaitAsync();
        try
        {
            if (remote != owner || !Connected) return;
            var id = selected; long revision = generation;
            await NegotiateAsync(owner.Peer);
            var agent = Json.Read<AgentInfo[]>(await owner.Peer.CallAsync("agents", null)).FirstOrDefault(a => a.Available);
            var sessions = Sorted(await owner.Peer.CallAsync("sessions", null));
            SessionView? view = id is null ? null : Json.Read<SessionView>(await owner.Peer.CallAsync("attach", new { id }));
            if (remote != owner || revision != generation) return;
            Agent = agent; Sessions = sessions; if (view is not null) Accept(view, true); Status = "已恢复连接 · 未重发任何操作";
            await RefreshFilesCoreAsync();
        }
        catch (Exception ex) { if (remote == owner) Status = "恢复读取失败：" + ex.Message; }
        finally { operation.Release(); Notify(); }
    }

    public Task RefreshAsync() => Run(async () =>
    {
        Sessions = Sorted(await Peer.CallAsync("sessions", null));
        if (selected is { } id) Accept(Json.Read<SessionView>(await Peer.CallAsync("attach", new { id })), true);
        await RefreshFilesCoreAsync();
    });

    public async Task ResumeAsync()
    {
        var owner = remote;
        if (owner is null) return; // Explicit disconnect must never be undone.
        if (!Connected) owner.WakeReconnect();
        if (!await operation.WaitAsync(0)) return; // Do not disturb an uncertain send or file transfer.
        Busy = true; Notify();
        try
        {
            if (remote != owner || !await owner.CheckConnectionAsync()) return;
            if (selected is { } id)
            {
                var view = Json.Read<SessionView>(await owner.Peer.CallAsync("attach", new { id }));
                if (remote == owner && selected == id) Accept(view);
            }
        }
        catch (Exception ex) { if (remote == owner) Status = "恢复读取中：" + ex.Message; }
        finally { Busy = false; operation.Release(); Notify(); }
    }

    public Task SelectAsync(string id) => Run(async () =>
    {
        long revision = ++generation;
        // Preserve the old selection when a backend refuses switching during a running task.
        var oldView = View; selecting = id;
        try
        {
            var saved = cache is null ? null : await cache.ReadLatestAsync(cacheScope, id);
            if (saved is not null && saved.Id == id)
            { View = saved with { CanSend = false, CanStop = false, Prompts = [] }; Status = "显示本地缓存 · 正在同步最新状态…"; Notify(); }
            var view = Json.Read<SessionView>(await Peer.CallAsync("attach", new { id }));
            if (revision != generation) return;
            selected = id; Settings = null; Accept(view, true); Status = "已同步最新记录";
            await RefreshFilesCoreAsync();
        }
        catch { View = oldView; throw; }
        finally { selecting = null; }
    });

    public Task CreateAsync() => Run(async () =>
    {
        if (Agent?.CanCreate != true) throw new NotSupportedException("此 Agent 请先在电脑端新建会话。");
        pendingCreate ??= Guid.NewGuid().ToString("N");
        var created = Json.Read<SessionInfo>(await Peer.CallAsync("create", new { id = pendingCreate, title = "手机新会话" }));
        pendingCreate = null;
        var view = Json.Read<SessionView>(await Peer.CallAsync("attach", new { id = created.Id }));
        ++generation; selected = created.Id; Accept(view, true);
        Sessions = Sorted(await Peer.CallAsync("sessions", null));
    });

    public Task SendAsync(string text, IReadOnlyList<string> fileIds) => Run(async () =>
    {
        if (SendUncertain) throw new IOException("上次提交结果未知，请先核对远端历史，再解除发送保护。");
        if (View is not { CanSend: true } view) throw new IOException("当前会话暂不可发送。");
        if (string.IsNullOrWhiteSpace(text) && fileIds.Count == 0) throw new ArgumentException("请输入内容或添加文件。");
        if (text.Length > 100000) throw new ArgumentException("消息过长。");
        var peer = Peer;
        try
        {
            Status = "正在提交，请等待确认…"; Notify();
            if (Agent?.CanFiles == true) await new FileTransferClient(peer).SendAsync(view.Id,
                string.IsNullOrWhiteSpace(text) ? "请查看所附文件。" : text, fileIds, responseTimeout: TimeSpan.FromMinutes(3));
            else
            {
                if (fileIds.Count > 0) throw new NotSupportedException("此 Agent 不支持文件。");
                await peer.CallAsync("send", new { text }, responseTimeout: TimeSpan.FromMinutes(3));
            }
            Status = "已收到提交确认 · 等待 Agent 输出";
        }
        catch { SendUncertain = true; Status = "提交结果未知 · 草稿已保留，请先核对历史"; throw; }
    });

    public Task StopAsync() => Run(async () =>
    {
        if (View?.CanStop != true) throw new IOException("当前运行不可从此端停止。");
        await Peer.CallAsync("interrupt", null); Status = "已提交停止请求";
    });

    public Task RespondAsync(string id, string decision, JsonNode? response = null) => Run(async () =>
    {
        if (View?.Prompts.Any(p => p.Id == id) != true) throw new IOException("该确认已失效，请刷新。");
        await Peer.CallAsync("respond", new { id, decision, response });
    });

    public async Task<RemoteFile> UploadAsync(string path, IProgress<FileProgress> progress)
    {
        RemoteFile? uploaded = null;
        await Run(async () =>
        {
            if (Agent?.CanFiles != true || View is null) throw new IOException("请先打开支持附件的会话。");
            uploaded = await new FileTransferClient(Peer).UploadAsync(View.Id, path, progress);
        });
        return uploaded!;
    }

    public async Task<List<RemoteFile>> ListOutputFilesAsync()
    {
        List<RemoteFile> output = [];
        await Run(async () =>
        {
            if (Agent?.CanFiles != true || View is null) throw new IOException("请先打开支持附件的会话。");
            var workspace = Json.Read<FileWorkspace>(await Peer.CallAsync("files/list", new { session = View.Id }));
            output = workspace.Files.Where(f => f.Id.StartsWith("outbox/", StringComparison.Ordinal)).ToList();
            SetOutputFiles(output);
        });
        return output;
    }

    private void SetOutputFiles(List<RemoteFile> output)
    {
        if (View is not null) View = View with { OutputFiles = output };
        if (latest is not null && latest.Id == View?.Id) latest = latest with { OutputFiles = output };
    }
    private async Task RefreshFilesCoreAsync()
    {
        if (Agent?.CanFiles != true || View is null) return;
        try
        {
            var workspace = await new FileTransferClient(Peer).ListAsync(View.Id);
            SetOutputFiles(workspace.Files.Where(f => f.Id.StartsWith("outbox/", StringComparison.Ordinal)).ToList());
        }
        catch (IOException) { Status += " · 文件列表暂不可用，可点附件重试"; }
        catch (TimeoutException) { Status += " · 文件列表读取超时，可点附件重试"; }
    }

    public Task DownloadAsync(RemoteFile file, string destination, IProgress<FileProgress> progress, CancellationToken ct = default) => Run(async () =>
    {
        if (View is null) throw new IOException("请先打开会话。");
        await new FileTransferClient(Peer).DownloadAsync(View.Id, file.Id, destination, progress, ct);
    });

    public async Task<AgentSettings> GetSettingsAsync()
    {
        AgentSettings? settings = null;
        await Run(async () => Settings = settings = Json.Read<AgentSettings>(await Peer.CallAsync("settings", null, responseTimeout: TimeSpan.FromMinutes(1))));
        return settings!;
    }
    public Task ConfigureAsync(string model, string provider, string mode, string? reasoningEffort = null) => Run(async () =>
    {
        if (Agent?.CanConfigure != true) throw new NotSupportedException("远端不支持设置。");
        await Peer.CallAsync("configure", new { model, provider, mode, reasoningEffort });
        if (Settings is { } current) Settings = current with { Model = model, Provider = provider, Mode = mode, ReasoningEffort = reasoningEffort };
    });
    public Task ArchiveAsync() => Run(async () =>
    {
        if (Agent?.CanArchive != true || View is null) throw new NotSupportedException("当前不支持归档。");
        await Peer.CallAsync("archive", new { session = View.Id, archived = true });
        ++generation; selected = null; View = null; Settings = null;
        Sessions = Sorted(await Peer.CallAsync("sessions", null));
    });

    public Task DisconnectAsync() => Run(async () =>
    {
        await CloseConnectionAsync(); View = null; latest = null; readingOlder = false; selected = null; Settings = null; Sessions = []; Agent = null; Status = "已断开 · 远端任务不受影响";
    });
    private async Task CloseConnectionAsync()
    {
        ++generation;
        var old = remote; remote = null;
        if (heartbeat is not null) { await heartbeat.DisposeAsync(); heartbeat = null; }
        if (old is not null) await old.DisposeAsync();
        registry?.Dispose(); registry = null;
    }
    public async ValueTask DisposeAsync() => await CloseConnectionAsync();
}
