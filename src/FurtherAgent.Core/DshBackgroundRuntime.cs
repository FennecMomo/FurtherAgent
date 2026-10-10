using System.Diagnostics;
using System.Text.Json.Nodes;
namespace FurtherAgent.Core;

/// <summary>Owns only a dedicated isolated DSH process, never the user's desktop process.</summary>
public sealed class DshBackgroundRuntime : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stdout, stderr;
    public string DescriptorPath { get; private set; } = "";
    private DshBackgroundRuntime(Process process) { this.process = process; stdout = Drain(process.StandardOutput); stderr = Drain(process.StandardError); }
    private static async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is not null) { /* Never log launch URLs or credentials. */ } }
    public static async Task<DshBackgroundRuntime> StartAsync(string desktopExecutable, CancellationToken ct = default)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FurtherAgent", "dsh");
        string patch = Path.Combine(root, "background.patch.yml");
        if (!File.Exists(patch)) throw new IOException("请先运行 Install-DshBridge.ps1 -Mode background。");
        string exe = Path.GetFullPath(desktopExecutable);
        string cli = Path.Combine(Path.GetDirectoryName(exe)!, "resources", "app.asar", "dsh", "node_modules", "@deepseek-ai", "dsh-desktop-host", "lib", "cli.js");
        if (!File.Exists(exe) || !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "resources", "app.asar"))) throw new IOException("请选择已安装的 DSH 桌面版程序，以使用其匹配运行时。");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = root };
        // Inherit OS plumbing only. Global model keys or DSH overrides must not
        // silently turn an isolated profile into another authenticated desktop.
        var operatingSystemVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PATH", "PATHEXT", "COMSPEC", "USERNAME", "USERDOMAIN", "ProgramFiles", "ProgramFiles(x86)", "ProgramData", "HOMEDRIVE", "HOMEPATH" };
        foreach (string key in start.Environment.Keys.ToArray())
            if (!operatingSystemVariables.Contains(key)) start.Environment.Remove(key);
        start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        start.Environment["DSH_HOME"] = Path.Combine(root, "background-home");
        // Independent settings/authentication: never copy ~/.dsh into this home.
        foreach (var value in new[] { "--expose-internals", cli, "--profile", "web", "--patch", patch, "--no-open", "--port", "0" }) start.ArgumentList.Add(value);
        var runtime = new DshBackgroundRuntime(Process.Start(start) ?? throw new IOException("无法启动 DSH 后台运行时。"));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(90));
            while (!runtime.process.HasExited)
            {
                foreach (string file in Directory.Exists(DshAdapter.InstanceDirectory) ? Directory.GetFiles(DshAdapter.InstanceDirectory, "background-*.json") : [])
                {
                    try { var d = JsonNode.Parse(await File.ReadAllTextAsync(file, deadline.Token)); if (d?["pid"]?.GetValue<int>() == runtime.process.Id) { runtime.DescriptorPath = file; return runtime; } }
                    catch (System.Text.Json.JsonException) { }
                }
                await Task.Delay(500, deadline.Token);
            }
            throw new IOException("DSH 后台进程启动失败；请检查独立 profile 和运行时，不会改用桌面会话。");
        }
        catch { await runtime.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (DescriptorPath.Length > 0)
            {
                await using var adapter = new DshAdapter(DescriptorPath);
                // The plugin refuses shutdown while any run or pending inbox remains.
                await adapter.ShutdownBackgroundAsync(grace.Token);
                try { await process.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException) { throw new IOException("DSH 后台没有及时退出，已保留进程，未强制终止任务。"); }
            }
            else
            {
                // Only our failed startup process; it was never published for remote use.
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        await Task.WhenAll(stdout, stderr); process.Dispose();
    }
}
