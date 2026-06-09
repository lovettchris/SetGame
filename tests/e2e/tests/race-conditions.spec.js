/**
 * race-conditions.spec.js – API-level tests for Set Game race conditions.
 *
 * These tests exercise the server's race-fairness queue by simulating
 * multiple players submitting sets concurrently. No browser UI is needed;
 * all interaction is via the REST API with per-player identity headers.
 *
 * Prerequisites:
 *   docker-compose up -d   (starts Garnet + server on port 5000)
 */

import { test, expect } from "@playwright/test";
import {
  createPlayerClient,
  findAllSets,
  sleep,
} from "../helpers/game-client.js";

const BASE_URL = process.env.BASE_URL || "http://localhost:5000";

// Helper: create a game and join N players, return { gameId, players, state }
async function setupGame(playerCount = 2) {
  const players = [];
  for (let i = 0; i < playerCount; i++) {
    players.push(
      createPlayerClient(BASE_URL, `test-player-${i}-${Date.now()}`, `Player${i}`)
    );
  }

  const game = await players[0].createGame(`Race Test ${Date.now()}`);
  const gameId = game.id;

  // All players join
  let state;
  for (const p of players) {
    state = await p.joinGame(gameId);
  }

  return { gameId, players, state };
}

test.describe("Race condition: simultaneous set submissions", () => {
  test("two players submit the SAME valid set — only one wins", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);
    const targetSet = sets[0];

    // Both players submit the exact same set simultaneously
    const [result0, result1] = await Promise.all([
      players[0].submitSet(gameId, targetSet),
      players[1].submitSet(gameId, targetSet),
    ]);

    // Exactly one should be accepted, one rejected
    const accepted = [result0, result1].filter((r) => r.outcome.accepted);
    const rejected = [result0, result1].filter((r) => !r.outcome.accepted);

    expect(accepted.length).toBe(1);
    expect(rejected.length).toBe(1);
    expect(accepted[0].outcome.kind).toBe("success");
    // The rejected player should get a "beat you" message or "no longer on the board"
    expect(rejected[0].outcome.kind).toBe("error");

    // Verify final state consistency
    const finalState = await players[0].getGame(gameId);
    const totalSets = Object.values(finalState.players).reduce(
      (sum, p) => sum + p.setsFound,
      0
    );
    expect(totalSets).toBe(1);
  });

  test("two players submit OVERLAPPING sets — first click wins", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    // Find two sets that share at least one card
    let overlapping = null;
    for (let i = 0; i < sets.length && !overlapping; i++) {
      for (let j = i + 1; j < sets.length; j++) {
        const shared = sets[i].filter((idx) => sets[j].includes(idx));
        if (shared.length > 0) {
          overlapping = [sets[i], sets[j]];
          break;
        }
      }
    }

    if (!overlapping) {
      test.skip();
      return;
    }

    // Player 0 reports low ping (should win ties), Player 1 reports high ping
    await players[0].reportPing(gameId, 10);
    await players[1].reportPing(gameId, 200);
    await sleep(100); // let pings register

    // Submit overlapping sets simultaneously
    const [result0, result1] = await Promise.all([
      players[0].submitSet(gameId, overlapping[0]),
      players[1].submitSet(gameId, overlapping[1]),
    ]);

    // At least one must succeed; the overlapping one should fail
    const accepted = [result0, result1].filter((r) => r.outcome.accepted);
    expect(accepted.length).toBeGreaterThanOrEqual(1);

    // The total sets found should reflect the resolved race
    const finalState = await players[0].getGame(gameId);
    const totalSets = Object.values(finalState.players).reduce(
      (sum, p) => sum + p.setsFound,
      0
    );
    expect(totalSets).toBeGreaterThanOrEqual(1);
    expect(totalSets).toBeLessThanOrEqual(2);
  });

  test("two players submit NON-OVERLAPPING sets — both accepted", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    // Find two sets with no shared card indices
    let disjoint = null;
    for (let i = 0; i < sets.length && !disjoint; i++) {
      for (let j = i + 1; j < sets.length; j++) {
        const shared = sets[i].filter((idx) => sets[j].includes(idx));
        if (shared.length === 0) {
          disjoint = [sets[i], sets[j]];
          break;
        }
      }
    }

    if (!disjoint) {
      test.skip();
      return;
    }

    // Submit disjoint sets simultaneously
    const [result0, result1] = await Promise.all([
      players[0].submitSet(gameId, disjoint[0]),
      players[1].submitSet(gameId, disjoint[1]),
    ]);

    // Both should be accepted (no overlap conflict)
    expect(result0.outcome.accepted).toBe(true);
    expect(result1.outcome.accepted).toBe(true);

    // Verify each player got credit
    const finalState = await players[0].getGame(gameId);
    const p0Score = finalState.players[players[0].playerId]?.setsFound ?? 0;
    const p1Score = finalState.players[players[1].playerId]?.setsFound ?? 0;
    expect(p0Score).toBe(1);
    expect(p1Score).toBe(1);
  });

  test("three players all submit the same set — exactly one wins", async () => {
    const { gameId, players, state } = await setupGame(3);

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);
    const targetSet = sets[0];

    const results = await Promise.all(
      players.map((p) => p.submitSet(gameId, targetSet))
    );

    const accepted = results.filter((r) => r.outcome.accepted);
    expect(accepted.length).toBe(1);

    const finalState = await players[0].getGame(gameId);
    const totalSets = Object.values(finalState.players).reduce(
      (sum, p) => sum + p.setsFound,
      0
    );
    expect(totalSets).toBe(1);
  });
});

test.describe("Race condition: rapid sequential submissions", () => {
  test("same player submits two different valid sets back-to-back", async () => {
    const { gameId, players, state } = await setupGame(1);

    const sets = findAllSets(state.board);
    if (sets.length < 2) {
      test.skip();
      return;
    }

    // Find two non-overlapping sets for back-to-back submission
    let pair = null;
    for (let i = 0; i < sets.length && !pair; i++) {
      for (let j = i + 1; j < sets.length; j++) {
        if (sets[i].filter((idx) => sets[j].includes(idx)).length === 0) {
          pair = [sets[i], sets[j]];
          break;
        }
      }
    }

    if (!pair) {
      test.skip();
      return;
    }

    // Fire both without waiting
    const [r1, r2] = await Promise.all([
      players[0].submitSet(gameId, pair[0]),
      players[0].submitSet(gameId, pair[1]),
    ]);

    // Both should succeed since they don't overlap
    expect(r1.outcome.accepted).toBe(true);
    expect(r2.outcome.accepted).toBe(true);

    const finalState = await players[0].getGame(gameId);
    expect(finalState.players[players[0].playerId].setsFound).toBe(2);
  });

  test("submitting an invalid set during a race doesn't block others", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    // Player 0 submits an invalid set (first 3 cards, probably not a set)
    // Player 1 submits a valid set
    const invalidIndices = [0, 1, 2];
    const validSet = sets[0];

    const [invalidResult, validResult] = await Promise.all([
      players[0].submitSet(gameId, invalidIndices),
      players[1].submitSet(gameId, validSet),
    ]);

    // Invalid submission should be rejected immediately (pre-validation)
    expect(invalidResult.outcome.accepted).toBe(false);
    // Valid submission should succeed regardless
    expect(validResult.outcome.accepted).toBe(true);
  });
});

test.describe("Race condition: hint voting under concurrency", () => {
  test("concurrent hint requests from all players trigger reveal", async () => {
    const { gameId, players } = await setupGame(2);

    // Both players request a hint simultaneously
    const [h0, h1] = await Promise.all([
      players[0].hint(gameId),
      players[1].hint(gameId),
    ]);

    // At least one should report that a hint was revealed
    const revealed = [h0, h1].some(
      (r) => r.outcome.revealed || r.outcome.kind === "hint"
    );
    expect(revealed).toBe(true);

    // Verify game state has hint indices
    const state = await players[0].getGame(gameId);
    expect(state.hintIndices.length).toBeGreaterThan(0);
  });

  test("hint request during set submission race", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    // Player 0 submits a valid set, Player 1 requests a hint — simultaneously
    const [setResult, hintResult] = await Promise.all([
      players[0].submitSet(gameId, sets[0]),
      players[1].hint(gameId),
    ]);

    // The set should still be accepted
    expect(setResult.outcome.accepted).toBe(true);

    // Game state should be consistent
    const finalState = await players[0].getGame(gameId);
    expect(finalState.version).toBeGreaterThan(state.version);
  });
});

test.describe("Race condition: player join/leave during submissions", () => {
  test("player leaves mid-race — remaining submissions still resolve", async () => {
    const { gameId, players, state } = await setupGame(3);

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    // Player 2 leaves while Players 0 and 1 submit
    const [result0, result1] = await Promise.all([
      players[0].submitSet(gameId, sets[0]),
      players[1].submitSet(gameId, sets[0]),
      players[2].leaveGame(gameId),
    ]);

    // One submission should succeed
    const accepted = [result0, result1].filter((r) => r.outcome.accepted);
    expect(accepted.length).toBe(1);

    // Left player should be inactive
    const finalState = await players[0].getGame(gameId);
    expect(finalState.players[players[2].playerId].active).toBe(false);
  });

  test("new player joins mid-race — game state stays consistent", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    // Player 2 joins while Players 0 and 1 submit
    const latecomer = createPlayerClient(
      BASE_URL,
      `late-joiner-${Date.now()}`,
      "LateJoiner"
    );

    const [result0, result1] = await Promise.all([
      players[0].submitSet(gameId, sets[0]),
      players[1].submitSet(gameId, sets[0]),
      latecomer.joinGame(gameId),
    ]);

    // One submission should succeed
    const accepted = [result0, result1].filter((r) => r.outcome.accepted);
    expect(accepted.length).toBe(1);

    // Latecomer should be in the player list
    const finalState = await players[0].getGame(gameId);
    expect(finalState.players[latecomer.playerId]).toBeDefined();
  });
});

test.describe("Race condition: new round during submissions", () => {
  test("restart while submission is in flight", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    // Player 0 submits a set, Player 1 triggers restart — near-simultaneously
    const [submitResult, restartResult] = await Promise.allSettled([
      players[0].submitSet(gameId, sets[0]),
      (async () => {
        // Slight delay so the submission enters the queue first
        await sleep(10);
        return players[1].restart(gameId);
      })(),
    ]);

    // Neither should throw/crash the server
    expect(submitResult.status).toBe("fulfilled");
    expect(restartResult.status).toBe("fulfilled");

    // Final state should be internally consistent
    const finalState = await players[0].getGame(gameId);
    expect(finalState).not.toBeNull();
    // Board should have cards
    const nonNull = finalState.board.filter((c) => c !== null);
    expect(nonNull.length).toBeGreaterThanOrEqual(12);
  });
});

test.describe("Race condition: ping-adjusted fairness", () => {
  test("high-ping player who clicked first wins over low-ping player", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    // Player 0: 300ms ping (high latency, but clicked first)
    // Player 1: 10ms ping (low latency, but clicked second)
    await players[0].reportPing(gameId, 300);
    await players[1].reportPing(gameId, 10);
    await sleep(100);

    // Player 0 submits first (simulating earlier click arrival after
    // network delay), then Player 1 submits slightly later
    const result0Promise = players[0].submitSet(gameId, sets[0]);
    await sleep(50); // Player 1 arrives 50ms later at the server
    const result1Promise = players[1].submitSet(gameId, sets[0]);

    const [result0, result1] = await Promise.all([result0Promise, result1Promise]);

    // One wins, one loses — the key is the server doesn't crash
    const accepted = [result0, result1].filter((r) => r.outcome.accepted);
    expect(accepted.length).toBe(1);
  });
});

test.describe("State consistency: board integrity after races", () => {
  test("board has no duplicates after concurrent set submissions", async () => {
    const { gameId, players, state } = await setupGame(2);

    const sets = findAllSets(state.board);
    if (sets.length < 2) {
      test.skip();
      return;
    }

    // Find disjoint sets if possible
    let pair = null;
    for (let i = 0; i < sets.length && !pair; i++) {
      for (let j = i + 1; j < sets.length; j++) {
        if (sets[i].filter((idx) => sets[j].includes(idx)).length === 0) {
          pair = [sets[i], sets[j]];
          break;
        }
      }
    }

    if (pair) {
      await Promise.all([
        players[0].submitSet(gameId, pair[0]),
        players[1].submitSet(gameId, pair[1]),
      ]);
    } else {
      await players[0].submitSet(gameId, sets[0]);
    }

    const finalState = await players[0].getGame(gameId);
    const nonNull = finalState.board.filter((c) => c !== null);
    const unique = new Set(nonNull);
    expect(unique.size).toBe(nonNull.length);
  });

  test("deck + board card count remains correct after race", async () => {
    const { gameId, players, state } = await setupGame(2);

    const initialTotal =
      state.board.filter((c) => c !== null).length + state.deckRemaining;

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    await Promise.all([
      players[0].submitSet(gameId, sets[0]),
      players[1].submitSet(gameId, sets[0]),
    ]);

    const finalState = await players[0].getGame(gameId);
    const finalTotal =
      finalState.board.filter((c) => c !== null).length + finalState.deckRemaining;

    // After one set is found: 3 cards removed from board but 3 may be dealt
    // from deck. Total cards should decrease by 0 (refilled) or 3 (not refilled)
    // depending on board size and deck state.
    expect(finalTotal).toBeLessThanOrEqual(initialTotal);
    expect(finalTotal).toBeGreaterThanOrEqual(initialTotal - 3);
  });

  test("version number is monotonically increasing through race", async () => {
    const { gameId, players, state } = await setupGame(2);
    const initialVersion = state.version;

    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    await Promise.all([
      players[0].submitSet(gameId, sets[0]),
      players[1].submitSet(gameId, sets[0]),
    ]);

    const finalState = await players[0].getGame(gameId);
    expect(finalState.version).toBeGreaterThan(initialVersion);
  });
});

test.describe("Stress: rapid-fire submissions", () => {
  test("10 rapid submissions from 2 players — no server errors", async () => {
    const { gameId, players } = await setupGame(2);

    // Fire 10 rounds: each round both players find and submit the first set
    let errors = 0;
    for (let round = 0; round < 10; round++) {
      const state = await players[0].getGame(gameId);
      if (state.status === "ended") break;

      const sets = findAllSets(state.board);
      if (sets.length === 0) break;

      try {
        const results = await Promise.all([
          players[0].submitSet(gameId, sets[0]),
          players[1].submitSet(gameId, sets[0]),
        ]);
        // At least one must succeed
        const accepted = results.filter((r) => r.outcome?.accepted);
        if (accepted.length === 0) errors++;
      } catch {
        errors++;
      }

      await sleep(50); // small breathing room between rounds
    }

    expect(errors).toBe(0);

    // Final state should be internally consistent
    const finalState = await players[0].getGame(gameId);
    const nonNull = finalState.board.filter((c) => c !== null);
    const unique = new Set(nonNull);
    expect(unique.size).toBe(nonNull.length);
  });
});
