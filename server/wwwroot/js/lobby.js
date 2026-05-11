// Lobby controller. Lists open games, lets the user create one, and
// navigates to /game.html?id=... to actually play.

document.addEventListener('DOMContentLoaded', async () => {
    const meEl = document.getElementById('me');
    const gamesListEl = document.getElementById('games-list');
    const emptyEl = document.getElementById('lobby-empty');
    const leaderboardEl = document.getElementById('leaderboard-list');
    const leaderboardEmptyEl = document.getElementById('leaderboard-empty');
    const createForm = document.getElementById('create-form');
    const createInput = document.getElementById('create-name');
    const refreshBtn = document.getElementById('refresh-btn');

    await SetClient.loadMe();
    meEl.textContent = SetClient.me.name;

    function gotoGame(id) {
        window.location.href = `/game.html?id=${encodeURIComponent(id)}`;
    }

    async function refreshLeaderboard() {
        try {
            const leaders = await SetClient.getLeaderboard();
            leaderboardEl.innerHTML = '';
            leaderboardEmptyEl.hidden = leaders.length > 0;
            const medals = ['🥇', '🥈', '🥉'];
            leaders.forEach((entry, i) => {
                const li = document.createElement('li');
                const rankNum = i + 1;
                li.className = `leader-row leader-rank-${rankNum}`;
                const rankLabel = rankNum <= 3 ? medals[i] : `${rankNum}.`;
                const starHtml = rankNum === 1
                    ? `<span class="leader-star">✨</span>`
                    : '';
                li.innerHTML = `
                    <span class="leader-rank">${rankLabel}</span>
                    <span class="leader-name">${escapeHtml(entry.playerName)}</span>
                    <span class="leader-score"><strong>${entry.score}</strong> pts</span>
                    <span class="leader-wins">${entry.wins} win${entry.wins === 1 ? '' : 's'}</span>
                    ${starHtml}
                `;
                leaderboardEl.appendChild(li);
            });
        } catch {
            // Leaderboard is non-critical; don't block the lobby if it fails.
        }
    }

    async function refreshLobby() {
        const games = await SetClient.listGames();
        gamesListEl.innerHTML = '';
        emptyEl.hidden = games.length > 0;
        for (const g of games) {
            const li = document.createElement('li');
            li.className = 'game-row';
            const startedAgo = formatAgo(Date.now() - g.startedAt);
            li.innerHTML = `
                <div class="game-row-main">
                    <span class="game-row-name">${escapeHtml(g.name)}</span>
                    <span class="game-row-meta">${g.playerCount} player${g.playerCount === 1 ? '' : 's'} · started ${startedAgo}</span>
                </div>
                <button class="join-btn" data-id="${g.id}">Join</button>
            `;
            gamesListEl.appendChild(li);
        }
        gamesListEl.querySelectorAll('.join-btn').forEach(btn =>
            btn.addEventListener('click', () => gotoGame(btn.dataset.id)));
    }

    refreshBtn.addEventListener('click', () => { refreshLobby(); refreshLeaderboard(); });
    createForm.addEventListener('submit', async e => {
        e.preventDefault();
        const name = createInput.value.trim();
        if (!name) return;
        const summary = await SetClient.createGame(name);
        createInput.value = '';
        gotoGame(summary.id);
    });

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
        }[c]));
    }

    function formatAgo(ms) {
        const s = Math.max(0, Math.floor(ms / 1000));
        if (s < 60) return `${s}s ago`;
        const m = Math.floor(s / 60);
        if (m < 60) return `${m}m ago`;
        const h = Math.floor(m / 60);
        return `${h}h ago`;
    }

    refreshLobby();
    refreshLeaderboard();
});
