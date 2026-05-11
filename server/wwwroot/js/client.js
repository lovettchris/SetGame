// SetGame API + SSE client. All game state is server-authoritative; the
// browser only POSTs intents and reflects state pushed via Server-Sent Events.

const SetClient = {
    me: null,

    /** Attach the resolved identity to every API call so the server uses
     *  the gatekeeper-derived name, not a fresh per-cookie guest id. */
    _apiFetch(url, opts) {
        const init = { ...(opts || {}) };
        const headers = new Headers(init.headers || {});
        if (this.me) {
            // Encode to keep header values strictly ASCII (names may contain
            // non-ASCII characters). Server URL-decodes them.
            headers.set('X-SetGame-Player-Id', encodeURIComponent(this.me.id));
            headers.set('X-SetGame-Player-Name', encodeURIComponent(this.me.name));
        }
        init.headers = headers;
        return fetch(url, init);
    },

    async loadMe() {
        try {
            const r = await fetch('https://gatekeeper.msrhub.microsoft.com/definitions/whoami', {
                credentials: 'include',
            });
            if (r.ok) {
                const raw = (await r.text()).trim().replace(/^"|"$/g, '');
                if (raw) {
                    // Strip @domain.* so the player bubble shows just the alias.
                    const name = raw.replace(/@[^@\s]+$/, '');
                    this.me = { id: raw, name };
                    return this.me;
                }
            }
        } catch { /* network or CORS error: fall back to guest */ }
        this.me = await this._guestMe();
        return this.me;
    },

    async _guestMe() {
        const read = () => {
            const m = document.cookie.match(/(?:^|;\s*)setgame_pid=([^;]+)/);
            return m ? decodeURIComponent(m[1]) : null;
        };
        let pid = read();
        if (!pid) {
            // Touch any server endpoint so PlayerIdentity pins a setgame_pid cookie.
            try { await fetch('/api/games'); } catch {}
            pid = read();
        }
        if (!pid) {
            // Last-ditch fallback if cookie still wasn't set (e.g. blocked).
            pid = 'anon-' + Math.random().toString(36).slice(2, 10);
        }
        const suffix = pid.startsWith('anon-') ? pid.slice(5) : pid;
        return { id: pid, name: 'Guest ' + suffix };
    },

    async listGames() {
        const r = await this._apiFetch('/api/games');
        return r.json();
    },

    async getLeaderboard() {
        const r = await this._apiFetch('/api/leaderboard');
        return r.json();
    },

    async createGame(name) {
        const r = await this._apiFetch('/api/games', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name }),
        });
        if (!r.ok) throw new Error('Failed to create game');
        return r.json();
    },

    async joinGame(id) {
        const r = await this._apiFetch(`/api/games/${id}/join`, { method: 'POST' });
        if (!r.ok) throw new Error('Failed to join game');
        return r.json();
    },

    async leaveGame(id) {
        try { await this._apiFetch(`/api/games/${id}/leave`, { method: 'POST' }); } catch {}
    },

    async submitSelection(id, indices) {
        const r = await this._apiFetch(`/api/games/${id}/select`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ indices }),
        });
        return r.json();
    },

    /** Measure round-trip time to the server. Returns ms (rounded). */
    async measurePing() {
        const t0 = (typeof performance !== 'undefined' ? performance.now() : Date.now());
        try {
            await this._apiFetch('/api/ping', { cache: 'no-store' });
        } catch { return null; }
        const t1 = (typeof performance !== 'undefined' ? performance.now() : Date.now());
        return Math.max(0, Math.round(t1 - t0));
    },

    /** Tell the server this client's most recent measured RTT so it can
     *  size the race-fairness window for set submissions. */
    async reportPing(id, pingMs) {
        try {
            await this._apiFetch(`/api/games/${id}/ping`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ pingMs }),
            });
        } catch {}
    },

    async hint(id, selection) {
        const r = await this._apiFetch(`/api/games/${id}/hint`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ selection }),
        });
        return r.json();
    },

    async deal3(id) {
        const r = await this._apiFetch(`/api/games/${id}/deal3`, { method: 'POST' });
        return r.json();
    },

    async restart(id) {
        const r = await this._apiFetch(`/api/games/${id}/restart`, { method: 'POST' });
        return r.json();
    },

    /** Download a JSON record of the game (initial deck + every move).
     *  Triggers a browser download — does not return the parsed body. */
    async exportGame(id, name) {
        const r = await this._apiFetch(`/api/games/${id}/export`);
        if (!r.ok) throw new Error('Export failed');
        const blob = await r.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        const safe = (name || id).replace(/[^a-z0-9-_]+/gi, '_');
        a.href = url;
        a.download = `setgame-${safe}-${id}.json`;
        document.body.appendChild(a);
        a.click();
        a.remove();
        // Give the browser a tick to start the download before revoking.
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    /** Open an EventSource for the given game; calls onState(stateObject)
     *  for every "state" event, including the initial one. */
    openEvents(id, onState) {
        const es = new EventSource(`/api/games/${id}/events`);
        es.addEventListener('state', e => {
            try { onState(JSON.parse(e.data)); } catch (err) { console.error(err); }
        });
        es.onerror = err => console.warn('SSE error', err);
        return es;
    },

    /** Parse a server card id like "2-oval-striped-purple" into the shape the
     *  CardRenderer expects. */
    parseCard(id) {
        const [number, shape, shading, color] = id.split('-');
        return { id, number: Number(number), shape, shading, color };
    },
};
