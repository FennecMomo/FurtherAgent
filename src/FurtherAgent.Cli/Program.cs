using FurtherAgent.Cli;
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
return await CliRunner.RunAsync(args, Console.Out, Console.Error, shutdown.Token);
