# F5 admin — the dashboard on MudBlazor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the presentation of `MMLib.Alvo.Admin` on MudBlazor 9.10.0 under one binding interaction pattern language (spec §3), so every screen edits, confirms, reports and waits the same way, and the e2e suite asserts that behaviour rather than rendering.

**Architecture:** MudBlazor is a package dependency of the admin RCL only. Its stylesheet is imported into a cascade layer beneath Alvo's five, so Alvo's rules win while screens migrate one at a time. A thin wrapper layer (`AlvoButton`, `AlvoAlert`, `AlvoEditor`, `AlvoConfirm`, `AlvoTheme`) carries the policy MudBlazor 9 cannot set globally: defaults, roles, focus, the dirty guard, and busy submits. No wrapper takes a Mud type as a parameter. Screens use Mud directly for everything else. Every logic type stays as it is (spec D6), and only markup and the component code-behind change.

**Tech Stack:** .NET 10, Blazor server-interactive RCL, MudBlazor 9.10.0 (MIT), xUnit v3 + Shouldly + NSubstitute (unit), Microsoft.Playwright 1.56 + xUnit (e2e), Node `@playwright/test` (the prototype suite, untouched).

**Spec:** `docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md` (binding: its §3 pattern language and its §5 test rules are requirements).
**Evidence:** `docs/superpowers/specs/evidence/2026-09-24-mudblazor-study.md` (cited below as *study §n*) and `docs/superpowers/specs/evidence/2026-09-24-admin-ui-inventory.md` (cited as *inventory §n / defect #n*).

**Out of scope (named so nobody folds them in):**
- Tasks 5–8 of `docs/superpowers/plans/2026-09-24-f5-admin-audit-fixes.md`.
- `docs/todo-admin.md` §8d item 30 (a set-password page for a created person) and every other open item in that file.

A separate plan builds those on this foundation.

Also not done:
- No screen removes an entity today (`WorkingCopy.RemoveEntity` has no caller), so no remove-entity confirm is added (spec §3.2 lists one for when that control exists).
- The F5 design prototype is not rebuilt (D11).

## Global Constraints

- **Scope.** `src/MMLib.Alvo.Admin/**`, its two test projects (`test/MMLib.Alvo.Admin.Tests`, `test/MMLib.Alvo.Admin.Tests.EndToEnd`), `docs/**` and `Directory.Packages.props`. `src/MMLib.Alvo.Host` changes only if wiring needs it; no task here needs it. No core package changes: no `src/MMLib.Alvo`, `src/MMLib.Alvo.Abstractions`, or the Management/Data APIs.
- **Package.** `MudBlazor` pinned exactly at `9.10.0` in `Directory.Packages.props`. It is referenced only by `MMLib.Alvo.Admin.csproj`. No other UI package: no `Extensions.MudBlazor.StaticInput` (study §2.1) and no bUnit (spec §6).
- **D5 (hard).** No MudBlazor type appears in a public member of any component:
  - not in a parameter, a public method, or a base type;
  - never in `PublicApi.MMLib.Alvo.Admin.verified.txt`.
  - Wrapper options are Alvo enums nested in the wrapper (`AlvoButton.ButtonTone`, `AlvoAlert.AlertTone`), the same pattern `Skeleton.SkeletonSize` already uses.
  - `LibraryBoundaryTests` (Task 1) pins all of this.
- **D6 (hard).** Logic types stay verbatim:
  - `Internal/*` (only the additive interop methods named in a task are allowed);
  - `WorkingCopy*`, `FieldFacets*`, `Map/*`;
  - the Data helpers (`FieldLocks`, `FieldMasks`, `FocusReturn`, `FormFields`, `FormValue`, `GridCell`, `GridColumns`, `GridQuery`, `RecordDraft`, `RefLabels`, `RefPicker`, `RowCursor`);
  - `RevisionHistory` (one additive static method in Task 8), `RollbackGate`, `LineDiff`, the gateways.
- **D7.** These are kept, not replaced by Mud:
  - the system map, `CodeBlock`, `DescriptorDiff`, `SplitHandle` + the `a-split` pane;
  - `alvo.js`'s key map (`j`/`k`, `g`-letter, ⌘K, `/`);
  - the design tokens, Public Sans + IBM Plex Mono self-hosted from `wwwroot/fonts`;
  - the Data record form's reference combobox (`RefPicker`): MudAutocomplete has no `aria-activedescendant` (study §4.1), and Alvo's has.
- **Pattern language (spec §3) — every task implements the rows it touches, and nothing contradicts it.**
  - **Editing.** A multi-value create/edit uses `AlvoEditor`. Reading beside the content uses a non-modal pane. Inline editing is for one atomic value with an explicit commit. Never save-on-blur. Never a dialog over a dialog.
  - **Destructive actions** use `AlvoConfirm`: centred, no light dismiss, Escape = Cancel, the button names the verb. Destructive apply and rollback also need the type-the-name step.
  - **Feedback.**
    - A success gets a snackbar through `ISnackbar.Confirm(...)` (Task 1).
    - An error gets `ErrorPanel` / `AlvoAlert` in place, with focus moved to it on a failed submit.
    - **No error is ever a snackbar.**
  - **Forms and keys.**
    - Enter submits a single-line form.
    - Ctrl/Cmd+Enter submits a multi-line one.
    - Escape closes the topmost dialog, but asks "Discard your changes?" first when it holds unsaved changes.
    - The submit button is disabled and shows progress while busy; Cancel stays enabled.
    - Focus goes to the first field on open, and back to the trigger on close.
    - A message box is cleared after it is sent.
  - **Lists and new items.** A new item appears in place. A growing thread follows its newest item unless the operator scrolled up.
  - **Loading, empty, error.** `MudSkeleton` for loading, one sentence plus one action when empty, and the `ErrorBoundary` panel for failures.
  - **Navigation.** One app bar, one nav drawer, the pending bar under the app bar.
- **CSS.**
  - Every Alvo rule lives in `wwwroot/alvo.css`, inside one of its five layers. That covers the Mud seams (`.mud-tab`, `.mud-input-slot`, `.a-editor`) too.
  - Colours are tokens only (`ComponentLayerTests`, `DesignTokenTests`, `GalleryTests`).
  - Breakpoints stay exactly {720px, 1100px} (`StylesheetHygieneTests`).
  - Every `z-index` names a `--z-*` plane.
  - `wwwroot/alvo-mud.css` holds exactly the layer statement and the layered `@import` (Task 1), nothing else.
  - **When a task stops naming an `a-*` class, it deletes the class's rule from `alvo.css` in the same commit, or marks it `/* prototype-only */` if `docs/design/f5-admin` still renders it.** `StylesheetHygieneTests` names every class that needs one or the other, so run it.
- **The control migration rule.** Every task applies it to the files it owns, so the rule is written once, here.
  - **Buttons.** A `<button class="a-btn …">` or `<a class="a-btn …">` becomes `AlvoButton`, with the tone taken from the old modifier:
    - `a-btn--primary` → `Primary`
    - `a-btn--danger` → `Danger`
    - `a-btn--ghost` → `Ghost`
    - no modifier → `Secondary`
    - `a-btn--sm` → `Small="true"`
    - an `<a href>` → `Href`

    Keep the element's `data-testid`, `id`, `aria-*` and `title`. An `@onclick="X"` whose handler takes no argument becomes `OnClick="_ => X()"`.
  - **Text inputs.** An `<input class="a-input">` or `<textarea class="a-textarea">` becomes `MudTextField T="string"`, with:
    - `Variant="Variant.Outlined"`, `Immediate="true"`
    - `Label` = the enclosing `Field`'s label, `HelperText` = its plain-text hint
    - the **same `id`**, which lands on the native input (study §2.2 [S])
    - `Lines="n"` for a textarea
    - `InputType.Password` / `InputType.Email` where the old `type` said so

    The `Field` wrapper then goes. It stays only around a `ChipGroup` (group label) or a hint with markup in it; there, keep `Field` with its `LabelContent`/`Hint` and give the `MudTextField` no `Label`.
  - **Choices.** `ChipGroup` stays: its markup, its API and its eight call sites, per V7. A scenario picks an option by role:
    - one-of: `GetByRole(AriaRole.Radio, new() { Name = … })`
    - several-of: `GetByRole(AriaRole.Button, new() { Name = …, Pressed = … })`

    It never uses `.a-choice button:has-text(…)`.
  - **Forms.** Inside an `AlvoEditor` (a `<form>`), every plain `<button>` in the content must say `type="button"`. A bare button is a submit button and would stage the form. `AlvoButton` already renders `type="button"` unless `Submit` is set.
- **Prototype stays green.** `docs/design/f5-admin` links `alvo.css` from `src/`, so `alvo.css` must never `@import` anything. A missing import is a failed request, and the prototype's `guardConsole` fails on it. That is why the Mud import lives in the separate `alvo-mud.css`, which only the dashboard's document links.
- **Encodings.**
  - `.cs`: UTF-8 **with BOM** and **CRLF**.
  - `.razor`: UTF-8 **with BOM** and **LF** (match the neighbours).
  - `alvo.css`, `alvo-mud.css`, `*.js`: UTF-8 **without BOM**, LF.
  - Write new files with the editor tools. If Bash or python touched a `.cs`, normalise it before staging, or the pre-commit `dotnet format` check fails.
- **Code style** (`.claude/skills/alvo-dotnet-conventions`).
  - Methods stay at or under ~25 lines; extract instead.
  - XML docs on every member.
  - A comment says *why*, never *what*.
  - English only.
  - No `// X, not Y because` comments: rename instead.
- **Public API.**
  - `PublicApi.MMLib.Alvo.Admin.verified.txt` changes only by Alvo components added or removed, and their members. It never gains a Mud type.
  - Accept the `.received.txt` in the same commit, and say in the commit body which components and why ("Blazor compiles every component public; F-10: `Components.*` is implementation").
  - The turn-review hook will ask for `alvo-snapshot-judge` and for the `alvo-architecture-rules` justification of a grown baseline. **The controller dispatches those, not the implementer.**
- **E2E selectors.**
  - Use `GetByRole`, `GetByLabel` or `GetByTestId`. A scenario never adds a raw `.a-*` class selector or a `:has-text(`.
  - A task that removes selectors lowers `ClassSelectors` / `HasTextSelectors` in `test/MMLib.Alvo.Admin.Tests/EndToEndSelectorTests.cs` to the counts its failure message reports, in the same commit. The ratchet fails both when a count grows and when it falls without being lowered.
  - Mud popovers render under `<body>`, so option and menu-item lookups are page-scoped (`AdminSession.ChooseAsync`).
  - Keep every existing `data-testid`, unless a task names its replacement.
- **Commits.**
  - Conventional Commits. The message ends with the line `Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV`.
  - Stage files **by name**, never `git add -A`.
  - Never push, never switch branches, never dispatch subagents, and never touch a process on port 5080 or 5090.
- **Gates at the end of every task, in this order, all green:**
  1. `dotnet test --project test/MMLib.Alvo.Admin.Tests`
  2. `scripts/test-admin-e2e`
  3. `scripts/test-prototype`
  4. `scripts/test-ring1`

  **Task 1 and Task 11 also run a Release image build** from the repository root: `docker build -f src/MMLib.Alvo.Host/Dockerfile -t alvo-admin-mud-check .`. The rings are Debug and CI is Release, and only the Dockerfile reproduces CI's analyzer set.
- **Screenshots belong to the controller.** After each task the controller (not the implementer) screenshots every screen the task touched:
  - at 1440 px and at 390 px;
  - in the light and the dark theme.

  The task is not accepted until that review passes.

---

## File structure

What changes, by responsibility. Paths are under `src/MMLib.Alvo.Admin/` unless noted.

| Area | Files | Responsibility |
|---|---|---|
| Package + library assets | `Directory.Packages.props` (repo root), `MMLib.Alvo.Admin.csproj`, `_Imports.razor`, `Internal/LibraryAssets.cs` (new), `wwwroot/alvo-mud.css` (new), `Components/AdminApp.razor`, `AlvoAdminServiceCollectionExtensions.cs`, `AlvoAdminAssets.cs` (remark only) | MudBlazor pinned, imported under `@layer mud`, services registered inside `AddAlvoAdmin` so a host never learns about Mud |
| Theme | `Internal/AlvoMudTheme.cs` (new), `Components/Shell/AlvoTheme.razor` (new), `wwwroot/alvo.js` | `MudTheme` built from Alvo's tokens. A light provider on `:root` plus a dark one scoped to `:root[data-theme=dark]`. `alvo.js` always writes a resolved `data-theme` |
| Wrappers (policy) | `Components/DesignSystem/AlvoButton.razor`, `ButtonLook.cs`, `AlvoAlert.razor`, `AlertLook.cs`, `AlvoEditor.razor`, `EditorCloseGuard.cs`, `SubmitGate.cs`, `AlvoConfirm.razor`, `ConfirmGate.cs`; `Internal/AdminSnackbar.cs` (all new) | The §3 rules implemented once |
| Readiness + interop | `wwwroot/admin.js`, `Internal/AdminInterop.cs` (additive methods only) | `markShellReady`, `followNewest`, `copyText` |
| Shell | `Components/Shell/AdminLayout.razor`, `NavList.razor`, `ThemeToggle.razor`, `SignedInAs.razor`, `PendingBar.razor`, `CommandPalette.razor`, `SignInLayout.razor` | `MudLayout` / `MudAppBar` / `MudDrawer` / `MudMainContent` + the palette on `MudDialog` |
| Screens | `Components/{Schema,Data,Access,History,Rules,Assistant,Home,Settings,Integrations,Shell}/*.razor` | Mud composition per study §4.2, with the deviations stated in each task |
| New small logic | `Components/Schema/EntityFilter.cs`, `Components/Rules/RuleDrafts.cs`, `Components/Access/PersonDraft.cs`, one static method on `Components/History/RevisionHistory.cs` | Pure, unit-tested |
| Unit tests | `test/MMLib.Alvo.Admin.Tests/LibraryBoundaryTests.cs`, `LibraryLayerTests.cs`, `Internal/AlvoMudThemeTests.cs`, `DesignSystem/{EditorCloseGuard,SubmitGate,ConfirmGate,ButtonLook,AlertLook}Tests.cs`, `Schema/EntityFilterTests.cs`, `Rules/RuleDraftsTests.cs`, `Access/PersonDraftTests.cs`, `History/RevisionHistoryPreviousTests.cs` (all new); `Internal/AdminInteropTests.cs`, `GalleryTests.cs`, `EndToEndSelectorTests.cs`, `PublicApi.MMLib.Alvo.Admin.verified.txt` (modified) | |
| E2E | `test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs` (helpers), new `FoundationScenarios.cs`, `ShellScenarios.cs`, `EditorScenarios.cs`, `RecordEditorScenarios.cs`, `SchemaListScenarios.cs`, `RuleEditingScenarios.cs`, `PersonEditorScenarios.cs`, `HistoryScenarios.cs`, `AssistantKeyboardScenarios.cs`, `SettingsScenarios.cs`; existing `*Scenarios.cs` ported where their DOM changes | Behaviour, per spec §5 |
| Docs | `docs/architecture/admin-dashboard-review.md` (dated reversal note), `docs/design/gallery.html` | D1 note; the gallery documents tokens, Alvo's own pieces and the wrappers |

**Deviations this plan records, each with its reason. Every one also goes in the commit body of the task that makes it.**

- **V1. A second stylesheet.** `alvo-mud.css` is linked alongside `alvo.css`. The review's "one stylesheet" rejection (admin-dashboard-review.md) was about hosts having to link files in order. Here the dashboard's own document links both, and no host does. `alvo.css` itself cannot carry the `@import`, because the prototype links it (see Global Constraints).
- **V2. The Mud assets are internal.** They sit in `Internal/LibraryAssets`, not on `AlvoAdminAssets`. `AdminApp.razor` is the package's own document, so no host writes these tags, and a public property would be contract for a file that follows MudBlazor's versioning.
- **V3. The palette is duplicated from the tokens.** It is not derived: the hex values sit in `AlvoMudTheme.Colours` beside `alvo.css`, and a unit test pins them equal. Study §3.4 suggested the reverse (Alvo variables defined from `--mud-palette-*`). That would break `DesignTokenTests`' `light-dark()` contrast proof and the prototype, which has no Mud.
- **V4. The editor is full-width below 560 px, not 600 px.** One rule, `width: min(560px, 100vw)`, and no third breakpoint. `StylesheetHygieneTests` pins breakpoints to {720, 1100}.
- **V5. `a-split` + `SplitHandle` stay for History and Rules.** They are not replaced by `MudSplitPanel`. Its phone fallback and size persistence are unverified (study §8.1 spike 7). Alvo's split already has `role=separator`, keyboard resize, persistence and the 1100 px stack (`ShellFrameScenarios` pins them).
- **V6. `MudSimpleTable` for the record grid, not `MudDataGrid`.** Mud's grid rows are not focusable and expose no verified row-attribute hook (study §4.1). The `j`/`k` cursor needs `aria-selected` + `tabindex` on the rows. Keyset paging is already Alvo's. `MudSimpleTable` gives Mud's table styling over Alvo's own `<tr>`s.
- **V7. `ChipGroup` stays Alvo's own control.** Study §4.2 maps it to `MudChipSet`, which is not done here.
  - `ChipGroup` is already a WAI-ARIA radio group (roving tabindex, arrows) for one-of, and a toggle-button group (`aria-pressed`) for several-of.
  - `MudChipSet` carries the open WCAG nested-interactive issue #12613.
  - `MudRadioGroup` would drop the "raised on every press, the chosen chip's too" contract that screens rely on (`ChipGroup.SelectedChanged` remarks).
  - Only its look moves: Task 5 restyles `.a-chip` from the theme's `--mud-palette-*` variables.
- **V8. The nav entries stay `SectionLink`** inside `MudNavMenu` (Task 2). See that task.

---
### Task 1: Foundation — package, layered CSS, theme, providers, wrappers (invisible to the operator)

Spec: D1, D4, D5, D8, D9 and §3 (the wrappers carry rules 1–4). Study §1.3, §3, §4.1, §6.2, §8.1 spikes 1–5.

The operator sees no change from this task. Every screen renders as before: the providers are mounted and the wrappers exist, but nothing uses them yet.

**Files:**
- Modify: `Directory.Packages.props`: add the `MudBlazor` `PackageVersion` at the end of the first `<ItemGroup>`.
- Modify: `src/MMLib.Alvo.Admin/MMLib.Alvo.Admin.csproj`: add the `PackageReference`.
- Modify: `src/MMLib.Alvo.Admin/_Imports.razor`: add `@using MudBlazor` plus two aliases.
- Modify: `src/MMLib.Alvo.Admin/AlvoAdminServiceCollectionExtensions.cs`: call `AddLibrary(services)` from `AddAlvoAdmin`.
- Modify: `src/MMLib.Alvo.Admin/AlvoAdminAssets.cs`: correct the `StyleSheet` remark that says "no component library beneath it".
- Modify: `src/MMLib.Alvo.Admin/Components/AdminApp.razor`: the library link and script.
- Modify: `src/MMLib.Alvo.Admin/Components/Shell/AdminLayout.razor`: providers at the top, and the shell-ready mark.
- Modify: `src/MMLib.Alvo.Admin/Components/Shell/SignInLayout.razor`: `<AlvoTheme />`.
- Modify: `src/MMLib.Alvo.Admin/wwwroot/alvo.js`: always a resolved `data-theme`, and follow the system when nothing is stored.
- Modify: `src/MMLib.Alvo.Admin/wwwroot/admin.js`: `markShellReady`.
- Modify: `src/MMLib.Alvo.Admin/Internal/AdminInterop.cs`: `MarkShellReadyAsync`.
- Modify: `src/MMLib.Alvo.Admin/wwwroot/alvo.css`: a new "Library seams" section at the end of the last `@layer components { … }` block, before `@layer utilities`.
- Create:
  - `src/MMLib.Alvo.Admin/wwwroot/alvo-mud.css`
  - `src/MMLib.Alvo.Admin/Internal/LibraryAssets.cs`
  - `src/MMLib.Alvo.Admin/Internal/AlvoMudTheme.cs`
  - `src/MMLib.Alvo.Admin/Internal/AdminSnackbar.cs`
  - `src/MMLib.Alvo.Admin/Components/Shell/AlvoTheme.razor`
  - `src/MMLib.Alvo.Admin/Components/DesignSystem/{AlvoButton.razor, ButtonLook.cs, AlvoAlert.razor, AlertLook.cs, SubmitGate.cs, EditorCloseGuard.cs, ConfirmGate.cs, AlvoEditor.razor, AlvoConfirm.razor}`
- Create tests:
  - `test/MMLib.Alvo.Admin.Tests/LibraryBoundaryTests.cs`
  - `test/MMLib.Alvo.Admin.Tests/LibraryLayerTests.cs`
  - `test/MMLib.Alvo.Admin.Tests/Internal/AlvoMudThemeTests.cs`
  - `test/MMLib.Alvo.Admin.Tests/DesignSystem/{SubmitGateTests, EditorCloseGuardTests, ConfirmGateTests, ButtonLookTests, AlertLookTests}.cs`
  - `test/MMLib.Alvo.Admin.Tests.EndToEnd/FoundationScenarios.cs`
- Modify tests:
  - `test/MMLib.Alvo.Admin.Tests/Internal/AdminInteropTests.cs`
  - `test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs`
  - `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt` (it gains the five new components)
- Modify docs: `docs/architecture/admin-dashboard-review.md`: a dated note under "Considered and rejected".

**Interfaces:**
- Produces, all in namespace `MMLib.Alvo.Admin.Components.DesignSystem` unless noted:
  - `AlvoButton` (component). Parameters:
    - `ButtonTone Tone` (Primary | Secondary | Danger | Ghost; default Secondary)
    - `bool Busy`, `bool Disabled`, `bool Submit`, `bool Small`
    - `string? Href`
    - `EventCallback<MouseEventArgs> OnClick`
    - `RenderFragment? ChildContent`
    - captured `IReadOnlyDictionary<string, object>? Attributes`, splatted onto the `MudButton`: `data-testid`, `aria-*`, `id`, `form`, `class`.
  - `AlvoAlert` (component). Parameters:
    - `AlertTone Tone` (Info | Success | Warning | Error; default Info)
    - `string? Title`, `bool TakeFocus`, `string? TestId`, `string? TitleTestId`
    - `RenderFragment? ChildContent`, `RenderFragment? Actions`
    - It renders `role="alert"` for Warning/Error and `role="status"` for Info/Success.
  - `AlvoEditor` (component). Parameters:
    - `string Title` (required), `string? Eyebrow`, `string? Subtitle`
    - `string SubmitText` (required), `string SubmitTestId = "editor-submit"`, `string TestId = "editor"`
    - `bool Dirty`, `bool Busy`, `bool CanSubmit = true`
    - `EventCallback OnSubmit`, `EventCallback OnClose`
    - `RenderFragment? ChildContent`, `RenderFragment? ExtraActions`
    - Fixed test ids inside it: `editor-cancel`, `editor-discard-question`, `editor-discard`, `editor-keep`.
  - `AlvoConfirm` (component). Parameters:
    - `bool Open`, `string Title` (required), `string Consequence` (required), `string Verb` (required)
    - `string? TypeToConfirm`, `bool Allowed = true`, `bool Busy`
    - `EventCallback OnConfirm`, `EventCallback OnCancel`
    - `RenderFragment? ChildContent`
    - `string TestId = "confirm-dialog"`, `string ConfirmTestId = "confirm-run"`, `string CancelTestId = "confirm-cancel"`
    - The typed-name input carries `id="confirm-name"`.
  - `AlvoTheme` (component, `Components.Shell`). No parameters.
  - `internal sealed class SubmitGate { bool Busy; bool TryBegin(); void End(); }`
  - `internal sealed class EditorCloseGuard { bool Asking; bool RequestClose(bool dirty); void KeepEditing(); void Discard(); }`
  - `internal static class ConfirmGate { bool CanConfirm(string? expected, string typed, bool busy, bool allowed); }`
  - `internal readonly record struct ButtonLook(Variant Variant, Color Color) { static ButtonLook Of(AlvoButton.ButtonTone tone); }`
  - `internal static class AlertLook { Severity SeverityOf(AlvoAlert.AlertTone); string RoleOf(AlvoAlert.AlertTone); }`
  - `internal static class AlvoMudTheme` (`MMLib.Alvo.Admin.Internal`):
    - `IReadOnlyList<ThemeColour> Colours`
    - `MudTheme Light`
    - `MudTheme DarkScoped`
    - `const string DarkScope = ":root[data-theme=dark]"`
    - `internal sealed record ThemeColour(string Token, string Light, string Dark)`
  - `internal static class AdminSnackbar { static void Confirm(this ISnackbar snackbar, string message); }` (`MMLib.Alvo.Admin.Internal`)
  - `internal static class LibraryAssets { string StyleSheet; string Script; }` (`MMLib.Alvo.Admin.Internal`)
  - `AdminInterop.MarkShellReadyAsync()`, backed by admin.js `markShellReady()`, which sets `html[data-alvo-shell="ready"]`.
  - E2E `AdminSession`:
    - `ILocator Dialog(string testId)`
    - `Task ChooseAsync(ILocator combobox, string option)`
    - `Task SnackbarAsync(string text)`
    - `Task<string> FocusedAsync()`
    - `Task<bool> FocusIsInsideAsync(string testId)`
    - `SettleAsync()` now also waits for `data-alvo-shell="ready"`.

- [ ] **Step 1: Write the failing unit tests**

`test/MMLib.Alvo.Admin.Tests/LibraryBoundaryTests.cs`:

```csharp
using Microsoft.AspNetCore.Components;
using System.Reflection;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// MudBlazor is how the dashboard is drawn, never part of what it promises (spec D5).
/// </summary>
/// <remarks>
/// Razor compiles every component public, so a Mud enum on a parameter would be a Mud type in
/// <c>PublicApi.MMLib.Alvo.Admin.verified.txt</c> — and every MudBlazor major (roughly yearly, study §1.1)
/// a breaking change of Alvo's. The wrappers take Alvo's own nested enums instead.
/// </remarks>
public sealed class LibraryBoundaryTests
{
    private const string Library = "MudBlazor";

    private static readonly Assembly _admin = typeof(AlvoAdmin).Assembly;

    [Fact]
    public void No_public_member_of_a_component_is_typed_by_the_library()
        => PublicComponents()
            .SelectMany(type => SignatureTypes(type).Select(signature => (type, signature)))
            .Where(pair => Mentions(pair.signature.Type))
            .Select(pair => $"{pair.type.Name}.{pair.signature.Member}")
            .ShouldBeEmpty("a MudBlazor type on a public member makes every MudBlazor major a breaking change of Alvo");

    [Fact]
    public void No_public_component_derives_from_a_library_component()
        => PublicComponents()
            .Where(type => Ancestors(type).Any(IsLibrary))
            .Select(type => type.FullName)
            .ShouldBeEmpty();

    [Fact]
    public void The_approved_public_api_names_no_library_type()
        => File.ReadAllText(Path.Combine(
                RepositoryRoot.Find(), "test", "MMLib.Alvo.Admin.Tests", "PublicApi.MMLib.Alvo.Admin.verified.txt"))
            .ShouldNotContain(Library);

    [Fact]
    public void The_check_sees_a_library_type_inside_a_generic_argument_or_an_array()
    {
        Mentions(typeof(EventCallback<MudBlazor.Color>)).ShouldBeTrue();
        Mentions(typeof(MudBlazor.Severity[])).ShouldBeTrue();
        Mentions(typeof(IReadOnlyList<string>)).ShouldBeFalse();
    }

    private static IEnumerable<Type> PublicComponents()
        => _admin.GetExportedTypes().Where(type => typeof(IComponent).IsAssignableFrom(type));

    private static IEnumerable<(string Member, Type Type)> SignatureTypes(Type type)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var property in type.GetProperties(declared))
        {
            yield return (property.Name, property.PropertyType);
        }

        foreach (var method in type.GetMethods(declared).Where(method => !method.IsSpecialName))
        {
            yield return (method.Name, method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                yield return ($"{method.Name}({parameter.Name})", parameter.ParameterType);
            }
        }
    }

    private static IEnumerable<Type> Ancestors(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool IsLibrary(Type type) => type.Assembly.GetName().Name == Library;

    private static bool Mentions(Type type)
        => IsLibrary(type)
            || (type.HasElementType && Mentions(type.GetElementType()!))
            || type.GetGenericArguments().Any(Mentions);
}
```

`test/MMLib.Alvo.Admin.Tests/LibraryLayerTests.cs`:

```csharp
using MMLib.Alvo.Admin.Tests.Internal;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// MudBlazor's stylesheet sits beneath every Alvo rule (spec D8), and nothing is fetched from another origin.
/// </summary>
/// <remarks>
/// Mud's reset (<c>*{margin:0;padding:0;border-width:0}</c>, <c>button:focus{outline:none}</c>) is unlayered, and an
/// unlayered rule beats every layered one: without the layer, every screen not yet migrated would lose its spacing
/// and its focus ring the moment the package is referenced (study §6.2).
/// </remarks>
public sealed partial class LibraryLayerTests
{
    private static readonly string _wwwroot = Path.GetDirectoryName(Stylesheet.AlvoCssPath)!;

    private static readonly string _layered = File.ReadAllText(Path.Combine(_wwwroot, "alvo-mud.css"));

    private static readonly string _document = File.ReadAllText(
        Path.Combine(Stylesheet.AdminSourcePath, "Components", "AdminApp.razor"));

    [Fact]
    public void The_library_is_imported_into_the_lowest_layer_and_the_sheet_says_nothing_else()
        => Statements(_layered).ShouldBe(
        [
            "@layer mud, tokens, base, layout, components, utilities;",
            "@import url('../MudBlazor/MudBlazor.min.css') layer(mud);",
        ]);

    [Fact]
    public void The_design_systems_own_layer_order_is_the_tail_of_the_librarys()
        => File.ReadAllText(Stylesheet.AlvoCssPath)
            .ShouldContain("@layer tokens, base, layout, components, utilities;");

    [Fact]
    public void The_document_links_the_layered_library_before_the_design_system_and_never_the_library_itself()
    {
        _document.IndexOf("LibraryAssets.StyleSheet", StringComparison.Ordinal)
            .ShouldBeLessThan(_document.IndexOf("AlvoAdminAssets.StyleSheet", StringComparison.Ordinal));
        _document.ShouldNotContain("MudBlazor.min.css");
        _document.ShouldContain("LibraryAssets.Script");
    }

    [Fact]
    public void Nothing_the_document_or_the_stylesheets_load_comes_from_another_origin()
    {
        foreach (var text in new[] { _document, _layered, File.ReadAllText(Stylesheet.AlvoCssPath) })
        {
            text.ShouldNotContain("http://");
            text.ShouldNotContain("https://");
        }
    }

    private static string[] Statements(string css)
        => Comment().Replace(css, string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
```

`test/MMLib.Alvo.Admin.Tests/Internal/AlvoMudThemeTests.cs`:

```csharp
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>The library's palette is Alvo's tokens, value for value, in both themes (spec §6: no second theme).</summary>
public sealed class AlvoMudThemeTests
{
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> _tokens =
        Stylesheet.ReadThemedTokens(File.ReadAllText(Stylesheet.AlvoCssPath));

    [Fact]
    public void Every_colour_the_theme_carries_is_the_stylesheet_token_of_the_same_name()
    {
        AlvoMudTheme.Colours.ShouldNotBeEmpty();
        foreach (var colour in AlvoMudTheme.Colours)
        {
            _tokens.ShouldContainKey(colour.Token);
            colour.Light.ShouldBe(_tokens[colour.Token].Light, $"{colour.Token}, light");
            colour.Dark.ShouldBe(_tokens[colour.Token].Dark, $"{colour.Token}, dark");
        }
    }

    [Theory]
    [InlineData("--accent", "--accentText")]
    [InlineData("--danger-fg", "--panel")]
    public void Text_on_a_filled_button_meets_AA_in_both_themes(string fill, string text)
    {
        Stylesheet.ContrastRatio(Hex(fill).Light, Hex(text).Light).ShouldBeGreaterThanOrEqualTo(4.5);
        Stylesheet.ContrastRatio(Hex(fill).Dark, Hex(text).Dark).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void The_dark_palette_is_scoped_to_the_resolved_theme_attribute()
    {
        AlvoMudTheme.DarkScope.ShouldBe(":root[data-theme=dark]");
        AlvoMudTheme.DarkScoped.PseudoCss.Scope.ShouldBe(AlvoMudTheme.DarkScope);
    }

    [Fact]
    public void The_type_is_Alvos_and_nothing_shouts()
    {
        var type = AlvoMudTheme.Light.Typography;
        type.Default.FontFamily.ShouldNotBeNull().First().ShouldBe("Public Sans");
        type.Default.FontWeight.ShouldBe("500");
        type.Button.TextTransform.ShouldBe("none");
        AlvoMudTheme.Light.LayoutProperties.DefaultBorderRadius.ShouldBe("6px");
    }

    private static (string Light, string Dark) Hex(string token)
    {
        var colour = AlvoMudTheme.Colours.Single(entry => entry.Token == token);
        return (colour.Light, colour.Dark);
    }
}
```

(If `BaseTypography.FontWeight` is not a `string` in 9.10.0, the compiler says so. Match the library's type in both the theme and this assertion. The build is the authority, study §1.2.)

`test/MMLib.Alvo.Admin.Tests/DesignSystem/SubmitGateTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>One submit at a time — the double click that created two people (inventory defect #6).</summary>
public sealed class SubmitGateTests
{
    [Fact]
    public void A_second_submit_is_refused_while_the_first_is_in_flight()
    {
        var gate = new SubmitGate();

        gate.TryBegin().ShouldBeTrue();
        gate.Busy.ShouldBeTrue();
        gate.TryBegin().ShouldBeFalse();
    }

    [Fact]
    public void The_gate_opens_again_when_the_submit_ends()
    {
        var gate = new SubmitGate();
        gate.TryBegin();

        gate.End();

        gate.Busy.ShouldBeFalse();
        gate.TryBegin().ShouldBeTrue();
    }
}
```

`test/MMLib.Alvo.Admin.Tests/DesignSystem/EditorCloseGuardTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>Closing an editor with unsaved changes asks first (spec §3.4; inventory defect #5).</summary>
public sealed class EditorCloseGuardTests
{
    [Fact]
    public void A_clean_editor_closes_at_once()
    {
        var guard = new EditorCloseGuard();

        guard.RequestClose(dirty: false).ShouldBeTrue();
        guard.Asking.ShouldBeFalse();
    }

    [Fact]
    public void A_dirty_editor_asks_instead_of_closing()
    {
        var guard = new EditorCloseGuard();

        guard.RequestClose(dirty: true).ShouldBeFalse();
        guard.Asking.ShouldBeTrue();
    }

    [Fact]
    public void Asking_again_does_not_turn_the_question_into_a_discard()
    {
        var guard = new EditorCloseGuard();
        guard.RequestClose(dirty: true);

        guard.RequestClose(dirty: true).ShouldBeFalse();
        guard.Asking.ShouldBeTrue();
    }

    [Fact]
    public void Keep_editing_and_discard_both_end_the_question()
    {
        var kept = new EditorCloseGuard();
        kept.RequestClose(dirty: true);
        kept.KeepEditing();
        kept.Asking.ShouldBeFalse();

        var discarded = new EditorCloseGuard();
        discarded.RequestClose(dirty: true);
        discarded.Discard();
        discarded.Asking.ShouldBeFalse();
    }
}
```

`test/MMLib.Alvo.Admin.Tests/DesignSystem/ConfirmGateTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>When a destructive confirm may run (spec §3.2).</summary>
public sealed class ConfirmGateTests
{
    [Theory]
    [InlineData(null, "", false, true, true)]
    [InlineData(null, "", true, true, false)]
    [InlineData(null, "", false, false, false)]
    [InlineData("field-service", "", false, true, false)]
    [InlineData("field-service", "Field-Service", false, true, false)]
    [InlineData("field-service", "field-service ", false, true, false)]
    [InlineData("field-service", "field-service", false, true, true)]
    [InlineData("field-service", "field-service", true, true, false)]
    public void It_runs_only_when_allowed_not_busy_and_the_name_is_typed_exactly(
        string? expected, string typed, bool busy, bool allowed, bool runs)
        => ConfirmGate.CanConfirm(expected, typed, busy, allowed).ShouldBe(runs);
}
```

`test/MMLib.Alvo.Admin.Tests/DesignSystem/ButtonLookTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.DesignSystem;
using MudBlazor;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>Each tone is one Mud look, set in one place (MudBlazor 9 has no global defaults; study §1.2).</summary>
public sealed class ButtonLookTests
{
    [Theory]
    [InlineData(AlvoButton.ButtonTone.Primary, Variant.Filled, Color.Primary)]
    [InlineData(AlvoButton.ButtonTone.Danger, Variant.Filled, Color.Error)]
    [InlineData(AlvoButton.ButtonTone.Secondary, Variant.Outlined, Color.Default)]
    [InlineData(AlvoButton.ButtonTone.Ghost, Variant.Text, Color.Default)]
    public void A_tone_is_one_variant_and_one_colour(AlvoButton.ButtonTone tone, Variant variant, Color color)
        => ButtonLook.Of(tone).ShouldBe(new ButtonLook(variant, color));
}
```

`test/MMLib.Alvo.Admin.Tests/DesignSystem/AlertLookTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.DesignSystem;
using MudBlazor;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>MudAlert renders no role at all (study §4.1); the wrapper gives every tone the right one.</summary>
public sealed class AlertLookTests
{
    [Theory]
    [InlineData(AlvoAlert.AlertTone.Error, "alert", Severity.Error)]
    [InlineData(AlvoAlert.AlertTone.Warning, "alert", Severity.Warning)]
    [InlineData(AlvoAlert.AlertTone.Info, "status", Severity.Info)]
    [InlineData(AlvoAlert.AlertTone.Success, "status", Severity.Success)]
    public void A_tone_has_a_role_and_a_severity(AlvoAlert.AlertTone tone, string role, Severity severity)
    {
        AlertLook.RoleOf(tone).ShouldBe(role);
        AlertLook.SeverityOf(tone).ShouldBe(severity);
    }
}
```

Add to `test/MMLib.Alvo.Admin.Tests/Internal/AdminInteropTests.cs`, beside the other one-call facts:

```csharp
    [Fact]
    public async Task The_shell_says_when_it_is_ready()
    {
        var interop = new AdminInterop(_js, _logger);

        await interop.MarkShellReadyAsync();

        await _module.Received(1).InvokeAsync<IJSVoidResult>("markShellReady", Arg.Any<object?[]?>());
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: a build failure. `AlvoMudTheme`, `SubmitGate`, `EditorCloseGuard`, `ConfirmGate`, `ButtonLook`, `AlertLook`, `AlvoButton.ButtonTone`, `AlvoAlert.AlertTone`, `MarkShellReadyAsync` and the `MudBlazor` namespace do not exist.

- [ ] **Step 3: Pin and reference the package, and import it**

In `Directory.Packages.props`, at the end of the first `<ItemGroup>`:

```xml
    <!-- The admin dashboard's component library (docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md,
         D1). MIT, stable on net10.0, plain HTML, so Playwright's role locators work (study §5.1). Built with 0
         warnings under this repository's analyzers in the spike. Pinned EXACTLY: minors land every 2-5 weeks and
         most online answers are v6-v8 syntax (study §1.2), so a bump is a deliberate PR. Referenced by
         MMLib.Alvo.Admin only; an embedded host that never maps the dashboard still acquires it transitively,
         which is the package boundary the admin RCL exists to draw. -->
    <PackageVersion Include="MudBlazor" Version="9.10.0" />
```

In `src/MMLib.Alvo.Admin/MMLib.Alvo.Admin.csproj`, a new `<ItemGroup>` after the framework reference:

```xml
  <ItemGroup>
    <!-- The components the screens are drawn with (spec D1). Its types never reach a public member of this
         package: LibraryBoundaryTests holds that (spec D5). -->
    <PackageReference Include="MudBlazor" />
  </ItemGroup>
```

In `src/MMLib.Alvo.Admin/_Imports.razor`, after `@using MMLib.Alvo.Schema`:

```razor
@using MudBlazor
@*
    Two names both namespaces declare. MudBlazor has an `Icons` (Material paths) and a `FieldType`; Alvo's are
    the stroked 20×20 set and the schema's field type, and every screen means Alvo's. A using alias outranks a
    namespace import, so these keep every existing `Icons.Search` and `FieldType.Enum` meaning what it meant.
*@
@using Icons = MMLib.Alvo.Admin.Components.DesignSystem.Icons
@using FieldType = MMLib.Alvo.Schema.FieldType
```

- [ ] **Step 4: Layer the library's stylesheet and name the assets**

Create `src/MMLib.Alvo.Admin/wwwroot/alvo-mud.css`. No BOM, LF:

```css
/* ===========================================================================
   MudBlazor, beneath every rule of the design system.

   Mud's stylesheet is unlayered and opens with a global reset, and an unlayered
   rule beats every layered one: linked as it ships, it would strip the spacing
   and the focus ring from every screen alvo.css draws. Imported into the first
   layer of the order below instead, it loses to all five of Alvo's.

   The order restates alvo.css's own statement with `mud` in front, so whichever
   file the browser reads first, the order is the same. This file is linked only
   by the dashboard's own document, never by alvo.css: the design prototype links
   alvo.css from the repository, where no MudBlazor file exists to import.
   =========================================================================== */
@layer mud, tokens, base, layout, components, utilities;
@import url('../MudBlazor/MudBlazor.min.css') layer(mud);
```

Create `src/MMLib.Alvo.Admin/Internal/LibraryAssets.cs`. BOM, CRLF:

```csharp
namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The component library's two browser assets, by the path the dashboard's document references them at.
/// </summary>
/// <remarks>
/// Internal, unlike <see cref="AlvoAdminAssets"/>: <c>AdminApp.razor</c> is this package's own document, so no host
/// writes these tags, and a public property would promise a file whose name follows MudBlazor's versioning.
/// </remarks>
internal static class LibraryAssets
{
    /// <summary>The layered import of MudBlazor's stylesheet (<c>wwwroot/alvo-mud.css</c>), linked before alvo.css.</summary>
    public static string StyleSheet { get; } = "_content/MMLib.Alvo.Admin/alvo-mud.css";

    /// <summary>
    /// MudBlazor's script. It ships no JS initializer, so the tag is what loads it; without it the theme provider
    /// logs a missing-script error on every page (study §1.3).
    /// </summary>
    public static string Script { get; } = "_content/MudBlazor/MudBlazor.min.js";
}
```

In `src/MMLib.Alvo.Admin/Components/AdminApp.razor`:
- Head: before `<link rel="stylesheet" href="@AlvoAdminAssets.StyleSheet" />`, insert `<link rel="stylesheet" href="@LibraryAssets.StyleSheet" />`.
- Body: after `<script src="_framework/blazor.web.js"></script>`, insert `<script src="@LibraryAssets.Script"></script>`.
- Header comment: add a fourth bullet: "The library's stylesheet is linked FIRST and reaches the page through `alvo-mud.css`, which imports it into the lowest cascade layer. See that file. Linking `MudBlazor.min.css` directly would let its reset beat every Alvo rule."

In `src/MMLib.Alvo.Admin/AlvoAdminAssets.cs`, on `StyleSheet`, replace the `<remarks>` with:

```csharp
    /// <remarks>
    /// The one stylesheet a host that styles its own page with Alvo's look links. The dashboard's own document also
    /// links its component library through an internal, layered sheet beneath this one; that is the document's
    /// business, not a host's, so it is not listed here.
    /// </remarks>
```

- [ ] **Step 5: Register the library inside `AddAlvoAdmin`**

In `AlvoAdminServiceCollectionExtensions.cs`, add `using MudBlazor;` and `using MudBlazor.Services;`. Call `AddLibrary(services);` right after `services.AddAuthorizationCore();`. Then add:

```csharp
    /// <summary>
    /// The component library's services, configured for the dashboard's feedback rules.
    /// </summary>
    /// <remarks>
    /// Registered here so an embedded host never learns the library exists (study §1.3). The snackbar settings
    /// are spec §3.3: bottom-right, a few seconds, at most two, and never a duplicate. The breakpoint is Alvo's
    /// one phone width (720 px, pinned by StylesheetHygieneTests), so the responsive drawer turns temporary where
    /// the bottom bar appears rather than at Mud's own 600.
    /// </remarks>
    private static void AddLibrary(IServiceCollection services)
        => services.AddMudServices(library =>
        {
            library.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            library.SnackbarConfiguration.VisibleStateDuration = 5000;
            library.SnackbarConfiguration.MaxDisplayedSnackbars = 2;
            library.SnackbarConfiguration.PreventDuplicates = true;
            library.SnackbarConfiguration.ShowCloseIcon = true;
            library.ResizeOptions.BreakpointDefinitions = new Dictionary<Breakpoint, int>
            {
                [Breakpoint.Xs] = 0,
                [Breakpoint.Sm] = 720,
                [Breakpoint.Md] = 960,
                [Breakpoint.Lg] = 1280,
                [Breakpoint.Xl] = 1920,
                [Breakpoint.Xxl] = 2560,
            };
        });
```

Also extend the class remarks' list of registered services with this sentence: "and MudBlazor's own services, through `AddLibrary`". If `ResizeOptions.BreakpointDefinitions` does not compile in 9.10.0, delete those lines and record it in the commit body; Task 2's shell scenarios then measure the drawer at 390 and 1440, where 600 and 720 agree. The other settings are study §4.1 [V].

Create `src/MMLib.Alvo.Admin/Internal/AdminSnackbar.cs`:

```csharp
using MudBlazor;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>The dashboard's one way to say an action worked (spec §3.3).</summary>
/// <remarks>
/// There is deliberately no <c>Refuse</c> beside it: an error is an <c>AlvoAlert</c> in place, never a snackbar
/// that disappears with the fix it carried (design §5.5).
/// </remarks>
internal static class AdminSnackbar
{
    /// <summary>Shows a short confirmation of what just happened, such as "Saved to the working copy".</summary>
    public static void Confirm(this ISnackbar snackbar, string message)
    {
        ArgumentNullException.ThrowIfNull(snackbar);
        snackbar.Add(message, Severity.Success);
    }
}
```

- [ ] **Step 6: The theme, built from the tokens, and the pair of providers**

Create `src/MMLib.Alvo.Admin/Internal/AlvoMudTheme.cs`:

```csharp
using MudBlazor;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Alvo's identity as a <see cref="MudTheme"/>: the same colours as alvo.css, the same type, no Material shadows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two themes, one palette each.</b> The theme provider writes one palette per page, server-side, so a dark
/// operator would get light until the circuit connected. <see cref="Light"/> is written on <c>:root</c>, and
/// <see cref="DarkScoped"/> is written under <see cref="DarkScope"/>. alvo.js sets that attribute before the first
/// paint, so the right palette wins with no round trip (study §3.3, verified in the spike).
/// </para>
/// <para>
/// <b>The hex values are alvo.css's, duplicated deliberately</b> (plan deviation V3), and
/// <c>AlvoMudThemeTests</c> pins them equal to the tokens of the same name.
/// </para>
/// </remarks>
internal static class AlvoMudTheme
{
    /// <summary>The attribute selector the dark palette is scoped under; alvo.js always writes it.</summary>
    public const string DarkScope = ":root[data-theme=dark]";

    /// <summary>Every colour the theme carries, by the token it copies.</summary>
    public static IReadOnlyList<ThemeColour> Colours { get; } =
    [
        new("--accent", "#0f7a48", "#39e991"),
        new("--accentText", "#ffffff", "#12241a"),
        new("--bg", "#f7f8fa", "#1e2029"),
        new("--panel", "#ffffff", "#262833"),
        new("--panel2", "#f2f4f7", "#2e3040"),
        new("--border", "#e8eaef", "#34364a"),
        new("--border2", "#d3d7e0", "#3b3e52"),
        new("--text", "#1c1e26", "#e8eaf0"),
        new("--dim", "#5f6577", "#9aa0b8"),
        new("--faint", "#6b7180", "#8b92ab"),
        new("--ok-fg", "#0f7a48", "#39e991"),
        new("--warn-fg", "#8a5a00", "#f5c451"),
        new("--danger-fg", "#b3261e", "#ff8f8f"),
        new("--neutral-fg", "#5f6577", "#9aa0b8"),
    ];

    /// <summary>The theme written on <c>:root</c>; its light palette is the one in force by default.</summary>
    public static MudTheme Light { get; } = Build(scope: null);

    /// <summary>The same theme, written under <see cref="DarkScope"/> by a provider in dark mode.</summary>
    public static MudTheme DarkScoped { get; } = Build(DarkScope);

    private static MudTheme Build(string? scope)
    {
        var theme = new MudTheme
        {
            PaletteLight = Paint(new PaletteLight(), colour => colour.Light),
            PaletteDark = Paint(new PaletteDark(), colour => colour.Dark),
            Typography = Type(),
            LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" },
            Shadows = new Shadow { Elevation = [.. Enumerable.Repeat("none", 26)] },
        };

        if (scope is not null)
        {
            theme.PseudoCss = new PseudoCss { Scope = scope };
        }

        return theme;
    }

    private static T Paint<T>(T palette, Func<ThemeColour, string> side) where T : Palette
    {
        string Of(string token) => side(Colours.Single(colour => colour.Token == token));

        palette.Primary = Of("--accent");
        palette.PrimaryContrastText = Of("--accentText");
        palette.Background = Of("--bg");
        palette.BackgroundGray = Of("--panel2");
        palette.Surface = Of("--panel");
        palette.AppbarBackground = Of("--panel");
        palette.AppbarText = Of("--text");
        palette.DrawerBackground = Of("--panel");
        palette.DrawerText = Of("--text");
        palette.TextPrimary = Of("--text");
        palette.TextSecondary = Of("--dim");
        palette.TextDisabled = Of("--faint");
        palette.LinesDefault = Of("--border");
        palette.LinesInputs = Of("--border2");
        palette.Divider = Of("--border");
        palette.TableLines = Of("--border");
        palette.Success = Of("--ok-fg");
        palette.Warning = Of("--warn-fg");
        palette.Error = Of("--danger-fg");
        palette.ErrorContrastText = Of("--panel");
        palette.Info = Of("--neutral-fg");
        return palette;
    }

    /// <summary>Public Sans at 500 (the fonts folder ships no 400, study §3.2), and no uppercase buttons.</summary>
    private static Typography Type()
    {
        string[] family = ["Public Sans", "system-ui", "-apple-system", "Segoe UI", "sans-serif"];
        return new Typography
        {
            Default = new DefaultTypography { FontFamily = family, FontWeight = "500" },
            Body1 = new Body1Typography { FontFamily = family, FontWeight = "500" },
            Body2 = new Body2Typography { FontFamily = family, FontWeight = "500" },
            Button = new ButtonTypography { FontFamily = family, FontWeight = "600", TextTransform = "none" },
        };
    }
}

/// <summary>One colour the theme carries, and the token it copies.</summary>
/// <param name="Token">The custom property's name in alvo.css, such as <c>--accent</c>.</param>
/// <param name="Light">Its light value, exactly as alvo.css writes it.</param>
/// <param name="Dark">Its dark value, exactly as alvo.css writes it.</param>
internal sealed record ThemeColour(string Token, string Light, string Dark);
```

(The palette members take a `MudColor`, which converts implicitly from a hex string. `PseudoCss.Scope` is normalised by its setter to what the test expects, per study §3.3 [V].)

Create `src/MMLib.Alvo.Admin/Components/Shell/AlvoTheme.razor` (BOM, LF):

```razor
@*
    The library's palette, both themes, from Alvo's tokens.

    Two providers, deliberately. The first writes the light palette on :root; the second writes the dark one under
    :root[data-theme=dark], which alvo.js sets before the first paint, so a dark operator never sees light, not even
    during prerender, and flipping the theme is an attribute change with no server round trip (study §3.3).
    ObserveSystemDarkModeChange is off: alvo.js is the one authority on the theme, and a second observer would fight
    the toggle. Static rendering is fine here: the providers only write <style>, which is why the sign-in layout
    can use this too.
*@
<MudThemeProvider Theme="AlvoMudTheme.Light" IsDarkMode="false" ObserveSystemDarkModeChange="false" DefaultScrollbar="true" />
<MudThemeProvider Theme="AlvoMudTheme.DarkScoped" IsDarkMode="true" ObserveSystemDarkModeChange="false" DefaultScrollbar="true" />
```

In `src/MMLib.Alvo.Admin/Components/Shell/SignInLayout.razor`, put `<AlvoTheme />` as the first line after `@inherits LayoutComponentBase`. Add a comment line: the sign-in page is static, so only the theme provider is mounted here, never the popover, dialog or snackbar providers, which are interactive-only (study §2.3).

In `AdminLayout.razor`, put these at the very top of the markup, before `<div class="a-shell">`:

```razor
@*
    The library's four providers, in the interactive layout because the library does not render them statically
    (study §1.3). Exactly one popover provider: a second throws. Dialog defaults are the pattern language's:
    focus lands on the first field, and a click on the scrim never dismisses (spec §3.1-3.2). Each wrapper still
    states its own options.
*@
<AlvoTheme />
<MudPopoverProvider />
<MudDialogProvider DefaultFocus="DefaultFocus.FirstChild" BackdropClick="false" CloseButton="false" />
<MudSnackbarProvider />
```

Also add a first-render mark, with `@inject AdminInterop Interop` (the layout already `@implements IDisposable`):

```csharp
    /// <summary>
    /// Says the shell has rendered interactively, providers and all. It is the readiness signal a scenario waits
    /// on before it opens a popover or a dialog (study §5.1 gotcha 2): a click before the circuit is live is lost.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Interop.MarkShellReadyAsync();
        }
    }
```

- [ ] **Step 7: `alvo.js` always writes a resolved theme; `admin.js` marks the shell ready**

In `src/MMLib.Alvo.Admin/wwwroot/alvo.js`, replace the header comment of the Theme section and `applyStored` with:

```js
  /* --- Theme -------------------------------------------------------------
     Applied synchronously, before paint, and ALWAYS resolved to light or dark.
     The component library's dark palette is scoped to [data-theme=dark] (see
     AlvoTheme.razor), and a scoped variable cannot follow prefers-color-scheme on
     its own, so a viewer who never chose gets the system's answer written down.
     ---------------------------------------------------------------------- */

  const DARK = '(prefers-color-scheme: dark)';

  const systemTheme = () => (window.matchMedia(DARK).matches ? 'dark' : 'light');

  const storedTheme = () => {
    const theme = readStored(THEME_KEY);
    return theme === 'light' || theme === 'dark' ? theme : null;
  };

  const applyStored = () => {
    document.documentElement.dataset.theme = storedTheme() ?? systemTheme();

    const density = readStored(DENSITY_KEY);
    if (density === 'comfortable' || density === 'compact') {
      document.documentElement.dataset.density = density;
    }
  };

  /* Nothing stored means "follow the system", and the system can change under an open page. */
  const followSystem = () =>
    window.matchMedia(DARK).addEventListener('change', () => {
      if (storedTheme() === null) {
        document.documentElement.dataset.theme = systemTheme();
        emit('theme', { value: document.documentElement.dataset.theme });
      }
    });
```

Change `resolvedTheme` to `const resolvedTheme = () => document.documentElement.dataset.theme ?? systemTheme();`. Add `followSystem();` right after `applyStored();` at the bottom of the file.

In `src/MMLib.Alvo.Admin/wwwroot/admin.js`, after `markKeyboardReady`:

```js
/**
 * Marks the shell as rendered over the circuit, with the component library's providers mounted.
 *
 * The keyboard mark says the palette is listening; this one says a popover or a dialog can open. They are two
 * marks because they are two components' first renders, and a scenario that waited on the first and then opened a
 * select raced the second.
 */
export function markShellReady() {
  document.documentElement.dataset.alvoShell = 'ready';
}
```

In `Internal/AdminInterop.cs`, beside `MarkKeyboardReadyAsync`:

```csharp
    /// <summary>Says the shell rendered interactively, providers included; see <c>markShellReady</c> in admin.js.</summary>
    public Task MarkShellReadyAsync() => QuietlyAsync(module => module.InvokeVoidAsync("markShellReady"));
```

- [ ] **Step 8: The wrappers**

`Components/DesignSystem/ButtonLook.cs`:

```csharp
using MudBlazor;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>What each <see cref="AlvoButton.ButtonTone"/> looks like in the library, decided once.</summary>
/// <param name="Variant">The library's fill style.</param>
/// <param name="Color">The library's colour role.</param>
internal readonly record struct ButtonLook(Variant Variant, Color Color)
{
    /// <summary>The look of <paramref name="tone"/>.</summary>
    public static ButtonLook Of(AlvoButton.ButtonTone tone) => tone switch
    {
        AlvoButton.ButtonTone.Primary => new(Variant.Filled, Color.Primary),
        AlvoButton.ButtonTone.Danger => new(Variant.Filled, Color.Error),
        AlvoButton.ButtonTone.Ghost => new(Variant.Text, Color.Default),
        _ => new(Variant.Outlined, Color.Default),
    };
}
```

`Components/DesignSystem/AlvoButton.razor`:

```razor
@*
    A button with Alvo's defaults set once: no ripple, no shadow, no uppercase (the theme), and a busy state that
    disables the button and shows progress in it (spec §3.4, so there is no double submit anywhere). MudBlazor 9
    removed its global defaults and advises exactly this wrapper (study §1.2). The tone is Alvo's enum, never Mud's
    (spec D5). Anything else, such as data-testid, aria-label, id or form, passes through to the button.
*@
<MudButton Variant="_look.Variant" Color="_look.Color" Size="@(Small ? Size.Small : Size.Medium)"
           Ripple="false" DropShadow="false" Disabled="@(Disabled || Busy)"
           ButtonType="@(Submit ? ButtonType.Submit : ButtonType.Button)" Href="@Href" OnClick="OnClick"
           aria-busy="@(Busy ? "true" : null)" @attributes="Attributes">
    @if (Busy)
    {
        <MudProgressCircular Size="Size.Small" Indeterminate="true" Class="a-busy" aria-hidden="true" />
    }
    @ChildContent
</MudButton>

@code {
    private ButtonLook _look = ButtonLook.Of(ButtonTone.Secondary);

    /// <summary>What the button is for, in the order a screen reaches for them.</summary>
    public enum ButtonTone
    {
        /// <summary>The one action a surface exists for: Save, Create, Apply.</summary>
        Primary,

        /// <summary>Any other action on the surface.</summary>
        Secondary,

        /// <summary>An action that destroys or revokes: Delete record, Disable person.</summary>
        Danger,

        /// <summary>A quiet action beside a primary one: Cancel, Close, an icon in the app bar.</summary>
        Ghost,
    }

    /// <summary>What the button is for.</summary>
    [Parameter]
    public ButtonTone Tone { get; set; } = ButtonTone.Secondary;

    /// <summary>Whether the action it started is still running: disabled, with progress drawn in it.</summary>
    [Parameter]
    public bool Busy { get; set; }

    /// <summary>Whether the action is unavailable for a reason the surface states.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Whether it submits its form, which makes Enter in a single-line field press it.</summary>
    [Parameter]
    public bool Submit { get; set; }

    /// <summary>The dense size a row or an app bar uses.</summary>
    [Parameter]
    public bool Small { get; set; }

    /// <summary>A link target, which makes it a link drawn as a button.</summary>
    [Parameter]
    public string? Href { get; set; }

    /// <summary>Raised on a press.</summary>
    [Parameter]
    public EventCallback<MouseEventArgs> OnClick { get; set; }

    /// <summary>The label, and an icon beside it when there is one.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Every other attribute, passed to the button.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? Attributes { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet() => _look = ButtonLook.Of(Tone);
}
```

`Components/DesignSystem/AlertLook.cs`:

```csharp
using MudBlazor;

namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>The role and the library severity of each <see cref="AlvoAlert.AlertTone"/>.</summary>
internal static class AlertLook
{
    /// <summary><c>alert</c> for what went or may go wrong, <c>status</c> for what is merely so.</summary>
    public static string RoleOf(AlvoAlert.AlertTone tone)
        => tone is AlvoAlert.AlertTone.Error or AlvoAlert.AlertTone.Warning ? "alert" : "status";

    /// <summary>The library's severity for <paramref name="tone"/>.</summary>
    public static Severity SeverityOf(AlvoAlert.AlertTone tone) => tone switch
    {
        AlvoAlert.AlertTone.Error => Severity.Error,
        AlvoAlert.AlertTone.Warning => Severity.Warning,
        AlvoAlert.AlertTone.Success => Severity.Success,
        _ => Severity.Info,
    };
}
```

`Components/DesignSystem/AlvoAlert.razor`:

```razor
@*
    A persistent message in place: state, or an error with the fix as its action (spec §3.3).

    MudAlert renders no role (study §4.1), so the role is on this frame, which is also what takes focus after a
    failed submit. Focus is sent to the frame rather than into the alert, so a screen reader reads the whole message
    from its first word.
*@
<div class="a-alert" role="@AlertLook.RoleOf(Tone)" tabindex="-1" data-testid="@TestId" @ref="_frame">
    <MudAlert Severity="@AlertLook.SeverityOf(Tone)" Variant="Variant.Outlined" Dense="true" Elevation="0" Class="a-alert__body">
        @if (Title is { Length: > 0 })
        {
            <div class="a-alert__title" data-testid="@TitleTestId">@Title</div>
        }
        @ChildContent
        @if (Actions is not null)
        {
            <div class="a-row a-row--gap-2 a-alert__actions">@Actions</div>
        }
    </MudAlert>
</div>

@code {
    private ElementReference _frame;

    /// <summary>What kind of message it is.</summary>
    public enum AlertTone
    {
        /// <summary>A fact about the state of things.</summary>
        Info,

        /// <summary>Something that worked and stays worth reading, such as "Applied as revision 12".</summary>
        Success,

        /// <summary>Something that will go wrong if nothing changes.</summary>
        Warning,

        /// <summary>A refusal or a failure.</summary>
        Error,
    }

    /// <summary>What kind of message it is.</summary>
    [Parameter]
    public AlertTone Tone { get; set; } = AlertTone.Info;

    /// <summary>The headline.</summary>
    [Parameter]
    public string? Title { get; set; }

    /// <summary>Whether the message takes focus when it appears, which a failed submit asks for (spec §3.3).</summary>
    [Parameter]
    public bool TakeFocus { get; set; }

    /// <summary>The frame's test id.</summary>
    [Parameter]
    public string? TestId { get; set; }

    /// <summary>The headline's test id.</summary>
    [Parameter]
    public string? TitleTestId { get; set; }

    /// <summary>The message.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>What the operator can do about it: the structured fix, a retry, a way onward.</summary>
    [Parameter]
    public RenderFragment? Actions { get; set; }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && TakeFocus)
        {
            await _frame.FocusAsync();
        }
    }
}
```

`Components/DesignSystem/SubmitGate.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>One submit at a time: a second press while the first is in flight does nothing.</summary>
/// <remarks>
/// The disabled attribute alone is not enough: it arrives one render after the first click, and a fast double click
/// lands both clicks before that render (inventory defect #6).
/// </remarks>
internal sealed class SubmitGate
{
    /// <summary>Whether a submit is in flight.</summary>
    public bool Busy { get; private set; }

    /// <summary>Starts a submit; answers <see langword="false"/> when one is already running.</summary>
    public bool TryBegin()
    {
        if (Busy)
        {
            return false;
        }

        Busy = true;
        return true;
    }

    /// <summary>Ends the submit in flight, whatever its outcome.</summary>
    public void End() => Busy = false;
}
```

`Components/DesignSystem/EditorCloseGuard.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>Whether an editor may close now, or must first ask "Discard your changes?" (spec §3.4).</summary>
internal sealed class EditorCloseGuard
{
    /// <summary>Whether the question is on screen.</summary>
    public bool Asking { get; private set; }

    /// <summary>
    /// Answers whether the editor may close. For a dirty editor it answers <see langword="false"/> and starts
    /// asking. A second request while asking stays a question: pressing Escape twice must not discard.
    /// </summary>
    public bool RequestClose(bool dirty)
    {
        if (!dirty)
        {
            Asking = false;
            return true;
        }

        Asking = true;
        return false;
    }

    /// <summary>The operator chose to go on editing.</summary>
    public void KeepEditing() => Asking = false;

    /// <summary>The operator chose to lose the changes; the editor closes next.</summary>
    public void Discard() => Asking = false;
}
```

`Components/DesignSystem/ConfirmGate.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.DesignSystem;

/// <summary>When a destructive confirm's button may run (spec §3.2).</summary>
internal static class ConfirmGate
{
    /// <summary>
    /// Allowed, not busy, and, when a name must be typed, typed exactly. An ordinal comparison with no trimming:
    /// "almost the name" is exactly the slip the step exists to catch.
    /// </summary>
    public static bool CanConfirm(string? expected, string typed, bool busy, bool allowed)
        => allowed && !busy && (expected is null || string.Equals(typed, expected, StringComparison.Ordinal));
}
```

`Components/DesignSystem/AlvoEditor.razor`:

```razor
@*
    Creating or editing one item with more than one value (spec §3.1): a right-hand modal side sheet.

    A right-positioned MudDialog rather than a MudDrawer, because the dialog brings role=dialog, aria-modal, a focus
    trap, Escape and focus return, and the drawer brings none of them (study §4.1, verified in the spike). Full height
    and 560 px wide, so on a phone it is the whole screen (plan V4).

    The policy this adds on top of the dialog:
    * Escape and Cancel ask "Discard your changes?" when Dirty (EditorCloseGuard). The question replaces the form in
      place instead of opening a second dialog (spec §3.1). The scrim never closes the editor.
    * Enter in a single-line field submits (a real form with a submit button), Ctrl/Cmd+Enter submits from anywhere,
      and the primary button is busy and disabled while OnSubmit runs (SubmitGate). Cancel stays enabled.

    Closed means absent: the owner removes this component, and disposing it closes the dialog, so focus goes back to
    the trigger.

    The content renders inside MudDialogProvider, not under the page. A cascade from the page does not reach it, so
    a CascadingValue the content needs (an AdminProblem for ErrorPanel) goes inside ChildContent.
*@
@implements IAsyncDisposable
@inject AdminInterop Interop

<MudDialog @ref="_dialog" Visible="true" Options="_options" Class="a-editor" ContentClass="a-editor__body"
           ActionsClass="a-editor__actions" OnKeyDown="OnKeyDownAsync" data-testid="@TestId">
    <TitleContent>
        <div class="a-editor__head">
            @if (Eyebrow is { Length: > 0 })
            {
                <span class="a-pagehead__eyebrow">@Eyebrow</span>
            }
            <span class="a-editor__title">@Title</span>
            @if (Subtitle is { Length: > 0 })
            {
                <span class="a-section__sub">@Subtitle</span>
            }
        </div>
    </TitleContent>
    <DialogContent>
        @if (_guard.Asking)
        {
            <AlvoAlert Tone="AlvoAlert.AlertTone.Warning" Title="Discard your changes?" TakeFocus="true"
                       TestId="editor-discard-question">
                <ChildContent>What you changed here has not been saved, and closing loses it.</ChildContent>
                <Actions>
                    <AlvoButton Tone="AlvoButton.ButtonTone.Danger" Small="true" data-testid="editor-discard"
                                OnClick="DiscardAsync">Discard changes</AlvoButton>
                    <AlvoButton Small="true" data-testid="editor-keep" OnClick="KeepEditing">Keep editing</AlvoButton>
                </Actions>
            </AlvoAlert>
        }
        <form id="@_formId" class="a-stack" hidden="@_guard.Asking" @onsubmit="SubmitAsync">
            @ChildContent
        </form>
    </DialogContent>
    <DialogActions>
        @if (!_guard.Asking)
        {
            @ExtraActions
            <span class="a-spacer"></span>
            <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" data-testid="editor-cancel" OnClick="RequestCloseAsync">Cancel</AlvoButton>
            <AlvoButton Tone="AlvoButton.ButtonTone.Primary" Submit="true" form="@_formId" Busy="@(Busy || _gate.Busy)"
                        Disabled="@(!CanSubmit)" data-testid="@SubmitTestId">@SubmitText</AlvoButton>
        }
    </DialogActions>
</MudDialog>

@code {
    private static int _editors;
    private readonly string _formId = $"alvo-editor-{Interlocked.Increment(ref _editors)}";
    private readonly EditorCloseGuard _guard = new();
    private readonly SubmitGate _gate = new();
    private readonly DialogOptions _options = new()
    {
        Position = DialogPosition.CenterRight,
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
        CloseOnEscapeKey = false,
        BackdropClick = false,
        CloseButton = false,
        DefaultFocus = DefaultFocus.FirstChild,
    };

    private MudDialog? _dialog;
    private ScrollLock? _scrollLock;

    /// <summary>"New …" or "Edit …" (spec §3.1).</summary>
    [Parameter, EditorRequired]
    public string Title { get; set; } = string.Empty;

    /// <summary>What the subject belongs to, set above the title the way a page header sets its section.</summary>
    [Parameter]
    public string? Eyebrow { get; set; }

    /// <summary>One line under the title, or nothing.</summary>
    [Parameter]
    public string? Subtitle { get; set; }

    /// <summary>The primary action's label: "Save to the working copy", "Save record", "Create person".</summary>
    [Parameter, EditorRequired]
    public string SubmitText { get; set; } = string.Empty;

    /// <summary>The primary action's test id.</summary>
    [Parameter]
    public string SubmitTestId { get; set; } = "editor-submit";

    /// <summary>The dialog's test id.</summary>
    [Parameter]
    public string TestId { get; set; } = "editor";

    /// <summary>Whether the form holds something closing would lose.</summary>
    [Parameter]
    public bool Dirty { get; set; }

    /// <summary>Whether the owner is still busy with something the form started, beyond the submit itself.</summary>
    [Parameter]
    public bool Busy { get; set; }

    /// <summary>Whether the primary action is available.</summary>
    [Parameter]
    public bool CanSubmit { get; set; } = true;

    /// <summary>Raised on the primary action, on Enter in a single-line field, and on Ctrl/Cmd+Enter.</summary>
    [Parameter]
    public EventCallback OnSubmit { get; set; }

    /// <summary>Raised when the operator closes it, once any discard question has been answered.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>The fields.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Actions left of Cancel, such as "Save and add another" or "Delete record".</summary>
    [Parameter]
    public RenderFragment? ExtraActions { get; set; }

    private ScrollLock Lock => _scrollLock ??= new ScrollLock(Interop);

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Lock.HoldAsync();
        }
    }

    private async Task SubmitAsync()
    {
        if (!CanSubmit || Busy || !_gate.TryBegin())
        {
            return;
        }

        try
        {
            await OnSubmit.InvokeAsync();
        }
        finally
        {
            _gate.End();
        }
    }

    private async Task RequestCloseAsync()
    {
        if (_guard.RequestClose(Dirty))
        {
            await OnClose.InvokeAsync();
        }
    }

    private Task DiscardAsync()
    {
        _guard.Discard();
        return OnClose.InvokeAsync();
    }

    private void KeepEditing() => _guard.KeepEditing();

    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            await EscapeAsync();
        }
        else if (args.Key == "Enter" && (args.CtrlKey || args.MetaKey))
        {
            await SubmitAsync();
        }
    }

    /// <summary>Escape answers the topmost thing: the question if one is asked, the editor otherwise.</summary>
    private Task EscapeAsync()
    {
        if (!_guard.Asking)
        {
            return RequestCloseAsync();
        }

        KeepEditing();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_dialog is not null)
        {
            await _dialog.CloseAsync();
        }

        if (_scrollLock is not null)
        {
            await _scrollLock.ReleaseAsync();
        }
    }
}
```

Three facts are unverified for an *inline* `MudDialog` in 9.10.0. Each one is settled by the build or by Task 3's scenarios, and each has its fallback written here:

1. **`MudDialog.OnKeyDown`** (study §4.1 [V] documents it). If it does not compile, remove the attribute and wrap `@ChildContent` in `<div @onkeydown="OnKeyDownAsync">` inside the form. Keys bubble from the fields.
2. **`MudDialog.CloseAsync()`**. If it does not compile, give the component a `bool _visible = true`, bind `Visible="_visible"`, and close by setting `_visible = false` + `StateHasChanged()`. That runs in the owner's `OnClose` handler, before the owner removes the editor, so dispose has nothing left to close.
3. **Where `data-testid` lands.** If `data-testid` does not land on the `role=dialog` element, move it to the `<div class="a-editor__head">` wrapper's parent by wrapping `TitleContent` + `DialogContent` in `<div data-testid="@TestId">`. Task 3's scenarios find the editor by test id and then assert `role=dialog` on its closest ancestor.

`Components/DesignSystem/AlvoConfirm.razor`:

```razor
@*
    Confirming a destructive action (spec §3.2): a centred dialog, no light dismiss, Escape = Cancel.

    Title = the action; the first sentence is the consequence; the button names the verb ("Delete record"). An
    irreversible, wide-blast-radius action (a destructive apply, a rollback) sets TypeToConfirm: the button stays
    disabled until the name is typed exactly. A checkbox is one click from the button beside it, and the click that
    matters looks like the one that does not (design §5.5, kept from ConfirmByName).

    Opened after any editor closed: never over a dialog (spec §3.1). The first focusable element takes focus, which
    is the typed-name field when there is one and Cancel when there is not, the safe choice either way.
*@
<MudDialog Visible="Open" VisibleChanged="OnVisibleChangedAsync" Options="_options" Class="a-confirm-dialog"
           data-testid="@TestId">
    <TitleContent><span class="a-editor__title">@Title</span></TitleContent>
    <DialogContent>
        <div class="a-stack">
            <p class="a-confirm-dialog__consequence">@Consequence</p>
            @ChildContent
            @if (TypeToConfirm is { } expected)
            {
                <MudTextField T="string" Value="_typed" ValueChanged="Typed" Immediate="true" Variant="Variant.Outlined"
                              Label="@($"Type {expected} to allow it")" id="confirm-name" autocomplete="off" spellcheck="false" />
            }
        </div>
    </DialogContent>
    <DialogActions>
        <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" data-testid="@CancelTestId" OnClick="CancelAsync">Cancel</AlvoButton>
        <AlvoButton Tone="AlvoButton.ButtonTone.Danger" Busy="@(Busy || _gate.Busy)" data-testid="@ConfirmTestId"
                    Disabled="@(!ConfirmGate.CanConfirm(TypeToConfirm, _typed, Busy, Allowed))"
                    OnClick="ConfirmAsync">@Verb</AlvoButton>
    </DialogActions>
</MudDialog>

@code {
    private readonly SubmitGate _gate = new();
    private readonly DialogOptions _options = new()
    {
        Position = DialogPosition.Center,
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
        CloseOnEscapeKey = true,
        BackdropClick = false,
        CloseButton = false,
        DefaultFocus = DefaultFocus.FirstChild,
    };

    private string _typed = string.Empty;

    /// <summary>Whether it is on screen.</summary>
    [Parameter]
    public bool Open { get; set; }

    /// <summary>The action, as a question or a verb phrase: "Delete WO-0001?".</summary>
    [Parameter, EditorRequired]
    public string Title { get; set; } = string.Empty;

    /// <summary>What is lost, concretely, first.</summary>
    [Parameter, EditorRequired]
    public string Consequence { get; set; } = string.Empty;

    /// <summary>The button's label, naming the verb.</summary>
    [Parameter, EditorRequired]
    public string Verb { get; set; } = string.Empty;

    /// <summary>The name to type before it may run, for an irreversible, wide-blast-radius action.</summary>
    [Parameter]
    public string? TypeToConfirm { get; set; }

    /// <summary>Whether the action is possible at all, such as a removal nothing still names.</summary>
    [Parameter]
    public bool Allowed { get; set; } = true;

    /// <summary>Whether the owner is still running the action.</summary>
    [Parameter]
    public bool Busy { get; set; }

    /// <summary>What else the operator must read first: the references, the plan's steps.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Raised on the verb.</summary>
    [Parameter]
    public EventCallback OnConfirm { get; set; }

    /// <summary>Raised on Cancel and on Escape.</summary>
    [Parameter]
    public EventCallback OnCancel { get; set; }

    /// <summary>The dialog's test id.</summary>
    [Parameter]
    public string TestId { get; set; } = "confirm-dialog";

    /// <summary>The verb button's test id.</summary>
    [Parameter]
    public string ConfirmTestId { get; set; } = "confirm-run";

    /// <summary>The Cancel button's test id.</summary>
    [Parameter]
    public string CancelTestId { get; set; } = "confirm-cancel";

    /// <summary>A closed confirm forgets what was typed, so the next one starts empty.</summary>
    protected override void OnParametersSet()
    {
        if (!Open)
        {
            _typed = string.Empty;
        }
    }

    private void Typed(string? typed) => _typed = typed ?? string.Empty;

    private async Task ConfirmAsync()
    {
        if (!ConfirmGate.CanConfirm(TypeToConfirm, _typed, Busy, Allowed) || !_gate.TryBegin())
        {
            return;
        }

        try
        {
            await OnConfirm.InvokeAsync();
        }
        finally
        {
            _gate.End();
        }
    }

    private Task CancelAsync() => OnCancel.InvokeAsync();

    private Task OnVisibleChangedAsync(bool visible) => visible ? Task.CompletedTask : OnCancel.InvokeAsync();
}
```

- [ ] **Step 9: The library seams in `alvo.css`**

At the end of the last `@layer components { … }` block, before `@layer utilities`, add this. Tokens only; every new class is named by a wrapper above:

```css
  /* ===========================================================================
     Library seams: MudBlazor's own classes, overridden once, and the wrappers'
     frames. MudBlazor sits in the lowest layer (alvo-mud.css), so these win over
     it whatever its specificity. The base layer's :focus-visible ring already
     reaches every Mud button, link and tab for the same reason.
     =========================================================================== */

  /* Tabs are not covered by the theme's button typography; the library uppercases them itself. */
  .mud-tab {
    text-transform: none;
    letter-spacing: normal;
  }

  /* An outlined input draws its own focused border; a second ring inside it is noise, not a signal. */
  .mud-input-slot:focus-visible {
    box-shadow: none;
  }

  .a-editor {
    width: min(560px, 100vw);
    max-width: 100vw;
    height: 100dvh;
    max-height: 100dvh;
    margin: 0;
    border-radius: 0;
    display: flex;
    flex-direction: column;
  }

  .a-editor__head {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
  }

  .a-editor__title {
    font-size: var(--text-lg);
    font-weight: var(--weight-bold);
    color: var(--text);
  }

  .a-editor__body {
    flex: 1;
    overflow: auto;
  }

  .a-editor__actions {
    gap: var(--space-2);
    border-top: 1px solid var(--border);
    padding: var(--space-3) var(--space-4);
  }

  .a-confirm-dialog__consequence {
    margin: 0;
    color: var(--text);
  }

  .a-alert:focus-visible {
    box-shadow: var(--focus-ring);
  }

  .a-alert__title {
    font-weight: var(--weight-bold);
  }

  .a-alert__actions {
    margin-top: var(--space-2);
  }

  .a-busy {
    margin-right: var(--space-2);
  }
```

`a-confirm-dialog`, `a-alert__body` and `a-editor__body` are named in markup only through `Class=`. Add bare rules for any class that `StylesheetHygieneTests.Every_class_the_product_names_is_defined` reports. An empty rule with a *why* comment is acceptable: `.a-confirm-dialog { /* the confirm's frame is Mud's own; the class is its handle */ }`.

- [ ] **Step 10: The review's reversal note**

In `docs/architecture/admin-dashboard-review.md`, under "### Considered and rejected", replace the "**A component library** (MudBlazor, Fluent UI)" bullet with:

```markdown
- ~~**A component library** (MudBlazor, Fluent UI).~~ **Reversed 24 Sep 2026 by the maintainer**
  (`docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md`, D1): the hand-rolled primitives are where the
  dashboard failed. There were four patterns for editing an item and a delete with no confirm, and nothing asserted
  behaviour (`evidence/2026-09-24-admin-ui-inventory.md`). MudBlazor 9.10.0 now draws the screens. The tokens stay
  the one source of the identity, mapped into its theme and pinned equal by `AlvoMudThemeTests`, so the "second
  theme" this bullet feared is a test, not a drift. Fluent UI Blazor v5 stays rejected (spec D2).
```

- [ ] **Step 11: Run the unit tests and make them pass**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: every new fact passes. The public-API approval fails once, because `AlvoButton`, `AlvoAlert`, `AlvoEditor`, `AlvoConfirm` and `AlvoTheme` are new.

Inspect the `.received.txt`. It must contain those five components, their parameters and their nested enums, and **no `MudBlazor`** (`LibraryBoundaryTests.The_approved_public_api_names_no_library_type` also says so). Then move it over the `.verified.txt` and rerun: PASS.

If `StylesheetHygieneTests` names a class, define or delete it, as Global Constraints say.

- [ ] **Step 12: The e2e helpers and the foundation scenarios (failing first)**

In `test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs`, add these members after `Button(...)`:

```csharp
    /// <summary>A dialog by its test id: an <c>AlvoEditor</c>, an <c>AlvoConfirm</c> or the palette.</summary>
    /// <param name="testId">The dialog's test id.</param>
    /// <returns>The dialog.</returns>
    public ILocator Dialog(string testId) => Page.GetByTestId(testId);

    /// <summary>Picks an option of a <c>MudSelect</c> by its visible name.</summary>
    /// <remarks>
    /// Page-scoped on purpose: the library renders options in its popover provider under <c>body</c>, so an option is
    /// never a descendant of the select, nor of the dialog the select sits in (study §5.1 gotcha 1).
    /// </remarks>
    /// <param name="combobox">The select, found by role and name.</param>
    /// <param name="option">The option's name, exactly.</param>
    public async Task ChooseAsync(ILocator combobox, string option)
    {
        await combobox.ClickAsync().ConfigureAwait(false);
        await Page.GetByRole(AriaRole.Option, new() { Name = option, Exact = true }).ClickAsync().ConfigureAwait(false);
    }

    /// <summary>Waits for the snackbar that says <paramref name="text"/>.</summary>
    /// <remarks>
    /// The library's own class is the handle: a snackbar is <c>role=alert</c>, and so is every error panel, and the
    /// difference between those two is exactly what a scenario asserts (spec §3.3). This is the one library class the
    /// suite names, here and nowhere in a scenario.
    /// </remarks>
    /// <param name="text">What it says, or part of it.</param>
    public Task SnackbarAsync(string text)
        => Page.Locator(".mud-snackbar").Filter(new() { HasText = text }).First.WaitForAsync();

    /// <summary>The focused element, as <c>tag#id[test id]</c>, for the focus rules of spec §3.4.</summary>
    /// <returns>A short description of <c>document.activeElement</c>.</returns>
    public Task<string> FocusedAsync()
        => Page.EvaluateAsync<string>(
            "() => { const e = document.activeElement; if (!e) return '';"
            + " return `${e.tagName.toLowerCase()}#${e.id}[${e.getAttribute('data-testid') ?? ''}]`; }");

    /// <summary>Whether focus is inside the element with <paramref name="testId"/>.</summary>
    /// <param name="testId">The container's test id.</param>
    /// <returns><see langword="true"/> when the focused element is it or inside it.</returns>
    public Task<bool> FocusIsInsideAsync(string testId)
        => Page.EvaluateAsync<bool>(
            "id => !!document.activeElement?.closest(`[data-testid='${id}']`)", testId);
```

In `SettleAsync`, after the keyboard wait, add:

```csharp
        /* And wait for the shell, which the keyboard does NOT imply: the keyboard is the palette's first render and
           the popover and dialog providers are the layout's. A select opened between the two opened nothing. */
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.dataset.alvoShell === 'ready'", null, _polling).ConfigureAwait(false);
```

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/FoundationScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The library is loaded beneath Alvo's rules, and both themes are right on the first paint (spec D8, D9).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class FoundationScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private const string LightAccent = "#0f7a48";
    private const string DarkAccent = "#39e991";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_library_stylesheet_is_loaded_into_the_lowest_layer_and_Alvos_spacing_survives_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        var layered = await session.Page.EvaluateAsync<bool>(
            "() => [...document.styleSheets].flatMap(s => { try { return [...s.cssRules]; } catch { return []; } })"
            + ".some(r => r instanceof CSSImportRule && r.layerName === 'mud' && r.styleSheet?.cssRules.length > 0)");
        layered.ShouldBeTrue("MudBlazor.min.css must arrive through alvo-mud.css, inside @layer mud");

        /* Mud's reset is *{padding:0}; the content pane's padding is an Alvo rule it must not beat. */
        (await session.Content.EvaluateAsync<string>("e => getComputedStyle(e).paddingTop")).ShouldNotBe("0px");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_stored_dark_theme_is_dark_on_the_first_paint_before_the_circuit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");
        await session.Page.EvaluateAsync("() => localStorage.setItem('alvo.theme', 'dark')");

        await session.Page.GotoAsync(session.Page.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        (await ThemeAsync(session)).ShouldBe("dark");
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);
        await session.SettleAsync();
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);
        await session.Page.EvaluateAsync("() => localStorage.removeItem('alvo.theme')");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task With_nothing_stored_the_theme_is_the_systems_and_follows_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await session.GoAsync("");

        (await ThemeAsync(session)).ShouldBe("dark");
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);

        await session.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await session.Page.WaitForFunctionAsync("() => document.documentElement.dataset.theme === 'light'");
        (await PrimaryAsync(session)).ShouldStartWith(LightAccent);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_theme_toggle_flips_the_library_palette_with_no_round_trip()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await session.GoAsync("");
        (await PrimaryAsync(session)).ShouldStartWith(LightAccent);

        await session.Page.GetByTestId("theme-toggle").ClickAsync();

        await session.Page.WaitForFunctionAsync("() => document.documentElement.dataset.theme === 'dark'");
        (await PrimaryAsync(session)).ShouldStartWith(DarkAccent);
        await session.Page.EvaluateAsync("() => localStorage.removeItem('alvo.theme')");
    }

    private static Task<string> ThemeAsync(AdminSession session)
        => session.Page.EvaluateAsync<string>("() => document.documentElement.dataset.theme ?? ''");

    private static async Task<string> PrimaryAsync(AdminSession session)
        => (await session.Page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-primary')")).Trim().ToLowerInvariant();
}
```

Run: `scripts/test-admin-e2e --filter FoundationScenarios`. Before Steps 3–7 are in place it fails (no `mud` layer, no `--mud-palette-primary`, no `data-alvo-shell`). With them it passes. If the library writes the colour in another notation (`rgba(…)`), assert against that notation for both accents and record it in the commit body. The spike read `#39e991` (study §3.3).

- [ ] **Step 13: All gates, including the Release image**

Run, in order:
1. `dotnet test --project test/MMLib.Alvo.Admin.Tests`
2. `scripts/test-admin-e2e`
3. `scripts/test-prototype`
4. `scripts/test-ring1`
5. `docker build -f src/MMLib.Alvo.Host/Dockerfile -t alvo-admin-mud-check .`

Expected: all green, and the image builds with 0 warnings. A CA/MUD analyzer error that only Release shows is fixed here, not deferred.

Existing scenarios must pass unchanged, which proves the layering. If one fails on a visual measurement (`AssertNoHorizontalScrollAsync`, `AssertNoVerticalTextAsync`), find the Mud rule that leaked. It can only be unlayered markup the providers inject (`#blazor-error-ui`, `#components-reconnect-modal`, study §6.2). Neutralise it in the seams block.

- [ ] **Step 14: Commit**

```bash
git add Directory.Packages.props src/MMLib.Alvo.Admin/MMLib.Alvo.Admin.csproj src/MMLib.Alvo.Admin/_Imports.razor \
  src/MMLib.Alvo.Admin/AlvoAdminServiceCollectionExtensions.cs src/MMLib.Alvo.Admin/AlvoAdminAssets.cs \
  src/MMLib.Alvo.Admin/Components/AdminApp.razor src/MMLib.Alvo.Admin/Components/Shell/AdminLayout.razor \
  src/MMLib.Alvo.Admin/Components/Shell/SignInLayout.razor src/MMLib.Alvo.Admin/Components/Shell/AlvoTheme.razor \
  src/MMLib.Alvo.Admin/wwwroot/alvo-mud.css src/MMLib.Alvo.Admin/wwwroot/alvo.css src/MMLib.Alvo.Admin/wwwroot/alvo.js \
  src/MMLib.Alvo.Admin/wwwroot/admin.js src/MMLib.Alvo.Admin/Internal/AdminInterop.cs \
  src/MMLib.Alvo.Admin/Internal/LibraryAssets.cs src/MMLib.Alvo.Admin/Internal/AlvoMudTheme.cs \
  src/MMLib.Alvo.Admin/Internal/AdminSnackbar.cs \
  src/MMLib.Alvo.Admin/Components/DesignSystem/AlvoButton.razor src/MMLib.Alvo.Admin/Components/DesignSystem/ButtonLook.cs \
  src/MMLib.Alvo.Admin/Components/DesignSystem/AlvoAlert.razor src/MMLib.Alvo.Admin/Components/DesignSystem/AlertLook.cs \
  src/MMLib.Alvo.Admin/Components/DesignSystem/AlvoEditor.razor src/MMLib.Alvo.Admin/Components/DesignSystem/EditorCloseGuard.cs \
  src/MMLib.Alvo.Admin/Components/DesignSystem/SubmitGate.cs src/MMLib.Alvo.Admin/Components/DesignSystem/AlvoConfirm.razor \
  src/MMLib.Alvo.Admin/Components/DesignSystem/ConfirmGate.cs \
  test/MMLib.Alvo.Admin.Tests/LibraryBoundaryTests.cs test/MMLib.Alvo.Admin.Tests/LibraryLayerTests.cs \
  test/MMLib.Alvo.Admin.Tests/Internal/AlvoMudThemeTests.cs test/MMLib.Alvo.Admin.Tests/Internal/AdminInteropTests.cs \
  test/MMLib.Alvo.Admin.Tests/DesignSystem/SubmitGateTests.cs test/MMLib.Alvo.Admin.Tests/DesignSystem/EditorCloseGuardTests.cs \
  test/MMLib.Alvo.Admin.Tests/DesignSystem/ConfirmGateTests.cs test/MMLib.Alvo.Admin.Tests/DesignSystem/ButtonLookTests.cs \
  test/MMLib.Alvo.Admin.Tests/DesignSystem/AlertLookTests.cs test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt \
  test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/FoundationScenarios.cs \
  docs/architecture/admin-dashboard-review.md
git commit -m "feat(admin): MudBlazor foundation beneath the design system, with the wrapper layer

MudBlazor 9.10.0, pinned exactly, is imported into @layer mud beneath Alvo's five layers
(alvo-mud.css), so no existing screen moves. The theme is built from the tokens and pinned
equal to them; a second provider scoped to [data-theme=dark] plus alvo.js always writing a
resolved theme gives dark mode with no flash. AlvoButton, AlvoAlert, AlvoEditor, AlvoConfirm
and AlvoTheme carry the pattern language's policy; none takes a MudBlazor type (D5, pinned by
LibraryBoundaryTests). The public API grows by those five components only: Blazor compiles
every component public, and Components.* is implementation (F-10).

Deviations: V1 (a second, internal stylesheet), V2 (library assets internal), V3 (palette
duplicated and pinned), V4 (editor full-width below 560 px).

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---
### Task 2: The shell — app bar, nav drawer, account menu, pending bar, and the palette on `MudDialog`

Spec §3.7 (navigation), §3.4 (focus/Escape), §4 row "Command palette". Study §4.2 rows Shell / CommandPalette / PendingBar / ThemeToggle / SignedInAs. Inventory defect #11 (the palette re-implemented modal chrome).

**Deviation V8 (record it in the commit):** the nav entries stay `SectionLink` (Alvo's `NavLink` subclass) inside `MudNavMenu`. `MudNavLink` cannot express `AdminSection.OwnsAlso`: Schema owns `/changes`, Data owns the record routes. A second matching rule would be a second navigation to keep in step (the shell's own argument, `AdminLayout.razor` header).

**Files:**
- Modify: `Components/Shell/AdminLayout.razor`. The `a-shell` / `a-sidebar` / `a-header` markup is replaced by `MudLayout` / `MudAppBar` / `MudDrawer` / `MudMainContent`. The phone "sections sheet" is deleted: the temporary drawer replaces it.
- Modify:
  - `Components/Shell/NavList.razor`: wrapped in `MudNavMenu Dense="true"`, links unchanged.
  - `Components/Shell/ThemeToggle.razor`: an `AlvoButton` Ghost icon button.
  - `Components/Shell/SignedInAs.razor`: a `MudMenu` in the app bar.
  - `Components/Shell/PendingBar.razor`: `AlvoButton`s.
  - `Components/Shell/CommandPalette.razor`: an inline `MudDialog`.
- Modify: `wwwroot/alvo.css`:
  - Shell section: replace the `.a-shell`, `.a-sidebar*`, `.a-header`, `.a-main`, `.a-sheet` (sections-sheet) and `.a-scrim--palette` / `.a-palette` frame rules the new markup no longer names.
  - Add `.a-appbar`, `.a-navdrawer`, `.a-main`, `.a-palette-dialog`.
  - Keep `.a-bottomnav*`, `.a-content`, `.a-nav*`, `.a-palette__*` (the list inside the dialog).
  - Mark `/* prototype-only */` what the prototype still renders.
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/ShellScenarios.cs`.
- Modify (port): `PhoneAndKeyboardScenarios.cs`, `ShellFrameScenarios.cs`, `SignInScenarios.cs`, `test/MMLib.Alvo.Admin.Tests/EndToEndSelectorTests.cs` (lower the constants).

**Interfaces:**
- Consumes (Task 1): `AlvoButton`, `AlvoTheme`, the providers, `AdminSession.Dialog`, `AdminSession.FocusedAsync`.
- Produces these test ids for later tasks:
  - `appbar` (the `MudAppBar`)
  - `sidebar` (the `<nav>` inside the drawer, kept)
  - `more-sections` (the app bar button that opens the drawer on a phone, kept)
  - `account-menu` (the account menu's activator button, named "Account")
  - `palette` (the palette dialog), `palette-input` (its search box)
  - `theme-toggle` (kept)
  - `pending-bar`, `pending-count`, `pending-discard`, `pending-preview` (kept)
- The `<main class="a-content" id="a-content">` landmark stays exactly as it is. `AdminSession.Content` and `AssertNoHorizontalScrollAsync` read it.

- [ ] **Step 1: Write the failing shell scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/ShellScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// One app bar, one nav drawer, and a palette that is a real dialog (spec §3.7, §3.4; inventory defect #11).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class ShellScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_palette_is_a_dialog_that_takes_focus_and_gives_it_back_on_Escape()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        var search = session.Page.GetByTestId("appbar").GetByRole(AriaRole.Button, new() { Name = "Search" });

        await search.ClickAsync();

        var palette = session.Dialog("palette");
        await palette.WaitForAsync();
        (await palette.GetAttributeAsync("role")).ShouldBe("dialog");
        (await session.FocusedAsync()).ShouldContain("[palette-input]");

        await session.Page.Keyboard.PressAsync("Escape");
        await palette.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await search.EvaluateAsync<bool>("e => e === document.activeElement")).ShouldBeTrue("focus returns to the trigger");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Tabbing_to_an_app_bar_button_shows_a_focus_ring()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");
        var toggle = session.Page.GetByTestId("theme-toggle");

        await toggle.FocusAsync();
        await session.Page.Keyboard.PressAsync("Shift+Tab");
        await session.Page.Keyboard.PressAsync("Tab");

        (await toggle.EvaluateAsync<bool>("e => e.matches(':focus-visible')")).ShouldBeTrue();
        (await toggle.EvaluateAsync<string>("e => getComputedStyle(e).boxShadow")).ShouldNotBe("none");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task On_a_phone_the_drawer_is_closed_until_asked_for_and_closes_on_a_choice()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 390);
        await session.GoAsync("");
        var sidebar = session.Page.GetByTestId("sidebar");
        (await sidebar.IsVisibleAsync()).ShouldBeFalse();
        (await session.Page.GetByTestId("bottom-nav").IsVisibleAsync()).ShouldBeTrue();

        await session.Page.GetByTestId("more-sections").ClickAsync();
        await sidebar.GetByRole(AriaRole.Link, new() { Name = "History", Exact = true }).ClickAsync();

        await session.Page.WaitForURLAsync("**/history");
        await sidebar.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await session.AssertNoHorizontalScrollAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task On_a_desktop_the_drawer_is_open_and_the_bottom_bar_is_gone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 1440);
        await session.GoAsync("");

        (await session.Page.GetByTestId("sidebar").IsVisibleAsync()).ShouldBeTrue();
        (await session.Page.GetByTestId("bottom-nav").IsVisibleAsync()).ShouldBeFalse();
        (await session.Page.GetByTestId("more-sections").IsVisibleAsync()).ShouldBeFalse();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_account_menu_says_who_is_signed_in_and_signs_them_out()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.GetByTestId("account-menu").ClickAsync();
        await session.Page.GetByText(AdminWorld.AdminEmail).First.WaitForAsync();
        await session.Page.GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" }).ClickAsync();

        await session.Page.WaitForURLAsync("**/admin/sign-in**");
    }
}
```

Run: `scripts/test-admin-e2e --filter ShellScenarios`
Expected: FAIL. There is no `appbar`, `palette` or `account-menu`, and the palette is not `role=dialog`.

- [ ] **Step 2: The layout on `MudLayout`**

Replace the markup of `AdminLayout.razor` from `<div class="a-shell">` down to the closing `</div>` before `<CommandPalette …/>` with this. Keep the providers from Task 1 above it, keep the whole `@code` block, and adapt it as shown after the markup:

```razor
<MudLayout Class="a-shell">
    <MudAppBar Dense="true" Elevation="0" Class="a-appbar" data-testid="appbar">
        <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" class="a-only-mobile" aria-label="Sections"
                    aria-expanded="@(_navOpen ? "true" : "false")" data-testid="more-sections" OnClick="ToggleNav">
            <Icon Path="@Icons.More" />
        </AlvoButton>
        <a class="a-brand" href="@AdminPaths.Overview" aria-label="Alvo">
            <img src="@AlvoAdminAssets.Mark" width="26" height="26" alt="" />
            <span>Alvo</span>
        </a>
        <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" aria-label="Search" OnClick="OpenPalette">
            <Icon Path="@Icons.Search" />
            <span>Search</span>
            <kbd class="a-kbd">&#8984;K</kbd>
        </AlvoButton>
        <MudSpacer />
        <ThemeToggle />
        <SignedInAs />
    </MudAppBar>

    <MudDrawer @bind-Open="_navOpen" Variant="DrawerVariant.Responsive" Breakpoint="Breakpoint.Sm"
               ClipMode="DrawerClipMode.Always" Elevation="0" Class="a-navdrawer">
        <nav aria-label="Sections" data-testid="sidebar" class="a-navdrawer__nav">
            <ProjectSwitcher />
            <MudNavMenu Dense="true">
                <NavList OnNavigate="CloseNavOnPhone" />
            </MudNavMenu>
        </nav>
    </MudDrawer>

    <MudMainContent Class="a-main">
        <PendingBar />
        <main class="a-content" id="a-content">
            @* (the ErrorBoundary block, unchanged, including its comment) *@
        </main>
        @if (_assistant)
        {
            <AssistantDrawer />
        }
    </MudMainContent>
</MudLayout>

<nav class="a-bottomnav" aria-label="Sections" data-testid="bottom-nav">
    @foreach (var entry in AdminNavigation.Bar)
    {
        @SectionLink.For(entry, "a-bottomnav__item", "a-bottomnav__item--active", BarContent(entry))
    }
</nav>
```

In `@code`:
- Delete `_sheetOpen`, `OpenSheet` and `CloseSheet`.
- Add:

```csharp
    private bool _navOpen = true;

    private void ToggleNav() => _navOpen = !_navOpen;

    /// <summary>
    /// A choice in the drawer closes it on a phone, where it covers the page; on a desktop it is the persistent
    /// sidebar and stays. The responsive drawer reports which it is by closing itself below the breakpoint.
    /// </summary>
    private void CloseNavOnPhone()
    {
        if (_phone)
        {
            _navOpen = false;
        }
    }
```

For `_phone`, use the library's viewport service: `@inject IBrowserViewportService Viewport`. In `OnAfterRenderAsync(firstRender)`, after `MarkShellReadyAsync`, subscribe with `await Viewport.SubscribeAsync(this, fireImmediately: true)`, and implement `IBrowserViewportObserver`, whose `NotifyBrowserViewportChangeAsync(BrowserViewportEventArgs e)` sets `_phone = e.Breakpoint <= Breakpoint.Xs` and calls `StateHasChanged` through `InvokeAsync`. Unsubscribe in `Dispose` (`Viewport.UnsubscribeAsync(this)`).

Two cases need a fallback:
- **The observer interface differs in 9.10.0.** Drop the service. Pass `OnNavigate="CloseNavOnPhone"` and make `CloseNavOnPhone` set `_navOpen = false` unconditionally. A persistent drawer ignores `Open=false` above the breakpoint only if the library says so, so measure it with `On_a_desktop_the_drawer_is_open_…` after one navigation.
- **Implementing an interface would put a library type on a public component** (`LibraryBoundaryTests` checks public members, and `IBrowserViewportObserver`'s members would be public). Implement the observer on a small `internal sealed class PhoneWatch : IBrowserViewportObserver` that raises an `Action<bool>` instead. The layout holds one, so the layout stays free of library types.

`NavList` loses its `BeyondTheBar` use: the drawer lists every section on both widths. Keep the parameter only if another caller remains (none does), otherwise delete it and its branch. The PublicApi baseline then loses `NavList.BeyondTheBar`, which is expected; say so in the commit body.

- [ ] **Step 3: Theme toggle, account menu, pending bar**

`ThemeToggle.razor`: replace the `<button …>` with this. The `@code` block is unchanged:

```razor
<AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" OnClick="Toggle" aria-label="@Label" title="@Label"
            data-testid="theme-toggle">
    <Icon Path="@(_theme == "dark" ? Icons.Sun : Icons.Moon)" />
</AlvoButton>
```

`AlvoButton.OnClick` is `EventCallback<MouseEventArgs>`, and `Toggle` takes none. Keep `private async Task Toggle()` and pass `OnClick="_ => Toggle()"`.

`SignedInAs.razor`: replace the `<Authorized>` content with a menu. The sign-out stays a real form post, because that is the whole security point: a GET sign-out is triggerable by an `<img>`.

```razor
    <Authorized>
        <MudMenu AnchorOrigin="Origin.BottomRight" TransformOrigin="Origin.TopRight" Dense="true" Class="a-account">
            <ActivatorContent>
                <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" aria-label="Account" data-testid="account-menu">
                    <span class="a-brand-mark a-brand-mark--quiet">@Initial(context.User.Identity?.Name)</span>
                </AlvoButton>
            </ActivatorContent>
            <ChildContent>
                <div class="a-account__who">
                    <span class="a-switcher-name">@context.User.Identity?.Name</span>
                    <span class="a-meta">@Roles(context) · @Tenant(context)</span>
                </div>
                <form method="post" action="@AlvoAdmin.SignOutEndpoint" data-enhance="false">
                    <AntiforgeryToken />
                    <button type="submit" role="menuitem" class="a-account__signout">Sign out</button>
                </form>
            </ChildContent>
        </MudMenu>
    </Authorized>
```

In 9.10.0, `ActivatorContent` is a `RenderFragment<MenuContext>` (study §1.2). If the activator does not open the menu on click, add `OnClick="_ => context.ToggleAsync()"` to the `AlvoButton`. Inside `ActivatorContent`, `context` is the menu's context. Rename the `AuthorizeView` context to `auth` (`<AuthorizeView Context="auth">`) so the two do not collide, and update `@Initial(auth.User…)`, `Roles(auth)`, `Tenant(auth)`.

`PendingBar.razor`: replace the two controls with:

```razor
            <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="pending-discard"
                        OnClick="() => _discarding = true">Discard</AlvoButton>
            <AlvoButton Tone="AlvoButton.ButtonTone.Primary" Small="true" data-testid="pending-preview"
                        Href="@AdminPaths.Changes">Preview</AlvoButton>
```

- [ ] **Step 4: The palette on `MudDialog`**

In `CommandPalette.razor`, replace the `@if (_open) { <div class="a-scrim a-scrim--palette" …> … </div> }` block with an inline dialog. The list and its keyboard handling are unchanged:

```razor
<MudDialog Visible="_open" VisibleChanged="OnVisibleChanged" Options="_options" Class="a-palette-dialog"
           data-testid="palette" OnKeyDown="OnKeyDown">
    <DialogContent>
        <input class="a-palette__input" placeholder="Go to a section or an entity, or run an action…"
               @ref="_input" @bind="_query" @bind:event="oninput" aria-label="Search" data-testid="palette-input" />
        @FocusOnRender.On(() => _input)
        <div role="listbox" class="a-palette__list">
            @* (the empty row and the option buttons, exactly as they are today) *@
        </div>
    </DialogContent>
</MudDialog>
```

In `@code`:

```csharp
    private readonly DialogOptions _options = new()
    {
        Position = DialogPosition.TopCenter,
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
        NoHeader = true,
        CloseOnEscapeKey = true,
        BackdropClick = true,
        CloseButton = false,
        DefaultFocus = DefaultFocus.FirstChild,
    };

    /// <summary>Escape or the scrim closed it; the palette forgets its query the same way its own Close does.</summary>
    private void OnVisibleChanged(bool visible)
    {
        if (!visible)
        {
            Close();
        }
    }
```

Then:
- Delete the palette's own Escape branch in `OnKeyDown`, because the dialog owns Escape now.
- Delete the `_focusPending` + `FocusAndSelectAsync` path, because `FocusOnRender` inside the dialog content replaces it.
- Set `_query = string.Empty` in `OpenAsync`, so a reopened palette starts empty.
- Keep the `ScrollLock` hold/release. The content pane is what scrolls, and the dialog does not lock it.
- Keep the `OnDismiss` subscription only if something other than the palette still needs `alvo:dismiss`; otherwise remove it and its subscription.

The palette's `OnKeyDown` keeps ArrowUp/ArrowDown/Enter. `MudDialog.OnKeyDown` receives them from the input as they bubble. That depends on the same unverified `OnKeyDown` as Task 1, with the same fallback: `<div @onkeydown="OnKeyDown">` around the content.

- [ ] **Step 5: CSS**

In `alvo.css`, Shell section (`@layer layout`), replace the old frame rules with these. Tokens only; the breakpoints stay the existing `720px` queries:

```css
  .a-appbar {
    border-bottom: 1px solid var(--border);
    gap: var(--space-2);
  }

  .a-navdrawer {
    border-right: 1px solid var(--border);
  }

  .a-navdrawer__nav {
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    padding: var(--space-3);
  }

  /* The pane scrolls, never the document: the app bar and the drawer stay put (ShellFrameScenarios). */
  .a-main {
    display: flex;
    flex-direction: column;
    height: 100dvh;
    min-height: 0;
  }
```

- In the existing `@media (max-width: 720px)` block, keep the bottom bar's rules.
- Add `.a-main { height: calc(100dvh - var(--a-bottomnav-h, 56px)); }` if the bottom bar would otherwise cover the pane's last row. `The_phone_bar_sits_under_the_content_not_beside_it` measures it.
- Delete every rule for `.a-sidebar`, `.a-sidebar__foot`, `.a-header`, `.a-scrim--palette` and the `.a-palette` frame that no product markup names. If the prototype renders one, mark it `/* prototype-only */` instead.
- Add `.a-palette-dialog { /* the palette's frame is the library's dialog; the class is its handle */ }` if the hygiene test asks for it.

- [ ] **Step 6: Port the scenarios the new DOM changes**

- `PhoneAndKeyboardScenarios.cs`:
  - Every `session.Page.Locator(".a-palette")` becomes `session.Dialog("palette")`.
  - `WaitForFunctionAsync("document.activeElement?.classList.contains('a-palette__input')")` becomes `WaitForFunctionAsync("document.activeElement?.dataset.testid === 'palette-input'")`.
  - `.a-palette [role='option']:has-text('Data') kbd:has-text('g d')` becomes `session.Dialog("palette").GetByRole(AriaRole.Option, new() { Name = "Data" }).Filter(new() { HasText = "g d" })`.
  - `Every_section_is_reachable_from_a_phone`: `sections-sheet` becomes `session.Page.GetByTestId("sidebar")` after clicking `more-sections`. The "Sign out" check moves to the account menu: click `account-menu`, then `GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" })` count is 1.
- `ShellFrameScenarios.The_chrome_stays_put_while_the_content_scrolls`:
  - The brand link is `session.Page.GetByTestId("appbar").GetByRole(AriaRole.Link, new() { Name = "Alvo" })`.
  - The signed-in check is `session.Page.GetByTestId("account-menu")` in view.
  - `Button("Search")` is unchanged.
- `SignInScenarios.cs:210`: `ClickAsync("button:has-text('Sign out')")` becomes an open of `account-menu` followed by `GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" }).ClickAsync()`.
- `EndToEndSelectorTests.cs`: run `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-method "*scenarios*"`, or just the class. Set `ClassSelectors` and `HasTextSelectors` to the counts its message reports; both are lower than 43 / 51.

- [ ] **Step 7: Run everything and make it pass**

Run: `scripts/test-admin-e2e`
Expected: PASS, including `ShellScenarios`, `PhoneAndKeyboardScenarios`, `ShellFrameScenarios`, `SignInScenarios`, `RevokedSessionScenarios` and `OtherCircuitApplyScenarios` (which clicks a sidebar link).

Then run:
1. `dotnet test --project test/MMLib.Alvo.Admin.Tests`, accepting the `.received.txt` if `NavList.BeyondTheBar` left.
2. `scripts/test-prototype`
3. `scripts/test-ring1`

- [ ] **Step 8: Commit**

Stage by name every file touched above, and commit:

```text
feat(admin): the shell on MudLayout, with the palette as a real dialog

One app bar (search, theme, the account menu with a form-post sign-out), one responsive nav
drawer that is a temporary drawer on a phone, and the bottom bar kept. The command palette is
an inline MudDialog: focus, Escape and focus return come from the dialog instead of its own
scrim (inventory defect #11). Nav entries stay SectionLink inside MudNavMenu (V8): MudNavLink
cannot express a section owning another section's routes.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---

### Task 3: The overlay primitive — every `Sheet` user onto `AlvoEditor` / `AlvoConfirm`, errors onto `AlvoAlert`

Spec §3.1–§3.4. Inventory defects #5 (no unsaved-changes guard) and #8/#9 in their structural half (the confirms themselves land with their screens in Tasks 4 and 7). Study §7.1, §7.4.

**Files:**
- Modify:
  - `Components/Schema/FieldEditor.razor` + `FieldEditor.razor.cs`: `Sheet` becomes `AlvoEditor`. Add a dirty fingerprint and the submit through `OnSubmit`.
  - `Components/Data/RecordForm.razor` + `RecordForm.razor.cs`: `Sheet` becomes `AlvoEditor`, dirty is `_draft.Dirty`, and Delete moves to `ExtraActions`. Its confirm lands in Task 4.
  - `Components/Schema/Entity.razor` + `Entity.razor.cs`: the rename `Sheet` becomes an `AlvoEditor`. The remove-field `Sheet` becomes an `AlvoConfirm` listing the references, with `Allowed` false while one blocks.
  - `Components/Schema/DiscardSheet.razor`, renamed to `Components/Schema/DiscardConfirm.razor`: an `AlvoConfirm`. Update its two callers, `Shell/PendingBar.razor` and `Schema/Preview.razor`.
  - `Components/DesignSystem/PageHeader.razor`: the phone overflow `Sheet` becomes a `MudMenu`.
  - `Components/DesignSystem/ErrorPanel.razor`: rendered through `AlvoAlert` (Tone Error, `TestId="error-panel"`, `TitleTestId="error-title"`), with a new `bool TakeFocus` parameter passed through.
- Modify: `wwwroot/alvo.css`: delete or mark the rules the retired markup no longer names (`.a-sheet__actions`, the rename and remove sheet bodies). `.a-sheet*` itself stays until Task 11, while `Sheet.razor` still names it.
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/EditorScenarios.cs`.
- Modify (port):
  - `SchemaEditingScenarios.cs`: `sheet-close` becomes `editor-cancel`; `[data-testid='rename-sheet'] .a-error` becomes `Dialog("rename-sheet").GetByTestId("error-panel")`; remove-field testids.
  - `PendingWorkScenarios.cs`: discard.
  - `DestructivePlanScenarios.cs` and every other scenario that clicks `remove-field-*`: add `await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();` after it, because every removal now confirms.
  - `PhoneAndKeyboardScenarios.cs`: `pagehead-overflow-sheet` becomes a `menuitem`.
  - `FieldDefaultScenarios.cs`, `FieldFacetScenarios.cs`, `FieldReferenceScenarios.cs`, `MaintainedFieldScenarios.cs`: only where they close a sheet.
  - `EndToEndSelectorTests.cs`.
  - `PublicApi.MMLib.Alvo.Admin.verified.txt`: `DiscardSheet` becomes `DiscardConfirm`, and `ErrorPanel.TakeFocus` is added.

**Interfaces:**
- Consumes (Task 1): the full `AlvoEditor`, `AlvoConfirm` and `AlvoAlert` parameter lists, and `AdminSession.Dialog/FocusedAsync/FocusIsInsideAsync/SnackbarAsync`.
- Produces:
  - Test ids kept: `field-sheet` (the field `AlvoEditor`), `field-save` (its submit), `field-save-another` (in `ExtraActions`), `record-sheet`, `rename-sheet`, `rename-save`.
  - `remove-field-sheet` (the `AlvoConfirm`) with `remove-field-anyway` as its `ConfirmTestId` and `remove-field-cancel` as its `CancelTestId`; `remove-field-blocked` and `field-reference` are kept.
  - `discard-sheet` / `discard-confirm` / `discard-cancel` (kept, as `AlvoConfirm` test ids).
  - `pagehead-overflow` (the menu activator, kept); the menu items are `role=menuitem`.
  - `ErrorPanel.TakeFocus`.
  - Snackbars (through `ISnackbar.Confirm`):
    - "Saved to the working copy" after a field add or edit.
    - "Renamed to {name} in the working copy" after a rename.
    - "Discarded the working copy" after a discard.

- [ ] **Step 1: Write the failing editor scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/EditorScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The side-sheet editor behaves the same way everywhere (spec §3.1, §3.4): focus lands on the first field, Escape
/// closes and gives focus back, unsaved work is guarded, a submit cannot run twice, and a refusal takes focus.
/// </summary>
/// <remarks>Its own world: it stages fields, and the working copy is one per operator.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class EditorScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_editor_opens_on_its_first_field_and_Escape_gives_focus_back_to_the_trigger()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        var trigger = session.Page.GetByTestId("add-field");

        await trigger.ClickAsync();
        var editor = session.Dialog("field-sheet");
        await editor.WaitForAsync();

        (await editor.EvaluateAsync<string?>("e => e.closest('[role=dialog]')?.getAttribute('aria-modal')")).ShouldBe("true");
        (await session.FocusedAsync()).ShouldStartWith("input#new-field-name");

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await trigger.EvaluateAsync<bool>("e => e === document.activeElement")).ShouldBeTrue();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_editor_with_changes_asks_before_it_closes_and_keeps_them_when_told_to()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var editor = session.Dialog("field-sheet");
        await session.Page.FillAsync("#new-field-name", "half_typed");

        await session.Page.Keyboard.PressAsync("Escape");
        var question = editor.GetByTestId("editor-discard-question");
        await question.WaitForAsync();
        (await session.FocusIsInsideAsync("editor-discard-question")).ShouldBeTrue();

        await editor.GetByTestId("editor-keep").ClickAsync();
        (await session.Page.InputValueAsync("#new-field-name")).ShouldBe("half_typed");

        await editor.GetByTestId("editor-cancel").ClickAsync();
        await editor.GetByTestId("editor-discard").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("field-row-half_typed").CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_save_stages_one_field_and_says_so()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.FillAsync("#new-field-name", "loyalty_tier");

        await session.Dialog("field-sheet").GetByTestId("field-save").DblClickAsync();

        await session.SnackbarAsync("Saved to the working copy");
        await session.Page.GetByTestId("field-row-loyalty_tier").WaitForAsync();
        (await session.Page.GetByTestId("field-row-loyalty_tier").CountAsync()).ShouldBe(1);
        (await session.Page.GetByTestId("staged-loyalty_tier").CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_field_is_an_alert_in_the_editor_and_it_takes_focus()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.FillAsync("#new-field-name", "Not A Name");

        await session.Dialog("field-sheet").GetByTestId("field-save").ClickAsync();

        var alert = session.Dialog("field-sheet").GetByTestId("error-panel");
        await alert.WaitForAsync();
        (await alert.GetAttributeAsync("role")).ShouldBe("alert");
        (await session.FocusIsInsideAsync("error-panel")).ShouldBeTrue();
        (await session.Page.Locator(".mud-snackbar").CountAsync()).ShouldBe(0, "an error is never a snackbar");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_field_cannot_happen_without_its_confirm()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        var remove = session.Page.GetByTestId("remove-field-name");

        await remove.ClickAsync();
        var confirm = session.Dialog("remove-field-sheet");
        await confirm.WaitForAsync();
        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("staged-name").CountAsync()).ShouldBe(0, "Escape is Cancel");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Discarding_the_working_copy_is_a_confirm_whose_Cancel_keeps_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        await session.Page.FillAsync("#new-field-name", "kept_until_discarded");
        await session.Dialog("field-sheet").GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId("pending-bar").WaitForAsync();

        await session.Page.GetByTestId("pending-discard").ClickAsync();
        await session.Dialog("discard-sheet").GetByTestId("discard-cancel").ClickAsync();
        (await session.Page.GetByTestId("pending-bar").IsVisibleAsync()).ShouldBeTrue();

        await session.Page.GetByTestId("pending-discard").ClickAsync();
        await session.Dialog("discard-sheet").GetByTestId("discard-confirm").ClickAsync();
        await session.SnackbarAsync("Discarded the working copy");
        await session.Page.GetByTestId("pending-bar").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }
}
```

`customers` has a `name` field in the field-service example, so `remove-field-name` exists. If that field is required by a rule, the confirm shows `remove-field-blocked` instead of an enabled verb, and the scenario still holds: Escape cancels either way. The `"Not A Name"` refusal comes from `FieldFacets.Build` (the name pattern `DescriptorNames.Hint` describes).

Run: `scripts/test-admin-e2e --filter EditorScenarios`
Expected: FAIL. There is no `role=dialog`/`aria-modal` on the sheet's ancestor chain in the Mud sense, no `editor-discard-question`, no snackbar, the double click stages twice or the second click lands on a closed sheet, and there is no remove confirm.

- [ ] **Step 2: `ErrorPanel` over `AlvoAlert`**

Replace the markup of `ErrorPanel.razor` from `<div class="a-error" …>` to its closing `</div>`. The header comment and the whole `@code` block stay; add the parameter shown after the markup:

```razor
<AlvoAlert Tone="AlvoAlert.AlertTone.Error" Title="@_text.Headline" TakeFocus="TakeFocus"
           TestId="error-panel" TitleTestId="error-title">
    <ChildContent>
        <div class="a-error__detail">@_text.Detail</div>
        @if (_text.Fix is { Length: > 0 })
        {
            <div class="a-error__fix">@_text.Fix</div>
        }
        @if (Slug is { Length: > 0 })
        {
            <code class="a-error__type">@Slug</code>
        }
    </ChildContent>
    <Actions>
        @if (_text.OffersSignOut)
        {
            @* (the sign-out form, unchanged, with its comment; its button becomes an AlvoButton Small Submit
               data-testid="forbidden-sign-out") *@
        }
        @Actions
    </Actions>
</AlvoAlert>
```

```csharp
    /// <summary>
    /// Whether the panel takes focus as it appears, which a failed submit asks for (spec §3.3). A panel that was
    /// already on screen when the page loaded does not, because it would pull focus off the heading the router gave it.
    /// </summary>
    [Parameter]
    public bool TakeFocus { get; set; }
```

`AlvoAlert` renders `Actions` whenever it is non-null. Pass the `<Actions>` fragment only when there is something in it: build it as `RenderFragment? ActionsFor()`, which returns `null` when there is no sign-out and no `Actions`.

- [ ] **Step 3: `FieldEditor` on `AlvoEditor`**

In `FieldEditor.razor`, replace `<Sheet Open="true" TestId="field-sheet" …>` and its closing `</Sheet>` with the tags below. Everything between them stays, except the old action row: delete the `<div class="a-sheet__actions">…</div>` (or its equivalent) that held `field-save` and `field-save-another`.

```razor
<AlvoEditor TestId="field-sheet" Title="@(IsEditing ? $"Edit {Editing}" : "New field")"
            Subtitle="@(IsEditing
                ? "The change joins the working copy. Preview shows what it does to the table before anything runs."
                : "It joins the working copy. Preview shows what it does to the table before anything runs.")"
            SubmitText="Save to the working copy" SubmitTestId="field-save" Dirty="Dirty"
            OnSubmit="Add" OnClose="Cancel">
    <ExtraActions>
        @if (!IsEditing)
        {
            <AlvoButton data-testid="field-save-another" OnClick="_ => AddAnother()">Save and add another</AlvoButton>
        }
    </ExtraActions>
    <ChildContent>
        @* (the existing content, from `<div class="a-stack">` on, with its refusal panel changed as below) *@
    </ChildContent>
</AlvoEditor>
```

- The title "Add a field" becomes "New field", per spec §3.1 ("New …" / "Edit …"). Search the e2e for "Add a field" and update those assertions.
- The refusal panel becomes `<ErrorPanel TakeFocus="true" Title="…" Fix="@_refusal" />` (spec §3.3: on a failed submit, focus moves to the error).

In `FieldEditor.razor.cs`:
1. Add the dirty fingerprint:

```csharp
    private string _opened = string.Empty;

    /// <summary>Whether the form differs from how it opened, which is what Escape must not throw away.</summary>
    private bool Dirty => Fingerprint() != _opened;

    /// <summary>
    /// The field the form would stage, or, when it could not stage one yet, the choices that are made so far. Two
    /// forms with the same fingerprint lose nothing by closing.
    /// </summary>
    private string Fingerprint()
        => _facets.Build(Editing, EditingJson, Siblings, out _)?.ToJsonString()
            ?? string.Join('|', "unbuilt", _facets.Name, _facets.Kind, _facets.Type, _facets.Values);
```

2. Capture the opening fingerprint at the end of `OnParametersSet` the first time, and whenever the target changes: `if (_opened.Length == 0 || prefilledNow) { _opened = Fingerprint(); }`, where `prefilledNow` is `true` in the branch that calls `FieldFacets.Prefill`.
3. `AddAnother` resets `_facets`, so reset `_opened = Fingerprint()` there too.
4. Remove the `Cancel` → `OnClose.InvokeAsync()` indirection only if `AlvoEditor.OnClose` can bind `OnClose` directly. It cannot: `OnClose` is the component's own parameter, so keep `Cancel`.
5. The page that owns the editor (`Entity.razor.cs`, the `OnAdd` handler) calls `Snackbar.Confirm("Saved to the working copy")` after a successful stage. Add `@inject ISnackbar Snackbar` to `Entity.razor`.

- [ ] **Step 4: `RecordForm` on `AlvoEditor`**

In `RecordForm.razor`, replace `<Sheet Open="true" TestId="record-sheet" Eyebrow="@Entity.Name" Title="@Title" OnClose="Cancel">` / `</Sheet>` with the markup below, and delete the old `<div class="a-sheet__actions">` block:

```razor
<AlvoEditor TestId="record-sheet" Eyebrow="@Entity.Name" Title="@Title"
            SubmitText="@(RecordId is null ? "Create record" : "Save record")" SubmitTestId="record-save"
            Dirty="_draft.Dirty" Busy="_saving" OnSubmit="Save" OnClose="Cancel">
    <ExtraActions>
        @if (RecordId is not null)
        {
            <AlvoButton Tone="AlvoButton.ButtonTone.Danger" Small="true" data-testid="record-delete"
                        Disabled="_saving" OnClick="_ => Delete()">Delete record</AlvoButton>
        }
    </ExtraActions>
    <ChildContent>
        @* (the existing content, unchanged: the problem panel with TakeFocus="true", the fields, the readout, the
           API disclosure) *@
    </ChildContent>
</AlvoEditor>
```

The e2e today clicks buttons named "Create" and "Save" inside `record-sheet`: `ChangeTheBackendScenarios.SheetButton`, `RecordFormScenarios`, `DataGridScenarios`. Port those to `session.Dialog("record-sheet").GetByTestId("record-save")`.

- [ ] **Step 5: Rename, remove-field and discard**

In `Entity.razor`, the rename becomes:

```razor
    @if (_renaming)
    {
        <AlvoEditor TestId="rename-sheet" Title="@($"Rename {EntityName}")"
                    Subtitle="The apply moves the table. Its rows come with it." SubmitText="Rename in the working copy"
                    SubmitTestId="rename-save" Dirty="@(!string.Equals(_newName, EntityName, StringComparison.Ordinal))"
                    OnSubmit="RenameAsync" OnClose="CloseRename">
            @if (_renameRefusal is { Length: > 0 })
            {
                <ErrorPanel TakeFocus="true" Title="That name cannot be used" Fix="@_renameRefusal" />
            }
            <MudTextField T="string" Value="_newName" ValueChanged="name => _newName = name ?? string.Empty"
                          Immediate="true" Variant="Variant.Outlined" Label="New name" id="rename-entity-name"
                          autocomplete="off" Class="a-mono"
                          HelperText="@($"{DescriptorNames.Hint} The change is carried by renamedFrom, so the apply moves the table instead of dropping it.")" />
        </AlvoEditor>
    }
```

The remove-field sheet becomes:

```razor
    <AlvoConfirm Open="_removal is not null" TestId="remove-field-sheet" ConfirmTestId="remove-field-anyway"
                 CancelTestId="remove-field-cancel" Title="@($"Remove {_removal?.Field}?")"
                 Consequence="It leaves the working copy now; the column and its data are dropped only when you apply."
                 Verb="Remove field" Allowed="@(_removal?.References.Any(reference => reference.Blocks) != true)"
                 OnConfirm="RemoveAnyway" OnCancel="CloseRemoval">
        @foreach (var reference in _removal?.References ?? [])
        {
            @* (the ListRow data-testid="field-reference" rows, unchanged) *@
        }
        @if (_removal?.References.Any(reference => reference.Blocks) == true)
        {
            <Refusal data-testid="remove-field-blocked">Change or remove the places that name it first.</Refusal>
        }
    </AlvoConfirm>
```

Find the place where a field with *no* references is removed today (`Entity.References.cs`: the path that calls `Copy.RemoveField` without opening `_removal`). Route it through `_removal` too: every removal opens the confirm, with an empty references list (spec §3.2; inventory §2c.3).

`DiscardSheet.razor` becomes `DiscardConfirm.razor`. Same parameters (`Open`, `OnClose`, `OnDiscarded`), and the discard logic it holds is unchanged:

```razor
<AlvoConfirm Open="Open" TestId="discard-sheet" ConfirmTestId="discard-confirm" CancelTestId="discard-cancel"
             Title="Discard every unapplied change?"
             Consequence="@($"The working copy goes back to revision {Revision}. There is no undo.")"
             Verb="Discard changes" OnConfirm="DiscardAsync" OnCancel="OnClose" />
```

- Keep whatever `Revision` source the old sheet used.
- `DiscardAsync` calls the existing discard, then `Snackbar.Confirm("Discarded the working copy")`, then `OnDiscarded`.
- Rename the two usages.

- [ ] **Step 6: `PageHeader`'s phone overflow as a menu**

In `PageHeader.razor`, replace the `<Sheet Open="_overflow" … TestId="pagehead-overflow-sheet">` and the button that opens it with this. Keep `a-only-mobile` on the activator so the desktop keeps the inline row:

```razor
<MudMenu Class="a-only-mobile" AnchorOrigin="Origin.BottomRight" TransformOrigin="Origin.TopRight" Dense="true">
    <ActivatorContent>
        <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" aria-label="More actions" data-testid="pagehead-overflow">
            <Icon Path="@Icons.More" />
        </AlvoButton>
    </ActivatorContent>
    <ChildContent>
        <div class="a-pagehead__menu" role="none">@Secondary</div>
    </ChildContent>
</MudMenu>
```

The `Secondary` links are rendered inside a `role=menu` list. Give each link `role="menuitem"` in `PageHeader` by wrapping it: `<div role="menuitem">`. If Playwright's `GetByRole(AriaRole.Link)` still finds the `<a>`, the port below uses that. Port `PhoneAndKeyboardScenarios.Secondary_actions_fold_into_the_overflow_menu_on_a_phone`:

```csharp
        await session.Page.GetByTestId("pagehead-overflow").ClickAsync();
        await session.Page.GetByRole(AriaRole.Link, new() { Name = "Browse records" }).Last.WaitForAsync();
```

Page-scoped, because the menu is a popover under `<body>`.

- [ ] **Step 7: Port, run, pass**

Port the scenarios listed under **Files**. The rename refusal assertion becomes `await session.Dialog("rename-sheet").GetByTestId("error-panel").WaitForAsync();`. Lower the `EndToEndSelectorTests` constants to the reported counts.

Run: `scripts/test-admin-e2e`
Expected: PASS, including every `SchemaEditingScenarios` fact (field add/edit/rename/remove) and every `EditorScenarios` fact.

Then run: `dotnet test --project test/MMLib.Alvo.Admin.Tests` (accept the `DiscardSheet` → `DiscardConfirm` and `ErrorPanel.TakeFocus` baseline change), `scripts/test-prototype`, `scripts/test-ring1`.

- [ ] **Step 8: Commit**

```text
feat(admin): one editor and one confirm for every overlay

Field, record and rename editors are AlvoEditor: a right-hand modal side sheet with focus on the
first field, Escape and Cancel asking "Discard your changes?" when there are any (inventory
defect #5), Enter / Ctrl+Enter submitting, and a submit that cannot run twice. Removing a field
and discarding the working copy are AlvoConfirm, and every field removal now asks, not only one
that is referenced. Errors are AlvoAlert with role=alert and take focus on a failed submit;
successes are a snackbar. PublicApi: DiscardSheet renamed DiscardConfirm; ErrorPanel.TakeFocus
added.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---
### Task 4: Data screens — grid, record editor controls, the delete confirm, snackbars

Spec §3.2, §3.3, §3.5; §4 row "Data record". Study §4.2 rows DataList / EntityData. Inventory defect #9 (a record delete was one click) and success pattern §2d.2 (a status line with Dismiss and no timeout).

**Deviation V6** applies (`MudSimpleTable`, not `MudDataGrid`). **D7** keeps the `RefPicker` combobox.

**Files:**
- Modify: `Components/Data/DataList.razor`: the entity list in a `MudSimpleTable Dense Hover`. The tenant warning is an `AlvoAlert` Warning.
- Modify: `Components/Data/EntityData.razor` + `EntityData.razor.cs`:
  - `Report(string)` becomes `Snackbar.Confirm(status)`, and the `record-status` live region with its Dismiss button is deleted.
  - Add the delete confirm (`_deleting`), which closes the editor first.
  - The search box is a `MudTextField` that keeps `data-testid="grid-search"` and `data-alvo-search`.
  - Its buttons follow the control migration rule.
- Modify: `Components/Data/RecordGrid.razor`:
  - `<table class="a-grid">` becomes `<MudSimpleTable Dense="true" Hover="true" FixedHeader="true" Class="a-grid">` inside a `<div data-testid="record-grid" data-alvo-keyboard=…>`. The `<thead>`/`<tbody>`/`<tr>` markup, `aria-selected`, `tabindex`, `j`/`k` and the phone cards are unchanged.
  - Previous/Next become `AlvoButton`s.
- Modify: `Components/Data/RecordForm.razor` + `RecordForm.razor.cs`:
  - The text, number and date inputs become `MudTextField` (same `id="rf-{name}"`).
  - A boolean becomes a `MudSwitch T="bool"` (label = the column header, `id="rf-{name}"`).
  - An enum stays a `ChipGroup` (re-skinned in Task 5).
  - The ref combobox is kept.
  - Delete no longer deletes: `OnDeleted` goes and `EventCallback<string> OnDeleteRequested` (the record's label) comes in. `Delete()` is removed and `Data.DeleteAsync` moves to `EntityData`.
- Modify: `wwwroot/alvo.css`: delete `.a-status-line` and the old `.a-grid` frame declarations Mud's table now draws (row hover and borders). Keep `.a-grid__row` selection and focus, `.a-row-card*` and `.a-combo*`.
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/RecordEditorScenarios.cs`.
- Modify (port):
  - `DataGridScenarios.cs`: `table.a-grid tbody tr` → `GetByTestId("grid-row")`; `th[title='customer_id']` → `GetByRole(AriaRole.Columnheader, new() { Name = "Customer" })`.
  - `RecordFormScenarios.cs`: the status line → `SnackbarAsync`; `button:has-text('Save')` → `record-save`; drop the `aria-live` assertion, because a snackbar is announced by the library (`role=alert aria-live=polite`, study §4.1).
  - `ChangeTheBackendScenarios.cs` (`SheetButton` → `record-save`).
  - `PhoneAndKeyboardScenarios.cs` (`table.a-grid tbody tr`).
  - `EndToEndSelectorTests.cs`.
  - `PublicApi…verified.txt` (`RecordForm.OnDeleted` → `OnDeleteRequested`).

**Interfaces:**
- Consumes: `AlvoEditor` (as wired in Task 3), `AlvoConfirm`, `ISnackbar.Confirm`, `ErrorPanel.TakeFocus`.
- Produces these test ids:
  - `record-save`, `record-delete` (inside `record-sheet`)
  - `delete-record` (the `AlvoConfirm`), `delete-record-run`, `delete-record-cancel`
  - `grid-row`, `grid-search`, `record-grid` (kept)
- Snackbars:
  - "Created {label}", "Saved {label}" (the existing `Report` strings)
  - "Deleted {label}"

- [ ] **Step 1: Write the failing record scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/RecordEditorScenarios.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A record is created, edited and deleted under the pattern language (spec §3): the editor guards its changes, a
/// save is a snackbar, and a delete cannot happen without its confirm (inventory defect #9).
/// </summary>
/// <remarks>Its own world: it writes and deletes rows whose keys another class also uses.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RecordEditorScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly TenantId _tenant = TenantId.New();

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_record_opens_on_its_first_field_and_a_double_click_creates_one()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/regions");

        await session.Button("New record", exact: true).ClickAsync();
        var editor = session.Dialog("record-sheet");
        await editor.WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#rf-code");

        await session.Page.FillAsync("#rf-code", "NORTH-1");
        await session.Page.FillAsync("#rf-name", "North one");
        await editor.GetByTestId("record-save").DblClickAsync();

        await session.SnackbarAsync("Created");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("grid-row").Filter(new() { HasText = "NORTH-1" }).CountAsync()).ShouldBe(1);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_edited_record_asks_before_Escape_throws_the_edit_away()
    {
        await SeedAsync("WO-0101");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");
        await RowEditAsync(session, "WO-0101");
        await session.Page.FillAsync("#rf-title", "Changed but not saved");

        await session.Page.Keyboard.PressAsync("Escape");

        await session.Dialog("record-sheet").GetByTestId("editor-discard-question").WaitForAsync();
        await session.Dialog("record-sheet").GetByTestId("editor-discard").ClickAsync();
        await session.Dialog("record-sheet").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await Row(session, "WO-0101").InnerTextAsync()).ShouldNotContain("Changed but not saved");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_save_closes_the_editor_and_says_so_in_a_snackbar()
    {
        await SeedAsync("WO-0102");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");
        await RowEditAsync(session, "WO-0102");

        await session.Page.FillAsync("#rf-title", "Service call WO-0102 amended");
        await session.Page.Keyboard.PressAsync("Control+Enter");

        await session.SnackbarAsync("Saved Service call WO-0102 amended");
        await session.Dialog("record-sheet").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_delete_needs_its_confirm_and_Cancel_keeps_the_record()
    {
        await SeedAsync("WO-0103");
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/work_orders");

        await RowEditAsync(session, "WO-0103");
        await session.Dialog("record-sheet").GetByTestId("record-delete").ClickAsync();
        var confirm = session.Dialog("delete-record");
        await confirm.WaitForAsync();
        (await session.Dialog("record-sheet").CountAsync()).ShouldBe(0, "never a dialog over a dialog");
        await confirm.GetByTestId("delete-record-cancel").ClickAsync();
        await Row(session, "WO-0103").WaitForAsync();

        await RowEditAsync(session, "WO-0103");
        await session.Dialog("record-sheet").GetByTestId("record-delete").ClickAsync();
        await session.Dialog("delete-record").GetByTestId("delete-record-run").ClickAsync();

        await session.SnackbarAsync("Deleted");
        await Row(session, "WO-0103").WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    private static ILocator Row(AdminSession session, string reference)
        => session.Page.GetByTestId("grid-row").Filter(new() { HasText = reference });

    private static async Task RowEditAsync(AdminSession session, string reference)
    {
        await Row(session, reference).GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        await session.Dialog("record-sheet").WaitForAsync();
    }

    private async Task SeedAsync(string reference)
    {
        using var scope = world.Services.CreateScope();
        await FieldServiceSeed.GrantTheOperatorAsync(scope.ServiceProvider, _tenant);
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        var region = await FieldServiceSeed.RegionAsync(data, system, $"R-{reference}");
        var customer = await FieldServiceSeed.CustomerAsync(data, system, _tenant, $"Customer of {reference}");
        await FieldServiceSeed.WorkOrderAsync(data, system, _tenant, reference, customer, region);
    }
}
```

`regions` accepts an admin's create, and `work_orders` accepts an admin's update and delete (`examples/field-service/field-service.alvo.json` rules). `rf-code` is the first editable field of `regions`.

Run: `scripts/test-admin-e2e --filter RecordEditorScenarios`
Expected: FAIL, because there is no snackbar, no `record-delete`/`delete-record` and no `record-save`.

- [ ] **Step 2: `EntityData`: snackbars and the delete confirm**

In `EntityData.razor`:
- Add `@inject ISnackbar Snackbar`.
- Delete the `<div role="status" aria-live="polite" data-testid="record-status">…</div>` block.
- Pass `OnDeleteRequested="AskToDelete"` instead of `OnDeleted="Saved"` to `RecordForm`.
- Add after the form:

```razor
<AlvoConfirm Open="_deleting is not null" TestId="delete-record" ConfirmTestId="delete-record-run"
             CancelTestId="delete-record-cancel" Title="@($"Delete {_deleting?.Label}?")"
             Consequence="@($"The record is deleted from {EntityName} for everyone who can read it. There is no undo.")"
             Verb="Delete record" OnConfirm="DeleteAsync" OnCancel="() => _deleting = null" />
```

In `EntityData.razor.cs`:

```csharp
    private PendingDelete? _deleting;

    /// <summary>What the operator asked to delete, held while the confirm is on screen.</summary>
    /// <param name="Id">The record's id.</param>
    /// <param name="Label">What the confirm and the snackbar call it.</param>
    private sealed record PendingDelete(Guid Id, string Label);

    /// <summary>
    /// Closes the editor and asks: the confirm comes after the editor, never over it (spec §3.1).
    /// </summary>
    private void AskToDelete(string label)
    {
        if (_editing is { } id)
        {
            CloseForm();
            _deleting = new PendingDelete(id, label);
        }
    }

    /// <summary>Deletes what the confirm named, says so, and reads the page again.</summary>
    private async Task DeleteAsync()
    {
        var target = _deleting!;
        _deleting = null;
        try
        {
            await Records.DeleteAsync(EntityName, target.Id, CancellationToken.None);
            Snackbar.Confirm($"Deleted {target.Label}");
            await LoadAsync();
        }
        catch (Exception exception)
        {
            _problem = AdminProblem.From(exception, Logger, ProblemSite.RecordWrite);
        }
    }
```

- `Report(string status)` becomes `private void Report(string status) => Snackbar.Confirm(status);`. `RecordFormScope.Report` still calls it, so `RecordForm`'s "Created …" and "Saved …" strings now arrive as snackbars unchanged.
- Delete `_status`.
- The page-level `<ErrorPanel />` that renders `_problem` gets `TakeFocus="true"` when the problem came from a delete. Add `private bool _problemFromWrite;`, set it in the catch above, and pass `TakeFocus="_problemFromWrite"`.
- Confirm the exact `DataGateway` delete signature against `RecordForm.razor.cs:410` (`Data.DeleteAsync(Entity.Name, id, CancellationToken.None)`); use the same call.

- [ ] **Step 3: `RecordForm`: Mud controls, delete request**

- Delete `Delete()` and the `OnDeleted` parameter from `RecordForm.razor.cs`, and add:

```csharp
    /// <summary>
    /// Raised with the record's label when the operator asks to delete it. The page confirms and deletes, after
    /// this editor has closed, so no dialog opens over it.
    /// </summary>
    [Parameter]
    public EventCallback<string> OnDeleteRequested { get; set; }
```

- `record-delete`'s `OnClick` becomes `_ => OnDeleteRequested.InvokeAsync(LabelOf(new AlvoRecord(Values)))`.
- In `RecordForm.razor`, the `Control(column)` switch's default and `FieldType.Text` arms become:

```razor
        FieldType.Text => @<MudTextField T="string" Variant="Variant.Outlined" Lines="4" Immediate="true"
                                         id="@ControlId(column)" Label="@GridColumns.Header(column)"
                                         Required="@column.Required" Value="@_draft.Text(column.Name)"
                                         ValueChanged="text => Set(column.Name, text)" />,
        _ => @<MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="@ControlId(column)"
                           InputType="@MudInputType(column)" Label="@GridColumns.Header(column)"
                           Required="@column.Required" Value="@_draft.Text(column.Name)"
                           ValueChanged="text => Set(column.Name, text)" />,
```

  - `MudInputType` maps the existing `InputType(column)` string (`"number"`, `"date"`, `"datetime-local"`, `"text"`) to `MudBlazor.InputType` (`Number`, `Date`, `DateTimeLocal`, `Text`). Write it as a private static switch next to `InputType`, which stays, because `FormValue` round-trips the text.
  - The label moves into the control, so the `<label class="a-label">` for these kinds is rendered only for an enum (whose group needs `aria-labelledby`) and for a ref (the combobox keeps its native label).
  - The hint `<span class="a-hint" id="@HintId(column)">` stays, and `aria-describedby` goes onto the `MudTextField` as an attribute.
- `Flag(column)` becomes:

```razor
        return @<MudSwitch T="bool" Value="on" ValueChanged="_ => Toggle(column.Name)" Color="Color.Primary"
                           Label="@GridColumns.Header(column)" id="@ControlId(column)" />;
```

- `_problems` (per-field refusals) render under the control as `<span class="a-field__problem" data-testid="field-problem">`, unchanged (spec §3.3, field error).

- [ ] **Step 4: `RecordGrid` and `DataList`**

In `RecordGrid.razor`, wrap the table as shown below. Also:
- Delete `class="a-grid"` from the `<table>` element: `MudSimpleTable` renders the table.
- Move `data-testid="record-grid"` and `data-alvo-keyboard` to the outer `<div>`.
- Keep `@ref="_body"` on the `<tbody>`.
- Apply the control migration rule to Edit, Previous and Next.

```razor
<div data-testid="record-grid" data-alvo-keyboard="@(_listening ? "ready" : null)">
    <MudSimpleTable Dense="true" Hover="true" FixedHeader="true" Elevation="0" Class="a-grid">
        <thead>@* unchanged *@</thead>
        <tbody @ref="_body">@* unchanged rows *@</tbody>
    </MudSimpleTable>
</div>
```

In `DataList.razor`:
- The entity list becomes `<MudSimpleTable Dense="true" Hover="true" Elevation="0">` with one `<tr>` per entity: a name link, a scoped chip, a row count if shown today.
- The tenant warning becomes `<AlvoAlert Tone="AlvoAlert.AlertTone.Warning" Title="…">…</AlvoAlert>`.
- Keep `NotYetPanel`.

- [ ] **Step 5: Port, lower the ratchet, run everything**

Port as listed under **Files**. `RecordFormScenarios`' status assertions become:

```csharp
        await session.Dialog("record-sheet").GetByTestId("record-save").ClickAsync();
        await session.SnackbarAsync("Saved Service call WO-0001");
        await session.Dialog("record-sheet").WaitForAsync(new() { State = WaitForSelectorState.Detached });
```

Run:
1. `scripts/test-admin-e2e`: PASS, including `RecordEditorScenarios`, `RecordFormScenarios`, `DataGridScenarios`, `ChangeTheBackendScenarios` and `PhoneAndKeyboardScenarios.A_data_screen_shows_its_rows_at_this_width`.
2. `dotnet test --project test/MMLib.Alvo.Admin.Tests`, accepting the `RecordForm` baseline change.
3. `scripts/test-prototype`
4. `scripts/test-ring1`

- [ ] **Step 6: Commit**

```text
feat(admin): records are edited, saved and deleted under the pattern language

The record editor's fields are Mud inputs over the same FormValue text round-trip; the ref
combobox stays Alvo's (it has aria-activedescendant, Mud's autocomplete does not). A delete
closes the editor and asks in an AlvoConfirm, since it was one click before (inventory defect #9).
Created / Saved / Deleted are snackbars instead of a live-region line that never went away.
The grid is MudSimpleTable over Alvo's own rows (V6), so j/k, aria-selected and the phone
cards are unchanged. PublicApi: RecordForm.OnDeleted -> OnDeleteRequested.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---
### Task 5: Schema — the entity list with a filter, the new-entity editor, entity tabs, index/hook editors, rules with an explicit save

Spec §3.1 (no save-on-blur, editors for every multi-value create), §3.2, §3.4; §4 row "Schema". Study §4.2 rows SchemaList / Entity. Inventory §2a patterns 3–4, §2b.2, defects #6 (index/hook double submit) and #7 (rules saved on blur), and feature gap #2 (no filter). **The system map is kept (D7), untouched.**

**Files:**
- Create: `Components/Schema/EntityFilter.cs`, `Components/Rules/RuleDrafts.cs`, `test/MMLib.Alvo.Admin.Tests/Schema/EntityFilterTests.cs`, `test/MMLib.Alvo.Admin.Tests/Rules/RuleDraftsTests.cs`.
- Modify: `Components/Schema/SchemaList.razor`:
  - The `_adding` top-strip `Panel` becomes an `AlvoEditor` (TestId `new-entity`).
  - A `MudTextField` filter over the list (test id `entity-filter`).
  - Each list row gets `data-testid="entity-row-{name}"`.
  - The control migration rule for its buttons.
  - The List/Map `ChipGroup` and the map stay.
- Modify: `Components/Schema/Entity.razor` + `Entity.razor.cs`:
  - The hand-rolled `a-tabs` strip becomes `MudTabs`. The `?tab=` URL behaviour through `EntityTabs.FromUri` / `Open(tab)` is unchanged.
  - The tab body switch is extracted into `RenderFragment TabContent(EntityTab tab)`.
  - Snackbars for staged rule, index and hook changes.
- Modify: `Components/Schema/EntityTabs.cs`: delete `Move` (its only caller was the hand-rolled strip; `MudTabs` owns roving now) and its unit tests, if any exist under `test/MMLib.Alvo.Admin.Tests/Schema/`.
- Modify: `Components/Schema/Indexes.razor`:
  - The permanent inline add form moves into an `AlvoEditor` (TestId `index-editor`) opened by an "Add index" button (`index-new`).
  - Remove → `AlvoConfirm` (`remove-index` / `remove-index-run` / `remove-index-cancel`).
  - `Refusal data-testid="index-refusal"` becomes `AlvoAlert Tone=Error TakeFocus TestId="index-refusal"`.
- Modify: `Components/Schema/HooksTab.razor` + `HooksTab.razor.cs`: the same shape.
  - `hook-new` opens `AlvoEditor` TestId `hook-editor`; submit `hook-add` "Add the hook".
  - Remove → `AlvoConfirm` `remove-hook` / `remove-hook-run` / `remove-hook-cancel`.
  - `hook-refusal` becomes an `AlvoAlert`.
- Modify: `Components/Rules/RulesTab.razor`, the editable branch: each operation is a `MudTextField` (`id="rule-{op}"`, `Lines="2"`) over `RuleDrafts`, with an explicit "Save rule" (`rule-save-{op}`), a dirty badge (`rule-dirty-{op}`), Ctrl/Cmd+Enter = save and Escape = revert. **No `@onchange`.**
- Modify: `Components/Schema/FieldEditor.razor`, `Relationships.razor`, `ApiTab.razor`, `Fields.razor`: apply the control migration rule. `FieldEditor`'s inputs keep their ids (`new-field-name`, `new-field-max`, `new-field-precision`, `new-field-scale`, `new-field-values`, `new-field-computed`, `new-field-default`).
- Modify: `wwwroot/alvo.css`:
  - delete `.a-tabs`, `.a-tab*` unless prototype-only;
  - `.a-chip` gets Mud's chip metrics: `min-height: var(--space-8)` and `border-radius: var(--radius-pill)`, with colours unchanged;
  - delete the inline add-form layout rules the editors replaced.
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/SchemaListScenarios.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/RuleEditingScenarios.cs`.
- Modify (port):
  - `SchemaEditingScenarios.cs`: the index and hook flows open `index-new` / `hook-new` first; a remove confirms.
  - `PendingWorkScenarios.cs`: `button.a-tab--active:has-text('Indexes')` → `GetByRole(AriaRole.Tab, new() { Name = "Indexes", Selected = true })`.
  - `FieldDefaultScenarios.cs`: `.a-choice button:has-text('integer')` → `GetByRole(AriaRole.Radio, new() { Name = "integer", Exact = true })`.
  - `ChangeTheBackendScenarios.cs`: rules are saved with Ctrl+Enter, not blur.
  - `OtherCircuitApplyScenarios.cs` and `ChangeTheBackendScenarios.cs`: New entity is now an editor whose submit is still named "Add to the working copy".
  - `SchemaScenarios.cs`: `main.a-content .a-panel` → a test id you add on the panel.
  - `AccessScenarios.cs`: `.a-choice button` → `GetByRole(AriaRole.Button, new() { Name = "dispatcher", Pressed = … })`.
  - `PhoneAndKeyboardScenarios.An_entity_tab_survives_a_reload_and_follows_history`: if it pressed an arrow to *activate* a tab, add `Enter`. `MudTabs` moves focus on an arrow and activates on Enter/Space (study §4.1 [S]).
  - `EndToEndSelectorTests.cs`.

**Interfaces:**
- Consumes: `AlvoEditor`, `AlvoConfirm`, `AlvoAlert`, `ISnackbar.Confirm`, `AdminSession.*`.
- Produces:
  - `internal static class EntityFilter { static IReadOnlyList<string> Apply(IEnumerable<string> names, string? term); }`
  - `internal sealed class RuleDrafts { string Text(string operation, string? declared); void Set(string operation, string text); bool IsDirty(string operation, string? declared); string Take(string operation, string? declared); void Revert(string operation); }`
  - Snackbars:
    - "Added {name} to the working copy" (new entity)
    - "Rule saved to the working copy"
    - "Index added to the working copy"
    - "Hook added to the working copy"
    - "Removed from the working copy" (index or hook)

- [ ] **Step 1: Write the failing unit tests**

`test/MMLib.Alvo.Admin.Tests/Schema/EntityFilterTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The entity list narrows as the operator types (inventory feature gap #2).</summary>
public sealed class EntityFilterTests
{
    private static readonly string[] _names = ["customers", "regions", "work_orders", "work_order_lines"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_term_is_every_entity_in_order(string? term)
        => EntityFilter.Apply(_names, term).ShouldBe(_names);

    [Fact]
    public void A_term_keeps_the_names_that_contain_it_ignoring_case_and_the_ends_of_the_term()
        => EntityFilter.Apply(_names, "  WORK ").ShouldBe(["work_orders", "work_order_lines"]);

    [Fact]
    public void A_term_nothing_contains_is_an_empty_list()
        => EntityFilter.Apply(_names, "invoices").ShouldBeEmpty();
}
```

`test/MMLib.Alvo.Admin.Tests/Rules/RuleDraftsTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Rules;

namespace MMLib.Alvo.Admin.Tests.Rules;

/// <summary>A rule is typed, then saved on purpose: never staged by leaving the box (inventory defect #7).</summary>
public sealed class RuleDraftsTests
{
    private const string Declared = "'admin' in @user.roles";

    [Fact]
    public void Untouched_it_reads_what_the_descriptor_declares_and_is_clean()
    {
        var drafts = new RuleDrafts();

        drafts.Text("list", Declared).ShouldBe(Declared);
        drafts.IsDirty("list", Declared).ShouldBeFalse();
    }

    [Fact]
    public void Typing_makes_it_dirty_and_typing_it_back_makes_it_clean()
    {
        var drafts = new RuleDrafts();

        drafts.Set("list", "true");
        drafts.IsDirty("list", Declared).ShouldBeTrue();

        drafts.Set("list", Declared + "  ");
        drafts.IsDirty("list", Declared).ShouldBeFalse("surrounding space is not a change the apply sees");
    }

    [Fact]
    public void Take_answers_the_trimmed_draft_and_forgets_it()
    {
        var drafts = new RuleDrafts();
        drafts.Set("get", "  true ");

        drafts.Take("get", null).ShouldBe("true");
        drafts.IsDirty("get", "true").ShouldBeFalse();
        drafts.Text("get", "true").ShouldBe("true");
    }

    [Fact]
    public void Revert_puts_back_what_the_descriptor_declares()
    {
        var drafts = new RuleDrafts();
        drafts.Set("delete", "false");

        drafts.Revert("delete");

        drafts.Text("delete", Declared).ShouldBe(Declared);
    }

    [Fact]
    public void An_operation_with_no_rule_reads_empty()
        => new RuleDrafts().Text("create", null).ShouldBe(string.Empty);
}
```

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: a build failure, because `EntityFilter` and `RuleDrafts` do not exist.

- [ ] **Step 2: The two helpers**

`Components/Schema/EntityFilter.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>Narrows the entity list to what the operator typed.</summary>
internal static class EntityFilter
{
    /// <summary>The names containing <paramref name="term"/>, ignoring case and the term's surrounding space, in their order.</summary>
    public static IReadOnlyList<string> Apply(IEnumerable<string> names, string? term)
    {
        ArgumentNullException.ThrowIfNull(names);
        var wanted = term?.Trim() ?? string.Empty;
        return wanted.Length == 0
            ? [.. names]
            : [.. names.Where(name => name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
    }
}
```

`Components/Rules/RuleDrafts.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Rules;

/// <summary>
/// What the operator has typed into each operation's rule and not yet saved.
/// </summary>
/// <remarks>
/// A rule used to be staged on blur, so tabbing away staged a change nobody meant to make (inventory defect #7).
/// A draft is held here until "Save rule" or Ctrl/Cmd+Enter takes it, and Escape reverts it.
/// </remarks>
internal sealed class RuleDrafts
{
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    /// <summary>What the box shows: the draft, or what the descriptor declares.</summary>
    public string Text(string operation, string? declared)
        => _drafts.TryGetValue(operation, out var draft) ? draft : declared ?? string.Empty;

    /// <summary>Holds what was typed.</summary>
    public void Set(string operation, string text) => _drafts[operation] = text;

    /// <summary>Whether saving would change the descriptor; surrounding space is not a change.</summary>
    public bool IsDirty(string operation, string? declared)
        => _drafts.TryGetValue(operation, out var draft)
            && !string.Equals(draft.Trim(), (declared ?? string.Empty).Trim(), StringComparison.Ordinal);

    /// <summary>Answers the trimmed rule to stage, and forgets the draft.</summary>
    public string Take(string operation, string? declared)
    {
        var text = Text(operation, declared).Trim();
        _drafts.Remove(operation);
        return text;
    }

    /// <summary>Drops the draft, so the box reads the descriptor again.</summary>
    public void Revert(string operation) => _drafts.Remove(operation);
}
```

Run the unit tests: PASS.

- [ ] **Step 3: Write the failing scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/SchemaListScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The entity list is filtered, and the entity's collections are added to through editors (spec §3.1, §3.2, §3.4).
/// </summary>
/// <remarks>Its own world: it stages entities and indexes into the operator's one working copy.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class SchemaListScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task New_entity_is_an_editor_that_opens_on_its_name_and_adds_on_Enter()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Button("New entity", exact: true).ClickAsync();
        await session.Dialog("new-entity").WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#new-entity-name");

        await session.Page.Keyboard.TypeAsync("tickets");
        await session.Page.Keyboard.PressAsync("Enter");

        await session.Page.WaitForURLAsync("**/schema/tickets");
        await session.SnackbarAsync("Added tickets to the working copy");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_half_named_entity_is_not_lost_to_an_Escape()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "invo");

        await session.Page.Keyboard.PressAsync("Escape");

        await session.Dialog("new-entity").GetByTestId("editor-discard-question").WaitForAsync();
        await session.Dialog("new-entity").GetByTestId("editor-keep").ClickAsync();
        (await session.Page.InputValueAsync("#new-entity-name")).ShouldBe("invo");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_filter_narrows_the_list_and_says_when_nothing_matches()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Page.GetByLabel("Filter entities").FillAsync("work");

        await session.Page.GetByTestId("entity-row-customers").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("entity-row-work_orders").IsVisibleAsync()).ShouldBeTrue();

        await session.Page.GetByLabel("Filter entities").FillAsync("zzz");
        await session.Content.GetByText("Nothing matches").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_system_map_is_still_one_choice_away()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");

        await session.Page.GetByRole(AriaRole.Radio, new() { Name = "Map", Exact = true }).ClickAsync();

        await session.Page.GetByTestId("system-map").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_index_is_added_through_an_editor_and_a_double_click_adds_one()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("Indexes");
        var before = await session.Page.GetByTestId("index-row").CountAsync();

        await session.Page.GetByTestId("index-new").ClickAsync();
        var editor = session.Dialog("index-editor");
        await editor.GetByTestId("index-fields")
            .GetByRole(AriaRole.Button, new() { Name = "name", Exact = true }).ClickAsync();
        await editor.GetByTestId("index-add").DblClickAsync();

        await session.SnackbarAsync("Index added to the working copy");
        (await session.Page.GetByTestId("index-row").CountAsync()).ShouldBe(before + 1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Removing_a_staged_index_cannot_happen_without_its_confirm()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("Indexes");
        await session.Page.GetByTestId("index-new").ClickAsync();
        await session.Dialog("index-editor").GetByTestId("index-fields")
            .GetByRole(AriaRole.Button, new() { Name = "name", Exact = true }).ClickAsync();
        await session.Dialog("index-editor").GetByTestId("index-add").ClickAsync();
        var rows = await session.Page.GetByTestId("index-row").CountAsync();

        await session.Page.GetByTestId("index-remove").Last.ClickAsync();
        await session.Dialog("remove-index").GetByTestId("remove-index-cancel").ClickAsync();
        (await session.Page.GetByTestId("index-row").CountAsync()).ShouldBe(rows);

        await session.Page.GetByTestId("index-remove").Last.ClickAsync();
        await session.Dialog("remove-index").GetByTestId("remove-index-run").ClickAsync();
        await session.SnackbarAsync("Removed from the working copy");
    }
}
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/RuleEditingScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A rule is staged by Save or Ctrl/Cmd+Enter, never by leaving the box (spec §3.1, §3.4; defect #7).</summary>
/// <param name="world">The running host and browser.</param>
public sealed class RuleEditingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Leaving_a_changed_rule_stages_nothing_and_Ctrl_Enter_saves_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");
        await session.OpenTabAsync("Rules");

        await session.Page.FillAsync("#rule-list", "'admin' in @user.roles");
        await session.Page.GetByTestId("rule-dirty-list").WaitForAsync();
        await session.Page.Locator("#rule-get").FocusAsync();
        await session.SettleAsync();
        (await session.Page.GetByTestId("pending-bar").CountAsync()).ShouldBe(0, "leaving the box saves nothing");

        await session.Page.Locator("#rule-list").FocusAsync();
        await session.Page.Keyboard.PressAsync("Control+Enter");

        await session.SnackbarAsync("Rule saved to the working copy");
        await session.Page.GetByTestId("rule-dirty-list").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.GetByTestId("pending-bar").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Enter_is_a_newline_and_Escape_puts_back_what_the_descriptor_says()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.OpenTabAsync("Rules");
        var declared = await session.Page.InputValueAsync("#rule-get");

        await session.Page.Locator("#rule-get").FocusAsync();
        await session.Page.Keyboard.PressAsync("End");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.Keyboard.TypeAsync("|| false");
        (await session.Page.InputValueAsync("#rule-get")).ShouldContain("\n");
        await session.Page.GetByTestId("rule-dirty-get").WaitForAsync();

        await session.Page.Keyboard.PressAsync("Escape");
        await session.Page.WaitForFunctionAsync(
            "([id, value]) => document.getElementById(id)?.value === value", new[] { "rule-get", declared });
    }
}
```

Run: `scripts/test-admin-e2e --filter "SchemaListScenarios|RuleEditingScenarios"`
Expected: FAIL, because there is no `new-entity` dialog, no filter, no `index-new`, and the rule saves on blur.

- [ ] **Step 4: SchemaList**

Replace the `@if (_adding) { <Panel … data-testid="new-entity" …> … </Panel> }` block with:

```razor
    @if (_adding)
    {
        <AlvoEditor TestId="new-entity" Title="New entity" SubmitText="Add to the working copy"
                    Subtitle="It arrives with one required name field and opens on its Fields tab. Nothing reaches the database until you preview and apply."
                    Dirty="@(_name.Trim().Length > 0 || _scoped || _audited)" OnSubmit="AddEntity" OnClose="CancelAdding">
            @if (_refusal is { Length: > 0 })
            {
                <ErrorPanel TakeFocus="true" Title="That entity cannot be added" Fix="@_refusal" />
            }
            <MudTextField T="string" Value="_name" ValueChanged="name => _name = name ?? string.Empty" Immediate="true"
                          Variant="Variant.Outlined" Label="Name" id="new-entity-name" autocomplete="off"
                          HelperText="@DescriptorNames.Hint" />
            <Field Label="Constraints">
                <ChildContent>
                    <MudCheckBox T="bool" Value="_scoped" ValueChanged="on => _scoped = on" Label="tenant-scoped" />
                    <MudCheckBox T="bool" Value="_audited" ValueChanged="on => _audited = on" Label="audited" />
                </ChildContent>
                <Hint>
                    Scoped means every row belongs to a tenant and a caller with none is refused with 403.
                    Audited adds the four stamp columns and a version, which is what makes a conditional write possible.
                </Hint>
            </Field>
        </AlvoEditor>
    }
```

- `AddEntity` calls `Snackbar.Confirm($"Added {name} to the working copy")` before it navigates. Add `@inject ISnackbar Snackbar`.
- Delete `_nameInput` and its `FocusOnRender`: `DefaultFocus.FirstChild` lands on the name box.

Above the list branch (not the map), add the filter, and iterate `EntityFilter.Apply(_schema.Entities.Select(entity => entity.Name), _filter)` instead of `_schema.Entities`, looking each entity up by name:

```razor
        <MudTextField T="string" Value="_filter" ValueChanged="term => _filter = term ?? string.Empty" Immediate="true"
                      Variant="Variant.Outlined" Label="Filter entities" Clearable="true" data-testid="entity-filter"
                      Class="a-filter" />
```

When the filtered list is empty and `_filter` is not, render `<EmptyState Title="@($"Nothing matches “{_filter.Trim()}”")" Body="The filter looks at entity names." />`.

- [ ] **Step 5: Entity tabs on `MudTabs`, with the tab body extracted**

Replace the `<div class="a-tabs" role="tablist" …> … </div>` strip and the `<Panel role="tabpanel" …> @switch … </Panel>` with:

```razor
        <MudTabs ActivePanelIndex="@EntityTabs.All.ToList().IndexOf(_tab)" ActivePanelIndexChanged="OpenAt"
                 Elevation="0" Ripple="false" KeepPanelsAlive="false" Class="a-entitytabs"
                 aria-label="@($"{EntityName} sections")">
            @foreach (var tab in EntityTabs.All)
            {
                <MudTabPanel Text="@tab.Title" ID="@tab.Slug">
                    @if (tab == _tab)
                    {
                        <Panel>@TabContent(tab)</Panel>
                    }
                </MudTabPanel>
            }
        </MudTabs>
```

In `Entity.razor.cs`:

```csharp
    /// <summary>A tab chosen in the strip; the URL follows, exactly as a click on the old strip made it.</summary>
    private void OpenAt(int index) => Open(EntityTabs.All[index]);
```

- `TabContent(EntityTab tab)` is the existing `@switch (_tab.Slug) { … }` moved into a `RenderFragment` method in the `@code` block, unchanged except that it switches on `tab.Slug`.
- Delete `Move(string key)` and the `onkeydown` it served.

- [ ] **Step 6: Indexes and hooks through editors; remove through a confirm**

In `Indexes.razor`:
- Replace the permanently rendered add form with an "Add index" button that opens an editor over the same form.
- Keep `_chosen`, `_unique`, `_refusal` and `AddAsync` as they are.
- The `index-unique` checkbox becomes `MudCheckBox T="bool" … data-testid="index-unique" Label="Unique"`.

```razor
    <AlvoButton Small="true" data-testid="index-new" OnClick="_ => _adding = true">Add index</AlvoButton>

    @if (_adding)
    {
        <AlvoEditor TestId="index-editor" Title="New index" SubmitText="Add the index" SubmitTestId="index-add"
                    Dirty="@(_chosen.Count > 0 || _unique)" OnSubmit="AddAsync" OnClose="CloseAdding">
            @if (_refusal is { Length: > 0 })
            {
                <AlvoAlert Tone="AlvoAlert.AlertTone.Error" TakeFocus="true" TestId="index-refusal">@_refusal</AlvoAlert>
            }
            @* (the index-fields ChipGroups and the unique box, unchanged apart from the checkbox) *@
        </AlvoEditor>
    }

    <AlvoConfirm Open="_removing is not null" TestId="remove-index" ConfirmTestId="remove-index-run"
                 CancelTestId="remove-index-cancel" Title="Remove this index?"
                 Consequence="It leaves the working copy now; the database drops it when you apply."
                 Verb="Remove index" OnConfirm="RemoveAsync" OnCancel="() => _removing = null" />
```

- `AddAsync`: on success, `_adding = false`, then the parent's `OnAdd` handler in `Entity.razor.cs` calls `Snackbar.Confirm("Index added to the working copy")`.
- `index-remove` sets `_removing = at` instead of invoking `OnRemove`.
- `RemoveAsync` invokes `OnRemove.InvokeAsync(_removing!)`, clears `_removing`, and the parent confirms "Removed from the working copy".
- `CloseAdding` clears `_chosen`, `_unique` and `_refusal`, and sets `_adding = false`.

`HooksTab.razor` + `.razor.cs` get the same shape with `hook-new`, `hook-editor`, `hook-add`, `hook-refusal` (an `AlvoAlert`) and `remove-hook*`. The "When" and "Then" `ChipGroup`s keep `data-testid="hook-points"` / `"hook-actions"`. `Dirty` is `!_hook.Equals(new HookBuilder())`; if `HookBuilder` has no value equality, use `_hook.Point is not null || _hook.Kind is not null`, whichever its public shape makes true.

- [ ] **Step 7: Rules: explicit save**

Replace the editable branch's `@foreach` body in `RulesTab.razor`:

```razor
    @foreach (var operation in _operations)
    {
        var declared = Declared(operation);
        <ListRow class="a-listrow--roomy a-listrow--stacked">
            <div class="a-row">
                <span class="@($"a-badge {(declared is { Length: > 0 } ? "a-badge--accent" : string.Empty)}")">@operation</span>
                <code class="a-ident">@Route(operation)</code>
                @if (_drafts.IsDirty(operation, declared))
                {
                    <span class="a-badge a-badge--warn" data-testid="@($"rule-dirty-{operation}")">unsaved</span>
                }
            </div>
            <MudTextField T="string" Variant="Variant.Outlined" Lines="2" Immediate="true" id="@($"rule-{operation}")"
                          Class="a-mono" spellcheck="false" Label="@($"{operation} rule")"
                          Placeholder="No rule — refused for everyone. Try: 'dispatcher' in @@user.roles"
                          Value="@_drafts.Text(operation, declared)" ValueChanged="text => _drafts.Set(operation, text ?? string.Empty)"
                          OnKeyDown="args => KeyAsync(operation, args)" />
            <div class="a-row">
                <AlvoButton Tone="AlvoButton.ButtonTone.Primary" Small="true" data-testid="@($"rule-save-{operation}")"
                            Disabled="@(!_drafts.IsDirty(operation, declared))" OnClick="_ => SaveAsync(operation)">Save rule</AlvoButton>
            </div>
        </ListRow>
    }
```

In `@code`:

```csharp
    private readonly RuleDrafts _drafts = new();

    private async Task KeyAsync(string operation, KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && (args.CtrlKey || args.MetaKey))
        {
            await SaveAsync(operation);
        }
        else if (args.Key == "Escape")
        {
            _drafts.Revert(operation);
        }
    }

    /// <summary>Stages what was typed, only when it differs from what the descriptor says.</summary>
    private Task SaveAsync(string operation)
    {
        var declared = Declared(operation);
        return _drafts.IsDirty(operation, declared)
            ? OnChanged.InvokeAsync((operation, _drafts.Take(operation, declared)))
            : Task.CompletedTask;
    }
```

- Delete the `Write` method and the `@onchange`.
- `Entity.razor.cs`'s `OnChanged` handler stages the rule as before and then calls `Snackbar.Confirm("Rule saved to the working copy")`.
- Update the `RulesTab` comment block: the rules are no longer staged when the box loses focus.

- [ ] **Step 8: FieldEditor's inputs; CSS; ports**

Apply the control migration rule to every native input in `FieldEditor.razor`, keeping the ids listed above. The `ChipGroup`s and their `Field` wrappers stay. Numeric boxes use `InputType.Number` and keep the existing `Number`/`OptionalNumber` parsing: the value stays a `string` into those helpers.

Update `alvo.css` as listed, and do the ports listed under **Files**. `ChangeTheBackendScenarios`' rules loop becomes:

```csharp
        foreach (var operation in new[] { "list", "get", "create", "update", "delete" })
        {
            await session.Page.FillAsync($"#rule-{operation}", "'admin' in @user.roles");
            await session.Page.Locator($"#rule-{operation}").PressAsync("Control+Enter");
            await session.Page.GetByTestId($"rule-dirty-{operation}")
                .WaitForAsync(new() { State = WaitForSelectorState.Detached });
        }
```

Lower the ratchet.

- [ ] **Step 9: Run everything**

Run:
1. `scripts/test-admin-e2e`: PASS, every Schema*, FieldDefault/Facet/Reference, MaintainedField, PendingWork, ChangeTheBackend, OtherCircuitApply and SystemMap* scenario included.
2. `dotnet test --project test/MMLib.Alvo.Admin.Tests`, accepting the baseline: `RulesTab` gains nothing public, and `Entity`, `Indexes` and `HooksTab` private members do not show.
3. `scripts/test-prototype`
4. `scripts/test-ring1`

- [ ] **Step 10: Commit**

```text
feat(admin): schema editing through editors, confirms and an explicit rule save

New entity, index and hook are AlvoEditor instead of a strip above the list and two forms left
open under the tabs (inventory §2a.3-4). Their submit cannot run twice (defect #6), and removing
an index or a hook asks first. A rule is staged by Save or Ctrl/Cmd+Enter and marked unsaved
until then, never on blur (defect #7). The entity list has a filter (gap #2). Entity tabs are
MudTabs over the same ?tab= URL. The system map is unchanged. ChipGroup stays Alvo's own
ARIA radio/toggle group (V7).

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---
### Task 6: Preview and Transfer — the destructive apply behind a typed-name confirm, busy apply, snackbar + persistent result

Spec §3.2 (a destructive apply is an `AlvoConfirm` with the type-the-name step), §3.3 (the snackbar plus the persistent success panel, which stays because it links onward), §3.4 (busy, Ctrl/Cmd+Enter on the import box). Study §4.2 rows Preview / Transfer.

**Files:**
- Modify: `Components/Schema/Preview.razor`:
  - The inline `ConfirmByName` becomes a persistent `AlvoAlert` Warning "This plan destroys data" (state), plus an `AlvoConfirm` with `TypeToConfirm="@Gateway.Project"`, opened by Apply when the plan is destructive.
  - Apply is an `AlvoButton` Primary with `Busy="_busy"` and no longer disabled for "not confirmed": the confirm is the gate now.
  - The reason is a `MudTextField` with `id="apply-reason"`.
  - Replan and Discard follow the control migration rule.
  - A successful apply keeps the accent panel and adds `Snackbar.Confirm($"Applied as revision {result.Revision}")`.
  - A refused apply renders the page `ErrorPanel` with `TakeFocus="true"`.
- Modify: `Components/Schema/Transfer.razor`:
  - The import textarea becomes `MudTextField Lines="12" id="import-json"`, with `OnKeyDown` so Ctrl/Cmd+Enter imports.
  - Import and Download become `AlvoButton`s (Import `Busy` while it runs).
  - The refusal `ErrorPanel` takes `TakeFocus="true"`.
- Modify: `wwwroot/alvo.css`: delete or mark the `.a-confirm*` rules that no markup names once `ConfirmByName` has no caller. History still uses it until Task 8, so the rules probably stay until then; the hygiene test decides.
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/ApplyScenarios.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/TransferScenarios.cs`.
- Modify (port):
  - `DestructivePlanScenarios.cs`: the Apply button is enabled now, and the guard is the confirm. Also `main.a-content` → `session.Content`.
  - `ChangeTheBackendScenarios.cs`, `OtherCircuitApplyScenarios.cs`, `AssistantScenarios.cs`: unchanged text. "Apply these changes" and "Applied as revision" still exist; verify only.
  - `EndToEndSelectorTests.cs`.

**Interfaces:**
- Consumes: `AlvoConfirm` (`TypeToConfirm`), `AlvoAlert`, `AlvoButton`, `ISnackbar.Confirm`.
- Produces:
  - `apply-confirm` / `apply-confirm-run` / `apply-confirm-cancel` (the destructive apply's confirm)
  - `plan-destroys` (the warning alert)
  - `import-json` (kept), `import-run` (the Import button)

- [ ] **Step 1: Write the failing scenarios**

Add to `DestructivePlanScenarios.cs` (same class, same world; this fact applies nothing):

```csharp
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_destructive_apply_cannot_run_until_the_project_name_is_typed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("remove-field-code").ClickAsync();
        await session.Dialog("remove-field-sheet").GetByTestId("remove-field-anyway").ClickAsync();
        await session.PreviewPendingAsync();

        (await session.Page.GetByTestId("plan-destroys").GetAttributeAsync("role")).ShouldBe("alert");
        await session.Button("Apply these changes").ClickAsync();

        var confirm = session.Dialog("apply-confirm");
        await confirm.WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#confirm-name");
        (await confirm.GetByTestId("apply-confirm-run").IsDisabledAsync()).ShouldBeTrue();

        await session.Page.Keyboard.TypeAsync("field-servic");
        (await confirm.GetByTestId("apply-confirm-run").IsDisabledAsync()).ShouldBeTrue("almost the name is not the name");
        await session.Page.Keyboard.TypeAsync("e");
        await session.Page.WaitForFunctionAsync(
            "() => !document.querySelector(\"[data-testid='apply-confirm-run']\")?.disabled");

        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByText("Applied as revision").CountAsync()).ShouldBe(0);
    }
```

`field-service` is the project name (`examples/field-service/field-service.alvo.json` `"name"`), which `Gateway.Project` answers.

In the existing fact, replace the final assertion `(await session.Page.Locator("button:has-text('Apply these changes')").IsDisabledAsync()).ShouldBeTrue();` with:

```csharp
        (await session.Page.GetByTestId("plan-destroys").IsVisibleAsync()).ShouldBeTrue();
        await session.Button("Apply these changes").ClickAsync();
        await session.Dialog("apply-confirm").WaitForAsync();
        await session.Dialog("apply-confirm").GetByTestId("apply-confirm-cancel").ClickAsync();
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/ApplyScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>An apply cannot run twice, and says what it did twice: briefly, and where it stays (spec §3.3, §3.4).</summary>
/// <remarks>Its own world, because it applies; one apply per world (see ChangeTheBackendScenarios).</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class ApplyScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_apply_appends_one_revision_and_says_so()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "tickets");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForURLAsync("**/schema/tickets");
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", "Add tickets");

        await session.Button("Apply these changes").DblClickAsync();

        await session.SnackbarAsync("Applied as revision");
        await session.Content.GetByText("Applied as revision").WaitForAsync();
        await session.GoAsync("/history");
        (await session.Page.GetByTestId("revision-row").CountAsync()).ShouldBe(1);
    }
}
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/TransferScenarios.cs`:

```csharp
namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The import box submits on Ctrl/Cmd+Enter, and a refusal is an alert that takes focus (spec §3.3, §3.4).</summary>
/// <param name="world">The running host and browser.</param>
public sealed class TransferScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_import_is_an_alert_with_focus_and_Enter_alone_is_a_newline()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");
        await session.Page.Locator("#import-json").FocusAsync();

        await session.Page.Keyboard.TypeAsync("{ \"not\": ");
        await session.Page.Keyboard.PressAsync("Enter");
        (await session.Page.InputValueAsync("#import-json")).ShouldContain("\n");
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "Enter alone does not import");

        await session.Page.Keyboard.PressAsync("Control+Enter");

        await session.Page.GetByTestId("error-panel").WaitForAsync();
        (await session.FocusIsInsideAsync("error-panel")).ShouldBeTrue();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_good_import_goes_to_preview_on_Meta_Enter()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/transfer");

        await session.Page.FillAsync("#import-json", Descriptors.ComplexCrm);
        await session.Page.Locator("#import-json").PressAsync("Meta+Enter");

        await session.Page.WaitForURLAsync("**/changes");
        await session.WaitForPlanAsync();
    }
}
```

Run: `scripts/test-admin-e2e --filter "DestructivePlanScenarios|ApplyScenarios|TransferScenarios"`
Expected: FAIL, because there is no `plan-destroys` or `apply-confirm`, the textarea has no Ctrl+Enter, and the apply button is disabled rather than confirming.

- [ ] **Step 2: Preview**

Replace the `@if (_plan.Plan.HasDestructiveChanges) { <ConfirmByName …/> }` block with:

```razor
            @if (_plan.Plan.HasDestructiveChanges)
            {
                <AlvoAlert Tone="AlvoAlert.AlertTone.Warning" Title="This plan destroys data" TestId="plan-destroys">
                    Permission to lose data is never implied: not by this preview, and not by your management level.
                    Applying asks you to type the project's name.
                </AlvoAlert>
            }
```

Replace the reason `Panel`'s `Field`/`input` with the control migration rule: `MudTextField … Label="Why" id="apply-reason" HelperText="Configuration history shows this on the revision forever." Placeholder="@ReasonPlaceholder"`.

Replace the Apply button with this, and add the confirm after the toolbar:

```razor
                <AlvoButton Tone="AlvoButton.ButtonTone.Primary" Busy="_busy" OnClick="_ => RequestApplyAsync()">
                    Apply these changes
                </AlvoButton>
```

```razor
    <AlvoConfirm Open="_confirming" TestId="apply-confirm" ConfirmTestId="apply-confirm-run" CancelTestId="apply-confirm-cancel"
                 Title="Apply a plan that destroys data?"
                 Consequence="The steps marked as destructive drop data that no rollback brings back."
                 Verb="Apply and destroy data" TypeToConfirm="@Gateway.Project" Busy="_busy"
                 OnConfirm="ApplyConfirmedAsync" OnCancel="() => _confirming = false">
        <PlanSteps Steps="@(_plan?.Plan.Steps ?? [])" />
    </AlvoConfirm>
```

In `@code`:

```csharp
    private bool _confirming;

    /// <summary>A destructive plan asks for the project's name first; any other applies at once.</summary>
    private Task RequestApplyAsync()
    {
        if (_plan?.Plan.HasDestructiveChanges == true)
        {
            _confirming = true;
            return Task.CompletedTask;
        }

        return ApplyAsync();
    }

    /// <summary>The name was typed, which is the permission to lose data the apply carries.</summary>
    private async Task ApplyConfirmedAsync()
    {
        _allowed = true;
        _confirming = false;
        await ApplyAsync();
    }
```

- `ApplyAsync`/`SendAsync` stay as they are. `SendAsync` already passes `dryRun || _allowed` and guards `_busy`.
- Add a `SubmitGate` check at the top of `SendAsync`, `if (_busy) return;`, if it does not guard already. The double-click scenario pins it.
- After the success branch sets `_applied = result`, add `Snackbar.Confirm($"Applied as revision {result.Revision}")`, and inject `ISnackbar`.
- On failure, the existing `_problem` panel renders with `TakeFocus="true"`.
- `DiscardSheet` was renamed to `DiscardConfirm` in Task 3, so nothing more is needed there.

- [ ] **Step 3: Transfer**

- The import `<textarea …>` becomes `<MudTextField T="string" Variant="Variant.Outlined" Lines="12" Immediate="true" id="import-json" Class="a-mono" spellcheck="false" Label="Descriptor JSON" Value="_pasted" ValueChanged="text => _pasted = text ?? string.Empty" OnKeyDown="ImportKeyAsync" />`.
- The Import button becomes `<AlvoButton Tone="AlvoButton.ButtonTone.Primary" Busy="_importing" data-testid="import-run" OnClick="_ => Import()">Import</AlvoButton>`, keeping its existing enabled condition as `Disabled`.
- Add:

```csharp
    private bool _importing;

    /// <summary>Ctrl/Cmd+Enter imports; Enter alone is a newline in a JSON document (spec §3.4).</summary>
    private Task ImportKeyAsync(KeyboardEventArgs args)
        => args.Key == "Enter" && (args.CtrlKey || args.MetaKey) ? Import() : Task.CompletedTask;
```

- If `Import` is `void` today, make it `Task`, wrapped in `_importing = true; try { … } finally { _importing = false; }`.
- The refusal `ErrorPanel` gets `TakeFocus="true"`.
- Download becomes `AlvoButton` Secondary.

- [ ] **Step 4: Run everything and commit**

Run `scripts/test-admin-e2e` (PASS, including every apply-bearing class), `dotnet test --project test/MMLib.Alvo.Admin.Tests`, `scripts/test-prototype` and `scripts/test-ring1`.

Commit:

```text
feat(admin): a destructive apply asks for the project's name in a confirm

Preview states a destructive plan as a warning alert and applies it only through an
AlvoConfirm whose button waits for the project name, typed exactly. Every apply is busy while
it runs, so a double click appends one revision, and is reported as a snackbar beside the
persistent result that links to history. The import box submits on Ctrl/Cmd+Enter and a
refused import takes focus.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---

### Task 7: Access — the person editor, the token in the dialog with Copy, the disable confirm, add person

Spec §3.1–§3.4; §4 row "Access". Study §4.2 row Access. Inventory §2a.2 (the inline expanding row), §2b.3, and defects #6 (Create double submit), #8 (Disable with no confirm) and #10 (the token at the top of the page with no dismiss).

**Files:**
- Create: `Components/Access/PersonDraft.cs`, `Components/Access/PersonEditor.razor`, `test/MMLib.Alvo.Admin.Tests/Access/PersonDraftTests.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/PersonEditorScenarios.cs`.
- Modify: `Components/Access/PersonRow.razor`: the row only. It shows who, the roles and tenant summary, and a "Change" button (`id="change-{id}"`) raising `OnOpen`. The `Expanded` body and every per-change callback leave it.
- Modify: `Components/Access/Access.razor`:
  - It owns `_editing` (a person) and renders `PersonEditor` when set.
  - The top token panel is deleted.
  - "Add a person" becomes a button (`person-new`) that opens an `AlvoEditor` (TestId `person-create`, submit `person-create-run` "Create person").
  - The Disable `AlvoConfirm` (`disable-person` / `disable-person-run` / `disable-person-cancel`).
  - Snackbars.
- Modify: `wwwroot/admin.js` (`copyText`) and `Internal/AdminInterop.cs` (`CopyAsync`), plus a test in `AdminInteropTests.cs`.
- Modify: `wwwroot/alvo.css`: delete `.a-listrow__under` and the other expanded-row rules, unless prototype-only.
- Modify (port):
  - `AccessScenarios.cs`: the role toggle now goes open editor → the role's pressed button → Save.
  - `EndToEndSelectorTests.cs`.
  - `PublicApi…verified.txt`: `PersonRow` loses its parameters (`Expanded`, `OnToggle`, `OnSetRoles`, `OnSetTenant`, `OnClearTenant`, `OnIssueToken`, `OnSetDisabled`) and gains `OnOpen`; add `PersonEditor`.

**Interfaces:**
- Consumes: `AlvoEditor`, `AlvoConfirm`, `AlvoAlert`, `AlvoButton`, `ChipGroup` (roles, several-of), `ISnackbar.Confirm`, `ManagementGateway`'s existing people methods (the ones `Access.razor` calls today: `SetRolesAsync`, `SetTenantAsync`, `ClearTenantAsync`, `IssueTokenAsync`, `SetDisabledAsync`, `CreateAsync`, whatever their exact names are in `Access.razor`'s `@code`; reuse them unchanged).
- Produces:
  - `internal sealed class PersonDraft`:
    - `PersonDraft(IReadOnlyCollection<string> roles, string? tenant)`
    - `IReadOnlyList<string> Roles { get; }`, `string Tenant { get; }`
    - `void SetRoles(IReadOnlyList<string> roles)`, `void SetTenant(string text)`
    - `bool RolesChanged`, `bool TenantChanged`, `bool Dirty`
  - `PersonEditor` (component):
    - `ManagementUser Person` (the type `PersonRow.Person` has today), `IReadOnlyList<string> Roles`, `bool IsSelf`
    - `ManagementCredentialToken? Token` (the type `_token` has today)
    - `EventCallback<(IReadOnlyList<string>? Roles, string? Tenant, bool ClearTenant)> OnSave`
    - `EventCallback OnIssueToken`, `EventCallback OnRequestDisable`, `EventCallback OnLetBackIn`, `EventCallback OnClose`
    - `EventCallback OnCopyToken`
    - `bool Busy`
    - `RenderFragment? Problem` (the refusal panel `Access` builds; `AdminProblem` is internal and cannot be a parameter)
  - Test ids:
    - `person-editor`, `person-save`, `person-token`, `person-token-copy`, `person-issue-token`, `person-disable`, `person-let-in`
    - `person-new`, `person-create`, `person-create-run`
    - `disable-person`, `disable-person-run`, `disable-person-cancel`
  - Snackbars:
    - "Saved {email}"
    - "Created {email}"
    - "Disabled {email}"
    - "{email} can sign in again"
    - "Token copied"

- [ ] **Step 1: Failing unit tests for the draft, and the interop copy**

`test/MMLib.Alvo.Admin.Tests/Access/PersonDraftTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Access;

namespace MMLib.Alvo.Admin.Tests.Access;

/// <summary>What a person's editor changes, and whether closing it would lose anything.</summary>
public sealed class PersonDraftTests
{
    [Fact]
    public void An_untouched_draft_is_clean()
    {
        var draft = new PersonDraft(["dispatcher"], "7b2e…");

        draft.Dirty.ShouldBeFalse();
        draft.RolesChanged.ShouldBeFalse();
        draft.TenantChanged.ShouldBeFalse();
    }

    [Fact]
    public void The_same_roles_in_another_order_are_not_a_change()
    {
        var draft = new PersonDraft(["dispatcher", "technician"], null);

        draft.SetRoles(["technician", "dispatcher"]);

        draft.RolesChanged.ShouldBeFalse();
    }

    [Fact]
    public void A_role_toggled_is_a_change_and_so_is_a_tenant_typed()
    {
        var draft = new PersonDraft(["dispatcher"], null);

        draft.SetRoles(["dispatcher", "technician"]);
        draft.SetTenant("  3f0c2b8e-0000-0000-0000-000000000001 ");

        draft.RolesChanged.ShouldBeTrue();
        draft.TenantChanged.ShouldBeTrue();
        draft.Tenant.ShouldBe("3f0c2b8e-0000-0000-0000-000000000001");
        draft.Dirty.ShouldBeTrue();
    }

    [Fact]
    public void Clearing_the_tenant_box_of_a_person_who_has_none_is_not_a_change()
    {
        var draft = new PersonDraft([], null);

        draft.SetTenant("   ");

        draft.TenantChanged.ShouldBeFalse();
    }
}
```

Add to `AdminInteropTests.cs`:

```csharp
    [Fact]
    public async Task A_copy_hands_the_text_to_the_script()
    {
        var interop = new AdminInterop(_js, _logger);

        await interop.CopyAsync("tok_123");

        Arguments("copyText").ShouldHaveSingleItem()[0].ShouldBe("tok_123");
    }
```

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: a build failure, because `PersonDraft` and `CopyAsync` do not exist.

- [ ] **Step 2: `PersonDraft` and `CopyAsync`**

`Components/Access/PersonDraft.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Access;

/// <summary>The roles and the tenant being edited for one person, against what the store says they are.</summary>
internal sealed class PersonDraft(IReadOnlyCollection<string> roles, string? tenant)
{
    private readonly HashSet<string> _roles = new(roles, StringComparer.Ordinal);
    private readonly string _tenant = tenant?.Trim() ?? string.Empty;

    /// <summary>The roles as the editor has them.</summary>
    public IReadOnlyList<string> Roles { get; private set; } = [.. roles];

    /// <summary>The tenant box's text, trimmed.</summary>
    public string Tenant { get; private set; } = tenant?.Trim() ?? string.Empty;

    /// <summary>Whether the role set differs, order aside.</summary>
    public bool RolesChanged => !_roles.SetEquals(Roles);

    /// <summary>Whether the tenant differs, surrounding space aside.</summary>
    public bool TenantChanged => !string.Equals(_tenant, Tenant, StringComparison.Ordinal);

    /// <summary>Whether closing would lose anything.</summary>
    public bool Dirty => RolesChanged || TenantChanged;

    /// <summary>The roles the chips now say.</summary>
    public void SetRoles(IReadOnlyList<string> roles) => Roles = roles;

    /// <summary>What the tenant box now says.</summary>
    public void SetTenant(string text) => Tenant = text.Trim();
}
```

In `admin.js`:

```js
/**
 * Copies text to the clipboard: the credential token, which is shown once and has to leave this page intact.
 * A snackbar is never its only copy (spec §3.3); this is the operator's second one.
 */
export async function copyText(text) {
  await navigator.clipboard.writeText(text);
}
```

In `AdminInterop.cs`:

```csharp
    /// <summary>Copies <paramref name="text"/> to the clipboard; see <c>copyText</c> in admin.js.</summary>
    public Task CopyAsync(string text) => QuietlyAsync(module => module.InvokeVoidAsync("copyText", text));
```

Run the unit tests: PASS.

- [ ] **Step 3: Write the failing scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/PersonEditorScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A person is created, changed and disabled under the pattern language: an editor, a token kept in the dialog with
/// a Copy button, a confirm before access is revoked (inventory defects #6, #8, #10).
/// </summary>
/// <remarks>Its own world: it creates people whose addresses another class also uses.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class PersonEditorScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_create_creates_one_person()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        await session.Page.GetByTestId("person-new").ClickAsync();
        await session.Dialog("person-create").WaitForAsync();
        (await session.FocusedAsync()).ShouldStartWith("input#new-person-email");
        await session.Page.Keyboard.TypeAsync("twice@example.com");
        await session.Dialog("person-create").GetByTestId("person-create-run").DblClickAsync();

        await session.SnackbarAsync("Created twice@example.com");
        (await session.Content.GetByText("twice@example.com").CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_token_stays_in_the_editor_until_it_is_closed_and_Copy_puts_it_on_the_clipboard()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var person = await CreateAsync(session, "token@example.com");

        await OpenAsync(session, person);
        await session.Dialog("person-editor").GetByTestId("person-issue-token").ClickAsync();
        var token = session.Dialog("person-editor").GetByTestId("person-token");
        await token.WaitForAsync();
        await session.Dialog("person-editor").GetByTestId("person-token-copy").ClickAsync();

        await session.SnackbarAsync("Token copied");
        var copied = await session.Page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        (await token.InnerTextAsync()).ShouldContain(copied);
        (await session.Content.GetByText("Credential token for").CountAsync()).ShouldBe(0, "no panel at the top of the page");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Disabling_a_person_closes_the_editor_and_needs_a_confirm()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var person = await CreateAsync(session, "leaver@example.com");

        await OpenAsync(session, person);
        await session.Dialog("person-editor").GetByTestId("person-disable").ClickAsync();
        var confirm = session.Dialog("disable-person");
        await confirm.WaitForAsync();
        (await session.Dialog("person-editor").CountAsync()).ShouldBe(0, "never a dialog over a dialog");

        await confirm.GetByTestId("disable-person-cancel").ClickAsync();
        (await Row(session, person).InnerTextAsync()).ShouldNotContain("disabled");

        await OpenAsync(session, person);
        await session.Dialog("person-editor").GetByTestId("person-disable").ClickAsync();
        await session.Dialog("disable-person").GetByTestId("disable-person-run").ClickAsync();
        await session.SnackbarAsync("Disabled leaver@example.com");
        await Row(session, person).Filter(new() { HasText = "disabled" }).WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Roles_change_on_save_and_an_unsaved_change_is_guarded()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var person = await CreateAsync(session, "roles@example.com");

        await OpenAsync(session, person);
        await session.Dialog("person-editor").GetByRole(AriaRole.Button, new() { Name = "dispatcher", Exact = true }).ClickAsync();
        await session.Page.Keyboard.PressAsync("Escape");
        await session.Dialog("person-editor").GetByTestId("editor-discard-question").WaitForAsync();
        await session.Dialog("person-editor").GetByTestId("editor-keep").ClickAsync();

        await session.Dialog("person-editor").GetByTestId("person-save").ClickAsync();

        await session.SnackbarAsync("Saved roles@example.com");
        await Row(session, person).Filter(new() { HasText = "dispatcher" }).WaitForAsync();
    }

    private static ILocator Row(AdminSession session, string id) => session.Page.Locator($"#person-{id}");

    private static async Task OpenAsync(AdminSession session, string id)
    {
        await session.Page.Locator($"#change-{id}").ClickAsync();
        await session.Dialog("person-editor").WaitForAsync();
    }

    /// <summary>Creates a person through the editor and answers the id their row carries.</summary>
    private static async Task<string> CreateAsync(AdminSession session, string email)
    {
        await session.GoAsync("/access");
        await session.Page.GetByTestId("person-new").ClickAsync();
        await session.Page.FillAsync("#new-person-email", email);
        await session.Page.Keyboard.PressAsync("Enter");
        await session.SnackbarAsync($"Created {email}");
        var row = session.Content.Locator("[id^='person-']").Filter(new() { HasText = email });
        return (await row.GetAttributeAsync("id"))!["person-".Length..];
    }
}
```

The field-service descriptor declares a `dispatcher` role, which `AccessScenarios` already uses. Headless Chromium grants the clipboard through `GrantPermissionsAsync`.

Run: `scripts/test-admin-e2e --filter PersonEditorScenarios`
Expected: FAIL, because there is no `person-new`, no `person-editor`, no confirm and no in-dialog token.

- [ ] **Step 4: `PersonEditor`, the slimmed `PersonRow`, and `Access`**

`Components/Access/PersonEditor.razor`: an `AlvoEditor` over a `PersonDraft`.
- The roles use `ChipGroup Multiple` (label "Roles", with the no-roles hint from today's row).
- The tenant is either the self sentence (`tenant-self`) or a `MudTextField id="tenant-{id}"` plus a "Remove tenant" `AlvoButton` Ghost, which sets `ClearTenant`.
- The token block is shown when `Token` is set: an `AlvoAlert Tone=Info Title="Credential token"`, carrying `<pre class="a-code" data-testid="person-token">` and a Copy `AlvoButton` (`person-token-copy`). Its sentence stays: "Hand this over out of band: this build sends no email. It works once, until {expiry}."
- `ExtraActions` hold "Issue a credential token" (`person-issue-token`) and either "Disable" (`person-disable`, Danger) or "Let them back in" (`person-let-in`).

```razor
<AlvoEditor TestId="person-editor" Title="@($"Edit {Person.Email}")" SubmitText="Save" SubmitTestId="person-save"
            Dirty="_draft.Dirty" Busy="Busy" CanSubmit="_draft.Dirty" OnSubmit="SaveAsync" OnClose="OnClose">
    <ExtraActions>
        <AlvoButton Small="true" data-testid="person-issue-token" OnClick="_ => OnIssueToken.InvokeAsync()">Issue a credential token</AlvoButton>
        @if (Person.IsDisabled)
        {
            <AlvoButton Small="true" data-testid="person-let-in" OnClick="_ => OnLetBackIn.InvokeAsync()">Let them back in</AlvoButton>
        }
        else
        {
            <AlvoButton Tone="AlvoButton.ButtonTone.Danger" Small="true" data-testid="person-disable"
                        OnClick="_ => OnRequestDisable.InvokeAsync()">Disable</AlvoButton>
        }
    </ExtraActions>
    <ChildContent>
        @* roles, tenant and token blocks as described above *@
    </ChildContent>
</AlvoEditor>

@code {
    private PersonDraft _draft = new([], null);
    private object? _opened;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_opened, Person))
        {
            _opened = Person;
            _draft = new PersonDraft(Person.RoleNames, Person.Tenant?.Value.ToString());
        }
    }

    private Task SaveAsync() => OnSave.InvokeAsync((
        _draft.RolesChanged ? _draft.Roles : null,
        _draft.TenantChanged && _draft.Tenant.Length > 0 ? _draft.Tenant : null,
        _draft.TenantChanged && _draft.Tenant.Length == 0));
}
```

Write each parameter with its XML doc. The roles hint text and the self-tenant sentence move verbatim from `PersonRow.razor`, comments included.

`PersonRow.razor` keeps the `ListRow` with `id="person-{id}"`, the name, the meta line, and:

```razor
        <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" id="@($"change-{Person.Id.Value}")"
                    OnClick="_ => OnOpen.InvokeAsync()">Change</AlvoButton>
```

Its parameters become `Person`, `OnOpen`. Delete the rest.

In `Access.razor`:
1. Delete the `@if (_token is { } token) { <Panel …> … }` block.
2. Replace the `a-panel__foot` add row with `<AlvoButton Small="true" data-testid="person-new" OnClick="_ => _creating = true">Add a person</AlvoButton>`, keeping its explanatory sentence as the editor's `Subtitle`.
3. Render:

```razor
@if (_creating)
{
    <AlvoEditor TestId="person-create" Title="New person" SubmitText="Create person" SubmitTestId="person-create-run"
                Subtitle="No password field, deliberately: a credential that travels as a value is readable by whoever handles it. The account is created without one and you hand over a single-use token."
                Dirty="@(_newEmail.Trim().Length > 0)" CanSubmit="@(_newEmail.Trim().Length > 0)"
                OnSubmit="CreateAsync" OnClose="() => { _creating = false; _newEmail = string.Empty; }">
        <MudTextField T="string" Value="_newEmail" ValueChanged="email => _newEmail = email ?? string.Empty"
                      Immediate="true" Variant="Variant.Outlined" Label="Email" InputType="InputType.Email"
                      id="new-person-email" Placeholder="dispatcher@example.com" />
    </AlvoEditor>
}

@if (_editing is { } person)
{
    <PersonEditor Person="person" Roles="_roles" IsSelf="person.Id == _self" Token="@(_tokenFor == person.Email ? _token : null)"
                  Busy="_busy" OnSave="change => SaveAsync(person, change)" OnIssueToken="() => IssueTokenAsync(person)"
                  OnRequestDisable="() => AskToDisable(person)" OnLetBackIn="() => SetDisabledAsync(person, false)"
                  OnClose="CloseEditor" />
}

<AlvoConfirm Open="_disabling is not null" TestId="disable-person" ConfirmTestId="disable-person-run"
             CancelTestId="disable-person-cancel" Title="@($"Disable {_disabling?.Email}?")"
             Consequence="They can no longer sign in, and every session they hold stops at its next request. You can let them back in later."
             Verb="Disable person" OnConfirm="DisableConfirmedAsync" OnCancel="() => _disabling = null" />
```

In `@code`:
- Add `_creating`, `_editing` (the person type) and `_disabling`.
- `SaveAsync(person, change)` calls the existing roles and tenant methods for the parts that changed, then `Snackbar.Confirm($"Saved {person.Email}")`, then closes the editor and reloads.
- `AskToDisable` sets `_editing = null` first and `_disabling = person` second, so the confirm opens after the editor closes.
- `DisableConfirmedAsync` calls the existing `SetDisabledAsync(person, true)` and `Snackbar.Confirm($"Disabled {person.Email}")`.
- Letting someone back in is not destructive, so it has no confirm and gets `Snackbar.Confirm($"{person.Email} can sign in again")`.
- `CreateAsync` wraps the existing create: on success `Snackbar.Confirm($"Created {email}")`, `_creating = false`, reload.
- The token's Copy button in `PersonEditor` raises `OnCopyToken`. `Access` handles it with `Interop.CopyAsync(_token!.Token)` and `Snackbar.Confirm("Token copied")`, and passes it as `OnCopyToken="CopyTokenAsync"`. Inject `AdminInterop` and `ISnackbar`.
- A refusal from any of these sets the page `_problem`, and the page `ErrorPanel` renders it with `TakeFocus="true"` after the editor closes. A refusal while the editor is open (roles, tenant) renders inside the editor: `<CascadingValue Value="_problem"><ErrorPanel TakeFocus="true" /></CascadingValue>` as the editor's first child, which means `Access` passes the problem into `PersonEditor` as a `RenderFragment? Problem` parameter. `AdminProblem` is internal and cannot be a public parameter; see the ErrorPanel remarks.

- [ ] **Step 5: Port `AccessScenarios`, run everything, commit**

- In `AccessScenarios.cs`, the three `#person-{x} .a-choice button:has-text('dispatcher')` locators become: open `#change-{x}`, then `session.Dialog("person-editor").GetByRole(AriaRole.Button, new() { Name = "dispatcher", Exact = true })`, and the pressed-state check is `new() { Name = "dispatcher", Exact = true, Pressed = true }`.
- Each toggle is followed by `person-save`.
- `main.a-content` becomes `session.Content`.
- Lower the ratchet.

Run `scripts/test-admin-e2e`, `dotnet test --project test/MMLib.Alvo.Admin.Tests` (accept the `PersonRow`/`PersonEditor` baseline), `scripts/test-prototype` and `scripts/test-ring1`.

Commit:

```text
feat(admin): people are edited in an editor, and revoking access asks first

A person's roles and tenant are edited in an AlvoEditor with Save, replacing the row that
expanded in place (inventory §2a.2). The credential token stays in that editor with a Copy
button, not at the top of the page (defect #10). Disable closes the editor and asks in an
AlvoConfirm (defect #8). Add a person is an editor whose Create cannot run twice (defect #6).
PublicApi: PersonRow narrows to Person + OnOpen; PersonEditor added.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---
### Task 8: History and Rules — the split screens, the diff against the previous revision by default, rollback behind a typed-name confirm

Spec §3.1 (content read beside the main content is a non-modal pane), §3.2 (a rollback is irreversible and wide-blast-radius, so it is typed-name), §4 row "History". Study §4.2 rows History / Rules. Inventory defect #4 and feature gap #1: History showed the whole JSON and never the change. **V5 applies:** `a-split` + `SplitHandle` stay, and the pane contents move onto Mud.

**Files:**
- Modify: `Components/History/RevisionHistory.cs`: add one static method, `Previous`. Nothing else changes (D6).
- Create: `test/MMLib.Alvo.Admin.Tests/History/RevisionHistoryPreviousTests.cs`.
- Modify: `Components/Schema/DescriptorDiff.razor`: one new optional parameter, `string? Identical`, which is the sentence shown when there is no difference. The default is today's working-copy sentence.
- Modify: `Components/History/History.razor`:
  - The selected revision's pane is a `MudTabs` with "Changes from r{n−1}" (the default; `DescriptorDiff` of the previous revision's descriptor against this one's) and "Descriptor" (`CodeBlock`).
  - The first revision shows only "Descriptor" and says why.
  - The rollback plan stays inline; "Roll back to r{n}" opens an `AlvoConfirm` with `TypeToConfirm="@Gateway.Project"` and the plan steps as its content. `ConfirmByName` leaves this page.
  - A snackbar reports the rollback.
  - Buttons follow the control migration rule.
- Modify: `Components/Rules/Rules.razor`:
  - The hand-rolled `a-tabs` entity strip becomes `MudTabs` (≤ 6 entities). The `<select>` becomes `MudSelect T="string" Label="Entity" data-testid="rules-entity"` (> 6).
  - Buttons and the simulation's "Try again" follow the control migration rule.
  - The split and `SplitHandle` stay.
- Modify: `wwwroot/alvo.css`: `.a-tabs`/`.a-tab*` and `.a-select` are deleted if no markup names them any more (unless prototype-only); `.a-confirm*` goes with the last `ConfirmByName` caller.
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/HistoryScenarios.cs`.
- Modify (port): `PolicyScenarios.cs` (`main.a-content` → `session.Content`), `ShellFrameScenarios.cs` (verify only: the separator is unchanged), `EndToEndSelectorTests.cs`, `PublicApi…verified.txt` (`DescriptorDiff.Identical`).

**Interfaces:**
- Consumes: `AlvoConfirm` (`TypeToConfirm`), `ISnackbar.Confirm`, `RollbackGate` (unchanged), `DescriptorDiff`, `CodeBlock`.
- Produces:
  - `internal static int? RevisionHistory.Previous(IReadOnlyList<ManagementRevision> revisions, int revision)`
  - `DescriptorDiff.Identical`
  - Test ids:
    - `revision-diff` (the diff pane), `revision-descriptor` (the JSON pane), `revision-first` (the first-revision sentence)
    - `rollback-plan` (kept), `rollback-run` (kept; it now opens the confirm)
    - `rollback-confirm`, `rollback-confirm-run`, `rollback-confirm-cancel`
  - Snackbar: "Rolled back to r{n} as revision {m}".

- [ ] **Step 1: Failing unit test**

`test/MMLib.Alvo.Admin.Tests/History/RevisionHistoryPreviousTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.History;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.History;

/// <summary>Which revision a revision's change is shown against (inventory defect #4).</summary>
public sealed class RevisionHistoryPreviousTests
{
    private static readonly DateTimeOffset _at = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly ManagementRevision[] _newestFirst =
    [
        new(5, _at, "a@x", null, null),
        new(4, _at, "a@x", null, 2),
        new(2, _at, "a@x", null, null),
        new(1, _at, null, null, null),
    ];

    [Theory]
    [InlineData(5, 4)]
    [InlineData(4, 2)]
    [InlineData(2, 1)]
    public void It_is_the_nearest_earlier_revision_listed_whatever_the_gap(int revision, int previous)
        => RevisionHistory.Previous(_newestFirst, revision).ShouldBe(previous);

    [Fact]
    public void The_first_revision_has_none()
        => RevisionHistory.Previous(_newestFirst, 1).ShouldBeNull();

    [Fact]
    public void The_order_of_the_list_does_not_matter()
        => RevisionHistory.Previous([.. _newestFirst.Reverse()], 5).ShouldBe(4);
}
```

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: a build failure, because `Previous` does not exist.

- [ ] **Step 2: `Previous`**

In `RevisionHistory.cs`:

```csharp
    /// <summary>
    /// The revision applied just before <paramref name="revision"/>: the one its change is read against. A rollback
    /// is a revision like any other, so this is the nearest earlier number listed, never the one it restored.
    /// </summary>
    /// <returns>That revision's number, or <see langword="null"/> for the first.</returns>
    public static int? Previous(IReadOnlyList<ManagementRevision> revisions, int revision)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        return revisions.Where(entry => entry.Revision < revision).Select(entry => (int?)entry.Revision).Max();
    }
```

Run the unit tests: PASS.

- [ ] **Step 3: Write the failing History scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/HistoryScenarios.cs`:

```csharp
using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A revision opens on what it changed (inventory defect #4), and a rollback cannot run without the project's name
/// typed (spec §3.2).
/// </summary>
/// <remarks>
/// Its own world, because it applies twice: a diff needs a revision before the one it shows. The world boots a
/// mounted descriptor, which is not a revision, so the first apply is r1 and has nothing before it.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class HistoryScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_revision_opens_on_its_change_against_the_one_before()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var first = await ApplyNewEntityAsync(session, "tickets");
        var second = await ApplyNewEntityAsync(session, "invoices");

        await session.GoAsync("/history");
        await session.Page.GetByTestId("revision-row").Filter(new() { HasText = $"r{second}" }).ClickAsync();

        var selected = session.Page.GetByRole(AriaRole.Tab, new() { Selected = true });
        (await selected.InnerTextAsync()).Trim().ShouldBe($"Changes from r{first}");
        var diff = session.Page.GetByTestId("revision-diff");
        (await diff.InnerTextAsync()).ShouldContain("+ ");
        (await diff.InnerTextAsync()).ShouldContain("invoices");

        await session.OpenTabAsync("Descriptor");
        await session.Page.GetByTestId("revision-descriptor").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_first_revision_says_there_is_nothing_before_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/history");

        await session.Page.GetByTestId("revision-row").Last.ClickAsync();

        await session.Page.GetByTestId("revision-first").WaitForAsync();
        await session.Page.GetByTestId("revision-descriptor").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rollback_waits_for_the_project_name_and_Escape_runs_nothing()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/history");
        var rows = await session.Page.GetByTestId("revision-row").CountAsync();
        await session.Page.GetByTestId("revision-row").Last.ClickAsync();

        await session.Page.GetByTestId("rollback-plan").ClickAsync();
        await session.Page.GetByTestId("rollback-run").ClickAsync();
        var confirm = session.Dialog("rollback-confirm");
        await confirm.WaitForAsync();
        (await confirm.GetByTestId("rollback-confirm-run").IsDisabledAsync()).ShouldBeTrue();

        await session.Page.Keyboard.PressAsync("Escape");
        await confirm.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId("revision-row").CountAsync()).ShouldBe(rows);
    }

    private static async Task<string> ApplyNewEntityAsync(AdminSession session, string name)
    {
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", name);
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForURLAsync($"**/schema/{name}");
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", $"Add {name}");
        await session.Button("Apply these changes").ClickAsync();
        var announced = session.Content.GetByText("Applied as revision").First;
        await announced.WaitForAsync();
        return Regex.Match(await announced.InnerTextAsync(), @"revision (\d+)").Groups[1].Value;
    }
}
```

xUnit runs a class's facts in declaration order by default. The second and third facts read the revisions the first one applied, and they are written to hold only in that order: the second opens `.Last`, which is r1. The third plans a rollback to r1 and never runs it.

`ChangeTheBackendScenarios`' remarks record that "a second apply did not finish under the in-process host". If the second `ApplyNewEntityAsync` here times out, do not raise the timeout. Seed r1 without the browser instead: in the first fact, before any UI step, call `IAlvoManagement.ApplyDescriptorAsync("field-service", new ManagementApplyRequest(<the field-service descriptor with a `tickets` entity added>, ExpectedRevision: 0, Author: AdminWorld.AdminEmail, Reason: "Add tickets"))` from `world.Services`, inside a scope with the system context, the way `FieldServiceSeed` writes rows. Then apply only `invoices` through the UI, and record the fallback in the commit body.

Run: `scripts/test-admin-e2e --filter HistoryScenarios`
Expected: FAIL. There is no tab named "Changes from r…", no `revision-diff`, and `rollback-run` rolls back instead of confirming.

- [ ] **Step 4: History**

In `@code`:
- Add `private ManagementRevisionDetail? _previous;`.
- In `OpenAsync(int revision)`, after `_selected` is loaded:

```csharp
            _previous = RevisionHistory.Previous(_revisions!, revision) is { } before
                ? await Gateway.RevisionAsync(before, CancellationToken.None)
                : null;
```

Keep the method at or under 25 lines: extract `LoadPreviousAsync(int revision)` if it grows.

In the aside, replace `<CodeBlock Json="@_selected.DescriptorJson" />` with:

```razor
                    @if (_previous is { } before)
                    {
                        <MudTabs Elevation="0" Ripple="false" KeepPanelsAlive="false" Class="a-revtabs">
                            <MudTabPanel Text="@($"Changes from r{before.Version.Revision}")">
                                <div data-testid="revision-diff">
                                    <DescriptorDiff Before="@before.DescriptorJson" After="@_selected.DescriptorJson"
                                                    Revision="before.Version.Revision"
                                                    Identical="@($"The descriptor is the same as r{before.Version.Revision}'s.")" />
                                </div>
                            </MudTabPanel>
                            <MudTabPanel Text="Descriptor">
                                <div data-testid="revision-descriptor"><CodeBlock Json="@_selected.DescriptorJson" /></div>
                            </MudTabPanel>
                        </MudTabs>
                    }
                    else
                    {
                        <p class="a-section__sub" data-testid="revision-first">
                            The first revision applied through this API: there is nothing before it to compare with.
                        </p>
                        <div data-testid="revision-descriptor"><CodeBlock Json="@_selected.DescriptorJson" /></div>
                    }
```

Replace the rollback block's `@if (_rollback.HasDestructiveChanges) { <ConfirmByName …/> }` and its run button with:

```razor
                                    <AlvoButton Tone="AlvoButton.ButtonTone.Danger" data-testid="rollback-run" Busy="_busy"
                                                OnClick="_ => _confirmingRollback = true">
                                        Roll back to r@(_selected.Version.Revision)
                                    </AlvoButton>
```

and, at the page's end:

```razor
<AlvoConfirm Open="_confirmingRollback" TestId="rollback-confirm" ConfirmTestId="rollback-confirm-run"
             CancelTestId="rollback-confirm-cancel" Title="@($"Roll back to r{_selected?.Version.Revision}?")"
             Consequence="The reverse migration is appended as a new revision. It can discard data added since, and nothing restores that."
             Verb="Roll back" TypeToConfirm="@Gateway.Project" Busy="_busy"
             OnConfirm="RollbackConfirmedAsync" OnCancel="() => _confirmingRollback = false">
    @if (_rollback is not null)
    {
        <PlanSteps Steps="_rollback.Steps" />
    }
</AlvoConfirm>
```

```csharp
    private bool _confirmingRollback;

    /// <summary>The name was typed: that is the permission a destructive reverse migration needs.</summary>
    private async Task RollbackConfirmedAsync()
    {
        _allowed = true;
        _confirmingRollback = false;
        var target = _selected!.Version.Revision;
        await RollbackAsync();
        if (_problem is null)
        {
            Snackbar.Confirm($"Rolled back to r{target} as revision {_current}");
        }
    }
```

- `RollbackAsync` already reloads `_current` through `LoadAsync`, so read `_current` after it.
- Keep `RollbackGate.AllowDestructive(_rollback, _allowed)` as it is.
- Reset `_previous` wherever `_selected` is reset.
- Inject `ISnackbar`.
- The page `ErrorPanel` gets `TakeFocus="@(_problem?.Site == ProblemSite.Rollback)"`, or the equivalent through the classifier's own property, if `AdminProblem` exposes its site (check `Internal/AdminProblem.cs`). Otherwise add `private bool _rollbackFailed;`, set it in `SendRollbackAsync`'s catch, and pass `TakeFocus="_rollbackFailed"`.

In `DescriptorDiff.razor`, add the parameter and use it in the empty branch:

```csharp
    /// <summary>What to say when there is no difference; the working-copy sentence by default.</summary>
    [Parameter]
    public string? Identical { get; set; }
```

```razor
    <div class="a-section__sub a-panel__body">
        @(Identical ?? $"The working copy is identical to revision {Revision}.")
    </div>
```

- [ ] **Step 5: Rules**

Replace the entity strip's `<div class="a-tabs"> … </div>` with this, keeping `Choose(string)` as it is:

```razor
            <MudTabs ActivePanelIndex="@_schema.Entities.ToList().FindIndex(entity => entity.Name == Selected)"
                     ActivePanelIndexChanged="index => Choose(_schema.Entities[index].Name)"
                     Elevation="0" Ripple="false" KeepPanelsAlive="false" aria-label="Entity">
                @foreach (var entity in _schema.Entities)
                {
                    <MudTabPanel Text="@entity.Name" />
                }
            </MudTabs>
```

Replace the `<select>` with `<MudSelect T="string" Value="Selected" ValueChanged="Choose" Label="Entity" Variant="Variant.Outlined" data-testid="rules-entity" Class="a-mono">` holding one `<MudSelectItem Value="@entity.Name">@entity.Name</MudSelectItem>` per entity. `_schema.Entities` is an `IReadOnlyList`; if it is only enumerable, index through a local `[.. _schema.Entities]`.

- [ ] **Step 6: Ports, run everything, commit**

Port as listed under **Files** and lower the ratchet. Then run:
1. `scripts/test-admin-e2e`: PASS, including `HistoryScenarios`, `PolicyScenarios` and `ShellFrameScenarios.The_reading_pane_is_resized_remembered_and_reset`.
2. `dotnet test --project test/MMLib.Alvo.Admin.Tests`, accepting `DescriptorDiff.Identical`.
3. `scripts/test-prototype`
4. `scripts/test-ring1`

Commit:

```text
feat(admin): a revision opens on what it changed, and a rollback asks for the name

History's pane opens on "Changes from r(n-1)": DescriptorDiff of the previous revision's
descriptor against this one's, with the full descriptor a tab away. It showed only the whole
JSON before (inventory defect #4, gap #1). A rollback is always an AlvoConfirm with the
project name typed. The Rules entity picker is MudTabs or a MudSelect. Both screens keep
Alvo's resizable split (V5). PublicApi: DescriptorDiff.Identical added.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---

### Task 9: The assistant pane — full height from the app bar, Ctrl/Cmd+Enter, follows the newest turn, cleared on send

Spec §4 row "Assistant" (every item in it is a requirement), §3.1 (non-modal pane), §3.4, §3.5. Study §4.2 row AssistantDrawer. Inventory defects #1, #2 and #3.

**Files:**
- Modify: `Components/Shell/AdminLayout.razor`:
  - The assistant is mounted in an end `MudDrawer` (persistent above the breakpoint, temporary below it), a sibling of `MudMainContent`.
  - Its launcher is an `AlvoButton` in the app bar (`assistant-launch`, `aria-expanded`).
  - The layout owns `_assistantOpen`.
- Modify: `Components/Assistant/AssistantDrawer.razor`:
  - It loses its launcher and its own `_open`, and renders the pane: head, thread, proposal, ask.
  - The thread gets `aria-live="polite"`, `@ref`, `data-testid="assistant-thread"`, and `data-testid="assistant-turn"` per turn.
  - The textarea becomes `MudTextField` (`id="assistant-message"`, `Lines="3"`, `Immediate="true"`, `Value`/`ValueChanged`, `OnKeyDown`).
  - Send is an `AlvoButton` with `Busy="_busy"`.
  - After each render it calls `Interop.FollowNewestAsync(_thread)`.
- Modify: `wwwroot/admin.js` (`followNewest`), `Internal/AdminInterop.cs` (`FollowNewestAsync`), `AdminInteropTests.cs`.
- Modify: `wwwroot/alvo.css`: `.a-assistant` becomes a full-height flex column, `.a-assistant__thread` takes `flex: 1; overflow: auto`, and the floating `.a-assistant__launch` rules are deleted.
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AssistantKeyboardScenarios.cs`.
- Modify (port): `AssistantScenarios.cs`. The launcher is in the app bar and `#assistant-message` is kept. `The_drawer_fits_a_phone_and_clears_the_bottom_bar` keeps its assertion: the app bar is above the bottom bar by construction. Also `EndToEndSelectorTests.cs`.

**Interfaces:**
- Consumes: `AlvoButton`, `AssistantGateway` (unchanged), `ScriptedAssistant` / `AssistantWorld` (test).
- Produces: `AdminInterop.FollowNewestAsync(ElementReference thread)`, backed by admin.js `followNewest(element)`, which scrolls to the bottom unless the operator has scrolled up. Test ids `assistant-launch`, `assistant-drawer`, `assistant-thread`, `assistant-turn`, `assistant-send` (kept or new as listed).

- [ ] **Step 1: Failing interop test and assistant scenarios**

Add to `AdminInteropTests.cs`:

```csharp
    [Fact]
    public async Task Following_the_newest_turn_is_one_call_with_the_thread()
    {
        var interop = new AdminInterop(_js, _logger);

        await interop.FollowNewestAsync(default);

        Arguments("followNewest").ShouldHaveSingleItem();
    }
```

`test/MMLib.Alvo.Admin.Tests.EndToEnd/AssistantKeyboardScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The assistant is typed to the way every chat box is: Ctrl/Cmd+Enter sends, Enter is a newline, the box empties,
/// and the thread follows the newest turn (inventory defects #1–#3; spec §4 "Assistant").
/// </summary>
/// <remarks>Its own world: a working copy is per operator, and the scripted assistant answers every question.</remarks>
/// <param name="world">A host with the scripted assistant.</param>
public sealed class AssistantKeyboardScenarios(AssistantWorld world) : IClassFixture<AssistantWorld>
{
    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("Control+Enter")]
    [InlineData("Meta+Enter")]
    public async Task The_chord_sends_the_question_and_empties_the_box(string chord)
    {
        await using var session = await OpenAsync();

        await session.Page.Locator("#assistant-message").FillAsync("add an invoices entity");
        await session.Page.Locator("#assistant-message").PressAsync(chord);

        await Turns(session).First.WaitForAsync();
        (await Turns(session).First.InnerTextAsync()).ShouldContain("add an invoices entity");
        await session.Page.WaitForFunctionAsync("() => document.getElementById('assistant-message')?.value === ''");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Enter_alone_is_a_newline_and_sends_nothing()
    {
        await using var session = await OpenAsync();
        await session.Page.Locator("#assistant-message").FocusAsync();

        await session.Page.Keyboard.TypeAsync("line one");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.Keyboard.TypeAsync("line two");

        (await session.Page.InputValueAsync("#assistant-message")).ShouldBe("line one\nline two");
        (await Turns(session).CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_thread_follows_the_newest_turn_unless_the_operator_scrolled_up()
    {
        await using var session = await OpenAsync(height: 560);
        for (var turn = 0; turn < 6; turn++)
        {
            await AskAsync(session, $"question {turn}: add an invoices entity");
        }

        (await AtBottomAsync(session)).ShouldBeTrue("the newest turn is in view");

        await session.Page.GetByTestId("assistant-thread").EvaluateAsync("e => { e.scrollTop = 0; }");
        await AskAsync(session, "one more question");
        (await session.Page.GetByTestId("assistant-thread").EvaluateAsync<double>("e => e.scrollTop")).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_pane_is_a_labelled_non_modal_region_as_tall_as_the_window_and_announces_turns()
    {
        await using var session = await OpenAsync();
        var pane = session.Page.GetByTestId("assistant-drawer");

        (await pane.EvaluateAsync<string>("e => e.closest('aside')?.getAttribute('aria-label') ?? ''")).ShouldBe("Assistant");
        (await session.Page.Locator("[aria-modal='true']").CountAsync()).ShouldBe(0, "the pane is beside the page, not over it");
        (await session.Page.GetByTestId("assistant-thread").GetAttributeAsync("aria-live")).ShouldBe("polite");
        var height = await pane.EvaluateAsync<double>("e => e.getBoundingClientRect().height");
        var window = await session.Page.EvaluateAsync<double>("() => window.innerHeight");
        height.ShouldBeGreaterThan(window * 0.8);
        (await session.Page.GetByTestId("assistant-send").IsDisabledAsync()).ShouldBeTrue("nothing to send yet");
    }

    private async Task<AdminSession> OpenAsync(int height = 900)
    {
        var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.SetViewportSizeAsync(1400, height);
        await session.GoAsync("/schema");
        await session.Page.GetByTestId("assistant-launch").ClickAsync();
        await session.Page.GetByTestId("assistant-drawer").WaitForAsync();
        return session;
    }

    private static ILocator Turns(AdminSession session)
        => session.Page.GetByTestId("assistant-thread").GetByTestId("assistant-turn");

    private static async Task AskAsync(AdminSession session, string question)
    {
        var before = await Turns(session).CountAsync();
        await session.Page.Locator("#assistant-message").FillAsync(question);
        await session.Page.Locator("#assistant-message").PressAsync("Control+Enter");
        await session.Page.WaitForFunctionAsync(
            "n => document.querySelectorAll(\"[data-testid='assistant-turn']\").length >= n + 2", before);
    }

    private static Task<bool> AtBottomAsync(AdminSession session)
        => session.Page.GetByTestId("assistant-thread")
            .EvaluateAsync<bool>("e => e.scrollHeight - e.scrollTop - e.clientHeight < 4");
}
```

`AskAsync` waits for two new turns because the scripted assistant answers with text (study of `ScriptedAssistant.cs`: it streams "Adds an invoices entity…"), so each question adds "You" and "Alvo". The `OpenAsync` returns a session the caller disposes: write each fact as `await using var session = await OpenAsync();`, as shown.

Run: `scripts/test-admin-e2e --filter AssistantKeyboardScenarios`
Expected: FAIL: Ctrl+Enter does nothing, the box keeps its text, there is no `assistant-turn`, and there is no `aside` label.

- [ ] **Step 2: `followNewest`**

In `admin.js`:

```js
/**
 * Keeps a growing list's newest item in view, unless the operator has scrolled up to read something older.
 *
 * "Scrolled up" is remembered from the operator's own scrolling, not measured after the list grew: once the new
 * turn is in, every list is "not at the bottom", and a check made then would never follow.
 */
export function followNewest(element) {
  if (!(element instanceof HTMLElement)) {
    return;
  }

  if (!element.dataset.alvoFollow) {
    element.dataset.alvoFollow = 'on';
    element.addEventListener('scroll', () => {
      const gap = element.scrollHeight - element.scrollTop - element.clientHeight;
      element.dataset.alvoFollow = gap < 48 ? 'on' : 'off';
    }, { passive: true });
  }

  if (element.dataset.alvoFollow === 'on') {
    element.scrollTop = element.scrollHeight;
  }
}
```

In `AdminInterop.cs`:

```csharp
    /// <summary>Keeps a growing thread's newest item in view; see <c>followNewest</c> in admin.js.</summary>
    public Task FollowNewestAsync(ElementReference thread)
        => QuietlyAsync(module => module.InvokeVoidAsync("followNewest", thread));
```

- [ ] **Step 3: The pane in the layout, the launcher in the app bar**

In `AdminLayout.razor`:
- In the app bar, before `<ThemeToggle />`:

```razor
        @if (_assistant)
        {
            <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="assistant-launch"
                        aria-expanded="@(_assistantOpen ? "true" : "false")" OnClick="_ => _assistantOpen = !_assistantOpen">
                @(_assistantOpen ? "Close assistant" : "Ask Alvo")
            </AlvoButton>
        }
```

- Replace the `@if (_assistant) { <AssistantDrawer /> }` inside `MudMainContent` with a drawer placed after `MudMainContent`, inside `MudLayout`:

```razor
    @if (_assistant)
    {
        @*
            Beside the page, not over it (spec §3.1): a persistent end drawer, with no scrim and no trap, labelled so a
            screen reader can reach it as a region. Kept mounted while closed, because the thread lives in the
            component and closing the pane must not end the conversation.
        *@
        <MudDrawer @bind-Open="_assistantOpen" Anchor="Anchor.End" Variant="DrawerVariant.Responsive"
                   Breakpoint="Breakpoint.Sm" ClipMode="DrawerClipMode.Always" Elevation="0" Width="420px"
                   Class="a-assistant-drawer" aria-label="Assistant">
            <AssistantDrawer />
        </MudDrawer>
    }
```

- Add `private bool _assistantOpen;`.
- `MudDrawer` renders an `<aside>` (study §4.1 [S]). If `aria-label` does not land on it, wrap the content in `<section aria-label="Assistant" role="complementary">` and change the scenario's `closest('aside')` to `closest('[role=complementary]')`. Do both in the same commit.

- [ ] **Step 4: The pane itself**

In `AssistantDrawer.razor`:
- Delete the launcher `<button>`, `_open`, `Toggle` and the `@if (_open)` wrapper. The root element is `<div class="a-assistant" data-testid="assistant-drawer">`.
- The thread:

```razor
        <div class="a-assistant__thread" data-testid="assistant-thread" aria-live="polite" @ref="_thread">
            @foreach (var turn in _turns)
            {
                <div class="a-assistant__turn" data-testid="assistant-turn">
                    <span class="a-assistant__who">@(turn.Role is AssistantRole.Operator ? "You" : "Alvo")</span>
                    <span class="a-assistant__text">@turn.Text</span>
                </div>
            }
            @* tools, streaming turn (also data-testid="assistant-turn"), failure panel, unchanged otherwise *@
        </div>
```

- The ask box:

```razor
        <div class="a-assistant__ask">
            <MudTextField T="string" Variant="Variant.Outlined" Lines="3" Immediate="true" id="assistant-message"
                          Label="Your question" Placeholder="Add a nullable note column to work orders"
                          HelperText="Ctrl+Enter or ⌘+Enter sends. Enter is a new line."
                          Value="_message" ValueChanged="text => _message = text ?? string.Empty"
                          OnKeyDown="KeyAsync" />
            <div class="a-row a-row--gap-2">
                <AlvoButton Tone="AlvoButton.ButtonTone.Primary" data-testid="assistant-send" Busy="_busy"
                            Disabled="@(_message.Trim().Length == 0)" OnClick="_ => AskAsync()">
                    @(_busy ? "Thinking…" : "Ask")
                </AlvoButton>
            </div>
        </div>
```

- In `@code`:

```csharp
    private ElementReference _thread;

    /// <summary>Ctrl/Cmd+Enter asks; Enter alone stays a newline, which the text box does by itself (spec §3.4).</summary>
    private Task KeyAsync(KeyboardEventArgs args)
        => args.Key == "Enter" && (args.CtrlKey || args.MetaKey) ? AskAsync() : Task.CompletedTask;

    /// <summary>After every render the thread follows its newest turn, unless the operator scrolled up (spec §3.5).</summary>
    protected override Task OnAfterRenderAsync(bool firstRender) => Interop.FollowNewestAsync(_thread);
```

- Add `@inject AdminInterop Interop`.
- `AskAsync` already refuses while `_busy` or empty, and `Begin` already sets `_message = string.Empty`. That clear now reaches the screen, because the value is component-driven (study §4.1 [S]), which fixes defect #3 with no other change.
- The input is deliberately **not** disabled while a turn streams: the operator may type the next question. Only Send is busy.
- The streaming turn's `<div>` gets `data-testid="assistant-turn"` too, so a streamed answer counts as the turn it becomes.

- [ ] **Step 5: CSS, ports, run, commit**

In `alvo.css`:

```css
  .a-assistant {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    gap: var(--space-3);
    padding: var(--space-4);
  }

  .a-assistant__thread {
    flex: 1;
    min-height: 0;
    overflow: auto;
  }
```

Delete `.a-assistant__launch` and the fixed-position rules of the old floating drawer. If any remaining `z-index` names `--z-assistant` and nothing uses it any more, delete the token too: `Every_z_index_names_a_stacking_plane` still holds.

In `AssistantScenarios.cs`, `ClickAsync("[data-testid='assistant-launch']")` is unchanged (same test id). `FillAsync("#assistant-message", …)` is unchanged. The send stays a click on `assistant-send`, which still works. Lower the ratchet if anything moved.

Run:
1. `scripts/test-admin-e2e`: PASS, including `AssistantScenarios`, `RefusedProposalScenarios`, `ConfiguringTheAssistantScenarios` and `AssistantKeyboardScenarios`.
2. `dotnet test --project test/MMLib.Alvo.Admin.Tests`
3. `scripts/test-prototype`
4. `scripts/test-ring1`

Commit:

```text
feat(admin): the assistant is a full-height pane you type to like a chat

The assistant opens from the app bar as a non-modal end drawer as tall as the window. Ctrl/Cmd+
Enter sends and Enter is a newline (defect #1). The thread follows its newest turn unless the
operator scrolled up (defect #2), and announces turns politely. The box empties on send because
its value is component-driven now (defect #3). Send is busy while a turn streams.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---
### Task 10: Read-only screens, Settings, and the sign-in page on the library's variables

Spec §3.3 (Settings: a snackbar "Saved" and an `AlvoAlert` refusal), §3.6 (loading, empty and error), D10 (sign-in is native markup on Mud's CSS variables). Study §2.3, §4.2 rows Overview / Welcome / NotYet / Integrations / Settings / SignIn. Inventory success pattern §2d.3 ("Saved." left beside the button forever).

**Files:**
- Modify: `Components/DesignSystem/Skeleton.razor`: the body becomes `MudSkeleton`s. The `SkeletonSize` API and every call site are unchanged: Sm = 1 line, Md = 3, Lg = 6. The frame is `<div class="a-skeleton-frame" aria-busy="true" aria-label="Loading">`.
- Modify: `Components/Home/Overview.razor`, `Home/Stat.razor`, `Home/Welcome.razor`, `Home/Step.razor`:
  - Stat tiles are `MudPaper Outlined="true" Elevation="0"`.
  - The "project is new" banner is `AlvoAlert Tone=Info` with an `AlvoButton Href` action.
  - Apply the control migration rule.
- Modify: `Components/Integrations/Integrations.razor`: each declared block is a `MudExpansionPanel` (inside `MudExpansionPanels Elevation="0"`) holding its `CodeBlock`. "Not declared" stays one sentence.
- Modify: `Components/Shell/NotYet.razor`, `DesignSystem/NotYetPanel.razor`: the panel's frame is an `AlvoAlert Tone=Info`. `NotYetConsequence` and the `a-notyet*` / `a-refused*` classes stay, because `ComponentLayerTests.Warned_and_refused_are_two_different_classes` pins them.
- Modify: `Components/Settings/Settings.razor`:
  - The AI form follows the control migration rule (`ai-endpoint`, `ai-model`, `ai-key` with `InputType.Password`, all ids kept). The kind stays a `ChipGroup`.
  - Save is `AlvoButton Primary Busy` (`ai-save`, kept).
  - The `_saved` "Saved." span is deleted, and success becomes `Snackbar.Confirm("Saved the AI connection")`.
  - `_saveRefusal` renders `ErrorPanel TakeFocus="true"`.
- Modify: `Components/Shell/SignIn.razor`: unchanged markup (native `<input>` / `<button>`, D10). Only the error panel's test id port below, and a class hook `a-signin` on the form for the CSS.
- Modify: `wwwroot/alvo.css`:
  - Delete the `.a-skeleton--*` size rules that `Skeleton.razor` no longer names. Mark `/* gallery-only */` those the gallery still draws, until Task 11 redraws it.
  - Add `.a-skeleton-frame`.
  - Add the sign-in seam:

```css
  /* Sign-in is static, so the library's inputs cannot run there (D10, study §2.3); its native controls take the
     library's variables, which the theme provider writes statically too. Each falls back to Alvo's token, so a
     page with no provider (the gallery) still draws. */
  .a-signin .a-btn--primary {
    background: var(--mud-palette-primary, var(--accent));
    color: var(--mud-palette-primary-text, var(--accentText));
    border-radius: var(--mud-default-borderradius, var(--radius-xs));
  }

  .a-signin .a-input {
    border-radius: var(--mud-default-borderradius, var(--radius-xs));
  }

  .a-signin .a-input:focus-visible {
    border-color: var(--mud-palette-primary, var(--accent));
  }
```

- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/SettingsScenarios.cs`.
- Modify (port):
  - `SignInScenarios.cs`: `page.Locator(".a-error__title")` ×2 → `page.GetByTestId("error-title")`.
  - `SchemaScenarios.cs` and `PhoneAndKeyboardScenarios.cs`: `main.a-content` → `session.Content` where a scenario reads text.
  - `AssistantScenarios.ConfiguringTheAssistantScenarios`: only if it asserted the "Saved." text; it now waits for `SnackbarAsync("Saved the AI connection")`.
  - `EndToEndSelectorTests.cs`.

**Interfaces:**
- Consumes: `AlvoAlert`, `AlvoButton`, `ErrorPanel.TakeFocus`, `ISnackbar.Confirm`, `ConfigurableAssistantWorld` (test).
- Produces: the snackbar "Saved the AI connection".

- [ ] **Step 1: Write the failing scenarios**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/SettingsScenarios.cs`:

```csharp
namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Saving the AI connection is a snackbar, not a word left beside the button (spec §3.3; inventory §2d.3).</summary>
/// <param name="world">A host with an agent installed and a writable secret store.</param>
public sealed class SettingsScenarios(ConfigurableAssistantWorld world) : IClassFixture<ConfigurableAssistantWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Saving_the_connection_says_so_once_and_leaves_nothing_behind()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/settings");

        await session.Page.FillAsync("#ai-endpoint", "http://127.0.0.1:1/v1");
        await session.Page.FillAsync("#ai-model", "scripted");
        await session.Page.GetByTestId("ai-save").ClickAsync();

        await session.SnackbarAsync("Saved the AI connection");
        (await session.Content.GetByText("Saved.", new() { Exact = true }).CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task While_the_page_loads_it_shows_its_shape_not_a_blank()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.Page.GotoAsync(session.Page.Url.Replace("/admin", "/admin/settings", StringComparison.Ordinal),
            new() { WaitUntil = Microsoft.Playwright.WaitUntilState.Commit });

        await session.Page.GetByLabel("Loading").First.WaitForAsync();
    }
}
```

The second fact holds only if the prerendered page renders the skeleton, which it does: every screen's first render is its loading branch. If `SignInAsync` leaves `Page.Url` somewhere other than an `/admin` path, build the address from `AdminWorld.BaseAddress` instead: `$"{world.BaseAddress}{AlvoAdmin.BasePath}/settings"`.

Run: `scripts/test-admin-e2e --filter SettingsScenarios`
Expected: FAIL: there is no snackbar, and "Saved." is still on the page.

- [ ] **Step 2: Implement the listed changes**

`Skeleton.razor`:

```razor
@*
    Loading, drawn as the shape of what is coming (spec §3.6): known-shape content gets a skeleton, never a spinner
    on an empty page. The frame is labelled, so "loading" is something a screen reader hears, not only something a
    sighted operator infers from grey bars.
*@
<div class="a-skeleton-frame" aria-busy="true" aria-label="Loading">
    @for (var line = 0; line < Lines; line++)
    {
        <MudSkeleton SkeletonType="SkeletonType.Text" Animation="Animation.Wave" Width="@Width(line)" />
    }
</div>

@code {
    /// <summary>How much content is coming.</summary>
    [Parameter]
    public SkeletonSize Size { get; set; } = SkeletonSize.Md;

    /// <summary>How much content is coming, as the three heights a screen reaches for.</summary>
    public enum SkeletonSize
    {
        /// <summary>One line.</summary>
        Sm,

        /// <summary>A short block.</summary>
        Md,

        /// <summary>A panel's worth.</summary>
        Lg,
    }

    private int Lines => Size switch { SkeletonSize.Sm => 1, SkeletonSize.Lg => 6, _ => 3 };

    /// <summary>Uneven line lengths, so the shape reads as text rather than as a table.</summary>
    private static string Width(int line) => (line % 3) switch { 0 => "70%", 1 => "55%", _ => "62%" };
}
```

Keep the existing XML docs of `SkeletonSize`'s members if they say more.

For the other files, follow the **Files** list. The Settings save handler after success reads:

```csharp
            _saveRefusal = null;
            Snackbar.Confirm("Saved the AI connection");
```

(`_saved` and its span are gone.)

- [ ] **Step 3: Ports, run everything, commit**

Port as listed under **Files** and lower the ratchet. Then run:
1. `scripts/test-admin-e2e`: PASS, including `SignInScenarios`, whose sign-in page is still native, and `RevokedSessionScenarios` (`forbidden-sign-out` inside the re-skinned `ErrorPanel`).
2. `dotnet test --project test/MMLib.Alvo.Admin.Tests`
3. `scripts/test-prototype`
4. `scripts/test-ring1`

Commit:

```text
feat(admin): read-only screens, settings and sign-in on the library's look

Overview, Welcome, Integrations and the not-yet screens are composed from Mud surfaces, and
loading is MudSkeleton in a labelled frame. Settings reports a saved connection as a snackbar
and a refusal as an alert that takes focus, instead of a "Saved." that stayed. Sign-in stays
native markup (it is static) and takes the library's CSS variables, falling back to the tokens.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---

### Task 11: Retire what was replaced; the gallery documents the wrappers; the ratchet lands; the Release image

Spec D4, D7, D11 and §5 (the ratchet must *fall*). Study §8.2 step 11.

**Files:**
- Delete: `Components/DesignSystem/Sheet.razor` and `Components/DesignSystem/ConfirmByName.razor` (no caller after Tasks 3–8; `grep -rn "<Sheet\|<ConfirmByName" src/MMLib.Alvo.Admin` must print nothing first).
- Delete any `DesignSystem` or `Shell` component that the tasks above left without a caller. Check each one with `grep -rln "<Name[ >]" src/MMLib.Alvo.Admin` before deleting; the likely ones are only those two.
- **Keep** (D7 / V5 / V7): `SplitHandle`, `ChipGroup` + `ChipSelection`, `CodeBlock`, `DescriptorDiff`, `Icon`/`Icons`, `Field` (sign-in and group labels), `Panel`, `ListRow`, `PageHeader`, `SectionHead`, `EmptyState`, `Refusal`, `NotYetPanel`, `NotYetConsequence`, `ErrorPanel`, `Skeleton`, `CssClass`, `ScrollLock` and the map.
- Modify: `wwwroot/alvo.css`:
  - Delete every rule whose class no product source names any more: `.a-sheet*`, `.a-scrim*`, `.a-confirm` + `.a-confirm__field`, and any `.a-btn*`, `.a-input`, `.a-textarea` or `.a-select` variant left unnamed.
  - Leave in place what `docs/design/f5-admin` still renders, marked `/* prototype-only: … */`. What the gallery alone draws gets `/* gallery-only: … */`.
  - `StylesheetHygieneTests` is the list; run it until it is green.
  - Keep the `mud` layer and `alvo-mud.css`: Alvo keeps CSS of its own (study §8.2 step 11).
- Modify: `docs/design/gallery.html`:
  - Remove the drawings of retired primitives (the `a-confirm` block at ~line 115, the `a-palette` block at ~line 497, and every `a-skeleton` inline block, which the gallery drew with inline `style=`).
  - Add a "Wrappers" section (below).
  - Keep tokens, the map, `CodeBlock`, the diff, chips (`a-choice`, still Alvo's) and the not-yet classes.
- Modify: `test/MMLib.Alvo.Admin.Tests/GalleryTests.cs`:
  - Drop `[InlineData("a-skeleton--sm")]` if that class is retired.
  - Keep `role="radiogroup"` (chips stay).
  - Add the wrappers theory.
- Modify: `test/MMLib.Alvo.Admin.Tests/EndToEndSelectorTests.cs`: the final constants, which must be below 43 and 51.
- Modify: `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt`: `Sheet` and `ConfirmByName` removed.

**Interfaces:** consumes everything above and produces nothing new.

- [ ] **Step 1: The failing gallery fact**

Add to `GalleryTests.cs`:

```csharp
    /// <summary>
    /// The gallery documents the wrappers the pattern language is built on, by name, with the rule each carries.
    /// </summary>
    /// <remarks>
    /// It cannot draw them: the gallery links alvo.css from the repository, and the library's stylesheet is in a
    /// NuGet package, not the repository (plan V1). So it names each wrapper and the §3 rule it implements, which is
    /// what a reader deciding which component to reach for needs.
    /// </remarks>
    [Theory]
    [InlineData("AlvoEditor")]
    [InlineData("AlvoConfirm")]
    [InlineData("AlvoAlert")]
    [InlineData("AlvoButton")]
    [InlineData("AlvoTheme")]
    public void The_gallery_names_each_wrapper(string wrapper)
        => _gallery.ShouldContain(wrapper);
```

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: FAIL on all five wrappers.

- [ ] **Step 2: The gallery's wrapper section**

Add before the gallery's footer paragraph. It uses only classes the gallery already has, plus `a-*` classes from alvo.css, and has no inline colour (`GalleryTests.The_gallery_declares_no_colour_of_its_own`):

```html
<section class="g-section" id="wrappers">
  <h2>Wrappers: the pattern language, implemented once</h2>
  <p>
    The screens are drawn with MudBlazor 9.10.0, imported beneath this stylesheet's layers. These five Alvo components
    carry the policy the library cannot set globally; every screen uses them rather than the library directly for
    these jobs (<code>docs/superpowers/specs/2026-09-24-f5-admin-mudblazor-design.md</code> §3). None takes a MudBlazor
    type as a parameter.
  </p>
  <table class="a-grid">
    <thead><tr><th>Component</th><th>Use it for</th><th>What it guarantees</th></tr></thead>
    <tbody>
      <tr><td><code>AlvoEditor</code></td><td>Creating or editing one item with more than one value</td>
          <td>Right-hand modal side sheet; focus on the first field; Escape and Cancel ask “Discard your changes?” when dirty; Enter and Ctrl/Cmd+Enter submit; the submit is busy and cannot run twice.</td></tr>
      <tr><td><code>AlvoConfirm</code></td><td>Any destructive action</td>
          <td>Centred; no light dismiss; Escape = Cancel; the button names the verb; <code>TypeToConfirm</code> for an irreversible, wide-blast one (destructive apply, rollback).</td></tr>
      <tr><td><code>AlvoAlert</code></td><td>State, and every error</td>
          <td><code>role=alert</code> for errors and warnings, <code>role=status</code> otherwise; takes focus after a failed submit. Errors are never snackbars.</td></tr>
      <tr><td><code>AlvoButton</code></td><td>Every button</td>
          <td>Four tones (Primary, Secondary, Danger, Ghost); no ripple, shadow or uppercase; <code>Busy</code> disables it and shows progress.</td></tr>
      <tr><td><code>AlvoTheme</code></td><td>The library’s palette</td>
          <td>Both themes from this stylesheet’s tokens (pinned equal by <code>AlvoMudThemeTests</code>); dark is scoped to <code>[data-theme=dark]</code>, written before paint by alvo.js.</td></tr>
    </tbody>
  </table>
</section>
```

If `g-section` is not the gallery's section class, use whichever class its other sections use. Read the file first and match it. If `a-grid` is retired from alvo.css by Step 3 (Task 4 kept it), use the gallery's own table styling instead.

- [ ] **Step 3: Retire, then let the hygiene test finish the list**

Delete the two components. Then run `dotnet test --project test/MMLib.Alvo.Admin.Tests` and read three facts:
- `StylesheetHygieneTests.Every_class_the_stylesheet_defines_is_named_by_the_product_or_marked_gallery_only`: every class it lists is deleted, or marked if the prototype or gallery renders it.
- `A_prototype_only_class_is_rendered_by_the_prototype…`: a mark on a class the prototype does not render is a wrong mark.
- `Every_class_the_prototype_renders_is_defined_by_the_files_it_links`: a class deleted that the prototype still renders must come back, marked.

Repeat until all three pass. Accept the `.received.txt` of the public API: `Sheet` and `ConfirmByName` gone, nothing added, and no `MudBlazor`.

- [ ] **Step 4: The ratchet, final**

Run `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class "*EndToEndSelectorTests"`, or the whole project. Set `ClassSelectors` and `HasTextSelectors` to the reported counts. **Both must be lower than the 43 and 51 this plan started from** (spec §5: they must fall). If either is not, find the remaining `.a-*` or `:has-text(` uses in `*Scenarios.cs` that a task above left, and port them to roles or test ids now.

- [ ] **Step 5: All gates, including the Release image**

Run, in order:
1. `dotnet test --project test/MMLib.Alvo.Admin.Tests`
2. `scripts/test-admin-e2e`
3. `scripts/test-prototype` (the prototype still renders everything it linked, with the `prototype-only` rules)
4. `scripts/test-ring1`
5. `docker build -f src/MMLib.Alvo.Host/Dockerfile -t alvo-admin-mud-check .`

Expected: all green, and the image builds under the Release analyzer set.

- [ ] **Step 6: Commit**

```text
refactor(admin): retire the hand-rolled overlay primitives; the gallery names the wrappers

Sheet and ConfirmByName have no caller since every overlay is AlvoEditor or AlvoConfirm, so
they and their stylesheet rules go; what the F5 prototype still renders is kept and marked
prototype-only (D11). The gallery documents the five wrappers and the rule each carries, since
it cannot draw library components. The e2e selector ratchet lands lower than it started, as spec
§5 requires. PublicApi: Sheet and ConfirmByName removed.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV
```

---

## Self-review (done while writing; kept so a reviewer can check it)

**Spec coverage:**

| Spec item | Where |
|---|---|
| D1 MudBlazor 9.10.0 pinned, review note reversed | Task 1 Steps 3 and 10 |
| D2 not Fluent | Header; Task 1 note |
| D3 pattern language binding | Global Constraints; every task names its §3 rules |
| D4 wrappers (`AlvoEditor`, `AlvoConfirm` + typed-name, `AlvoAlert`, `AlvoButton`, focus ring) | Task 1 Steps 8–9; the base-layer `:focus-visible` ring reaches Mud through the layer, measured in Task 2 |
| D5 no Mud type in a public member | Task 1 `LibraryBoundaryTests`; every baseline change is checked |
| D6 logic verbatim | Global Constraints; the additive methods are named (`RevisionHistory.Previous`, interop) |
| D7 kept pieces | Global Constraints; Tasks 4 (RefPicker), 5 (map), 8 (split), 11 (keep list) |
| D8 layered CSS + focus ring | Task 1 (`alvo-mud.css`, `LibraryLayerTests`, `FoundationScenarios`); Task 2 (focus-ring scenario) |
| D9 dark mode without a flash | Task 1 (`AlvoTheme`, `alvo.js`, three theme scenarios) |
| D10 sign-in native on Mud variables | Task 10 |
| D11 prototype not rebuilt, stays green | Global Constraints (no `@import` in alvo.css); every task's gates; Task 11 marks |
| §3.1 editing places | Tasks 3, 4, 5, 7 (editors); 8, 9 (panes); 5 (rules inline + explicit commit) |
| §3.2 destructive | Tasks 3 (remove field, discard), 4 (delete record), 5 (index/hook), 6 (apply, typed), 7 (disable), 8 (rollback, typed) |
| §3.3 feedback | `ISnackbar.Confirm` + `AlvoAlert`/`ErrorPanel.TakeFocus`; the "never a snackbar for an error" assertion in Task 3 |
| §3.4 forms and keys | `AlvoEditor` (Enter, Ctrl/Cmd+Enter, Escape, busy, focus); rules, import and assistant chords in Tasks 5, 6, 9 |
| §3.5 lists | Task 9 (`followNewest`); new items appear in place (the Task 3, 5 and 7 scenarios assert the row). **Gap, stated:** "scrolled to and highlighted" for a created list item is not asserted beyond "appears". The staged badge is the highlight Alvo already draws; a scroll-into-view for a created row is left to the follow-up plan. |
| §3.6 loading, empty, error | Task 10 (`MudSkeleton` frame); empty states kept; the `ErrorBoundary` panel is `ErrorPanel` over `AlvoAlert` (Task 3) |
| §3.7 navigation | Task 2 |
| §4 Assistant | Task 9 (every bullet) |
| §4 History | Task 8 |
| §4 Data record | Tasks 3–4 |
| §4 Access | Task 7 |
| §4 Schema | Tasks 3, 5 |
| §4 Command palette | Task 2 |
| §4 Settings | Task 10 |
| §5 behavioural scenarios | Each task's new `*Scenarios.cs` |
| §5 ratchet falls | Every task lowers the counts; Task 11 asserts they end lower |
| §6 no API or core changes, no bUnit, no rebrand | Global Constraints |

**Placeholder scan.** Where the plan says "unchanged", it names the exact block that stays. Every place where the library's 9.10.0 surface is unverified names the check (the build, or a named scenario) and the fallback markup:
- `MudDialog.OnKeyDown`
- `MudDialog.CloseAsync`
- `ResizeOptions.BreakpointDefinitions`
- `IBrowserViewportObserver`
- `BaseTypography.FontWeight`'s type
- where `data-testid`/`aria-label` land on `MudDialog`/`MudDrawer`

**Type consistency.** These names are used identically in every task:
- `AlvoButton.ButtonTone`, `AlvoAlert.AlertTone`
- `AlvoEditor`'s `SubmitTestId`/`TestId`/`Dirty`/`Busy`/`CanSubmit`/`OnSubmit`/`OnClose`/`ExtraActions`
- `AlvoConfirm`'s `Open`/`TestId`/`ConfirmTestId`/`CancelTestId`/`TypeToConfirm`/`Allowed`/`Busy`/`OnConfirm`/`OnCancel`
- `ErrorPanel.TakeFocus`
- `ISnackbar.Confirm`
- `AdminSession.Dialog/ChooseAsync/SnackbarAsync/FocusedAsync/FocusIsInsideAsync`
- `RevisionHistory.Previous`
- `RuleDrafts`, `EntityFilter`, `PersonDraft`
