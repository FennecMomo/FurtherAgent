namespace FurtherAgent.Core;

/// <summary>FurtherAgent-owned traffic never inherits system or environment HTTP proxies.
/// TUN routing remains an OS/VPN concern; installers must add DIRECT rules there.</summary>
public static class DirectNetwork
{
    public static SocketsHttpHandler CreateHandler(bool allowAutoRedirect = true) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = allowAutoRedirect
    };

    public static HttpClient CreateHttpClient(TimeSpan? timeout = null) => new(CreateHandler())
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(100)
    };
}
