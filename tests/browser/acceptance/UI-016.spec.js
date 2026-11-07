// Owner: ui (T16). UI-016: a deliberate, visibly coloured Causa identity on semantic theme tokens, with WCAG AA
// contrast, non-colour status cues, the same token semantics on every surface, grayscale-legible print, and the
// evidence the requirement asks for (screenshots at every target size, token and contrast checks, focus and
// reduced-motion checks). Colour is judged from what the browser actually paints.
import { test, expect } from '@playwright/test';
import * as kit from '../support/layout-assertions.mjs';

// ---- shared prelude (each ui spec carries its own copy: tests/browser/support is integration's) -------------------
// The built application (npm run build writes dist/). CAUSA_APP_PATH points elsewhere, e.g. a deployed URL.
const APP = process.env.CAUSA_APP_PATH || '/dist/index.html';
// The repository the deterministic suite connects to (the stateful fake behind the test build), as the journeys do.
const REPO = {
  owner: process.env.CAUSA_TEST_OWNER || 'harness',
  repo: process.env.CAUSA_TEST_REPO || 'causa-test',
  branch: process.env.CAUSA_TEST_BRANCH || 'main',
  token: process.env.CAUSA_WRITE_TOKEN || 'harness-token',
};
// The layout contract's viewports (forma-layout-contract.json).
const VP = {
  'VP-320': { width: 320, height: 640 },
  'VP-390': { width: 390, height: 844 },
  'VP-IPAD-PORTRAIT': { width: 820, height: 1180 },
  'VP-IPAD-LANDSCAPE': { width: 1180, height: 820 },
  'VP-DESKTOP': { width: 1440, height: 900 },
};
const DESTINATIONS = [
  { name: 'Investigations', hash: '#/', heading: /^Investigations$/ },
  { name: 'Administration', hash: '#/administration', heading: /^Administration$/ },
  { name: 'Settings', hash: '#/settings', heading: /^Settings$/ },
];
// The engine has rendered its first view: the loading shell is gone and the screen's heading is shown.
const open = async (page, hash = '#/') => {
  // A fresh document every time: a hash-only change would keep the previous page's focus and marks.
  await page.goto('about:blank');
  await page.goto(APP + hash);
  await expect(page.locator('#causa-main'), 'the engine did not render its first view').toBeVisible({ timeout: 30000 });
  await expect(page.locator('main h1').first()).toBeVisible({ timeout: 15000 });
};
const primary = (page) => page.getByRole('navigation', { name: 'Primary', exact: true }).filter({ visible: true }).first();
const sections = (page) => page.getByRole('navigation', { name: /investigation sections/i }).first();
const none = (label, violations) => expect(violations, `${label}: ${JSON.stringify(violations).slice(0, 1500)}`).toEqual([]);
const connect = async (page) => {
  await open(page, '#/');
  await page.getByLabel('Repository owner', { exact: true }).fill(REPO.owner);
  await page.getByLabel('Repository', { exact: true }).fill(REPO.repo);
  await page.getByLabel('Branch', { exact: true }).fill(REPO.branch);
  await page.getByLabel('Access token', { exact: true }).fill(REPO.token);
  await page.getByRole('button', { name: 'Connect data repository', exact: true }).click();
  await expect(page.getByText(/^Connected to/).first(), 'Connect did not succeed against the test repository').toBeVisible({ timeout: 20000 });
};
// Opens the first investigation of the given copy kind ("Demonstration" or "Working copy") from the list.
const openInvestigation = async (page, tag) => {
  const cards = page.locator('[data-requirement-id="INV-039"] article');
  await expect(cards.first(), 'no investigation is listed in the test repository').toBeVisible({ timeout: 20000 });
  const card = tag ? cards.filter({ hasText: tag }).first() : cards.first();
  await expect(card, `no ${tag} investigation is listed`).toBeVisible();
  const link = card.getByRole('link').first();
  const title = (await link.innerText()).trim();
  await link.click();
  await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible({ timeout: 20000 });
  return title;
};
// ---------------------------------------------------------------------------------------------------------------------
// Each obligation test walks several screens and viewports.
test.describe.configure({ timeout: 300_000 });

// UI-016's canonical identity colours. Amber is drawn with Forma's --ef-color-status-warning (ODR-V41-05).
const PALETTE = {
  navy: [0x14, 0x28, 0x3f],
  teal: [0x08, 0x7f, 0x8c],
  amber: [0xe9, 0xa2, 0x3b],
  coral: [0xd6, 0x6b, 0x58],
  ivory: [0xf5, 0xf3, 0xed],
};
// A painted colour counts as an identity colour within this distance (sRGB units). Derived shades of the same
// colour (UI-016-O10: hover, strong, border) stay within it; another hue does not.
const NEAR = 48;
const dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]);

// Every opaque colour the page paints (backgrounds, text, borders, outlines) on visible elements, as sRGB triples.
const painted = (page) => page.evaluate(() => {
  const c = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
  const rgb = (v) => { c.clearRect(0, 0, 1, 1); c.fillStyle = '#0000'; c.fillStyle = v; c.fillRect(0, 0, 1, 1); const d = c.getImageData(0, 0, 1, 1).data; return d[3] > 200 ? [d[0], d[1], d[2]] : null; };
  const out = [];
  for (const e of [document.documentElement, document.body, ...document.body.querySelectorAll('*')]) {
    const r = e.getBoundingClientRect();
    if (e !== document.documentElement && e !== document.body && (r.width < 1 || r.height < 1)) continue;
    const s = getComputedStyle(e);
    if (s.visibility === 'hidden' || s.display === 'none') continue;
    const add = (v, role) => { const x = rgb(v); if (x) out.push({ rgb: x, role, tag: e.tagName.toLowerCase(), cls: String(e.className || '').slice(0, 60) }); };
    add(s.backgroundColor, 'background');
    if ((e.textContent || '').trim()) add(s.color, 'text');
    for (const m of (s.boxShadow === 'none' ? [] : s.boxShadow.match(/rgba?\([^)]*\)/g) || [])) add(m, 'indicator');
    for (const side of ['Top', 'Right', 'Bottom', 'Left']) if (parseFloat(s[`border${side}Width`]) > 0 && s[`border${side}Style`] !== 'none') add(s[`border${side}Color`], 'border');
  }
  return out;
});
// Forma's warning-status colour in the page's own theme.
const warningToken = (page) => page.evaluate(() => {
  const p = document.createElement('span');
  p.style.color = 'var(--ef-color-status-warning)';
  document.body.append(p);
  const m = getComputedStyle(p).color.match(/\d+(\.\d+)?/g).map(Number);
  p.remove();
  return m.slice(0, 3);
});
const chroma = ([r, g, b]) => (Math.max(r, g, b) - Math.min(r, g, b)) / 255;
const hue = ([r, g, b]) => {
  const mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
  if (!d) return 0;
  const h = mx === r ? ((g - b) / d) % 6 : mx === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return Math.round(((h * 60) + 360) % 360);
};
// The states the shell can reach without data, plus a refused connection (a critical state) and the populated list.
const collect = async (page, withData) => {
  const all = [];
  for (const hash of ['#/', '#/administration', '#/settings', '#/no-such-page']) { await open(page, hash); all.push(...await painted(page)); }
  if (withData) { await connect(page); all.push(...await painted(page)); await openInvestigation(page); all.push(...await painted(page)); }
  return all;
};
const present = (colours, target) => colours.some((c) => dist(c.rgb, target) <= NEAR);

test.describe('UI-016', () => {
  test('UI-016-O1', async ({ page }) => {
    // Visibly coloured, not black/grey/white: at least three distinct chromatic hues are painted on the shell.
    const colours = await collect(page, false);
    const hues = new Set(colours.filter((c) => chroma(c.rgb) >= 0.2).map((c) => Math.round(hue(c.rgb) / 30)));
    expect(hues.size, `chromatic hue families painted: ${[...hues]}`).toBeGreaterThanOrEqual(2);
    expect(colours.filter((c) => c.role === 'background' && chroma(c.rgb) >= 0.2).length, 'no coloured surface at all').toBeGreaterThan(0);
  });

  test('UI-016-O2', async ({ page }) => {
    // The five canonical palette values are in use: navy, teal, coral and ivory are painted; amber is the warning token.
    const colours = await collect(page, false);
    for (const name of ['navy', 'teal', 'ivory']) expect(present(colours, PALETTE[name]), `${name} is not painted`).toBe(true);
    const warn = await warningToken(page);
    expect(dist(warn, PALETTE.amber) <= NEAR || chroma(warn) > 0.3, `the warning token ${warn} is not amber`).toBe(true);
    const tokens = await page.evaluate(() => [...document.styleSheets].flatMap((s) => { try { return [...s.cssRules]; } catch { return []; } }).map((r) => r.cssText).join('\n'));
    for (const name of ['navy', 'teal', 'coral', 'ivory']) expect(tokens, `no theme token for ${name}`).toMatch(new RegExp(`--causa-${name}\\s*:`, 'i'));
  });

  test('UI-016-O3', async ({ page }) => {
    // Deep navy #14283F: primary navigation, masthead, structural emphasis.
    await open(page, '#/');
    const masthead = await page.getByRole('banner').evaluate((e) => getComputedStyle(e).backgroundColor);
    const m = masthead.match(/\d+/g).map(Number);
    expect(dist(m, PALETTE.navy), `masthead background ${masthead} is not deep navy`).toBeLessThanOrEqual(NEAR);
    const navText = await primary(page).getByRole('link').first().evaluate((e) => getComputedStyle(e).color);
    expect(chroma(navText.match(/\d+/g).map(Number)) < 0.3, 'primary navigation text is not legible on navy').toBe(true);
    none('masthead textContrast', await kit.textContrast(page));
  });

  test('UI-016-O4', async ({ page }) => {
    // Teal #087F8C: primary investigation actions, active selection, positive workflow emphasis.
    await open(page, '#/');
    const button = page.getByRole('button', { name: 'Connect data repository', exact: true });
    await expect(button).toBeVisible();
    const bg = (await button.evaluate((e) => getComputedStyle(e).backgroundColor)).match(/\d+/g).map(Number);
    expect(dist(bg, PALETTE.teal), `primary action ${bg} is not teal`).toBeLessThanOrEqual(NEAR);
    const current = primary(page).getByRole('link', { name: 'Investigations', exact: true });
    const marks = await current.evaluate((e) => { const s = getComputedStyle(e); return [s.borderBottomColor, s.textDecorationColor, s.backgroundColor, s.boxShadow, s.borderInlineStartColor]; });
    const tealMark = marks.some((v) => (v.match(/\d+/g) || []).length >= 3 && dist(v.match(/\d+/g).slice(0, 3).map(Number), PALETTE.teal) <= NEAR * 2);
    expect(tealMark, `the active selection is not marked with teal (${marks})`).toBe(true);
    none('textContrast', await kit.textContrast(page));
  });

  test('UI-016-O5', async ({ page }) => {
    // Amber: unresolved unknowns, pending review and attention, drawn with Forma's warning token and always labelled.
    await open(page, '#/');
    const warn = await warningToken(page);
    expect(chroma(warn), `the warning token ${warn} is not a colour`).toBeGreaterThan(0.3);
    expect(hue(warn) >= 20 && hue(warn) <= 50, `the warning token ${warn} is not in the amber hue range`).toBe(true);
    const css = await (await page.request.get(new URL('screens/investigation-list.css', page.url()).href)).text();
    expect(css).toMatch(/--ef-color-status-warning/);
    for (const hash of ['#/', '#/administration', '#/settings']) { await open(page, hash); none(`${hash} amberLabelled`, await kit.amberLabelled(page)); }
  });

  test('UI-016-O6', async ({ page }) => {
    // Coral #D66B58: contradictions, conflicts, critical alerts. The theme defines it and the critical state uses it.
    await open(page, '#/');
    const coral = await page.evaluate(() => {
      const p = document.createElement('span'); p.style.color = 'var(--causa-coral)'; document.body.append(p);
      const v = getComputedStyle(p).color; p.remove(); return v;
    });
    const c = coral.match(/\d+/g).map(Number).slice(0, 3);
    expect(dist(c, PALETTE.coral), `--causa-coral is ${coral}`).toBeLessThanOrEqual(NEAR);
    // A refused connection is a critical alert: shown in words, coloured from the coral family.
    await page.getByLabel('Repository owner', { exact: true }).fill('a');
    await page.getByLabel('Repository', { exact: true }).fill('b');
    await page.getByLabel('Branch', { exact: true }).fill('main');
    await page.getByLabel('Access token', { exact: true }).fill('not-a-valid-token');
    await page.route('https://api.github.com/**', (route) => route.fulfill({ status: 401, contentType: 'application/json', body: '{"message":"Bad credentials"}' }));
    await page.getByRole('button', { name: 'Connect data repository', exact: true }).click();
    const alert = page.locator('main').getByRole('alert').filter({ hasText: /refused/i }).first();
    await expect(alert).toBeVisible({ timeout: 20000 });
    const col = (await alert.evaluate((e) => getComputedStyle(e).color)).match(/\d+/g).map(Number);
    expect(hue(col) <= 20 || hue(col) >= 340, `the critical alert colour ${col} is not coral/red`).toBe(true);
    none('textContrast', await kit.textContrast(page));
  });

  test('UI-016-O7', async ({ page }) => {
    // Soft ivory #F5F3ED: the main workspace surface and light backgrounds.
    for (const hash of ['#/', '#/administration', '#/settings']) {
      await open(page, hash);
      const bg = await page.evaluate(() => {
        for (const e of [document.querySelector('main'), document.querySelector('.ef-site'), document.body, document.documentElement]) {
          const v = e && getComputedStyle(e).backgroundColor;
          if (v && !/rgba\(0, 0, 0, 0\)|transparent/.test(v)) return v;
        }
        return 'none';
      });
      const m = (bg.match(/\d+/g) || [0, 0, 0]).map(Number);
      expect(dist(m, PALETTE.ivory), `${hash}: workspace surface ${bg} is not soft ivory`).toBeLessThanOrEqual(NEAR / 2);
    }
  });

  test('UI-016-O8', async ({ page }) => {
    // Every colour comes from a named semantic token: the screen stylesheets and causa.css hold no colour literal, and
    // every --ef-* they read exists in Forma.
    await open(page, '#/');
    const sheets = await page.evaluate(() => [...document.querySelectorAll('link[rel=stylesheet]')].map((l) => l.href).filter((h) => !/vendor\/forma/.test(h)));
    expect(sheets.length).toBeGreaterThan(0);
    for (const href of sheets) {
      const css = (await (await page.request.get(href)).text()).replace(/\/\*[\s\S]*?\*\//g, '');
      if (/theme/i.test(new URL(href).pathname)) {
        // The theme file: literals only as custom-property values.
        const stray = css.split(/[;{}]/).filter((d) => /#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(/i.test(d) && !/^\s*--[\w-]+\s*:/.test(d));
        expect(stray, `${href}: colour literals outside custom-property definitions`).toEqual([]);
        continue;
      }
      const literal = css.replace(/var\(\s*--[\w-]+/g, 'var(').match(/(#[0-9a-f]{3,8}\b|rgba?\([^)]*\)|hsla?\([^)]*\)|(?<![\w-])(red|blue|green|orange|navy|teal|white|black|gray|grey)(?![\w-]))/gi) || [];
      expect(literal, `${href}: hard-coded colours`).toEqual([]);
    }
    // The inventory is the vendored Forma stylesheet's own custom properties.
    const inventory = await page.evaluate(async () => {
      const all = await (await fetch(new URL('vendor/forma/all.css', location.href))).text();
      return [...new Set(all.match(/--ef-[a-z0-9-]+/g))];
    });
    expect(inventory.length).toBeGreaterThan(100);
    none('onlyFormaTokens', await kit.onlyFormaTokens(page, { inventory, formaHref: 'vendor/forma/all.css' }));
  });

  test('UI-016-O9', async ({ page }) => {
    // Forma components for surfaces, controls, states and focus treatment.
    await open(page, '#/');
    expect(await page.locator('link[href*="forma"]').count(), 'Forma is not loaded').toBeGreaterThan(0);
    for (const sel of ['.ef-site-header', '.ef-button', '.ef-field', '.ef-form', '.ef-skip-link']) expect(await page.locator(sel).count(), `${sel} is not used`).toBeGreaterThan(0);
    await open(page, '#/administration');
    expect(await page.locator('.ef-key-value-list').count()).toBeGreaterThan(0);
    await open(page, '#/');
    none('focusVisible', await kit.focusVisible(page));
  });

  test('UI-016-O10', async ({ page }) => {
    // Derived tokens are measured: ivory text on teal fails 4.5:1 (4.27), so a stronger teal carries text; text on
    // the identity colours passes, in light and in forced-colours modes.
    await open(page, '#/');
    const button = page.getByRole('button', { name: 'Connect data repository', exact: true });
    const [fg, bg] = await button.evaluate((e) => [getComputedStyle(e).color, getComputedStyle(e).backgroundColor]);
    const lum = (s) => s.match(/\d+/g).slice(0, 3).map(Number).map((v) => v / 255).map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4)).reduce((a, v, k) => a + v * [0.2126, 0.7152, 0.0722][k], 0);
    const ratio = (a, b) => (Math.max(lum(a), lum(b)) + 0.05) / (Math.min(lum(a), lum(b)) + 0.05);
    expect(ratio(fg, bg), `primary action text ${fg} on ${bg}`).toBeGreaterThanOrEqual(4.5);
    none('textContrast', await kit.textContrast(page));
    none('nonTextContrast', await kit.nonTextContrast(page));
    // Status indicators (Administration's fact lozenges) stay distinct under forced colours.
    await open(page, '#/administration');
    none('administration textContrast', await kit.textContrast(page));
    none('forcedColorsDistinct', await kit.forcedColorsDistinct(page));
  });

  test('UI-016-O11', async ({ page }) => {
    // WCAG AA: 4.5:1 normal text, 3:1 large text, 3:1 relevant non-text indicators, at VP-320 and desktop.
    for (const id of ['VP-320', 'VP-DESKTOP']) {
      await page.setViewportSize(VP[id]);
      for (const hash of ['#/', '#/administration', '#/settings', '#/no-such-page']) {
        await open(page, hash);
        none(`${id} ${hash} textContrast`, await kit.textContrast(page));
        none(`${id} ${hash} nonTextContrast`, await kit.nonTextContrast(page));
      }
    }
  });

  test('UI-016-O12', async ({ page }) => {
    // Amber paired with a text label; amber as the only cue would need 3:1.
    for (const id of ['VP-320', 'VP-DESKTOP']) {
      await page.setViewportSize(VP[id]);
      for (const hash of ['#/', '#/administration', '#/settings']) { await open(page, hash); none(`${id} ${hash} amberLabelled`, await kit.amberLabelled(page)); }
    }
  });

  test('UI-016-O13', async ({ page }) => {
    // Status is never colour alone: every toned element carries text, the current page has a non-colour cue, and the
    // connection state is stated in words.
    for (const hash of ['#/', '#/administration', '#/settings']) {
      await open(page, hash);
      const silent = await page.evaluate(() => [...document.querySelectorAll('[data-tone], [data-state], .ef-status-lozenge, .ef-badge')]
        .filter((e) => e.offsetParent !== null && !(e.textContent || '').trim()).map((e) => e.outerHTML.slice(0, 100)));
      expect(silent, `${hash}: coloured status without text`).toEqual([]);
      none(`${hash} amberLabelled`, await kit.amberLabelled(page));
    }
    await open(page, '#/');
    await expect(page.getByText(/^Connection: Not connected$/)).toBeVisible();
    await page.emulateMedia({ forcedColors: 'active' });
    await expect(primary(page).getByRole('link', { name: 'Investigations', exact: true })).toHaveAttribute('aria-current', 'page');
    const cue = await page.evaluate(() => {
      const links = [...document.querySelectorAll('nav[aria-label="Primary"] a')].filter((a) => a.offsetParent);
      const cur = links.find((a) => a.getAttribute('aria-current') === 'page'), other = links.find((a) => a.getAttribute('aria-current') !== 'page');
      const s = getComputedStyle(cur), o = getComputedStyle(other);
      return ['textDecorationLine', 'fontWeight', 'borderBottomWidth', 'borderBottomStyle', 'borderInlineStartWidth', 'outlineStyle'].some((p) => s[p] !== o[p]);
    });
    expect(cue, 'the current page is told apart by colour alone').toBe(true);
  });

  test('UI-016-O14', async ({ page }) => {
    // The same token semantics across surfaces: the masthead, the primary action, the focus ring and the workspace
    // surface paint the same colours on every screen, the loading shell included.
    const sample = async () => page.evaluate(() => {
      const bg = (sel) => { const e = document.querySelector(sel); return e ? getComputedStyle(e).backgroundColor : null; };
      return { masthead: bg('header'), surface: getComputedStyle(document.body).backgroundColor };
    });
    const seen = [];
    for (const hash of ['#/', '#/administration', '#/settings', '#/no-such-page']) { await open(page, hash); seen.push(await sample()); }
    expect(new Set(seen.map((s) => s.masthead)).size, JSON.stringify(seen)).toBe(1);
    expect(new Set(seen.map((s) => s.surface)).size, JSON.stringify(seen)).toBe(1);
    const ring = [];
    for (const hash of ['#/', '#/settings']) {
      await open(page, hash);
      await page.keyboard.press('Tab');
      ring.push(await page.evaluate(() => getComputedStyle(document.activeElement).outlineColor));
    }
    expect(new Set(ring).size, `focus rings differ: ${ring}`).toBe(1);
    // The loading shell uses the same surface as the screens.
    const p2 = await page.context().newPage();
    await p2.route(/\/_framework\//, () => {});
    await p2.goto(APP, { waitUntil: 'domcontentloaded' });
    expect(await p2.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe(seen[0].surface);
    await p2.close();
  });

  test('UI-016-O15', async ({ page }) => {
    // Print stays legible in grayscale: under print media, text keeps 4.5:1 luminance contrast (luminance is what
    // survives grayscale), and no status relies on hue.
    await page.emulateMedia({ media: 'print' });
    for (const hash of ['#/', '#/administration', '#/settings']) {
      await open(page, hash);
      await page.addStyleTag({ content: 'html { filter: grayscale(1); }' });
      none(`print ${hash} textContrast`, await kit.textContrast(page));
      none(`print ${hash} amberLabelled`, await kit.amberLabelled(page));
    }
  });

  test('UI-016-O16', async ({ page }, info) => {
    // Evidence: screenshots of the exact build at desktop, iPad portrait and landscape and mobile, with token presence,
    // contrast, keyboard focus and reduced-motion checks.
    for (const id of ['VP-DESKTOP', 'VP-IPAD-PORTRAIT', 'VP-IPAD-LANDSCAPE', 'VP-390', 'VP-320']) {
      await page.setViewportSize(VP[id]);
      for (const hash of ['#/', '#/administration', '#/settings']) {
        await open(page, hash);
        const name = `${id}-${hash.replace(/[#/]/g, '') || 'investigations'}.png`;
        await info.attach(name, { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
        none(`${id} ${hash} textContrast`, await kit.textContrast(page));
      }
    }
    await page.setViewportSize(VP['VP-DESKTOP']);
    await open(page, '#/');
    const tokens = await page.evaluate(() => ['--ef-color-surface-primary', '--ef-color-status-warning', '--ef-color-focus-ring', '--causa-navy', '--causa-teal', '--causa-coral', '--causa-ivory']
      .filter((t) => !getComputedStyle(document.documentElement).getPropertyValue(t).trim()));
    expect(tokens, 'tokens not defined').toEqual([]);
    none('focusVisible', await kit.focusVisible(page));
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await open(page, '#/');
    none('reducedMotion', await kit.reducedMotion(page));
  });

  test('UI-016-O17', async ({ page }) => {
    // Navy, teal, amber, coral and ivory appear in appropriate populated states; no data does not force a status colour.
    await connect(page);
    const listed = await painted(page);
    for (const name of ['navy', 'teal', 'ivory']) expect(present(listed, PALETTE[name]), `${name} not painted on the populated list`).toBe(true);
    // Unknown counts are words in the ordinary text colour, never a warning or danger colour.
    const unknown = page.locator('dd').filter({ hasText: /^(Not known|Not available)$/ });
    for (let i = 0; i < await unknown.count(); i++) {
      const col = (await unknown.nth(i).evaluate((e) => getComputedStyle(e).color)).match(/\d+/g).map(Number);
      expect(chroma(col) < 0.25, `an absent value is drawn in a status colour ${col}`).toBe(true);
    }
    // An empty repository shows the empty state in neutral colours.
    await open(page, '#/');
    const neutral = await painted(page);
    expect(neutral.filter((c) => c.role === 'text' && dist(c.rgb, PALETTE.coral) <= NEAR).map((c) => c.tag), 'coral text with no critical state').toEqual([]);
  });

  test('UI-016-O18', async ({ page }) => {
    // A grayscale-only interface fails: chromatic colour is painted on structural surfaces and on the primary action.
    const colours = await collect(page, false);
    const chromaticBackgrounds = colours.filter((c) => c.role === 'background' && chroma(c.rgb) >= 0.15);
    expect(chromaticBackgrounds.length, 'only grey backgrounds').toBeGreaterThan(0);
    const share = colours.filter((c) => chroma(c.rgb) >= 0.15).length / colours.length;
    expect(share, `chromatic share of painted colours ${share.toFixed(3)}`).toBeGreaterThan(0.02);
  });
});
