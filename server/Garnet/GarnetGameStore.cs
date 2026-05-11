using System.Net;
using System.Text.Json;
using Garnet.client;

namespace SetGameServer.GarnetIO;

/// <summary>
/// JSON-typed wrapper around the Garnet C# client (<see cref="GarnetClient"/>) for
/// the application's KV + PUBLISH needs. The Garnet C# client exposes
/// <c>StringSetAsync</c>, <c>StringGetAsync</c>, <c>KeyDeleteAsync</c>, and a
/// low-level <c>ExecuteForLongResultAsync</c> we use for PUBLISH and
/// SADD/SREM/SMEMBERS.
/// </summary>
public class GarnetGameStore : IAsyncDisposable
{
    private readonly GarnetClient _client;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GarnetGameStore(string host, int port, ILogger<GarnetGameStore> logger)
    {
        _client = new GarnetClient(new IPEndPoint(ResolveHost(host), port), logger: logger);
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var entry = Dns.GetHostEntry(host);
        return entry.AddressList.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }

    public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);

    public async Task<T?> GetJsonAsync<T>(string key)
    {
        var s = await _client.StringGetAsync(key);
        if (string.IsNullOrEmpty(s)) return default;
        return JsonSerializer.Deserialize<T>(s, _json);
    }

    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, _json);

    public Task SetJsonAsync<T>(string key, T value)
        => _client.StringSetAsync(key, Serialize(value));

    public Task DeleteAsync(string key) => _client.KeyDeleteAsync(key);

    public Task PublishAsync(string channel, string payload)
        => _client.ExecuteForLongResultAsync("PUBLISH", new[] { channel, payload });

    public Task<long> SAddAsync(string key, string member)
        => _client.ExecuteForLongResultAsync("SADD", new[] { key, member });

    public Task<long> SRemAsync(string key, string member)
        => _client.ExecuteForLongResultAsync("SREM", new[] { key, member });

    public async Task<string[]> SMembersAsync(string key)
        => await _client.ExecuteForStringArrayResultAsync("SMEMBERS", new[] { key }) ?? Array.Empty<string>();

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>Acquire the per-process write gate, providing read-modify-write
    /// serialization for callers. (This server is the sole writer for its
    /// games, so coarse-grained locking is sufficient.)</summary>
    public async Task<IDisposable> LockAsync()
    {
        await _gate.WaitAsync();
        return new Releaser(_gate);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _s;
        public Releaser(SemaphoreSlim s) => _s = s;
        public void Dispose() => _s.Release();
    }
}
