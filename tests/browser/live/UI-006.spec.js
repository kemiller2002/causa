// Owner: persistence (T19). UI-006 (live, real GitHub): save, sync and conflict status. Needs CAUSA_WRITE_TOKEN and
// CAUSA_GITHUB_TOKEN against CAUSA_TEST_OWNER/CAUSA_TEST_REPO@CAUSA_TEST_BRANCH; without them every test is BLOCKED, never a pass.
import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// ---- shared prelude (each persistence spec carries its own copy: tests/browser/support is integration's) ---------
// The built application (npm run build writes dist/); CAUSA_APP_PATH points elsewhere, e.g. a deployed URL.
const APP = process.env.CAUSA_APP_PATH || '/dist/index.html';
// The repository the suite connects to: the stateful fake behind the test build, or the live test repository.
const REPO = {
  owner: process.env.CAUSA_TEST_OWNER || 'harness',
  repo: process.env.CAUSA_TEST_REPO || 'causa-test',
  branch: process.env.CAUSA_TEST_BRANCH || 'main',
  token: process.env.CAUSA_WRITE_TOKEN || 'harness-token',
};
const mark = (label) => `${label}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
const open = async (page, hash = '#/') => {
  await page.goto(APP + hash);
  await expect(page.locator('#causa-main'), 'the engine did not render its first view').toBeVisible({ timeout: 30000 });
};
const firstVisible = async (candidates) => {
  for (const l of candidates) if ((await l.count()) && (await l.first().isVisible())) return l.first();
  return candidates[0].first();
};
const connect = async (page) => {
  await open(page);
  const owner = () => firstVisible([page.getByLabel('Repository owner', { exact: true }), page.getByLabel(/owner/i)]);
  if (!(await (await owner()).isVisible().catch(() => false))) await page.getByRole('button', { name: /connect/i }).first().click();
  await (await owner()).fill(REPO.owner);
  await (await firstVisible([page.getByLabel('Repository', { exact: true }), page.getByLabel(/repository name|^repo/i)])).fill(REPO.repo);
  await (await firstVisible([page.getByLabel(/branch/i)])).fill(REPO.branch);
  await (await firstVisible([page.getByLabel(/token|credential/i)])).fill(REPO.token);
  await page.getByRole('button', { name: /connect/i }).first().click();
  await expect(page.getByRole('link', { name: /new investigation/i }).or(page.getByRole('button', { name: /new investigation/i })).first()).toBeVisible({ timeout: 20000 });
};
const saveState = (page) => page.getByRole('status', { name: /save state/i }).or(page.locator('[data-requirement-id="INV-016"]')).first();
const field = (page, name) => page.getByLabel(name, { exact: true });
const button = (page, name) => page.getByRole('button', { name, exact: true });
/// Starts an investigation (not saved); returns its values.
const start = async (page, label) => {
  const v = { problem: mark(`${label}-problem`), title: mark(`${label}-title`) };
  await page.getByRole('link', { name: /new investigation/i }).or(page.getByRole('button', { name: /new investigation/i })).first().click();
  await field(page, 'Problem statement').fill(v.problem);
  await field(page, 'Title').fill(v.title);
  await button(page, 'Start investigation').click();
  await expect(page.getByText(v.problem).first()).toBeVisible({ timeout: 20000 });
  return v;
};
const save = async (page) => {
  await button(page, 'Save').click();
  await expect(saveState(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 20000 });
};
/// Every browser store's contents as text (PERSIST-003): localStorage, sessionStorage, IndexedDB, Cache Storage, OPFS.
const storeContents = (page) => page.evaluate(async () => {
  const out = [];
  for (const s of [localStorage, sessionStorage]) for (let i = 0; i < s.length; i++) out.push(`${s.key(i)}=${s.getItem(s.key(i))}`);
  if (indexedDB.databases) for (const d of await indexedDB.databases()) out.push(`indexedDB:${d.name}`);
  if (self.caches) for (const k of await caches.keys()) out.push(`cache:${k}`);
  try { const root = await navigator.storage.getDirectory(); for await (const name of root.keys()) out.push(`opfs:${name}`); } catch { /* unsupported */ }
  return out.join('\n');
});
/// The text of the application's own files in the built artifact (PAT-13 scans the production artifact). The .NET
/// runtime's _framework directory caches its static assets, which PERSIST-003-O4 permits, so it is not scanned.
const artifactText = () => {
  const dir = path.resolve(process.env.CAUSA_DIST_DIR || 'dist');
  const texts = [];
  const walk = (d) => { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) { if (e.name !== '_framework') walk(p); } else if (/\.(js|mjs|html)$/.test(e.name)) texts.push(fs.readFileSync(p, 'utf8')); } };
  if (!fs.existsSync(dir)) throw new Error(`no built artifact at ${dir}: run the build first`);
  walk(dir);
  return texts.join('\n');
};
// ---------------------------------------------------------------------------------------------------------------------

const blocked = () => {
  const missing = ['CAUSA_WRITE_TOKEN', 'CAUSA_GITHUB_TOKEN', 'CAUSA_TEST_OWNER', 'CAUSA_TEST_REPO', 'CAUSA_TEST_BRANCH'].filter((v) => !process.env[v]);
  if (missing.length) throw new Error(`BLOCKED: no credential: ${missing.join(', ')} not set`);
};
/// The text GitHub holds under the test branch for a value, read directly with the read token (never through the app).
const githubHolds = async (request, needle) => {
  const h = { Authorization: `Bearer ${process.env.CAUSA_GITHUB_TOKEN}`, Accept: 'application/vnd.github+json' };
  const base = `https://api.github.com/repos/${REPO.owner}/${REPO.repo}`;
  const ref = await (await request.get(`${base}/git/ref/heads/${REPO.branch}`, { headers: h })).json();
  const commit = await (await request.get(`${base}/commits/${ref.object.sha}`, { headers: h })).json();
  for (const f of commit.files || []) {
    if (!f.filename.endsWith('events.ndjson')) continue;
    const c = await (await request.get(`${base}/contents/${f.filename}?ref=${ref.object.sha}`, { headers: h })).json();
    if (Buffer.from(c.content || '', 'base64').toString('utf8').includes(needle)) return true;
  }
  return false;
};
const SEVEN = ['Saved', 'Unsaved', 'Saving', 'Offline', 'Conflict', 'Unknown commit outcome', 'Save failed'];

test('UI-006-O1', async ({ page }) => {
  blocked();
  await connect(page);
  await start(page, 'ui006o1');
  const text = (await saveState(page).textContent()) || '';
  expect(SEVEN.some((s) => text.includes(s))).toBe(true);
  await expect(saveState(page)).toHaveAttribute('role', 'status');
});

test('UI-006-O2', async ({ page }) => {
  blocked();
  await connect(page);
  await start(page, 'ui006o2');
  await expect(saveState(page)).toHaveText(/Unsaved/);
  await button(page, 'Save').click();
  await expect(saveState(page)).toHaveText(/Saving|Saved/);
  await expect(saveState(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 30000 });
});

test('UI-006-O3', async ({ page, request }) => {
  blocked();
  await connect(page);
  const v = await start(page, 'ui006o3');
  await button(page, 'Save').click();
  await expect(saveState(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 30000 });
  expect(await githubHolds(request, v.problem)).toBe(true);
});

test('UI-006-O4', async ({ page }) => {
  blocked();
  await connect(page);
  await start(page, 'ui006o4');
  await save(page);
  await expect(page.getByText(/saved (at|on)|last saved/i).first().or(page.locator('time').first())).toBeVisible();
});

test('UI-006-O5', async ({ browser }) => {
  blocked();
  const a = await (await browser.newContext()).newPage();
  const b = await (await browser.newContext()).newPage();
  await connect(a);
  const v = await start(a, 'ui006o5');
  await save(a);
  await connect(b);
  await b.getByRole('link', { name: v.title }).click();
  const mine = mark('ui006o5-a');
  for (const [p, text] of [[a, mine], [b, mark('ui006o5-b')]]) {
    await p.getByRole('button', { name: /add observation|record observation/i }).first().click();
    await p.getByLabel(/observation/i).first().fill(text);
    await p.getByRole('button', { name: /^(add|record)/i }).last().click();
  }
  await save(b);
  await button(a, 'Save').click();
  await expect(saveState(a)).toHaveText(/Conflict/, { timeout: 30000 });
  await expect(a.getByText(mine).first()).toBeVisible();
});

test('UI-006-O6', async ({ page }) => {
  blocked();
  await connect(page);
  await page.getByRole('navigation', { name: 'Primary', exact: true }).getByRole('link', { name: 'Investigations' }).first().click();
  const catalogCase = page.getByRole('link', { name: /Apollo|Challenger|lightbulb/i }).first();
  await catalogCase.click();
  await expect(button(page, 'Save')).toHaveCount(0);
});

test('UI-006-O7', async ({ page }) => {
  blocked();
  await connect(page);
  await start(page, 'ui006o7');
  await save(page);
  expect(await page.content()).not.toContain(REPO.token);
  expect(page.url()).not.toContain(REPO.token);
});
