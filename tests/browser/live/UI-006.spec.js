// Owner: persistence (T19 browser specs, written by the domain lane on reassignment). UI-006: save, sync and conflict
// status. Every state is reached through its real entry condition against the live test repository, except Save
// failed, whose refusal is injected at the network boundary (GitHub cannot be made to refuse on demand; nothing is sent
// to GitHub for that save, so the injected answer cannot hide a write).
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

const WORDS = ['Saved', 'Unsaved', 'Saving', 'Offline', 'Conflict', 'Unknown commit outcome', 'Save failed'];
const isMutation = (req) => req.method() === 'POST' && req.url().startsWith('https://api.github.com/graphql') && /mutation/i.test(req.postData() || '');
const explanation = async (page) =>
  saveState(page).evaluate((el) => (el.getAttribute('aria-describedby') || '').split(/\s+/).map((id) => document.getElementById(id)?.textContent || '').join(' ').replace(/\s+/g, ' ').trim());
/// The lozenge facts of the status as shown: its word, Forma class, glyph (generated content) and accessible name.
const lozenge = async (page) => {
  const s = saveState(page);
  return {
    word: (await s.textContent()).trim(),
    forma: await s.evaluate((el) => el.classList.contains('ef-status-lozenge')),
    glyph: await s.evaluate((el) => getComputedStyle(el, '::before').content),
    role: await s.getAttribute('role'),
  };
};

// ---- the seven states, each from its entry condition --------------------------------------------------------------
const drive = {
  /// A new investigation holds a change that was never sent.
  Unsaved: async (page) => { const v = await startInvestigation(page, 'ui006-unsaved'); await expect(saveState(page)).toHaveText(/^\s*Unsaved\s*$/); return v; },
  /// The mutation is held in flight for a few seconds, then sent for real.
  Saving: async (page) => {
    const v = await startInvestigation(page, 'ui006-saving');
    await page.route('https://api.github.com/graphql', async (route) => {
      if (isMutation(route.request())) await new Promise((r) => setTimeout(r, 5000));
      await route.continue();
    });
    await saveButton(page).click();
    await expect(saveState(page)).toHaveText(/^\s*Saving\s*$/, { timeout: 5000 });
    return v;
  },
  Saved: async (page) => { const v = await startInvestigation(page, 'ui006-saved'); await saveAndConfirm(page); return v; },
  /// The browser goes offline while the tab holds an unsaved change.
  Offline: async (page) => {
    const v = await startInvestigation(page, 'ui006-offline');
    await page.context().setOffline(true);
    await expect(saveState(page)).toHaveText(/^\s*Offline\s*$/, { timeout: 15000 });
    return v;
  },
  /// The mutation reaches GitHub, but its answer is lost on the way back to the tab.
  'Unknown commit outcome': async (page) => {
    const v = await startInvestigation(page, 'ui006-unknown');
    let lost = false;
    await page.route('https://api.github.com/graphql', async (route) => {
      if (!lost && isMutation(route.request())) {
        lost = true;
        await route.fetch();
        await route.abort('connectionreset');
      } else await route.continue();
    });
    await saveButton(page).click();
    await expect(saveState(page)).toHaveText(/^\s*Unknown commit outcome\s*$/, { timeout: 15000 });
    return v;
  },
  /// GitHub refuses the commit with a validation error (GH-VALIDATION); the mutation is not sent.
  'Save failed': async (page) => {
    const v = await startInvestigation(page, 'ui006-failed');
    await page.route('https://api.github.com/graphql', async (route) => {
      if (isMutation(route.request())) {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ data: { createCommitOnBranch: null }, errors: [{ type: 'UNPROCESSABLE', path: ['createCommitOnBranch'], message: 'A path in the commit is not valid' }] }) });
      } else await route.continue();
    });
    await saveButton(page).click();
    await expect(saveState(page)).toHaveText(/^\s*Save failed\s*$/, { timeout: 15000 });
    return v;
  },
  /// Another writer (a second tab) saves the same investigation first; this tab's save then finds it on GitHub.
  Conflict: async (page) => {
    const v = await startInvestigation(page, 'ui006-conflict');
    const other = await page.context().newPage();
    await connect(other);
    const theirs = await startInvestigation(other, 'ui006-other-writer', v.id);
    await saveAndConfirm(other);
    await saveButton(page).click();
    await expect(saveState(page)).toHaveText(/^\s*Conflict\s*$/, { timeout: 60000 });
    return { ...v, theirs, other };
  },
};

test.describe.configure({ timeout: 300000 });
test.beforeEach(() => requireLive());

test('UI-006-O1', async ({ browser }) => {
  // Exactly seven states, each shown with the Forma status lozenge, its word, a glyph and an accessible status.
  const seen = new Map();
  for (const word of WORDS) {
    const context = await browser.newContext();
    const page = await context.newPage();
    await connect(page);
    await drive[word](page);
    const l = await lozenge(page);
    expect(l.word).toBe(word);
    expect(l.forma, `${word}: not a Forma status lozenge`).toBe(true);
    expect(l.role).toBe('status');
    expect(l.glyph === 'none' || l.glyph === 'normal' || l.glyph === '', `${word}: no lozenge glyph (state shown by colour alone)`).toBe(false);
    seen.set(word, l);
    await context.close();
  }
  expect([...seen.keys()].sort()).toEqual([...WORDS].sort());
});

test('UI-006-O2', async ({ browser }) => {
  // PERSIST-031's words and actions for each state, and PERSIST-032: no persistence claim outside Saved.
  const expectations = {
    Unsaved: { text: [/only in this tab/i], save: 'offered' },
    Saving: { text: [/being saved to GitHub/i, /not yet confirmed/i], save: 'absent' },
    Saved: { text: [], save: 'absent' },
    Offline: { text: [/only in this tab/i, /connection returns/i], save: 'absent' },
    'Unknown commit outcome': { text: [/may or may not have reached GitHub/i], save: 'absent' },
    'Save failed': { text: [/kept in this tab/i], save: 'absent', actions: [/^(retry|reconnect)$/i] },
    Conflict: { text: [/another writer/i, /kept/i], save: 'absent', actions: [/review/i, /reapply/i, /discard/i] },
  };
  for (const [word, want] of Object.entries(expectations)) {
    const context = await browser.newContext();
    const page = await context.newPage();
    await connect(page);
    await drive[word](page);
    const said = await explanation(page);
    for (const re of want.text) expect(said, `${word}: the explanation does not say ${re}`).toMatch(re);
    if (word !== 'Saved') expect(`${await saveState(page).textContent()} ${said}`, `${word}: claims persistence`).not.toMatch(/\b(saved|synced|stored|safe)\b|saved locally|on this device|queued|saved later/i);
    const save = saveButton(page);
    if (want.save === 'offered') await expect(save).toBeEnabled();
    else expect((await save.count()) === 0 || (await save.isDisabled()), `${word}: Save is offered`).toBe(true);
    for (const re of want.actions || []) await expect(page.getByRole('button', { name: re }).first(), `${word}: no ${re} action`).toBeVisible();
    if (word === 'Saved') expect(await page.getByRole('button', { name: /^(retry|check again|reconnect)$/i }).count(), 'Saved offers an action').toBe(0);
    await context.close();
  }
});

test('UI-006-O3', async ({ page }) => {
  // Saved is claimed only after GitHub returned the commit and the read-back returned the written bytes. Every read
  // after the commit answer is held for 4 s: the status must not say Saved while they are held.
  await connect(page);
  const v = await startInvestigation(page, 'ui006-o3');
  let answered = 0;
  let held = 0;
  await page.route('https://api.github.com/**', async (route) => {
    const req = route.request();
    if (isMutation(req)) {
      const response = await route.fetch();
      answered = Date.now();
      await route.fulfill({ response });
    } else if (answered && req.method() === 'GET') {
      held += 1;
      await new Promise((r) => setTimeout(r, 4000));
      await route.continue();
    } else await route.continue();
  });
  const states = await watchStates(page);
  await saveButton(page).click();
  await expect.poll(() => answered, { timeout: 60000 }).toBeGreaterThan(0);
  await page.waitForTimeout(2000);
  expect(held, 'no read-back followed the commit answer').toBeGreaterThan(0);
  await expect(saveState(page)).not.toHaveText(/^\s*Saved\s*$/);
  await expect(saveState(page)).toHaveText(/^\s*Saved\s*$/, { timeout: 60000 });
  const files = await filesUnder(v.prefix);
  expect(files.some((f) => f.text.includes(v.problem)), 'Saved shown, but GitHub does not hold the problem statement').toBe(true);
  const history = await states();
  expect(history.indexOf('Saving'), `states: ${history.join(' > ')}`).toBeGreaterThan(-1);
  expect(history.indexOf('Saved')).toBeGreaterThan(history.indexOf('Saving'));
});

test('UI-006-O4', async ({ page }) => {
  // The time of the last confirmed save is shown, and it is the commit's time on GitHub.
  await connect(page);
  const v = await startInvestigation(page, 'ui006-o4');
  await saveAndConfirm(page);
  const said = await explanation(page);
  const shown = said.match(/Last saved:\s*(\d{1,2} \w{3} \d{4}, \d{2}:\d{2} UTC)/);
  expect(shown, `no last-saved time in "${said}"`).not.toBeNull();
  const [commit] = await commitsTouching(v.prefix);
  const at = new Date(commit.commit.committer.date);
  const fmt = (d) => `${d.getUTCDate()} ${d.toLocaleString('en-GB', { month: 'short', timeZone: 'UTC' })} ${d.getUTCFullYear()}, ${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')} UTC`;
  const near = [fmt(at), fmt(new Date(at.getTime() - 60000)), fmt(new Date(at.getTime() + 60000))];
  expect(near, `shown ${shown[1]}, commit at ${at.toISOString()}`).toContain(shown[1]);
});

test('UI-006-O5', async ({ page }) => {
  // A genuine conflict keeps the tab's changes visible, waits for an explicit choice and never overwrites GitHub.
  await connect(page);
  const v = await drive.Conflict(page);
  await expect(page.getByText(v.problem).first(), 'the unsaved change is no longer visible').toBeVisible();
  for (const re of [/review/i, /reapply/i, /discard/i]) await expect(page.getByRole('button', { name: re }).first(), `no ${re} choice`).toBeVisible();
  const sent = [];
  page.on('request', (req) => { if (isMutation(req)) sent.push(req.url()); });
  const head = await branchHead();
  await page.waitForTimeout(8000);
  expect(sent, 'a mutation was sent while in Conflict').toEqual([]);
  await expect(saveState(page)).toHaveText(/^\s*Conflict\s*$/);
  expect(await branchHead(), 'the branch moved while in Conflict').toBe(head);
  const files = await filesUnder(v.prefix);
  expect(files.some((f) => f.text.includes(v.theirs.problem)), "the other writer's version is gone").toBe(true);
  expect(files.some((f) => f.text.includes(v.problem)), "this tab's change was written over the other writer").toBe(false);
  const [theirCommit] = await commitsTouching(v.prefix);
  expect(await reachable(theirCommit.sha)).toBe(true);
});

test('UI-006-O6', async ({ page }) => {
  // Frozen demonstrations are read-only: no save, no edit, and nothing is written while one is open.
  const sent = [];
  page.on('request', (req) => { if (isMutation(req) || ['PUT', 'PATCH', 'DELETE'].includes(req.method())) sent.push(`${req.method()} ${req.url()}`); });
  await connect(page);
  const head = await branchHead();
  const frozen = page.locator('[data-requirement-id="INV-039"] article').filter({ hasText: 'Demonstration' }).first();
  await expect(frozen, 'no frozen demonstration is listed in the test repository').toBeVisible({ timeout: 30000 });
  await frozen.getByRole('link').first().click();
  await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible({ timeout: 30000 });
  await expect(page.getByText(/frozen demonstration|read-only/i).first()).toBeVisible();
  expect(await saveButton(page).count(), 'a frozen demonstration offers Save').toBe(0);
  expect(await page.getByRole('button', { name: /edit|record|add|link evidence|save details/i }).count(), 'a frozen demonstration offers an editing action').toBe(0);
  await page.waitForTimeout(3000);
  expect(sent, 'a write was sent while a frozen demonstration was open').toEqual([]);
  expect(await branchHead()).toBe(head);
});

test('UI-006-O7', async ({ page }) => {
  // The token never appears in the page, the address, storage, the console or any request other than GitHub's
  // Authorization header.
  const leaks = [];
  const token = LIVE.writeToken;
  page.on('console', (m) => { if (m.text().includes(token)) leaks.push(`console: ${m.type()}`); });
  page.on('request', (req) => {
    if (req.url().includes(token)) leaks.push(`url: ${req.url().slice(0, 80)}`);
    if ((req.postData() || '').includes(token)) leaks.push(`body: ${req.url().slice(0, 80)}`);
    for (const [name, value] of Object.entries(req.headers())) {
      if (!value.includes(token)) continue;
      const allowed = name.toLowerCase() === 'authorization' && req.url().startsWith('https://api.github.com/');
      if (!allowed) leaks.push(`header ${name}: ${req.url().slice(0, 80)}`);
    }
  });
  await connect(page);
  await startInvestigation(page, 'ui006-o7');
  await saveAndConfirm(page);
  const where = await page.evaluate((t) => {
    const found = [];
    if (document.documentElement.outerHTML.includes(t)) found.push('page HTML');
    if (location.href.includes(t)) found.push('address');
    if (document.title.includes(t)) found.push('title');
    for (const s of [localStorage, sessionStorage]) for (let i = 0; i < s.length; i++) if ((s.getItem(s.key(i)) || '').includes(t)) found.push('web storage');
    if (document.cookie.includes(t)) found.push('cookie');
    return found;
  }, token);
  const cookies = (await page.context().cookies()).filter((c) => c.value.includes(token)).map(() => 'cookie jar');
  expect([...leaks, ...where, ...cookies]).toEqual([]);
});
