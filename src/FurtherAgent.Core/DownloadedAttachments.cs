using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FurtherAgent.Core;

// Records only completed, integrity-checked downloads; no credentials are persisted.
public sealed class DownloadedAttachments(string root)
{
    public const long MaxPrefetchedImageBytes = 10L * 1024 * 1024;
    private readonly SemaphoreSlim imageSlot = new(1, 1);
    private readonly Dictionary<string, Prefetch> prefetches = [];

    public static string Key(string connection, string session, RemoteFile file) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { connection, session, file.Id, file.Version, file.Size, file.Sha256 }))));

    public string? Find(string key)
    {
        try
        {
            var record = JsonSerializer.Deserialize<Entry>(File.ReadAllText(Path.Combine(root, key + ".json")));
            return record is not null && File.Exists(record.Path) && new FileInfo(record.Path).Length == record.Size ? record.Path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task<string?> FindOrWaitAsync(string key, CancellationToken ct = default)
    {
        Task<string?>? pending;
        lock (prefetches) pending = prefetches.GetValueOrDefault(key)?.Completion.Task;
        return pending is null ? Find(key) : await pending.WaitAsync(ct).ConfigureAwait(false) ?? Find(key);
    }

    public async Task<string?> PrefetchImageAsync(RpcPeer? peer, string key, string session, RemoteFile file, CancellationToken ct = default)
    {
        if (Find(key) is { } existing) return existing;
        if (peer?.IsDirectRoute != true || file.Size is <= 0 or > MaxPrefetchedImageBytes || !ImageThumbnail.Supported(file.Name))
            return null;
        while (!peer.IsClosed && !ct.IsCancellationRequested)
        {
            Prefetch pending;
            lock (prefetches)
            {
                if (!prefetches.TryGetValue(key, out pending!))
                {
                    pending = new(peer, ct);
                    prefetches.Add(key, pending);
                    _ = FetchImageAsync(key, session, file, pending);
                }
            }
            var path = await pending.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            if (path is not null || (!pending.Cancellation.IsCancellationRequested && !pending.Peer.IsClosed))
                return path;
            // A different window or an older connection may have owned the cancelled download.
        }
        return null;
    }

    private async Task FetchImageAsync(string key, string session, RemoteFile file, Prefetch pending)
    {
        var ct = pending.Cancellation;
        string? result = null, destination = null;
        try
        {
            await imageSlot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Find(key) is { } existing) { result = existing; return; }
                destination = NewPath(file.Name);
                await new FileTransferClient(pending.Peer).DownloadDirectImageAsync(session, file, destination, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Remember(key, destination);
                result = destination;
            }
            finally { imageSlot.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
            FormatException or InvalidOperationException or TimeoutException or OperationCanceledException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
        { /* The normal preview and manual download still work when prefetch cannot finish. */ }
        finally
        {
            if (result is null && destination is not null)
            {
                try { File.Delete(destination); Directory.Delete(Path.GetDirectoryName(destination)!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            lock (prefetches)
            {
                prefetches.Remove(key);
                pending.Completion.TrySetResult(result);
            }
        }
    }

    public void Remember(string key, string path)
    {
        var info = new FileInfo(path); // Called only after the transfer succeeds.
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, key + ".json"), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry(info.FullName, info.Length)));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string NewPath(string name)
    {
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string safeName = Path.GetFileName(name.Replace('\\', '/'));
        return Path.Combine(directory, string.IsNullOrWhiteSpace(safeName) || safeName is "." or ".." ? "attachment" : safeName);
    }

    private sealed record Entry(string Path, long Size);

    private sealed record Prefetch(RpcPeer Peer, CancellationToken Cancellation)
    {
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
