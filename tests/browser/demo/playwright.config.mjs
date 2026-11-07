// Owner: coordinator's demo verification (branch demo/verification). The demo suite: black-box checks of each
// user-visible flow against a deployed site. It has its own config so it touches no slice config.
//
//   DEMO_URL=https://<site>/ npx playwright test --config tests/browser/demo/playwright.config.mjs
//
// Optional: DEMO_RESULTS (output directory, default tests/browser/demo/results), DEMO_OWNER/DEMO_REPO/DEMO_BRANCH/
// DEMO_TOKEN (used only when the site shows its Connect form), DEMO_CATALOG_TITLE (default "Apollo 11"),
// DEMO_GITHUB_DOUBLE (a local harness double's URL; for checking a local build only, never demo evidence),
// DEMO_SKIP_GITHUB=1 (run only the flows that need no GitHub answer: 1, 2, 5, 8; GitHub requests are blocked).
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, devices } from '@playwright/test';

const here = path.dirname(fileURLToPath(import.meta.url));
const results = process.env.DEMO_RESULTS || path.join(here, 'results');
const chromium = existsSync('/opt/pw-browsers/chromium') ? { executablePath: '/opt/pw-browsers/chromium' } : {};

export default defineConfig({
  testDir: here,
  testMatch: /flows\.spec\.mjs$/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  outputDir: path.join(results, 'artifacts'),
  reporter: [
    ['list'],
    ['json', { outputFile: path.join(results, 'demo-results.json') }],
    [path.join(here, 'summary-reporter.mjs'), { outputFile: path.join(results, 'demo-summary.md') }],
  ],
  use: { baseURL: process.env.DEMO_URL, colorScheme: 'light', actionTimeout: 15_000, navigationTimeout: 45_000, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], launchOptions: chromium } }],
});
