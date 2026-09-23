# F5 admin — UX & visual pass: implementation plan

**Spec:** `docs/superpowers/specs/2026-09-23-f5-admin-ux-pass-design.md` (binding). Finding ids (D-1…D-10) and
decision numbers (§4.1…§4.10) below refer to it.
**Branch:** `f5/ai-agent` (PR #264). One PR; every task commits on this branch.

## Global Constraints

- Scope is `src/MMLib.Alvo.Admin`, `test/MMLib.Alvo.Admin.Tests*`, `examples/bike-workshop`, `scripts/demo-admin`,
  and the two tests named in Task 1. **Do not change `src/MMLib.Alvo` (the core) or any other src project.**
- Visual identity stays: no new colours outside the token block in `alvo.css`, no new token values for the
  existing palette, the type/space/radius scales keep their steps. `DesignTokenTests` and
  `ComponentLayerTests` (`test/MMLib.Alvo.Admin.Tests`) must stay green; a new colour token must declare both
  themes via `light-dark()` and meet WCAG AA as those tests require.
- No component library, no JS framework. Blazor server-interactive, minimal JS in `wwwroot/alvo.js` /
  `admin.js` only.
- Public API: `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt` should not grow. New
  types are `internal` (the assembly already grants `InternalsVisibleTo` to its tests). If a snapshot must move,
  justify each symbol in the commit message.
- `.cs` / `.razor` files: UTF-8 **with BOM** and **CRLF**, like the existing files (the pre-commit
  `dotnet format` check fails otherwise). Prefer the Edit/Write tools over sed/python for these files.
- Code style: read `.claude/skills/alvo-dotnet-conventions/SKILL.md`. Short single-purpose methods (~25 lines),
  match the surrounding comment density and voice (these files explain *why* in prose comments; keep new ones
  proportionate and never narrate *what*).
- Tests: `scripts/test-ring0` after each step; `scripts/test-admin-e2e` (Playwright over the real host) **must be
  green at the end of every task that touches markup or copy** — many scenarios assert text and
  `data-testid`s. Keep every existing `data-testid`; when copy an e2e asserts changes on purpose, update the
  assertion in the same commit and say so.
- Conventional Commits (`feat(f5): …`, `fix(f5): …`); end every commit message with
  `Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV`. Never push; never touch `main`.
- Never dispatch subagents. Never switch branches.
- A running demo host may be on port 5080 (`scripts/demo-admin`); do not kill it and do not use that port.

## Task 1: Land the demo and fix the two tests it trips

The demo files exist uncommitted: `examples/bike-workshop/**`, `scripts/demo-admin`, one bullet in
`examples/README.md`. Two tests go red with a fifth positive example:

1. `test/MMLib.Alvo.Api.Invariants.Tests.Integration/DocumentContractTests.cs` — `Shipped()` pins the number of
   positive examples at 4; it is now 5. Update the pin (and any prose beside it that names the examples).
2. `DescriptorToSchemaMapperTests.No_shipped_example_declares_an_after_hook_so_pr5a_exposes_none_of_their_cel_defects`
   — its premise (no shipped example declares after-hooks) is no longer true and after-hooks are honoured.
   Read the test and its history (`git log -S` on its name) to understand what defect it was guarding. If the
   guarded CEL defects are fixed, retire the guard in favour of a positive assertion that the example's
   after-hooks compile; if they are not, make the guard exempt the bike-workshop hooks only for the reason it
   exists and say why. Do not simply delete it.

Also review `scripts/demo-admin` for Linux portability (GNU vs BSD utilities — `mktemp`, `sed -i`, `date`,
`stat`) and fix anything BSD-only. Run the affected test projects and `scripts/demo-admin --help` (or a dry
path) to verify. Commit as `feat(f5): a bike-workshop demo backend the dashboard can be judged against`.

## Task 2: Design-system foundation (CSS + fonts)

All in `src/MMLib.Alvo.Admin/wwwroot/alvo.css` plus new font assets, with the minimum markup change to keep
screens working. Decisions §4.1, §4.3, §4.8 and D-1, D-5.

1. **Fonts.** Add `wwwroot/fonts/` with woff2 files for Public Sans 500/600/700 and IBM Plex Mono 400/500,
   latin and latin-ext subsets, from the `@fontsource/public-sans` and `@fontsource/ibm-plex-mono` npm
   packages (download from `https://cdn.jsdelivr.net/npm/@fontsource/<pkg>@<version>/files/...`, pin the
   version in a comment). Add each family's `OFL.txt` licence beside them. `@font-face` rules at the top of
   `alvo.css` with `font-display: swap` and `unicode-range` per subset. Make sure the files are served as static
   web assets of the Razor class library (check the path under `_content/MMLib.Alvo.Admin/`) and that
   `AlvoAdminAssets` / any asset tests know about them if they enumerate assets.
2. **`.a-mono` is family-only.** It keeps `font-family: var(--font-mono)` and the overflow-wrap behaviour, but
   no longer sets `font-size` or `color`. Introduce `.a-code` = mono + `--text-xs` + `--dim` for the places that
   relied on the small dim look (grep every `a-mono` usage and choose per site: identifiers inside prose and
   titles → `.a-mono`; standalone small ids/routes/meta → `.a-code`). `h1.a-page-title.a-mono` must render at
   `--text-xl` on desktop.
3. **Type usage.** `.a-section-sub`, `.a-note`, body paragraphs in panels, table cells (`.a-table td` or the
   grid's cell class) and form hints move from `--text-xs` (12) to `--text-sm` (13). `--text-2xs` (11) stays for
   badges, chips, meta, eyebrows only. Do not change the token values.
4. **Section heads stack.** `.a-section` becomes a column: title on top, subtitle beneath (`gap: var(--space-1)`),
   left-aligned. `.a-section--bar` keeps a single row with the control on the right, the title/sub pair stacked
   on the left (wrap the pair if needed — update the markup of every `.a-section--bar` usage accordingly). In
   `.a-notyet-panel`, section heads are left-aligned too (only its empty-state body may centre).
5. **`.a-note` inside panels gets the panel's horizontal padding** (fix D-5 at the CSS level so every
   placement is covered: e.g. `.a-panel > .a-note { padding: 0 var(--space-5) var(--space-4) }` or equivalent —
   verify on Settings and Integrations).
6. **Tone utilities.** HTTP verb badges get a tone per verb (GET neutral/info, POST ok, PATCH warn, DELETE
   danger) using existing semantic tokens — add a modifier class and apply it on the API tab. Enum value badge
   class `.a-badge--value` (neutral, tabular) for later use by the grid. Amber `.a-badge--warn` / warn text are
   no longer used for plain explanations: change the explanatory amber blocks on Settings and Integrations to
   neutral `.a-note` (leave real warnings amber).
7. **Placeholders** use `color: var(--faint); opacity: .8` (or a new `--placeholder` token declared in both
   themes and AA-checked by the token tests) so a placeholder is distinguishable from a value.
8. **Destructive buttons.** Make sure `.a-btn--danger` exists and reads as destructive in both themes.

Verify in the running host (Playwright screenshots are fine) at 1440 and 390 widths, both themes. e2e green.

## Task 3: One page header, and the shell's small affordances

Decisions §4.2, §4.10 (theme toggle, project card), D-4.

1. Every routed page renders `Components/Shared/PageHeader.razor` instead of a hand-rolled
   `<h1 class="a-page-title">`: Overview, Schema list, Data list, Rules, Access, History, Integrations,
   Settings, Welcome, NotYet (Automations/Functions), Preview, Transfer, Entity, EntityData. Crumb/eyebrow:
   Preview and Transfer → eyebrow "Schema" linking to schema; Entity → "Schema"; EntityData → "Data". Page
   intro paragraphs become `Subtitle` (or a `ChildContent`-style intro slot if the text is rich — add a
   `RenderFragment? Intro` parameter if needed rather than stuffing markup into a string). Page actions go into
   `Primary`/`Secondary`. Keep the one `<h1>` per page and focus behaviour. Remove per-page wrapper markup and
   inline styles that only existed to imitate a header.
2. Entity and EntityData titles stay mono (`Mono="true"`) and now render at title size (Task 2 fixed the CSS).
3. **Theme toggle** (`Layout/ThemeToggle.razor`): an icon button (sun/moon from `Icons.cs`, add paths if
   missing) with `aria-label` "Switch to light theme"/"Switch to dark theme" and a matching `title`; no bare
   "Light"/"Dark" word.
4. **Project card** (`Layout/ProjectSwitcher.razor`): with exactly one project it is not interactive — no
   hover/pointer, not a button. It shows the name and the current revision; drop the data-provider type name
   (`EfAlvoData`) from the card. After an apply the revision updates without a reload (D-4): find how Preview
   applies (ManagementGateway) and raise/consume a change notification the layout already has a pattern for
   (`AssistantGateway.ConnectionChanged` is the precedent), or re-read on navigation.
5. Overview's chips: keep "Revision N applied"; replace the raw `DataProvider` / `Mode` / `StartupMode` chips
   with one quiet line (e.g. "Standalone · applies on start") — the provider type name belongs on Settings only.

e2e green (update header-related assertions deliberately).

## Task 4: The shell shows pending work; staged edits are visible; adding stays on the entity

Decisions §4.4, §4.5, D-9 (design doc §4.5 "What the shell shows").

1. **Pending bar.** When the signed-in operator's working copy `IsDirty`, every admin screen shows a slim bar
   under the top bar: "*N* unapplied change(s)" + a primary **Preview** button (to `/admin/schema/preview`) + a
   ghost "Discard" that opens the existing discard confirmation. N = number of changed top-level units
   (entities added/removed/changed, access/auth blocks changed…) computed from the working vs applied JSON —
   put the counting in `Internal/WorkingCopy.cs` (or a small internal helper next to it) with unit tests in
   `test/MMLib.Alvo.Admin.Tests/Internal`. The bar is hidden on the Preview page itself. The project card shows
   the same count as a small badge. The bar must update when the copy changes on the same circuit (the
   `WorkingCopyStore` / copy likely needs a `Changed` event; follow the existing pattern).
2. **Adding an entity or a field stays on the entity.** `SchemaList` "New entity" → navigates to the new
   entity's Fields tab; `Entity.AddFieldAsync` and `RemoveField` no longer navigate to Preview. The pending bar
   is the route to Preview. Update the New entity copy accordingly. New entity form gets a **Cancel** and no
   longer hides the entity list (render it above the list, or as a Sheet like the field editor).
3. **Staged rows are visible.** Fields, Indexes and On write render from the working copy (not only the applied
   schema), and a row that differs from the applied revision carries a small badge: `new`, `changed`, or — for a
   removed field — the row stays, struck through, with `removed` and an **Undo** that restores it from the
   applied revision. Figure out the cleanest source: `WorkingCopy.FieldsOf/IndexesOf/HooksOf` already exist.
4. The per-entity header "Preview changes" primary button is removed (the bar replaces it) — keep its
   `data-testid` on the bar's Preview button if an e2e targets it, or update the e2e.
5. **Discard confirmation** (D-7): the confirm button is `a-btn--danger`, with a **Cancel** ghost button beside
   it; the sheet's header uses the stacked section head.

Add e2e scenarios (in the existing style of `SchemaEditingScenarios.cs` / `PhoneAndKeyboardScenarios.cs`):
pending bar appears on Data after adding a field on an entity; the staged field is visible on its Fields tab
with a `new` badge; discard confirm is danger + Cancel closes without discarding. e2e green.

## Task 5: Overview, History, Access and the copy pass

D-2, D-3, D-6, D-10, decision §4.9.

1. **History newest first** (D-3) and **Overview's latest change = highest revision** (D-2). Find where
   revisions are listed (`ManagementGateway`) and sort once, in one place, descending; RevisionRow badges align
   (fixed badge width, tabular numerals).
2. **Overview "not yet" panel** (D-6): split capabilities into *not running* vs *partly running*. The data lives in
   `ManagementCapabilities` from the Management API — do not change the core; in the Admin, classify by what the
   dashboard can know (e.g. a block whose consequence the API marks partial, or — if the API gives no such
   signal — rename the heading to "Declared, with limits in this build" and the sub to "Parts of these blocks are
   not honoured yet — each says which."). Render it as a normal left-aligned panel (list rows: block badge + the
   consequence as plain 13 px text, not a mono box), below the stats, not as a centred callout.
3. **Overview earns its place**: add a row of quick links (Schema, Data, Rules, Access — with a one-line count
   each where cheap, e.g. entity count, rule count) and, when revision 1 has no reason, show "Initial
   descriptor" instead of "No reason recorded · code-first or system".
4. **Access self-row** (D-10): on the signed-in operator's own row, do not offer Grant/Remove for the tenant;
   show inline why ("You cannot grant yourself a tenant — another administrator can.") Roles chips show
   assigned vs not (selected style + `aria-pressed`). Fix label/hint/action spacing in the person editor.
5. **Copy pass** — operator-facing strings only (Razor markup), keep comments/docs:
   - Remove implementation type names from UI copy: `EfAlvoData`, `IAlvoData`, `IApiKeyStore`, `FindAsync`,
     `TouchAsync`, `IPolicyEngine`, `ManagementPolicySimulation`, "PUT /management/…?dryRun=true" style
     sentences on Preview/Rules/Settings/Transfer — replace with what the operator needs to know in one
     sentence (it is fine to keep one small "API: `PUT …`" code hint where it helps an agent/developer).
   - Preview plan steps render as sentences: map planner op names (`CreateEntity`, `AddField`, `DropField`, …) to
     "Create table `technicians`", "Add column `technicians.skill_level`", "Drop column `customers.notes` —
     destroys its data"; strip the `<- destructive` suffix (the badge already says it). Find where steps are
     rendered and where their text comes from; do the mapping in the Admin.
   - Preview "Why" placeholder: derived from the change (e.g. "Add skill_level to technicians") via
     `WorkingCopy.SuggestedReason` if it exists, else generic "What does this change do, and why?".
   - Destructive typed-confirmation input: no placeholder.
   - Settings: "Danger zone — There isn't one." section removed; API keys / AI assistant explanations as short
     neutral notes.
   - Add-field sheet: "Facets this build refuses" collapses into a `<details>` ("2 facets are not supported
     yet") at the bottom; placeholders prefixed "e.g." ; the regex hint becomes "Lower case letters, digits and
     _; starts with a letter." with the regex in `title`.

e2e green (update copy assertions deliberately, list them in the commit body).

## Task 6: The data grid reads like data

Decision §4.6. `Pages/EntityData.razor` and whatever grid/cell components it uses; `Internal/DataGateway.cs`.

1. **Ref labels.** For each `ref` column on the current page, collect distinct ids, query the target entity once
   with an `AlvoComparison(id, In, ids)` filter and `Select` = `id` + label field, and render the label. Label
   field = target's first required `string` field, else first `string`, else none (then show the first 8 chars of
   the id in `.a-code` with the full id in `title`). Put the label resolution in an internal, unit-tested helper
   (`Internal/RefLabels.cs` or similar) — pure selection logic tested without a database; the query goes through
   `DataGateway`. Respect `AlvoFilter.MaxInCandidates`. A label is a link to that record in the target's Data
   screen (open its edit sheet via a query parameter, e.g. `/admin/data/bikes?record=<id>` — implement the
   parameter on EntityData).
   A hidden/masked label field (CEL `hidden`) comes back null → fall back to the short id.
2. **Headers** humanised: `order_number` → "Order number", `bike_id` → "Bike" (drop a trailing `_id` on ref
   columns), with the identifier in `title`. Sentence case, not uppercase — change the CSS `text-transform` if
   that is where the uppercase comes from.
3. **Cells.** Enum values in `.a-badge--value`; booleans as a check/— ; decimals formatted with their declared
   scale, invariant culture, tabular numerals, right-aligned; dates/datetimes as today; short values
   (≤ 24 chars, identifiers) `white-space: nowrap`; long text truncated with ellipsis + `title`.
4. **Column choice.** Prefer, in order: label field, enums, ref columns, dates, decimals, then the rest; cap at the
   current count; exclude `json` and long `text` from the default set. Computed and rollup fields ARE eligible
   (they are the interesting numbers).
5. **Quick search** input above the grid ("Search <entity>…", `/` focuses it per design §5.5): an `ilike`
   `%term%` OR over the entity's string fields (non-hidden), debounced, through `AlvoQuery.Filter`. Resets paging.
6. **Sort** by clicking a header (asc → desc → none), through `AlvoQuery.Sort`; default sort = `created_at` desc
   when the entity is audited, else none. Keyset paging must keep working with a sort — check what
   `AlvoQuery.After` requires with a custom sort and fall back to offset paging only if the data port demands it.
7. Clicking a row opens its edit sheet (keep the Edit button for keyboard users; row gets `cursor:pointer` and is
   reachable via the Edit button).
8. Phone card view keeps working: card title = label field, other fields as today, same formatting rules.

Unit tests for the helpers; e2e scenario: on `bike-workshop`-like data… the e2e world boots `field-service`, so
assert on it: `work_orders` grid shows the customer's **name** in the Customer column, not a GUID, and search
narrows rows. e2e green.

## Task 7: The record form edits what a human can

Decision §4.7, D-8. `Components/Shared/RecordForm.razor` (+ EntityData's sheet).

1. **Ref fields** → a searchable select: a text input that queries the target entity (label field `ilike`,
   limit 20) and lists label + short id; choosing sets the id; the current value shows its label (resolved like
   the grid). Keep a way to paste a raw id (typing a full GUID accepts it).
2. **Calculated group.** Computed, rollup and read-only (incl. CEL readOnly evaluated false for the caller — if
   the Admin cannot evaluate CEL, treat declared `readOnly` of any kind as read-only in the form) fields render
   read-only at the bottom under "Calculated", formatted like the grid; never sent on save.
3. **Decimals** are rendered and parsed with `CultureInfo.InvariantCulture`, showing the declared scale
   (`21.60`). Find why `1,0` appeared (server culture) and fix the formatting at its source in the Admin.
4. **Save feedback.** After a successful save/create/delete, the sheet closes and a quiet status line above the
   grid says "Saved SO-2026-0163" / "Created …" / "Deleted …" (label field), dismissible, polite `aria-live`.
   Errors stay inline as today (design §5.5: errors are not toasts).
5. **No-op save.** If nothing changed, Save closes without a PATCH.
6. Title: "Edit <label>" with the entity name as eyebrow; the `PATCH /api/…/{id}` line moves to a small
   `<details>` "API" hint or is dropped.
7. Labels: "Order number" (humanised) with the identifier and type in the hint line, required marked with `*`
   and `aria-required`.

Unit tests for any formatting/parsing helper; e2e: edit a `work_orders` record, change its customer through the
select by typing part of a name, save, see "Saved …" and the new customer name in the grid. e2e green.

## Task 8: Small affordances and keyboard

Decision §4.10, design §5.5.

1. Entity tabs in the URL: `/admin/schema/{entity}?tab=rules` (lower-case tab slug); tab clicks update the URL
   without a full navigation (`NavigationManager.NavigateTo(..., replace: true)` or history push); reload and
   back/forward restore the tab. Tabs get `role="tablist"/"tab"`, `aria-selected`, arrow-key movement.
2. Relationships: the target entity name links to its schema page; "Pointed at by" rows link too.
3. `g` then a letter jumps to a section: `g o` Overview, `g s` Schema, `g d` Data, `g r` Rules, `g a` Access,
   `g h` History, `g i` Integrations, `g ,` Settings — ignored while typing in an input/textarea/select or with a
   modifier; implemented in `alvo.js` (or the existing keyboard handler, wherever ⌘K lives). The command
   palette lists these shortcuts next to its section items and gains two actions: "New entity" and "Toggle
   theme".
4. Rules page entity picker: when there are more than 6 entities, use a select (or the palette-style
   filterable list) instead of a tab strip; the simulator card matches the rules card width on phone.
5. Index field picker: selected chips use the selected style (`aria-pressed`), system columns listed after
   declared ones under a small "Maintained by Alvo" label.

e2e scenarios: `g d` from Overview lands on Data; reloading `/admin/schema/work_orders?tab=rules` shows the
Rules tab. e2e green.
