using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

/// <summary>A session peer on a device's existing transport, with a legacy-host fallback.</summary>
public sealed class RemoteSessionConnection(RemoteDevice owner, RegistryClient registry, string code) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RpcPeer? peer;
    private RemoteDevice? legacy;
    private bool legacyMode;
    private ConnectionHeartbeat? legacyHeartbeat;
    private Task? recovery;
    private int disposed;
    public RpcPeer Peer => legacy?.Peer ?? Volatile.Read(ref peer)!;
    public bool IsSharedConnection => !legacyMode;
    public bool IsNetworkConnected => !lifetime.IsCancellationRequested && (legacyMode ? legacy?.IsConnected == true : owner.IsConnected);
    public bool IsConnected => IsNetworkConnected && Peer?.Completion?.IsCompleted == false;
    public event Action<string, JsonNode?>? Notification;
    public event Action<bool, string>? ConnectionChanged;
    public event Action? Reconnected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (recovery is null)
        {
            owner.ConnectionChanged += OwnerChanged;
            owner.Reconnected += OwnerReconnected;
            recovery = RunAsync();
        }
        await ready.Task.WaitAsync(ct);
    }
    private void OwnerReconnected() => Wake();
    private void OwnerChanged(bool online, string message)
    {
        if (legacyMode || lifetime.IsCancellationRequested) return;
        if (!online) ConnectionChanged?.Invoke(false, message);
        Wake();
    }
    private void Wake()
    {
        try { wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }
    private async Task DelayAsync(int attempt)
        => await wake.WaitAsync(TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Min(attempt, 4)))), lifetime.Token);

    private async Task RunAsync()
    {
        int attempt = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (!owner.IsConnected) { await DelayAsync(0); continue; }
                if (!owner.SupportsSessionChannels)
                {
                    legacyMode = true;
                    await RunLegacyAsync();
                    return;
                }
                var physical = owner.Peer;
                RpcPeer? opened = null;
                try
                {
                    var channel = await physical.SessionChannels!.OpenAsync(lifetime.Token);
                    if (!owner.IsConnected || owner.Peer != physical)
                    {
                        await channel.DisposeAsync();
                        throw new IOException("设备连接已更新，正在恢复会话通道。");
                    }
                    opened = new RpcPeer(channel);
                    var sync = new SessionSyncDecoder();
                    var observed = opened;
                    opened.Notification += (method, args) =>
                    {
                        if (Volatile.Read(ref peer) != observed || !IsNetworkConnected || owner.Peer != physical) return;
                        if (method == "session-update") Notification?.Invoke("session", Json.Node(sync.Apply(Json.Read<SessionUpdate>(args))));
                        else Notification?.Invoke(method, args);
                    };
                    opened.Start();
                    await opened.CallAsync("session-sync", new { version = 2 }, lifetime.Token);
                    if (!owner.IsConnected || owner.Peer != physical) throw new IOException("设备连接已更新，正在恢复会话通道。");
                    Volatile.Write(ref peer, opened);
                    attempt = 0;
                    ready.TrySetResult();
                    ConnectionChanged?.Invoke(true, "已连接 · 共用设备连接");
                    Reconnected?.Invoke();
                    await Task.WhenAny(opened.Completion!, physical.Completion!);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    ConnectionChanged?.Invoke(false, owner.IsConnected ? "网络已连接 · 会话通道等待恢复：" + ex.Message : "网络连接已断开，等待设备重连。");
                }
                finally
                {
                    if (ReferenceEquals(Volatile.Read(ref peer), opened)) Volatile.Write(ref peer, null);
                    if (opened is not null) await opened.DisposeAsync();
                }
                if (!lifetime.IsCancellationRequested)
                {
                    ConnectionChanged?.Invoke(false, owner.IsConnected ? "网络已连接 · 正在恢复会话通道。" : "网络连接已断开，等待设备重连。");
                    await DelayAsync(attempt++);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { ready.TrySetCanceled(lifetime.Token); }
    }

    private async Task RunLegacyAsync()
    {
        int attempt = 0;
        while (!lifetime.IsCancellationRequested)
        {
            var candidate = new RemoteDevice();
            legacy = candidate;
            candidate.Notification += (method, args) => { if (legacy == candidate) Notification?.Invoke(method, args); };
            candidate.ConnectionChanged += (online, message) => { if (legacy == candidate) ConnectionChanged?.Invoke(online, message); };
            candidate.Reconnected += () => { if (legacy == candidate) Reconnected?.Invoke(); };
            try
            {
                await candidate.ConnectAsync(registry, code, lifetime.Token);
                legacyHeartbeat = new ConnectionHeartbeat(registry, candidate);
                legacyHeartbeat.Start();
                ready.TrySetResult();
                ConnectionChanged?.Invoke(true, "已连接 · 接入端使用兼容连接");
                Reconnected?.Invoke();
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
                return;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                legacy = null;
                await candidate.DisposeAsync();
                ConnectionChanged?.Invoke(false, "兼容连接正在重试：" + ex.Message);
                await DelayAsync(attempt++);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        owner.ConnectionChanged -= OwnerChanged;
        owner.Reconnected -= OwnerReconnected;
        lifetime.Cancel();
        // Cancel the logical read loop without touching its parent socket.
        var current = Volatile.Read(ref peer);
        if (current is not null) await current.DisposeAsync();
        if (recovery is not null) await recovery;
        if (legacyHeartbeat is not null) await legacyHeartbeat.DisposeAsync();
        if (legacy is not null) await legacy.DisposeAsync();
        wake.Dispose(); lifetime.Dispose();
    }
}
