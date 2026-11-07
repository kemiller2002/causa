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
  await expect(page.locator('#causa-main')).toBeVisible({ timeout: 60000 });
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

// A writable working copy: the editors record an observation and a hypothesis as unsaved changes, Save commits them
// through the save path (Unsaved -> Saved), and a fresh tab reads both back.
test('a working copy records an observation and a hypothesis, saves them and reads them back', async ({ browser }) => {
  const { context, page, errors } = await connected(browser, { width: 1280, height: 900 });
  const tag = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const status = (p) => p.getByRole('status', { name: /save state/i }).first();
  await page.getByRole('link', { name: /new investigation/i }).first().click();
  await page.getByLabel('Problem statement', { exact: true }).fill(`wc-problem-${tag}`);
  await page.getByLabel('Title', { exact: true }).fill(`wc-title-${tag}`);
  await page.getByLabel('Primary investigative question', { exact: true }).fill(`wc-question-${tag}`);
  await page.getByRole('button', { name: 'Start investigation', exact: true }).click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(status(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 20000 });
  await expect(page.getByLabel('Observation text', { exact: true }), 'no observation editor on the working copy').toBeVisible({ timeout: 20000 });
  // Cancel drops what was typed and records nothing.
  await page.getByLabel('Observation text', { exact: true }).fill(`wc-cancelled-${tag}`);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByLabel('Observation text', { exact: true })).toHaveValue('');
  await expect(status(page)).toHaveText(/^\s*Saved\s*$/);
  // A blank observation is refused with a message, nothing recorded.
  await page.getByRole('button', { name: 'Record an observation', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: /observation text/i }).first()).toBeVisible();
  await page.getByLabel('Observation text', { exact: true }).fill(`wc-obs-${tag}`);
  await page.getByLabel('Observation source reference', { exact: true }).fill(`wc-src-${tag}`);
  await page.getByRole('button', { name: 'Record an observation', exact: true }).click();
  await expect(page.getByText(`wc-obs-${tag}`).first()).toBeVisible();
  await expect(status(page)).toHaveText(/^\s*Unsaved\s*$/);
  const sections = page.getByRole('navigation', { name: 'Investigation sections' });
  await sections.getByRole('link', { name: 'Hypotheses', exact: true }).click();
  await page.getByLabel('Hypothesis statement', { exact: true }).fill(`wc-hyp-${tag}`);
  await page.getByLabel('Hypothesis explanation', { exact: true }).fill(`wc-mech-${tag}`);
  await page.getByRole('button', { name: 'Record a hypothesis', exact: true }).click();
  await expect(page.getByRole('article').filter({ hasText: `wc-hyp-${tag}` })).toBeVisible();
  await expect(status(page)).toHaveText(/^\s*Unsaved\s*$/);
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(status(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 20000 });
  // A fresh tab reads both back from the repository.
  const fresh = await connected(browser, { width: 1280, height: 900 });
  await fresh.page.getByRole('link', { name: `wc-title-${tag}` }).click();
  await expect(fresh.page.getByText(`wc-obs-${tag}`).first()).toBeVisible({ timeout: 20000 });
  await expect(fresh.page.getByText(`wc-cancelled-${tag}`)).toHaveCount(0);
  await fresh.page.getByRole('navigation', { name: 'Investigation sections' }).getByRole('link', { name: 'Hypotheses', exact: true }).click();
  await expect(fresh.page.getByRole('article').filter({ hasText: `wc-hyp-${tag}` })).toContainText(`wc-mech-${tag}`);
  expect([...errors, ...fresh.errors], 'page errors').toEqual([]);
  await fresh.context.close();
  await context.close();
});

// The causal graph (Causal Analysis): an SVG picture with role img and a summary, one line and one word per current
// assessment, and the relationship index beside it with the same count.
test('Apollo 11 causal graph draws every assessment and keeps its relationship index', async ({ browser }) => {
  const { context, page, errors } = await connected(browser, { width: 1280, height: 900 });
  await page.getByRole('link', { name: CASES[0].title }).first().click();
  const nav = page.getByRole('navigation', { name: 'Investigation sections' });
  await nav.getByRole('link', { name: 'Causal Analysis', exact: true }).click();
  await expect(nav.getByRole('link', { name: 'Causal Analysis', exact: true })).toHaveAttribute('aria-current', 'page');
  const picture = page.getByRole('img', { name: /^Causal graph:/ });
  await expect(picture).toBeAttached({ timeout: 20000 });
  const summary = await picture.getAttribute('aria-label');
  const assessments = Number(summary.match(/by (\d+) assessments/)[1]);
  expect(assessments).toBeGreaterThan(0);
  const figure = page.locator('figure.causa-graph');
  await expect(figure.locator('path.causa-graph__edge')).toHaveCount(assessments);
  await expect(figure.locator('text.causa-graph__word')).toHaveCount(assessments);
  const hypotheses = Number(summary.match(/to (\d+) hypotheses/)[1]);
  await expect(figure.locator('rect.causa-graph__node--hypothesis')).toHaveCount(hypotheses);
  // The textual equivalent: every assessment listed as text, the same number.
  const index = page.locator('main section.causa-section').filter({ has: page.getByRole('heading', { name: 'Relationship index' }) }).locator('li');
  await expect(index).toHaveCount(assessments);
  // The words are on the picture, so colour is never the only cue.
  const words = await figure.locator('text.causa-graph__word').allTextContents();
  expect(words.every((w) => /^(Supports|Contradicts|Consistent with|Inconclusive|\w+)$/.test(w.trim()))).toBe(true);
  // The graph scrolls inside its own box; the page never scrolls sideways.
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await page.setViewportSize({ width: 1280, height: 900 });
  await figure.scrollIntoViewIfNeeded();
  await figure.screenshot({ path: process.env.CAUSA_GRAPH_SHOT || test.info().outputPath('apollo-11-graph.png') });
  expect(errors, 'page errors').toEqual([]);
  await context.close();
});
