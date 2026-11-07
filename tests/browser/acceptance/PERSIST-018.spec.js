// Owner: persistence (T19). PERSIST-018: editing while disconnected stays in session memory; no offline queue (PAT-07).
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

const githubRequests = (page) => {
  const seen = [];
  page.on('request', (r) => { if (r.url().startsWith('https://api.github.com/')) seen.push(`${r.method()} ${r.url()}`); });
  return seen;
};

test('PERSIST-018-O1', async ({ page, context }) => {
  await connect(page);
  await start(page, 'p018o1');
  await context.setOffline(true);
  await expect(saveState(page)).not.toHaveText(/^\s*Saved\s*$/);
  await expect(saveState(page)).toHaveText(/Offline|Unsaved/, { timeout: 20000 });
});

test('PERSIST-018-O2', async ({ page, context }) => {
  await connect(page);
  await start(page, 'p018o2');
  const seen = githubRequests(page);
  await context.setOffline(true);
  await button(page, 'Save').click();
  await expect(saveState(page)).toHaveText(/Offline/, { timeout: 20000 });
  expect(seen.filter((s) => s.startsWith('POST'))).toEqual([]);
});

test('PERSIST-018-O3', async ({ page, context }) => {
  await connect(page);
  await start(page, 'p018o3');
  await context.setOffline(true);
  await button(page, 'Save').click();
  await expect(saveState(page)).toHaveText(/Offline/, { timeout: 20000 });
  const seen = githubRequests(page);
  await context.setOffline(false);
  await page.waitForTimeout(3000);
  expect(seen.filter((s) => s.startsWith('POST'))).toEqual([]);
  await expect(saveState(page)).toHaveText(/Unsaved/);
});

test('PERSIST-018-O4', async ({ page, context }) => {
  await connect(page);
  await start(page, 'p018o4');
  const seen = githubRequests(page);
  await context.setOffline(true);
  await button(page, 'Save').click();
  await expect(saveState(page)).toHaveText(/Offline/, { timeout: 20000 });
  await context.setOffline(false);
  await page.waitForTimeout(2000);
  expect(seen.filter((s) => s.startsWith('POST'))).toEqual([]);
  await save(page);
});
