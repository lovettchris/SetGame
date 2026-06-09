namespace SetGameServer.Engine;

/// <summary>
/// Pure, stateless game-rule operations on a <see cref="GameState"/>.
/// Mutating operations return an outcome describing the user-facing effect; the
/// caller is responsible for persisting the new state.
/// </summary>
public static class SetGameEngine
{
    public const int InitialDeal = 12;
    public const int MaxBoard = 18;

    public record SubmitOutcome(bool Accepted, string Message, string Kind, int[] Indices);
    public record HintOutcome(bool Success, string Message, string Kind, int[] Indices,
                              int Requested, int Required, bool Revealed);
    public record DealOutcome(bool Success, string Message, string Kind);

    public static GameState NewGame(string id, string name, Random rng)
    {
        var deck = Cards.NewShuffledDeck(rng);
        // Snapshot the full shuffle before any cards are dealt so the
        // initial state can be reconstructed for an export/replay.
        var initialDeck = new List<string>(deck);
        var board = new List<string?>();
        Deal(board, deck, InitialDeal);
        return new GameState
        {
            Id = id,
            Name = name,
            Deck = deck,
            Board = board,
            InitialDeck = initialDeck,
            Status = "active",
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Version = 1,
        };
    }

    public static void AddPlayer(GameState s, string playerId, string name)
    {
        if (s.Players.TryGetValue(playerId, out var existing))
        {
            // Rejoin — keep score/sets/penalties; reactivate; refresh name.
            var wasInactive = !existing.Active;
            existing.Active = true;
            if (!string.IsNullOrEmpty(name)) existing.Name = name;
            if (wasInactive)
            {
                s.LastDealtIndices.Clear();
                s.Version++;
                Announce(s, $"{existing.Name} rejoined the game", "info");
            }
        }
        else
        {
            s.Players[playerId] = new PlayerState { Id = playerId, Name = name };
            s.LastDealtIndices.Clear();
            s.Version++;
            Announce(s, $"{name} joined the game", "info");
        }
    }

    public static void RemovePlayer(GameState s, string playerId)
    {
        // Don't actually drop the player — keep their score around in case
        // they rejoin. Just mark inactive and drop their hint vote so the
        // remaining (active) players can still reach quorum.
        if (!s.Players.TryGetValue(playerId, out var p) || !p.Active) return;
        p.Active = false;
        s.HintRequests.Remove(playerId);
        s.LastDealtIndices.Clear();
        s.Version++;
        Announce(s, $"{p.Name} left the game", "info");
        // Departure may have satisfied the threshold for the remaining
        // active players — try to reveal so nobody is stuck waiting on a
        // ghost.
        TryRevealHint(s, Array.Empty<int>());
    }

    private static int ActivePlayerCount(GameState s)
        => s.Players.Values.Count(p => p.Active);

    private static void Deal(List<string?> board, List<string> deck, int n)
    {
        var count = Math.Min(n, deck.Count);
        for (int i = 0; i < count; i++)
        {
            board.Add(deck[^1]);
            deck.RemoveAt(deck.Count - 1);
        }
    }

    public static List<int[]> FindAllSets(IList<string?> board)
    {
        var sets = new List<int[]>();
        for (int i = 0; i < board.Count - 2; i++)
        {
            if (board[i] == null) continue;
            for (int j = i + 1; j < board.Count - 1; j++)
            {
                if (board[j] == null) continue;
                for (int k = j + 1; k < board.Count; k++)
                {
                    if (board[k] == null) continue;
                    if (Cards.IsSet(board[i]!, board[j]!, board[k]!))
                        sets.Add(new[] { i, j, k });
                }
            }
        }
        return sets;
    }

    /// <summary>Submit three card indices as a candidate set.</summary>
    public static SubmitOutcome SubmitSet(GameState s, string playerId, int[] indices)
        => SubmitSet(s, playerId, indices, awardScore: true);

    /// <summary>Validates a submission without mutating state. Returns null
    /// when the submission is well-formed and forms a Set on the current
    /// board; otherwise returns the rejection outcome the caller should
    /// surface to the player.</summary>
    public static SubmitOutcome? ValidateSubmission(GameState s, string playerId, int[] indices)
    {
        if (indices.Length != 3 || indices.Distinct().Count() != 3)
            return new(false, "Selection must contain three distinct cards.", "error", indices);

        if (indices.Any(i => i < 0 || i >= s.Board.Count || s.Board[i] == null))
            return new(false, "One of the selected cards is no longer on the board.", "error", indices);

        if (!s.Players.ContainsKey(playerId))
            return new(false, "Player has not joined this game.", "error", indices);

        var c1 = s.Board[indices[0]]!;
        var c2 = s.Board[indices[1]]!;
        var c3 = s.Board[indices[2]]!;
        if (!Cards.IsSet(c1, c2, c3))
            return new(false, $"Not a Set: {Cards.WhyNotSet(c1, c2, c3)}", "error", indices);

        return null;
    }

    private static SubmitOutcome SubmitSet(GameState s, string playerId, int[] indices, bool awardScore)
    {
        s.LastDealtIndices.Clear();
        if (indices.Length != 3 || indices.Distinct().Count() != 3)
            return new(false, "Selection must contain three distinct cards.", "error", indices);

        if (indices.Any(i => i < 0 || i >= s.Board.Count || s.Board[i] == null))
            return new(false, "One of the selected cards is no longer on the board.", "error", indices);

        if (!s.Players.TryGetValue(playerId, out var player))
            return new(false, "Player has not joined this game.", "error", indices);

        var c1 = s.Board[indices[0]]!;
        var c2 = s.Board[indices[1]]!;
        var c3 = s.Board[indices[2]]!;

        if (!Cards.IsSet(c1, c2, c3))
        {
            s.Version++;
            var why = $"Not a Set: {Cards.WhyNotSet(c1, c2, c3)}";
            // Don't broadcast — the rejection is shown only to the player
            // who made the bad selection (kind=error in their applyResult).
            return new(false, why, "error", indices);
        }

        // Hint-driven completions don't award set credit — the hint UI
        // walked the whole table to the answer, so it isn't a real find.
        // The board still advances and the highlight still fires so
        // everyone sees the cards before they flip away. The sparkle path
        // is skipped (no scoredPlayerId) since nobody actually scored.
        if (awardScore)
        {
            player.SetsFound++;
        }
        // Record the move in the append-only history before we clear the
        // cards from the board, so the export captures the exact card
        // ids the player selected (indices alone become meaningless once
        // the board collapses or refills).
        s.Moves.Add(new MoveRecord
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PlayerId = awardScore ? playerId : "hint",
            PlayerName = awardScore ? player.Name : "hint",
            Cards = new[] { c1, c2, c3 },
            Kind = awardScore ? "found" : "hint",
        });
        foreach (var idx in indices) s.Board[idx] = null;

        FillBlanks(s, indices);
        ResetHint(s);
        s.Version++;
        Announce(s, "", "success",
                 scoredPlayerId: awardScore ? playerId : null,
                 foundSetIndices: indices);
        if (s.DeckRemaining == 0 && FindAllSets(s.Board).Count == 0)
            s.Status = "ended";

        var msg = awardScore
            ? $"{player.Name} found a set!"
            : "Hint completed the set";
        return new(true, msg, "success", indices);
    }

    private static void FillBlanks(GameState s, int[] blankIndices)
    {
        s.LastDealtIndices.Clear();

        if (s.Deck.Count >= 3 && s.Board.Count <= 12)
        {
            foreach (var idx in blankIndices)
            {
                s.Board[idx] = s.Deck[^1];
                s.Deck.RemoveAt(s.Deck.Count - 1);
                s.LastDealtIndices.Add(idx);
            }
        }
        else if (s.Deck.Count >= 3)
        {
            // Board is in the expanded layout (>12 cards) and the deck
            // still has cards. Collapse rule: every card in the base
            // 4×3 layout (indices 0..11) MUST stay in its original slot.
            // Holes left by the matched set in [0..11] are filled by
            // pulling cards from the right-most extras column; then any
            // null slots in the extras region are removed so the column
            // collapses cleanly. Cards in [0..11] never move.
            foreach (var hole in blankIndices.Where(i => i < 12).OrderBy(i => i))
            {
                int srcIdx = -1;
                for (int i = s.Board.Count - 1; i >= 12; i--)
                {
                    if (s.Board[i] != null) { srcIdx = i; break; }
                }
                if (srcIdx < 0) break;
                s.Board[hole] = s.Board[srcIdx];
                s.Board[srcIdx] = null;
            }
            for (int i = s.Board.Count - 1; i >= 12; i--)
            {
                if (s.Board[i] == null) s.Board.RemoveAt(i);
            }
        }
        // else: deck exhausted; leave nulls in place to preserve spatial layout
    }

    /// <summary>Cast a hint vote. Each round of voting (requiring every
    /// active player to click) reveals the next card of the chosen set —
    /// up to all three. Each requester pays <see cref="HintCost"/> per
    /// reveal.</summary>
    public static HintOutcome Hint(GameState s, string playerId, int[] currentSelection)
    {
        s.LastDealtIndices.Clear();

        if (!s.Players.TryGetValue(playerId, out var player))
            return new(false, "Player has not joined this game.", "error",
                       Array.Empty<int>(), 0, Math.Max(1, ActivePlayerCount(s)), false);

        // Already shown the whole set — nothing left to reveal.
        if (s.HintIndices.Count >= 3 && s.HintTarget.Count == 3)
            return new(true, "Full hint already shown.", "hint",
                       s.HintIndices.ToArray(), s.HintRequests.Count,
                       Math.Max(1, ActivePlayerCount(s)), true);

        var newVote = s.HintRequests.Add(playerId);
        s.Version++;

        var prevShown = s.HintIndices.Count;
        TryRevealHint(s, currentSelection);
        var nowShown = s.HintIndices.Count;
        var didReveal = nowShown > prevShown;

        int got = s.HintRequests.Count;
        int need = Math.Max(1, ActivePlayerCount(s));

        if (didReveal)
        {
            // Final reveal completes the set automatically — no need for
            // anyone to click. Score goes to whoever cast the deciding
            // vote. SubmitSet handles version, sparkle broadcast, and
            // ResetHint, so we just return its outcome.
            if (nowShown == 3)
            {
                var target = s.HintTarget.ToArray();
                SubmitSet(s, playerId, target, awardScore: false);
                return new(true, "Hint completed the set", "hint",
                           target, 0, need, true);
            }

            var msg = nowShown == 1 ? "Hint revealed" : "Second card revealed";
            Announce(s, msg, "hint");
            return new(true, msg, "hint", s.HintIndices.ToArray(), 0, need, true);
        }

        // No-set deal-3 fallback: only fires once the whole table has voted,
        // mirroring the quorum requirement for a normal hint reveal.
        if (got >= need && s.HintTarget.Count == 0 && FindAllSets(s.Board).Count == 0)
        {
            // Nothing to hint at — fall through to dealing 3 more cards.
            // (The hint button doubles as the old "No Set" button now.)
            s.HintRequests.Clear();
            var deal = DealMore(s);
            // DealMore bumps the version and announces nothing on its own,
            // so attach a single broadcast describing the fallback.
            var msg = deal.Success
                ? $"{player.Name}: no sets — dealt 3 more cards"
                : $"{player.Name}: no sets — {deal.Message.ToLowerInvariant()}";
            Announce(s, msg, deal.Success ? "info" : "warning");
            return new(deal.Success, msg, deal.Success ? "info" : "warning",
                       Array.Empty<int>(), 0, need, false);
        }

        var label = s.HintIndices.Count == 0 ? "a hint" : "the next card";
        var pendingMsg = newVote
            ? $"{player.Name} asked for {label} ({got}/{need})"
            : $"Hint vote pending ({got}/{need})";
        // Only broadcast on a fresh vote so re-clicks don't spam everyone.
        if (newVote) Announce(s, pendingMsg, "info");
        return new(true, pendingMsg, "info", s.HintIndices.ToArray(),
                   got, need, s.HintIndices.Count > 0);
    }

    private static void Announce(GameState s, string message, string kind,
                                 string? scoredPlayerId = null, int[]? foundSetIndices = null)
    {
        s.LastBroadcast = new BroadcastEvent
        {
            Message = message,
            Kind = kind,
            Version = s.Version,
            ScoredPlayerId = scoredPlayerId,
            FoundSetIndices = foundSetIndices,
        };
    }

    /// <summary>If every active player has voted, reveal the next card of
    /// the chosen hint set, charge each requester, and clear the vote
    /// tally so the next round can begin. Returns the full list of
    /// currently-revealed indices.</summary>
    private static int[] TryRevealHint(GameState s, int[] currentSelection)
    {
        if (ActivePlayerCount(s) == 0) return s.HintIndices.ToArray();
        if (s.HintRequests.Count < ActivePlayerCount(s)) return s.HintIndices.ToArray();
        if (s.HintIndices.Count >= 3) return s.HintIndices.ToArray();

        // Pick (and lock in) the target set on the first reveal so that
        // subsequent reveals show *the next card of the same set*.
        if (s.HintTarget.Count == 0)
        {
            var sets = FindAllSets(s.Board);
            if (sets.Count == 0) return Array.Empty<int>();
            int[] target = sets.FirstOrDefault(set => currentSelection.All(idx => set.Contains(idx)))
                           ?? sets[0];
            s.HintTarget = target.ToList();
        }

        // Reveal the next index from HintTarget, preferring one outside the
        // caller's current selection on the very first reveal so the hint
        // feels useful.
        int next;
        if (s.HintIndices.Count == 0)
        {
            next = s.HintTarget.FirstOrDefault(idx => !currentSelection.Contains(idx), -1);
            if (next < 0) next = s.HintTarget[0];
        }
        else
        {
            next = s.HintTarget.FirstOrDefault(idx => !s.HintIndices.Contains(idx), -1);
            if (next < 0) return s.HintIndices.ToArray();
        }

        s.HintIndices.Add(next);

        // Clear votes so the next reveal needs another full quorum.
        s.HintRequests.Clear();
        return s.HintIndices.ToArray();
    }

    private static void ResetHint(GameState s)
    {
        s.HintRequests.Clear();
        s.HintTarget.Clear();
        s.HintIndices.Clear();
        // A board change makes any prior hint announcement stale.
        s.LastBroadcast = null;
    }

    public static DealOutcome DealMore(GameState s)
    {
        if (s.Deck.Count == 0)
            return new(false, "Deck is empty.", "warning");
        if (s.Board.Count >= MaxBoard)
            return new(false, "Board is at maximum size (18 cards).", "warning");
        if (FindAllSets(s.Board).Count > 0)
            return new(false, "There are still sets on the board.", "warning");

        Deal(s.Board, s.Deck, 3);
        s.LastDealtIndices = Enumerable.Range(s.Board.Count - 3, 3).ToList();
        // Record the deal so the replay engine can reconstruct board state.
        s.Moves.Add(new MoveRecord
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PlayerId = "deal",
            PlayerName = "deal",
            Cards = new[] { s.Board[^3]!, s.Board[^2]!, s.Board[^1]! },
            Kind = "deal",
        });
        ResetHint(s);
        s.Version++;
        return new(true, "No sets found \u2014 dealt 3 more cards", "info");
    }
}
