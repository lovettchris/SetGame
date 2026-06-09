// Game Replay engine — loads a saved game from /api/games/{id}/replay and
// lets the user step through each move frame-by-frame.

document.addEventListener('DOMContentLoaded', async () => {
    const params = new URLSearchParams(window.location.search);
    const gameId = params.get('game');
    if (!gameId) { window.location.href = '/'; return; }

    document.getElementById('back-link').href = '/';

    const boardEl       = document.getElementById('board');
    const moveListEl    = document.getElementById('move-list');
    const gameNameEl    = document.getElementById('replay-game-name');
    const playersEl     = document.getElementById('replay-players');
    const scrubberEl    = document.getElementById('scrubber');
    const statusEl      = document.getElementById('replay-status');
    const btnFirst      = document.getElementById('btn-first');
    const btnPrev       = document.getElementById('btn-prev');
    const btnPlay       = document.getElementById('btn-play');
    const btnNext       = document.getElementById('btn-next');
    const btnLast       = document.getElementById('btn-last');
    const meEl          = document.getElementById('me');

    await SetClient.loadMe();
    meEl.textContent = SetClient.me.name;

    // ── Load replay ──────────────────────────────────────────────────────────
    let replayData;
    try {
        replayData = await SetClient.getReplay(gameId);
    } catch {
        statusEl.textContent = 'Failed to load replay.';
        return;
    }
    if (!replayData || !replayData.initialDeck) {
        statusEl.textContent = 'Replay not available for this game.';
        return;
    }

    const { name, startedAt, players, moves, initialDeck } = replayData;

    gameNameEl.textContent = name;
    document.title = `Replay: ${name}`;
    const playerSummary = (players || [])
        .sort((a, b) => b.setsFound - a.setsFound)
        .map(p => `${escapeHtml(p.name)}\u00a0${p.setsFound}`)
        .join(', ');
    playersEl.textContent = playerSummary ? `★ ${playerSummary}` : '';

    // ── Build frames ─────────────────────────────────────────────────────────
    // Each frame: { board (array of id|null), move|null, elapsed, foundCards|null }
    // "board" is the state BEFORE the move is highlighted; the move's matched
    // cards are shown with the found-set glow on this board, then the next
    // frame shows the post-fill board.
    const frames = buildFrames(initialDeck, moves || [], startedAt);

    // ── Populate move list sidebar ───────────────────────────────────────────
    buildMoveList(frames, players || []);

    // ── Playback state ───────────────────────────────────────────────────────
    let currentFrame = 0;
    let playing = false;
    let playSpeed = 1;
    let playTimer = null;
    const BASE_INTERVAL_MS = 1200; // ms per frame at 1×

    scrubberEl.max = frames.length - 1;
    scrubberEl.value = 0;

    renderFrame(0);

    // ── Controls ─────────────────────────────────────────────────────────────
    btnFirst.addEventListener('click', () => { pause(); goTo(0); });
    btnPrev.addEventListener('click',  () => { pause(); goTo(currentFrame - 1); });
    btnNext.addEventListener('click',  () => { pause(); goTo(currentFrame + 1); });
    btnLast.addEventListener('click',  () => { pause(); goTo(frames.length - 1); });
    btnPlay.addEventListener('click',  () => { playing ? pause() : play(); });

    scrubberEl.addEventListener('input', () => {
        pause();
        goTo(Number(scrubberEl.value));
    });

    document.querySelectorAll('.speed-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            playSpeed = Number(btn.dataset.speed);
            document.querySelectorAll('.speed-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            if (playing) { clearInterval(playTimer); scheduleNext(); }
        });
    });

    function play() {
        if (currentFrame >= frames.length - 1) goTo(0);
        playing = true;
        btnPlay.textContent = '⏸';
        scheduleNext();
    }

    function pause() {
        playing = false;
        btnPlay.textContent = '▶';
        if (playTimer) { clearInterval(playTimer); playTimer = null; }
    }

    function scheduleNext() {
        if (playTimer) clearInterval(playTimer);
        playTimer = setInterval(() => {
            if (currentFrame < frames.length - 1) {
                goTo(currentFrame + 1);
            } else {
                pause();
            }
        }, BASE_INTERVAL_MS / playSpeed);
    }

    function goTo(idx) {
        idx = Math.max(0, Math.min(frames.length - 1, idx));
        currentFrame = idx;
        scrubberEl.value = idx;
        renderFrame(idx);
        updateMoveHighlight(idx);
    }

    // ── Rendering ────────────────────────────────────────────────────────────
    function renderFrame(idx) {
        const frame = frames[idx];

        // Mirror game.js board layout: fixed 4-column base grid for indices
        // 0..11; extra cards stack in column 5+ (one column per 3 extra cards).
        const extraCols = frame.board.length > 12 ? Math.ceil((frame.board.length - 12) / 3) : 0;
        const cols = 4 + extraCols;
        boardEl.style.gridTemplateColumns = `repeat(${cols}, var(--col-w))`;
        boardEl.style.gridTemplateRows = 'repeat(3, auto)';
        boardEl.style.gridAutoFlow = 'row';
        document.body.style.setProperty('--board-cols', String(cols));

        // Assign explicit grid position so every card (including blank
        // placeholders) stays pinned to its logical board slot regardless of
        // surrounding nulls or expanded-column state.
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

        boardEl.innerHTML = '';
        frame.board.forEach((cardId, i) => {
            const div = document.createElement('div');
            div.className = 'card';
            placeCell(div, i);
            if (!cardId) {
                div.classList.add('blank');
            } else {
                const isHighlighted = frame.foundCards && frame.foundCards.includes(cardId);
                if (isHighlighted) div.classList.add('found-set');
                const card = SetClient.parseCard(cardId);
                div.innerHTML = CardRenderer.render(card, false);
            }
            boardEl.appendChild(div);
        });

        // Status bar
        const move = frame.move;
        let moveDesc = '';
        if (move) {
            if (move.kind === 'deal') {
                moveDesc = '💡 Deal 3 more cards';
            } else if (move.kind === 'hint') {
                moveDesc = '💡 Hint completed the set';
            } else {
                moveDesc = `${escapeHtml(move.playerName)} found a set!`;
            }
        } else {
            moveDesc = 'Game start';
        }
        const elapsed = formatElapsed(frame.elapsed);
        statusEl.textContent = `Move ${idx} / ${frames.length - 1}  •  ${elapsed}  •  ${moveDesc}`;
    }

    function buildMoveList(frames, players) {
        moveListEl.innerHTML = '';
        // Player name → color index mapping
        const playerColors = {};
        let colorIdx = 0;
        frames.forEach((frame, i) => {
            if (!frame.move) return; // skip frame 0 (initial deal)
            const li = document.createElement('li');
            li.className = 'move-item';
            li.dataset.frameIdx = i;

            const move = frame.move;
            const elapsed = formatElapsed(frame.elapsed);

            if (move.kind === 'deal') {
                li.innerHTML = `<span class="move-elapsed">${elapsed}</span><span class="move-actor">💡 Deal 3</span>`;
            } else {
                const isHint = move.kind === 'hint';
                const actor = isHint ? '💡 Hint' : escapeHtml(move.playerName);
                if (!isHint && !playerColors[move.playerName]) {
                    playerColors[move.playerName] = `color-${(colorIdx++ % 6) + 1}`;
                }
                const cls = isHint ? '' : playerColors[move.playerName];
                li.innerHTML = `<span class="move-elapsed">${elapsed}</span><span class="move-actor ${cls}">${actor}</span><span class="move-check">✓</span>`;
            }
            li.addEventListener('click', () => { pause(); goTo(i); });
            moveListEl.appendChild(li);
        });
    }

    function updateMoveHighlight(idx) {
        moveListEl.querySelectorAll('.move-item').forEach(li => {
            li.classList.toggle('current', Number(li.dataset.frameIdx) === idx);
        });
        // Scroll the current item into view
        const current = moveListEl.querySelector('.move-item.current');
        if (current) current.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }

    // ── Utilities ────────────────────────────────────────────────────────────
    function formatElapsed(ms) {
        const totalSec = Math.max(0, Math.floor(ms / 1000));
        const m = Math.floor(totalSec / 60);
        const s = totalSec % 60;
        return m > 0 ? `${m}:${String(s).padStart(2, '0')}` : `0:${String(s).padStart(2, '0')}`;
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
        }[c]));
    }
});

// ── Frame builder ─────────────────────────────────────────────────────────────
// Reconstructs board state from the export's initialDeck + moves[].
// Returns array of { board, foundCards, move, elapsed }.
function buildFrames(initialDeck, moves, startedAt) {
    // Start with a copy of the deck and deal the last 12 cards to the board.
    const deck = initialDeck.slice();
    const board = [];
    for (let i = 0; i < 12; i++) {
        board.push(deck.pop());
    }

    const frames = [];
    // Frame 0: initial board state after the opening deal
    frames.push({ board: board.slice(), foundCards: null, move: null, elapsed: 0 });

    for (const move of moves) {
        const elapsed = move.timestamp - startedAt;

        if (move.kind === 'deal') {
            // DealMore: add the 3 recorded cards to the board.
            for (const cardId of move.cards) {
                board.push(cardId);
                // Remove from deck so positions stay in sync.
                const di = deck.lastIndexOf(cardId);
                if (di >= 0) deck.splice(di, 1);
            }
            frames.push({ board: board.slice(), foundCards: null, move, elapsed });
            continue;
        }

        // Set found (kind "found" or "hint"): highlight the matched cards,
        // then produce a post-fill frame.
        const foundCards = move.cards ? move.cards.slice() : [];

        // Frame BEFORE removal: show the board with found-set highlight.
        frames.push({ board: board.slice(), foundCards, move, elapsed });

        // Apply the removal and fill on the working board for the next frame.
        const blankIndices = [];
        for (const cardId of foundCards) {
            const idx = board.indexOf(cardId);
            if (idx >= 0) {
                board[idx] = null;
                blankIndices.push(idx);
            }
        }
        replayFillBlanks(board, deck, blankIndices);
    }

    return frames;
}

// Mirror of SetGameEngine.FillBlanks in C#.
function replayFillBlanks(board, deck, blankIndices) {
    if (deck.length >= 3 && board.length <= 12) {
        // Standard refill: put a new card in each blank slot.
        for (const idx of blankIndices) {
            board[idx] = deck.pop();
        }
    } else if (deck.length >= 3) {
        // Expanded board (>12): fill holes in [0..11] from rightmost extras,
        // then collapse null slots in the extras region.
        const holes = blankIndices.filter(i => i < 12).sort((a, b) => a - b);
        for (const hole of holes) {
            let srcIdx = -1;
            for (let i = board.length - 1; i >= 12; i--) {
                if (board[i] != null) { srcIdx = i; break; }
            }
            if (srcIdx < 0) break;
            board[hole] = board[srcIdx];
            board[srcIdx] = null;
        }
        // Remove null slots from the extras region (indices >= 12).
        for (let i = board.length - 1; i >= 12; i--) {
            if (board[i] == null) board.splice(i, 1);
        }
    }
    // else: deck exhausted; leave nulls in place.
}
