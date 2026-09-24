# F5 admin — architecture pass: a foundation the next screens compose rather than copy

**Why:** an architecture review of `src/MMLib.Alvo.Admin` after the UX pass
(`docs/superpowers/specs/2026-09-23-f5-admin-ux-pass-design.md`) found a strong logic layer (pure tested
`Internal/*` helpers, a locked and evented `WorkingCopy`, a correct token layer) under a thin screen layer: the
design system is CSS classes rather than components, page plumbing is copied, errors are swallowed without
logging, routes are strings (one collides), and the public surface accretes without a policy. The review — with
27 findings (F-1…F-27), file:line evidence, measurements, and a list of what deliberately NOT to do — is
committed at `docs/architecture/admin-dashboard-review.md` (Task 1 commits it) and is this plan's spec.
**Branch:** `f5/ai-agent` (PR #264), same PR as the UX pass.

## Global Constraints

- Everything in the UX-pass global constraints still binds: scope is `src/MMLib.Alvo.Admin` +
  `test/MMLib.Alvo.Admin.Tests*` (+ docs); no core changes; visual identity unchanged; `DesignTokenTests` /
  `ComponentLayerTests` green; UTF-8 BOM + CRLF for `.cs`; the `alvo-dotnet-conventions` code style (~25-line
  methods); Conventional Commits ending with
  `Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV`; never push, never switch branches,
  never dispatch subagents; do not touch a process on port 5080.
- **Behaviour-preserving.** These are refactors: every screen renders and behaves as before unless a finding says
  it is a defect (F-9 logging/ErrorBoundary, F-12 route collision, F-15 staleness, F-18 j/k). `scripts/test-ring1`
  and `scripts/test-admin-e2e` green at the end of every task.
- **Do not do** what the review's "Considered and rejected" section rejects: no component library, no CSS
  isolation or build step, no split of `alvo.css` into several links, no public page base class, no generic
  `DataGrid<T>`, no host extension API, no state library/message bus, no bUnit, no big-bang CSS rename.
- **Public API**: from Task 1 on, components carry `[EditorBrowsable(Never)]` and are documented as
  implementation; the baseline may still move (namespaces in Task 12), each commit says why.

## Task 1: Public-surface policy, parameter vocabulary, stale docs (F-10, F-7, F-27)

Commit the review as `docs/architecture/admin-dashboard-review.md` (copy of
`.superpowers/sdd/2026-09-23-f5-admin-ux-pass/arch-review.md`, add a one-line header that it is the review this
plan executes). Add `@attribute [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]`
to `_Imports.razor` (check it applies to every component; if a component is meant as a supported extension point,
say so — the review says none are). State the policy in `Properties/AssemblyInfo.cs` remarks and in the package
README if one exists (else in `AlvoAdmin.cs` remarks): the seven types are the contract, `Components.*` may change
in any minor version. Fix the two stale remarks (F-27). Settle the vocabulary for the primitives Task 5 adds —
`Title / Subtitle / ChildContent / Actions`, `OnClose / OnSave / OnAdd / OnRemove`, `Refused` for refused-facet
lists — and apply the renames on existing components where they are cheap and local (`ErrorPanel.Heading → Title`,
`EmptyState.Body → Subtitle`?, `FieldEditor.OnCancelled → OnClose`, `FieldEditor.OnAdded → OnAdd`,
`Capabilities → Refused`); leave a rename out if it would ripple through more than ~10 call sites and record why.

## Task 2: One error policy (F-9, final-review minor on inconsistent catches)

Internal `AdminProblem.From(Exception)` classifier: expected domain refusals (the management exceptions, the data
port's refusals — authorization, validation, not found, conflict, …) → a mapped title + fix (absorbing the five
per-page `Refused`/`Fix` switches: EntityData, Access, Preview, RecordForm, ErrorPanel); `OperationCanceledException`
/ `JSDisconnectedException` / `ObjectDisposedException` after dispose → dropped; anything else → logged through
`ILogger<T>` (inject where needed; the Admin currently logs nothing) and shown with a generic sentence, never
`Exception.Message`. Replace the 10 copies of the 3-type filter and the 16 bare `catch (Exception)` with it.
Wrap `@Body` in `AdminLayout` with `<ErrorBoundary>` whose error content uses `ErrorPanel` and a "Reload" action,
and logs. Unit-test the classifier. The project card's "not admitted" for a transient error (UX ledger minor)
goes through the classifier too.

## Task 3: `AdminSession` (F-8, F-17)

Internal scoped service: `CallerAsync()`, `WorkingCopyAsync()` (resolve + `Take` if unloaded, the one place),
`Follow(WorkingCopy, Func<Task> onChanged) → IDisposable` owning the disposed-during-await guard and the
`InvokeAsync` marshalling (the component passes its `InvokeAsync`), and a small per-component cancellation helper
cancelled on dispose (use it where a component already has a dispose; do not thread tokens everywhere). Convert
the 8 working-copy sites and the 4 `Changed` variants; remove `DataGateway` injections that exist only to learn the
user id. Unit-test `Follow` (guard and unsubscribe).

## Task 4: Routes (F-12, F-14)

Internal `AdminPaths` (`Overview`, `Schema`, `Entity(name, tab?)`, `Records(entity, id?)`, `Changes`, `Transfer`,
`Rules(entity?)`, `Access`, `History`, …) used by every href and `NavigateTo`; a unit test pins each `@page`
template against `AdminPaths` (reflection over `RouteAttribute`). Move Preview and Transfer off
`/admin/schema/{EntityName}`: Preview → `/admin/changes`, Transfer → `/admin/transfer` (keep the old URLs as
redirects only if trivially cheap; otherwise not). Add an e2e: an entity named `preview` (and `transfer`) is
creatable and its schema screen is reachable. Entity tab dispatch switches on `EntityTab.Slug` (or a component
type), never on the display title.

## Task 5: Design-system primitives, adopted on the three heaviest pages (F-1…F-6, F-20, part of F-22)

In `Components/DesignSystem/`: `Panel` (renders `.a-panel` + stacked section head, `Title`, `Subtitle`, `Actions`,
`ChildContent`, `Padded`), `ListRow` (+ `.a-listrow--action`), `Field` (`Label`, `For`, `Hint`, `Required`;
new `.a-hint` class so `.a-section-sub` means only a section subtitle), `ChipGroup<TValue>` (single/multi, owns
`role`/`aria-pressed`/`aria-checked` and keyboard), `Skeleton` (`Size` Sm/Md/Lg → three CSS modifiers),
`NotYetPanel` and `Refusal` (the §4.1 "warned ≠ refused" rule in one place). Add `.a-spacer` and
`a-row--gap-2/3`. Declare `@layer tokens, base, layout, components, utilities;` in `alvo.css` and move rules into
layers (teach `ComponentLayerTests.TopLevelRule` the indentation). Show each primitive in `docs/design/gallery.html`.
Adopt on **Access, SchemaList, Preview** (the most inline styles) — markup only, same rendering.

## Task 6: Adopt the primitives on every remaining screen (F-22 remainder, F-4 remainder)

All other pages/components: panels, section heads, list rows, fields, chip groups, skeletons, not-yet/refusal.
Target: fewer than 20 inline `style=` attributes left in the package (each remaining one a true one-off, with a
reason if non-obvious); every chip toggle through `ChipGroup`. Remove `p-hstack`, `.a-switcher-meta` as generic
meta → `.a-meta` (F-19). The `.a-notyet-panel__consequence` 12 px mono paragraph (UX ledger) becomes readable 13 px
text.

## Task 7: Break up the large components (F-13, F-26)

Code-behind `.razor.cs` partials for EntityData, Entity, RecordForm, FieldEditor, HooksTab. Lift `FieldEditor.Add`
/ `WriteDefault` into an internal `FieldFacets` builder and `HooksTab.Build` into `HookBuilder`, both unit-tested.
Split `EntityData` into page + `RecordGrid` (desktop table + phone cards + pager); split `Access` into page +
`PersonRow`; `NavList` for the six navigation loops in `AdminLayout`, `AdminSection.ShortTitle` instead of
`Short()`. The command palette's overlay uses the shared scrim/scroll-lock (no inline z-index). Every method
≤ ~25 lines in the touched files.

## Task 8: `WorkingCopy` split (F-16)

One `Edit(Func<JsonObject, bool> change)` primitive owning `lock` + `Touch` + `Settle`; the 15 copies call it.
Partial files per block: `WorkingCopy.Entities.cs`, `.Fields.cs`, `.Indexes.cs`, `.Hooks.cs`, `.Rules.cs`.
`SuggestReason` under the gate. `WorkingCopyStore.Forget`: call it where its doc says (after an apply) if that is
correct for a copy shared across tabs — otherwise delete it and its doc. Existing suites unchanged and green.

## Task 9: Interop service and j/k (F-18)

Internal scoped `AdminInterop`: lazy single `admin.js` import per circuit, `SubscribeAsync(name, Func<string?,
Task>) → IAsyncDisposable`, `LockScrollAsync`, `DownloadAsync`, one disconnect policy (JSDisconnected/ODE/OCE).
CommandPalette, ThemeToggle, Transfer, Sheet use it. Build design §5.5's row navigation on the data grid: `j`/`k`
move the selected row (`aria-selected`, visible focus, scrolled into view), `Enter` opens it; the `move`/`open`
events get their subscriber; `search` is the existing `/`. If a keyboard event stays unused, stop emitting it.
E2e: `j j Enter` opens the second row's sheet.

## Task 10: Cache invalidation across circuits (F-15)

The gateway's descriptor/schema cache must not outlive an apply made in another circuit (another tab, another
admin, the assistant path): invalidate on `LocationChanged`, or key the cache by the last applied revision the
store knows — choose the simpler that is correct, no bus. E2e: apply in one page, open another page (new
context/tab) previously loaded, navigate, see the new revision.

## Task 11: CSS hygiene (F-19, F-21) and the e2e selector policy (F-23)

Remove dead selectors or mark them `/* gallery-only */`; define or remove the used-but-undefined classes;
tokenise z-index (`--z-overlay`, `--z-palette`, …); pin the 720 px breakpoint with one stylesheet test (all media
queries use the same value). Normalise the names touched by Tasks 5–7 (`a-section__title/__sub`,
`a-pagehead__title`) only where the markup already moved. E2e selector policy: a guard test over
`test/MMLib.Alvo.Admin.Tests.EndToEnd/*Scenarios.cs` that fails when the count of raw `.a-*` class selectors and
`:has-text(` grows above today's (a ratchet), and migrate the scenarios this PR touched to `data-testid` / role +
name. Final-review minors: Preview copy naming `schema/project.schema.json` → operator wording; record-form ref
search skips CEL-hidden label fields for that caller.

## Task 12: Feature folders (F-25)

One mechanical move following §0.9 vertical slices: `Shell/` (layout, nav, palette, pending bar, theme, project
card), `DesignSystem/` (primitives, Sheet, PageHeader, EmptyState, ErrorPanel, CodeBlock…), `Schema/` (list,
entity, tabs, field editor, preview/changes, transfer, working copy + staged helpers), `Data/` (list, grid, record
form, refs, grid/form helpers), `Access/`, `History/`, `Assistant/`, `Settings/`. Internal helpers move next to the
feature that owns them (a truly shared one stays in `Internal/`). Namespaces follow folders; the PublicApi baseline
moves once, commit says so. No behaviour change.
