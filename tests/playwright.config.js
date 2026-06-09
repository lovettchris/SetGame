/**
 * Playwright configuration for Set Game race-condition tests.
 *
 * ── Test projects ──────────────────────────────────────────────────────────
 *
 * api-tests (default)    API-level race condition tests. No browser UI needed;
 *                        talks directly to the REST endpoints. Requires the
 *                        server + Garnet to be running (docker-compose up).
 *
 * interactive            Headed Chromium tests that open multiple browser
 *                        contexts simulating concurrent players. Useful for
 *                        visually debugging timing issues.
 *
 * ── Quick-start commands ──────────────────────────────────────────────────
 *
 *   # Start the server stack:
 *   docker-compose up -d
 *
 *   # Install Playwright (one-time):
 *   cd tests && npm install && npx playwright install chromium
 *
 *   # Run all API race-condition tests:
 *   cd tests && npm test
 *
 *   # Run interactive (headed) tests:
 *   cd tests && npm run test:interactive
 *
 *   # Run with Playwright UI (watch mode):
 *   cd tests && npm run test:ui
 *
 * ── Environment variables ──────────────────────────────────────────────────
 *
 *   BASE_URL – Override server URL (default: http://localhost:5000)
 *
 * @type {import('@playwright/test').PlaywrightTestConfig}
 */

import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  globalSetup: "./global-setup.js",
  globalTeardown: "./global-teardown.js",
  timeout: 60_000,
  retries: process.env.CI ? 1 : 0,
  workers: 1, // serialize to avoid cross-test game interference

  reporter: [
    ["list"],
    ["html", { outputFolder: "e2e/test-results/html", open: "never" }],
  ],

  use: {
    baseURL: process.env.BASE_URL || "http://localhost:5000",
    trace: "on-first-retry",
    screenshot: "only-on-failure",
  },

  projects: [
    // API-level race condition tests — no browser needed
    {
      name: "api-tests",
      testDir: "./e2e/tests",
      testMatch: ["race-conditions.spec.js"],
      use: {
        ...devices["Desktop Chrome"],
      },
    },

    // Interactive headed tests for visual debugging (Edge)
    {
      name: "interactive",
      testDir: "./e2e/tests",
      testMatch: ["interactive-race.spec.js"],
      use: {
        ...devices["Desktop Edge"],
        channel: "msedge",
        headless: false,
        slowMo: 250,
        viewport: { width: 1400, height: 900 },
      },
    },
  ],
});
