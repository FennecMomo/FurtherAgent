using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace FurtherAgent.Core;

// Short-lived metadata client. Never sends turns or resumes an existing desktop writer.
internal interface ICodexMetadata : IAsyncDisposable
{
    Task<JsonNode> CallAsync(string method, object args, CancellationToken ct);
}
internal sealed class CodexAppServer : ICodexMetadata
{
    private readonly Process process;
    private readonly Task stderr;
    private int sequence;
    private CodexAppServer(Process process)
    {
        this.process = process;
        stderr = DrainAsync(process.StandardError);
    }
    private static async Task DrainAsync(StreamReader reader)
    { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) != 0) { } }
    internal static string FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("FURTHERAGENT_CODEX_EXECUTABLE") is { Length: > 0 } configured)
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured)) throw new IOException("FURTHERAGENT_CODEX_EXECUTABLE 必须指向 Codex 可执行文件的绝对路径。");
            return configured;
        }
        // Prefer the runtime already used by this Windows user's Codex desktop.
        foreach (var item in Process.GetProcessesByName("codex"))
        {
            using (item)
                try { if (item.MainModule?.FileName is { } path && File.Exists(path)) return path; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        foreach (var item in Process.GetProcessesByName("ChatGPT"))
        {
            using (item)
                try
                {
                    var path = Path.Combine(Path.GetDirectoryName(item.MainModule!.FileName)!, "resources", "codex.exe");
                    if (File.Exists(path)) return path;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var path = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (File.Exists(path)) return path;
        }
        throw new IOException("未找到 Codex 运行时。请在远端打开 Codex 桌面，或设置 FURTHERAGENT_CODEX_EXECUTABLE。");
    }
    internal static async Task<CodexAppServer> OpenAsync(string home, CancellationToken ct)
    {
        var start = new ProcessStartInfo(FindExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = home
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        start.Environment["CODEX_HOME"] = home;
        var client = new CodexAppServer(Process.Start(start) ?? throw new IOException("无法启动 Codex 元数据接口。"));
        try
        {
            await client.CallAsync("initialize", new { clientInfo = new { name = "furtheragent", title = "FurtherAgent", version = "0.11.0" }, capabilities = new { experimentalApi = true } }, ct);
            await client.process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            await client.process.StandardInput.FlushAsync(ct);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }
    public async Task<JsonNode> CallAsync(string method, object args, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        int id = ++sequence;
        await process.StandardInput.WriteLineAsync(Json.Node(new { id, method, @params = args }).ToJsonString(Json.Options).AsMemory(), deadline.Token);
        await process.StandardInput.FlushAsync(deadline.Token);
        while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
        {
            var reply = JsonNode.Parse(line);
            if (reply?["id"]?.ToString() != id.ToString()) continue;
            if (reply["error"] is { } error) throw new IOException($"Codex {method}：{error["message"]}。请确认远端 Codex 支持工作空间接口。");
            return reply["result"] ?? throw new IOException("Codex 返回了空结果。");
        }
        throw new IOException("Codex 元数据接口已断开。");
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            await stderr;
        }
        finally { process.Dispose(); }
    }
}
