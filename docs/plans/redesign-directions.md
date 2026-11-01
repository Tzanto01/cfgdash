# Redesign directions (revision 1, concept only, not approved)

Produced by @visual-design-planner on 2026-11-01 for [the redesign plan](redesign.md), phase 1. Comparison file: [redesign-directions.html](redesign-directions.html).

## Shared across all three

- Route: conventional frontend (dense lookup tool, keyboard use, generic blocks, 200 KB JS budget, CSP).
- Themes: `data-theme` on `<html>`, default follows the system, toggle cycles system / light / dark. Both themes are designed, not inverted.
- Status: same meaning everywhere (ok green, warn amber, danger red, company violet or pink). Color is never the only carrier: every status has an icon and a word, every layer a glyph (M, L, P, U) and a name.
- Contrast: text and status at least 4.5:1, focus and `borderStrong` at least 3:1; dark ratios against `raised`, light against `bg`. Values below are hand-computed and must be re-verified by script.
- Tints: `color-mix(in srgb, X 12% (light) / 14% (dark), surface)`.
- Sample data (synthetic): host `devbox`, `devbox.example.ts.net`, folder `/home/user/ws/app`.
  - Settings: `model` = `opus` applied from local, user `sonnet` overridden; `permissions.defaultMode` = `acceptEdits` from project; `permissions.allow` 2 entries merged from project and user; `env.OTEL_EXPORTER_OTLP_HEADERS` = `[redacted]` from user.
  - Instructions: `~/.claude/CLAUDE.md` user 2,250 tok; `/home/user/ws/app/CLAUDE.md` project 1,300 tok with children `@AGENTS.md` 1,000 tok, `docs/AGENTS.override.md` (referenced) 200 tok, `@docs/missing.md` (error); `Output style: Concise`; `web/CLAUDE.md` lazy 50 tok; total 4,600 tok est.
  - Health: danger `missing-script` `hooks/guard.sh`; warn `duplicate` agent; info unavailable collector `jobs.systemd-user` ("Linux only").
- Instruction-chain bar shows relative share plus the total, no budget claim (no budget in the snapshot). Health cards show message and target; explanation is optional.

## A. Instrument panel: mono-forward, flat, keyboard-first (recommended)

Thesis: the interface reads like a precise instrument; everything is a labelled row of mono values with a four-cell lineage strip, and color appears only where state lives.

- Shell: header 40px (machine, folder switcher, Ctrl K, theme, Edit); sidebar 208px collapsible to a 48px icon rail, 11px uppercase tracked group labels, right-aligned mono counts, active item 2px accent bar plus `hover` bg. Page head 56px with title and counts; toolbar 40px with key filter and chips All / Overridden / Redacted / Company. Rows 28px (44px coarse), 1px rules, no cards, 12px uppercase section labels.
- Row select (Enter or click) expands inline (48px, `raised`) into one line per layer: `[L] project (local)  opus  applied`, `[U] user  sonnet  overridden` (struck). "Open file" opens a 480px right side sheet with the redacted file in mono with line numbers, a source-layer chip and Copy path.
- Type: Geist Sans 13/18 500 for UI; Geist Mono 12.5/18 for values, keys, paths, badges, cells; section label Geist Sans 11/14 600 uppercase +0.06em; page title 20/28 600; overview numbers Geist Mono 28/32 tabular.
- Palette (light / dark): bg `#f2f3f5` / `#0a0c0f`; surface `#fafafb` / `#101318`; raised `#ffffff` / `#171b21`; hover `#e8eaee` / `#1d222a`; border `#d5d9de` / `#262c34`; borderStrong `#7f8791` / `#6b7580`; text `#14181d` / `#e8eaed`; muted `#565f6b` / `#9ba4ae`; accent `#00677a` / `#56d4e8`; accentFg `#ffffff` / `#06242b`; focus `#0086a3` / `#9fe6f3`; ok `#166534` / `#74d39a`; warn `#7a4f00` / `#e3b55e`; danger `#b42318` / `#f29a95`; company `#6d3fc0` / `#c3a8f2`; layer-managed `#0a2540` / `#f0f4fa`; layer-local `#1b4a8a` / `#b4ceff`; layer-project `#2f5f9f` / `#8fb0ec`; layer-user `#476e9f` / `#6f8fc2` (one-hue "ice" ramp, strongest layer most vivid).
- Shape: radius 3px controls, 2px badges, 1px borders, no shadows except palette and sheet (hairline plus `0 8px 24px rgb(0 0 0 / .35)` dark, `.12` light). lucide 16px stroke 1.5. Flag badges 20px, mono 11px, tint plus 1px status border, always with icon (`missing-script` file-warning, `error` octagon, `duplicate` copy, `overridden` strikethrough, `company` building, `redacted` eye-off, `link` link, `lazy` clock, neutral otherwise).
- Motion: hover/focus/selection 100ms linear; palette 100ms opacity plus 4px; sheet 160ms ease-out; changed row tint flash 400ms; nothing else. Reduced motion: all 0, flash becomes a static 6px "changed" dot.
- Lineage: `[M][L][P][U]`, four 18px squares, 2px gap. Winner filled in its layer color with bold glyph; overridden outlined with struck glyph; not set hollow dotted; merged half-filled diagonally. `aria-label` "Effective from project (local); overridden: user"; tooltip with contributing values.
- Instruction chain: tree table with `#`, path (mono, 16px indent), badges, token bar 96x6px filled to share of total, `est.` numbers right-aligned; full-width total bar on top; bars `muted`, error node danger outline.
- Diff review: unified diff mono 12.5px, two 40px line-number gutters, 16px `+`/`-` column, 14% ok/danger tint; sticky 32px file header (path, scope, `+n -n`); pinned 56px bottom bar "2 files, +3 -1, dry run" with Cancel and accent Apply; errors as a danger banner between header and diff, staged changes kept.
- Overview: no cards; mono identity strip `devbox  windows x64  tailnet devbox.example.ts.net  collected 10:00Z  v0.1.0`; counts table, one linked row per view with a big mono number; Unavailable list with reasons; Health count chip.
- Health: table severity (icon plus word), rule id (mono), target (copyable), message, Open; sorted by severity; filter chips; `j`/`k` and Enter.
- Palette: Ctrl/Cmd+K, 560px, 12vh from top, mono input, groups Views / Folders / Search results / Actions with key hints, footer `↑↓ move, ↵ open, esc close`, matches underlined.
- 375 px: 44px top bar (hamburger nav sheet with folder switcher, title, palette button); settings rows stack key / value / lineage plus flags; tap expands; sheet full height; diff scrolls horizontally with sticky gutters; pinned Apply bar with safe-area inset.
- Ports: shadcn/ui sidebar, command (cmdk), sheet, tabs, tooltip, switch, select, sonner, skeleton (MIT); lucide (ISC); Motion (MIT).
- Risks: "yet another dark dev tool" (mitigated by lineage and a real light theme); dense mono at 375 px; the ice ramp is only ordinal (local and project are close; glyph, name and position carry it).
- Rejects: cards everywhere, gradients, glow, big hero numbers, motion beyond state feedback, serif type.

## B. Field guide: editorial reference, lineage as the centerpiece

Thesis: a printed reference for one machine; a warm paper theme and a lamp-lit dark theme, serif headings, and lineage drawn as numbered strata so "why this value" reads like a footnote.

- Shell: sidebar 232px on `surface`, 12px uppercase group labels, 15px items, serif wordmark, 3px active accent bar; header 48px with breadcrumb-style folder button, palette trigger, theme toggle; reading column 720px for lists, wider for tables; title Fraunces 32/38 with a one-line subtitle from `views.ts`. Settings is master-detail: a sticky 400px "specimen" panel at 1100px and wider (bottom drawer at 768 to 1099px).
- Type: Fraunces (titles 32/38, sections 22/28, 600; plate numbers 13/16 600); IBM Plex Sans 15/24 body, 13/18 labels; IBM Plex Mono 13/20.
- Palette (light "paper" / dark "lamp"): bg `#f6f1e7` / `#16130f`; surface `#fffdf8` / `#1d1914`; raised `#ffffff` / `#26211a`; hover `#ede6d7` / `#2e281f`; border `#ddd4c3` / `#3a3328`; borderStrong `#857a66` / `#7d7260`; text `#1f1b16` / `#f0e9db`; muted `#5c5448` / `#b0a692`; accent `#a13d1e` / `#f0a07a`; accentFg `#ffffff` / `#26140b`; focus `#2456c7` / `#8fb4ff`; ok `#166534` / `#74d39a`; warn `#7a4f00` / `#e3b55e`; danger `#b42318` / `#f29a95`; company `#6d3fc0` / `#c3a8f2`; layer-managed `#1f1b16` / `#f0e9db`; layer-local `#0e5c6b` / `#6fd0dc`; layer-project `#1f5fa6` / `#8db4f2`; layer-user `#5b5f6b` / `#a3a6b3`. Accent only on links, active nav and primary button; danger always icon plus word.
- Shape: rows 36px (44px coarse); cards 1px border, no shadow (sheet and palette only); radius 6px, badges 4px; lucide 16px 1.5; hairline rules, thin top rule on section headings.
- Motion: route content fade and 8px rise 220ms ease-out-quart once per route; strata 40ms stagger (max 160ms); sheet 240ms; accordion 180ms. Reduced motion: opacity cut or instant.
- Lineage ("strata"): vertical stack of four rows numbered 1 to 4 in Fraunces, 3px left bar in layer color, layer name, path (mono, middle-truncated), value. Winner full weight with "applied"; overridden muted, struck value, "overridden"; not set faint dashed "not set". List rows show a compact "● L" chip.
- Instruction chain: contents page with dotted leaders to right-aligned token counts; 4px bar under each row scaled to the largest file; footer "4 files, 4,600 tokens est."; children indented 20px with "└".
- Badges: pills, 1px status border, icon, sentence-case label ("missing script").
- Diff review: "Proposal 1 of 2" Fraunces heading over scope and path; diff in a `raised` card, mono 13/20, `+`/`-` gutter, 12% tint; sticky bar "Apply 3 changes to user settings. A dry run showed no conflicts." plus Apply.
- Overview: prose block ("devbox is a Windows x64 machine. 4 of 6 collectors are active. 3 findings need attention.") above a typographic index of views with description and count; "Not available here" with reasons.
- Health: grouped under "Needs attention (1)", "Worth a look (1)", "For information (1)"; cards with rule, target, message.
- Palette: centered 640px on `raised`, serif group headings, 15px items with muted description, key hints; results show path and snippet.
- 375 px: 48px top bar (wordmark, palette, drawer); single column; specimen becomes an inline accordion; leaders kept, paths wrap at `/`; pinned Apply bar.
- Ports: shadcn/ui command, sheet, accordion, tabs, sonner (MIT); lucide; Motion. Fonts Fraunces, IBM Plex Sans, IBM Plex Mono (OFL 1.1, verify).
- Risks: three font families (subset to under about 100 KB woff2 each, unmeasured); master-detail needs 1100px; serif headings in a tool is the most subjective choice.
- Rejects: dense mono-everywhere tables, bento tiles, neon accents, motion on state change, icon-only nav.

## C. Control room: live state, bento overview, motion for changes

Thesis: a status board; a tile grid shows the machine's live state with counts that tick, an indigo accent and a route-style lineage make changes visible, and the views behind it stay dense.

- Shell: 64px icon rail expanding to 232px on hover or focus-within, pinnable, group dividers, count dots, tooltips; header 52px. Overview: 4-column grid, 12px gap, 1200px max, tiles 14px radius on `surface` with 1px border. Health 2x2 (segmented severity bar with counts as text, top 2 findings), Identity 2x1, count tiles 1x1 linking to views (Instrument Sans 36/40), Collectors tile with unavailable reasons. Inner views: 40px header, 32px rows, 8px-radius section cards.
- Type: Instrument Sans 14/20, h1 24/30, h2 17/24 (400/600), tile numbers 36/40 tabular, labels 12/16 500 sentence case; JetBrains Mono 12.5/18.
- Palette (light / dark): bg `#eef0f7` / `#0c0e18`; surface `#f8f9fd` / `#131627`; raised `#ffffff` / `#1b1f36`; hover `#e3e6f3` / `#232844`; border `#d3d7e8` / `#2a3050`; borderStrong `#7a82a8` / `#6a7399`; text `#12152b` / `#eceefa`; muted `#4e5578` / `#a3a9c9`; accent `#3a49d6` / `#8b9cff`; accentFg `#ffffff` / `#0c0e18`; focus `#2a3bd0` / `#c0c9ff`; ok `#166534` / `#74d39a`; warn `#7a4f00` / `#e3b55e`; danger `#b42318` / `#f29a95`; company `#a3207f` / `#f0a0dc` (pink, always with building icon); layer-managed `#2a3bd0` / `#aab6ff`; layer-local `#0a6490` / `#62c3ee`; layer-project `#7a4fd1` / `#c2a2ff`; layer-user `#5a6286` / `#9aa2c4`.
- Shape: radius 14px tiles, 8px cards, 6px controls; 1px borders, 2px accent ring on active tile; no shadows; lucide 16px (20px in tiles) stroke 1.75; 20px rounded-square layer glyphs; 22px badge pills.
- Motion (springs only on state change): spring stiffness 400 damping 34 for sheet, palette and list reorder; number tickers 600ms when a count changes after refresh (not first paint); changed tile border pulses once 600ms; lineage route lights nodes with 80ms stagger on folder change. Reduced motion: no springs, tickers or pulse; persistent "changed" dot.
- Lineage ("route"): horizontal chain of four 20px nodes joined by 12px lines in precedence order; winner filled with a 2px accent outer ring; overridden outlined, struck glyph, dashed connector; not set hollow and dim. In Settings each key is a small card with key, mono value and the route.
- Instruction chain: tree in a card, 120x8px rounded token bars in accent at 70%; header with large "4,600 tok est." and a segmented bar with one segment per file.
- Diff review: `raised` card per file, collapsible hunks, mono 12.5px, `+`/`-` and 14% tint; header with path, scope chip, "dry run"; pinned 64px footer card with "3 changes" and a large Apply; errors as a danger banner on the affected card.
- Health: 320px findings list with severity icons plus a detail pane (message, target, Open file, rule id), segmented severity bar on top; detail becomes a sheet when narrow.
- Palette: 640px, two panes above 768px (results and preview), single pane below.
- 375 px: 48px top bar, 56px bottom tab bar (Overview, Health, Settings, Search, More); bento to one column, count tiles 2-up; route wraps under the key; Apply bar above the tab bar.
- Ports: shadcn/ui sidebar, command, sheet, tabs, card, sonner (MIT); Magic UI number ticker (MIT); Motion; lucide.
- Risks: most "template" look; most reduced-motion work; tile count misread as importance; 14px tile radius vs tight views; bottom tab bar hides views behind More.
- Rejects: ambient motion, full-page gradients, decorative charts, ring gauges, mono-only Overview.

## D. Control panel: C's style, A's clarity (revision 2)

Requested by Nico on 2026-11-01: "keep the style of C and the cleanness of A". Thesis: C's identity (indigo palette, Instrument Sans, rounded surfaces, bento overview, route lineage, motion on change) on A's reading structure (ruled rows, mono values, one accent, color only where state lives).

What makes C harder to read than A, and the fix in D:

| C | A | D |
|---|---|---|
| Settings as a grid of key cards | One ruled row per key | A's rows, inside one rounded section card per group |
| Icon rail hides labels | Labelled 208px sidebar | Labelled 208px sidebar in C's style (accent-tint active pill, count pills), collapsible to C's 64px rail |
| Route with connectors and accent ring in every card | Compact four-cell strip | Strip in rows (C's rounded nodes, no connectors, no ring); full route only in the expanded row and the palette preview |
| Health as list plus detail pane | Severity table | C's segmented severity bar on top, A's table below, detail in the side sheet |
| Accent on tiles, bars and rings | Accent only for action and focus | Accent for action, focus, active nav and token bars; no accent rings except focus |

- Tokens: C's palette unchanged (light and dark), C's fonts (Instrument Sans for UI 14/20, JetBrains Mono 12.5/18 for keys, values, paths and numbers in tables).
- Shell: header 52px (C: folder pill, machine, Search Ctrl K, theme, Edit). Sidebar 208px on `surface`, sentence-case group labels 12/16 500 muted, items 36px with 8px radius, active item accent tint plus accent text, counts as small pills.
- Views: page head (h1 24/30 600, muted count), a toolbar with filter and chips, then one card per section (`surface`, 1px `border`, 8px radius) with a 40px section header (14/20 600, sentence case) and A-style rows inside: 32px high (44px coarse), 1px rules, no row radius, columns key / value / lineage / flags. Selection: `hover` background plus a 2px accent inset on the left.
- Lineage in rows: four 18px nodes, 5px radius, 1.5px border in the layer color, 2px gap, no connectors. Winner filled; overridden outlined with a struck glyph; not set dotted and dim; merged half-filled. Expanded row (inline, `raised`): C's route with 12px connectors (dashed into overridden layers) above A's contribution lines and "Open file".
- Flag badges: 22px pills, 12px sans, icon plus label, tint plus status border.
- Instructions: one card. Header: total in 28/32 600 tabular "4,600 tok est." with C's segmented bar (one segment per loaded file). Rows: A's tree table with 96x6 bars, 3px radius, accent at 70%; error node dashed danger outline; referenced file faint and not counted.
- Overview: C's bento (4 columns, 12px gap, 12px tile radius, no shadows). Tile numbers 32/36 600 tabular, labels 12/16 muted; Health 2x2 with the segmented bar and the top two findings as A-style rows; Identity 2x1 in mono; Collectors 2x1 with reasons. No accent ring on tiles; only focus shows a ring.
- Health: segmented severity bar with counts as text, then a card with A's table (severity icon plus word, rule mono, target copyable, message, Open); `j`/`k` and Enter; detail opens the side sheet.
- Diff review: C's `raised` card (8px radius) with a 40px sticky file header (path, scope pill, "dry run", `+n -n`) and a collapsible hunk row; A's diff body (two 40px gutters, `+`/`-` column, 14% tints). Pinned Apply bar: C's floating 64px footer card.
- Palette: C's 640px dialog, 12px radius, results plus preview pane above 768px (preview shows the route); 36px items with 8px radius highlight; A's footer hints.
- Motion: C's grammar on the Overview only (tickers 600ms after a refresh, one border pulse, springs for sheet and palette); inside views A's 100ms feedback and the 400ms row tint flash. Reduced motion as in A and C.
- 375 px: C's 48px top bar and 56px bottom tab bar; A's stacked rows (key and lineage on line 1, value on line 2, flags on line 3); bento 2-up with Health full width.
- Rejects: card grids for lists, connectors and rings in dense rows, ambient motion, uppercase tracked labels.
