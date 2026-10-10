using System.Runtime.InteropServices;
using FurtherAgent.Core;

if (args.Contains("--help"))
{
    Console.WriteLine("FurtherAgent.Host [--probe | --reset-pairing]\n环境变量：FURTHERAGENT_IDENTITY_FILE（可选固定身份文件）、FURTHERAGENT_SERVER、FURTHERAGENT_NAME、HERMES_API_URL（默认 http://127.0.0.1:8642）、HERMES_API_KEY_FILE（仅含 key 的文件）或 HERMES_API_KEY。\n--probe 只读取能力和会话数量，不连接中继、不执行 Agent。退出接入端不会停止 Hermes 运行；重启后不恢复运行归属。");
    return;
}
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; lifetime.Cancel(); });
try
{
    string backend = Environment.GetEnvironmentVariable("FURTHERAGENT_BACKEND") ?? "hermes";
    if (backend is not ("hermes" or "dsh-desktop" or "dsh-background")) throw new ArgumentException("FURTHERAGENT_BACKEND 必须是 hermes、dsh-desktop 或 dsh-background。");
    string defaultName = Environment.MachineName + " · " + (backend == "hermes" ? "Hermes" : backend);
    if (args.Contains("--reset-pairing"))
    {
        using var resetRegistry = new RegistryClient(Environment.GetEnvironmentVariable("FURTHERAGENT_SERVER") ?? throw new ArgumentException("请配置 FURTHERAGENT_SERVER。"));
        string resetName = Environment.GetEnvironmentVariable("FURTHERAGENT_NAME") ?? defaultName;
        DeviceIdentity.Reset(DeviceIdentity.DefaultPath(resetRegistry.ServerIdentity, resetName));
        Console.WriteLine("设备身份已重置，下次启动使用新的固定配对码。请勿删除身份文件来升级软件。");
        return;
    }
    string descriptor = Environment.GetEnvironmentVariable("FURTHERAGENT_DSH_DESCRIPTOR") ?? "";
    bool followDesktopRestarts = backend == "dsh-desktop" && descriptor.Length == 0;
    await using var background = backend == "dsh-background" && descriptor.Length == 0
        ? await DshBackgroundRuntime.StartAsync(Environment.GetEnvironmentVariable("FURTHERAGENT_DSH_EXE") ?? @"D:\Software\DeepseekHarness\DeepSeek Harness.exe", lifetime.Token) : null;
    if (background is not null) descriptor = background.DescriptorPath;
    if (backend.StartsWith("dsh") && descriptor.Length == 0) descriptor = DshAdapter.FindDescriptor("desktop");
    string? keyFile = backend == "hermes" ? Environment.GetEnvironmentVariable("HERMES_API_KEY_FILE") : null;
    string key = backend != "hermes" ? "" : keyFile is null ? Environment.GetEnvironmentVariable("HERMES_API_KEY") ?? "" : (await File.ReadAllTextAsync(keyFile)).Trim();
    await using IAgentAdapter adapter = backend.StartsWith("dsh") ? new DshAdapter(descriptor, followDesktopRestarts)
        : new HermesAdapter(Environment.GetEnvironmentVariable("HERMES_API_URL") ?? "http://127.0.0.1:8642", key);
    var capabilities = adapter is DshAdapter dsh ? await dsh.ProbeAsync(lifetime.Token) : await ((HermesAdapter)adapter).ProbeAsync(lifetime.Token);
    Console.WriteLine(adapter.Info.Name + " 能力检查通过。");
    if (args.Contains("--probe"))
    {
        Console.WriteLine(capabilities?.ToJsonString());
        Console.WriteLine($"可读取会话数量（最多 1000）：{(await adapter.ListAsync(lifetime.Token)).Count}");
        return;
    }
    using var registry = new RegistryClient(Environment.GetEnvironmentVariable("FURTHERAGENT_SERVER")
        ?? throw new ArgumentException("请配置 FURTHERAGENT_SERVER。"));
    await using var host = new DeviceHost(registry, () => new BorrowedAgentAdapter(adapter), persistentIdentity: true);
    host.Status += Console.WriteLine;
    host.CodeChanged += code => Console.WriteLine($"授权码：{code}（持有码可操作 Agent，请仅提供给可信任的人）");
    await host.StartAsync(Environment.GetEnvironmentVariable("FURTHERAGENT_NAME") ?? defaultName, lifetime.Token);
    Console.WriteLine("接入端已就绪。" + adapter.Info.Detail);
    await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
catch (Exception ex) { Console.Error.WriteLine("接入端启动/运行失败：" + ex.Message); Environment.ExitCode = 1; }
