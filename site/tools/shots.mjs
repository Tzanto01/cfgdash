import { createRequire } from 'node:module';
import { mkdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../..');
const { chromium } = createRequire(path.join(repo, 'web/package.json'))('playwright-core');

const base = process.env.MOCK_URL ?? 'http://127.0.0.1:4312';
const raw = path.resolve(process.argv[2] ?? path.join(os.tmpdir(), 'cfgdash-shots'));
const pub = path.join(repo, 'site/public');
const img = path.join(pub, 'assets/img');
mkdirSync(raw, { recursive: true });
mkdirSync(img, { recursive: true });

const views = [
  { name: 'overview', path: '/', ready: (p) => p.getByText('Hook script not found').first().waitFor() },
  {
    name: 'settings',
    path: '/settings',
    ready: async (p) => {
      const row = p.getByRole('button', { name: 'model', exact: true });
      await row.click();
      await p.locator('[aria-expanded="true"]').first().waitFor();
    }
  },
  { name: 'instructions', path: '/instructions', ready: (p) => p.getByText('tok est.').first().waitFor() },
  { name: 'health', path: '/health', ready: (p) => p.getByRole('cell', { name: 'Hook script not found' }).first().waitFor() },
  { name: 'usage', path: '/usage', ready: (p) => p.locator('svg[role="img"]').first().waitFor() }
];

const browser = await chromium.launch();
try {
  for (const theme of ['light', 'dark']) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 2, colorScheme: theme });
    await context.route('**/api/v1/snapshot*', async (route) => {
      const response = await route.fetch();
      const body = await response.json();
      body.machine.cfgdashVersion = '0.6.0';
      await route.fulfill({ response, json: body });
    });
    const page = await context.newPage();
    for (const view of views) {
      await page.goto(base + view.path);
      await view.ready(page);
      await page.evaluate(() => document.fonts.ready);
      await page.waitForFunction(() => !document.querySelector('[role="status"] .animate-spin, .animate-spin'));
      await page.mouse.move(1279, 799);
      await page.screenshot({ path: path.join(raw, `${view.name}-${theme}.png`) });
    }
    await context.close();
  }

  const render = async (template, width, height, out) => {
    const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1, colorScheme: 'dark' });
    await page.goto(pathToFileURL(path.join(here, template)).href);
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: path.join(pub, out) });
    await page.close();
  };
  await render('og.html', 1200, 630, 'og.png');
  await render('icon.html', 180, 180, 'apple-touch-icon.png');
} finally {
  await browser.close();
}

const convert = `
import sys, pathlib
from PIL import Image
raw, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
for png in sorted(raw.glob('*-*.png')):
    im = Image.open(png).convert('RGB')
    for w in (2560, 1280):
        target = im if im.width == w else im.resize((w, round(im.height * w / im.width)), Image.LANCZOS)
        dest = out / f'{png.stem}-{w}.webp'
        target.save(dest, 'WEBP', quality=82, method=6)
        print(dest.name, dest.stat().st_size)
`;
const result = spawnSync('python3', ['-I', '-c', convert, raw, img], { stdio: 'inherit' });
process.exit(result.status ?? 1);
