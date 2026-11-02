/**
 * Browser test of a published cfgdash exe (v0.5 plan section 5), run by CI after the Linux publish.
 *
 * Builds a synthetic harness in a temp dir, starts `cfgdash run` with HOME and the XDG dirs pointed at it, and drives
 * the UI with Chromium: walks every view in the nav, does one edit in the project scope (propose, apply, undo) and checks
 * the file after each step, asserts the CSP header and that no request leaves the cfgdash origin, then runs `cfgdash stop`.
 *
 * The token is read from run.json in this process, sent only as a request header and never printed.
 *
 * Usage: node e2e/browser.mjs <cfgdash exe> [artifacts dir]
 */
import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { access, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { chromium } from 'playwright-core';

const CSP = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
const exe = path.resolve(process.argv[2] ?? 'cfgdash');
const artifacts = path.resolve(process.argv[3] ?? 'e2e-artifacts');
let token = '';

const hide = (text) => (token ? String(text).split(token).join('[token]') : String(text));
const step = (text) => console.log(`- ${text}`);
const exists = (file) => access(file).then(() => true, () => false);

async function makeHarness() {
  const root = await mkdtemp(path.join(os.tmpdir(), 'cfgdash-e2e-'));
  const home = path.join(root, 'home');
  const claude = path.join(home, '.claude');
  const project = path.join(home, 'ws', 'app');
  await mkdir(path.join(claude, 'agents'), { recursive: true });
  await mkdir(project, { recursive: true });
  await writeFile(path.join(claude, 'settings.json'), `${JSON.stringify({ model: 'sonnet', permissions: { allow: ['Bash(ls)'] } }, null, 2)}\n`);
  await writeFile(path.join(claude, 'agents', 'writer.md'), '---\nname: writer\ndescription: Writes docs.\nmodel: sonnet\n---\nWrite clear docs.\n');
  await writeFile(path.join(project, 'CLAUDE.md'), '# App\n\nKeep changes small.\n');
  const env = {
    ...process.env,
    HOME: home,
    XDG_CONFIG_HOME: path.join(home, '.config'),
    XDG_CACHE_HOME: path.join(home, '.cache'),
    XDG_DATA_HOME: path.join(home, '.local', 'share'),
    XDG_STATE_HOME: path.join(home, '.local', 'state')
  };
  for (const name of ['CLAUDE_CONFIG_DIR', 'DISPLAY', 'WAYLAND_DISPLAY']) {
    delete env[name];
  }
  return { root, project, env, runFile: path.join(env.XDG_CACHE_HOME, 'cfgdash', 'run.json') };
}

async function waitForRun(harness, child) {
  for (let i = 0; i < 240; i++) {
    if (child.exitCode !== null) {
      throw new Error(`cfgdash run exited with ${child.exitCode}`);
    }
    try {
      const run = JSON.parse(await readFile(harness.runFile, 'utf8'));
      if (run.port && run.token) {
        return run;
      }
    } catch {
      /* not written yet */
    }
    await sleep(250);
  }
  throw new Error('cfgdash did not write run.json within 60 s');
}

function runStop(env) {
  return new Promise((resolve) => {
    execFile(exe, ['stop'], { env, timeout: 30_000 }, (error) => resolve(error ? (error.code ?? 1) : 0));
  });
}

async function main() {
  const harness = await makeHarness();
  const settingsFile = path.join(harness.project, '.claude', 'settings.json');
  const child = spawn(exe, ['run', '--port', '0', '--no-open', '--dir', harness.project], { env: harness.env, stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', (chunk) => (stderr += chunk));
  const exited = new Promise((resolve) => child.once('exit', resolve));
  let browser;
  let page;
  try {
    const run = await waitForRun(harness, child);
    token = run.token;
    const origin = `http://127.0.0.1:${run.port}`;
    step(`cfgdash is running on port ${run.port}`);

    browser = await chromium.launch();
    const context = await browser.newContext({ extraHTTPHeaders: { Authorization: `Bearer ${token}` } });
    page = await context.newPage();
    const foreign = [];
    const errors = [];
    page.on('request', (request) => {
      const url = new URL(request.url());
      if (url.protocol !== 'data:' && url.origin !== origin) {
        foreign.push(`${url.origin}${url.pathname}`);
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));
    page.on('console', (message) => message.type() === 'error' && errors.push(message.text()));

    const snapshotResponse = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/v1/snapshot');
    const response = await page.goto(`${origin}/overview`);
    assert.equal(response.status(), 200);
    assert.equal(response.headers()['content-security-policy'], CSP, 'CSP of the page');
    const api = await snapshotResponse;
    assert.equal(api.status(), 200);
    assert.equal(api.headers()['content-security-policy'], CSP, 'CSP of an API response');
    step('CSP header is exact on the page and the API');

    const nav = page.locator('nav[aria-label="Views"] a');
    await nav.first().waitFor();
    const hrefs = await nav.evaluateAll((links) => links.map((a) => a.getAttribute('href')));
    assert.ok(hrefs.includes('/settings') && hrefs.includes('/fleet') && hrefs.includes('/health'), `nav has the main views: ${hrefs.join(', ')}`);
    for (const href of hrefs) {
      const link = page.locator(`nav[aria-label="Views"] a[href="${href}"]`);
      const label = await link.locator('span').first().innerText();
      await link.click();
      await page.waitForURL((url) => url.pathname === href);
      await page.locator('main h1', { hasText: label }).waitFor();
      await page.waitForFunction(() => !document.querySelector('main [role="status"]'));
      const main = await page.locator('main').innerText();
      assert.ok(!/Couldn't load|Nothing here|cfgdash needs a reload/.test(main), `${href} rendered an error: ${main.slice(0, 200)}`);
      step(`${href}: ${label}`);
    }

    await page.locator('header a[aria-label="cfgdash settings"]').click();
    await page.locator('main h1', { hasText: 'cfgdash settings' }).waitFor();
    await page.getByRole('switch', { name: 'Start at login' }).waitFor();
    step('/cfgdash: the gear opens cfgdash settings');

    await page.goto(`${origin}/settings`);
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.waitForURL((url) => url.searchParams.get('edit') === '1');
    await page.getByRole('searchbox', { name: 'Search settings' }).fill('Always thinking');
    await page.getByRole('radio', { name: 'Project' }).click();
    await page.getByRole('switch', { name: 'Always thinking' }).click();
    await page.getByRole('button', { name: 'Review changes' }).click();
    await page.getByRole('heading', { name: 'Review changes' }).waitFor();
    assert.equal(await exists(settingsFile), false, 'the dry run writes nothing');
    step('proposed: dry-run diff shown, project settings file not created');

    await page.getByRole('button', { name: 'Apply 1 change' }).click();
    await page.getByText(/^Applied\. Audit entry /).waitFor();
    assert.deepEqual(JSON.parse(await readFile(settingsFile, 'utf8')), { alwaysThinkingEnabled: true });
    step('applied: project settings file holds alwaysThinkingEnabled true');

    await page.getByRole('button', { name: /^Undo change / }).first().click();
    await page.getByRole('heading', { name: /^Review undo of / }).waitFor();
    await page.getByRole('button', { name: 'Apply undo' }).click();
    await page.getByText(/^Undone\. Audit entry /).waitFor();
    assert.equal(await exists(settingsFile), false, 'the undo removes the file the apply created');
    step('undone: project settings file removed again');

    assert.deepEqual(foreign, [], 'requests that left the cfgdash origin');
    assert.deepEqual(errors, [], 'page and console errors');
    step('no request left the cfgdash origin; no page or console errors');

    await browser.close();
    browser = undefined;
    assert.equal(await runStop(harness.env), 0, 'cfgdash stop exit code');
    const code = await Promise.race([exited, sleep(15_000, 'timeout')]);
    assert.notEqual(code, 'timeout', 'cfgdash run did not exit after cfgdash stop');
    assert.equal(await exists(harness.runFile), false, 'run.json is removed on stop');
    step('cfgdash stop ended the server');
  } catch (error) {
    if (page) {
      await mkdir(artifacts, { recursive: true });
      await page.screenshot({ path: path.join(artifacts, 'failure.png'), fullPage: true }).catch(() => undefined);
    }
    console.error(hide(stderr));
    throw error;
  } finally {
    await browser?.close();
    if (child.exitCode === null) {
      child.kill();
    }
    await rm(harness.root, { recursive: true, force: true });
  }
}

main().then(
  () => console.log('browser test passed'),
  (error) => {
    console.error(hide(error?.stack ?? error));
    process.exitCode = 1;
  }
);
