// Owner: persistence (T19). PERSIST-022: one save state per investigation (PAT-02, PAT-05).
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
// CAUSA_PROVIDER=harness-double answers api.github.com from a local GitHub double (synthetic, never evidence), as the
// governed journeys do; otherwise nothing is intercepted and the suite talks to the real test repository.
const routedContexts = new WeakSet();
test.describe.configure({ timeout: 120_000 });
const useDouble = async (context) => {
  if (process.env.CAUSA_PROVIDER !== 'harness-double' || routedContexts.has(context)) return;
  routedContexts.add(context);
  const double = process.env.CAUSA_DOUBLE_URL || 'http://127.0.0.1:4610';
  await context.route('https://api.github.com/**', async (route) => {
    const r = route.request();
    const u = new URL(r.url());
    const cors = { 'access-control-allow-origin': '*', 'access-control-allow-headers': '*', 'access-control-allow-methods': 'GET,POST,PUT,PATCH,DELETE' };
    if (r.method() === 'OPTIONS') return route.fulfill({ status: 204, headers: cors });
    if (offlineContexts.has(context)) return route.abort('internetdisconnected');
    // read the whole answer before fulfilling: a streamed answer can be disposed if the page moves on
    let answer, body;
    try { answer = await route.fetch({ url: double + u.pathname + u.search }); body = await answer.body(); } catch { return route.abort('failed').catch(() => {}); }
    await route.fulfill({ status: answer.status(), headers: { ...answer.headers(), ...cors }, body }).catch(() => {});
  });
};
/// Offline as the browser sees it; with the double, its route refuses too (a routed answer would bypass setOffline).
const offlineContexts = new WeakSet();
const goOffline = async (context) => { offlineContexts.add(context); await context.setOffline(true); };
const goOnline = async (context) => { offlineContexts.delete(context); await context.setOffline(false); };
const open = async (page, hash = '#/') => {
  await useDouble(page.context());
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
  const newInvestigation = page.getByRole('link', { name: /new investigation/i }).or(page.getByRole('button', { name: /new investigation/i })).first();
  if (!(await newInvestigation.isVisible().catch(() => false))) await page.getByRole('navigation', { name: 'Primary', exact: true }).getByRole('link', { name: 'Investigations' }).first().click();
  await newInvestigation.click();
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

test('PERSIST-022-O1', async ({ page }) => {
  await connect(page);
  const first = await start(page, 'p022o1a');
  const firstUrl = page.url();
  await save(page);
  await start(page, 'p022o1b');
  await expect(saveState(page)).toHaveText(/Unsaved/);
  await page.goto(firstUrl);
  await expect(page.getByText(first.problem).first()).toBeVisible();
  await expect(saveState(page)).toHaveText(/^\s*Saved\s*$/);
});

test('PERSIST-022-O2', async ({ page }) => {
  await connect(page);
  await start(page, 'p022o2a');
  const unsavedUrl = page.url();
  const saved = await start(page, 'p022o2b');
  await save(page);
  await expect(page.getByText(saved.problem).first()).toBeVisible();
  await expect(saveState(page)).toHaveText(/^\s*Saved\s*$/);
  await page.goto(unsavedUrl);
  await expect(saveState(page)).toHaveText(/Unsaved/);
});

test('PERSIST-022-O3', async ({ page }) => {
  await connect(page);
  await start(page, 'p022o3a');
  await start(page, 'p022o3b');
  await save(page);
  await page.getByRole('navigation', { name: 'Primary', exact: true }).getByRole('link', { name: 'Investigations' }).first().click();
  const summary = page.getByRole('status', { name: /workspace|summary/i });
  if (await summary.count()) {
    await expect(summary.first()).not.toHaveText(/all (investigations )?saved/i);
  }
});

test('PERSIST-022-O4', async ({ page }) => {
  await connect(page);
  await start(page, 'p022o4a');
  const a = page.url();
  await start(page, 'p022o4b');
  await save(page);
  const b = page.url();
  await page.goto(a);
  await expect(saveState(page)).toHaveText(/Unsaved/);
  await page.goto(b);
  await expect(saveState(page)).toHaveText(/^\s*Saved\s*$/);
});
