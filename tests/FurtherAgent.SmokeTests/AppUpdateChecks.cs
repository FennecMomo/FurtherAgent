using System.Net;
using System.Security.Cryptography;
using System.Text;
using FurtherAgent.Mobile;

internal static class AppUpdateChecks
{
    private sealed class Reply(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }
    private sealed class ResumeReply(byte[] bytes, bool ignoreRange = false, bool badRange = false) : HttpMessageHandler
    {
        public long? Offset;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Offset = request.Headers.Range?.Ranges.Single().From;
            int start = ignoreRange ? 0 : (int)(Offset ?? 0);
            var response = new HttpResponseMessage(ignoreRange ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { RequestMessage = request, Content = new ByteArrayContent(bytes[start..]) };
            if (!ignoreRange) response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(badRange ? 0 : start, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }
    public static async Task Run(Action<bool, string> check)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("update fixture");
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string json = System.Text.Json.JsonSerializer.Serialize(new { version = "0.12.1", versionCode = 1201, url = "https://updates.example/app.apk", sha256 = hash, size = bytes.Length });
        var info = AppUpdate.Parse(json);
        check(info.VersionCode == 1201, "self-hosted update manifest parses version code and checksum");
        bool rejected = false;
        try { AppUpdate.Parse(json.Replace("https://", "http://")); } catch (IOException) { rejected = true; }
        check(rejected, "app updater rejects insecure download URL");
        string directory = Path.Combine(Path.GetTempPath(), "fa-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "app.apk");
            using var http = new HttpClient(new Reply(bytes));
            await info.DownloadAsync(http, path, null, default);
            check((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "updater publishes verified download");
            rejected = false;
            try { await (info with { Sha256 = new string('0', 64) }).DownloadAsync(http, path, null, default); } catch (InvalidDataException) { rejected = true; }
            check(rejected && !Directory.EnumerateFiles(directory, "*.part").Any() && (await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "checksum failure removes partial file and preserves prior verified APK");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            rejected = false;
            try { await info.DownloadAsync(http, Path.Combine(directory, "cancel.apk"), null, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            check(rejected && !File.Exists(Path.Combine(directory, "cancel.apk")), "cancelled update never publishes an installable file");
            using var oversized = new HttpClient(new Reply(new byte[65537]));
            rejected = false;
            try { await AppUpdate.CheckAsync(oversized, "https://updates.example/latest.json", default); } catch (IOException) { rejected = true; }
            check(rejected, "update manifest response has bounded size");
            string resume = Path.Combine(directory, "resume.apk"), partial = resume + "." + hash.ToLowerInvariant() + ".part";
            await File.WriteAllBytesAsync(partial, bytes[..4]);
            using var resumeHandler = new ResumeReply(bytes);
            using var resumeHttp = new HttpClient(resumeHandler);
            await info.DownloadAsync(resumeHttp, resume, null, default);
            check(resumeHandler.Offset == 4 && (await File.ReadAllBytesAsync(resume)).SequenceEqual(bytes), "update resumes matching partial file with HTTP Range");
            File.Delete(resume); await File.WriteAllBytesAsync(partial, bytes[..4]);
            using var ignoredHttp = new HttpClient(new ResumeReply(bytes, ignoreRange: true));
            await info.DownloadAsync(ignoredHttp, resume, null, default);
            check((await File.ReadAllBytesAsync(resume)).SequenceEqual(bytes), "server ignoring Range safely restarts full download");
            File.Delete(resume); await File.WriteAllBytesAsync(partial, bytes[..4]);
            using var invalidRange = new HttpClient(new ResumeReply(bytes, badRange: true));
            rejected = false;
            try { await info.DownloadAsync(invalidRange, resume, null, default); } catch (InvalidDataException) { rejected = true; }
            check(rejected && !File.Exists(resume) && !File.Exists(partial), "invalid Content-Range never appends or installs corrupt data");
            byte[] original = Enumerable.Repeat((byte)42, 4096).ToArray();
            byte[] updated = original.Concat(new byte[] { 1, 2, 3 }).ToArray();
            string baseline = Path.Combine(directory, "base.apk"); await File.WriteAllBytesAsync(baseline, original);
            string patch = Path.Combine(directory, "patch.fad");
            using (var file = File.Create(patch))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress))
            using (var writer = new BinaryWriter(gzip))
            {
                writer.Write(0x31444146u); writer.Write((byte)1); writer.Write(0L); writer.Write(original.Length);
                writer.Write((byte)2); writer.Write(3); writer.Write(new byte[] { 1, 2, 3 }); writer.Write((byte)0);
            }
            byte[] patchBytes = await File.ReadAllBytesAsync(patch);
            var delta = new AppDelta(1200, Convert.ToHexString(SHA256.HashData(original)), "https://updates.example/patch.fad",
                Convert.ToHexString(SHA256.HashData(patchBytes)), patchBytes.Length);
            var target = info with { Size = updated.Length, Sha256 = Convert.ToHexString(SHA256.HashData(updated)), Deltas = [delta] };
            check(await AppDelta.SelectAsync(target, 1200, baseline, default) == delta, "delta requires matching installed version and exact APK hash");
            check(await AppDelta.SelectAsync(target, 1199, baseline, default) is null, "cross-version update uses full package");
            using var deltaHttp = new HttpClient(new Reply(patchBytes));
            await delta.DownloadAndApplyAsync(deltaHttp, baseline, path, target, null, default);
            check((await File.ReadAllBytesAsync(path)).SequenceEqual(updated), "downloaded delta reconstructs byte-identical target");
            rejected = false;
            try { await delta.ApplyAsync(baseline, patch, path, target with { Sha256 = new string('0', 64) }, default); } catch (IOException) { rejected = true; }
            check(rejected && !File.Exists(path + ".merge") && (await File.ReadAllBytesAsync(path)).SequenceEqual(updated), "bad reconstructed hash preserves verified APK and removes merge file");
            await File.WriteAllBytesAsync(baseline, new byte[4096]);
            check(await AppDelta.SelectAsync(target, 1200, baseline, default) is null, "modified baseline skips delta");
            rejected = false;
            try { await delta.ApplyAsync(baseline, patch, path, target, default); } catch (IOException) { rejected = true; }
            check(rejected, "apply rechecks baseline even after selection");
        }
        finally { Directory.Delete(directory, true); }
    }
}
