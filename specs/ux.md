# Game UX

## Lobby & Multiplayer

- The app is split into two pages:
  - **Lobby** (`/`) — landing page that shows the user's identity in the
    top bar and lists all open games (name, player count, elapsed time
    since started).
  - **Game** (`/game.html?id=...`) — a dedicated page for an in-progress
    game. The game id is carried in the query string so each game has a
    bookmarkable / shareable URL.
- **Create a game**: enter a friendly name (e.g. "Friday Lunch") and click
  **Create**. The browser navigates to the new game's page.
- **Join a game**: click **Join** on any row in the open-games list — the
  browser navigates to that game's page.
- **Leave**: the **← Leave Game** button in the in-game toolbar removes
  the player from the game (server-side) and navigates the browser back
  to the lobby. Other players in the game see the player count update in
  real time.
- All game state is authoritative on the server (stored in Garnet); clients
  receive incremental updates over a persistent **Server-Sent Events** stream
  for the current game. Card selections, scores, the board, and hint state
  all stay in sync across every client viewing the same game.

## Players Panel

- A **Players** strip above the board lists everyone currently in the game,
  sorted by **sets found** (descending). Each row shows the player's
  name and their sets-found count.
- The local player's row is highlighted distinctly.
- A player who **leaves** the game stays on the panel with their sets
  count preserved, but the row is **dimmed** (opacity / grayscale, dashed
  border) and tagged **LEFT**. If they rejoin, the row returns to normal
  and their sets continue from where they left off.
- The lobby's player count for each game shows only **active** players.
  Hint quorum likewise only counts active players, so a player who has
  left can't block the rest of the table.
- Joining, leaving, and rejoining are all broadcast as messages
  (e.g. *"Alice left the game"*, *"Alice rejoined the game"*) so every
  client sees the change in real time.
- The personal scoreboard (Your Sets / Deck) reflects only the local
  player's stats. **Your Sets** is rendered as the headline stat (gold,
  larger type) since it is the only competitive measure.

- Cards are rendered as inline SVG with three shapes: **diamond** (polygon), **oval** (rounded rectangle with radius = half height, giving a capsule/stadium shape), and **squiggle** (smooth Catmull-Rom bezier path from custom reference coordinates).
- Each card displays 1, 2, or 3 shapes arranged vertically and centered.
- Three shadings:
  - **Solid**: filled with the card's color.
  - **Striped**: filled with a horizontal stripe pattern using `crispEdges`
    rendering for pixel-sharp lines. Default pattern is 4×4 with 1px stripes.
    The renderer accepts optional `stripeSize` / `stripeWidth` overrides for
    contexts where cards are rendered at a smaller scale (e.g. the help page
    uses 6×6 / 2px so stripes remain visible at ~50 % zoom).
  - **Open**: transparent fill with a colored outline.
- Three colors: **red** (`#e74c3c`), **green** (`#27ae60`), **purple** (`#6a3dba` — shifted slightly blue).
- Cards have rounded corners and a white background.

## Layout

- The game page is locked to the viewport height (`100dvh`, no scrolling).
  The board, toolbar, scoreboard, and players panel all shrink responsively
  so everything stays on screen at any window size.
- A JavaScript layout engine (`updateColWidth`) runs on every `resize` event
  and whenever the column count changes. It computes `--col-w` as the
  minimum of a **width budget** (viewport width ÷ current column count,
  minus gaps and padding) and a **height budget** (viewport height minus
  the measured chrome height above the board, divided across 3 card rows
  at a 140 : 200 aspect ratio). The result is clamped between 50 px and
  200 px and set as an inline style on `<body>`, along with `--col-gap`
  and `--board-pad` which also scale with the smaller viewport dimension
  (minimums as low as 2 px / 4 px on compact screens).
- All chrome elements (topbar, toolbar, scoreboard, players panel) use
  `max-width: var(--layout-w)` where `--layout-w` is derived from
  `--board-cols`, `--col-w`, `--col-gap`, and `--board-pad`, so everything
  stays aligned and centered together.
- Cards are displayed in a **4-column × 3-row** base grid (12 cards).
  The board's width is driven by `--board-cols` (a CSS custom property set
  by JS, default 4) so it is only as wide as its current column count and
  is **centered** on the page via `margin: auto`. There is no reserved
  space for future columns — the board simply grows and recenters when
  extra columns are dealt, and shrinks back when they collapse.
- When **deal-3** grows the board, the new 3 cards are appended as a
  **new rightmost column** (column 5, then column 6, up to column 7).
  The JS updates `--board-cols` and reruns the layout engine so the board
  recenters and the column width recalculates to fit the wider grid.
- When the board **collapses** (a set is found while the board has
  >12 cards), the rule is strict: **every card in the base 4×3 layout
  must remain in its original location**. The only cards permitted to
  move are cards from the right-most extras column, which slide into
  any holes left by the matched set in the base grid. Once the holes
  are filled, the now-empty extras column is removed, `--board-cols`
  decreases, and the layout engine recenters the narrower board.
- The slide animation is rendered with a FLIP transition so users can
  see exactly which card moved into which hole.
- On narrow viewports (≤600 px) the grid falls back to 3 columns for
  legibility.
- On **landscape phones / short viewports** (orientation: landscape,
  max-height: 540 px) the topbar is hidden and the game switches to a
  two-column layout: a slim sidebar (toolbar, scoreboard, players)
  on the left and the board filling the right. The layout engine
  detects this mode and subtracts the sidebar width from the available
  width budget.

## Selection

- **Selections are per-user.** What another player has clicked is
  invisible to you, and ordinary state pushes (another player's
  submission, ping updates, joins/leaves, etc.) never modify your local
  selection.
- **One exception — hint reveals.** When a hint vote reaches quorum and
  a card is revealed, every client's local selection is **replaced**
  with the revealed hint cards at that moment, so the hint shows
  cleanly with no other cards in the way. This replacement happens
  **once per reveal**: subsequent state pushes do **not** re-apply
  previously-revealed hint cards. If you clear your selection or pick
  different cards after a reveal, the old hint cards stay gone — they
  only come back if a new hint round reaches quorum (which again wipes
  your selection down to just the revealed cards).
- **Board collapse clears selection.** When the board shrinks (an extra
  column is removed after a set is found in an expanded board), cards
  slide into new positions and indices no longer correspond to the same
  cards. The local selection is cleared entirely to prevent stale
  highlights pointing at the wrong cards.
- Clicking a card toggles its selection. Selected cards get a blue highlight border and shadow.
- Clicking outside the cards (on the page background) clears all selections.
- When 3 cards are selected, the game automatically checks if they form a valid set.

## Valid Set Feedback

- A successful submission **does not display a "Set Found!" banner** —
  the visual feedback (sparkle on the player row, card flip on the
  board) is enough.
- The 3 matched cards are **blanked out** (replaced with dashed placeholder slots) for **200ms** before new cards are dealt.
- New cards appear with a **card flip animation**: they start showing a patterned card back, then flip to reveal the front face over 0.4s.
- The flip animation runs on **every** client — both the player who
  found the set and all spectators.
- If the board has 12 or fewer cards and the deck has cards, replacements are dealt into the same slots. If the board is above 12 cards and the deck has cards, the blank slots are collapsed to shrink back.
- When the deck is empty, blank slots are **left in place** to preserve the spatial layout and support spatial memory for the remaining cards.
- The deal-flip animation is only triggered by an **actual deal**.
  Refreshing the page (F5), reconnecting, or another player joining /
  leaving does **not** re-animate the previous deal.

## Invalid Set Feedback

- A red **"Not a Set!"** error message appears with a human-readable explanation of **one** failing property (the first one found). For example:
  - Number: *"1 shape on two cards and 3 shapes on one card — each property must be all the same or all different"*
  - Other properties: *"color: purple on two cards and red on one card — each property must be all the same or all different"*
- The 3 selected cards **remain highlighted** while the error message is visible (`pendingClear` state).
- The error message **stays visible** until the user clicks on another card or clicks the page background (not on the message itself, so the text can be selected/copied).
- Clicking a new card clears the message, clears the old selection, and begins a new selection with the clicked card.

## Race Fairness (ping compensation)

- Players have varying network latency. The server pairs every set
  submission with the client's most recently measured RTT and applies a
  short race window so the player who *actually clicked first* wins,
  regardless of whose packets arrived at the server first.
- Each client measures round-trip time to `/api/ping` every 10 seconds
  and reports it via `POST /api/games/{id}/ping`. The server keeps the
  most recent RTT per active player.
- When a `/select` arrives, the server doesn't apply it immediately:
  - It records `arrival = now()` and `adjusted = arrival - pingMs/2`.
  - Submissions arriving within the next `max(known pings)` ms (clamped
    50–500 ms) are batched into the same race window.
  - When the window closes, batched submissions are sorted by `adjusted`
    ascending and applied in order against the live state.
- A submission whose cards were already taken by an earlier-adjusted
  submission in the same window receives the outcome **"&lt;winner&gt;
  beat you, sorry"** (kind=`error`, surfaces as a red banner). The
  winner sees the normal sparkle + flip.
- Submissions for non-overlapping cards in the same window can both
  succeed independently.

## Messages

- Messages overlay the scoreboard area using absolute positioning — they **do not cause layout shift** or scroll the board.
- Message types: success (green), error (red), hint (orange), warning (orange), info (blue).
- Non-error messages auto-dismiss after 3 seconds.
- Error messages persist until the next user interaction (card click or background click).
- Clicking inside the message does nothing (allows text selection/copy).

## Toolbar & Scoreboard

- **← Leave Game** button: removes the player from the game and navigates
  back to the lobby page.
- **Hint** button (distributed quorum, see below). When the board has no
  valid sets, clicking Hint instead **deals 3 more cards** automatically
  (replacing the old "No Set" button). The board can grow up to a
  **maximum of 18 cards**; if it's already at maximum or the deck is
  empty, an appropriate warning is broadcast.
- There is **no New Round button during play**. A round can only be
  restarted after the deck is exhausted and no sets remain — the
  Game-Over overlay offers a **New Round** option at that point.
- Personal scoreboard displays: **Your Sets** (gold-highlighted headline
  stat) and **Deck** remaining. There is no per-point score and no
  penalty count — the goal is simply to find the most sets.

## Move Broadcasts & Score Sparkle

- A successful set submission **does not broadcast a chat message**, but
  it does trigger two coordinated effects on every other connected
  client:
  1. The three cards of the matched set are briefly outlined with a
     **pulsing yellow border** on the OLD board (~1 second), so
     spectators can see *which* cards formed the set before they
     disappear.
  2. After the highlight, the new state is applied: the matched cards
     flip and new cards animate in, and the scoring player's row in the
     **Players** panel **sparkles** (soft gold glow, score-text shine,
     small ✨ at the upper-right).
- The acting player sees the **same** highlight + animation sequence as
  spectators — the 1-second yellow pulse plays for everyone (including
  the scorer) so the collapse / flip animation has time to read on
  every screen.
- A failed set submission ("Not a Set: …") is **not broadcast** — only
  the player who made the bad selection sees the rejection message
  locally. Their penalty count still updates in everyone's Players
  panel via the regular state push.
- The sparkle / yellow highlight is suppressed on initial load so
  reconnecting clients don't see stale congratulations.

## Distributed Hint

- The Hint button is a **vote**, not an immediate reveal. A hint only shows
  once **every active player in the game** has clicked Hint.
- The button shows a **progress badge** (e.g. `1/3`) indicating how many
  players have voted in the current round. Once you've voted, the button is
  disabled and dimmed (`.voted`) until either the round completes or the
  board changes.
- When a player casts a fresh vote, an announcement
  (e.g. *"Alice asked for a hint (1/2)"*) is broadcast to **every
  connected client** as part of the state update, so the whole table
  sees who's calling for hints.
- Newly-connected clients do **not** see stale broadcast messages from
  before they joined; only events that occur after their subscription are
  shown.
- When the quorum is reached, every client's local selection is
  **replaced** with the revealed hint cards at that single instant —
  driven by the authoritative state push. There is no separate "hint
  highlight"; the cards simply appear as selected (blue) on every
  client, with anything the user had picked previously cleared so the
  hint reads cleanly.
- The replacement happens **once per reveal**. Once a client has
  already applied a given reveal, subsequent state pushes carrying the
  same `hintIndices` do **not** re-apply it. If a player changes their
  selection after the reveal, the previously-revealed cards stay gone
  — they only come back if a new hint round reaches quorum (which
  again resets the selection to just the revealed cards).
- Each round of voting reveals **one more card** from the *same* set:
  - Round 1 → 1 card added to selection
  - Round 2 → 2 cards added to selection
  - Round 3 → all 3 cards selected — the server **automatically submits
    the set**. Hint-driven completions **award no set credit** to
    anyone (the hint UI walked the table to the answer; it isn't a real
    find), but the yellow-highlight + flip animation still fires on
    every client (including the deciding voter) so everyone sees the
    answer before the new cards are dealt.
- After each (non-final) reveal, votes are cleared so the next reveal
  needs another full quorum.
- Hints are **free** — the only cost is social: every other player has
  to agree before the hint shows.
- Hint state (votes + revealed cards + chosen target set) is fully reset
  whenever the board changes: a successful set submission, a Hint-driven
  deal-3, a New Round, or a player leaving (which may also tip the
  remaining group over the quorum and trigger an immediate reveal).
- If the board has **no valid sets**, the Hint vote still requires a
  full quorum — once every active player has clicked Hint, the request
  **falls through to dealing 3 more cards** (replacing the old "No Set"
  button). On the very first vote in a no-sets situation, players see a
  normal pending-vote announcement and the badge counts up like any
  other round; only the deciding vote triggers the deal.

## Game Over

- When the deck is empty and no sets remain, a centered overlay shows the
  **winner** (most sets found across all players), the local player's
  **final sets count**, and **elapsed time** (formatted as e.g.
  "3m 42s"), with a **New Round** button (restart in place) and a
  **Back to Lobby** button.
