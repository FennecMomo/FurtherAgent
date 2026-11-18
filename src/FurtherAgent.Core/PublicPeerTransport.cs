using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SIPSorcery.Net;

namespace FurtherAgent.Core;

// SDP travels on the already paired relay connection. WebRTC authenticates the
// peer's DTLS certificate against its signaled fingerprint before opening SCTP.
internal sealed class PublicPeerTransport : IConnectionTransport
{
    private const string ChannelLabel = "furtheragent-rpc-v1";
    private const string DefaultStun = "stun:stun.cloudflare.com:3478";
    private const int FragmentSize = 16 * 1024;
    internal const int MaxDescriptionLength = 24 * 1024;
    private readonly RTCPeerConnection connection;
    private readonly TaskCompletionSource<RTCDataChannel> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> fragments = Channel.CreateBounded<byte[]>(256);
    private readonly SemaphoreSlim writer = new(1, 1);
    private RTCDataChannel? channel;
    private long receivedBytes, sentBytes;
    private int closed, disposed;

    internal static bool Enabled => Environment.GetEnvironmentVariable("FURTHERAGENT_P2P") != "0";
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    public long SentBytes => Interlocked.Read(ref sentBytes);

    private PublicPeerTransport()
    {
        string? configured = Environment.GetEnvironmentVariable("FURTHERAGENT_STUN")?.Trim();
        string stun = string.IsNullOrEmpty(configured) ? DefaultStun : configured;
        bool localOnly = stun.Equals("off", StringComparison.OrdinalIgnoreCase);
        if (!localOnly && !stun.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
            throw new IOException("直连探测仅支持 STUN 服务。");

        connection = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = localOnly ? [] : [new RTCIceServer { urls = stun }],
            X_GatherTimeoutMs = 2500,
        });
        connection.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
                SignalClosed();
        };
        connection.ondatachannel += BindChannel;
    }

    internal static async Task<(PublicPeerTransport Transport, string Offer)> CreateOfferAsync(CancellationToken ct)
    {
        var transport = new PublicPeerTransport();
        try
        {
            var channel = await transport.connection.createDataChannel(ChannelLabel).WaitAsync(ct);
            transport.BindChannel(channel);
            string offer = await transport.CreateDescriptionAsync(RTCSdpType.offer, ct);
            return (transport, offer);
        }
        catch { await transport.DisposeAsync(); throw; }
    }

    internal static async Task<(PublicPeerTransport Transport, string Answer)> AcceptOfferAsync(string offer, CancellationToken ct)
    {
        var remote = ParseDescription(offer, RTCSdpType.offer);
        var transport = new PublicPeerTransport();
        try
        {
            transport.ApplyDescription(remote);
            string answer = await transport.CreateDescriptionAsync(RTCSdpType.answer, ct);
            return (transport, answer);
        }
        catch { await transport.DisposeAsync(); throw; }
    }

    internal async Task ConnectAsync(string answer, CancellationToken ct)
    {
        ApplyDescription(ParseDescription(answer, RTCSdpType.answer));
        await WaitReadyAsync(ct);
    }

    internal async Task WaitReadyAsync(CancellationToken ct)
    {
        await ready.Task.WaitAsync(ct);
        ThrowIfClosed();
    }

    private async Task<string> CreateDescriptionAsync(RTCSdpType type, CancellationToken ct)
    {
        var gathering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Gathered(RTCIceGatheringState state)
        {
            if (state == RTCIceGatheringState.complete) gathering.TrySetResult();
        }
        connection.onicegatheringstatechange += Gathered;
        try
        {
            Gathered(connection.iceGatheringState);
            try { await gathering.Task.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch (TimeoutException) { /* Use the candidates available so far. */ }
            ct.ThrowIfCancellationRequested();
            var description = type == RTCSdpType.offer ? connection.createOffer() : connection.createAnswer();
            _ = ParseDescription(description.sdp, type);
            await connection.setLocalDescription(description).WaitAsync(ct);
            return description.sdp;
        }
        finally { connection.onicegatheringstatechange -= Gathered; }
    }

    private static RTCSessionDescriptionInit ParseDescription(string sdp, RTCSdpType type)
    {
        if (string.IsNullOrWhiteSpace(sdp) || sdp.Length > MaxDescriptionLength)
            throw new IOException("公网直连协商信息无效。");

        var lines = sdp.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var media = lines.Where(line => line.StartsWith("m=", StringComparison.Ordinal)).ToArray();
        if (lines.Length > 160 || media.Length != 1 || !media[0].StartsWith("m=application ", StringComparison.Ordinal) ||
            !lines.Any(line => line.StartsWith("a=fingerprint:sha-256 ", StringComparison.OrdinalIgnoreCase)) ||
            !lines.Any(line => line.StartsWith("a=candidate:", StringComparison.Ordinal)) ||
            lines.Any(line => line.StartsWith("a=candidate:", StringComparison.Ordinal) && line.Contains(" typ relay", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("公网直连需要带证书指纹的点对点数据通道。");

        return new RTCSessionDescriptionInit { type = type, sdp = sdp };
    }

    private void ApplyDescription(RTCSessionDescriptionInit description)
    {
        if (connection.setRemoteDescription(description) != SetDescriptionResultEnum.OK)
            throw new IOException("公网直连无法应用对端协商信息。");
    }

    private void BindChannel(RTCDataChannel candidate)
    {
        if (candidate.label != ChannelLabel || !candidate.ordered || candidate.maxPacketLifeTime.GetValueOrDefault() != 0 ||
            candidate.maxRetransmits.GetValueOrDefault() != 0 || Interlocked.CompareExchange(ref channel, candidate, null) is not null)
        {
            SignalClosed();
            return;
        }
        void Opened() => ready.TrySetResult(candidate);
        candidate.onopen += Opened;
        candidate.onclose += SignalClosed;
        candidate.onerror += _ => SignalClosed();
        candidate.onmessage += (_, protocol, data) =>
        {
            if (Volatile.Read(ref closed) != 0) return;
            if (protocol != DataChannelPayloadProtocols.WebRTC_Binary || data.Length is < 1 or > FragmentSize ||
                !fragments.Writer.TryWrite(data))
                SignalClosed();
        };
        if (candidate.readyState == RTCDataChannelState.open) Opened();
    }

    public async Task<JsonNode> ReadAsync(CancellationToken ct)
    {
        byte[] first = await ReadFragmentAsync(ct);
        if (first.Length <= sizeof(int)) throw InvalidFrame();
        int length = BinaryPrimitives.ReadInt32LittleEndian(first);
        int offset = first.Length - sizeof(int);
        if (length < offset || length > Frames.MaxBytes) throw InvalidFrame();
        Interlocked.Add(ref receivedBytes, offset);
        var payload = new byte[length];
        first.AsSpan(sizeof(int)).CopyTo(payload);
        while (offset < length)
        {
            byte[] next = await ReadFragmentAsync(ct);
            if (next.Length > length - offset) throw InvalidFrame();
            next.CopyTo(payload, offset);
            offset += next.Length;
            Interlocked.Add(ref receivedBytes, next.Length);
        }
        return JsonNode.Parse(payload) ?? throw InvalidFrame();
    }

    private async Task<byte[]> ReadFragmentAsync(CancellationToken ct)
    {
        ThrowIfClosed();
        try { return await fragments.Reader.ReadAsync(ct); }
        catch (ChannelClosedException ex) { throw new IOException("公网直连已断开。", ex); }
    }

    public async Task WriteAsync(JsonNode message, CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Json.Options);
        if (payload.Length > Frames.MaxBytes) throw new IOException("公网直连消息超过 32 MiB。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(120, 15 + payload.Length / 8192d)));
        var token = timeout.Token;
        await writer.WaitAsync(token);
        try
        {
            var target = await ready.Task.WaitAsync(token);
            int firstLength = Math.Min(payload.Length, FragmentSize - sizeof(int));
            var first = new byte[firstLength + sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(first, payload.Length);
            payload.AsSpan(0, firstLength).CopyTo(first.AsSpan(sizeof(int)));
            await SendFragmentAsync(target, first, 0, first.Length, token);
            for (int offset = firstLength; offset < payload.Length; offset += FragmentSize)
                await SendFragmentAsync(target, payload, offset, Math.Min(FragmentSize, payload.Length - offset), token);
            Interlocked.Add(ref sentBytes, payload.Length);
        }
        catch
        {
            // Once a frame starts, a partial write cannot be followed by another frame.
            Abort();
            throw;
        }
        finally { writer.Release(); }
    }

    private async Task SendFragmentAsync(RTCDataChannel target, byte[] data, int offset, int count, CancellationToken ct)
    {
        while (target.bufferedAmount > 256 * 1024)
        {
            ThrowIfClosed();
            await Task.Delay(10, ct);
        }
        ct.ThrowIfCancellationRequested();
        ThrowIfClosed();
        if (target.readyState != RTCDataChannelState.open || connection.sctp.state != RTCSctpTransportState.Connected)
            throw new IOException("公网直连数据通道未就绪。");
        target.send(data, offset, count);
    }

    private IOException InvalidFrame()
    {
        SignalClosed();
        return new IOException("公网直连消息格式无效。");
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref closed) != 0) throw new IOException("公网直连已断开。");
    }

    private void SignalClosed()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        fragments.Writer.TryComplete();
        ready.TrySetException(new IOException("公网直连通道未建立或已经关闭。"));
    }

    public void Abort() => SignalClosed();

    public ValueTask DisposeAsync()
    {
        SignalClosed();
        if (Interlocked.Exchange(ref disposed, 1) == 0) connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
