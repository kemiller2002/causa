// Owner: ui (feature/causes-actions). Why did it happen, and what are we doing about it, on a phone: on a writable
// working copy at 390 px, record a hypothesis and evidence supporting it, record a cause (RecordCausalAssertion), a
// corrective action for that cause (RecordCorrectiveAction) and a conclusion that names the cause
// (RecordConclusion.CauseIds), Save, and read everything back in a fresh context. Corrective Actions lists the action,
// every editor's tap target is at least 44 px and nothing scrolls sideways.
//
// Runs against the built application (dist/) with GitHub answered by the governed harness double
// (Indy-init verification/harness/github-double.mjs), seeded with causa-data's active catalog (demonstration-v1).
// The double is synthetic and never real-provider evidence. Without it this spec fails closed: BLOCKED.
//   CAUSA_DOUBLE_URL   the double's base URL (default http://127.0.0.1:4611)
//   CAUSA_APP_PATH     the application path on the test server (default /dist/index.html)
import { test, expect } from '@playwright/test';
import { touchTargets } from '../support/layout-assertions.mjs';

const APP = process.env.CAUSA_APP_PATH || '/dist/index.html';
const DOUBLE = process.env.CAUSA_DOUBLE_URL || 'http://127.0.0.1:4611';
const PHONE = { width: 390, height: 844 };

test.describe.configure({ timeout: 300_000 });

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
  await expect(link, `the working copy does not list the ${name} section`).toBeVisible({ timeout: 20000 });
  await link.click();
  await expect(link).toHaveAttribute('aria-current', 'page');
};
const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
const phoneLayout = async (page, where) => {
  expect(await touchTargets(page), `touch targets under 44 px at 390 px (${where})`).toEqual([]);
  expect(await overflow(page), `horizontal overflow at 390 px (${where})`).toBeLessThanOrEqual(0);
};

test('at 390 px a working copy records a cause, a corrective action for it and a conclusion naming it, saves and reads them back', async ({ browser }) => {
  const { context, page, errors } = await connected(browser, PHONE);
  const tag = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
  const status = (p) => p.getByRole('status', { name: /save state/i }).first();
  const title = `ca-title-${tag}`, hyp = `ca-hyp-${tag}`, label = `ca-ev-${tag}`, content = `ca-content-${tag}`, source = `ca-src-${tag}`;
  const why = `ca-why-${tag}`, outcome = `ca-outcome-${tag}`, mechanism = `ca-mechanism-${tag}`, action = `ca-action-${tag}`;
  const conclusion = `ca-conclusion-${tag}`, residual = `ca-residual-${tag}`;

  await page.getByRole('link', { name: /new investigation/i }).first().click();
  await page.getByLabel('Problem statement', { exact: true }).fill(`ca-problem-${tag}`);
  await page.getByLabel('Title', { exact: true }).fill(title);
  await page.getByLabel('Primary investigative question', { exact: true }).fill(`ca-question-${tag}`);
  await page.getByRole('button', { name: 'Start investigation', exact: true }).click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(status(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 20000 });

  // 1. A hypothesis, and evidence supporting it.
  await openSection(page, 'Hypotheses');
  await page.getByLabel('Hypothesis statement', { exact: true }).fill(hyp);
  await page.getByRole('button', { name: 'Record a hypothesis', exact: true }).click();
  await expect(page.getByRole('article').filter({ hasText: hyp })).toBeVisible();
  await openSection(page, 'Evidence');
  await page.getByLabel('Evidence label', { exact: true }).fill(label);
  await page.getByLabel('Evidence original content', { exact: true }).fill(content);
  await page.getByLabel('Evidence source reference', { exact: true }).fill(source);
  await page.getByRole('group', { name: 'Evidence category' }).getByRole('radio', { name: 'Log', exact: true }).check();
  await page.getByRole('button', { name: 'Record evidence', exact: true }).click();
  const evidenceCard = page.getByRole('article').filter({ hasText: label });
  await expect(evidenceCard).toBeVisible();
  await page.getByRole('combobox', { name: 'Evidence', exact: true }).selectOption({ label });
  await page.getByRole('combobox', { name: 'Hypothesis', exact: true }).selectOption({ label: hyp });
  await page.getByRole('group', { name: 'Evidence relationship kind' }).getByLabel('supports', { exact: true }).check();
  await page.getByLabel('Evidence relationship rationale', { exact: true }).fill(why);
  await page.getByRole('button', { name: 'Link evidence to a hypothesis', exact: true }).click();
  await expect(evidenceCard).toContainText(`supports “${hyp}”. Why: ${why}`);

  // 2. A cause: the outcome it explains, the hypothesis in its role, the mechanism.
  await openSection(page, 'Causal Analysis');
  const recordCause = page.getByRole('button', { name: 'Record a cause', exact: true });
  await expect(recordCause).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: /Recorded causes are listed above/ })).toHaveCount(0);
  await page.getByLabel('Outcome it explains', { exact: true }).fill(outcome);
  await page.getByRole('combobox', { name: 'Hypothesis', exact: true }).selectOption({ label: hyp });
  const roles = page.getByRole('group', { name: 'Cause role' });
  await expect(roles.getByRole('radio')).toHaveCount(4);
  for (const r of await roles.getByRole('radio').all()) await expect(r).not.toBeChecked();
  await roles.getByRole('radio', { name: 'Root cause', exact: true }).check();
  await page.getByLabel('Mechanism', { exact: true }).fill(mechanism);
  await phoneLayout(page, 'cause editor');
  await recordCause.click();
  const causeCard = page.getByRole('article').filter({ hasText: outcome });
  await expect(causeCard, 'the cause is not listed').toBeVisible();
  await expect(causeCard).toContainText('Proposed');
  await expect(causeCard).toContainText(`Root cause: “${hyp}”`);
  await expect(causeCard).toContainText(mechanism);
  await expect(page.getByRole('status').filter({ hasText: /Recorded causes are listed above\..*not saved yet/ })).toBeVisible();
  await expect(page.getByLabel('Outcome it explains', { exact: true })).toHaveValue('');
  await expect(status(page)).toHaveText(/^\s*Unsaved\s*$/);

  // 3. A corrective action for the cause, listed under Corrective Actions.
  await openSection(page, 'Corrective Actions');
  const recordAction = page.getByRole('button', { name: 'Record a corrective action', exact: true });
  await expect(recordAction).toBeVisible();
  await page.getByRole('combobox', { name: 'Cause it addresses', exact: true }).selectOption({ label: outcome });
  await page.getByLabel('Corrective action', { exact: true }).fill(action);
  const kinds = page.getByRole('group', { name: 'Corrective action kind' });
  await expect(kinds.getByRole('radio')).toHaveCount(4);
  await kinds.getByRole('radio', { name: 'Correction', exact: true }).check();
  await phoneLayout(page, 'corrective action editor');
  await recordAction.click();
  const actionCard = page.getByRole('article').filter({ hasText: action });
  await expect(actionCard, 'the corrective action is not listed').toBeVisible();
  await expect(actionCard).toContainText('Correction');
  await expect(actionCard).toContainText('Proposed');
  await expect(actionCard).toContainText(`Addresses: “${outcome}”`);
  await expect(page.getByRole('status').filter({ hasText: /Recorded corrective actions are listed above\..*not saved yet/ })).toBeVisible();

  // 4. A conclusion that names the cause.
  await openSection(page, 'Overview');
  await page.getByLabel('Conclusion statement', { exact: true }).fill(conclusion);
  await page.getByRole('group', { name: 'Conclusion strength' }).getByRole('radio', { name: 'Supported cause', exact: true }).check();
  await page.getByLabel('Residual uncertainty', { exact: true }).fill(residual);
  const toggle = page.getByRole('group', { name: 'Causes this conclusion names' }).getByRole('button', { name: outcome, exact: true });
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await phoneLayout(page, 'conclusion editor');
  await page.getByRole('button', { name: 'Record a conclusion', exact: true }).click();
  const conclusionCard = page.locator('li.causa-item--conclusion').filter({ hasText: conclusion });
  await expect(conclusionCard).toBeVisible();
  await expect(conclusionCard).toContainText('Supported cause');
  await expect(conclusionCard).toContainText(`Causes it names: “${outcome}”`);

  // 5. Save.
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(status(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 30000 });

  // 6. A fresh context reads everything back from the repository.
  const fresh = await connected(browser, PHONE);
  await fresh.page.getByRole('link', { name: title }).click();
  const back = fresh.page.locator('li.causa-item--conclusion').filter({ hasText: conclusion });
  await expect(back).toBeVisible({ timeout: 30000 });
  await expect(back).toContainText('Supported cause');
  await expect(back).toContainText(residual);
  await expect(back).toContainText(`Causes it names: “${outcome}”`);
  expect(await overflow(fresh.page), 'horizontal overflow at 390 px (Overview, reopened)').toBeLessThanOrEqual(0);
  await openSection(fresh.page, 'Hypotheses');
  await expect(fresh.page.getByRole('article').filter({ hasText: hyp }).getByText(`“${label}” supports it.`, { exact: true })).toBeVisible();
  await openSection(fresh.page, 'Evidence');
  const evidenceBack = fresh.page.getByRole('article').filter({ hasText: label });
  await expect(evidenceBack).toContainText(content);
  await expect(evidenceBack).toContainText(`supports “${hyp}”. Why: ${why}`);
  await openSection(fresh.page, 'Causal Analysis');
  const causeBack = fresh.page.getByRole('article').filter({ hasText: outcome });
  await expect(causeBack).toContainText(`Root cause: “${hyp}”`);
  await expect(causeBack).toContainText(mechanism);
  expect(await overflow(fresh.page), 'horizontal overflow at 390 px (Causal Analysis, reopened)').toBeLessThanOrEqual(0);
  await openSection(fresh.page, 'Corrective Actions');
  const actionBack = fresh.page.getByRole('article').filter({ hasText: action });
  await expect(actionBack, 'Corrective Actions does not show the action after a reload').toBeVisible();
  await expect(actionBack).toContainText('Correction');
  await expect(actionBack).toContainText(`Addresses: “${outcome}”`);
  expect(await overflow(fresh.page), 'horizontal overflow at 390 px (Corrective Actions, reopened)').toBeLessThanOrEqual(0);
  expect([...errors, ...fresh.errors], 'page errors').toEqual([]);
  await fresh.context.close();
  await context.close();
});
