using System.Runtime.InteropServices;
using FurtherAgent.Core;

if (args.Contains("--help"))
{
    Console.WriteLine("FurtherAgent.Host [--probe]\n环境变量：FURTHERAGENT_SERVER、FURTHERAGENT_NAME、HERMES_API_URL（默认 http://127.0.0.1:8642）、HERMES_API_KEY_FILE（仅含 key 的文件）或 HERMES_API_KEY。\n--probe 只读取能力和会话数量，不连接中继、不执行 Agent。退出接入端不会停止 Hermes 运行；重启后不恢复运行归属。");
    return;
}
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; lifetime.Cancel(); });
try
{
    string? keyFile = Environment.GetEnvironmentVariable("HERMES_API_KEY_FILE");
    string key = keyFile is null ? Environment.GetEnvironmentVariable("HERMES_API_KEY") ?? "" : (await File.ReadAllTextAsync(keyFile)).Trim();
    await using var adapter = new HermesAdapter(Environment.GetEnvironmentVariable("HERMES_API_URL") ?? "http://127.0.0.1:8642", key);
    var capabilities = await adapter.ProbeAsync(lifetime.Token);
    Console.WriteLine("Hermes 原生 API 能力检查通过。");
    if (args.Contains("--probe"))
    {
        Console.WriteLine(capabilities["features"]?.ToJsonString());
        Console.WriteLine($"可读取会话数量（最多 1000）：{(await adapter.ListAsync(lifetime.Token)).Count}");
        return;
    }
    using var registry = new RegistryClient(Environment.GetEnvironmentVariable("FURTHERAGENT_SERVER")
        ?? throw new ArgumentException("请配置 FURTHERAGENT_SERVER。"));
    await using var host = new DeviceHost(registry, () => new BorrowedAgentAdapter(adapter));
    host.Status += Console.WriteLine;
    host.CodeChanged += code => Console.WriteLine($"授权码：{code}（持有码可操作 Hermes，请仅提供给可信任的人）");
    await host.StartAsync(Environment.GetEnvironmentVariable("FURTHERAGENT_NAME") ?? Environment.MachineName + " · Hermes", lifetime.Token);
    Console.WriteLine("接入端已就绪。仅控制本进程发起的运行；不要在运行中重启接入端。");
    await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
catch (Exception ex) { Console.Error.WriteLine("接入端启动/运行失败：" + ex.Message); Environment.ExitCode = 1; }
