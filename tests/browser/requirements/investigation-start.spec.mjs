// Owner: ui-2 (WI-INV-START; catalog verification inputs assigned by the coordinator). Requirement probes for screen
// investigation-start (#/investigations/new): INV-001..006 and INV-019..023 of CAT-CAUSA-INVESTIGATION.
//
// Each test drives the built application in a real browser, black-box (role, accessible name and data-requirement-id
// only), and records what it observed as Dokimos requirement observations (`dokimos.requirement-observations/1`): for
// each obligation, the element facts it read, the values it entered with what the page then showed, or the expected and
// observed outcome. Nothing is recorded that was not observed; an observation is only evidence when its test passed
// (Dokimos binds each probe to this test's result in the Playwright JSON report).
//
// The governed gate (verification-gate.yml, "Governed catalog acceptance") sets CAUSA_OBSERVATIONS (the directory the
// observations are written to; the compiled manifest sits beside it as ../manifest.json), CAUSA_RUN_ID and
// CAUSA_CANDIDATE. Without CAUSA_OBSERVATIONS the tests still run and assert; they only skip writing the file.
import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// ---- shared prelude (the ui specs' pattern: the built application and the deterministic test repository) ----------
const APP = process.env.CAUSA_APP_PATH || '/dist/index.html';
const REPO = {
  owner: process.env.CAUSA_TEST_OWNER || 'harness',
  repo: process.env.CAUSA_TEST_REPO || 'causa-test',
  branch: process.env.CAUSA_TEST_BRANCH || 'main',
  token: process.env.CAUSA_WRITE_TOKEN || 'harness-token',
};
const SPEC = 'requirements/investigation-start.spec.mjs';
const marker = (label) => `${label}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

const open = async (page, hash = '#/') => {
  await page.goto('about:blank');
  await page.goto(APP + hash);
  await expect(page.locator('#causa-main'), 'the engine did not render its first view').toBeVisible({ timeout: 30000 });
};
const connect = async (page) => {
  await open(page, '#/');
  await page.getByLabel('Repository owner', { exact: true }).fill(REPO.owner);
  await page.getByLabel('Repository', { exact: true }).fill(REPO.repo);
  await page.getByLabel('Branch', { exact: true }).fill(REPO.branch);
  await page.getByLabel('Access token', { exact: true }).fill(REPO.token);
  await page.getByRole('button', { name: 'Connect data repository', exact: true }).click();
  await expect(page.getByText(/^Connected to/).first(), 'Connect did not succeed against the test repository').toBeVisible({ timeout: 20000 });
};
/// The start screen, reached as a user reaches it: Connect, then New investigation.
const startScreen = async (page) => {
  await connect(page);
  await page.getByRole('link', { name: /new investigation/i }).or(page.getByRole('button', { name: /new investigation/i })).first().click();
  await expect(page.getByRole('heading', { name: 'New investigation', level: 1 })).toBeVisible({ timeout: 20000 });
};

// ---- the screen's elements, by catalog name ------------------------------------------------------------------------
const FIELDS = {
  'INV-001': 'Problem statement',
  'INV-002': 'Title',
  'INV-023': 'Primary investigative question',
  'INV-006': 'Investigation identifier',
};
const field = (page, id) => page.getByLabel(FIELDS[id], { exact: true });
const startButton = (page) => page.getByRole('button', { name: 'Start investigation', exact: true });
const cancelButton = (page) => page.getByRole('button', { name: 'Cancel investigation start', exact: true });
const destination = (page) => page.locator('[data-requirement-id="INV-005"]');

// ---- observations -----------------------------------------------------------------------------------------------------
const probes = [];
const identity = () => `playwright:${SPEC} > ${test.info().title} [${test.info().project.name}]`;
const record = (obligation, observation) => probes.push({ obligation, test: identity(), ...observation });

/// What the page shows for one element: its tag, input type, role, accessible name (the name Playwright resolved it by,
/// confirmed against the element's labels), visibility and whether it can be edited.
const facts = async (locator, accessibleName, role) => {
  await expect(locator).toHaveCount(1);
  const dom = await locator.evaluate((e) => ({
    tag: e.tagName.toLowerCase(),
    inputType: e.getAttribute('type') || '',
    explicitRole: e.getAttribute('role') || '',
    readOnlyAttr: e.hasAttribute('readonly'),
  }));
  const disabled = await locator.isDisabled().catch(() => false);
  return {
    tag: dom.tag,
    inputType: dom.inputType,
    role: dom.explicitRole || role,
    accessibleName,
    visible: await locator.isVisible(),
    disabled,
    readOnly: dom.readOnlyAttr,
  };
};

/// After Start was pressed: was the field's value accepted (no error on it), and what did the page say about it?
const verdictFor = async (locator) => {
  const state = await locator.evaluate((e) => ({
    invalid: e.getAttribute('aria-invalid') === 'true',
    message: (e.getAttribute('aria-describedby') || '')
      .split(/\s+/)
      .map((id) => document.getElementById(id))
      .filter((d) => d && d.classList.contains('causa-field-error'))
      .map((d) => d.textContent.trim())
      .join(' '),
  }));
  return { accepted: !state.invalid, observed: state.message };
};

test.afterAll(async ({}, info) => {
  const dir = process.env.CAUSA_OBSERVATIONS;
  if (!dir || probes.length === 0) return;
  fs.mkdirSync(dir, { recursive: true });
  const manifest = path.join(path.dirname(dir), 'manifest.json');
  const manifestHash = fs.existsSync(manifest) ? JSON.parse(fs.readFileSync(manifest, 'utf8')).contentHash || '' : '';
  const out = {
    schema: 'dokimos.requirement-observations/1',
    runId: process.env.CAUSA_RUN_ID || '',
    sourceCommit: process.env.CAUSA_CANDIDATE || '',
    manifestHash,
    // The governed fact (catalog-verification-contract.json): the competition offers no domain profile selection.
    facts: { 'domain-profile-selected': false },
    probes,
  };
  fs.writeFileSync(path.join(dir, `investigation-start-${info.project.name}.json`), JSON.stringify(out, null, 2));
});

// ---- the probes ---------------------------------------------------------------------------------------------------------

test('INV-START fields and actions are present, labelled and editable', async ({ page }) => {
  await startScreen(page);
  for (const [id, name] of Object.entries(FIELDS)) {
    const locator = field(page, id);
    const element = await facts(locator, name, 'textbox');
    expect(element.visible, `${name} is not visible`).toBe(true);
    expect(await locator.isEditable(), `${name} cannot be edited`).toBe(true);
    for (const check of ['V1/exists', 'V2/visible', 'V2/accessible', 'V3/editable']) record(`${id}/${check}`, { elements: [element] });
    // Enter: type a value and read back what the field holds.
    const value = id === 'INV-006' ? '0f9e8d7c-6b5a-4f3e-8d2c-1b0a99887766' : marker(id);
    await locator.fill(value);
    const observed = await locator.inputValue();
    expect(observed).toBe(value);
    record(`${id}/V3/enter`, { elements: [element], attempts: [{ value, observed, accepted: observed === value }] });
  }
  // INV-006 is pre-filled with a new identifier.
  await open(page, '#/');
  await startScreen(page);
  const prefilled = await field(page, 'INV-006').inputValue();
  expect(prefilled).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);

  // INV-005: where the investigation will be written, shown before anything is written.
  const where = destination(page);
  const whereText = (await where.innerText()).trim();
  expect(whereText).toContain(REPO.repo);
  const whereFacts = await facts(where, whereText, 'paragraph');
  for (const check of ['V1/exists', 'V2/visible', 'V2/accessible']) record(`INV-005/${check}`, { elements: [whereFacts] });

  // INV-004 and INV-022: the two actions.
  for (const [id, locator, name] of [['INV-004', startButton(page), 'Start investigation'], ['INV-022', cancelButton(page), 'Cancel investigation start']]) {
    const element = await facts(locator, name, 'button');
    expect(element.visible, `${name} is not visible`).toBe(true);
    expect(element.disabled, `${name} is disabled`).toBe(false);
    for (const check of ['V1/exists', 'V2/visible', 'V2/accessible']) record(`${id}/${check}`, { elements: [element] });
  }
});

test('INV-START validation refuses what the catalog refuses and accepts what it allows', async ({ page }) => {
  await startScreen(page);
  // Each case keeps another field invalid where the value under test is valid, so pressing Start never creates an
  // investigation here: the form is judged, field by field, and stays on the start screen.
  const cases = [
    // [field id, obligation ids, invalid values, a valid value, the field kept invalid meanwhile]
    ['INV-001', ['INV-019'], ['', '   \n  '], marker('problem'), ['INV-002', 'x'.repeat(201)]],
    ['INV-001', ['INV-021'], ['p'.repeat(10001)], 'p'.repeat(10000), ['INV-002', 'x'.repeat(201)]],
    ['INV-002', ['INV-020'], ['t'.repeat(201)], 't'.repeat(200), ['INV-001', '']],
    ['INV-023', ['INV-023'], ['q'.repeat(10001)], 'q'.repeat(10000), ['INV-001', '']],
    ['INV-006', ['INV-006'], ['not-a-guid', '1234'], '0f9e8d7c-6b5a-4f3e-8d2c-1b0a99887766', ['INV-001', '']],
  ];
  for (const [id, owners, invalid, valid, [otherId, otherValue]] of cases) {
    const locator = field(page, id);
    await field(page, otherId).fill(otherValue);
    const attempts = [];
    for (const value of [...invalid, valid]) {
      await locator.fill(value);
      await startButton(page).click();
      await expect(page.getByRole('heading', { name: 'New investigation', level: 1 }), 'the form left the start screen').toBeVisible();
      const verdict = await verdictFor(locator);
      attempts.push({ value, observed: verdict.observed, accepted: verdict.accepted });
    }
    for (const a of attempts.slice(0, -1)) expect(a.accepted, `${FIELDS[id]} accepted ${JSON.stringify(a.value.slice(0, 20))}`).toBe(false);
    for (const a of attempts.slice(0, -1)) expect(a.observed, `${FIELDS[id]} was refused without a message`).not.toBe('');
    expect(attempts.at(-1).accepted, `${FIELDS[id]} refused a valid value`).toBe(true);
    for (const owner of owners) {
      record(`${owner}/V4/validation`, { attempts });
      record(`${owner}/V4/rejects`, { attempts: attempts.slice(0, -1) });
    }
    // Leave the field valid for the next case.
    await locator.fill(id === 'INV-006' ? valid : '');
  }
});

test('INV-START cancel leaves the start screen and creates nothing', async ({ page }) => {
  await startScreen(page);
  const title = marker('cancelled-title');
  await field(page, 'INV-001').fill(marker('cancelled-problem'));
  await field(page, 'INV-002').fill(title);
  await cancelButton(page).click();
  const back = page.getByRole('heading', { name: 'Investigations', level: 1 });
  await expect(back).toBeVisible({ timeout: 20000 });
  const observedRoute = await page.evaluate(() => location.hash || '#/');
  record('INV-022/V3/activate', { expected: 'the investigation list (#/)', observed: observedRoute === '#/' || observedRoute === '#' ? 'the investigation list (#/)' : observedRoute });
  // Nothing was created: the cancelled title is not listed, now or after reconnecting.
  await connect(page);
  const listed = await page.getByRole('link', { name: title, exact: true }).count();
  expect(listed).toBe(0);
  record('INV-022/V4/outcome', { expected: 'no investigation created', observed: listed === 0 ? 'no investigation created' : `${listed} investigation(s) created` });
});

test('INV-START start creates the investigation and its values survive save and reload', async ({ page, browser }) => {
  await startScreen(page);
  const v = { problem: marker('problem'), title: marker('title'), question: marker('question') };
  await field(page, 'INV-001').fill(v.problem);
  await field(page, 'INV-002').fill(v.title);
  await field(page, 'INV-023').fill(v.question);
  const id = await field(page, 'INV-006').inputValue();
  await startButton(page).click();
  await expect(page.getByText(v.problem).first(), 'Start did not open the new investigation').toBeVisible({ timeout: 20000 });
  const url = page.url();
  record('INV-004/V3/activate', { expected: 'the new investigation is shown', observed: url.includes(id) ? 'the new investigation is shown' : url });
  record('INV-004/V4/outcome', { expected: v.problem, observed: (await page.getByText(v.problem).first().innerText()).trim() });

  // Save, then read the investigation back in a fresh browser context.
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  const saveState = page.getByRole('status', { name: /save state/i }).or(page.locator('[data-requirement-id="INV-016"]')).first();
  await expect(saveState).toHaveText(/^\s*Saved\s*$/, { timeout: 20000 });
  const fresh = await (await browser.newContext()).newPage();
  await connect(fresh);
  await fresh.getByRole('link', { name: v.title, exact: true }).click();
  const shown = async (value) => ((await fresh.getByText(value).first().isVisible({ timeout: 20000 }).catch(() => false)) ? value : '');
  for (const [obligation, value] of [['INV-001/V5/save', v.problem], ['INV-002/V5/save', v.title], ['INV-023/V5/save', v.question], ['INV-023/V5/reload', v.question]]) {
    const observed = await shown(value);
    record(obligation, { expected: value, observed });
    expect(observed, `${obligation}: ${value} was not shown after reload`).toBe(value);
  }

  // Update: the question is changed in the workspace's details editor, saved and read back.
  const edited = marker('question-edited');
  const edit = fresh.getByRole('button', { name: /edit details/i }).first();
  const editable = await edit.isVisible().catch(() => false);
  let observed = '';
  if (editable) {
    await edit.click();
    await fresh.getByLabel(FIELDS['INV-023'], { exact: true }).fill(edited);
    await fresh.getByRole('button', { name: /save details/i }).first().click();
    await fresh.getByRole('button', { name: 'Save', exact: true }).click();
    const again = await (await browser.newContext()).newPage();
    await connect(again);
    await again.getByRole('link', { name: v.title, exact: true }).click();
    observed = (await again.getByText(edited).first().isVisible({ timeout: 20000 }).catch(() => false)) ? edited : '';
  }
  record('INV-023/V5/update', { expected: edited, observed });
  expect(editable, 'the workspace offers no way to edit the primary investigative question (INV-023 update)').toBe(true);
  expect(observed).toBe(edited);
});
