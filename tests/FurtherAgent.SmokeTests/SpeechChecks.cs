using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using FurtherAgent.Mobile;

internal static class SpeechChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS: " + name); }
        var transcript = new SpeechTranscript();
        JsonObject Sentence(int id, string text, bool final = false) => new() { ["sentence_id"] = id, ["text"] = text, ["sentence_end"] = final };
        transcript.Update(Sentence(1, "你")); transcript.Update(Sentence(1, "你好。", true));
        transcript.Update(Sentence(1, "你好")); transcript.Update(Sentence(2, "继续。", true));
        Check(transcript.Text == "你好。继续。", "partial replacement and late-partial protection");
        Check(!transcript.Update(Sentence(2, "继续。", true)), "duplicate final is idempotent");
        Check(!transcript.Update(new JsonObject { ["heartbeat"] = true, ["sentence_id"] = 0 }), "heartbeat ignored");
        Check(SpeechTranscript.Append("KEEP", transcript.Text) == "KEEP\n你好。继续。", "draft retained");
        Check(SpeechTranscript.Append("KEEP", "") == "KEEP", "empty recognition preserves draft");
        Check(FunAsrClient.Endpoint("123").Host == "123.cn-beijing.maas.aliyuncs.com", "workspace endpoint");
        try { FunAsrClient.Endpoint("bad.example/steal"); throw new Exception("accepted bad workspace"); }
        catch (ArgumentException) { Check(true, "workspace cannot redirect key to arbitrary host"); }

        foreach (string scenario in new[] { "success", "denied", "disconnect", "cancel", "missing-final" })
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
            int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var cancel = new CancellationTokenSource();
            bool audioStarted = false; int audioBytes = 0; string resultText = "";
            var provider = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                Check(context.Request.Headers["Authorization"] == "Bearer fixture-only", scenario + " bearer header");
                using var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                var run = await Read(ws, deadline.Token); string id = run!["header"]!["task_id"]!.ToString();
                Check(run["payload"]?["model"]?.ToString() == "fun-asr-realtime" && run["payload"]?["parameters"]?["sample_rate"]?.GetValue<int>() == 16000, scenario + " model and audio contract");
                async Task Event(string name, JsonObject? sentence = null)
                {
                    var evt = new JsonObject { ["header"] = new JsonObject { ["event"] = name, ["task_id"] = id },
                        ["payload"] = new JsonObject { ["output"] = new JsonObject { ["sentence"] = sentence } } };
                    // Exercise fragmented UTF-8 responses across websocket frames.
                    var bytes = Encoding.UTF8.GetBytes(evt.ToJsonString()); int split = bytes.Length / 2;
                    await ws.SendAsync(bytes.AsMemory(0, split), WebSocketMessageType.Text, false, deadline.Token);
                    await ws.SendAsync(bytes.AsMemory(split), WebSocketMessageType.Text, true, deadline.Token);
                }
                if (scenario == "denied") { await Event("task-failed"); return; }
                await Event("task-started");
                var buffer = new byte[8192];
                var audio = await ws.ReceiveAsync(buffer.AsMemory(), deadline.Token);
                Check(audio.MessageType == WebSocketMessageType.Binary, scenario + " raw audio after start"); audioBytes += audio.Count;
                await Event("result-generated", Sentence(1, "你"));
                await Event("result-generated", Sentence(1, "你好。", true));
                if (scenario == "disconnect") { ws.Abort(); return; }
                if (scenario == "cancel") { cancel.Cancel(); return; }
                var finish = await Read(ws, deadline.Token);
                Check(finish?["header"]?["action"]?.ToString() == "finish-task", scenario + " explicit finish");
                if (scenario == "missing-final") { await Task.Delay(12000, deadline.Token); return; }
                await Event("result-generated", Sentence(2, "测试。", true));
                await Event("task-finished");
            });
            async IAsyncEnumerable<byte[]> Audio([EnumeratorCancellation] CancellationToken ct)
            {
                audioStarted = true; yield return new byte[3200];
                if (scenario is "disconnect" or "cancel") await Task.Delay(Timeout.Infinite, ct);
            }
            Exception? failure = null;
            try { await new FunAsrClient().RunAsync("fixture-only", new Uri($"ws://127.0.0.1:{port}/"), Audio, text => resultText = text, cancel.Token); }
            catch (Exception ex) { failure = ex; }
            await provider;
            if (scenario == "success") Check(failure is null && resultText == "你好。测试。" && audioBytes == 3200, "stream final result without duplicates");
            else if (scenario == "denied") Check(failure is IOException && !audioStarted, "denied request never starts microphone");
            else if (scenario == "cancel") Check(failure is OperationCanceledException, "cancellation stops active upload");
            else if (scenario == "missing-final") Check(failure is TimeoutException && resultText == "你好。", "bounded finish wait retains text");
            else Check(failure is IOException, "disconnect stops active upload");
        }
        Console.WriteLine($"ALL {checks} SPEECH CHECKS PASSED");
    }
    private static async Task<JsonNode?> Read(WebSocket ws, CancellationToken ct)
    {
        using var stream = new MemoryStream(); var bytes = new byte[8192]; ValueWebSocketReceiveResult result;
        do { result = await ws.ReceiveAsync(bytes.AsMemory(), ct); if (result.MessageType != WebSocketMessageType.Text) throw new IOException(); stream.Write(bytes, 0, result.Count); }
        while (!result.EndOfMessage);
        return JsonNode.Parse(stream.ToArray());
    }
}
