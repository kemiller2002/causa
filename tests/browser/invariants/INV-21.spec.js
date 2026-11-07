// Owner: ui (written by the T18 completion lane under the coordinator's ownership exception).
// INV-21: the credential exists only in tab memory and is sent only in the Authorization header over HTTPS to the
// GitHub API. Driven against the built application (npm run build writes dist/) by role and accessible name; GitHub is
// answered inside the test (the site's own test double, INV-21 "or the site's own origin in tests"), so no real
// credential is needed. After connecting, every place a credential could leak is scanned for the test token:
// requests (URL, headers other than Authorization, bodies), destinations, localStorage, sessionStorage, cookies,
// IndexedDB, Cache Storage, the URL and history, console output, the rendered DOM and unmasked inputs (screenshots),
// and again after a reload.
import { test, expect } from '@playwright/test';

// The built application; CAUSA_APP_PATH points elsewhere (for example a deployed URL).
const APP = process.env.CAUSA_APP_PATH || '/dist/index.html';
const TOKEN = `causa-inv21-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
const REPO = { owner: 'causa-inv21', repo: 'causa-test-data', branch: 'causa-test' };
const OID = '4f1c7e0d2a9b8c6e5f3a1b0c9d8e7f6a5b4c3d2e';

// Recorded GitHub answer shapes: the repository with its stated permissions, a branch head, and Not Found otherwise.
const answer = (url) => {
  const u = new URL(url);
  const repoPath = `/repos/${REPO.owner}/${REPO.repo}`;
  const json = (status, body) => ({ status, contentType: 'application/json; charset=utf-8', body: JSON.stringify(body) });
  if (u.pathname === repoPath) {
    return json(200, { full_name: `${REPO.owner}/${REPO.repo}`, private: true, default_branch: REPO.branch, permissions: { admin: false, maintain: false, push: true, triage: true, pull: true } });
  }
  if (u.pathname === `${repoPath}/git/ref/heads/${REPO.branch}`) {
    return json(200, { ref: `refs/heads/${REPO.branch}`, object: { sha: OID, type: 'commit' } });
  }
  if (u.pathname === '/graphql') {
    return json(200, { data: { repository: { ref: { target: { oid: OID, committedDate: '2026-11-07T18:00:00Z' } } } } });
  }
  return json(404, { message: 'Not Found', documentation_url: 'https://docs.github.com/rest' });
};

// Everything the page keeps that a credential could hide in.
const pageStores = (page) =>
  page.evaluate(async () => {
    const dump = { local: {}, session: {}, cookies: document.cookie, url: location.href, historyLength: history.length, indexedDB: [], caches: [] };
    for (let i = 0; i < localStorage.length; i++) dump.local[localStorage.key(i)] = localStorage.getItem(localStorage.key(i));
    for (let i = 0; i < sessionStorage.length; i++) dump.session[sessionStorage.key(i)] = sessionStorage.getItem(sessionStorage.key(i));
    if (indexedDB.databases) {
      for (const info of await indexedDB.databases()) {
        const db = await new Promise((resolve) => { const r = indexedDB.open(info.name); r.onsuccess = () => resolve(r.result); r.onerror = () => resolve(null); });
        if (!db) { dump.indexedDB.push({ name: info.name }); continue; }
        const stores = {};
        for (const name of db.objectStoreNames) {
          stores[name] = await new Promise((resolve) => {
            const req = db.transaction(name, 'readonly').objectStore(name).getAll();
            req.onsuccess = () => resolve(JSON.stringify(req.result));
            req.onerror = () => resolve('unreadable');
          });
        }
        db.close();
        dump.indexedDB.push({ name: info.name, stores });
      }
    }
    if (self.caches) {
      for (const key of await caches.keys()) {
        const cache = await caches.open(key);
        dump.caches.push({ key, requests: (await cache.keys()).map((r) => r.url) });
      }
    }
    // Inputs still holding the token must be masked (type=password) so no screenshot can show it.
    dump.unmasked = [...document.querySelectorAll('input, textarea')].filter((el) => el.value && el.type !== 'password').map((el) => el.value);
    dump.dom = document.documentElement.outerHTML;
    return dump;
  });

test('no credential in storage, URL or artifact', async ({ page, context }) => {
  const sent = [];
  const logs = [];
  page.on('console', (m) => logs.push(m.text()));
  page.on('pageerror', (e) => logs.push(String(e && e.stack ? e.stack : e)));
  page.on('request', async (request) => {
    sent.push({ url: request.url(), method: request.method(), headers: await request.allHeaders().catch(() => request.headers()), body: request.postData() || '' });
  });
  // GitHub is answered here; any other host is left to the network (and is checked below).
  await context.route('https://api.github.com/**', (route) => route.fulfill(answer(route.request().url())));

  const response = await page.goto(APP);
  expect(response && response.ok(), `the built application ${APP} is not served (npm run build writes dist/)`).toBeTruthy();
  const connectAction = page.getByRole('button', { name: /connect/i }).first();
  await expect(connectAction, 'no Connect action on a fresh launch').toBeVisible({ timeout: 30000 });
  const owner = page.getByLabel(/owner/i).first();
  if (!(await owner.isVisible().catch(() => false))) await connectAction.click();
  await page.getByLabel(/owner/i).first().fill(REPO.owner);
  await page.getByLabel(/^repository$|repository name|^repo/i).first().fill(REPO.repo);
  await page.getByLabel(/branch/i).first().fill(REPO.branch);
  await page.getByLabel(/token|credential/i).first().fill(TOKEN);
  await page.getByRole('button', { name: /connect/i }).first().click();
  await expect.poll(() => sent.filter((r) => r.url.startsWith('https://api.github.com/')).length, { message: 'Connect sent no request to the GitHub API', timeout: 20000 }).toBeGreaterThan(0);
  await page.waitForLoadState('networkidle');

  const leaks = (label, value) => (String(value).includes(TOKEN) ? [label] : []);
  const scan = async (moment) => {
    const stores = await pageStores(page);
    const cookies = await context.cookies();
    return [
      ...leaks(`${moment}: localStorage`, JSON.stringify(stores.local)),
      ...leaks(`${moment}: sessionStorage`, JSON.stringify(stores.session)),
      ...leaks(`${moment}: document.cookie`, stores.cookies),
      ...leaks(`${moment}: cookies`, JSON.stringify(cookies)),
      ...leaks(`${moment}: IndexedDB`, JSON.stringify(stores.indexedDB)),
      ...leaks(`${moment}: Cache Storage`, JSON.stringify(stores.caches)),
      ...leaks(`${moment}: URL`, stores.url),
      ...leaks(`${moment}: page URL`, page.url()),
      ...leaks(`${moment}: rendered DOM`, stores.dom),
      ...leaks(`${moment}: unmasked input (visible in a screenshot)`, JSON.stringify(stores.unmasked)),
      ...leaks(`${moment}: console`, logs.join('\n')),
    ];
  };

  const afterConnect = await scan('after connect');
  const beforeReload = sent.length;
  await page.reload();
  await page.waitForLoadState('networkidle');
  const afterReload = await scan('after reload');
  expect([...afterConnect, ...afterReload], 'the credential leaked').toEqual([]);

  // Destinations: the token travels only to https://api.github.com and only as "Authorization: Bearer <token>".
  const carrying = sent.filter((r) => JSON.stringify(r).includes(TOKEN));
  expect(carrying.length, 'the credential was never sent, so Connect did not use it').toBeGreaterThan(0);
  for (const r of carrying) {
    const u = new URL(r.url);
    expect(`${u.protocol}//${u.host}`, `the credential was sent to ${r.url}`).toBe('https://api.github.com');
    expect(r.url, 'the credential is in a URL').not.toContain(TOKEN);
    expect(r.body, `the credential is in the body of ${r.url}`).not.toContain(TOKEN);
    const headers = Object.entries(r.headers).filter(([, v]) => String(v).includes(TOKEN));
    expect(headers, `the credential is in headers other than Authorization on ${r.url}`).toEqual([['authorization', `Bearer ${TOKEN}`]]);
  }
  // Tab memory only: a reload forgets it, so nothing sent after the reload carries it.
  expect(sent.slice(beforeReload).filter((r) => JSON.stringify(r).includes(TOKEN)), 'the credential survived a reload').toEqual([]);
});
