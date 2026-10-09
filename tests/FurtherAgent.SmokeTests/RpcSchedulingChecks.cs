using System.Text.Json.Nodes;
using System.Threading.Channels;
using FurtherAgent.Core;

static class RpcSchedulingChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var transport = new ProbeTransport();
        await using var peer = new RpcPeer(transport);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Notification += (_, _) => received.TrySetResult();
        var original = SynchronizationContext.Current;
        var blockedUi = new UnpumpedContext();
        try { SynchronizationContext.SetSynchronizationContext(blockedUi); peer.Start(); }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await transport.Input.Writer.WriteAsync(Json.Node(new { method = "session", args = new { } }));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(blockedUi.Posts == 0, "RPC receives notifications without needing the caller UI dispatcher");
        var request = peer.CallAsync("send", new { text = "one submission" }, responseTimeout: TimeSpan.FromSeconds(2));
        var sent = await transport.Output.Reader.ReadAsync();
        await Task.Delay(100);
        await transport.Input.Writer.WriteAsync(new JsonObject { ["id"] = sent["id"]!.ToString(), ["result"] = new JsonObject { ["ok"] = true } });
        check((await request)?["ok"]?.GetValue<bool>() == true && !transport.Output.Reader.TryRead(out _),
            "delayed acknowledgement resolves the original request without resubmitting");
        check(peer.LastReceivedAt is not null && peer.ReceivedMessages == 2, "RPC reports actual application receive time and count");
    }
    private sealed class UnpumpedContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
    }
    private sealed class ProbeTransport : IJsonTransport
    {
        public Channel<JsonNode> Input = Channel.CreateUnbounded<JsonNode>();
        public Channel<JsonNode> Output = Channel.CreateUnbounded<JsonNode>();
        public async Task<JsonNode> ReadAsync(CancellationToken ct) => await Input.Reader.ReadAsync(ct);
        public async Task WriteAsync(JsonNode message, CancellationToken ct) => await Output.Writer.WriteAsync(message, ct);
        public ValueTask DisposeAsync() { Input.Writer.TryComplete(); Output.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
