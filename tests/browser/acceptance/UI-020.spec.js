// Owner: ui (T16). UI-020: clear, accessible top-level navigation to Investigations, Administration and Settings
// (CAUSA-UI-EXPERIENCE-REQUIREMENTS UI-020; forma-layout-contract FL-09). One test per obligation; black-box, by
// role and accessible name.
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
const connect = async (page) => {
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

test.describe('UI-020', () => {
  test('UI-020-O1', async ({ page }) => {
    // Clear, accessible top-level navigation to the three destinations, from every destination.
    await open(page, '#/');
    const nav = primary(page);
    await expect(nav).toBeVisible();
    await expect(nav.getByRole('link')).toHaveText(['Investigations', 'Administration', 'Settings']);
    for (const d of DESTINATIONS) {
      await nav.getByRole('link', { name: d.name, exact: true }).click();
      await expect(page).toHaveURL(new RegExp(d.hash.replace(/[#/]/g, (c) => '\\' + c) + '$'));
      await expect(page.locator('main').getByRole('heading', { level: 1, name: d.heading })).toBeVisible();
      await expect(primary(page).getByRole('link', { name: d.name, exact: true })).toHaveAttribute('aria-current', 'page');
    }
  });

  test('UI-020-O2', async ({ page }) => {
    // Workflow actions stay in investigation context; operational information is in Administration; preferences in
    // Settings.
    await open(page, '#/administration');
    const admin = page.locator('main');
    await expect(admin.getByRole('heading', { name: 'System' })).toBeVisible();
    await expect(admin.getByText(/provider/i).first()).toBeVisible();
    for (const action of [/new investigation/i, /^save$/i, /record/i, /start investigation/i])
      await expect(admin.getByRole('button', { name: action }).or(admin.getByRole('link', { name: action }))).toHaveCount(0);
    await expect(admin.getByRole('combobox').or(admin.getByRole('radio')).or(admin.getByRole('checkbox'))).toHaveCount(0);

    await open(page, '#/settings');
    const settings = page.locator('main');
    await expect(settings.getByRole('heading', { name: 'Preferences' })).toBeVisible();
    await expect(settings.getByRole('combobox').or(settings.getByRole('radio')).or(settings.getByRole('checkbox')).first()).toBeVisible();
    for (const fact of [/deployed commit/i, /application version/i, /storage contract/i])
      await expect(settings.getByText(fact)).toHaveCount(0);
    for (const action of [/new investigation/i, /^save$/i, /start investigation/i])
      await expect(settings.getByRole('button', { name: action }).or(settings.getByRole('link', { name: action }))).toHaveCount(0);

    await open(page, '#/');
    const list = page.locator('main');
    for (const fact of [/deployed commit/i, /application version/i, /storage contract/i])
      await expect(list.getByText(fact)).toHaveCount(0);
  });


  test('UI-020-O3', async ({ page }) => {
    // Desktop, iPad, mobile, keyboard and assistive technology; an identifiable active page; sensible narrow
    // behaviour (no sideways scroll, the navigation stays reachable or collapses through Forma's pattern).
    for (const [id, size] of Object.entries(VP)) {
      await page.setViewportSize(size);
      for (const d of DESTINATIONS) {
        await open(page, d.hash);
        const current = page.getByRole('navigation', { name: 'Primary', exact: true }).getByRole('link', { name: d.name, exact: true }).filter({ visible: true });
        await expect(current.first(), `${id} ${d.name}: active page not marked`).toHaveAttribute('aria-current', 'page');
        for (const other of DESTINATIONS.filter((o) => o !== d))
          await expect(page.getByRole('navigation', { name: 'Primary', exact: true }).getByRole('link', { name: other.name, exact: true }).first()).not.toHaveAttribute('aria-current', 'page');
        none(`${id} ${d.name} noHorizontalScroll`, await kit.noHorizontalScroll(page));
      }
      none(`${id} navigationReachable`, await kit.navigationReachable(page));
      none(`${id} navigationCollapses`, await kit.navigationCollapses(page));
    }
    // Keyboard alone: Tab reaches Administration in the primary navigation and Enter opens it.
    await page.setViewportSize(VP['VP-DESKTOP']);
    await open(page, '#/');
    let reached = false;
    for (let i = 0; i < 25 && !reached; i++) {
      await page.keyboard.press('Tab');
      reached = await page.evaluate(() => !!document.activeElement?.closest('nav') && /^\s*Administration\s*$/.test(document.activeElement?.textContent || ''));
    }
    expect(reached, 'Administration is not reachable by Tab').toBe(true);
    await page.keyboard.press('Enter');
    await expect(page.locator('main').getByRole('heading', { level: 1, name: 'Administration' })).toBeVisible();
  });

  test('UI-020-O4', async ({ page }) => {
    // No undifferentiated dashboard: each destination is its own page with its own heading, title and content.
    const seen = [];
    for (const d of DESTINATIONS) {
      await open(page, d.hash);
      const h1 = (await page.locator('main h1').first().innerText()).trim();
      const title = await page.title();
      const text = (await page.locator('main').innerText()).trim();
      seen.push({ h1, title, text });
      await expect(page.locator('main h1')).toHaveCount(1);
      expect(title, `${d.name}: page title`).toMatch(new RegExp(`^${d.name} · Causa$`));
    }
    expect(new Set(seen.map((s) => s.h1)).size).toBe(3);
    expect(new Set(seen.map((s) => s.text)).size).toBe(3);
    // The landing page is the investigation list, not a mixed dashboard of system facts and preferences.
    await open(page, '#/');
    await expect(page.locator('main').getByRole('heading', { name: 'System' })).toHaveCount(0);
    await expect(page.locator('main').getByRole('heading', { name: 'Preferences' })).toHaveCount(0);
  });
});
