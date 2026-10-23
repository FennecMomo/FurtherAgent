using System.Diagnostics;

namespace FurtherAgent.Core;

public record HeartbeatState(string State, DateTimeOffset? LastSuccess = null, long? Milliseconds = null);

/// <summary>Independent HTTP relay health and end-to-end, read-only Host RPC checks.</summary>
public sealed class ConnectionHeartbeat(RegistryClient registry, RemoteDevice remote) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private HeartbeatState relay = new("检测中"), host = new("检测中");
    public HeartbeatState Relay => Volatile.Read(ref relay);
    public HeartbeatState Host => Volatile.Read(ref host);
    public void Start() => loop ??= Task.Run(() => Task.WhenAll(RunAsync(CheckRelay), RunAsync(CheckHost)));
    private async Task RunAsync(Func<Task> check)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await check();
                await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task CheckRelay()
    {
        if (remote.ConnectionKind == "局域网直连") { Volatile.Write(ref relay, new("未使用（局域网直连）")); return; }
        var previous = Relay;
        Volatile.Write(ref relay, previous with { State = "检测中" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var watch = Stopwatch.StartNew();
        try
        {
            await registry.CheckHealthAsync(timeout.Token);
            Volatile.Write(ref relay, new("在线", DateTimeOffset.UtcNow, watch.ElapsedMilliseconds));
        }
        catch { Volatile.Write(ref relay, previous with { State = "不可达 / 超时" }); }
    }
    private async Task CheckHost()
    {
        var previous = Host;
        if (!remote.IsConnected) { Volatile.Write(ref host, previous with { State = "断线 / 重连中" }); return; }
        Volatile.Write(ref host, previous with { State = "检测中" });
        var watch = Stopwatch.StartNew();
        try
        {
            // New hosts answer outside the agent queue; legacy hosts use cached agents metadata.
            if (!await remote.CheckConnectionAsync(stop.Token))
            {
                Volatile.Write(ref host, previous with { State = "断线 / 重连中" });
                return;
            }
            Volatile.Write(ref host, new("在线", DateTimeOffset.UtcNow, watch.ElapsedMilliseconds));
        }
        catch { Volatile.Write(ref host, previous with { State = remote.IsConnected ? "响应超时（可能繁忙）" : "断线 / 重连中" }); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null) await loop;
        stop.Dispose();
    }
}
