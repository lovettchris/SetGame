// In-game controller. The game id comes from the ?id=... query string.
// "Leave" calls the leave API and navigates back to the lobby.

document.addEventListener('DOMContentLoaded', async () => {
    const meEl = document.getElementById('me');
    const boardEl = document.getElementById('board');
    const playersEl = document.getElementById('players');
    const messageEl = document.getElementById('message');
    const gameNameEl = document.getElementById('game-name');
    const leaveBtn = document.getElementById('leave-btn');
    const hintBtn = document.getElementById('hint-btn');
    const hintBadge = document.getElementById('hint-badge');

    const params = new URLSearchParams(window.location.search);
    const currentGameId = params.get('id');
    if (!currentGameId) {
        window.location.href = '/';
        return;
    }

    let currentState = null;
    let eventSource = null;
    let selectedIndices = [];
    let pendingClear = false;
    let messageTimeout = null;
    let lastDealtIndices = [];
    let lastVersion = 0;
    let lastShownBroadcast = -1;
    // When another player finds a set, we briefly show the OLD board with
    // the three cards of that set outlined in yellow before applying the
    // new state (which has fresh cards in those slots).
    let foundSetHighlight = null;
    // Forces the next _applyState to honor state.lastDealtIndices and
    // sparkle the scorer even though lastShownBroadcast was reserved
    // up-front by the highlight defer (which would otherwise suppress
    // both on spectators).
    let forceNextDealAnim = false;
    let forceNextSparkleIds = null;
    // Hint reveals are per-user merged exactly once each: when the server
    // pushes a longer state.hintIndices than we've seen before, the new
    // card is appended to the local selection. We track the count we've
    // already absorbed so subsequent state pushes carrying the same
    // hintIndices do NOT re-add cards a user has cleared / replaced.
    let hintInFlight = false;
    // Wall-clock time until which a deal-flip animation is in flight; while
    // this is in the future the Hint button is disabled. This stops rapid
    // hint clicks from racing against a deal-3 (or new-round) animation
    // and ending up with stale or missing selection indices.
    let dealAnimUntil = 0;
    const DEAL_ANIM_BUFFER_MS = 600;
    let appliedHintCount = 0;
    // Set true while a /join request triggered by an auto-rejoin (e.g.
    // after "New Round" wiped the player list) is still in flight, so
    // we don't fire another join for the next state push that happens
    // to arrive before we land in the players map.
    let rejoiningInFlight = false;
    // When set, the next _applyState should treat every populated board
    // slot as freshly dealt so the deal-flip animation runs across the
    // whole board (used when a "new round" sweep finishes).
    let forceFullBoardDeal = false;
    // When > 0, the next render staggers each card's deal-flip by this
    // many milliseconds in board-index order, so a full-board deal (e.g.
    // a new round) cascades instead of flipping every card at once.
    let nextDealStaggerMs = 0;
    const NEW_ROUND_BLANK_MS = 1000;
    const NEW_ROUND_STAGGER_MS = 100;
    let pendingState = null;
    let pendingTimer = null;
    const FOUND_SET_HIGHLIGHT_MS = 1000;
    let pingTimer = null;
    const PING_INTERVAL_MS = 10000;

    await SetClient.loadMe();
    meEl.textContent = SetClient.me.name;

    // ─── Game lifecycle ───────────────────────────────────────────────────
    try {
        const initial = await SetClient.joinGame(currentGameId);
        applyState(initial);
    } catch {
        // Game vanished — bounce back to the lobby.
        window.location.href = '/';
        return;
    }
    eventSource = SetClient.openEvents(currentGameId, applyState);

    // Kick off ping measurement immediately, then every 10s. Reporting
    // it back to the server lets the server size its race-fairness
    // window so the slowest player still has time to submit.
    async function pingNow() {
        const rtt = await SetClient.measurePing();
        if (rtt != null) {
            await SetClient.reportPing(currentGameId, rtt);
        }
    }
    pingNow();
    pingTimer = setInterval(pingNow, PING_INTERVAL_MS);

    async function leaveGame() {
        if (eventSource) { eventSource.close(); eventSource = null; }
        if (pingTimer) { clearInterval(pingTimer); pingTimer = null; }
        try { await SetClient.leaveGame(currentGameId); } catch {}
        window.location.href = '/';
    }

    leaveBtn.addEventListener('click', leaveGame);

    // Auto-leave when navigating away (back button, tab close, etc.)
    window.addEventListener('pagehide', () => {
        if (currentGameId) {
            navigator.sendBeacon(`/api/games/${currentGameId}/leave`);
        }
    });


    hintBtn.addEventListener('click', async (e) => {
        e.stopPropagation();
        if (hintBtn.disabled || hintInFlight || Date.now() < dealAnimUntil) return;
        hintInFlight = true;
        renderHintButton();
        try {
            clearMessage();
            const result = await SetClient.hint(currentGameId, selectedIndices);
            applyResult(result);
        } finally {
            hintInFlight = false;
            renderHintButton();
        }
    });

    document.addEventListener('click', (e) => {
        if (!messageEl.contains(e.target)) {
            clearMessage();
            if (selectedIndices.length > 0 || pendingClear) {
                selectedIndices = [];
                pendingClear = false;
                renderBoard();
            }
        }
    });

    // ─── State application ────────────────────────────────────────────────
    function applyState(state) {
        if (!state) return;
        const bc = state.lastBroadcast;
        const isNewBroadcast = bc && bc.version > lastShownBroadcast;

        // "New round" — clear the board to 12 blank slots immediately
        // (no spin/scale animation), then a moment later flip in the
        // fresh deck using the same deal-flip animation as a normal
        // deal. We also wipe every scrap of prior animation state so
        // the previous round's deal can't replay on top of the new one.
        const isNewRound = isNewBroadcast && bc.kind === 'new-round';
        if (isNewRound) {
            const overlay = document.querySelector('.game-over-overlay');
            if (overlay) overlay.remove();
            // Cancel any in-flight found-set defer; the new round trumps it.
            if (pendingTimer) { clearTimeout(pendingTimer); pendingTimer = null; pendingState = null; }
            foundSetHighlight = null;
            forceNextDealAnim = false;
            forceNextSparkleIds = null;
            lastDealtIndices = [];
            selectedIndices = [];
            pendingClear = false;
            appliedHintCount = 0;
            lastShownBroadcast = bc.version;
            // Replace the entire board with 12 plain blank slots, no
            // classes or transitions inherited from the old cards.
            boardEl.innerHTML = '';
            boardEl.style.gridTemplateColumns = 'repeat(4, var(--col-w))';
            boardEl.style.gridTemplateRows = 'repeat(3, auto)';
            boardEl.style.gridAutoFlow = 'row';
            for (let i = 0; i < 12; i++) {
                const slot = document.createElement('div');
                slot.className = 'card blank';
                slot.style.gridRow = String(Math.floor(i / 4) + 1);
                slot.style.gridColumn = String((i % 4) + 1);
                boardEl.appendChild(slot);
            }
            pendingState = state;
            pendingTimer = setTimeout(() => {
                const s = pendingState;
                pendingState = null;
                pendingTimer = null;
                forceFullBoardDeal = true;
                nextDealStaggerMs = NEW_ROUND_STAGGER_MS;
                _applyState(s);
            }, NEW_ROUND_BLANK_MS);
            return;
        }

        // Run the highlight + delayed-apply sequence for every player
        // (including the one who scored), so the collapse / flip
        // animation has time to read on screen everywhere.
        const isFoundSet = isNewBroadcast
            && Array.isArray(bc.foundSetIndices)
            && bc.foundSetIndices.length > 0
            && bc.foundSetIndices.length % 3 === 0;

        if (isFoundSet && currentState && !pendingTimer) {
            foundSetHighlight = bc.foundSetIndices.slice();
            // Reserve the broadcast id now so the eventual real apply
            // below doesn't replay sparkle/messaging on us.
            lastShownBroadcast = bc.version;
            forceNextDealAnim = true;
            forceNextSparkleIds = Array.isArray(bc.scoredPlayerIds) && bc.scoredPlayerIds.length
                ? bc.scoredPlayerIds.slice()
                : (bc.scoredPlayerId ? [bc.scoredPlayerId] : []);
            renderBoard();
            pendingState = state;
            pendingTimer = setTimeout(() => {
                const s = pendingState;
                pendingState = null;
                pendingTimer = null;
                foundSetHighlight = null;
                _applyState(s);
            }, FOUND_SET_HIGHLIGHT_MS);
            return;
        }

        // Already showing a highlight — queue the latest state for when
        // the timer fires; don't re-trigger the highlight.
        if (pendingTimer) {
            if (!state.version || !pendingState ||
                state.version >= pendingState.version) {
                pendingState = state;
            }
            return;
        }

        _applyState(state);
    }

    function _applyState(state) {
        if (!state || state.version <= lastVersion) return;
        const isFirstLoad = lastVersion === 0;
        lastVersion = state.version;
        currentState = state;
        // lastDealtIndices on the server persists from the most recent deal,
        // so a refresh / join / unrelated broadcast (which all push state)
        // would otherwise re-animate that stale deal. Only honor it when
        // this state push is actually carrying a new broadcast.
        const bcForDeal = state.lastBroadcast;
        const dealIsFresh = forceNextDealAnim ||
                            (!isFirstLoad && bcForDeal &&
                             bcForDeal.version > lastShownBroadcast);
        forceNextDealAnim = false;
        // For a fresh new round, treat every populated board slot as a
        // newly-dealt card so the existing flip-from-back animation
        // plays across the whole board.
        if (forceFullBoardDeal) {
            lastDealtIndices = (state.board || [])
                .map((c, i) => (c == null ? -1 : i))
                .filter(i => i >= 0);
            forceFullBoardDeal = false;
        } else {
            lastDealtIndices = dealIsFresh ? (state.lastDealtIndices || []) : [];
        }
        if (lastDealtIndices.length > 0) {
            // Block hint clicks until the flip-deal animation finishes;
            // include the per-card stagger if one was just queued.
            const animMs = DEAL_ANIM_BUFFER_MS
                + (nextDealStaggerMs > 0
                    ? nextDealStaggerMs * Math.max(0, lastDealtIndices.length - 1)
                    : 0);
            dealAnimUntil = Date.now() + animMs;
            setTimeout(renderHintButton, animMs + 20);
        }
        // If the board just had cards replaced (someone else found a set, or
        // a deal-3 happened), drop any local selection that now points at a
        // freshly-dealt card the user never chose.
        if (lastDealtIndices.length > 0) {
            selectedIndices = selectedIndices.filter(i => !lastDealtIndices.includes(i));
        }
        // Hint reveals are merged into the local selection ONCE per
        // reveal: when the server's hintIndices grows, append the newly
        // revealed cards to whatever the user currently has selected.
        // Subsequent state pushes that carry the same hintIndices do not
        // re-apply them — so a user who clears or changes their picks
        // after a reveal won't have the old hint card snap back. The
        // counter resets to 0 below whenever hintIndices is empty (a new
        // round / board change cleared the hint).
        const hintIndices = Array.isArray(state.hintIndices) ? state.hintIndices : [];
        if (hintIndices.length === 0) {
            appliedHintCount = 0;
        } else if (hintIndices.length > appliedHintCount) {
            // A fresh reveal just landed: replace the local selection with
            // ONLY the revealed hint cards. Anything the user had picked
            // before the reveal would otherwise muddy the display and make
            // the hint hard to read. Between reveals the user can still
            // tinker with their selection — but each new reveal resets it
            // back to "just the hint".
            selectedIndices = hintIndices.slice();
            appliedHintCount = hintIndices.length;
            pendingClear = false;
        }
        gameNameEl.textContent = state.name;

        // Decide whether to sparkle a player row based on the authoritative
        // broadcast (so every client animates the same player at the same
        // moment, regardless of who clicked).
        const bc = state.lastBroadcast;
        const sparkleIds = forceNextSparkleIds
            || ((bc && !isFirstLoad && bc.version > lastShownBroadcast)
                ? (Array.isArray(bc.scoredPlayerIds) && bc.scoredPlayerIds.length
                    ? bc.scoredPlayerIds.slice()
                    : (bc.scoredPlayerId ? [bc.scoredPlayerId] : []))
                : []);
        forceNextSparkleIds = null;

        renderHintButton();
        renderBoard();
        renderPlayers(sparkleIds);
        renderMyStats();

        // Surface the broadcast text the same way (skip on first load, and
        // skip empty messages — those are sparkle-only signals).
        if (bc && !isFirstLoad && bc.version > lastShownBroadcast) {
            lastShownBroadcast = bc.version;
            if (bc.message) showMessage(bc.message, bc.kind || 'info');
        } else if (bc && isFirstLoad) {
            lastShownBroadcast = bc.version;
        }

        if (state.status === 'ended') {
            showGameOver();
        } else {
            // Another player started a new round — close the overlay on
            // every client so everyone moves on together.
            const overlay = document.querySelector('.game-over-overlay');
            if (overlay) overlay.remove();
        }

        // Auto-rejoin: a "new round" wipes the player list, so every
        // connected client needs to reannounce itself. We also catch the
        // less-common case where the server lost our entry for some
        // other reason (e.g. an explicit kick down the line).
        if (state.status === 'active'
            && SetClient.me
            && !state.players[SetClient.me.id]
            && !rejoiningInFlight) {
            rejoiningInFlight = true;
            SetClient.joinGame(currentGameId)
                .catch(err => console.warn('Auto-rejoin failed', err))
                .finally(() => { rejoiningInFlight = false; });
        }
    }

    function renderHintButton() {
        const requests = (currentState && currentState.hintRequests) || [];
        const players = currentState ? Object.values(currentState.players) : [];
        const total = players.filter(p => p.active !== false).length;
        const myId = SetClient.me.id;
        const iVoted = requests.includes(myId);

        if (requests.length === 0) {
            hintBadge.hidden = true;
            hintBadge.textContent = '';
        } else {
            hintBadge.hidden = false;
            hintBadge.textContent = `${requests.length}/${total}`;
        }
        // Disabled while we're waiting on the rest of the table to vote
        // (after quorum, HintRequests is cleared by the server, so the
        // button comes back automatically for the next round), while a
        // hint request is in flight, or while a deal-flip animation is
        // still playing — clicks during the animation can otherwise race
        // the hint state and produce wrong-looking selections.
        const animBlocked = Date.now() < dealAnimUntil;
        const voteBlocked = iVoted && requests.length < total;
        hintBtn.disabled = voteBlocked || hintInFlight || animBlocked;
        hintBtn.classList.toggle('voted', voteBlocked);
    }

    function applyResult(result) {
        if (!result) return;
        if (result.outcome) {
            const o = result.outcome;
            const kind = o.kind || (o.success === false || o.accepted === false ? 'error' : 'info');
            // Hint announcements are broadcast via state.lastBroadcast so
            // every player sees them — don't double-show on the requester.
            // Success (set found) gives no banner — the sparkle + flip is
            // feedback enough. Errors still surface to the acting player.
            if (kind === 'error') {
                const text = o.message || '';
                if (text) showMessage(text, kind);
            }
            if (kind === 'error') {
                pendingClear = true;
            } else if (kind !== 'info' && kind !== 'hint') {
                selectedIndices = [];
                pendingClear = false;
            }
        }
        if (result.state) applyState(result.state);
    }

    async function onCardClick(index) {
        if (!currentState) return;
        clearMessage();
        if (pendingClear) {
            selectedIndices = [];
            pendingClear = false;
        }

        const idx = selectedIndices.indexOf(index);
        if (idx >= 0) selectedIndices.splice(idx, 1);
        else selectedIndices.push(index);

        if (selectedIndices.length === 3) {
            const submitted = selectedIndices.slice();
            // Render the third selection immediately so the user sees the
            // card highlighted before the network round-trip resolves.
            renderBoard();
            const result = await SetClient.submitSelection(currentGameId, submitted);
            applyResult(result);
        } else {
            renderBoard();
        }
    }

    // ─── Rendering ────────────────────────────────────────────────────────
    function renderBoard() {
        if (!currentState) return;

        // Pin the board's current height during the rebuild so the
        // document doesn't briefly shrink (which would otherwise cause
        // the browser to snap the page scroll position when innerHTML
        // is cleared and re-populated).
        const prevHeight = boardEl.getBoundingClientRect().height;
        if (prevHeight > 0) boardEl.style.minHeight = prevHeight + 'px';

        // FLIP-style slide animation: capture each card's pre-render
        // bounding rect keyed by its stable cardId so we can animate
        // the delta after re-render. Used for the collapse case where
        // surviving cards shift into freed-up positions.
        const oldRects = new Map();
        for (const child of boardEl.children) {
            const cid = child.dataset.cardId;
            if (cid) oldRects.set(cid, child.getBoundingClientRect());
        }

        boardEl.innerHTML = '';
        const board = currentState.board;
        // Fixed 4-column "base" grid for cards 0..11 (preserves the
        // physical position of every card across deals). When deal-3
        // grows the board, the extra cards stack vertically in a new
        // rightmost column (col 5, then col 6 if a second deal happens),
        // so existing cards never shift.
        const extraCols = board.length > 12 ? Math.ceil((board.length - 12) / 3) : 0;
        const cols = 4 + extraCols;
        // Use the same --col-w custom property the CSS uses so the
        // base 4 columns stay at identical positions whether or not
        // the extras column is present.
        boardEl.style.gridTemplateColumns = `repeat(${cols}, var(--col-w))`;
        boardEl.style.gridTemplateRows = 'repeat(3, auto)';
        boardEl.style.gridAutoFlow = 'row';

        function placeCell(el, index) {
            let row, col;
            if (index < 12) {
                row = Math.floor(index / 4) + 1;
                col = (index % 4) + 1;
            } else {
                const extras = index - 12;
                col = 5 + Math.floor(extras / 3);
                row = (extras % 3) + 1;
            }
            el.style.gridRow = String(row);
            el.style.gridColumn = String(col);
        }

        board.forEach((cardId, index) => {
            const cardEl = document.createElement('div');
            cardEl.className = 'card';
            placeCell(cardEl, index);

            if (cardId === null || cardId === undefined) {
                cardEl.classList.add('blank');
                boardEl.appendChild(cardEl);
                return;
            }

            cardEl.dataset.cardId = cardId;
            const card = SetClient.parseCard(cardId);
            const isSelected = selectedIndices.includes(index);
            const isFoundHighlight = foundSetHighlight && foundSetHighlight.includes(index);
            if (isSelected) cardEl.classList.add('selected');
            if (isFoundHighlight) cardEl.classList.add('found-set');

            const flipper = document.createElement('div');
            flipper.className = 'card-flipper';
            const back = document.createElement('div');
            back.className = 'card-face card-back';
            const front = document.createElement('div');
            front.className = 'card-face card-front';
            front.innerHTML = CardRenderer.render(card, isSelected);
            flipper.appendChild(back);
            flipper.appendChild(front);
            cardEl.appendChild(flipper);

            cardEl.addEventListener('click', e => { e.stopPropagation(); onCardClick(index); });

            if (lastDealtIndices.includes(index)) {
                // Insert into the DOM in the back-face state, then force a
                // synchronous reflow to commit rotateY(180°) as the starting
                // point of the transition. Only after that do we swap to
                // .dealt — which is what makes the 0.4s flip animate.
                cardEl.classList.add('dealing');
                if (nextDealStaggerMs > 0) {
                    // Cascade the deal-flip across the board: each card's
                    // transition starts one stagger interval after the
                    // previous one in board order.
                    const order = lastDealtIndices.indexOf(index);
                    flipper.style.transitionDelay = (order * nextDealStaggerMs) + 'ms';
                }
                boardEl.appendChild(cardEl);
                void cardEl.offsetHeight;
                cardEl.classList.remove('dealing');
                cardEl.classList.add('dealt');
            } else {
                cardEl.classList.add('dealt');
                boardEl.appendChild(cardEl);
            }
        });
        lastDealtIndices = [];
        nextDealStaggerMs = 0;

        // FLIP step 2: for each surviving card whose physical position
        // changed (e.g. board collapsed and it slid into a freed slot),
        // pin it at its old screen position with a transform, then
        // transition the transform back to identity so it visibly
        // slides into place.
        if (oldRects.size > 0) {
            for (const child of boardEl.children) {
                const cid = child.dataset.cardId;
                if (!cid) continue;
                const oldRect = oldRects.get(cid);
                if (!oldRect) continue;
                // Don't fight with the deal-flip animation.
                if (child.classList.contains('dealing')) continue;
                const newRect = child.getBoundingClientRect();
                const dx = oldRect.left - newRect.left;
                const dy = oldRect.top - newRect.top;
                if (Math.abs(dx) < 1 && Math.abs(dy) < 1) continue;
                child.style.transition = 'none';
                child.style.transform = `translate(${dx}px, ${dy}px)`;
                void child.offsetHeight;
                child.style.transition = 'transform 0.4s ease';
                child.style.transform = '';
                setTimeout(() => {
                    child.style.transition = '';
                }, 450);
            }
        }
        // Release the height pin once the new content is in place; the
        // grid's natural size is identical to (or larger than) the old
        // height, so removing the pin causes no visible jump.
        boardEl.style.minHeight = '';
    }

    function renderPlayers(sparkleIds) {
        playersEl.innerHTML = '';
        const sparkleSet = new Set(sparkleIds || []);
        const sorted = Object.values(currentState.players)
            .sort((a, b) => b.setsFound - a.setsFound);
        for (const p of sorted) {
            const row = document.createElement('div');
            const classes = ['player'];
            if (p.id === SetClient.me.id) classes.push('player-me');
            if (p.active === false) classes.push('player-inactive');
            row.className = classes.join(' ');
            if (sparkleSet.has(p.id)) row.classList.add('sparkle');
            const inactiveTag = p.active === false ? ' <span class="player-tag">left</span>' : '';
            const pingTag = (p.pingMs && p.active !== false)
                ? ` <span class="player-ping" title="round-trip time to server">${p.pingMs} ms</span>`
                : '';
            row.innerHTML = `
                <span class="player-name">${escapeHtml(p.name)}${inactiveTag}</span>
                <span class="player-score">${p.setsFound}</span>
                <span class="player-sets">sets</span>${pingTag}
            `;
            playersEl.appendChild(row);
        }
    }

    function renderMyStats() {
        const me = currentState.players[SetClient.me.id];
        document.getElementById('stat-sets').textContent = me ? me.setsFound : 0;
        document.getElementById('stat-deck').textContent = currentState.deckRemaining;
    }

    function showMessage(text, type) {
        messageEl.textContent = text;
        messageEl.className = `message ${type}`;
        messageEl.classList.add('visible');
        if (messageTimeout) clearTimeout(messageTimeout);
        messageTimeout = null;
        if (type !== 'error') messageTimeout = setTimeout(clearMessage, 3000);
    }

    function clearMessage() {
        messageEl.classList.remove('visible');
    }

    function showGameOver() {
        if (document.querySelector('.game-over-overlay')) return;
        const me = currentState.players[SetClient.me.id];
        const winner = Object.values(currentState.players)
            .sort((a, b) => b.setsFound - a.setsFound)[0];
        const elapsed = Date.now() - currentState.startedAt;
        const totalSec = Math.floor(elapsed / 1000);
        const m = Math.floor(totalSec / 60), s = totalSec % 60;
        const timeStr = m > 0 ? `${m}m ${s}s` : `${s}s`;
        const overlay = document.createElement('div');
        overlay.className = 'game-over-overlay';
        overlay.innerHTML = `
            <div class="game-over-panel">
                <h2>Game Over!</h2>
                <p>Winner: <strong>${escapeHtml(winner ? winner.name : '—')}</strong> (${winner ? winner.setsFound : 0} sets)</p>
                <p>Your sets: <strong>${me ? me.setsFound : 0}</strong></p>
                <p>Time: <strong>${timeStr}</strong></p>
                <button id="play-again-btn">New Round</button>
                <button id="export-btn" class="ghost">Export Game</button>
                <button id="back-btn" class="ghost">Back to Lobby</button>
            </div>
        `;
        document.body.appendChild(overlay);
        document.getElementById('play-again-btn').addEventListener('click', async () => {
            overlay.remove();
            await SetClient.restart(currentGameId);
        });
        document.getElementById('export-btn').addEventListener('click', async () => {
            try { await SetClient.exportGame(currentGameId, currentState.name); }
            catch (err) { console.error(err); }
        });
        document.getElementById('back-btn').addEventListener('click', async () => {
            overlay.remove();
            await leaveGame();
        });
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
        }[c]));
    }
});
