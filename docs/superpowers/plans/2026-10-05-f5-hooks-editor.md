# F5 hooks editor (slice B, #276) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An operator authors a hook end to end in the dashboard — edits it in place, sets mutate literals and several fields, picks endpoints and templates, declares them on Integrations behind a prominent "unsigned" statement, writes a `{{…}}` payload, and builds a condition as guided rows that emit ordinary CEL.

**Architecture:** Admin only (no Abstractions, Management or core product change). Pure, unit-tested pieces first — one legality table (`ConditionTable`), readers (`DescriptorLens`), a shape classifier (`HookShape`), the replace primitive (`WorkingCopy.ReplaceHook`), literal fit (`MutateLiteral`), the patch (`HookPatch`), slot placement (`ExpressionSlots.ForHook`), declaration drafts and writers, the condition generator/recognizer (`ConditionText`) — then the screens (`HooksTab`, `Integrations`, `EndpointEditor`, `TemplateEditor`, `ConditionBuilder`). The core stays the only authority: `cel/check` judges every typed slot, apply judges the rest, and Host.Tests facts pin each client restatement to the core it restates.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.10.0 (pinned), `System.Text.Json.Nodes`, Microsoft.Testing.Platform + xUnit v3 + Shouldly, CsCheck (property test), Microsoft.Playwright (admin e2e).

**Spec:** `docs/superpowers/specs/2026-10-05-f5-hooks-editor-design.md` — read it first; this plan argues from it (§ numbers below are the spec's). House example: `docs/superpowers/plans/2026-10-01-f5-expression-check.md`. Base: branch `feat/hooks-editor` stacked on `feat/expression-check` (PR #298); `ExpressionCheck`, `ExpressionSlots` and the `HooksTab` check wiring come from there.

## Global Constraints

- Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after Tasks 7, 14, 19, `scripts/test-ring2` before the PR. **Rings are Debug, CI is Release**: a CA analyzer error passes every ring; run `dotnet build -c Release` once at the end (Task 21) — and remember only `docker build` reproduces CI exactly.
- New/edited `.cs` files are **UTF-8 with BOM and CRLF** (pre-commit `dotnet format` rejects otherwise). Create with the `Write` tool, then normalise: `python3 -c "p='<file>';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"`. `.razor` files are **UTF-8 with BOM and LF** in this repo (`file src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor` says so) — normalise a new one with `python3 -c "p='<file>';b=open(p,'rb').read().replace(b'\r\n',b'\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"`. Edits to existing files keep their line endings.
- Markdown files: LF, **no BOM**.
- Methods ≤ ~25 lines, one purpose each; every internal type and member has XML docs (`alvo-dotnet-conventions`). Prose in docs and UI: plain, specific, no marketing words.
- `MMLib.Alvo.Admin` holds **no reference to `MMLib.Alvo`** (`BoundaryArchitectureTests`); it uses Abstractions only (`FieldSchema`, `CelFieldType`, `AlvoManagedColumns`, `SecretName`, `ManagementWarnedBlock`). Host.Tests is the one suite that sees both assemblies' internals — every Admin↔core agreement fact lives there.
- `PublicApi.MMLib.Alvo.Admin.verified.txt` may grow by exactly: `ConditionBuilder`, `EndpointEditor`, `TemplateEditor` (their constructors and string/`EventCallback` parameters) and `Integrations.Dispose` (+ `IDisposable`). The Stop hook demands a justification per added symbol (`alvo-architecture-rules`: a Razor component cannot be internal and still be a tag; no Mud or internal type in a public parameter). Anything else is a mistake to undo.
- Pattern language (MudBlazor design §3) is binding: `PatternLanguageTests` and `FieldConventionTests` must pass unchanged for every new editor — titles "New …"/"Edit …", submit text from the fixed set, create triggers in `<Actions>`/`<Primary>`, inputs named by `Field For`/`aria-labelledby`, no `Label`/`HelperText` on library text inputs (a `MudCheckBox` keeps its `Label`, as `Indexes.razor:106` does), `aria-describedby="{For}-hint"` on a hinted input, multi-line boxes in an editor with `ChordHint.Of(`. No bUnit (design §6).
- e2e runs outside the rings: `scripts/test-admin-e2e --filter-class '*<ClassName>'` (it builds, installs Chromium, then `dotnet test` with the arguments). Never run two e2e or Stryker runs at once; never edit a script while it runs.
- New e2e classes use `BikeWorkshopWorld` (`test/MMLib.Alvo.Admin.Tests.EndToEnd/ComputedTextScenarios.cs:8`). One world per class fixture: scenarios in one class share one working copy, so each scenario in a class touches different hooks/names; a scenario that imports a descriptor gets its own class.
- Never merge or push to `main`; never push at all from a task. Branch `feat/hooks-editor`, worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hooks`. **One writer per worktree** — a review agent reads a frozen tree.
- Commits: Conventional Commits, each message ending with a blank line and `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Stage files by name, never `git add -A`.

## Review Focus

Hostile or odd inputs a person will hit; each line names where its test lives.

1. A hook's index shifted by another tab's edit between open and save — the save re-finds it by its drawn JSON and replaces it there; one that is gone is refused in place, nothing written. [Task 3 `WorkingCopyHookReplaceTests`; Task 8 e2e `A_hook_another_tab_removed_is_not_overwritten`, `A_hook_moved_by_another_tab_is_still_the_one_saved`]
2. Two operators — each has their own working copy (`WorkingCopyStore` keys by user); the later apply is refused by `If-Match`. Existing behaviour, pinned by `OtherCircuitApplyScenarios`; nothing new to build. [Task 21 lists it]
3. A hook shape the editor cannot draw (`entity.update`, email `data`, an `x-` key) — read-only with its reason, kept byte-for-byte while a neighbour is edited. [Task 3 `HookShapeTests`; Task 8 e2e `HookShapeScenarios`]
4. A name or message with markup (`<b>`, `<script>`) — rendered as text everywhere. [Task 8 e2e `Editing_a_hook_keeps_its_place_lights_it_and_says_saved` asserts no `<b>` element]
5. A 2,001-character condition — a local sentence says so (the check's own refusal arrives too); the guided generator refuses to write one. [Task 8 e2e `A_condition_over_two_thousand_characters_is_said_under_the_box`; Task 16 `ConditionTextTests`]
6. A mutate on a managed or computed column — never offered; an imported hook naming one keeps it, labelled "(not offered)". [Task 4 `HookFieldsTests`; Task 9 e2e `A_managed_or_computed_field_is_not_offered`]
7. Unicode and quoting (`it's`, `\`, `čaj`, `中`, ` && ` inside a value) — round-trips through generator and recognizer. [Task 17 property test]
8. A hook naming an endpoint no longer declared — the picker keeps "(not declared)", never silently replaces it. [Task 10 e2e `UndeclaredReferenceScenarios`]
9. Pending versus applied declarations — pickers offer a pending one ("(not applied yet)"), Integrations badges it. [Task 12 `IntegrationRowsTests`; Tasks 13, 14 e2e]
10. `rental-desk`'s `http://127.0.0.1` (loopback, accepted) versus `http://example.com` (refused). [Task 11 `EndpointDraftTests`; Task 13 e2e; Task 15 agreement]
11. A template placeholder `{{@tenant.id}}` or `{{@user.roles}}` — refused in the sheet with the reason. [Task 11 `TemplateDraftTests`; Task 14 e2e]
12. A payload over 8,000 characters — a local sentence; the check is not asked. [Task 10 e2e `A_payload_over_eight_thousand_characters_is_said_under_the_box`]
13. Double submit — one hook, one endpoint. [Task 8 e2e `A_double_click_on_Add_adds_one_hook`; Task 13 e2e]
14. Escape on a dirty sheet — "Discard your changes?". [Task 8 e2e; Task 13 e2e]
15. Phone width (375 px) — no horizontal scroll with every sheet open. [Tasks 8, 10, 13, 14, 19 e2e]
16. A secret value pasted into "Secret name" — refused, and the refusal never echoes it. [Task 11 `EndpointDraftTests`; Task 13 e2e]
17. A broken condition next to a payload — the payload is still judged (D4). [Task 6 `HookBuilderEditTests`; Task 10 e2e]

## File map

| File | Task | Responsibility |
|---|---|---|
| `src/MMLib.Alvo.Admin/Components/Schema/ConditionTable.cs` | 1 | the one legality table (kinds, operators, images, formats) |
| `src/MMLib.Alvo.Admin/Internal/DescriptorLens.cs` | 2 | + `Endpoints`, `Templates`, `IntegrationUses` |
| `src/MMLib.Alvo.Admin/Components/Schema/HookShape.cs`, `WorkingCopy.Hooks.cs` | 3 | drawable or read-only, with the reason; + `ReplaceHook` |
| `src/MMLib.Alvo.Admin/Components/Schema/MutateRow.cs`, `MutateLiteral.cs`, `HookFields.cs` | 4 | a mutate row, its literal fit, the entity's fields |
| `src/MMLib.Alvo.Admin/Components/Schema/HookPatch.cs` | 5 | an edit patched onto the hook it opened |
| `src/MMLib.Alvo.Admin/Components/Schema/HookBuilder.cs`, `HookBuilder.Load.cs` | 6 | rows, payload, loading a declared hook |
| `src/MMLib.Alvo.Admin/Components/Schema/ExpressionSlots.cs` | 7 | + `ForHook` (position-aware); core test pins string slots |
| `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor`, `.razor.cs`, `HooksTab.Edit.cs` | 8 | edit in place |
| `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Mutate.cs` | 9 | mutate rows |
| `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Pickers.cs` | 10 | endpoint/template pickers, payload, to |
| `src/MMLib.Alvo.Admin/Components/Integrations/EndpointDraft.cs`, `TemplateDraft.cs`, `IntegrationStatement.cs` | 11 | declaration rules and the statement |
| `src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Webhooks.cs`, `WorkingCopy.Templates.cs`, `Components/Integrations/IntegrationRows.cs` | 12 | writers and list rows |
| `src/MMLib.Alvo.Admin/Components/Integrations/Integrations.razor`, `EndpointEditor.razor` | 13 | the screen on the working copy, and the endpoint sheet |
| `src/MMLib.Alvo.Admin/Components/Integrations/TemplateEditor.razor` | 14 | the template sheet |
| `test/MMLib.Alvo.Host.Tests/HooksEditorAgreementTests.cs` | 15 | URL, literal fit, template roots, statement claims ↔ core |
| `src/MMLib.Alvo.Admin/Components/Schema/ConditionText.cs` | 16 | the generator |
| `src/MMLib.Alvo.Admin/Components/Schema/ConditionText.Recognize.cs` | 17 | the strict recognizer, round-trip property |
| `test/MMLib.Alvo.Host.Tests/GuidedConditionConformanceTests.cs` | 18 | every table cell against the real validator |
| `src/MMLib.Alvo.Admin/Components/Schema/ConditionBuilder.razor`, `HooksTab.Condition.cs` | 19 | guided rows + the text switch |
| docs | 20 | `todo-admin.md`, `management-api.md`, spec "As built" |
| — | 21 | whole-slice verification |

---

### Task 1: `ConditionTable` — the one legality table

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/ConditionTable.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HookBuilder.cs:93-106` (`Images`, `Example` delegate to the table)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/ConditionTableTests.cs`

**Interfaces:**
- Consumes: `MMLib.Alvo.Schema.FieldType` (Abstractions); `HookBuilder.IsBefore(string)` (`HookBuilder.cs:109-110`).
- Produces (all `internal`, namespace `MMLib.Alvo.Admin.Components.Schema`):
  - `enum FieldKind { Text, Choice, Number, Flag, Moment, Identity, Json }`
  - `enum ConditionOperator { Is, IsNot, IsNotOrEmpty, Less, LessOrEqual, Greater, GreaterOrEqual, IsTrue, IsFalse, HasValue, IsEmpty, Changed, IsTheWriter, HasRole, LacksRole }`
  - `enum RowImage { New, Old }`, `enum OperandKind { None, Literal, Role }`
  - `sealed record OperatorSpec(ConditionOperator Operator, string Words, string Format, OperandKind Operand, IReadOnlyList<FieldKind> Kinds, bool NullableOnly, bool UpdateOnly, bool BeforeOnly, string WhenEmpty)`
  - `static class ConditionTable` with `const string Writer = "@user"`, `const int MaxConditionLength = 2000`, `IReadOnlyList<OperatorSpec> Rows`, `OperatorSpec Of(ConditionOperator)`, `FieldKind KindOf(FieldType)`, `bool HasNew(string point)`, `bool HasOld(string point)`, `bool IsUpdate(string point)`, `IReadOnlyList<RowImage> ImagesAt(string point)`, `bool Allows(OperatorSpec, string point, FieldKind, bool nullable)`, `IReadOnlyList<OperatorSpec> For(string point, FieldKind, bool nullable)`, `IReadOnlyList<OperatorSpec> ForWriter(string point)`, `string Prefix(RowImage)`, `string ImagesMarkup(string point)`, `string Example(string point)`.
  - Format tokens: `{f}` = `new.field`/`old.field`; `{n}` = the bare field name (only `changed`); `{v}` = the spelled literal; `{r}` = the quoted role.

- [ ] **Step 1: Write the failing tests**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The one table the guided condition offers from, the generator writes from, the recognizer reads with and the Host.Tests
/// conformance fact compiles (spec §7.1).
/// </summary>
public class ConditionTableTests
{
    [Theory]
    [InlineData(FieldType.String, FieldKind.Text)]
    [InlineData(FieldType.Text, FieldKind.Text)]
    [InlineData(FieldType.Enum, FieldKind.Choice)]
    [InlineData(FieldType.Integer, FieldKind.Number)]
    [InlineData(FieldType.Decimal, FieldKind.Number)]
    [InlineData(FieldType.Boolean, FieldKind.Flag)]
    [InlineData(FieldType.Date, FieldKind.Moment)]
    [InlineData(FieldType.DateTime, FieldKind.Moment)]
    [InlineData(FieldType.Uuid, FieldKind.Identity)]
    [InlineData(FieldType.Ref, FieldKind.Identity)]
    [InlineData(FieldType.Json, FieldKind.Json)]
    public void A_field_type_has_one_kind(FieldType type, FieldKind kind) => ConditionTable.KindOf(type).ShouldBe(kind);

    [Theory]
    [InlineData("beforeCreate", new[] { RowImage.New })]
    [InlineData("beforeUpdate", new[] { RowImage.New, RowImage.Old })]
    [InlineData("beforeDelete", new[] { RowImage.Old })]
    [InlineData("afterCreate", new[] { RowImage.New })]
    [InlineData("afterUpdate", new[] { RowImage.New, RowImage.Old })]
    [InlineData("afterDelete", new[] { RowImage.Old })]
    public void A_point_offers_only_the_images_it_has(string point, RowImage[] images)
        => ConditionTable.ImagesAt(point).ShouldBe(images);

    [Theory]
    [InlineData("beforeUpdate", true)]
    [InlineData("afterUpdate", true)]
    [InlineData("beforeCreate", false)]
    [InlineData("beforeDelete", false)]
    [InlineData("afterCreate", false)]
    [InlineData("afterDelete", false)]
    public void Changed_is_offered_only_where_a_write_has_two_images(string point, bool offered)
        => ConditionTable.For(point, FieldKind.Text, nullable: false)
            .Any(spec => spec.Operator == ConditionOperator.Changed).ShouldBe(offered);

    [Theory]
    [InlineData("beforeCreate", true)]
    [InlineData("beforeUpdate", true)]
    [InlineData("beforeDelete", true)]
    [InlineData("afterCreate", false)]
    [InlineData("afterUpdate", false)]
    [InlineData("afterDelete", false)]
    public void A_role_row_is_offered_only_before_the_commit(string point, bool offered)
        => (ConditionTable.ForWriter(point).Count > 0).ShouldBe(offered);

    [Fact]
    public void The_operators_that_admit_an_empty_value_are_offered_only_on_a_nullable_field()
    {
        var required = ConditionTable.For("beforeUpdate", FieldKind.Text, nullable: false).Select(spec => spec.Operator).ToList();
        var optional = ConditionTable.For("beforeUpdate", FieldKind.Text, nullable: true).Select(spec => spec.Operator).ToList();

        required.ShouldNotContain(ConditionOperator.IsNotOrEmpty);
        required.ShouldNotContain(ConditionOperator.HasValue);
        required.ShouldNotContain(ConditionOperator.IsEmpty);
        optional.ShouldContain(ConditionOperator.IsNotOrEmpty);
        optional.ShouldContain(ConditionOperator.HasValue);
        optional.ShouldContain(ConditionOperator.IsEmpty);
    }

    [Fact]
    public void A_moment_takes_no_literal_and_a_json_field_only_presence()
    {
        ConditionTable.For("beforeUpdate", FieldKind.Moment, nullable: true).ShouldAllBe(spec => spec.Operand == OperandKind.None);
        ConditionTable.For("beforeUpdate", FieldKind.Json, nullable: true).Select(spec => spec.Operator)
            .ShouldBe([ConditionOperator.HasValue, ConditionOperator.IsEmpty]);
    }

    [Fact]
    public void Relational_operators_are_offered_on_numbers_only()
    {
        ConditionTable.For("beforeCreate", FieldKind.Text, nullable: true).ShouldNotContain(spec => spec.Operator == ConditionOperator.Less);
        ConditionTable.For("beforeCreate", FieldKind.Number, nullable: false).ShouldContain(spec => spec.Operator == ConditionOperator.Less);
    }

    [Fact]
    public void Every_format_carries_exactly_the_placeholder_its_operand_needs()
    {
        foreach (var spec in ConditionTable.Rows)
        {
            spec.Format.Contains("{v}", StringComparison.Ordinal).ShouldBe(spec.Operand == OperandKind.Literal, spec.Operator.ToString());
            spec.Format.Contains("{r}", StringComparison.Ordinal).ShouldBe(spec.Operand == OperandKind.Role, spec.Operator.ToString());
        }
    }

    [Fact]
    public void Every_operator_has_exactly_one_row()
        => ConditionTable.Rows.Select(spec => spec.Operator).ShouldBe(Enum.GetValues<ConditionOperator>(), ignoreOrder: true);

    [Theory]
    [InlineData("beforeCreate", "<code class=\"a-mono\">new</code>", "new.priority == 'high'")]
    [InlineData("beforeUpdate", "<code class=\"a-mono\">new</code>, <code class=\"a-mono\">old</code>", "new.status == 'completed'")]
    [InlineData("beforeDelete", "<code class=\"a-mono\">old</code>", "old.status == 'completed'")]
    [InlineData("afterCreate", "<code class=\"a-mono\">new</code>", "new.status == 'completed'")]
    [InlineData("afterDelete", "<code class=\"a-mono\">old</code>", "old.status == 'completed'")]
    public void The_hint_and_the_example_name_only_the_images_the_point_has(string point, string images, string example)
    {
        ConditionTable.ImagesMarkup(point).ShouldBe(images);
        ConditionTable.Example(point).ShouldBe(example);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ConditionTableTests'`
Expected: build FAIL — `ConditionTable` does not exist.

- [ ] **Step 3: Implement `ConditionTable.cs`**

```csharp
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>What a field is, as far as a hook condition can compare it.</summary>
internal enum FieldKind
{
    /// <summary>A <c>string</c> or <c>text</c> field: compared with a quoted literal.</summary>
    Text,

    /// <summary>An <c>enum</c> field: compared with one of its declared values.</summary>
    Choice,

    /// <summary>An <c>integer</c> or <c>decimal</c> field: compared with a number.</summary>
    Number,

    /// <summary>A <c>boolean</c> field.</summary>
    Flag,

    /// <summary>A <c>date</c> or <c>datetime</c> field: CEL has no literal for one, so only presence and change.</summary>
    Moment,

    /// <summary>A <c>uuid</c> or <c>ref</c> field: no literal either, but it can be the person writing.</summary>
    Identity,

    /// <summary>A <c>json</c> field: not comparable (<c>CelTypeChecker</c>), so only presence.</summary>
    Json,
}

/// <summary>The relations the guided condition offers; each is exactly one row of <see cref="ConditionTable"/>.</summary>
internal enum ConditionOperator
{
    /// <summary><c>f == v</c>.</summary>
    Is,

    /// <summary><c>f != v</c> — false when the field is empty.</summary>
    IsNot,

    /// <summary><c>!(f == v)</c> — true when the field is empty.</summary>
    IsNotOrEmpty,

    /// <summary><c>f &lt; v</c>.</summary>
    Less,

    /// <summary><c>f &lt;= v</c>.</summary>
    LessOrEqual,

    /// <summary><c>f &gt; v</c>.</summary>
    Greater,

    /// <summary><c>f &gt;= v</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>f == true</c>.</summary>
    IsTrue,

    /// <summary><c>f == false</c>.</summary>
    IsFalse,

    /// <summary><c>has(f)</c>.</summary>
    HasValue,

    /// <summary><c>!has(f)</c>.</summary>
    IsEmpty,

    /// <summary><c>changed(f)</c>.</summary>
    Changed,

    /// <summary><c>f == @user.id</c>.</summary>
    IsTheWriter,

    /// <summary><c>'r' in @user.roles</c>.</summary>
    HasRole,

    /// <summary><c>!('r' in @user.roles)</c>.</summary>
    LacksRole,
}

/// <summary>Which image of the row a condition reads: the one being written, or the one before the write.</summary>
internal enum RowImage
{
    /// <summary><c>new.</c> — the row as the write leaves it.</summary>
    New,

    /// <summary><c>old.</c> — the row before the write.</summary>
    Old,
}

/// <summary>What an operator takes on its right.</summary>
internal enum OperandKind
{
    /// <summary>Nothing: <c>has(f)</c>, <c>changed(f)</c>, <c>f == true</c>.</summary>
    None,

    /// <summary>A literal the field's kind spells.</summary>
    Literal,

    /// <summary>A role name from <c>auth.roles</c>.</summary>
    Role,
}

/// <summary>One relation: its words, its canonical CEL and where it is legal.</summary>
/// <param name="Operator">The relation.</param>
/// <param name="Words">What the form calls it.</param>
/// <param name="Format">The canonical CEL, with <c>{f}</c>, <c>{n}</c>, <c>{v}</c> or <c>{r}</c> in place of its parts.</param>
/// <param name="Operand">What it takes on its right.</param>
/// <param name="Kinds">The field kinds it applies to; empty for a role row.</param>
/// <param name="NullableOnly">Whether it is offered only on a field that may be empty.</param>
/// <param name="UpdateOnly">Whether it is offered only where a write has both images.</param>
/// <param name="BeforeOnly">Whether it is offered only before the commit (an event envelope carries no roles).</param>
/// <param name="WhenEmpty">What it evaluates to when the field is empty — the hint the form shows.</param>
internal sealed record OperatorSpec(
    ConditionOperator Operator,
    string Words,
    string Format,
    OperandKind Operand,
    IReadOnlyList<FieldKind> Kinds,
    bool NullableOnly,
    bool UpdateOnly,
    bool BeforeOnly,
    string WhenEmpty);

/// <summary>
/// The one table of what the guided condition may write: operator × field kind × point × canonical CEL × null semantics.
/// </summary>
/// <remarks>
/// <para>
/// <b>One source for four readers</b> (spec §7.1): the form offers from it, <c>ConditionText</c> generates and recognizes
/// with it, and <c>GuidedConditionConformanceTests</c> compiles every cell against the real validator. It replaces the
/// per-point strings <see cref="HookBuilder"/> carried, which drifted (an <c>afterDelete</c> example named <c>new.</c>).
/// </para>
/// <para>
/// <b>It decides what to offer, never what is valid.</b> The live <c>cel/check</c> and the apply judge the text written.
/// Narrower than apply on purpose: no after-commit image the point lacks, no string relational, no negative number, no
/// literal for a moment or an id — each of those is either refused by the core or meaningless, and stays in text mode.
/// </para>
/// </remarks>
internal static class ConditionTable
{
    /// <summary>The pseudo-field a role row names: the person writing.</summary>
    public const string Writer = "@user";

    /// <summary>The most characters a condition holds (schema <c>$defs/cel</c>).</summary>
    public const int MaxConditionLength = 2000;

    private static readonly FieldKind[] _compared = [FieldKind.Text, FieldKind.Choice, FieldKind.Number];
    private static readonly FieldKind[] _number = [FieldKind.Number];
    private static readonly FieldKind[] _flag = [FieldKind.Flag];
    private static readonly FieldKind[] _identity = [FieldKind.Identity];
    private static readonly FieldKind[] _none = [];

    private static readonly FieldKind[] _every =
        [FieldKind.Text, FieldKind.Choice, FieldKind.Number, FieldKind.Flag, FieldKind.Moment, FieldKind.Identity, FieldKind.Json];

    private static readonly FieldKind[] _changeable =
        [FieldKind.Text, FieldKind.Choice, FieldKind.Number, FieldKind.Flag, FieldKind.Moment, FieldKind.Identity];

    /// <summary>Every relation, in the order the form lists them.</summary>
    public static IReadOnlyList<OperatorSpec> Rows { get; } =
    [
        new(ConditionOperator.Is, "is", "{f} == {v}", OperandKind.Literal, _compared, false, false, false, "false: an empty value equals nothing"),
        new(ConditionOperator.IsNot, "is not (and has a value)", "{f} != {v}", OperandKind.Literal, _compared, false, false, false, "false: every comparison with an empty value is false, != included"),
        new(ConditionOperator.IsNotOrEmpty, "is not, or is empty", "!({f} == {v})", OperandKind.Literal, _compared, true, false, false, "true"),
        new(ConditionOperator.Less, "is less than", "{f} < {v}", OperandKind.Literal, _number, false, false, false, "false"),
        new(ConditionOperator.LessOrEqual, "is at most", "{f} <= {v}", OperandKind.Literal, _number, false, false, false, "false"),
        new(ConditionOperator.Greater, "is more than", "{f} > {v}", OperandKind.Literal, _number, false, false, false, "false"),
        new(ConditionOperator.GreaterOrEqual, "is at least", "{f} >= {v}", OperandKind.Literal, _number, false, false, false, "false"),
        new(ConditionOperator.IsTrue, "is true", "{f} == true", OperandKind.None, _flag, false, false, false, "false: empty is neither true nor false"),
        new(ConditionOperator.IsFalse, "is false", "{f} == false", OperandKind.None, _flag, false, false, false, "false: empty is neither true nor false"),
        new(ConditionOperator.HasValue, "has a value", "has({f})", OperandKind.None, _every, true, false, false, "false"),
        new(ConditionOperator.IsEmpty, "is empty", "!has({f})", OperandKind.None, _every, true, false, false, "true"),
        new(ConditionOperator.Changed, "changed", "changed({n})", OperandKind.None, _changeable, false, true, false, "decided by the build"),
        new(ConditionOperator.IsTheWriter, "is the person writing", "{f} == @user.id", OperandKind.None, _identity, false, false, false, "false"),
        new(ConditionOperator.HasRole, "has the role", "{r} in @user.roles", OperandKind.Role, _none, false, false, true, "false"),
        new(ConditionOperator.LacksRole, "does not have the role", "!({r} in @user.roles)", OperandKind.Role, _none, false, false, true, "true"),
    ];

    /// <summary>The row of one relation.</summary>
    /// <param name="relation">The relation.</param>
    public static OperatorSpec Of(ConditionOperator relation) => Rows.Single(spec => spec.Operator == relation);

    /// <summary>What a declared field type is, for a condition.</summary>
    /// <param name="type">The field's declared type.</param>
    public static FieldKind KindOf(FieldType type) => type switch
    {
        FieldType.String or FieldType.Text => FieldKind.Text,
        FieldType.Enum => FieldKind.Choice,
        FieldType.Integer or FieldType.Decimal => FieldKind.Number,
        FieldType.Boolean => FieldKind.Flag,
        FieldType.Date or FieldType.DateTime => FieldKind.Moment,
        FieldType.Uuid or FieldType.Ref => FieldKind.Identity,
        _ => FieldKind.Json,
    };

    /// <summary>Whether a point has the row as the write leaves it.</summary>
    /// <param name="point">The hook point.</param>
    public static bool HasNew(string point) => point is "beforeCreate" or "beforeUpdate" or "afterCreate" or "afterUpdate";

    /// <summary>Whether a point has the row as it was before the write.</summary>
    /// <param name="point">The hook point.</param>
    public static bool HasOld(string point) => point is "beforeUpdate" or "beforeDelete" or "afterUpdate" or "afterDelete";

    /// <summary>Whether a point is an update's, the one write with both images.</summary>
    /// <param name="point">The hook point.</param>
    public static bool IsUpdate(string point) => point.EndsWith("Update", StringComparison.Ordinal);

    /// <summary>The images a condition at this point may read, <c>new</c> first.</summary>
    /// <param name="point">The hook point.</param>
    public static IReadOnlyList<RowImage> ImagesAt(string point)
    {
        var images = new List<RowImage>(2);
        if (HasNew(point))
        {
            images.Add(RowImage.New);
        }

        if (HasOld(point))
        {
            images.Add(RowImage.Old);
        }

        return images;
    }

    /// <summary>Whether a relation is offered on a field of this kind at this point.</summary>
    /// <param name="spec">The relation.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="kind">The field's kind.</param>
    /// <param name="nullable">Whether the field may be empty.</param>
    public static bool Allows(OperatorSpec spec, string point, FieldKind kind, bool nullable)
        => spec.Operand != OperandKind.Role
           && spec.Kinds.Contains(kind)
           && (!spec.NullableOnly || nullable)
           && (!spec.UpdateOnly || IsUpdate(point))
           && (!spec.BeforeOnly || HookBuilder.IsBefore(point));

    /// <summary>The relations offered on a field of this kind at this point, in the table's order.</summary>
    /// <param name="point">The hook point.</param>
    /// <param name="kind">The field's kind.</param>
    /// <param name="nullable">Whether the field may be empty.</param>
    public static IReadOnlyList<OperatorSpec> For(string point, FieldKind kind, bool nullable)
        => [.. Rows.Where(spec => Allows(spec, point, kind, nullable))];

    /// <summary>The role relations offered at this point — none after the commit.</summary>
    /// <param name="point">The hook point.</param>
    public static IReadOnlyList<OperatorSpec> ForWriter(string point)
        => [.. Rows.Where(spec => spec.Operand == OperandKind.Role && (!spec.BeforeOnly || HookBuilder.IsBefore(point)))];

    /// <summary>The CEL prefix of an image.</summary>
    /// <param name="image">The image.</param>
    public static string Prefix(RowImage image) => image == RowImage.New ? "new" : "old";

    /// <summary>The images a point has, as the condition hint's markup.</summary>
    /// <param name="point">The hook point.</param>
    public static string ImagesMarkup(string point)
        => string.Join(", ", ImagesAt(point).Select(image => $"<code class=\"a-mono\">{Prefix(image)}</code>"));

    /// <summary>A condition valid at this point, for the placeholder.</summary>
    /// <param name="point">The hook point.</param>
    public static string Example(string point) => point switch
    {
        "beforeCreate" => "new.priority == 'high'",
        "beforeDelete" or "afterDelete" => "old.status == 'completed'",
        _ => "new.status == 'completed'",
    };
}
```

- [ ] **Step 4: Make `HookBuilder` read the table.** In `HookBuilder.cs` replace the bodies of `Images` (lines 93-98) and `Example` (lines 101-106) — keep their XML docs and remarks:

```csharp
    public string Images => ConditionTable.ImagesMarkup(Point);
```

```csharp
    public string Example => ConditionTable.Example(Point);
```

- [ ] **Step 5: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ConditionTableTests' --filter-class '*HookBuilderTests'`
Expected: PASS (`HookBuilderTests.The_guidance_names_only_the_row_images_the_point_has` still passes — the before-points' strings are unchanged). Normalise both `.cs` files (BOM+CRLF), re-run, then `scripts/test-ring0`.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/ConditionTable.cs src/MMLib.Alvo.Admin/Components/Schema/HookBuilder.cs test/MMLib.Alvo.Admin.Tests/Schema/ConditionTableTests.cs
git commit -m "feat(admin): one table of what a hook condition may say, where, and what it means when empty

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 2: `DescriptorLens` reads endpoints, templates and who uses them

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Internal/DescriptorLens.cs` (add after `FieldDeclarations`, `:186-205`)
- Test: `test/MMLib.Alvo.Admin.Tests/Internal/DescriptorLensIntegrationTests.cs`

**Interfaces:**
- Consumes: `DescriptorLens.Parse` (private, `:240-250`).
- Produces:
  - `static IReadOnlyList<KeyValuePair<string, JsonElement>> DescriptorLens.Endpoints(string descriptorJson)` — `webhooks.endpoints` by name, values cloned.
  - `static IReadOnlyList<KeyValuePair<string, JsonElement>> DescriptorLens.Templates(string descriptorJson)` — `templates` by name, cloned.
  - `sealed record DescriptorLens.IntegrationUse(string Kind, string Name, string Entity, string Point, int Position)` — `Kind` is `"endpoint"` or `"template"`.
  - `static IReadOnlyList<DescriptorLens.IntegrationUse> DescriptorLens.IntegrationUses(string descriptorJson)` — every after-hook `webhook`/`email` action; automation is not read (it delivers nothing in this build, spec §4.7).

- [ ] **Step 1: Write the failing tests**

```csharp
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>What the pickers and Integrations read off the working copy (spec §4.4, §4.7).</summary>
public class DescriptorLensIntegrationTests
{
    private const string Descriptor = """
        {
          "webhooks": { "endpoints": {
            "rental-desk": { "url": "http://127.0.0.1:5081/hooks/rentals", "secretRef": "rental-desk-signing-key" },
            "billing": { "url": "https://billing.example/hooks", "secretRef": "billing-key", "description": "Invoices" } } },
          "templates": {
            "order-ready": { "subject": "Ready {{new.order_number}}", "body": "Hello" },
            "legacy": { "bodyFile": "legacy.md" } },
          "entities": {
            "rentals": { "fields": {}, "hooks": { "afterCreate": [ { "action": { "type": "webhook", "endpoint": "rental-desk" } } ] } },
            "service_orders": { "fields": {}, "hooks": {
              "beforeUpdate": [ { "action": { "reject": "no" } } ],
              "afterUpdate": [
                { "action": { "type": "email", "template": "order-ready", "to": "{{new.contact_email}}" } },
                { "action": { "type": "webhook", "endpoint": "billing" } } ] } } }
        }
        """;

    [Fact]
    public void Endpoints_are_read_by_name_in_the_descriptors_order()
    {
        var endpoints = DescriptorLens.Endpoints(Descriptor);

        endpoints.Select(endpoint => endpoint.Key).ShouldBe(["rental-desk", "billing"]);
        endpoints[1].Value.GetProperty("url").GetString().ShouldBe("https://billing.example/hooks");
    }

    [Fact]
    public void Templates_are_read_by_name_a_body_file_one_included()
        => DescriptorLens.Templates(Descriptor).Select(template => template.Key).ShouldBe(["order-ready", "legacy"]);

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"webhooks": []}""")]
    [InlineData("""{"webhooks": {"endpoints": "x"}}""")]
    [InlineData("""{"templates": 3}""")]
    public void A_missing_or_malformed_block_reads_as_none(string json)
    {
        DescriptorLens.Endpoints(json).ShouldBeEmpty();
        DescriptorLens.Templates(json).ShouldBeEmpty();
        DescriptorLens.IntegrationUses(json).ShouldBeEmpty();
    }

    [Fact]
    public void Every_hook_that_posts_or_sends_is_a_use_with_its_place()
        => DescriptorLens.IntegrationUses(Descriptor).ShouldBe(
        [
            new DescriptorLens.IntegrationUse("endpoint", "rental-desk", "rentals", "afterCreate", 0),
            new DescriptorLens.IntegrationUse("template", "order-ready", "service_orders", "afterUpdate", 0),
            new DescriptorLens.IntegrationUse("endpoint", "billing", "service_orders", "afterUpdate", 1),
        ]);

    [Fact]
    public void A_declaration_read_outlives_the_parse()
    {
        var url = DescriptorLens.Endpoints(Descriptor)[0].Value;

        url.GetProperty("secretRef").GetString().ShouldBe("rental-desk-signing-key");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*DescriptorLensIntegrationTests'`
Expected: build FAIL — `Endpoints` is not defined.

- [ ] **Step 3: Implement** — add to `DescriptorLens` (after `FieldDeclarations`):

```csharp
    /// <summary>The webhook endpoints a descriptor declares, by name, in its own order.</summary>
    /// <remarks>
    /// Read from the working copy by the pickers and Integrations (spec B5), so a declaration staged a minute ago is
    /// offered. A block of the wrong shape reads as none: the apply is the authority that refuses it.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor, applied or working.</param>
    /// <returns>Name to declaration, cloned to outlive the parse.</returns>
    public static IReadOnlyList<KeyValuePair<string, JsonElement>> Endpoints(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        return document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("webhooks", out var webhooks)
            && webhooks.ValueKind == JsonValueKind.Object
            && webhooks.TryGetProperty("endpoints", out var endpoints)
                ? Declarations(endpoints)
                : [];
    }

    /// <summary>The message templates a descriptor declares, by name, in its own order — a <c>bodyFile</c> one included.</summary>
    /// <param name="descriptorJson">The descriptor, applied or working.</param>
    /// <returns>Name to declaration, cloned to outlive the parse.</returns>
    public static IReadOnlyList<KeyValuePair<string, JsonElement>> Templates(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        return document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("templates", out var templates)
                ? Declarations(templates)
                : [];
    }

    /// <summary>One after-hook that posts to an endpoint or sends a template.</summary>
    /// <param name="Kind"><c>endpoint</c> or <c>template</c>.</param>
    /// <param name="Name">The declaration it names.</param>
    /// <param name="Entity">The entity the hook is on.</param>
    /// <param name="Point">The hook point.</param>
    /// <param name="Position">Its position within the point.</param>
    public sealed record IntegrationUse(string Kind, string Name, string Entity, string Point, int Position);

    /// <summary>Every hook action that names an endpoint or a template, entity by entity.</summary>
    /// <remarks>
    /// Hooks only: an <c>automation</c> rule's action names an endpoint too, but no rule is evaluated in this build, so it
    /// delivers nothing and counting it would call an endpoint used that receives nothing.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor, applied or working.</param>
    public static IReadOnlyList<IntegrationUse> IntegrationUses(string descriptorJson)
    {
        using var document = Parse(descriptorJson);
        if (document is null
            || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("entities", out var entities)
            || entities.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. entities.EnumerateObject().SelectMany(entity => UsesOf(entity.Name, entity.Value))];
    }

    private static List<KeyValuePair<string, JsonElement>> Declarations(JsonElement block)
        => block.ValueKind == JsonValueKind.Object
            ? [.. block.EnumerateObject().Select(pair => new KeyValuePair<string, JsonElement>(pair.Name, pair.Value.Clone()))]
            : [];

    private static IEnumerable<IntegrationUse> UsesOf(string entity, JsonElement declared)
    {
        if (declared.ValueKind != JsonValueKind.Object
            || !declared.TryGetProperty("hooks", out var hooks)
            || hooks.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var point in hooks.EnumerateObject().Where(point => point.Value.ValueKind == JsonValueKind.Array))
        {
            var position = 0;
            foreach (var hook in point.Value.EnumerateArray())
            {
                if (UseOf(entity, point.Name, position++, hook) is { } use)
                {
                    yield return use;
                }
            }
        }
    }

    private static IntegrationUse? UseOf(string entity, string point, int position, JsonElement hook)
    {
        if (hook.ValueKind != JsonValueKind.Object
            || !hook.TryGetProperty("action", out var action)
            || action.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TextOf(action, "endpoint") is { } endpoint)
        {
            return new IntegrationUse("endpoint", endpoint, entity, point, position);
        }

        return TextOf(action, "template") is { } template ? new IntegrationUse("template", template, entity, point, position) : null;
    }

    private static string? TextOf(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
```

- [ ] **Step 4: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*DescriptorLensIntegrationTests'`
Expected: PASS. Then `scripts/test-ring0`.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Admin/Internal/DescriptorLens.cs test/MMLib.Alvo.Admin.Tests/Internal/DescriptorLensIntegrationTests.cs
git commit -m "feat(admin): read declared endpoints, templates and the hooks that use them off any descriptor

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 3: `HookShape` and `WorkingCopy.ReplaceHook`

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/HookShape.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Hooks.cs` (add after `RemoveHook`, `:70-96`)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/HookShapeTests.cs`, `test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyHookReplaceTests.cs`

**Interfaces:**
- Consumes: `HookBuilder.IsBefore`, `HookBuilder.Webhook`, `HookBuilder.Email` (consts); `WorkingCopy.Edit` (`WorkingCopy.cs:370`), `WorkingCopy.Readable` (`:76`).
- Produces:
  - `internal static class HookShape { public static string? Undrawable(JsonNode? hook, string point); }` — `null` when the editor can draw and rewrite it, else one sentence why not (spec §5.2).
  - `public bool WorkingCopy.ReplaceHook(string entity, string point, int position, string expectedHook, JsonObject hook)` — writes a clone of `hook` at `position` only when the entry there, read indented with the relaxed encoder, equals `expectedHook` (spec §5.1).

- [ ] **Step 1: Write the failing shape tests**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Which hooks the editor may open, and the one sentence a read-only one carries (spec §5.2).</summary>
public class HookShapeTests
{
    [Theory]
    [InlineData("beforeUpdate", """{"condition":"old.a == 1","action":{"reject":"No."}}""")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{"a":{"$cel":"now()"},"b":"x","c":1,"d":true,"e":null}}}""")]
    [InlineData("afterCreate", """{"action":{"type":"webhook","endpoint":"desk"}}""")]
    [InlineData("afterCreate", """{"action":{"type":"webhook","endpoint":"desk","payload":"[{{new.total}}]"}}""")]
    [InlineData("afterUpdate", """{"action":{"type":"email","template":"t","to":"a@b.c"}}""")]
    [InlineData("afterUpdate", """{"action":{"type":"email","template":"t","to":"a@b.c"},"condition":"changed(stage)"}""")]
    public void A_shape_the_editor_writes_is_drawable(string point, string json)
        => HookShape.Undrawable(JsonNode.Parse(json), point).ShouldBeNull();

    [Theory]
    [InlineData("afterUpdate", """{"action":{"type":"entity.update","entity":"x","payload":{}}}""", "'entity.update' is refused")]
    [InlineData("afterUpdate", """{"action":{"type":"function","name":"f"}}""", "'function' is refused")]
    [InlineData("afterUpdate", """{"action":{"type":"http.call","url":"https://x"}}""", "'http.call' is refused")]
    [InlineData("afterUpdate", """{"action":{"type":"email","template":"t","to":"a@b.c","data":"{{new.a}}"}}""", "'data'")]
    [InlineData("afterUpdate", """{"action":{"type":"sms","to":"x"}}""", "not one the schema declares")]
    [InlineData("afterUpdate", """{"action":{"type":"webhook","endpoint":"d","x-note":"y"}}""", "x-note")]
    [InlineData("afterUpdate", """{"action":{"type":"webhook"}}""", "'endpoint' is missing")]
    [InlineData("afterUpdate", """{"action":{"type":"webhook","endpoint":"d","payload":7}}""", "'payload' is not a string")]
    [InlineData("beforeUpdate", """{"condition":"a","action":{"reject":"x"},"x-owner":"ops"}""", "x-owner")]
    [InlineData("beforeUpdate", """{"condition":7,"action":{"reject":"x"}}""", "condition is not a string")]
    [InlineData("beforeUpdate", """{"condition":null,"action":{"reject":"x"}}""", "condition is not a string")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{"prefs":{"a":1}}}}""", "'prefs'")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{"tags":[1,2]}}}""", "'tags'")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{}}}""", "patches no field")]
    [InlineData("beforeDelete", """{"action":{"mutate":{"a":"x"}}}""", "beforeDelete")]
    [InlineData("beforeUpdate", """{"action":{"type":"webhook","endpoint":"d"}}""", "neither a reject nor a mutate")]
    [InlineData("afterCreate", """{"action":{"reject":"x"}}""", "has no type")]
    [InlineData("afterCreate", """{"action":"x"}""", "action is not an object")]
    [InlineData("afterCreate", """[1]""", "not an object")]
    public void A_shape_the_editor_cannot_write_is_read_only_with_its_reason(string point, string json, string reason)
        => HookShape.Undrawable(JsonNode.Parse(json), point).ShouldNotBeNull().ShouldContain(reason);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*HookShapeTests'`
Expected: build FAIL — `HookShape` does not exist.

- [ ] **Step 3: Implement `HookShape.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Whether the hook editor can draw a declared hook — and so rewrite it without losing anything — or must leave it as it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The edit-in-place half of <c>A_hook_the_editor_cannot_draw_survives_beside_it</c></b> (spec §5.2, ruling B1). A writer
/// that rebuilt a hook it cannot draw would drop what it does not know — an email's <c>data</c>, a refused action type, an
/// <c>x-</c> key — which is the silent narrowing <see cref="WorkingCopy"/> exists against. Such a hook is read-only: its row
/// keeps Remove and says why it has no Edit.
/// </para>
/// <para>
/// Every read-only shape is one the apply refuses or the schema rejects; it reaches a working copy through an import or the
/// assistant, never through this editor.
/// </para>
/// </remarks>
internal static class HookShape
{
    private static readonly HashSet<string> _hookKeys = new(StringComparer.Ordinal) { "condition", "action" };
    private static readonly HashSet<string> _webhookKeys = new(StringComparer.Ordinal) { "type", "endpoint", "payload" };
    private static readonly HashSet<string> _emailKeys = new(StringComparer.Ordinal) { "type", "template", "to" };
    private static readonly HashSet<string> _refusedTypes = new(StringComparer.Ordinal) { "function", "http.call", "entity.update" };

    /// <summary>Why the editor cannot draw this hook, or <see langword="null"/> when it can.</summary>
    /// <param name="hook">The declared hook.</param>
    /// <param name="point">The point it is declared at.</param>
    /// <returns>One sentence, or <see langword="null"/>.</returns>
    public static string? Undrawable(JsonNode? hook, string point)
    {
        if (hook is not JsonObject declared)
        {
            return "It is not an object.";
        }

        if (Unknown(declared, _hookKeys) is { } keys)
        {
            return $"It carries keys the editor does not know: {keys}.";
        }

        if (declared.TryGetPropertyValue("condition", out var condition) && !IsString(condition))
        {
            return "Its condition is not a string.";
        }

        if (declared["action"] is not JsonObject action)
        {
            return "Its action is not an object.";
        }

        return HookBuilder.IsBefore(point) ? BeforeAction(action, point) : AfterAction(action);
    }

    private static string? BeforeAction(JsonObject action, string point)
    {
        if (action.Count == 1 && action.TryGetPropertyValue("reject", out var reject))
        {
            return IsString(reject) ? null : "Its reject message is not a string.";
        }

        if (action.Count != 1 || action["mutate"] is not JsonObject patch)
        {
            return "Its action is neither a reject nor a mutate, the two a before-hook may take.";
        }

        if (point == "beforeDelete")
        {
            return "A mutate under beforeDelete is refused by this build: the row is being removed.";
        }

        return patch.Count == 0 ? "Its mutate patches no field." : patch.Select(MutateValue).FirstOrDefault(reason => reason is not null);
    }

    private static string? MutateValue(KeyValuePair<string, JsonNode?> pair) => pair.Value switch
    {
        null => null,
        JsonObject tagged when tagged.Count == 1 && IsString(tagged["$cel"]) => null,
        JsonObject or JsonArray => $"The mutate value for '{pair.Key}' is a JSON object or array, which the editor cannot draw.",
        _ => null,
    };

    private static string? AfterAction(JsonObject action)
    {
        if (action["type"] is not JsonValue value || !value.TryGetValue<string>(out var type))
        {
            return "Its action has no type.";
        }

        return type switch
        {
            HookBuilder.Webhook => Keys(action, _webhookKeys) ?? Required(action, "endpoint") ?? Optional(action, "payload"),
            HookBuilder.Email when action.ContainsKey("data") => "It carries 'data', which this build refuses (email.data).",
            HookBuilder.Email => Keys(action, _emailKeys) ?? Required(action, "template", "to"),
            _ when _refusedTypes.Contains(type) => $"Its action type '{type}' is refused by this build.",
            _ => $"Its action type '{type}' is not one the schema declares.",
        };
    }

    private static string? Keys(JsonObject action, HashSet<string> allowed)
        => Unknown(action, allowed) is { } keys ? $"Its action carries keys the editor does not know: {keys}." : null;

    private static string? Unknown(JsonObject owner, HashSet<string> allowed)
    {
        var unknown = owner.Select(pair => pair.Key).Where(key => !allowed.Contains(key)).ToList();
        return unknown.Count == 0 ? null : string.Join(", ", unknown);
    }

    private static string? Required(JsonObject action, params string[] keys)
        => keys.FirstOrDefault(key => !IsString(action[key])) is { } missing ? $"Its '{missing}' is missing or not a string." : null;

    private static string? Optional(JsonObject action, string key)
        => action.TryGetPropertyValue(key, out var value) && !IsString(value) ? $"Its '{key}' is not a string." : null;

    private static bool IsString(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String;
}
```

- [ ] **Step 4: Run** `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*HookShapeTests'` — PASS. (The webhook-missing-endpoint case reads "Its 'endpoint' is missing or not a string." — the theory asserts the substring `'endpoint' is missing`.)

- [ ] **Step 5: Write the failing replace tests**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Editing a hook in place: what <c>ReplaceHook</c> writes, and what it refuses (spec §5.1).</summary>
public class WorkingCopyHookReplaceTests
{
    /// <summary>How the On write tab draws one hook — <c>HooksTab._readable</c>'s options.</summary>
    private static readonly JsonSerializerOptions _drawn = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void The_hook_at_its_position_is_replaced_and_the_others_keep_their_places()
    {
        var copy = Copy();

        copy.ReplaceHook("work_orders", "beforeUpdate", 0, Drawn(copy, 0), Hook("Edited.")).ShouldBeTrue();

        Messages(copy).ShouldBe(["Edited.", "Second."]);
    }

    [Fact]
    public void A_hook_that_changed_since_it_was_drawn_is_left_alone()
    {
        var copy = Copy();
        var drawn = Drawn(copy, 0);
        copy.ReplaceHook("work_orders", "beforeUpdate", 0, drawn, Hook("Another tab.")).ShouldBeTrue();
        var before = copy.Json;

        copy.ReplaceHook("work_orders", "beforeUpdate", 0, drawn, Hook("Stale.")).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Theory]
    [InlineData("work_orders", "beforeUpdate", 2)]
    [InlineData("work_orders", "beforeUpdate", -1)]
    [InlineData("work_orders", "afterCreate", 0)]
    [InlineData("invoices", "beforeUpdate", 0)]
    public void A_place_nothing_is_at_changes_nothing(string entity, string point, int position)
    {
        var copy = Copy();
        var before = copy.Json;

        copy.ReplaceHook(entity, point, position, Drawn(copy, 0), Hook("Edited.")).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void The_replacement_is_a_copy_so_a_later_change_to_the_argument_does_not_reach_the_document()
    {
        var copy = Copy();
        var hook = Hook("Edited.");

        copy.ReplaceHook("work_orders", "beforeUpdate", 0, Drawn(copy, 0), hook).ShouldBeTrue();
        hook["action"]!["reject"] = "Changed afterwards.";

        Messages(copy)[0].ShouldBe("Edited.");
    }

    [Fact]
    public void A_replacement_is_a_pending_edit_and_raises_one_change()
    {
        var copy = Copy();
        var raised = 0;
        copy.Changed += () => raised++;

        copy.ReplaceHook("work_orders", "beforeUpdate", 0, Drawn(copy, 0), Hook("Edited.")).ShouldBeTrue();

        raised.ShouldBe(1);
        copy.PendingCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_text_the_guard_compares_is_the_one_the_tab_draws_apostrophes_unescaped()
        => Drawn(Copy(), 0).ShouldContain("old.status == 'completed'");

    private static JsonObject Hook(string message) => new() { ["action"] = new JsonObject { ["reject"] = message } };

    private static string Drawn(WorkingCopy copy, int at)
        => ((JsonArray)JsonNode.Parse(copy.HooksOf("work_orders").Single(point => point.Key == "beforeUpdate").Value)!)[at]!
            .ToJsonString(_drawn);

    private static List<string> Messages(WorkingCopy copy)
        => [.. ((JsonArray)JsonNode.Parse(copy.Json)!["entities"]!["work_orders"]!["hooks"]!["beforeUpdate"]!)
            .Select(hook => hook!["action"]!["reject"]!.GetValue<string>())];

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "alvo.dev/v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": { "status": { "type": "string" } },
                  "hooks": {
                    "beforeUpdate": [
                      { "condition": "old.status == 'completed'", "action": { "reject": "First." } },
                      { "action": { "reject": "Second." } }
                    ]
                  }
                }
              }
            }
            """,
            revision: 3);

        return copy;
    }
}
```

- [ ] **Step 6: Run to verify failure** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*WorkingCopyHookReplaceTests'`. Expected: build FAIL — `ReplaceHook` is not defined.

- [ ] **Step 7: Implement `ReplaceHook`** — in `WorkingCopy.Hooks.cs`, after `RemoveHook`:

```csharp
    /// <summary>
    /// Replaces the hook at one position of one point with an edited one, keeping its place in the ordered list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Guarded by the one entry, not the whole list</b> (spec §5.1): an unrelated hook appended to the same point by
    /// another tab must not refuse an edit, and two identical entries are indistinguishable, so replacing either writes the
    /// same document. <see cref="RemoveHook"/> guards the list because a removal shifts every entry after it; a replace
    /// shifts nothing.
    /// </para>
    /// <para>
    /// <b>What is written is the caller's patched node</b> (<c>HookPatch</c>), cloned so the caller's object stays its own.
    /// This writer never rebuilds a hook: a shape the editor cannot draw never reaches it (<c>HookShape</c>).
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="position">The hook's position within that point.</param>
    /// <param name="expectedHook">The hook as the screen drew it (indented, relaxed encoder).</param>
    /// <param name="hook">The edited hook.</param>
    /// <returns><see langword="true"/> when it was written.</returns>
    public bool ReplaceHook(string entity, string point, int position, string expectedHook, JsonObject hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        return Edit(root =>
        {
            if (HookList(root, entity, point) is not { } list
                || position < 0
                || position >= list.Count
                || !string.Equals(Readable(list[position], "{}"), expectedHook, StringComparison.Ordinal))
            {
                return false;
            }

            list[position] = hook.DeepClone();
            return true;
        });
    }

    /// <summary>One point's hook list, when every container on the way to it is the shape the schema declares.</summary>
    private static JsonArray? HookList(JsonObject root, string entity, string point)
        => root["entities"] is JsonObject entities
           && entities[entity] is JsonObject declared
           && declared["hooks"] is JsonObject hooks
               ? hooks[point] as JsonArray
               : null;
```

- [ ] **Step 8: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*WorkingCopyHookReplaceTests' --filter-class '*WorkingCopyHookTests' --filter-class '*HookShapeTests'`
Expected: PASS. Then `scripts/test-ring0`.

- [ ] **Step 9: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/HookShape.cs src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Hooks.cs test/MMLib.Alvo.Admin.Tests/Schema/HookShapeTests.cs test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyHookReplaceTests.cs
git commit -m "feat(admin): replace a hook where it sits, guarded by what the screen drew, and know which hooks cannot be drawn

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 4: a mutate row, its literal fit, and the entity's fields

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/MutateRow.cs`, `MutateLiteral.cs`, `HookFields.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/MutateLiteralTests.cs`, `test/MMLib.Alvo.Admin.Tests/Schema/HookFieldsTests.cs`

**Interfaces:**
- Consumes: `CelFieldType.Of(FieldType)` and `CelValueType` (`MMLib.Alvo.Expressions`, Abstractions); `FieldSchema` (`MMLib.Alvo.Schema`); `PendingSchema.Read(string, string)` (`PendingSchema.cs:35`); `AlvoManagedColumns.For(EntitySchema)`.
- Produces:
  - `internal enum MutateMode { Literal, Expression }`
  - `internal sealed class MutateRow` — `MutateRow()`, `MutateRow(string field, MutateMode mode, string text)`, settable `Field`, `Mode` (default `Literal`), `Text`, `Empty`.
  - `internal static class MutateLiteral` — `bool TryValue(MutateRow row, FieldSchema field, out JsonNode? value, out string? refusal)`, `MutateRow Row(string field, JsonNode? value)`, `string Hint(FieldSchema field)`.
  - `internal static class HookFields` — `IReadOnlyDictionary<string, FieldSchema> Declared(string workingJson, string entity)`, `IReadOnlyList<string> Writable(string workingJson, string entity)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A mutate literal as the field's type holds it — the same <c>System.Text.Json</c> calls <c>BeforeHookCompiler.Convert</c>
/// makes (spec B3); Host.Tests holds the two to one answer.
/// </summary>
public class MutateLiteralTests
{
    [Theory]
    [InlineData(FieldType.String, "it's", "\"it's\"")]
    [InlineData(FieldType.String, "", "\"\"")]
    [InlineData(FieldType.Text, "line", "\"line\"")]
    [InlineData(FieldType.Integer, "3", "3")]
    [InlineData(FieldType.Integer, "-2", "-2")]
    [InlineData(FieldType.Decimal, "12.50", "12.50")]
    [InlineData(FieldType.Decimal, "-0.01", "-0.01")]
    [InlineData(FieldType.Boolean, "true", "true")]
    [InlineData(FieldType.DateTime, "2026-10-05T12:00:00Z", "\"2026-10-05T12:00:00Z\"")]
    [InlineData(FieldType.Uuid, "3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f", "\"3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f\"")]
    public void A_literal_the_field_holds_is_written_as_its_json(FieldType type, string text, string json)
    {
        MutateLiteral.TryValue(Row(text), Field(type), out var value, out var refusal).ShouldBeTrue(refusal);

        value!.ToJsonString().ShouldBe(json);
    }

    [Theory]
    [InlineData(FieldType.Integer, "1.5", "whole number")]
    [InlineData(FieldType.Integer, "", "whole number")]
    [InlineData(FieldType.Decimal, "1e3", "number")]
    [InlineData(FieldType.Decimal, "abc", "number")]
    [InlineData(FieldType.Boolean, "yes", "true or false")]
    [InlineData(FieldType.DateTime, "tomorrow", "date and time")]
    [InlineData(FieldType.Uuid, "42", "an id")]
    [InlineData(FieldType.Json, "{}", "json field")]
    public void A_literal_the_field_cannot_hold_is_refused_with_what_it_takes(FieldType type, string text, string says)
    {
        MutateLiteral.TryValue(Row(text), Field(type), out _, out var refusal).ShouldBeFalse();

        refusal.ShouldNotBeNull().ShouldContain(says);
    }

    [Fact]
    public void An_enum_literal_must_be_a_declared_value()
    {
        var status = new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "closed"] };

        MutateLiteral.TryValue(Row("open"), status, out _, out _).ShouldBeTrue();
        MutateLiteral.TryValue(Row("bogus"), status, out _, out var refusal).ShouldBeFalse();
        refusal.ShouldNotBeNull().ShouldContain("open, closed");
    }

    [Fact]
    public void Empty_is_written_as_null_only_into_a_field_that_is_not_required()
    {
        var empty = new MutateRow("f", MutateMode.Literal, string.Empty) { Empty = true };

        MutateLiteral.TryValue(empty, Field(FieldType.String), out var value, out _).ShouldBeTrue();
        value.ShouldBeNull();
        MutateLiteral.TryValue(empty, Field(FieldType.String) with { Required = true }, out _, out var refusal).ShouldBeFalse();
        refusal.ShouldNotBeNull().ShouldContain("required");
    }

    [Theory]
    [InlineData("\"it's\"", "it's", false)]
    [InlineData("12.50", "12.50", false)]
    [InlineData("true", "true", false)]
    [InlineData("null", "", true)]
    public void A_declared_literal_loads_into_a_row_that_writes_it_back(string json, string text, bool empty)
    {
        var row = MutateLiteral.Row("f", JsonNode.Parse(json));

        (row.Field, row.Mode, row.Text, row.Empty).ShouldBe(("f", MutateMode.Literal, text, empty));
    }

    private static MutateRow Row(string text) => new("f", MutateMode.Literal, text);

    private static FieldSchema Field(FieldType type) => new() { Name = "f", Type = type };
}
```

```csharp
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The fields a mutate may name (spec §4.3): declared in the working copy, not managed, not derived.</summary>
public class HookFieldsTests
{
    private const string Working = """
        {
          "entities": {
            "orders": {
              "audit": true,
              "fields": {
                "status": { "type": "enum", "values": ["open", "closed"], "required": true },
                "total": { "type": "decimal", "computed": "price * quantity" },
                "lines_count": { "type": "integer", "rollup": { "op": "count", "from": "order_lines" } },
                "price": { "type": "decimal" },
                "quantity": { "type": "integer" }
              }
            }
          }
        }
        """;

    [Fact]
    public void Every_declared_field_is_known_by_name_with_its_type()
    {
        var declared = HookFields.Declared(Working, "orders");

        declared.Keys.ShouldBe(["status", "total", "lines_count", "price", "quantity"], ignoreOrder: true);
        declared["status"].EnumValues.ShouldBe(["open", "closed"]);
        declared["status"].Required.ShouldBeTrue();
    }

    [Fact]
    public void A_computed_or_rollup_field_is_not_writable()
        => HookFields.Writable(Working, "orders").ShouldBe(["status", "price", "quantity"]);

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    public void An_entity_the_copy_does_not_declare_has_no_fields(string entity)
    {
        HookFields.Declared(Working, entity).ShouldBeEmpty();
        HookFields.Writable(Working, entity).ShouldBeEmpty();
    }

    [Fact]
    public void Text_that_is_not_a_descriptor_has_no_fields()
        => HookFields.Writable("not json", "orders").ShouldBeEmpty();
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*MutateLiteralTests' --filter-class '*HookFieldsTests'`. Expected: build FAIL.

- [ ] **Step 3: Implement the three files**

`MutateRow.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>How a mutate row writes its value.</summary>
internal enum MutateMode
{
    /// <summary>A JSON literal the field's type holds.</summary>
    Literal,

    /// <summary>A <c>{"$cel": "…"}</c> expression in the Mutate profile.</summary>
    Expression,
}

/// <summary>One field a mutate patches, as the hook editor holds it while it is typed.</summary>
internal sealed class MutateRow
{
    /// <summary>Initializes a blank row that writes a value.</summary>
    public MutateRow()
    {
    }

    /// <summary>Initializes a row.</summary>
    /// <param name="field">The field it patches.</param>
    /// <param name="mode">How it writes its value.</param>
    /// <param name="text">The literal as typed, or the CEL.</param>
    public MutateRow(string field, MutateMode mode, string text)
    {
        Field = field;
        Mode = mode;
        Text = text;
    }

    /// <summary>Gets or sets the field it patches.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets how it writes its value.</summary>
    public MutateMode Mode { get; set; } = MutateMode.Literal;

    /// <summary>Gets or sets the literal as typed, or the CEL.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a literal row sets the field to empty (<c>null</c>).</summary>
    public bool Empty { get; set; }
}
```

`MutateLiteral.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// A mutate literal as the target field holds it, or why it cannot — checked as the operator types (spec B3, §4.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The same calls the apply makes.</b> <c>BeforeHookCompiler.Convert</c> reads the literal's JSON with
/// <c>TryGetInt64</c>, <c>TryGetDecimal</c>, <c>TryGetDateTimeOffset</c> and <c>TryGetGuid</c>
/// (src/MMLib.Alvo/Rules/Internal/BeforeHookCompiler.cs:435-445); this converts the typed text to the JSON it will write
/// and asks the same <c>System.Text.Json</c> questions, so a literal accepted here is accepted there
/// (<c>HooksEditorAgreementTests</c>).
/// </para>
/// <para>
/// <b>Stricter in one place, stated</b>: an enum literal must be a declared value. The apply converts it as a plain
/// string and checks no membership (spec §11) — a finding, not a rule to copy.
/// </para>
/// </remarks>
internal static class MutateLiteral
{
    /// <summary>The JSON a literal row writes into a field, or why the field cannot hold it.</summary>
    /// <param name="row">The row.</param>
    /// <param name="field">The field it patches, as the working copy declares it.</param>
    /// <param name="value">The literal; <see langword="null"/> for empty.</param>
    /// <param name="refusal">Why it cannot be written, when it cannot.</param>
    /// <returns><see langword="true"/> when it can be written.</returns>
    public static bool TryValue(MutateRow row, FieldSchema field, out JsonNode? value, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(field);
        if (row.Empty)
        {
            value = null;
            refusal = EmptyRefusal(field);
            return refusal is null;
        }

        (value, refusal) = Convert(row.Text, field);
        return refusal is null;
    }

    /// <summary>A declared literal as the row that writes it back.</summary>
    /// <param name="field">The field it patches.</param>
    /// <param name="value">The declared literal.</param>
    public static MutateRow Row(string field, JsonNode? value) => value switch
    {
        null => new MutateRow(field, MutateMode.Literal, string.Empty) { Empty = true },
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => new MutateRow(field, MutateMode.Literal, scalar.GetValue<string>()),
        _ => new MutateRow(field, MutateMode.Literal, value.ToJsonString()),
    };

    /// <summary>What a literal box for this field takes, in one sentence.</summary>
    /// <param name="field">The field.</param>
    public static string Hint(FieldSchema field) => CelFieldType.Of(field.Type) switch
    {
        CelValueType.String => "Text, written as typed.",
        CelValueType.Int => "A whole number such as 3 or -2.",
        CelValueType.Decimal => "A number such as 12.5.",
        CelValueType.Bool => "true or false.",
        CelValueType.Timestamp => "A date and time such as 2026-10-05T12:00:00Z.",
        CelValueType.Uuid => "An id such as 3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f.",
        _ => "A json field takes no literal value here.",
    };

    private static string? EmptyRefusal(FieldSchema field) => field.Required
        ? $"'{field.Name}' is required, so a hook cannot set it to empty: every write it fires on would carry a null the engine refuses."
        : null;

    private static (JsonNode? Value, string? Refusal) Convert(string text, FieldSchema field) => CelFieldType.Of(field.Type) switch
    {
        CelValueType.String => Text(text, field),
        CelValueType.Int => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
            ? (JsonValue.Create(whole), null)
            : (null, Shape(field, "a whole number such as 3 or -2")),
        CelValueType.Decimal => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? (JsonValue.Create(number), null)
            : (null, Shape(field, "a number such as 12.5")),
        CelValueType.Bool => text is "true" or "false" ? (JsonValue.Create(text == "true"), null) : (null, Shape(field, "true or false")),
        CelValueType.Timestamp => Reads(text, element => element.TryGetDateTimeOffset(out _))
            ? (JsonValue.Create(text), null)
            : (null, Shape(field, "a date and time such as 2026-10-05T12:00:00Z")),
        CelValueType.Uuid => Reads(text, element => element.TryGetGuid(out _))
            ? (JsonValue.Create(text), null)
            : (null, Shape(field, "an id such as 3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f")),
        _ => (null, $"'{field.Name}' is a json field, and this build converts no literal into one. Write an expression, or set it to empty."),
    };

    private static (JsonNode? Value, string? Refusal) Text(string text, FieldSchema field)
    {
        if (field.EnumValues is { Count: > 0 } values && !values.Contains(text, StringComparer.Ordinal))
        {
            return (null, $"'{text}' is not one of {field.Name}'s values: {string.Join(", ", values)}.");
        }

        return (JsonValue.Create(text), null);
    }

    /// <summary>Asks one <c>System.Text.Json</c> question of the JSON string the row would write — the apply's own question.</summary>
    private static bool Reads(string text, Func<JsonElement, bool> question)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(text));
        return question(document.RootElement);
    }

    private static string Shape(FieldSchema field, string takes)
        => $"'{field.Name}' is a {field.Type.ToString().ToLowerInvariant()} field: it takes {takes}.";
}
```

`HookFields.cs`:

```csharp
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The entity's fields as the working copy declares them, for the hook editor's mutate rows (spec §4.3).</summary>
/// <remarks>
/// Read through <see cref="PendingSchema"/> — the reader the entity screen already draws a staged entity with — so a field
/// added a minute ago is offered and its type, values and <c>required</c> are the ones apply will see.
/// </remarks>
internal static class HookFields
{
    /// <summary>Every field the entity declares, by name.</summary>
    /// <param name="workingJson">The working copy's text.</param>
    /// <param name="entity">The entity.</param>
    public static IReadOnlyDictionary<string, FieldSchema> Declared(string workingJson, string entity)
        => PendingSchema.Read(workingJson, entity)?.Fields
               .DistinctBy(field => field.Name, StringComparer.Ordinal)
               .ToDictionary(field => field.Name, StringComparer.Ordinal)
           ?? new Dictionary<string, FieldSchema>(StringComparer.Ordinal);

    /// <summary>The fields a mutate may patch, in declaration order.</summary>
    /// <remarks>
    /// Not a column the framework manages (apply refuses those, <c>BeforeHookCompiler.Target</c>), and not a computed or
    /// rollup field, which the database or the framework maintains — apply does not refuse a mutate of one (unverified what
    /// the write then does), so the form simply does not offer it.
    /// </remarks>
    /// <param name="workingJson">The working copy's text.</param>
    /// <param name="entity">The entity.</param>
    public static IReadOnlyList<string> Writable(string workingJson, string entity)
    {
        if (PendingSchema.Read(workingJson, entity) is not { } schema)
        {
            return [];
        }

        var managed = AlvoManagedColumns.For(schema);
        return [.. schema.Fields
            .Where(field => !managed.Contains(field.Name) && field.ComputedExpression is null && field.Rollup is null)
            .Select(field => field.Name)];
    }
}
```

- [ ] **Step 4: Run, normalise, run again** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*MutateLiteralTests' --filter-class '*HookFieldsTests'`. Expected: PASS (`FieldSchema` is a `sealed record`, so `with` compiles). If `DateTime` parsing of `2026-10-05T12:00:00Z` fails on `TryGetDateTimeOffset`, read `BeforeHookCompiler.Convert` again — the apply's own answer is the expectation, not this plan's. Then `scripts/test-ring0`.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/MutateRow.cs src/MMLib.Alvo.Admin/Components/Schema/MutateLiteral.cs src/MMLib.Alvo.Admin/Components/Schema/HookFields.cs test/MMLib.Alvo.Admin.Tests/Schema/MutateLiteralTests.cs test/MMLib.Alvo.Admin.Tests/Schema/HookFieldsTests.cs
git commit -m "feat(admin): a mutate literal checked against its field the way apply converts it

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---
### Task 5: `HookPatch` — an edit writes only what changed

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/HookPatch.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/HookPatchTests.cs`

**Interfaces:**
- Consumes: the `HookBuilder.Reject` / `HookBuilder.Mutate` constants (`HookBuilder.cs:22-25`, unchanged).
- Produces: `internal static class HookPatch { public static JsonObject Apply(JsonObject? original, string? condition, JsonObject action); }` (spec §5.3) — a new hook condition first; an edit keeps the original's key order, merges the action in place while its kind is unchanged and replaces it when the kind changed.

- [ ] **Step 1: Write the failing patch tests**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What an edit writes onto the hook it opened (spec §5.3, D2): only what the form changed, in the original's order.</summary>
public class HookPatchTests
{
    [Fact]
    public void A_new_hook_is_written_condition_first()
        => Written(null, "new.a == 'x'", Reject("No.")).ShouldBe("""{"condition":"new.a == 'x'","action":{"reject":"No."}}""");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_new_hook_with_no_condition_carries_no_condition_key(string? condition)
        => Written(null, condition, Reject("No.")).ShouldBe("""{"action":{"reject":"No."}}""");

    [Fact]
    public void An_edit_keeps_the_originals_key_order()
        => Written(Parse("""{"action":{"reject":"Old."},"condition":"new.a == 1"}"""), "new.a == 2", Reject("New."))
            .ShouldBe("""{"action":{"reject":"New."},"condition":"new.a == 2"}""");

    [Fact]
    public void Clearing_the_condition_removes_its_key()
        => Written(Parse("""{"condition":"new.a == 1","action":{"reject":"Old."}}"""), string.Empty, Reject("Old."))
            .ShouldBe("""{"action":{"reject":"Old."}}""");

    [Fact]
    public void A_condition_added_to_a_hook_without_one_goes_first()
        => Written(Parse("""{"action":{"reject":"Old."}}"""), "new.a == 1", Reject("Old."))
            .ShouldBe("""{"condition":"new.a == 1","action":{"reject":"Old."}}""");

    [Fact]
    public void Keeping_the_kind_merges_the_action_in_place()
        => Written(
                Parse("""{"action":{"payload":"[1]","type":"webhook","endpoint":"a"}}"""),
                null,
                Parse("""{"type":"webhook","endpoint":"b","payload":"[2]"}"""))
            .ShouldBe("""{"action":{"payload":"[2]","type":"webhook","endpoint":"b"}}""");

    [Fact]
    public void A_payload_the_form_cleared_is_removed()
        => Written(Parse("""{"action":{"type":"webhook","endpoint":"a","payload":"[1]"}}"""), null, Parse("""{"type":"webhook","endpoint":"a"}"""))
            .ShouldBe("""{"action":{"type":"webhook","endpoint":"a"}}""");

    [Fact]
    public void Changing_the_kind_replaces_the_action()
        => Written(
                Parse("""{"action":{"type":"webhook","endpoint":"a","payload":"[1]"}}"""),
                null,
                Parse("""{"type":"email","template":"t","to":"a@b.c"}"""))
            .ShouldBe("""{"action":{"type":"email","template":"t","to":"a@b.c"}}""");

    [Fact]
    public void A_mutate_keeps_its_fields_in_their_order_and_appends_new_ones()
        => Written(Parse("""{"action":{"mutate":{"b":1,"a":2}}}"""), null, Parse("""{"mutate":{"a":3,"c":4,"b":1}}"""))
            .ShouldBe("""{"action":{"mutate":{"b":1,"a":3,"c":4}}}""");

    [Fact]
    public void A_mutate_field_the_form_removed_is_removed()
        => Written(Parse("""{"action":{"mutate":{"a":1,"b":2}}}"""), null, Parse("""{"mutate":{"a":1}}"""))
            .ShouldBe("""{"action":{"mutate":{"a":1}}}""");

    [Fact]
    public void The_original_and_the_action_are_left_as_they_were()
    {
        var original = Parse("""{"condition":"new.a == 1","action":{"reject":"Old."}}""");
        var action = Reject("New.");
        var before = (original.ToJsonString(), action.ToJsonString());

        HookPatch.Apply(original, null, action);

        (original.ToJsonString(), action.ToJsonString()).ShouldBe(before);
    }

    private static string Written(JsonObject? original, string? condition, JsonObject action)
        => HookPatch.Apply(original, condition, action).ToJsonString(Relaxed.Options);

    private static JsonObject Reject(string message) => new() { ["reject"] = message };

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*HookPatchTests'`. Expected: build FAIL — `HookPatch` is not defined.

- [ ] **Step 3: Implement `HookPatch.cs`**

```csharp
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Writes what the hook editor holds onto the hook it opened — or into a new one — changing only what the form changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A patch, never a rebuild</b> (ruling B1, spec §5.3). The editor only opens a hook it can draw whole
/// (<see cref="HookShape"/>), so a rebuild would lose nothing — except the author's key order, and Preview would then show
/// every reordered line as a change nobody made. The condition is replaced where it stands; the action is merged in place
/// while its kind is unchanged and replaced when the operator chose another kind (D2).
/// </para>
/// <para>A new hook is written condition first, the order <see cref="WorkingCopy.AddHook"/> writes.</para>
/// </remarks>
internal static class HookPatch
{
    /// <summary>The hook to write.</summary>
    /// <param name="original">The hook as declared, or <see langword="null"/> for a new one; not modified.</param>
    /// <param name="condition">The condition, or blank for a hook that always runs.</param>
    /// <param name="action">The action the form built; not modified.</param>
    public static JsonObject Apply(JsonObject? original, string? condition, JsonObject action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (original?.DeepClone() is not JsonObject hook)
        {
            return Fresh(condition, action);
        }

        SetCondition(hook, condition);
        hook["action"] = hook["action"] is JsonObject old && KindOf(old) == KindOf(action)
            ? Merged(old, action)
            : action.DeepClone();
        return hook;
    }

    private static JsonObject Fresh(string? condition, JsonObject action)
    {
        var hook = new JsonObject();
        if (!string.IsNullOrWhiteSpace(condition))
        {
            hook["condition"] = condition;
        }

        hook["action"] = action.DeepClone();
        return hook;
    }

    private static void SetCondition(JsonObject hook, string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            hook.Remove("condition");
            return;
        }

        if (hook.ContainsKey("condition"))
        {
            hook["condition"] = condition;
            return;
        }

        var rest = hook.Select(pair => (pair.Key, Value: pair.Value?.DeepClone())).ToList();
        hook.Clear();
        hook["condition"] = condition;
        foreach (var (key, value) in rest)
        {
            hook[key] = value;
        }
    }

    private static string? KindOf(JsonObject action)
        => action.ContainsKey(HookBuilder.Reject) ? HookBuilder.Reject
            : action.ContainsKey(HookBuilder.Mutate) ? HookBuilder.Mutate
            : action["type"] is JsonValue type && type.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Merged(JsonObject old, JsonObject action)
    {
        var merged = (JsonObject)old.DeepClone();
        foreach (var key in merged.Select(pair => pair.Key).Where(key => !action.ContainsKey(key)).ToList())
        {
            merged.Remove(key);
        }

        foreach (var (key, value) in action)
        {
            merged[key] = key == HookBuilder.Mutate && merged[key] is JsonObject before && value is JsonObject after
                ? Ordered(before, after)
                : value?.DeepClone();
        }

        return merged;
    }

    /// <summary>The new patch with the fields it keeps in the old order, then the fields it adds.</summary>
    private static JsonObject Ordered(JsonObject before, JsonObject after)
    {
        var patch = new JsonObject();
        foreach (var key in before.Select(pair => pair.Key).Where(after.ContainsKey))
        {
            patch[key] = after[key]?.DeepClone();
        }

        foreach (var (key, value) in after.Where(pair => !before.ContainsKey(pair.Key)))
        {
            patch[key] = value?.DeepClone();
        }

        return patch;
    }
}
```

- [ ] **Step 4: Run, normalise, run again** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*HookPatchTests'`. Expected: PASS. `scripts/test-ring0`.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/HookPatch.cs test/MMLib.Alvo.Admin.Tests/Schema/HookPatchTests.cs
git commit -m "feat(admin): patch an edit onto the hook it opened, keeping the author's order

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 6: `HookBuilder` learns rows, a payload and loading

**Files:**
- Modify (full replacement): `src/MMLib.Alvo.Admin/Components/Schema/HookBuilder.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/HookBuilder.Load.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor` (mutate case of `ActionFields`), `HooksTab.razor.cs` (mutate typing, check, `Dirty`, `AddAsync`) — interim, so the tab compiles and behaves as today until Task 9
- Modify: `test/MMLib.Alvo.Admin.Tests/Schema/HookBuilderTests.cs`, `test/MMLib.Alvo.Admin.Tests/Schema/ExpressionSlotsTests.cs` (the `MutateField` initialisers)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/HookBuilderEditTests.cs`

**Interfaces:**
- Consumes: `ConditionTable` (Task 1), `HookShape.Undrawable` (Task 3), `MutateRow`, `MutateMode`, `MutateLiteral` (Task 4), `HookPatch.Apply` (Task 5), `FieldSchema`.
- Produces: `HookBuilder` (now `internal sealed partial class`): removed `MutateField`/`MutateValue`; added `List<MutateRow> MutateRows` (starts **empty**), `string Payload`, `IReadOnlyDictionary<string, FieldSchema> Fields`, `bool PointLocked`, `bool HasInput`, `string Fingerprint()`, `JsonObject? BuildHook(JsonObject? original, out string? refusal)`, `JsonObject CandidateHook(JsonObject? original, bool withCondition)`, `static HookBuilder? From(string point, JsonObject hook, IReadOnlyDictionary<string, FieldSchema> fields)`, `const int MaxPayloadLength = 8000`. Kept: `Points`, `Point`, `Kind`, `Condition`, `RejectMessage`, `Endpoint`, `Template`, `To`, `Kinds`, `Images`, `Example`, `IsBefore`, `Choose` (ignored while `PointLocked`), `Build`, `Draft`, `Clear`.

- [ ] **Step 1: Write the failing builder edit tests**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The hook editor opening a declared hook, and what Save writes back (spec §4.2, §5.2, §5.3, B1, B3, D1, D2, D4).</summary>
public class HookBuilderEditTests
{
    private static readonly IReadOnlyDictionary<string, FieldSchema> _fields = new Dictionary<string, FieldSchema>(StringComparer.Ordinal)
    {
        ["completed_at"] = new() { Name = "completed_at", Type = FieldType.DateTime },
        ["status"] = new() { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "closed"], Required = true },
        ["quantity"] = new() { Name = "quantity", Type = FieldType.Integer },
        ["paid"] = new() { Name = "paid", Type = FieldType.Boolean },
        ["note"] = new() { Name = "note", Type = FieldType.String },
    };

    [Theory]
    [InlineData("beforeUpdate", """{"condition":"old.status == 'closed'","action":{"reject":"Closed."}}""")]
    [InlineData("beforeUpdate", """{"action":{"mutate":{"completed_at":{"$cel":"now()"},"status":"open","quantity":3,"paid":true,"note":null}}}""")]
    [InlineData("afterCreate", """{"action":{"type":"webhook","endpoint":"desk","payload":"[{{new.quantity}}]"},"condition":"new.paid == true"}""")]
    [InlineData("afterUpdate", """{"action":{"type":"email","template":"done","to":"{{new.note}}"}}""")]
    public void A_drawable_hook_saved_unchanged_is_written_back_byte_for_byte(string point, string json)
    {
        var original = Parse(json);
        var builder = HookBuilder.From(point, original, _fields).ShouldNotBeNull();

        var saved = builder.BuildHook(original, out var refusal);

        saved.ShouldNotBeNull(refusal).ToJsonString(Relaxed.Options).ShouldBe(original.ToJsonString(Relaxed.Options));
    }

    [Fact]
    public void A_loaded_hook_fixes_its_point()
    {
        var builder = HookBuilder.From("beforeUpdate", Parse("""{"action":{"reject":"x"}}"""), _fields)!;

        builder.Choose("afterCreate");

        (builder.Point, builder.PointLocked, builder.Kind).ShouldBe(("beforeUpdate", true, HookBuilder.Reject));
    }

    [Fact]
    public void A_loaded_mutate_keeps_each_fields_mode_and_order()
        => HookBuilder.From(
                "beforeUpdate",
                Parse("""{"action":{"mutate":{"completed_at":{"$cel":"now()"},"status":"open","note":null}}}"""),
                _fields)!
            .MutateRows.Select(row => (row.Field, row.Mode, row.Text, row.Empty)).ShouldBe(
            [
                ("completed_at", MutateMode.Expression, "now()", false),
                ("status", MutateMode.Literal, "open", false),
                ("note", MutateMode.Literal, string.Empty, true),
            ]);

    [Fact]
    public void A_shape_the_editor_cannot_draw_does_not_load()
        => HookBuilder.From("afterUpdate", Parse("""{"action":{"type":"email","template":"t","to":"a@b.c","data":"{{new.a}}"}}"""), _fields)
            .ShouldBeNull();

    [Fact]
    public void Editing_a_webhook_keeps_its_key_order_and_clearing_the_payload_removes_it()
    {
        var original = Parse("""{"action":{"type":"webhook","endpoint":"desk","payload":"[1]"},"condition":"new.paid == true"}""");
        var builder = HookBuilder.From("afterCreate", original, _fields)!;
        builder.Endpoint = "billing";
        builder.Payload = string.Empty;

        builder.BuildHook(original, out _)!.ToJsonString(Relaxed.Options)
            .ShouldBe("""{"action":{"type":"webhook","endpoint":"billing"},"condition":"new.paid == true"}""");
    }

    [Fact]
    public void Switching_the_kind_replaces_the_action_and_drops_what_belonged_to_the_old_one()
    {
        var original = Parse("""{"action":{"type":"webhook","endpoint":"desk","payload":"[1]"}}""");
        var builder = HookBuilder.From("afterCreate", original, _fields)!;
        builder.Kind = HookBuilder.Email;
        builder.Template = "done";
        builder.To = "ops@example.com";

        builder.BuildHook(original, out _)!.ToJsonString(Relaxed.Options)
            .ShouldBe("""{"action":{"type":"email","template":"done","to":"ops@example.com"}}""");
    }

    [Theory]
    [InlineData("quantity", "1.5", "whole number")]
    [InlineData("status", "bogus", "open, closed")]
    [InlineData("missing", "x", "not a field")]
    public void A_mutate_row_that_cannot_be_written_is_refused_with_its_reason(string field, string text, string says)
    {
        var builder = new HookBuilder { Kind = HookBuilder.Mutate, Fields = _fields, MutateRows = { new MutateRow(field, MutateMode.Literal, text) } };

        builder.Build(out var refusal).ShouldBeNull();

        refusal.ShouldNotBeNull().ShouldContain(says);
    }

    [Fact]
    public void A_field_patched_twice_is_refused()
    {
        var builder = new HookBuilder
        {
            Kind = HookBuilder.Mutate,
            Fields = _fields,
            MutateRows = { new MutateRow("note", MutateMode.Literal, "a"), new MutateRow("note", MutateMode.Expression, "'b'") },
        };

        builder.Build(out var refusal).ShouldBeNull();

        refusal.ShouldNotBeNull().ShouldContain("patched twice");
    }

    [Fact]
    public void Several_fields_are_one_mutate_a_literal_beside_an_expression()
    {
        var builder = new HookBuilder
        {
            Kind = HookBuilder.Mutate,
            Fields = _fields,
            MutateRows = { new MutateRow("paid", MutateMode.Literal, "true"), new MutateRow("note", MutateMode.Expression, "'Paid in full'") },
        };

        builder.Build(out _)!.ToJsonString(Relaxed.Options).ShouldBe("""{"mutate":{"paid":true,"note":{"$cel":"'Paid in full'"}}}""");
    }

    [Fact]
    public void A_candidate_for_an_action_slot_carries_no_condition_so_a_broken_one_cannot_silence_its_check()
    {
        var builder = new HookBuilder { Kind = HookBuilder.Webhook, Endpoint = "desk", Payload = "[1]", Condition = "new.nope ==" };
        builder.Choose("afterCreate");

        builder.CandidateHook(original: null, withCondition: false).ContainsKey("condition").ShouldBeFalse();
        builder.CandidateHook(original: null, withCondition: true)["condition"]!.GetValue<string>().ShouldBe("new.nope ==");
    }

    [Fact]
    public void The_fingerprint_moves_with_anything_typed_and_not_without()
    {
        var builder = HookBuilder.From("beforeUpdate", Parse("""{"action":{"reject":"x"}}"""), _fields)!;
        var opened = builder.Fingerprint();

        builder.Fingerprint().ShouldBe(opened);
        builder.RejectMessage = "y";
        builder.Fingerprint().ShouldNotBe(opened);
    }

    [Fact]
    public void A_fresh_builder_has_no_input_until_something_is_typed()
    {
        var builder = new HookBuilder();
        builder.HasInput.ShouldBeFalse();

        builder.MutateRows.Add(new MutateRow());
        builder.HasInput.ShouldBeFalse();

        builder.MutateRows[0].Text = "1";
        builder.HasInput.ShouldBeTrue();
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
```

- [ ] **Step 2: Update the two existing test files** (they set the removed `MutateField`):
  - `HookBuilderTests.cs`: add `using MMLib.Alvo.Schema;`. In `An_action_missing_what_the_schema_requires_is_refused` replace `new HookBuilder { Kind = kind, MutateField = "status", Template = "done" }` with `new HookBuilder { Kind = kind, MutateRows = { new MutateRow("status", MutateMode.Expression, string.Empty) }, Template = "done" }`. Replace the body of `A_mutate_stores_its_value_as_cel` with:

```csharp
        => Built(new HookBuilder
        {
            Kind = HookBuilder.Mutate,
            MutateRows = { new MutateRow("completed_on", MutateMode.Expression, "now()") },
            Fields = new Dictionary<string, FieldSchema>(StringComparer.Ordinal)
            {
                ["completed_on"] = new() { Name = "completed_on", Type = FieldType.DateTime },
            },
        }).ShouldBe("""{"mutate":{"completed_on":{"$cel":"now()"}}}""");
```

  - `ExpressionSlotsTests.cs` (two places, `:221` and `:254`): replace `new HookBuilder { Kind = kind, MutateField = "total" }` with `new HookBuilder { Kind = kind, MutateRows = { new MutateRow("total", MutateMode.Expression, string.Empty) } }` and `new HookBuilder { Kind = HookBuilder.Mutate, MutateField = "total" }` with `new HookBuilder { Kind = HookBuilder.Mutate, MutateRows = { new MutateRow("total", MutateMode.Expression, string.Empty) } }`.

- [ ] **Step 3: Run to verify failure** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*HookBuilderEditTests'`. Expected: build FAIL — `MutateRows`, `From` are not defined.

- [ ] **Step 4: Replace `HookBuilder.cs` whole** (its class remarks stay; the new members are documented):

```csharp
using MMLib.Alvo.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// What the hook editor holds, and the action it builds from it in the schema's own shape — for a new hook, or for a declared
/// one it opened (<see cref="From"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A before-hook and an after-hook are not the same editor.</b> The frozen schema's
/// <c>$defs/beforeHookList</c> admits <c>reject</c> and <c>mutate</c> only — a before-hook runs inside the
/// write's own transaction, with no network — and an after-hook is where a webhook or an email belongs. The
/// kinds a point admits are therefore decided by the point, and switching the point moves the kind with it.
/// </para>
/// <para>
/// <b>An edit is a patch</b> (<see cref="BuildHook"/>, <see cref="HookPatch"/>): what the form changed, written onto the
/// hook it opened. A mutate patches any number of fields, each with a literal or a CEL value (spec §4.3).
/// </para>
/// <para>
/// <b>Out of the component so it can be tested</b> (docs/architecture/admin-dashboard-review.md, F-13).
/// </para>
/// </remarks>
internal sealed partial class HookBuilder
{
    /// <summary>The kind that refuses the write.</summary>
    public const string Reject = "reject";

    /// <summary>The kind that patches the row.</summary>
    public const string Mutate = "mutate";

    /// <summary>The kind that delivers to a declared endpoint.</summary>
    public const string Webhook = "webhook";

    /// <summary>The kind that sends a templated email.</summary>
    public const string Email = "email";

    /// <summary>The most characters a payload holds (schema <c>$defs/jsonata</c>).</summary>
    public const int MaxPayloadLength = 8000;

    private static readonly string[] _beforeKinds = [Reject, Mutate];
    private static readonly string[] _deleteKinds = [Reject];
    private static readonly string[] _afterKinds = [Webhook, Email];
    private static readonly IReadOnlyDictionary<string, FieldSchema> _noFields = new Dictionary<string, FieldSchema>(StringComparer.Ordinal);

    /// <summary>The six hook points, in the order a write passes them.</summary>
    public static IReadOnlyList<string> Points { get; } =
        ["beforeCreate", "beforeUpdate", "beforeDelete", "afterCreate", "afterUpdate", "afterDelete"];

    /// <summary>The point the hook is declared at.</summary>
    public string Point { get; private set; } = Points[0];

    /// <summary>Whether the point is fixed — an opened hook keeps its place (spec D1).</summary>
    public bool PointLocked { get; private set; }

    /// <summary>The action's kind; one of <see cref="Kinds"/>.</summary>
    public string Kind { get; set; } = Reject;

    /// <summary>The CEL guard, or empty for a hook that always runs.</summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>A reject's message, which the caller reads as the problem's detail.</summary>
    public string RejectMessage { get; set; } = string.Empty;

    /// <summary>The fields a mutate patches, in the order they are written; empty until the form adds one.</summary>
    public List<MutateRow> MutateRows { get; } = [];

    /// <summary>A webhook's endpoint, by its declared name.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>A webhook's <c>{{…}}</c> payload, or empty for the CloudEvents envelope.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>An email's template.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>Who an email goes to.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>The entity's fields as the working copy declares them, which a mutate literal is converted for.</summary>
    public IReadOnlyDictionary<string, FieldSchema> Fields { get; set; } = _noFields;

    /// <summary>
    /// The action kinds this point admits.
    /// </summary>
    /// <remarks>
    /// <b>Three answers, not two, and the third does not come from the schema.</b> <c>$defs/beforeHookList</c>
    /// is one definition for all three before-points, so the schema alone would offer <c>mutate</c> at
    /// <c>beforeDelete</c> — which <c>BeforeHookCompiler</c> refuses outright, because the row is being
    /// removed and a patch against it is a slot that compiles cleanly and is then discarded.
    /// </remarks>
    public IReadOnlyList<string> Kinds => Point switch
    {
        "beforeDelete" => _deleteKinds,
        _ => IsBefore(Point) ? _beforeKinds : _afterKinds,
    };

    /// <summary>The row images a condition at this point may name, as markup — read off <see cref="ConditionTable"/>.</summary>
    public string Images => ConditionTable.ImagesMarkup(Point);

    /// <summary>A condition that is valid at this point, for the placeholder — read off <see cref="ConditionTable"/>.</summary>
    public string Example => ConditionTable.Example(Point);

    /// <summary>Whether anything has been typed — what closing a new hook's editor would lose.</summary>
    /// <remarks>The point and the kind are not counted, for the reason <c>HooksTab.Dirty</c> gives.</remarks>
    public bool HasInput => Condition.Length > 0 || RejectMessage.Length > 0 || Endpoint.Length > 0 || Payload.Length > 0
        || Template.Length > 0 || To.Length > 0
        || MutateRows.Any(row => row.Field.Length > 0 || row.Text.Length > 0 || row.Empty);

    /// <summary>Whether a point runs inside the write's transaction.</summary>
    public static bool IsBefore(string point)
        => point.StartsWith("before", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Switches the point, and the action kind with it when the current one no longer fits — unless the point is fixed.
    /// </summary>
    /// <remarks>
    /// Moving from <c>afterCreate</c> to <c>beforeCreate</c> with <c>webhook</c> still selected would leave
    /// the editor holding a descriptor the apply refuses — a before-hook may not touch the network — and the
    /// refusal would name a choice the operator never made.
    /// </remarks>
    public void Choose(string point)
    {
        if (PointLocked)
        {
            return;
        }

        Point = point;
        if (!Kinds.Contains(Kind))
        {
            Kind = Kinds[0];
        }
    }

    /// <summary>Everything the form holds, as one string: an edit is dirty while this differs from what it opened with.</summary>
    public string Fingerprint() => JsonSerializer.Serialize(new
    {
        Point,
        Kind,
        Condition,
        RejectMessage,
        Endpoint,
        Payload,
        Template,
        To,
        Rows = MutateRows.Select(row => new { row.Field, Mode = row.Mode.ToString(), row.Text, row.Empty }),
    });

    /// <summary>
    /// The action in the schema's shape, or why it cannot be built.
    /// </summary>
    /// <remarks>
    /// The schema's own <c>required</c> per kind, and for a mutate the rows' own refusals: a field named twice, a field the
    /// working copy does not declare, a literal its type cannot hold. Whether the CEL compiles, or the endpoint or the
    /// template exists, is the apply's — and the live check's.
    /// </remarks>
    /// <param name="refusal">Why the action cannot be built, when it cannot.</param>
    /// <returns>The action, or <see langword="null"/> with <paramref name="refusal"/> set.</returns>
    public JsonObject? Build(out string? refusal)
    {
        refusal = Missing() ?? (Kind == Mutate ? MutateRefusal() : null);
        return refusal is null ? Action() : null;
    }

    /// <summary>The whole hook to write: the form patched onto <paramref name="original"/>, or a new hook.</summary>
    /// <param name="original">The declared hook the editor opened, or <see langword="null"/> for a new one.</param>
    /// <param name="refusal">Why it cannot be built, when it cannot.</param>
    public JsonObject? BuildHook(JsonObject? original, out string? refusal)
        => Build(out refusal) is { } action ? HookPatch.Apply(original, Condition, action) : null;

    /// <summary>
    /// The action the form would add, or — while it cannot be built yet — the same kind with the smallest valid stand-in
    /// for each facet still blank.
    /// </summary>
    /// <remarks>
    /// For the live expression check, which asks about one box before the rest of the form is filled in: the answer is
    /// filtered to that box's own slot, so the stand-ins are never judged. Never staged; <see cref="Build"/> is the way in.
    /// </remarks>
    /// <returns>The action in the schema's shape.</returns>
    public JsonObject Draft() => Build(out _) ?? Standing().Action();

    /// <summary>The hook a check is asked about: the draft patched onto the original, with or without the condition.</summary>
    /// <remarks>
    /// An action slot is checked without the condition (spec D4): <c>AfterHookCompiler.CompileHook</c> skips the action when
    /// the condition fails, so a broken condition would otherwise read as a green payload.
    /// </remarks>
    /// <param name="original">The declared hook the editor opened, or <see langword="null"/>.</param>
    /// <param name="withCondition">Whether the condition is part of the question.</param>
    public JsonObject CandidateHook(JsonObject? original, bool withCondition)
        => HookPatch.Apply(original, withCondition ? Condition : null, Draft());

    /// <summary>Empties what was typed, and keeps the point and the kind for the next hook.</summary>
    public void Clear()
    {
        Condition = string.Empty;
        RejectMessage = string.Empty;
        Endpoint = string.Empty;
        Payload = string.Empty;
        Template = string.Empty;
        To = string.Empty;
        MutateRows.Clear();
    }

    /// <summary>The schema's required facet the kind is missing, or nothing.</summary>
    /// <remarks>
    /// Anything that is not one of the first three is built as an email, so it is refused as one: a kind
    /// this editor does not know can never build an email with no template and no recipient.
    /// </remarks>
    private string? Missing() => Kind switch
    {
        Reject => Blank(RejectMessage) ? "A reject carries the message the caller reads. Write one." : null,
        Mutate => MutateRows.Count == 0 || MutateRows.Any(IsIncomplete)
            ? "A mutate patches at least one field. Name the field and what to set it to."
            : null,
        Webhook => Blank(Endpoint) ? "A webhook names an endpoint declared under 'webhooks.endpoints'." : null,
        _ => Blank(Template) || Blank(To) ? "An email names a template and who it goes to." : null,
    };

    private static bool IsIncomplete(MutateRow row) => Blank(row.Field) || (row.Mode == MutateMode.Expression && Blank(row.Text));

    private string? MutateRefusal()
    {
        var twice = MutateRows.GroupBy(row => row.Field.Trim(), StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        return twice is not null
            ? $"'{twice.Key}' is patched twice. Keep one row per field."
            : MutateRows.Select(RowRefusal).FirstOrDefault(refusal => refusal is not null);
    }

    private string? RowRefusal(MutateRow row)
    {
        var name = row.Field.Trim();
        if (!Fields.TryGetValue(name, out var field))
        {
            return $"'{name}' is not a field of this entity in the working copy, so this mutate could never be written.";
        }

        return row.Mode == MutateMode.Literal && !MutateLiteral.TryValue(row, field, out _, out var refusal) ? refusal : null;
    }

    private JsonObject Action() => Kind switch
    {
        Reject => new JsonObject { [Reject] = RejectMessage },
        Mutate => new JsonObject { [Mutate] = Patch() },
        Webhook => WebhookAction(),
        _ => new JsonObject { ["type"] = Email, ["template"] = Template, ["to"] = To },
    };

    private JsonObject WebhookAction()
    {
        var action = new JsonObject { ["type"] = Webhook, ["endpoint"] = Endpoint };
        if (!Blank(Payload))
        {
            action["payload"] = Payload;
        }

        return action;
    }

    private JsonObject Patch()
    {
        var patch = new JsonObject();
        foreach (var row in MutateRows.Where(row => !Blank(row.Field)))
        {
            patch[row.Field.Trim()] = ValueOf(row);
        }

        return patch;
    }

    private JsonNode? ValueOf(MutateRow row)
    {
        if (row.Mode == MutateMode.Expression)
        {
            return new JsonObject { ["$cel"] = row.Text };
        }

        return Fields.TryGetValue(row.Field.Trim(), out var field) && MutateLiteral.TryValue(row, field, out var value, out _)
            ? value
            : JsonValue.Create(row.Text);
    }

    /// <summary>The same form with a stand-in for every blank the schema requires, for <see cref="Draft"/>.</summary>
    private HookBuilder Standing()
    {
        var stand = new HookBuilder
        {
            Kind = Kind,
            RejectMessage = Stand(RejectMessage),
            Endpoint = Stand(Endpoint),
            Payload = Payload,
            Template = Stand(Template),
            To = Stand(To),
            Fields = Fields,
        };
        stand.MutateRows.AddRange(MutateRows.Where(row => !Blank(row.Field)).Select(row =>
            new MutateRow(row.Field, row.Mode, row.Mode == MutateMode.Expression ? Stand(row.Text) : row.Text) { Empty = row.Empty }));
        if (stand.MutateRows.Count == 0)
        {
            stand.MutateRows.Add(new MutateRow("x", MutateMode.Expression, "x"));
        }

        return stand;
    }

    private static string Stand(string value) => Blank(value) ? "x" : value;

    private static bool Blank(string value) => string.IsNullOrWhiteSpace(value);
}
```

`HookBuilder.Load.cs`:

```csharp
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* Opening a declared hook: the editor loads what it can draw, and nothing else (spec §5.2). */
internal sealed partial class HookBuilder
{
    /// <summary>A builder holding a declared hook, its point fixed — or <see langword="null"/> when the editor cannot draw it.</summary>
    /// <param name="point">The point it is declared at.</param>
    /// <param name="hook">The declared hook; not modified.</param>
    /// <param name="fields">The entity's fields as the working copy declares them.</param>
    public static HookBuilder? From(string point, JsonObject hook, IReadOnlyDictionary<string, FieldSchema> fields)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (HookShape.Undrawable(hook, point) is not null || hook["action"] is not JsonObject action)
        {
            return null;
        }

        var builder = new HookBuilder { Fields = fields, Condition = (string?)hook["condition"] ?? string.Empty };
        builder.Choose(point);
        builder.PointLocked = true;
        builder.Load(action);
        return builder;
    }

    private void Load(JsonObject action)
    {
        if (action.ContainsKey(Reject))
        {
            (Kind, RejectMessage) = (Reject, (string)action[Reject]!);
        }
        else if (action[Mutate] is JsonObject patch)
        {
            Kind = Mutate;
            LoadRows(patch);
        }
        else if ((string?)action["type"] == Webhook)
        {
            (Kind, Endpoint, Payload) = (Webhook, (string)action["endpoint"]!, (string?)action["payload"] ?? string.Empty);
        }
        else
        {
            (Kind, Template, To) = (Email, (string)action["template"]!, (string)action["to"]!);
        }
    }

    private void LoadRows(JsonObject patch)
    {
        MutateRows.Clear();
        foreach (var (field, value) in patch)
        {
            MutateRows.Add(value is JsonObject tagged
                ? new MutateRow(field, MutateMode.Expression, (string)tagged["$cel"]!)
                : MutateLiteral.Row(field, value));
        }
    }
}
```

- [ ] **Step 5: Keep `HooksTab` compiling, behaving as today** (Task 9 replaces this with rows):
  - `HooksTab.razor`, in `ActionFields`' mutate case: replace `Value="_hook.MutateField"` with `Value="FirstRow.Field"` and `Value="_hook.MutateValue"` with `Value="FirstRow.Text"`.
  - `HooksTab.razor.cs`: replace the bodies of `TypeMutateValue` and `TypeMutateField`:

```csharp
    private void TypeMutateValue(string? text)
    {
        FirstRow.Text = text ?? string.Empty;
        FirstRow.Mode = MutateMode.Expression;
        _ = CheckMutateValueAsync();
    }

    /// <summary>The patched field names the slot the value is checked in, so it is asked again.</summary>
    private void TypeMutateField(string? text)
    {
        FirstRow.Field = text ?? string.Empty;
        _ = CheckMutateValueAsync();
    }

    /// <summary>The one field-and-value pair the form draws until it draws rows (plan Task 9 replaces it).</summary>
    private MutateRow FirstRow
    {
        get
        {
            if (_hook.MutateRows.Count == 0)
            {
                _hook.MutateRows.Add(new MutateRow { Mode = MutateMode.Expression });
            }

            return _hook.MutateRows[0];
        }
    }
```

  - In `CheckMutateValueAsync` replace `_hook.MutateValue` with `FirstRow.Text` and `_hook.MutateField.Trim()` with `FirstRow.Field.Trim()`.
  - Replace the whole `Dirty` expression with `private bool Dirty => _hook.HasInput;` (keep its remarks).
  - In `AddAsync`, first line: `_hook.Fields = Copy is { } copy ? HookFields.Declared(copy.Json, Entity) : _hook.Fields;`

- [ ] **Step 6: Run, normalise, run again**

Run: `dotnet build` (0 warnings), then `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*HookBuilderEditTests' --filter-class '*HookBuilderTests' --filter-class '*ExpressionSlotsTests' --filter-class '*HookPatchTests'`.
Expected: PASS. Then `scripts/test-ring0`, and `scripts/test-admin-e2e --filter-class '*HookEditingScenarios'` and `--filter-class '*ExpressionCheckScenarios'` — both PASS unchanged (the tab behaves as before).

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/HookBuilder.cs src/MMLib.Alvo.Admin/Components/Schema/HookBuilder.Load.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs test/MMLib.Alvo.Admin.Tests/Schema/HookBuilderEditTests.cs test/MMLib.Alvo.Admin.Tests/Schema/HookBuilderTests.cs test/MMLib.Alvo.Admin.Tests/Schema/ExpressionSlotsTests.cs
git commit -m "feat(admin): the hook builder opens a declared hook, with several mutate rows and a payload

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 7: `ExpressionSlots.ForHook`, and the core pins string-slot checks

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/ExpressionSlots.cs` (add `ForHook` + `Place`)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/ExpressionSlotsHookTests.cs`
- Modify (test only): `test/MMLib.Alvo.Tests/Management/ExpressionSlotCheckTests.cs` — two facts and a fixture builder

**Interfaces:**
- Consumes: `ExpressionSlots.Clone`, `Entity`, `Pointer` (private/`public` in the same file); `ExpressionSlotCheck.Check` (core, test only).
- Produces: `public static (string Json, string Path)? ExpressionSlots.ForHook(string workingJson, string entity, string point, int? position, JsonObject hook, params string[] slot)`.

- [ ] **Step 1: Write the failing Admin tests**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Where a whole hook is placed for a check: appended when new, at its own position when edited (spec §4.2, D4).</summary>
public class ExpressionSlotsHookTests
{
    private const string Working = """{"entities":{"orders":{"fields":{"total":{"type":"decimal"}},"hooks":{"beforeUpdate":[{"action":{"reject":"first"}},{"action":{"reject":"second"}}]}},"bare":{"fields":{}},"a/b":{"fields":{}}}}""";

    [Fact]
    public void A_new_hook_is_appended_and_the_slot_names_its_position()
    {
        var (json, path) = ExpressionSlots.ForHook(Working, "orders", "beforeUpdate", null, Hook("third"), "action", "reject")!.Value;

        path.ShouldBe("/entities/orders/hooks/beforeUpdate/2/action/reject");
        List(json, "orders", "beforeUpdate").Count.ShouldBe(3);
    }

    [Fact]
    public void An_edited_hook_is_placed_where_it_sits()
    {
        var (json, path) = ExpressionSlots.ForHook(Working, "orders", "beforeUpdate", 0, Hook("edited"), "action", "reject")!.Value;

        path.ShouldBe("/entities/orders/hooks/beforeUpdate/0/action/reject");
        var list = List(json, "orders", "beforeUpdate");
        list.Count.ShouldBe(2);
        list[0]!["action"]!["reject"]!.GetValue<string>().ShouldBe("edited");
        list[1]!["action"]!["reject"]!.GetValue<string>().ShouldBe("second");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void A_position_nothing_is_at_has_nothing_to_check(int position)
        => ExpressionSlots.ForHook(Working, "orders", "beforeUpdate", position, Hook("x"), "action", "reject").ShouldBeNull();

    [Theory]
    [InlineData("missing")]
    public void An_entity_the_copy_does_not_declare_has_nothing_to_check(string entity)
        => ExpressionSlots.ForHook(Working, entity, "beforeUpdate", null, Hook("x"), "action", "reject").ShouldBeNull();

    [Fact]
    public void Text_that_is_not_a_descriptor_has_nothing_to_check()
        => ExpressionSlots.ForHook("not json", "orders", "beforeUpdate", null, Hook("x"), "condition").ShouldBeNull();

    [Fact]
    public void A_point_the_entity_has_no_list_for_is_created_on_the_clone_only()
    {
        var (json, path) = ExpressionSlots.ForHook(Working, "bare", "afterCreate", null, Hook("x"), "action", "reject")!.Value;

        path.ShouldBe("/entities/bare/hooks/afterCreate/0/action/reject");
        List(json, "bare", "afterCreate").Count.ShouldBe(1);
        JsonNode.Parse(Working)!["entities"]!["bare"]!["hooks"].ShouldBeNull();
    }

    [Fact]
    public void A_name_with_a_slash_is_escaped_in_the_pointer()
        => ExpressionSlots.ForHook(Working, "a/b", "beforeCreate", null, Hook("x"), "action", "mutate", "c~d")!.Value.Path
            .ShouldBe("/entities/a~1b/hooks/beforeCreate/0/action/mutate/c~0d");

    [Fact]
    public void The_hook_argument_is_not_attached_to_the_clone()
    {
        var hook = Hook("x");

        ExpressionSlots.ForHook(Working, "orders", "beforeUpdate", null, hook, "action", "reject");

        hook.Parent.ShouldBeNull();
    }

    private static JsonObject Hook(string message) => new() { ["action"] = new JsonObject { ["reject"] = message } };

    private static JsonArray List(string json, string entity, string point)
        => (JsonArray)JsonNode.Parse(json)!["entities"]![entity]!["hooks"]![point]!;
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ExpressionSlotsHookTests'`. Expected: build FAIL — `ForHook` is not defined.

- [ ] **Step 3: Implement** — in `ExpressionSlots.cs`, after `ForComputed`:

```csharp
    /// <summary>Places a whole hook — the one the editor would write, with the typed text already in its slot — on a clone.</summary>
    /// <remarks>
    /// The position-aware successor of <see cref="ForHookCondition"/> and <see cref="ForMutateValue"/> (kept for the add
    /// path's existing callers and tests): an edited hook is checked where it sits, a new one is appended. The hook is
    /// <c>HookPatch</c>'s output, so what is checked is what Add or Save would stage; spec D4 decides whether it carries the
    /// condition.
    /// </remarks>
    /// <param name="workingJson">The working copy's text; not modified.</param>
    /// <param name="entity">The entity the hook is on.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="position">The edited hook's position, or <see langword="null"/> to append.</param>
    /// <param name="hook">The hook; not modified.</param>
    /// <param name="slot">The slot inside the hook, outermost first: <c>condition</c>, or <c>action</c>, <c>payload</c>.</param>
    /// <returns>The clone's text and the slot's pointer, or <see langword="null"/> when there is no such entity or position.</returns>
    public static (string Json, string Path)? ForHook(
        string workingJson, string entity, string point, int? position, JsonObject hook, params string[] slot)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (Clone(workingJson) is not { } root || Entity(root, entity) is not { } declared)
        {
            return null;
        }

        var hooks = declared["hooks"] as JsonObject ?? (JsonObject)(declared["hooks"] = new JsonObject());
        var list = hooks[point] as JsonArray ?? (JsonArray)(hooks[point] = new JsonArray());
        var at = Place(list, position, (JsonObject)hook.DeepClone());
        return at < 0
            ? null
            : (root.ToJsonString(), Pointer([.. new[] { "entities", entity, "hooks", point, at.ToString(CultureInfo.InvariantCulture) }, .. slot]));
    }

    /// <summary>Appends the hook, or puts it at its position; the index it landed at, or -1 when nothing is there.</summary>
    private static int Place(JsonArray list, int? position, JsonObject hook)
    {
        if (position is not { } at)
        {
            list.Add(hook);
            return list.Count - 1;
        }

        if (at < 0 || at >= list.Count)
        {
            return -1;
        }

        list[at] = hook;
        return at;
    }
```

- [ ] **Step 4: Run** the Admin tests — PASS; normalise; `scripts/test-ring0`.

- [ ] **Step 5: Pin the core behaviour the dashboard now relies on (spec B4 deviation).** In `test/MMLib.Alvo.Tests/Management/ExpressionSlotCheckTests.cs`, add next to `A_mutate_value_is_spliced_in_the_dollar_cel_form_the_descriptor_documents` (its helpers `Descriptor()`, `Validator()`, `IsError` already exist at `:417-462`):

```csharp
    [Fact]
    public void A_webhook_payload_slot_is_spliced_as_text_and_judged_as_apply_judges_it()
    {
        var descriptor = DescriptorWithAfterHooks().ToJsonString();
        const string pointer = "/entities/orders/hooks/afterCreate/0/action/payload";

        ExpressionSlotCheck.Check(Validator(), descriptor, pointer, "[{{new.total}}]").Where(IsError).ShouldBeEmpty();
        ExpressionSlotCheck.Check(Validator(), descriptor, pointer, "{\"id\": \"{{new.id}}\"}").Where(IsError)
            .ShouldNotBeEmpty("a brace outside a placeholder is raw JSONata to this build (JsonataSlot), refused at apply");
    }

    [Fact]
    public void An_email_to_slot_is_spliced_as_text_and_more_than_one_recipient_is_refused()
    {
        var descriptor = DescriptorWithAfterHooks().ToJsonString();
        const string pointer = "/entities/orders/hooks/afterCreate/1/action/to";

        ExpressionSlotCheck.Check(Validator(), descriptor, pointer, "ops@example.com").Where(IsError).ShouldBeEmpty();
        ExpressionSlotCheck.Check(Validator(), descriptor, pointer, "ops@example.com, sales@example.com").Where(IsError).ShouldNotBeEmpty();
    }

    private static JsonObject DescriptorWithAfterHooks()
    {
        var descriptor = Descriptor();
        descriptor["webhooks"] = JsonNode.Parse("""{"endpoints":{"desk":{"url":"https://example.com/hook","secretRef":"desk-key"}}}""");
        descriptor["templates"] = JsonNode.Parse("""{"done":{"subject":"Done","body":"Order done."}}""");
        descriptor["entities"]!["orders"]!["hooks"] = JsonNode.Parse(
            """
            {"afterCreate":[
              {"action":{"type":"webhook","endpoint":"desk","payload":"[1]"}},
              {"action":{"type":"email","template":"done","to":"ops@example.com"}}]}
            """);
        return descriptor;
    }
```

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExpressionSlotCheckTests'`. Expected: PASS (no product change — the splice already treats a non-mutate slot as a bare string, `ExpressionSlotCheck.cs:235-252`). If either fact fails, **stop and report**: the B4 deviation's premise is false and the payload box must fall back to the build's `JSONata` refusal sentence from `capabilities.refused` without a live check.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/ExpressionSlots.cs test/MMLib.Alvo.Admin.Tests/Schema/ExpressionSlotsHookTests.cs test/MMLib.Alvo.Tests/Management/ExpressionSlotCheckTests.cs
git commit -m "feat(admin): place an edited hook where it sits for the live check; pin that cel/check judges string slots

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

Then `scripts/test-ring1`.

---

### Task 8: edit in place on the On write tab

**Files:**
- Modify (full replacement): `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor`, `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Edit.cs`
- Modify: `src/MMLib.Alvo.Admin/wwwroot/alvo.css:1765` (rename `.a-hookrow__remove` → `.a-hookrow__actions`)
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs` (add `TypeConditionAsync`, the one way every new scenario types a condition — Task 19 teaches it the text switch)
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/HookEditInPlaceScenarios.cs`

**Interfaces:**
- Consumes: `HookBuilder` (Task 6), `HookShape` (Task 3), `HookPatch` (Task 5), `HookFields.Declared` (Task 4), `ExpressionSlots.ForHook` (Task 7), `WorkingCopy.ReplaceHook` (Task 3), `StagedWords.Saved`, `AdminSnackbar.Confirm`, `RefusalPanel.Of`, `ConditionTable.MaxConditionLength` (Task 1).
- Produces (internal to the component; later tasks edit these by name):
  - property `Current` (the builder on screen), fields `_new`, `_editing`, `_adding`;
  - record `Editing(string Point, int Position, string Json, JsonObject Original, HookBuilder Builder, string Opened)` in `HooksTab.Edit.cs`;
  - methods `OpenNew()`, `OpenEdit(string point, int position, string json)`, `SubmitAsync()`, `SaveEdit(Editing)`, `CloseEditor()`, `CheckAll()`, `CurrentPosition(WorkingCopy)`, `PositionIn(...)`, `Reveal(string point, int position)`, `DeclaredFields()`;
  - RenderFragment properties in `HooksTab.razor` `@code`: `ConditionField`, `RejectFields`, `MutateFields`, `WebhookFields`, `EmailFields` (Tasks 9, 10, 19 replace them whole);
  - test ids: `hook-edit`, `hook-save`, `hook-readonly`, `hook-point-fixed`, `hook-condition-length`.

**What is new in the two full-file replacements** (everything else is today's text, carried over so later tasks can replace
named fragments; a reviewer diffs against `HEAD` and expects only this):
- Markup: the row's Edit button (`hook-edit`) beside Remove inside `.a-hookrow__actions`, and the read-only note
  (`hook-readonly`) for an undrawable hook; the sheet opens for `_adding || _editing`, its title/submit/subtitle switch on
  `_editing`; the fixed point (`hook-point-fixed`) replaces the point chips in Edit; the action boxes moved, unchanged, into
  `RejectFields` / `MutateFields` / `WebhookFields` / `EmailFields`; the condition box moved into `ConditionField` and
  gained the 2,000-character sentence (`ConditionLength`); `@inject ISnackbar Snackbar`.
- Code: `Current` (edited builder or `_new`) replaces `_hook`; `OpenNew`, `SubmitAsync`, `CloseEditor` (was
  `CloseAdding`), `Reveal`, `CountAt`, `Undrawable`, `DeclaredFields`; the checks place the whole hook with
  `ExpressionSlots.ForHook` at `CurrentPosition`; `PositionOf` became `PositionIn(hooks, point, json)`; `Dirty` compares
  fingerprints in Edit. `HooksTab.Edit.cs` is new: `OpenEdit`, `SaveEdit`, `CurrentPosition`, `ConditionDescribedBy`,
  the `Editing` record.

- [ ] **Step 1: Write the failing e2e scenarios** (`HookEditInPlaceScenarios.cs`):

```csharp
using Microsoft.Playwright;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A hook is edited where it sits (spec §4.1, §4.2, §5.1; ruling B1): it keeps its place in its point's ordered list, it
/// is lit, the sheet is titled by the hook, and an edit that another tab overtook is refused in place rather than written.
/// </summary>
/// <remarks>
/// One shared working copy for the class, so each scenario edits a different hook of the bike-workshop descriptor:
/// service_orders beforeUpdate (two hooks), order_lines beforeCreate (opened, never saved), rentals afterCreate, and
/// customers beforeDelete (made by the scenario itself).
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class HookEditInPlaceScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Editing_a_hook_keeps_its_place_lights_it_and_says_saved()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "service_orders");
        var editor = await OpenEditAsync(session, "beforeUpdate", 0);

        await session.Page.FillAsync("#hook-reject", "Collected <b>orders</b> stay closed.");
        await editor.GetByTestId("hook-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Hook beforeUpdate saved to the working copy");
        var row = session.Page.Locator("#hook-beforeUpdate-0");
        (await row.InnerTextAsync()).ShouldContain("Collected <b>orders</b> stay closed.");
        (await row.Locator("b").CountAsync()).ShouldBe(0, "a message is text, never markup");
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true");
        (await row.GetByTestId("hook-staged").CountAsync()).ShouldBe(1, "an edited hook badges as new (ruling B2)");
        (await session.Page.Locator("#hook-beforeUpdate-1").InnerTextAsync()).ShouldContain("completed_at", Case.Sensitive, "the next hook did not move");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_edit_sheet_is_titled_by_the_hook_and_keeps_its_point()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "order_lines");
        var editor = await OpenEditAsync(session, "beforeCreate", 0);

        (await editor.InnerTextAsync()).ShouldContain("Edit beforeCreate hook 1");
        (await editor.GetByTestId("hook-save").InnerTextAsync()).ShouldBe("Save to the working copy");
        (await editor.GetByTestId("hook-point-fixed").InnerTextAsync()).ShouldBe("beforeCreate");
        (await editor.GetByTestId("hook-points").CountAsync()).ShouldBe(0, "an opened hook keeps its point (spec D1)");
        (await session.Page.InputValueAsync("#hook-condition")).ShouldBe("new.quantity <= 0");

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Escape_on_a_changed_hook_asks_before_it_discards()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "order_lines");
        var editor = await OpenEditAsync(session, "beforeCreate", 0);
        var declared = await session.Page.Locator("#hook-beforeCreate-0").InnerTextAsync();

        await session.Page.FillAsync("#hook-reject", "A changed message.");
        await session.Page.Keyboard.PressAsync("Escape");
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        await editor.GetByTestId("editor-keep").ClickAsync();
        (await session.FocusIsInsideAsync("hook-editor")).ShouldBeTrue();

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.GetByTestId("editor-discard").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.Locator("#hook-beforeCreate-0").InnerTextAsync()).ShouldBe(declared);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_another_tab_removed_is_not_overwritten()
    {
        await using var editing = await world.SignInAsync(TestContext.Current.CancellationToken);
        await using var other = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(editing, "rentals");
        var editor = await OpenEditAsync(editing, "afterCreate", 0);
        await editing.TypeConditionAsync("new.status == 'active'");

        await OnWriteAsync(other, "rentals");
        await other.Page.Locator("#hook-afterCreate-0 [data-testid='hook-remove']").ClickAsync();
        await other.Dialog("remove-hook").GetByTestId("remove-hook-run").ClickAsync();

        await editor.GetByTestId("hook-save").ClickAsync();
        var refusal = editor.GetByTestId("error-panel");
        await refusal.WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("changed in the working copy after you opened it");
        await editing.WaitForFocusInsideAsync("error-panel");
        (await editor.IsVisibleAsync()).ShouldBeTrue("the sheet stays open with what was typed");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_moved_by_another_tab_is_still_the_one_saved()
    {
        await using var editing = await world.SignInAsync(TestContext.Current.CancellationToken);
        await using var other = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(editing, "customers");
        await AddRejectAsync(editing, "beforeDelete", "First.");
        await AddRejectAsync(editing, "beforeDelete", "Second.");
        var editor = await OpenEditAsync(editing, "beforeDelete", 1);
        await editing.Page.FillAsync("#hook-reject", "Second, edited.");

        await OnWriteAsync(other, "customers");
        await other.Page.Locator("#hook-beforeDelete-0 [data-testid='hook-remove']").ClickAsync();
        await other.Dialog("remove-hook").GetByTestId("remove-hook-run").ClickAsync();

        await editor.GetByTestId("hook-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        var row = editing.Page.Locator("#hook-beforeDelete-0");
        (await row.InnerTextAsync()).ShouldContain("Second, edited.");
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true");
        (await editing.Page.Locator("#hook-beforeDelete-1").CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_condition_over_two_thousand_characters_is_said_under_the_box()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "bikes");
        await session.Page.GetByTestId("hook-new").ClickAsync();

        await session.TypeConditionAsync("new.brand == '" + new string('x', 1990) + "'");
        var sentence = session.Page.GetByTestId("hook-condition-length");
        await sentence.WaitForAsync();
        (await sentence.InnerTextAsync()).ShouldContain("2000");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_Add_adds_one_hook()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await OnWriteAsync(session, "parts");
        var before = await session.Page.GetByTestId("hook-row").CountAsync();
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.FillAsync("#hook-reject", "Parts are added by purchasing.");

        await session.Dialog("hook-editor").GetByTestId("hook-add").DblClickAsync();

        await session.Page.GetByTestId("hook-row").Nth(before).WaitForAsync();
        (await session.Page.GetByTestId("hook-row").CountAsync()).ShouldBe(before + 1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_edit_sheet_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await OnWriteAsync(session, "order_lines");
        await OpenEditAsync(session, "beforeCreate", 0);

        await session.AssertNoHorizontalScrollAsync();
    }

    internal static async Task OnWriteAsync(AdminSession session, string entity)
    {
        await session.GoAsync($"/schema/{entity}");
        await session.OpenTabAsync("On write");
    }

    internal static async Task<ILocator> OpenEditAsync(AdminSession session, string point, int position)
    {
        await session.Page.Locator($"#hook-{point}-{position} [data-testid='hook-edit']").ClickAsync();
        var editor = session.Dialog("hook-editor");
        await editor.WaitForAsync();
        return editor;
    }

    private static async Task AddRejectAsync(AdminSession session, string point, string message)
    {
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = point, Exact = true }).ClickAsync();
        await session.Page.FillAsync("#hook-reject", message);
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }
}

/// <summary>
/// A hook the editor cannot draw is read-only, and an edit beside it keeps it (spec §5.2) — its own world, because the
/// descriptor carrying it arrives by import, which replaces the working copy every other scenario of a world shares.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class HookShapeScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_the_editor_cannot_draw_is_read_only_and_survives_an_edit_beside_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var descriptor = JsonNode.Parse(Descriptors.BikeWorkshop)!.AsObject();
        descriptor["entities"]!["rentals"]!["hooks"]!["afterUpdate"] = JsonNode.Parse(
            """[ { "action": { "type": "entity.update", "entity": "rental_fleet", "payload": {} } } ]""");
        await session.GoAsync("/transfer");
        await session.Page.FillAsync("#import-json", descriptor.ToJsonString());
        await session.Page.Locator("#import-json").PressAsync("Meta+Enter");
        await session.Page.WaitForURLAsync("**/changes");

        await HookEditInPlaceScenarios.OnWriteAsync(session, "rentals");
        var readOnly = session.Page.GetByTestId("hook-readonly");
        await readOnly.WaitForAsync();
        (await readOnly.InnerTextAsync()).ShouldContain("'entity.update' is refused");
        (await session.Page.Locator("#hook-afterUpdate-0 [data-testid='hook-edit']").CountAsync()).ShouldBe(0);

        var editor = await HookEditInPlaceScenarios.OpenEditAsync(session, "afterCreate", 0);
        await session.TypeConditionAsync("new.status == 'active'");
        await editor.GetByTestId("hook-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-afterUpdate-0").InnerTextAsync()).ShouldContain("entity.update");
        await readOnly.WaitForAsync();
    }
}
```

- [ ] **Step 2: Add the condition helper** to `AdminSession.cs` (after `ChooseAsync`, `:99-103`):

```csharp
    /// <summary>Types a hook condition as CEL into the open hook sheet.</summary>
    /// <remarks>One place for every scenario: the guided form (plan Task 19) puts a mode switch in front of the box.</remarks>
    /// <param name="condition">The CEL.</param>
    public Task TypeConditionAsync(string condition) => Page.FillAsync("#hook-condition", condition);
```

- [ ] **Step 3: Run to verify failure** — `scripts/test-admin-e2e --filter-class '*HookEditInPlaceScenarios'`. Expected: FAIL — no `hook-edit` button.

- [ ] **Step 4: Replace `HooksTab.razor` whole**

```razor
@*
    What runs on a write.

    A before-hook and an after-hook are not the same editor and must not look like one: the frozen
    schema's `$defs/beforeHookList` admits `reject` and `mutate` only — "no network, no external
    calls" — because a before-hook runs inside the write's own transaction. An after-hook is where
    a webhook or an email belongs. The point's name is therefore shown with what it may contain,
    rather than the two being flattened into one list of "actions".

    All six points are honoured by this build — PR5a landed the three `after*`, PR5b the three
    `before*` — which is what unblocked editing them at all. What is still refused is three of the
    five *action types*, and those are read off `capabilities` rather than listed here, so the day
    one of them lands the tab stops warning about it without anybody editing this file.

    One sheet adds and edits (spec §4.2): an edit keeps the hook's place in its point's ordered list,
    and a hook the editor cannot draw whole has no Edit at all — it is kept as it is (§5.2).
*@
@inject AdminInterop Interop
@inject ManagementGateway Gateway
@inject ILogger<HooksTab> Logger
@inject ISnackbar Snackbar
@implements IDisposable

@* A create inside a tab sits in its section's head, right-aligned (spec §3.7). *@
<SectionHead Title="On write">
    <Subtitle>
        Before-hooks run inside the transaction and can only refuse or change the row.
        After-hooks run once it is committed and are where anything that leaves the process belongs.
    </Subtitle>
    <Actions>
        @if (Editable)
        {
            <AlvoButton Small="true" data-testid="hook-new" OnClick="OpenNew">New hook</AlvoButton>
        }
    </Actions>
</SectionHead>

@*
    Folded by default. Three refusals at full length were the first screen of this tab on a phone — the
    operator scrolled past a wall to reach what the entity actually declares. Shut, it is one line that
    still says the count and the names; the wording inside is the framework's own, unshortened.
*@
@if (HookRefusals.Count > 0)
{
    <details class="a-refused a-refused--fold" data-testid="hooks-refused">
        <summary>
            @HookRefusals.Count thing@(HookRefusals.Count == 1 ? "" : "s") this build refuses on a hook —
            <span class="a-mono">@string.Join(", ", HookRefusals.Select(refusal => refusal.Slot))</span>
        </summary>
        @foreach (var refusal in HookRefusals)
        {
            <span class="a-refused__reason">@refusal.Consequence</span>
        }
    </details>
}

@if (Hooks.Count == 0)
{
    <EmptyState Title="Nothing runs on a write to this entity"
                Body="No hooks are declared. Writes are validated against the field facets and the rules, and nothing else happens." />
}
else
{
    @foreach (var point in Hooks)
    {
        <ListRow class="a-listrow--roomy a-listrow--stacked">
            <div class="a-row">
                <span class="@($"a-badge {(HookBuilder.IsBefore(point.Key) ? "a-badge--warn" : "a-badge--accent")}")">@point.Key</span>
                <span class="a-section__sub">
                    @(HookBuilder.IsBefore(point.Key)
                        ? "in the transaction · may refuse or change the row · no network"
                        : "after the commit · may leave the process")
                </span>
            </div>

            @{
                var declared = Declared(point.Value);
                var name = point.Key;
            }

            @for (var position = 0; position < declared.Count; position++)
            {
                var hook = declared[position];
                var at = position;
                var undrawable = Undrawable(name, hook);

                @*
                    The controls sit under the declaration on a phone and beside it on a desktop. Beside it
                    at 375 px, the code block gave up a third of the row to one word and every condition
                    wrapped mid-token.
                *@
                <div class="a-hookrow" data-testid="hook-row" id="@RowId(name, at)"
                     data-alvo-new="@(RowId(name, at) == _reveal ? "true" : null)">
                    @if (Staged.IsNewHook(name, at))
                    {
                        <span class="a-badge a-badge--accent a-hookrow__badge"
                              data-testid="hook-staged">new</span>
                    }
                    <CodeBlock Json="@hook" />
                    @if (Editable)
                    {
                        <div class="a-row a-hookrow__actions">
                            @if (undrawable is null)
                            {
                                <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="hook-edit"
                                            aria-label="@($"Edit hook {at + 1} of {name}")"
                                            OnClick="_ => OpenEdit(name, at, hook)">Edit</AlvoButton>
                            }
                            <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true"
                                        data-testid="hook-remove" aria-label="@($"Remove hook {at + 1} of {name}")"
                                        OnClick="_ => _removing = new PendingRemoval(name, hook)">Remove</AlvoButton>
                        </div>
                    }
                </div>
                @if (undrawable is not null)
                {
                    <p class="a-note" data-testid="hook-readonly">
                        @undrawable It is kept as it is: remove it here, or change it in the descriptor.
                    </p>
                }
            }
        </ListRow>
    }
}

@if (_reveal is { } revealed)
{
    @RevealOnRender.On($"#{revealed}", _reveals)
}

@if (_adding || _editing is not null)
{
    <AlvoEditor TestId="hook-editor"
                Title="@(_editing is { } editing ? $"Edit {editing.Point} hook {editing.Position + 1}" : "New hook")"
                SubmitText="@(_editing is null ? "Add to the working copy" : "Save to the working copy")"
                SubmitTestId="@(_editing is null ? "hook-add" : "hook-save")"
                Subtitle="@(_editing is { } opened
                    ? $"It keeps its place: the hooks at one point run in order, and this is number {opened.Position + 1} of {CountAt(opened.Point)}."
                    : "The point decides what the action may be. A before-hook runs in the transaction and may only refuse or patch the row; a beforeDelete may only refuse, because the row is going.")"
                Dirty="Dirty" OnSubmit="SubmitAsync" OnClose="CloseEditor">
        @RefusalPanel.Of(_refusal, _editing is null ? "That hook cannot be added" : "That hook could not be saved")

        @if (_editing is null)
        {
            <Field Label="When" LabelId="hook-points-label">
                <ChipGroup TValue="string" Items="HookBuilder.Points" Selected="[Current.Point]" aria-labelledby="hook-points-label"
                           data-testid="hook-points" SelectedChanged="points => Choose(points[0])" />
            </Field>
        }
        else
        {
            <Field Label="When">
                <ChildContent>
                    <span class="a-mono" data-testid="hook-point-fixed">@Current.Point</span>
                </ChildContent>
                <Hint>To move it to another point, remove it and add a new one.</Hint>
            </Field>
        }

        @ConditionField

        <Field Label="Then" LabelId="hook-actions-label">
            <ChipGroup TValue="string" Items="Current.Kinds" Selected="[Current.Kind]" aria-labelledby="hook-actions-label"
                       data-testid="hook-actions" SelectedChanged="kinds => ChooseKind(kinds[0])" />
        </Field>

        @ActionFields
    </AlvoEditor>
}

@* Names the hook by its point and shows what it does, which is all a hook is: its position is not a name. *@
<AlvoConfirm Open="_removing is not null" TestId="remove-hook" ConfirmTestId="remove-hook-run"
             CancelTestId="remove-hook-cancel" Title="@($"Remove this {_removing?.Point} hook?")"
             Consequence="It leaves the working copy now; writes stop running it when you apply."
             Verb="Remove hook" OnConfirm="RemoveAsync" OnCancel="CancelRemoval">
    @if (_removing is { } removing)
    {
        <CodeBlock Json="@removing.Json" />
    }
</AlvoConfirm>

@code {
    /// <summary>What apply would say about the expression in a box, under it, as its sentences.</summary>
    private RenderFragment Findings(string id) => @<text>
        @foreach (var (finding, index) in _check.Findings(id).Select((finding, index) => (finding, index)))
        {
            <p class="@ExpressionCheck.ProblemClass(finding)" id="@(index == 0 ? $"{id}-check" : $"{id}-check-{index}")" role="status"
               data-testid="@($"check-{id}")" data-severity="@finding.Severity">
                @finding.Message
                @if (finding.FixSuggestion is { } fix)
                {
                    <span class="a-field__fix">@fix</span>
                }
            </p>
        }
    </text>;

    /// <summary>The box is described by its hint, and by the sentence under it while it has one.</summary>
    private string Described(string id) => _check.DescribedBy(id) is { } check ? $"{id}-hint {check}" : $"{id}-hint";

    /// <summary>The condition box (plan Task 19 adds the guided rows and the switch).</summary>
    private RenderFragment ConditionField => @<text>
        <Field Label="Only when (optional)" For="hook-condition">
            <ChildContent>
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-condition" Class="a-mono"
                              autocomplete="off" Placeholder="@Current.Example" aria-describedby="@ConditionDescribedBy"
                              Value="Current.Condition" ValueChanged="TypeCondition" />
                @ConditionLength
                @Findings("hook-condition")
            </ChildContent>
            <Hint>
                <span data-testid="hook-condition-hint">
                    CEL over @((MarkupString)Current.Images) and <code class="a-mono">@@user</code>. Left empty, the hook
                    runs on every write.
                </span>
            </Hint>
        </Field>
    </text>;

    /// <summary>Said under the condition when it is longer than a condition may be, without asking the server (spec §4.6).</summary>
    private RenderFragment ConditionLength => @<text>
        @if (Current.Condition.Length > ConditionTable.MaxConditionLength)
        {
            <p class="a-field__problem" id="hook-condition-length" data-testid="hook-condition-length">
                This condition is @Current.Condition.Length characters; a condition holds at most @ConditionTable.MaxConditionLength.
            </p>
        }
    </text>;

    /// <summary>The boxes the chosen action takes.</summary>
    private RenderFragment ActionFields => @<text>
        @switch (Current.Kind)
        {
            case HookBuilder.Reject:
                @RejectFields
                break;
            case HookBuilder.Mutate:
                @MutateFields
                break;
            case HookBuilder.Webhook:
                @WebhookFields
                break;
            case HookBuilder.Email:
                @EmailFields
                break;
        }
    </text>;

    /// <summary>A reject's message.</summary>
    private RenderFragment RejectFields => @<text>
        <Field Label="Refuse with" For="hook-reject">
            <ChildContent>
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-reject" autocomplete="off"
                              aria-describedby="hook-reject-hint" MaxLength="500" Placeholder="A completed work order cannot be reopened."
                              Value="Current.RejectMessage" ValueChanged="text => Current.RejectMessage = text ?? string.Empty" />
            </ChildContent>
            <Hint>
                Becomes the <code class="a-mono">detail</code> of the RFC 7807 problem the caller
                reads, so write it for them rather than for the log.
            </Hint>
        </Field>
    </text>;

    /// <summary>A mutate's one field and value (plan Task 9 replaces this with rows).</summary>
    private RenderFragment MutateFields => @<text>
        <div class="a-field-row">
            <Field Label="Field" For="hook-mutate-field">
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-mutate-field"
                              Class="a-mono" autocomplete="off" Placeholder="completed_on"
                              Value="FirstRow.Field" ValueChanged="TypeMutateField" />
            </Field>
            <Field Label="Set to" For="hook-mutate-value">
                <ChildContent>
                    <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-mutate-value"
                                  aria-describedby="@Described("hook-mutate-value")" Class="a-mono" autocomplete="off" Placeholder="now()"
                                  Value="FirstRow.Text" ValueChanged="TypeMutateValue" />
                    @Findings("hook-mutate-value")
                </ChildContent>
                <Hint>
                    A CEL expression, stored as <code class="a-mono">{"$cel": "…"}</code>. Wrap it in
                    quotes for a literal string.
                </Hint>
            </Field>
        </div>
    </text>;

    /// <summary>A webhook's endpoint (plan Task 10 replaces this with the picker and the payload box).</summary>
    private RenderFragment WebhookFields => @<text>
        <Field Label="Endpoint" For="hook-endpoint">
            <ChildContent>
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-endpoint" Class="a-mono"
                              aria-describedby="hook-endpoint-hint" autocomplete="off" Placeholder="dispatch"
                              Value="Current.Endpoint" ValueChanged="text => Current.Endpoint = text ?? string.Empty" />
            </ChildContent>
            <Hint>
                A name declared under <code class="a-mono">webhooks.endpoints</code>, not a URL — the
                managed path is the one this build delivers on.
            </Hint>
        </Field>
    </text>;

    /// <summary>An email's template and recipient (plan Task 10 replaces this with the picker and the checked box).</summary>
    private RenderFragment EmailFields => @<text>
        <div class="a-field-row">
            <Field Label="Template" For="hook-template">
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-template"
                              Class="a-mono" autocomplete="off" Placeholder="work_order_completed"
                              Value="Current.Template" ValueChanged="text => Current.Template = text ?? string.Empty" />
            </Field>
            <Field Label="To" For="hook-to">
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-to"
                              Class="a-mono" autocomplete="off" Placeholder="{{new.contact_email}}"
                              Value="Current.To" ValueChanged="text => Current.To = text ?? string.Empty" />
            </Field>
        </div>
    </text>;
}
```

- [ ] **Step 5: Replace `HooksTab.razor.cs` whole**

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The entity's On write tab: the hooks it declares, and the one sheet that adds or edits one.</summary>
public partial class HooksTab
{
    /// <summary>
    /// How one hook is written for a person to read.
    /// </summary>
    /// <remarks>
    /// <b>Both options earn their place, and the encoder is the one that was missing.</b> A bare
    /// <c>ToJsonString()</c> takes the default encoder, which escapes anything that could be dangerous in
    /// HTML — so a CEL condition reached this tab as
    /// <c>old.status == &#92;u0027completed&#92;u0027 &#92;u0026&#92;u0026 …</c> and was rendered exactly like that.
    /// It is the defect <c>WorkingCopy</c> documents at length for the document as a whole, arriving one
    /// re-serialisation later. Safe because <c>CodeBlock</c> HTML-encodes before it highlights.
    /// Indented because one long line is what pushed this row wider than a phone. <c>WorkingCopy.ReplaceHook</c>
    /// compares against exactly this text (spec §5.1).
    /// </remarks>
    private static readonly JsonSerializerOptions _readable = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HookBuilder _new = new();
    private readonly RefusalState<string> _refusal = new();
    private bool _adding;
    private PendingRemoval? _removing;
    private string? _reveal;
    private int _reveals;

    /// <summary>The hooks, as the descriptor declares them: point to the raw JSON of its list.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<KeyValuePair<string, string>> Hooks { get; set; } = [];

    /// <summary>The entity the hooks are on, for the expression check.</summary>
    [Parameter]
    public string Entity { get; set; } = string.Empty;

    /// <summary>Whether this operator has a working copy to edit at all.</summary>
    [Parameter]
    public bool Editable { get; set; }

    /// <summary>
    /// What the working copy changed, cascaded by the entity screen — a hook only it declares is badged
    /// <c>new</c>, because it does not run yet and must not read as if it did.
    /// </summary>
    [CascadingParameter]
    private StagedView Staged { get; set; } = StagedView.None;

    /// <summary>
    /// What this build refuses that belongs on this tab.
    /// </summary>
    /// <remarks>
    /// <b>Read from <c>ManagementCapabilities.Refused</c>, not listed in the markup.</b> Three of the five
    /// action types are declared by the schema and never run — and the tab said nothing about any of them,
    /// so the absence of a control for <c>entity.update</c> read as an oversight rather than a refusal.
    /// <c>FieldEditor</c> already does exactly this for its own half; this is the entity half the audit
    /// found nobody consuming.
    /// </remarks>
    [Parameter]
    public IReadOnlyList<ManagementRefusedFeature> Refused { get; set; } = [];

    /// <summary>Raised with the hook to declare.</summary>
    [Parameter]
    public EventCallback<NewHook> OnAdd { get; set; }

    /// <summary>Raised with the hook to remove.</summary>
    [Parameter]
    public EventCallback<HookAt> OnRemove { get; set; }

    /// <summary>A hook this editor built, in the schema's own shape.</summary>
    /// <param name="Point">The hook point it belongs to.</param>
    /// <param name="Condition">The CEL guard, or empty for a hook that always runs.</param>
    /// <param name="Action">The action object.</param>
    public sealed record NewHook(string Point, string? Condition, JsonObject Action);

    /// <summary>One declared hook, by where it sits.</summary>
    /// <param name="Point">The hook point.</param>
    /// <param name="Position">Its position within that point's list.</param>
    public sealed record HookAt(string Point, int Position);

    /// <summary>The refusals that are about a hook, which are the only kind this tab can show.</summary>
    /// <remarks>
    /// The three action types this build never runs (<c>function</c>, <c>http.call</c>, <c>entity.update</c>), plus
    /// the two refused forms of an after-action's values (<c>JSONata</c>, <c>email.data</c>) — placed by
    /// <see cref="RefusalPlaces"/>, because a slot is the feature's own name and no prefix finds all five.
    /// </remarks>
    private IReadOnlyList<ManagementRefusedFeature> HookRefusals => RefusalPlaces.On(RefusalScreen.OnWrite, Refused);

    /// <summary>
    /// The working copy a typed expression is checked against, on a clone — and the one an edit is saved into (spec D3) —
    /// cascaded by the entity screen only, so its model stays internal.
    /// </summary>
    [CascadingParameter]
    private WorkingCopy? Copy { get; set; }

    private readonly ExpressionCheck _check = new();
    private readonly ComponentLifetime _lifetime = new();

    /// <summary>Redraws the boxes whenever a check has something new to show.</summary>
    public HooksTab() => _check.Changed += Redraw;

    /// <summary>The builder on screen: the hook being edited, or the next new one.</summary>
    private HookBuilder Current => _editing?.Builder ?? _new;

    private void Redraw() => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose()
    {
        _check.Changed -= Redraw;
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Opens the sheet for a new hook, with the point and the kind the last one had.</summary>
    private void OpenNew()
    {
        _new.Fields = DeclaredFields();
        _editing = null;
        _refusal.Clear();
        _adding = true;
    }

    /// <summary>Switches the point, which may switch the kind (<see cref="HookBuilder.Choose"/>).</summary>
    private void Choose(string point)
    {
        Current.Choose(point);
        _refusal.Clear();
        CheckAll();
    }

    private void ChooseKind(string kind)
    {
        Current.Kind = kind;
        CheckAll();
    }

    private void TypeCondition(string? text)
    {
        Current.Condition = text ?? string.Empty;
        _ = CheckConditionAsync();
    }

    private void TypeMutateValue(string? text)
    {
        FirstRow.Text = text ?? string.Empty;
        FirstRow.Mode = MutateMode.Expression;
        _ = CheckMutateValueAsync();
    }

    /// <summary>The patched field names the slot the value is checked in, so it is asked again.</summary>
    private void TypeMutateField(string? text)
    {
        FirstRow.Field = text ?? string.Empty;
        _ = CheckMutateValueAsync();
    }

    /// <summary>The one field-and-value pair the form draws until it draws rows (plan Task 9 replaces it).</summary>
    private MutateRow FirstRow
    {
        get
        {
            if (Current.MutateRows.Count == 0)
            {
                Current.MutateRows.Add(new MutateRow { Mode = MutateMode.Expression });
            }

            return Current.MutateRows[0];
        }
    }

    /// <summary>The point and the kind decide every slot's path, so every box on the form is asked again.</summary>
    private void CheckAll()
    {
        _ = CheckConditionAsync();
        _ = CheckMutateValueAsync();
    }

    /// <summary>The condition, checked in a hook that carries the draft's action (stand-ins for blanks).</summary>
    private Task CheckConditionAsync() => CheckAsync("hook-condition", Current.Condition, (copy, source)
        => ExpressionSlots.ForHook(
            copy.Json, Entity, Current.Point, CurrentPosition(copy), HookPatch.Apply(_editing?.Original, source, Current.Draft()), "condition"));

    /// <summary>The mutate value, checked in a hook without the condition (spec D4).</summary>
    private Task CheckMutateValueAsync() => CheckAsync(
        "hook-mutate-value", Current.Kind == HookBuilder.Mutate ? FirstRow.Text : string.Empty, (copy, _)
        => string.IsNullOrWhiteSpace(FirstRow.Field)
            ? null
            : ExpressionSlots.ForHook(
                copy.Json, Entity, Current.Point, CurrentPosition(copy),
                Current.CandidateHook(_editing?.Original, withCondition: false), "action", "mutate", FirstRow.Field.Trim()));

    /// <summary>
    /// Runs the check to its end and observes its fault: it is fire-and-forget, so an unobserved exception would
    /// otherwise vanish, and a helper that fails must never be the reason the form misbehaves.
    /// </summary>
    private async Task CheckAsync(string id, string text, Func<WorkingCopy, string, (string Json, string Path)?> place)
    {
        try
        {
            await _check.SubmitAsync(id, text, (source, ct) => AskAsync(place, source, ct));
        }
        catch (Exception ex)
        {
            CheckFailed(Logger, ex);
        }
    }

    /// <summary>Fixed text and the exception only: the entity name is the caller's string and is never logged raw.</summary>
    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "The expression check on a hook input failed")]
    private static partial void CheckFailed(ILogger logger, Exception exception);

    private Task<ManagementExpressionVerdict?> AskAsync(
        Func<WorkingCopy, string, (string Json, string Path)?> place, string source, CancellationToken ct)
        => !_lifetime.Ended && Copy is { } copy && place(copy, source) is { } slot
            ? Gateway.CheckExpressionAsync(slot.Json, slot.Path, source, ct)
            : Task.FromResult<ManagementExpressionVerdict?>(null);

    /// <summary>
    /// The hooks declared at one point, each as its own JSON.
    /// </summary>
    /// <remarks>
    /// Parsed here rather than upstream so the tab keeps taking the one shape both the applied descriptor
    /// and the working copy are already read in. A point whose value is not an array renders as nothing
    /// rather than throwing: the descriptor reaching this screen has been applied, but the working copy may
    /// have been imported a moment ago and this tab is not the authority that refuses it.
    /// </remarks>
    private static List<string> Declared(string listJson)
    {
        try
        {
            var parsed = JsonNode.Parse(listJson);
            return parsed is JsonArray list
                ? [.. list.Select(hook => hook?.ToJsonString(_readable) ?? "{}")]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Why the editor cannot draw this hook, or <see langword="null"/> when its row offers Edit.</summary>
    private static string? Undrawable(string point, string hookJson)
    {
        try
        {
            return HookShape.Undrawable(JsonNode.Parse(hookJson), point);
        }
        catch (JsonException)
        {
            return "It is not JSON the editor can read.";
        }
    }

    /// <summary>How many hooks a point declares now, for the edit sheet's subtitle.</summary>
    private int CountAt(string point) => Declared(Hooks.FirstOrDefault(declared => declared.Key == point).Value ?? "[]").Count;

    /// <summary>The primary action: adds the new hook, or saves the edited one.</summary>
    private async Task SubmitAsync()
    {
        if (_editing is { } editing)
        {
            SaveEdit(editing);
            return;
        }

        await AddAsync();
    }

    /// <summary>Declares the hook <see cref="HookBuilder.Build"/> makes, or shows why it cannot be made.</summary>
    private async Task AddAsync()
    {
        _new.Fields = DeclaredFields();
        if (_new.Build(out var refusal) is not { } action)
        {
            _refusal.Show(refusal);
            return;
        }

        /* The working copy appends to the point's list, so the new hook lands after the ones drawn there now. */
        var point = _new.Point;
        var position = CountAt(point);
        await OnAdd.InvokeAsync(new NewHook(point, _new.Condition, action));
        Reveal(point, position);
        CloseEditor();
    }

    /// <summary>Closes the sheet and forgets what was typed; a new hook's point and kind stay for the next one.</summary>
    private void CloseEditor()
    {
        _new.Clear();
        _editing = null;
        _adding = false;
        _refusal.Clear();
        CheckAll();
    }

    /// <summary>Lights the row at a place, and scrolls to it (spec §3.5).</summary>
    private void Reveal(string point, int position)
    {
        _reveal = RowId(point, position);
        _reveals++;
    }

    /// <summary>Whether the sheet holds anything closing would lose.</summary>
    /// <remarks>
    /// A new hook: anything typed — the point and the kind are not counted, because the builder keeps them from the last
    /// hook on purpose (<see cref="HookBuilder.Clear"/>), so counting them would ask "Discard your changes?" of a sheet the
    /// operator has not touched. An edit: anything that differs from what it opened with, the kind included.
    /// </remarks>
    private bool Dirty => _editing is { } editing ? Current.Fingerprint() != editing.Opened : _new.HasInput;

    /// <summary>The entity's fields as the working copy declares them now.</summary>
    private IReadOnlyDictionary<string, FieldSchema> DeclaredFields()
        => Copy is { } copy ? HookFields.Declared(copy.Json, Entity) : new Dictionary<string, FieldSchema>(StringComparer.Ordinal);

    /// <summary>
    /// The confirm's verb: asks the entity screen to drop the hook that was asked about, found again where it is now.
    /// </summary>
    /// <remarks>
    /// By what the hook is, not by where it was, for the Indexes tab's reason: another tab's edit reaching the copy
    /// while the confirm is up moves the rows. One no longer declared is left alone. Focus then goes to the row that
    /// took its place, or to the list's New hook (spec §3.2).
    /// </remarks>
    private async Task RemoveAsync()
    {
        if (_removing is not { } removing)
        {
            return;
        }

        _removing = null;
        var position = PositionIn(Hooks, removing.Point, removing.Json);
        if (position >= 0)
        {
            /* The rows after it move up, so the lit one would be a different hook. */
            _reveal = null;
            await OnRemove.InvokeAsync(new HookAt(removing.Point, position));
        }

        await Interop.FocusFirstOnceClosedAsync(
            [RemoveButton(removing.Point, position), RemoveButton(removing.Point, position - 1), "[data-testid='hook-new']"]);
    }

    /// <summary>Cancel and Escape keep the hook, and focus goes back to its Remove.</summary>
    private Task CancelRemoval()
    {
        var removing = _removing;
        _removing = null;
        return removing is null
            ? Task.CompletedTask
            : Interop.FocusFirstOnceClosedAsync(
                [RemoveButton(removing.Point, PositionIn(Hooks, removing.Point, removing.Json)), "[data-testid='hook-new']"]);
    }

    /// <summary>Where a hook, as drawn, is declared at its point in <paramref name="hooks"/>, or -1 when it no longer is.</summary>
    private static int PositionIn(IReadOnlyList<KeyValuePair<string, string>> hooks, string point, string json)
        => Declared(hooks.FirstOrDefault(declared => declared.Key == point).Value ?? "[]").IndexOf(json);

    private static string RemoveButton(string point, int position) => $"#{RowId(point, position)} [data-testid='hook-remove']";

    /// <summary>The hook the confirm is asking about: its point, and the declaration as it was drawn.</summary>
    /// <param name="Point">The hook point.</param>
    /// <param name="Json">The hook as the row drew it.</param>
    private sealed record PendingRemoval(string Point, string Json);

    /// <summary>One hook row's element id, by where it sits.</summary>
    private static string RowId(string point, int position) => $"hook-{point}-{position}";
}
```

`ConditionDescribedBy` is used by the markup — add it to `HooksTab.Edit.cs` below (it belongs with the length sentence).

- [ ] **Step 6: Create `HooksTab.Edit.cs`**

```csharp
using MMLib.Alvo.Admin.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* Editing a declared hook where it sits (spec §4.2, §5.1; ruling B1). */
public partial class HooksTab
{
    /// <summary>The in-place refusal when the hook changed under the open sheet.</summary>
    private const string ChangedElsewhere =
        "This hook changed in the working copy after you opened it — in another tab, by the assistant or by an import. "
        + "Nothing was saved. Close this editor and open the hook again.";

    private Editing? _editing;

    /// <summary>Opens the sheet on a declared hook, loaded whole; a hook the editor cannot draw has no Edit to press.</summary>
    /// <param name="point">The point it is declared at.</param>
    /// <param name="position">Where its row is.</param>
    /// <param name="json">The hook as its row drew it.</param>
    private void OpenEdit(string point, int position, string json)
    {
        if (JsonNode.Parse(json) is not JsonObject original
            || HookBuilder.From(point, original, DeclaredFields()) is not { } builder)
        {
            return;
        }

        _adding = false;
        _editing = new Editing(point, position, json, original, builder, builder.Fingerprint());
        _refusal.Clear();
        CheckAll();
    }

    /// <summary>
    /// Writes the edit onto the hook it opened — found again by what it was, then replaced under the copy's gate only if it
    /// is still exactly that (spec §5.1). Otherwise the sheet stays open with the refusal in place.
    /// </summary>
    private void SaveEdit(Editing editing)
    {
        editing.Builder.Fields = DeclaredFields();
        if (editing.Builder.BuildHook(editing.Original, out var refusal) is not { } hook)
        {
            _refusal.Show(refusal);
            return;
        }

        var position = Copy is { } copy ? PositionIn(copy.HooksOf(Entity), editing.Point, editing.Json) : -1;
        if (position < 0 || !Copy!.ReplaceHook(Entity, editing.Point, position, editing.Json, hook))
        {
            _refusal.Show(ChangedElsewhere);
            return;
        }

        Snackbar.Confirm(StagedWords.Saved("Hook", editing.Point));
        Reveal(editing.Point, position);
        CloseEditor();
    }

    /// <summary>Where the edited hook sits in the copy now — or <see langword="null"/> for a new hook, which is appended.</summary>
    private int? CurrentPosition(WorkingCopy copy)
        => _editing is { } editing ? PositionIn(copy.HooksOf(Entity), editing.Point, editing.Json) : null;

    /// <summary>The condition box's descriptions: its hint, the length sentence while there is one, and the check's.</summary>
    private string ConditionDescribedBy
        => Current.Condition.Length > ConditionTable.MaxConditionLength
            ? $"{Described("hook-condition")} hook-condition-length"
            : Described("hook-condition");

    /// <summary>The hook the sheet opened.</summary>
    /// <param name="Point">The point it is declared at.</param>
    /// <param name="Position">Where its row was when it opened.</param>
    /// <param name="Json">The hook as its row drew it — what the save finds it by.</param>
    /// <param name="Original">The hook as declared, which the edit is patched onto.</param>
    /// <param name="Builder">What the sheet holds.</param>
    /// <param name="Opened">The builder's fingerprint when it opened.</param>
    private sealed record Editing(string Point, int Position, string Json, JsonObject Original, HookBuilder Builder, string Opened);
}
```

- [ ] **Step 7: Rename the row control class** — in `alvo.css:1765` change `.a-hookrow__remove {` to `.a-hookrow__actions {` (the rule's comment stays true: the controls are trailing on a phone). `grep -rn "a-hookrow__remove" src docs test` must then print nothing.

- [ ] **Step 8: Run, normalise, run again**

Run: `dotnet build` (0 warnings); `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*PatternLanguageTests' --filter-class '*FieldConventionTests' --filter-class '*StylesheetHygieneTests'` — PASS; `scripts/test-ring0`; then `scripts/test-admin-e2e --filter-class '*HookEditInPlaceScenarios'`, `--filter-class '*HookShapeScenarios'`, `--filter-class '*HookEditingScenarios'`, `--filter-class '*ConfirmFocusScenarios'`, `--filter-class '*ExpressionCheckScenarios'`, `--filter-class '*CreateActionScenarios'` — all PASS. If `A_hook_moved_by_another_tab…` finds the edited row not lit, the entity screen's `OnCopyChanged` redraw arrived after the reveal; the reveal is keyed by attempt, so wait for the row text before reading the attribute.

- [ ] **Step 9: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Edit.cs src/MMLib.Alvo.Admin/wwwroot/alvo.css test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/HookEditInPlaceScenarios.cs
git commit -m "feat(admin): edit a hook where it sits, refused in place when another tab overtook it

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---
### Task 9: mutate rows — several fields, a value or an expression each

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor` (replace the `MutateFields` property; add `MutateValue` and `Fit`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs` (remove the interim single pair; `CheckAll`, `OpenNew`, `ChooseKind`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Edit.cs` (`OpenEdit` reads the writable fields)
- Create: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Mutate.cs`
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/ExpressionCheckScenarios.cs:86-105` (row ids)
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/MutateEditingScenarios.cs`

**Interfaces:**
- Consumes: `MutateRow`, `MutateMode`, `MutateLiteral.TryValue/Hint` (Task 4), `HookFields.Writable` (Task 4), `ExpressionSlots.ForHook` (Task 7), `Current`, `CheckAsync`, `CurrentPosition`, `Described`, `Findings` (Task 8).
- Produces: test ids `hook-mutate-row`, `hook-mutate-field-{i}`, `hook-mutate-mode-{i}`, `hook-mutate-value-{i}` (the check key too), `hook-mutate-fit-{i}`, `hook-mutate-empty-{i}`, `hook-mutate-remove-{i}`, `hook-mutate-add`; labels "Field {n}", "Field {n} takes", "Set field {n} to"; methods `CheckMutateValues()`, `EnsureMutateRow()`, `WritableFields()`, field `_writable`.

- [ ] **Step 1: Write the failing e2e scenarios** (`MutateEditingScenarios.cs`):

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A mutate patches several fields, each with a value its type holds or a CEL expression (spec §4.3, ruling B3). Each
/// scenario works on a different entity of the bike-workshop descriptor, because the class shares one working copy.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class MutateEditingScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_mutate_sets_a_literal_and_an_expression_in_one_hook()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateAsync(session, "service_orders", "beforeUpdate");

        await session.ChooseAsync(Combobox(session, "Field 1"), "paid");
        await session.Page.GetByTestId("hook-mutate-value-0").GetByRole(AriaRole.Radio, new() { Name = "true", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-mutate-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Field 2"), "work_notes");
        await session.Page.GetByTestId("hook-mutate-mode-1").GetByRole(AriaRole.Radio, new() { Name = "an expression", Exact = true }).ClickAsync();
        await session.Page.FillAsync("#hook-mutate-value-1", "'Paid in full'");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var row = await session.Page.Locator("#hook-beforeUpdate-2").InnerTextAsync();
        row.ShouldContain("\"paid\": true");
        row.ShouldContain("\"work_notes\"");
        row.ShouldContain("'Paid in full'");

        await session.GoAsync("/changes");
        await session.WaitForPlanAsync();
        await session.Page.Locator("#apply-reason").WaitForAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0, "the apply accepts what the rows wrote");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_literal_that_does_not_fit_its_field_is_said_under_it_and_refused_on_submit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateAsync(session, "bikes", "beforeCreate");
        await session.ChooseAsync(Combobox(session, "Field 1"), "model_year");

        await session.Page.FillAsync("#hook-mutate-value-0", "abc");
        var fit = session.Page.GetByTestId("hook-mutate-fit-0");
        await fit.WaitForAsync();
        (await fit.InnerTextAsync()).ShouldContain("whole number");

        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await session.WaitForFocusInsideAsync("error-panel");
        (await editor.GetByTestId("error-panel").InnerTextAsync()).ShouldContain("whole number");

        await session.Page.FillAsync("#hook-mutate-value-0", "2026");
        await fit.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_enum_literal_is_chosen_from_its_declared_values()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateAsync(session, "rentals", "beforeUpdate");
        await session.ChooseAsync(Combobox(session, "Field 1"), "status");

        await session.ChooseAsync(Combobox(session, "Set field 1 to"), "returned");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-beforeUpdate-0").InnerTextAsync()).ShouldContain("\"status\": \"returned\"");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_managed_or_computed_field_is_not_offered()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateAsync(session, "service_orders", "beforeCreate");

        await Combobox(session, "Field 1").ClickAsync();
        (await Option(session, "status").CountAsync()).ShouldBe(1);
        (await Option(session, "total").CountAsync()).ShouldBe(0, "a computed field is maintained by the database");
        (await Option(session, "lines_count").CountAsync()).ShouldBe(0, "a rollup is maintained by the framework");
        (await Option(session, "created_at").CountAsync()).ShouldBe(0, "an audit column is the framework's");
        (await Option(session, "id").CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Two_rows_fit_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await NewMutateAsync(session, "customers", "beforeUpdate");
        await session.Page.GetByTestId("hook-mutate-add").ClickAsync();

        await session.AssertNoHorizontalScrollAsync();
    }

    private static async Task NewMutateAsync(AdminSession session, string entity, string point)
    {
        await HookEditInPlaceScenarios.OnWriteAsync(session, entity);
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = point, Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = "mutate", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-mutate-row").First.WaitForAsync();
    }

    private static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });

    private static ILocator Option(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });
}
```

- [ ] **Step 2: Run to verify failure** — `scripts/test-admin-e2e --filter-class '*MutateEditingScenarios'`. Expected: FAIL — no `hook-mutate-row`.

- [ ] **Step 3: Create `HooksTab.Mutate.cs`**

```csharp
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Schema;

/* A mutate's rows: several fields, each a value its type holds or a CEL expression (spec §4.3, ruling B3). */
public partial class HooksTab
{
    private const string ValueWord = "a value";
    private const string ExpressionWord = "an expression";
    private static readonly string[] _mutateModes = [ValueWord, ExpressionWord];
    private static readonly string[] _flags = ["true", "false"];

    /// <summary>The fields a mutate may name, read when the sheet opens (<see cref="HookFields.Writable"/>).</summary>
    private IReadOnlyList<string> _writable = [];

    private void AddMutateRow()
    {
        Current.MutateRows.Add(new MutateRow());
        CheckMutateValues();
    }

    /// <summary>Drops a row; the last row's check key no longer names a row, so its sentence goes too.</summary>
    private void RemoveMutateRow(int index)
    {
        if (Current.MutateRows.Count < 2 || index >= Current.MutateRows.Count)
        {
            return;
        }

        Current.MutateRows.RemoveAt(index);
        _ = CheckAsync(MutateValueId(Current.MutateRows.Count), string.Empty, (_, _) => null);
        CheckMutateValues();
    }

    /// <summary>A new field takes a new value: what was typed for the old one is not its.</summary>
    private void ChooseMutateField(int index, string? field)
    {
        var row = Current.MutateRows[index];
        row.Field = field ?? string.Empty;
        row.Text = string.Empty;
        row.Empty = false;
        CheckMutateValues();
    }

    private void ChooseMutateMode(int index, string word)
    {
        var row = Current.MutateRows[index];
        row.Mode = word == ExpressionWord ? MutateMode.Expression : MutateMode.Literal;
        row.Empty = false;
        CheckMutateValues();
    }

    private void TypeMutateText(int index, string? text)
    {
        Current.MutateRows[index].Text = text ?? string.Empty;
        _ = CheckMutateValueAsync(index);
    }

    private void SetMutateEmpty(int index, bool empty) => Current.MutateRows[index].Empty = empty;

    /// <summary>Asks about every row again: a row's index is part of its slot, and rows move.</summary>
    private void CheckMutateValues()
    {
        for (var index = 0; index < Current.MutateRows.Count; index++)
        {
            _ = CheckMutateValueAsync(index);
        }
    }

    /// <summary>One row's value, checked in a hook without the condition (spec D4); a literal is no CEL slot, so it clears.</summary>
    private Task CheckMutateValueAsync(int index)
    {
        var row = Current.MutateRows[index];
        var source = Current.Kind == HookBuilder.Mutate && row.Mode == MutateMode.Expression ? row.Text : string.Empty;
        var field = row.Field.Trim();
        return CheckAsync(MutateValueId(index), source, (copy, _) => field.Length == 0
            ? null
            : ExpressionSlots.ForHook(
                copy.Json, Entity, Current.Point, CurrentPosition(copy),
                Current.CandidateHook(_editing?.Original, withCondition: false), "action", "mutate", field));
    }

    /// <summary>A mutate always shows one row at least, so its first field is one choice away.</summary>
    private void EnsureMutateRow()
    {
        if (Current.Kind == HookBuilder.Mutate && Current.MutateRows.Count == 0)
        {
            Current.MutateRows.Add(new MutateRow());
        }
    }

    private IReadOnlyList<string> WritableFields() => Copy is { } copy ? HookFields.Writable(copy.Json, Entity) : [];

    private static string MutateValueId(int index) => $"hook-mutate-value-{index}";

    private static string ModeWord(MutateRow row) => row.Mode == MutateMode.Expression ? ExpressionWord : ValueWord;

    private FieldSchema? FieldOf(MutateRow row) => Current.Fields.GetValueOrDefault(row.Field.Trim());

    /// <summary>The writable fields, and — for a hook that names another — that one too, so it is shown rather than lost.</summary>
    private IReadOnlyList<string> MutateTargets(MutateRow row)
        => row.Field.Length == 0 || _writable.Contains(row.Field, StringComparer.Ordinal) ? _writable : [row.Field, .. _writable];

    private string TargetLabel(string target) => _writable.Contains(target, StringComparer.Ordinal) ? target : $"{target} (not offered)";

    private static IReadOnlyCollection<string> FlagSelected(MutateRow row) => row.Text.Length > 0 ? [row.Text] : [];

    private string InputMode(MutateRow row) => FieldOf(row)?.Type is FieldType.Integer or FieldType.Decimal ? "decimal" : "text";

    /// <summary>Why the typed literal does not fit its field — computed on each render, focus-free (spec §4.3).</summary>
    private string? MutateFit(MutateRow row)
        => row.Mode == MutateMode.Literal && (row.Text.Length > 0 || row.Empty) && FieldOf(row) is { } field
           && !MutateLiteral.TryValue(row, field, out _, out var refusal)
            ? refusal
            : null;

    private string LiteralDescribedBy(MutateRow row, int index)
        => MutateFit(row) is null ? $"{MutateValueId(index)}-hint" : $"{MutateValueId(index)}-hint {MutateValueId(index)}-fit";
}
```

- [ ] **Step 4: Replace the `MutateFields` property in `HooksTab.razor`** (the whole `private RenderFragment MutateFields => @<text> … </text>;`) with these three:

```razor
    /// <summary>A mutate's rows: each a field, a value or an expression, and the box that value takes (spec §4.3).</summary>
    private RenderFragment MutateFields => @<text>
        @for (var index = 0; index < Current.MutateRows.Count; index++)
        {
            var row = Current.MutateRows[index];
            var i = index;
            <div class="a-stack" role="group" aria-label="@($"Patched field {i + 1}")" data-testid="hook-mutate-row">
                <div class="a-field-row">
                    <Field Label="@($"Field {i + 1}")" LabelId="@($"hook-mutate-field-{i}-label")">
                        <MudSelect T="string" Variant="Variant.Outlined" Value="row.Field" ValueChanged="field => ChooseMutateField(i, field)"
                                   aria-labelledby="@($"hook-mutate-field-{i}-label")" Class="a-mono"
                                   data-testid="@($"hook-mutate-field-{i}")">
                            @foreach (var target in MutateTargets(row))
                            {
                                <MudSelectItem Value="@target">@TargetLabel(target)</MudSelectItem>
                            }
                        </MudSelect>
                    </Field>
                    <Field Label="@($"Field {i + 1} takes")" LabelId="@($"hook-mutate-mode-{i}-label")">
                        <ChipGroup TValue="string" Items="_mutateModes" Selected="[ModeWord(row)]"
                                   aria-labelledby="@($"hook-mutate-mode-{i}-label")" data-testid="@($"hook-mutate-mode-{i}")"
                                   SelectedChanged="modes => ChooseMutateMode(i, modes[0])" />
                    </Field>
                </div>
                @MutateValue(row, i)
                @if (Current.MutateRows.Count > 1)
                {
                    <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="@($"hook-mutate-remove-{i}")"
                                aria-label="@($"Remove patched field {i + 1}")" OnClick="_ => RemoveMutateRow(i)">Remove</AlvoButton>
                }
            </div>
        }
        <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="hook-mutate-add" OnClick="AddMutateRow">Add a field</AlvoButton>
    </text>;

    /// <summary>The box a row's field and mode take: CEL, a declared value, true/false, or typed text.</summary>
    private RenderFragment MutateValue(MutateRow row, int i) => @<text>
        @if (row.Mode == MutateMode.Expression)
        {
            <Field Label="@($"Set field {i + 1} to")" For="@MutateValueId(i)">
                <ChildContent>
                    <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="@MutateValueId(i)" Class="a-mono"
                                  autocomplete="off" Placeholder="now()" aria-describedby="@Described(MutateValueId(i))"
                                  Value="row.Text" ValueChanged="text => TypeMutateText(i, text)" />
                    @Findings(MutateValueId(i))
                </ChildContent>
                <Hint>A CEL expression in the Mutate profile, stored as <code class="a-mono">{"$cel": "…"}</code>.</Hint>
            </Field>
        }
        else if (FieldOf(row) is { Type: FieldType.Enum } choice)
        {
            <Field Label="@($"Set field {i + 1} to")" LabelId="@($"{MutateValueId(i)}-label")">
                <MudSelect T="string" Variant="Variant.Outlined" Value="row.Text" ValueChanged="text => TypeMutateText(i, text)"
                           aria-labelledby="@($"{MutateValueId(i)}-label")" Class="a-mono" Disabled="row.Empty"
                           data-testid="@MutateValueId(i)">
                    @foreach (var value in choice.EnumValues ?? Array.Empty<string>())
                    {
                        <MudSelectItem Value="@value">@value</MudSelectItem>
                    }
                </MudSelect>
            </Field>
        }
        else if (FieldOf(row) is { Type: FieldType.Boolean })
        {
            <Field Label="@($"Set field {i + 1} to")" LabelId="@($"{MutateValueId(i)}-label")">
                <ChipGroup TValue="string" Items="_flags" Selected="FlagSelected(row)"
                           aria-labelledby="@($"{MutateValueId(i)}-label")" data-testid="@MutateValueId(i)"
                           SelectedChanged="flags => TypeMutateText(i, flags[0])" />
            </Field>
        }
        else if (FieldOf(row) is { Type: FieldType.Json } json)
        {
            <p class="a-note" data-testid="@($"hook-mutate-json-{i}")">
                '@json.Name' is a json field, and this build converts no literal into one. Write an expression, or set it to empty.
            </p>
        }
        else
        {
            <Field Label="@($"Set field {i + 1} to")" For="@MutateValueId(i)">
                <ChildContent>
                    <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="@MutateValueId(i)" Class="a-mono"
                                  autocomplete="off" inputmode="@InputMode(row)" Disabled="row.Empty"
                                  aria-describedby="@LiteralDescribedBy(row, i)" Value="row.Text" ValueChanged="text => TypeMutateText(i, text)" />
                    @Fit(row, i)
                </ChildContent>
                <Hint>@(FieldOf(row) is { } field ? MutateLiteral.Hint(field) : "Choose the field first.")</Hint>
            </Field>
        }
        @if (row.Mode == MutateMode.Literal && FieldOf(row) is { Required: false })
        {
            <MudCheckBox T="bool" Value="row.Empty" ValueChanged="on => SetMutateEmpty(i, on)" Label="Set to empty"
                         data-testid="@($"hook-mutate-empty-{i}")" />
        }
    </text>;

    /// <summary>The fit sentence under a literal box, after its hint (spec §3.8).</summary>
    private RenderFragment Fit(MutateRow row, int i) => @<text>
        @if (MutateFit(row) is { } fit)
        {
            <p class="a-field__problem" id="@($"{MutateValueId(i)}-fit")" data-testid="@($"hook-mutate-fit-{i}")">@fit</p>
        }
    </text>;
```

- [ ] **Step 5: Remove the interim pair and wire the rows** in `HooksTab.razor.cs`:
  - delete `TypeMutateValue`, `TypeMutateField`, the `FirstRow` property and the parameterless `CheckMutateValueAsync()`;
  - `CheckAll` becomes:

```csharp
    private void CheckAll()
    {
        _ = CheckConditionAsync();
        CheckMutateValues();
    }
```

  - in `OpenNew`, after `_refusal.Clear();` add `_writable = WritableFields();` and `EnsureMutateRow();`;
  - `ChooseKind` becomes:

```csharp
    private void ChooseKind(string kind)
    {
        Current.Kind = kind;
        EnsureMutateRow();
        CheckAll();
    }
```

  - in `HooksTab.Edit.cs` `OpenEdit`, after the `_editing = new Editing(…)` line add `_writable = WritableFields();`.

- [ ] **Step 6: Point the existing mutate check scenario at the rows** — in `ExpressionCheckScenarios.A_mutate_value_naming_an_undeclared_field_is_flagged_and_the_flag_clears_when_it_is_fixed` replace the body from the `hook-actions` click to the end with:

```csharp
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = "mutate", Exact = true }).ClickAsync();
        await session.ChooseAsync(session.Page.GetByRole(AriaRole.Combobox, new() { Name = "Field 1", Exact = true }), "name");
        await session.Page.GetByTestId("hook-mutate-mode-0").GetByRole(AriaRole.Radio, new() { Name = "an expression", Exact = true }).ClickAsync();

        await session.Page.FillAsync("#hook-mutate-value-0", "new.no_such_field");
        var finding = session.Page.GetByTestId("check-hook-mutate-value-0").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("no_such_field");
        await ShouldKeepFocusAsync(session, "hook-mutate-value-0");

        await session.Page.FillAsync("#hook-mutate-value-0", "new.code");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
        (await session.Page.GetByTestId("check-hook-mutate-value-0").CountAsync()).ShouldBe(0, "every sentence goes, not only the first");
        await ShouldKeepFocusAsync(session, "hook-mutate-value-0");
```

- [ ] **Step 7: Run, normalise, run again** — `dotnet build`; `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*PatternLanguageTests' --filter-class '*FieldConventionTests'`; `scripts/test-ring0`; `scripts/test-admin-e2e --filter-class '*MutateEditingScenarios'`, `--filter-class '*ExpressionCheckScenarios'`, `--filter-class '*HookEditInPlaceScenarios'` — all PASS. If the first `MudSelect` inside the sheet does not open (a popover under a `MudDialog` is new on this dashboard, spec §11), read MudBlazor 9.10's `MudSelect` popover docs and the `MudPopoverProvider` placement in `AdminLayout.razor` before changing anything else; the fix belongs in the layout, not per select.

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Edit.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Mutate.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/MutateEditingScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExpressionCheckScenarios.cs
git commit -m "feat(admin): a mutate patches several fields, each a value its type holds or an expression

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 10: endpoint and template pickers, the payload box, a checked recipient

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor` (replace `WebhookFields` and `EmailFields`; add `PayloadNotes`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs` (`OpenNew`, `CheckAll`), `HooksTab.Edit.cs` (`OpenEdit`)
- Create: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Pickers.cs`
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/HookPickerScenarios.cs`

**Interfaces:**
- Consumes: `DescriptorLens.Endpoints/Templates` (Task 2), `HookBuilder.MaxPayloadLength` (Task 6), `ExpressionSlots.ForHook` (Task 7), `ChordHint.Of`, Task 8's `Current`, `CheckAsync`, `CurrentPosition`, `Described`, `Findings`.
- Produces: test ids `hook-endpoint`, `hook-endpoint-hint`, `hook-payload`, `hook-payload-length`, `hook-payload-pending`, `hook-template`, `hook-template-hint`, `hook-to`; check keys `hook-payload`, `hook-to`; labels "Endpoint", "Template", "Payload (optional)", "To"; method `ReadPickers()`.

- [ ] **Step 1: Write the failing e2e scenarios** (`HookPickerScenarios.cs`):

```csharp
using Microsoft.Playwright;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// An after-hook names a declared endpoint or template, picked from the working copy, and its payload and recipient are
/// judged by the build as they are typed (spec §4.4, §4.5; rulings B4, B5).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class HookPickerScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_endpoint_picker_offers_the_declared_endpoint_and_writes_its_name()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewAfterHookAsync(session, "rentals", "afterUpdate", "webhook");

        await session.ChooseAsync(Combobox(session, "Endpoint"), "rental-desk");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-afterUpdate-0").InnerTextAsync()).ShouldContain("\"endpoint\": \"rental-desk\"");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_raw_jsonata_payload_gets_the_builds_own_refusal_and_a_broken_condition_does_not_hide_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewAfterHookAsync(session, "rentals", "afterDelete", "webhook");
        await session.ChooseAsync(Combobox(session, "Endpoint"), "rental-desk");
        await session.TypeConditionAsync("old.nope == 1");

        await session.Page.FillAsync("#hook-payload", "{\"id\": \"{{old.id}}\"}");
        var finding = session.Page.GetByTestId("check-hook-payload").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("JSONata transformations are not evaluated yet");

        await session.Page.FillAsync("#hook-payload", "[\"{{old.id}}\"]");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_email_picks_its_template_and_a_second_recipient_is_flagged()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewAfterHookAsync(session, "service_orders", "afterDelete", "email");

        await Combobox(session, "Template").ClickAsync();
        (await Option(session, "express-order-received").CountAsync()).ShouldBe(1);
        await Option(session, "order-ready").ClickAsync();

        await session.Page.FillAsync("#hook-to", "a@example.com, b@example.com");
        var finding = session.Page.GetByTestId("check-hook-to").First;
        await finding.WaitForAsync(new() { Timeout = 3_000 });
        (await finding.InnerTextAsync()).ShouldContain("not exactly one mailbox");

        await session.Page.FillAsync("#hook-to", "ops@example.com");
        await finding.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 3_000 });
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_payload_over_eight_thousand_characters_is_said_under_the_box()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewAfterHookAsync(session, "bikes", "afterCreate", "webhook");
        await session.ChooseAsync(Combobox(session, "Endpoint"), "rental-desk");

        await session.Page.FillAsync("#hook-payload", "[" + new string('1', 8000) + "]");

        var sentence = session.Page.GetByTestId("hook-payload-length");
        await sentence.WaitForAsync();
        (await sentence.InnerTextAsync()).ShouldContain("8000");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_webhook_sheet_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await NewAfterHookAsync(session, "parts", "afterUpdate", "webhook");
        await session.Page.FillAsync("#hook-payload", "[\"{{new.sku}}\", {{new.unit_price}}]");

        await session.AssertNoHorizontalScrollAsync();
    }

    internal static async Task NewAfterHookAsync(AdminSession session, string entity, string point, string kind)
    {
        await HookEditInPlaceScenarios.OnWriteAsync(session, entity);
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = point, Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = kind, Exact = true }).ClickAsync();
    }

    internal static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });

    private static ILocator Option(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });
}

/// <summary>
/// A hook naming an endpoint the copy does not declare keeps that name in the picker (spec §4.4) — its own world, because
/// the descriptor arrives by import.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class UndeclaredReferenceScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_hook_naming_an_undeclared_endpoint_keeps_it_and_says_so()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var descriptor = JsonNode.Parse(Descriptors.BikeWorkshop)!.AsObject();
        descriptor["entities"]!["rentals"]!["hooks"]!["afterCreate"]![0]!["action"]!["endpoint"] = "gone-desk";
        await session.GoAsync("/transfer");
        await session.Page.FillAsync("#import-json", descriptor.ToJsonString());
        await session.Page.Locator("#import-json").PressAsync("Meta+Enter");
        await session.Page.WaitForURLAsync("**/changes");

        await HookEditInPlaceScenarios.OnWriteAsync(session, "rentals");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "afterCreate", 0);

        (await session.Page.GetByTestId("hook-endpoint-hint").InnerTextAsync()).ShouldContain("'gone-desk' is not declared");
        await HookPickerScenarios.Combobox(session, "Endpoint").ClickAsync();
        (await session.Page.GetByRole(AriaRole.Option, new() { Name = "gone-desk (not declared)", Exact = true }).CountAsync()).ShouldBe(1);
        (await session.Page.GetByRole(AriaRole.Option, new() { Name = "rental-desk", Exact = true }).CountAsync()).ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `scripts/test-admin-e2e --filter-class '*HookPickerScenarios'`. Expected: FAIL — no combobox named "Endpoint".

- [ ] **Step 3: Create `HooksTab.Pickers.cs`**

```csharp
using MMLib.Alvo.Admin.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The endpoint and template pickers, the payload box and the recipient (spec §4.4, §4.5; rulings B4, B5). */
public partial class HooksTab
{
    private IReadOnlyList<Choice> _endpoints = [];
    private IReadOnlyList<Choice> _templates = [];
    private int _bodyFileTemplates;

    /// <summary>Reads what the pickers offer from the working copy — a declaration not applied yet included (ruling B5).</summary>
    private void ReadPickers()
    {
        if (Copy is not { } copy)
        {
            _endpoints = [];
            _templates = [];
            _bodyFileTemplates = 0;
            return;
        }

        var endpoints = DescriptorLens.Endpoints(copy.Json).Select(pair => pair.Key).ToList();
        _endpoints = Choices(endpoints, Names(DescriptorLens.Endpoints(copy.AppliedJson)), Current.Endpoint, "not declared");
        var templates = DescriptorLens.Templates(copy.Json);
        var bodyFiles = templates.Where(pair => HasBodyFile(pair.Value)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        _bodyFileTemplates = bodyFiles.Count;
        var offered = templates.Select(pair => pair.Key).Where(name => !bodyFiles.Contains(name)).ToList();
        var missing = bodyFiles.Contains(Current.Template) ? "reads a bodyFile" : "not declared";
        _templates = Choices(offered, Names(DescriptorLens.Templates(copy.AppliedJson)), Current.Template, missing);
    }

    private void ChooseEndpoint(string? name)
    {
        Current.Endpoint = name ?? string.Empty;
        _ = CheckPayloadAsync();
    }

    private void TypePayload(string? text)
    {
        Current.Payload = text ?? string.Empty;
        _ = CheckPayloadAsync();
    }

    private void ChooseTemplate(string? name)
    {
        Current.Template = name ?? string.Empty;
        _ = CheckToAsync();
    }

    private void TypeTo(string? text)
    {
        Current.To = text ?? string.Empty;
        _ = CheckToAsync();
    }

    /// <summary>
    /// The payload, judged by the live check at <c>…/action/payload</c> (the B4 deviation: the build's own refusal, not a
    /// copy of its classifier) — once a declared endpoint is chosen, because an undeclared one stops the compiler first.
    /// </summary>
    private Task CheckPayloadAsync() => CheckAsync(
        "hook-payload",
        Current.Kind == HookBuilder.Webhook && EndpointDeclared && Current.Payload.Length <= HookBuilder.MaxPayloadLength
            ? Current.Payload
            : string.Empty,
        (copy, _) => ExpressionSlots.ForHook(
            copy.Json, Entity, Current.Point, CurrentPosition(copy),
            Current.CandidateHook(_editing?.Original, withCondition: false), "action", "payload"));

    /// <summary>The recipient, judged at <c>…/action/to</c> once a declared template is chosen, for the same reason.</summary>
    private Task CheckToAsync() => CheckAsync(
        "hook-to",
        Current.Kind == HookBuilder.Email && TemplateDeclared ? Current.To : string.Empty,
        (copy, _) => ExpressionSlots.ForHook(
            copy.Json, Entity, Current.Point, CurrentPosition(copy),
            Current.CandidateHook(_editing?.Original, withCondition: false), "action", "to"));

    private bool EndpointDeclared => _endpoints.Any(choice => choice.Declared && choice.Name == Current.Endpoint);

    private bool TemplateDeclared => _templates.Any(choice => choice.Declared && choice.Name == Current.Template);

    private string EndpointHint => _endpoints.Count == 0
        ? "No endpoint is declared yet. Declare one on the Integrations screen first — this sheet does not keep what you typed if you leave it."
        : Current.Endpoint.Length > 0 && !EndpointDeclared
            ? $"'{Current.Endpoint}' is not declared; the apply refuses a hook that names it. Choose a declared endpoint, or declare it on the Integrations screen."
            : "An endpoint declared under webhooks.endpoints — the Integrations screen declares one. A declaration not applied yet is offered too.";

    private string TemplateHint
        => (_templates.Count == 0
               ? "No template is declared yet. Declare one on the Integrations screen first — this sheet does not keep what you typed if you leave it."
               : "A template declared under templates — the Integrations screen declares one.")
           + (_bodyFileTemplates > 0
               ? $" Not offered: {_bodyFileTemplates} that read a bodyFile, because this build refuses an email that sends one."
               : string.Empty);

    /// <summary>The payload box's descriptions: its hint, the local sentence while there is one, and the check's.</summary>
    private string PayloadDescribedBy
    {
        get
        {
            var ids = new List<string> { "hook-payload-hint" };
            if (Current.Payload.Length > HookBuilder.MaxPayloadLength)
            {
                ids.Add("hook-payload-length");
            }
            else if (Current.Payload.Length > 0 && !EndpointDeclared)
            {
                ids.Add("hook-payload-pending");
            }

            if (_check.DescribedBy("hook-payload") is { } check)
            {
                ids.Add(check);
            }

            return string.Join(' ', ids);
        }
    }

    /// <summary>The offered names, each saying whether it is applied, and the hook's own value first when nothing offers it.</summary>
    private static List<Choice> Choices(IReadOnlyList<string> offered, HashSet<string> applied, string current, string missing)
    {
        var choices = offered
            .Select(name => new Choice(name, applied.Contains(name) ? name : $"{name} (not applied yet)", Declared: true))
            .ToList();
        if (current.Length > 0 && !offered.Contains(current, StringComparer.Ordinal))
        {
            choices.Insert(0, new Choice(current, $"{current} ({missing})", Declared: false));
        }

        return choices;
    }

    private static HashSet<string> Names(IReadOnlyList<KeyValuePair<string, JsonElement>> declared)
        => declared.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

    private static bool HasBodyFile(JsonElement template)
        => template.ValueKind == JsonValueKind.Object && template.TryGetProperty("bodyFile", out _);

    /// <summary>One picker entry.</summary>
    /// <param name="Name">The declared name, the value written.</param>
    /// <param name="Label">What the option reads.</param>
    /// <param name="Declared">Whether the working copy declares it in a form the apply can send.</param>
    private sealed record Choice(string Name, string Label, bool Declared);
}
```

- [ ] **Step 4: Replace `WebhookFields` and `EmailFields` in `HooksTab.razor`**, and add `PayloadNotes` beside them:

```razor
    /// <summary>A webhook's endpoint, picked from the working copy, and its optional payload (spec §4.4, §4.5).</summary>
    private RenderFragment WebhookFields => @<text>
        <Field Label="Endpoint" LabelId="hook-endpoint-label">
            <ChildContent>
                <MudSelect T="string" Variant="Variant.Outlined" Value="Current.Endpoint" ValueChanged="ChooseEndpoint"
                           aria-labelledby="hook-endpoint-label" Class="a-mono" data-testid="hook-endpoint"
                           Disabled="@(_endpoints.Count == 0)">
                    @foreach (var choice in _endpoints)
                    {
                        <MudSelectItem Value="@choice.Name">@choice.Label</MudSelectItem>
                    }
                </MudSelect>
            </ChildContent>
            <Hint><span id="hook-endpoint-hint" data-testid="hook-endpoint-hint">@EndpointHint</span></Hint>
        </Field>
        <Field Label="Payload (optional)" For="hook-payload">
            <ChildContent>
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-payload" Class="a-mono" Lines="4"
                              autocomplete="off" spellcheck="false" Placeholder="[&quot;{{new.order_number}}&quot;, {{new.total}}]"
                              aria-describedby="@PayloadDescribedBy" Value="Current.Payload" ValueChanged="TypePayload" />
                @PayloadNotes
                @Findings("hook-payload")
            </ChildContent>
            <Hint>
                Left empty, the endpoint receives the CloudEvents envelope: the whole row, <code class="a-mono">hidden</code>
                fields included. A payload is JSON around <code class="a-mono">{{…}}</code> placeholders over
                <code class="a-mono">new</code>, <code class="a-mono">old</code>, <code class="a-mono">event</code> and
                <code class="a-mono">@@user.id</code> — in quotes for text (<code class="a-mono">"{{new.order_number}}"</code>),
                bare for a value (<code class="a-mono">[{{new.total}}]</code>). This build reads a <code class="a-mono">{</code>
                or <code class="a-mono">}</code> outside a placeholder as JSONata and refuses it, so an object payload is
                refused today — write an array.
                @ChordHint.Of(_editing is null ? "adds the hook to the working copy" : "saves the hook to the working copy")
            </Hint>
        </Field>
    </text>;

    /// <summary>What the payload box says without asking the server: too long, or not checked until an endpoint is declared.</summary>
    private RenderFragment PayloadNotes => @<text>
        @if (Current.Payload.Length > HookBuilder.MaxPayloadLength)
        {
            <p class="a-field__problem" id="hook-payload-length" data-testid="hook-payload-length">
                This payload is @Current.Payload.Length characters; a payload holds at most @HookBuilder.MaxPayloadLength.
            </p>
        }
        else if (Current.Payload.Length > 0 && !EndpointDeclared)
        {
            <p class="a-hint" id="hook-payload-pending" data-testid="hook-payload-pending">Checked once a declared endpoint is chosen.</p>
        }
    </text>;

    /// <summary>An email's template, picked from the working copy, and its recipient, checked as typed (spec §4.2, §4.4).</summary>
    private RenderFragment EmailFields => @<text>
        <Field Label="Template" LabelId="hook-template-label">
            <ChildContent>
                <MudSelect T="string" Variant="Variant.Outlined" Value="Current.Template" ValueChanged="ChooseTemplate"
                           aria-labelledby="hook-template-label" Class="a-mono" data-testid="hook-template"
                           Disabled="@(_templates.Count == 0)">
                    @foreach (var choice in _templates)
                    {
                        <MudSelectItem Value="@choice.Name">@choice.Label</MudSelectItem>
                    }
                </MudSelect>
            </ChildContent>
            <Hint><span id="hook-template-hint" data-testid="hook-template-hint">@TemplateHint</span></Hint>
        </Field>
        <Field Label="To" For="hook-to">
            <ChildContent>
                <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-to" Class="a-mono" autocomplete="off"
                              Placeholder="{{new.contact_email}}" aria-describedby="@Described("hook-to")"
                              Value="Current.To" ValueChanged="TypeTo" />
                @Findings("hook-to")
            </ChildContent>
            <Hint>
                One address, or a <code class="a-mono">{{…}}</code> placeholder such as
                <code class="a-mono">{{new.contact_email}}</code>, on one line. Checked once a declared template is chosen.
            </Hint>
        </Field>
    </text>;
```

- [ ] **Step 5: Wire the pickers** — in `HooksTab.razor.cs` `OpenNew`, after `_writable = WritableFields();` add `ReadPickers();`; `CheckAll` gains two lines:

```csharp
    private void CheckAll()
    {
        _ = CheckConditionAsync();
        CheckMutateValues();
        _ = CheckPayloadAsync();
        _ = CheckToAsync();
    }
```

  In `HooksTab.Edit.cs` `OpenEdit`, after `_writable = WritableFields();` add `ReadPickers();`.

- [ ] **Step 6: Run, normalise, run again** — `dotnet build`; `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*PatternLanguageTests' --filter-class '*FieldConventionTests'` (the payload box is multi-line inside an `AlvoEditor` and the file holds `ChordHint.Of(`); `scripts/test-ring0`; `scripts/test-admin-e2e --filter-class '*HookPickerScenarios'`, `--filter-class '*UndeclaredReferenceScenarios'`, `--filter-class '*HookEditInPlaceScenarios'`, `--filter-class '*RefusalPlacementScenarios'` — all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Edit.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Pickers.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/HookPickerScenarios.cs
git commit -m "feat(admin): pick a declared endpoint or template, and write a payload the build judges as you type

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---
### Task 11: the declaration rules and the statement

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Integrations/EndpointDraft.cs`, `TemplateDraft.cs`, `IntegrationStatement.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Integrations/EndpointDraftTests.cs`, `test/MMLib.Alvo.Admin.Tests/Integrations/TemplateDraftTests.cs`

**Interfaces:**
- Consumes: `SecretName.TryParse(string?, out SecretName?)` (`MMLib.Alvo.Secrets`, Abstractions, `SecretName.cs:48`).
- Produces (namespace `MMLib.Alvo.Admin.Components.Integrations`, all `internal`):
  - `sealed partial class EndpointDraft` — consts `NameField = "endpoint-name"`, `UrlField = "endpoint-url"`, `SecretField = "endpoint-secret"`; properties `Name`, `Url`, `SecretRef`, `Description`, `SecretTouched`; `static EndpointDraft From(string name, JsonElement declared)`; `void TypeName(string)`, `void TypeSecret(string)`; `IReadOnlyList<KeyValuePair<string, string>> Refusals(IReadOnlyCollection<string> declared, bool editing)`; `static string? NameRefusal(string, IReadOnlyCollection<string>)`, `static string? UrlRefusal(string)`, `static bool IsDeliverable(Uri)`, `static string? SecretRefusal(string)`.
  - `sealed partial class TemplateDraft` — consts `NameField = "template-name"`, `SubjectField = "template-subject"`, `BodyField = "template-body"`; `static IReadOnlyList<string> Roots` (`new`, `old`, `event`, `@user`); properties `Name`, `Subject`, `Body`; `static TemplateDraft From(string, JsonElement)`; `Refusals(IReadOnlyCollection<string> declared, bool editing)`; `static string? NameRefusal(...)`, `SubjectRefusal(string)`, `BodyRefusal(string)`, `PlaceholderRefusal(string)`.
  - `static class IntegrationStatement` — consts `Title`, `HiddenFields`, `PrivateDestinations`, `NoRedelivery`, `SecretHint`, `AllowedNetworksSetting = "Alvo:Events:WebhookAllowedNetworks"` (spec §6.3).

- [ ] **Step 1: Write the failing tests**

```csharp
using MMLib.Alvo.Admin.Components.Integrations;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Integrations;

/// <summary>What the endpoint sheet refuses at its fields — the apply's rules, said where they can be acted on (spec §6.1).</summary>
public class EndpointDraftTests
{
    [Theory]
    [InlineData("billing-system", null)]
    [InlineData("a", null)]
    [InlineData("Billing", "not an endpoint name")]
    [InlineData("billing_system", "not an endpoint name")]
    [InlineData("9lives", "not an endpoint name")]
    [InlineData("rental-desk", "already declared")]
    [InlineData("čaj", "not an endpoint name")]
    public void A_name_is_the_schemas_endpoint_key_and_unique(string name, string? refused)
    {
        var refusal = EndpointDraft.NameRefusal(name, ["rental-desk"]);

        if (refused is null)
        {
            refusal.ShouldBeNull();
        }
        else
        {
            refusal.ShouldNotBeNull().ShouldContain(refused);
        }
    }

    [Theory]
    [InlineData("https://billing.example/hooks", true)]
    [InlineData("http://localhost:5081/hooks", true)]
    [InlineData("http://127.0.0.1:5081/hooks/rentals", true)]
    [InlineData("http://[::1]:5081/hooks", true)]
    [InlineData("http://example.com/hooks", false)]
    [InlineData("ftp://example.com/x", false)]
    [InlineData("/hooks/relative", false)]
    [InlineData("htp://x", false)]
    [InlineData("", false)]
    public void A_url_is_accepted_exactly_as_the_apply_accepts_it(string url, bool accepted)
        => (EndpointDraft.UrlRefusal(url) is null).ShouldBe(accepted);

    [Fact]
    public void A_cleartext_url_is_refused_with_the_reason_and_without_echoing_the_url()
    {
        var refusal = EndpointDraft.UrlRefusal("http://example.com/hooks?token=s3cr3t");

        refusal.ShouldNotBeNull().ShouldContain("https");
        refusal.ShouldNotContain("s3cr3t", Case.Sensitive, "a URL can be its own bearer secret (events.md)");
    }

    [Theory]
    [InlineData("rental-desk-signing-key", true)]
    [InlineData("billing.key", true)]
    [InlineData("Sup3r+Secret/Value==", false)]
    [InlineData("", false)]
    [InlineData("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", false)]
    public void A_secret_name_follows_the_stores_own_name_rule(string secret, bool accepted)
        => (EndpointDraft.SecretRefusal(secret) is null).ShouldBe(accepted);

    [Fact]
    public void A_refused_secret_name_never_echoes_what_was_pasted()
        => EndpointDraft.SecretRefusal("Sup3r+Secret/Value==").ShouldNotBeNull().ShouldNotContain("Sup3r");

    [Fact]
    public void The_secret_name_follows_the_endpoint_name_until_it_is_typed()
    {
        var draft = new EndpointDraft();

        draft.TypeName("billing");
        draft.SecretRef.ShouldBe("billing-signing-key");
        draft.TypeSecret("vault.billing");
        draft.TypeName("billing-system");
        draft.SecretRef.ShouldBe("vault.billing");
    }

    [Fact]
    public void Every_refusal_names_the_field_it_belongs_to_in_the_order_the_sheet_draws_them()
    {
        var draft = new EndpointDraft { Name = "Bad", Url = "http://example.com", SecretRef = "Bad Secret" };

        draft.Refusals(["rental-desk"], editing: false).Select(refusal => refusal.Key)
            .ShouldBe([EndpointDraft.NameField, EndpointDraft.UrlField, EndpointDraft.SecretField]);
    }

    [Fact]
    public void An_edit_does_not_refuse_its_own_name()
    {
        var draft = EndpointDraft.From("rental-desk", JsonDocument.Parse("""{"url":"http://127.0.0.1:5081/h","secretRef":"rental-desk-signing-key"}""").RootElement);

        draft.Refusals(["rental-desk"], editing: true).ShouldBeEmpty();
        (draft.Url, draft.SecretRef, draft.Description).ShouldBe(("http://127.0.0.1:5081/h", "rental-desk-signing-key", string.Empty));
    }
}
```

```csharp
using MMLib.Alvo.Admin.Components.Integrations;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Integrations;

/// <summary>What the template sheet refuses at its fields (spec §6.2).</summary>
public class TemplateDraftTests
{
    [Theory]
    [InlineData("order-ready", null)]
    [InlineData("order_ready", null)]
    [InlineData("Order", "not a template name")]
    [InlineData("order-ready-2", null)]
    [InlineData("taken", "already declared")]
    public void A_name_is_an_identifier_and_unique(string name, string? refused)
    {
        var refusal = TemplateDraft.NameRefusal(name, ["taken"]);

        if (refused is null)
        {
            refusal.ShouldBeNull();
        }
        else
        {
            refusal.ShouldNotBeNull().ShouldContain(refused);
        }
    }

    [Theory]
    [InlineData("Your bike is ready — order {{new.order_number}}", true)]
    [InlineData("Tabs\tare one line", true)]
    [InlineData("Two\nlines", false)]
    [InlineData("Two\r\nlines", false)]
    [InlineData("Line\u2028separator", false)]
    [InlineData("Bell\u0007", false)]
    public void A_subject_is_one_line(string subject, bool accepted)
        => (TemplateDraft.SubjectRefusal(subject) is null).ShouldBe(accepted);

    [Theory]
    [InlineData("Hello {{new.contact_email}}, {{old.status}} {{event.type}} {{@user.id}}", true)]
    [InlineData("Hi {{@tenant.id}}", false)]
    [InlineData("Hi {{ @tenant.id }}", false)]
    [InlineData("Roles {{@user.roles}}", false)]
    [InlineData("Hi {{customer.name}}", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void A_body_is_required_and_names_only_roots_an_email_can_resolve(string body, bool accepted)
        => (TemplateDraft.BodyRefusal(body) is null).ShouldBe(accepted);

    [Fact]
    public void A_tenant_placeholder_is_refused_with_the_reason()
        => TemplateDraft.BodyRefusal("Hi {{@tenant.id}}").ShouldNotBeNull().ShouldContain("carries no tenant");

    [Fact]
    public void An_edit_loads_the_subject_and_body_and_does_not_refuse_its_own_name()
    {
        var draft = TemplateDraft.From("order-ready", JsonDocument.Parse("""{"subject":"Ready","body":"Hello"}""").RootElement);

        (draft.Subject, draft.Body).ShouldBe(("Ready", "Hello"));
        draft.Refusals(["order-ready"], editing: true).ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*EndpointDraftTests' --filter-class '*TemplateDraftTests'`. Expected: build FAIL.

- [ ] **Step 3: Implement the three files**

`EndpointDraft.cs`:

```csharp
using MMLib.Alvo.Secrets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>What the endpoint sheet holds, and what it refuses at a field before anything is written (spec §6.1).</summary>
/// <remarks>
/// <para>
/// <b>The URL rule is the apply's, by the same calls</b> — <c>Uri.TryCreate(…, Absolute)</c>, then <c>https</c>, or
/// <c>http</c> with <c>Uri.IsLoopback</c> (<c>AfterHookCompiler.ResolveTarget</c>/<c>IsDeliverable</c>); Host.Tests holds the
/// two to one answer. Stricter in time, stated in the hint: the apply checks an endpoint's URL only once a hook posts to it.
/// </para>
/// <para>
/// <b>The secret is a name, checked as one</b> (<see cref="SecretName"/>): the schema requires <c>secretRef</c> and this build
/// never reads it. A refusal never echoes what was pasted, which may have been the secret itself; nor does a URL refusal
/// echo the URL, which can be its own bearer token.
/// </para>
/// </remarks>
internal sealed partial class EndpointDraft
{
    /// <summary>The name input's id.</summary>
    public const string NameField = "endpoint-name";

    /// <summary>The URL input's id.</summary>
    public const string UrlField = "endpoint-url";

    /// <summary>The secret name input's id.</summary>
    public const string SecretField = "endpoint-secret";

    /// <summary>Gets or sets the endpoint's name — its key under <c>webhooks.endpoints</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL deliveries are posted to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the name the signing secret will be stored under.</summary>
    public string SecretRef { get; set; } = string.Empty;

    /// <summary>Gets or sets the description, which nothing in this build reads.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the secret name was typed, so the name no longer suggests it.</summary>
    public bool SecretTouched { get; set; }

    /// <summary>A draft holding a declared endpoint.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="declared">Its declaration.</param>
    public static EndpointDraft From(string name, JsonElement declared) => new()
    {
        Name = name,
        Url = Text(declared, "url"),
        SecretRef = Text(declared, "secretRef"),
        Description = Text(declared, "description"),
        SecretTouched = true,
    };

    /// <summary>The name, typed — and the suggested secret name with it, until that is typed itself.</summary>
    /// <param name="name">What was typed.</param>
    public void TypeName(string name)
    {
        Name = name;
        if (!SecretTouched)
        {
            SecretRef = name.Length > 0 ? $"{name}-signing-key" : string.Empty;
        }
    }

    /// <summary>The secret name, typed.</summary>
    /// <param name="secret">What was typed.</param>
    public void TypeSecret(string secret)
    {
        SecretRef = secret;
        SecretTouched = true;
    }

    /// <summary>Each field's refusal, in the order the sheet draws the fields.</summary>
    /// <param name="declared">The endpoint names the working copy declares.</param>
    /// <param name="editing">Whether an existing endpoint is edited, whose name is fixed.</param>
    public IReadOnlyList<KeyValuePair<string, string>> Refusals(IReadOnlyCollection<string> declared, bool editing)
    {
        var refusals = new List<KeyValuePair<string, string>>();
        Add(refusals, NameField, editing ? null : NameRefusal(Name.Trim(), declared));
        Add(refusals, UrlField, UrlRefusal(Url.Trim()));
        Add(refusals, SecretField, SecretRefusal(SecretRef.Trim()));
        return refusals;
    }

    /// <summary>Why a name cannot be an endpoint's key, or <see langword="null"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="declared">The names already declared.</param>
    public static string? NameRefusal(string name, IReadOnlyCollection<string> declared)
    {
        if (!EndpointName().IsMatch(name))
        {
            return $"'{name}' is not an endpoint name. A name is lower case, starts with a letter, and holds letters, digits "
                + "and dashes — up to 63 characters, such as 'billing-system'.";
        }

        return declared.Contains(name, StringComparer.Ordinal)
            ? $"An endpoint named '{name}' is already declared. Edit it, or choose another name."
            : null;
    }

    /// <summary>Why the apply would refuse this URL once a hook posts to it, or <see langword="null"/>.</summary>
    /// <param name="url">The URL.</param>
    public static string? UrlRefusal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return "That is not an absolute URL, so no delivery could ever be attempted. Give one such as 'https://example.com/hooks/alvo'.";
        }

        return IsDeliverable(parsed)
            ? null
            : $"This URL uses '{parsed.Scheme}'. A delivery carries the record's complete image, unsigned, so it must be 'https' — "
                + "'http' is accepted only for a loopback host (localhost, 127.0.0.1, [::1]).";
    }

    /// <summary>The apply's own test: <c>https</c>, or <c>http</c> to a loopback host.</summary>
    /// <param name="url">An absolute URL.</param>
    public static bool IsDeliverable(Uri url)
        => string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
           || (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && url.IsLoopback);

    /// <summary>Why this cannot be a secret's name, or <see langword="null"/> — never repeating what was typed.</summary>
    /// <param name="secret">The secret name.</param>
    public static string? SecretRefusal(string secret)
        => SecretName.TryParse(secret, out _)
            ? null
            : "That is not a secret name. Write the name the signing secret will be stored under — lower case, starting with "
                + "a letter, such as 'billing-signing-key' — never the secret itself.";

    private static void Add(List<KeyValuePair<string, string>> refusals, string field, string? refusal)
    {
        if (refusal is not null)
        {
            refusals.Add(new KeyValuePair<string, string>(field, refusal));
        }
    }

    private static string Text(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointName();
}
```

`TemplateDraft.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>What the template sheet holds, and what it refuses at a field (spec §6.2).</summary>
/// <remarks>
/// Subject and body only: <c>bodyFile</c> is refused by this build and has no control here. A placeholder's field is judged
/// on apply, against the entity of each email hook that sends the template; what is refused here is what no entity changes —
/// a line break in the subject header, a root an event envelope cannot answer.
/// </remarks>
internal sealed partial class TemplateDraft
{
    /// <summary>The name input's id.</summary>
    public const string NameField = "template-name";

    /// <summary>The subject input's id.</summary>
    public const string SubjectField = "template-subject";

    /// <summary>The body input's id.</summary>
    public const string BodyField = "template-body";

    /// <summary>The placeholder roots an email can resolve — <c>TemplatePlaceholder.Roots</c>, pinned by Host.Tests.</summary>
    public static IReadOnlyList<string> Roots { get; } = ["new", "old", "event", "@user"];

    /// <summary>Gets or sets the template's name — its key under <c>templates</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the subject line.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the body.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>A draft holding a declared template.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="declared">Its declaration.</param>
    public static TemplateDraft From(string name, JsonElement declared) => new()
    {
        Name = name,
        Subject = Text(declared, "subject"),
        Body = Text(declared, "body"),
    };

    /// <summary>Each field's refusal, in the order the sheet draws the fields.</summary>
    /// <param name="declared">The template names the working copy declares.</param>
    /// <param name="editing">Whether an existing template is edited, whose name is fixed.</param>
    public IReadOnlyList<KeyValuePair<string, string>> Refusals(IReadOnlyCollection<string> declared, bool editing)
    {
        var refusals = new List<KeyValuePair<string, string>>();
        Add(refusals, NameField, editing ? null : NameRefusal(Name.Trim(), declared));
        Add(refusals, SubjectField, SubjectRefusal(Subject));
        Add(refusals, BodyField, BodyRefusal(Body));
        return refusals;
    }

    /// <summary>Why a name cannot be a template's key, or <see langword="null"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="declared">The names already declared.</param>
    public static string? NameRefusal(string name, IReadOnlyCollection<string> declared)
    {
        if (!Identifier().IsMatch(name))
        {
            return $"'{name}' is not a template name. A name is lower case, starts with a letter, and holds letters, digits, "
                + "dashes and underscores — up to 63 characters, such as 'order-ready'.";
        }

        return declared.Contains(name, StringComparer.Ordinal)
            ? $"A template named '{name}' is already declared. Edit it, or choose another name."
            : null;
    }

    /// <summary>Why a subject cannot be sent, or <see langword="null"/>: it is one header line.</summary>
    /// <param name="subject">The subject.</param>
    public static string? SubjectRefusal(string subject)
        => subject.Any(BreaksALine)
            ? "A subject is one line: a line break or another control character in a mail header is header injection, and the build refuses it."
            : PlaceholderRefusal(subject);

    /// <summary>Why a body cannot be sent, or <see langword="null"/>.</summary>
    /// <param name="body">The body.</param>
    public static string? BodyRefusal(string body)
        => string.IsNullOrWhiteSpace(body)
            ? "A template carries a body. Write one — this sheet does not offer 'bodyFile', which this build does not read."
            : PlaceholderRefusal(body);

    /// <summary>The first placeholder whose root an email cannot resolve, said, or <see langword="null"/>.</summary>
    /// <param name="text">A subject or a body.</param>
    public static string? PlaceholderRefusal(string text)
    {
        foreach (Match match in Placeholder().Matches(text))
        {
            var inner = match.Groups[1].Value.Trim();
            if (inner.StartsWith("@tenant", StringComparison.Ordinal) || inner == "@user.roles")
            {
                return $"'{{{{{inner}}}}}' cannot be answered by an email: the event it is sent from carries no tenant and no roles. "
                    + "Read a field of the row instead, such as new.tenant_id.";
            }

            if (!Roots.Contains(inner.Split('.', 2)[0], StringComparer.Ordinal))
            {
                return $"'{{{{{inner}}}}}' names no root an email can resolve. Available roots: {string.Join(", ", Roots)}.";
            }
        }

        return null;
    }

    /// <summary><c>MailHeaders.BreaksALine</c>'s test: a control character but tab, or a line or paragraph separator.</summary>
    private static bool BreaksALine(char character)
        => (char.IsControl(character) && character != '\t')
           || char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static void Add(List<KeyValuePair<string, string>> refusals, string field, string? refusal)
    {
        if (refusal is not null)
        {
            refusals.Add(new KeyValuePair<string, string>(field, refusal));
        }
    }

    private static string Text(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
```

`IntegrationStatement.cs`:

```csharp
namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>
/// The endpoint sheet's statement (spec §6.3): the facts an operator must read before a destination receives a record.
/// </summary>
/// <remarks>
/// <para>
/// <b>The build's own sentence comes first, verbatim</b> — <c>capabilities.warned</c>'s <c>webhooks</c> consequence, which
/// says deliveries are unsigned and unprojected. These four are the facts that sentence does not carry, so this dashboard
/// says them (a recorded deviation from ruling B6, spec §3): the build publishes nothing for the egress guard, the explicit
/// <c>hidden</c> disclosure or the missing dead-letter queue. <c>HooksEditorAgreementTests</c> pins each claim to the core it
/// describes, and a follow-up moves them into <c>capabilities</c>.
/// </para>
/// <para>Sources: <c>docs/architecture/events.md</c> ("the unmasked record", "the attempt ceiling is the DLQ stand-in");
/// <c>AlvoEventOptions.WebhookAllowedNetworks</c>; <c>WebhookEgressGuard</c>.</para>
/// </remarks>
internal static class IntegrationStatement
{
    /// <summary>The host setting that admits a non-public network — the only one, and not the dashboard's.</summary>
    public const string AllowedNetworksSetting = "Alvo:Events:WebhookAllowedNetworks";

    /// <summary>The statement's title.</summary>
    public const string Title = "Deliveries to this endpoint are not signed, and carry the whole row";

    /// <summary>The disclosure #152 closes.</summary>
    public const string HiddenFields = "Each delivery carries the record's complete image — fields declared 'hidden' included.";

    /// <summary>Where the egress guard acts, and who can lift it.</summary>
    public const string PrivateDestinations =
        "A destination on a private, loopback, link-local or otherwise non-public network is accepted here and on apply, and "
        + "refused on every delivery: the check runs when the connection is made, on the address the name resolves to. A "
        + "loopback address passes only when the URL names it (localhost, 127.0.0.1, [::1]). Only the host's configuration, "
        + AllowedNetworksSetting + ", admits another network — this dashboard cannot.";

    /// <summary>What happens to a delivery that keeps failing.</summary>
    public const string NoRedelivery =
        "A delivery that keeps failing is retried up to the attempt ceiling and then abandoned; there is no dead-letter queue "
        + "and no redelivery screen yet.";

    /// <summary>The secret name field's hint.</summary>
    public const string SecretHint = "The name the signing secret will be stored under, never the secret itself. This build does not read it.";
}
```

- [ ] **Step 4: Run, normalise, run again** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*EndpointDraftTests' --filter-class '*TemplateDraftTests'`. Expected: PASS. (`whsec_…` fails `SecretName`'s rule on its upper-case letters; if the store's rule ever admits it, the test is right to fail — read `SecretName.cs:35` and change the case, not the rule.) `scripts/test-ring0`.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Integrations/EndpointDraft.cs src/MMLib.Alvo.Admin/Components/Integrations/TemplateDraft.cs src/MMLib.Alvo.Admin/Components/Integrations/IntegrationStatement.cs test/MMLib.Alvo.Admin.Tests/Integrations/EndpointDraftTests.cs test/MMLib.Alvo.Admin.Tests/Integrations/TemplateDraftTests.cs
git commit -m "feat(admin): the rules an endpoint and a template are declared by, and the statement an endpoint is declared under

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 12: writing `webhooks` and `templates`, and the rows Integrations lists

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Webhooks.cs`, `src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Templates.cs`
- Create: `src/MMLib.Alvo.Admin/Components/Integrations/IntegrationRows.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyIntegrationTests.cs`, `test/MMLib.Alvo.Admin.Tests/Integrations/IntegrationRowsTests.cs`

**Interfaces:**
- Consumes: `WorkingCopy.Edit`, `WorkingCopy.Ensure` (`WorkingCopy.cs:370`, `:445-461`); `DescriptorLens.Endpoints/Templates/IntegrationUses` (Task 2).
- Produces:
  - `public bool WorkingCopy.DeclareEndpoint(string name, string url, string secretRef, string? description, bool editing)`
  - `public bool WorkingCopy.DeclareTemplate(string name, string? subject, string body, bool editing)`
  - `internal sealed record EndpointRow(string Name, string Url, string SecretRef, bool Staged, bool Drawable, IReadOnlyList<DescriptorLens.IntegrationUse> Uses)`
  - `internal sealed record TemplateRow(string Name, string Subject, bool HasBodyFile, bool Staged, bool Drawable, IReadOnlyList<DescriptorLens.IntegrationUse> Uses)`
  - `internal static class IntegrationRows { Endpoints(string workingJson, string appliedJson); Templates(string workingJson, string appliedJson); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>Declaring an endpoint or a template in the working copy (spec §5.5).</summary>
public class WorkingCopyIntegrationTests
{
    [Fact]
    public void A_new_endpoint_creates_its_blocks_and_is_written_in_the_schemas_order()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {} }""");

        copy.DeclareEndpoint("billing", "https://billing.example/hooks", "billing-signing-key", "Invoices", editing: false).ShouldBeTrue();

        Node(copy)["webhooks"]!["endpoints"]!["billing"]!.ToJsonString()
            .ShouldBe("""{"url":"https://billing.example/hooks","secretRef":"billing-signing-key","description":"Invoices"}""");
    }

    [Fact]
    public void A_new_endpoint_with_a_name_already_declared_is_refused_and_changes_nothing()
    {
        var copy = Copy(WithEndpoint);
        var before = copy.Json;

        copy.DeclareEndpoint("rental-desk", "https://x.example", "k", null, editing: false).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void An_edit_patches_the_endpoint_in_place_and_keeps_what_it_does_not_draw()
    {
        var copy = Copy(WithEndpoint);

        copy.DeclareEndpoint("rental-desk", "https://desk.example/h", "desk-key", null, editing: true).ShouldBeTrue();

        Node(copy)["webhooks"]!["endpoints"]!["rental-desk"]!.ToJsonString()
            .ShouldBe("""{"url":"https://desk.example/h","secretRef":"desk-key","x-owner":"ops"}""");
    }

    [Fact]
    public void Editing_an_endpoint_nothing_declares_is_refused_and_creates_nothing()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {} }""");
        var before = copy.Json;

        copy.DeclareEndpoint("ghost", "https://x.example", "k", null, editing: true).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Theory]
    [InlineData("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {}, "webhooks": [] }""")]
    [InlineData("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {}, "webhooks": { "endpoints": "x" } }""")]
    public void A_block_of_the_wrong_shape_is_left_for_the_apply_to_refuse(string json)
    {
        var copy = Copy(json);
        var before = copy.Json;

        copy.DeclareEndpoint("billing", "https://x.example", "k", null, editing: false).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void A_new_template_is_subject_then_body_and_a_blank_subject_is_left_out()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {} }""");

        copy.DeclareTemplate("ready", "Ready {{new.order_number}}", "Hello", editing: false).ShouldBeTrue();
        copy.DeclareTemplate("plain", "  ", "Hello", editing: false).ShouldBeTrue();

        Node(copy)["templates"]!.ToJsonString(Relaxed.Options)
            .ShouldBe("""{"ready":{"subject":"Ready {{new.order_number}}","body":"Hello"},"plain":{"body":"Hello"}}""");
    }

    [Fact]
    public void A_template_that_reads_a_body_file_is_not_edited()
    {
        var copy = Copy("""{ "apiVersion": "alvo.dev/v1", "name": "x", "entities": {}, "templates": { "legacy": { "bodyFile": "a.md" } } }""");
        var before = copy.Json;

        copy.DeclareTemplate("legacy", null, "Inline now", editing: true).ShouldBeFalse();

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void A_declaration_is_a_pending_edit()
    {
        var copy = Copy(WithEndpoint);

        copy.DeclareTemplate("ready", null, "Hello", editing: false).ShouldBeTrue();

        copy.PendingCount.ShouldBeGreaterThan(0);
    }

    private const string WithEndpoint = """
        { "apiVersion": "alvo.dev/v1", "name": "x", "entities": {},
          "webhooks": { "endpoints": { "rental-desk": { "url": "http://127.0.0.1:5081/h", "secretRef": "rental-desk-signing-key", "x-owner": "ops" } } } }
        """;

    private static WorkingCopy Copy(string json)
    {
        var copy = new WorkingCopy();
        copy.Take(json, revision: 1);
        return copy;
    }

    private static JsonNode Node(WorkingCopy copy) => JsonNode.Parse(copy.Json)!;
}
```

Note the `x-owner` key: the schema's `additionalProperties: false` refuses it at apply, which is exactly why the writer must keep it rather than decide (the apply decides). (`IntegrationRows` marks such an endpoint not drawable, so the sheet never opens it — this test pins the writer alone.)

```csharp
using MMLib.Alvo.Admin.Components.Integrations;

namespace MMLib.Alvo.Admin.Tests.Integrations;

/// <summary>The rows Integrations lists from the working copy (spec §4.7).</summary>
public class IntegrationRowsTests
{
    private const string Applied = """
        { "webhooks": { "endpoints": {
            "rental-desk": { "url": "http://127.0.0.1:5081/h", "secretRef": "rental-desk-signing-key" },
            "old-desk": { "url": "https://old.example", "secretRef": "old-key" } } },
          "templates": { "order-ready": { "subject": "Ready", "body": "Hello" } },
          "entities": {} }
        """;

    private const string Working = """
        { "webhooks": { "endpoints": {
            "rental-desk": { "url": "http://127.0.0.1:5081/h", "secretRef": "rental-desk-signing-key" },
            "old-desk": { "url": "https://changed.example", "secretRef": "old-key" },
            "billing": { "url": "https://billing.example", "secretRef": "billing-key", "x-owner": "ops" } } },
          "templates": {
            "order-ready": { "subject": "Ready", "body": "Hello" },
            "legacy": { "bodyFile": "legacy.md" } },
          "entities": { "rentals": { "fields": {}, "hooks": { "afterCreate": [ { "action": { "type": "webhook", "endpoint": "rental-desk" } } ] } } } }
        """;

    [Fact]
    public void An_endpoint_is_staged_when_the_applied_revision_lacks_it_or_holds_it_otherwise()
        => IntegrationRows.Endpoints(Working, Applied).Select(row => (row.Name, row.Staged))
            .ShouldBe([("rental-desk", false), ("old-desk", true), ("billing", true)]);

    [Fact]
    public void An_endpoint_with_a_key_the_sheet_does_not_draw_is_not_drawable()
        => IntegrationRows.Endpoints(Working, Applied).Single(row => row.Name == "billing").Drawable.ShouldBeFalse();

    [Fact]
    public void An_endpoint_carries_the_hooks_that_post_to_it()
    {
        var desk = IntegrationRows.Endpoints(Working, Applied).Single(row => row.Name == "rental-desk");

        desk.Uses.ShouldHaveSingleItem().Entity.ShouldBe("rentals");
        (desk.Url, desk.SecretRef, desk.Drawable).ShouldBe(("http://127.0.0.1:5081/h", "rental-desk-signing-key", true));
    }

    [Fact]
    public void A_template_that_reads_a_body_file_is_flagged_and_not_drawable()
    {
        var legacy = IntegrationRows.Templates(Working, Applied).Single(row => row.Name == "legacy");

        (legacy.HasBodyFile, legacy.Drawable, legacy.Staged).ShouldBe((true, false, true));
    }

    [Fact]
    public void An_applied_template_unchanged_is_not_staged()
        => IntegrationRows.Templates(Working, Applied).Single(row => row.Name == "order-ready").Staged.ShouldBeFalse();

    [Fact]
    public void Text_that_is_not_a_descriptor_lists_nothing()
    {
        IntegrationRows.Endpoints("nope", "{}").ShouldBeEmpty();
        IntegrationRows.Templates("nope", "{}").ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*WorkingCopyIntegrationTests' --filter-class '*IntegrationRowsTests'`. Expected: build FAIL.

- [ ] **Step 3: Implement**

`WorkingCopy.Webhooks.cs`:

```csharp
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The webhooks block: declaring and editing an endpoint, through Edit like every other block. */
internal sealed partial class WorkingCopy
{
    /// <summary>
    /// Declares a webhook endpoint, or edits a declared one in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Re-checked under the gate</b> (spec §5.5): a new name another tab declared meanwhile, or an edited one it removed,
    /// is refused rather than overwritten or created. A block of the wrong shape (<c>webhooks</c> or <c>endpoints</c> not an
    /// object) is left for the apply to refuse; replacing it would silently drop what the author wrote.
    /// </para>
    /// <para>
    /// An edit patches the existing object, so a key the sheet does not draw stays; a new one is written in the schema's
    /// order — <c>url</c>, <c>secretRef</c>, <c>description</c>.
    /// </para>
    /// </remarks>
    /// <param name="name">The endpoint's key.</param>
    /// <param name="url">The URL deliveries are posted to.</param>
    /// <param name="secretRef">The name the signing secret will be stored under — never a secret.</param>
    /// <param name="description">The description, or blank for none.</param>
    /// <param name="editing">Whether an existing endpoint is edited.</param>
    /// <returns><see langword="true"/> when it was written.</returns>
    public bool DeclareEndpoint(string name, string url, string secretRef, string? description, bool editing) => Edit(root =>
    {
        if (!Settable(root["webhooks"]) || (root["webhooks"] is JsonObject webhooks && !Settable(webhooks["endpoints"])))
        {
            return false;
        }

        var current = (root["webhooks"]?["endpoints"] as JsonObject)?[name];
        if ((current is not null) != editing || (editing && current is not JsonObject))
        {
            return false;
        }

        var endpoint = current as JsonObject ?? new JsonObject();
        endpoint["url"] = url;
        endpoint["secretRef"] = secretRef;
        SetOrRemove(endpoint, "description", description);
        if (!editing)
        {
            Ensure(Ensure(root, "webhooks"), "endpoints")[name] = endpoint;
        }

        return true;
    });

    /// <summary>Whether a block may be written into: absent, or an object.</summary>
    private static bool Settable(JsonNode? node) => node is null or JsonObject;

    /// <summary>Sets a text key, or removes it when the text is blank.</summary>
    private static void SetOrRemove(JsonObject owner, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            owner.Remove(key);
        }
        else
        {
            owner[key] = value;
        }
    }
}
```

`WorkingCopy.Templates.cs`:

```csharp
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The templates block: declaring and editing a message template, through Edit like every other block. */
internal sealed partial class WorkingCopy
{
    /// <summary>
    /// Declares a message template, or edits a declared one in place.
    /// </summary>
    /// <remarks>
    /// Re-checked under the gate as <see cref="DeclareEndpoint"/> is. A template that reads a <c>bodyFile</c> is never edited
    /// here: the sheet offers no <c>bodyFile</c>, and writing a <c>body</c> beside one would leave two bodies to disagree.
    /// </remarks>
    /// <param name="name">The template's key.</param>
    /// <param name="subject">The subject, or blank for none.</param>
    /// <param name="body">The body.</param>
    /// <param name="editing">Whether an existing template is edited.</param>
    /// <returns><see langword="true"/> when it was written.</returns>
    public bool DeclareTemplate(string name, string? subject, string body, bool editing) => Edit(root =>
    {
        if (!Settable(root["templates"]))
        {
            return false;
        }

        var current = (root["templates"] as JsonObject)?[name];
        if ((current is not null) != editing
            || (editing && current is not JsonObject)
            || (current as JsonObject)?.ContainsKey("bodyFile") == true)
        {
            return false;
        }

        var template = current as JsonObject ?? new JsonObject();
        SetOrRemove(template, "subject", subject);
        template["body"] = body;
        if (!editing)
        {
            Ensure(root, "templates")[name] = template;
        }

        return true;
    });
}
```

`IntegrationRows.cs`:

```csharp
using MMLib.Alvo.Admin.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Integrations;

/// <summary>One declared endpoint, as Integrations lists it.</summary>
/// <param name="Name">Its key.</param>
/// <param name="Url">Its URL.</param>
/// <param name="SecretRef">Its secret's name.</param>
/// <param name="Staged">Whether the applied revision lacks it, or holds it otherwise.</param>
/// <param name="Drawable">Whether the sheet can open it: only the three keys it draws, each a string.</param>
/// <param name="Uses">The hooks that post to it.</param>
internal sealed record EndpointRow(
    string Name, string Url, string SecretRef, bool Staged, bool Drawable, IReadOnlyList<DescriptorLens.IntegrationUse> Uses);

/// <summary>One declared template, as Integrations lists it.</summary>
/// <param name="Name">Its key.</param>
/// <param name="Subject">Its subject, or empty.</param>
/// <param name="HasBodyFile">Whether it reads a <c>bodyFile</c>, which this build refuses to send.</param>
/// <param name="Staged">Whether the applied revision lacks it, or holds it otherwise.</param>
/// <param name="Drawable">Whether the sheet can open it: subject and body only, a body present.</param>
/// <param name="Uses">The hooks that send it.</param>
internal sealed record TemplateRow(
    string Name, string Subject, bool HasBodyFile, bool Staged, bool Drawable, IReadOnlyList<DescriptorLens.IntegrationUse> Uses);

/// <summary>The working copy's endpoints and templates as Integrations lists them (spec §4.7, ruling B5).</summary>
internal static class IntegrationRows
{
    private static readonly HashSet<string> _endpointKeys = new(StringComparer.Ordinal) { "url", "secretRef", "description" };
    private static readonly HashSet<string> _templateKeys = new(StringComparer.Ordinal) { "subject", "body" };

    /// <summary>The endpoints, in the working copy's order.</summary>
    /// <param name="workingJson">The working copy's text.</param>
    /// <param name="appliedJson">The applied revision's text.</param>
    public static IReadOnlyList<EndpointRow> Endpoints(string workingJson, string appliedJson)
    {
        var applied = ByName(DescriptorLens.Endpoints(appliedJson));
        var uses = DescriptorLens.IntegrationUses(workingJson);
        return [.. DescriptorLens.Endpoints(workingJson).Select(pair => new EndpointRow(
            pair.Key,
            Text(pair.Value, "url"),
            Text(pair.Value, "secretRef"),
            IsStaged(pair, applied),
            Drawable(pair.Value, _endpointKeys, "url", "secretRef"),
            [.. uses.Where(use => use.Kind == "endpoint" && use.Name == pair.Key)]))];
    }

    /// <summary>The templates, in the working copy's order.</summary>
    /// <param name="workingJson">The working copy's text.</param>
    /// <param name="appliedJson">The applied revision's text.</param>
    public static IReadOnlyList<TemplateRow> Templates(string workingJson, string appliedJson)
    {
        var applied = ByName(DescriptorLens.Templates(appliedJson));
        var uses = DescriptorLens.IntegrationUses(workingJson);
        return [.. DescriptorLens.Templates(workingJson).Select(pair => new TemplateRow(
            pair.Key,
            Text(pair.Value, "subject"),
            pair.Value.ValueKind == JsonValueKind.Object && pair.Value.TryGetProperty("bodyFile", out _),
            IsStaged(pair, applied),
            Drawable(pair.Value, _templateKeys, "body"),
            [.. uses.Where(use => use.Kind == "template" && use.Name == pair.Key)]))];
    }

    private static Dictionary<string, JsonElement> ByName(IReadOnlyList<KeyValuePair<string, JsonElement>> declared)
        => declared.DistinctBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static bool IsStaged(KeyValuePair<string, JsonElement> declared, Dictionary<string, JsonElement> applied)
        => !applied.TryGetValue(declared.Key, out var before) || !JsonElement.DeepEquals(before, declared.Value);

    private static bool Drawable(JsonElement declared, HashSet<string> keys, params string[] required)
        => declared.ValueKind == JsonValueKind.Object
           && declared.EnumerateObject().All(property => keys.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
           && required.All(key => declared.TryGetProperty(key, out _));

    private static string Text(JsonElement owner, string name)
        => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;
}
```

- [ ] **Step 4: Run, normalise, run again** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*WorkingCopyIntegrationTests' --filter-class '*IntegrationRowsTests'`. Expected: PASS. `scripts/test-ring0`.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Webhooks.cs src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Templates.cs src/MMLib.Alvo.Admin/Components/Integrations/IntegrationRows.cs test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyIntegrationTests.cs test/MMLib.Alvo.Admin.Tests/Integrations/IntegrationRowsTests.cs
git commit -m "feat(admin): declare an endpoint or a template in the working copy, and list them as staged or applied

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 13: Integrations reads the working copy, and declares endpoints

**Files:**
- Modify (full replacement): `src/MMLib.Alvo.Admin/Components/Integrations/Integrations.razor`
- Create: `src/MMLib.Alvo.Admin/Components/Integrations/EndpointEditor.razor`
- Modify: `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt` (regenerated; see Step 6)
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/IntegrationsScenarios.cs`

**Interfaces:**
- Consumes: `AdminSession.WorkingCopyAsync/Follow`, `ManagementGateway.CapabilitiesAsync`, `IntegrationRows` (Task 12), `EndpointDraft`, `IntegrationStatement` (Task 11), `WorkingCopy.DeclareEndpoint` (Task 12), `FieldRefusals`, `RefusalPanel`, `RevealOnRender`, `StagedWords`, `RefusalPlaces`.
- Produces: public component `EndpointEditor` (`[Parameter] string? Editing`, `[Parameter] string? Warning`, `[Parameter] EventCallback<string> OnSaved`, `[Parameter] EventCallback OnClose`); `Integrations` gains `IDisposable`; its `@code` holds `_copy`, `_endpointEditor`, `Opened`, `Saved(string row)`, `CloseEditors()`, `Warned(string)`, `TemplateRowView`, which Task 14 extends. Test ids: `endpoint-new`, `endpoint-row`, `endpoint-staged`, `endpoint-uses`, `endpoint-edit`, `endpoint-editor`, `endpoint-save`, `endpoint-statement`, `endpoint-statement-build`, `endpoint-name-fixed`, `template-row`, `template-staged`; row ids `endpoint-{name}`, `template-{name}`; unchanged `integrations-refused-{slot}`.

- [ ] **Step 1: Write the failing e2e scenarios** (`IntegrationsScenarios.cs`):

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Integrations lists the working copy's endpoints and templates and declares endpoints behind the build's statement (spec
/// §4.7, §4.8, §6; rulings B5, B6). Each scenario uses its own names: the class shares one working copy.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class IntegrationsScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_screen_lists_the_working_copys_endpoints_and_templates_with_who_uses_them()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");

        var desk = await session.Page.Locator("#endpoint-rental-desk").InnerTextAsync();
        desk.ShouldContain("posted to by rentals afterCreate");
        desk.ShouldContain("not signed");
        desk.ShouldContain("rental-desk-signing-key");
        (await session.Page.Locator("#template-order-ready").InnerTextAsync()).ShouldContain("sent by service_orders afterUpdate");
        await session.Page.GetByTestId("integrations-refused-bodyFile").WaitForAsync();
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_endpoint_is_declared_under_the_statement_and_lit_in_the_list()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("endpoint-new").ClickAsync();
        var editor = session.Dialog("endpoint-editor");

        var statement = await editor.GetByTestId("endpoint-statement").InnerTextAsync();
        statement.ShouldContain("Deliveries to this endpoint are not signed, and carry the whole row");
        statement.ShouldContain("WebhookAllowedNetworks");
        (await editor.GetByTestId("endpoint-statement-build").InnerTextAsync()).ShouldContain("no delivery is signed");

        await session.Page.FillAsync("#endpoint-name", "billing-system");
        (await session.Page.InputValueAsync("#endpoint-secret")).ShouldBe("billing-system-signing-key");
        await session.Page.FillAsync("#endpoint-url", "https://billing.example/hooks/alvo");
        await editor.GetByTestId("endpoint-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Endpoint billing-system added to the working copy");
        var row = session.Page.Locator("#endpoint-billing-system");
        (await row.GetAttributeAsync("data-alvo-new")).ShouldBe("true");
        var text = await row.InnerTextAsync();
        text.ShouldContain("not applied yet");
        text.ShouldContain("no hook posts here");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_endpoint_the_build_would_refuse_is_refused_at_its_fields_without_echoing_a_secret()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("endpoint-new").ClickAsync();
        var editor = session.Dialog("endpoint-editor");

        await session.Page.FillAsync("#endpoint-name", "Billing");
        await session.Page.FillAsync("#endpoint-url", "http://example.com/hooks");
        await session.Page.FillAsync("#endpoint-secret", "Sup3r+Secret==");
        await editor.GetByTestId("endpoint-save").ClickAsync();

        await session.WaitForFocusOnAsync("endpoint-name");
        (await session.Page.Locator("#endpoint-name-problem").InnerTextAsync()).ShouldContain("not an endpoint name");
        (await session.Page.Locator("#endpoint-url-problem").InnerTextAsync()).ShouldContain("https");
        (await session.Page.Locator("#endpoint-secret-problem").InnerTextAsync()).ShouldNotContain("Sup3r");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_loopback_http_url_is_accepted_as_the_build_accepts_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("endpoint-new").ClickAsync();
        var editor = session.Dialog("endpoint-editor");

        await session.Page.FillAsync("#endpoint-name", "local-desk");
        await session.Page.FillAsync("#endpoint-url", "http://localhost:5099/hook");
        await editor.GetByTestId("endpoint-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.Page.Locator("#endpoint-local-desk").WaitForAsync();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_endpoint_is_edited_with_its_name_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.Locator("#endpoint-rental-desk [data-testid='endpoint-edit']").ClickAsync();
        var editor = session.Dialog("endpoint-editor");

        (await editor.InnerTextAsync()).ShouldContain("Edit endpoint rental-desk");
        (await editor.GetByTestId("endpoint-name-fixed").InnerTextAsync()).ShouldBe("rental-desk");
        await session.Page.FillAsync("#endpoint-description", "The rental counter's receiver");
        await editor.GetByTestId("endpoint-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Endpoint rental-desk saved to the working copy");
        (await session.Page.Locator("#endpoint-rental-desk").InnerTextAsync()).ShouldContain("not applied yet");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_endpoint_is_offered_to_a_webhook_hook_before_it_is_applied()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("endpoint-new").ClickAsync();
        var editor = session.Dialog("endpoint-editor");
        await session.Page.FillAsync("#endpoint-name", "dispatch-desk");
        await session.Page.FillAsync("#endpoint-url", "https://dispatch.example/hooks");
        await editor.GetByTestId("endpoint-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await HookPickerScenarios.NewAfterHookAsync(session, "rentals", "afterUpdate", "webhook");
        await HookPickerScenarios.Combobox(session, "Endpoint").ClickAsync();
        (await session.Page.GetByRole(AriaRole.Option, new() { Name = "dispatch-desk (not applied yet)", Exact = true }).CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_double_click_on_Add_declares_one_endpoint()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("endpoint-new").ClickAsync();
        var editor = session.Dialog("endpoint-editor");
        await session.Page.FillAsync("#endpoint-name", "twice-desk");
        await session.Page.FillAsync("#endpoint-url", "https://twice.example/hooks");

        await editor.GetByTestId("endpoint-save").DblClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#endpoint-twice-desk").CountAsync()).ShouldBe(1);
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Escape_on_a_typed_endpoint_asks_before_it_discards()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("endpoint-new").ClickAsync();
        var editor = session.Dialog("endpoint-editor");
        await session.Page.FillAsync("#endpoint-name", "unsaved-desk");

        await session.Page.Keyboard.PressAsync("Escape");
        await editor.GetByTestId("editor-discard-question").WaitForAsync();
        await editor.GetByTestId("editor-keep").ClickAsync();
        (await session.FocusIsInsideAsync("endpoint-editor")).ShouldBeTrue();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_endpoint_sheet_and_the_list_fit_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/integrations");
        await session.AssertNoHorizontalScrollAsync();

        await session.Page.GetByTestId("endpoint-new").ClickAsync();
        await session.Dialog("endpoint-editor").WaitForAsync();
        await session.AssertNoHorizontalScrollAsync();
    }
}
```

- [ ] **Step 2: Run to verify failure** — `scripts/test-admin-e2e --filter-class '*IntegrationsScenarios'`. Expected: FAIL — no `#endpoint-rental-desk`.

- [ ] **Step 3: Create `EndpointEditor.razor`**

```razor
@*
    Declares or edits one webhook endpoint in the working copy (spec §4.8, §6.1). The statement comes first, above the
    fields, in the warning tone — it is what an operator must read before a destination receives a record (§6.3): the
    build's own sentence verbatim, then the facts the build does not publish yet. The name is fixed in an edit: a hook
    names the endpoint by it.
*@
@inject ISnackbar Snackbar

<AlvoEditor TestId="endpoint-editor" Title="@(Editing is null ? "New endpoint" : $"Edit endpoint {Editing}")"
            SubmitText="@(Editing is null ? "Add to the working copy" : "Save to the working copy")" SubmitTestId="endpoint-save"
            Subtitle="It joins the working copy. A hook's webhook action names it; nothing is delivered until you apply."
            Dirty="Dirty" OnSubmit="SaveAsync" OnClose="OnClose">
    <AlvoAlert Tone="AlvoAlert.AlertTone.Warning" Title="@IntegrationStatement.Title" TestId="endpoint-statement">
        @if (Warning is { Length: > 0 })
        {
            <p data-testid="endpoint-statement-build">@Warning</p>
        }
        <p>@IntegrationStatement.HiddenFields</p>
        <p>@IntegrationStatement.PrivateDestinations</p>
        <p>@IntegrationStatement.NoRedelivery</p>
    </AlvoAlert>
    @RefusalPanel.Of(_refusal, "That endpoint could not be saved")

    @if (Editing is null)
    {
        <Field Label="Name" For="endpoint-name" Required="true">
            <ChildContent>
                <MudTextField @ref="_nameBox" T="string" Variant="Variant.Outlined" Immediate="true" id="endpoint-name" Class="a-mono"
                              autocomplete="off" spellcheck="false" Placeholder="billing-system" aria-required="true"
                              Error="@_fields.Has(EndpointDraft.NameField)"
                              aria-describedby="@_fields.DescribedBy(EndpointDraft.NameField, "endpoint-name-hint")"
                              Value="_draft.Name" ValueChanged="TypeName" />
                @_fields.Under(EndpointDraft.NameField, () => _nameBox!.FocusAsync())
            </ChildContent>
            <Hint>Lower case letters, digits and dashes, such as <code class="a-mono">billing-system</code>. A hook's webhook action names it.</Hint>
        </Field>
    }
    else
    {
        <Field Label="Name">
            <ChildContent><span class="a-mono" data-testid="endpoint-name-fixed">@Editing</span></ChildContent>
            <Hint>A hook names the endpoint by it, so it is not renamed here.</Hint>
        </Field>
    }

    <Field Label="URL" For="endpoint-url" Required="true">
        <ChildContent>
            <MudTextField @ref="_urlBox" T="string" Variant="Variant.Outlined" Immediate="true" id="endpoint-url" Class="a-mono"
                          autocomplete="off" spellcheck="false" Placeholder="https://billing.example/hooks/alvo" aria-required="true"
                          Error="@_fields.Has(EndpointDraft.UrlField)"
                          aria-describedby="@_fields.DescribedBy(EndpointDraft.UrlField, "endpoint-url-hint")"
                          Value="_draft.Url" ValueChanged="TypeUrl" />
            @_fields.Under(EndpointDraft.UrlField, () => _urlBox!.FocusAsync())
        </ChildContent>
        <Hint>
            An absolute <code class="a-mono">https</code> URL; plain <code class="a-mono">http</code> only for a loopback host —
            localhost, 127.0.0.1, [::1]. Checked here as the apply checks it once a hook posts to this endpoint.
        </Hint>
    </Field>

    <Field Label="Secret name" For="endpoint-secret" Required="true">
        <ChildContent>
            <MudTextField @ref="_secretBox" T="string" Variant="Variant.Outlined" Immediate="true" id="endpoint-secret" Class="a-mono"
                          autocomplete="off" spellcheck="false" Placeholder="billing-system-signing-key" aria-required="true"
                          Error="@_fields.Has(EndpointDraft.SecretField)"
                          aria-describedby="@_fields.DescribedBy(EndpointDraft.SecretField, "endpoint-secret-hint")"
                          Value="_draft.SecretRef" ValueChanged="TypeSecret" />
            @_fields.Under(EndpointDraft.SecretField, () => _secretBox!.FocusAsync())
        </ChildContent>
        <Hint>@IntegrationStatement.SecretHint</Hint>
    </Field>

    <Field Label="Description (optional)" For="endpoint-description">
        <ChildContent>
            <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="endpoint-description" autocomplete="off"
                          aria-describedby="endpoint-description-hint" Value="_draft.Description" ValueChanged="TypeDescription" />
        </ChildContent>
        <Hint>For whoever reads the descriptor; this build does not read it.</Hint>
    </Field>
</AlvoEditor>

@code {
    private readonly FieldRefusals _fields = new();
    private readonly RefusalState<string> _refusal = new();
    private EndpointDraft _draft = new();
    private string _opened = string.Empty;
    private MudTextField<string>? _nameBox;
    private MudTextField<string>? _urlBox;
    private MudTextField<string>? _secretBox;

    /// <summary>The endpoint being edited, or <see langword="null"/> for a new one.</summary>
    [Parameter]
    public string? Editing { get; set; }

    /// <summary>The build's own sentence about the <c>webhooks</c> block, verbatim from <c>capabilities.warned</c>.</summary>
    [Parameter]
    public string? Warning { get; set; }

    /// <summary>Raised with the endpoint's name once it is in the working copy.</summary>
    [Parameter]
    public EventCallback<string> OnSaved { get; set; }

    /// <summary>Raised when the sheet closes without saving.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>The working copy the declaration is written into — cascaded, so its model stays internal.</summary>
    [CascadingParameter]
    private WorkingCopy? Copy { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        if (Editing is { } name && Copy is { } copy)
        {
            foreach (var (key, declared) in DescriptorLens.Endpoints(copy.Json).Where(pair => pair.Key == name))
            {
                _draft = EndpointDraft.From(key, declared);
            }
        }

        _opened = Snapshot();
    }

    private bool Dirty => Snapshot() != _opened;

    private string Snapshot() => string.Join('\n', _draft.Name, _draft.Url, _draft.SecretRef, _draft.Description);

    private void TypeName(string? text)
    {
        _draft.TypeName(text ?? string.Empty);
        _fields.Clear(EndpointDraft.NameField);
    }

    private void TypeUrl(string? text)
    {
        _draft.Url = text ?? string.Empty;
        _fields.Clear(EndpointDraft.UrlField);
    }

    private void TypeSecret(string? text)
    {
        _draft.TypeSecret(text ?? string.Empty);
        _fields.Clear(EndpointDraft.SecretField);
    }

    private void TypeDescription(string? text) => _draft.Description = text ?? string.Empty;

    /// <summary>Refuses at the field what the apply would refuse, then writes the declaration and says so — the name only.</summary>
    private async Task SaveAsync()
    {
        if (Copy is not { } copy)
        {
            return;
        }

        var refusals = _draft.Refusals([.. DescriptorLens.Endpoints(copy.Json).Select(pair => pair.Key)], Editing is not null);
        if (refusals.Count > 0)
        {
            _fields.RefuseAll(refusals);
            return;
        }

        var name = Editing ?? _draft.Name.Trim();
        if (!copy.DeclareEndpoint(name, _draft.Url.Trim(), _draft.SecretRef.Trim(), _draft.Description.Trim(), Editing is not null))
        {
            _refusal.Show(Overtaken(name));
            return;
        }

        Snackbar.Confirm(Editing is null ? StagedWords.Added("Endpoint", name) : StagedWords.Saved("Endpoint", name));
        await OnSaved.InvokeAsync(name);
    }

    private string Overtaken(string name) => Editing is null
        ? $"An endpoint named '{name}' was declared in the working copy while this sheet was open. Nothing was saved; choose another name."
        : $"The endpoint '{name}' is no longer declared in the working copy. Nothing was saved.";
}
```

- [ ] **Step 4: Replace `Integrations.razor` whole** (templates are listed; declaring one is Task 14)

```razor
@page "/admin/integrations"
@attribute [Microsoft.AspNetCore.Authorization.Authorize]
@inject ManagementGateway Gateway
@inject AdminSession Session
@implements IDisposable

<PageTitle>Integrations · Alvo</PageTitle>

@*
    The webhook endpoints and message templates of the operator's working copy (spec §4.7, ruling B5): a declaration
    staged a minute ago is listed, and a hook's picker offers it. What the build does with each block is its own
    sentence, served verbatim from capabilities.warned. Each list's create sits in its panel's head (spec §3.7), and the
    new or edited row is lit (§3.5).
*@
<div class="a-stack">
    <PageHeader Title="Integrations"
                Subtitle="The webhook endpoints and message templates your working copy declares. A hook's webhook or email action names them; what the build does with each is said under it, in its own words." />

    @if (_capabilities is null || _copy is null)
    {
        <Skeleton />
    }
    else
    {
        <Panel Title="Webhook endpoints" data-testid="integrations-endpoints">
            <Actions>
                @if (_copy.Loaded)
                {
                    <AlvoButton Small="true" data-testid="endpoint-new" OnClick="NewEndpoint">New endpoint</AlvoButton>
                }
            </Actions>
            <ChildContent>
                @if (Warned("webhooks") is { } webhooks)
                {
                    <NotYetConsequence Consequence="@webhooks" Badge="not yet" />
                }
                @if (_endpoints.Count == 0)
                {
                    <EmptyState Title="No endpoint declared"
                                Body="An endpoint is a name and a URL an after-hook's webhook action posts to. Declaring one changes the descriptor, so it joins the working copy like any other edit." />
                }
                @foreach (var endpoint in _endpoints)
                {
                    @EndpointRowView(endpoint)
                }
                @AsHeld("webhooks")
            </ChildContent>
        </Panel>

        <Panel Title="Message templates" data-testid="integrations-templates">
            <ChildContent>
                @if (Warned("templates") is { } templates)
                {
                    <NotYetConsequence Consequence="@templates" Badge="not yet" />
                }
                @if (_templates.Count == 0)
                {
                    <EmptyState Title="No template declared"
                                Body="A template is a subject and a body with {{…}} placeholders that an after-hook's email action renders." />
                }
                @foreach (var template in _templates)
                {
                    @TemplateRowView(template)
                }
                @AsHeld("templates")
            </ChildContent>
        </Panel>

        <Panel Title="What this build refuses in these blocks">
            @if (Refusals.Count == 0)
            {
                <p class="a-note">This build refuses nothing in these blocks.</p>
            }
            else
            {
                @foreach (var refusal in Refusals)
                {
                    <p class="a-note" data-testid="@($"integrations-refused-{refusal.Slot}")">
                        <code class="a-mono">@refusal.Slot</code> — @refusal.Consequence @refusal.Fix
                    </p>
                }
            }
        </Panel>

        <CascadingValue Value="_copy">
            @if (_endpointEditor is { } endpointEditor)
            {
                <EndpointEditor Editing="@endpointEditor.Name" Warning="@Warned("webhooks")" OnSaved="EndpointSaved" OnClose="CloseEditors" />
            }
        </CascadingValue>

        @if (_reveal is { } revealed)
        {
            @RevealOnRender.On($"#{revealed}", _reveals)
        }
    }
</div>

@code {
    private readonly ComponentLifetime _lifetime = new();
    private ManagementCapabilities? _capabilities;
    private WorkingCopy? _copy;
    private IReadOnlyList<EndpointRow> _endpoints = [];
    private IReadOnlyList<TemplateRow> _templates = [];
    private Opened? _endpointEditor;
    private string? _reveal;
    private int _reveals;

    /// <summary>
    /// The refusals that belong to this screen, by <see cref="RefusalPlaces"/>.
    /// </summary>
    /// <remarks>
    /// Served verbatim from <c>capabilities.refused</c>. A control for any of these must not exist
    /// (§4.1), so the screen shows the framework's own sentence in place of the control rather than
    /// offering one that can only end in a refusal. Placed by the map rather than a prefix, which
    /// matched neither <c>bodyFile</c> nor <c>JSONata</c> (docs/todo-admin.md §8d item 19).
    /// </remarks>
    private IReadOnlyList<ManagementRefusedFeature> Refusals => _capabilities is null
        ? []
        : RefusalPlaces.On(RefusalScreen.Integrations, _capabilities.Refused);

    /// <summary>The build's refusal of a template's <c>bodyFile</c>, shown under a row that reads one.</summary>
    private ManagementRefusedFeature? BodyFileRefusal => Refusals.FirstOrDefault(refusal => refusal.Slot == "bodyFile");

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _capabilities = await Gateway.CapabilitiesAsync(_lifetime.Token);
        _copy = await Session.WorkingCopyAsync(_lifetime.Token);
        Session.Follow(_copy, InvokeAsync, OnCopyChanged, _lifetime.Token);
        Read();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Another tab — or the assistant — moved the copy: read it again and redraw.</summary>
    private void OnCopyChanged()
    {
        Read();
        StateHasChanged();
    }

    private void Read()
    {
        if (_copy is { } copy)
        {
            _endpoints = IntegrationRows.Endpoints(copy.Json, copy.AppliedJson);
            _templates = IntegrationRows.Templates(copy.Json, copy.AppliedJson);
        }
    }

    /// <summary>The build's own sentence about a block, or nothing when it reports none.</summary>
    private string? Warned(string block) => _capabilities?.Warned
        .FirstOrDefault(warned => string.Equals(warned.Block, block, StringComparison.Ordinal))?.Consequence;

    private void NewEndpoint() => _endpointEditor = new Opened(null);

    private void EditEndpoint(string name) => _endpointEditor = new Opened(name);

    private void CloseEditors() => _endpointEditor = null;

    private void EndpointSaved(string name) => Saved($"endpoint-{name}");

    /// <summary>Closes the sheet, reads the copy, and lights the saved row (spec §3.5).</summary>
    private void Saved(string row)
    {
        CloseEditors();
        Read();
        _reveal = row;
        _reveals++;
    }

    /// <summary>"posted to by rentals afterCreate and 1 more", or what to say when no hook uses it.</summary>
    private static string UsesWords(IReadOnlyList<DescriptorLens.IntegrationUse> uses, string some, string none) => uses.Count switch
    {
        0 => none,
        1 => $"{some} {uses[0].Entity} {uses[0].Point}",
        _ => $"{some} {uses[0].Entity} {uses[0].Point} and {uses.Count - 1} more",
    };

    /// <summary>One endpoint's row.</summary>
    private RenderFragment EndpointRowView(EndpointRow row) => @<text>
        <ListRow class="a-listrow--stacked" id="@($"endpoint-{row.Name}")" data-testid="endpoint-row"
                 data-alvo-new="@($"endpoint-{row.Name}" == _reveal ? "true" : null)">
            <div class="a-row a-row--wrap">
                <code class="a-mono">@row.Name</code>
                @if (row.Staged)
                {
                    <span class="a-badge a-badge--accent" data-testid="endpoint-staged">not applied yet</span>
                }
                <span class="@(row.Uses.Count > 0 ? "a-badge a-badge--ok" : "a-badge")" data-testid="endpoint-uses">
                    @UsesWords(row.Uses, "posted to by", "no hook posts here")
                </span>
                <span class="a-badge a-badge--warn">not signed</span>
                @if (row.Drawable && _copy!.Loaded)
                {
                    <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="endpoint-edit"
                                aria-label="@($"Edit endpoint {row.Name}")" OnClick="_ => EditEndpoint(row.Name)">Edit</AlvoButton>
                }
            </div>
            <span class="a-mono">@row.Url</span>
            <span class="a-hint">secretRef <code class="a-mono">@row.SecretRef</code> — declared, and not read by this build.</span>
        </ListRow>
    </text>;

    /// <summary>One template's row.</summary>
    private RenderFragment TemplateRowView(TemplateRow row) => @<text>
        <ListRow class="a-listrow--stacked" id="@($"template-{row.Name}")" data-testid="template-row"
                 data-alvo-new="@($"template-{row.Name}" == _reveal ? "true" : null)">
            <div class="a-row a-row--wrap">
                <code class="a-mono">@row.Name</code>
                @if (row.Staged)
                {
                    <span class="a-badge a-badge--accent" data-testid="template-staged">not applied yet</span>
                }
                <span class="@(row.Uses.Count > 0 ? "a-badge a-badge--ok" : "a-badge")">@UsesWords(row.Uses, "sent by", "no hook sends it")</span>
                @if (row.HasBodyFile)
                {
                    <span class="a-badge a-badge--warn">bodyFile — not read</span>
                }
            </div>
            @if (row.Subject.Length > 0)
            {
                <span class="a-hint">@row.Subject</span>
            }
            @if (row.HasBodyFile && BodyFileRefusal is { } refusal)
            {
                <span class="a-refused__reason">@refusal.Consequence</span>
            }
        </ListRow>
    </text>;

    /// <summary>The block as the working copy holds it, folded — for a declaration the rows cannot draw.</summary>
    private RenderFragment AsHeld(string block) => @<text>
        @if (DescriptorLens.Block(_copy!.Json, block) is { Length: > 0 } json)
        {
            <MudExpansionPanels Elevation="0" Class="a-integration">
                <MudExpansionPanel Text="@($"The {block} block, as the working copy holds it")">
                    <CodeBlock Json="@json" />
                </MudExpansionPanel>
            </MudExpansionPanels>
        }
    </text>;

    /// <summary>Which sheet is open: a new declaration (no name) or an edit.</summary>
    /// <param name="Name">The declaration being edited.</param>
    private sealed record Opened(string? Name);
}
```

- [ ] **Step 5: Run** — `dotnet build`; `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*PatternLanguageTests' --filter-class '*FieldConventionTests' --filter-class '*StylesheetHygieneTests' --filter-class '*ComponentLayerTests'`; normalise both `.razor` files (BOM + LF).

- [ ] **Step 6: Approve the public-API growth deliberately.** The approval test fails with a `.received.txt` beside `PublicApi.MMLib.Alvo.Admin.verified.txt`; copy it over the verified file and read the diff: it must add **only** `EndpointEditor` (constructor, `Editing`, `Warning`, `OnSaved`, `OnClose`, and the component base overrides the generator emits) and `Integrations : IDisposable` with `Dispose()`. The Stop hook then asks for a justification per symbol — give the spec §14 one: a Razor component cannot be internal and still be a tag; every parameter is a string or an `EventCallback` of one; the working copy reaches it by cascade; `Integrations` now follows the working copy and must stop when it goes. Anything else in the diff is a mistake to undo.

- [ ] **Step 7: Run the e2e** — `scripts/test-ring0`; `scripts/test-admin-e2e --filter-class '*IntegrationsScenarios'` and `--filter-class '*RefusalPlacementScenarios'` (unchanged, must pass), `--filter-class '*PhoneAndKeyboardScenarios'` (every screen still fits 375 px).

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Integrations/Integrations.razor src/MMLib.Alvo.Admin/Components/Integrations/EndpointEditor.razor test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt test/MMLib.Alvo.Admin.Tests.EndToEnd/IntegrationsScenarios.cs
git commit -m "feat(admin): Integrations reads the working copy and declares endpoints behind the build's statement

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 14: declaring and editing a template

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Integrations/TemplateEditor.razor`
- Modify: `src/MMLib.Alvo.Admin/Components/Integrations/Integrations.razor` (the template create, edit and sheet)
- Modify: `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt` (regenerated)
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/TemplateDeclarationScenarios.cs`

**Interfaces:**
- Consumes: `TemplateDraft` (Task 11), `WorkingCopy.DeclareTemplate` (Task 12), Task 13's `Integrations` members (`_copy`, `Opened`, `Saved`, `CloseEditors`, `Warned`, `TemplateRowView`), `HookPickerScenarios.NewAfterHookAsync/Combobox` (Task 10 e2e).
- Produces: public component `TemplateEditor` (same four parameters as `EndpointEditor`); test ids `template-new`, `template-edit`, `template-editor`, `template-save`, `template-statement-build`, `template-name-fixed`.

- [ ] **Step 1: Write the failing e2e scenarios** (`TemplateDeclarationScenarios.cs`):

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A message template is declared and edited on Integrations — subject and body only — and an email hook offers it before
/// it is applied (spec §4.8, §6.2). Each scenario uses its own names: the class shares one working copy.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class TemplateDeclarationScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_template_with_a_broken_subject_or_a_tenant_placeholder_is_refused_at_its_fields()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("template-new").ClickAsync();
        var editor = session.Dialog("template-editor");

        await session.Page.FillAsync("#template-name", "broken-template");
        await session.Page.FillAsync("#template-subject", "Ready\u2028now");
        await session.Page.FillAsync("#template-body", "Hi {{@tenant.id}}");
        await editor.GetByTestId("template-save").ClickAsync();

        await session.WaitForFocusOnAsync("template-subject");
        (await session.Page.Locator("#template-subject-problem").InnerTextAsync()).ShouldContain("one line");
        (await session.Page.Locator("#template-body-problem").InnerTextAsync()).ShouldContain("carries no tenant");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_new_template_is_offered_to_an_email_hook_before_it_is_applied()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("template-new").ClickAsync();
        var editor = session.Dialog("template-editor");
        await session.Page.FillAsync("#template-name", "pickup-reminder");
        await session.Page.FillAsync("#template-subject", "Your bike {{new.order_number}} is waiting");
        await session.Page.FillAsync("#template-body", "Please collect it.");
        await editor.GetByTestId("template-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await HookPickerScenarios.NewAfterHookAsync(session, "service_orders", "afterUpdate", "email");
        await HookPickerScenarios.Combobox(session, "Template").ClickAsync();
        (await session.Page.GetByRole(AriaRole.Option, new() { Name = "pickup-reminder (not applied yet)", Exact = true }).CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_template_is_edited_with_its_name_fixed()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/integrations");
        await session.Page.Locator("#template-order-ready [data-testid='template-edit']").ClickAsync();
        var editor = session.Dialog("template-editor");

        (await editor.InnerTextAsync()).ShouldContain("Edit template order-ready");
        (await editor.GetByTestId("template-name-fixed").InnerTextAsync()).ShouldBe("order-ready");
        await session.Page.FillAsync("#template-subject", "Your bike is ready: order {{new.order_number}}");
        await editor.GetByTestId("template-save").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.SnackbarAsync("Template order-ready saved to the working copy");
        (await session.Page.Locator("#template-order-ready").GetAttributeAsync("data-alvo-new")).ShouldBe("true");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_template_sheet_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await session.GoAsync("/integrations");
        await session.Page.GetByTestId("template-new").ClickAsync();
        await session.Dialog("template-editor").WaitForAsync();

        await session.AssertNoHorizontalScrollAsync();
    }
}
```

- [ ] **Step 2: Run to verify failure** — `scripts/test-admin-e2e --filter-class '*TemplateDeclarationScenarios'`. Expected: FAIL — no `template-new`.

- [ ] **Step 3: Create `TemplateEditor.razor`**

```razor
@*
    Declares or edits one message template in the working copy (spec §4.8, §6.2): a subject and a body, nothing else —
    `bodyFile` and `email.data` are refused by this build and stay on the refusal panel. The build's own sentence about
    the templates block comes first. The name is fixed in an edit: an email action names the template by it.
*@
@inject ISnackbar Snackbar

<AlvoEditor TestId="template-editor" Title="@(Editing is null ? "New template" : $"Edit template {Editing}")"
            SubmitText="@(Editing is null ? "Add to the working copy" : "Save to the working copy")" SubmitTestId="template-save"
            Subtitle="It joins the working copy. An after-hook's email action names it; nothing is sent until you apply."
            Dirty="Dirty" OnSubmit="SaveAsync" OnClose="OnClose">
    @if (Warning is { Length: > 0 })
    {
        <AlvoAlert Tone="AlvoAlert.AlertTone.Info" Title="What this build does with a template" TestId="template-statement">
            <p data-testid="template-statement-build">@Warning</p>
        </AlvoAlert>
    }
    @RefusalPanel.Of(_refusal, "That template could not be saved")

    @if (Editing is null)
    {
        <Field Label="Name" For="template-name" Required="true">
            <ChildContent>
                <MudTextField @ref="_nameBox" T="string" Variant="Variant.Outlined" Immediate="true" id="template-name" Class="a-mono"
                              autocomplete="off" spellcheck="false" Placeholder="order-ready" aria-required="true"
                              Error="@_fields.Has(TemplateDraft.NameField)"
                              aria-describedby="@_fields.DescribedBy(TemplateDraft.NameField, "template-name-hint")"
                              Value="_draft.Name" ValueChanged="TypeName" />
                @_fields.Under(TemplateDraft.NameField, () => _nameBox!.FocusAsync())
            </ChildContent>
            <Hint>Lower case letters, digits, dashes and underscores, such as <code class="a-mono">order-ready</code>.</Hint>
        </Field>
    }
    else
    {
        <Field Label="Name">
            <ChildContent><span class="a-mono" data-testid="template-name-fixed">@Editing</span></ChildContent>
            <Hint>An email action names the template by it, so it is not renamed here.</Hint>
        </Field>
    }

    <Field Label="Subject (optional)" For="template-subject">
        <ChildContent>
            <MudTextField @ref="_subjectBox" T="string" Variant="Variant.Outlined" Immediate="true" id="template-subject"
                          autocomplete="off" Error="@_fields.Has(TemplateDraft.SubjectField)"
                          aria-describedby="@_fields.DescribedBy(TemplateDraft.SubjectField, "template-subject-hint")"
                          Value="_draft.Subject" ValueChanged="TypeSubject" />
            @_fields.Under(TemplateDraft.SubjectField, () => _subjectBox!.FocusAsync())
        </ChildContent>
        <Hint>
            One line. Placeholders such as <code class="a-mono">{{new.order_number}}</code> over <code class="a-mono">new</code>,
            <code class="a-mono">old</code>, <code class="a-mono">event</code> and <code class="a-mono">@@user.id</code>.
        </Hint>
    </Field>

    <Field Label="Body" For="template-body" Required="true">
        <ChildContent>
            <MudTextField @ref="_bodyBox" T="string" Variant="Variant.Outlined" Immediate="true" id="template-body" Lines="6"
                          autocomplete="off" aria-required="true" Error="@_fields.Has(TemplateDraft.BodyField)"
                          aria-describedby="@_fields.DescribedBy(TemplateDraft.BodyField, "template-body-hint")"
                          Value="_draft.Body" ValueChanged="TypeBody" />
            @_fields.Under(TemplateDraft.BodyField, () => _bodyBox!.FocusAsync())
        </ChildContent>
        <Hint>
            The same placeholders, on as many lines as you like. A placeholder's field is checked against the entity of each
            email hook that sends this template — on apply.
            @ChordHint.Of(Editing is null ? "adds it to the working copy" : "saves it to the working copy")
        </Hint>
    </Field>
</AlvoEditor>

@code {
    private readonly FieldRefusals _fields = new();
    private readonly RefusalState<string> _refusal = new();
    private TemplateDraft _draft = new();
    private string _opened = string.Empty;
    private MudTextField<string>? _nameBox;
    private MudTextField<string>? _subjectBox;
    private MudTextField<string>? _bodyBox;

    /// <summary>The template being edited, or <see langword="null"/> for a new one.</summary>
    [Parameter]
    public string? Editing { get; set; }

    /// <summary>The build's own sentence about the <c>templates</c> block, verbatim from <c>capabilities.warned</c>.</summary>
    [Parameter]
    public string? Warning { get; set; }

    /// <summary>Raised with the template's name once it is in the working copy.</summary>
    [Parameter]
    public EventCallback<string> OnSaved { get; set; }

    /// <summary>Raised when the sheet closes without saving.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>The working copy the declaration is written into — cascaded, so its model stays internal.</summary>
    [CascadingParameter]
    private WorkingCopy? Copy { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        if (Editing is { } name && Copy is { } copy)
        {
            foreach (var (key, declared) in DescriptorLens.Templates(copy.Json).Where(pair => pair.Key == name))
            {
                _draft = TemplateDraft.From(key, declared);
            }
        }

        _opened = Snapshot();
    }

    private bool Dirty => Snapshot() != _opened;

    private string Snapshot() => string.Join('\n', _draft.Name, _draft.Subject, _draft.Body);

    private void TypeName(string? text)
    {
        _draft.Name = text ?? string.Empty;
        _fields.Clear(TemplateDraft.NameField);
    }

    private void TypeSubject(string? text)
    {
        _draft.Subject = text ?? string.Empty;
        _fields.Clear(TemplateDraft.SubjectField);
    }

    private void TypeBody(string? text)
    {
        _draft.Body = text ?? string.Empty;
        _fields.Clear(TemplateDraft.BodyField);
    }

    /// <summary>Refuses at the field what the apply would refuse, then writes the declaration and says so.</summary>
    private async Task SaveAsync()
    {
        if (Copy is not { } copy)
        {
            return;
        }

        var refusals = _draft.Refusals([.. DescriptorLens.Templates(copy.Json).Select(pair => pair.Key)], Editing is not null);
        if (refusals.Count > 0)
        {
            _fields.RefuseAll(refusals);
            return;
        }

        var name = Editing ?? _draft.Name.Trim();
        if (!copy.DeclareTemplate(name, _draft.Subject, _draft.Body, Editing is not null))
        {
            _refusal.Show($"The template '{name}' changed in the working copy while this sheet was open. Nothing was saved.");
            return;
        }

        Snackbar.Confirm(Editing is null ? StagedWords.Added("Template", name) : StagedWords.Saved("Template", name));
        await OnSaved.InvokeAsync(name);
    }
}
```

- [ ] **Step 4: Wire it into `Integrations.razor`** — four edits:

  1. In the "Message templates" `Panel`, before `<ChildContent>`, add:

```razor
            <Actions>
                @if (_copy.Loaded)
                {
                    <AlvoButton Small="true" data-testid="template-new" OnClick="NewTemplate">New template</AlvoButton>
                }
            </Actions>
```

  2. In `TemplateRowView`, after the `bodyFile` badge's closing `}` (inside the `a-row` div), add:

```razor
                @if (row.Drawable && _copy!.Loaded)
                {
                    <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="template-edit"
                                aria-label="@($"Edit template {row.Name}")" OnClick="_ => EditTemplate(row.Name)">Edit</AlvoButton>
                }
```

  3. Inside the `<CascadingValue Value="_copy">`, after the `EndpointEditor` block, add:

```razor
            @if (_templateEditor is { } templateEditor)
            {
                <TemplateEditor Editing="@templateEditor.Name" Warning="@Warned("templates")" OnSaved="TemplateSaved" OnClose="CloseEditors" />
            }
```

  4. In `@code`, add the field `private Opened? _templateEditor;` after `_endpointEditor`, and replace the editor methods with:

```csharp
    private void NewEndpoint() => (_templateEditor, _endpointEditor) = (null, new Opened(null));

    private void EditEndpoint(string name) => (_templateEditor, _endpointEditor) = (null, new Opened(name));

    private void NewTemplate() => (_endpointEditor, _templateEditor) = (null, new Opened(null));

    private void EditTemplate(string name) => (_endpointEditor, _templateEditor) = (null, new Opened(name));

    private void CloseEditors() => (_endpointEditor, _templateEditor) = (null, null);

    private void EndpointSaved(string name) => Saved($"endpoint-{name}");

    private void TemplateSaved(string name) => Saved($"template-{name}");
```

- [ ] **Step 5: Run** — `dotnet build`; `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*PatternLanguageTests' --filter-class '*FieldConventionTests'` (the body box is multi-line in an `AlvoEditor` and the file holds `ChordHint.Of(`); normalise `TemplateEditor.razor` (BOM + LF). Approve the public-API diff as in Task 13 Step 6 — it must add only `TemplateEditor`'s constructor, its four parameters and the generator's overrides. Then `scripts/test-ring0`; `scripts/test-admin-e2e --filter-class '*TemplateDeclarationScenarios'`, `--filter-class '*IntegrationsScenarios'`, `--filter-class '*RefusalPlacementScenarios'`.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Integrations/TemplateEditor.razor src/MMLib.Alvo.Admin/Components/Integrations/Integrations.razor test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt test/MMLib.Alvo.Admin.Tests.EndToEnd/TemplateDeclarationScenarios.cs
git commit -m "feat(admin): declare and edit a message template on Integrations, subject and body only

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

Then `scripts/test-ring1`.

---
### Task 15: Host.Tests holds every client restatement to the core

**Files:**
- Test: `test/MMLib.Alvo.Host.Tests/HooksEditorAgreementTests.cs`

**Interfaces:**
- Consumes (Admin internals, visible to Host.Tests by `src/MMLib.Alvo.Admin/Properties/AssemblyInfo.cs`): `EndpointDraft.UrlRefusal`, `MutateLiteral.TryValue`, `MutateRow`, `HookFields.Declared`, `TemplateDraft.Roots`, `IntegrationStatement` (Tasks 4, 11). Core internals: `DescriptorValidator` (`MMLib.Alvo.Descriptor.Internal`), `TemplatePlaceholder.Roots` (`MMLib.Alvo.Events.Internal`, `AlvoTemplate.cs:216-219`), `CapabilityReport.Project()` (`MMLib.Alvo.Management.Internal`), public `AlvoEventOptions`.
- Produces: nothing used later.

- [ ] **Step 1: Write the facts**

```csharp
using MMLib.Alvo.Admin.Components.Integrations;
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Management.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>Every place the hooks editor restates a core rule gives the core's answer.</b>
/// </summary>
/// <remarks>
/// The dashboard cannot reference <c>MMLib.Alvo</c>, so the URL rule, the mutate literal fit, the template roots and the
/// statement's claims are restated there (spec §6, §11). This suite sees both assemblies' internals, so the agreement is
/// measured here — the <c>DeclaredSlotsAgreementTests</c> pattern — against the real validator, not a copy of it.
/// </remarks>
public sealed class HooksEditorAgreementTests
{
    [Theory]
    [InlineData("https://example.com/hooks")]
    [InlineData("http://localhost:5081/hooks")]
    [InlineData("http://127.0.0.1:5081/hooks")]
    [InlineData("http://[::1]:5081/hooks")]
    [InlineData("http://127.0.0.2/hooks")]
    [InlineData("http://example.com/hooks")]
    [InlineData("ftp://example.com/x")]
    [InlineData("/hooks/relative")]
    [InlineData("htp://x")]
    [InlineData("https://")]
    [InlineData("https://user:pass@example.com/h")]
    [InlineData("")]
    public void The_endpoint_sheet_refuses_a_url_exactly_when_apply_does(string url)
    {
        var applyRefuses = Errors(WithWebhookTo(url)).Any(error => error.Path.EndsWith("/webhooks/endpoints/desk/url", StringComparison.Ordinal));

        (EndpointDraft.UrlRefusal(url) is not null).ShouldBe(applyRefuses, url);
    }

    [Theory]
    [InlineData("integer", "3")]
    [InlineData("integer", "-2")]
    [InlineData("integer", "9223372036854775807")]
    [InlineData("decimal", "12.5")]
    [InlineData("decimal", "-0.01")]
    [InlineData("boolean", "true")]
    [InlineData("datetime", "2026-10-05T12:00:00Z")]
    [InlineData("uuid", "3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f")]
    [InlineData("string", "it's")]
    [InlineData("text", "")]
    public void A_literal_the_mutate_row_accepts_is_one_apply_accepts(string type, string text)
    {
        var descriptor = Probe(type);
        var field = HookFields.Declared(descriptor, "probe")["f"];

        MutateLiteral.TryValue(new MutateRow("f", MutateMode.Literal, text), field, out var value, out var refusal).ShouldBeTrue(refusal);

        MutateErrors(descriptor, value).ShouldBeEmpty($"{type} '{text}'");
    }

    [Theory]
    [InlineData("date", "2026-10-05")]
    [InlineData("datetime", "2026-10-05")]
    [InlineData("datetime", "tomorrow")]
    [InlineData("datetime", "2026-10-05 12:00")]
    [InlineData("uuid", "42")]
    [InlineData("uuid", "3F2C1A9E-6B7D-4C8E-9F10-2A3B4C5D6E7F")]
    public void The_mutate_row_and_apply_agree_on_text_typed_for_a_moment_or_an_id(string type, string text)
    {
        var descriptor = Probe(type);
        var field = HookFields.Declared(descriptor, "probe")["f"];
        var rowAccepts = MutateLiteral.TryValue(new MutateRow("f", MutateMode.Literal, text), field, out _, out _);

        (MutateErrors(descriptor, JsonValue.Create(text)).Count == 0).ShouldBe(rowAccepts, $"{type} '{text}'");
    }

    /// <summary>The one place the row is stricter than apply, recorded as a finding (spec §11): delete this when apply refuses it.</summary>
    [Fact]
    public void An_enum_literal_outside_its_values_is_refused_by_the_row_and_still_accepted_by_apply()
    {
        var descriptor = Probe("enum");
        var field = HookFields.Declared(descriptor, "probe")["f"];

        MutateLiteral.TryValue(new MutateRow("f", MutateMode.Literal, "bogus"), field, out _, out _).ShouldBeFalse();
        MutateErrors(descriptor, JsonValue.Create("bogus")).ShouldBeEmpty(
            "if apply now refuses an enum literal outside its values, the spec §11 finding is fixed: delete this fact");
    }

    [Fact]
    public void The_template_sheet_knows_exactly_the_roots_an_email_resolves()
        => TemplateDraft.Roots.ShouldBe(TemplatePlaceholder.Roots);

    [Fact]
    public void The_builds_webhooks_sentence_still_says_deliveries_are_unsigned_and_unprojected()
    {
        var sentence = CapabilityReport.Project().Warned.Single(block => block.Block == "webhooks").Consequence;

        sentence.ShouldContain("signed", Case.Sensitive, "IntegrationStatement.Title says so; when signing lands (#120) both change");
        sentence.ShouldContain("#152", Case.Sensitive, "IntegrationStatement.HiddenFields says so; projection retires it");
    }

    [Fact]
    public void The_setting_the_statement_names_is_the_options_own_and_admits_nothing_by_default()
    {
        IntegrationStatement.AllowedNetworksSetting.ShouldBe($"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.WebhookAllowedNetworks)}");
        new AlvoEventOptions().WebhookAllowedNetworks.ShouldBeEmpty();
    }

    private static string WithWebhookTo(string url) => new JsonObject
    {
        ["apiVersion"] = "alvo.dev/v1",
        ["name"] = "agreement",
        ["webhooks"] = new JsonObject
        {
            ["endpoints"] = new JsonObject { ["desk"] = new JsonObject { ["url"] = url, ["secretRef"] = "desk-key" } },
        },
        ["entities"] = new JsonObject
        {
            ["orders"] = new JsonObject
            {
                ["fields"] = new JsonObject { ["total"] = new JsonObject { ["type"] = "decimal", ["precision"] = 12, ["scale"] = 2 } },
                ["hooks"] = new JsonObject
                {
                    ["afterCreate"] = new JsonArray(new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "webhook", ["endpoint"] = "desk" },
                    }),
                },
            },
        },
    }.ToJsonString();

    private static string Probe(string type)
    {
        var field = new JsonObject { ["type"] = type };
        if (type == "decimal")
        {
            field["precision"] = 18;
            field["scale"] = 4;
        }
        else if (type == "enum")
        {
            field["values"] = new JsonArray("open", "closed");
        }

        return new JsonObject
        {
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = "agreement",
            ["entities"] = new JsonObject { ["probe"] = new JsonObject { ["fields"] = new JsonObject { ["f"] = field } } },
        }.ToJsonString();
    }

    private static List<DescriptorValidationError> MutateErrors(string descriptor, JsonNode? value)
    {
        var root = JsonNode.Parse(descriptor)!;
        root["entities"]!["probe"]!["hooks"] = new JsonObject
        {
            ["beforeUpdate"] = new JsonArray(new JsonObject
            {
                ["action"] = new JsonObject { ["mutate"] = new JsonObject { ["f"] = value?.DeepClone() } },
            }),
        };

        return [.. Errors(root.ToJsonString()).Where(error => error.Path.Contains("/hooks/beforeUpdate/0", StringComparison.Ordinal))];
    }

    private static List<DescriptorValidationError> Errors(string json)
        => [.. new DescriptorValidator().Validate(json).Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error)];
}
```

- [ ] **Step 2: Run** — `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*HooksEditorAgreementTests'`. Expected: PASS. A failure is a **finding**, not a test to soften: a URL case → `EndpointDraft.UrlRefusal` must change to the apply's answer; a literal case → `MutateLiteral` must change; a roots mismatch → `TemplateDraft.Roots` follows the core; a statement claim → `IntegrationStatement` and spec §6.3 change together. Normalise, `scripts/test-ring0`.

- [ ] **Step 3: Commit**

```bash
git add test/MMLib.Alvo.Host.Tests/HooksEditorAgreementTests.cs
git commit -m "test(admin): the hooks editor's URL rule, literal fit, template roots and statement agree with the core

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 16: `ConditionText` — the generator

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/ConditionText.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/ConditionTextTests.cs`

**Interfaces:**
- Consumes: `ConditionTable` (Task 1); `EntitySchema` (Abstractions).
- Produces (internal, namespace `MMLib.Alvo.Admin.Components.Schema`):
  - `sealed record ConditionRow(ConditionOperator Operator, RowImage Image, string Field, FieldKind Kind, string Value)` — `Field` is `ConditionTable.Writer` for a role row; `Value` is the literal unquoted, the role name, or empty.
  - `sealed record GuidedCondition(bool All, IReadOnlyList<ConditionRow> Rows)` with structural `Equals`, `static Empty`.
  - `sealed record ConditionField(string Name, FieldKind Kind, bool Nullable, IReadOnlyList<string> Values)`
  - `sealed record ConditionScope(IReadOnlyList<ConditionField> Fields, IReadOnlyList<string> Roles)` with `static From(EntitySchema?, IReadOnlyList<string> roles)`, `ConditionField? Field(string)`.
  - `static partial class ConditionText` — `string Generate(GuidedCondition)`, `string Row(ConditionRow)`, `string Quote(string)`, `string? Refusal(GuidedCondition)`, `GuidedCondition Normalize(GuidedCondition)`; private consts `And`, `Or` and regex `NumberLiteral()` that Task 17's half reuses.

- [ ] **Step 1: Write the failing tests** (`ConditionTextTests.cs`):

```csharp
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The guided condition's text, written canonically (spec §7.2).</summary>
public class ConditionTextTests
{
    [Theory]
    [InlineData(ConditionOperator.Is, RowImage.New, "status", FieldKind.Choice, "ready", "new.status == 'ready'")]
    [InlineData(ConditionOperator.IsNotOrEmpty, RowImage.Old, "note", FieldKind.Text, "x", "!(old.note == 'x')")]
    [InlineData(ConditionOperator.LessOrEqual, RowImage.New, "quantity", FieldKind.Number, "0", "new.quantity <= 0")]
    [InlineData(ConditionOperator.HasValue, RowImage.New, "note", FieldKind.Text, "", "has(new.note)")]
    [InlineData(ConditionOperator.Changed, RowImage.New, "status", FieldKind.Choice, "", "changed(status)")]
    [InlineData(ConditionOperator.IsTrue, RowImage.New, "notify_customer", FieldKind.Flag, "", "new.notify_customer == true")]
    [InlineData(ConditionOperator.IsTheWriter, RowImage.New, "owner_id", FieldKind.Identity, "", "new.owner_id == @user.id")]
    [InlineData(ConditionOperator.LacksRole, RowImage.New, "@user", FieldKind.Text, "manager", "!('manager' in @user.roles)")]
    public void A_row_is_written_in_its_canonical_cel(ConditionOperator relation, RowImage image, string field, FieldKind kind, string value, string cel)
        => ConditionText.Row(new ConditionRow(relation, image, field, kind, value)).ShouldBe(cel);

    [Fact]
    public void A_value_is_quoted_with_only_the_escapes_the_lexer_reads()
        => ConditionText.Quote("it's \\ \"ok\"\n\t\r").ShouldBe("'it\\'s \\\\ \"ok\"\\n\\t\\r'");

    [Fact]
    public void Rows_are_joined_by_all_or_any()
    {
        ConditionRow[] rows =
        [
            new(ConditionOperator.Is, RowImage.New, "status", FieldKind.Choice, "ready"),
            new(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, FieldKind.Text, "manager"),
        ];

        ConditionText.Generate(new GuidedCondition(true, rows)).ShouldBe("new.status == 'ready' && 'manager' in @user.roles");
        ConditionText.Generate(new GuidedCondition(false, rows)).ShouldBe("new.status == 'ready' || 'manager' in @user.roles");
    }

    [Theory]
    [InlineData("-5", "negative number")]
    [InlineData("1e3", "not a number")]
    [InlineData("12.", "not a number")]
    public void A_number_a_condition_cannot_hold_is_refused(string value, string says)
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "quantity", FieldKind.Number, value)))
            .ShouldNotBeNull().ShouldContain(says);

    [Fact]
    public void A_condition_longer_than_a_condition_may_be_is_refused()
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", FieldKind.Text, new string('x', 2000))))
            .ShouldNotBeNull().ShouldContain("2000");

    [Fact]
    public void A_value_with_a_control_character_the_lexer_cannot_spell_is_refused()
        => ConditionText.Refusal(Single(new ConditionRow(ConditionOperator.Is, RowImage.New, "title", FieldKind.Text, "bell\u0007")))
            .ShouldNotBeNull().ShouldContain("text mode");

    private static GuidedCondition Single(ConditionRow row) => new(true, [row]);
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ConditionTextTests'`. Expected: build FAIL — `ConditionText` does not exist.

- [ ] **Step 3: Implement `ConditionText.cs`**

```csharp
using MMLib.Alvo.Schema;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One row of a guided condition.</summary>
/// <param name="Operator">The relation.</param>
/// <param name="Image">The image the field is read from; <c>New</c> for <c>changed</c> and role rows, which have none.</param>
/// <param name="Field">The field, or <see cref="ConditionTable.Writer"/> for a role row.</param>
/// <param name="Kind">What the field is; <c>Text</c> for a role row.</param>
/// <param name="Value">The literal unquoted, the role's name, or empty when the relation takes nothing.</param>
internal sealed record ConditionRow(ConditionOperator Operator, RowImage Image, string Field, FieldKind Kind, string Value);

/// <summary>A guided condition: rows joined by all (<c>&amp;&amp;</c>) or any (<c>||</c>).</summary>
/// <param name="All">Whether every row must hold; otherwise any one.</param>
/// <param name="Rows">The rows, in order.</param>
internal sealed record GuidedCondition(bool All, IReadOnlyList<ConditionRow> Rows)
{
    /// <summary>No rows: the hook runs on every write.</summary>
    public static GuidedCondition Empty { get; } = new(true, []);

    /// <summary>Equal when the joiner and every row are.</summary>
    /// <param name="other">The other condition.</param>
    public bool Equals(GuidedCondition? other) => other is not null && All == other.All && Rows.SequenceEqual(other.Rows);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(All, Rows.Count);
}

/// <summary>A field a condition may name.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Nullable">Whether it may be empty.</param>
/// <param name="Values">An enum's declared values; empty otherwise.</param>
internal sealed record ConditionField(string Name, FieldKind Kind, bool Nullable, IReadOnlyList<string> Values);

/// <summary>What a condition may name on one entity: its stored fields and the declared roles.</summary>
/// <param name="Fields">The fields, computed and rollup ones excluded (a before-hook's image does not carry them).</param>
/// <param name="Roles">The roles <c>auth.roles</c> declares.</param>
internal sealed record ConditionScope(IReadOnlyList<ConditionField> Fields, IReadOnlyList<string> Roles)
{
    /// <summary>The scope of one entity as the working copy declares it.</summary>
    /// <param name="entity">The entity (<c>PendingSchema.Read</c>), or <see langword="null"/>.</param>
    /// <param name="roles">The declared roles.</param>
    public static ConditionScope From(EntitySchema? entity, IReadOnlyList<string> roles) => new(
        entity is null
            ? []
            : [.. entity.Fields
                .Where(field => field.ComputedExpression is null && field.Rollup is null)
                .Select(field => new ConditionField(field.Name, ConditionTable.KindOf(field.Type), field.Nullable, field.EnumValues ?? Array.Empty<string>()))],
        roles);

    /// <summary>The field by name, or <see langword="null"/>.</summary>
    /// <param name="name">The name.</param>
    public ConditionField? Field(string name) => Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// The guided condition's text: the one canonical CEL spelling of every row <see cref="ConditionTable"/> allows (spec §7.2,
/// ruling B7); <c>ConditionText.Recognize.cs</c> reads exactly that spelling back (§7.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a CEL parser</b> (analysis §4.5.2: the dashboard has no second one). The recognizer accepts only text the generator
/// writes, byte for byte — it matches the table's own formats and then demands <c>Generate(result) == text</c>; anything else
/// is text mode. The core stays the authority: the text is judged by <c>cel/check</c> and by the apply.
/// </para>
/// <para>Text literals use only the lexer's escapes (<c>\\ \' \n \r \t</c>, <c>CelLexer.ReadEscape</c>).</para>
/// </remarks>
internal static partial class ConditionText
{
    private const string And = " && ";
    private const string Or = " || ";

    /// <summary>The condition's CEL; empty for no rows.</summary>
    /// <param name="condition">The condition.</param>
    public static string Generate(GuidedCondition condition)
        => string.Join(condition.All ? And : Or, condition.Rows.Select(Row));

    /// <summary>One row's CEL, in its table format.</summary>
    /// <param name="row">The row.</param>
    public static string Row(ConditionRow row)
    {
        var spec = ConditionTable.Of(row.Operator);
        var text = spec.Format
            .Replace("{f}", $"{ConditionTable.Prefix(row.Image)}.{row.Field}", StringComparison.Ordinal)
            .Replace("{n}", row.Field, StringComparison.Ordinal);

        /* The value goes in last, and only one of the two placeholders exists per format, so a value that happens to hold
           "{r}" is never substituted again. */
        return spec.Operand switch
        {
            OperandKind.Literal => text.Replace("{v}", row.Kind == FieldKind.Number ? row.Value : Quote(row.Value), StringComparison.Ordinal),
            OperandKind.Role => text.Replace("{r}", Quote(row.Value), StringComparison.Ordinal),
            _ => text,
        };
    }

    /// <summary>A CEL string literal: single quotes, and only the escapes the lexer reads.</summary>
    /// <param name="value">The text.</param>
    public static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            quoted.Append(character switch
            {
                '\\' => @"\\",
                '\'' => @"\'",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => character.ToString(),
            });
        }

        return quoted.Append('\'').ToString();
    }

    /// <summary>Why the rows cannot be written as a condition, or <see langword="null"/>.</summary>
    /// <param name="condition">The condition.</param>
    public static string? Refusal(GuidedCondition condition)
    {
        if (condition.Rows.Select(RowRefusal).FirstOrDefault(refusal => refusal is not null) is { } refusal)
        {
            return refusal;
        }

        var length = Generate(condition).Length;
        return length > ConditionTable.MaxConditionLength
            ? $"These conditions make {length} characters; a condition holds at most {ConditionTable.MaxConditionLength}."
            : null;
    }

    /// <summary>The condition as the recognizer returns it: one joiner for one row, no image where a row reads none.</summary>
    /// <param name="condition">The condition.</param>
    public static GuidedCondition Normalize(GuidedCondition condition)
        => new(condition.All || condition.Rows.Count < 2, [.. condition.Rows.Select(NormalizeRow)]);

    private static ConditionRow NormalizeRow(ConditionRow row)
    {
        var operand = ConditionTable.Of(row.Operator).Operand;
        var imageless = operand == OperandKind.Role || row.Operator == ConditionOperator.Changed;
        return row with
        {
            Image = imageless ? RowImage.New : row.Image,
            Kind = operand == OperandKind.Role ? FieldKind.Text : row.Kind,
            Value = operand == OperandKind.None ? string.Empty : row.Value,
        };
    }

    private static string? RowRefusal(ConditionRow row) => ConditionTable.Of(row.Operator).Operand switch
    {
        OperandKind.Literal when row.Kind == FieldKind.Number && !NumberLiteral().IsMatch(row.Value)
            => $"'{row.Value}' is not a number a condition can hold: write digits, with a point for a decimal, such as 12 or 4.5. "
               + "A negative number cannot be written in a condition in this build.",
        OperandKind.Literal when row.Value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'))
            => "This value holds a control character a condition cannot spell. Write the condition in text mode.",
        OperandKind.Role when row.Value.Length == 0 => "Choose a role.",
        _ => null,
    };

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLiteral();
}
```

- [ ] **Step 4: Run, normalise, run again** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ConditionTextTests'`. Expected: PASS. `scripts/test-ring0`.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/ConditionText.cs test/MMLib.Alvo.Admin.Tests/Schema/ConditionTextTests.cs
git commit -m "feat(admin): write a guided condition as canonical CEL from the one table

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 17: the strict recognizer, and the round trip

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/ConditionText.Recognize.cs`
- Modify: `test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj` (add `<PackageReference Include="CsCheck" />` beside NSubstitute; the version is central in `Directory.Packages.props:29`)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/ConditionTextRecognitionTests.cs`, `test/MMLib.Alvo.Admin.Tests/Schema/ConditionTextRoundTripTests.cs`

**Interfaces:**
- Consumes: Task 16's `ConditionText` (`Generate`, `Normalize`, `And`, `Or`, `NumberLiteral()`), `ConditionScope`, `ConditionRow`, `GuidedCondition`; `ConditionTable` (Task 1).
- Produces: `static GuidedCondition? ConditionText.Recognize(string text, string point, ConditionScope scope)` — rows only when `Generate(result) == text`, otherwise `null` (text mode).

- [ ] **Step 1: Write the failing recognition tests** (`ConditionTextRecognitionTests.cs`):

```csharp
using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The strict recognizer: a condition is rows only when it is exactly what the generator writes (spec §7.3, D5).</summary>
public class ConditionTextRecognitionTests
{
    private static readonly ConditionScope _scope = new(
    [
        new("status", FieldKind.Choice, false, ["received", "ready", "collected", "cancelled"]),
        new("priority", FieldKind.Choice, false, ["normal", "express"]),
        new("quantity", FieldKind.Number, false, []),
        new("discount", FieldKind.Number, true, []),
        new("title", FieldKind.Text, false, []),
        new("note", FieldKind.Text, true, []),
        new("notify_customer", FieldKind.Flag, true, []),
        new("owner_id", FieldKind.Identity, true, []),
        new("due_on", FieldKind.Moment, true, []),
    ],
    ["manager", "reception"]);

    [Theory]
    [InlineData("beforeUpdate", "old.status == 'collected' && new.status != 'collected'")]
    [InlineData("beforeCreate", "new.quantity <= 0")]
    [InlineData("beforeDelete", "old.status != 'received' && old.status != 'cancelled'")]
    [InlineData("afterCreate", "new.priority == 'express'")]
    [InlineData("beforeUpdate", "changed(status) && new.status == 'ready'")]
    [InlineData("beforeUpdate", "!has(new.note) || 'reception' in @user.roles")]
    [InlineData("beforeUpdate", "new.title == 'a && b' && new.title != 'it\\'s'")]
    [InlineData("beforeUpdate", "")]
    public void A_canonical_condition_is_read_as_rows_that_write_it_back(string point, string text)
        => ConditionText.Generate(ConditionText.Recognize(text, point, _scope).ShouldNotBeNull()).ShouldBe(text);

    [Theory]
    [InlineData("beforeUpdate", "new.status == \"ready\"", "double quotes are not the generator's")]
    [InlineData("beforeUpdate", "new.status=='ready'", "spacing")]
    [InlineData("beforeUpdate", "new.status == 'ready' && new.quantity > 1 || new.title == 'x'", "a mix of && and ||")]
    [InlineData("beforeUpdate", "new.nope == 'x'", "an undeclared field")]
    [InlineData("beforeCreate", "old.status == 'ready'", "an image the point lacks")]
    [InlineData("beforeCreate", "changed(status)", "changed outside an update")]
    [InlineData("afterCreate", "'manager' in @user.roles", "a role after the commit")]
    [InlineData("beforeUpdate", "'nobody' in @user.roles", "an undeclared role")]
    [InlineData("beforeUpdate", "new.status == 'bogus'", "an enum value not declared")]
    [InlineData("beforeUpdate", "new.quantity == -1", "a negative number")]
    [InlineData("beforeUpdate", "new.title < 'a'", "string relational")]
    [InlineData("beforeUpdate", "has(new.title)", "presence on a required field")]
    [InlineData("afterUpdate", "new.status == 'ready' && old.status != 'ready' && new.notify_customer", "a bare boolean (spec D5)")]
    [InlineData("beforeUpdate", "new.title == 'a\\x'", "an escape the lexer does not read")]
    [InlineData("beforeUpdate", " ", "whitespace is not the empty condition")]
    [InlineData("beforeUpdate", "new.title == 'open", "an unclosed quote")]
    public void A_text_the_generator_would_not_write_stays_text(string point, string text, string because)
        => ConditionText.Recognize(text, point, _scope).ShouldBeNull(because);
}
```

- [ ] **Step 2: Write the failing property tests** (`ConditionTextRoundTripTests.cs`):

```csharp
using CsCheck;
using MMLib.Alvo.Admin.Components.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// <c>Recognize(Generate(c)) == c</c> and <c>Generate(Recognize(t)) == t</c> for every condition the generator can write,
/// over hostile values (spec §7.3, acceptance criterion 4: 2,000 cases, none failing).
/// </summary>
public sealed class ConditionTextRoundTripTests
{
    private const string Alphabet = "ab '\\\"&|!()\n\t\rčé中 ";

    private static readonly ConditionScope _scope = new(
    [
        new("title", FieldKind.Text, false, []),
        new("note", FieldKind.Text, true, []),
        new("status", FieldKind.Choice, false, ["open", "it's", "a && b", "čaj", "back\\slash"]),
        new("tier", FieldKind.Choice, true, ["gold"]),
        new("quantity", FieldKind.Number, false, []),
        new("discount", FieldKind.Number, true, []),
        new("paid", FieldKind.Flag, true, []),
        new("due_on", FieldKind.Moment, true, []),
        new("owner_id", FieldKind.Identity, true, []),
        new("extra", FieldKind.Json, true, []),
    ],
    ["manager", "it's-ok"]);

    [Fact]
    public void Every_condition_the_generator_writes_is_read_back_as_itself()
        => Gen.Select(Gen.Int, Gen.OneOfConst(HookBuilder.Points.ToArray())).Sample(
            (seed, point) => RoundTrips(Condition(new Random(seed), point), point),
            iter: 2_000);

    [Fact]
    public void A_generated_text_changed_by_one_character_is_read_as_rows_only_if_it_is_still_canonical()
        => Gen.Select(Gen.Int, Gen.OneOfConst(HookBuilder.Points.ToArray())).Sample(
            (seed, point) =>
            {
                var random = new Random(seed);
                var changed = Mutated(random, ConditionText.Generate(Condition(random, point)));
                return ConditionText.Recognize(changed, point, _scope) is not { } rows || ConditionText.Generate(rows) == changed;
            },
            iter: 2_000);

    private static bool RoundTrips(GuidedCondition condition, string point)
    {
        var text = ConditionText.Generate(condition);
        var back = ConditionText.Recognize(text, point, _scope);
        return back is not null && ConditionText.Generate(back) == text && back.Equals(ConditionText.Normalize(condition));
    }

    private static GuidedCondition Condition(Random random, string point)
    {
        var candidates = Candidates(point);
        var rows = Enumerable.Range(0, random.Next(1, 5)).Select(_ => Row(random, point, candidates[random.Next(candidates.Count)])).ToList();
        return new GuidedCondition(random.Next(2) == 0, rows);
    }

    private static List<(ConditionField? Field, OperatorSpec Spec)> Candidates(string point)
        =>
        [
            .. _scope.Fields.SelectMany(field => ConditionTable.For(point, field.Kind, field.Nullable).Select(spec => ((ConditionField?)field, spec))),
            .. ConditionTable.ForWriter(point).Select(spec => ((ConditionField?)null, spec)),
        ];

    private static ConditionRow Row(Random random, string point, (ConditionField? Field, OperatorSpec Spec) pick)
    {
        if (pick.Field is not { } field)
        {
            return new ConditionRow(pick.Spec.Operator, RowImage.New, ConditionTable.Writer, FieldKind.Text, _scope.Roles[random.Next(_scope.Roles.Count)]);
        }

        var images = ConditionTable.ImagesAt(point);
        return new ConditionRow(pick.Spec.Operator, images[random.Next(images.Count)], field.Name, field.Kind, Value(random, pick.Spec, field));
    }

    private static string Value(Random random, OperatorSpec spec, ConditionField field) => spec.Operand != OperandKind.Literal
        ? string.Empty
        : field.Kind switch
        {
            FieldKind.Number => random.Next(2) == 0
                ? random.Next(100_000).ToString(CultureInfo.InvariantCulture)
                : $"{random.Next(1000).ToString(CultureInfo.InvariantCulture)}.{random.Next(100).ToString(CultureInfo.InvariantCulture)}",
            FieldKind.Choice => field.Values[random.Next(field.Values.Count)],
            _ => new string([.. Enumerable.Range(0, random.Next(0, 13)).Select(_ => Alphabet[random.Next(Alphabet.Length)])]),
        };

    private static string Mutated(Random random, string text)
    {
        var at = random.Next(text.Length + 1);
        return at < text.Length && random.Next(2) == 0
            ? text.Remove(at, 1)
            : text.Insert(at, Alphabet[random.Next(Alphabet.Length)].ToString());
    }
}
```

- [ ] **Step 3: Run to verify failure** — add the `CsCheck` package reference, then `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ConditionTextRecognitionTests' --filter-class '*ConditionTextRoundTripTests'`. Expected: build FAIL — `Recognize` is not defined.

- [ ] **Step 4: Implement `ConditionText.Recognize.cs`**

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The strict recognizer: a condition is rows only when it is byte for byte what Generate writes (spec §7.3, D5). */
internal static partial class ConditionText
{
    private const string FieldName = "[a-z][a-z0-9_]{0,62}";
    private const string QuotedText = @"'(?:[^'\\]|\\.)*'";
    private const string Number = @"(?:0|[1-9][0-9]*)(?:\.[0-9]+)?";

    private static readonly Dictionary<ConditionOperator, Regex> _patterns =
        ConditionTable.Rows.ToDictionary(spec => spec.Operator, spec => PatternOf(spec.Format));

    /// <summary>
    /// The rows a condition is, when it is exactly what the generator writes for this point and scope; otherwise
    /// <see langword="null"/>, and the condition stays text.
    /// </summary>
    /// <param name="text">The condition's CEL.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="scope">The entity's fields and the declared roles.</param>
    public static GuidedCondition? Recognize(string text, string point, ConditionScope scope)
    {
        if (text.Length == 0)
        {
            return GuidedCondition.Empty;
        }

        if (Split(text) is not { } split)
        {
            return null;
        }

        var rows = new List<ConditionRow>(split.Parts.Count);
        foreach (var part in split.Parts)
        {
            if (RecognizeRow(part, point, scope) is not { } row)
            {
                return null;
            }

            rows.Add(row);
        }

        var condition = new GuidedCondition(split.All || rows.Count < 2, rows);
        return string.Equals(Generate(condition), text, StringComparison.Ordinal) ? condition : null;
    }

    /// <summary>The parts between the top-level joiners, outside quotes; <see langword="null"/> for a mix or an open quote.</summary>
    private static (bool All, List<string> Parts)? Split(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        string? joiner = null;
        for (var index = 0; index < text.Length; index++)
        {
            if (quoted)
            {
                if (text[index] == '\\')
                {
                    index++;
                }
                else if (text[index] == '\'')
                {
                    quoted = false;
                }

                continue;
            }

            if (text[index] == '\'')
            {
                quoted = true;
                continue;
            }

            if (JoinerAt(text, index) is not { } found)
            {
                continue;
            }

            if (joiner is not null && joiner != found)
            {
                return null;
            }

            joiner = found;
            parts.Add(text[start..index]);
            start = index + found.Length;
            index = start - 1;
        }

        parts.Add(text[start..]);
        return quoted ? null : (joiner != Or, parts);
    }

    private static string? JoinerAt(string text, int index)
        => text.AsSpan(index).StartsWith(And, StringComparison.Ordinal) ? And
            : text.AsSpan(index).StartsWith(Or, StringComparison.Ordinal) ? Or
            : null;

    private static ConditionRow? RecognizeRow(string part, string point, ConditionScope scope)
    {
        foreach (var spec in ConditionTable.Rows)
        {
            if (_patterns[spec.Operator].Match(part) is { Success: true } match && Resolve(spec, match, point, scope) is { } row)
            {
                return row;
            }
        }

        return null;
    }

    private static ConditionRow? Resolve(OperatorSpec spec, Match match, string point, ConditionScope scope)
    {
        var raw = match.Groups["value"].Success ? match.Groups["value"].Value : null;
        if (spec.Operand == OperandKind.Role)
        {
            return ConditionTable.ForWriter(point).Contains(spec) && Unquote(raw!) is { } role && scope.Roles.Contains(role, StringComparer.Ordinal)
                ? new ConditionRow(spec.Operator, RowImage.New, ConditionTable.Writer, FieldKind.Text, role)
                : null;
        }

        if (scope.Field(match.Groups["field"].Value) is not { } field || !ConditionTable.Allows(spec, point, field.Kind, field.Nullable))
        {
            return null;
        }

        var image = match.Groups["image"].Value == "old" ? RowImage.Old : RowImage.New;
        if (match.Groups["image"].Success && !ConditionTable.ImagesAt(point).Contains(image))
        {
            return null;
        }

        return ValueOf(spec, field, raw) is { } value ? new ConditionRow(spec.Operator, image, field.Name, field.Kind, value) : null;
    }

    private static string? ValueOf(OperatorSpec spec, ConditionField field, string? raw)
    {
        if (spec.Operand == OperandKind.None)
        {
            return string.Empty;
        }

        if (field.Kind == FieldKind.Number)
        {
            return raw is not null && NumberLiteral().IsMatch(raw) ? raw : null;
        }

        if (raw is null || !raw.StartsWith('\'') || Unquote(raw) is not { } text)
        {
            return null;
        }

        return field.Kind != FieldKind.Choice || field.Values.Contains(text, StringComparer.Ordinal) ? text : null;
    }

    /// <summary>A quoted literal's text, or <see langword="null"/> for an escape the lexer does not read.</summary>
    private static string? Unquote(string quoted)
    {
        var text = new StringBuilder(quoted.Length);
        for (var index = 1; index < quoted.Length - 1; index++)
        {
            if (quoted[index] != '\\')
            {
                text.Append(quoted[index]);
                continue;
            }

            if (++index >= quoted.Length - 1 || Unescape(quoted[index]) is not { } character)
            {
                return null;
            }

            text.Append(character);
        }

        return text.ToString();
    }

    private static char? Unescape(char escaped) => escaped switch
    {
        '\\' => '\\',
        '\'' => '\'',
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        _ => null,
    };

    /// <summary>An anchored regex of one table format, its parts named <c>image</c>, <c>field</c> and <c>value</c>.</summary>
    private static Regex PatternOf(string format)
    {
        var pattern = Regex.Escape(format)
            .Replace(@"\{f}", $@"(?<image>new|old)\.(?<field>{FieldName})", StringComparison.Ordinal)
            .Replace(@"\{n}", $"(?<field>{FieldName})", StringComparison.Ordinal)
            .Replace(@"\{v}", $"(?<value>{QuotedText}|{Number})", StringComparison.Ordinal)
            .Replace(@"\{r}", $"(?<value>{QuotedText})", StringComparison.Ordinal);
        return new Regex($"^{pattern}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
```

- [ ] **Step 5: Run, normalise, run again** — `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ConditionText*'`. Expected: PASS, the property tests with 2,000 iterations each. If a round trip fails, CsCheck prints the shrunk seed and point: reproduce with `Condition(new Random(seed), point)` in a scratch fact — the fix is in the generator or the recognizer, never a narrower generator. `Split` is ~40 lines: extract the quoted-state step into `private static bool StillQuoted(string text, ref int index)` to meet the ~25-line ceiling (same behaviour; the property tests guard it). `scripts/test-ring0`.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/ConditionText.Recognize.cs test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj test/MMLib.Alvo.Admin.Tests/Schema/ConditionTextRecognitionTests.cs test/MMLib.Alvo.Admin.Tests/Schema/ConditionTextRoundTripTests.cs
git commit -m "feat(admin): read a condition back as rows only when it is exactly what the rows write

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 18: every table cell against the real validator

**Files:**
- Test: `test/MMLib.Alvo.Host.Tests/GuidedConditionConformanceTests.cs`

**Interfaces:**
- Consumes: `ConditionTable`, `ConditionText`, `ConditionScope`, `ConditionRow`, `GuidedCondition` (Tasks 1, 16, 17); `PendingSchema.Read` (Admin internal); `DescriptorValidator` (core internal).
- Produces: nothing used later.

- [ ] **Step 1: Write the facts**

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>Everything the guided condition offers, the apply accepts; what it withholds before the commit, the apply refuses.</b>
/// </summary>
/// <remarks>
/// <c>ConditionTable</c> is the one source the form, the generator and the recognizer read (spec §7.1). This compiles every
/// cell — point × field kind × nullability × relation × image — through the real <c>DescriptorValidator</c>, the code
/// path apply runs (the hook compilers' phase and envelope rules included, which <c>CelTypeChecker</c> alone would miss),
/// so the table cannot drift from the core without this failing (spec §7.4, acceptance criterion 4).
/// </remarks>
public sealed class GuidedConditionConformanceTests
{
    private static readonly (string Name, string Declaration)[] _fields =
    [
        ("text_req", """{"type":"string","required":true}"""),
        ("text_opt", """{"type":"text"}"""),
        ("choice_req", """{"type":"enum","values":["open","it's"],"required":true}"""),
        ("choice_opt", """{"type":"enum","values":["open","it's"]}"""),
        ("number_req", """{"type":"integer","required":true}"""),
        ("number_opt", """{"type":"decimal","precision":12,"scale":2}"""),
        ("flag_req", """{"type":"boolean","required":true}"""),
        ("flag_opt", """{"type":"boolean"}"""),
        ("moment_req", """{"type":"datetime","required":true}"""),
        ("moment_opt", """{"type":"date"}"""),
        ("identity_req", """{"type":"uuid","required":true}"""),
        ("identity_opt", """{"type":"uuid"}"""),
        ("json_opt", """{"type":"json"}"""),
    ];

    [Fact]
    public void Every_combination_the_form_offers_is_accepted_by_apply()
    {
        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var point in HookBuilder.Points)
        {
            foreach (var row in Offered(point))
            {
                var condition = ConditionText.Generate(new GuidedCondition(true, [row]));
                checkedCount++;
                if (ErrorsAt(point, condition) is [var first, ..])
                {
                    failures.Add($"{point}: {condition} — {first.Message}");
                }
            }
        }

        checkedCount.ShouldBeGreaterThan(200, "the probe covers every kind at every point");
        failures.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("beforeCreate", "old.text_opt == 'x'")]
    [InlineData("beforeDelete", "new.text_opt == 'x'")]
    [InlineData("beforeCreate", "changed(text_opt)")]
    [InlineData("beforeDelete", "changed(text_opt)")]
    [InlineData("afterCreate", "'clerk' in @user.roles")]
    [InlineData("afterUpdate", "!('clerk' in @user.roles)")]
    public void What_the_table_withholds_before_the_commit_or_from_an_envelope_apply_refuses(string point, string condition)
    {
        ErrorsAt(point, condition).ShouldNotBeEmpty($"{condition} at {point}");
        ConditionText.Recognize(condition, point, Scope(point)).ShouldBeNull("the form never offers it");
    }

    /// <summary>
    /// Where the table is narrower than apply — recorded, so the spec §7.1 "unverified" becomes a measured fact. If apply
    /// starts refusing these, move them to the theory above and drop the sentence from the spec.
    /// </summary>
    [Theory]
    [InlineData("afterCreate", "old.text_opt == 'x'")]
    [InlineData("afterDelete", "new.text_opt == 'x'")]
    public void What_the_table_withholds_after_the_commit_apply_still_accepts(string point, string condition)
    {
        ErrorsAt(point, condition).ShouldBeEmpty($"{condition} at {point}");
        ConditionText.Recognize(condition, point, Scope(point)).ShouldBeNull("the form never offers an image the point lacks");
    }

    private static IEnumerable<ConditionRow> Offered(string point)
    {
        var scope = Scope(point);
        foreach (var field in scope.Fields)
        {
            foreach (var spec in ConditionTable.For(point, field.Kind, field.Nullable))
            {
                foreach (var row in Rows(spec, field, point))
                {
                    yield return row;
                }
            }
        }

        foreach (var spec in ConditionTable.ForWriter(point))
        {
            yield return new ConditionRow(spec.Operator, RowImage.New, ConditionTable.Writer, FieldKind.Text, "clerk");
        }
    }

    private static IEnumerable<ConditionRow> Rows(OperatorSpec spec, ConditionField field, string point)
    {
        IReadOnlyList<RowImage> images = spec.Operator == ConditionOperator.Changed ? [RowImage.New] : ConditionTable.ImagesAt(point);
        foreach (var image in images)
        {
            foreach (var value in Samples(spec, field))
            {
                yield return new ConditionRow(spec.Operator, image, field.Name, field.Kind, value);
            }
        }
    }

    private static string[] Samples(OperatorSpec spec, ConditionField field) => spec.Operand != OperandKind.Literal
        ? [string.Empty]
        : field.Kind switch
        {
            FieldKind.Number => ["12", "4.5"],
            FieldKind.Choice => ["it's"],
            _ => ["it's \\ \"quoted\"\n"],
        };

    private static ConditionScope Scope(string point)
        => ConditionScope.From(PendingSchema.Read(Probe(point, "true"), "probe"), ["clerk"]);

    private static List<DescriptorValidationError> ErrorsAt(string point, string condition)
    {
        var prefix = $"/entities/probe/hooks/{point}/0";
        return [.. new DescriptorValidator().Validate(Probe(point, condition)).Errors
            .Where(error => error.Severity == DescriptorValidationSeverity.Error)
            .Where(error => error.Path.StartsWith(prefix, StringComparison.Ordinal) || error.Path.StartsWith("#" + prefix, StringComparison.Ordinal))];
    }

    private static string Probe(string point, string condition)
    {
        var fields = new JsonObject();
        foreach (var (name, declaration) in _fields)
        {
            fields[name] = JsonNode.Parse(declaration);
        }

        JsonNode action = HookBuilder.IsBefore(point)
            ? new JsonObject { ["reject"] = "No." }
            : new JsonObject { ["type"] = "webhook", ["endpoint"] = "probe-desk" };
        return new JsonObject
        {
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = "probe",
            ["auth"] = new JsonObject { ["roles"] = new JsonArray("clerk") },
            ["webhooks"] = JsonNode.Parse("""{"endpoints":{"probe-desk":{"url":"https://example.com/hook","secretRef":"probe-desk-key"}}}"""),
            ["entities"] = new JsonObject
            {
                ["probe"] = new JsonObject
                {
                    ["fields"] = fields,
                    ["hooks"] = new JsonObject { [point] = new JsonArray(new JsonObject { ["condition"] = condition, ["action"] = action }) },
                },
            },
        }.ToJsonString();
    }
}
```

- [ ] **Step 2: Run** — `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*GuidedConditionConformanceTests'`. Expected: PASS. A failing cell is a **finding about the table**: remove or narrow that cell in `ConditionTable` (and say why in its row's comment and spec §7.1), never filter it out of the fact. If `== @user.id` fails on an `Identity` field ("Cannot compare …"), `@user.id` is not typed `Uuid` in this build: drop `IsTheWriter` from the table and record it in spec §7.1. If the second theory's `changed` rows pass apply at a create/delete point, apply has no phase rule for them there — move them to the third theory and record it. Record the printed count of checked cells for spec §17. Normalise; `scripts/test-ring0`.

- [ ] **Step 3: Commit**

```bash
git add test/MMLib.Alvo.Host.Tests/GuidedConditionConformanceTests.cs
git commit -m "test(admin): every guided condition the table offers compiles in the real validator, and what it withholds is refused

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 19: the guided rows and the switch to text

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/ConditionBuilder.razor`, `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Condition.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor` (replace `ConditionField`), `HooksTab.razor.cs` (`OpenNew`, `Choose`), `HooksTab.Edit.cs` (`OpenEdit`)
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs` (`TypeConditionAsync` switches to Text first)
- Modify: the scenarios that fill `#hook-condition` directly — `ExpressionCheckScenarios.cs:74,80,165`, `SchemaEditingScenarios.cs:286,486,516`, `SystemMapScenarios.cs:210` → `session.TypeConditionAsync(…)`; `HookEditInPlaceScenarios.The_edit_sheet_is_titled_by_the_hook_and_keeps_its_point` (reads the readout)
- Modify: `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt` (regenerated)
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/GuidedConditionScenarios.cs`

**Interfaces:**
- Consumes: `ConditionText`, `ConditionScope`, `ConditionRow`, `GuidedCondition` (Tasks 16, 17), `ConditionTable` (Task 1), `PendingSchema.Read`, `DescriptorLens.DeclaredRoles`, Task 8's `Current`, `TypeCondition`, `Findings`, `ConditionLength`, `ConditionDescribedBy`, `_check`.
- Produces: public component `ConditionBuilder` (`[Parameter, EditorRequired] string Point`, `[Parameter, EditorRequired] string Entity`, `[Parameter] string Value`, `[Parameter] EventCallback<string> ValueChanged`); test ids `hook-condition-mode`, `hook-condition-note`, `hook-condition-readout`, `condition-joiner`, `condition-row`, `condition-{i}-field`, `condition-{i}-image`, `condition-{i}-operator`, `condition-{i}-value`, `condition-{i}-remove`, `condition-add`, `condition-refusal`; labels "Condition {n} field", "Condition {n} reads", "Condition {n} operator", "Condition {n} value", "Condition {n} role".

- [ ] **Step 1: Write the failing e2e scenarios** (`GuidedConditionScenarios.cs`):

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The guided condition: rows that write ordinary CEL, a readout the build checks, and a switch to text for anything the
/// rows cannot say (spec §4.6, §7; ruling B7). Each scenario uses a different entity or point: the class shares one copy.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class GuidedConditionScenarios(BikeWorkshopWorld world) : IClassFixture<BikeWorkshopWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Rows_write_the_condition_and_the_hook_is_added_with_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "service_orders");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = "beforeUpdate", Exact = true }).ClickAsync();

        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "status");
        await session.ChooseAsync(Combobox(session, "Condition 1 value"), "ready");
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 2 field"), "The person writing");
        await session.ChooseAsync(Combobox(session, "Condition 2 role"), "manager");

        var readout = session.Page.GetByTestId("hook-condition-readout");
        (await readout.InnerTextAsync()).ShouldBe("new.status == 'ready' && 'manager' in @user.roles");
        await session.Page.FillAsync("#hook-reject", "Only a manager marks an order ready.");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        (await session.Page.Locator("#hook-beforeUpdate-2").InnerTextAsync()).ShouldContain("new.status == 'ready' && 'manager' in @user.roles");
        await session.GoAsync("/changes");
        await session.WaitForPlanAsync();
        await session.Page.Locator("#apply-reason").WaitForAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_canonical_condition_opens_as_rows_and_a_hand_written_one_as_text()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "order_lines");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "beforeCreate", 0);

        (await Mode(session, "Guided").CountAsync()).ShouldBe(1);
        (await session.Page.GetByTestId("hook-condition-readout").InnerTextAsync()).ShouldBe("new.quantity <= 0");
        (await Combobox(session, "Condition 1 operator").InnerTextAsync()).ShouldContain("is at most");
        await session.Page.Keyboard.PressAsync("Escape");
        await session.Dialog("hook-editor").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await HookEditInPlaceScenarios.OnWriteAsync(session, "service_orders");
        await HookEditInPlaceScenarios.OpenEditAsync(session, "afterUpdate", 0);
        (await Mode(session, "Text").CountAsync()).ShouldBe(1, "a bare boolean is not a row (spec D5)");
        (await session.Page.InputValueAsync("input#hook-condition")).ShouldContain("new.notify_customer");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Text_that_cannot_be_rows_stays_text_and_says_why()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "rentals");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.TypeConditionAsync("new.status == \"reserved\"");

        await session.Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = "Guided", Exact = true }).ClickAsync();

        (await session.Page.GetByTestId("hook-condition-note").InnerTextAsync()).ShouldContain("cannot be shown as rows");
        (await Mode(session, "Text").CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_role_row_is_not_offered_after_the_commit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "bikes");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = "afterCreate", Exact = true }).ClickAsync();
        await session.Page.GetByTestId("condition-add").ClickAsync();

        await Combobox(session, "Condition 1 field").ClickAsync();
        (await Option(session, "The person writing").CountAsync()).ShouldBe(0, "an event envelope carries no roles");
        (await Option(session, "brand").CountAsync()).ShouldBe(1);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_negative_number_is_refused_in_the_rows()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "order_lines");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "quantity");

        await session.Page.FillAsync("#condition-0-value", "-5");

        var refusal = session.Page.GetByTestId("condition-refusal");
        await refusal.WaitForAsync();
        (await refusal.InnerTextAsync()).ShouldContain("negative number");
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_guided_rows_fit_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, 375);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "customers");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "first_name");
        await session.Page.GetByTestId("condition-add").ClickAsync();

        await session.AssertNoHorizontalScrollAsync();
    }

    private static ILocator Mode(AdminSession session, string name)
        => session.Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = name, Exact = true, Checked = true });

    private static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });

    private static ILocator Option(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });
}
```

- [ ] **Step 2: Run to verify failure** — `scripts/test-admin-e2e --filter-class '*GuidedConditionScenarios'`. Expected: FAIL — no `condition-add`.

- [ ] **Step 3: Create `ConditionBuilder.razor`**

```razor
@*
    The guided form of a hook condition (spec §4.6, §7; ruling B7): rows of field, operator and value that write ordinary
    CEL through ConditionText. ConditionTable decides what each row may offer; cel/check and the apply judge the text it
    writes — the parent shows that text (the Expression readout) and the switch to text mode. A row is written once it
    has what its relation needs; until then it is not part of the condition, which the readout shows.
*@

<Field Label="Match" LabelId="condition-joiner-label">
    <ChipGroup TValue="string" Items="_joiners" Selected="[_all ? AllWord : AnyWord]" aria-labelledby="condition-joiner-label"
               data-testid="condition-joiner" SelectedChanged="words => ChooseJoinerAsync(words[0])" />
</Field>

@for (var index = 0; index < _rows.Count; index++)
{
    var row = _rows[index];
    var i = index;
    <div class="a-stack" role="group" aria-label="@($"Condition {i + 1}")" data-testid="condition-row">
        <div class="a-field-row">
            <Field Label="@($"Condition {i + 1} field")" LabelId="@($"condition-{i}-field-label")">
                <MudSelect T="string" Variant="Variant.Outlined" Value="row.Field" ValueChanged="field => ChooseFieldAsync(i, field)"
                           ToStringFunc="FieldWords" aria-labelledby="@($"condition-{i}-field-label")" Class="a-mono"
                           data-testid="@($"condition-{i}-field")">
                    @if (ConditionTable.ForWriter(Point).Count > 0)
                    {
                        <MudSelectItem Value="@ConditionTable.Writer">The person writing</MudSelectItem>
                    }
                    @foreach (var field in OfferedFields)
                    {
                        <MudSelectItem Value="@field.Name">@field.Name</MudSelectItem>
                    }
                </MudSelect>
            </Field>
            @if (ShowsImage(row))
            {
                <Field Label="@($"Condition {i + 1} reads")" LabelId="@($"condition-{i}-image-label")">
                    <ChipGroup TValue="string" Items="_images" Selected="[row.Image == RowImage.New ? NowWord : BeforeWord]"
                               aria-labelledby="@($"condition-{i}-image-label")" data-testid="@($"condition-{i}-image")"
                               SelectedChanged="words => ChooseImageAsync(i, words[0])" />
                </Field>
            }
            <Field Label="@($"Condition {i + 1} operator")" LabelId="@($"condition-{i}-operator-label")">
                <MudSelect T="string" Variant="Variant.Outlined" Value="row.Operator?.ToString()" ValueChanged="op => ChooseOperatorAsync(i, op)"
                           ToStringFunc="OperatorWords" aria-labelledby="@($"condition-{i}-operator-label")"
                           data-testid="@($"condition-{i}-operator")" Disabled="@(row.Field.Length == 0)">
                    @foreach (var spec in OperatorsFor(row))
                    {
                        <MudSelectItem Value="@spec.Operator.ToString()">@spec.Words</MudSelectItem>
                    }
                </MudSelect>
            </Field>
            @ValueInput(row, i)
        </div>
        @if (row.Operator is { } relation && row.Field != ConditionTable.Writer)
        {
            <span class="a-hint">When the field is empty, this row is @ConditionTable.Of(relation).WhenEmpty.</span>
        }
        <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="@($"condition-{i}-remove")"
                    aria-label="@($"Remove condition {i + 1}")" OnClick="_ => RemoveRowAsync(i)">Remove</AlvoButton>
    </div>
}
<AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="condition-add" OnClick="AddRow">Add a condition</AlvoButton>
@if (ConditionText.Refusal(Condition()) is { } refusal)
{
    <p class="a-field__problem" id="condition-refusal" data-testid="condition-refusal">@refusal</p>
}

@code {
    private const string AllWord = "all";
    private const string AnyWord = "any";
    private const string NowWord = "now";
    private const string BeforeWord = "before";
    private static readonly string[] _joiners = [AllWord, AnyWord];
    private static readonly string[] _images = [NowWord, BeforeWord];
    private readonly List<Draft> _rows = [];
    private ConditionScope _scope = new([], []);
    private bool _all = true;
    private string? _emitted;

    /// <summary>The hook point, which decides what a row may offer.</summary>
    [Parameter, EditorRequired]
    public string Point { get; set; } = "beforeCreate";

    /// <summary>The entity whose fields the rows name.</summary>
    [Parameter, EditorRequired]
    public string Entity { get; set; } = string.Empty;

    /// <summary>The condition's CEL; read as rows when it is exactly what the rows write.</summary>
    [Parameter]
    public string Value { get; set; } = string.Empty;

    /// <summary>Raised with the CEL the rows write, after every change.</summary>
    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    /// <summary>The working copy the fields and roles are read from — cascaded, so its model stays internal.</summary>
    [CascadingParameter]
    private WorkingCopy? Copy { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _scope = Copy is { } copy
            ? ConditionScope.From(PendingSchema.Read(copy.Json, Entity), DescriptorLens.DeclaredRoles(copy.Json))
            : new ConditionScope([], []);
        if (Value != _emitted && ConditionText.Recognize(Value, Point, _scope) is { } condition)
        {
            _all = condition.All;
            _rows.Clear();
            _rows.AddRange(condition.Rows.Select(Draft.Of));
        }

        _emitted = Value;
    }

    private IEnumerable<ConditionField> OfferedFields
        => _scope.Fields.Where(field => ConditionTable.For(Point, field.Kind, field.Nullable).Count > 0);

    private IReadOnlyList<OperatorSpec> OperatorsFor(Draft row) => row.Field == ConditionTable.Writer
        ? ConditionTable.ForWriter(Point)
        : _scope.Field(row.Field) is { } field ? ConditionTable.For(Point, field.Kind, field.Nullable) : [];

    private bool ShowsImage(Draft row)
        => row.Field.Length > 0 && row.Field != ConditionTable.Writer && row.Operator != ConditionOperator.Changed
           && ConditionTable.ImagesAt(Point).Count > 1;

    private static string? FieldWords(string? field) => field == ConditionTable.Writer ? "The person writing" : field;

    private static string? OperatorWords(string? relation)
        => Enum.TryParse<ConditionOperator>(relation, out var parsed) ? ConditionTable.Of(parsed).Words : relation;

    private OperandKind OperandOf(Draft row) => row.Operator is { } relation ? ConditionTable.Of(relation).Operand : OperandKind.None;

    private void AddRow() => _rows.Add(new Draft { Image = ConditionTable.ImagesAt(Point).FirstOrDefault() });

    private Task RemoveRowAsync(int index)
    {
        _rows.RemoveAt(index);
        return EmitAsync();
    }

    private Task ChooseJoinerAsync(string word)
    {
        _all = word == AllWord;
        return EmitAsync();
    }

    /// <summary>A new field takes its first allowed relation and an empty value.</summary>
    private Task ChooseFieldAsync(int index, string? field)
    {
        var row = _rows[index];
        row.Field = field ?? string.Empty;
        row.Operator = OperatorsFor(row).FirstOrDefault()?.Operator;
        row.Value = string.Empty;
        return EmitAsync();
    }

    private Task ChooseImageAsync(int index, string word)
    {
        _rows[index].Image = word == NowWord ? RowImage.New : RowImage.Old;
        return EmitAsync();
    }

    private Task ChooseOperatorAsync(int index, string? relation)
    {
        _rows[index].Operator = Enum.TryParse<ConditionOperator>(relation, out var parsed) ? parsed : null;
        return EmitAsync();
    }

    private Task TypeValueAsync(int index, string? value)
    {
        _rows[index].Value = value ?? string.Empty;
        return EmitAsync();
    }

    /// <summary>Writes the rows as CEL and hands it to the parent, which checks it.</summary>
    private Task EmitAsync()
    {
        _emitted = ConditionText.Generate(Condition());
        return ValueChanged.InvokeAsync(_emitted);
    }

    /// <summary>The rows that have what their relation needs, as a condition.</summary>
    private GuidedCondition Condition() => new(_all, [.. _rows.Select(ToRow).OfType<ConditionRow>()]);

    private ConditionRow? ToRow(Draft row)
    {
        if (row.Operator is not { } relation)
        {
            return null;
        }

        var operand = ConditionTable.Of(relation).Operand;
        if (operand == OperandKind.Role)
        {
            return row.Value.Length == 0 ? null : new ConditionRow(relation, RowImage.New, ConditionTable.Writer, FieldKind.Text, row.Value);
        }

        if (_scope.Field(row.Field) is not { } field || (operand == OperandKind.Literal && field.Kind != FieldKind.Text && row.Value.Length == 0))
        {
            return null;
        }

        var image = relation == ConditionOperator.Changed ? RowImage.New : row.Image;
        return new ConditionRow(relation, image, field.Name, field.Kind, operand == OperandKind.Literal ? row.Value : string.Empty);
    }

    /// <summary>The value box a row's relation takes: a role, a declared value, or typed text.</summary>
    private RenderFragment ValueInput(Draft row, int i) => @<text>
        @switch (OperandOf(row))
        {
            case OperandKind.Role:
                <Field Label="@($"Condition {i + 1} role")" LabelId="@($"condition-{i}-value-label")">
                    <MudSelect T="string" Variant="Variant.Outlined" Value="row.Value" ValueChanged="value => TypeValueAsync(i, value)"
                               aria-labelledby="@($"condition-{i}-value-label")" Class="a-mono" data-testid="@($"condition-{i}-value")">
                        @foreach (var role in _scope.Roles)
                        {
                            <MudSelectItem Value="@role">@role</MudSelectItem>
                        }
                    </MudSelect>
                </Field>
                break;
            case OperandKind.Literal when _scope.Field(row.Field) is { Kind: FieldKind.Choice } choice:
                <Field Label="@($"Condition {i + 1} value")" LabelId="@($"condition-{i}-value-label")">
                    <MudSelect T="string" Variant="Variant.Outlined" Value="row.Value" ValueChanged="value => TypeValueAsync(i, value)"
                               aria-labelledby="@($"condition-{i}-value-label")" Class="a-mono" data-testid="@($"condition-{i}-value")">
                        @foreach (var value in choice.Values)
                        {
                            <MudSelectItem Value="@value">@value</MudSelectItem>
                        }
                    </MudSelect>
                </Field>
                break;
            case OperandKind.Literal:
                <Field Label="@($"Condition {i + 1} value")" For="@($"condition-{i}-value")">
                    <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="@($"condition-{i}-value")" Class="a-mono"
                                  autocomplete="off" inputmode="@(_scope.Field(row.Field)?.Kind == FieldKind.Number ? "decimal" : "text")"
                                  Value="row.Value" ValueChanged="value => TypeValueAsync(i, value)" />
                </Field>
                break;
        }
    </text>;

    /// <summary>One row as the form holds it while it is being built.</summary>
    private sealed class Draft
    {
        /// <summary>Gets or sets the field, or <see cref="ConditionTable.Writer"/>.</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>Gets or sets the relation, once the field allows one.</summary>
        public ConditionOperator? Operator { get; set; }

        /// <summary>Gets or sets the image read.</summary>
        public RowImage Image { get; set; } = RowImage.New;

        /// <summary>Gets or sets the value, unquoted.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>A recognized row, as a draft.</summary>
        /// <param name="row">The row.</param>
        public static Draft Of(ConditionRow row) => new() { Field = row.Field, Operator = row.Operator, Image = row.Image, Value = row.Value };
    }
}
```

- [ ] **Step 4: Create `HooksTab.Condition.cs`**

```csharp
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The guided condition and the switch to text (spec §4.6, §7; ruling B7). */
public partial class HooksTab
{
    private const string GuidedWord = "Guided";
    private const string TextWord = "Text";
    private static readonly string[] _conditionModes = [GuidedWord, TextWord];
    private bool _guided = true;
    private string? _conditionNote;

    /// <summary>Text always; rows only when the condition is exactly what the rows write — otherwise it says so and stays text.</summary>
    private void ChooseConditionMode(string mode)
    {
        _conditionNote = null;
        if (mode == TextWord)
        {
            _guided = false;
        }
        else if (Recognizes(Current.Condition))
        {
            _guided = true;
        }
        else
        {
            _conditionNote = "This condition cannot be shown as rows; it stays as text.";
        }
    }

    /// <summary>On open: rows for a condition the rows write (or none), text for anything else (spec §4.2).</summary>
    private void SettleConditionMode()
    {
        _conditionNote = null;
        _guided = Recognizes(Current.Condition);
    }

    /// <summary>After a point change: rows that no longer fit the point fall back to text, and say so.</summary>
    private void KeepConditionModeFitting()
    {
        if (_guided && !Recognizes(Current.Condition))
        {
            _guided = false;
            _conditionNote = $"The rows do not fit {Current.Point}; the condition is kept as text.";
        }
    }

    private bool Recognizes(string condition)
        => Copy is { } copy
           && ConditionText.Recognize(
               condition,
               Current.Point,
               ConditionScope.From(PendingSchema.Read(copy.Json, Entity), DescriptorLens.DeclaredRoles(copy.Json))) is not null;
}
```

- [ ] **Step 5: Replace `ConditionField` in `HooksTab.razor`**

```razor
    /// <summary>The condition: guided rows that write CEL, or the CEL itself — one switch between them (spec §4.6).</summary>
    private RenderFragment ConditionField => @<text>
        <Field Label="Only when (optional)" LabelId="hook-condition-mode-label">
            <ChildContent>
                <ChipGroup TValue="string" Items="_conditionModes" Selected="[_guided ? GuidedWord : TextWord]"
                           aria-labelledby="hook-condition-mode-label" data-testid="hook-condition-mode"
                           SelectedChanged="modes => ChooseConditionMode(modes[0])" />
                @if (_conditionNote is { } note)
                {
                    <p class="a-hint" data-testid="hook-condition-note">@note</p>
                }
            </ChildContent>
            <Hint>
                <span data-testid="hook-condition-hint">
                    CEL over @((MarkupString)Current.Images) and <code class="a-mono">@@user</code>. Left empty, the hook
                    runs on every write.
                </span>
            </Hint>
        </Field>
        @if (_guided)
        {
            <ConditionBuilder Point="@Current.Point" Entity="@Entity" Value="@Current.Condition" ValueChanged="TypeCondition" />
            <Field Label="Expression">
                <ChildContent>
                    <code class="a-mono" id="hook-condition" data-testid="hook-condition-readout"
                          aria-describedby="@_check.DescribedBy("hook-condition")">@(Current.Condition.Length == 0 ? "(none: the hook runs on every write)" : Current.Condition)</code>
                    @ConditionLength
                    @Findings("hook-condition")
                </ChildContent>
            </Field>
        }
        else
        {
            <Field Label="Condition (CEL)" For="hook-condition">
                <ChildContent>
                    <MudTextField T="string" Variant="Variant.Outlined" Immediate="true" id="hook-condition" Class="a-mono"
                                  autocomplete="off" Placeholder="@Current.Example" aria-describedby="@ConditionDescribedBy"
                                  Value="Current.Condition" ValueChanged="TypeCondition" />
                    @ConditionLength
                    @Findings("hook-condition")
                </ChildContent>
                <Hint>
                    What the guided rows write, as text. Anything the rows cannot say — a function, a field compared with a
                    field, a mix of all and any — is written here.
                </Hint>
            </Field>
        }
    </text>;
```

- [ ] **Step 6: Wire the mode** — `HooksTab.razor.cs`: in `OpenNew` add `SettleConditionMode();` as its last line; in `Choose` add `KeepConditionModeFitting();` right after `Current.Choose(point);`. `HooksTab.Edit.cs`: in `OpenEdit` add `SettleConditionMode();` after `ReadPickers();`.

- [ ] **Step 7: Teach the scenarios the switch.** `AdminSession.TypeConditionAsync` becomes:

```csharp
    /// <summary>Types a hook condition as CEL: switches the condition to text mode first, which the guided form is not.</summary>
    /// <param name="condition">The CEL.</param>
    public async Task TypeConditionAsync(string condition)
    {
        await Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = "Text", Exact = true })
            .ClickAsync().ConfigureAwait(false);
        await Page.FillAsync("input#hook-condition", condition).ConfigureAwait(false);
    }
```

  Replace each `await session.Page.FillAsync("#hook-condition", X);` in `ExpressionCheckScenarios.cs` (`:74`, `:80`, `:165`), `SchemaEditingScenarios.cs` (`:286`, `:486`, `:516`) and `SystemMapScenarios.cs` (`:210`) with `await session.TypeConditionAsync(X);` (`grep -n 'FillAsync("#hook-condition"' test/MMLib.Alvo.Admin.Tests.EndToEnd/*.cs` must then print nothing). In `HookEditInPlaceScenarios.The_edit_sheet_is_titled_by_the_hook_and_keeps_its_point` replace the `InputValueAsync("#hook-condition")` line with `(await session.Page.GetByTestId("hook-condition-readout").InnerTextAsync()).ShouldBe("new.quantity <= 0");`.

- [ ] **Step 8: Run** — `dotnet build`; `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*PatternLanguageTests' --filter-class '*FieldConventionTests'`; normalise `ConditionBuilder.razor` (BOM + LF) and `HooksTab.Condition.cs` (BOM + CRLF). The approval test fails with the new component: copy the `.received.txt` over `PublicApi.MMLib.Alvo.Admin.verified.txt` and check the diff adds only `ConditionBuilder` (constructor, `Point`, `Entity`, `Value`, `ValueChanged`, generator overrides); justify it as in Task 13 Step 6. Then `scripts/test-ring0` and `scripts/test-admin-e2e --filter-class '*GuidedConditionScenarios'`, `'*HookEditInPlaceScenarios'`, `'*HookShapeScenarios'`, `'*HookPickerScenarios'`, `'*HookEditingScenarios'`, `'*ExpressionCheckScenarios'`, `'*SystemMapScenarios'`, `'*SchemaEditingScenarios'`, `'*ConfirmFocusScenarios'` — all PASS.

- [ ] **Step 9: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/ConditionBuilder.razor src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Condition.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Edit.cs test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/ExpressionCheckScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/SchemaEditingScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/SystemMapScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/HookEditInPlaceScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/GuidedConditionScenarios.cs
git commit -m "feat(admin): say a hook condition as guided rows that write CEL, with a switch to text

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

Then `scripts/test-ring1`.

---

### Task 20: docs — what the dashboard can now do, and the spec as built

**Files:**
- Modify: `docs/todo-admin.md` (§5a rows `:151`, `:153`; §5b `:168`; §8a hook rows `:469-477`; templates/webhooks rows `:496-497`, `:501-502`; §8b `:522`; §8d item 26 `:629-632`)
- Modify: `docs/architecture/management-api.md` (a paragraph after "The slot must already exist", `:134-137`)
- Modify: `docs/superpowers/specs/2026-10-05-f5-hooks-editor-design.md` (§17 As built)

- [ ] **Step 1: `todo-admin.md`** — replace, line by line (text from the left column to the right; keep the table pipes):

| Row | New "Dashboard" / "When it meets one" cells |
|---|---|
| §5a `templates` (`:151`) | `Integrations | **edit** | Declared and edited from the dashboard (subject and body); the build's warning verbatim; bodyFile refused, never offered (#276). |` |
| §5a `webhooks.endpoints` (`:153`) | `Integrations | **edit** | Declared and edited behind the build's "unsigned" statement; secretRef is a name (#276). |` |
| §5b `hooks` (`:168`) | append ` Edit in place, mutate literals and several fields, endpoint/template pickers, a payload box and a guided condition (#276).` |
| §8a six points (`:469`) | `edit ×6 — add, edit in place (keeps the position), remove | an undrawable hook is read-only with its reason, kept | HooksTab.Edit.cs; WC.Hooks ReplaceHook` |
| before `mutate.<f>` literal (`:472`) | `edit — a value its type holds, checked as typed | — | MutateLiteral.cs` |
| before `mutate.<f>.$cel` (`:473`) | `edit — several fields, field picked from the writable ones | — | HooksTab.Mutate.cs` |
| `webhook.endpoint` (`:475`) | `edit (picker over the working copy, pending included) | an undeclared name is kept, labelled | HooksTab.Pickers.cs` |
| `webhook.payload` (`:476`) | `edit — {{…}} payload, judged live by cel/check; raw JSONata gets the build's refusal | — | HooksTab.Pickers.cs` |
| `email.template` (`:477`) | `edit (picker; a bodyFile template is not offered) | — | HooksTab.Pickers.cs` |
| `templates.<t>.subject` / `.body` (`:496-497`) | `edit (Integrations) | — | TemplateEditor.razor` |
| `webhooks.endpoints.<n>.url` / `.secretRef` (`:501-502`) | `edit (Integrations; https or loopback http; secretRef a SecretName) | — | EndpointEditor.razor` |
| §8b "Edit a hook in place" (`:522`) | `| Edit a hook in place | — | edit — keeps its place, guarded by what the screen drew | WC.Hooks ReplaceHook |` |

  and mark item 26 done: prefix it with `✅` and add, after its "Deferred →" line:

  `**Done** in #276 (slice B, `docs/superpowers/specs/2026-10-05-f5-hooks-editor-design.md`): edit in place keeping the position (guarded, re-found by what the screen drew; an undrawable hook is read-only with its reason); a mutate patches several fields, each a value its type holds or an expression; endpoint and template pickers over the working copy; a `{{…}}` payload judged by cel/check; endpoints and templates declared and edited on Integrations behind the build's "unsigned" statement; a guided condition that writes canonical CEL, with a text switch. Removing an endpoint or a template is a follow-up.`

- [ ] **Step 2: `management-api.md`** — after the paragraph that begins "**The slot must already exist.**", add:

```markdown
**String slots that are not CEL.** The splice writes a bare string at any pointer that is not a mutate value, so the
dashboard also asks about a webhook `payload` and an email `to` (`/entities/<e>/hooks/<point>/<i>/action/payload`,
`…/action/to`): the after-hook compiler reports the `JSONata`, JSON-rendering, placeholder and mail-header refusals at
those paths — the build's own sentence, not a copy of its classifier in the dashboard (hooks editor spec, B4 deviation).
Pinned by `ExpressionSlotCheckTests.A_webhook_payload_slot_is_spliced_as_text_and_judged_as_apply_judges_it` and
`An_email_to_slot_is_spliced_as_text_and_more_than_one_recipient_is_refused`. Two limits the dashboard states rather than
hides: an undeclared endpoint or template stops the compiler before the slot (nothing is reported), and so does a failing
condition — so an action slot is asked about in a hook without its condition (hooks editor spec D4).
```

- [ ] **Step 3: Spec §17 "As built"** — replace the placeholder with: the commit list (`git log --oneline <base>..HEAD` where `<base>` is `feat/expression-check`'s tip `0527bb9`), each deviation from this plan with its reason (as slice A's §8 does), the measured numbers (round-trip iterations, the conformance fact's checked-cell count, the e2e scenario count per class), what the agreement and conformance facts found (each a finding or "none"), and the follow-ups from spec §16 still open.

- [ ] **Step 4: Check** — `scripts/check-brief-freshness` (no spec/analysis source changed, so the brief stays fresh); markdown files LF, no BOM.

- [ ] **Step 5: Commit**

```bash
git add docs/todo-admin.md docs/architecture/management-api.md docs/superpowers/specs/2026-10-05-f5-hooks-editor-design.md
git commit -m "docs(f5): the hooks editor as built — todo-admin rows, cel/check on string slots, spec As built

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 21: whole-slice verification

No new code. Each step's expected result is stated; a failure goes back to the task that owns the file.

- [ ] **Step 1: Rings** — `scripts/test-ring1` then `scripts/test-ring2`. Expected: green. (ring2 paging timeouts on an Npgsql *connect* are infrastructure — read the trace before calling it a regression.)
- [ ] **Step 2: Release** — `dotnet build -c Release` → 0 warnings, 0 errors. CI builds Release; a CA error passes every Debug ring.
- [ ] **Step 3: e2e whole** — `scripts/test-admin-e2e` (every class, one run, nothing else running). Expected: green, including the unchanged `RefusalPlacementScenarios`, `OtherCircuitApplyScenarios` (Review Focus 2), `PhoneAndKeyboardScenarios`, `CreateActionScenarios`, `ConfirmFocusScenarios`, `FieldConsistencyScenarios`.
- [ ] **Step 4: Scans** — `grep -rn "MutateField\|MutateValue\b\|a-hookrow__remove\|FirstRow" src test` prints nothing; `grep -rn 'FillAsync("#hook-condition"' test` prints nothing; `git diff 0527bb9 --stat -- src/MMLib.Alvo src/MMLib.Alvo.Abstractions` prints nothing (Admin-only slice; the core changed only in tests).
- [ ] **Step 5: Freeze the tree**, then dispatch `alvo-plan-guard` (read-only verdict on plan drift, §0 principles, security core). The review commands are user-only (memory "Review commands blocked"): dispatch `csharp-reviewer` over the diff as the correctness substitute and a security-focused reviewer with the `alvo-security-core-review` checklist over the mutate path and the endpoint sheet (SC-adjacent per the triage: in-transaction mutate values; a URL that may be its own secret; a secret name field) — label both as substitutes. Fix findings in the owning task's files, re-run ring1 and the affected e2e classes.
- [ ] **Step 6: Hand back** — report to the controller: the branch, the commit list, the public-API justification (spec §14), the findings from Tasks 15 and 18, and the open questions (spec §15). The controller builds the PR report (`alvo-pr-report`) and opens the PR; this plan never pushes.

---

## Self-review

1. **Spec coverage.** §4.1 list + Edit/read-only → Tasks 3, 8. §4.2 sheet (title, submit, fixed point, dirty, guard refusal) → Tasks 6, 8. §4.3 mutate rows → Tasks 4, 6, 9. §4.4 pickers → Tasks 2, 10. §4.5 payload → Tasks 7, 10. §4.6 guided + switch → Tasks 1, 16, 17, 18, 19. §4.7 Integrations on the working copy → Tasks 2, 12, 13. §4.8 sheets → Tasks 11, 13, 14. §5.1 ReplaceHook → Task 3. §5.2 shapes → Task 3. §5.3 patch → Task 5. §5.4 badge/order → Task 8 e2e (badge, index). §5.5 writes → Task 12. §6.1–§6.3 rules and statement → Tasks 11, 13, 14, 15. §7.1–§7.5 → Tasks 1, 16, 17, 18, 19. §8 security → Tasks 11 (no echo), 13 (name-only snackbar), 3/8 (preserve), 10 (refusals stay on panels). §9 a11y/keyboard → labels and `aria-describedby` in Tasks 8–10, 13, 14, 19; Escape/dirty e2e in Tasks 8, 13. §11 findings → Tasks 15 (enum literal fact), 10 (object payload hint). §12 criteria 1–6 → Tasks 8–10, 13, 14, 17, 18, 20. §13 test strategy → every listed class exists in a task. §14 public API → Tasks 13, 14, 19. §17 As built → Task 20.
2. **Placeholders.** None of "TBD/TODO/similar to"; every class and test is written out. Every e2e helper used across classes (`OnWriteAsync`, `OpenEditAsync`, `NewAfterHookAsync`, `Combobox`) is defined as `internal static` in the task that first needs it (Tasks 8 and 10).
3. **Type consistency.** `ConditionTable` / `OperatorSpec` / `FieldKind` / `ConditionOperator` / `RowImage` / `OperandKind` (Task 1) are used with those names in Tasks 16–19; `MutateRow(field, mode, text)` + `MutateMode` (Task 4) in Tasks 6, 9, 15; `HookPatch.Apply(original, condition, action)` (Task 5) in Tasks 6, 8; `HookBuilder.MutateRows/Payload/Fields/From/BuildHook/CandidateHook/Fingerprint/HasInput/MaxPayloadLength` (Task 6) in Tasks 8–10; `ExpressionSlots.ForHook(json, entity, point, int?, JsonObject, params string[])` (Task 7) in Tasks 8–10; `WorkingCopy.ReplaceHook(entity, point, position, expectedHook, hook)` (Task 3) in Task 8; `DeclareEndpoint/DeclareTemplate` (Task 12) in Tasks 13, 14; `DescriptorLens.IntegrationUse` (Task 2) in Tasks 12, 13; `ConditionText.Generate/Normalize` (Task 16) in Tasks 17–19, `Recognize` (Task 17) in Tasks 18, 19; `TypeConditionAsync` (Task 8) redefined in Task 19 with the same signature.
4. **Review Focus.** Each of the 17 lines names its test and the test exists in the named task.
5. **Size.** Tasks 6, 8 and 13 carry full-file listings (`HookBuilder.cs`, `HooksTab.razor(.cs)`, `Integrations.razor`) because later tasks replace named fragments of them; most of each listing is carried over unchanged, and each task names what is new (Task 8's "What is new" list).
