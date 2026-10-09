using System.Net.WebSockets;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FurtherAgent.SmokeTests")]

namespace FurtherAgent.Core;

// Only wrap HTTP request establishment / WebSocket ConnectAsync, never RPC or socket writes.
internal static class TlsHandshakeRetry
{
    internal static bool IsTlsFailure(Exception error) => error switch
    {
        HttpRequestException http => http.HttpRequestError == HttpRequestError.SecureConnectionError,
        WebSocketException { InnerException: { } inner } => IsTlsFailure(inner),
        _ => false
    };

    internal static async Task<T> RunAsync<T>(Func<Task<T>> connect, Action<int, Exception>? retry, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await connect(); }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested && IsTlsFailure(ex))
            {
                retry?.Invoke(attempt, ex);
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
            }
        }
    }
}
