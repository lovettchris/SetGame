# Game Replay — Feature Spec

**Goal:** After a game ends, any participant (or spectator) can step through the
entire game move-by-move, seeing exactly which cards were on the board, who found
each set, and how the board evolved. Replays are persisted in Garnet so they can
be linked from the leaderboard.

---

## Data availability

The server already captures everything needed:

- **`GET /api/games/{id}/export`** returns `initialDeck`, `players`, and the
  append-only `moves[]` array (each with `timestamp`, `playerName`, `cards[]`,
  `kind`).
- `InitialDeck` is snapshotted at game creation and never mutated, so the full
  board sequence can be reconstructed client-side by replaying the engine rules.

**No changes to the export endpoint are required.** The data model is already
production-ready.

---

## Replay persistence

To make replays discoverable after a game ends (and to link them from the
leaderboard), the server saves a dedicated replay record when a game transitions
to `status == "ended"`.

### New Garnet keys

| Key | Primitive | Contents |
|---|---|---|
| `replay:{id}` | `STRING` | Full export snapshot JSON (same shape as `/export`) |
| `replays:index` | `SET` | All game IDs with a saved replay |

### When it is written

Inside `GameService.MutateAsync`, immediately after a mutation that flips
`state.Status` to `"ended"`:

1. Serialize the export snapshot (same projection as `Program.cs /export`).
2. `SET replay:{id} <json>` in Garnet.
3. `SADD replays:index {id}`.
4. Update the winner's `LeaderboardEntry` to include `LastReplayGameId = id`
   (see leaderboard change below).

Replays share the 24-hour GC TTL of the parent game; `SweepStaleGamesAsync`
deletes `replay:{id}` and `SREM replays:index {id}` alongside `game:{id}:state`.

### New API endpoints

```
GET /api/replays
```
Returns a list of `{ id, name, startedAt, playerCount }` for all games that
have a saved replay, ordered newest-first. Used by the lobby to decorate ended
games without re-reading every game's full state.

```
GET /api/games/{id}/replay
```
Returns the full replay export JSON for a specific game (reads `replay:{id}`).
Falls back to `/export` for in-progress games or games created before the
persistence feature landed.

---

## Leaderboard change

`LeaderboardEntry` gains one optional field:

```csharp
public record LeaderboardEntry(
    string PlayerId, string PlayerName,
    int Wins, int Score,
    string? LastReplayGameId   // ← new: most recent won game with a saved replay
);
```

`GameService.UpdateLeaderboardAsync` sets `LastReplayGameId` to the ending game
ID when it records a win. This is the replay the leaderboard 🎬 icon links to.

---

## New surface: `/replay.html?game={id}`

A standalone HTML page (mirrors `game.html` in structure). Accessible from:

1. The **Game Over overlay** — "▶ Watch Replay" button (links to the just-ended game).
2. The **Lobby** — a 🎬 icon/button next to ended games (appears only when `hasReplay` is true in `GameSummary`).
3. The **Leaderboard** — a 🎬 icon next to each entry that has a `lastReplayGameId`.

`GameSummary` gains a boolean field `HasReplay` (populated from a Garnet
`SISMEMBER replays:index {id}` check in `ListAsync`).

---

## Replay engine (client-side JS)

A new `replay.js` loads the replay JSON from `/api/games/{id}/replay` and
re-simulates the game deterministically:

```
Frame 0        – board after initial 12-card deal
Frame 1..N     – board after each accepted move (set removed + refill)
```

Each frame stores: `board` (12–18 card ids or `null`), `move` (the `MoveRecord`
that produced it), `elapsed` (ms since game start).

The existing `renderer.js` `renderCard()` function is reused unchanged to draw
each frame. No server involvement after the initial fetch.

### Reconstructing board state

The engine rules mirror `SetGameEngine.cs` exactly:

1. Start with `initialDeck`; deal the last 12 cards to the board.
2. For each `MoveRecord`, remove the three matched card IDs from the board and
   apply the same fill/collapse logic (`FillBlanks`).

Because card IDs are recorded (not board indices), reconstruction is
deterministic regardless of any race or concurrent activity at play time.

---

## UI controls

| Control | Behaviour |
|---|---|
| ◀◀ / ▶▶ | Jump to first / last frame |
| ◀ / ▶ | Step one move backward / forward |
| ▶ Play / ⏸ Pause | Auto-advance at chosen speed |
| Speed selector | 0.5× · 1× · 2× · 4× |
| Progress scrubber | Click/drag to any frame |

---

## Layout

```
┌─────────────────────────────────────────────────────┐
│  ← Back   "Game Name"   ★ Players: Alice 5, Bob 3   │
├──────────────────────────────┬──────────────────────┤
│                              │  Move list (sidebar) │
│   SET board (renderer.js)    │  ─────────────────── │
│                              │  ▶ 00:12  Alice  ✓   │
│  [highlighted set cards]     │    00:45  hint   ✓   │
│                              │  ● 01:03  Bob    ✓   │  ← current
├──────────────────────────────┴──────────────────────┤
│  ◀◀  ◀  ▶ Play  ▶  ▶▶   ████████░░░░  0.5× 1× 2×  │
│  Move 7 / 24  •  01:03 elapsed  •  Bob found a set! │
└─────────────────────────────────────────────────────┘
```

Cards that formed the highlighted set get the same flash animation already used
in live play (`foundSetIndices` highlight class in `renderer.js`).

---

## Implementation plan

### Backend

| # | File | Change |
|---|---|---|
| 1 | `Engine/GameState.cs` | Add `LastReplayGameId` to `LeaderboardEntry` record |
| 2 | `Garnet/GarnetGameStore.cs` | Add `SetReplayAsync(id, json)`, `GetReplayAsync(id)`, `SIsMemberAsync`, `SRemAsync` for replay index |
| 3 | `Games/GameService.cs` | In `MutateAsync`, detect `status == "ended"` and call `PersistReplayAsync`; add `PersistReplayAsync`; update `ListAsync` to populate `HasReplay`; update `SweepStaleGamesAsync` to also delete replay keys |
| 4 | `Program.cs` | Add `GET /api/replays` and `GET /api/games/{id}/replay` endpoints |

### Frontend

| # | File | Change |
|---|---|---|
| 5 | `wwwroot/replay.html` | New page (≈40 lines, mirrors `game.html` skeleton) |
| 6 | `wwwroot/js/replay.js` | New: frame builder + playback controller (~200 lines) |
| 7 | `wwwroot/css/style.css` | Replay-specific layout rules (sidebar, scrubber, 🎬 icon) |
| 8 | `wwwroot/game.html` | Add "▶ Watch Replay" button to game-over overlay |
| 9 | `wwwroot/js/game.js` | Wire button → `/replay.html?game={id}` |
| 10 | `wwwroot/index.html` | Add 🎬 button to ended-game rows in lobby list |
| 11 | `wwwroot/js/lobby.js` | Render 🎬 on ended games (`g.hasReplay`); render 🎬 on leaderboard rows (`entry.lastReplayGameId`); both link to `/replay.html?game={id}` |

---

## Edge cases

- **In-progress games** — replay link hidden in lobby; `/replay` endpoint falls
  back to `/export` so the page still works if someone navigates directly.
- **Hint moves** — attributed to "💡 Hint" in the move list with no sparkle on
  the scoreboard column.
- **Board > 12 cards** — frames faithfully show the expanded layout; the
  collapse-on-fill logic runs in the client replay engine, matching
  `SetGameEngine.cs` exactly.
- **Single-player games** — fully supported; move list just shows one name.
- **Legacy games** — games created before `InitialDeck`/`Moves` existed have
  empty replay snapshots; `HasReplay` is `false` for them, so no 🎬 icon appears.
- **GC** — replays are deleted together with the parent game by
  `SweepStaleGamesAsync` (24-hour TTL).

---

## Estimated effort

~1.5 days (0.5 day backend persistence + 1 day replay UI).
