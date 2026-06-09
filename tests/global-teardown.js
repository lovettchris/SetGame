/**
 * global-teardown.js – Playwright global teardown hook for Set Game tests.
 *
 * Nothing to clean up (no Edge/CDP management needed).
 */

export default async function globalTeardown() {
  console.log("[global-teardown] done");
}
