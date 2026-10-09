using System.Net.WebSockets;
using FurtherAgent.Core;

static class TlsRetryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        static HttpRequestException Tls() => new(HttpRequestError.SecureConnectionError, "test TLS handshake failure");
        int attempts = 0, notices = 0;
        var result = await TlsHandshakeRetry.RunAsync(() => ++attempts < 3 ? Task.FromException<int>(Tls()) : Task.FromResult(7),
            (_, _) => notices++, default);
        check(result == 7 && attempts == 3 && notices == 2, "TLS establishment retries recover with a bounded three attempts");
        attempts = 0;
        try { await TlsHandshakeRetry.RunAsync<int>(() => { attempts++; throw Tls(); }, null, default); }
        catch (HttpRequestException) { }
        check(attempts == 3, "persistent TLS failure is surfaced after three attempts");
        foreach (Exception error in new Exception[] { new IOException("RPC lost"), new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized), new HttpRequestException(HttpRequestError.ConnectionError, "connection lost"), new OperationCanceledException() })
        {
            attempts = 0;
            try { await TlsHandshakeRetry.RunAsync<int>(() => { attempts++; throw error; }, null, default); } catch { }
            check(attempts == 1, "no retry for " + error.Message);
        }
        using var cancel = new CancellationTokenSource();
        attempts = 0;
        try { await TlsHandshakeRetry.RunAsync<int>(() => { attempts++; throw Tls(); }, (_, _) => cancel.Cancel(), cancel.Token); }
        catch (OperationCanceledException) { }
        check(attempts == 1, "cancellation stops TLS retry delay");
        check(TlsHandshakeRetry.IsTlsFailure(new WebSocketException("connect failed", Tls())), "WebSocket TLS inner failure classified");
    }
}
