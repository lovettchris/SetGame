/**
 * game-client.js – API helper for Set Game Playwright tests.
 *
 * Wraps the game REST endpoints with per-player identity headers,
 * so each test can simulate multiple concurrent players without
 * needing separate browser contexts.
 */

/**
 * Create a player client that sends all requests with the given identity.
 * @param {string} baseURL - Server base URL (e.g. "http://localhost:5000")
 * @param {string} playerId - Unique player identifier
 * @param {string} playerName - Display name
 */
export function createPlayerClient(baseURL, playerId, playerName) {
  const headers = {
    "X-SetGame-Player-Id": encodeURIComponent(playerId),
    "X-SetGame-Player-Name": encodeURIComponent(playerName),
    "Content-Type": "application/json",
  };

  async function apiFetch(path, opts = {}) {
    const url = `${baseURL}${path}`;
    const res = await fetch(url, {
      ...opts,
      headers: { ...headers, ...(opts.headers || {}) },
    });
    return res;
  }

  return {
    playerId,
    playerName,

    async createGame(name) {
      const res = await apiFetch("/api/games", {
        method: "POST",
        body: JSON.stringify({ name }),
      });
      if (!res.ok) throw new Error(`createGame failed: ${res.status}`);
      return res.json();
    },

    async joinGame(id) {
      const res = await apiFetch(`/api/games/${id}/join`, { method: "POST" });
      if (!res.ok) throw new Error(`joinGame failed: ${res.status}`);
      return res.json();
    },

    async leaveGame(id) {
      await apiFetch(`/api/games/${id}/leave`, { method: "POST" });
    },

    async getGame(id) {
      const res = await apiFetch(`/api/games/${id}`);
      if (!res.ok) return null;
      return res.json();
    },

    async submitSet(id, indices) {
      const res = await apiFetch(`/api/games/${id}/select`, {
        method: "POST",
        body: JSON.stringify({ indices }),
      });
      return res.json();
    },

    async hint(id, selection = []) {
      const res = await apiFetch(`/api/games/${id}/hint`, {
        method: "POST",
        body: JSON.stringify({ selection }),
      });
      return res.json();
    },

    async reportPing(id, pingMs) {
      await apiFetch(`/api/games/${id}/ping`, {
        method: "POST",
        body: JSON.stringify({ pingMs }),
      });
    },

    async restart(id) {
      const res = await apiFetch(`/api/games/${id}/restart`, { method: "POST" });
      return res.json();
    },
  };
}

/**
 * Parse a card id like "2-oval-striped-purple" into its properties.
 */
export function parseCard(id) {
  const [number, shape, shading, color] = id.split("-");
  return { id, number: Number(number), shape, shading, color };
}

/**
 * Check whether three cards form a valid Set.
 */
export function isSet(a, b, c) {
  const pa = parseCard(a), pb = parseCard(b), pc = parseCard(c);
  const ok = (x, y, z) =>
    (x === y && y === z) || (x !== y && y !== z && x !== z);
  return (
    ok(pa.number, pb.number, pc.number) &&
    ok(pa.shape, pb.shape, pc.shape) &&
    ok(pa.shading, pb.shading, pc.shading) &&
    ok(pa.color, pb.color, pc.color)
  );
}

/**
 * Find all valid sets on the board.
 * @param {(string|null)[]} board
 * @returns {number[][]} Array of [i, j, k] index triples
 */
export function findAllSets(board) {
  const sets = [];
  for (let i = 0; i < board.length - 2; i++) {
    if (!board[i]) continue;
    for (let j = i + 1; j < board.length - 1; j++) {
      if (!board[j]) continue;
      for (let k = j + 1; k < board.length; k++) {
        if (!board[k]) continue;
        if (isSet(board[i], board[j], board[k])) {
          sets.push([i, j, k]);
        }
      }
    }
  }
  return sets;
}

/**
 * Open an SSE connection and collect state events.
 * Returns an object with .states array and .close() method.
 */
export function openSSE(baseURL, gameId) {
  const url = `${baseURL}/api/games/${gameId}/events`;
  const es = new EventSource(url);
  const states = [];
  const errors = [];

  es.addEventListener("state", (e) => {
    try {
      states.push(JSON.parse(e.data));
    } catch (err) {
      errors.push(err);
    }
  });
  es.onerror = (err) => errors.push(err);

  return {
    states,
    errors,
    close: () => es.close(),
    /** Wait until at least `n` state events have arrived, with timeout. */
    async waitForStates(n, timeoutMs = 5000) {
      const deadline = Date.now() + timeoutMs;
      while (states.length < n && Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 50));
      }
      return states.length >= n;
    },
  };
}

/**
 * Small delay helper.
 */
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
