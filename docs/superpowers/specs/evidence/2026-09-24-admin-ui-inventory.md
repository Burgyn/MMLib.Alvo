# MMLib.Alvo.Admin — UI inventory

Root: `src/MMLib.Alvo.Admin/Components/`. All paths below are relative to the repo root
`/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo/`.

---

## 1. Per-screen inventory

Legend for "where": **Sheet** = `DesignSystem/Sheet.razor` overlay (bottom sheet on phone, centred
dialog on desktop); **inline panel** = expands in place inside a `Panel`/`ListRow` without navigation;
**top strip** = appears above the list content, pushing it down, without an overlay; **separate page**
= its own `@page` route; **in-row** = controls live inside the `ListRow` itself.

### Shell / cross-cutting (not routed pages)

| Component | Job | Interactions | Where | Confirm | Success feedback | Errors | Loading | Empty | Keyboard | Scroll | Cite |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `AdminLayout.razor` | Sidebar+header+content shell, mounts assistant | Search button opens palette; theme toggle; mobile "more sections" sheet | Sheet (mobile nav), separate `CommandPalette` overlay | n/a | n/a | `ErrorBoundary` → `ErrorPanel` with "Reload" action | n/a | n/a | none itself; delegates | n/a | `Shell/AdminLayout.razor:1-179` |
| `CommandPalette.razor` (⌘K) | Jump to section/entity/action | Type to filter; Arrow/Enter/Escape; click row | Modal overlay (own scrim, not `Sheet`) | none | navigates away | nothing (silently drops entity list on 403) | n/a (loads on open) | "Nothing matches" row | ⌘K opens, ↑↓ moves, Enter selects, Esc closes, focus-and-select on open | scroll lock while open | `Shell/CommandPalette.razor:1-236` |
| `NavList.razor` / bottom nav | Navigate sections | Click | separate pages via links | none | navigation | n/a | n/a | n/a | none explicit | n/a | `Shell/NavList.razor` |
| `PendingBar.razor` | Show unapplied-change count everywhere | Discard / Preview | Discard opens `DiscardSheet` (Sheet); Preview is a link | ConfirmByName-less; discard sheet has its own danger confirm | bar disappears | n/a | n/a | hidden when 0 pending | none | n/a | `Shell/PendingBar.razor:16-32` |
| `ThemeToggle.razor` | Light/dark | Click | in-row header button | none | icon flips | n/a | n/a | n/a | none | n/a | `Shell/ThemeToggle.razor` |
| `ProjectSwitcher.razor` | Show project+revision+pending | none (label only, by design — API serves one project) | n/a | n/a | n/a | rendered inline as "…", never thrown | n/a | shows "…"/"no project" | n/a | n/a | `Shell/ProjectSwitcher.razor:19-32` |
| `SignedInAs.razor` | Who + sign out | Sign out | in-row form post | none | full navigation (redirect) | n/a | n/a | n/a | none | n/a | `Shell/SignedInAs.razor:20-27` |
| `AssistantDrawer.razor` | AI schema-change chat, mounted globally | Ask; open proposal in Preview | inline drawer (toggle button + panel, not a Sheet) | none | turn appended to thread; "Saved"-style text absent (see defects) | `ErrorPanel` inline in thread | "Thinking…" button label only, no skeleton | n/a (empty thread shows nothing) | **no** Ctrl/Cmd+Enter, **no** scroll-to-latest, **input clear is broken** (see §3) | thread div has no auto-scroll | `Assistant/AssistantDrawer.razor:1-236` |

### Routed screens

| Route | Job | Data shown | Key interactions | Where | Confirm | Success | Errors | Loading | Empty | Keyboard | Scroll | Cite |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `/admin` Overview | Landing dashboard | Entity/revision/honoured-block counts, declared-limits list, latest revision | Links only (no writes) | separate page links | n/a | n/a | `ErrorPanel` full-page | 3 `Skeleton` cards | "This project is new" banner + link | none | n/a | `Home/Overview.razor:1-181` |
| `/admin/welcome` Welcome | First-run checklist | 4 steps (signed-in, entities, rules, tenant) | Links to Schema/Rules/Access/Transfer | separate pages | none | n/a | absorbed silently, greets by id | n/a | n/a | none | n/a | `Home/Welcome.razor:1-122` |
| `/admin/schema` SchemaList | List/Map of entities | Entity list w/ badges, or SVG system map | New entity, view switch (List/Map), import/export link | **New entity = top strip/inline panel above the list** (`_adding` Panel) | none (immediate stage) | entity added → navigates to its page | `ErrorPanel` full page + inline `ErrorPanel` in the add form | `Skeleton` Lg | "no entities yet" + import link | name input `FocusOnRender` on open | n/a | `Schema/SchemaList.razor:34-89,156-443` |
| `/admin/schema/{EntityName}` Entity | One entity: fields/relationships/rules/hooks/indexes/API | Tabs; field editor; rename; remove-field impact | Add/Edit field, rename entity, remove field, add index/hook, set rule | **Field editor = Sheet**; **rename = Sheet**; **remove-field impact = Sheet**; **rule edit = inline textarea in tab (no sheet)**; **index/hook add = inline panel under the list in the tab** | Remove field: Sheet with reference list, **no typed confirm, no button confirm if unblocked — a single click "Remove from the working copy"**; rename: none | field/hook/index/rule staged, badge "new"/"changed"/"removed" appears in list | `ErrorPanel` per-context (rename sheet, remove sheet, page) | `Skeleton` Lg while entity loads | "no entity called X" | WAI-ARIA tabs (arrows/Home/End move + focus), name-input `FocusOnRender` | none explicit; sheet opens over current scroll | `Schema/Entity.razor:1-202`, `Entity.razor.cs:1-487` |
| `/admin/transfer` Transfer | Export/import descriptor | Export button + `CodeBlock`; paste-to-import textarea | Download, Import | separate page, no overlay | none | navigates to Preview on import | inline `ErrorPanel` (refusal) | n/a | n/a | none | n/a | `Schema/Transfer.razor:1-131` |
| `/admin/changes` Preview | Dry-run diff + plan + apply | `DescriptorDiff`, `PlanSteps`, reason field | Plan again, Discard, Apply | Discard = Sheet (`DiscardSheet`); everything else inline on page | destructive apply = `ConfirmByName` (type project name) | "Applied as revision N" accent panel | `ErrorPanel` full page | `Skeleton` while planning | "Nothing to apply" | none explicit | n/a | `Schema/Preview.razor:1-270` |
| `/admin/rules[/{EntityName}]` Rules | Policy simulator + rules per entity (read-only here) | CEL per operation; simulated verdict | Pick entity (tabs/select), pick caller/operation | split pane (`a-split`), resizable aside | none (read-only viewer) | verdict panel updates live | `ErrorPanel` (page + simulation-scoped, with "Try again") | none (verdict panel swaps instantly) | n/a | tab strip switches to `<select>` past 6 entities | resizable split via `SplitHandle` (drag/arrow keys) | `Rules/Rules.razor:1-251` |
| `/admin/automations`, `/admin/functions` NotYet | Explain unimplemented subsystem | Framework's warned-block sentence | none (informational) | n/a | n/a | n/a | n/a | `Skeleton` while capabilities load | n/a | none | n/a | `Shell/NotYet.razor:1-69` |
| `/admin/data` DataList | List of entities to browse | Entity list, scoped badge | Click to open | separate page links | n/a | n/a | `ErrorPanel` full page | `Skeleton` Lg | n/a (only tenant-warning banner) | none | n/a | `Data/DataList.razor:1-73` |
| `/admin/data/{EntityName}` EntityData | Record grid + record form | Grid/cards, search, sort, paging | New record, open row, search, sort, page | **Record form = Sheet** | delete = single "Delete" button inside the sheet, **no typed/second confirm** | `role=status` live-region text ("Created X"/"Saved X"/"Deleted X") with **Dismiss** button, no auto-timeout | `ErrorPanel` full page + inline (in form sheet) | `Skeleton` Lg | "No rows"/"Nothing matches" + explanation of RLS empty-page behaviour | `j`/`k` row select, Enter opens, `/` focuses search — all global via alvo.js | grid row focus is scrolled into view on `j`/`k` (admin.js `focusSelected`); **record form sheet has no scroll-to-error/field** | `Data/EntityData.razor:1-133`, `.razor.cs:1-419` |
| `/admin/access` Access | Membership + access levels + roles | People list, access levels, role catalogue, credential token | Expand/collapse person row, set roles/tenant, issue token, disable, create person | **Person edit = inline expanding row (`Expanded`), not a Sheet** | Disable: single-click danger button, **no confirm at all** (destructive-ish: revokes access) | credential-token panel appears at top of page (`_token`), no dismiss/close control except navigating away | `ErrorPanel` full page, keyed by `ProblemSite.People` | `Skeleton` on people list | "no access block" empty state | none explicit | none; token panel appears at top, no scroll-to | `Access/Access.razor:1-231`, `PersonRow.razor:1-171` |
| `/admin/history` History | Append-only configuration history | Revision list + selected revision's **full JSON only** | Click revision, plan rollback, roll back | split pane; rollback plan = inline block in aside (not a sheet) | destructive rollback = `ConfirmByName` | n/a (list re-reads after rollback; selection clears) | `ErrorPanel` full page + `ProblemSite.Rollback` | `Skeleton` Lg | "Nothing applied yet" | none | resizable split via `SplitHandle` | `History/History.razor:1-192` |
| `/admin/settings` Settings | Build info, docs links, AI connection, API keys | Version/mode/provider; AI kind/endpoint/model/key form | Save AI connection | inline panel, no overlay | none | "Saved." text next to button (no auto-dismiss, stays until next save attempt) | inline `ErrorPanel` (`_saveRefusal`) | `Skeleton` | n/a | none | n/a | `Settings/Settings.razor:1-204` |
| `/admin/integrations` Integrations | Webhooks/templates declared, and why no "new endpoint" | Raw JSON of each block via `CodeBlock` | none (fully read-only) | n/a | n/a | n/a | n/a | `Skeleton` | "Not declared" per block | none | n/a | `Integrations/Integrations.razor:1-90` |
| `/admin/sign-in` SignIn | Auth | Email/password form | Submit | separate static page (real POST, not enhanced) | none | full navigation | inline `ErrorPanel` on `?failed=true` | n/a | n/a | Enter submits (native form), autofocus on email | n/a | `Shell/SignIn.razor:1-82` |

---

## 2. Pattern census

### (a) Editing an item — **4 distinct patterns, no consistent rule**
1. **Sheet (modal)**: field add/edit (`FieldEditor`), entity rename, remove-field, record create/edit (`RecordForm`), overflow menu (`PageHeader`'s mobile sheet).
2. **Inline expanding row** (no overlay, toggled open in place): Access → `PersonRow` (roles/tenant/token/disable all appear under the row when "Change" is clicked).
3. **Inline panel/list under the tab content** (not a sheet, not a row-expand — a form permanently rendered at the bottom of the tab): add-index (`Indexes.razor`), add-hook (`HooksTab.razor`), add-rule (`RulesTab.razor` — one `<textarea>` per operation, saved on blur via `@onchange`, no explicit Save button).
4. **Top strip above the list** (a `Panel` inserted above the collection it edits, not overlaying anything): "New entity" form on `SchemaList.razor:34-89`.

So a field is edited in a Sheet, a rule is edited inline with no button, an index/hook is added in a permanently-visible inline form, and a person's roles are edited by expanding the row. Four different affordances for "change something," with no shared visual language between them beyond the raw HTML control types.

### (b) Creating an item — **3 patterns**
1. **Sheet**: new record (`RecordForm` with `RecordId == null`), new/edit field.
2. **Top strip / inline panel above the list**: new entity (`SchemaList.razor`).
3. **Inline row at the bottom of a panel** (`a-panel__foot`, not a sheet, not a strip): "Add a person" on Access (`Access.razor:63-77`) — one email input + Create button sitting directly under the person list.

### (c) Confirming a destructive action — **4 patterns, deliberately inconsistent by design comment, but inconsistent in coverage too**
1. **`ConfirmByName`** (type the project's name): schema apply with destructive changes (`Preview.razor:88-92`), rollback with destructive changes (`History.razor:83-86`).
2. **`DiscardSheet`** (danger button + Cancel, no typed name): discarding the whole working copy — reachable from `PendingBar` and `Preview`.
3. **Single click, no confirmation of any kind**: removing a field once unblocked (`Entity.razor:77-81`, "Remove from the working copy" — one click), removing an index (`Indexes.razor:57-60`), removing a hook (`HooksTab.razor:83-89`), **deleting a data record** (`RecordForm.razor:85-87`, one "Delete" button), **disabling a person** (`PersonRow.razor:100-104`, one click flips access).
4. **Nothing at all**: `Access.razor`'s "Disable" and "Let them back in" toggle is a single click with no undo affordance beyond clicking it again.

So the only two actions that get a real confirmation ritual are schema-apply and rollback (both funneled through the same `ConfirmByName`/`RollbackGate`); every other destructive action in the app (delete a record, disable a person, remove a field/index/hook) is one click.

### (d) Success feedback — **5 patterns**
1. **Accent panel with a link onward**: applied-revision panel on Preview (`Preview.razor:126-137`), Overview "this project is new" banner.
2. **Live-region status line with a Dismiss button, no auto-timeout**: record create/update/delete on Data (`EntityData.razor:83-92`).
3. **Plain inline text next to the button, never dismissed, survives until the next click**: "Saved." on Settings (`Settings.razor:134-137`).
4. **Navigation to another screen** (the "feedback" is arriving somewhere else): adding an entity → jumps to its page; renaming an entity → jumps to new URL; importing a descriptor → jumps to Preview.
5. **Nothing rendered at all, only a badge appearing in a list later**: adding a field/index/hook/rule — the only feedback is a "new"/"changed" badge appearing next to the row once the sheet closes or the inline form is re-read.

### (e) Error display — **consistent in shape, one exception**
All screens correctly use cascaded `AdminProblem` → `ErrorPanel` inline at the point of failure (never a toast), per `DesignSystem/ErrorPanel.razor`. The one structural inconsistency: **the Rules simulator's error is a second, independently-cascaded `AdminProblem` (`_simulationProblem`) sitting beside the page-level one (`_problem`)** — two cascade scopes on one page (`Rules.razor:14-19,90-100`), which is defensible (different failure domain) but is a second error-rendering path a reader has to learn.

### (f) Overlays — **4 distinct overlay mechanisms sharing one primitive, plus one that doesn't**
1. **`Sheet`** (shared scrim/Escape/focus/scroll-lock): field editor, record form, rename, remove-field, discard, mobile "more sections," `PageHeader`'s mobile overflow menu.
2. **`CommandPalette`** — its own scrim (`a-scrim a-scrim--palette`), **does not reuse `Sheet`**, duplicates modal semantics (Escape, focus, scroll lock) in its own code (`CommandPalette.razor:20-25,74-90`) even though the shared `ScrollLock`/`AdminInterop` helpers are reused.
3. **Inline expanding row** (Access `PersonRow`) — not an overlay at all, competes with the Sheet pattern for the same "edit this item's details" job.
4. **Assistant drawer** — a third kind of panel, permanently in the DOM once mounted, toggled by CSS-adjacent boolean, not a `Sheet`, not a palette.
5. **Split-pane aside** (History, Rules) — not an overlay, a resizable side panel (`SplitHandle`), a fifth distinct "extra content" affordance.

---

## 3. Behavioural defects (file:line)

1. **Assistant: no Ctrl/Cmd+Enter to send.** `Assistant/AssistantDrawer.razor:89-97` — the `<textarea>` has only `@oninput`; no `@onkeydown` handler at all. Confirmed globally too: `wwwroot/alvo.js:112` (`isTypingTarget`) deliberately disables every alvo.js shortcut while a textarea has focus, and no component-level handler fills the gap. The Send button (line 93-96) is the only path.
2. **Assistant: no scroll-to-latest.** `Assistant/AssistantDrawer.razor:27-58` — `.a-assistant__thread` renders turns/streaming text/tools with no `scrollIntoView`/anchor call anywhere. Confirmed by `grep -rn scrollIntoView` across `Components/`, `Internal/`, `wwwroot/admin.js` — the only occurrence is `admin.js:98` (`focusSelected`, for the **data grid**, unrelated). A long conversation leaves the newest turn off-screen with nothing to bring it into view.
3. **Assistant: input is not visually cleared after Ask (classic Blazor `<textarea>` child-content bug).** `Assistant/AssistantDrawer.razor:89-91` uses `<textarea ...>@_message</textarea>` (child content) rather than a `value="@_message"` attribute. `Begin()` at `AssistantDrawer.razor:158-167` does set `_message = string.Empty;`, but because the textarea's *content* was set once on first render and Blazor diffs the virtual text node against its own previous render (not the live DOM `.value`, which the browser mutated as the operator typed), the diff sees "old content == new content == the render before typing" and skips the DOM write — the box keeps showing what was typed even though `_message` is empty in C#. Every other multi-line/text control in the app (`FieldEditor.razor:236-238`, `RecordForm` textarea in `RecordForm.razor:101-104`, `RulesTab.razor:57-61`) correctly uses a `value="@..."` attribute instead and does not have this bug — the assistant textarea is the one outlier.
4. **History has no diff against the previous revision.** `History/History.razor:102` renders the selected revision with `<CodeBlock Json="@_selected.DescriptorJson" />` — the full document, syntax-highlighted, full stop. `DescriptorDiff` (used by `Schema/Preview.razor:41`) exists and does exactly the job (line-level diff with a gutter and `+`/`-`) but is never used here, and there is no "compare to previous" control on the revision row at all.
5. **No unsaved-changes guard when closing the field/record editor.** `Schema/FieldEditor.razor:22` (`OnClose="Cancel"`, `Cancel()` in `FieldEditor.razor.cs:168` just calls `OnClose.InvokeAsync()`), same for `RecordForm.razor:22`/`Cancel()` at `RecordForm.razor.cs:339` — clicking the scrim, pressing Escape, or clicking Close discards typed-but-unsent text with no "discard unsaved changes?" prompt, even though the sheet may hold several filled-in fields.
6. **Double-submit is possible on several forms.** `Access.razor:74-76` — the "Create" person button has no busy/disabled state beyond the empty-email guard (`disabled="@(_newEmail.Trim().Length == 0)"`); nothing disables it while `CreateAsync()` (`Access.razor:199-212`) is in flight, so a fast double-click can send two creates. Likewise `HooksTab.razor:198-200` ("Add the hook") and `Indexes.razor:106-108` ("Add the index") have no busy flag at all — contrast with `RecordForm.razor:90-92` and `Preview.razor:115-119`, which correctly gate on `_saving`/`_busy`.
7. **`RulesTab` rule editor saves on blur with no visible "saved" state and no explicit button.** `Rules/RulesTab.razor:57-61` — `@onchange` (fires on blur, not on typing) with placeholder text as the only affordance; an operator who tabs away without noticing has silently staged a change, and nothing on screen marks the textarea as dirty/saved differently from a declared rule until they navigate to the Fields tab's staged-badge system (which `RulesTab` itself does not show).
8. **Access "Disable" has zero confirmation for an access-revoking action.** `Access/PersonRow.razor:100-104` — a single click flips a person's ability to sign in; every other destructive-ish action in the app either has `ConfirmByName` or at least a `DiscardSheet`-style Cancel/Confirm pair, but this one is bare.
9. **Data record Delete has no confirmation at all.** `Data/RecordForm.razor:85-87` — "Delete" is a single click inside the sheet; there is no second click, no typed name, not even a native `confirm()`. This is the one truly irreversible data-destroying action reachable from the Data screen and it has the *least* ceremony of anything in the app.
10. **Credential-token panel on Access has no dismiss and no scroll-to.** `Access/Access.razor:19-29` — issuing a token for a person far down a long list renders the token panel at the very top of the page with no `scrollIntoView` and no close button; the operator has to scroll up to find it, then it stays there (covering "for @_tokenFor") until another token is issued or the page reloads.
11. **`CommandPalette` reimplements modal chrome instead of reusing `Sheet`.** `Shell/CommandPalette.razor:20-25` builds its own scrim/dialog/Escape handling in parallel with `DesignSystem/Sheet.razor`, which already centralizes exactly this (see design comment at `Sheet.razor:1-9`, "one overlay... a second implementation is a second set of behaviours to keep in step"). The palette is the one place that doesn't follow its own codebase's stated principle.
12. **Two independent error-cascade scopes on one page.** `Rules/Rules.razor:14-19` and `:90-100` — page-level `_problem` and simulation-scoped `_simulationProblem` are each wrapped in their own `<CascadingValue Value="...">`, meaning `ErrorPanel` instances resolve different `AdminProblem` values depending on where they sit in the tree; a reader/maintainer has to know this pattern exists rather than there being one error channel per page.

---

## 4. Logic worth keeping vs. UI to replace

### Pure logic / internal helpers — keep as-is (no `.razor`, framework-agnostic transforms over schema/descriptor JSON)
- `Schema/WorkingCopy*.cs` (WorkingCopy, .Entities, .Fields, .Hooks, .Indexes, .Rules, WorkingCopyStore) — the draft-document model (add/rename/remove field, entity, hook, index, rule; dirty-tracking; discard/replace/take). This is the backbone of "everything is staged before apply" and has no UI concerns baked in.
- `Schema/FieldFacets*.cs` (FieldFacets, .Maintained, .Maintained.Notes, .Notes) — builds/validates a field's JSON facets per type, including which facets a type may carry and which this build refuses. Pure schema-shape logic.
- `Schema/StagedChanges.cs`, `StagedView.cs`, `PendingSchema.cs`, `RollupSources.cs`, `DescriptorLens.cs` (referenced, not read directly but used everywhere), `DescriptorNames.cs`, `LineDiff.cs`, `HookBuilder.cs`, `EntityTabs.cs`, `PlanStep.cs`, `FacetNote.cs`, `FieldKind.cs`, `FieldReferences.cs`, `EntityReferences.cs`, `Entity.References.cs`, `CelNames.cs` — all pure transforms/predicates over descriptor JSON or view-model shaping, zero rendering.
- `Schema/Map/*.cs` (SystemGraph + .Reader, MapLayout, MapCentre, MapPicture, MapText, MapWords) — the layout engine for the system map (graph construction, box placement, wire routing, text fitting). `SystemMap.razor` is a thin renderer over this; the layout math is reusable regardless of what draws it.
- `Data/*.cs` (FieldLocks, FieldMasks, FocusReturn, FormFields, FormValue, GridCell, GridColumns, GridQuery, RecordDraft, RecordFormScope, RecordGridScope, RefLabels, RefPicker, RowCursor) — all pure: which fields are editable/hidden/locked, dirty-diff-only-changed-fields (`RecordDraft`), decimal/locale-safe value round-tripping (`FormValue` — fixes a documented D-8 locale bug), the label heuristic (`RefLabels`), keyset-cursor math (`RowCursor`, `j`/`k` movement without wraparound), grid column selection. None of this depends on Blazor except `RefPicker`/`RecordDraft` holding component-adjacent mutable state, and even that is UI-agnostic.
- `History/RevisionHistory.cs`, `RollbackGate.cs` — pure: revision title/author naming rules, rollback-button gating logic (plan required, destructive requires confirmation).
- `Home/DeclaredLimits.cs` — pure: intersects declared descriptor blocks with the framework's warned-block capability list.
- `Internal/*` (ComponentLifetime, ScrollLock, AdminInterop, FocusOnRender) — thin, correct, reusable interop/lifetime plumbing; keep.

### Presentation-only — safe to redesign without touching behavior
- `DesignSystem/*.razor` — the actual design-system primitives (Sheet, Panel, ErrorPanel, Field, ChipGroup, ListRow, PageHeader, SectionHead, Skeleton, EmptyState, CodeBlock, ConfirmByName, Refusal, NotYetPanel/Consequence, SplitHandle, Icon). These are the right layer to redesign from — most of the inconsistencies in §2 come from *screens* choosing different container shapes (Sheet vs inline vs strip vs row-expand) around these primitives, not from the primitives themselves being wrong.
- All `.razor` files under `Home/`, `Access/` (markup half), `History/` (markup half), `Rules/` (markup half), `Settings/`, `Integrations/`, `Shell/` (markup half), `Schema/*.razor` (markup halves; `.razor.cs` code-behinds hold real screen orchestration logic, not pure logic, but are UI-flow-specific and would need rewriting alongside any UI rebuild) — pure presentation wired directly to the gateways/session, redesign freely as long as the gateway calls and cascaded scopes are preserved.
- `Schema/Map/SystemMap.razor` — the SVG-drawing half only (the layout math in `Map/*.cs` above is the part to keep).

---

## 5. Feature gaps obvious from using it

1. **No diff view in History** (explicitly asked about) — `History.razor:102` shows the whole document, not a diff against the prior revision, even though the diffing code (`DescriptorDiff`/`LineDiff`) already exists and is used one screen over (Preview).
2. **No search/filter across the schema entity list or the rules screen** — `SchemaList.razor` and `Rules.razor` both just enumerate every entity; past a handful of entities `Rules.razor:31-42` degrades to a plain `<select>`, which is a workaround, not a search.
3. **No bulk actions on the Data grid** — every row action (edit/delete) is one row at a time via `RecordGrid.razor`; no multi-select, no bulk delete/export.
4. **No undo for a discarded working copy** — `DiscardSheet.razor:18-19` states "There is no undo" as a matter of design; combined with defect #5 above (no unsaved-changes guard on individual sheets), there are two separate ways to silently lose work with nothing to recover it.
5. **No visible history/audit of who changed *data* records**, only of *schema* revisions — `History.razor:10` says this explicitly ("An audit trail of the data is a different thing and does not exist yet").
6. **No inline validation preview for CEL rules/hooks before staging** — `RulesTab.razor:44-49` and `HooksTab.razor` both defer all validation to the apply/dry-run step; an operator only learns a CEL expression is malformed after navigating to Preview and running a plan.
7. **No "jump to the field/row that changed" from the Preview diff** — `DescriptorDiff.razor` renders a flat line diff with no links back to the Schema screen/tab that produced each hunk.
8. **Assistant proposals cannot be edited in place** — `AssistantDrawer.razor:208-215` (`Review`) replaces the whole working copy with the proposal and navigates to Preview; there's no way to accept part of a proposal or tweak it before it lands in the diff.
9. **No keyboard shortcut discovery beyond the palette** — the `g <letter>` chords (`alvo.js:150-152`) and `j`/`k`/`/` are only documented implicitly via the palette's per-entry `Shortcut` badges; there is no help/shortcuts screen.
