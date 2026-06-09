using SetGameServer.Engine;
using SetGameServer.GarnetIO;
using System.Collections.Concurrent;

namespace SetGameServer.Games;

/// <summary>
/// Orchestrates game lifecycle on top of <see cref="GarnetGameStore"/>:
/// games are stored as JSON under <c>game:{id}:state</c>; the lobby index is
/// <c>games:index</c> (a Garnet SET); per-game updates are broadcast on the
/// pub/sub channel <c>game:{id}:events</c>.
/// </summary>
public class GameService
{
    private readonly GarnetGameStore _store;
    private readonly IBackupQueue _backup;
    private readonly Random _rng = new();

    // Per-game ping-fairness state. Players have varying network latency;
    // when several race to find the same set, the server collects all
    // submissions arriving within a short window (sized by the slowest
    // known ping), then sorts them by their effective click time
    // (server-arrival minus ping/2) to decide who really won.
    private readonly ConcurrentDictionary<string, RaceQueue> _races = new();
    // Most recently measured RTT per player, per game. Used to size the
    // race window so we wait long enough for the slowest player's
    // submission to arrive.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _pings = new();
    // Server timestamp (unix ms) of each player's most recent ping report.
    // The inactivity sweeper uses this to mark silent players inactive
    // without re-broadcasting state on every ping.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _lastPingAt = new();
    private const int MinWindowMs = 50;
    private const int MaxWindowMs = 500;
    private const int DefaultWindowMs = 200;
    private const int InactivityTimeoutMs = 30_000;
    private const int InactivitySweepMs = 5_000;
    private const int LobbyMaxGames = 1000;
    private const long GameTtlMs = 24L * 60 * 60 * 1000; // 1 day
    private const int GcSweepMs = 10 * 60 * 1000;        // every 10 min
    private readonly Timer _inactivityTimer;
    private readonly Timer _gcTimer;

    public GameService(GarnetGameStore store, IBackupQueue backup)
    {
        _store = store;
        _backup = backup;
        _inactivityTimer = new Timer(_ => _ = SweepInactivePlayersAsync(),
            null, InactivitySweepMs, InactivitySweepMs);
        _gcTimer = new Timer(_ => _ = SweepStaleGamesAsync(),
            null, GcSweepMs, GcSweepMs);
    }

    private static string StateKey(string id) => $"game:{id}:state";
    public static string Channel(string id) => $"game:{id}:events";
    private const string IndexKey = "games:index";
    private const string LeaderboardKey = "leaderboard:stats";
    private static string ReplayKey(string id) => $"replay:{id}";
    private const string ReplayIndexKey = "replays:index";

    public async Task<GameSummary> CreateAsync(string friendlyName)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var state = SetGameEngine.NewGame(id, friendlyName, _rng);
        state.LastActivityAt = state.StartedAt;
        using (await _store.LockAsync())
        {
            await _store.SetJsonAsync(StateKey(id), state);
            await _store.SAddAsync(IndexKey, id);
        }
        _backup.EnqueueGame(state);
        return ToSummary(state);
    }

    public async Task<List<GameSummary>> ListAsync()
    {
        var ids = await _store.SMembersAsync(IndexKey);
        var replayIds = new HashSet<string>(await _store.SMembersAsync(ReplayIndexKey));
        var summaries = new List<GameSummary>(ids.Length);
        foreach (var id in ids)
        {
            var state = await _store.GetJsonAsync<GameState>(StateKey(id));
            if (state == null)
            {
                // stale index entry — clean it up
                await _store.SRemAsync(IndexKey, id);
                continue;
            }
            summaries.Add(ToSummary(state, replayIds.Contains(id)));
        }
        return summaries
            .OrderByDescending(s => s.StartedAt)
            .Take(LobbyMaxGames)
            .ToList();
    }

    public async Task<GameState?> GetAsync(string id)
    {
        var s = await _store.GetJsonAsync<GameState>(StateKey(id));
        if (s != null) BackfillLegacyExportFields(s);
        return s;
    }

    /// <summary>Games created before <see cref="GameState.InitialDeck"/> and
    /// <see cref="GameState.Moves"/> existed in the schema deserialize with
    /// those fields empty. If no submissions have been made yet (board is
    /// fully populated and deck + 12 == 81), reconstruct the original
    /// shuffle from current state. <see cref="GameState.Moves"/> always
    /// starts empty so future submissions are recorded.</summary>
    private static void BackfillLegacyExportFields(GameState s)
    {
        if (s.InitialDeck.Count == 0)
        {
            var nonNullBoard = s.Board.Where(c => c != null).Cast<string>().ToList();
            // Reverse the board because Deal pops the last card from the
            // deck first, so board[0] originally sat on top of the deck.
            if (nonNullBoard.Count + s.Deck.Count == 81 && s.Board.All(c => c != null))
            {
                var rebuilt = new List<string>(81);
                rebuilt.AddRange(s.Deck);
                for (int i = nonNullBoard.Count - 1; i >= 0; i--) rebuilt.Add(nonNullBoard[i]);
                s.InitialDeck = rebuilt;
            }
        }
        s.Moves ??= new List<MoveRecord>();
        if (s.LastActivityAt == 0) s.LastActivityAt = s.StartedAt;
    }

    public async Task<GameState?> JoinAsync(string id, string playerId, string playerName)
    {
        return await MutateAsync(id, state =>
        {
            SetGameEngine.AddPlayer(state, playerId, playerName);
            return true;
        });
    }

    public async Task LeaveAsync(string id, string playerId)
    {
        // Drop the player's recorded ping — they can't influence the
        // race window after they leave.
        if (_pings.TryGetValue(id, out var perPlayer))
            perPlayer.TryRemove(playerId, out _);
        if (_lastPingAt.TryGetValue(id, out var seen))
            seen.TryRemove(playerId, out _);

        await MutateAsync(id, state =>
        {
            SetGameEngine.RemovePlayer(state, playerId);
            return true;
        });
    }

    /// <summary>Records the most recently measured client RTT for this
    /// player in this game. Used to size the race-fairness window.</summary>
    public void RecordPing(string gameId, string playerId, int pingMs)
    {
        if (pingMs < 0) pingMs = 0;
        if (pingMs > MaxWindowMs * 4) pingMs = MaxWindowMs * 4;
        var perPlayer = _pings.GetOrAdd(gameId, _ => new ConcurrentDictionary<string, int>());
        perPlayer[playerId] = pingMs;
    }

    /// <summary>Records the ping (as <see cref="RecordPing"/>) and also
    /// persists it onto the player's state so every client sees it in the
    /// player bubble. Called from the periodic /ping endpoint. Also
    /// reactivates a player previously timed out for inactivity.</summary>
    public async Task RecordPingAsync(string gameId, string playerId, int pingMs)
    {
        RecordPing(gameId, playerId, pingMs);
        var lastSeen = _lastPingAt.GetOrAdd(gameId, _ => new ConcurrentDictionary<string, long>());
        lastSeen[playerId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await MutateAsync(gameId, state =>
        {
            if (!state.Players.TryGetValue(playerId, out var p)) return false;
            bool changed = false;
            if (p.PingMs != pingMs) { p.PingMs = pingMs; changed = true; }
            if (!p.Active) { p.Active = true; changed = true; }
            if (changed) state.Version++;
            return changed;
        });
    }

    /// <summary>Periodic sweep that marks players who haven't pinged in
    /// <see cref="InactivityTimeoutMs"/> ms as inactive — covers the case
    /// where a browser tab is closed or the network drops without a clean
    /// /leave call.</summary>
    private async Task SweepInactivePlayersAsync()
    {
        try
        {
            var ids = await _store.SMembersAsync(IndexKey);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var id in ids)
            {
                await MutateAsync(id, state =>
                {
                    bool changed = false;
                    foreach (var p in state.Players.Values)
                    {
                        bool active = false;
                        if (_lastPingAt.TryGetValue(id, out var lastSeen) && lastSeen.TryGetValue(p.Id, out var ts))
                        {
                            if (now - ts < InactivityTimeoutMs)
                            {
                                active = true;
                            }
                        }
                        if (p.Active != active)
                        {
                            changed = true;
                            p.Active = active;
                        }                        
                    }
                    if (changed) state.Version++;
                    return changed;
                });
            }
        }
        catch
        {
            // Best-effort cleanup; swallow to keep the timer alive.
        }
    }

    /// <summary>Periodic GC: drop games whose last activity is older than
    /// <see cref="GameTtlMs"/>, removing the state blob, the lobby index
    /// entry, and any in-memory ping/race bookkeeping.</summary>
    private async Task SweepStaleGamesAsync()
    {
        try
        {
            var ids = await _store.SMembersAsync(IndexKey);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var id in ids)
            {
                var state = await _store.GetJsonAsync<GameState>(StateKey(id));
                if (state == null)
                {
                    await _store.SRemAsync(IndexKey, id);
                    DropInMemory(id);
                    continue;
                }
                var lastActivity = state.LastActivityAt > 0 ? state.LastActivityAt : state.StartedAt;
                if (now - lastActivity > GameTtlMs)
                {
                    using (await _store.LockAsync())
                    {
                        await _store.DeleteAsync(StateKey(id));
                        await _store.SRemAsync(IndexKey, id);
                        await _store.DeleteAsync(ReplayKey(id));
                        await _store.SRemAsync(ReplayIndexKey, id);
                    }
                    DropInMemory(id);
                }
            }
        }
        catch
        {
            // Best-effort cleanup; swallow to keep the timer alive.
        }
    }

    private void DropInMemory(string id)
    {
        _races.TryRemove(id, out _);
        _pings.TryRemove(id, out _);
        _lastPingAt.TryRemove(id, out _);
    }

    private int CurrentMaxPing(string gameId)
    {
        if (!_pings.TryGetValue(gameId, out var perPlayer) || perPlayer.IsEmpty)
            return DefaultWindowMs;
        return perPlayer.Values.Max();
    }

    public async Task<(GameState? state, SetGameEngine.SubmitOutcome outcome)>
        SubmitAsync(string id, string playerId, int[] indices)
    {
        // Use the most recently reported ping for this player (collected
        // by the periodic /ping endpoint) to size the race window and
        // back-date the click time below.
        var pingMs = 0;
        if (_pings.TryGetValue(id, out var perPlayer)
            && perPlayer.TryGetValue(playerId, out var p)) pingMs = p;

        // Pre-validate against the current state so an invalid submission
        // (wrong shape, missing cards, not a Set) gets an immediate error
        // and never enters the race queue. This way every queued entry
        // is known to be a real Set, and a losing player can be told
        // unambiguously that someone else beat them to it.
        var snapshot = await GetAsync(id);
        if (snapshot == null)
            return (null, new SetGameEngine.SubmitOutcome(
                false, "Game not found.", "error", indices));
        var rejection = SetGameEngine.ValidateSubmission(snapshot, playerId, indices);
        if (rejection != null) return (snapshot, rejection);

        var arrival = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Effective time the player clicked the third card, estimated as
        // server arrival minus half the round-trip. A higher-ping client
        // who arrives "later" in wall time may have actually clicked
        // first — the adjustment removes that unfairness.
        var adjusted = arrival - pingMs / 2;

        var tcs = new TaskCompletionSource<(GameState?, SetGameEngine.SubmitOutcome)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingSubmission(playerId, indices, adjusted, tcs);

        var race = _races.GetOrAdd(id, _ => new RaceQueue());
        bool startTimer;
        int windowMs;
        lock (race.Lock)
        {
            race.Pending.Add(pending);
            startTimer = !race.Scheduled;
            race.Scheduled = true;
            windowMs = Math.Clamp(CurrentMaxPing(id), MinWindowMs, MaxWindowMs);
        }

        if (startTimer)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(windowMs);
                    await ResolveRaceAsync(id, race);
                }
                catch (Exception ex)
                {
                    // Surface a generic failure to anyone still waiting so
                    // their HTTP call doesn't hang forever.
                    List<PendingSubmission> stranded;
                    lock (race.Lock)
                    {
                        stranded = race.Pending.ToList();
                        race.Pending.Clear();
                        race.Scheduled = false;
                    }
                    foreach (var s in stranded)
                    {
                        s.Tcs.TrySetResult((null, new SetGameEngine.SubmitOutcome(
                            false, $"Server error: {ex.Message}", "error", s.Indices)));
                    }
                }
            });
        }

        return await tcs.Task;
    }

    private async Task ResolveRaceAsync(string id, RaceQueue race)
    {
        List<PendingSubmission> batch;
        lock (race.Lock)
        {
            batch = race.Pending.ToList();
            race.Pending.Clear();
            race.Scheduled = false;
        }

        // Sort by effective click time so the player who really clicked
        // first is processed first, regardless of network jitter.
        var sorted = batch.OrderBy(p => p.AdjustedTime).ToList();

        // Process every submission in this race inside ONE state mutation
        // so the broadcast is a single combined event. Two players who
        // each found independent (non-overlapping) sets in the same race
        // window animate together — clients see all the new cards at once
        // instead of one set's animation getting overwritten by the next
        // SSE push.
        var outcomes = new Dictionary<PendingSubmission, SetGameEngine.SubmitOutcome>();
        var state = await MutateAsync(id, s =>
        {
            var combinedDealt = new List<int>();
            var combinedFound = new List<int>();
            var scorers = new List<(string Id, string Name)>();
            bool anyAccepted = false;

            foreach (var sub in sorted)
            {
                var winnerSub = sorted.FirstOrDefault(o =>
                    o != sub
                    && o.AdjustedTime < sub.AdjustedTime
                    && o.Indices.Intersect(sub.Indices).Any());

                if (winnerSub != null)
                {
                    var wname = s.Players.TryGetValue(winnerSub.PlayerId, out var wp)
                        ? wp.Name : "Someone";
                    outcomes[sub] = new SetGameEngine.SubmitOutcome(
                        false, $"{wname} beat you, sorry", "error", sub.Indices);
                    continue;
                }

                var outcome = SetGameEngine.SubmitSet(s, sub.PlayerId, sub.Indices);
                outcomes[sub] = outcome;
                if (!outcome.Accepted) continue;

                anyAccepted = true;
                // SubmitSet rewrote LastDealtIndices/LastBroadcast with
                // just this set's info — capture it before the next
                // SubmitSet (if any) overwrites them.
                combinedDealt.AddRange(s.LastDealtIndices);
                combinedFound.AddRange(sub.Indices);
                if (s.Players.TryGetValue(sub.PlayerId, out var pp))
                    scorers.Add((pp.Id, pp.Name));
            }

            if (anyAccepted)
            {
                s.LastDealtIndices = combinedDealt;
                var msg = string.Join(", ", scorers.Select(x => $"{x.Name} found a set!"));
                s.LastBroadcast = new BroadcastEvent
                {
                    Message = msg,
                    Kind = "success",
                    Version = s.Version,
                    ScoredPlayerId = scorers[0].Id,
                    ScoredPlayerIds = scorers.Select(x => x.Id).ToArray(),
                    FoundSetIndices = combinedFound.ToArray(),
                };
            }

            return anyAccepted;
        });

        foreach (var sub in sorted)
            sub.Tcs.TrySetResult((state, outcomes[sub]));
    }

    private sealed class RaceQueue
    {
        public readonly object Lock = new();
        public readonly List<PendingSubmission> Pending = new();
        public bool Scheduled;
    }

    private sealed record PendingSubmission(
        string PlayerId,
        int[] Indices,
        long AdjustedTime,
        TaskCompletionSource<(GameState?, SetGameEngine.SubmitOutcome)> Tcs);

    public async Task<(GameState? state, SetGameEngine.HintOutcome outcome)>
        HintAsync(string id, string playerId, int[] selection)
    {
        SetGameEngine.HintOutcome? captured = null;
        var state = await MutateAsync(id, s =>
        {
            captured = SetGameEngine.Hint(s, playerId, selection);
            return true;
        });
        return (state, captured!);
    }

    public async Task<(GameState? state, SetGameEngine.DealOutcome outcome)>
        DealAsync(string id)
    {
        SetGameEngine.DealOutcome? captured = null;
        var state = await MutateAsync(id, s =>
        {
            captured = SetGameEngine.DealMore(s);
            return true;
        });
        return (state, captured!);
    }

    public async Task<GameState?> NewRoundAsync(string id)
    {
        return await MutateAsync(id, state =>
        {
            var fresh = SetGameEngine.NewGame(state.Id, state.Name, _rng);
            // Brand-new round: drop ALL players (and their pings). Each
            // connected client will detect that it is no longer in the
            // player list on the next state push and auto-rejoin via
            // the SSE handler — that way scores reset cleanly and the
            // player order reflects who is actually still here.
            _pings.TryRemove(id, out _);
            _lastPingAt.TryRemove(id, out _);
            state.Deck = fresh.Deck;
            state.Board = fresh.Board;
            state.Status = fresh.Status;
            state.LastDealtIndices = fresh.LastDealtIndices;
            state.StartedAt = fresh.StartedAt;
            state.Players = new Dictionary<string, PlayerState>();
            state.HintRequests = new HashSet<string>();
            state.HintTarget = new List<int>();
            state.HintIndices = new List<int>();
            state.InitialDeck = fresh.InitialDeck;
            state.Moves = new List<MoveRecord>();
            state.Version++;
            // Tag the broadcast with kind="new-round" so every client
            // can play the full board-clear + reshuffle animation.
            state.LastBroadcast = new BroadcastEvent
            {
                Message = "New round!",
                Kind = "new-round",
                Version = state.Version,
            };
            return true;
        });
    }

    /// <summary>Acquire the store lock, mutate the loaded state, and (when
    /// <paramref name="mutate"/> returns true) persist + publish. Returning
    /// false skips persistence — used when a submission is rejected without
    /// changing game state (e.g. a lost race). If the game transitions from
    /// "active" to "ended" the leaderboard is updated within the same lock.</summary>
    private async Task<GameState?> MutateAsync(string id, Func<GameState, bool> mutate)
    {
        using (await _store.LockAsync())
        {
            var state = await _store.GetJsonAsync<GameState>(StateKey(id));
            if (state == null) return null;
            BackfillLegacyExportFields(state);
            var prevStatus = state.Status;
            var persist = mutate(state);
            if (persist)
            {
                state.LastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await _store.SetJsonAsync(StateKey(id), state);
                await _store.PublishAsync(Channel(id), _store.Serialize(state));

                // Record the winner the first (and only) time this game ends.
                if (prevStatus == "active" && state.Status == "ended")
                {
                    await UpdateLeaderboardAsync(state);
                    await PersistReplayAsync(state);
                }

                _backup.EnqueueGame(state);
            }
            return state;
        }
    }

    /// <summary>Called inside the store lock when a game transitions to
    /// "ended". Finds the player(s) with the most sets found and increments
    /// their all-time win count in the global leaderboard.</summary>
    private async Task UpdateLeaderboardAsync(GameState state)
    {
        if (!state.Players.Any()) return;
        var maxSets = state.Players.Values.Max(p => p.SetsFound);
        if (maxSets == 0) return;
        var winners = state.Players.Values.Where(p => p.SetsFound == maxSets);

        // Score per win = number of opponents defeated (total players minus the winner).
        var opponents = Math.Max(0, state.Players.Count - 1);

        var stats = await _store.GetJsonAsync<Dictionary<string, LeaderboardEntry>>(LeaderboardKey)
                    ?? new Dictionary<string, LeaderboardEntry>();
        foreach (var w in winners)
        {
            stats[w.Id] = stats.TryGetValue(w.Id, out var e)
                ? e with { PlayerName = w.Name, Wins = e.Wins + 1, Score = e.Score + opponents, LastReplayGameId = state.Id }
                : new LeaderboardEntry(w.Id, w.Name, 1, opponents, state.Id);
        }
        await _store.SetJsonAsync(LeaderboardKey, stats);
        _backup.EnqueueLeaderboard(stats);
    }

    public async Task<List<LeaderboardEntry>> GetLeaderboardAsync()
    {
        var stats = await _store.GetJsonAsync<Dictionary<string, LeaderboardEntry>>(LeaderboardKey)
                    ?? new Dictionary<string, LeaderboardEntry>();
        return stats.Values
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.Wins)
            .Take(10)
            .ToList();
    }

    private static GameSummary ToSummary(GameState s, bool hasReplay = false)
        => new(s.Id, s.Name, s.Players.Values.Count(p => p.Active), s.StartedAt, s.Status, hasReplay);

    // ──────────────────── Replay persistence ────────────────────────────────

    private object BuildExportObject(GameState s) => new
    {
        id = s.Id,
        name = s.Name,
        status = s.Status,
        startedAt = s.StartedAt,
        exportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        playerCount = s.Players.Count,
        initialDeck = s.InitialDeck,
        players = s.Players.Values.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            setsFound = p.SetsFound,
        }),
        moves = s.Moves,
    };

    /// <summary>Persists a replay snapshot immediately after a game ends.
    /// Called inside the store lock from <see cref="MutateAsync"/>.</summary>
    private async Task PersistReplayAsync(GameState state)
    {
        var json = _store.Serialize(BuildExportObject(state));
        await _store.SetStringAsync(ReplayKey(state.Id), json);
        await _store.SAddAsync(ReplayIndexKey, state.Id);
    }

    /// <summary>Returns the raw replay JSON for the given game. Falls back
    /// to a live export when no persisted replay exists (in-progress or
    /// legacy game).</summary>
    public async Task<string?> GetReplayJsonAsync(string id)
    {
        var raw = await _store.GetStringAsync(ReplayKey(id));
        if (raw != null) return raw;
        // Fall back: build from current live state (in-progress or legacy)
        var s = await GetAsync(id);
        if (s == null) return null;
        return _store.Serialize(BuildExportObject(s));
    }

    private sealed class ReplayMeta
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long StartedAt { get; set; }
        public int PlayerCount { get; set; }
    }

    /// <summary>Returns summary metadata for all persisted replays,
    /// ordered newest-first.</summary>
    public async Task<List<object>> GetReplaysAsync()
    {
        var ids = await _store.SMembersAsync(ReplayIndexKey);
        var results = new List<ReplayMeta>(ids.Length);
        foreach (var id in ids)
        {
            var meta = await _store.GetJsonAsync<ReplayMeta>(ReplayKey(id));
            if (meta == null)
            {
                // Stale index entry — clean up.
                await _store.SRemAsync(ReplayIndexKey, id);
                continue;
            }
            results.Add(meta);
        }
        return results
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (object)new { r.Id, r.Name, r.StartedAt, r.PlayerCount })
            .ToList();
    }
}
