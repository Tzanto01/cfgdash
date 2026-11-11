# cfgdash product site

Static, single-page product site for `https://cfgdash.nicowoellnitz.de/`. Plan and content contract: [docs/plans/product-site.md](../docs/plans/product-site.md). Hand-written HTML, one CSS file, two small scripts, no build step. `public/` is the document root that gets uploaded.

## Preview

Any static server over `public/` works, for example:

```
python3 -m http.server 8080 --bind 127.0.0.1 --directory site/public
```

Asset paths are root-relative (`/assets/...`), so open it at the server root, not as a `file://` page.

## Checks

```
node site/tools/check.mjs [artifacts-dir]
```

Serves `public/` on a random loopback port with the production CSP and drives Chromium (`playwright-core` from `web/node_modules`, so run `npm ci` in `web` first) at 320, 375, 768 and 1280 wide in both themes. Fails on console errors, CSP violations, requests to other origins, horizontal scroll, images without `alt`, `width` or `height`, and in-page links without a target; exercises the demo rows, chips, filter, tabs, copy, theme persistence, the mobile menu, the no-JS page and the 404 page; runs the static greps (no `style=`, no inline script or handler, no `http://` asset, no em or en dash, no spaced hyphen). Full-page screenshots go to the artifacts folder (default: the OS temp dir). Exit code 1 on any failure.

## Screenshots

```
cd web && npm run build && npm run preview
node site/tools/shots.mjs [raw-dir]
```

With the mock UI on `http://127.0.0.1:4312` (override with `MOCK_URL`), captures Overview, Settings (model row expanded), Instructions, Health and Usage at 1280x800, device scale 2, in light and dark (the UI follows `prefers-color-scheme` while no theme is stored). The snapshot's `cfgdashVersion` is set to `0.6.0` on the fly so the Identity tile matches the page. Raw PNGs go to `raw-dir` (default: the OS temp dir); Pillow (`python3`) writes WebP at 2560 and 1280 wide, quality 82, into `public/assets/img/`. It also renders `public/og.png` (1200x630) and `public/apple-touch-icon.png` (180x180) from `tools/og.html` and `tools/icon.html`. Look at every capture before committing it.

## Deploy

Nico's call; steps are in the plan's "Deploy" section. `public/.htaccess` only sets the strict CSP and 30-day caching for fonts and images; redirects, the other security headers, 300s caching and compression come from the portfolio's `.htaccess` in the parent document root.
