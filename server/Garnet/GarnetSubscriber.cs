using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace SetGameServer.GarnetIO;

/// <summary>
/// Garnet pub/sub bridge. Maintains a single dedicated TCP connection in
/// SUBSCRIBE mode (Garnet uses RESP, but the high-level <c>GarnetClient</c>
/// does not expose a SUBSCRIBE callback API, so we speak raw RESP here). For
/// each channel we hold an in-process fan-out so multiple SSE clients
/// listening on the same game share one Garnet subscription.
/// </summary>
public class GarnetSubscriber : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<GarnetSubscriber> _log;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _readLoop;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly ConcurrentDictionary<string, ChannelFanout> _channels = new();

    public GarnetSubscriber(string host, int port, ILogger<GarnetSubscriber> log)
    {
        _host = host;
        _port = port;
        _log = log;
    }

    public async Task StartAsync()
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port);
        _stream = _tcp.GetStream();
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Subscribe to <paramref name="channel"/>. The returned
    /// <see cref="Subscription"/> exposes a reader of payloads; disposing it
    /// removes the listener and unsubscribes from Garnet when the last
    /// listener is gone.</summary>
    public async Task<Subscription> SubscribeAsync(string channel)
    {
        var fanout = _channels.GetOrAdd(channel, c => new ChannelFanout(c));
        var listener = fanout.AddListener();

        if (Interlocked.Increment(ref fanout.RefCount) == 1)
            await SendCommandAsync("SUBSCRIBE", channel);

        return new Subscription(this, channel, listener);
    }

    private async Task ReleaseAsync(string channel, ChannelFanout.Listener listener)
    {
        if (!_channels.TryGetValue(channel, out var fanout)) return;
        fanout.RemoveListener(listener);
        if (Interlocked.Decrement(ref fanout.RefCount) == 0)
        {
            _channels.TryRemove(channel, out _);
            try { await SendCommandAsync("UNSUBSCRIBE", channel); }
            catch (Exception ex) { _log.LogWarning(ex, "UNSUBSCRIBE failed for {channel}", channel); }
        }
    }

    private async Task SendCommandAsync(params string[] parts)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(parts.Length).Append("\r\n");
        foreach (var p in parts)
            sb.Append('$').Append(Encoding.UTF8.GetByteCount(p)).Append("\r\n").Append(p).Append("\r\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        await _writeGate.WaitAsync();
        try
        {
            await _stream!.WriteAsync(bytes);
            await _stream.FlushAsync();
        }
        finally { _writeGate.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var reader = new RespReader(_stream!);
        try
        {
            while (!_stopCts.IsCancellationRequested)
            {
                var frame = await reader.ReadFrameAsync(_stopCts.Token);
                if (frame is not string[] arr) continue;
                if (arr.Length == 3 && arr[0] == "message")
                {
                    if (_channels.TryGetValue(arr[1], out var fanout))
                        fanout.Publish(arr[2]);
                }
                // SUBSCRIBE/UNSUBSCRIBE acks: ["subscribe", channel, count] — ignore.
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_stopCts.IsCancellationRequested)
        {
            _log.LogError(ex, "Garnet subscriber read loop died");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopCts.Cancel();
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        if (_readLoop != null) try { await _readLoop; } catch { }
    }

    public sealed class Subscription : IAsyncDisposable
    {
        private readonly GarnetSubscriber _owner;
        private readonly string _channel;
        public ChannelFanout.Listener Listener { get; }
        public Subscription(GarnetSubscriber owner, string channel, ChannelFanout.Listener listener)
        {
            _owner = owner; _channel = channel; Listener = listener;
        }
        public ValueTask DisposeAsync() => new(_owner.ReleaseAsync(_channel, Listener));
    }

    public sealed class ChannelFanout
    {
        public readonly string Channel;
        public int RefCount;
        private readonly ConcurrentDictionary<Guid, Listener> _listeners = new();

        public ChannelFanout(string channel) { Channel = channel; }

        public Listener AddListener()
        {
            var l = new Listener();
            _listeners[l.Id] = l;
            return l;
        }

        public void RemoveListener(Listener l)
        {
            _listeners.TryRemove(l.Id, out _);
            l.Complete();
        }

        public void Publish(string payload)
        {
            foreach (var l in _listeners.Values)
                l.Push(payload);
        }

        public sealed class Listener
        {
            public Guid Id { get; } = Guid.NewGuid();
            private readonly System.Threading.Channels.Channel<string> _ch =
                System.Threading.Channels.Channel.CreateUnbounded<string>(
                    new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
            public System.Threading.Channels.ChannelReader<string> Reader => _ch.Reader;
            public void Push(string payload) => _ch.Writer.TryWrite(payload);
            public void Complete() => _ch.Writer.TryComplete();
        }
    }

    /// <summary>Minimal RESP reader: arrays of bulk strings, simple strings,
    /// errors, and integers. Sufficient for SUBSCRIBE/MESSAGE frames.</summary>
    private sealed class RespReader
    {
        private readonly NetworkStream _s;
        private readonly byte[] _buf = new byte[8192];
        private int _len = 0, _pos = 0;
        public RespReader(NetworkStream s) { _s = s; }

        public async Task<object?> ReadFrameAsync(CancellationToken ct)
        {
            var c = await ReadCharAsync(ct);
            return c switch
            {
                '*' => await ReadArrayAsync(ct),
                '$' => await ReadBulkAsync(ct),
                '+' => await ReadLineAsync(ct),
                '-' => await ReadLineAsync(ct),
                ':' => long.Parse(await ReadLineAsync(ct)),
                _ => null,
            };
        }

        private async Task<string[]> ReadArrayAsync(CancellationToken ct)
        {
            var n = int.Parse(await ReadLineAsync(ct));
            if (n < 0) return Array.Empty<string>();
            var arr = new string[n];
            for (int i = 0; i < n; i++)
            {
                var f = await ReadFrameAsync(ct);
                arr[i] = f?.ToString() ?? "";
            }
            return arr;
        }

        private async Task<string?> ReadBulkAsync(CancellationToken ct)
        {
            var n = int.Parse(await ReadLineAsync(ct));
            if (n < 0) return null;
            var bytes = new byte[n];
            int got = 0;
            while (got < n)
            {
                if (_pos >= _len) await FillAsync(ct);
                int take = Math.Min(n - got, _len - _pos);
                Array.Copy(_buf, _pos, bytes, got, take);
                _pos += take; got += take;
            }
            await ReadCharAsync(ct); // \r
            await ReadCharAsync(ct); // \n
            return Encoding.UTF8.GetString(bytes);
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var c = await ReadCharAsync(ct);
                if (c == '\r')
                {
                    await ReadCharAsync(ct); // \n
                    return sb.ToString();
                }
                sb.Append(c);
            }
        }

        private async Task<char> ReadCharAsync(CancellationToken ct)
        {
            if (_pos >= _len) await FillAsync(ct);
            return (char)_buf[_pos++];
        }

        private async Task FillAsync(CancellationToken ct)
        {
            _pos = 0;
            _len = await _s.ReadAsync(_buf, ct);
            if (_len == 0) throw new IOException("Garnet connection closed");
        }
    }
}
