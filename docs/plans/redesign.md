# Plan: UI redesign in React (ROADMAP 10)

Owner: department lead. Status: Gate 1 approved by Nico on 2026-11-01 with sequencing A, light and dark themes following the system, and the Health view in scope. Phase 1 (directions) running.

## Goal

Replace the Angular UI in `web/` with a React UI that is more interesting to look at, more comfortable to move around in and more engaging, without losing any current behavior. Design runs through the `impeccable` skill (`shape`, `craft`, then `audit` and `polish`) and the `plan-visual-design` gates; components take ideas from the designer's free sources (`gather-design-references/references/source-catalog.md`).

Surface mode: **Operate**. cfgdash is a dense tool someone opens to answer "what does this machine load, and why". Character comes from hierarchy, type, color by role and a few signature components, not from decoration.

## Facts this plan builds on

- Today: Angular 22, SCSS tokens in `web/src/styles.scss` (dark only), Vitest with jsdom, about 3.4k lines in `web/src` and 50 UI tests. The production JS is 334 KB (100 KB gzip).
- The host embeds `web/dist/cfgdash/browser/**` as resources (`Cfgdash.Host.csproj`, `CfgdashWebDist`).
- CSP from security-v1: `default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:`. No inline scripts, no CDN and no web fonts from elsewhere; inline styles (Motion) are allowed.
- The UI renders the generic `kv`, `table` and `tree` blocks, so new collectors (T3, Codex, OpenCode) need no UI code. Only the Usage view (3) and the Settings screen (7, edits-v1 "UI" section) are bespoke.
- None of the v0.3 team branches (`v1-usage`, `v1-collectors`, `v1-edits`) has commits yet.

## Stack

| Concern | Choice | Why |
|---|---|---|
| Framework | React 19, TypeScript strict, Vite | Nico's pick; designer default |
| Styling | Tailwind CSS v4 with CSS-variable tokens | The designer's sources are Tailwind; tokens stay semantic |
| Primitives | shadcn/ui (copied into `web/src/components/ui`, Radix primitives) | Accessible primitives we own as source, no runtime dependency on a kit |
| Motion | Motion (`motion/react`), honoring `prefers-reduced-motion` | Designer default; inline styles fit the CSP |
| Routing | React Router, keeping today's URLs and query state (`url-state.ts`) | Deep links and the folder `dir` param keep working |
| Icons | lucide-react | shadcn default, ISC license |
| Fonts | Self-hosted variable fonts embedded in the bundle (for example Geist or Inter for UI, JetBrains Mono or Geist Mono for paths and values), OFL only | CSP and offline use |
| Data | Port `api-client.ts` and `models.ts` as-is (framework-free TS) plus small hooks; no query library unless a slice proves the need | Smallest change |
| Tests | Vitest, jsdom, Testing Library; every kept behavior keeps a test that passes the test value bar | Same runner as today |

No other runtime dependencies without a line in this plan. Budget: initial JS at most 200 KB gzip, with heavy views (Sessions, Memory, Usage, Settings) split by route.

## Information architecture

- **Shell:** a left sidebar grouped as *Machine* (Overview, Health once ROADMAP 12 exists), *Harness* (Settings, Instructions, Agents, Skills & commands, Hooks, MCP & permissions, Environment, Jobs, plus Codex/OpenCode/T3 when present) and *Activity* (Sessions, Usage, Memory), then Other. A header with the machine name, the folder (target dir) switcher and the edit-settings button (only when health says `edits: true`).
- **Command palette (Ctrl/Cmd+K):** jump to a view, switch folder, full-text search (today's search page becomes palette results plus a full results page).
- **File and detail content** open in a side sheet with the redacted file, its source layer and a copy-path action, instead of replacing the page.
- **Signature components** (where the character comes from):
  1. *Layer lineage:* for every effective value, a compact stack showing managed > local > project > user with the winning layer lit and overridden layers struck through. This is cfgdash's core idea made visible.
  2. *Instruction chain:* the load-order tree with a token bar per file and the total against the context budget.
  3. *Flags as status:* `missing-script`, `error`, `duplicate`, `overridden` and `company` get one consistent badge language and color role.
  4. *Diff review:* the edits "Review changes" step as a real unified-diff viewer with per-file headers and the confirm action pinned at the bottom.
  5. *Overview at a glance:* machine identity, counts that link into views, unavailable collectors with their reason, and the health count.

## Component sources

Free items only (catalog rules): never Pro or paid, check each license, port the idea into our tokens, keep keyboard, contrast and reduced motion working. Every copied component gets a line in `web/THIRD_PARTY.md` with source URL and license.

| Need | Look at first |
|---|---|
| Sidebar, command palette, sheet, tabs, table, tooltip, switch, select, dialog, skeleton, toast | shadcn/ui |
| Component behavior and states | The Component Gallery |
| Dashboard density, light/dark colorblind-safe tokens (reference only, no license) | AI Driven Dashboard Component Library |
| Number tickers, subtle borders and highlights on the overview | Magic UI (MIT) |
| Diff and code viewers, data tables | 21st.dev (per-component license) |
| Loaders, toggles, small micro-interactions | Uiverse (MIT), React Bits (MIT + Commons Clause, fine inside the app) |
| Primitive alternatives if Radix falls short | Wensity UI / Base UI (MIT) |
| Settings screen layout | T3 Code's settings (edits-v1 names it), Mobbin for comparable developer-tool settings |

Rejected for this surface: animated backgrounds, shaders, 3D, scroll-driven storytelling (route: conventional frontend, because this is a dense tool used for lookups).

## Phases and gates

**Gate 1 (Nico): this plan and the sequencing decision below.**

1. **Research and directions** (@visual-design-planner, no product code). `impeccable init` writes `web/PRODUCT.md` and `web/DESIGN.md` from repo facts. `gather-design-references` with the sources above. Output: one static HTML file with three directions on fake data, each showing the shell, Settings with lineage, and the instruction chain at desktop and 375 px. Seed directions:
   - *Instrument panel:* dense, mono-forward, keyboard-first, near-black with one accent and status colors only where state lives.
   - *Field guide:* calmer and editorial, generous type, lineage and load order as the visual centerpiece, light and dark.
   - *Control room:* bento overview with live counts and a stronger accent, more motion on state changes, denser views behind it.
   **Gate 2 (Nico): pick or combine a direction.**
2. **Storyboard and blueprint** (@visual-design-planner). Frames: Overview, a settings view with lineage, Instructions, Sessions table, file sheet, command palette, Settings edit with diff review and stale/mismatch errors, Health, narrow width, empty/unavailable/error states. Then the implementation blueprint with tokens. **Gate 3 (Nico): approve the storyboard revision.**
3. **Foundation** (@designer, branch `v1-web`, owns all of `web/`). React/Vite/Tailwind/shadcn scaffold, tokens and fonts from `DESIGN.md`, ported `api-client`, `models`, URL state and access token handling, the shell and the generic `kv`/`table`/`tree` blocks. Keep `web/mock` as the dev backend. Host: point `CfgdashWebDist` at the new `dist` path; nothing else in the host changes.
4. **Parity** (@designer + @tester). Every current behavior, with its tests ported: all views and the Other fallback, folder picker, search, file viewer, detail seam, flags, redacted values, collector states and reasons, idle and access handling, deep links. Angular is deleted in the same branch once parity holds; the two UIs never ship side by side.
5. **New surfaces.** Command palette; Settings screen against edits-v1 and its mock endpoints; Usage view against ROADMAP 3's blocks; Health view; its findings come from ROADMAP 12 under a `health-v1` contract the department lead writes before phase 5.
6. **QA and review.** `impeccable audit` then `polish`; `ui-ux-qa` checklist; @e2e-runner on the real single-file exe (embedded UI, real CSP) for the main journeys and the edit flow; @code-reviewer on the diff; @security-reviewer on the Settings screen (confirm flow, nothing unredacted rendered, `X-Cfgdash-Request` and origin handling, no new CSP exceptions). Release as a minor version.

## Sequencing against v0.3 (decision for Gate 1)

| Option | What happens | Cost |
|---|---|---|
| **A (recommended)** | v0.3 teams build only core and host (collectors, `/api/v2`, usage blocks) and add no Angular code. The `web` team owns `web/` and builds Usage and the Settings screen directly in React. | Usage and Settings UI arrive with the redesign, not before |
| B | v0.3 ships in full in Angular first, then the redesign | The Settings screen and Usage view get built twice |
| C | Redesign first, v0.3 waits | Slowest path to the v0.3 features |

Option A changes edits-v1's "producer: team `edits` (core, host, web)" to core and host only, with the web part moved to the `web` team; the department lead updates that contract line when Nico picks A.

## Acceptance

- Every behavior in phase 4's list works in the exe on Windows and Linux, and its tests pass.
- `npm ci && npm test && npm run build` and `dotnet build`/`dotnet test` pass with warnings as errors.
- WCAG 2.1 AA: keyboard reach for everything including the palette and sheets, visible focus, contrast in every state, reduced motion respected. Usable at 375 px.
- Initial JS at most 200 KB gzip; CSP unchanged; no network requests outside the cfgdash origin.
- Light and dark themes follow the system, with a manual toggle.
- `THIRD_PARTY.md` lists every copied component and font with its license.

## Risks

- **Generic AI look:** shadcn defaults plus Magic UI effects can make it look like every template. The gates and `impeccable critique` exist to catch this; effects are limited to state changes and the overview.
- **Parity regressions:** the Angular specs are the checklist; port behavior before restyling.
- **Large tables:** Sessions and Memory can be long. Measure first, add `@tanstack/react-virtual` only if scrolling drops frames.
- **License drift** on copied components: recorded per item in `THIRD_PARTY.md`.

## Decisions (Nico, 2026-11-01)

1. Sequencing A: v0.3 teams build core and host only; team `web` builds Usage and the Settings screen in React.
2. Light and dark themes, following the system, with a manual toggle.
3. The Health view (ROADMAP 12) is part of the redesign and is designed in from the start.
