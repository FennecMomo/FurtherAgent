using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

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
        if (!IsSupportedAddress(uri))
            throw new ArgumentException("请使用 HTTPS 中继地址，或填写 http://IP:端口 直连中继。");
        http = DirectNetwork.CreateHttpClient(TimeSpan.FromSeconds(10));
        http.BaseAddress = uri;
    }
    public static bool IsSupportedAddress(Uri uri) => uri.IsAbsoluteUri &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp &&
            (uri.IsLoopback || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6));

    public async Task<T> PostAsync<T>(string path, object value, CancellationToken ct = default)
    {
        using var response = await TlsHandshakeRetry.RunAsync(
            () => http.PostAsJsonAsync(path, value, Json.Options, ct),
            (attempt, error) => ConnectionRetry?.Invoke(path, attempt, error), ct);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            // ASP.NET can return error messages as JSON strings with escaped Unicode.
            // Decode them before matching the protocol's explicit rejection reasons.
            if (body.AsSpan().TrimStart().StartsWith("\"", StringComparison.Ordinal))
                try { body = JsonSerializer.Deserialize<string>(body) ?? body; }
                catch (JsonException) { /* Retain the original response for diagnostics. */ }
            throw new HttpRequestException($"中继服务 {response.StatusCode}：{body}", null, response.StatusCode);
        }
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
