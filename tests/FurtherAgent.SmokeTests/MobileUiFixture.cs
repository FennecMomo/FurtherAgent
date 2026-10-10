using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FurtherAgent.Core;

// Isolated, explicit Android UI fixture. Never connects to a real Agent or uses user credentials.
internal static class MobileUiFixture
{
    public static async Task RunAsync()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string output = Path.Combine(root, "artifacts/android-validation"); Directory.CreateDirectory(output);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "src/FurtherAgent.Server/bin/Release/net10.0/FurtherAgent.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        using var process = Process.Start(start)!; process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient();
            for (int i = 0; ; i++)
            {
                try { if ((await http.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode) break; } catch when (i < 50) { }
                await Task.Delay(100);
            }
            Environment.SetEnvironmentVariable("FURTHERAGENT_FILES_ROOT", Path.Combine(output, "files"));
            using var registry = new RegistryClient($"http://127.0.0.1:{port}");
            await using var host = new DeviceHost(registry, () => new UiAdapter()); await host.StartAsync("Android 隔离测试设备");
            await File.WriteAllTextAsync(Path.Combine(output, "fixture.json"), Json.Node(new { port, code = host.Lease!.Code }).ToJsonString());
            Console.WriteLine("ANDROID_UI_FIXTURE_READY (local details stored under artifacts/android-validation)");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            while (!File.Exists(Path.Combine(output, "stop"))) await Task.Delay(500, timeout.Token);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private sealed class UiAdapter : IAgentAdapter
    {
        private string selected = "android-check";
        private readonly List<ChatEntry> messages = [new("assistant", "## Android 联调会话\n\n这是**隔离测试 Agent**，通过真实中继连接。\n\n可以发送消息、测试确认和文件上传；不会操作任何业务项目。")];
        private List<PendingPrompt> prompts = [];
        public AgentInfo Info => new("fixture", "Android 测试 Agent", true, "仅联调，不执行真实任务", true, true, CanArchive: true);
        public event Action<SessionView>? Changed;
        public event Action<string>? Faulted { add { } remove { } }
        private SessionView View() => new(selected, "Android 首版联调", "", "idle", null, messages.ToList(), prompts, CanStop: false);
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionInfo>>([new("android-check", "Android 首版联调", "2026-10-10"), new("history", "历史与 Markdown 示例", "2026-10-09")]);
        public Task<SessionView> AttachAsync(string id, CancellationToken ct) { selected = id; return Task.FromResult(View()); }
        public Task<SessionInfo> CreateAsync(string id, string title, CancellationToken ct) => Task.FromResult(new SessionInfo(id, title, DateTimeOffset.UtcNow.ToString("O")));
        public Task SendAsync(string text, CancellationToken ct)
        {
            messages.Add(new("user", text));
            messages.Add(new("工具", "测试连接与消息传输", "这是隔离 fixture 的工具记录，没有执行系统命令。"));
            messages.Add(new("assistant", "收到手机消息。**中文与增量同步正常。**\n\n| 检查 | 结果 |\n|---|---|\n| 中继 RPC | 已收到 |\n| 会话更新 | 已发送 |\n\n```text\nAndroid → Relay → Test Agent\n```"));
            prompts = [new("fixture-approval", "hermes/approval", "隔离测试确认", "此操作只记录你的确认选择，不执行命令。", null)];
            Changed?.Invoke(View()); return Task.CompletedTask;
        }
        public Task RespondAsync(string requestId, string decision, JsonNode? response, CancellationToken ct)
        { prompts = []; messages.Add(new("assistant", "已收到选择：" + decision)); Changed?.Invoke(View()); return Task.CompletedTask; }
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<AgentSettings> SettingsAsync(CancellationToken ct) => Task.FromResult(new AgentSettings("test-model", "fixture", "default", [new("test-model", "fixture", "隔离模型")], true, false, "测试设置，不调用模型。"));
        public Task ConfigureAsync(string model, string provider, string mode, CancellationToken ct) => Task.CompletedTask;
        public Task ArchiveAsync(string id, bool archived, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
