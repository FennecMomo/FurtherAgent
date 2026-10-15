using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

public sealed record LanPairing(string Name, string CertificateSha256, string Token, int Port);

public static class LanTrustStore
{
    private static string PathFor(string server, string code) => Path.Combine(
        Environment.GetEnvironmentVariable("FURTHERAGENT_LAN_TRUST_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "lan-pairings"),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\n" + Normalize(code)))) + ".json");
    public static string Normalize(string code) => code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
    private static bool Valid(LanPairing? value) => value is not null && value.Port is >= 45984 and <= 45999
        && value.CertificateSha256 is { Length: 64 } && value.CertificateSha256.All(Uri.IsHexDigit)
        && value.Token is { Length: 64 } && value.Token.All(Uri.IsHexDigit);
    public static LanPairing? Load(string server, string code)
    {
        try
        {
            string text = File.ReadAllText(PathFor(server, code));
            if (OperatingSystem.IsWindows()) text = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), Encoding.UTF8.GetBytes(server), DataProtectionScope.CurrentUser));
            var value = JsonSerializer.Deserialize<LanPairing>(text); return Valid(value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException) { return null; }
    }
    public static void Save(string server, string code, LanPairing value)
    {
        if (!Valid(value)) throw new IOException("局域网配对信息无效。");
        var path = PathFor(server, code); var dir = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            string text = JsonSerializer.Serialize(value);
            if (OperatingSystem.IsWindows()) text = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(server), DataProtectionScope.CurrentUser));
            File.WriteAllText(temporary, text);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class LanTransport(TcpClient client, SslStream stream) : IConnectionTransport
{
    private long received, sent;
    public long ReceivedBytes => Interlocked.Read(ref received);
    public long SentBytes => Interlocked.Read(ref sent);
    public async Task<JsonNode> ReadAsync(CancellationToken ct)
    {
        var value = await Frames.ReadAsync(stream, ct); Interlocked.Add(ref received, Encoding.UTF8.GetByteCount(value.ToJsonString()) + 4); return value;
    }
    public async Task WriteAsync(JsonNode value, CancellationToken ct)
    {
        await Frames.WriteAsync(stream, value, ct); Interlocked.Add(ref sent, Encoding.UTF8.GetByteCount(value.ToJsonString()) + 4);
    }
    public void Abort() => client.Dispose();
    public async ValueTask DisposeAsync() { client.Dispose(); await stream.DisposeAsync(); }
}

public sealed class LanHost : IAsyncDisposable
{
    public const int DiscoveryPort = 45371;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.42.99");
    private readonly CancellationTokenSource stop = new();
    private readonly X509Certificate2 certificate;
    private readonly string token;
    private readonly Func<IConnectionTransport, CancellationToken, Task> serve;
    private TcpListener? listener;
    private UdpClient? discovery;
    private Task? acceptLoop, discoveryLoop;
    private readonly List<Task> clients = [];
    private readonly SemaphoreSlim slots = new(8, 8);
    public LanPairing Pairing { get; private set; }
    public int Port => Pairing.Port;
    public LanHost(string name, string identityPath, string identity, Func<IConnectionTransport, CancellationToken, Task> serve)
    {
        this.serve = serve;
        token = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(identity), Encoding.UTF8.GetBytes("FurtherAgent/LAN/controller/v1")));
        var path = identityPath + ".lan.pfx";
        if (!File.Exists(path))
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=FurtherAgent LAN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        Pairing = new(name, certificate.GetCertHashString(HashAlgorithmName.SHA256), token, 0);
    }
    public void Start()
    {
        for (int port = 45984; port <= 45999; port++)
        {
            try { listener = new TcpListener(IPAddress.Any, port); listener.Start(8); Pairing = Pairing with { Port = port }; break; }
            catch (SocketException) { listener = null; }
        }
        if (listener is null) throw new IOException("局域网直连端口已被占用。");
        acceptLoop = Task.Run(AcceptLoop);
        try
        {
            discovery = new UdpClient(AddressFamily.InterNetwork);
            discovery.Client.ExclusiveAddressUse = false;
            discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            discovery.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            foreach (var address in Addresses())
                try { discovery.JoinMulticastGroup(Group, address); } catch (SocketException) { }
            discoveryLoop = Task.Run(DiscoverLoop);
        }
        catch { discovery?.Dispose(); discovery = null; /* Existing cached-address dialing remains available. */ }
    }
    public static IEnumerable<IPAddress> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsLocal(a));
    public static bool IsLocal(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        var b = a.GetAddressBytes(); return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168
            || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254);
    }
    private async Task DiscoverLoop()
    {
        var last = DateTimeOffset.MinValue;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await discovery!.ReceiveAsync(stop.Token);
                if (!IsLocal(packet.RemoteEndPoint.Address) || packet.Buffer.Length > 256 || DateTimeOffset.UtcNow - last < TimeSpan.FromMilliseconds(100)) continue;
                var request = Encoding.ASCII.GetString(packet.Buffer);
                if (!request.StartsWith("FA-LAN-1 ") || request.Length != 41) continue;
                last = DateTimeOffset.UtcNow;
                // Discovery never publishes pairing codes or bearer tokens.
                var answer = JsonSerializer.SerializeToUtf8Bytes(new { nonce = request[9..], fingerprint = Pairing.CertificateSha256, port = Port });
                await discovery.SendAsync(answer, packet.RemoteEndPoint, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (!IsLocal(((IPEndPoint)client.Client.RemoteEndPoint!).Address) || !slots.Wait(0)) { client.Dispose(); continue; }
                lock (clients) { clients.RemoveAll(t => t.IsCompleted); clients.Add(Task.Run(() => Handle(client))); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Handle(TcpClient client)
    {
        using (client)
        await using (var stream = new SslStream(client.GetStream(), false))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(6));
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
                // Authentication has its own 4 KiB frame cap, before allocating a normal RPC frame.
                byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, timeout.Token);
                int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size is < 1 or > 4096) return;
                byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, timeout.Token);
                var supplied = JsonNode.Parse(body)?["token"]?.ToString() ?? "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(token))) return;
                await using var transport = new LanTransport(client, stream);
                await transport.WriteAsync(Json.Node(new { ok = true, protocol = 2 }), timeout.Token);
                await serve(transport, stop.Token);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException or JsonException or ObjectDisposedException) { }
            finally { slots.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener?.Stop(); discovery?.Dispose();
        if (acceptLoop is not null) await acceptLoop;
        if (discoveryLoop is not null) await discoveryLoop;
        Task[] pending; lock (clients) pending = clients.ToArray(); await Task.WhenAll(pending);
        certificate.Dispose(); stop.Dispose(); slots.Dispose();
    }

    public static async Task<IConnectionTransport?> ConnectAsync(LanPairing pairing, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var udp = new UdpClient(AddressFamily.InterNetwork); udp.EnableBroadcast = true;
        string nonce = Guid.NewGuid().ToString("N"); byte[] query = Encoding.ASCII.GetBytes("FA-LAN-1 " + nonce);
        var destinations = new HashSet<IPAddress> { IPAddress.Loopback, Group, IPAddress.Broadcast };
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var unicast in network.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLocal(a.Address)))
            {
                var ip = unicast.Address.GetAddressBytes(); var mask = unicast.IPv4Mask.GetAddressBytes();
                destinations.Add(new IPAddress(ip.Zip(mask, (a, b) => (byte)(a | ~b)).ToArray()));
            }
        foreach (var destination in destinations)
            try { await udp.SendAsync(query, new IPEndPoint(destination, DiscoveryPort), timeout.Token); } catch (SocketException) { }
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult response;
            using var receive = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            receive.CancelAfter(TimeSpan.FromMilliseconds(400));
            try { response = await udp.ReceiveAsync(receive.Token); }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
            {
                foreach (var destination in destinations)
                    try { await udp.SendAsync(query, new IPEndPoint(destination, DiscoveryPort), timeout.Token); } catch (SocketException) { }
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            if (response.Buffer.Length > 1024 || !IsLocal(response.RemoteEndPoint.Address)) continue;
            try
            {
                var info = JsonNode.Parse(response.Buffer);
                int port = info?["port"]?.GetValue<int>() ?? 0;
                if (info?["nonce"]?.ToString() != nonce || !string.Equals(info["fingerprint"]?.ToString(), pairing.CertificateSha256, StringComparison.OrdinalIgnoreCase) || port is < 45984 or > 45999) continue;
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(response.RemoteEndPoint.Address, port, timeout.Token);
                    var stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
                        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).Equals(pairing.CertificateSha256, StringComparison.OrdinalIgnoreCase));
                    await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "furtheragent.local", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
                    var transport = new LanTransport(client, stream);
                    await transport.WriteAsync(Json.Node(new { token = pairing.Token }), timeout.Token);
                    if ((await transport.ReadAsync(timeout.Token))["ok"]?.GetValue<bool>() != true) { await transport.DisposeAsync(); continue; }
                    return transport;
                }
                catch { client.Dispose(); throw; }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or JsonException or InvalidOperationException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }
        ct.ThrowIfCancellationRequested(); return null;
    }
}
