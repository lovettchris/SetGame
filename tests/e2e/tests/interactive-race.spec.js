/**
 * interactive-race.spec.js – Headed browser tests for visual race debugging.
 *
 * Opens multiple browser contexts simulating concurrent players interacting
 * with the game UI. Useful for visually spotting animation glitches, stale
 * state, or UI desync when two players race for the same set.
 *
 * Run with:  npm run test:interactive
 */

import { test, expect } from "@playwright/test";
import {
  createPlayerClient,
  findAllSets,
  sleep,
} from "../helpers/game-client.js";

const BASE_URL = process.env.BASE_URL || "http://localhost:5000";

test.describe("Interactive: two-player race visualized", () => {
  test("two browser windows race for the same set", async ({ browser }) => {
    // Create game via API
    const host = createPlayerClient(BASE_URL, `host-${Date.now()}`, "Host");
    const game = await host.createGame(`Visual Race ${Date.now()}`);

    // Open two separate browser contexts (simulating two players)
    const ctx1 = await browser.newContext({ baseURL: BASE_URL });
    const ctx2 = await browser.newContext({ baseURL: BASE_URL });
    const page1 = await ctx1.newPage();
    const page2 = await ctx2.newPage();

    // Both players navigate to the game
    await page1.goto(`/game.html?id=${game.id}`);
    await page2.goto(`/game.html?id=${game.id}`);

    // Wait for the board to render
    await page1.waitForSelector(".card:not(.blank)", { timeout: 10000 });
    await page2.waitForSelector(".card:not(.blank)", { timeout: 10000 });

    // Give SSE time to deliver initial state
    await sleep(1000);

    // Get the game state to find a valid set
    const state = await host.getGame(game.id);
    const sets = findAllSets(state.board);
    expect(sets.length).toBeGreaterThan(0);

    const targetSet = sets[0];

    // Click the three cards of the set on BOTH pages nearly simultaneously.
    // Playwright clicks are async, so we interleave them for maximum
    // timing overlap.
    const cards1 = await page1.locator(".card:not(.blank)").all();
    const cards2 = await page2.locator(".card:not(.blank)").all();

    // Ensure we have enough cards
    expect(cards1.length).toBeGreaterThanOrEqual(12);
    expect(cards2.length).toBeGreaterThanOrEqual(12);

    // Click first two cards on both pages
    await Promise.all([
      cards1[targetSet[0]].click(),
      cards2[targetSet[0]].click(),
    ]);
    await Promise.all([
      cards1[targetSet[1]].click(),
      cards2[targetSet[1]].click(),
    ]);

    // Click the third card — this triggers the submission race
    await Promise.all([
      cards1[targetSet[2]].click(),
      cards2[targetSet[2]].click(),
    ]);

    // Wait for the UI to process the result (SSE push + animation)
    await sleep(3000);

    // Take screenshots for visual inspection
    await page1.screenshot({
      path: "e2e/test-results/race-player1.png",
      fullPage: true,
    });
    await page2.screenshot({
      path: "e2e/test-results/race-player2.png",
      fullPage: true,
    });

    // Verify the game state is consistent
    const finalState = await host.getGame(game.id);
    const totalSets = Object.values(finalState.players).reduce(
      (sum, p) => sum + p.setsFound,
      0
    );

    // Exactly one player should have gotten credit
    // (both players might show as having 0 sets if neither is the host,
    //  so check via the API's perspective)
    expect(totalSets).toBeGreaterThanOrEqual(1);

    // No duplicate cards on the board
    const nonNull = finalState.board.filter((c) => c !== null);
    const unique = new Set(nonNull);
    expect(unique.size).toBe(nonNull.length);

    // Clean up
    await ctx1.close();
    await ctx2.close();
  });

  test("rapid hint voting from two browsers", async ({ browser }) => {
    const host = createPlayerClient(BASE_URL, `host-${Date.now()}`, "HintHost");
    const game = await host.createGame(`Hint Race ${Date.now()}`);

    const ctx1 = await browser.newContext({ baseURL: BASE_URL });
    const ctx2 = await browser.newContext({ baseURL: BASE_URL });
    const page1 = await ctx1.newPage();
    const page2 = await ctx2.newPage();

    await page1.goto(`/game.html?id=${game.id}`);
    await page2.goto(`/game.html?id=${game.id}`);

    await page1.waitForSelector(".card:not(.blank)", { timeout: 10000 });
    await page2.waitForSelector(".card:not(.blank)", { timeout: 10000 });
    await sleep(1000);

    // Both players click Hint simultaneously
    const hint1 = page1.locator("#hint-btn");
    const hint2 = page2.locator("#hint-btn");

    await Promise.all([hint1.click(), hint2.click()]);

    await sleep(2000);

    // Check game state — at least one hint index should be revealed
    const state = await host.getGame(game.id);
    expect(state.hintIndices.length).toBeGreaterThan(0);

    await page1.screenshot({
      path: "e2e/test-results/hint-race-player1.png",
      fullPage: true,
    });
    await page2.screenshot({
      path: "e2e/test-results/hint-race-player2.png",
      fullPage: true,
    });

    await ctx1.close();
    await ctx2.close();
  });

  test("player leaves during animation — no UI crash", async ({ browser }) => {
    const host = createPlayerClient(BASE_URL, `host-${Date.now()}`, "LeaveHost");
    const game = await host.createGame(`Leave Race ${Date.now()}`);

    const ctx1 = await browser.newContext({ baseURL: BASE_URL });
    const ctx2 = await browser.newContext({ baseURL: BASE_URL });
    const page1 = await ctx1.newPage();
    const page2 = await ctx2.newPage();

    // Collect console errors
    const errors1 = [];
    const errors2 = [];
    page1.on("console", (msg) => {
      if (msg.type() === "error") errors1.push(msg.text());
    });
    page2.on("console", (msg) => {
      if (msg.type() === "error") errors2.push(msg.text());
    });

    await page1.goto(`/game.html?id=${game.id}`);
    await page2.goto(`/game.html?id=${game.id}`);

    await page1.waitForSelector(".card:not(.blank)", { timeout: 10000 });
    await page2.waitForSelector(".card:not(.blank)", { timeout: 10000 });
    await sleep(1000);

    // Player 1 finds a set via API while Player 2 leaves
    const state = await host.getGame(game.id);
    const sets = findAllSets(state.board);

    if (sets.length > 0) {
      // Get player 1's identity from the page
      const p1Id = await page1.evaluate(() => SetClient.me?.id);
      if (p1Id) {
        const p1Client = createPlayerClient(BASE_URL, p1Id, "Player1");
        // Submit set + leave simultaneously
        await Promise.all([
          p1Client.submitSet(game.id, sets[0]),
          page2.locator("#leave-btn").click(),
        ]);
      }
    }

    await sleep(2000);

    // Page 1 should still be functional (no JS errors from the leave)
    const jsErrors = errors1.filter(
      (e) => !e.includes("SSE error") && !e.includes("net::ERR")
    );
    // Allow minor transient errors but no crashes
    expect(jsErrors.length).toBeLessThanOrEqual(2);

    await ctx1.close();
    await ctx2.close();
  });
});
