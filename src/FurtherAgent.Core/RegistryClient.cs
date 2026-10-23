using System.Net.Http.Json;
using System.Net.WebSockets;

namespace FurtherAgent.Core;

public sealed class RegistryClient : IDisposable
{
    private readonly HttpClient http;
    public string ServerIdentity => http.BaseAddress!.AbsoluteUri;
    public event Action<string, int, Exception>? ConnectionRetry;
    public async Task CheckHealthAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("health", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct).ConfigureAwait(false);
        if (body?["status"]?.ToString() != "ok" || body?["mode"]?.ToString() != "relay")
            throw new IOException("中继健康检查返回异常。");
    }
    public RegistryClient(string url)
    {
        var uri = new Uri(url.TrimEnd('/') + '/');
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("公网配对服务必须使用 HTTPS；HTTP 仅允许本机测试。");
        http = DirectNetwork.CreateHttpClient(TimeSpan.FromSeconds(10));
        http.BaseAddress = uri;
    }
    public async Task<T> PostAsync<T>(string path, object value, CancellationToken ct = default)
    {
        using var response = await TlsHandshakeRetry.RunAsync(
            () => http.PostAsJsonAsync(path, value, Json.Options, ct),
            (attempt, error) => ConnectionRetry?.Invoke(path, attempt, error), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"中继服务 {response.StatusCode}：{await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json.Options, ct))!;
    }
    public async Task<WebSocketTransport> OpenSocketAsync(string path, CancellationToken ct)
        => await TlsHandshakeRetry.RunAsync(() => OpenSocketOnceAsync(path, ct),
            (attempt, error) => ConnectionRetry?.Invoke(path, attempt, error), ct);

    private async Task<WebSocketTransport> OpenSocketOnceAsync(string path, CancellationToken ct)
    {
        var uri = new UriBuilder(new Uri(http.BaseAddress!, path));
        uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        // Use the same explicit direct handler for HTTP and WebSocket handshakes.
        try { await socket.ConnectAsync(uri.Uri, http, ct); return new WebSocketTransport(socket); }
        catch { socket.Dispose(); throw; }
    }
    public void Dispose() => http.Dispose();
}
