using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SetGameServer.GarnetIO;

using SetGameServer.Engine;

/// <summary>
/// Background service that drains game-state and leaderboard snapshots
/// into <see cref="CosmosStore"/> on a low-priority queue. Writes are
/// coalesced per game id so only the latest state is persisted even if
/// mutations arrive faster than Cosmos can absorb.
///
/// Also runs a 24-hour GC sweep that removes Cosmos documents for games
/// inactive for 30 days.
/// </summary>
public sealed class CosmosBackupQueue : BackgroundService, IBackupQueue
{
    private readonly CosmosStore _cosmos;
    private readonly ILogger<CosmosBackupQueue> _log;

    // Coalescing maps — producers overwrite, consumer drains latest.
    private readonly ConcurrentDictionary<string, GameState> _pendingGames = new();
    private volatile Dictionary<string, LeaderboardEntry>? _pendingLeaderboard;

    // Signal channel: a single item means "there's work to do".
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GcInterval = TimeSpan.FromHours(24);
    private const long GcMaxInactivityMs = 30L * 24 * 60 * 60 * 1000; // 30 days

    public CosmosBackupQueue(CosmosStore cosmos, ILogger<CosmosBackupQueue> log)
    {
        _cosmos = cosmos;
        _log = log;
    }

    public void EnqueueGame(GameState state)
    {
        _pendingGames[state.Id] = state;
        _signal.Writer.TryWrite(0);
    }

    public void EnqueueLeaderboard(Dictionary<string, LeaderboardEntry> stats)
    {
        // Snapshot the dictionary so callers can keep mutating the original.
        _pendingLeaderboard = new Dictionary<string, LeaderboardEntry>(stats);
        _signal.Writer.TryWrite(0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("CosmosBackupQueue started");

        // Kick off the GC timer as a parallel task.
        var gcTask = RunGcLoopAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait for a signal or flush interval, whichever comes first.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(FlushInterval);
                try { await _signal.Reader.ReadAsync(cts.Token); }
                catch (OperationCanceledException) { }

                await FlushAsync();
            }

            // Graceful shutdown: drain remaining items.
            await FlushAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            _log.LogInformation("CosmosBackupQueue stopping");
            try { await gcTask; } catch { }
        }
    }

    private async Task FlushAsync()
    {
        // Drain all coalesced game states.
        var gameIds = _pendingGames.Keys.ToArray();
        foreach (var id in gameIds)
        {
            if (_pendingGames.TryRemove(id, out var state))
            {
                try
                {
                    await _cosmos.UpsertGameAsync(state);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cosmos backup failed for game {id}", id);
                    // Re-enqueue so the next flush retries with the latest state.
                    _pendingGames.TryAdd(id, state);
                }
            }
        }

        // Drain leaderboard snapshot.
        var lb = _pendingLeaderboard;
        if (lb != null)
        {
            _pendingLeaderboard = null;
            try
            {
                await _cosmos.UpsertLeaderboardAsync(lb);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cosmos leaderboard backup failed");
                // Re-enqueue for retry.
                _pendingLeaderboard ??= lb;
            }
        }
    }

    private async Task RunGcLoopAsync(CancellationToken ct)
    {
        // Offset the first GC run by a few minutes after startup.
        try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - GcMaxInactivityMs;
                var deleted = await _cosmos.SweepInactiveGamesAsync(cutoff);
                if (deleted > 0)
                    _log.LogInformation("Cosmos GC: deleted {count} inactive games", deleted);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cosmos GC sweep failed");
            }

            try { await Task.Delay(GcInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
