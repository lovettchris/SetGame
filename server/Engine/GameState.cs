namespace SetGameServer.Engine;

public class PlayerState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SetsFound { get; set; }
    /// <summary>False when the player has left the game. Their row is kept
    /// (and dimmed on clients) so their score is preserved if they rejoin.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Most recently reported client→server round-trip time (ms).
    /// Surfaced in the player bubble so everyone can see who has the worst
    /// connection. Zero means no measurement received yet.</summary>
    public int PingMs { get; set; }
}

public class GameState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Deck { get; set; } = new();
    public List<string?> Board { get; set; } = new();
    public Dictionary<string, PlayerState> Players { get; set; } = new();
    public string Status { get; set; } = "active";
    public long StartedAt { get; set; }
    /// <summary>Unix-ms timestamp of the most recent mutation. Used by the
    /// GC sweep to drop games no one has touched in a while.</summary>
    public long LastActivityAt { get; set; }
    public long Version { get; set; }
    public List<int> LastDealtIndices { get; set; } = new();

    /// <summary>Initial 81-card shuffle for this game, captured at
    /// creation time so the entire game can be replayed/exported. Never
    /// mutated after construction.</summary>
    public List<string> InitialDeck { get; set; } = new();

    /// <summary>Append-only history of accepted set submissions (regular
    /// finds and hint-driven completions). Used by the export endpoint.
    /// Stores card ids (not board indices) so the record is meaningful
    /// independent of the live board layout.</summary>
    public List<MoveRecord> Moves { get; set; } = new();

    /// <summary>Player ids who have voted to reveal a hint on the current
    /// board. Cleared whenever the board changes.</summary>
    public HashSet<string> HintRequests { get; set; } = new();

    /// <summary>The 3-card set chosen for hinting on the current board, or
    /// empty when no hint is in progress. Cards from this set are revealed
    /// one at a time as players vote.</summary>
    public List<int> HintTarget { get; set; } = new();

    /// <summary>Card indices already revealed (a prefix of <see
    /// cref="HintTarget"/>). Length 0..3.</summary>
    public List<int> HintIndices { get; set; } = new();

    /// <summary>True once at least one card has been revealed.</summary>
    public bool HintRevealed => HintIndices.Count > 0;

    /// <summary>Last user-facing announcement attached to a state update.
    /// Broadcast to every client so e.g. "Alice asked for a hint (1/2)"
    /// shows up everywhere, not just for the player who clicked.</summary>
    public BroadcastEvent? LastBroadcast { get; set; }

    public int DeckRemaining => Deck.Count;
}

public record GameSummary(string Id, string Name, int PlayerCount, long StartedAt, string Status, bool HasReplay = false);

/// <summary>All-time wins for one player, stored in the global leaderboard.
/// Score accumulates opponents defeated (players in game minus 1) per win.</summary>
public record LeaderboardEntry(string PlayerId, string PlayerName, int Wins, int Score, string? LastReplayGameId = null);

/// <summary>One accepted set submission. Recorded for export/replay.
/// <see cref="Kind"/> is "found" for a normal scoring find or "hint"
/// when the third hint reveal auto-submitted the set.</summary>
public class MoveRecord
{
    public long Timestamp { get; set; }
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string[] Cards { get; set; } = Array.Empty<string>();
    public string Kind { get; set; } = "found";
}

/// <summary>
/// A short, user-facing announcement attached to a state update so it is
/// pushed to every connected client (not just the actor).
/// </summary>
public class BroadcastEvent
{
    public string Message { get; set; } = "";
    public string Kind { get; set; } = "info";
    public long Version { get; set; }
    /// <summary>If the broadcast was triggered by a scoring event, the
    /// player whose score changed — used by clients to sparkle the row.</summary>
    public string? ScoredPlayerId { get; set; }
    /// <summary>When a single race resolves multiple successful sets at
    /// once, all winning players are listed here so every row sparkles.
    /// <see cref="ScoredPlayerId"/> still carries the first winner for
    /// older clients.</summary>
    public string[]? ScoredPlayerIds { get; set; }
    /// <summary>If the broadcast was a successful set submission, the
    /// three board indices that formed the set. Clients use this to
    /// briefly highlight the cards before they animate away.</summary>
    public int[]? FoundSetIndices { get; set; }
}
