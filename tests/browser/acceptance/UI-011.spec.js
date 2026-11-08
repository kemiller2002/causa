// Owner: ui (T16). UI-011: accessibility and responsiveness of every screen, run through the Forma layout contract's
// executable assertions (tests/browser/support/layout-assertions.mjs; FL-01, FL-02, FL-04, FL-05).
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

const SCREENS = ['#/', '#/administration', '#/settings', '#/no-such-page'];
// Activates a primary destination as a user would at any width: through Forma's collapsed Menu when it is collapsed.
const navTo = async (page, name) => {
  if (!(await primary(page).getByRole('link', { name, exact: true }).isVisible().catch(() => false)))
    await page.locator('.ef-navigation-shell__compact > summary').filter({ visible: true }).first().click();
  await primary(page).getByRole('link', { name, exact: true }).click();
};

test.describe('UI-011', () => {
  test('UI-011-O1', async ({ page }) => {
    // Keyboard-only navigation, visible focus, skip-to-content, landmarks, semantic status announcements, labelled
    // controls, non-colour-only states and minimum touch targets, at VP-320 and VP-DESKTOP.
    for (const id of ['VP-320', 'VP-DESKTOP']) {
      await page.setViewportSize(VP[id]);
      for (const hash of SCREENS) {
        await open(page, hash);
        const at = `${id} ${hash}`;
        none(`${at} skipLinkFirst`, await kit.skipLinkFirst(page));
        await open(page, hash);
        none(`${at} focusVisible`, await kit.focusVisible(page));
        await open(page, hash);
        none(`${at} focusOrder`, await kit.focusOrder(page));
        await open(page, hash);
        none(`${at} touchTargets`, await kit.touchTargets(page));
        none(`${at} amberLabelled`, await kit.amberLabelled(page));
        // FL-04 dialogs: judged on every screen that has a dialog invoker; the shell screens have none (the kit
        // then reports only that no invoker exists, which is not a dialog defect).
        if (await page.locator('[commandfor], [popovertarget], [aria-controls], dialog').count())
          none(`${at} dialogFocus`, await kit.dialogFocus(page));
        // Landmarks: one banner, one main, the primary navigation.
        await expect(page.getByRole('banner')).toHaveCount(1);
        await expect(page.getByRole('main')).toHaveCount(1);
        await expect(page.getByRole('navigation', { name: 'Primary', exact: true }).first()).toBeAttached();
        // Live regions for announcements.
        expect(await page.locator('[aria-live="polite"], [role="status"]').count(), `${at}: no polite live region`).toBeGreaterThan(0);
        expect(await page.locator('[aria-live="assertive"], [role="alert"]').count(), `${at}: no assertive live region`).toBeGreaterThan(0);
        // Every form control has an accessible name.
        const unnamed = await page.evaluate(() => [...document.querySelectorAll('input:not([type=hidden]), select, textarea, button')]
          .filter((e) => e.offsetParent !== null)
          .filter((e) => !(e.labels?.length || e.getAttribute('aria-label') || e.getAttribute('aria-labelledby') || (e.tagName === 'BUTTON' && e.textContent.trim())))
          .map((e) => e.outerHTML.slice(0, 120)));
        expect(unnamed, `${at}: unlabelled controls`).toEqual([]);
      }
    }
    // The skip link moves to the main content.
    await page.setViewportSize(VP['VP-DESKTOP']);
    await open(page, '#/');
    await page.keyboard.press('Tab');
    await expect(page.getByRole('link', { name: /skip to (main )?content/i })).toBeFocused();
    await page.keyboard.press('Enter');
    expect(await page.evaluate(() => !!document.activeElement?.closest('main'))).toBe(true);
  });

  test('UI-011-O2', async ({ page }) => {
    // iPad portrait and landscape, narrow mobile, desktop, 200% text, reduced motion.
    for (const [id, size] of Object.entries(VP)) {
      await page.setViewportSize(size);
      for (const hash of SCREENS) {
        await open(page, hash);
        none(`${id} ${hash} noHorizontalScroll`, await kit.noHorizontalScroll(page));
        none(`${id} ${hash} scrollRegionsAccessible`, await kit.scrollRegionsAccessible(page));
        none(`${id} ${hash} textNotClipped`, await kit.textNotClipped(page));
      }
    }
    // 200% text (VP-200-TEXT): the root font size doubles, as with a browser's text-only zoom.
    await page.setViewportSize({ width: 1280, height: 800 });
    for (const hash of SCREENS) {
      await open(page, hash);
      await page.addStyleTag({ content: 'html { font-size: 200% !important; }' });
      none(`200% ${hash} noHorizontalScroll`, await kit.noHorizontalScroll(page));
      none(`200% ${hash} textNotClipped`, await kit.textNotClipped(page));
      await expect(page.locator('main h1')).toBeVisible();
    }
    // Reduced motion, including the loading shell.
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.setViewportSize(VP['VP-DESKTOP']);
    for (const hash of SCREENS) {
      await open(page, hash);
      none(`reduced motion ${hash}`, await kit.reducedMotion(page));
    }
    // Screen readers: every screen has one h1 that names it, and the title says the same.
    for (const hash of SCREENS) {
      await open(page, hash);
      await expect(page.locator('main h1')).toHaveCount(1);
      const h1 = (await page.locator('main h1').innerText()).trim();
      await expect(page).toHaveTitle(new RegExp('^' + h1.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    }
  });

  test('UI-011-O3', async ({ page }) => {
    // No essential operation needs drag-and-drop or hover: every destination opens by keyboard alone, nothing is
    // revealed only on hover, and nothing is draggable as the only way to act.
    await open(page, '#/');
    for (const d of [DESTINATIONS[1], DESTINATIONS[2], DESTINATIONS[0]]) {
      const link = primary(page).getByRole('link', { name: d.name, exact: true });
      await link.focus();
      await page.keyboard.press('Enter');
      await expect(page.locator('main h1')).toHaveText(d.heading);
    }
    for (const hash of SCREENS) {
      await open(page, hash);
      expect(await page.locator('[draggable="true"]').count(), `${hash}: a draggable element`).toBe(0);
      // Every control that is shown on hover is also shown without it.
      await page.mouse.move(0, 0);
      const shown = await page.evaluate(() => [...document.querySelectorAll('a[href], button, input, select, textarea')]
        .filter((e) => e.offsetParent !== null).length);
      const hoverOnly = await page.evaluate(() => {
        const rules = [];
        for (const sheet of document.styleSheets) {
          let list; try { list = sheet.cssRules; } catch { continue; }
          for (const r of list) if (r.selectorText && /:hover/.test(r.selectorText) && /(display|visibility|opacity)\s*:/.test(r.cssText) && !/@media/.test(r.cssText)) rules.push(r.selectorText);
        }
        return rules.filter((s) => !/ef-/.test(s));
      });
      expect(hoverOnly, `${hash}: content revealed only on hover`).toEqual([]);
      expect(shown, `${hash}: no operable control without hover`).toBeGreaterThan(0);
    }
  });

  test('UI-011-O4', async ({ browser }) => {
    // Focus and scroll context survive transitions and the WebAssembly handoff.
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    let release;
    const held = new Promise((r) => { release = r; });
    await page.route(/\/_framework\//, async (route) => { await held; await route.continue(); });
    await page.goto(APP, { waitUntil: 'domcontentloaded' });
    // During loading the user tabs to the skip link; the handoff must not throw focus to nowhere.
    await page.keyboard.press('Tab');
    const before = await page.evaluate(() => document.activeElement?.textContent?.trim());
    expect(before).toMatch(/skip to/i);
    release();
    await expect(page.locator('#causa-main')).toBeVisible({ timeout: 30000 });
    const after = await page.evaluate(() => ({ body: document.activeElement === document.body, text: document.activeElement?.textContent?.trim() }));
    expect(after.body, 'focus was lost during the WebAssembly handoff').toBe(false);
    // Transitions: the activated navigation link keeps focus (or focus moves into the new screen), never the body.
    await primary(page).getByRole('link', { name: 'Settings', exact: true }).focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('main h1')).toHaveText('Settings');
    expect(await page.evaluate(() => document.activeElement !== document.body && !!document.activeElement?.closest('nav, main, header'))).toBe(true);
    // Scroll context: returning to a screen with Back restores where the user was, not an unrelated position.
    await page.setViewportSize({ width: 390, height: 500 });
    await open(page, '#/administration');
    // Forma sets scroll-behavior: smooth, so an ordinary scrollTo is still under way when scrollY is read (WebKit reads
    // 0); the user's scroll is made instantly, and the check below is unchanged.
    await page.evaluate(() => window.scrollTo({ top: Math.max(0, document.scrollingElement.scrollHeight - innerHeight), behavior: 'instant' }));
    const y = await page.evaluate(() => scrollY);
    expect(y, 'the Administration screen does not scroll at 390x500, so nothing is being checked').toBeGreaterThan(0);
    // A user reads where they scrolled to before choosing the next destination.
    await page.waitForTimeout(400);
    await navTo(page, 'Settings');
    await expect(page.locator('main h1')).toHaveText('Settings');
    await page.goBack();
    await expect(page.locator('main h1')).toHaveText('Administration');
    // The restored position is reached by Forma's smooth scrolling, so it is read once that scroll has settled.
    await expect.poll(async () => Math.abs((await page.evaluate(() => scrollY)) - y), { message: 'scroll position not preserved on Back', timeout: 5000 })
      .toBeLessThanOrEqual(Math.max(48, y * 0.1));
    await ctx.close();
  });

  test('UI-011-O5', async ({ page }) => {
    // Graphs have an equivalent accessible textual interaction: in the investigation's Causal Analysis, every
    // relationship the graph draws is also listed as text with keyboard-operable links.
    await connect(page);
    await openInvestigation(page, 'Frozen demonstration');
    await sections(page).getByRole('link', { name: 'Causal Analysis', exact: true }).click();
    const main = page.locator('main');
    const graphs = main.locator('svg, canvas, [class*="ef-diagram"]').filter({ visible: true });
    const textual = main.getByRole('list').or(main.getByRole('table')).filter({ hasText: /supports|contradicts|consistent|inconclusive/i }).first();
    await expect(textual, 'no textual list of relationships in Causal Analysis').toBeVisible({ timeout: 20000 });
    for (let i = 0; i < await graphs.count(); i++) {
      const g = graphs.nth(i);
      const named = await g.evaluate((e) => !!(e.getAttribute('aria-label') || e.getAttribute('aria-labelledby') || e.querySelector('title') || e.getAttribute('aria-hidden') === 'true'));
      expect(named, 'a graph with no accessible name or textual equivalent').toBe(true);
    }
    const links = textual.getByRole('link').or(textual.getByRole('button'));
    if (await links.count()) {
      await links.first().focus();
      await expect(links.first()).toBeFocused();
    }
  });
});
