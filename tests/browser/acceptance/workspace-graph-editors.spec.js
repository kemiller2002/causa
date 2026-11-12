// Owner: ui (T22). Building your own causal graph on a phone: on a writable working copy at 390 px, admit one evidence
// item, link it to a hypothesis as supporting, Save, and read it back in a fresh context: the evidence and the link on
// its card, and Causal Analysis's hypothesis panel with that supporting card.
//
// Runs against the built application (dist/) with GitHub answered by the governed harness double
// (Indy-init verification/harness/github-double.mjs), seeded with causa-data's active catalog (demonstration-v1).
// The double is synthetic and never real-provider evidence. Without it this spec fails closed: BLOCKED.
//   CAUSA_DOUBLE_URL   the double's base URL (default http://127.0.0.1:4611)
//   CAUSA_APP_PATH     the application path on the test server (default /dist/index.html)
//   CAUSA_EDITOR_SHOT  where to write the 390 px screenshot of the evidence editors (default: the test output dir)
import { test, expect } from '@playwright/test';
import { touchTargets } from '../support/layout-assertions.mjs';

const APP = process.env.CAUSA_APP_PATH || '/dist/index.html';
const DOUBLE = process.env.CAUSA_DOUBLE_URL || 'http://127.0.0.1:4611';
const PHONE = { width: 390, height: 844 };

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
  await page.waitForLoadState('networkidle');
  await page.getByLabel('Repository owner', { exact: true }).fill('harness');
  await page.getByLabel('Repository', { exact: true }).fill('causa-test');
  await page.getByLabel('Branch', { exact: true }).fill('main');
  await page.getByLabel('Access token', { exact: true }).fill('harness-token');
  await page.getByRole('button', { name: 'Connect data repository', exact: true }).click();
  await expect(page.getByText(/^Connected to/).first()).toBeVisible({ timeout: 20000 });
  return { context, page, errors };
};

const sections = (page) => page.getByRole('navigation', { name: 'Investigation sections' });
const openSection = async (page, name) => {
  const link = sections(page).getByRole('link', { name, exact: true });
  await expect(link, `BLOCKED on the integration lane: the working copy does not list the ${name} section`).toBeVisible({ timeout: 20000 });
  await link.click();
  await expect(link).toHaveAttribute('aria-current', 'page');
};
const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

test('at 390 px a working copy admits evidence, links it to a hypothesis as supporting, saves and reads both back', async ({ browser }) => {
  const { context, page, errors } = await connected(browser, PHONE);
  const tag = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const status = (p) => p.getByRole('status', { name: /save state/i }).first();
  const hyp = `ge-hyp-${tag}`, label = `ge-ev-${tag}`, content = `ge-content-${tag}`, source = `ge-src-${tag}`, why = `ge-why-${tag}`;

  await page.getByRole('link', { name: /new investigation/i }).first().click();
  await page.getByLabel('Problem statement', { exact: true }).fill(`ge-problem-${tag}`);
  await page.getByLabel('Title', { exact: true }).fill(`ge-title-${tag}`);
  await page.getByLabel('Primary investigative question', { exact: true }).fill(`ge-question-${tag}`);
  await page.getByRole('button', { name: 'Start investigation', exact: true }).click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(status(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 20000 });

  // A hypothesis to link to (the existing editor).
  await openSection(page, 'Hypotheses');
  await page.getByLabel('Hypothesis statement', { exact: true }).fill(hyp);
  await page.getByRole('button', { name: 'Record a hypothesis', exact: true }).click();
  await expect(page.getByRole('article').filter({ hasText: hyp })).toBeVisible();

  // Admit one evidence item.
  await openSection(page, 'Evidence');
  const record = page.getByRole('button', { name: 'Record evidence', exact: true });
  await expect(record, 'BLOCKED on the integration lane: UIA-RECORD-EVIDENCE is not offered in AnalysisProjection.Actions').toBeVisible({ timeout: 20000 });
  await page.getByLabel('Evidence label', { exact: true }).fill(label);
  await page.getByLabel('Evidence original content', { exact: true }).fill(content);
  await page.getByLabel('Evidence source reference', { exact: true }).fill(source);
  await page.getByRole('group', { name: 'Evidence category' }).getByRole('radio', { name: 'Document', exact: true }).check();
  await record.click();
  const card = page.getByRole('article').filter({ hasText: label });
  await expect(card, 'the admitted evidence is not listed').toBeVisible();
  await expect(card).toContainText(content);
  await expect(card).toContainText(source);
  await expect(page.getByRole('status').filter({ hasText: /Recorded evidence items are listed above\..*not saved yet/ })).toBeVisible();
  await expect(status(page)).toHaveText(/^\s*Unsaved\s*$/);
  // The form is empty again for the next item.
  await expect(page.getByLabel('Evidence label', { exact: true })).toHaveValue('');

  // Link it to the hypothesis as supporting.
  const linkButton = page.getByRole('button', { name: 'Link evidence to a hypothesis', exact: true });
  await expect(linkButton, 'BLOCKED on the integration lane: INV-030 is not offered in AnalysisProjection.Actions').toBeVisible();
  await page.getByRole('combobox', { name: 'Evidence', exact: true }).selectOption({ label });
  await page.getByRole('combobox', { name: 'Hypothesis', exact: true }).selectOption({ label: hyp });
  await page.getByRole('group', { name: 'Evidence relationship kind' }).getByLabel('supports', { exact: true }).check();
  await page.getByLabel('Evidence relationship rationale', { exact: true }).fill(why);
  // Every tap target on the editors is at least 44 x 44 px, and nothing is wider than the phone.
  expect(await touchTargets(page), 'touch targets under 44 px at 390 px').toEqual([]);
  expect(await overflow(page), 'horizontal overflow at 390 px').toBeLessThanOrEqual(0);
  await linkButton.click();
  await expect(card, 'the link does not read on the evidence card').toContainText(`supports “${hyp}”. Why: ${why}`);
  await expect(page.getByRole('status').filter({ hasText: /Recorded assessments are listed on each evidence card above\..*not saved yet/ })).toBeVisible();
  // The shot: the whole Evidence section at 390 px, the card with its link above the two editors.
  await page.screenshot({ path: process.env.CAUSA_EDITOR_SHOT || test.info().outputPath('graph-editors-390.png'), fullPage: true });

  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(status(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 20000 });

  // A fresh context reads the evidence and the link back from the repository.
  const fresh = await connected(browser, PHONE);
  await fresh.page.getByRole('link', { name: `ge-title-${tag}` }).click();
  await openSection(fresh.page, 'Evidence');
  const back = fresh.page.getByRole('article').filter({ hasText: label });
  await expect(back).toBeVisible({ timeout: 20000 });
  await expect(back).toContainText(content);
  await expect(back).toContainText(`supports “${hyp}”. Why: ${why}`);
  await expect(back).toContainText('1 supporting');
  // The hypothesis card names the evidence and its kind without opening anything (J2-05).
  await openSection(fresh.page, 'Hypotheses');
  const hcard = fresh.page.getByRole('article').filter({ hasText: hyp });
  await expect(hcard.getByText(`\u201c${label}\u201d supports it.`, { exact: true })).toBeVisible();
  // Causal Analysis: the hypothesis panel, open, with that supporting card.
  await openSection(fresh.page, 'Causal Analysis');
  const panel = fresh.page.locator('details.causa-focus__details').filter({ has: fresh.page.locator('summary', { hasText: hyp }) });
  await expect(panel).toHaveCount(1);
  await expect(panel).toHaveAttribute('open', '');
  await expect(panel.locator('.causa-focus__counts')).toContainText('1 supporting');
  const supporting = panel.locator('li.causa-focus__card').filter({ hasText: label });
  await expect(supporting).toHaveCount(1);
  await expect(supporting).toContainText(content);
  await expect(supporting.locator('.causa-focus__word')).toHaveText('Supports');
  expect(await overflow(fresh.page), 'horizontal overflow at 390 px').toBeLessThanOrEqual(0);
  expect([...errors, ...fresh.errors], 'page errors').toEqual([]);
  await fresh.context.close();
  await context.close();
});

// A working copy with hypotheses and no evidence yet (the owner's "Checkout page outage" shape): Causal Analysis is
// listed; at 1280 px the full graph is open on arrival with every hypothesis drawn and noted "No evidence linked yet";
// at 390 px every hypothesis has its panel, each saying so, and the page never scrolls sideways.
test('a working copy with hypotheses and no evidence shows its graph and says no evidence is linked yet', async ({ browser }) => {
  const { context, page, errors } = await connected(browser, { width: 1280, height: 900 });
  const tag = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const hyps = [`ne-hyp-a-${tag}`, `ne-hyp-b-${tag}`];
  await page.getByRole('link', { name: /new investigation/i }).first().click();
  await page.getByLabel('Problem statement', { exact: true }).fill(`ne-problem-${tag}`);
  await page.getByLabel('Title', { exact: true }).fill(`ne-title-${tag}`);
  await page.getByLabel('Primary investigative question', { exact: true }).fill(`ne-question-${tag}`);
  await page.getByRole('button', { name: 'Start investigation', exact: true }).click();
  await openSection(page, 'Hypotheses');
  for (const h of hyps) {
    await page.getByLabel('Hypothesis statement', { exact: true }).fill(h);
    await page.getByRole('button', { name: 'Record a hypothesis', exact: true }).click();
    await expect(page.getByRole('article').filter({ hasText: h })).toBeVisible();
  }
  await openSection(page, 'Causal Analysis');
  await expect(page.getByRole('heading', { level: 3, name: 'Causal graph', exact: true })).toBeVisible();
  const picture = page.getByRole('img', { name: /^Causal graph:/ });
  await expect(picture).toBeVisible();
  await expect(picture).toHaveAttribute('aria-label', /to 2 hypotheses by 0 assessments.* 2 hypotheses have no evidence linked yet\./);
  const figure = page.locator('figure.causa-graph');
  await expect(figure.locator('rect.causa-graph__node--hypothesis')).toHaveCount(2);
  await expect(figure.locator('text.causa-graph__note')).toHaveText(['No evidence linked yet', 'No evidence linked yet']);
  // Every hypothesis has its panel, each saying no evidence is linked yet, on its summary (seen while closed).
  const panels = page.locator('details.causa-focus__details');
  await expect(panels).toHaveCount(2);
  for (const h of hyps) {
    const panel = panels.filter({ has: page.locator('summary', { hasText: h }) });
    await expect(panel.locator('.causa-focus__counts')).toHaveText('No evidence linked yet');
  }
  await expect(panels.first().getByText(/^No evidence linked yet\. /)).toBeVisible();
  // At 390 px: the panels are the view, the full graph collapsed, nothing wider than the screen.
  await page.setViewportSize(PHONE);
  await expect(panels.first()).toBeVisible();
  await expect(figure).toBeHidden();
  await expect(page.locator('summary', { hasText: 'Show full graph' })).toBeVisible();
  expect(await overflow(page), 'horizontal overflow at 390 px').toBeLessThanOrEqual(0);
  expect(await touchTargets(page), 'touch targets under 44 px at 390 px').toEqual([]);
  expect(errors, 'page errors').toEqual([]);
  await context.close();
});

// A frozen demonstration offers none of the three editors (UI-004: forms render only when the action is offered).
test('a frozen demonstration at 390 px shows no evidence, link or conclusion editor', async ({ browser }) => {
  const { context, page, errors } = await connected(browser, PHONE);
  await page.getByRole('link', { name: /lightbulb/i }).first().click();
  await expect(page.getByText(/Frozen demonstration \(read-only\)/).first()).toBeVisible({ timeout: 20000 });
  await openSection(page, 'Overview');
  await expect(page.getByRole('button', { name: 'Record a conclusion', exact: true })).toHaveCount(0);
  await expect(page.getByRole('group', { name: 'Conclusion strength' })).toHaveCount(0);
  await openSection(page, 'Evidence');
  await expect(page.getByRole('button', { name: /^(Record evidence|Link evidence to a hypothesis|Reassess)$/ })).toHaveCount(0);
  await expect(page.getByRole('combobox')).toHaveCount(0);
  expect(errors, 'page errors').toEqual([]);
  await context.close();
});
