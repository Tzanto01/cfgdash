import type { CompareResponse, ExportsResponse, FleetResponse } from '../lib/models';
import { makeSnapshot } from '../test/fixtures';
import { startApp, type AppHarness } from '../test/harness';
import { answerWhenSent } from '../test/lazy';

const fleet = (): FleetResponse => ({
  self: { name: 'devbox', dnsName: 'devbox.example.ts.net', os: 'linux' },
  peers: [
    { name: 'laptop', dnsName: 'laptop.example.ts.net', os: 'windows', online: true, cfgdash: 'ok', version: '0.5.0' },
    { name: 'homeserver', dnsName: 'homeserver.example.ts.net', os: 'linux', online: true, cfgdash: 'refused', reason: 'port 7820 not reachable on the tailnet' },
    { name: 'buildbox', dnsName: 'buildbox.example.ts.net', os: 'linux', online: true, cfgdash: 'absent', reason: 'cfgdash not running on the peer' },
    { name: 'tablet', dnsName: 'tablet.example.ts.net', os: 'linux', online: true, cfgdash: 'error', reason: 'TLS handshake failed' }
  ]
});

const exportsList = (): ExportsResponse => ({ exports: [{ id: 'devbox-20261101T080000Z', machine: 'devbox', collectedAt: '2026-11-01T08:00:00Z', dir: '/home/user/ws/app' }] });

const diff = (): CompareResponse => ({
  left: { machine: 'devbox', collectedAt: '2026-11-02T09:00:00Z', dir: '/home/user/ws/app' },
  right: { machine: 'laptop', collectedAt: '2026-11-02T08:58:00Z', dir: '/home/user/ws/app' },
  counts: { changed: 1, onlyLeft: 1, onlyRight: 1 },
  collectors: [
    { id: 'agents.definitions', state: { left: 'active', right: 'active' }, rows: [{ blockId: 'agents', itemId: 'user-x', change: 'onlyRight', right: { name: 'x' } }] },
    { id: 'hooks.registered', state: { left: 'active', right: 'active' }, rows: [] },
    {
      id: 'settings.effective',
      state: { left: 'active', right: 'notPresent' },
      rows: [
        { blockId: 'effective', itemId: 'env.EXAMPLE', change: 'onlyLeft', left: '1' },
        { blockId: 'effective', itemId: 'model', change: 'changed', left: 'opus', right: 'sonnet' }
      ]
    }
  ]
});

async function openFleet(): Promise<AppHarness> {
  const h = await startApp('/fleet', makeSnapshot({ targetDir: '/home/user/ws/app' }));
  await answerWhenSent(h, '/api/v1/fleet', fleet());
  await h.answer('/api/v1/exports', exportsList());
  return h;
}

const peerText = (name: string) =>
  ([...document.querySelectorAll('main li')].find((li) => li.querySelector('.font-medium')?.textContent === name)?.textContent ?? '').replace(/\s+/g, ' ');

describe('Fleet view', () => {
  it('lists peers with their state, version and reason, and explains refused and absent', async () => {
    const h = await openFleet();

    expect(peerText('laptop')).toContain('ok');
    expect(peerText('laptop')).toContain('cfgdash 0.5.0');
    expect(peerText('homeserver')).toContain('refused');
    expect(peerText('homeserver')).toContain('port 7820 not reachable on the tailnet');
    expect(peerText('buildbox')).toContain('absent');
    expect(peerText('tablet')).toContain('TLS handshake failed');
    expect(h.buttons(/^Compare with /).map((b) => b.textContent)).toEqual(['Compare with laptop']);
    expect(h.text()).toContain('Add an ACL grant for tcp:7820');
    expect(h.text()).toContain("Absent: cfgdash isn't running on that machine");
  });

  it('compares this folder with an export and shows counts, then changed rows first grouped by collector, without unchanged collectors', async () => {
    const h = await openFleet();

    await h.click(h.button('Compare'));
    const request = await h.answer('/api/v1/compare', diff());

    expect(request.url.searchParams.get('against')).toBe('export:devbox-20261101T080000Z');
    expect(request.url.searchParams.get('dir')).toBe('/home/user/ws/app');
    expect(h.text()).toContain('1 changed · 1 only on devbox · 1 only on laptop');
    const groups = [...document.querySelectorAll('main section[aria-label]')].map((s) => s.getAttribute('aria-label'));
    expect(groups).toEqual(['settings.effective', 'agents.definitions']);
    const rows = [...document.querySelectorAll('main section[aria-label="settings.effective"] li')].map((li) => li.textContent ?? '');
    expect(rows[0]).toContain('effective › model');
    expect(rows[0]).toContain('Changed');
    expect(rows[1]).toContain('Only on devbox');
    expect(h.text()).toContain('devbox: active · laptop: notPresent');
  });

  it('hides a compare result made for another folder after the folder changes', async () => {
    const h = await openFleet();
    await h.click(h.button('Compare'));
    await h.answer('/api/v1/compare', diff());
    expect(h.text()).toContain('1 changed');

    await h.navigate('/fleet?dir=%2Fhome%2Fuser%2Fws%2Fother');
    await h.answer('/api/v1/snapshot', makeSnapshot({ targetDir: '/home/user/ws/other' }));

    expect(h.text()).not.toContain('1 changed');
    expect(document.querySelectorAll('main section[aria-label]')).toHaveLength(0);
  });

  it('compares with a peer from its row and shows a peer that cannot be reached inline', async () => {
    const h = await openFleet();

    await h.click(h.button('Compare with laptop'));
    const request = await h.answer('/api/v1/compare', { error: 'peer-unavailable', reason: 'timed out' }, 502);

    expect(request.url.searchParams.get('against')).toBe('peer:laptop.example.ts.net');
    expect(document.querySelector('main [role="alert"]')?.textContent).toBe("That machine can't be compared: timed out.");
  });

  it('explains an empty fleet off the tailnet and how to make an export', async () => {
    const h = await startApp('/fleet');
    await answerWhenSent(h, '/api/v1/fleet', { self: { name: 'devbox', dnsName: null, os: 'linux' }, peers: [] });
    await h.answer('/api/v1/exports', { exports: [] });

    expect(h.text()).toContain("This machine isn't on a tailnet");
    expect(h.text()).toContain('No exports yet. Run cfgdash export to save one');
    expect(h.button('Compare').disabled).toBe(true);
  });
});
