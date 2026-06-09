/**
 * global-setup.js – Playwright global setup hook for Set Game tests.
 *
 * Verifies the server is reachable before tests start.
 */

export default async function globalSetup(config) {
  const baseURL = process.env.BASE_URL || "http://localhost:5000";
  console.log(`[global-setup] checking server at ${baseURL}`);

  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`${baseURL}/api/ping`);
      if (res.ok) {
        console.log("[global-setup] server is ready");
        return;
      }
    } catch {
      // not ready yet
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  console.warn(
    "[global-setup] WARNING: server did not respond within 15s — tests may fail.\n" +
    "  Make sure docker-compose up is running."
  );
}
