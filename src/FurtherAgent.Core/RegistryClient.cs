using System.Net.Http.Json;
using System.Net.WebSockets;

namespace FurtherAgent.Core;

public sealed class RegistryClient : IDisposable
{
    private readonly HttpClient http;
    public RegistryClient(string url)
    {
        var uri = new Uri(url.TrimEnd('/') + '/');
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("公网配对服务必须使用 HTTPS；HTTP 仅允许本机测试。");
        http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10) };
    }
    public async Task<T> PostAsync<T>(string path, object value, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(path, value, Json.Options, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"中继服务 {response.StatusCode}：{await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json.Options, ct))!;
    }
    public async Task<WebSocketTransport> OpenSocketAsync(string path, CancellationToken ct)
    {
        var uri = new UriBuilder(new Uri(http.BaseAddress!, path));
        uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        try { await socket.ConnectAsync(uri.Uri, ct); return new WebSocketTransport(socket); }
        catch { socket.Dispose(); throw; }
    }
    public void Dispose() => http.Dispose();
}
