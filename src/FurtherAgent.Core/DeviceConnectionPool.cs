using System.Security.Cryptography;
using System.Text;

namespace FurtherAgent.Core;

// UI selection is independent of connection lifetime. Failed additions never replace a live device.
public sealed class DeviceConnectionPool<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    private readonly Dictionary<string, T> entries = [];
    private readonly SemaphoreSlim operations = new(1, 1);
    private bool disposed;
    public IReadOnlyDictionary<string, T> Entries { get { lock (entries) return new Dictionary<string, T>(entries); } }
    public static string Identity(string server, string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        new Uri(server.Trim().TrimEnd('/') + '/').AbsoluteUri + "\n" + code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant())));
    public async Task<T> OpenAsync(string server, string code, Func<Task<T>> create)
    {
        string id = Identity(server, code);
        await operations.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (entries) if (entries.TryGetValue(id, out var existing)) return existing;
            var connection = await create();
            lock (entries) entries.Add(id, connection);
            return connection;
        }
        finally { operations.Release(); }
    }
    public async Task CloseAsync(string id)
    {
        await operations.WaitAsync();
        try
        {
            T? connection;
            lock (entries) entries.Remove(id, out connection);
            if (connection is not null) await connection.DisposeAsync();
        }
        finally { operations.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync();
        try
        {
            disposed = true;
            T[] connections;
            lock (entries) { connections = entries.Values.ToArray(); entries.Clear(); }
            await Task.WhenAll(connections.Select(async c => await c.DisposeAsync()));
        }
        finally { operations.Release(); }
    }
}
