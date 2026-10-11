using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FurtherAgent.Core;

namespace FurtherAgent.Mobile;

public sealed class HistoryCache(string directory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string PathFor(string key) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    private static string PageKey(string scope, string id, string revision, int end) => $"page\n{scope}\n{id}\n{revision}\n{end}";
    public Task<HistoryPage?> ReadPageAsync(string scope, string id, string revision, int end) => ReadAsync<HistoryPage>(PageKey(scope, id, revision, end));
    public Task SavePageAsync(string scope, HistoryPage page) => WriteAsync(PageKey(scope, page.Session, page.Revision, page.End), page);
    public Task<SessionView?> ReadLatestAsync(string scope, string id) => ReadAsync<SessionView>($"latest\n{scope}\n{id}");
    public async Task SaveLatestAsync(string scope, SessionView view)
    {
        await WriteAsync($"latest\n{scope}\n{view.Id}", view);
        if (view.History is { } h) await SavePageAsync(scope, new(view.Id, h.Revision, h.Start, h.Total, h.Total, view.Messages));
    }
    private async Task<T?> ReadAsync<T>(string key)
    {
        await gate.WaitAsync();
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return default;
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path), Json.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
        finally { gate.Release(); }
    }
    private async Task WriteAsync<T>(string key, T value)
    {
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
            if (bytes.Length > 2 * 1024 * 1024) return;
            string path = PathFor(key), temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes); File.Move(temp, path, true);
            var files = new DirectoryInfo(directory).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToArray();
            long total = files.Sum(f => f.Length);
            foreach (var file in files) { if (total <= 64L * 1024 * 1024) break; total -= file.Length; file.Delete(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Cache failure cannot break chat. */ }
        finally { gate.Release(); }
    }
    public async Task ClearAsync()
    {
        await gate.WaitAsync();
        try { if (Directory.Exists(directory)) foreach (var file in Directory.EnumerateFiles(directory, "*.json")) File.Delete(file); }
        finally { gate.Release(); }
    }
}
