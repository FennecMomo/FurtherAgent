using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace FurtherAgent.Mobile;

public sealed record AppUpdate(string Version, int VersionCode, string Url, string Sha256, long Size, string Notes = "", AppDelta[]? Deltas = null)
{
    public const long MaxSize = 100L * 1024 * 1024;
    public static Uri Https(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
            throw new IOException("更新地址必须是 HTTPS，且不能包含账号密码。");
        return uri;
    }
    public static AppUpdate Parse(string json)
    {
        var info = JsonSerializer.Deserialize<AppUpdate>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("更新信息为空。");
        _ = Https(info.Url);
        if (!System.Version.TryParse(info.Version, out _) || info.VersionCode < 1 || info.Size < 1 || info.Size > MaxSize
            || info.Sha256 is null || info.Sha256.Length != 64 || info.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new IOException("更新信息无效：版本、大小或 SHA256 不正确。");
        return info;
    }
    public static async Task<AppUpdate> CheckAsync(HttpClient http, string address, CancellationToken ct)
    {
        using var response = await http.GetAsync(Https(address), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        _ = Https(response.RequestMessage!.RequestUri!.AbsoluteUri);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 65536) throw new IOException("更新信息过大。");
            buffer.Write(chunk, 0, count);
        }
        return Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }
    public async Task DownloadAsync(HttpClient http, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        string part = destination + "." + Sha256.ToLowerInvariant() + ".part";
        ct.ThrowIfCancellationRequested();
        if (File.Exists(destination) && new FileInfo(destination).Length == Size &&
            (await AppDelta.HashAsync(destination, ct)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
        { progress?.Report(1); return; }
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset >= Size)
        {
            if (offset == Size && (await AppDelta.HashAsync(part, ct)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            { File.Move(part, destination, true); progress?.Report(1); return; }
            File.Delete(part); offset = 0;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Https(Url));
            if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            _ = Https(response.RequestMessage!.RequestUri!.AbsoluteUri);
            if (response.StatusCode == System.Net.HttpStatusCode.OK) offset = 0;
            else if (response.StatusCode != System.Net.HttpStatusCode.PartialContent
                || response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != Size
                || response.Content.Headers.ContentRange?.To != Size - 1)
                throw new InvalidDataException("更新源返回的续传范围无效。");
            if (response.Content.Headers.ContentLength is long length && length != Size - offset) throw new InvalidDataException("安装包大小与版本信息不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] bytes = new byte[131072]; long total = offset; int count;
                while ((count = await input.ReadAsync(bytes, ct)) > 0)
                {
                    total += count;
                    if (total > Size) throw new InvalidDataException("安装包超出预期大小。");
                    await output.WriteAsync(bytes.AsMemory(0, count), ct);
                    progress?.Report((double)total / Size);
                }
                if (total != Size) throw new IOException("下载中断，下次更新将继续下载。");
            }
            if (!(await AppDelta.HashAsync(part, ct)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包校验失败，请重新下载。");
            ct.ThrowIfCancellationRequested();
            File.Move(part, destination, true);
        }
        catch (InvalidDataException) { if (File.Exists(part)) File.Delete(part); throw; }
    }
}
