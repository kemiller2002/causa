// Owner: ui (T22). The four catalog investigations open read-only, and every section the workspace lists renders its
// heading and its records with no page error, at desktop and at 390 px without sideways scrolling.
//
// Runs against the built application (dist/) with GitHub answered by the governed harness double
// (Indy-init verification/harness/github-double.mjs), seeded with causa-data's active catalog (demonstration-v1).
// The double is synthetic and never real-provider evidence. Without it this spec fails closed: BLOCKED.
//   CAUSA_DOUBLE_URL   the double's base URL (default http://127.0.0.1:4611)
//   CAUSA_APP_PATH     the application path on the test server (default /dist/index.html)
import { test, expect } from '@playwright/test';

const APP = process.env.CAUSA_APP_PATH || '/dist/index.html';
const DOUBLE = process.env.CAUSA_DOUBLE_URL || 'http://127.0.0.1:4611';
const CASES = [
  { name: 'Apollo 11', title: /^Apollo 11 lunar landing/ },
  { name: 'Apollo 1', title: /^Apollo 1 \(AS-204\)/ },
  { name: 'Challenger', title: /Challenger/ },
  { name: 'Burned-out ceiling lightbulb', title: /lightbulb/i },
];
const SECTIONS = ['Overview', 'Causal Analysis', 'Evidence', 'Hypotheses', 'Timeline', 'Corrective Actions'];

test.describe.configure({ timeout: 240_000 });

const connected = async (browser, viewport) => {
  const ok = await fetch(`${DOUBLE}/repos/harness/causa-test`, { headers: { authorization: 'Bearer harness-token' } }).then((r) => r.ok, () => false);
  expect(ok, `BLOCKED: no seeded harness double answers at ${DOUBLE}`).toBe(true);
  const context = await browser.newContext({ viewport });
  await context.route('https://api.github.com/**', async (route) => {
    const u = new URL(route.request().url());
    try {
      const r = await route.fetch({ url: DOUBLE + u.pathname + u.search });
      await route.fulfill({ status: r.status(), headers: r.headers(), body: await r.body() });
    } catch { await route.abort().catch(() => {}); }
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(APP + '#/');
  await expect(page.locator('#causa-main')).toBeVisible({ timeout: 30000 });
  // The start-up reads finish before typing: a later first view would otherwise reset the fields.
  await page.waitForLoadState('networkidle');
  await page.getByLabel('Repository owner', { exact: true }).fill('harness');
  await page.getByLabel('Repository', { exact: true }).fill('causa-test');
  await page.getByLabel('Branch', { exact: true }).fill('main');
  await page.getByLabel('Access token', { exact: true }).fill('harness-token');
  await page.getByRole('button', { name: 'Connect data repository', exact: true }).click();
  await expect(page.getByText(/^Connected to/).first()).toBeVisible({ timeout: 20000 });
  return { context, page, errors };
};

for (const viewport of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
  for (const c of CASES) {
    test(`${c.name} opens with every listed section populated at ${viewport.width} px`, async ({ browser }) => {
      const { context, page, errors } = await connected(browser, viewport);
      await page.getByRole('link', { name: c.title }).first().click();
      await expect(page.getByText(/Frozen demonstration \(read-only\)/).first()).toBeVisible({ timeout: 20000 });
      const nav = page.getByRole('navigation', { name: 'Investigation sections' });
      await expect(nav).toBeVisible({ timeout: 20000 });
      const listed = (await nav.getByRole('link').allInnerTexts()).map((t) => t.trim());
      expect(listed[0], 'the Overview is not the first section').toBe('Overview');
      expect(listed.every((n) => SECTIONS.includes(n)), `unknown sections: ${listed}`).toBe(true);
      expect(listed.length, 'only the Overview is listed').toBeGreaterThan(1);
      for (const name of listed) {
        await nav.getByRole('link', { name, exact: true }).click();
        await expect(nav.getByRole('link', { name, exact: true })).toHaveAttribute('aria-current', 'page');
        const section = page.locator('main section.causa-section').filter({ has: page.getByRole('heading', { level: 2, name, exact: true }) });
        await expect(section, `${name}: no section with its heading`).toBeVisible();
        const items = section.locator('li');
        expect(await items.count(), `${name}: no records`).toBeGreaterThan(0);
        await expect(items.first()).toBeVisible();
        expect(((await items.first().innerText()) || '').trim().length, `${name}: an empty record`).toBeGreaterThan(0);
        // Read-only: no edit controls in a catalog case.
        await expect(section.getByRole('textbox')).toHaveCount(0);
        await expect(page.getByRole('button', { name: /^(Save|Record|Edit)/ })).toHaveCount(0);
        // A long list folds behind "Show N more"; opening it shows the rest.
        const more = section.locator('summary.causa-more__toggle').first();
        if (await more.count()) {
          const before = await items.filter({ visible: true }).count();
          await more.click();
          await expect.poll(async () => items.filter({ visible: true }).count()).toBeGreaterThan(before);
        }
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        expect(overflow, `${name}: horizontal overflow at ${viewport.width} px`).toBeLessThanOrEqual(0);
      }
      expect(errors, 'page errors').toEqual([]);
      await context.close();
    });
  }
}
