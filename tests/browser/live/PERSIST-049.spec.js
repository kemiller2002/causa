// Owner: persistence (T19 browser specs, written by the domain lane on reassignment). PERSIST-049, PAT-10: changes
// saved from several tabs are reopened from GitHub in a new tab and in a new browser context, live. A tab reloaded with
// an unsaved change shows only what GitHub holds.
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

/// PAT-10: two tabs each save a new investigation; one also starts an investigation it never saves. Then every tab is
/// closed.
const pat10 = async (browser) => {
  const context = await browser.newContext();
  const saved = [];
  let unsaved;
  for (let i = 0; i < 2; i++) {
    const page = await context.newPage();
    await connect(page);
    const v = await startInvestigation(page, `pat10-tab${i + 1}`);
    await saveAndConfirm(page);
    v.commit = (await commitsTouching(v.prefix))[0];
    saved.push(v);
    if (i === 1) {
      await page.getByRole('link', { name: 'Investigations', exact: true }).first().click();
      unsaved = await startInvestigation(page, 'pat10-never-saved');
      await expect(saveState(page)).toHaveText(/^\s*Unsaved\s*$/);
    }
  }
  for (const p of context.pages()) await p.close();
  return { context, saved, unsaved };
};

/// Opens an investigation by its title from the list of a fresh connection, and reads what it shows.
const reopen = async (page, v) => {
  await connect(page);
  await page.getByRole('link', { name: v.title, exact: true }).click();
  await expect(page.getByText(v.problem).first()).toBeVisible({ timeout: 30000 });
  return {
    state: (await saveState(page).textContent()).trim(),
    title: await page.getByText(v.title).first().isVisible(),
    problem: await page.getByText(v.problem).first().isVisible(),
    question: await page.getByText(v.question).first().isVisible(),
    url: page.url(),
  };
};
const listed = async (page, title) => page.getByRole('link', { name: title, exact: true }).count();

test.describe.configure({ timeout: 300000 });
test.beforeEach(() => requireLive());

test('PERSIST-049-O1', async ({ browser }) => {
  // Save from several tabs, close them, open each investigation in a new tab and in a new browser context.
  const run = await pat10(browser);
  const tab = await run.context.newPage();
  const fresh = await (await browser.newContext()).newPage();
  for (const v of run.saved) {
    for (const page of [tab, fresh]) {
      const r = await reopen(page, v);
      expect(r.url).toContain(v.id);
      expect(r.problem).toBe(true);
    }
  }
});

test('PERSIST-049-O2', async ({ browser }) => {
  // Each reopened investigation shows Saved at its latest commit, holds every confirmed change and nothing else.
  const run = await pat10(browser);
  for (const make of [() => run.context.newPage(), async () => (await browser.newContext()).newPage()]) {
    const page = await make();
    for (const v of run.saved) {
      const latest = (await commitsTouching(v.prefix))[0];
      expect(latest.sha, 'a later commit appeared for the investigation').toBe(v.commit.sha);
      const r = await reopen(page, v);
      expect(r.state).toBe('Saved');
      expect([r.title, r.problem, r.question]).toEqual([true, true, true]);
    }
    await connect(page);
    expect(await listed(page, run.unsaved.title), 'an investigation that was never saved is listed').toBe(0);
    expect((await filesUnder(run.unsaved.prefix)).length, 'GitHub holds an investigation that was never saved').toBe(0);
  }
});

test('PERSIST-049-O3', async ({ page }) => {
  // A tab reloaded while it holds an unsaved change shows only the confirmed GitHub state.
  await connect(page);
  const kept = await startInvestigation(page, 'pat10-reload-saved');
  await saveAndConfirm(page);
  await page.getByRole('link', { name: 'Investigations', exact: true }).first().click();
  const lost = await startInvestigation(page, 'pat10-reload-unsaved');
  await expect(saveState(page)).toHaveText(/^\s*Unsaved\s*$/);
  await page.reload();
  await connect(page);
  expect(await listed(page, lost.title), 'the unsaved investigation survived the reload').toBe(0);
  const r = await reopen(page, kept);
  expect(r.state).toBe('Saved');
  expect(r.problem).toBe(true);
});

test('PERSIST-049-O4', async ({ browser }) => {
  // The whole PAT-10 test, live, on the candidate (the stateful-fake mode is the .NET test of the same identity).
  const run = await pat10(browser);
  const fresh = await (await browser.newContext()).newPage();
  for (const v of run.saved) {
    const r = await reopen(fresh, v);
    expect(r.state).toBe('Saved');
    expect([r.title, r.problem, r.question]).toEqual([true, true, true]);
  }
  await connect(fresh);
  expect(await listed(fresh, run.unsaved.title)).toBe(0);
  test.info().annotations.push({ type: 'candidate', description: process.env.CAUSA_CANDIDATE || 'unknown' });
});

test('PERSIST-049-O5', async ({ browser }) => {
  // Covers PERSIST-005 (opened = the last confirmed commit on GitHub), PERSIST-007 (Saved only for confirmed bytes),
  // PERSIST-020 (the Connect screen says unsaved changes exist only in this tab; a reload starts from GitHub) and
  // PERSIST-021 (a new context loads the latest confirmed commit and shows Saved).
  const page = await (await browser.newContext()).newPage();
  await open(page, '#/');
  await expect(page.getByText(/unsaved changes exist only in (this|the current) tab/i).first(), 'the Connect screen does not state that unsaved changes exist only in this tab').toBeVisible();
  const run = await pat10(browser);
  for (const v of run.saved) {
    const files = await filesUnder(v.prefix);
    for (const value of [v.problem, v.title]) expect(files.some((f) => f.text.includes(value)), `GitHub does not hold ${value}`).toBe(true);
    const r = await reopen(page, v);
    expect(r.state).toBe('Saved');
    expect([r.title, r.problem]).toEqual([true, true]);
  }
  await connect(page);
  expect(await listed(page, run.unsaved.title)).toBe(0);
});
