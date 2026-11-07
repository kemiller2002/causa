// Owner: ui (T16). UI-014: consistent Causa identity (name, icon, typography, navigation hierarchy, page titles,
// favicon, accessible status colours); the icon and startup behaviour of WASM-LOADING-EXPERIENCE-REQUIREMENTS; and
// navigation that tells frozen demonstrations from writable copies and shows the current investigation.
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

// Engine assets (the .NET WebAssembly runtime and the application assemblies).
const ENGINE = /\/_framework\/.*(\.m?js|\.wasm|\.dll|\.dat|\.blat|\.json)(\?.*)?$/;

test.describe('UI-014', () => {
  test('UI-014-O1', async ({ page, request }) => {
    const fonts = [];
    for (const d of [...DESTINATIONS, { name: 'Page not found', hash: '#/no-such-page' }]) {
      await open(page, d.hash);
      // Page title: "<screen> · Causa", matching the screen's one h1.
      const h1 = (await page.locator('main h1').first().innerText()).trim();
      await expect(page).toHaveTitle(`${h1} · Causa`);
      // Name and icon in the masthead.
      const banner = page.getByRole('banner');
      await expect(banner.getByText('Causa', { exact: true }).first()).toBeVisible();
      // Navigation hierarchy: the same three destinations in the same order.
      await expect(primary(page).getByRole('link')).toHaveText(['Investigations', 'Administration', 'Settings']);
      fonts.push(await page.evaluate(() => ({ h1: getComputedStyle(document.querySelector('main h1')).fontFamily, body: getComputedStyle(document.body).fontFamily })));
      // Accessible status colours.
      none(`${d.hash} textContrast`, await kit.textContrast(page));
      none(`${d.hash} amberLabelled`, await kit.amberLabelled(page));
    }
    expect(new Set(fonts.map((f) => f.h1)).size, 'heading typography differs between screens').toBe(1);
    expect(new Set(fonts.map((f) => f.body)).size, 'body typography differs between screens').toBe(1);
    // Favicon: declared, local, and served.
    const icon = await page.locator('link[rel~="icon"]').first().getAttribute('href');
    expect(icon, 'no favicon is declared').toBeTruthy();
    const url = new URL(icon, page.url());
    expect(url.origin, 'the favicon is not a local asset').toBe(new URL(page.url()).origin);
    const res = await request.get(url.href);
    expect(res.status(), 'the favicon is not served').toBe(200);
  });

  test('UI-014-O2', async ({ browser }) => {
    // WASM-LOAD-001..008: a branded, accessible loading screen before the engine, no fabricated progress, a ready
    // handoff without a blank frame, and a failure with a working Retry.
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    let release;
    const held = new Promise((r) => { release = r; });
    await page.route(ENGINE, async (route) => { await held; await route.continue(); });
    await page.goto(APP, { waitUntil: 'commit' });
    const status = page.getByRole('status').filter({ hasText: /loading|starting/i }).first();
    await expect(status, 'no loading status before the engine starts').toBeVisible({ timeout: 5000 });
    await expect(page.locator('#causa-boot').getByRole('img', { name: /causa/i }).or(page.locator('#causa-boot').getByText('Causa', { exact: true })).first()).toBeVisible();
    await expect(page.locator('#causa-boot img, #causa-boot svg[role="img"]').first(), 'no Causa mark on the loading screen').toBeVisible();
    const bootText = await page.locator('#causa-boot').innerText();
    expect(bootText, 'loading screen shows a fabricated percentage').not.toMatch(/\d+\s*%/);
    await expect(page.locator('#causa-main')).toBeHidden();
    // Handoff: the loading screen stays until the engine has rendered, then the screen appears with one heading.
    let blank = false;
    await page.exposeFunction('__causaBlank', () => { blank = true; });
    await page.evaluate(() => {
      const check = () => {
        const boot = document.getElementById('causa-boot');
        const main = document.getElementById('causa-main');
        const fault = document.getElementById('causa-boot-fault');
        if (boot?.hidden && main?.hidden && fault?.hidden) window.__causaBlank();
      };
      new MutationObserver(check).observe(document.body, { attributes: true, subtree: true, attributeFilter: ['hidden'] });
    });
    release();
    await expect(page.locator('#causa-main')).toBeVisible({ timeout: 30000 });
    await expect(page.locator('#causa-boot')).toBeHidden();
    await expect(page.locator('main h1')).toHaveCount(1);
    expect(blank, 'a blank frame appeared during the handoff').toBe(false);
    await ctx.close();

    // Failure and recovery: the runtime cannot be fetched; a readable fault with a keyboard-operable Retry that works.
    const ctx2 = await browser.newContext();
    const p2 = await ctx2.newPage();
    let fail = true;
    await p2.route(/\/_framework\/dotnet\.js(\?.*)?$/, (route) => (fail ? route.abort('failed') : route.continue()));
    await p2.goto(APP);
    const fault = p2.getByRole('alert').filter({ hasText: /could not start/i });
    await expect(fault).toBeVisible({ timeout: 20000 });
    await expect(p2.getByRole('status').filter({ hasText: /loading/i })).toBeHidden();
    const faultText = await fault.innerText();
    expect(faultText).not.toMatch(/exception|stack|TypeError|at \w+ \(/i);
    const retry = p2.getByRole('button', { name: 'Retry', exact: true });
    await expect(retry).toBeFocused();
    fail = false;
    await p2.keyboard.press('Enter');
    await expect(p2.locator('#causa-main')).toBeVisible({ timeout: 30000 });
    await expect(fault).toBeHidden();
    await ctx2.close();
  });

  test('UI-014-O3', async ({ page }) => {
    // The list tells frozen demonstrations from writable copies in words; an open investigation's context (title and
    // copy kind) is shown in the shell beside the navigation.
    await connect(page);
    const cards = page.locator('[data-requirement-id="INV-039"] article');
    await expect(cards.first()).toBeVisible({ timeout: 20000 });
    const n = await cards.count();
    for (let i = 0; i < n; i++)
      await expect(cards.nth(i), `card ${i} does not say whether it is a demonstration or a working copy`).toContainText(/Frozen demonstration \(read-only\)|Your working copy \(editable\)/);
    await expect(cards.filter({ hasText: 'Frozen demonstration (read-only)' }).first(), 'no frozen demonstration is listed').toBeVisible();
    const title = await openInvestigation(page, 'Frozen demonstration');
    const context = page.getByRole('banner').getByLabel('Current investigation');
    await expect(context).toContainText(title);
    await expect(context).toContainText(/Frozen demonstration \(read-only\)/);
    // The context link returns to the investigation's overview.
    await expect(context.getByRole('link', { name: title })).toHaveAttribute('href', /#\/investigations\//);
  });
});
