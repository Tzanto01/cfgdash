# cfgdash UI design: direction D "Control panel"

Implementation blueprint. Source: section D of `docs/plans/redesign-directions.md` and tab D of `docs/plans/redesign-directions.html` (approved by Nico on 2026-11-01). Product facts: [PRODUCT.md](PRODUCT.md).

Thesis: C's identity (indigo palette, Instrument Sans, rounded surfaces, bento overview, route lineage, motion on change) on A's reading structure (ruled rows, mono values, one accent, color only where state lives).

## Tokens

CSS custom properties on `:root[data-theme=light|dark]` in `src/styles.css`, exposed to Tailwind v4 through `@theme inline` (`bg-surface`, `text-muted`, `border-border`, ...). Components use tokens only, never raw hex.

| Token | Light | Dark | Role |
| --- | --- | --- | --- |
| `bg` | `#eef0f7` | `#0c0e18` | page |
| `surface` | `#f8f9fd` | `#131627` | sidebar, cards, tiles |
| `raised` | `#ffffff` | `#1b1f36` | expanded rows, sheets, dialogs, palette |
| `hover` | `#e3e6f3` | `#232844` | hover and selected row |
| `border` | `#d3d7e8` | `#2a3050` | rules and card borders |
| `border-strong` | `#7a82a8` | `#6a7399` | control borders, neutral badges (3:1) |
| `text` | `#12152b` | `#eceefa` | body |
| `muted` | `#4e5578` | `#a3a9c9` | secondary text (4.5:1 on every surface) |
| `accent` | `#3a49d6` | `#8b9cff` | action, focus, active nav, token bars |
| `accent-fg` | `#ffffff` | `#0c0e18` | text on accent |
| `focus` | `#2a3bd0` | `#c0c9ff` | focus ring |
| `ok` | `#166534` | `#74d39a` | status |
| `warn` | `#7a4f00` | `#e3b55e` | status |
| `danger` | `#b42318` | `#f29a95` | status |
| `company` | `#a3207f` | `#f0a0dc` | company flag, always with the building icon |
| `layer-managed` | `#2a3bd0` | `#aab6ff` | lineage M |
| `layer-local` | `#0a6490` | `#62c3ee` | lineage L |
| `layer-project` | `#7a4fd1` | `#c2a2ff` | lineage P |
| `layer-user` | `#5a6286` | `#9aa2c4` | lineage U |
| `layer-fg` | `#ffffff` | `#0c0e18` | glyph on a filled layer node |

- Tint of a status or accent `X`: `color-mix(in srgb, X 12%, surface)` light, `14%` dark (`--tint` holds the percentage).
- Shadow (sheet, palette, dialogs only): `0 8px 24px rgb(0 0 0 / .12)` light, `.35` dark.
- Contrast (verified by script, see the receipt): text, muted, accent and every status at least 4.5:1 on `bg`, `surface`, `raised`, `hover` and on their own tint; `focus` and `border-strong` at least 3:1; `layer-fg` on every layer at least 5:1.

## Theme

`data-theme` on `<html>`. Default follows `prefers-color-scheme` (CSS falls back to the media query while `data-theme` is unset, so there is no flash and no inline script); the header toggle cycles system, light, dark and stores the choice in `localStorage` (`cfgdash-theme`). `color-scheme` follows the theme.

## Type

Self-hosted OFL variable fonts (`@fontsource-variable/instrument-sans`, `@fontsource-variable/jetbrains-mono`), `font-display: swap`, fallbacks `system-ui` and `ui-monospace`.

| Use | Font | Size/line | Weight |
| --- | --- | --- | --- |
| UI body | Instrument Sans | 14/20 | 400, 500 |
| Page title (h1) | Instrument Sans | 24/30 | 600 |
| Section header (card) | Instrument Sans | 14/20 | 600 |
| Labels, group labels, counts | Instrument Sans | 12/16 | 500, sentence case, no tracking |
| Keys, values, paths, cells, numbers in tables | JetBrains Mono | 12.5/18 | 400 (700 for lineage glyphs) |
| Overview tile number | Instrument Sans | 32/36 | 600, tabular |
| Instruction total | Instrument Sans | 28/32 | 600, tabular |
| Badges | Instrument Sans | 12/16 | 500 |

## Shape and icons

- Radius: tiles 12px, palette 12px, cards and sheets 8px, nav items 8px, controls 6px, lineage nodes 5px, badges full pill, token bars 3px. Rows have no radius.
- Borders 1px; no shadows except sheet, palette and dialogs. No accent rings except focus (2px `focus`, offset 1px; inside cards offset -2px).
- lucide-react, 16px, stroke 1.75 (20px in tiles and the bottom tab bar). Icons are decorative (`aria-hidden`); controls carry the name.

## Layout

### Shell (at least 1024px)

- Header 52px on `surface` with bottom border: folder pill (folder icon, mono path, chevron; opens the folder picker), machine name (monitor icon, muted), spacer, Search button (search icon, "Search", `Ctrl K` kbd; opens the palette), Settings link (cog icon plus "Settings", bordered like a control; only when `GET /api/v1/health` says `edits: true`, so never on the tailnet listener), refresh icon button, theme toggle.
- Sidebar 208px on `surface`, right border, groups with sentence-case labels 12/16 500 muted: *Machine* (Overview, Health), *Harness* (Settings, Instructions, Agents, Skills & commands, Hooks, MCP & permissions, Environment, Jobs, T3 Code, Codex, OpenCode), *Activity* (Sessions, Usage, Memory), then Other without a label. Only visible views (`views.ts`). Items 36px, radius 8px, icon plus label plus count pill (mono 11px, `raised` with border); active item accent tint background, accent text, weight 600, `aria-current="page"`. A warn dot plus screen-reader text when the view has an unavailable source. Health shows no count pill: a badge with critical plus warning findings (danger tint when any critical, else warn; hidden at zero, before findings load and on error; a dot in the collapsed rail; screen-reader text "n findings need attention"). Foot: "Collected 10:00 · 2 min ago" (muted 12px) and Collapse. Collapsed: 64px rail, icons only with tooltips, counts hidden, labels kept as accessible names.
- Main column: page padding 24px, max content width 1200px.
- 768 to 1023px: sidebar starts collapsed (rail).

### Narrow (below 768px, designed at 375px)

- Top bar 48px: cfgdash mark, current page title, palette button, Settings icon link (when edits are available), theme toggle.
- Bottom tab bar 56px, safe-area inset: Overview, Settings, Search, More (Settings only when visible). More opens a left sheet with the full nav, the folder pill, collected time and Refresh.
- Rows stack: key and lineage on line 1, value on line 2, flags on line 3. Tables become stacked cards (label from the column). Bento 2-up. Sheets full height and width. Touch targets at least 44px (rows 44px on coarse pointers).

## Views

- Page head: h1 24/30 600 plus a muted count ("42 keys", "4 files"); the view description from `views.ts` below in muted 14/20. Views with a target-dir collector show the folder row (label, mono path, "Change folder" button).
- Toolbar (only on views with `kv` or `table` blocks): "Filter" input (search icon, `aria-label="Filter rows"`) and chips All / Overridden / Redacted / Company as toggle buttons (`aria-pressed`), 28px, radius 6px. Filter matches kv key and value text and table cell text, case-insensitive; chips match flags (`overridden` also matches rows with an overridden contribution). A count line announces "n of m rows" when filtered.
- Unavailable sources: a warn note per collector above the cards, "Title: unavailable (reason)". Not-present sources: one muted line at the end, "Not on this machine: A (reason), B".
- One card per section: `surface`, 1px border, radius 8px, 40px header (14/20 600) with the collector notes below in muted 12px; block titles that differ from the section title render as a 32px sub-header inside the card.
- Other: one group per unknown view id with the id as a mono h2.

### kv rows

- Grid columns (wide): key 280px, value 1fr, lineage 84px, flags/source 220px; 32px high (44px coarse), 1px bottom rule, hover background; keys and values mono 12.5/18 with ellipsis.
- Selection by click or Enter/Space: `hover` background plus a 2px accent inset on the left, `aria-expanded` on the row button. Expanded panel (inline, `raised`, padding 10/16/12/30): the full route (four 20px nodes joined by 12px connectors, dashed into overridden layers), then one contribution line per layer (glyph, layer label, value, status word; overridden values struck), then the source chip "Open file" when the source has a file.
- Collapsed rows still show "Overrides `<value>` from `<layer>`" under the value when a contribution was overridden (redacted rows show a redacted marker instead of the value).
- Values: string (clamp at 240 chars with Show more), number/boolean literal, `[]`, scalar list (first 10, Show all n), JSON pretty (first 12 lines, Show more), "not set", redacted (eye-off icon, `[redacted]`, screen-reader "value redacted").

### Lineage strip

Four 18px nodes (M L P U), 5px radius, 1.5px border in the layer color, 2px gap, JetBrains Mono 700 10.5px glyph. Winner (`applied`): filled with `layer-fg` glyph. `overridden`: outlined, glyph struck. `merged`: half filled diagonally. Not set: dotted `border-strong` border, muted glyph. Layer from the contribution source label: contains "managed" → M, "local" → L, "project" → P, "user" → U. Rows whose contributions map to no layer show no strip. `role="img"` with `aria-label` "Effective from project (local); overridden: user", tooltip with the contributing values (not for redacted rows).

### Table blocks

Semantic `<table>` with a screen-reader caption, sortable headers (button, cycle ascending, descending, original; `aria-sort`), 32px rows with rules, mono for link cells and path-like strings, numbers right-aligned tabular. First cell opens the row file (or the row detail); a second link to the same file renders as text. Empty cells "-" with screen-reader "empty". Flags column only when any row has flags. First 50 rows, then "Show all n rows".

### Tree blocks (instruction chain)

- Header when the block has a summary: "Loaded for `<targetDir>`" muted 12px, the total 28/32 600 tabular "4,600" plus "tok est." muted, and a segmented bar (10px, radius 5px, 2px gaps) with one segment per counted node that has `meta.tokens`, sized by share, accent at decreasing opacity. Below it the summary line "4 files · 18.0 KB · about 4,600 tokens (estimate)".
- Tree table: `#` (order), path (mono, 16px indent per level, disclosure button "Children of X" with `aria-expanded`), flags, share bar 96x6 (accent 70%) when the node has tokens, size and tokens right-aligned. Lazy and referenced nodes are faint and "not counted". An error node shows a dashed danger outline in place of the bar and its error text in danger. Expand all and Collapse all when any node has children. Top-level nodes with children start expanded.

### Badges and flags

22px pills, 12px sans, icon plus label, tint background plus 1px status border. `missing-script` file-warning danger, `error` circle-x danger, `duplicate` copy warn, `overridden` strikethrough warn, `network-path` network warn, `conditional` split warn, `company` building company, `redacted` eye-off neutral, `link` link neutral, `lazy` clock neutral, `referenced` file-symlink neutral, `loaded`/`applied` check ok; unknown flags neutral with no icon. Text is always shown.

### Collector state chip

Dot plus word: active (ok), unavailable (warn), not on this machine (muted). Never color alone.

### Overview (bento)

4 columns, 12px gap, tiles 12px radius on `surface` with border, 16px padding, no shadows, no accent rings.

- Identity 2x1: icon plus "Identity" label, then a mono line: hostname (700), OS description and arch, tailnet DNS name and tags, collected time, cfgdash version. The h1 is "Overview" with "hostname · collected 10:00" muted beside it.
- Health 2x2 (full width below 1024px) after Identity: icon plus "Health" and a "View all" link, the severity bar with the three counts, then the top two findings as ruled rows (severity icon plus word, title) linking to Health. "Checking..." while loading, "Couldn't load findings" on error.
- Count tiles 1x1 for every visible view except Overview and Health: icon plus label (12/16 muted), number 32/36 600 tabular, whole tile is the link. A tile whose view has an unavailable source shows a warn dot and "1 unavailable".
- Collectors 2x1: "n of m active", then each non-active source as a row (mono title, state chip, reason, link to its view).
- Context paths: a full-width card below the bento with A-style rows: key (mono), value (mono, or "not set" muted), source (muted), "missing" badge when the path does not exist; first row "Target folder".
- Then the overview sections as cards.
- Motion: after a refresh (never on first paint) changed numbers tick for 600ms and the tile border pulses once in accent; reduced motion shows a static "changed" dot until the next refresh.

### Health

- Data: `GET /api/v1/findings?dir=` after every new snapshot (load, folder change, refresh); the last findings for the same folder stay visible while a refresh's findings load.
- Page head (h1 "Health", "n findings", description), then the severity bar: thin 8px segmented bar (critical `danger`, warning `warn`, info `border-strong`, 2px gaps, zero parts omitted, decorative) with the counts as text "1 critical · 7 warning · 4 info"; "No findings" when all are zero.
- Chips All / Critical / Warning / Info (`aria-pressed`, pressing the active chip returns to All) with an announced "n of m findings" line.
- One card with a ruled table: Severity (icon plus word), Title (button, opens the sheet), Where (mono, "Section › Block › Item", or the collector title for collector-level findings). Stacked cards below 768px.
- Finding sheet (right, as the file sheet): title (h2, focus target), severity and rule id, detail text, Where, "Show in <View>" (closes the sheet, then jumps to the evidence row with the search-hit flash and focus), "Open file" when the evidence has a file (closes the finding sheet, then opens the file sheet; focus returns to the row), Close.
- States: "Checking this harness..." loading, "Couldn't load findings" with Try again, "No findings. Nothing in this snapshot needs attention."

### Usage

- Lazy route. Page head with the description and the collector notes.
- Totals tiles from the `totals` kv (Cost today, Cost 7 days, Cost 30 days in USD with 2 decimals; Tokens 30 days compact; Sessions; Active time when present), 2-up at 375px, 3 across from 640px, 6 across from 1536px; each tile is a jump target (`<collector>/totals/<key>`). Redacted or null values keep their marker. A muted "Source host:port · range" line below.
- "Cost per day (USD)" card: plain SVG columns, oldest left, one accent hue, no legend; columns at most 24px wide with a 4px rounded top and a square baseline; hairline gridlines, "USD" y label with 3 to 5 clean ticks, dates every 7th day plus the last; hover tooltip (value strong, date muted). The svg is `role="img"` with a summary label; the by-day table right below is the keyboard and screen-reader path. No chart when no day has a cost ("No cost recorded in this range.").
- Then by-model, by-machine, by-project and any other block through the generic table block, each in its own card, only when present.
- States: unavailable shows the warn note with the reason and no tiles; not present shows "Not on this machine: title (reason)".

### Settings editor (edits-v1)

- Lazy route `/edit`, h1 "Edit settings" with the target folder below. Data from `GET /api/v2/settings?dir=`; 404 says editing is only available from this machine's browser.
- Left: "Search settings" input and the category nav (200px, sticky; a `<select>` below 768px). Search filters rows by label, key and description across categories, grouped by category; "No settings match".
- Scope switch above the cards: a radio group of the scopes the shown rows use, in order User, Project, Local, Machine (the Config repo category shows only Machine). Below it "Writes to <path>", plus "not created yet" and "tracked by git" notes.
- One D card per category, one row per setting: label, key (mono, muted), description; on the right the control for the selected scope (switch for bool, select with "Not set" for enum, number and text inputs, a one-item-per-line textarea for lists), the effective value with its source, "Overridden by <Scope>" when a higher-precedence scope wins, "Restore default" (stages an unset) and Undo for a staged row. Read-only rows show the value and the reason in words (secret, can run code, not in the catalog, scope unavailable, unsafe link) and no control; a redacted value is never put into a control.
- Staged changes: a "Changed" pill per row and a sticky bar "n changes staged" with Discard and "Review changes".
- Review: "Review changes" (dry run) replaces the cards with one diff card per file: `raised`, 8px radius, 40px sticky header (path, scope pill, "new file", "dry run", "tracked by git", +n −n, collapse toggle), the reformat note when the diff starts with `# cfgdash: file will be reformatted`, then the diff with two 40px line-number gutters, a +/- column and ok/danger tints (screen-reader "added"/"removed"). The floating Apply bar (64px raised card, above the narrow tab bar) holds "Back to editing" and "Apply n changes".
- Apply sends the proposal's confirm. Success: "Applied. Audit entry <id>." and the snapshot refreshes. Stale, mismatch, expired, forbidden, validation (inline on the row, in words) and write failures show as a focused alert at the top and keep the staged changes; "Review again" where it helps.

## Sheets and dialogs

- File and detail sheet: right side, 560px (full width below 768px), `raised`, shadow, spring (stiffness 400, damping 34) or 160ms ease-out. Header: mono name (h2, focus target), path, kind, size, tokens, company badge; Copy path (feedback "Copied" or "Couldn't copy", announced) and Close. Body: truncated notice, Wrap lines toggle (`aria-pressed`), an ordered list of lines with line numbers in a 40px gutter, the target line highlighted and scrolled into view. Loading, 404 ("This file isn't available for the selected folder.") and other errors with Try again. Escape and the scrim close it; focus returns to the trigger.
- Folder picker: centered dialog 560px, `raised`, radius 12px: path field with Open, suggestions, "Folders in `<path>`" list with Up one level, filter when more than 20 entries, hidden folders muted, footer Cancel and "Use this folder" (primary).
- Command palette: Ctrl/Cmd+K (and `/` outside text fields), 640px dialog at 12vh, radius 12px, 52px input (15px), groups Views, Folders (suggestions plus "Choose a folder..."), Search results (after 2 characters, debounced 200ms, top 8 hits plus "Show all results"), Actions (Refresh, toggle theme). 36px items with 8px radius, selected item accent tint. Above 768px a 240px preview pane on `surface` shows the selected item's description, or for a kv hit its value and lineage route. Footer hints `↑↓ move, ↵ open, esc close` in mono 11px. Matches underlined.

## Motion

Inside views: 100ms linear hover, focus and selection; 400ms row tint flash for a jump target (from search or palette). Overview: tickers and one pulse after refresh. Sheet and palette: springs. Nothing ambient. `prefers-reduced-motion: reduce`: every transition 0ms, no springs, no tickers, no pulse, no flash (the jump target keeps a static highlight for 2s and the overview a "changed" dot).

## States

Every async surface designs loading (spinner plus text, `role="status"`), empty, error with recovery, and content:

- Snapshot loading: "Reading this machine's harness..."; refresh keeps content and shows a spinning refresh icon; a failed refresh shows a notice "Couldn't refresh. Showing the snapshot from 10:00." with Try again.
- Snapshot errors: bad dir ("This folder can't be used", the path, "Back to the start folder" link, "Choose another folder"), unreachable ("cfgdash isn't answering"), other ("Couldn't load the snapshot"), each with Try again.
- Access panel (full page, replaces the shell): signed out, forbidden with reason, bad host; Try again.
- One polite live region announces loads, refreshes, folder changes, search results and failures.

## Accessibility

Skip link; one h1 per page focused on route change (not when following a fragment), after a folder change settles and when the access panel appears. Visible focus everywhere. Keyboard reach for nav, rows, sheets, dialogs and the palette; Escape closes; focus returns to the trigger. Native elements first (`button`, `a`, `table`). Contrast as above in both themes. Zoom never disabled.

## Rejects

Card grids for lists, connectors and rings in dense rows, ambient motion, gradients, glow, uppercase tracked labels, accent on anything that is not action, focus, active nav or token bars, color as the only carrier of meaning.
