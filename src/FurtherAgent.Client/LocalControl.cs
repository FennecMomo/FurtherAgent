using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace FurtherAgent.Client;

internal sealed class LocalControl(Func<string, Task<object>> handle) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    public void Start() => _ = RunAsync();
    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream($"FurtherAgent.Control.{Environment.ProcessId}", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string? command = await reader.ReadLineAsync(timeout.Token);
                var result = await handle(command ?? "");
                await writer.WriteLineAsync(JsonSerializer.Serialize(result));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch { /* A disconnected local controller must not terminate the UI. */ }
        }
    }
    public void Dispose() => stop.Cancel();
}
