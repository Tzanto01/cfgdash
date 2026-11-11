# cfgdash product site

Plan and content contract for the public product page (Nico, 2026-11-11). Target address: `https://cfgdash.nicowoellnitz.de/`, a subdomain on the same Hetzner Webhosting S (Apache, static files, FTPS upload) that serves `nicowoellnitz.de`. Design source: [web/DESIGN.md](../../web/DESIGN.md) and [web/PRODUCT.md](../../web/PRODUCT.md).

## Goal

A static, English, single-page site that advertises cfgdash in cfgdash's own look: same tokens, typefaces, shapes and voice, with a working replica of the product's core interaction as the hero. Design, features shown and accessibility matter most.

## Hard rules

- Plain HTML, CSS and JavaScript. No framework, no build step, no package added to the repo. Everything self-hosted: no CDN, no remote fonts, no analytics, no third-party request of any kind.
- Works without JavaScript: all copy, all screenshots and every demo row are readable and their ARIA states are true to what is shown; JS only enhances (tabs, filter, expand, copy, theme toggle, menu). If `site.js` fails to load, the page falls back to the no-JS presentation instead of hiding content behind dead controls.
- CSP-clean under `default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`: no inline `<script>` (JSON-LD data block excepted), no `<style>` element, no `style=""` attribute, no inline event handlers. Sizes that vary (bar segments) come from classes or SVG attributes.
- Tokens only, copied verbatim from `web/src/styles.css` (light, dark, and the `prefers-color-scheme` fallback while `data-theme` is unset). Never a raw hex outside the token block.
- Truthful copy. Every product claim on the page is in this file; do not invent features, numbers, platforms, prices or dates. The repo and releases are private, so the page offers no download and no repo link.
- Published prose has no em dash, no en dash and no spaced hyphen. Use commas, periods, colons or parentheses. Sentence case. No exclamation marks. No uppercase tracked labels.
- Synthetic data only in demos and screenshots: hosts `*.example` or `example.ts.net`, user paths with the user name `user` or `~`, machine names `devbox` and `laptop`.
- Nico's public name is "Nico Wöllnitz". Never name an employer.
- WCAG 2.2 AA. Usable from 320px wide with no horizontal scroll. Zoom never disabled.

## Files

```
site/
  README.md            preview, checks, screenshots, deploy notes
  THIRD_PARTY.md       fonts (OFL-1.1), lucide icon paths (ISC)
  public/              the document root that gets uploaded
    index.html
    404.html
    .htaccess
    robots.txt
    sitemap.xml
    favicon.svg  apple-touch-icon.png  og.png
    assets/site.css  assets/theme.js  assets/site.js
    assets/fonts/*.woff2
    assets/img/*.webp
  tools/
    shots.mjs          captures the screenshots from the web mock, both themes
    check.mjs          browser smoke over site/public (see Checks)
```

Writable paths for the builder: `site/**` only. `web/**`, `src/**`, `docs/contracts/**` and agent-instruction files are read-only. `web/dist` and `web/node_modules` may be rebuilt (both untracked).

## Look

Follow DESIGN.md. What a marketing page adds on top:

- Display scale in Instrument Sans: h1 `clamp(2.25rem, 5.5vw, 3.75rem)`, weight 600, line-height 1.05, letter-spacing -0.02em; h2 `clamp(1.625rem, 3vw, 2.25rem)` weight 600; lead 18/28 in `muted`; body 16/26. Everything literal (commands, keys, values, paths, counts in demos) is JetBrains Mono, as in the product.
- Page on `bg`, content max width 1120px, side padding 24px (16px below 640px), sections 96px apart (64px below 768px). Sections are separated by space and 1px rules, not by alternating fills.
- Surfaces as in the product: tiles 12px radius on `surface` with a 1px border, cards 8px, controls 6px, pills full. Shadows only on the open mobile menu. Accent only on actions, focus, the active tab and the bars.
- Kept rejects: no gradients, no glow, no ambient or scroll-triggered animation, no decorative blobs, no stock imagery, no emoji, no color as the only carrier of meaning.
- Motion: 100ms on hover, focus and selection; 160ms ease-out on tab change and row expand. `prefers-reduced-motion: reduce` sets all of it to 0ms.
- Icons: lucide paths as inline SVG, 16px, stroke 1.75 (20px in bento tiles), `aria-hidden`. The mark is the three stacked layers from `web/public/favicon.svg`, drawn with `accent` on `surface`; the site favicon uses the dark-theme `bg` and `accent` values.
- Theme: `data-theme` on `<html>`; unset follows the system. `assets/theme.js` is a tiny blocking script in `<head>` that applies the stored choice before first paint. The header toggle cycles system, light, dark, stores it in `localStorage` under `cfgdash-site-theme` (try/catch) and says the current state in its accessible name. `color-scheme` follows the theme. This key is the only thing the site stores.

## Page structure and copy

Copy below is final unless marked provisional. Landmarks: `header`, `nav`, `main`, `footer`; skip link "Skip to content"; exactly one `h1`; each section has an `h2` and an `id`.

### Header

Sticky, 52px, `surface`, bottom border (the product header). Left: mark and "cfgdash" (weight 600), linking to the top. Middle: Features, Views, Security, Commands, FAQ. Right: theme toggle (icon button), primary button "Get access" (to `#access`). Below 768px the middle links move into a menu button ("Menu", `aria-expanded`, `aria-controls`; Escape closes and returns focus); without JS the links stay visible and wrap.

### Hero

- Pill (mono, neutral): `v0.6 · Windows and Linux`
- h1: See what your Claude Code harness actually loads.
- Lead: cfgdash reads the settings, instructions, agents, skills, hooks and MCP servers on your machine and shows which file set each value and which layer won. One executable, local only, read-only by default.
- Actions: primary "Get access" (`#access`), secondary "See the views" (`#views`).
- Command line below the actions: a mono chip `cfgdash` with a "Copy" button and the note "Serves on http://127.0.0.1:7820 and opens your browser."

Hero demo, full content width, directly below: a working replica of the Settings view's card, framed like the product shell (a 52px top bar with the folder pill `~/ws/app`, the machine name `devbox`, and at the right the text "Live demo with made-up data"). Inside:

- Toolbar: "Filter" input (`aria-label="Filter rows"`) and chips All, Overridden, Redacted as toggle buttons (`aria-pressed`; pressing the active chip returns to All). A polite live line says "n of 5 rows" when filtered, and "No rows match" when empty.
- Card header "Effective settings", sub line "Merged from 4 layers for ~/ws/app".
- kv rows exactly as DESIGN.md "kv rows" and "Lineage strip" describe (grid key, value, lineage, source; stacked below the container width; row is a button with `aria-expanded`; expanded panel shows the route and one contribution line per layer with its status word; overridden values struck through and also marked in words). Data:

| Key | Effective value | Layers (M L P U) | Source chip | Contributions when expanded |
| --- | --- | --- | --- | --- |
| `model` | `opus`, sub line "Overrides sonnet from user" | L applied, U overridden, others not set | project (local) | Local `~/ws/app/.claude/settings.local.json` `opus` applied; User `~/.claude/settings.json` `sonnet` overridden |
| `permissions.defaultMode` | `acceptEdits`, sub line "Overrides default from user" | P applied, U overridden | project | Project `~/ws/app/.claude/settings.json` `acceptEdits` applied; User `~/.claude/settings.json` `default` overridden |
| `permissions.allow` | `Bash(npm test *)`, `Bash(npm run build)`, `Read(./tests/**)` | P merged, U merged | 2 layers | Project: `Bash(npm test *)`, `Bash(npm run build)` merged; User: `Read(./tests/**)` merged |
| `permissions.disableBypassPermissionsMode` | `disable` | M applied | managed | Managed `managed-settings.json` `disable` applied |
| `env.OTEL_EXPORTER_OTLP_HEADERS` | eye-off icon, `[redacted]` (screen-reader "value redacted") | U applied | user, plus a `redacted` badge | User `~/.claude/settings.json`, value redacted (never print a value) |

With JS the first row starts expanded and the others collapsed, with no flash on load; without JS every row is expanded. Each lineage strip is `role="img"` with a label such as "Effective from local; overridden: user". Under the demo, one muted line: "This is the Settings view. Select a row to see every layer that set it."

### The problem (`#why`, h2: One harness, many files)

Intro: Claude Code builds its behavior from settings in four layers, instruction files from several folders, and a growing set of agents, skills, hooks and MCP servers. When something behaves differently than you expect, the answer is somewhere in those files.

Three ruled rows (not cards), each a short title and one sentence:

1. Four layers, one effective value. Managed, local, project and user settings merge per key. cfgdash shows the winner, the file it came from and what it overrode.
2. Instructions load from many places. cfgdash shows the chain for a folder in load order, with sizes and token estimates, including imports.
3. Broken config fails quietly. A hook whose script is gone, an agent that loses to a duplicate, an MCP command that is not on PATH. Health lists each one with its evidence.

### Features (`#features`, h2: What it shows)

Intro, muted: "The examples in these tiles use made-up data." Tile visuals may show illustrative values (session rows, chart columns, search results) as long as they are plainly demo data and consistent with the screenshots.

A bento grid as on the product's Overview (4 columns, 12px gap; 2 columns from 640px; 1 column below). Each tile: icon, title (14/20 600), one or two sentences, and a small live-looking visual built in HTML/CSS/SVG from the product's components. Tiles:

1. **Layer lineage** (2 wide). "Every setting carries its route through the four layers: managed, local, project, user." Visual: a legend of the four strip states with words: applied (filled), overridden (outlined, struck glyph), merged (half filled), not set (dotted).
2. **Instruction chain** (2 wide). "What loads for a folder, in order, with a token estimate per file." Visual: "Loaded for ~/ws/app", total `4,600` plus "tok est.", the segmented bar, then `~/.claude/CLAUDE.md` 1,900, `~/ws/CLAUDE.md` 1,200, `~/ws/app/CLAUDE.md` 900, `@AGENTS.md` 600, and the line "4 files · 18.0 KB · about 4,600 tokens (estimate)".
3. **Health** (2 wide). "Deterministic rules over the snapshot, no AI involved. `cfgdash doctor` prints the same findings and exits with code 3 on a critical one, so a hook or CI job can use it." Visual: the severity bar with "1 critical · 7 warning · 7 info" (the counts the mock shows in the screenshots) and two rows: Critical "Hook script not found", Warning "Duplicate entry" (icon plus word).
4. **Search everything** (1 wide). "Keys, values, labels and file contents, from the command palette." Visual: a search field with a `Ctrl K` kbd.
5. **Fleet** (1 wide). "Compare this machine with another one on your tailnet, or with an earlier export of itself." Visual: "devbox compared with laptop", "3 changed · 1 only on devbox · 2 only on laptop".
6. **Guarded edits** (2 wide). "Settings and text config files can be changed from localhost. Every change is a dry-run diff first, applied only after you confirm, written to an audit log and undoable." Visual: a diff card header (`~/.claude/settings.json`, pill "user", "dry run", "+1 −1") with a removed line `"model": "sonnet",` and an added line `"model": "opus",` (danger and ok tints, plus the +/- column and screen-reader "removed"/"added").
7. **Config assistant** (1 wide). "A contained Claude session that reads the redacted snapshot, explains a finding and proposes a fix as a diff you review."
8. **Usage** (1 wide). "Cost, tokens and sessions per day, model and project, when your machine has an OpenTelemetry metrics store." Visual: a small column chart (plain SVG, one accent hue, `role="img"` with a summary label).
9. **More than Claude Code** (2 wide). "Codex, OpenCode and T3 Code configs appear in the same model, and Health flags where the tools disagree about MCP servers or instruction files." Visual: three state chips (dot plus word): Claude Code active, Codex active, OpenCode not on this machine.
10. **Sessions and memory** (2 wide). "Recent Claude Code sessions with tokens and estimated cost, and what agents remember in the Engram store, when the machine has one."

### Views (`#views`, h2: The views)

Intro: Screenshots of the real UI, running on its built-in demo data.

A tab list (WAI-ARIA tabs: `role="tablist"`, arrow keys, Home and End, roving tabindex, each `tabpanel` labelled by its tab) over real screenshots: Overview, Settings, Instructions, Health, plus one or two more that look good on the mock (candidates: the settings editor's diff review, Fleet compare, Usage). Each panel: the caption from `web/src/lib/views.ts` for that view and the screenshot inside a frame (12px radius, 1px border, a slim bar showing `127.0.0.1:7820` in mono). Screenshots follow the page theme: a light and a dark capture per view, switched in CSS by `data-theme` and the system fallback. Each `img` has `width`, `height`, a real `alt` describing what the view shows, `loading="lazy"` and `decoding="async"`, WebP with a `srcset` (about 1280 and 2560 wide). Below 640px each view shows a phone capture of the same view instead (the product has its own narrow layout), through `picture` with a `media` source that carries its own `width` and `height`. Without JS all panels show stacked.

### Security (`#security`, h2: Local by design)

Intro: cfgdash looks at files that hold your tooling's secrets, so the rules are strict and written down.

A ruled key and value list styled as the product's kv rows (mono key, plain sentence). A `dl`:

| Key | Statement |
| --- | --- |
| `listens on` | 127.0.0.1 only. Never a LAN address. |
| `access` | A fresh random token for every run, in the URL it prints. |
| `secrets` | Credential files are never opened. Secret-looking keys and values are redacted before anything leaves the core, and over-redaction is the accepted failure mode. |
| `writes` | Nothing by default. Edits work only from localhost, after a diff and an explicit confirm, and every one is audited and undoable. |
| `network` | The UI loads nothing from outside its own origin: no CDN, no remote fonts, no analytics. |
| `idle` | It stops itself after 30 minutes without a request. |
| `tailnet` | Optional. `--tailnet` publishes through Tailscale Serve to your own tailnet, and every request needs the token plus a tailnet identity. Never Funnel. |

### Commands (`#commands`, h2: One command)

Intro: A single self-contained executable for Windows x64 and Linux x64. No runtime to install.

A terminal-styled block (`raised`, mono, 8px radius) listing commands with a description column, each command with its own "Copy" button (accessible name "Copy cfgdash doctor"; feedback "Copied" or "Couldn't copy" through one polite live region):

| Command | Description |
| --- | --- |
| `cfgdash` | Serve on 127.0.0.1:7820 and open the browser |
| `cfgdash --dir <path>` | Start in another folder |
| `cfgdash --tailnet` | Also publish to your tailnet |
| `cfgdash doctor` | Print health findings; exit code 3 on a critical one |
| `cfgdash context` | Print what the harness loads for a folder |
| `cfgdash export` | Write a snapshot as JSON |
| `cfgdash autostart on` | Start at login |
| `cfgdash update` | Install the latest release |
| `cfgdash stop` | Stop the running instance |

### Get access (`#access`, h2: Get access) (provisional)

cfgdash is not public yet. The repository and its releases are private while it settles. If you want to try it on your own harness, or want to know when it opens up, write to me.

Primary button "Write to contact@nicowoellnitz.de" (`mailto:contact@nicowoellnitz.de?subject=cfgdash%20access`). Next to it, muted: "Windows x64 and Linux x64 · v0.6.0".

This block is provisional until Nico decides how cfgdash goes public. Keep it one self-contained `section` so it can be swapped for a download block.

### FAQ (`#faq`, h2: Questions)

Native `details`/`summary`:

- **Does it change my configuration?** Not unless you ask it to. Viewing never writes. The edit path is separate: it works only from localhost, shows a dry-run diff, applies after you confirm, logs the change and can undo it.
- **Does anything leave my machine?** The dashboard itself makes no outside requests. Two features talk to other systems when you use them: the config assistant starts a session with your own Claude CLI, which sends the redacted data it reads to your model provider, and `cfgdash update` downloads the next release.
- **Do I need Tailscale?** No. It is only needed to open the dashboard from your other devices (`--tailnet`) and to compare machines in Fleet.
- **What if my machine only has part of this?** cfgdash shows what the machine has. A source that is missing shows as "not on this machine" with the reason, and is never an error.
- **Which platforms?** Windows x64 and Linux x64.
- **Is it open source?** Not at the moment. The repository is private for now.

### Footer

Mark and "cfgdash v0.6.0". "Built by Nico Wöllnitz" (link `https://nicowoellnitz.de/`). Links: "Impressum" (`https://nicowoellnitz.de/impressum/`), "Datenschutz" (`https://nicowoellnitz.de/datenschutz/`), both with `hreflang="de"`. Muted line: "cfgdash is an independent project and is not affiliated with Anthropic. Claude and Claude Code are trademarks of Anthropic."

### Head and extras

- `<title>`: "cfgdash: see what your Claude Code harness loads". Description: "A local, read-only dashboard for the Claude Code harness on your machine: effective settings with the file that set each value, the instruction load chain, agents, skills, hooks, MCP servers and health findings."
- Canonical `https://cfgdash.nicowoellnitz.de/`, Open Graph and Twitter card tags with absolute URLs, `og.png` 1200x630 (rendered from a small HTML template by the screenshot tool, dark theme: mark, name, the h1, a lineage strip), `theme-color` per scheme, `color-scheme` meta, `referrer` `strict-origin-when-cross-origin`.
- JSON-LD `SoftwareApplication` (name, description, `applicationCategory` "DeveloperApplication", `operatingSystem` "Windows, Linux", `softwareVersion` "0.6.0", author Person "Nico Wöllnitz" with the portfolio URL). No `offers`, no rating.
- Preload the two latin `woff2` files. `font-display: swap`. Fonts copied from `web/node_modules/@fontsource-variable/*/files/*-latin-wght-normal.woff2` and `*-latin-ext-wght-normal.woff2`.
- `404.html`: same header and footer, h1 "This page doesn't exist", a link back to the start, `noindex`.
- `robots.txt` allows all and names the sitemap; `sitemap.xml` lists the one URL.
- `.htaccess`: complete on its own. The subdomain has its own document root and the portfolio's `.htaccess` does not apply to it (verified live on the hosting). It carries `Options -Indexes`, `FileETag None`, `ErrorDocument 404 /404.html`, an HTTPS redirect that keeps the host (`RewriteCond %{HTTPS} off`, excluding `/.well-known/` so certificate renewals keep working), `Header always set` for `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`, `Permissions-Policy`, `Strict-Transport-Security` and the CSP above, `Cache-Control` 300s by default and 30 days for `assets/fonts` and `assets/img`, and `mod_deflate` for text types.

## Checks

`node site/tools/check.mjs` serves `site/public` on a loopback port with the CSP header above and drives Chromium (`playwright-core` from `web/node_modules`) at 320, 375, 768 and 1280 wide in both themes. It fails on: a console error or CSP violation, a request to another origin, horizontal page scroll, an `img` without `alt`, `width` or `height`, an in-page link without a target. It exercises: the row expand, the chips and the filter (and its count line), the tabs with arrow keys, a copy button, the theme toggle surviving a reload, the menu button at 375 with Escape. It writes full-page screenshots per width and theme to an artifacts folder outside the repo for review. Static greps: no `style=` attribute, no inline script, no `http://` asset, no em or en dash in `site/public/*.html`.

## Deploy (Nico's call, not part of the build)

1. The subdomain `cfgdash.nicowoellnitz.de` exists (FTP path `/cfgdash`, inside the portfolio's document root). It needs an A record only (IPv6 is broken on this hosting) and its own Let's Encrypt certificate (domain → Services → SSL Manager → Install Certificate next to the subdomain). konsoleH's "HTTPS redirect" toggle stays off.
2. Upload with the hosting repo's tool (`personal/hosting/tools/deploy-ftp.py`): `FTP_DIST=<repo>/site/public FTP_ROOT=/cfgdash`, `--dry-run` first. The portfolio deploy keeps that folder (`FTP_KEEP`).
3. After the first upload: `curl -sI https://cfgdash.nicowoellnitz.de/` must show exactly one `Content-Security-Policy` header and the other security headers, the http form must 301 to https, and a font and an image must come back with the 30-day `Cache-Control`.
4. The portfolio's Datenschutz page gets one sentence covering the subdomain and its `cfgdash-site-theme` storage key before the page goes live.
