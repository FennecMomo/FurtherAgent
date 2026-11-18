using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace FurtherAgent.Core;

public sealed class ConnectionOpenException(string message, bool canRetry, Exception? innerException = null)
    : IOException(message, innerException)
{
    public bool CanRetry { get; } = canRetry;

    internal static ConnectionOpenException During(string stage, Exception error)
    {
        if (error is HttpRequestException { StatusCode: { } status })
        {
            if (status == HttpStatusCode.Conflict && error.Message.Contains("暂时离线", StringComparison.Ordinal))
                return new("接入端暂时离线。中继正在等待接入端重新上线。", true, error);
            if (status == HttpStatusCode.Conflict && error.Message.Contains("数量达到上限", StringComparison.Ordinal))
                return new("接入端的控制连接数量已满。请关闭一个已有控制连接后重试。", false, error);
            if (status == HttpStatusCode.NotFound && stage == "设备配对")
                return new("中继找不到该设备的在线登记。请确认远端已开启共享，并核对配对码。", false, error);

            return new($"{stage}失败。中继返回 HTTP {(int)status}。",
                status is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout, error);
        }

        if (error is OperationCanceledException or TimeoutException)
            return new($"{stage}超时。", true, error);
        if (error is WebSocketException or EndOfStreamException || error.InnerException is WebSocketException)
            return new($"{stage}期间，WebSocket 连接意外中断。", true, error);
        if (error is HttpRequestException or SocketException or IOException)
            return new($"{stage}期间连接中断。", true, error);
        if (error is JsonException)
            return new($"{stage}失败。收到的连接响应格式不正确。", false, error);

        return new($"{stage}失败。", false, error);
    }
}
