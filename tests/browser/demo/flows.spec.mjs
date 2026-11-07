// Owner: coordinator's demo verification. One test per user-visible flow of the deployed site, black-box (role,
// accessible name, visible text). A flow fails with a sentence saying what did not work; nothing passes without having
// been observed. Flow 6 writes one new investigation to the site's test data repository.
import { test, expect } from '@playwright/test';

const URL_ = process.env.DEMO_URL;
const CATALOG = new RegExp(process.env.DEMO_CATALOG_TITLE || 'Apollo 11', 'i');
const CREDS = {
  owner: process.env.DEMO_OWNER || '',
  repo: process.env.DEMO_REPO || '',
  branch: process.env.DEMO_BRANCH || '',
  token: process.env.DEMO_TOKEN || '',
};
const marker = (label) => `demo-${label}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
const note = (text) => test.info().annotations.push({ type: 'note', description: text });

test.beforeEach(() => {
  if (!URL_) throw new Error('DEMO_URL is not set: give the deployed site address');
});

/// A page in a fresh context. Page errors and console errors are collected for the flow to judge. With
/// DEMO_GITHUB_DOUBLE (local checks only) the browser's GitHub requests go to that double.
const freshPage = async (browser) => {
  const context = await browser.newContext({ colorScheme: 'light' });
  if (process.env.DEMO_GITHUB_DOUBLE) {
    await context.route('https://api.github.com/**', async (route) => {
      const u = new URL(route.request().url());
      try {
        const r = await route.fetch({ url: process.env.DEMO_GITHUB_DOUBLE + u.pathname + u.search });
        await route.fulfill({ status: r.status(), headers: r.headers(), body: await r.body() });
      } catch {
        await route.abort().catch(() => {});
      }
    });
  }
  const page = await context.newPage();
  page.problems = [];
  page.on('pageerror', (e) => page.problems.push(`page error: ${e.message.split('\n')[0]}`));
  // A failed load is reported with its address (the console message alone does not name it).
  page.on('response', (r) => { if (r.status() >= 400 && !r.url().startsWith('https://api.github.com/')) page.problems.push(`HTTP ${r.status()} ${r.url()}`); });
  page.on('console', (m) => {
    if (m.type() === 'error' && !/^Failed to load resource/.test(m.text())) page.problems.push(`console error: ${m.text().split('\n')[0].slice(0, 200)}`);
  });
  return page;
};
const open = async (page, hash = '#/') => {
  await page.goto(new URL(hash, URL_).toString());
  await expect(page.locator('#causa-main'), 'the application did not render (no main content after 30 s)').toBeVisible({ timeout: 30000 });
  await expect(page.locator('main h1').first(), 'the page has no heading').toBeVisible({ timeout: 20000 });
};
const noProblems = (page) => expect(page.problems, 'the page reported errors').toEqual([]);
const primary = (page) => page.getByRole('navigation', { name: 'Primary', exact: true }).filter({ visible: true }).first();
const connectedText = (page) => page.getByText(/^Connected to/).first();
const connectButton = (page) => page.getByRole('button', { name: 'Connect data repository', exact: true });

/// Whether the site's build carries a deploy connection (only its presence is read, never its key).
const hasDeployConnection = async (page) =>
  page.evaluate(async () => {
    try {
      const r = await fetch(new URL('deploy-connection.json', location.href), { cache: 'no-store' });
      return r.ok && !!(await r.json())?.connection;
    } catch {
      return false;
    }
  });
/// Waits until the site is connected or settled on its Connect form. With a deploy connection the form may show for a
/// moment before the app connects, so the form counts only after 20 s without a connection.
const connectionMode = async (page) => {
  await expect(connectedText(page).or(connectButton(page)).first(), 'neither "Connected to …" nor the Connect form appeared within 30 s').toBeVisible({ timeout: 30000 });
  if (await connectedText(page).isVisible()) return 'connected';
  try {
    await expect(connectedText(page)).toBeVisible({ timeout: 20000 });
    return 'connected';
  } catch {
    return 'form';
  }
};
const connectionProblem = async (page) => {
  const refused = page.getByText(/refused the access token|could not connect|not connected/i).first();
  return (await refused.isVisible()) ? (await refused.innerText()).replace(/\s+/g, ' ').trim() : '';
};
/// Connected to the data repository: by the deploy key, or with DEMO_* credentials when the site shows its form.
const ensureConnected = async (page) => {
  await open(page, '#/');
  if ((await connectionMode(page)) === 'connected') return;
  if (await hasDeployConnection(page)) throw new Error(`NOT CONNECTED: the site has a deploy connection but shows its Connect form: ${(await connectionProblem(page)) || 'no reason shown'}`);
  if (!CREDS.token) throw new Error('NOT CONNECTED: the site shows its Connect form (no deploy key) and DEMO_OWNER/DEMO_REPO/DEMO_BRANCH/DEMO_TOKEN are not set');
  await page.getByLabel('Repository owner', { exact: true }).fill(CREDS.owner);
  await page.getByLabel('Repository', { exact: true }).fill(CREDS.repo);
  await page.getByLabel('Branch', { exact: true }).fill(CREDS.branch);
  await page.getByLabel('Access token', { exact: true }).fill(CREDS.token);
  await connectButton(page).click();
  await expect(connectedText(page), 'Connect with the DEMO_* credentials did not succeed').toBeVisible({ timeout: 30000 });
};
/// Fails with the page's own words when it shows a failure instead of the content (waits briefly for either).
const failureIfShown = async (page, what) => {
  const failure = page.getByRole('alert').filter({ hasText: /not available|could not|failed|error/i }).first();
  await expect(page.locator('main h1').or(failure).first(), `${what} shows neither a heading nor a failure`).toBeVisible({ timeout: 30000 });
  await page.waitForTimeout(1500);
  if (await failure.isVisible()) throw new Error(`${what} shows a failure: ${(await failure.innerText()).replace(/\s+/g, ' ').trim().slice(0, 300)}`);
};
/// The listed investigations, black-box: the list items of the main region that hold a link (the card title).
const cards = (page) => page.getByRole('main').getByRole('listitem').filter({ has: page.getByRole('link') });
const newInvestigation = (page) => page.getByRole('link', { name: /new investigation/i }).or(page.getByRole('button', { name: /new investigation/i })).first();
const field = (page, name) => page.getByLabel(name, { exact: true });

test('1. The app loads with the light theme', async ({ browser }) => {
  const page = await freshPage(browser);
  await open(page, '#/');
  const banner = page.getByRole('banner');
  await expect(banner, 'no masthead').toBeVisible();
  await expect(banner.getByText('Causa', { exact: true }).first(), 'the masthead does not say Causa').toBeVisible();
  await expect(page, 'the page title does not name Causa').toHaveTitle(/Causa/);
  const styles = await page.evaluate(() => {
    const root = getComputedStyle(document.documentElement);
    const body = getComputedStyle(document.body);
    const rgb = (c) => (c.match(/\d+(\.\d+)?/g) || []).slice(0, 3).map(Number);
    const [r, g, b] = rgb(body.backgroundColor);
    return {
      forma: [...document.styleSheets].some((s) => /forma|design-system|vendor\/forma/i.test(s.href || '')) && root.getPropertyValue('--ef-color-text-primary').trim() !== '',
      luminance: (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255,
      background: body.backgroundColor,
    };
  });
  expect(styles.forma, 'Forma styles are not applied (no Forma stylesheet or --ef-color-text-primary)').toBe(true);
  expect(styles.luminance, `the page background ${styles.background} is not light`).toBeGreaterThan(0.75);
  await page.waitForTimeout(2000);
  noProblems(page);
  note(`background ${styles.background}`);
});

test('2. Navigation between Investigations, Administration and Settings', async ({ browser }) => {
  const page = await freshPage(browser);
  await open(page, '#/');
  const destinations = [
    ['Administration', /^Administration$/],
    ['Settings', /^Settings$/],
    ['Investigations', /^Investigations$/],
  ];
  for (const [name, heading] of destinations) {
    await primary(page).getByRole('link', { name, exact: true }).click();
    await expect(page.locator('main h1').first(), `${name} did not open`).toHaveText(heading, { timeout: 15000 });
    await expect(primary(page).getByRole('link', { name, exact: true }), `${name} is not marked current`).toHaveAttribute('aria-current', 'page');
    for (const [other] of destinations.filter(([n]) => n !== name)) {
      await expect(primary(page).getByRole('link', { name: other, exact: true })).not.toHaveAttribute('aria-current', 'page');
    }
    await expect(page, `${name}: the page title is wrong`).toHaveTitle(new RegExp(`^${name} · Causa$`));
  }
  await page.goBack();
  await expect(page.locator('main h1').first(), 'Back did not restore Settings').toHaveText(/^Settings$/, { timeout: 15000 });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('button', { name: /^menu$/i }).or(page.locator('summary').filter({ hasText: /^\s*Menu\s*$/ })).first(), 'the phone width shows no Menu').toBeVisible();
  noProblems(page);
});

test('3. The connection state', async ({ browser }) => {
  const page = await freshPage(browser);
  await open(page, '#/');
  const mode = await connectionMode(page);
  if (mode === 'connected') {
    const text = (await connectedText(page).innerText()).replace(/\s+/g, ' ').trim();
    expect(text, 'the connection line does not name owner/repo@branch (test deployment)').toMatch(/^Connected to \S+\/\S+@\S+ \(test deployment\)/);
    await expect(cards(page).or(page.getByText(/no investigations/i)).first(), 'connected, but neither investigations nor an empty list are shown').toBeVisible({ timeout: 30000 });
    note(`${text}; ${await cards(page).count()} investigations listed`);
  } else {
    if (await hasDeployConnection(page)) throw new Error(`the site has a deploy connection but shows its Connect form: ${(await connectionProblem(page)) || 'no reason shown'}`);
    for (const name of ['Repository owner', 'Repository', 'Branch', 'Access token']) await expect(field(page, name), `the Connect form has no ${name}`).toBeVisible();
    await expect(connectButton(page)).toBeEnabled();
    note('no deploy key: the Connect form is shown');
  }
  noProblems(page);
});

test('4. The investigation list opens a workspace', async ({ browser }) => {
  const page = await freshPage(browser);
  await ensureConnected(page);
  await expect(cards(page).first(), 'no investigation is listed').toBeVisible({ timeout: 30000 });
  const count = await cards(page).count();
  for (let i = 0; i < count; i++) await expect(cards(page).nth(i).getByRole('link').first(), `card ${i + 1} has no title link`).toBeVisible();
  const link = cards(page).first().getByRole('link').first();
  const title = (await link.innerText()).trim();
  await link.click();
  await expect(page, 'the title link did not open a workspace address').toHaveURL(/#\/investigations\/[0-9a-f-]{36}/i, { timeout: 30000 });
  await failureIfShown(page, 'the workspace');
  await expect(page.locator('main h1').first(), 'the workspace has no heading').toBeVisible({ timeout: 30000 });
  noProblems(page);
  note(`${count} listed; opened "${title}"`);
});

test('5. New investigation: form, validation and Cancel', async ({ browser }) => {
  const page = await freshPage(browser);
  await ensureConnected(page);
  await expect(newInvestigation(page), 'no New investigation action').toBeVisible({ timeout: 30000 });
  await newInvestigation(page).click();
  await expect(page.getByRole('heading', { name: 'New investigation', level: 1 })).toBeVisible({ timeout: 20000 });
  for (const name of ['Problem statement', 'Title', 'Primary investigative question', 'Investigation identifier']) await expect(field(page, name), `no ${name} field`).toBeVisible();
  await expect(field(page, 'Investigation identifier'), 'the identifier is not pre-filled').toHaveValue(/^[0-9a-f-]{36}$/i);
  // An empty problem statement and an over-long title are refused with messages; nothing starts.
  await field(page, 'Title').fill('t'.repeat(201));
  await page.getByRole('button', { name: 'Start investigation', exact: true }).click();
  const alert = page.getByRole('alert').first();
  await expect(alert, 'no validation summary').toBeVisible({ timeout: 10000 });
  await expect(alert).toContainText(/problem statement/i);
  await expect(alert).toContainText(/200/);
  await expect(page.getByRole('heading', { name: 'New investigation', level: 1 }), 'an invalid form left the start screen').toBeVisible();
  // Cancel leaves without creating anything.
  const title = marker('cancelled');
  await field(page, 'Problem statement').fill(marker('cancelled-problem'));
  await field(page, 'Title').fill(title);
  await page.getByRole('button', { name: /cancel/i }).first().click();
  await expect(page.locator('main h1').first(), 'Cancel did not return to Investigations').toHaveText(/^Investigations$/, { timeout: 20000 });
  await expect(page.getByRole('link', { name: title, exact: true }), 'Cancel created an investigation').toHaveCount(0);
  noProblems(page);
});

test('6. Start, Save, and reopen in a fresh context', async ({ browser }) => {
  const page = await freshPage(browser);
  await ensureConnected(page);
  const v = { problem: marker('problem'), title: marker('title'), question: marker('question') };
  await newInvestigation(page).click();
  await field(page, 'Problem statement').fill(v.problem);
  await field(page, 'Title').fill(v.title);
  await field(page, 'Primary investigative question').fill(v.question);
  const id = await field(page, 'Investigation identifier').inputValue();
  await page.getByRole('button', { name: 'Start investigation', exact: true }).click();
  await expect(page.getByText(v.problem).first(), 'Start did not open the new investigation').toBeVisible({ timeout: 30000 });
  await expect(page, 'the workspace address does not carry the identifier').toHaveURL(new RegExp(id));
  const status = page.getByRole('status', { name: /save state/i }).first();
  await expect(status, 'Saved shown before saving').not.toHaveText(/^\s*Saved\s*$/);
  const save = page.getByRole('button', { name: 'Save', exact: true });
  await expect(save, 'no Save button').toBeVisible();
  if (!(await save.isEnabled())) {
    const why = await status.evaluate((el) => (el.getAttribute('aria-describedby') || '').split(/\s+/).map((i) => document.getElementById(i)?.textContent || '').join(' ').replace(/\s+/g, ' ').trim());
    throw new Error(`Save is disabled: ${why || 'no reason shown'}`);
  }
  await save.click();
  try {
    await expect(status).toHaveText(/^\s*Saved\s*$/, { timeout: 60000 });
  } catch {
    const why = await status.evaluate((el) => (el.getAttribute('aria-describedby') || '').split(/\s+/).map((i) => document.getElementById(i)?.textContent || '').join(' ').replace(/\s+/g, ' ').trim());
    throw new Error(`Save did not reach Saved within 60 s: the status is "${(await status.innerText()).trim()}"${why ? ` (${why})` : ''}`);
  }
  const fresh = await freshPage(browser);
  await ensureConnected(fresh);
  await expect(fresh.getByRole('link', { name: v.title, exact: true }), 'the saved investigation is not listed in a fresh context').toBeVisible({ timeout: 30000 });
  await fresh.getByRole('link', { name: v.title, exact: true }).click();
  for (const value of [v.title, v.problem, v.question]) await expect(fresh.getByText(value).first(), `${value} was not shown after reopening`).toBeVisible({ timeout: 30000 });
  noProblems(page);
  noProblems(fresh);
  note(`saved "${v.title}" (${id})`);
});

test('7. A catalog investigation renders', async ({ browser }) => {
  const page = await freshPage(browser);
  await ensureConnected(page);
  const card = cards(page).filter({ hasText: CATALOG }).first();
  await expect(card, `no investigation matching ${CATALOG} is listed`).toBeVisible({ timeout: 30000 });
  const link = card.getByRole('link').first();
  const title = (await link.innerText()).trim();
  await link.click();
  await failureIfShown(page, 'the investigation');
  await expect(page.locator('main h1').first(), 'the investigation has no heading').toBeVisible({ timeout: 30000 });
  await expect(page.getByText(title).first(), 'the investigation title is not shown').toBeVisible();
  const words = (await page.locator('main').innerText()).split(/\s+/).length;
  expect(words, 'the investigation shows almost no content').toBeGreaterThan(20);
  await page.waitForTimeout(2000);
  noProblems(page);
  note(`opened "${title}"`);
});

test('8. The connection status never shows any part of a GitHub token', async ({ browser }) => {
  const page = await freshPage(browser);
  await open(page, '#/');
  const mode = await connectionMode(page).catch(() => 'unknown');
  // What the user can read about the connection: the connection line or the Connect region with its state and errors.
  const statusText = mode === 'connected'
    ? await connectedText(page).innerText()
    : await page.getByRole('region', { name: /connect data repository/i }).or(page.locator('main')).first().innerText();
  const tokenShapes = /\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{4,}|github_pat_[A-Za-z0-9_]{4,}/;
  expect(statusText, 'the connection status shows a GitHub token').not.toMatch(tokenShapes);
  // The deployed key itself (public in deploy-connection.json by design) appears nowhere in the page, its address, its
  // title or web storage, not even a fragment of eight or more characters.
  const deployed = await page.evaluate(async () => {
    try {
      const r = await fetch(new URL('deploy-connection.json', location.href), { cache: 'no-store' });
      return r.ok ? (await r.json())?.connection?.key || '' : '';
    } catch {
      return '';
    }
  });
  const seen = await page.evaluate(() => [document.documentElement.outerHTML, location.href, document.title, ...Object.values(localStorage), ...Object.values(sessionStorage)].join('\n'));
  expect(seen, 'the page carries a GitHub token').not.toMatch(tokenShapes);
  if (deployed) {
    const fragments = [];
    for (let i = 0; i + 8 <= deployed.length; i += 4) fragments.push(deployed.slice(i, i + 8));
    const leaked = fragments.filter((f) => seen.includes(f));
    expect(leaked.length, `${leaked.length} fragments of the deploy key appear in the page`).toBe(0);
    note(`deploy key present; none of its ${fragments.length} fragments shown (mode: ${mode})`);
  } else note(`no deploy key in this build (mode: ${mode})`);
});
