using System.Text.Json.Nodes;
using System.Threading.Channels;
using FurtherAgent.Core;

static class InterruptSendChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (var mode in new[] { "success", "files", "timeout", "changed", "rejected", "unowned" })
        {
            var transport = new Fake(mode);
            await using var peer = new RpcPeer(transport); peer.Start();
            bool accepted = false;
            try
            {
                await SessionActions.InterruptAndSendAsync(peer, new("hermes", "test", true, "", CanFiles: mode == "files"),
                    transport.View with { CanStop = mode != "unowned" }, "replacement", ["file-1"], stopTimeout: TimeSpan.FromMilliseconds(1200));
                accepted = true;
            }
            catch (Exception) when (mode is not ("success" or "files")) { }
            check(accepted == (mode is "success" or "files") && transport.Sends == (accepted ? 1 : 0),
                $"interrupt-send {mode}: only confirmed end permits exactly one submission");
            if (accepted) check(transport.Ended && transport.Stops == 1 && transport.LastMethod == (mode == "files" ? "send-files" : "send"),
                $"interrupt-send {mode}: stop once, wait through stopping, shared file/plain send routing");
        }
    }
    sealed class Fake(string mode) : IJsonTransport
    {
        readonly Channel<JsonNode> input = Channel.CreateUnbounded<JsonNode>();
        public int Stops, Sends, Polls; public bool Ended; public string? LastMethod;
        public SessionView View = new("session", "test", "", "active", "run-1", [], [], CanStop: true);
        public async Task<JsonNode> ReadAsync(CancellationToken ct) => await input.Reader.ReadAsync(ct);
        public async Task WriteAsync(JsonNode request, CancellationToken ct)
        {
            var method = request["method"]!.ToString(); JsonNode result;
            if (method == "attach")
            {
                var view = View;
                if (Stops > 0)
                {
                    Polls++; Ended = Polls > 1 && mode != "timeout";
                    view = view with { CanStop = false, CanSend = Ended, Status = Ended ? "unknown" : "active", TurnId = mode == "changed" ? "run-2" : "run-1" };
                }
                result = Json.Node(view);
            }
            else
            {
                if (method == "interrupt")
                {
                    if (mode == "rejected") throw new IOException("stop rejected");
                    Stops++;
                }
                else { if (!Ended) throw new Exception("Sent before stop confirmation"); Sends++; LastMethod = method; }
                result = Json.Node(new { ok = true });
            }
            await input.Writer.WriteAsync(new JsonObject { ["id"] = request["id"]!.ToString(), ["result"] = result }, ct);
        }
        public ValueTask DisposeAsync() { input.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
