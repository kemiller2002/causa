// Owner: persistence (T19 browser specs, written by the domain lane on reassignment). PERSIST-041, PAT-02: three tabs
// save changes to different investigations at the same moment against the live test repository. Every request the
// tabs send to GitHub is recorded; GitHub itself is read back with the read-only token.
import { test, expect } from '@playwright/test';

// ---- shared live prelude (each live spec carries its own copy: tests/browser/support is integration's) -------------
// Live (SU-BROWSER-LIVE): the built or deployed application connected with CAUSA_WRITE_TOKEN to the authorized test
// repository CAUSA_TEST_OWNER/CAUSA_TEST_REPO@CAUSA_TEST_BRANCH; GitHub itself is read back with CAUSA_GITHUB_TOKEN,
// never through the application. Without these the tests fail closed with "BLOCKED: no credential"; they never pass
// vacuously and never fall back to a fake.
const APP = process.env.CAUSA_APP_URL || process.env.CAUSA_APP_PATH || '/dist/index.html';
const LIVE = {
  owner: process.env.CAUSA_TEST_OWNER || '',
  repo: process.env.CAUSA_TEST_REPO || '',
  branch: process.env.CAUSA_TEST_BRANCH || '',
  writeToken: process.env.CAUSA_WRITE_TOKEN || '',
  readToken: process.env.CAUSA_GITHUB_TOKEN || '',
};
const requireLive = () => {
  const missing = Object.entries({ CAUSA_WRITE_TOKEN: LIVE.writeToken, CAUSA_GITHUB_TOKEN: LIVE.readToken, CAUSA_TEST_OWNER: LIVE.owner, CAUSA_TEST_REPO: LIVE.repo, CAUSA_TEST_BRANCH: LIVE.branch })
    .filter(([, v]) => !v).map(([k]) => k);
  if (missing.length) throw new Error(`BLOCKED: no credential (${missing.join(', ')} not set); this live test needs the authorized GitHub test repository`);
};
const marker = (label) => `${label}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

// GitHub, read directly with the read-only token.
const gh = async (path) => {
  const r = await fetch(`https://api.github.com${path}`, { headers: { authorization: `Bearer ${LIVE.readToken}`, accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28', 'cache-control': 'no-cache' } });
  if (!r.ok) throw new Error(`GitHub ${path}: ${r.status}`);
  return r.json();
};
const repoPath = (p) => `/repos/${LIVE.owner}/${LIVE.repo}${p}`;
const branchHead = async () => (await gh(repoPath(`/branches/${encodeURIComponent(LIVE.branch)}`))).commit.sha;
/// True when `oid` is reachable from the branch head (the head is the commit or a descendant of it).
const reachable = async (oid) => ['ahead', 'identical'].includes((await gh(repoPath(`/compare/${oid}...${encodeURIComponent(LIVE.branch)}`))).status);
/// The commits on the branch that touched a path, newest first.
const commitsTouching = async (path) => gh(repoPath(`/commits?sha=${encodeURIComponent(LIVE.branch)}&path=${encodeURIComponent(path)}&per_page=100`));
/// The decoded text of every file under a path at the branch head.
const filesUnder = async (prefix) => {
  const tree = await gh(repoPath(`/git/trees/${encodeURIComponent(LIVE.branch)}?recursive=1`));
  const blobs = (tree.tree || []).filter((t) => t.type === 'blob' && t.path.startsWith(prefix));
  return Promise.all(blobs.map(async (b) => ({ path: b.path, text: Buffer.from((await gh(repoPath(`/git/blobs/${b.sha}`))).content || '', 'base64').toString('utf8') })));
};

// The application, driven black-box (role, accessible name, data-requirement-id).
const open = async (page, hash = '#/') => {
  await page.goto('about:blank');
  await page.goto(APP + hash);
  await expect(page.locator('#causa-main'), 'the engine did not render its first view').toBeVisible({ timeout: 30000 });
};
const connect = async (page) => {
  await open(page, '#/');
  await page.getByLabel('Repository owner', { exact: true }).fill(LIVE.owner);
  await page.getByLabel('Repository', { exact: true }).fill(LIVE.repo);
  await page.getByLabel('Branch', { exact: true }).fill(LIVE.branch);
  await page.getByLabel('Access token', { exact: true }).fill(LIVE.writeToken);
  await page.getByRole('button', { name: 'Connect data repository', exact: true }).click();
  await expect(page.getByText(/^Connected to/).first(), 'Connect did not succeed against the test repository').toBeVisible({ timeout: 30000 });
};
const saveState = (page) => page.getByRole('status', { name: /save state/i }).or(page.locator('[data-requirement-id="INV-016"]')).first();
const saveButton = (page) => page.getByRole('button', { name: 'Save', exact: true });
/// Starts a new investigation from the list (unsaved); returns its values, id and the workspace path it will be saved to.
const startInvestigation = async (page, label, id) => {
  const v = { problem: marker(`${label}-problem`), title: marker(`${label}-title`), question: marker(`${label}-question`) };
  await page.getByRole('link', { name: /new investigation/i }).or(page.getByRole('button', { name: /new investigation/i })).first().click();
  await page.getByLabel('Problem statement', { exact: true }).fill(v.problem);
  await page.getByLabel('Title', { exact: true }).fill(v.title);
  await page.getByLabel('Primary investigative question', { exact: true }).fill(v.question);
  if (id) await page.getByLabel('Investigation identifier', { exact: true }).fill(id);
  v.id = await page.getByLabel('Investigation identifier', { exact: true }).inputValue();
  await page.getByRole('button', { name: 'Start investigation', exact: true }).click();
  await expect(page.getByText(v.problem).first(), 'Start did not open the new investigation').toBeVisible({ timeout: 30000 });
  const url = new URL(page.url().replace('#/', 'http://h/'));
  v.workspace = url.searchParams.get('ws') || '';
  v.prefix = `${v.workspace}/investigations/${v.id}/`;
  return v;
};
const saveAndConfirm = async (page) => {
  await saveButton(page).click();
  await expect(saveState(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 60000 });
};
/// Every text the save status takes, sampled as it changes.
const watchStates = async (page) => {
  await page.evaluate(() => {
    window.__causaStates = [];
    const read = () => {
      const el = document.querySelector('[data-requirement-id="INV-016"]');
      const t = el ? el.textContent.trim() : '';
      if (t && window.__causaStates.at(-1) !== t) window.__causaStates.push(t);
    };
    read();
    new MutationObserver(read).observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true });
  });
  return async () => page.evaluate(() => window.__causaStates.slice());
};
// ---------------------------------------------------------------------------------------------------------------------

const TABS = 3;

/// PAT-02: three tabs of one browser, each connected and holding a new investigation, press Save together.
const pat02 = async (browser) => {
  const headBefore = await branchHead();
  const context = await browser.newContext();
  const tabs = [];
  for (let i = 0; i < TABS; i++) {
    const page = await context.newPage();
    const mutations = [];
    const contentWrites = [];
    page.on('request', (req) => {
      if (!req.url().startsWith('https://api.github.com/')) return;
      if (req.method() === 'POST' && req.url().startsWith('https://api.github.com/graphql') && /mutation/i.test(req.postData() || '')) mutations.push(req.postDataJSON());
      if (['PUT', 'PATCH', 'DELETE'].includes(req.method())) contentWrites.push(`${req.method()} ${req.url()}`);
    });
    await connect(page);
    const v = await startInvestigation(page, `pat02-tab${i + 1}`);
    tabs.push({ page, v, mutations, contentWrites, states: await watchStates(page) });
  }
  await Promise.all(tabs.map((t) => saveButton(t.page).click()));
  await Promise.all(tabs.map((t) => expect(saveState(t.page)).toHaveText(/^\s*Saved\s*$/, { timeout: 120000 })));
  for (const t of tabs) {
    t.history = await t.states();
    t.commits = await commitsTouching(t.v.prefix);
    t.files = await filesUnder(t.v.prefix);
    t.shownTitle = (await t.page.getByRole('heading', { level: 1 }).first().textContent()).trim();
  }
  for (const t of tabs) test.info().annotations.push({ type: 'live-commit', description: `${t.v.prefix} ${t.commits.map((c) => c.sha).join(',')}` });
  return { context, tabs, headBefore };
};

// ---- checks shared by the identities ----------------------------------------------------------------------------------
const confirmedOnce = (run) => {
  for (const t of run.tabs) {
    expect(t.commits.length, `${t.v.prefix}: ${t.commits.length} commits, expected exactly one`).toBe(1);
    expect(t.files.some((f) => f.text.includes(t.v.problem)), `${t.v.prefix}: GitHub does not hold the change`).toBe(true);
  }
};
const ownStates = (run) => {
  for (const t of run.tabs) {
    expect(t.shownTitle, 'a tab shows another investigation').toBe(t.v.title);
    expect(t.history.at(-1)).toBe('Saved');
    // Saved appeared only after Saving (PERSIST-007): never straight from Unsaved.
    expect(t.history.indexOf('Saving'), `states: ${t.history.join(' > ')}`).toBeGreaterThan(-1);
    expect(t.history.indexOf('Saved')).toBeGreaterThan(t.history.indexOf('Saving'));
  }
};
const mutationShape = async (run) => {
  const trailers = [];
  for (const t of run.tabs) {
    expect(t.contentWrites, 'a save used a per-file or ref write').toEqual([]);
    expect(t.mutations.length, `${t.v.prefix}: no mutation recorded`).toBeGreaterThan(0);
    for (const m of t.mutations) {
      expect(m.query || '', 'a mutation that is not createCommitOnBranch').toMatch(/createCommitOnBranch/);
      const input = (m.variables && (m.variables.input || Object.values(m.variables)[0])) || {};
      expect(input.expectedHeadOid || '', 'a mutation without an expected head').toMatch(/^[0-9a-f]{40}$/);
      const changes = input.fileChanges || {};
      const paths = [...(changes.additions || []), ...(changes.deletions || [])].map((c) => c.path);
      expect(paths.length).toBeGreaterThan(0);
      for (const p of paths) expect(p.startsWith(t.v.prefix), `${p} is outside ${t.v.prefix}`).toBe(true);
    }
    for (const c of t.commits) {
      const lines = c.commit.message.split('\n').filter((l) => /^[A-Za-z][A-Za-z-]*:\s*\S/.test(l));
      expect(lines.length, `commit ${c.sha} carries no write-marker trailer`).toBeGreaterThan(0);
      trailers.push(lines.join('\n'));
      expect(await reachable(c.sha), `commit ${c.sha} is not reachable from the branch head`).toBe(true);
    }
  }
  expect(new Set(trailers).size, 'two commits carry the same write marker').toBe(trailers.length);
  expect(await reachable(run.headBefore), 'the head before the test is no longer reachable').toBe(true);
};

test.describe.configure({ timeout: 300000 });
test.beforeEach(() => requireLive());

test('PERSIST-041-O1', async ({ browser }) => {
  // At least three tabs save changes to different investigations at the same moment, live.
  const run = await pat02(browser);
  expect(new Set(run.tabs.map((t) => t.v.prefix)).size).toBe(TABS);
  for (const t of run.tabs) await expect(saveState(t.page)).toHaveText(/^\s*Saved\s*$/);
  await run.context.close();
});

test('PERSIST-041-O2', async ({ browser }) => {
  // Every change is confirmed exactly once, and each tab shows its own investigation's state.
  const run = await pat02(browser);
  confirmedOnce(run);
  ownStates(run);
  await run.context.close();
});

test('PERSIST-041-O3', async ({ browser }) => {
  // Every mutation is one createCommitOnBranch with an expected head and paths inside its investigation; markers are
  // unique; every commit stays reachable from the branch head.
  const run = await pat02(browser);
  await mutationShape(run);
  await run.context.close();
});

test('PERSIST-041-O4', async ({ browser }) => {
  // The whole PAT-02 test, live, on the candidate (the stateful-fake mode is the .NET test of the same identity).
  const run = await pat02(browser);
  confirmedOnce(run);
  ownStates(run);
  await mutationShape(run);
  test.info().annotations.push({ type: 'candidate', description: process.env.CAUSA_CANDIDATE || 'unknown' });
  await run.context.close();
});

test('PERSIST-041-O5', async ({ browser }) => {
  // Covers PERSIST-006 (one createCommitOnBranch per save, expected head, own paths), PERSIST-007 (Saved only after
  // Saving and with the bytes on GitHub), PERSIST-013 (every commit reachable), PERSIST-014 (unique markers),
  // PERSIST-022 (each tab its own state) and PERSIST-025 (live evidence naming the real commits).
  const run = await pat02(browser);
  await mutationShape(run);
  confirmedOnce(run);
  ownStates(run);
  const named = test.info().annotations.filter((a) => a.type === 'live-commit');
  expect(named.length).toBe(TABS);
  await run.context.close();
});
