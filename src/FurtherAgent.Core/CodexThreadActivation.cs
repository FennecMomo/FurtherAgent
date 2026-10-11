using System.Diagnostics;

namespace FurtherAgent.Core;

internal static class CodexThreadActivation
{
    internal static string ThreadUrl(string id)
    {
        if (!Guid.TryParse(id, out var guid)) throw new ArgumentException("会话 ID 无效。");
        return $"codex://threads/{guid:D}";
    }

    internal static void Open(string url)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Codex 桌面自动打开会话目前仅支持 Windows。");
        try { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { throw new IOException("无法自动打开 Codex 会话，请检查 Codex 桌面及 codex:// 协议关联。", ex); }
    }

    internal static async Task<string> ActivateAsync(string id, Action<string> open,
        Func<CancellationToken, Task<string?>> discover, CancellationToken ct,
        TimeSpan? timeout = null, TimeSpan? retryDelay = null)
    {
        string url = ThreadUrl(id);
        ct.ThrowIfCancellationRequested();
        open(url); // Once per selection; retries only discover, never repeatedly launch pages.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string? owner;
                try { owner = await discover(deadline.Token); }
                catch (TimeoutException) { owner = null; } // Only read-only discovery is retried.
                if (!string.IsNullOrEmpty(owner)) return owner;
                await Task.Delay(retryDelay ?? TimeSpan.FromMilliseconds(300), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("已请求 Codex 桌面打开该会话，但等待加载超时。请稍后重新选择；会话可能已归档、不可用或桌面版本不兼容。");
        }
    }
}
