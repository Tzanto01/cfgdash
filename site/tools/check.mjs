import http from 'node:http';
import os from 'node:os';
import { createRequire } from 'node:module';
import { mkdirSync, readFileSync, readdirSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../..');
const pub = path.join(repo, 'site/public');
const out = path.resolve(process.argv[2] ?? path.join(os.tmpdir(), 'cfgdash-site-check'));
mkdirSync(out, { recursive: true });
const { chromium } = createRequire(path.join(repo, 'web/package.json'))('playwright-core');

const CSP = "default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.webp': 'image/webp',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
  '.xml': 'application/xml'
};

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const rel = url.pathname.endsWith('/') ? `${url.pathname}index.html` : url.pathname;
  const file = path.join(pub, path.normalize(decodeURIComponent(rel)));
  const headers = { 'Content-Security-Policy': CSP, 'X-Content-Type-Options': 'nosniff' };
  try {
    if (!file.startsWith(pub)) {
      throw new Error('outside');
    }
    const body = await readFile(file);
    res.writeHead(200, { ...headers, 'Content-Type': TYPES[path.extname(file)] ?? 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404, { ...headers, 'Content-Type': TYPES['.html'] });
    res.end(await readFile(path.join(pub, '404.html')));
  }
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;

let passed = 0;
const failures = [];
const check = (name, ok, detail = '') => {
  if (ok) {
    passed++;
  } else {
    failures.push(`${name}${detail ? `: ${detail}` : ''}`);
  }
};

const htmlFiles = readdirSync(pub).filter((f) => f.endsWith('.html'));
for (const f of htmlFiles) {
  const html = readFileSync(path.join(pub, f), 'utf8');
  check(`${f}: no style attribute`, !/\sstyle\s*=/i.test(html));
  check(`${f}: no style element`, !/<style[\s>]/i.test(html));
  check(`${f}: no inline script`, [...html.matchAll(/<script\b([^>]*)>/gi)].every((m) => /\bsrc=/.test(m[1]) || /type="application\/ld\+json"/.test(m[1])));
  check(`${f}: no inline handler`, !/\son[a-z]+\s*=/i.test(html));
  check(`${f}: no http:// asset`, !/\b(?:src|href|srcset)="http:\/\//i.test(html));
  check(`${f}: no em or en dash`, !/[–—]/.test(html));
  check(`${f}: no spaced hyphen`, !/ - /.test(html));
}

const htaccess = readFileSync(path.join(pub, '.htaccess'), 'utf8');
const shippedCsp = /Header always set Content-Security-Policy "([^"]*)"/.exec(htaccess)?.[1];
check('.htaccess: shipped CSP equals the tested CSP', shippedCsp === CSP, shippedCsp ?? 'missing');

const watch = (page) => {
  const problems = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') {
      problems.push(`console: ${msg.text()}`);
    }
  });
  page.on('pageerror', (error) => problems.push(`pageerror: ${error.message}`));
  page.on('request', (request) => {
    const url = request.url();
    if (!url.startsWith(base) && !url.startsWith('data:')) {
      problems.push(`foreign request: ${url}`);
    }
  });
  return problems;
};

const pageChecks = async (page, label) => {
  const result = await page.evaluate(() => ({
    scroll: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    badImgs: [...document.images, ...document.querySelectorAll('picture source')]
      .filter((el) => (el.tagName === 'IMG' && !el.hasAttribute('alt')) || !el.getAttribute('width') || !el.getAttribute('height'))
      .map((el) => el.src || el.srcset),
    dupIds: [...document.querySelectorAll('[id]')].map((el) => el.id).filter((id, i, all) => all.indexOf(id) !== i),
    deadRefs: [...document.querySelectorAll('[aria-controls], [aria-labelledby], [aria-describedby]')]
      .flatMap((el) => ['aria-controls', 'aria-labelledby', 'aria-describedby'].flatMap((a) => (el.getAttribute(a) ?? '').split(/\s+/).filter(Boolean)))
      .filter((id) => !document.getElementById(id)),
    deadLinks: [...document.querySelectorAll('a[href^="#"]')].map((a) => a.getAttribute('href')).filter((href) => href.length < 2 || !document.getElementById(href.slice(1))),
    csp: window.cspViolations
  }));
  check(`${label}: no horizontal scroll`, result.scroll <= 0, `${result.scroll}px`);
  check(`${label}: images have alt, width, height`, !result.badImgs.length, result.badImgs.join(', '));
  check(`${label}: in-page links have targets`, !result.deadLinks.length, result.deadLinks.join(', '));
  check(`${label}: no CSP violation`, !result.csp.length, result.csp.join(', '));
  check(`${label}: ids are unique`, !result.dupIds.length, result.dupIds.join(', '));
  check(`${label}: ARIA references have targets`, !result.deadRefs.length, result.deadRefs.join(', '));
};

const demoLayout = (page) =>
  page.evaluate(() => {
    const textBox = (el) => {
      const box = { left: Infinity, top: Infinity, right: -Infinity, bottom: -Infinity };
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
      while (walker.nextNode()) {
        const node = walker.currentNode;
        if (!node.textContent.trim() || node.parentElement.closest('.sr-only')) {
          continue;
        }
        const range = document.createRange();
        range.selectNodeContents(node);
        const r = range.getBoundingClientRect();
        box.left = Math.min(box.left, r.left);
        box.top = Math.min(box.top, r.top);
        box.right = Math.max(box.right, r.right);
        box.bottom = Math.max(box.bottom, r.bottom);
      }
      return box;
    };
    const overlaps = [];
    for (const li of document.querySelectorAll('.contribs li')) {
      const a = textBox(li.querySelector('.c-val'));
      const b = textBox(li.querySelector('.status'));
      if (a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom) {
        overlaps.push(li.textContent.trim().slice(0, 40));
      }
    }
    const clipped = [];
    const card = document.querySelector('.demo .card').getBoundingClientRect();
    for (const el of document.querySelectorAll('.demo-body *')) {
      const style = getComputedStyle(el);
      if (!el.closest('.sr-only') && style.overflowX !== 'visible' && el.scrollWidth > el.clientWidth + 1) {
        clipped.push(`${el.className}: overflow`);
      }
      if (!el.children.length && el.textContent.trim() && el.checkVisibility()) {
        const r = textBox(el);
        if (el.closest('.card') && (r.right > card.right + 0.5 || r.left < card.left - 0.5)) {
          clipped.push(`${el.textContent.trim().slice(0, 30)}: outside card`);
        }
      }
    }
    return { overlaps, clipped };
  });

const fullShot = async (page, file) => {
  await page.evaluate(async () => {
    for (let y = 0; y < document.body.scrollHeight; y += 600) {
      window.scrollTo(0, y);
      await new Promise((r) => setTimeout(r, 30));
    }
    window.scrollTo(0, 0);
    await Promise.all([...document.images].filter((img) => img.checkVisibility()).map((img) => img.decode().catch(() => {})));
    await document.fonts.ready;
  });
  await page.screenshot({ path: path.join(out, file), fullPage: true });
};

const browser = await chromium.launch();
const newContext = (options) =>
  browser.newContext(options).then(async (context) => {
    await context.addInitScript(() => {
      window.cspViolations = [];
      document.addEventListener('securitypolicyviolation', (e) => window.cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
    });
    return context;
  });

try {
  for (const width of [320, 375, 768, 1280]) {
    for (const theme of ['light', 'dark']) {
      const context = await newContext({ viewport: { width, height: 900 }, colorScheme: theme });
      const page = await context.newPage();
      const problems = watch(page);
      await page.goto(`${base}/`);
      await page.evaluate(() => document.fonts.ready);
      await pageChecks(page, `${width} ${theme}`);
      await fullShot(page, `page-${width}-${theme}.png`);
      check(`${width} ${theme}: no console error or foreign request`, !problems.length, problems.join('; '));
      await context.close();
    }
  }

  for (const width of [320, 360, 375]) {
    const context = await newContext({ viewport: { width, height: 900 } });
    const page = await context.newPage();
    await page.goto(`${base}/`);
    await page.evaluate(() => document.fonts.ready);
    const collapsed = page.locator('.kv-btn[aria-expanded="false"]');
    while (await collapsed.count()) {
      await collapsed.first().click();
    }
    check(`${width} expanded: all rows open`, (await page.locator('.kv-panel:visible').count()) === 5);
    const layout = await demoLayout(page);
    check(`${width} expanded: values clear of status words`, !layout.overlaps.length, layout.overlaps.join('; '));
    check(`${width} expanded: no demo text clipped`, !layout.clipped.length, layout.clipped.join('; '));
    if (width === 320) {
      await fullShot(page, 'page-320-expanded.png');
    }
    await context.close();
  }

  {
    const context = await newContext({ viewport: { width: 375, height: 900 } });
    let release;
    const gate = new Promise((resolve) => (release = resolve));
    await context.route('**/assets/site.js', async (route) => {
      await gate;
      await route.continue();
    });
    const page = await context.newPage();
    await page.goto(`${base}/`, { waitUntil: 'domcontentloaded' }).catch(() => {});
    await page.locator('.kv-panel').first().waitFor();
    check('site.js pending: only the first row panel shows', (await page.locator('.kv-panel:visible').count()) === 1);
    release();
    await page.waitForFunction(() => document.documentElement.classList.contains('js-ready'));
    check('site.js ready: first row open, others collapsed', (await page.locator('.kv-panel:visible').count()) === 1 && (await page.locator('.kv-btn[aria-expanded="true"]').count()) === 1);
    await context.close();
  }

  {
    const context = await newContext({ viewport: { width: 375, height: 900 } });
    await context.route('**/assets/site.js', (route) => route.abort());
    const page = await context.newPage();
    await page.goto(`${base}/`, { waitUntil: 'load' });
    await page.waitForFunction(() => !document.documentElement.classList.contains('js'));
    check('site.js blocked: all view panels visible', (await page.locator('[role="tabpanel"]:visible').count()) === 5);
    check('site.js blocked: all row panels visible', (await page.locator('.kv-panel:visible').count()) === 5);
    check('site.js blocked: nav links visible', await page.locator('#site-nav a').first().isVisible());
    await context.close();
  }

  {
    const context = await newContext({ viewport: { width: 1280, height: 900 }, colorScheme: 'light' });
    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: base });
    const page = await context.newPage();
    const problems = watch(page);
    await page.goto(`${base}/`);
    const visibleRows = () => page.locator('.kv:visible').count();
    const count = () => page.locator('.count').textContent();

    const mode = page.getByRole('button', { name: 'permissions.defaultMode' });
    check('rows: second row starts collapsed', !(await page.locator('#kv-mode').isVisible()));
    await mode.click();
    check('rows: click expands', (await mode.getAttribute('aria-expanded')) === 'true' && (await page.locator('#kv-mode').isVisible()));
    await mode.press('Enter');
    check('rows: Enter collapses', (await mode.getAttribute('aria-expanded')) === 'false' && !(await page.locator('#kv-mode').isVisible()));

    const overridden = page.getByRole('button', { name: 'Overridden' });
    await overridden.click();
    check('chips: Overridden shows 2 rows', (await visibleRows()) === 2 && (await count()) === '2 of 5 rows', `${await visibleRows()} "${await count()}"`);
    check('chips: Overridden pressed', (await overridden.getAttribute('aria-pressed')) === 'true' && (await page.getByRole('button', { name: 'All', exact: true }).getAttribute('aria-pressed')) === 'false');
    await page.getByRole('button', { name: 'Redacted' }).click();
    check('chips: Redacted shows 1 row', (await visibleRows()) === 1 && (await count()) === '1 of 5 rows');
    await page.getByRole('button', { name: 'Redacted' }).click();
    check('chips: pressing active chip returns to All', (await visibleRows()) === 5 && (await count()) === '' && (await page.getByRole('button', { name: 'All', exact: true }).getAttribute('aria-pressed')) === 'true');

    const filter = page.getByRole('searchbox', { name: 'Filter rows' });
    await filter.fill('permissions');
    check('filter: matches keys', (await visibleRows()) === 3 && (await count()) === '3 of 5 rows', `${await visibleRows()} "${await count()}"`);
    await filter.fill('zzz');
    check('filter: empty state', (await visibleRows()) === 0 && (await count()) === 'No rows match' && (await page.locator('.kv-none').isVisible()));
    await filter.fill('');
    check('filter: cleared', (await visibleRows()) === 5 && (await count()) === '');

    const tab = (name) => page.getByRole('tab', { name, exact: true });
    await tab('Overview').focus();
    await page.keyboard.press('ArrowRight');
    check('tabs: ArrowRight selects Settings', (await tab('Settings').getAttribute('aria-selected')) === 'true' && (await page.locator('#panel-settings').isVisible()) && !(await page.locator('#panel-overview').isVisible()));
    check('tabs: focus follows', await tab('Settings').evaluate((el) => el === document.activeElement));
    await page.keyboard.press('End');
    check('tabs: End selects Usage', (await tab('Usage').getAttribute('aria-selected')) === 'true');
    await page.keyboard.press('ArrowRight');
    check('tabs: ArrowRight wraps to Overview', (await tab('Overview').getAttribute('aria-selected')) === 'true');
    await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('Home');
    check('tabs: Home selects Overview', (await tab('Overview').getAttribute('aria-selected')) === 'true' && (await tab('Overview').getAttribute('tabindex')) === '0' && (await tab('Usage').getAttribute('tabindex')) === '-1');

    await page.getByRole('button', { name: 'Copy cfgdash doctor' }).click();
    await page.waitForFunction(() => document.getElementById('copy-status').textContent === 'Copied');
    check('copy: clipboard holds the command', (await page.evaluate(() => navigator.clipboard.readText())) === 'cfgdash doctor');

    const toggle = page.locator('.theme-btn');
    check('theme: starts on system', (await toggle.getAttribute('aria-label')) === 'Theme: system. Switch to light');
    await toggle.click();
    await page.reload();
    check('theme: light survives reload', (await page.evaluate(() => document.documentElement.dataset.theme)) === 'light' && (await toggle.getAttribute('aria-label')) === 'Theme: light. Switch to dark');
    await toggle.click();
    await page.reload();
    check('theme: dark survives reload', (await page.evaluate(() => document.documentElement.dataset.theme)) === 'dark');
    await toggle.click();
    await page.reload();
    check('theme: back to system', await page.evaluate(() => !document.documentElement.hasAttribute('data-theme') && localStorage.length === 0));
    check('interactions: no console error', !problems.length, problems.join('; '));
    await context.close();
  }

  {
    const context = await newContext({ viewport: { width: 375, height: 800 }, colorScheme: 'dark', hasTouch: true, isMobile: true });
    const page = await context.newPage();
    const problems = watch(page);
    await page.goto(`${base}/`);
    const menu = page.getByRole('button', { name: 'Menu' });
    const nav = page.locator('#site-nav');
    check('menu: nav hidden at 375', !(await nav.isVisible()));
    await menu.click();
    check('menu: opens', (await menu.getAttribute('aria-expanded')) === 'true' && (await nav.isVisible()));
    await nav.getByRole('link', { name: 'Security' }).focus();
    await page.keyboard.press('Escape');
    check('menu: Escape closes and returns focus', !(await nav.isVisible()) && (await menu.evaluate((el) => el === document.activeElement)));
    await menu.click();
    await nav.getByRole('link', { name: 'FAQ' }).click();
    check('menu: link closes it', !(await nav.isVisible()) && (await menu.getAttribute('aria-expanded')) === 'false');
    await page.screenshot({ path: path.join(out, 'menu-375.png') });
    check('menu: no console error', !problems.length, problems.join('; '));
    await context.close();
  }

  {
    const context = await newContext({ viewport: { width: 320, height: 800 }, javaScriptEnabled: false });
    const page = await context.newPage();
    await page.goto(`${base}/`);
    check('no JS: nav links visible', await page.locator('#site-nav a').first().isVisible());
    check('no JS: all view panels visible', (await page.locator('[role="tabpanel"]:visible').count()) === 5);
    check('no JS: all row panels visible', (await page.locator('.kv-panel:visible').count()) === 5);
    check('no JS: every row says expanded', (await page.locator('.kv-btn[aria-expanded="true"]').count()) === 5);
    const shot = await page.screenshot({ path: path.join(out, 'page-320-nojs.png'), fullPage: true });
    check('no JS: no horizontal scroll', shot.readUInt32BE(16) === 320, `${shot.readUInt32BE(16)}px wide`);
    await context.close();
  }

  {
    const context = await newContext({ viewport: { width: 375, height: 800 } });
    const page = await context.newPage();
    const problems = watch(page);
    const response = await page.goto(`${base}/missing/page`);
    check('404: status and heading', response.status() === 404 && (await page.locator('h1').textContent()) === "This page doesn't exist");
    await pageChecks(page, '404');
    const other = problems.filter((p) => !p.includes('status of 404'));
    check('404: no console error besides its own status', !other.length, other.join('; '));
    await context.close();
  }
} finally {
  await browser.close();
  server.close();
}

console.log(`screenshots in ${out}`);
console.log(`${passed} passed, ${failures.length} failed`);
for (const failure of failures) {
  console.log(`FAIL ${failure}`);
}
process.exit(failures.length ? 1 : 0);
