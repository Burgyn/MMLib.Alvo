# F5 admin — the 24 Sep audit fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the §7/§8d queue items 14–20, 22–24 and 28 so that no dashboard control writes a facet nobody chose, composes a descriptor the apply refuses, or hides a refusal — and so rollups, computed fields, field references, rollback, entity removal and the people list are reachable from the screens.

**Architecture:** Every rule lands in a small, unit-testable internal type next to the screen it serves (`FieldFacets` + two partials, `RollupSources`, `FieldReferences` + `CelNames`, `EntityReferences.Inbound`, `RollbackGate`, `RefusalPlaces`, `FieldBadges`, `PeoplePaging`); the Razor components only draw what those types decide. All edits go into `WorkingCopy` and out through the one apply — no port, endpoint or core change.

**Tech Stack:** .NET 10, Blazor server-interactive Razor class library, `System.Text.Json.Nodes`, xUnit v3 + Shouldly (unit, `test/MMLib.Alvo.Admin.Tests`), Microsoft.Playwright + xUnit v3 (e2e, `test/MMLib.Alvo.Admin.Tests.EndToEnd`).

**Spec:** `docs/todo-admin.md` §7 and §8 (§8a evidence rows, §8d queue items 14–24 and 28). Read §7 and §8d before each task; the §8a row for the key you are touching carries the file:line evidence.

**Out of scope (said, not silent):**
- **21** (`storage: dynamic` dropped silently) — a core question first (§8d 21: the mapper discards the entity without a warning, `DescriptorToSchemaMapper.cs:44-46`); the dashboard answer depends on what the core decides to warn.
- **25** (last-writer-wins data writes, filters, batch, replace), **26** (hooks half-authorable), **27** (keys the build ignores without a word — owed by the core first), **29** (cosmetics; this includes `WorkingCopy.AddEntity` still writing `maxLength: 120`, `audit: false`, `tenancy: global` explicitly on the seed entity — visible in the add-entity sheet, and item 29's).
- #265 (`onDelete` control) stays filed; Task 1 only makes the silent `restrict` a *said* one.

## Apply-side truth this plan builds on (established, cited)

| Question | What the core does | Evidence |
|---|---|---|
| `maxLength` absent | accepted; the column is unbounded (`MaxLength = f.MaxLength`, nullable) | `DescriptorToSchemaMapper.cs:406`; schema `field.maxLength` optional, `minimum: 1` |
| `unique` on any type | accepted and mapped for every type — nothing refuses a unique boolean or ref | `DescriptorToSchemaMapper.cs:404` |
| `required` + literal `readOnly: true` | **refused** unless a literal (non-`$cel`) default is declared | `DescriptorValidator.cs:438,504-510,635` |
| a default on `ref` | **refused** (`Fits` → `_ => false`) | `FieldDefault.cs:190-199` |
| a default on `json` | accepted, any JSON kind | `FieldDefault.cs:197` |
| boolean default | only JSON `true`/`false` fits | `FieldDefault.cs:190` |
| `default.$cel` | **refused** (slot `field.default`) | `UnhonouredFeatures.cs:63-70` |
| default vs its own facets | **refused**: string longer than `maxLength` (UTF-16 `Length`), enum value not in `values`, non-uuid, non-date, decimal past precision | `FieldDefault.cs:121-145,156-172` |
| default beside `computed`/`rollup` | **refused** (and schema `if required computed/rollup then default: false`) | `FieldDefault.cs:88-99`; schema `field.allOf[7..8]` |
| rollup `from` | **refused** when undeclared, `storage: dynamic`, or its tenancy disagrees with the parent's (resolved: declared, else scoped when `tenancy.enabled`) | `RollupResolver.cs:48-69,153-172,186-192,218-227` |
| rollup `via` | optional with exactly one child ref to the parent; **refused** with none; **required** with more than one; **refused** when it is not such a ref | `RollupResolver.cs:252-279` |
| rollup `field` | **required** for every op but `count`; **refused** when the child does not declare it (its type is not checked) | `RollupResolver.cs:304-327` |
| rollup `where` | **refused** (slot `rollup.where`) | `RollupResolver.cs:107-115`; `UnhonouredFeatures.cs:128` |
| rollup + computed | **refused** | `RollupResolver.cs:81-92` |
| parent type of a rollup | not checked at all — a `string` count rollup applies | `RollupResolver.cs:46-69` |
| `computed` CEL | compiled at apply for `CelProfile.Computed` against the entity: own fields, arithmetic, `CASE WHEN`; **refused** when it does not compile or carries a constant (DDL has no bind parameter) | `ComputedColumnSql.cs:71-127` |
| a CEL rule/computed naming a column that does not exist | **refused** (compiled against the entity's own columns) | `RulesTab.razor:44-49`; `ComputedColumnSql.cs:96-104` |
| a `mutate` key that is not a field | **refused** | §8a `renamedFrom` row (`BeforeHookCompiler.cs` BHC) |
| a `ref` to an undeclared entity | **refused** (`users` exempt) | `DescriptorValidator.cs:419-425,734-752` |
| refusal slots published today | `field.validation`, `field.default`, `entity.softDelete`, `rollup.where`, `trigger.event`, `JSONata`, `email.data`, `bodyFile`, `function`, `http.call`, `entity.update` | `UnhonouredFeatures.cs:297-303` (`EveryRefusal`) |
| warned block names | `dynamicEntities`, `automation` (singular), `templates`, `webhooks`, `functions` — always listed, declared or not | `UnhonouredSubsystems.cs:119-147`; `CapabilityReport.cs:59` |

## Deliberate deviations from the spec's "probably" (recorded so a reader can tell a decision from an oversight)

1. **Field rename rewrites CEL token by token, not by word boundary (item 16).** A word-boundary replace of `manager` would rewrite `'manager' in @user.roles`. `CelNames.Rename` skips string literals, members of anything but `new`/`old`, names after `@`, and function names; it declines (returns `null`, and the place is *named* instead) when the name is a CEL reserved word or the expression uses a binding macro (`.all(` `.exists(` `.exists_one(` `.map(` `.filter(`) or a triple-quoted string. `EntityReferences` refuses CEL for entity renames because an identifier's referent is unknown there; for a field in its own entity's CEL it is known (a bare name is a column, `new.`/`old.` are the row images).
2. **A field removal is refused, not merely named, while a structured reference or a confirmed CEL reference names it (item 16).** The spec says "names what points at the field before staging"; the apply refuses every such result (RollupResolver, BHC, CEL compile), and "a control never composes a descriptor the apply refuses" wins. A mention the tokenizer cannot confirm is named and does not block.
3. **Entity removal is refused while a ref, a rollup or an `entity.update` action points at it (item 24)**, for the same reason (`DescriptorValidator.cs:419`, `RollupResolver.cs:186`). A trigger pattern naming it is named, not blocking (automation is warned; unverified whether the mapper refuses an exact pattern on a missing entity).
4. **`avg` is offered over the child's `decimal` fields only (item 14).** §7 says "the child field's type"; for an integer child that is an integer column holding a rounded mean, and deriving a decimal would invent a precision. `sum`/`min`/`max` take integer and decimal children. A declared precision/scale on a rollup is kept; it is derived from the child only when absent.
5. **A computed field's type is chosen, not derived (item 14).** The dashboard has no CEL compiler (`RulesTab.razor:44-49` gives the reason); it offers the scalar subset `string, text, integer, decimal, boolean, date, datetime` — withholding `ref`, `enum`, `json`, `uuid` is the conservative choice, unverified at apply.
6. **`required` beside `readOnly: true` stays a control; the build refuses the pair (item 17).** Hiding the checkbox would leave a declared `required: true` with no control; the refusal names the apply's reason and the way out (a default).
7. **A kept default the apply refuses (`$cel`, or beside a maintained value) blocks Save with a refusal and an explicit "Remove the declared default" checkbox (item 17).** This changes the existing fact `A_computed_fields_default_is_left_as_declared_because_there_is_no_control_for_it`: carrying a refused declaration forward is composing it.
8. **Rollback's typed-name confirm is asked only for a destructive plan (item 18)**, exactly as Preview asks it; a non-destructive rollback is one click after its plan is on screen. **No rollback e2e:** two revisions are needed and a second apply does not finish under the in-process host (`ChangeTheBackendScenarios.cs` remarks); `RollbackGate` is unit-tested and `PlanSteps` is the Preview's own rendering, which `DestructivePlanScenarios` drives.
9. **`users` is not offered as a ref target (item 28)** — the validator exempts it (`DescriptorValidator.cs:750`), whether the migrator can FK to it is unverified.
10. **Refusals: an explicit admin-side slot→screen map, and every slot the map does not place is shown on Overview (items 19–20).** The core-side answer is an owner field on `ManagementRefusedFeature` — #269's to decide (and a port change, out of this plan's scope). An e2e fact fails the day the core publishes a slot the map does not place.

## File structure

| File | Responsibility | Task |
|---|---|---|
| `src/MMLib.Alvo.Admin/Components/Schema/FacetNote.cs` (new) | `FacetFate`, `FacetNote` — a facet the editor does not draw and what Save does to it | 1 |
| `…/Schema/FieldFacets.cs` (modify) | supplied-kind build rules; becomes `partial` | 1, 2 |
| `…/Schema/FieldFacets.Notes.cs` (new) | the "not drawn here" notes | 1, 2 |
| `…/Schema/FieldKind.cs` (new) | `Supplied`/`Rollup`/`Computed` | 2 |
| `…/Schema/FieldFacets.Maintained.cs` (new) | rollup and computed build rules | 2 |
| `…/Schema/RollupSources.cs` (new) | the entities a rollup may aggregate, read from the working copy | 2 |
| `…/Schema/FieldEditor.razor(.cs)` (modify) | draws the above | 1, 2, 5 |
| `…/Schema/PendingSchema.cs` (modify) | staged rows keep rollup/computed/index/default/nullable; tenancy resolved | 2, 6 |
| `…/Schema/DescriptorReference.cs` (new) | `DescriptorReference(Place, Blocks)` | 3 |
| `…/Schema/CelNames.cs` (new) | token-level CEL field rename | 3 |
| `…/Schema/FieldReferences.cs` (new) | every place that names a field; rename carry | 3 |
| `…/Schema/WorkingCopy.Fields.cs` (modify) | rename carries references; `ReferencesToField` | 3 |
| `…/Schema/Entity.References.cs` (new, partial of `Entity`) | field-removal and entity-removal handlers | 3, 7 |
| `…/Schema/Entity.razor(.cs)` (modify — **shared with another agent, see constraints**) | sheets, cascades, targets, index view | 2, 3, 5, 6, 7 |
| `…/History/RollbackGate.cs` (new) | when rollback may run and with which permission | 4 |
| `…/Schema/PlanSteps.razor` (new component) | the planner's steps as sentences, shared | 4 |
| `…/Schema/Preview.razor`, `…/History/History.razor` (modify) | use `PlanSteps`; History dry-runs first | 4 |
| `src/MMLib.Alvo.Admin/Internal/RefusalPlaces.cs` (new) | slot → screen map | 5 |
| `…/Schema/HooksTab.razor(.cs)`, `…/Integrations/Integrations.razor`, `…/Shell/NotYet.razor`, `…/Home/Overview.razor` (modify) | read the map | 5 |
| `…/Schema/FieldBadges.cs` (new) | the Fields-tab badges, moved out of `Fields.razor` | 6 |
| `…/Schema/EntityReferences.cs` (modify) | `Inbound` | 7 |
| `…/Schema/WorkingCopy.Entities.cs` (modify) | `ReferencesToEntity` | 7 |
| `…/Access/PeoplePaging.cs` (new), `…/Access/Access.razor` (modify) | search + pages | 8 |
| `docs/todo-admin.md` §8d | one ✅ line per landed item | every task |

## Global Constraints

- Scope: `src/MMLib.Alvo.Admin`, `test/MMLib.Alvo.Admin.Tests`, `test/MMLib.Alvo.Admin.Tests.EndToEnd`, `docs/`. **No core changes** (nothing under `src/MMLib.Alvo*` other than `src/MMLib.Alvo.Admin`).
- **A control never composes a descriptor the apply refuses.** Every refusal the builders return is the apply's own reason, in the operator's words, cited in a `<remarks>` to the core file that owns it.
- **Refused facets get a sentence, not a control.** A refused form of an existing facet gets a control that removes it and nothing else.
- **A facet the current type/kind does not draw is either carried untouched and shown, or removed with a line saying so — never rewritten from a stale prefill.**
- Encoding: `.cs` UTF-8 **with BOM + CRLF**; `.razor` UTF-8 **with BOM + LF** (match the neighbours); `wwwroot/alvo.css` **no BOM**. After creating or editing files with a tool that writes LF/no-BOM, normalise before staging:
  ```bash
  python3 - FILE1 FILE2 <<'EOF'
  import sys, pathlib
  for p in sys.argv[1:]:
      f = pathlib.Path(p); t = f.read_bytes().decode('utf-8-sig').replace('\r\n', '\n')
      if p.endswith('.cs'): t = t.replace('\n', '\r\n')
      f.write_bytes(b'\xef\xbb\xbf' + t.encode('utf-8'))
  EOF
  ```
- Code style (`.claude/skills/alvo-dotnet-conventions`): methods ≤ ~25 lines (extract aggressively), XML docs on every member, comments say *why*. `TreatWarningsAsErrors` is on: interpolate numbers into user-facing strings through `string.Create(CultureInfo.InvariantCulture, $"…")`; no constant arrays passed as arguments (hoist to `static readonly` fields).
- CSS: tokens only (`DesignTokenTests`, `ComponentLayerTests`, `StylesheetHygieneTests` stay green). This plan needs **no new CSS**: every class used below (`a-hint`, `a-note`, `a-check`, `a-refused`, `a-badge--*`, `a-disclosure`, `a-listrow__*`, `a-row`, `a-stack`, `a-field`, `a-textarea`, `a-mono`) already exists.
- Internal types stay internal. A component parameter must be public, so new inputs to an existing component arrive as a **private `[CascadingParameter]` of an internal type** (the `StagedView` pattern), never as a new public parameter.
- `PublicApi.MMLib.Alvo.Admin.verified.txt` grows **only by new components** (Task 4's `PlanSteps`), and that commit says why (F-10 policy: `Components.*` are implementation). If any other task changes it, the task is wrong.
- E2E selectors: new code uses `GetByRole` / `GetByTestId` only (`EndToEndSelectorTests` ratchets raw `.a-*` and `:has-text(`; the counts must not move — the other agent may be changing `SystemMapScenarios.cs`, so re-read the two constants before assuming them).
- **Shared files.** Another agent is editing `Components/Schema/Relationships.razor`, `Components/Schema/Entity.razor(.cs)` and `test/MMLib.Alvo.Admin.Tests.EndToEnd/SystemMapScenarios.cs`. Before any task that touches `Entity.razor` or `Entity.razor.cs`, run `git status --short`; if either still shows the other agent's uncommitted hunks, stop and wait. Never stage, reformat or revert their hunks. Keep edits to those two files to the lines each task names; new handlers go in `Entity.References.cs`.
- Commits: Conventional Commits; stage files **by name** (never `git add -A` / `git add .`); message ends with the line `Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV`. Never push, never switch branches, never dispatch subagents, never touch processes on ports 5080 or 5090.
- Gates at the end of every task: `scripts/test-ring1` green, and — since every task here changes a screen — `scripts/test-admin-e2e` green.
- After each task, mark its item(s) in `docs/todo-admin.md` §8d: put `✅ ` after the item number and append one line starting `**Done:**` saying what landed (exact text given in each task). Stage it with the task's commit.

---

### Task 1: `FieldFacets` writes only what the operator chose (items 15 + 17)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/FacetNote.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.Notes.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.cs` (whole file: `MaxLength`, `Prefill`, `Build`, default writing, type facets)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor` (constraints, max length, default, notes block)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor.cs` (`OptionalNumber`)
- Modify: `test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsTests.cs` (two existing facts change meaning)
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsWriteRulesTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/FieldFacetScenarios.cs`
- Modify: `docs/todo-admin.md` §8d items 15, 17

**Interfaces:**
- Produces (namespace `MMLib.Alvo.Admin.Components.Schema`, all `internal`):

```csharp
internal enum FacetFate { Kept, Removed, Written }
internal sealed record FacetNote(string Facet, string Value, FacetFate Fate, string Reason);

internal sealed partial class FieldFacets
{
    public int? MaxLength { get; set; }                 // was int, default 120
    public bool RemoveUndrawnDefault { get; set; }
    public bool UniqueOffered { get; }                  // TakesUnique || the declaration carries unique: true
    public bool DrawsDefault { get; }                   // the box is drawn
    public bool KeepsUndrawnDefault { get; }            // a declared default no box shows, on the type it was declared for
    public IReadOnlyList<FacetNote> Notes();
    internal static string Word(FieldType type);        // "string", "datetime", …
    // unchanged: Prefill, Parse, Build(editing, editingJson, siblings, out refusal), TakesUnique, TakesADefault, MaintainedElsewhere (still settable in this task)
}
```

- [ ] **Step 1: Write the failing tests**

In `test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsTests.cs`, replace the first fact and the computed-default fact:

```csharp
    [Fact]
    public void A_new_string_field_carries_its_type_and_nothing_it_was_not_given()
    {
        var facets = Built(new FieldFacets { Name = "title", Required = true });

        facets.ToJsonString().ShouldBe("""{"type":"string","required":true}""");
    }
```

```csharp
    /// <summary>
    /// A computed field's own default is shown and refused until removed — carrying it forward would be composing
    /// the pair the apply refuses (FieldDefault.MaintainedElsewhere).
    /// </summary>
    [Fact]
    public void A_computed_fields_declared_default_is_refused_until_it_is_removed()
    {
        const string declared = """{"type":"decimal","precision":10,"scale":2,"computed":"a + b","default":1}""";
        var editor = FieldFacets.Prefill("total", declared);

        editor.TakesADefault.ShouldBeFalse();
        editor.Build("total", declared, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("Remove the declared default");

        editor.RemoveUndrawnDefault = true;
        editor.Build("total", declared, [], out _)!.ContainsKey("default").ShouldBeFalse();
    }
```

Create `test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsWriteRulesTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The editor writes what the operator chose and nothing else (docs/todo-admin.md §8d items 15 and 17): a facet
/// it does not draw is kept and shown, or removed and said — never rewritten from a stale prefill.
/// </summary>
public class FieldFacetsWriteRulesTests
{
    [Fact]
    public void Editing_an_unbounded_string_writes_it_back_unbounded()
    {
        const string declared = """{"type":"string","required":true}""";
        var editor = FieldFacets.Prefill("title", declared);

        editor.MaxLength.ShouldBeNull();
        editor.Build("title", declared, [], out _)!.ToJsonString().ShouldBe(declared);
    }

    [Fact]
    public void Clearing_the_max_length_removes_it()
    {
        const string declared = """{"type":"string","maxLength":40}""";
        var editor = FieldFacets.Prefill("code", declared);
        editor.MaxLength = null;

        editor.Build("code", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"string"}""");
    }

    [Fact]
    public void A_max_length_below_the_schemas_minimum_is_refused()
        => Refusal(new FieldFacets { Name = "code", MaxLength = 0 }).ShouldContain("at least 1");

    /// <summary>An edit that changes nothing is not a staged change: every key stays where it was.</summary>
    [Fact]
    public void An_edit_that_changes_nothing_writes_the_declaration_back_unchanged()
    {
        const string declared = """{"type":"string","description":"Where to send correspondence","maxLength":160,"format":"email"}""";

        FieldFacets.Prefill("email", declared).Build("email", declared, [], out _)!.ToJsonString().ShouldBe(declared);
    }

    [Fact]
    public void A_declared_unique_is_offered_on_a_type_the_checkbox_is_not_drawn_for_and_can_be_cleared()
    {
        const string declared = """{"type":"ref","entity":"customers","onDelete":"restrict","unique":true}""";
        var editor = FieldFacets.Prefill("customer_id", declared);

        editor.UniqueOffered.ShouldBeTrue();
        editor.Unique = false;
        editor.Build("customer_id", declared, [], out _)!.ContainsKey("unique").ShouldBeFalse();
    }

    [Fact]
    public void A_unique_ticked_before_a_retype_to_boolean_is_not_written_where_nobody_can_see_it()
    {
        var editor = new FieldFacets { Name = "active", Unique = true, Type = FieldType.Boolean };

        editor.UniqueOffered.ShouldBeFalse();
        Built(editor).ContainsKey("unique").ShouldBeFalse();
    }

    [Theory]
    [InlineData(FieldType.Ref)]
    [InlineData(FieldType.Json)]
    public void A_default_does_not_follow_a_retype_into_a_type_that_takes_none(FieldType type)
    {
        const string declared = """{"type":"string","maxLength":40,"default":"open"}""";
        var editor = FieldFacets.Prefill("state", declared);
        editor.Type = type;
        editor.Target = "customers";

        editor.Build("state", declared, [], out _)!.ContainsKey("default").ShouldBeFalse();
        editor.Notes().ShouldContain(note => note.Facet == "default" && note.Fate == FacetFate.Removed);
    }

    [Fact]
    public void A_json_fields_own_default_is_kept_and_said()
    {
        const string declared = """{"type":"json","default":{"a":1}}""";
        var editor = FieldFacets.Prefill("meta", declared);

        editor.Build("meta", declared, [], out _)!["default"]!.ToJsonString().ShouldBe("""{"a":1}""");
        editor.Notes().Single(note => note.Facet == "default").Fate.ShouldBe(FacetFate.Kept);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("TRUE")]
    public void A_boolean_default_that_is_not_true_or_false_is_refused_rather_than_saved_as_false(string typed)
        => Refusal(new FieldFacets { Name = "active", Type = FieldType.Boolean, Default = typed })
            .ShouldBe($"'{typed}' is not true or false, and this field's default has to be one.");

    [Theory]
    [InlineData(FieldType.Uuid, "not-a-uuid", "is not a uuid")]
    [InlineData(FieldType.Date, "yesterday", "is not a date")]
    public void A_default_the_type_cannot_parse_is_refused_as_the_apply_refuses_it(FieldType type, string typed, string says)
        => Refusal(new FieldFacets { Name = "value", Type = type, Default = typed }).ShouldContain(says);

    [Fact]
    public void An_enum_default_outside_its_values_is_refused()
        => Refusal(new FieldFacets { Name = "state", Type = FieldType.Enum, Values = "open, done", Default = "closed" })
            .ShouldContain("is not one of the values above");

    [Fact]
    public void A_string_default_longer_than_its_max_length_is_refused()
        => Refusal(new FieldFacets { Name = "code", MaxLength = 3, Default = "ABCD" })
            .ShouldBe("'ABCD' is 4 characters and the max length is 3.");

    [Fact]
    public void A_cel_default_is_not_put_in_the_box_and_is_not_rewritten_as_a_string()
    {
        const string declared = """{"type":"string","default":{"$cel":"now()"}}""";
        var editor = FieldFacets.Prefill("stamp", declared);

        editor.Default.ShouldBeEmpty();
        editor.DrawsDefault.ShouldBeFalse();
        editor.Build("stamp", declared, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("$cel");
    }

    [Fact]
    public void A_cel_default_can_be_removed_on_purpose()
    {
        const string declared = """{"type":"string","default":{"$cel":"now()"}}""";
        var editor = FieldFacets.Prefill("stamp", declared);
        editor.RemoveUndrawnDefault = true;

        editor.Build("stamp", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"string"}""");
    }

    [Fact]
    public void Required_beside_a_literal_read_only_is_refused_as_the_apply_refuses_it()
    {
        const string declared = """{"type":"string","maxLength":64,"readOnly":true}""";
        var editor = FieldFacets.Prefill("external_ref", declared);
        editor.Required = true;

        editor.Build("external_ref", declared, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("readOnly");
    }

    [Fact]
    public void Required_beside_read_only_with_a_literal_default_is_what_the_apply_accepts()
    {
        const string declared = """{"type":"string","maxLength":64,"readOnly":true}""";
        var editor = FieldFacets.Prefill("external_ref", declared);
        editor.Required = true;
        editor.Default = "none";

        editor.Build("external_ref", declared, [], out var refusal).ShouldNotBeNull();
        refusal.ShouldBeNull();
    }

    [Fact]
    public void Every_facet_the_editor_does_not_draw_is_named_as_kept()
    {
        var editor = FieldFacets.Prefill(
            "external_ref", """{"type":"string","maxLength":64,"readOnly":true,"description":"From the ERP"}""");

        editor.Notes().Select(note => (note.Facet, note.Fate))
            .ShouldBe([("readOnly", FacetFate.Kept), ("description", FacetFate.Kept)], ignoreOrder: true);
    }

    [Fact]
    public void A_format_is_named_as_removed_once_the_field_is_no_longer_a_string()
    {
        var editor = FieldFacets.Prefill("contact", """{"type":"string","format":"email"}""");
        editor.Type = FieldType.Text;

        editor.Notes().Single().ShouldBe(
            new FacetNote("format", "\"email\"", FacetFate.Removed, "belongs to a string, which this field no longer is."));
    }

    [Fact]
    public void A_new_ref_says_the_on_delete_it_writes()
        => new FieldFacets { Name = "bike", Type = FieldType.Ref, Target = "bikes" }.Notes()
            .Single().ShouldBe(new FacetNote(
                "onDelete", "\"restrict\"", FacetFate.Written,
                "the schema's default, written for a new ref — choosing another is #265."));

    private static JsonObject Built(FieldFacets editor)
    {
        var facets = editor.Build(null, null, [], out var refusal);
        refusal.ShouldBeNull();
        return facets.ShouldNotBeNull();
    }

    private static string Refusal(FieldFacets editor)
    {
        editor.Build(null, null, [], out var refusal).ShouldBeNull();
        return refusal.ShouldNotBeNull();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: compile errors — `FacetNote`, `FacetFate`, `Notes`, `UniqueOffered`, `DrawsDefault`, `RemoveUndrawnDefault` not found; `MaxLength = null` not assignable to `int`.

- [ ] **Step 3: Implement**

Create `src/MMLib.Alvo.Admin/Components/Schema/FacetNote.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>What a save does to a facet the field editor draws no control for.</summary>
internal enum FacetFate
{
    /// <summary>Written back exactly as declared.</summary>
    Kept,

    /// <summary>Dropped: it belongs to what the field no longer is, or the operator asked.</summary>
    Removed,

    /// <summary>Written for the operator with the schema's own default, with no control to choose another.</summary>
    Written,
}

/// <summary>One facet the editor does not draw, what the save does to it, and the sentence that says so.</summary>
/// <remarks>
/// It exists because "nothing below is destroyed by an edit" was false (docs/todo-admin.md §8c, 5c lead): a
/// facet without a control was either rewritten from a stale prefill or silently carried. Now each one is said.
/// </remarks>
/// <param name="Facet">The key, as the descriptor spells it.</param>
/// <param name="Value">Its value as JSON — as declared, or as written for <see cref="FacetFate.Written"/>.</param>
/// <param name="Fate">What the save does to it.</param>
/// <param name="Reason">Why, in the words the editor shows beside it.</param>
internal sealed record FacetNote(string Facet, string Value, FacetFate Fate, string Reason);
```

In `FieldFacets.cs`: make the class `internal sealed partial class FieldFacets`; delete `DefaultMaxLength`; replace `_typedFacets` and add the declared snapshot:

```csharp
    private const int DefaultPrecision = 10;
    private const int DefaultScale = 2;

    /// <summary>Every facet that belongs to exactly one type (the frozen schema's if/then rules).</summary>
    private static readonly string[] _typedFacets = ["maxLength", "format", "precision", "scale", "values", "entity", "onDelete"];

    /// <summary>The declaration the editor was opened on — empty for a new field. Never written to.</summary>
    private JsonObject _declared = [];

    /// <summary>The type it was declared with, so a facet of the old type can be told from one of the current.</summary>
    private FieldType _declaredType = FieldType.String;
```

Replace `MaxLength`:

```csharp
    /// <summary>A string's maximum length, or <see langword="null"/> for none.</summary>
    /// <remarks>
    /// <b>Absent is a declaration the editor must be able to keep.</b> The frozen schema makes <c>maxLength</c>
    /// optional (minimum 1) and the mapper leaves such a column unbounded (<c>DescriptorToSchemaMapper.cs:406</c>).
    /// This used to default to 120 and was always written, so opening Edit on an unbounded string — even to rename
    /// it — narrowed a column nobody asked to narrow (docs/todo-admin.md §8d item 15).
    /// </remarks>
    public int? MaxLength { get; set; }
```

Add, after `TakesUnique`:

```csharp
    /// <summary>Whether a declared default no box shows — a <c>$cel</c> object, or one no control draws — is dropped on save.</summary>
    public bool RemoveUndrawnDefault { get; set; }

    /// <summary>
    /// Whether the unique checkbox is drawn: for a type that takes one, and wherever the declaration already
    /// carries one.
    /// </summary>
    /// <remarks>
    /// The apply maps <c>unique</c> for every type (<c>DescriptorToSchemaMapper.cs:404</c>), so a declared
    /// <c>unique: true</c> on a ref is legal — and was unclearable, because the box was hidden and the prefilled
    /// value still written (§8d item 17). A control is owed for every facet the save writes.
    /// </remarks>
    public bool UniqueOffered => TakesUnique || Flag(_declared["unique"]);

    /// <summary>Whether the field's type or kind differs from the one it was declared with.</summary>
    private bool Retyped => Type != _declaredType;

    /// <summary>
    /// Whether the declaration carries a default no box can show, on the type it was declared for — a
    /// <c>$cel</c> object, or a literal on a type the editor draws no default for (json, a maintained value).
    /// </summary>
    public bool KeepsUndrawnDefault
        => !Retyped && _declared["default"] is { } declared && (declared is not JsonValue || !TakesADefault);

    /// <summary>Whether the default box is drawn: the type takes a literal, and nothing undrawn is being kept.</summary>
    public bool DrawsDefault => TakesADefault && !(KeepsUndrawnDefault && !RemoveUndrawnDefault);
```

Replace `Prefill` (and add the readers it uses):

```csharp
    /// <summary>The editor's values for a declared field, read from its facets.</summary>
    /// <param name="name">The field's name.</param>
    /// <param name="json">Its facets as the working copy carries them.</param>
    public static FieldFacets Prefill(string name, string? json)
    {
        var facets = Parse(json);
        var type = Enum.TryParse<FieldType>(Text(facets["type"]), ignoreCase: true, out var declared)
            ? declared : FieldType.String;

        return new FieldFacets
        {
            Name = name,
            Type = type,
            Required = Flag(facets["required"]),
            Unique = Flag(facets["unique"]),
            Indexed = Flag(facets["index"]),
            MaxLength = Whole(facets["maxLength"]),
            Precision = Whole(facets["precision"]) ?? DefaultPrecision,
            Scale = Whole(facets["scale"]) ?? DefaultScale,
            Target = Text(facets["entity"]),
            MaintainedElsewhere = facets["computed"] is not null || facets["rollup"] is not null,
            Default = LiteralText(facets["default"]),
            Values = facets["values"] is JsonArray values ? string.Join(", ", values.Select(Text)) : string.Empty,
            _declared = facets,
            _declaredType = type,
        };
    }

    /// <summary>
    /// A declared literal as the box shows it; a <c>$cel</c> object or an array is not one, and the box stays empty
    /// rather than holding the object's JSON — which a string field then saved as the literal <c>"{"$cel":…}"</c>.
    /// </summary>
    private static string LiteralText(JsonNode? declared) => declared switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        _ => string.Empty,
    };

    private static string Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static bool Flag(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var on) && on;

    private static int? Whole(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;

    /// <summary>A type as the descriptor spells it.</summary>
    internal static string Word(FieldType type) => type.ToString().ToLowerInvariant();
```

Replace `Build` and add `WriteSupplied`:

```csharp
    public JsonObject? Build(string? editing, string? editingJson, IReadOnlyList<string> siblings, out string? refusal)
    {
        refusal = RefuseName(editing, siblings);
        if (refusal is not null)
        {
            return null;
        }

        var facets = editing is { Length: > 0 } ? Parse(editingJson) : [];
        refusal = WriteSupplied(facets) ?? RefuseWhatTheApplyRefuses(facets);
        return refusal is null ? facets : null;
    }

    /// <summary>
    /// Writes a field a caller supplies. Every key that already exists is set in place, so an edit that changes
    /// nothing writes the declaration back unchanged — a staged row that says "changed" for an unchanged field is a
    /// second kind of lie about what the operator did.
    /// </summary>
    private string? WriteSupplied(JsonObject facets)
    {
        facets["type"] = Word(Type);
        Toggle(facets, "required", Required);
        if (WriteDefault(facets) is { } refused)
        {
            return refused;
        }

        if (UniqueOffered)
        {
            Toggle(facets, "unique", Unique);
        }

        Toggle(facets, "index", Indexed);
        ClearFacetsOfOtherTypes(facets);
        return WriteTypeFacets(facets);
    }
```

Replace `WriteDefault` and `TypedDefault`; add the rest of the default rules:

```csharp
    /// <summary>
    /// Writes the default the box holds, or does to a declared one what <see cref="DefaultFate"/> decides.
    /// </summary>
    /// <remarks>
    /// Typed here rather than sent as a string: the descriptor's <c>default</c> is a JSON literal and the apply
    /// refuses one its field cannot hold (<c>FieldDefault.cs:190-199</c>). A declared default no box draws is
    /// kept or removed — never rewritten from the box's prefill.
    /// </remarks>
    private string? WriteDefault(JsonObject facets)
    {
        if (DrawsDefault)
        {
            return WriteTypedDefault(facets);
        }

        if (DefaultFate() == FacetFate.Removed)
        {
            facets.Remove("default");
        }

        return null;
    }

    private string? WriteTypedDefault(JsonObject facets)
    {
        if (Default.Length == 0)
        {
            facets.Remove("default");
            return null;
        }

        if (TypedDefault() is not { } literal)
        {
            return DefaultRefusal();
        }

        facets["default"] = literal;
        return null;
    }

    /// <summary>
    /// What the save does to a declared default no box draws, or <see langword="null"/> when the box decides.
    /// </summary>
    private FacetFate? DefaultFate()
        => _declared["default"] is null || DrawsDefault ? null
            : KeepsUndrawnDefault && !RemoveUndrawnDefault ? FacetFate.Kept
            : FacetFate.Removed;

    /// <summary>The default as a literal of the field's type, or nothing when the apply would refuse it.</summary>
    /// <remarks>
    /// Each arm is <c>FieldDefault</c>'s own check (<c>Fits</c>, <c>Excluded</c>): a boolean is <c>true</c> or
    /// <c>false</c> and nothing else — anything else used to be saved as <c>false</c> — a uuid parses, a date
    /// parses, an enum default is one of its values, a string fits its max length.
    /// </remarks>
    private JsonNode? TypedDefault() => Type switch
    {
        FieldType.Boolean => Default switch { "true" => JsonValue.Create(true), "false" => JsonValue.Create(false), _ => null },
        FieldType.Integer => long.TryParse(Default, CultureInfo.InvariantCulture, out var whole) ? whole : null,
        FieldType.Decimal => decimal.TryParse(Default, CultureInfo.InvariantCulture, out var number) ? number : null,
        FieldType.Uuid => Guid.TryParse(Default, out _) ? Default : null,
        FieldType.Date or FieldType.DateTime
            => DateTimeOffset.TryParse(Default, CultureInfo.InvariantCulture, out _) ? Default : null,
        FieldType.Enum => SplitValues().Contains(Default, StringComparer.Ordinal) ? Default : null,
        _ => MaxLength is { } max && Default.Length > max ? null : Default,
    };

    /// <summary>Why <see cref="TypedDefault"/> found nothing, in the field's own terms.</summary>
    private string DefaultRefusal() => Type switch
    {
        FieldType.Integer or FieldType.Decimal => $"'{Default}' is not a number, and this field's default has to be one.",
        FieldType.Boolean => $"'{Default}' is not true or false, and this field's default has to be one.",
        FieldType.Uuid => $"'{Default}' is not a uuid, and this field's default has to be one.",
        FieldType.Date or FieldType.DateTime => $"'{Default}' is not a date, and this field's default has to be one.",
        FieldType.Enum => $"'{Default}' is not one of the values above, and this field's default has to be one.",
        _ => string.Create(
            CultureInfo.InvariantCulture, $"'{Default}' is {Default.Length} characters and the max length is {MaxLength}."),
    };
```

Replace `ClearFacetsOfOtherTypes` and the string arm of `WriteTypeFacets`:

```csharp
    /// <summary>Drops the facets that belong to a type this field no longer has, and only those.</summary>
    /// <remarks>
    /// The current type's own facets are left where they stand and set in place by <see cref="WriteTypeFacets"/>;
    /// removing and re-adding them moved them to the end of the object and badged an untouched field "changed".
    /// </remarks>
    private void ClearFacetsOfOtherTypes(JsonObject facets)
    {
        foreach (var facet in _typedFacets.Where(facet => OwnerOf(facet) != Type))
        {
            facets.Remove(facet);
        }
    }

    /// <summary>The one type a type-bound facet belongs to.</summary>
    private static FieldType OwnerOf(string facet) => facet switch
    {
        "maxLength" or "format" => FieldType.String,
        "precision" or "scale" => FieldType.Decimal,
        "values" => FieldType.Enum,
        _ => FieldType.Ref,
    };
```

```csharp
            case FieldType.String:
                return WriteMaxLength(facets);
```

```csharp
    private string? WriteMaxLength(JsonObject facets)
    {
        if (MaxLength is not { } max)
        {
            facets.Remove("maxLength");
            return null;
        }

        if (max < 1)
        {
            return "A max length is at least 1 — the schema's minimum. Leave the box empty for no limit.";
        }

        facets["maxLength"] = max;
        return null;
    }
```

Add the refusals of what the *composed* declaration still carries:

```csharp
    private const string CelDefaultRefusal =
        "Its declared default is a '$cel' expression, which this build refuses at apply (field.default). Tick "
        + "\"Remove the declared default\" to save the field, or declare a literal.";

    private const string MaintainedDefaultRefusal =
        "It declares a default beside a value that is maintained for it, which the apply refuses — that value can "
        + "never fall back to a default. Tick \"Remove the declared default\" to save the field.";

    private const string RequiredReadOnlyRefusal =
        "This field is readOnly: true, so no create could ever supply it, and the apply refuses 'required' beside "
        + "it. Give it a default, or leave required off.";

    /// <summary>What the apply would still refuse about the built declaration, checked on the result itself.</summary>
    private string? RefuseWhatTheApplyRefuses(JsonObject facets)
        => RefuseAKeptDefault(facets) ?? RefuseRequiredBesideReadOnly(facets);

    /// <summary>A kept default the apply refuses: <c>$cel</c> (<c>UnhonouredFeatures.cs:63</c>) or beside a maintained value (<c>FieldDefault.cs:88</c>).</summary>
    private string? RefuseAKeptDefault(JsonObject facets)
    {
        if (DefaultFate() != FacetFate.Kept || facets["default"] is not { } kept)
        {
            return null;
        }

        return IsCel(kept) ? CelDefaultRefusal : MaintainedElsewhere ? MaintainedDefaultRefusal : null;
    }

    /// <summary><c>DescriptorValidator.IsRequiredAndStaticallyReadOnly</c> (<c>DescriptorValidator.cs:504-521</c>), asked of the result.</summary>
    private static string? RefuseRequiredBesideReadOnly(JsonObject facets)
        => Flag(facets["required"]) && Flag(facets["readOnly"]) && (facets["default"] is null || IsCel(facets["default"]))
            ? RequiredReadOnlyRefusal
            : null;

    private static bool IsCel(JsonNode? node) => node is JsonObject tagged && tagged.ContainsKey("$cel");
```

Create `src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.Notes.cs`:

```csharp
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* What the editor says about the facets it draws no control for (docs/todo-admin.md §8d item 17). */
internal sealed partial class FieldFacets
{
    /// <summary>The keys a control draws; every other key a declaration carries becomes a note.</summary>
    private static readonly HashSet<string> _drawn = new(StringComparer.Ordinal)
    {
        "type", "required", "unique", "index", "maxLength", "precision", "scale", "values", "entity", "default",
    };

    /// <summary>Every declared facet this editor does not draw, with what the save does to it.</summary>
    public IReadOnlyList<FacetNote> Notes()
    {
        List<FacetNote> notes = [.. _declared.Where(pair => !_drawn.Contains(pair.Key)).Select(pair => Undrawn(pair.Key, pair.Value))];

        if (DefaultNote() is { } defaulted)
        {
            notes.Insert(0, defaulted);
        }

        if (Type == FieldType.Ref && !_declared.ContainsKey("onDelete"))
        {
            notes.Add(new("onDelete", "\"restrict\"", FacetFate.Written,
                "the schema's default, written for a new ref — choosing another is #265."));
        }

        return notes;
    }

    /// <summary>One undrawn facet: removed when it belongs to a type the field no longer has, else kept.</summary>
    private FacetNote Undrawn(string facet, JsonNode? value)
    {
        var json = value?.ToJsonString() ?? "null";

        return _typedFacets.Contains(facet) && OwnerOf(facet) != Type
            ? new(facet, json, FacetFate.Removed, $"belongs to a {Word(OwnerOf(facet))}, which this field no longer is.")
            : new(facet, json, FacetFate.Kept, "no control here — kept exactly as declared.");
    }

    /// <summary>The declared default, when no box decides it.</summary>
    private FacetNote? DefaultNote()
    {
        if (_declared["default"] is not { } declared || DefaultFate() is not { } fate)
        {
            return null;
        }

        var reason = fate == FacetFate.Kept ? KeptDefaultReason(declared) : RemovedDefaultReason();
        return new("default", declared.ToJsonString(), fate, reason);
    }

    private string KeptDefaultReason(JsonNode declared)
        => IsCel(declared) ? "a '$cel' expression, which this build refuses at apply — tick \"Remove the declared default\" to save the field."
            : MaintainedElsewhere ? "beside a value that is maintained for it, which the apply refuses — tick \"Remove the declared default\" to save the field."
            : $"no control draws a {Word(Type)} default — kept exactly as declared.";

    private string RemovedDefaultReason()
        => RemoveUndrawnDefault ? "removed, as asked."
            : $"a {Word(Type)} takes no default here, so it does not follow the field into its new type.";
}
```

In `FieldEditor.razor.cs` add:

```csharp
    /// <summary>A number box's value, or <see langword="null"/> when it was cleared — which is how "no limit" is said.</summary>
    private static int? OptionalNumber(object? value)
        => int.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
```

In `FieldEditor.razor`:

1. The unique checkbox: `@if (_facets.TakesUnique)` → `@if (_facets.UniqueOffered)`; the indexed checkbox guard `@if (!_facets.Unique)` → `@if (!(_facets.UniqueOffered && _facets.Unique))`; the redundancy hint guard `@if (_facets.Unique && _facets.Indexed)` → `@if (_facets.UniqueOffered && _facets.Unique && _facets.Indexed)`.
2. The max length field:

```razor
            case FieldType.String:
                <Field Label="Max length" For="new-field-max">
                    <ChildContent>
                        <input class="a-input" id="new-field-max" type="number" min="1" value="@_facets.MaxLength"
                               placeholder="no limit" @oninput="args => _facets.MaxLength = OptionalNumber(args.Value)" />
                    </ChildContent>
                    <Hint>Leave it empty for no limit — the schema's own default.</Hint>
                </Field>
                break;
```

3. The default: `@if (_facets.TakesADefault)` → `@if (_facets.DrawsDefault)`; directly after that block add:

```razor
        @if (_facets.KeepsUndrawnDefault)
        {
            <label class="a-check">
                <input type="checkbox" checked="@_facets.RemoveUndrawnDefault" data-testid="field-remove-default"
                       @onchange="args => _facets.RemoveUndrawnDefault = args.Value is true" />
                <span>Remove the declared default</span>
            </label>
        }

        @* Every facet this editor draws no control for, and what the save does to it. Before this, such a facet was
           either rewritten from a stale prefill or carried without a word (docs/todo-admin.md §8d item 17). *@
        @if (_facets.Notes() is { Count: > 0 } notes)
        {
            <div class="a-field" data-testid="field-notes">
                <span class="a-label">Not drawn here</span>
                @foreach (var note in notes)
                {
                    <span class="a-hint" data-testid="@($"field-note-{note.Facet}")">
                        <code class="a-mono">@note.Facet</code> <code class="a-mono">@Short(note.Value)</code> — @note.Reason
                    </span>
                }
            </div>
        }
```

and in `FieldEditor.razor.cs`:

```csharp
    private const int NoteValueLimit = 40;

    /// <summary>A declared value short enough to sit in a line of prose; a description can be a paragraph.</summary>
    private static string Short(string json) => json.Length <= NoteValueLimit ? json : json[..NoteValueLimit] + "…";
```

- [ ] **Step 4: Run the unit tests**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: PASS (all `FieldFacetsTests` and `FieldFacetsWriteRulesTests`; the `PublicApi` approval unchanged).

- [ ] **Step 5: Write the e2e scenarios**

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/FieldFacetScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field editor writes nothing nobody chose, and shows what it draws no control for.
/// </summary>
/// <remarks>
/// Its own world, for <c>FieldDefaultScenarios</c>' reason: a working copy is held per operator and these stage
/// edits. It stops before the preview — what is under test is what the editor composes.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class FieldFacetScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>A new string is unbounded unless a limit is typed — no silent <c>maxLength: 120</c> (§8d item 15).</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_string_field_is_unbounded_unless_a_max_length_is_typed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("dispatch_zone");
        (await sheet.GetByRole(AriaRole.Spinbutton, new() { Name = "Max length" }).InputValueAsync()).ShouldBeEmpty();
        await session.Page.GetByTestId("field-save").ClickAsync();

        var row = session.Page.GetByTestId("field-row-dispatch_zone");
        await row.WaitForAsync();
        (await row.InnerTextAsync()).ShouldNotContain("max");

        session.AssertConsoleClean();
    }

    /// <summary>
    /// <c>work_orders.external_ref</c> is <c>readOnly: true</c>: the editor says so, and refuses <c>required</c>
    /// beside it as the apply would (<c>DescriptorValidator.cs:504</c>).
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_read_only_facet_is_shown_and_required_beside_it_is_refused_in_the_editor()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("edit-field-external_ref").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        (await sheet.GetByTestId("field-note-readOnly").InnerTextAsync()).ShouldContain("kept exactly as declared");

        await sheet.GetByRole(AriaRole.Checkbox, new() { Name = "required", Exact = true }).CheckAsync();
        await session.Page.GetByTestId("field-save").ClickAsync();

        (await sheet.GetByTestId("error-panel").InnerTextAsync()).ShouldContain("readOnly");
        (await session.Page.GetByTestId("staged-external_ref").CountAsync()).ShouldBe(0, "a refused edit stages nothing");

        session.AssertConsoleClean();
    }
}
```

- [ ] **Step 6: Run the gates**

Run: `scripts/test-admin-e2e` then `scripts/test-ring1`
Expected: both green (the existing `FieldDefaultScenarios`, `SchemaEditingScenarios` and `RenameScenarios` still pass: they type a max length or edit a field that declares one).

- [ ] **Step 7: Mark the items done** — in `docs/todo-admin.md` §8d:
  - item 15: `15. ✅ **Opening Edit …` and append `**Done:** `maxLength` is optional in the editor — prefilled from the declaration, written only when set, clearable; a new string is unbounded unless a limit is typed.`
  - item 17: `17. ✅ **Hidden controls still write.** …` and append `**Done:** `FieldFacets` keeps or removes every undrawn facet and says which ("Not drawn here"); unique is offered wherever it is declared; a default never follows a retype into ref/json; boolean/uuid/date/enum/length defaults are refused as `FieldDefault` refuses them; a `$cel` default is kept and refused until removed; required beside `readOnly: true` is refused as `DescriptorValidator` refuses it.`

- [ ] **Step 8: Normalise and commit** — run the Global Constraints normaliser with the eight `.cs`/`.razor` paths below as its arguments, then:

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/FacetNote.cs src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.cs src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.Notes.cs src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor.cs test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsTests.cs test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsWriteRulesTests.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/FieldFacetScenarios.cs docs/todo-admin.md
git commit -m "fix(f5): the field editor writes only the facets the operator chose

maxLength is optional and clearable instead of a silent 120; a facet no
control draws is kept and shown, or removed and said; defaults are typed
and refused exactly as FieldDefault refuses them; required beside a
literal readOnly is refused as DescriptorValidator refuses it.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---

### Task 2: A *maintained by Alvo* kind — rollup and computed (item 14, and item 22's rollup/computed part)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/FieldKind.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/RollupSources.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.Maintained.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.cs` (`Build` dispatch, `WriteSupplied`, `Prefill`, `Retyped`, `UniqueOffered`; delete the settable `MaintainedElsewhere`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.Notes.cs` (`_drawn`, withheld notes)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor(.cs)` (kind chips, rollup and computed blocks, cascaded sources)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/PendingSchema.cs` (read `computed` and `rollup`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor` (wrap `<FieldEditor>` in a `CascadingValue`) — shared file, only these lines
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor.cs` (one field, one line in `ReadWorking`) — shared file, only these lines
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/RollupSourcesTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsMaintainedTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/PendingSchemaTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/MaintainedFieldScenarios.cs`
- Modify: `docs/todo-admin.md` §8d item 14, and §7's closing paragraph

**Interfaces:**
- Consumes (Task 1): `FacetNote`, `FacetFate`, `FieldFacets.Word`, `DefaultFate`, `WriteDefault`, `ClearFacetsOfOtherTypes`, `WriteTypeFacets`, `Toggle`, `Text`, `Flag`, `_declared`.
- Produces:

```csharp
internal enum FieldKind { Supplied, Rollup, Computed }

/// Numbers: the child's integer and decimal fields, in declaration order.
internal sealed record RollupChildField(string Name, FieldType Type, int? Precision, int? Scale);
/// Via: the child's ref fields that point at the parent. Refusal: why the apply would refuse a rollup from it, or null.
internal sealed record RollupSource(string Entity, IReadOnlyList<string> Via, IReadOnlyList<RollupChildField> Numbers, string? Refusal);

internal static class RollupSources
{
    public static IReadOnlyList<RollupSource> For(string descriptorJson, string parent);
}

internal sealed partial class FieldFacets
{
    public static IReadOnlyList<string> RollupOps { get; }          // ["sum","count","avg","min","max"]
    public static IReadOnlyList<FieldType> ComputedTypes { get; }   // string, text, integer, decimal, boolean, date, datetime
    public FieldKind Kind { get; set; }
    public string RollupFrom { get; set; }
    public string RollupOp { get; set; }                            // default "count"
    public string RollupField { get; set; }
    public string RollupVia { get; set; }
    public bool RemoveRollupFilter { get; set; }
    public string Computed { get; set; }
    public IReadOnlyList<RollupSource> Sources { get; set; }
    public bool MaintainedElsewhere { get; }                        // Kind != Supplied  (was settable)
    public bool RequiredOffered { get; }                            // Kind == Supplied
    public RollupSource? Source { get; }
    public IReadOnlyList<RollupChildField> Aggregatable { get; }
    public FieldType DerivedType { get; }
    public bool DeclaresRollupFilter { get; }
}
```

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Admin.Tests/Schema/RollupSourcesTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The entities a rollup may aggregate, read from the working copy, with the ones the apply refuses said
/// (<c>RollupResolver</c>: a child that references the parent, is physical, and agrees about tenancy).
/// </summary>
public class RollupSourcesTests
{
    [Fact]
    public void Every_entity_with_a_ref_here_is_a_source_with_its_refs_and_number_fields()
    {
        var sources = RollupSources.For(Descriptor, "customers");

        sources.Select(source => source.Entity).ShouldBe(["orders", "follows", "drafts"]);
        var orders = sources[0];
        orders.Via.ShouldBe(["customer_id"]);
        orders.Numbers.Select(field => field.Name).ShouldBe(["priority", "total"]);
        orders.Numbers[1].ShouldBe(new RollupChildField("total", FieldType.Decimal, 10, 2));
        orders.Refusal.ShouldBeNull();
        sources[1].Via.ShouldBe(["follower", "followee"]);
    }

    [Fact]
    public void A_dynamic_child_is_listed_as_refused()
        => RollupSources.For(Descriptor, "customers").Single(source => source.Entity == "drafts").Refusal
            .ShouldNotBeNull().ShouldContain("dynamic");

    /// <summary><c>regions</c> is global and <c>orders</c> resolves scoped (<c>tenancy.enabled</c>), so the pair crosses.</summary>
    [Fact]
    public void A_child_whose_tenancy_disagrees_is_listed_as_refused()
        => RollupSources.For(Descriptor, "regions").Single().Refusal.ShouldNotBeNull().ShouldContain("tenancy");

    [Theory]
    [InlineData("orders")]
    [InlineData("missing")]
    public void An_entity_nothing_points_at_has_no_source(string parent)
        => RollupSources.For(Descriptor, parent).ShouldBeEmpty();

    [Fact]
    public void A_document_that_is_not_json_has_no_source()
        => RollupSources.For("{not json", "customers").ShouldBeEmpty();

    private const string Descriptor = """
        {
          "tenancy": { "enabled": true },
          "entities": {
            "customers": { "fields": { "name": { "type": "string" } } },
            "regions": { "tenancy": "global", "fields": { "code": { "type": "string" } } },
            "orders": { "fields": {
              "customer_id": { "type": "ref", "entity": "customers" },
              "region_id": { "type": "ref", "entity": "regions" },
              "priority": { "type": "integer" },
              "total": { "type": "decimal", "precision": 10, "scale": 2 },
              "note": { "type": "string" } } },
            "follows": { "fields": {
              "follower": { "type": "ref", "entity": "customers" },
              "followee": { "type": "ref", "entity": "customers" } } },
            "drafts": { "storage": "dynamic", "fields": { "customer_id": { "type": "ref", "entity": "customers" } } }
          }
        }
        """;
}
```

Create `test/MMLib.Alvo.Admin.Tests/Schema/FieldFacetsMaintainedTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A rollup and a computed field in the editor: composed only as the apply accepts them (<c>RollupResolver</c>,
/// <c>ComputedColumnSql</c>), opened in their own kind, never as a plain integer (docs/todo-admin.md §7).
/// </summary>
public class FieldFacetsMaintainedTests
{
    private static readonly RollupSource _orders = new(
        "orders", ["customer_id"],
        [new("priority", FieldType.Integer, null, null), new("total", FieldType.Decimal, 10, 2)], null);

    private static readonly RollupSource _follows = new("follows", ["follower", "followee"], [], null);

    [Fact]
    public void A_count_rollup_is_an_integer_and_names_no_field()
        => Built(Rollup("orders", "count")).ToJsonString()
            .ShouldBe("""{"type":"integer","rollup":{"from":"orders","op":"count"}}""");

    [Fact]
    public void A_sum_takes_the_child_fields_type_precision_and_scale()
    {
        var editor = Rollup("orders", "sum");
        editor.RollupField = "total";

        Built(editor).ToJsonString().ShouldBe(
            """{"type":"decimal","precision":10,"scale":2,"rollup":{"from":"orders","op":"sum","field":"total"}}""");
    }

    [Fact]
    public void An_avg_is_offered_over_decimal_fields_only()
        => Rollup("orders", "avg").Aggregatable.Select(field => field.Name).ShouldBe(["total"]);

    [Fact]
    public void A_sum_with_no_field_is_refused_as_the_apply_refuses_it()
        => Refusal(Rollup("orders", "sum")).ShouldContain("pick one");

    [Fact]
    public void A_child_with_two_refs_here_needs_the_one_to_follow()
    {
        var editor = Rollup("follows", "count");
        Refusal(editor).ShouldContain("follower, followee");

        editor.RollupVia = "followee";
        Built(editor)["rollup"]!["via"]!.GetValue<string>().ShouldBe("followee");
    }

    [Fact]
    public void A_source_the_apply_refuses_is_refused_with_its_reason()
    {
        var editor = new FieldFacets
        {
            Name = "drafts_count", Kind = FieldKind.Rollup, RollupFrom = "drafts",
            Sources = [new("drafts", ["customer_id"], [], "drafts is a dynamic entity …")],
        };

        Refusal(editor).ShouldBe("drafts is a dynamic entity …");
    }

    [Fact]
    public void An_existing_rollup_opens_as_a_rollup_and_is_written_back_unchanged()
    {
        const string declared = """{"type":"integer","description":"How many bikes","rollup":{"from":"bikes","op":"count"}}""";
        var editor = FieldFacets.Prefill("bikes_count", declared);
        editor.Sources = [new("bikes", ["customer_id"], [], null)];

        editor.Kind.ShouldBe(FieldKind.Rollup);
        editor.RollupFrom.ShouldBe("bikes");
        editor.RequiredOffered.ShouldBeFalse();
        editor.UniqueOffered.ShouldBeFalse();
        editor.Build("bikes_count", declared, [], out _)!.ToJsonString().ShouldBe(declared);
    }

    [Fact]
    public void A_declared_filter_is_refused_until_it_is_removed()
    {
        const string declared = """{"type":"integer","rollup":{"from":"orders","op":"count","where":"priority > 1"}}""";
        var editor = FieldFacets.Prefill("urgent", declared);
        editor.Sources = [_orders];

        editor.DeclaresRollupFilter.ShouldBeTrue();
        editor.Build("urgent", declared, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("rollup.where");

        editor.RemoveRollupFilter = true;
        editor.Build("urgent", declared, [], out _)!["rollup"]!.AsObject().ContainsKey("where").ShouldBeFalse();
    }

    [Fact]
    public void Required_and_unique_on_a_rollup_are_carried_and_said_not_offered()
    {
        const string declared = """{"type":"integer","required":true,"unique":true,"rollup":{"from":"orders","op":"count"}}""";
        var editor = FieldFacets.Prefill("orders_count", declared);
        editor.Sources = [_orders];

        var facets = editor.Build("orders_count", declared, [], out _)!;
        facets["required"]!.GetValue<bool>().ShouldBeTrue();
        editor.Notes().Where(note => note.Fate == FacetFate.Kept).Select(note => note.Facet)
            .ShouldBe(["required", "unique"], ignoreOrder: true);
    }

    [Fact]
    public void Switching_a_supplied_string_to_a_rollup_drops_its_default_and_string_facets()
    {
        const string declared = """{"type":"string","maxLength":40,"default":"none"}""";
        var editor = FieldFacets.Prefill("orders_count", declared);
        editor.Sources = [_orders];
        editor.Kind = FieldKind.Rollup;
        editor.RollupFrom = "orders";

        editor.Build("orders_count", declared, [], out _)!.ToJsonString()
            .ShouldBe("""{"type":"integer","rollup":{"from":"orders","op":"count"}}""");
        editor.Notes().ShouldContain(note => note.Facet == "default" && note.Fate == FacetFate.Removed);
    }

    [Fact]
    public void A_field_declaring_both_rollup_and_computed_opens_as_a_rollup_and_loses_the_computed()
    {
        const string declared = """{"type":"integer","computed":"a + b","rollup":{"from":"orders","op":"count"}}""";
        var editor = FieldFacets.Prefill("both", declared);
        editor.Sources = [_orders];

        editor.Build("both", declared, [], out _)!.ContainsKey("computed").ShouldBeFalse();
    }

    [Fact]
    public void A_computed_field_carries_its_expression_and_chosen_type()
        => Built(new FieldFacets { Name = "twice", Kind = FieldKind.Computed, Type = FieldType.Integer, Computed = " priority + priority " })
            .ToJsonString().ShouldBe("""{"type":"integer","computed":"priority + priority"}""");

    [Fact]
    public void A_computed_field_with_no_expression_is_refused()
        => Refusal(new FieldFacets { Name = "twice", Kind = FieldKind.Computed, Type = FieldType.Integer })
            .ShouldContain("needs its expression");

    [Fact]
    public void A_computed_field_of_a_type_it_is_not_offered_as_is_refused()
        => Refusal(new FieldFacets { Name = "twice", Kind = FieldKind.Computed, Type = FieldType.Json, Computed = "a" })
            .ShouldContain("generated column");

    [Fact]
    public void An_existing_computed_field_opens_as_computed_and_is_written_back_unchanged()
    {
        const string declared = """{"type":"decimal","description":"Hours times rate","precision":10,"scale":2,"computed":"labour_hours * labour_rate"}""";
        var editor = FieldFacets.Prefill("labour_total", declared);

        editor.Kind.ShouldBe(FieldKind.Computed);
        editor.Computed.ShouldBe("labour_hours * labour_rate");
        editor.Build("labour_total", declared, [], out _)!.ToJsonString().ShouldBe(declared);
    }

    [Fact]
    public void Switching_a_computed_field_back_to_supplied_drops_the_expression()
    {
        const string declared = """{"type":"integer","computed":"a + b"}""";
        var editor = FieldFacets.Prefill("sum", declared);
        editor.Kind = FieldKind.Supplied;

        editor.Build("sum", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"integer"}""");
    }

    private static FieldFacets Rollup(string from, string op) => new()
    {
        Name = "value", Kind = FieldKind.Rollup, RollupFrom = from, RollupOp = op, Sources = [_orders, _follows],
    };

    private static JsonObject Built(FieldFacets editor)
    {
        var facets = editor.Build(null, null, [], out var refusal);
        refusal.ShouldBeNull();
        return facets.ShouldNotBeNull();
    }

    private static string Refusal(FieldFacets editor)
    {
        editor.Build(null, null, [], out var refusal).ShouldBeNull();
        return refusal.ShouldNotBeNull();
    }
}
```

Create `test/MMLib.Alvo.Admin.Tests/Schema/PendingSchemaTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>A staged field is drawn with the facets an applied one would be (docs/todo-admin.md §8d item 22).</summary>
public class PendingSchemaTests
{
    [Fact]
    public void A_staged_rollup_and_computed_keep_what_makes_them_one()
    {
        const string json = """
            {"entities":{"orders":{"fields":{
              "lines_count":{"type":"integer","rollup":{"from":"lines","op":"count"}},
              "twice":{"type":"integer","computed":"a + a"}}}}}
            """;
        var fields = PendingSchema.Read(json, "orders")!.Fields;

        fields[0].Rollup.ShouldNotBeNull().From.ShouldBe("lines");
        fields[0].Rollup!.Op.ShouldBe(RollupOperation.Count);
        fields[1].ComputedExpression.ShouldBe("a + a");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: compile errors — `RollupSources`, `RollupSource`, `RollupChildField`, `FieldKind`, `Kind`, `Sources` not found; `MaintainedElsewhere` not settable is fine (no test sets it).

- [ ] **Step 3: Implement the model**

Create `src/MMLib.Alvo.Admin/Components/Schema/FieldKind.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>Where a field's value comes from — chosen instead of a type rather than beside one (docs/todo-admin.md §7).</summary>
internal enum FieldKind
{
    /// <summary>A caller writes it, within its type and constraints.</summary>
    Supplied,

    /// <summary>Alvo maintains it from the rows of an entity that points here (<c>rollup</c>).</summary>
    Rollup,

    /// <summary>The database computes it from this row's own fields (<c>computed</c>).</summary>
    Computed,
}
```

Create `src/MMLib.Alvo.Admin/Components/Schema/RollupSources.cs`:

```csharp
using MMLib.Alvo.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One number field of a child entity — what <c>sum</c>, <c>avg</c>, <c>min</c> and <c>max</c> can aggregate.</summary>
/// <param name="Name">The child field.</param>
/// <param name="Type">Integer or decimal.</param>
/// <param name="Precision">A decimal's precision, as declared.</param>
/// <param name="Scale">A decimal's scale, as declared.</param>
internal sealed record RollupChildField(string Name, FieldType Type, int? Precision, int? Scale);

/// <summary>An entity a rollup on the parent could aggregate.</summary>
/// <param name="Entity">The child entity.</param>
/// <param name="Via">Its ref fields that point at the parent, in declaration order.</param>
/// <param name="Numbers">Its integer and decimal fields, in declaration order.</param>
/// <param name="Refusal">Why the apply would refuse a rollup from it, or <see langword="null"/>.</param>
internal sealed record RollupSource(
    string Entity, IReadOnlyList<string> Via, IReadOnlyList<RollupChildField> Numbers, string? Refusal);

/// <summary>
/// The entities whose rows a rollup on <c>parent</c> could aggregate, read from the <b>working copy</b> — so an
/// entity staged a moment ago is offered too — with the ones the apply refuses kept in the list and said.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusals are <c>RollupResolver</c>'s own</b>: a child must reference the parent
/// (<c>RollupResolver.cs:252-279</c>), be physical (<c>:218-227</c>), and agree with it about tenancy, where an
/// entity's tenancy is its declared one or <c>scoped</c> when the project turns tenancy on (<c>:153-172</c>, via
/// <c>DescriptorToSchemaMapper.ResolveTenancy</c>). An entity with no ref here is not a source at all.
/// </para>
/// <para>
/// Read as JSON (the <c>DescriptorLens</c> rule): an unreadable document is no sources, never a throw.
/// </para>
/// </remarks>
internal static class RollupSources
{
    /// <summary>The sources, in the descriptor's own entity order.</summary>
    /// <param name="descriptorJson">The working descriptor.</param>
    /// <param name="parent">The entity the rollup field would be on.</param>
    public static IReadOnlyList<RollupSource> For(string descriptorJson, string parent)
    {
        if (Parse(descriptorJson) is not { } root || root["entities"] is not JsonObject entities
            || entities[parent] is not JsonObject declaring)
        {
            return [];
        }

        var enabled = root["tenancy"]?["enabled"] is JsonValue value && value.TryGetValue<bool>(out var on) && on;

        return [.. entities
            .Where(pair => pair.Value is JsonObject)
            .Select(pair => Source(pair.Key, (JsonObject)pair.Value!, parent, declaring, enabled))
            .OfType<RollupSource>()];
    }

    private static RollupSource? Source(string name, JsonObject child, string parent, JsonObject declaring, bool enabled)
    {
        var fields = child["fields"] as JsonObject ?? [];
        List<string> via = [.. fields
            .Where(field => Is(field.Value?["type"], "ref") && Is(field.Value?["entity"], parent))
            .Select(field => field.Key)];

        return via.Count == 0 ? null : new RollupSource(name, via, Numbers(fields), Refusal(name, child, parent, declaring, enabled));
    }

    private static List<RollupChildField> Numbers(JsonObject fields)
        => [.. fields
            .Where(field => Is(field.Value?["type"], "integer") || Is(field.Value?["type"], "decimal"))
            .Select(field => new RollupChildField(
                field.Key,
                Is(field.Value?["type"], "decimal") ? FieldType.Decimal : FieldType.Integer,
                Whole(field.Value?["precision"]),
                Whole(field.Value?["scale"])))];

    private static string? Refusal(string name, JsonObject child, string parent, JsonObject declaring, bool enabled)
    {
        if (Is(child["storage"], "dynamic"))
        {
            return $"{name} is a dynamic entity, which is not part of the applied schema — nothing would maintain this rollup.";
        }

        return Scoped(child, enabled) == Scoped(declaring, enabled)
            ? null
            : $"{parent} is {Word(Scoped(declaring, enabled))} and {name} is {Word(Scoped(child, enabled))}: the apply "
                + "refuses a rollup across tenancy, because one tenant's number would be computed from another's rows.";
    }

    private static bool Scoped(JsonObject entity, bool enabled)
        => entity["tenancy"] is JsonValue value && value.TryGetValue<string>(out var declared)
            ? declared == "scoped"
            : enabled;

    private static string Word(bool scoped) => scoped ? "scoped" : "global";

    private static bool Is(JsonNode? node, string expected)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && text == expected;

    private static int? Whole(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

Create `src/MMLib.Alvo.Admin/Components/Schema/FieldFacets.Maintained.cs`:

```csharp
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The two kinds whose value is maintained for the field — a rollup (RollupResolver) and a computed column
   (ComputedColumnSql) — and exactly what the apply accepts of each. */
internal sealed partial class FieldFacets
{
    private const int CelMaxLength = 2000;

    /// <summary>The frozen schema's <c>rollup.op</c> values, in its own order.</summary>
    public static IReadOnlyList<string> RollupOps { get; } = ["sum", "count", "avg", "min", "max"];

    /// <summary>The types a computed column is offered as.</summary>
    /// <remarks>
    /// The scalar ones. The type is chosen rather than derived because the dashboard has no CEL compiler
    /// (<c>RulesTab.razor</c> gives the reason); a ref, an enum, a json or a uuid generated column is withheld
    /// as the conservative choice — unverified whether the migrator refuses one.
    /// </remarks>
    public static IReadOnlyList<FieldType> ComputedTypes { get; } =
        [FieldType.String, FieldType.Text, FieldType.Integer, FieldType.Decimal, FieldType.Boolean, FieldType.Date, FieldType.DateTime];

    private static readonly string[] _withheld = ["required", "unique"];

    private FieldKind _declaredKind;

    /// <summary>Where the value comes from.</summary>
    public FieldKind Kind { get; set; }

    /// <summary>The child entity a rollup aggregates.</summary>
    public string RollupFrom { get; set; } = string.Empty;

    /// <summary>The rollup's operation; one of <see cref="RollupOps"/>.</summary>
    public string RollupOp { get; set; } = "count";

    /// <summary>The child field aggregated — for every op but <c>count</c>.</summary>
    public string RollupField { get; set; } = string.Empty;

    /// <summary>The child's ref it follows, when it has more than one here.</summary>
    public string RollupVia { get; set; } = string.Empty;

    /// <summary>Whether a declared <c>rollup.where</c> — refused at apply — is dropped on save.</summary>
    public bool RemoveRollupFilter { get; set; }

    /// <summary>A computed field's CEL.</summary>
    public string Computed { get; set; } = string.Empty;

    /// <summary>The entities that could be rolled up, from <see cref="RollupSources.For"/>.</summary>
    public IReadOnlyList<RollupSource> Sources { get; set; } = [];

    /// <summary>Whether the value is maintained for the field, so a caller never writes it.</summary>
    public bool MaintainedElsewhere => Kind != FieldKind.Supplied;

    /// <summary>Whether <c>required</c> is drawn — a maintained value is never supplied by a caller.</summary>
    public bool RequiredOffered => Kind == FieldKind.Supplied;

    /// <summary>The chosen source, when it is one of <see cref="Sources"/>.</summary>
    public RollupSource? Source => Sources.FirstOrDefault(source => source.Entity == RollupFrom);

    /// <summary>The child fields the current op can aggregate: none for <c>count</c>, decimals only for <c>avg</c>.</summary>
    public IReadOnlyList<RollupChildField> Aggregatable => Source is not { } source || RollupOp == "count"
        ? []
        : [.. source.Numbers.Where(field => RollupOp != "avg" || field.Type == FieldType.Decimal)];

    /// <summary>The type a rollup is stored as: a count is whole, anything else takes the aggregated field's type.</summary>
    public FieldType DerivedType => Aggregatable.FirstOrDefault(field => field.Name == RollupField)?.Type ?? FieldType.Integer;

    /// <summary>Whether the declaration carries a <c>rollup.where</c>.</summary>
    public bool DeclaresRollupFilter => _declared["rollup"]?["where"] is not null;

    /// <summary>Reads the rollup or the computed expression a declaration carries, and so its kind.</summary>
    private void ReadMaintained(JsonObject facets)
    {
        if (facets["rollup"] is JsonObject rollup)
        {
            Kind = FieldKind.Rollup;
            RollupFrom = Text(rollup["from"]);
            RollupOp = Text(rollup["op"]) is { Length: > 0 } op ? op : "count";
            RollupField = Text(rollup["field"]);
            RollupVia = Text(rollup["via"]);
        }
        else if (facets["computed"] is JsonValue)
        {
            Kind = FieldKind.Computed;
            Computed = Text(facets["computed"]);
        }

        _declaredKind = Kind;
    }

    /// <summary>Writes a rollup, in place where the declaration already has one.</summary>
    private string? WriteRollup(JsonObject facets)
    {
        if (RefuseRollup() is { } refusal)
        {
            return refusal;
        }

        WriteDerivedType(facets);
        facets.Remove("computed");
        WriteRollupObject(RollupObject(facets), Source!);
        Toggle(facets, "index", Indexed);
        return WriteDefault(facets);
    }

    /// <summary>Why the apply would refuse this rollup, in <c>RollupResolver</c>'s order.</summary>
    private string? RefuseRollup()
    {
        if (Source is not { } source)
        {
            return Sources.Count == 0
                ? "Nothing points at this entity, so there is nothing to roll up — add a ref field here on the child first."
                : "A rollup needs the entity whose rows it aggregates — pick one of the entities that point here.";
        }

        if (source.Refusal is { } refused)
        {
            return refused;
        }

        if (!RollupOps.Contains(RollupOp, StringComparer.Ordinal))
        {
            return "A rollup's op is one of sum, count, avg, min or max.";
        }

        return RefuseAggregatedField() ?? RefuseVia(source);
    }

    private string? RefuseAggregatedField()
    {
        if (RollupOp == "count" || Aggregatable.Any(field => field.Name == RollupField))
        {
            return null;
        }

        return Aggregatable.Count == 0
            ? $"{RollupFrom} has no {(RollupOp == "avg" ? "decimal" : "number")} field a {RollupOp} can aggregate — use count, or add one."
            : $"A {RollupOp} needs the {RollupFrom} field it aggregates — pick one.";
    }

    private string? RefuseVia(RollupSource source)
        => source.Via.Count > 1 && !source.Via.Contains(RollupVia, StringComparer.Ordinal)
            ? $"{RollupFrom} points here through {string.Join(", ", source.Via)} — pick which one this rollup follows."
            : null;

    /// <summary>The derived type; a declared precision and scale are the author's, derived only when absent.</summary>
    private void WriteDerivedType(JsonObject facets)
    {
        var child = Aggregatable.FirstOrDefault(field => field.Name == RollupField);
        Type = child?.Type ?? FieldType.Integer;
        facets["type"] = Word(Type);
        ClearFacetsOfOtherTypes(facets);

        if (child is { Type: FieldType.Decimal })
        {
            facets["precision"] ??= child.Precision ?? DefaultPrecision;
            facets["scale"] ??= child.Scale ?? DefaultScale;
        }
    }

    private static JsonObject RollupObject(JsonObject facets)
    {
        if (facets["rollup"] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        facets["rollup"] = created;
        return created;
    }

    private void WriteRollupObject(JsonObject rollup, RollupSource source)
    {
        rollup["from"] = RollupFrom;
        rollup["op"] = RollupOp;
        SetOrRemove(rollup, "field", RollupOp == "count" ? null : RollupField);
        SetOrRemove(rollup, "via", source.Via.Contains(RollupVia, StringComparer.Ordinal) ? RollupVia : null);

        if (RemoveRollupFilter)
        {
            rollup.Remove("where");
        }
    }

    /// <summary>Writes a computed column: the expression and a chosen scalar type.</summary>
    private string? WriteComputed(JsonObject facets)
    {
        var expression = Computed.Trim();
        if (expression.Length == 0)
        {
            return "A computed field needs its expression — CEL over this row's own fields, such as unit_price * amount.";
        }

        if (expression.Length > CelMaxLength)
        {
            return "A computed expression is at most 2000 characters — the schema's limit for CEL.";
        }

        if (!ComputedTypes.Contains(Type))
        {
            return $"A computed value is stored as a generated column, offered here as {string.Join(", ", ComputedTypes.Select(Word))}.";
        }

        facets.Remove("rollup");
        facets["type"] = Word(Type);
        facets["computed"] = expression;
        Toggle(facets, "index", Indexed);
        ClearFacetsOfOtherTypes(facets);
        return WriteDefault(facets) ?? WriteTypeFacets(facets);
    }

    /// <summary>A kept <c>rollup.where</c> is refused at apply (<c>RollupResolver.cs:107</c>).</summary>
    private string? RefuseAKeptFilter(JsonObject facets)
        => Kind == FieldKind.Rollup && facets["rollup"]?["where"] is not null
            ? "Its rollup declares a 'where' filter, which this build refuses at apply (rollup.where). Tick \"Remove the declared filter\" to save the field."
            : null;

    /// <summary>A required or unique on a maintained value: carried, and said.</summary>
    private IEnumerable<FacetNote> Withheld()
        => MaintainedElsewhere
            ? _withheld.Where(facet => Flag(_declared[facet])).Select(facet => new FacetNote(
                facet, "true", FacetFate.Kept, "not offered for a value that is maintained for it — kept as declared."))
            : [];

    private static void SetOrRemove(JsonObject owner, string key, string? value)
    {
        if (value is { Length: > 0 })
        {
            owner[key] = value;
            return;
        }

        owner.Remove(key);
    }
}
```

In `FieldFacets.cs`:
- Delete the settable `MaintainedElsewhere` property and its initializer line in `Prefill`.
- `Prefill` ends with: `var editor = new FieldFacets { … };` then `editor.ReadMaintained(facets); return editor;`.
- `Retyped` → `private bool Retyped => Type != _declaredType || Kind != _declaredKind;`
- `UniqueOffered` → `public bool UniqueOffered => Kind == FieldKind.Supplied && (TakesUnique || Flag(_declared["unique"]));`
- In `Build`, replace `refusal = WriteSupplied(facets) ?? RefuseWhatTheApplyRefuses(facets);` with:

```csharp
        refusal = Kind switch
        {
            FieldKind.Rollup => WriteRollup(facets),
            FieldKind.Computed => WriteComputed(facets),
            _ => WriteSupplied(facets),
        } ?? RefuseWhatTheApplyRefuses(facets);
```

- `WriteSupplied` starts with `facets.Remove("rollup"); facets.Remove("computed");`.
- `RefuseWhatTheApplyRefuses` → `=> RefuseAKeptDefault(facets) ?? RefuseRequiredBesideReadOnly(facets) ?? RefuseAKeptFilter(facets);`

In `FieldFacets.Notes.cs`: add `"rollup"` and `"computed"` to `_drawn`; in `Notes()` add `notes.AddRange(Withheld());` before `return notes;`; in `RemovedDefaultReason` put first the arm `MaintainedElsewhere ? "a value that is maintained for it takes no default, so it does not follow the field."`.

`PendingSchema.Fields` — add to the `new FieldSchema { … }` initializer:

```csharp
            ComputedExpression = String(field.Value, "computed"),
            Rollup = Rollup(field.Value),
```

and the reader:

```csharp
    /// <summary>
    /// A staged rollup, so its row keeps the <c>rollup</c> badge the moment it is changed (§8d item 22).
    /// </summary>
    /// <remarks>
    /// <c>Via</c> is required on the applied shape because the resolver always resolves it; a staged one has not been
    /// resolved yet, so it is the declared <c>via</c> or empty — this is a renderer, and the apply resolves it.
    /// </remarks>
    private static RollupSchema? Rollup(JsonElement field)
        => field.TryGetProperty("rollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object
            && String(rollup, "from") is { Length: > 0 } from
            ? new RollupSchema
            {
                From = from,
                Op = Enum.TryParse<RollupOperation>(String(rollup, "op"), ignoreCase: true, out var op) ? op : RollupOperation.Count,
                Field = String(rollup, "field"),
                Via = String(rollup, "via") ?? string.Empty,
            }
            : null;
```

- [ ] **Step 4: Run the unit tests**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: PASS.

- [ ] **Step 5: Draw it in the editor**

`FieldEditor.razor.cs`:

```csharp
    private static readonly FieldKind[] _kinds = Enum.GetValues<FieldKind>();

    /// <summary>
    /// The entities a rollup here could aggregate, cascaded by the entity screen from the working copy.
    /// </summary>
    /// <remarks>
    /// Cascaded and private rather than a parameter — the <c>StagedView</c> pattern — because a component parameter
    /// must be public and <see cref="RollupSource"/> is internal; a parameter would grow the package's surface.
    /// </remarks>
    [CascadingParameter]
    private IReadOnlyList<RollupSource>? Sources { get; set; }

    /// <summary>The build's refusal of a rollup filter, shown where the filter would be.</summary>
    private ManagementRefusedFeature? WhereRefusal => Refused.FirstOrDefault(refusal => refusal.Slot == "rollup.where");

    private IReadOnlyList<string> RollupFroms => [.. _facets.Sources.Where(source => source.Refusal is null).Select(source => source.Entity)];

    private IReadOnlyList<string> AggregatableNames => [.. _facets.Aggregatable.Select(field => field.Name)];

    private static string KindWord(FieldKind kind) => kind switch
    {
        FieldKind.Rollup => "rollup",
        FieldKind.Computed => "computed",
        _ => "written by callers",
    };

    private string KindHint => _facets.Kind switch
    {
        FieldKind.Rollup => "Maintained by Alvo: aggregated from the rows of an entity that points here, in the same transaction as the write to them. Callers never write it.",
        FieldKind.Computed => "Maintained by the database: a stored generated column computed from this row's own fields. Callers never write it.",
        _ => "A caller writes it, within the type and constraints below.",
    };

    /// <summary>Switches the kind; a computed column keeps a type it can be, and decimal otherwise.</summary>
    private void ChooseKind(FieldKind kind)
    {
        _facets.Kind = kind;
        if (kind == FieldKind.Computed && !FieldFacets.ComputedTypes.Contains(_facets.Type))
        {
            _facets.Type = FieldType.Decimal;
        }

        _refusal = null;
    }
```

Replace `OnParametersSet`:

```csharp
    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        /* Prefilled only when the target changes: re-reading the parameters on every render would overwrite
           what the operator is in the middle of typing with what the document still says. */
        if (IsEditing && _prefilled != Editing)
        {
            _prefilled = Editing;
            _refusal = null;
            _facets = FieldFacets.Prefill(Editing!, EditingJson);
        }
        else if (!IsEditing)
        {
            _prefilled = null;
        }

        _facets.Sources = Sources ?? [];
    }
```

In `AddAnother`: `_facets = new FieldFacets { Type = _facets.Type, Kind = _facets.Kind, Sources = _facets.Sources };`.

`FieldEditor.razor` — directly after the Name `<Field>` add the kind:

```razor
        <Field Label="Value" LabelId="new-field-kind">
            <ChildContent>
                <ChipGroup TValue="FieldKind" Items="_kinds" Selected="[_facets.Kind]" aria-labelledby="new-field-kind"
                           Label="KindWord" SelectedChanged="kinds => ChooseKind(kinds[0])" />
            </ChildContent>
            <Hint>@KindHint</Hint>
        </Field>
```

Replace the Type `<Field>` with:

```razor
        @if (_facets.Kind == FieldKind.Rollup)
        {
            <Field Label="Type" LabelId="new-field-type">
                <span class="a-hint" data-testid="rollup-type">
                    Stored as <code class="a-mono">@FieldFacets.Word(_facets.DerivedType)</code> —
                    @(_facets.RollupOp == "count" ? "a count is a whole number." : "the type of the field it aggregates.")
                </span>
            </Field>
        }
        else
        {
            <Field Label="Type" LabelId="new-field-type">
                <ChipGroup TValue="FieldType" Items="@(_facets.Kind == FieldKind.Computed ? FieldFacets.ComputedTypes : _types)"
                           Selected="[_facets.Type]" aria-labelledby="new-field-type"
                           Label="Word" SelectedChanged="types => _facets.Type = types[0]" />
            </Field>
        }
```

Guard the required checkbox with `@if (_facets.RequiredOffered)`. Guard the type-facet `@switch (_facets.Type)` with `@if (_facets.Kind != FieldKind.Rollup)`. After it, add:

```razor
        @if (_facets.Kind == FieldKind.Rollup)
        {
            @if (_facets.Sources.Count == 0)
            {
                <Refusal data-testid="rollup-no-source">
                    Nothing points at <code class="a-mono">@Entity</code>. A rollup aggregates the rows of an entity with a
                    ref field here — add one on the child first.
                </Refusal>
            }
            else
            {
                <Field Label="Rolls up" LabelId="rollup-from">
                    <ChipGroup TValue="string" Items="RollupFroms" Selected="[_facets.RollupFrom]" aria-labelledby="rollup-from"
                               SelectedChanged="picked => _facets.RollupFrom = picked[0]" />
                </Field>
                @foreach (var refused in _facets.Sources.Where(source => source.Refusal is not null))
                {
                    <span class="a-hint" data-testid="@($"rollup-refused-{refused.Entity}")">
                        <code class="a-mono">@refused.Entity</code> — @refused.Refusal
                    </span>
                }

                <Field Label="Operation" LabelId="rollup-op">
                    <ChipGroup TValue="string" Items="FieldFacets.RollupOps" Selected="[_facets.RollupOp]" aria-labelledby="rollup-op"
                               SelectedChanged="ops => _facets.RollupOp = ops[0]" />
                </Field>

                @if (_facets.RollupOp != "count" && _facets.Source is not null)
                {
                    <Field Label="Of the field" LabelId="rollup-field">
                        <ChipGroup TValue="string" Items="AggregatableNames" Selected="[_facets.RollupField]" aria-labelledby="rollup-field"
                                   SelectedChanged="fields => _facets.RollupField = fields[0]" />
                    </Field>
                }

                @if (_facets.Source is { Via.Count: > 1 } source)
                {
                    <Field Label="Through" LabelId="rollup-via">
                        <ChipGroup TValue="string" Items="source.Via" Selected="[_facets.RollupVia]" aria-labelledby="rollup-via"
                                   SelectedChanged="refs => _facets.RollupVia = refs[0]" />
                    </Field>
                }
            }

            @* `where` is in the frozen schema and refused by this build, so it gets the build's sentence and no box. *@
            <Refusal data-testid="rollup-where-refused">
                <code class="a-mono">where</code> — @(WhereRefusal?.Consequence ?? "a filter on the child rows is not offered here.")
            </Refusal>
            @if (_facets.DeclaresRollupFilter)
            {
                <label class="a-check">
                    <input type="checkbox" checked="@_facets.RemoveRollupFilter" data-testid="rollup-remove-where"
                           @onchange="args => _facets.RemoveRollupFilter = args.Value is true" />
                    <span>Remove the declared filter</span>
                </label>
            }
        }

        @if (_facets.Kind == FieldKind.Computed)
        {
            <Field Label="Expression" For="new-field-computed">
                <ChildContent>
                    <textarea class="a-textarea a-mono" id="new-field-computed" rows="2" spellcheck="false"
                              placeholder="e.g. unit_price * amount" value="@_facets.Computed"
                              @oninput="args => _facets.Computed = args.Value?.ToString() ?? string.Empty"></textarea>
                </ChildContent>
                <Hint>
                    CEL over this row's own fields — and its rollups. No constants: the column is DDL, which carries no bound
                    value. The apply compiles it, and Preview shows its refusal in the framework's own words.
                </Hint>
            </Field>
        }
```

`Entity.razor.cs` (shared file — only these two edits): add the field `private IReadOnlyList<RollupSource> _rollupSources = [];` beside `_fields`, and at the end of `ReadWorking()` add `_rollupSources = RollupSources.For(Copy.Json, EntityName);`.

`Entity.razor` (shared file — only this edit): wrap the `<FieldEditor … />` element:

```razor
            <CascadingValue Value="_rollupSources">
                <FieldEditor Entity="@EntityName" Targets="_entities" OnAdd="AddFieldAsync"
                             Editing="@_editing" EditingJson="@EditedJson" OnClose="CloseEditor"
                             Siblings="_fieldNames" Refused="_refused" />
            </CascadingValue>
```

- [ ] **Step 6: Write the e2e scenarios**

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/MaintainedFieldScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A rollup is authored from the field editor, and the apply accepts what it composes (docs/todo-admin.md §7).
/// </summary>
/// <remarks>
/// <c>customers ← work_orders.customer_id</c> is the field-service pair both sides of which are scoped; the plan
/// appearing on Preview is the apply's own dry run accepting the declaration. Its own world: it stages.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class MaintainedFieldScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_count_rollup_is_composed_reopened_as_a_rollup_and_planned()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("open_orders");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "rollup", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "work_orders", Exact = true }).ClickAsync();

        (await sheet.GetByTestId("rollup-type").InnerTextAsync()).ShouldContain("integer");
        (await sheet.GetByRole(AriaRole.Checkbox, new() { Name = "required", Exact = true }).CountAsync()).ShouldBe(0);
        await sheet.GetByTestId("rollup-where-refused").WaitForAsync();
        await session.Page.GetByTestId("field-save").ClickAsync();

        var row = session.Page.GetByTestId("field-row-open_orders");
        await row.WaitForAsync();
        (await row.InnerTextAsync()).ShouldContain("rollup");

        await session.Page.GetByTestId("edit-field-open_orders").ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "rollup", Exact = true, Checked = true }).WaitForAsync();
        await session.Page.GetByTestId("sheet-close").ClickAsync();

        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        (await session.Content.InnerTextAsync()).ShouldContain("\"from\": \"work_orders\"");

        session.AssertConsoleClean();
    }

    /// <summary><c>regions</c> is global and <c>work_orders</c> scoped: the source is said, not offered.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_source_across_tenancy_is_said_and_not_offered()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "rollup", Exact = true }).ClickAsync();

        (await sheet.GetByTestId("rollup-refused-work_orders").InnerTextAsync()).ShouldContain("tenancy");
        (await sheet.GetByRole(AriaRole.Radio, new() { Name = "work_orders", Exact = true }).CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }
}

/// <summary>
/// A computed field is authored and keeps its kind once staged. It stops before Preview: whether the SQLite
/// migrator can add a stored generated column to an existing table is the migrator's question, not the editor's.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class ComputedFieldScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_computed_field_is_composed_and_reopened_as_computed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("priority_twice");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "computed", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "integer", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Expression" }).FillAsync("priority + priority");
        await session.Page.GetByTestId("field-save").ClickAsync();

        var row = session.Page.GetByTestId("field-row-priority_twice");
        await row.WaitForAsync();
        (await row.InnerTextAsync()).ShouldContain("computed");

        await session.Page.GetByTestId("edit-field-priority_twice").ClickAsync();
        (await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Expression" }).InputValueAsync()).ShouldBe("priority + priority");

        session.AssertConsoleClean();
    }
}
```

- [ ] **Step 7: Run the gates**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`, `scripts/test-admin-e2e`, `scripts/test-ring1`
Expected: green; `PublicApi` unchanged (the cascading parameter is private).

- [ ] **Step 8: Mark the item done** — §8d item 14: `14. ✅ **Rollup and computed …` and append `**Done:** the field editor has a Value kind — written by callers / rollup / computed. A rollup offers the working copy's sources (refused ones said: dynamic, tenancy), op, the child's number fields (decimals for avg), via when ambiguous, and the build's where refusal; its type is derived; required/unique/default are withheld and a declared one is kept and said. An existing rollup/computed field opens in its kind, and its badge survives staging.` In §7, append to the last paragraph: `**Done** — §8d item 14.`

- [ ] **Step 9: Normalise and commit** (all files above by name, plus `docs/todo-admin.md`):

```bash
git commit -m "feat(f5): rollup and computed fields are authored in the field editor

A Value kind replaces the type chips for a maintained field. The rollup
sources come from the working copy with RollupResolver's refusals said;
the type is derived; required/unique/default are withheld. The staged
row keeps its rollup/computed badge.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---

### Task 3: A field rename carries what names the field; a removal names it first (item 16)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/DescriptorReference.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/CelNames.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/FieldReferences.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Fields.cs` (`RenameField` overload, `ReferencesToField`)
- Create: `src/MMLib.Alvo.Admin/Components/Schema/Entity.References.cs` (partial of `Entity`: field-removal handlers)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor.cs` (delete `RemoveField`; `AddFieldAsync` keeps the leftovers) — shared file, only these lines
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor` (leftovers note on Fields; removal sheet) — shared file, only these lines
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/CelNamesTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyFieldReferenceTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/FieldReferenceScenarios.cs`
- Modify: `docs/todo-admin.md` §8d item 16

**Interfaces:**
- Produces:

```csharp
/// Place: e.g. "orders.indexes[0]", "orders.rules.list", "orders.hooks.beforeUpdate[0].mutate", "parents.fields.total.rollup.field".
/// Blocks: removing the field (or entity) would leave a descriptor the apply refuses.
internal sealed record DescriptorReference(string Place, bool Blocks);

internal static class CelNames
{
    public static string? Rename(string cel, string from, string to);   // null = not safely rewritable
    public static bool MayName(string cel, string name);                // word-boundary, for a place Rename declined
}

internal static class FieldReferences
{
    public static IReadOnlyList<DescriptorReference> Of(JsonObject root, string entity, string field);
    public static IReadOnlyList<DescriptorReference> Rename(JsonObject root, string entity, string from, string to); // the ones NOT carried
}

internal sealed partial class WorkingCopy
{
    public string? RenameField(string entity, string from, string to);  // unchanged signature, now carries
    public string? RenameField(string entity, string from, string to, out IReadOnlyList<DescriptorReference> uncarried);
    public IReadOnlyList<DescriptorReference> ReferencesToField(string entity, string field);
}
```

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Admin.Tests/Schema/CelNamesTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A field rename inside its own entity's CEL: a column reference is rewritten, nothing else is — and where that
/// cannot be told, the expression is left alone and named (deviation 1 in the plan).
/// </summary>
public class CelNamesTests
{
    [Theory]
    [InlineData("status == 'status'", "state == 'status'")]
    [InlineData("new.status != old.status", "new.state != old.state")]
    [InlineData("changed(status) && has(new.status)", "changed(state) && has(new.state)")]
    [InlineData("@user.status == status", "@user.status == state")]
    [InlineData("other.status == 1", "other.status == 1")]
    [InlineData("statuses == 1 || status_code == 2", "statuses == 1 || status_code == 2")]
    [InlineData("status(1) == 2", "status(1) == 2")]
    [InlineData("status == 'it\\'s status'", "state == 'it\\'s status'")]
    [InlineData("size(status) > 1e5", "size(state) > 1e5")]
    public void Only_a_reference_to_the_column_is_renamed(string cel, string renamed)
        => CelNames.Rename(cel, "status", "state").ShouldBe(renamed);

    [Theory]
    [InlineData("items.all(x, x.status == 'open')")]
    [InlineData("status == '''a'''")]
    public void An_expression_whose_names_cannot_be_told_apart_is_declined(string cel)
        => CelNames.Rename(cel, "status", "state").ShouldBeNull();

    [Fact]
    public void A_field_named_like_a_reserved_word_is_declined()
        => CelNames.Rename("true == in", "in", "inside").ShouldBeNull();

    [Theory]
    [InlineData("items.all(x, x.status)", true)]
    [InlineData("statuses", false)]
    public void A_declined_expression_may_still_be_named(string cel, bool names)
        => CelNames.MayName(cel, "status").ShouldBe(names);
}
```

Create `test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyFieldReferenceTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// Renaming a field carries every place the descriptor names it — composite indexes, a child rollup's field and
/// via, CEL in rules/computed/hooks, mutate keys, <c>{{new.…}}</c> placeholders — and a removal names them first.
/// Modelled on <see cref="WorkingCopyEntityReferenceTests"/>.
/// </summary>
public class WorkingCopyFieldReferenceTests
{
    [Fact]
    public void A_rename_carries_indexes_rules_hooks_mutate_keys_and_placeholders()
    {
        var copy = Copy();

        copy.RenameField("orders", "status", "state", out var uncarried).ShouldBeNull();

        var orders = Node(copy)["entities"]!["orders"]!;
        orders["indexes"]![0]!["fields"]!.ToJsonString().ShouldBe("""["state","total"]""");
        orders["rules"]!["list"]!.GetValue<string>().ShouldBe("state == 'open' || 'status' in @user.roles");
        orders["rules"]!["get"]!.GetValue<string>().ShouldBe("@user.status == 'x'");
        var before = orders["hooks"]!["beforeUpdate"]![0]!;
        before["condition"]!.GetValue<string>().ShouldBe("changed(state) && new.state == 'closed'");
        before["action"]!["mutate"]!["state"]!["$cel"]!.GetValue<string>().ShouldBe("lowerAscii(new.state)");
        orders["hooks"]!["afterUpdate"]![0]!["action"]!["to"]!.GetValue<string>().ShouldBe("{{ new.state }}@example.com");
        uncarried.ShouldBeEmpty();
    }

    [Fact]
    public void A_rename_on_the_child_carries_the_parents_rollup_field_and_via()
    {
        var copy = Copy();

        copy.RenameField("lines", "amount", "line_amount").ShouldBeNull();
        copy.RenameField("lines", "order_id", "order").ShouldBeNull();

        var rollup = Node(copy)["entities"]!["orders"]!["fields"]!["total"]!["rollup"]!;
        rollup["field"]!.GetValue<string>().ShouldBe("line_amount");
        rollup["via"]!.GetValue<string>().ShouldBe("order");
    }

    [Fact]
    public void A_rename_carries_a_computed_expression_that_names_it()
    {
        var copy = Copy();

        copy.RenameField("orders", "total", "grand_total").ShouldBeNull();

        Node(copy)["entities"]!["orders"]!["fields"]!["double_total"]!["computed"]!.GetValue<string>()
            .ShouldBe("grand_total + grand_total");
    }

    [Fact]
    public void An_expression_the_rename_cannot_rewrite_is_named_and_left_as_written()
    {
        var copy = Copy();

        copy.RenameField("orders", "flag", "marker", out var uncarried).ShouldBeNull();

        uncarried.ShouldBe([new DescriptorReference("orders.rules.delete", Blocks: false)]);
        Node(copy)["entities"]!["orders"]!["rules"]!["delete"]!.GetValue<string>().ShouldBe("tags.exists(t, t == flag)");
    }

    [Fact]
    public void A_removal_names_every_place_that_names_the_field_and_which_of_them_block_it()
    {
        var references = Copy().ReferencesToField("orders", "status");

        references.ShouldContain(new DescriptorReference("orders.indexes[0]", Blocks: true));
        references.ShouldContain(new DescriptorReference("orders.rules.list", Blocks: true));
        references.ShouldContain(new DescriptorReference("orders.hooks.beforeUpdate[0].mutate", Blocks: true));
        references.ShouldNotContain(reference => reference.Place == "orders.rules.get");
    }

    [Fact]
    public void A_child_fields_removal_names_the_rollup_that_aggregates_it()
        => Copy().ReferencesToField("lines", "amount")
            .ShouldBe([new DescriptorReference("orders.fields.total.rollup.field", Blocks: true)]);

    [Fact]
    public void Asking_what_names_a_field_changes_nothing()
    {
        var copy = Copy();
        var before = copy.Json;

        copy.ReferencesToField("orders", "status");

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void A_field_nothing_names_has_no_references()
        => Copy().ReferencesToField("orders", "note").ShouldBeEmpty();

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 3);
        return copy;
    }

    private static JsonNode Node(WorkingCopy copy) => JsonNode.Parse(copy.Json)!;

    private const string Descriptor = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "shop",
          "entities": {
            "orders": {
              "fields": {
                "status": { "type": "string" },
                "flag": { "type": "boolean" },
                "note": { "type": "text" },
                "total": { "type": "decimal", "precision": 10, "scale": 2, "rollup": { "from": "lines", "op": "sum", "field": "amount", "via": "order_id" } },
                "double_total": { "type": "decimal", "precision": 12, "scale": 2, "computed": "total + total" }
              },
              "indexes": [ { "fields": ["status", "total"] } ],
              "rules": {
                "list": "status == 'open' || 'status' in @user.roles",
                "get": "@user.status == 'x'",
                "delete": "tags.exists(t, t == flag)"
              },
              "hooks": {
                "beforeUpdate": [ { "condition": "changed(status) && new.status == 'closed'", "action": { "mutate": { "status": { "$cel": "lowerAscii(new.status)" } } } } ],
                "afterUpdate": [ { "action": { "type": "email", "template": "notice", "to": "{{ new.status }}@example.com" } } ]
              }
            },
            "lines": {
              "fields": {
                "order_id": { "type": "ref", "entity": "orders" },
                "amount": { "type": "decimal", "precision": 10, "scale": 2 }
              }
            }
          }
        }
        """;
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: compile errors — `CelNames`, `DescriptorReference`, the `RenameField` overload and `ReferencesToField` not found.

- [ ] **Step 3: Implement**

Create `src/MMLib.Alvo.Admin/Components/Schema/DescriptorReference.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One place in a descriptor that names a field or an entity.</summary>
/// <param name="Place">Where, as a dotted path an operator can find: <c>orders.indexes[0]</c>, <c>orders.rules.list</c>.</param>
/// <param name="Blocks">Whether removing what it names would leave a descriptor the apply refuses.</param>
internal sealed record DescriptorReference(string Place, bool Blocks);
```

Create `src/MMLib.Alvo.Admin/Components/Schema/CelNames.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Renames one field inside a CEL expression of its own entity — the column references and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Token by token, not by word boundary.</b> In an entity's own rules, computed and hook conditions a field is a
/// bare name (<c>owner_id == @user.id</c>) or a member of a row image (<c>new.status</c>, <c>old.status</c>). A
/// word-boundary replace also rewrites string literals — <c>'manager' in @user.roles</c> for a field called
/// <c>manager</c> — and members of <c>@user</c>. So strings are copied whole, a name after <c>@</c> or after a
/// member dot of anything but <c>new</c>/<c>old</c> is left alone, and a name followed by <c>(</c> is a function.
/// </para>
/// <para>
/// <b>Declined rather than guessed</b> when the answer cannot be told from the text: a binding macro
/// (<c>.all(x, …)</c> binds a variable that may shadow the field), a triple-quoted string, or a field named like a
/// CEL reserved word. The caller then names the place instead of rewriting it.
/// </para>
/// </remarks>
internal static class CelNames
{
    private static readonly HashSet<string> _reserved = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "in", "as", "break", "const", "continue", "else", "for", "function", "if",
        "import", "let", "loop", "package", "namespace", "return", "var", "void", "while",
    };

    private static readonly string[] _binders = [".all(", ".exists(", ".exists_one(", ".map(", ".filter(", "'''", "\"\"\""];

    private static readonly string[] _images = ["new", "old"];

    /// <summary>The expression with every reference to <paramref name="from"/> renamed, or <see langword="null"/> when that cannot be done safely.</summary>
    public static string? Rename(string cel, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(cel);
        if (_reserved.Contains(from) || _binders.Any(binder => cel.Contains(binder, StringComparison.Ordinal)))
        {
            return null;
        }

        var output = new StringBuilder(cel.Length);
        for (var at = 0; at < cel.Length;)
        {
            at = cel[at] is '\'' or '"' ? CopyString(cel, at, output)
                : IsStart(cel[at]) && !IsPart(Before(cel, at)) ? CopyName(cel, at, from, to, output)
                : Copy(cel, at, output);
        }

        return output.ToString();
    }

    /// <summary>Whether the text contains the name as a whole word — for a place <see cref="Rename"/> declined.</summary>
    public static bool MayName(string cel, string name)
        => Regex.IsMatch(cel, @"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static int CopyString(string cel, int at, StringBuilder output)
    {
        var quote = cel[at];
        var end = at + 1;
        while (end < cel.Length && cel[end] != quote)
        {
            end += cel[end] == '\\' ? 2 : 1;
        }

        end = Math.Min(end + 1, cel.Length);
        output.Append(cel, at, end - at);
        return end;
    }

    private static int CopyName(string cel, int at, string from, string to, StringBuilder output)
    {
        var end = at;
        while (end < cel.Length && IsPart(cel[end]))
        {
            end++;
        }

        var name = cel[at..end];
        output.Append(string.Equals(name, from, StringComparison.Ordinal) && IsColumn(cel, at, end) ? to : name);
        return end;
    }

    private static int Copy(string cel, int at, StringBuilder output)
    {
        output.Append(cel[at]);
        return at + 1;
    }

    /// <summary>A bare name that is not a call, or a member of <c>new</c>/<c>old</c>.</summary>
    private static bool IsColumn(string cel, int at, int end)
    {
        if (NextNonSpace(cel, end) == '(')
        {
            return false;
        }

        var before = PreviousNonSpace(cel, at, out var dot);
        return before switch
        {
            '@' => false,
            '.' => IsImageBefore(cel, dot),
            _ => true,
        };
    }

    private static bool IsImageBefore(string cel, int dot)
    {
        PreviousNonSpace(cel, dot, out var last);
        var start = last + 1;
        while (start > 0 && IsPart(cel[start - 1]))
        {
            start--;
        }

        return _images.Contains(cel[start..(last + 1)]) && PreviousNonSpace(cel, start, out _) is not ('.' or '@');
    }

    private static char PreviousNonSpace(string cel, int at, out int position)
    {
        position = at - 1;
        while (position >= 0 && char.IsWhiteSpace(cel[position]))
        {
            position--;
        }

        return position >= 0 ? cel[position] : '\0';
    }

    private static char NextNonSpace(string cel, int at)
    {
        while (at < cel.Length && char.IsWhiteSpace(cel[at]))
        {
            at++;
        }

        return at < cel.Length ? cel[at] : '\0';
    }

    private static char Before(string cel, int at) => at == 0 ? ' ' : cel[at - 1];

    private static bool IsStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
```

Note for the implementer: in `IsImageBefore`, `PreviousNonSpace(cel, dot, out var last)` returns the last character of the name before the dot and its index; the slice `cel[start..(last + 1)]` is that name. Walk through `"x.new.status"` by hand before running the tests — `new` is preceded by `.`, so the member is not a row image and `status` stays.

Create `src/MMLib.Alvo.Admin/Components/Schema/FieldReferences.cs`:

```csharp
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Every place a descriptor names one field, so a rename can carry them and a removal can name them first.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rename that left these behind was a plan the apply refuses</b> (docs/todo-admin.md §8d item 16): a child
/// rollup's <c>field</c>/<c>via</c> naming a field that is gone (<c>RollupResolver.cs:252-327</c>), a mutate key
/// that is not a field (BHC), a rule or computed expression compiled against columns that no longer include the
/// name, an index over a missing column. The <see cref="EntityReferences"/> pattern, one level down.
/// </para>
/// <para>
/// <b>The places, from the frozen schema:</b> the entity's <c>indexes[].fields</c>; its <c>rules</c>; its fields'
/// <c>computed</c>, <c>hidden</c>/<c>readOnly</c>/<c>validation</c> CEL and <c>default.$cel</c>; its hooks'
/// <c>condition</c>, <c>mutate</c> keys and <c>$cel</c> values, and the <c>{{new.…}}</c>/<c>{{old.…}}</c>
/// placeholders of its after-actions; and every rollup whose <c>from</c> is this entity — its <c>field</c>,
/// <c>via</c> and <c>where</c>. Top-level templates are shared between entities and are not attributed.
/// </para>
/// </remarks>
internal static class FieldReferences
{
    /// <summary>A name no field can have: a find is a rename to it that must change nothing it keeps.</summary>
    private const string Probe = "\u0001";

    private static readonly string[] _celFacets = ["computed", "hidden", "readOnly", "validation"];
    private static readonly string[] _rollupKeys = ["field", "via"];

    /// <summary>Every place that names the field; nothing is changed.</summary>
    public static IReadOnlyList<DescriptorReference> Of(JsonObject root, string entity, string field)
        => [.. Walk(root, entity, field, to: null).Select(found => found.Reference)];

    /// <summary>Renames the field everywhere it safely can, and answers with the places it could not.</summary>
    public static IReadOnlyList<DescriptorReference> Rename(JsonObject root, string entity, string from, string to)
        => [.. Walk(root, entity, from, to).Where(found => !found.Carried).Select(found => found.Reference)];

    private static List<Found> Walk(JsonObject root, string entity, string field, string? to)
    {
        var at = new Site(entity, field, to, []);
        if (root["entities"] is not JsonObject entities)
        {
            return at.Found;
        }

        if (entities[entity] is JsonObject declared)
        {
            Indexes(declared, at);
            Rules(declared, at);
            FieldExpressions(declared, at);
            Hooks(declared, at);
        }

        Rollups(entities, at);
        return at.Found;
    }

    private static void Indexes(JsonObject declared, Site at)
    {
        if (declared["indexes"] is not JsonArray indexes)
        {
            return;
        }

        for (var i = 0; i < indexes.Count; i++)
        {
            if (indexes[i]?["fields"] is JsonArray fields && fields.Any(name => Is(name, at.Field)))
            {
                Replace(fields, at);
                at.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{at.Entity}.indexes[{i}]"), blocks: true);
            }
        }
    }

    private static void Rules(JsonObject declared, Site at)
    {
        if (declared["rules"] is JsonObject rules)
        {
            foreach (var operation in rules.Select(pair => pair.Key).ToList())
            {
                Expression(rules, operation, $"{at.Entity}.rules.{operation}", at);
            }
        }
    }

    /// <summary>Each field's own CEL facets — except, for a removal, the removed field's, which go with it.</summary>
    private static void FieldExpressions(JsonObject declared, Site at)
    {
        if (declared["fields"] is not JsonObject fields)
        {
            return;
        }

        foreach (var (name, node) in fields.ToList())
        {
            if (node is not JsonObject field || (at.To is null && name == at.Field))
            {
                continue;
            }

            foreach (var facet in _celFacets)
            {
                Expression(field, facet, $"{at.Entity}.fields.{name}.{facet}", at);
            }

            if (field["default"] is JsonObject tagged)
            {
                Expression(tagged, "$cel", $"{at.Entity}.fields.{name}.default", at);
            }
        }
    }

    private static void Hooks(JsonObject declared, Site at)
    {
        if (declared["hooks"] is not JsonObject hooks)
        {
            return;
        }

        foreach (var (point, node) in hooks.ToList())
        {
            for (var i = 0; node is JsonArray list && i < list.Count; i++)
            {
                if (list[i] is JsonObject hook)
                {
                    Hook(hook, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{at.Entity}.hooks.{point}[{i}]"), at);
                }
            }
        }
    }

    private static void Hook(JsonObject hook, string place, Site at)
    {
        Expression(hook, "condition", $"{place}.condition", at);
        if (hook["action"] is not JsonObject action)
        {
            return;
        }

        if (action["mutate"] is JsonObject mutate)
        {
            Mutate(mutate, $"{place}.mutate", at);
            return;
        }

        Placeholders(action, $"{place}.action", at);
    }

    private static void Mutate(JsonObject mutate, string place, Site at)
    {
        foreach (var (key, value) in mutate.ToList())
        {
            if (value is JsonObject tagged)
            {
                Expression(tagged, "$cel", $"{place}.{key}", at);
            }
        }

        if (!mutate.ContainsKey(at.Field))
        {
            return;
        }

        /* A mutate that already sets the new name as well cannot be merged by a rename — it is named instead. */
        if (at.To is { } to && mutate.ContainsKey(to))
        {
            at.Found.Add(new(new(place, Blocks: true), Carried: false));
            return;
        }

        if (at.To is { } target)
        {
            Rekey(mutate, at.Field, target);
        }

        at.Add(place, blocks: true);
    }

    /// <summary><c>{{new.field}}</c> and <c>{{old.field}}</c> in an after-action's strings — exact placeholder syntax, so always carried.</summary>
    private static void Placeholders(JsonObject action, string place, Site at)
    {
        var pattern = Placeholder(at.Field);
        foreach (var (key, value) in action.ToList())
        {
            if (value is JsonObject nested)
            {
                Placeholders(nested, $"{place}.{key}", at);
            }
            else if (value is JsonValue text && text.TryGetValue<string>(out var template) && pattern.IsMatch(template))
            {
                if (at.To is { } to)
                {
                    action[key] = pattern.Replace(template, "${1}" + to + "${2}");
                }

                at.Add($"{place}.{key}", blocks: true);
            }
        }
    }

    private static void Rollups(JsonObject entities, Site at)
    {
        foreach (var (parent, node) in entities.ToList())
        {
            if (node?["fields"] is not JsonObject fields)
            {
                continue;
            }

            foreach (var (name, field) in fields.ToList())
            {
                if (field?["rollup"] is JsonObject rollup && Is(rollup["from"], at.Entity))
                {
                    Rollup(rollup, $"{parent}.fields.{name}.rollup", at);
                }
            }
        }
    }

    private static void Rollup(JsonObject rollup, string place, Site at)
    {
        Expression(rollup, "where", $"{place}.where", at);
        foreach (var key in _rollupKeys.Where(key => Is(rollup[key], at.Field)))
        {
            if (at.To is { } to)
            {
                rollup[key] = to;
            }

            at.Add($"{place}.{key}", blocks: true);
        }
    }

    /// <summary>One CEL slot: rewritten token by token when that is safe; otherwise named, and not blocking.</summary>
    private static void Expression(JsonObject owner, string key, string place, Site at)
    {
        if (owner[key] is not JsonValue value || !value.TryGetValue<string>(out var cel))
        {
            return;
        }

        if (CelNames.Rename(cel, at.Field, at.To ?? Probe) is not { } renamed)
        {
            if (CelNames.MayName(cel, at.Field))
            {
                at.Found.Add(new(new(place, Blocks: false), Carried: false));
            }

            return;
        }

        if (!string.Equals(renamed, cel, StringComparison.Ordinal))
        {
            if (at.To is not null)
            {
                owner[key] = renamed;
            }

            at.Add(place, blocks: true);
        }
    }

    private static void Replace(JsonArray fields, Site at)
    {
        for (var i = 0; at.To is { } to && i < fields.Count; i++)
        {
            if (Is(fields[i], at.Field))
            {
                fields[i] = to;
            }
        }
    }

    /// <summary>Moves a key without moving its position — <c>WorkingCopy.Rekey</c>'s reason.</summary>
    private static void Rekey(JsonObject owner, string from, string to)
    {
        var order = owner.Select(pair => (Key: pair.Key == from ? to : pair.Key, Value: pair.Value?.DeepClone())).ToList();
        owner.Clear();
        foreach (var (key, value) in order)
        {
            owner[key] = value;
        }
    }

    private static Regex Placeholder(string field)
        => new(@"(\{\{\s*(?:new|old)\.)" + Regex.Escape(field) + @"(\s*\}\})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool Is(JsonNode? node, string expected)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && text == expected;

    /// <summary>What one walk is about, and what it found.</summary>
    private sealed record Site(string Entity, string Field, string? To, List<Found> Found)
    {
        /// <summary>A place found; carried when this walk renames, since every caller rewrote it first.</summary>
        public void Add(string place, bool blocks) => Found.Add(new(new(place, blocks), Carried: To is not null));
    }

    private sealed record Found(DescriptorReference Reference, bool Carried);
}
```

In `WorkingCopy.Fields.cs` replace `RenameField`:

```csharp
    /// <inheritdoc cref="RenameField(string, string, string, out IReadOnlyList{DescriptorReference})"/>
    public string? RenameField(string entity, string from, string to) => RenameField(entity, from, to, out _);

    /// <summary>
    /// Renames a field, carrying the rename so the apply moves the column instead of dropping it, and carrying
    /// every place that names the field (<see cref="FieldReferences"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="RenameEntity"/>'s rules, one level down — including that the origin is read against the
    /// applied document, so a field on an entity this copy invented carries no <c>renamedFrom</c> either.
    /// </remarks>
    /// <param name="entity">The entity the field is on.</param>
    /// <param name="from">The name it has now.</param>
    /// <param name="to">The name it should have.</param>
    /// <param name="uncarried">The places that name it and could not be rewritten safely, for the screen to name.</param>
    /// <returns>A sentence saying why it cannot, or <see langword="null"/> when it was renamed.</returns>
    public string? RenameField(string entity, string from, string to, out IReadOnlyList<DescriptorReference> uncarried)
    {
        string? refusal = null;
        IReadOnlyList<DescriptorReference> left = [];
        Edit(root => (refusal = RefuseFieldRename(root, entity, from, to)) is null && MoveField(root, entity, from, to, out left));

        uncarried = left;
        return refusal;
    }

    /// <summary>Every place that names a field, and which of them would make the apply refuse its removal.</summary>
    public IReadOnlyList<DescriptorReference> ReferencesToField(string entity, string field)
        => Read<IReadOnlyList<DescriptorReference>>(
            () => _working is JsonObject root ? FieldReferences.Of(root, entity, field) : []);

    private static string? RefuseFieldRename(JsonObject root, string entity, string from, string to)
        => root["entities"]?[entity]?["fields"] is JsonObject fields && fields[from] is JsonObject
            ? Refusal(from, to, fields, "field")
            : $"There is no field called {from} on {entity}.";

    private bool MoveField(JsonObject root, string entity, string from, string to, out IReadOnlyList<DescriptorReference> uncarried)
    {
        var fields = (JsonObject)root["entities"]![entity]!["fields"]!;

        /* Against the entity's applied name, not its working one: renaming a field on an entity that was
           itself renamed in this copy must still resolve the column that exists in the database. */
        var origin = Origin(
            (JsonObject)fields[from]!, from, _applied?["entities"]?[AppliedNameOf(entity)]?["fields"]?[from] is not null);

        Rekey(fields, from, to);
        Carry(fields[to] as JsonObject, origin, to);
        uncarried = FieldReferences.Rename(root, entity, from, to);
        return true;
    }
```

Create `src/MMLib.Alvo.Admin/Components/Schema/Entity.References.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Schema;

/* What names a field or an entity, asked before it is renamed or removed. Kept out of Entity.razor.cs so the
   screen's own file stays about its tabs. */
public partial class Entity
{
    private (string Field, IReadOnlyList<DescriptorReference> References)? _removal;
    private (string From, IReadOnlyList<DescriptorReference> Uncarried)? _renameLeftovers;

    /// <summary>
    /// Drops a field from the working copy — at once when nothing names it, otherwise after a sheet has named
    /// what does.
    /// </summary>
    /// <remarks>
    /// No confirmation for a field nothing names, and that is deliberate: nothing has happened to the database
    /// yet, the preview states the cost as a plan, and the apply is where dropping a column is allowed. A field
    /// something <em>does</em> name is different — removing it composes a descriptor the apply refuses (a
    /// rollup, a mutate key, a rule compiled against a column that is gone), so the sheet refuses it until they
    /// are changed (docs/todo-admin.md §8d item 16).
    /// </remarks>
    private void RemoveField(string field)
    {
        var references = Copy.ReferencesToField(EntityName, field);
        if (references.Count > 0)
        {
            _removal = (field, references);
            return;
        }

        StageRemoval(field);
    }

    /// <summary>Removes the field the sheet named, when nothing it named blocks the removal.</summary>
    private void RemoveAnyway()
    {
        if (_removal is { } removal && !removal.References.Any(reference => reference.Blocks))
        {
            StageRemoval(removal.Field);
        }

        _removal = null;
    }

    private void CloseRemoval() => _removal = null;

    private void StageRemoval(string field)
    {
        Copy.RemoveField(EntityName, field);
        CloseEditor();
        ReadWorking();
    }
}
```

`Entity.razor.cs` (shared — only these edits): delete the old `RemoveField` method and its doc comment (moved above); in `AddFieldAsync` replace the rename call with:

```csharp
            if (!string.Equals(editing, added.Name, StringComparison.Ordinal))
            {
                Copy.RenameField(EntityName, editing, added.Name, out var uncarried);
                _renameLeftovers = (editing, uncarried);
            }
```

`Entity.razor` (shared — only these edits): inside `case EntityTabs.Fields:`, directly after the `</SectionHead>`, add:

```razor
                        @if (_renameLeftovers is { Uncarried.Count: > 0 } leftovers)
                        {
                            <p class="a-note" data-testid="rename-leftovers">
                                <code class="a-mono">@leftovers.From</code> is renamed, and these still name it — the
                                rename could not rewrite them safely, and the apply refuses them until they are edited:
                                <span class="a-mono">@string.Join(", ", leftovers.Uncarried.Select(reference => reference.Place))</span>
                            </p>
                        }
```

and after the rename `</Sheet>` add the removal sheet:

```razor
    <Sheet Open="_removal is not null" Title="@($"Remove {_removal?.Field}")" TestId="remove-field-sheet"
           Subtitle="These name the field. A removal that leaves one of the blocking ones behind is a descriptor the apply refuses."
           OnClose="CloseRemoval">
        <div class="a-stack">
            @foreach (var reference in _removal?.References ?? [])
            {
                <ListRow data-testid="field-reference">
                    <code class="a-mono a-listrow__key">@reference.Place</code>
                    <span class="@($"a-badge {(reference.Blocks ? "a-badge--danger" : "a-badge--warn")}")">
                        @(reference.Blocks ? "names it" : "may name it")
                    </span>
                </ListRow>
            }

            @if (_removal?.References.Any(reference => reference.Blocks) == true)
            {
                <Refusal data-testid="remove-field-blocked">Change or remove the places that name it first.</Refusal>
            }
            else
            {
                <div class="a-row">
                    <button type="button" class="a-btn a-btn--danger a-btn--sm" data-testid="remove-field-anyway"
                            @onclick="RemoveAnyway">Remove from the working copy</button>
                </div>
            }
        </div>
    </Sheet>
```

- [ ] **Step 4: Run the unit tests**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: PASS, including the existing `WorkingCopyRenameTests` (the three-argument overload is unchanged in meaning).

- [ ] **Step 5: Write the e2e scenarios**

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/FieldReferenceScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A field rename carries its references and plans cleanly; a removal names them first (docs/todo-admin.md §8d 16).
/// </summary>
/// <remarks>
/// <c>work_orders.assigned_to</c> is named by three rules (<c>assigned_to == @user.id</c>) and a single-field index;
/// the plan appearing is the apply's own dry run compiling the renamed rules. Its own world: it stages.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class FieldReferenceScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_rename_carries_the_rules_and_the_index_and_plans_cleanly()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("edit-field-assigned_to").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("technician_id");
        await session.Page.GetByTestId("field-save").ClickAsync();
        await session.Page.GetByTestId("field-row-technician_id").WaitForAsync();
        (await session.Page.GetByTestId("rename-leftovers").CountAsync()).ShouldBe(0);

        await session.PreviewPendingAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        (await session.Content.InnerTextAsync()).ShouldContain("technician_id == @user.id");

        session.AssertConsoleClean();
    }

    /// <summary><c>work_orders.status</c> leads the composite index: the removal is named and refused, not staged.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_removal_of_an_indexed_field_names_the_index_and_stages_nothing()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("remove-field-status").ClickAsync();
        var sheet = session.Page.GetByTestId("remove-field-sheet");
        (await sheet.GetByTestId("field-reference").First.InnerTextAsync()).ShouldContain("work_orders.indexes[0]");
        await sheet.GetByTestId("remove-field-blocked").WaitForAsync();
        (await sheet.GetByTestId("remove-field-anyway").CountAsync()).ShouldBe(0);

        await session.Page.GetByTestId("sheet-close").ClickAsync();
        (await session.Page.GetByTestId("staged-status").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }
}
```

- [ ] **Step 6: Run the gates** — `scripts/test-admin-e2e`, `scripts/test-ring1` → green (`DestructivePlanScenarios`, `PendingWorkScenarios` and `DiscardScenarios` remove `regions.code`, `regions.name`, `customers.notes`, which nothing names, so they stage at once as before).

- [ ] **Step 7: Mark the item done** — §8d item 16: `16. ✅ **A field rename …` and append `**Done:** a rename carries composite indexes, a child rollup's field/via/where, mutate keys, {{new./old.}} placeholders and — token by token — the entity's own rules/computed/hook CEL; a place it cannot rewrite safely is named on the Fields tab. A removal names every place first and is refused while one of them would make the apply refuse it.`

- [ ] **Step 8: Normalise and commit** (files above by name):

```bash
git commit -m "feat(f5): a field rename carries its references and a removal names them

FieldReferences walks every place the frozen schema lets a field be named;
CelNames rewrites only column references, and declines where it cannot
tell. A removal that would leave a refused descriptor is refused.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---

### Task 4: Rollback goes through a dry run (item 18)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/History/RollbackGate.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/PlanSteps.razor`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Preview.razor` (the steps loop → `<PlanSteps>`)
- Modify: `src/MMLib.Alvo.Admin/Components/History/History.razor` (rollback section and its code)
- Modify: `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt` (accept `PlanSteps` only)
- Create: `test/MMLib.Alvo.Admin.Tests/History/RollbackGateTests.cs`
- Modify: `docs/todo-admin.md` §8d item 18

**Interfaces:**
- Produces:

```csharp
namespace MMLib.Alvo.Admin.Components.History;
internal static class RollbackGate
{
    public static bool CanRollBack(ManagementPlanSummary? plan, bool confirmed);
    public static bool AllowDestructive(ManagementPlanSummary plan, bool confirmed);
}
// namespace MMLib.Alvo.Admin.Components.Schema — public by the Razor SDK:
public partial class PlanSteps { [Parameter, EditorRequired] public IReadOnlyList<string> Steps { get; set; } }
```

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Admin.Tests/History/RollbackGateTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.History;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.History;

/// <summary>
/// A rollback runs only after its plan is on screen, and permission to lose data is its own explicit confirm —
/// never implied by having typed a name (docs/todo-admin.md §8b, rollback dry run).
/// </summary>
public class RollbackGateTests
{
    private static readonly ManagementPlanSummary _safe = new(IsEmpty: false, HasDestructiveChanges: false, ["AddField a.b"]);
    private static readonly ManagementPlanSummary _destroys = new(IsEmpty: false, HasDestructiveChanges: true, ["DropField a.b  <- destructive"]);

    [Fact]
    public void Nothing_rolls_back_before_its_plan_is_on_screen()
        => RollbackGate.CanRollBack(null, confirmed: true).ShouldBeFalse();

    [Fact]
    public void A_plan_that_destroys_nothing_rolls_back_without_a_confirmation()
        => RollbackGate.CanRollBack(_safe, confirmed: false).ShouldBeTrue();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_plan_that_destroys_data_waits_for_its_confirmation(bool confirmed, bool can)
        => RollbackGate.CanRollBack(_destroys, confirmed).ShouldBe(can);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Permission_to_destroy_is_sent_only_for_a_confirmed_destructive_plan(bool destructive, bool confirmed, bool sent)
        => RollbackGate.AllowDestructive(destructive ? _destroys : _safe, confirmed).ShouldBe(sent);
}
```

- [ ] **Step 2: Run to verify it fails** — `dotnet test --project test/MMLib.Alvo.Admin.Tests` → compile error, `RollbackGate` not found.

- [ ] **Step 3: Implement**

Create `src/MMLib.Alvo.Admin/Components/History/RollbackGate.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.History;

/// <summary>
/// When a rollback may run, and whether it may destroy data — the Preview's rule, applied to a rollback.
/// </summary>
/// <remarks>
/// <b>It used to run blind and always destructive</b>: no plan, and <c>allowDestructive: true</c> hard-coded after a
/// typed-name confirm, beside a sentence saying "permission to lose data is never implied" (docs/todo-admin.md §8b).
/// Now the plan is asked first as a dry run (which asks with destruction allowed, because describing a drop destroys
/// nothing — Preview's reason), and the real rollback sends the permission only when the plan needs it and the
/// operator gave it.
/// </remarks>
internal static class RollbackGate
{
    /// <summary>Whether the rollback button is live.</summary>
    /// <param name="plan">The dry run's plan, or <see langword="null"/> before one is on screen.</param>
    /// <param name="confirmed">Whether the operator typed the confirmation.</param>
    public static bool CanRollBack(ManagementPlanSummary? plan, bool confirmed)
        => plan is not null && (!plan.HasDestructiveChanges || confirmed);

    /// <summary>What the real rollback sends as <c>allowDestructive</c>.</summary>
    /// <param name="plan">The plan on screen.</param>
    /// <param name="confirmed">Whether the operator typed the confirmation.</param>
    public static bool AllowDestructive(ManagementPlanSummary plan, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.HasDestructiveChanges && confirmed;
    }
}
```

Create `src/MMLib.Alvo.Admin/Components/Schema/PlanSteps.razor` (BOM + LF):

```razor
@*
    The planner's steps as sentences — the rendering Preview had inline, now shared with a rollback's dry run so
    the two plans read alike. PlanStep says why a step is reworded and what is carried through unchanged.
*@
@foreach (var step in Steps.Select(PlanStep.Parse))
{
    <ListRow data-testid="plan-step" class="a-listrow--inline">
        <span class="@($"a-badge a-listrow__aside {(step.Destructive ? "a-badge--danger" : "a-badge--ok")}")">
            @(step.Destructive ? "destroys" : "safe")
        </span>
        <span class="a-grow a-value">
            @* One expression with its trailing space: a bare `@step.Verb` followed by the element on the next line
               lost the whitespace between them ("Drop columncustomers.notes"). *@
            @($"{step.Verb} ")
            @if (step.Target.Length > 0)
            {
                <code class="a-mono">@step.Target</code>
            }
            @if (step.Consequence is { } consequence)
            {
                <text> — @consequence</text>
            }
        </span>
    </ListRow>
}

@code {
    /// <summary>The planner's lines, verbatim and in order (<c>ManagementPlanSummary.Steps</c>).</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<string> Steps { get; set; } = [];
}
```

In `Preview.razor` replace the whole `@foreach (var step in _plan.Plan.Steps.Select(PlanStep.Parse)) { … }` block inside the plan panel's `<ChildContent>` with `<PlanSteps Steps="_plan.Plan.Steps" />`.

In `History.razor` replace the block inside `@if (_selected.Version.Revision != _current) { … }` with:

```razor
                            <div class="a-stack">
                                <p class="a-section__sub">
                                    Rolling back does not rewrite history — it appends the reverse migration as a
                                    new revision at the top of the list, and this one stays where it is.
                                </p>

                                @if (_rollback is null)
                                {
                                    <button type="button" class="a-btn a-actions" data-testid="rollback-plan"
                                            disabled="@_busy" @onclick="PlanRollbackAsync">
                                        Plan the rollback to r@(_selected.Version.Revision)
                                    </button>
                                }
                                else
                                {
                                    <div data-testid="rollback-plan-steps">
                                        <span class="a-section__title">@RollbackTitle(_rollback)</span>
                                        <PlanSteps Steps="_rollback.Steps" />
                                    </div>

                                    @if (_rollback.HasDestructiveChanges)
                                    {
                                        <ConfirmByName Title="This rollback destroys data"
                                                       Detail="A reverse migration can discard data that was added after this revision. Permission to lose data is never implied — confirming here is what allows it."
                                                       Expected="@Gateway.Project"
                                                       OnConfirmedChanged="confirmed => _allowed = confirmed" />
                                    }

                                    <button type="button" class="a-btn a-btn--danger a-actions" data-testid="rollback-run"
                                            disabled="@(!RollbackGate.CanRollBack(_rollback, _allowed) || _busy)"
                                            @onclick="RollbackAsync">
                                        Roll back to r@(_selected.Version.Revision)
                                    </button>
                                }
                            </div>
```

In History's `@code`: add `private ManagementPlanSummary? _rollback;`; in `OpenAsync` also reset `_rollback = null;`; replace `RollbackAsync` with:

```csharp
    /// <summary>Asks the plan of the rollback as a dry run — Preview's rule: describing a drop destroys nothing.</summary>
    private async Task PlanRollbackAsync()
        => _rollback = (await SendRollbackAsync(dryRun: true, allowDestructive: true))?.Plan;

    /// <summary>Runs the rollback the plan on screen describes, with the permission the operator gave.</summary>
    private async Task RollbackAsync()
    {
        if (_rollback is null || await SendRollbackAsync(dryRun: false, RollbackGate.AllowDestructive(_rollback, _allowed)) is null)
        {
            return;
        }

        _selected = null;
        _rollback = null;
        _allowed = false;
        await LoadAsync();
    }

    private async Task<ManagementApplyResult?> SendRollbackAsync(bool dryRun, bool allowDestructive)
    {
        _busy = true;
        _problem = null;
        try
        {
            return await Gateway.RollbackAsync(
                _selected!.Version.Revision, _current, allowDestructive, dryRun,
                author: await Gateway.AuthorAsync(), reason: $"Restored r{_selected.Version.Revision}",
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _problem = AdminProblem.From(exception, Logger, ProblemSite.Rollback);
            return null;
        }
        finally
        {
            _busy = false;
        }
    }

    private static string RollbackTitle(ManagementPlanSummary plan) => plan.IsEmpty
        ? "No migration — the schema is the same"
        : $"{plan.Steps.Count} step{(plan.Steps.Count == 1 ? string.Empty : "s")} against the database";
```

(`reason: $"Restored r{…}"` interpolates an `int` exactly as the existing code does; if the analyzer objects, use `string.Create(CultureInfo.InvariantCulture, …)`.)

- [ ] **Step 4: Run** — `dotnet test --project test/MMLib.Alvo.Admin.Tests`. Expected: `RollbackGateTests` pass; `PublicApiApprovalTests` fails with a received file whose only difference is the new `PlanSteps` class. Accept it (copy the received over the verified baseline) **only if** that is the sole difference.

- [ ] **Step 5: Run the gates** — `scripts/test-admin-e2e` (Preview's steps now come from `PlanSteps`; `DestructivePlanScenarios` and `PendingWorkScenarios.Preview_plans_on_arrival_and_can_plan_again` drive that rendering), `scripts/test-ring1` → green. There is no rollback scenario: deviation 8.

- [ ] **Step 6: Mark the item done** — §8d item 18: `18. ✅ **Rollback is blind …` and append `**Done:** History asks the rollback's plan as a dry run and shows it with Preview's own step rendering (PlanSteps); a plan that destroys data asks its own typed confirm, and allowDestructive is sent only then (RollbackGate). No e2e: two revisions need a second in-process apply, which does not finish (ChangeTheBackendScenarios).`

- [ ] **Step 7: Normalise and commit** — files above by name, including the baseline:

```bash
git commit -m "feat(f5): a rollback is planned before it runs

History dry-runs the rollback, renders its plan with the steps Preview
draws, and sends allowDestructive only for a destructive plan the
operator confirmed. PlanSteps is a new component, extracted from Preview
so both plans render alike; the PublicApi baseline grows by it alone
(F-10: Components.* are implementation).

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---

### Task 5: Refusals reach their screens through an explicit map (items 19 + 20)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Internal/RefusalPlaces.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor.cs` (`FieldRefusals`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs` (`ActionRefusals` → `HookRefusals`) and `HooksTab.razor` (its summary line)
- Modify: `src/MMLib.Alvo.Admin/Components/Integrations/Integrations.razor` (`Refusals`, a test id per refusal)
- Modify: `src/MMLib.Alvo.Admin/Components/Shell/NotYet.razor` (`"automation"`, test ids, its refusals)
- Modify: `src/MMLib.Alvo.Admin/Components/Home/Overview.razor` (unplaced refusals)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor` (the `soft delete` badge) — shared file, only that `@if`
- Create: `test/MMLib.Alvo.Admin.Tests/Internal/RefusalPlacesTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/RefusalPlacementScenarios.cs`
- Modify: `docs/todo-admin.md` §8d items 19, 20

**Interfaces:**
- Produces (namespace `MMLib.Alvo.Admin.Internal`):

```csharp
internal enum RefusalScreen { FieldEditor, EntityHeader, OnWrite, Integrations, Automations, Functions }
internal static class RefusalPlaces
{
    public static IReadOnlyList<ManagementRefusedFeature> On(RefusalScreen screen, IReadOnlyList<ManagementRefusedFeature> refused);
    public static IReadOnlyList<ManagementRefusedFeature> Unplaced(IReadOnlyList<ManagementRefusedFeature> refused);
}
```

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Admin.Tests/Internal/RefusalPlacesTests.cs`:

```csharp
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Every refusal the build publishes is shown on the screen it is about — by an explicit map, because prefix
/// matching dropped four of them (docs/todo-admin.md §8d item 19): <c>entity.update</c> is an action, not an
/// entity facet, and <c>rollup.where</c>, <c>trigger.event</c>, <c>JSONata</c> and <c>bodyFile</c> matched nothing.
/// </summary>
public class RefusalPlacesTests
{
    /// <summary>The slots <c>UnhonouredFeatures.EveryRefusal</c> publishes at a1f1a57.</summary>
    [Theory]
    [InlineData("field.validation", RefusalScreen.FieldEditor)]
    [InlineData("field.default", RefusalScreen.FieldEditor)]
    [InlineData("rollup.where", RefusalScreen.FieldEditor)]
    [InlineData("entity.softDelete", RefusalScreen.EntityHeader)]
    [InlineData("function", RefusalScreen.OnWrite)]
    [InlineData("http.call", RefusalScreen.OnWrite)]
    [InlineData("entity.update", RefusalScreen.OnWrite)]
    [InlineData("JSONata", RefusalScreen.OnWrite)]
    [InlineData("JSONata", RefusalScreen.Integrations)]
    [InlineData("email.data", RefusalScreen.OnWrite)]
    [InlineData("email.data", RefusalScreen.Integrations)]
    [InlineData("bodyFile", RefusalScreen.Integrations)]
    [InlineData("trigger.event", RefusalScreen.Automations)]
    [InlineData("trigger.event", RefusalScreen.Functions)]
    public void A_published_slot_is_shown_on_its_screen(string slot, RefusalScreen screen)
        => RefusalPlaces.On(screen, [Refused(slot)]).ShouldHaveSingleItem().Slot.ShouldBe(slot);

    [Fact]
    public void An_action_type_is_not_mistaken_for_an_entity_facet()
        => RefusalPlaces.On(RefusalScreen.EntityHeader, [Refused("entity.update")]).ShouldBeEmpty();

    [Fact]
    public void A_slot_the_map_does_not_place_is_unplaced_rather_than_dropped()
    {
        var refused = new[] { Refused("entity.realtime"), Refused("bodyFile") };

        RefusalPlaces.Unplaced(refused).ShouldHaveSingleItem().Slot.ShouldBe("entity.realtime");
        Enum.GetValues<RefusalScreen>().ShouldAllBe(screen => RefusalPlaces.On(screen, refused).All(r => r.Slot != "entity.realtime"));
    }

    private static ManagementRefusedFeature Refused(string slot) => new(slot, "consequence", "fix");
}
```

- [ ] **Step 2: Run to verify it fails** — compile error, `RefusalPlaces` not found.

- [ ] **Step 3: Implement**

Create `src/MMLib.Alvo.Admin/Internal/RefusalPlaces.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>The screens a refusal is shown on.</summary>
internal enum RefusalScreen
{
    /// <summary>The field editor's refused-facets list.</summary>
    FieldEditor,

    /// <summary>The entity header, beside the flags.</summary>
    EntityHeader,

    /// <summary>The entity's On write tab.</summary>
    OnWrite,

    /// <summary>Integrations' "why there is no new endpoint button".</summary>
    Integrations,

    /// <summary>The Automations page.</summary>
    Automations,

    /// <summary>The Functions page.</summary>
    Functions,
}

/// <summary>
/// Which screen each refusal the build publishes belongs to — an explicit map, not a prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Prefix matching dropped four refusals on the floor</b> (docs/todo-admin.md §8d item 19): the field editor
/// admitted <c>field.*</c> and so never <c>rollup.where</c>; <c>trigger.event</c>, <c>JSONata</c> and <c>bodyFile</c>
/// matched no screen's prefix at all; and <c>entity.update</c> is an action type that an <c>entity.*</c> prefix would
/// have drawn on the entity header. A slot is a feature's own name, and only a table can say where it belongs.
/// </para>
/// <para>
/// <b>Admin-side, deliberately.</b> The core-side answer is an owner or area on <c>ManagementRefusedFeature</c>
/// itself — a port change, and #269's to decide. Until then a slot this map does not place is <see cref="Unplaced"/>,
/// which Overview shows and an end-to-end fact asserts is empty against the real host, so a new core slot is loud.
/// </para>
/// </remarks>
internal static class RefusalPlaces
{
    private static readonly Dictionary<string, RefusalScreen[]> _owners = new(StringComparer.Ordinal)
    {
        ["field.validation"] = [RefusalScreen.FieldEditor],
        ["field.default"] = [RefusalScreen.FieldEditor],
        ["rollup.where"] = [RefusalScreen.FieldEditor],
        ["entity.softDelete"] = [RefusalScreen.EntityHeader],
        ["function"] = [RefusalScreen.OnWrite],
        ["http.call"] = [RefusalScreen.OnWrite],
        ["entity.update"] = [RefusalScreen.OnWrite],
        ["JSONata"] = [RefusalScreen.OnWrite, RefusalScreen.Integrations],
        ["email.data"] = [RefusalScreen.OnWrite, RefusalScreen.Integrations],
        ["bodyFile"] = [RefusalScreen.Integrations],
        ["trigger.event"] = [RefusalScreen.Automations, RefusalScreen.Functions],
    };

    /// <summary>The refusals one screen shows, in the order the build published them.</summary>
    public static IReadOnlyList<ManagementRefusedFeature> On(RefusalScreen screen, IReadOnlyList<ManagementRefusedFeature> refused)
        => [.. refused.Where(refusal => _owners.TryGetValue(refusal.Slot, out var screens) && screens.Contains(screen))];

    /// <summary>The refusals no screen shows — a slot this build publishes and this map has not placed yet.</summary>
    public static IReadOnlyList<ManagementRefusedFeature> Unplaced(IReadOnlyList<ManagementRefusedFeature> refused)
        => [.. refused.Where(refusal => !_owners.ContainsKey(refusal.Slot))];
}
```

`FieldEditor.razor.cs`: `FieldRefusals => RefusalPlaces.On(RefusalScreen.FieldEditor, Refused);` (update its `<summary>`: "The refusals that belong to a field — its facets and its rollup — by `RefusalPlaces`.").

`HooksTab.razor.cs`: rename `ActionRefusals` to `HookRefusals` → `RefusalPlaces.On(RefusalScreen.OnWrite, Refused)`; its remark says the three action types plus the two refused forms of an after-action's values (`JSONata`, `email.data`). `HooksTab.razor`: both uses renamed; the summary line becomes:

```razor
            @HookRefusals.Count thing@(HookRefusals.Count == 1 ? "" : "s") this build refuses on a hook —
            <span class="a-mono">@string.Join(", ", HookRefusals.Select(refusal => refusal.Slot))</span>
```

`Integrations.razor`: `Refusals => _capabilities is null ? [] : RefusalPlaces.On(RefusalScreen.Integrations, _capabilities.Refused);` and each refusal paragraph gets `data-testid="@($"integrations-refused-{refusal.Slot}")"`.

`NotYet.razor`: `Block => IsFunctions ? "functions" : "automation";` with the comment `@* The build's own block name, singular (UnhonouredSubsystems.cs:127): "automations" found nothing, and the page denied the block existed (§8d item 20). *@`; `data-testid="not-yet-warned"` on the warned `NotYetPanel`, `data-testid="not-yet-unknown"` on the other; after the `@if/else` add:

```razor
    @foreach (var refusal in RefusalPlaces.On(IsFunctions ? RefusalScreen.Functions : RefusalScreen.Automations, _capabilities?.Refused ?? []))
    {
        <Refusal data-testid="@($"refused-{refusal.Slot}")">
            <code class="a-mono">@refusal.Slot</code> — @refusal.Consequence @refusal.Fix
        </Refusal>
    }
```

`Overview.razor`, after the "Declared, with limits" panel:

```razor
        @* A refusal no screen shows yet is a refusal nobody reads. RefusalPlaces explains why this is the fallback. *@
        @if (RefusalPlaces.Unplaced(_capabilities!.Refused) is { Count: > 0 } unplaced)
        {
            <Panel Title="Refused by this build, shown on no screen yet" data-testid="overview-unplaced">
                <ChildContent>
                    @foreach (var refusal in unplaced)
                    {
                        <ListRow>
                            <span class="a-listrow__type"><span class="a-badge a-badge--warn a-mono">@refusal.Slot</span></span>
                            <p class="a-note">@refusal.Consequence @refusal.Fix</p>
                        </ListRow>
                    }
                </ChildContent>
            </Panel>
        }
```

`Entity.razor` (shared — only this `@if`): replace `@if (_entity.SoftDelete) { <span class="a-badge">soft delete</span> }` with:

```razor
            @if (_entity.SoftDelete)
            {
                @* Only a pending entity can carry it — the apply refuses it (UnhonouredFeatures.OnAnEntity) — and a plain
                   badge read as a working flag (§8a softDelete row). *@
                @if (RefusalPlaces.On(RefusalScreen.EntityHeader, _refused).FirstOrDefault() is { } refusal)
                {
                    <Refusal data-testid="refused-entity.softDelete">
                        <code class="a-mono">softDelete</code> — @refusal.Consequence @refusal.Fix
                    </Refusal>
                }
                else
                {
                    <span class="a-badge">soft delete</span>
                }
            }
```

- [ ] **Step 4: Run the unit tests** — PASS.

- [ ] **Step 5: Write the e2e scenarios**

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/RefusalPlacementScenarios.cs`:

```csharp
namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Every refusal the real host publishes reaches a screen, and each "not yet" page finds its own warned block
/// (docs/todo-admin.md §8d items 19 and 20). Read-only: nothing is staged.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class RefusalPlacementScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary>The drift guard: a slot the core adds and <c>RefusalPlaces</c> does not place fails here.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task No_refusal_the_build_publishes_is_left_without_a_screen()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await session.Page.GetByTestId("overview-links").WaitForAsync();
        (await session.Page.GetByTestId("overview-unplaced").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }

    [Theory(Timeout = AdminWorld.ScenarioTimeout)]
    [InlineData("/automations")]
    [InlineData("/functions")]
    public async Task A_not_yet_page_finds_its_warned_block_and_shows_the_wildcard_refusal(string route)
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync(route);

        await session.Page.GetByTestId("not-yet-warned").WaitForAsync();
        (await session.Page.GetByTestId("not-yet-unknown").CountAsync()).ShouldBe(0);
        await session.Page.GetByTestId("refused-trigger.event").WaitForAsync();

        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Integrations_says_why_a_body_file_and_a_jsonata_payload_are_refused()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");

        await session.Page.GetByTestId("integrations-refused-bodyFile").WaitForAsync();
        await session.Page.GetByTestId("integrations-refused-JSONata").WaitForAsync();

        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_field_editor_lists_the_rollup_filter_among_its_refusals()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/customers");

        await session.Page.GetByTestId("add-field").ClickAsync();
        (await session.Page.GetByTestId("field-sheet").GetByTestId("refused-rollup.where").CountAsync()).ShouldBe(1);

        session.AssertConsoleClean();
    }
}
```

(`GoAsync("")` opens `/admin`, which is Overview; `overview-links` is its own nav's test id.)

- [ ] **Step 6: Run the gates** — `scripts/test-admin-e2e` (includes `HookEditingScenarios.The_tab_says_which_action_types_this_build_refuses`, which reads `hooks-refused` and still finds `entity.update` and `http.call`; `PhoneAndKeyboardScenarios` walks `/automations` and `/functions` at 375 px with the new refusal rows), `scripts/test-ring1` → green.

- [ ] **Step 7: Mark the items done** — §8d item 19: `19. ✅ **Four refusals …` + `**Done:** RefusalPlaces maps every published slot to its screens (field editor: rollup.where; On write: JSONata, email.data; Integrations: bodyFile; Automations/Functions: trigger.event; entity header: softDelete), and a slot it does not place is shown on Overview — an e2e fact fails on one. The core-side owner field stays #269's.`; item 20: `20. ✅ **The Automations page …` + `**Done:** the page reads "automation", and an e2e fact asserts both not-yet pages find their warned block.`

- [ ] **Step 8: Normalise and commit** (files above by name):

```bash
git commit -m "fix(f5): every refusal the build publishes reaches its screen

An explicit slot-to-screen map replaces prefix matching, which dropped
rollup.where, trigger.event, JSONata and bodyFile; unplaced slots show on
Overview and fail an e2e fact. The Automations page reads "automation".

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---

### Task 6: Staged rows keep their facets; pending entities and staged fields are choosable (item 22 rest + item 28)

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/PendingSchema.cs` (resolved tenancy; `Indexed`, `Default`, `Nullable`)
- Create: `src/MMLib.Alvo.Admin/Components/Schema/FieldBadges.cs` (moved from `Fields.razor`, plus the nullable rule)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Fields.razor` (use `FieldBadges.Of`; delete `Facets` and `Literal`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor.cs` (`_entities` from the working copy; `IndexView`) — shared file, only these lines
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor` (`<Indexes Entity="@IndexView(_entity)" …>`) — shared file, only this attribute
- Modify: `test/MMLib.Alvo.Admin.Tests/Schema/PendingSchemaTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/FieldBadgesTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/StagedFieldScenarios.cs`
- Modify: `docs/todo-admin.md` §8d items 22, 28

**Interfaces:**
- Consumes (Task 2): `PendingSchema` already reads `computed`/`rollup`.
- Produces: `internal static class FieldBadges { public static IEnumerable<string> Of(FieldSchema field); }`

- [ ] **Step 1: Write the failing tests**

Append to `PendingSchemaTests`:

```csharp
    [Fact]
    public void A_pending_entity_without_tenancy_is_scoped_when_the_project_turns_tenancy_on()
        => PendingSchema.Read("""{"tenancy":{"enabled":true},"entities":{"tickets":{"fields":{"t":{"type":"string"}}}}}""", "tickets")!
            .Tenancy.ShouldBe(TenancyMode.Scoped);

    [Fact]
    public void A_pending_entity_declared_global_stays_global_whatever_the_project_says()
        => PendingSchema.Read("""{"tenancy":{"enabled":true},"entities":{"tickets":{"tenancy":"global","fields":{"t":{"type":"string"}}}}}""", "tickets")!
            .Tenancy.ShouldBe(TenancyMode.Global);

    [Fact]
    public void A_pending_entity_without_tenancy_in_a_project_without_it_carries_none()
        => PendingSchema.Read("""{"entities":{"tickets":{"fields":{"t":{"type":"string"}}}}}""", "tickets")!
            .Tenancy.ShouldBeNull();

    [Fact]
    public void A_staged_field_keeps_its_index_its_default_and_its_nullability()
    {
        var field = PendingSchema.Read(
            """{"entities":{"t":{"fields":{"state":{"type":"string","index":true,"default":"open","nullable":false}}}}}""", "t")!
            .Fields.Single();

        field.Indexed.ShouldBeTrue();
        field.Default.ShouldNotBeNull().GetString().ShouldBe("open");
        field.Nullable.ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"type":"string","required":true}""", false)]
    [InlineData("""{"type":"string"}""", true)]
    public void Nullability_is_derived_from_required_when_it_is_not_declared(string declared, bool nullable)
        => PendingSchema.Read("""{"entities":{"t":{"fields":{"f":""" + declared + "}}}}", "t")!.Fields.Single()
            .Nullable.ShouldBe(nullable);

    [Fact]
    public void A_cel_default_is_not_a_literal_and_is_left_out_as_the_mapper_leaves_it_out()
        => PendingSchema.Read("""{"entities":{"t":{"fields":{"at":{"type":"datetime","default":{"$cel":"now()"}}}}}}""", "t")!
            .Fields.Single().Default.ShouldBeNull();
```

Create `test/MMLib.Alvo.Admin.Tests/Schema/FieldBadgesTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The Fields tab's badges: every one read off <see cref="FieldSchema"/>, never a guess.</summary>
public class FieldBadgesTests
{
    [Fact]
    public void A_field_is_badged_with_what_changes_what_a_caller_may_send_first()
        => FieldBadges.Of(new FieldSchema
        {
            Name = "code", Type = FieldType.String, Required = true, Unique = true, MaxLength = 12, Indexed = true,
            Default = JsonDocument.Parse("\"X\"").RootElement.Clone(),
        }).ShouldBe(["required", "default \"X\"", "unique", "max 12", "indexed"]);

    /// <summary>Only a nullability that differs from the one <c>required</c> implies is badged — anything else is noise on every row.</summary>
    [Theory]
    [InlineData(true, true, "nullable")]
    [InlineData(false, false, "not null")]
    public void An_explicit_nullability_is_badged(bool required, bool nullable, string badge)
        => FieldBadges.Of(new FieldSchema { Name = "f", Type = FieldType.Text, Required = required, Nullable = nullable })
            .ShouldContain(badge);

    [Fact]
    public void A_derived_nullability_is_not_badged()
        => FieldBadges.Of(new FieldSchema { Name = "f", Type = FieldType.Text, Nullable = true }).ShouldBeEmpty();

    [Fact]
    public void A_rollup_and_a_computed_field_say_so()
    {
        FieldBadges.Of(new FieldSchema
        {
            Name = "n", Type = FieldType.Integer, Nullable = true,
            Rollup = new RollupSchema { From = "lines", Op = RollupOperation.Count, Via = string.Empty },
        }).ShouldBe(["rollup"]);
        FieldBadges.Of(new FieldSchema { Name = "t", Type = FieldType.Integer, Nullable = true, ComputedExpression = "a + a" })
            .ShouldBe(["computed"]);
    }
}
```

- [ ] **Step 2: Run to verify they fail** — compile error (`FieldBadges`); the tenancy/index/default/nullable facts fail.

- [ ] **Step 3: Implement**

`PendingSchema.cs`: split `Read` so it stays short, resolve tenancy, and read the three facets:

```csharp
        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("entities", out var entities) || !entities.TryGetProperty(entity, out var declared))
            {
                return null;
            }

            var enabled = root.TryGetProperty("tenancy", out var tenancy) && tenancy.ValueKind == JsonValueKind.Object
                && Flag(tenancy, "enabled");
            return Entity(entity, declared, enabled);
        }
```

```csharp
    private static EntitySchema Entity(string entity, JsonElement declared, bool tenancyEnabled) => new()
    {
        Name = entity,
        Description = String(declared, "description"),
        Tenancy = Tenancy(String(declared, "tenancy"), tenancyEnabled),
        Audit = Flag(declared, "audit"),
        SoftDelete = Flag(declared, "softDelete"),
        Fields = Fields(declared),
    };

    /// <summary>
    /// The mapper's own rule (<c>DescriptorToSchemaMapper.ResolveTenancy</c>): the declared tenancy, else scoped when
    /// the project turns tenancy on, else none — a pending entity with no key used to be drawn global in a project
    /// that would apply it scoped (§8a <c>tenancy.enabled</c> row).
    /// </summary>
    private static TenancyMode? Tenancy(string? declared, bool enabled) => declared switch
    {
        "scoped" => TenancyMode.Scoped,
        "global" => TenancyMode.Global,
        _ => enabled ? TenancyMode.Scoped : null,
    };
```

Add to the field initializer: `Indexed = Flag(field.Value, "index"), Nullable = NullableOf(field.Value), Default = Literal(field.Value),` and:

```csharp
    /// <summary>The declared nullability, or the one <c>required</c> implies — the mapper's <c>f.Nullable ?? f.Required != true</c>.</summary>
    private static bool NullableOf(JsonElement field)
        => field.TryGetProperty("nullable", out var declared) && declared.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? declared.ValueKind == JsonValueKind.True
            : !Flag(field, "required");

    /// <summary>
    /// The declared literal default, cloned to outlive the document. A <c>$cel</c> object is not one — the build
    /// refuses it and the mapper resolves it to nothing (<c>FieldDefault.Resolve</c>) — so it is left out here too.
    /// </summary>
    private static JsonElement? Literal(JsonElement field)
        => field.TryGetProperty("default", out var declared) && declared.ValueKind != JsonValueKind.Null
            && !(declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty("$cel", out _))
            ? declared.Clone()
            : null;
```

Create `src/MMLib.Alvo.Admin/Components/Schema/FieldBadges.cs` — the body of `Fields.razor`'s `Facets` and `Literal`, moved verbatim, with the nullable rule after `required`:

```csharp
using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// The facets a field actually carries, as the Fields tab badges them — every one read off <see cref="FieldSchema"/>.
/// </summary>
/// <remarks>
/// Out of <c>Fields.razor</c> so it can be tested (the F-13 rule), and so an applied row and a staged row are badged
/// by one function: a staged row losing its <c>rollup</c> or <c>indexed</c> badge was the renderer's defect, not
/// the model's (docs/todo-admin.md §8d item 22).
/// </remarks>
internal static class FieldBadges
{
    private const int LiteralBadgeLimit = 24;

    /// <summary>The badges, ordered so the ones that change what a caller may send come first.</summary>
    public static IEnumerable<string> Of(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return [.. Constraints(field), .. Storage(field), .. Maintenance(field)];
    }

    private static IEnumerable<string> Constraints(FieldSchema field)
    {
        if (field.Required) { yield return "required"; }

        /* Only a nullability that differs from the one `required` implies — anything else would badge every row. */
        if (field.Required && field.Nullable) { yield return "nullable"; }
        if (!field.Required && !field.Nullable) { yield return "not null"; }
        if (field.Default is { } literal) { yield return $"default {Literal(literal)}"; }
        if (field.Unique) { yield return "unique"; }
        if (field.Format is { Length: > 0 } format) { yield return format; }
    }

    private static IEnumerable<string> Storage(FieldSchema field)
    {
        if (field.MaxLength is { } max) { yield return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"max {max}"); }
        if (field.Precision is { } precision)
        {
            yield return field.Scale is { } scale
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{precision},{scale}")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"precision {precision}");
        }

        if (field.EnumValues is { Count: > 0 } values) { yield return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{values.Count} values"); }
        if (field.Reference is { } reference) { yield return $"on delete {reference.OnDelete.ToString().ToLowerInvariant()}"; }
    }

    private static IEnumerable<string> Maintenance(FieldSchema field)
    {
        if (field.ComputedExpression is { Length: > 0 }) { yield return "computed"; }
        if (field.Rollup is not null) { yield return "rollup"; }
        if (field.Indexed) { yield return "indexed"; }
    }

    /// <summary>The declared default as the descriptor holds it — raw JSON, so a string reads quoted — short enough for a badge.</summary>
    private static string Literal(JsonElement literal)
    {
        var text = literal.GetRawText();
        return text.Length <= LiteralBadgeLimit ? text : text[..LiteralBadgeLimit] + "…";
    }
}
```

(Expand the one-line `if { yield return … }` bodies to the repository's brace style if `dotnet format` asks; keep the order exactly: required, nullable/not null, default, unique, format, max, precision, values, on delete, computed, rollup, indexed — the existing order with the nullable rule added.)

`Fields.razor`: `@foreach (var facet in Facets(field))` → `@foreach (var facet in FieldBadges.Of(field))`; delete `LiteralBadgeLimit`, `Literal` and `Facets` from its `@code`.

`Entity.razor.cs` (shared — only these edits): keep the applied-names line in `ReadAsync` as the fallback for a copy that is not loaded; at the start of `ReadWorking()` add:

```csharp
        /* Ref targets from the working copy, so an entity staged a moment ago can be pointed at (§8d item 28). */
        if (Copy.Loaded)
        {
            _entities = Copy.Entities;
        }
```

and add:

```csharp
    /// <summary>
    /// The entity as the Indexes tab offers it: the working copy's declared fields — a staged one included, a
    /// staged-removed one not — then the columns Alvo maintains (§8d item 28; §8a <c>indexes[].fields</c> row).
    /// </summary>
    private EntitySchema IndexView(EntitySchema entity)
    {
        if (!Copy.Loaded || PendingSchema.Read(Copy.Json, EntityName) is not { } working)
        {
            return entity;
        }

        var managed = AlvoManagedColumns.For(entity);
        return entity with
        {
            Fields = [.. working.Fields.Where(field => !managed.Contains(field.Name)), .. entity.Fields.Where(field => managed.Contains(field.Name))],
        };
    }
```

`Entity.razor` (shared — only this attribute): `<Indexes Entity="@_entity" …` → `<Indexes Entity="@IndexView(_entity)" …`.

- [ ] **Step 4: Run the unit tests** — PASS.

- [ ] **Step 5: Write the e2e scenarios**

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/StagedFieldScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// What is staged is drawn and choosable like what is applied (docs/todo-admin.md §8d items 22 and 28).
/// Its own world: it stages.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class StagedFieldScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_staged_field_keeps_its_badges_and_can_join_an_index()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/work_orders");

        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("zone");
        await sheet.GetByRole(AriaRole.Textbox, new() { Name = "Default value" }).FillAsync("north");
        await sheet.GetByRole(AriaRole.Checkbox, new() { Name = "indexed", Exact = true }).CheckAsync();
        await session.Page.GetByTestId("field-save").ClickAsync();

        var row = session.Page.GetByTestId("field-row-zone");
        await row.WaitForAsync();
        var badges = await row.InnerTextAsync();
        badges.ShouldContain("default \"north\"");
        badges.ShouldContain("indexed");

        await session.OpenTabAsync("Indexes");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "zone", Exact = true }).WaitForAsync();

        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_pending_entity_can_be_pointed_at()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("tickets");
        await session.Button("Add to the working copy").ClickAsync();
        await session.Page.WaitForURLAsync("**/schema/tickets");

        await session.GoAsync("/schema/customers");
        await session.Page.GetByTestId("add-field").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "ref", Exact = true }).ClickAsync();
        await sheet.GetByRole(AriaRole.Radio, new() { Name = "tickets", Exact = true }).WaitForAsync();

        session.AssertConsoleClean();
    }
}
```

- [ ] **Step 6: Run the gates** — `scripts/test-admin-e2e` (`SchemaScenarios.A_fields_facets_are_the_ones_the_descriptor_gave_it` reads applied badges — unchanged for field-service, which declares no explicit `nullable`), `scripts/test-ring1` → green.

- [ ] **Step 7: Mark the items done** — §8d item 22: `22. ✅ **The staged view …` + `**Done:** PendingSchema reads index, default (literal only), nullable, rollup and computed, and resolves a missing tenancy through tenancy.enabled; applied and staged rows are badged by one FieldBadges, which also badges an explicit nullability.`; item 28: `28. ✅ **Ref targets …` + `**Done:** ref targets and index candidates come from the working copy (users is not offered — unverified).`

- [ ] **Step 8: Normalise and commit** (files above by name):

```bash
git commit -m "fix(f5): staged rows keep their facets and staged names are choosable

PendingSchema reads what the applied schema carries and resolves tenancy
the mapper's way; one FieldBadges draws both kinds of row; ref targets and
index candidates are read from the working copy.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---

### Task 7: An entity can be removed, and what points at it is named (item 24)

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/EntityReferences.cs` (`Inbound`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Entities.cs` (`ReferencesToEntity`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.References.cs` (entity-removal handlers)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor` (Remove button beside Rename; the sheet) — shared file, only these lines
- Create: `test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyEntityRemovalTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/RemoveEntityScenarios.cs`
- Modify: `docs/todo-admin.md` §8d item 24

**Interfaces:**
- Consumes (Task 3): `DescriptorReference`, `Entity.References.cs`.
- Produces: `EntityReferences.Inbound(JsonObject root, string entity) → IReadOnlyList<DescriptorReference>`; `WorkingCopy.ReferencesToEntity(string entity) → IReadOnlyList<DescriptorReference>`.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyEntityRemovalTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// What points at an entity, asked before it is removed — the <see cref="EntityReferences"/> places, inbound
/// (docs/todo-admin.md §8d item 24).
/// </summary>
public class WorkingCopyEntityRemovalTests
{
    [Fact]
    public void A_ref_to_the_entity_blocks_its_removal()
    {
        var copy = new WorkingCopy();
        copy.Take(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", "field-service", "field-service.alvo.json")), 1);

        copy.ReferencesToEntity("regions").ShouldBe([new DescriptorReference("work_orders.region_id", Blocks: true)]);
    }

    [Fact]
    public void A_rollup_and_an_entity_update_block_it_and_a_trigger_is_named()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 1);

        copy.ReferencesToEntity("lines").ShouldBe(
        [
            new DescriptorReference("orders.total rollup", Blocks: true),
            new DescriptorReference("entities.orders.hooks.afterUpdate[0].action", Blocks: true),
            new DescriptorReference("automation.recount.trigger", Blocks: false),
        ], ignoreOrder: true);
    }

    /// <summary><c>orders.parent_id</c> is a self-ref: it goes with the entity and is not named.</summary>
    [Fact]
    public void A_self_ref_goes_with_the_entity_and_is_not_named()
        => Copy().ReferencesToEntity("orders").ShouldNotContain(reference => reference.Place == "orders.parent_id");

    [Fact]
    public void A_child_that_points_at_the_entity_blocks_it_even_when_the_entity_points_back()
        => Copy().ReferencesToEntity("orders").ShouldContain(new DescriptorReference("lines.order_id", Blocks: true));

    [Fact]
    public void Removing_an_entity_nothing_points_at_leaves_the_rest_as_it_was()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 1);
        copy.AddEntity("tickets", scoped: false, audited: false);

        copy.ReferencesToEntity("tickets").ShouldBeEmpty();
        copy.RemoveEntity("tickets");
        copy.IsDirty.ShouldBeFalse();
    }

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 1);
        return copy;
    }

    private const string Descriptor = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "shop",
          "entities": {
            "orders": {
              "fields": {
                "total": { "type": "decimal", "precision": 10, "scale": 2, "rollup": { "op": "sum", "from": "lines", "field": "amount" } },
                "parent_id": { "type": "ref", "entity": "orders" }
              },
              "hooks": { "afterUpdate": [ { "action": { "type": "entity.update", "entity": "lines", "payload": { "a": 1 } } } ] }
            },
            "lines": {
              "fields": { "order_id": { "type": "ref", "entity": "orders" }, "amount": { "type": "decimal", "precision": 10, "scale": 2 } },
              "hooks": { "afterCreate": [ { "action": { "type": "entity.update", "entity": "lines", "payload": { "a": 1 } } } ] }
            }
          },
          "automation": { "recount": { "trigger": { "event": "entity.lines.created" }, "actions": [] } }
        }
        """;
}
```

(The walk's paths, worked by hand: the `entity.update` inside `orders` is reached as `entities` → `entities.orders` → `…hooks` → `…afterUpdate[0]` → `…action`; the one inside `lines`' own hooks is skipped with `lines`; the trigger's node is `automation.recount.trigger`, which is the path at which its `event` is read.)

- [ ] **Step 2: Run to verify it fails** — compile error, `ReferencesToEntity` not found.

- [ ] **Step 3: Implement**

Append to `EntityReferences.cs`:

```csharp
    /// <summary>
    /// Every place outside the entity that names it, and whether removing the entity would leave the apply refusing
    /// the descriptor — a ref (<c>DescriptorValidator.cs:419</c>), a rollup (<c>RollupResolver.cs:186</c>) or an
    /// <c>entity.update</c> action does; a trigger pattern is named and does not block (automation is warned).
    /// </summary>
    /// <remarks>What the entity declares about itself — a self-ref, its own hooks — goes with it and is not named.</remarks>
    /// <param name="root">The working document.</param>
    /// <param name="entity">The entity to be removed.</param>
    public static IReadOnlyList<DescriptorReference> Inbound(JsonObject root, string entity)
    {
        var found = new List<DescriptorReference>();
        if (root["entities"] is JsonObject entities)
        {
            foreach (var (owner, node) in entities.Where(pair => pair.Key != entity))
            {
                InboundFields(owner, node?["fields"] as JsonObject, entity, found);
            }
        }

        InboundActions(root, string.Empty, entity, found);
        return found;
    }

    private static void InboundFields(string owner, JsonObject? fields, string entity, List<DescriptorReference> found)
    {
        foreach (var (name, node) in fields ?? [])
        {
            if (Is(node?["type"], "ref") && Is(node?["entity"], entity))
            {
                found.Add(new($"{owner}.{name}", Blocks: true));
            }

            if (node?["rollup"] is JsonObject rollup && Is(rollup["from"], entity))
            {
                found.Add(new($"{owner}.{name} rollup", Blocks: true));
            }
        }
    }

    private static void InboundActions(JsonObject node, string path, string entity, List<DescriptorReference> found)
    {
        if (Is(node["type"], "entity.update") && Is(node["entity"], entity))
        {
            found.Add(new(path, Blocks: true));
        }

        if (node["event"] is JsonValue value && value.TryGetValue<string>(out var pattern)
            && pattern.StartsWith($"entity.{entity}.", StringComparison.Ordinal))
        {
            found.Add(new(path, Blocks: false));
        }

        foreach (var (key, child) in node.Where(pair => pair.Key != "payload" && !(path == "entities" && pair.Key == entity)))
        {
            foreach (var (at, item) in Items(key, child))
            {
                InboundActions(item, path.Length == 0 ? at : $"{path}.{at}", entity, found);
            }
        }
    }

    private static IEnumerable<(string At, JsonObject Item)> Items(string key, JsonNode? child) => child switch
    {
        JsonObject single => [(key, single)],
        JsonArray list => list.Select((item, i) => (At: string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{key}[{i}]"), Item: item as JsonObject))
            .Where(pair => pair.Item is not null).Select(pair => (pair.At, pair.Item!)),
        _ => [],
    };
```

Add to `WorkingCopy.Entities.cs`:

```csharp
    /// <summary>What points at an entity from outside it, and which of those would make the apply refuse its removal.</summary>
    public IReadOnlyList<DescriptorReference> ReferencesToEntity(string entity)
        => Read<IReadOnlyList<DescriptorReference>>(
            () => _working is JsonObject root ? EntityReferences.Inbound(root, entity) : []);
```

and update `RemoveEntity`'s summary: `/// <summary>Removes an entity. The screen asks <see cref="ReferencesToEntity"/> first.</summary>`.

Append to `Entity.References.cs`:

```csharp
    private bool _removingEntity;
    private bool _removeConfirmed;
    private IReadOnlyList<DescriptorReference> _inbound = [];

    /// <summary>Opens the removal sheet with what points at this entity.</summary>
    /// <remarks>
    /// <c>WorkingCopy.RemoveEntity</c> was written and no screen called it — the §5f <c>Discard</c> pattern again
    /// (docs/todo-admin.md §8d item 24).
    /// </remarks>
    private void OpenRemoveEntity()
    {
        _inbound = Copy.ReferencesToEntity(EntityName);
        _removeConfirmed = false;
        _removingEntity = true;
    }

    private void CloseRemoveEntity() => _removingEntity = false;

    /// <summary>Removes the entity from the working copy and leaves its address, which now names nothing.</summary>
    private void RemoveEntity()
    {
        if (!_removeConfirmed || Copy.ReferencesToEntity(EntityName).Any(reference => reference.Blocks))
        {
            return;
        }

        Copy.RemoveEntity(EntityName);
        _removingEntity = false;
        Navigation.NavigateTo(AdminPaths.Schema);
    }
```

`Entity.razor` (shared — only these edits): in `<Secondary>`, inside the `@if (Copy.Loaded)` after the Rename button:

```razor
                @if (DeclaredHere)
                {
                    <button type="button" class="a-btn a-btn--sm" data-testid="remove-entity" @onclick="OpenRemoveEntity">Remove</button>
                }
```

and after the field-removal sheet:

```razor
    <Sheet Open="_removingEntity" Title="@($"Remove {EntityName}")" TestId="remove-entity-sheet" OnClose="CloseRemoveEntity"
           Subtitle="It leaves the working copy. The apply drops its table and every row in it, and asks again before it does.">
        <div class="a-stack">
            @foreach (var reference in _inbound)
            {
                <ListRow data-testid="entity-reference">
                    <code class="a-mono a-listrow__key">@reference.Place</code>
                    <span class="@($"a-badge {(reference.Blocks ? "a-badge--danger" : "a-badge--warn")}")">
                        @(reference.Blocks ? "points here" : "names it")
                    </span>
                </ListRow>
            }

            @if (_inbound.Any(reference => reference.Blocks))
            {
                <Refusal data-testid="remove-entity-blocked">
                    A ref, a rollup or an action still points here, and the apply refuses a descriptor that points at an
                    entity it does not declare. Change or remove them first.
                </Refusal>
            }
            else
            {
                <ConfirmByName Title="@($"This removes {EntityName}")"
                               Detail="Nothing runs yet — the preview states the drop as a destructive step, and the apply asks for it again."
                               Expected="@EntityName" OnConfirmedChanged="confirmed => _removeConfirmed = confirmed" />
                <div class="a-row">
                    <button type="button" class="a-btn a-btn--danger a-btn--sm" data-testid="remove-entity-confirm"
                            disabled="@(!_removeConfirmed)" @onclick="RemoveEntity">Remove from the working copy</button>
                </div>
            }
        </div>
    </Sheet>
```

- [ ] **Step 4: Run the unit tests** — PASS.

- [ ] **Step 5: Write the e2e scenarios**

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/RemoveEntityScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>An entity can be removed from the working copy, behind its own name (docs/todo-admin.md §8d item 24).</summary>
/// <param name="world">The running host and browser.</param>
public sealed class RemoveEntityScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    /// <summary><c>work_orders.region_id</c> points at <c>regions</c>: named, and the removal refused.</summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_something_points_at_names_it_and_is_not_removed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");

        await session.Page.GetByTestId("remove-entity").ClickAsync();
        var sheet = session.Page.GetByTestId("remove-entity-sheet");
        (await sheet.GetByTestId("entity-reference").InnerTextAsync()).ShouldContain("work_orders.region_id");
        await sheet.GetByTestId("remove-entity-blocked").WaitForAsync();
        (await sheet.GetByTestId("remove-entity-confirm").CountAsync()).ShouldBe(0);

        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_entity_nothing_points_at_is_removed_after_its_name_is_typed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync("tickets");
        await session.Button("Add to the working copy").ClickAsync();
        await session.Page.WaitForURLAsync("**/schema/tickets");

        await session.Page.GetByTestId("remove-entity").ClickAsync();
        var sheet = session.Page.GetByTestId("remove-entity-sheet");
        var confirm = sheet.GetByTestId("remove-entity-confirm");
        (await confirm.IsDisabledAsync()).ShouldBeTrue();
        await sheet.GetByRole(AriaRole.Textbox).FillAsync("tickets");
        await confirm.ClickAsync();

        await session.Page.WaitForURLAsync("**/admin/schema");
        (await session.Content.InnerTextAsync()).ShouldNotContain("tickets");

        session.AssertConsoleClean();
    }
}
```

- [ ] **Step 6: Run the gates** — `scripts/test-admin-e2e`, `scripts/test-ring1` → green (the header's secondary actions fold into the overflow menu below 720 px; `PhoneAndKeyboardScenarios` covers that the page still fits).

- [ ] **Step 7: Mark the item done** — §8d item 24: `24. ✅ **An entity cannot be removed.** …` + `**Done:** Remove sits beside Rename; the sheet names every ref, rollup, entity.update action and trigger that points at the entity (EntityReferences.Inbound), refuses while one would make the apply refuse, and otherwise asks for the entity's name before calling WorkingCopy.RemoveEntity.`

- [ ] **Step 8: Normalise and commit** (files above by name):

```bash
git commit -m "feat(f5): an entity can be removed, and what points at it is named

WorkingCopy.RemoveEntity had no caller. The Remove sheet names the inbound
refs, rollups, actions and triggers, refuses while the apply would, and
confirms by name.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---

### Task 8: The people list searches and pages (item 23)

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Access/PeoplePaging.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Access/Access.razor`
- Create: `test/MMLib.Alvo.Admin.Tests/Access/PeoplePagingTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/PeoplePagingScenarios.cs`
- Modify: `docs/todo-admin.md` §8d item 23

**Interfaces:**
- Produces (namespace `MMLib.Alvo.Admin.Components.Access`):

```csharp
internal sealed class PeoplePaging
{
    public string Search { get; }
    public bool HasEarlier { get; }
    public AlvoUserQuery Query { get; }
    public void Find(string search);
    public void Next(string cursor);
    public void Previous();
}
```

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Admin.Tests/Access/PeoplePagingTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Access;

namespace MMLib.Alvo.Admin.Tests.Access;

/// <summary>
/// The people list asks the port's own <c>Search</c> and <c>After</c> — it used to ask for the first 50 and stop
/// (docs/todo-admin.md §8d item 23; <c>IAlvoUserAdministration.cs:125</c>).
/// </summary>
public class PeoplePagingTests
{
    [Fact]
    public void The_first_page_asks_for_everyone_from_the_start()
        => new PeoplePaging().Query.ShouldBe(new AlvoUserQuery(Search: null, Limit: 50, After: null));

    [Fact]
    public void A_search_is_trimmed_and_starts_from_the_first_page()
    {
        var paging = new PeoplePaging();
        paging.Next("c1");

        paging.Find("  dispatcher ");

        paging.Query.ShouldBe(new AlvoUserQuery("dispatcher", 50, After: null));
        paging.HasEarlier.ShouldBeFalse();
    }

    [Fact]
    public void Next_and_previous_walk_the_cursors_back_to_the_start()
    {
        var paging = new PeoplePaging();

        paging.Next("c1");
        paging.Next("c2");
        paging.Query.After.ShouldBe("c2");

        paging.Previous();
        paging.Query.After.ShouldBe("c1");
        paging.Previous();
        paging.Query.After.ShouldBeNull();
        paging.HasEarlier.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_search_is_no_search()
    {
        var paging = new PeoplePaging();
        paging.Find("   ");

        paging.Query.Search.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails** — compile error, `PeoplePaging` not found.

- [ ] **Step 3: Implement**

Create `src/MMLib.Alvo.Admin/Components/Access/PeoplePaging.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Access;

/// <summary>
/// Where the people list is: a search, and the cursors that led to the page on screen.
/// </summary>
/// <remarks>
/// <b>Cursors, not page numbers</b>, because the port pages by an opaque <c>After</c> (<c>AlvoUserQuery</c>) and says
/// only where the next page starts. Going back is therefore the cursor that led here, kept on a stack; a new search
/// starts from the first page, because a cursor belongs to the query that produced it.
/// </remarks>
internal sealed class PeoplePaging
{
    /// <summary>The port's own page size.</summary>
    private const int Limit = 50;

    private readonly Stack<string?> _earlier = new();
    private string? _after;

    /// <summary>The address fragment searched for, or empty.</summary>
    public string Search { get; private set; } = string.Empty;

    /// <summary>Whether there is a page before this one.</summary>
    public bool HasEarlier => _earlier.Count > 0;

    /// <summary>What to ask the port for.</summary>
    public AlvoUserQuery Query => new(Search.Length > 0 ? Search : null, Limit, _after);

    /// <summary>Searches from the first page.</summary>
    public void Find(string search)
    {
        Search = search.Trim();
        _after = null;
        _earlier.Clear();
    }

    /// <summary>Moves to the page that starts at <paramref name="cursor"/>.</summary>
    public void Next(string cursor)
    {
        _earlier.Push(_after);
        _after = cursor;
    }

    /// <summary>Moves back to the page before this one.</summary>
    public void Previous()
    {
        if (_earlier.Count > 0)
        {
            _after = _earlier.Pop();
        }
    }
}
```

`Access.razor`: add `private readonly PeoplePaging _paging = new();`; in `LoadAsync` replace `new AlvoUserQuery()` with `_paging.Query`; inside the People panel's `else` branch, **before** the `@foreach (var person …)`:

```razor
                <div class="a-row a-row--gap-2 a-row--inset">
                    <input class="a-input a-input--grow" type="search" aria-label="Find a person by address"
                           placeholder="Find by address" value="@_paging.Search" data-testid="people-search"
                           @onchange="args => FindAsync(args.Value?.ToString() ?? string.Empty)" />
                </div>
```

and **after** the `@foreach`, before the "Add a person" foot:

```razor
                @if (_paging.HasEarlier || _people.NextCursor is not null)
                {
                    <div class="a-row a-row--gap-2 a-row--inset">
                        @if (_paging.HasEarlier)
                        {
                            <button type="button" class="a-btn a-btn--sm" data-testid="people-previous" @onclick="PreviousAsync">Previous page</button>
                        }
                        @if (_people.NextCursor is { } next)
                        {
                            <button type="button" class="a-btn a-btn--sm" data-testid="people-next" @onclick="() => NextAsync(next)">Next page</button>
                        }
                    </div>
                }
```

and in `@code`:

```csharp
    private Task FindAsync(string search)
    {
        _paging.Find(search);
        return LoadAsync();
    }

    private Task NextAsync(string cursor)
    {
        _paging.Next(cursor);
        return LoadAsync();
    }

    private Task PreviousAsync()
    {
        _paging.Previous();
        return LoadAsync();
    }
```

- [ ] **Step 4: Run the unit tests** — PASS.

- [ ] **Step 5: Write the e2e scenario**

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/PeoplePagingScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// People past the first 50 are reachable, by page and by search (docs/todo-admin.md §8d item 23).
/// </summary>
/// <remarks>
/// Fifty-one people are seeded through the unguarded administration — the test standing in for a directory that
/// grew, as <c>RevokedSessionScenarios</c> stands in for a second administrator. The list is ordered by address, so
/// the bootstrap administrator and <c>person-00</c>…<c>person-48</c> fill the first page. Its own world.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class PeoplePagingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_next_page_and_a_search_reach_everybody()
    {
        await SeedAsync(count: 51);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        (await session.Content.InnerTextAsync()).ShouldNotContain("person-50@alvo.test");
        await session.Page.GetByTestId("people-next").ClickAsync();
        await session.Page.GetByTestId("people-previous").WaitForAsync();
        (await session.Content.InnerTextAsync()).ShouldContain("person-50@alvo.test");

        var search = session.Page.GetByRole(AriaRole.Searchbox, new() { Name = "Find a person by address" });
        await search.FillAsync("person-07");
        await search.BlurAsync();
        await session.Page.GetByTestId("people-previous").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var found = await session.Content.InnerTextAsync();
        found.ShouldContain("person-07@alvo.test");
        found.ShouldNotContain("person-08@alvo.test");

        session.AssertConsoleClean();
    }

    private async Task SeedAsync(int count)
    {
        using var scope = world.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);
        for (var i = 0; i < count; i++)
        {
            await people.CreateAsync(new AlvoUserCreation(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"person-{i:D2}@alvo.test"), []));
        }
    }
}
```

- [ ] **Step 6: Run the gates** — `scripts/test-admin-e2e` (the existing `AccessScenarios` see one page and no search in their own world — unchanged), `scripts/test-ring1` → green.

- [ ] **Step 7: Mark the item done** — §8d item 23: `23. ✅ **People beyond the first 50 …` + `**Done:** Access searches by address and pages forward and back through the port's own Search/After (PeoplePaging).`

- [ ] **Step 8: Normalise and commit** (files above by name):

```bash
git commit -m "feat(f5): the people list searches and pages

Access asked for the first 50 and stopped; it now passes the port's own
Search and After, with a cursor stack for Previous.

Claude-Session: https://claude.ai/code/session_018YXhgd1YrKkzdt1CbE29fV"
```

---

## Coverage check (spec item → task)

| §8d item | Task | Notes |
|---|---|---|
| 14 (§7) | 2 | rollup + computed kind; badge after staging |
| 15 | 1 | |
| 16 | 3 | deviations 1–2 |
| 17 | 1 | deviations 6–7 |
| 18 | 4 | deviation 8 |
| 19 | 5 | deviation 10; #269 keeps the core-side owner field |
| 20 | 5 | |
| 21 | — | out of scope (core first) |
| 22 | 2 (rollup/computed), 6 (index, default, nullable, tenancy) | |
| 23 | 8 | |
| 24 | 7 | deviation 3 |
| 25–27, 29 | — | out of scope |
| 28 | 6 | deviation 9 |

## What this plan could not establish (for the executor to confirm, not to guess)

- Whether the SQLite migrator can add a stored generated column to an existing table in a dry run — why Task 2's computed scenario stops before Preview.
- Whether an index naming a missing field is refused at apply (no validator check; EF `HasIndex`) — Task 3 treats it as blocking regardless.
- Whether the mapper refuses an exact `trigger.event` pattern naming an undeclared entity — Task 7 names it without blocking.
- Whether a ref to the reserved `users` entity migrates — Task 6 does not offer it.
