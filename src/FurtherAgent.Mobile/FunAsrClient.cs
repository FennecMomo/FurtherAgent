using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

namespace FurtherAgent.Mobile;

/// <summary>A single, bounded dictation over the Fun-ASR duplex protocol. Never retries audio.</summary>
public sealed class FunAsrClient
{
    public static Uri Endpoint(string workspace)
    {
        workspace = workspace.Trim();
        if (workspace.Length == 0) return new("wss://dashscope.aliyuncs.com/api-ws/v1/inference");
        if (workspace.Length > 100 || workspace.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("业务空间 ID 格式不正确。");
        return new($"wss://{workspace}.cn-beijing.maas.aliyuncs.com/api-ws/v1/inference");
    }

    public async Task RunAsync(string apiKey, Uri endpoint,
        Func<CancellationToken, IAsyncEnumerable<byte[]>> audio, Action<string> textChanged,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(4));
        var ct = lifetime.Token;
        using var socket = new ClientWebSocket();
        using var http = DirectNetwork.CreateHttpClient();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        string id = Guid.NewGuid().ToString();
        Task? receive = null, upload = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcript = new SpeechTranscript();
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            { connect.CancelAfter(TimeSpan.FromSeconds(10)); await socket.ConnectAsync(endpoint, http, connect.Token).ConfigureAwait(false); }
            await SendAsync(new { header = new { action = "run-task", task_id = id, streaming = "duplex" },
                payload = new { task_group = "audio", task = "asr", function = "recognition", model = "fun-asr-realtime",
                    parameters = new { format = "pcm", sample_rate = 16000, language_hints = new[] { "zh" }, max_sentence_silence = 800 }, input = new { } } });
            receive = ReceiveAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            upload = UploadAsync();
            if (await Task.WhenAny(upload, receive).ConfigureAwait(false) == receive && !upload.IsCompleted)
            {
                await receive.ConfigureAwait(false);
                throw new IOException("语音服务提前结束，已识别的文字已保留。");
            }
            await upload.ConfigureAwait(false);
            await receive.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("语音连接超时，已识别的文字已保留，请检查网络后重试。"); }
        catch (WebSocketException)
        { throw new IOException("语音连接失败，请检查网络、北京地域 API Key 和模型权限。已识别的文字已保留。"); }
        finally
        {
            lifetime.Cancel(); socket.Abort();
            if (upload is not null) try { await upload.ConfigureAwait(false); } catch { }
            if (receive is not null) try { await receive.ConfigureAwait(false); } catch { }
        }

        async Task SendAsync(object value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, limit.Token).ConfigureAwait(false);
        }
        async Task UploadAsync()
        {
            await foreach (var chunk in audio(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.SendAsync(chunk.AsMemory(), WebSocketMessageType.Binary, true, limit.Token).ConfigureAwait(false);
            }
            await SendAsync(new { header = new { action = "finish-task", task_id = id, streaming = "duplex" }, payload = new { input = new { } } });
        }
        async Task ReceiveAsync()
        {
            try
            {
                byte[] buffer = new byte[8192];
                while (true)
                {
                    using var message = new MemoryStream();
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(20));
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), limit.Token).ConfigureAwait(false);
                        if (result.MessageType != WebSocketMessageType.Text) throw new IOException("语音服务连接中断，已识别的文字已保留。");
                        message.Write(buffer, 0, result.Count);
                        if (message.Length > 256 * 1024) throw new IOException("语音服务响应过大。");
                    } while (!result.EndOfMessage);
                    var node = JsonNode.Parse(message.ToArray()) ?? throw new IOException("语音服务响应为空。");
                    if (node["header"]?["task_id"]?.ToString() != id) throw new IOException("语音任务响应不匹配。");
                    switch (node["header"]?["event"]?.ToString())
                    {
                        case "task-started": started.TrySetResult(); break;
                        case "result-generated":
                            if (node["payload"]?["output"]?["sentence"] is JsonObject sentence && transcript.Update(sentence))
                                textChanged(transcript.Text);
                            break;
                        case "task-finished":
                            if (!started.Task.IsCompletedSuccessfully) throw new IOException("语音任务未启动。");
                            return;
                        case "task-failed":
                            // Provider error messages can echo request data. Show a fixed actionable message instead.
                            throw new IOException("语音服务拒绝了请求，请检查北京地域 API Key、Fun-ASR 权限和账户额度。已识别的文字已保留。");
                    }
                }
            }
            catch (Exception ex) { started.TrySetException(ex); throw; }
        }
    }
}

/// <summary>Revisions replace a sentence; finals cannot be rolled back by a late partial.</summary>
public sealed class SpeechTranscript
{
    private readonly SortedDictionary<int, (string Text, bool Final)> sentences = [];
    public string Text => string.Concat(sentences.Values.Select(s => s.Text));
    public bool Update(JsonObject sentence)
    {
        if (sentence["heartbeat"]?.GetValue<bool>() == true) return false;
        int id = sentence["sentence_id"]?.GetValue<int>() ?? throw new IOException("语音结果缺少句子序号。");
        if (id < 1) return false;
        string text = sentence["text"]?.ToString() ?? "";
        bool final = sentence["sentence_end"]?.GetValue<bool>() == true;
        if (sentences.TryGetValue(id, out var previous) && (previous.Final && !final || previous == (text, final))) return false;
        sentences[id] = (text, final); return true;
    }
    public static string Append(string original, string speech) => speech.Length == 0 ? original :
        original + (original.Length > 0 && !char.IsWhiteSpace(original[^1]) ? "\n" : "") + speech;
}
