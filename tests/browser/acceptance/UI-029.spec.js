// Owner: ui (T16). UI-029: two navigation levels. Persistent primary destinations (Investigations, Administration,
// Settings) and, inside an investigation, its own sections (Overview, Causal Analysis, Evidence, Hypotheses,
// Timeline, Corrective Actions where supported). FL-09: the Forma navigation shell; under 48rem it collapses through
// Forma's pattern.
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

const SECTIONS = ['Overview', 'Causal Analysis', 'Evidence', 'Hypotheses', 'Timeline'];

test.describe('UI-029', () => {
  test('UI-029-O1', async ({ page }) => {
    // Persistent, distinct primary destinations on every screen, the not-found screen included.
    for (const hash of ['#/', '#/administration', '#/settings', '#/no-such-page']) {
      await open(page, hash);
      const nav = primary(page);
      await expect(nav, `${hash}: no primary navigation`).toBeVisible();
      await expect(nav.getByRole('link')).toHaveText(['Investigations', 'Administration', 'Settings']);
      const hrefs = await nav.getByRole('link').evaluateAll((as) => as.map((a) => a.getAttribute('href')));
      expect(new Set(hrefs).size, `${hash}: the destinations are not distinct`).toBe(3);
    }
    // The navigation element itself survives a change of screen (persistent, not re-rendered per screen).
    await open(page, '#/');
    await page.evaluate(() => { window.__causaNav = document.querySelector('nav[aria-label="Primary"]'); });
    await primary(page).getByRole('link', { name: 'Settings', exact: true }).click();
    await expect(page.locator('main h1')).toHaveText('Settings');
    expect(await page.evaluate(() => window.__causaNav?.isConnected === true), 'the primary navigation was replaced on navigation').toBe(true);
    for (const size of [VP['VP-320'], VP['VP-DESKTOP']]) {
      await page.setViewportSize(size);
      none('navigationReachable', await kit.navigationReachable(page));
      none('navigationCollapses', await kit.navigationCollapses(page));
    }
  });

  test('UI-029-O2', async ({ page }) => {
    // Within an investigation: Overview, Causal Analysis, Evidence, Hypotheses, Timeline (and Corrective Actions where
    // supported), each opening and marking itself current.
    await connect(page);
    await openInvestigation(page);
    const nav = sections(page);
    await expect(nav, 'no investigation section navigation').toBeVisible();
    const names = (await nav.getByRole('link').allInnerTexts()).map((t) => t.trim());
    expect(names.slice(0, 5)).toEqual(SECTIONS);
    expect(names.filter((n) => !SECTIONS.includes(n))).toEqual(names.length > 5 ? ['Corrective Actions'] : []);
    for (const name of SECTIONS) {
      await nav.getByRole('link', { name, exact: true }).click();
      await expect(nav.getByRole('link', { name, exact: true })).toHaveAttribute('aria-current', 'page');
      await expect(page.locator('main').getByRole('heading', { name: new RegExp(name, 'i') }).first()).toBeVisible();
    }
  });

  test('UI-029-O3', async ({ page }) => {
    // The current destination is indicated (aria-current and a non-colour cue), and focus stays on the control the
    // user activated or moves into the new screen, never to the document body, at desktop, iPad and mobile.
    for (const [id, size] of Object.entries(VP)) {
      await page.setViewportSize(size);
      await open(page, '#/');
      for (const d of [DESTINATIONS[1], DESTINATIONS[2], DESTINATIONS[0]]) {
        const link = page.getByRole('navigation', { name: 'Primary', exact: true }).getByRole('link', { name: d.name, exact: true }).filter({ visible: true }).first();
        if (!(await link.isVisible())) {
          // Collapsed through Forma's navigation pattern: open it by keyboard.
          const toggle = page.locator('summary, button[aria-controls], button[aria-expanded]').filter({ visible: true }).first();
          await toggle.focus();
          await page.keyboard.press('Enter');
        }
        await link.focus();
        await page.keyboard.press('Enter');
        await expect(page.locator('main h1')).toHaveText(d.heading);
        await expect(link, `${id}: ${d.name} not current`).toHaveAttribute('aria-current', 'page');
        const focus = await page.evaluate(() => {
          const a = document.activeElement;
          return { body: a === document.body || a === null, inPage: !!a?.closest('nav, main, header') };
        });
        expect(focus.body || !focus.inPage, `${id}: focus was lost after opening ${d.name}`).toBe(false);
        // Current is not told by colour alone: the current link differs from the others in more than colour.
        const cue = await page.evaluate((name) => {
          const links = [...document.querySelectorAll('nav[aria-label="Primary"] a')].filter((a) => a.offsetParent);
          const cur = links.find((a) => a.textContent.trim() === name);
          const other = links.find((a) => a.textContent.trim() !== name);
          if (!cur || !other) return false;
          const s = getComputedStyle(cur), o = getComputedStyle(other);
          return ['textDecorationLine', 'fontWeight', 'borderBottomStyle', 'borderBottomWidth', 'borderInlineStartWidth', 'boxShadow', 'outlineStyle'].some((p) => s[p] !== o[p]);
        }, d.name);
        expect(cue, `${id}: the current destination differs from the others only by colour`).toBe(true);
      }
    }
  });

  test('UI-029-O4', async ({ page }) => {
    // Global and investigation destinations are separate navigations; settings are not folded into an investigation.
    await connect(page);
    await openInvestigation(page);
    const prim = primary(page);
    const sect = sections(page);
    await expect(prim.getByRole('link')).toHaveCount(3);
    await expect(sect).toBeVisible();
    const nested = await page.evaluate(() => {
      const navs = [...document.querySelectorAll('nav')];
      const p = navs.find((n) => n.getAttribute('aria-label') === 'Primary');
      const s = navs.find((n) => /investigation sections/i.test(n.getAttribute('aria-label') || ''));
      return !p || !s || p.contains(s) || s.contains(p);
    });
    expect(nested, 'primary and section navigation are one list').toBe(false);
    for (const global of ['Investigations', 'Administration', 'Settings'])
      await expect(sect.getByRole('link', { name: global, exact: true })).toHaveCount(0);
    for (const name of SECTIONS) await expect(prim.getByRole('link', { name, exact: true })).toHaveCount(0);
    await expect(page.locator('main details').filter({ hasText: /settings|preferences/i })).toHaveCount(0);
  });
});
