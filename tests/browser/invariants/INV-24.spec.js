// Owner: ui (T16). INV-24 (UI-011-O1, PERSIST-030, PERSIST-012, PERSIST-019): every save-state action and recovery
// path is operable by keyboard alone, has an accessible name, moves focus into a dialog it opens and back when it
// closes, and announces its result through the live regions. Run in WebKit (the iPad target).
import { test, expect } from '@playwright/test';
import * as kit from '../support/layout-assertions.mjs';

// ---- shared prelude (each ui spec carries its own copy: tests/browser/support is integration's) -------------------
// The built application (npm run build writes dist/). CAUSA_APP_PATH points elsewhere, e.g. a deployed URL.
const APP = process.env.CAUSA_APP_PATH || '/dist/index.html';
// The repository the deterministic suite connects to (the stateful fake behind the test build), as the journeys do.
const REPO = {
  owner: process.env.CAUSA_TEST_OWNER || 'harness',
  repo: process.env.CAUSA_TEST_REPO || 'causa-test',
  branch: process.env.CAUSA_TEST_BRANCH || 'main',
  token: process.env.CAUSA_WRITE_TOKEN || 'harness-token',
};
// The layout contract's viewports (forma-layout-contract.json).
const VP = {
  'VP-320': { width: 320, height: 640 },
  'VP-390': { width: 390, height: 844 },
  'VP-IPAD-PORTRAIT': { width: 820, height: 1180 },
  'VP-IPAD-LANDSCAPE': { width: 1180, height: 820 },
  'VP-DESKTOP': { width: 1440, height: 900 },
};
const DESTINATIONS = [
  { name: 'Investigations', hash: '#/', heading: /^Investigations$/ },
  { name: 'Administration', hash: '#/administration', heading: /^Administration$/ },
  { name: 'Settings', hash: '#/settings', heading: /^Settings$/ },
];
// The engine has rendered its first view: the loading shell is gone and the screen's heading is shown.
const open = async (page, hash = '#/') => {
  // A fresh document every time: a hash-only change would keep the previous page's focus and marks.
  await page.goto('about:blank');
  await page.goto(APP + hash);
  await expect(page.locator('#causa-main'), 'the engine did not render its first view').toBeVisible({ timeout: 30000 });
  await expect(page.locator('main h1').first()).toBeVisible({ timeout: 15000 });
};
const primary = (page) => page.getByRole('navigation', { name: 'Primary', exact: true }).filter({ visible: true }).first();
const sections = (page) => page.getByRole('navigation', { name: /investigation sections/i }).first();
const none = (label, violations) => expect(violations, `${label}: ${JSON.stringify(violations).slice(0, 1500)}`).toEqual([]);
// CAUSA_PROVIDER=harness-double answers api.github.com from the local GitHub double (synthetic, never evidence), as the
// persistence specs and the governed journeys do; otherwise nothing is intercepted and Connect reaches the real
// test repository. A page-level route a test sets itself (e.g. a refused Connect) still takes precedence.
const routedContexts = new WeakSet();
const useDouble = async (context) => {
  if (process.env.CAUSA_PROVIDER !== 'harness-double' || routedContexts.has(context)) return;
  routedContexts.add(context);
  const double = process.env.CAUSA_DOUBLE_URL || 'http://127.0.0.1:4610';
  await context.route('https://api.github.com/**', async (route) => {
    const r = route.request();
    const u = new URL(r.url());
    const cors = { 'access-control-allow-origin': '*', 'access-control-allow-headers': '*', 'access-control-allow-methods': 'GET,POST,PUT,PATCH,DELETE' };
    if (r.method() === 'OPTIONS') return route.fulfill({ status: 204, headers: cors });
    // read the whole answer before fulfilling: a streamed answer can be disposed if the page moves on
    let answer, body;
    try { answer = await route.fetch({ url: double + u.pathname + u.search }); body = await answer.body(); } catch { return route.abort('failed').catch(() => {}); }
    await route.fulfill({ status: answer.status(), headers: { ...answer.headers(), ...cors }, body }).catch(() => {});
  });
};
const connect = async (page) => {
  await useDouble(page.context());
  await open(page, '#/');
  await page.getByLabel('Repository owner', { exact: true }).fill(REPO.owner);
  await page.getByLabel('Repository', { exact: true }).fill(REPO.repo);
  await page.getByLabel('Branch', { exact: true }).fill(REPO.branch);
  await page.getByLabel('Access token', { exact: true }).fill(REPO.token);
  await page.getByRole('button', { name: 'Connect data repository', exact: true }).click();
  await expect(page.getByText(/^Connected to/).first(), 'Connect did not succeed against the test repository').toBeVisible({ timeout: 20000 });
};
// Opens the first investigation of the given copy kind ("Demonstration" or "Working copy") from the list.
const openInvestigation = async (page, tag) => {
  const cards = page.locator('[data-requirement-id="INV-039"] article');
  await expect(cards.first(), 'no investigation is listed in the test repository').toBeVisible({ timeout: 20000 });
  const card = tag ? cards.filter({ hasText: tag }).first() : cards.first();
  await expect(card, `no ${tag} investigation is listed`).toBeVisible();
  const link = card.getByRole('link').first();
  const title = (await link.innerText()).trim();
  await link.click();
  await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible({ timeout: 20000 });
  return title;
};
// ---------------------------------------------------------------------------------------------------------------------
// Each obligation test walks several screens and viewports.
test.describe.configure({ timeout: 300_000 });

const tabTo = async (page, locator, max = 60) => {
  for (let i = 0; i < max; i++) {
    if (await locator.evaluate((e) => e === document.activeElement).catch(() => false)) return true;
    await page.keyboard.press('Tab');
  }
  return false;
};

test('save-state and conflict actions by keyboard and screen reader', async ({ page }) => {
  await connect(page);
  // A working copy is writable; the save state lives in its header.
  await openInvestigation(page, 'Working copy');
  const status = page.getByRole('status', { name: /save state/i }).first();
  await expect(status, 'no status named "Save state"').toBeVisible({ timeout: 20000 });
  await expect(status).toHaveText(/^\s*(Saved|Unsaved|Saving|Offline|Conflict|Unknown commit outcome|Save failed)\s*$/);
  // The status is announced: role=status is a polite live region; an assertive region exists for failures.
  expect(await status.evaluate((e) => e.getAttribute('role') === 'status' || e.getAttribute('aria-live') === 'polite')).toBe(true);
  expect(await page.locator('[aria-live="assertive"], [role="alert"]').count()).toBeGreaterThan(0);

  // Make a change by keyboard alone, then Save by keyboard alone.
  const observation = page.getByLabel('Observation text', { exact: true });
  expect(await tabTo(page, observation), 'Observation text is not reachable by Tab').toBe(true);
  await page.keyboard.type(`inv24-${Date.now().toString(36)}`);
  const record = page.getByRole('button', { name: 'Record an observation', exact: true });
  expect(await tabTo(page, record), 'Record an observation is not reachable by Tab').toBe(true);
  await page.keyboard.press('Enter');
  await expect(status).toHaveText(/^\s*Unsaved\s*$/);
  const save = page.getByRole('button', { name: 'Save', exact: true });
  expect(await tabTo(page, save), 'Save is not reachable by Tab').toBe(true);
  await page.keyboard.press('Enter');
  await expect(status).toHaveText(/^\s*(Saving|Saved)\s*$/);
  await expect(status).toHaveText(/^\s*Saved\s*$/, { timeout: 20000 });
  const polite = await page.locator('[aria-live="polite"]').allInnerTexts();
  expect(polite.join(' ') + (await status.innerText()), 'the save result was not announced').toMatch(/saved/i);

  // Every save-state action on the page has an accessible name and is a real control.
  for (const name of [/^Save$/, /^Retry$/, /^Check again$/, /^Reconnect$/, /^Review/, /^Reapply/, /^Discard/]) {
    const controls = page.getByRole('button', { name });
    for (let i = 0; i < await controls.count(); i++) {
      const c = controls.nth(i);
      if (!(await c.isVisible())) continue;
      expect(await c.evaluate((e) => e.tabIndex >= 0 && !e.hasAttribute('aria-hidden'))).toBe(true);
    }
  }
  // Dialogs that save-state actions open declare their invoker and trap and return focus (FL-04).
  for (const id of ['VP-320', 'VP-DESKTOP']) {
    await page.setViewportSize(VP[id]);
    none(`${id} dialogFocus`, await kit.dialogFocus(page));
    none(`${id} focusVisible`, await kit.focusVisible(page, { maxTabs: 40 }));
  }
});
