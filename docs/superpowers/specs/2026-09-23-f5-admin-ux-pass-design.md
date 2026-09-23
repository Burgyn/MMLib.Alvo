# F5 — the admin dashboard, a UX & visual pass against real data

**Status:** design, landed with its implementation in the `f5/ai-agent` PR (#264).
**Builds on:** `2026-09-18-f5-admin-dashboard-design.md` (§4.5 shell, §5 design system, §6.2 Playwright).
**Scope:** `src/MMLib.Alvo.Admin` only, plus a demo backend (`examples/bike-workshop`, `scripts/demo-admin`).
The core (`src/MMLib.Alvo`) is not changed by this pass.

## 0. Why this exists

The dashboard was built scenario-first against `examples/field-service` — three entities, no rows for an
operator without a tenant. Every scenario passed, and every screen rendered. What had never happened is
somebody *using* it on a backend that looks like a real one. This pass did that: a full walkthrough in a real
Chrome, desktop (1549 css px) and phone (550 css px, Chrome's minimum window), in both themes, first over
field-service and then over a new demo — a Slovak bicycle workshop (`examples/bike-workshop`, 8 entities,
every field type, rollups, computed fields, CEL masking, before/after hooks, webhooks, 6 staff accounts,
~220 rows) seeded by `scripts/demo-admin`.

## 1. Method

- **Heuristic walkthrough** of every route in §4.2's map, every sheet, the palette, the destructive
  paths, both themes, both widths — findings logged with the screen, the observation and the cause.
- **Task-based usability tests** (§3): eight tasks an operator of this workshop actually has, each scored
  *completed / completed with workaround / failed* and counted in interactions.
- **Measurements** taken in the page with script: rendered font-size distribution, header patterns,
  columns whose content is an opaque id, keyboard commitments of design §5.5 checked against behaviour.

## 2. What was found

### 2.1 Measured

| Measure | Before | Target |
|---|---|---|
| Share of rendered text at ≤ 12 px (Rules screen) | **75 %** (11 px × 17, 12 px × 35 of 70 nodes) | ≤ 35 % |
| Distinct page-header patterns across 14 routes | **4** (bare h1 · inset h1 with bar · eyebrow + mono name · crumb + h1) | 1 |
| Pages using the `PageHeader` component | 2 / 14 | 14 / 14 |
| Declared typefaces actually shipped | **0 / 2** (Public Sans, IBM Plex Mono fall back to system-ui / Menlo) | 2 / 2 |
| Ref columns on `service_orders` shown as raw GUIDs | **3 / 7** visible columns | 0 |
| Computed / rollup values visible in the record form | **0 / 4** (`labour_total`, `parts_total`, `lines_count`, `total`) | 4 / 4 read-only |
| §4.5 "N unapplied" in the shell | absent | present on every screen |
| §5.5 `g`+letter section jump | absent | present |
| Entity page title size, desktop vs phone | **11 px** vs 26 px (inverted) | 20 px vs 26 px |

### 2.2 Defects (wrong behaviour, not taste)

- **D-1** `h1.a-page-title.a-mono` renders at 11 px on desktop: `.a-mono` (declared later) sets
  `font-size: --text-2xs` and wins; the phone media query happens to restore it. Root cause: a utility
  class that sets family **and** size **and** colour.
- **D-2** Overview's "Latest configuration change" shows the **oldest** revision (`_revisions[0]` of an
  ascending list) — after applying r2 it still reads "r1 · No reason recorded".
- **D-3** Configuration history lists oldest first.
- **D-4** The sidebar's project card keeps "revision 1" after an apply until a full reload.
- **D-5** `.a-note` has no padding and is placed straight into panels, so the text runs flush against the
  panel edge (Settings: AI assistant, API keys, Danger zone; Integrations).
- **D-6** Overview files `templates` and `webhooks` under **"Declared, and not running yet … nothing runs"**,
  while the consequence text itself says an after-hook's email/webhook *is* delivered — the demo's webhook
  receiver got 12 deliveries. The heading contradicts the capability it renders.
- **D-7** "Discard every unapplied change?" confirms with a **primary green** button and offers no Cancel
  beside it — the destructive action is styled as the safe one.
- **D-8** Decimals in the record form render in the browser's locale (`1,0`, `21,6`) — the server sends
  `1.0`, but an `<input type="number">` displays it localised — while the API and every other screen are
  invariant; date and datetime inputs also opened empty; the grid drops the declared scale (`17` next to `12.9` in a
  `decimal(…,2)` column).
- **D-9** The staged working copy is invisible where it is edited: a field added to `technicians` does not
  appear on its Fields tab; only a green "Preview changes" hints that *something, somewhere* is pending.
- **D-10** Access lets an administrator press **Grant** on their own row and then refuses it with a panel at
  the top of the page — the rule (§3.7 U3.2) is right, the control offering it is not.

### 2.3 Usability and visual findings, grouped by cause

**Hierarchy & rhythm.** Section heads put the title and a paragraph-long subtitle side by side
(`.a-section` is a flex row), so nearly every panel opens with a squeezed two-column block; inside the
centred `.a-notyet-panel` it becomes an odd centred callout. Labels, hints and the next field touch each
other (New entity, Access person editor). Most chrome is 11–12 px.

**Consistency.** Four header patterns; `scoped` is green on Schema and amber on Data; HTTP verbs are all
the same grey on the API tab; enum values are plain text in the grid; rule CEL is a code block on Rules
and a four-line textarea on the entity's Rules tab.

**Data is not human-readable.** Refs are GUIDs in the grid and GUID text boxes in the form ("the id of a
bikes record"); column headers are UPPERCASE snake_case; short identifiers wrap (`SO-2026-` / `0163`); the
most meaningful values (totals, counts) are never shown; there is no search, no sort, and the order is
arbitrary.

**Flow.** Adding an entity or a field always jumps to Preview, so staging three fields is three round trips,
and New entity's own copy says "everything else is added on its Fields tab". The New entity form replaces
the list and has no Cancel. Saving a record closes the sheet without a word.

**Copy.** Implementation names leak into operator copy (`EfAlvoData`, `IApiKeyStore.FindAsync`,
`ManagementPolicySimulation`, `CreateEntity technicians`, `<- destructive`); amber — the *warning* tone —
is used for plain explanation; placeholders are indistinguishable from values and in one case equal to the
text the operator must type to confirm.

**Affordance.** The project card has a hover state and does nothing with one project; the theme toggle
reads "Light" while dark (it names the target, with no icon); the entity tabs are not in the URL, so a
reload or a shared link loses them; a ref's target on Relationships is not a link.

## 3. Usability tests (before)

| # | Task (operator of Velo Dielňa) | Result | Interactions |
|---|---|---|---|
| T1 | Add entity `suppliers` with three fields and apply | completed | 19 (Preview forced after each add) |
| T2 | Which technician is on order SO-2026-0163? | **failed** in UI (GUID; no search on technicians) | — |
| T3 | Move order SO-2026-0163 to another bike | **workaround** (copy a GUID from another table) | 12+ |
| T4 | From Data, tell whether anything is waiting to be applied | **failed** (no indicator off the entity screen) | — |
| T5 | Why is the order's total 43.50? | **failed** (computed and rollup values not shown) | — |
| T6 | Find the rules that decide who may update a service order | completed | 3 |
| T7 | Remove a field and understand what it will destroy | completed | 5 |
| T8 | Share a link to "the rules of work_orders" with a colleague | **failed** (tab not in URL) | — |

## 4. Decisions

Visual identity is **kept** (maintainer's call): the palette, the accent, the scales and the radii stay.
This pass narrows usage and fixes defects; it does not redesign.

1. **Type.** Ship Public Sans (500/600/700) and IBM Plex Mono (500/600 — the weights the stylesheet actually requests; a mono title is capped at 600) as static web assets, latin +
   latin-ext subsets (Slovak diacritics), `font-display: swap`, OFL-1.1 licence files beside them. Raise
   *usage*, not the scale: body copy and section subtitles move from 12 to 13 px, table cells from 12 to
   13 px; 11 px stays for badges and meta only. `.a-mono` becomes family-only (size and colour inherit),
   with `.a-ident` for the old "small dim identifier" look where that is what is meant. Fixes D-1.
2. **One page header.** Every route renders `PageHeader`; the crumb/eyebrow, title, subtitle and actions
   have one markup. Identifiers stay mono (they are what the operator types), at title size.
3. **Section heads stack.** `.a-section` becomes title-over-subtitle; the bar variant keeps a control on
   the right. The centred "not yet" panel keeps its centring only for its empty state.
4. **The shell shows pending work (design §4.5, previously unbuilt).** The project card carries
   "*N* unapplied" and a pending bar sits under the top bar on every screen while the working copy is
   dirty, whose one action is **Preview**. The project card re-reads the revision after an apply (D-4).
5. **Staged edits are visible where they were made.** Fields/Indexes/Hooks render the working copy; a staged
   row carries a *new*/*changed* badge. Adding an entity or a field **stays** on the entity (the pending bar
   is the way to Preview), matching what indexes and rules already do. Fixes D-9 and T1.
6. **Data reads like data.** Ref cells resolve to the target's label — the target's first required
   `string` field, else its first `string`, else a short id — fetched once per page per ref column with
   one `in` filter (≤ `AlvoFilter.MaxInCandidates`); the cell links to the target record. Enum cells are
   neutral badges; headers are humanised ("Order number") with the identifier in `title`; short values do
   not wrap; decimals honour their declared scale with tabular numerals; the grid gets a quick search over
   string fields (`ilike`) and header sort, both through `AlvoQuery` — no new API.
7. **The record form edits what a human can.** A ref is a searchable select over the target's labelled
   rows; computed, rollup and read-only fields are shown read-only in a "Calculated" group; decimals are
   invariant-culture. A save reports inline ("Saved SO-2026-0163") and a save with no change does not write.
8. **Destructive looks destructive.** Every confirm whose action loses work uses `a-btn--danger` with a
   Cancel beside it; the typed-confirmation input has no placeholder.
9. **Copy is for the operator.** Implementation type names leave operator-facing copy (they stay in
   comments and docs); planner steps render as sentences ("Drop column `customers.notes` — destroys its
   data"); the Overview "not yet" panel distinguishes *partly running* from *not running* (D-6); amber is
   reserved for things that are actually a risk.
10. **Small affordances.** Entity tab in the URL (`?tab=rules`); Relationships targets link; the theme
    toggle is an icon button labelled with its action; the project card is inert with one project; the
    self-row in Access does not offer Grant (D-10); `g`+letter section jumps (§5.5).

### 4.1 Deliberate deviations

- **Adding a field no longer navigates to Preview.** Design §4.5's pending bar was meant to be the way to
  Preview; without it the jump was the only signal, so the jump was a workaround for an unbuilt shell, not
  a decision. With the bar built, the jump goes.
- **Ref labels are a heuristic, not a descriptor key.** The schema has no `displayField`; inventing one is a
  schema change outside F5. The heuristic is deterministic and documented, and a later `displayField` would
  replace it without a UI change.
- **Grid search is a quick filter, not the PostgREST query builder.** Design §5.6 lists the grid, not a
  query builder; the full filter syntax stays in the API and the docs.
- **Prototype not followed.** `docs/design/f5-admin` is not updated to match (maintainer's call); it
  retires with the prototype suite.

## 5. Acceptance

- The §2.1 measurements meet their targets, re-measured by the same scripts.
- T1–T8 re-run: none *failed*; T1 ≤ 12 interactions.
- `scripts/test-admin-e2e` green, and extended with scenarios for: pending bar visible off the entity
  screen; staged field visible on its tab; ref cell shows a label; history newest-first / Overview shows
  the latest revision; discard confirm is danger + cancellable.
- ring2 green; the two existing tests the new example trips (`DocumentContractTests.Shipped` count,
  `No_shipped_example_declares_an_after_hook…`) updated for the reason they now fail, not silenced.
