using System.IO.Compression;
using System.Security.Cryptography;

namespace FurtherAgent.Mobile;

public sealed record AppDelta(int FromVersionCode, string BaseSha256, string Url, string Sha256, long Size, string Algorithm = "fad1")
{
    public static async Task<AppDelta?> SelectAsync(AppUpdate update, int installedVersion, string installedApk, CancellationToken ct)
    {
        var candidates = update.Deltas?.Where(d => d is not null && d.Algorithm == "fad1" && d.FromVersionCode == installedVersion
            && d.Size > 0 && d.Size < update.Size * .6 && ValidHash(d.BaseSha256) && ValidHash(d.Sha256)).OrderBy(d => d.Size).ToArray();
        if (candidates is not { Length: > 0 } || !File.Exists(installedApk)) return null;
        string hash = await HashAsync(installedApk, ct);
        return candidates.FirstOrDefault(d => d.BaseSha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    // FAD1 gzip stream: magic, then copy(1, offset:int64, length:int32), literal(2, length:int32, bytes), end(0).
    public async Task ApplyAsync(string installedApk, string patch, string destination, AppUpdate target, CancellationToken ct)
    {
        if (!string.Equals(await HashAsync(installedApk, ct), BaseSha256, StringComparison.OrdinalIgnoreCase)
            || new FileInfo(patch).Length != Size || !string.Equals(await HashAsync(patch, ct), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("差分包或原安装包校验失败。");
        string temporary = destination + ".merge";
        try
        {
            using (var source = File.OpenRead(installedApk))
            using (var compressed = File.OpenRead(patch))
            using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            using (var reader = new BinaryReader(gzip))
            using (var output = File.Create(temporary))
            {
                if (reader.ReadUInt32() != 0x31444146) throw new IOException("未知差分包格式。");
                var buffer = new byte[131072];
                int operations = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    byte op = reader.ReadByte();
                    if (op == 0) break;
                    if (++operations > 100000 || op is not (1 or 2)) throw new IOException("差分指令无效。");
                    long offset = op == 1 ? reader.ReadInt64() : 0;
                    int length = reader.ReadInt32();
                    if (length <= 0 || length > target.Size - output.Length || (op == 1 && (offset < 0 || offset > source.Length - length)))
                        throw new IOException("差分指令越界。");
                    Stream input = op == 1 ? source : gzip;
                    if (op == 1) source.Position = offset;
                    while (length > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int count = input.Read(buffer, 0, Math.Min(buffer.Length, length));
                        if (count == 0) throw new IOException("差分包不完整。");
                        output.Write(buffer, 0, count); length -= count;
                    }
                }
                if (output.Length != target.Size || gzip.ReadByte() != -1) throw new IOException("差分包大小无效。");
            }
            if (!string.Equals(await HashAsync(temporary, ct), target.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("合成安装包校验失败。");
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task DownloadAndApplyAsync(HttpClient http, string installedApk, string destination, AppUpdate target, IProgress<double>? progress, CancellationToken ct)
    {
        string patch = destination + ".fad";
        try
        {
            await new AppUpdate(target.Version, target.VersionCode, Url, Sha256, Size).DownloadAsync(http, patch, progress, ct);
            await Task.Run(() => ApplyAsync(installedApk, patch, destination, target, ct), ct);
        }
        finally { if (File.Exists(patch)) File.Delete(patch); }
    }
}
