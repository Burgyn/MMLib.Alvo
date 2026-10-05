# F5 hook functions end to end (slice D) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A hook author gets 19 meaningful built-in CEL functions (32 overloads; standard and cel-go names, `math.abs`/`math.round` aligned, `math.round(x, digits)` for cents), fail-closed arithmetic in hook conditions and mutate values, and string `+` in mutate values; the hook editor offers every function its slot admits — built-in or host — with signature, summary and provenance, inserting a call at the caret; and a host function registered in C# is proved end to end in a real browser over the shipped host (listed, inserted, applied, evaluated by a Data API write) and shown as copyable code in the embedded sample.

**Architecture:** Core first, behind the existing catalog: a qualified-name production in the parser (`math.x(…)`), `lowerAscii` turned into an ordinary catalogued function, fourteen new bodies in `CelBuiltInFunctions`, an apply-time refusal of a built-in call whose literal arguments make it fail (plus `string()` over a `date` field), then the operators: one `EvalState` flag from `CompiledExpression.Profile` makes Condition and Mutate arithmetic throw the internal function failure on overflow and division by zero (Int stays Int, `/` truncates), and the Concatenation row gains Mutate with null in → null out. All interpreter-side, so `cel/check` and apply agree by construction (C1 R8); Rule and Computed keep their semantics. Then Admin: `ManagementGateway.CelFunctionsAsync`, a pure internal `FunctionOffer`, `HooksTab` fragments under the mutate and condition boxes, two tiny JS helpers for the caret, three guided operators in B's `ConditionTable`. No public symbol is added anywhere. Last, set up from code: `AdminWorld` gains an `IAlvoBuilder` seam and a world that registers `normalizeFrameNumber` and writes through the HTTP Data API, and the embedded sample registers `normalizeVin` with a README section and an integration fact.

**Tech Stack:** .NET 10, C# (nullable on), Blazor Server, MudBlazor 9.10.0 (pinned), ES modules (`admin.js`), Microsoft.Testing.Platform + xUnit v3 + Shouldly + CsCheck, Microsoft.Playwright (admin e2e).

**Spec:** `docs/superpowers/specs/2026-10-06-f5-hook-functions-end-to-end-design.md` — read it first, including §20 (the rulings D-1…D-17 on the validation); § numbers below are the spec's. Background: `docs/superpowers/specs/2026-10-05-f5-cel-functions-design.md` (C1, incl. §17 rulings K–V), `docs/superpowers/specs/2026-10-05-f5-hooks-editor-design.md` (B) and its plan `docs/superpowers/plans/2026-10-05-f5-hooks-editor.md` (B Task N interfaces are cited as **from B Task N**), `docs/architecture/cel.md`. Line numbers into C1 code are at C1 HEAD `3a3c31f` (PR #314); after Task 0's merge they may shift — find the member by name.

## Global Constraints

- **Prerequisites:** B (`feat/hooks-editor`) implemented through its Task 21; C1 (`feat/cel-functions`) through its pre-PR fix wave. Task 0 creates D's branch from B's tip and merges C1. Where this plan names a B member (`TypeMutateText`, `TypeCondition`, `MutateValue(row, i)`, `ConditionField`, `_guided`, `ConditionTable`, `ConditionText.Refusal`, `GuidedConditionConformanceTests`), it is the one B's plan declares in that task's **Interfaces** block; **the code on the branch wins over this plan's restatement** — adapt the call, not the behaviour (B's pre-flight already renamed `FieldKind` → `ConditionFieldKind`, and ruling D6 may have dropped `CandidateHook`'s `withCondition` parameter).
- Branch `feat/hook-functions`, worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hookfn`. **One writer per worktree**; review agents read a frozen tree. Never merge or push to `main`; never push from a task.
- Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after Tasks 2, 6, 7, 8, 14, `scripts/test-ring2` in Task 18. **Rings are Debug, CI is Release**: `dotnet build -c Release` in Task 18 (and only `docker build` reproduces CI exactly).
- e2e is in no ring: `scripts/test-admin-e2e --filter-class '*<ClassName>'`. Never two e2e or Stryker runs at once; never edit a script while it runs. Remove `.claude/worktrees` before rings if an isolated agent left one.
- New/edited `.cs` files: **UTF-8 with BOM, CRLF** — create with `Write`, then `python3 -c "p='<file>';b=open(p,'rb').read().replace(b'\r\n',b'\n').replace(b'\n',b'\r\n');b=b if b.startswith(b'\xef\xbb\xbf') else b'\xef\xbb\xbf'+b;open(p,'wb').write(b)"`. `.razor`: **UTF-8 with BOM, LF**. `.js`, `.css`, `.md`, `.json`: LF, **no BOM** (`alvo.css` must never get a BOM — `00b3be4`). Edits keep existing endings.
- Methods ≤ ~25 lines, one purpose; every type and member, internal too, has XML docs (`alvo-dotnet-conventions`). Prose: plain, specific.
- **No `PublicApi.*.verified.txt` may change** (spec §14). If one moves, the change is wrong: make the symbol internal. In particular no `CelBinaryOperator` member is added (that is why `%` is not in this slice).
- `MMLib.Alvo.Admin` never references `MMLib.Alvo` (`BoundaryArchitectureTests`); Admin↔core agreement facts live in `test/MMLib.Alvo.Host.Tests`.
- `CelAcceptanceBaseline.jsonl` stays unmodified **except** three regenerations, each by rule, each verified line by line, each listing its moved rows in the commit body: Task 2 (rows whose source names `lowerAscii`), Task 7 (rows whose errors carry an arithmetic gate message; 269 at C1 HEAD), Task 8 (rows whose errors carry the concatenation gate message; 51 at C1 HEAD, 48 of them already regenerated by Task 7).
- **Security core:** Tasks 6, 7 and 8 change the type checker's profile table and the interpreter's failure path. The fail-closed flag must be read from `CompiledExpression.Profile` in exactly one place, and Rule/Computed must keep answering `false`/`null` (Task 7's facts pin it). Task 18 runs the `alvo-security-core-review` checklist over them.
- The hooks skill's `<!-- gen:mutate-functions -->` region **follows the catalog in the same task** that changes the catalog (`SkillCoreClaimsTests.The_mutate_functions_are_the_ones_the_profile_allow_lists` runs in ring0); a skill example a task newly admits moves from *refused* to *allowed* in that task (`SkillConformanceTests` compiles them); the skill stays ≤ 6,144 bytes / 200 lines; `AlwaysInContextBudget` (22,758, `AlvoAssistantTests.cs:25`) is never raised.
- Pattern language is binding (MudBlazor design §3): `PatternLanguageTests` and `FieldConventionTests` pass unchanged; no bUnit.
- e2e assertions wait for what they assert (spec D-10): an absence only after the thing was shown in the same session or its request was recorded; a clean check only as the recorded verdict for that exact source; a dotted test id (`fn-math.round`) only through `GetByTestId`, never a CSS selector.
- `examples/vehicle-registry/vehicles.alvo.json` is never modified (spec E16).
- Test commands: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP).
- Commits: Conventional Commits, stage files by name (never `git add -A`), never a closing keyword in a body (write `Refs #…`), each message ending with a blank line and `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`.

## Review Focus

Hostile or odd inputs a person will hit; each line names where its test lives.

1. A field named `math`, `math .round (x)` with spaces, `math.nope(x)`, `old.round(x)` — the catalog decides; a field stays a field; the unknown one gets "did you mean". [Task 1 `CelQualifiedNameParsingTests`]
2. `new.title.trim()` and `title.trim()` — refused with the global form as the fix. [Task 1]
3. `lowerAscii(trim(x))`, `lowerAscii('ABC')` (now legal), `lowerAscii()` (arity), `lowerAscii` in a Rule (function gate) — and no corpus row moves unless its source names `lowerAscii`. [Task 2]
4. `substring` with `-1`, reversed bounds, past the end, an emoji, a lone surrogate; `!endsWith(new.name, 'x')` over an empty field is `true`. [Task 3 `CelTextBuiltInTests`, `CelBuiltInPropertyTests`]
5. `math.round(2.345, 2)`, `math.round(price, 29)` (literal: refused at apply), `math.round(price, qty)` with `qty` 29 (fails closed), `math.round(qty, 2)` (Int widened). [Tasks 4, 6]
6. `int('9223372036854775808')`, `int(' 7')`, `int(1e28)`, `timestamp('2026-02-30T00:00:00Z')`, a lower-case `z`, ten fraction digits, `string(-0.0)`, `string(due)` over a `date` field (refused). [Task 5 `CelConversionBuiltInTests`]
7. `math.greatest(1, 2.5)` (widening), a null argument, equal values of different scale. [Task 4 `CelMathBuiltInTests`]
8. A literal-only built-in call that always fails — refused at apply and by `cel/check`; a host function with literal arguments is never invoked at apply. [Task 6 `CelConstantCallTests`, `ExpressionCheckAgreementTests`]
9. `new.qty / 0` in a reject condition (throws, the write is refused — never a silent `false`), `9223372036854775807 + 1`, `-qty` at the smallest Int, the smallest Int `/ -1`, `price * price` at `decimal.MaxValue`, `7 / 2` = `3`, `-7 / 2` = `-3`, a null operand (null), and the same overflow in a computed field still `null`. [Task 7 `CelHookArithmeticTests`]
10. `'+421' + new.phone` over a null phone (null), `'#' + new.qty` (refused, fix names `string(x)`), concatenation in a condition (refused), a nullable operand in Computed (still refused). [Task 8 `CelMutateConcatenationTests`]
11. A host summary carrying markup — rendered as text. [Task 15 `HostFunctionScenarios`]
12. Insert into an empty box (prefill), at a caret inside text, over a selection, with Unicode before the caret, with no caret known; the check sentence appears within 3 s and focus stays in the box. [Task 10 `FunctionOfferTests`; Task 12 `FunctionOfferScenarios`]
13. `cel/functions` refused or failing — no list, the editor still adds the hook, asserted only after the refused request was recorded. [Task 13 `FunctionListUnavailableScenarios`]
14. Renaming a field named `math` leaves `math.round(new.x)` intact. [Task 11 `CelNamesTests`]
15. A guided "starts with" with an empty value, and hostile quoting inside the value. [Task 14]
16. Phone width with the list open under a mutate value and a condition: the document, the sheet, and no word broken per character. [Task 13 `FunctionListLayoutScenarios`]
17. The sample's function listed with provenance `Host` and evaluated by a write, while `vehicles.alvo.json` stays byte-identical. [Task 16 `EmbeddedSampleTests`]

## File map

| File | Task | Responsibility |
|---|---|---|
| (merge of `feat/cel-functions`), `test/MMLib.Alvo.Host.Tests/HooksEditorAgreementTests.cs` | 0 | D's branch; B pre-flight C7 and W1 |
| `src/MMLib.Alvo/Expressions/Internal/CelParser.cs`, `CelBuiltInFunctions.cs`, `HostCelFunction.cs` | 1 | qualified names, receiver fixes, `math.abs`/`math.round`, `math` reserved |
| `CelParser.cs`, `CelTypeChecker.cs`, `CelInterpreter.cs`, `CelBuiltInFunctions.cs`, `CelAcceptanceBaseline.jsonl` | 2 | `lowerAscii` catalogued; `upperAscii` |
| `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Text.cs` | 3 | `substring contains startsWith endsWith` |
| `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Math.cs` | 4 | `math.ceil math.floor math.greatest math.least`, `math.round(x, digits)` |
| `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Conversions.cs`, `CelTypeChecker.cs`, `HostCelFunction.cs` | 5 | `string int timestamp`; `string()` over a `date` field refused; built-ins first in `ReservedReason` |
| `CelTypeChecker.cs`, `CelFunction.cs`, `CelBuiltInFunctions.Math.cs` | 6 | literal arguments checked at apply |
| `CelTypeChecker.cs`, `CelInterpreter.cs`, new `CelHookArithmetic.cs`, `CelAcceptanceBaseline.jsonl` | 7 | fail-closed arithmetic in Condition and Mutate |
| `CelTypeChecker.cs`, `CelInterpreter.cs`, `CelAcceptanceBaseline.jsonl` | 8 | string `+` in Mutate |
| `test/MMLib.Alvo.Api.Tests/CelBuiltInWriteTests.cs`, `descriptors/built-in-functions.alvo.json` | 9 | the new built-ins and operators over HTTP |
| `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs`, `Components/Schema/FunctionOffer.cs`, `test/MMLib.Alvo.Host.Tests/FunctionOfferAgreementTests.cs` | 10 | the list, the offer, the agreement |
| `src/MMLib.Alvo.Admin/Components/Schema/CelNames.cs` | 11 | a name before `.` is never a column |
| `HooksTab.razor`, `HooksTab.Functions.cs`, `Internal/AdminInterop.cs`, `wwwroot/admin.js`, `wwwroot/alvo.css`, e2e `RecordingWorld.cs`, `FunctionOfferScenarios.cs` | 12 | the offering UI and its main scenarios |
| e2e `AdminSession.cs`, `FunctionListLayoutScenarios.cs`, `FunctionListUnavailableScenarios.cs` | 13 | phone, sheet, vertical text, absence, refusal |
| `ConditionTable.cs`, `ConditionText.cs` (B) | 14 | three guided operators |
| `test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminWorld.cs`, `HostFunctionWorld.cs`, `HostFunctionScenarios.cs`, `BuiltInConditionScenarios.cs` | 15 | set up from code, end to end |
| `samples/MMLib.Alvo.Samples.EmbeddedHost/SampleHost.cs`, `README.md`, `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/EmbeddedSampleTests.cs` | 16 | the copyable registration |
| `.claude/skills/alvo-descriptor-hooks/SKILL.md`, `docs/architecture/cel.md`, `extensibility.md`, `management-api.md`, `docs/todo-admin.md`, `test/MMLib.Alvo.Host.Tests/CelReferenceDocTests.cs` | 17 | docs, skill prose, the doc drift fact |
| — | 18 | whole-slice verification |

---

### Task 0: D's branch — B plus C1, and the two pre-flight items that waited for C1

**Files:**
- Merge: `feat/cel-functions` into a new branch `feat/hook-functions` from `feat/hooks-editor`
- Modify: `test/MMLib.Alvo.Host.Tests/HooksEditorAgreementTests.cs` (from B Task 15)

**Interfaces:**
- Consumes: B Task 15's `HooksEditorAgreementTests` helpers `MutateErrors(Probe(type), literal)` and `MutateLiteral.TryValue` (from B Task 4); C1's facet refusal at apply (C1 §17 Ruling V).
- Produces: a branch on which both slices build and every ring is green.

- [ ] **Step 1: Create the worktree and branch** (from the main checkout, which is itself a worktree — see memory "merge/worktree gotcha"):

```bash
git -C /Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo fetch --all --prune
git -C /Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo worktree add /Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hookfn -b feat/hook-functions feat/hooks-editor
cd /Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hookfn
git log --oneline -1 feat/cel-functions
```

If `feat/hooks-editor` or `feat/cel-functions` has already merged to `main`, branch from `origin/main` instead and merge whichever has not.

- [ ] **Step 2: Merge C1**

```bash
git merge --no-ff feat/cel-functions -m "Merge branch 'feat/cel-functions' into feat/hook-functions"
```

Expected conflicts and their resolution (both branches merged `feat/expression-check`, so most hunks are identical):

| File | Take |
|---|---|
| `src/MMLib.Alvo.Admin/Components/Schema/ExpressionSlots.cs`, `test/MMLib.Alvo.Admin.Tests/Schema/ExpressionSlotsTests.cs` | B's (B Task 7/8 replaced `ForMutateValue`/`ForHookCondition` with `ForHook`, pre-flight C1/C5) |
| `test/MMLib.Alvo.Admin.Tests.EndToEnd/ExpressionCheckScenarios.cs` | B's (row ids, pre-flight C2/C3) |
| `test/MMLib.Alvo.Admin.Tests.EndToEnd/ManagementDecorator.cs`, `KeptFollowScenarios.cs` | C1's added `GetCelFunctionsAsync` member, inside B's version |
| `docs/todo-admin.md`, `docs/architecture/management-api.md` | both sides' rows |

`git diff --name-only --diff-filter=U` must print nothing before committing.

- [ ] **Step 3: Run the agreement facts that pre-flight W1 flagged first**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*HooksEditorAgreementTests'`
Expected: PASS. If `The_mutate_row_and_apply_agree_on_text_typed_for_a_moment_or_an_id` fails because apply now refuses a date-only `datetime` literal (C1 Ruling V's `format`/type check), change `MutateLiteral` (B Task 4) to refuse it too — the client follows apply (B Task 15 Step 2), never the reverse — and keep the fact.

- [ ] **Step 4: Add the agreement pre-flight C7 deferred** — append to `HooksEditorAgreementTests`:

```csharp
    /// <summary>
    /// Pre-flight C7, owed once C1's facet check (#308) reached this branch: an enum literal outside its values and a
    /// null for a required field are refused by the mutate row and by apply alike.
    /// </summary>
    [Theory]
    [InlineData("enum", "bogus")]
    public void A_literal_the_row_refuses_for_a_facet_apply_refuses_too(string type, string literal)
    {
        var field = Probe(type);
        MutateLiteral.TryValue(new MutateRow(field.Name, MutateMode.Literal, literal), field, out _, out var refusal).ShouldBeFalse();
        refusal.ShouldNotBeNullOrWhiteSpace();
        MutateErrors(field, literal).ShouldNotBeEmpty("apply refuses a value outside the enum's declared values since #308");
    }

    [Fact]
    public void Setting_a_required_field_to_empty_is_refused_by_the_row_and_by_apply()
    {
        var field = Probe("string") with { Required = true };
        MutateLiteral.TryValue(new MutateRow(field.Name, MutateMode.Literal, string.Empty) { Empty = true }, field, out _, out _).ShouldBeFalse();
        MutateErrors(field, literal: null).ShouldNotBeEmpty("a null for a required field is refused at apply (C1 Ruling V)");
    }
```

`Probe`, `MutateErrors` and the `MutateRow` constructor are B Task 15/4's; if `MutateErrors` takes no null literal, add a `JsonNode?` overload beside it that writes JSON `null` into the probe hook. If apply does not refuse a literal `null` for a required field at apply (Ruling V refuses it at **write** time as 403), assert that instead and say so in the fact's summary: the row is then stricter than apply, which B3 permits.

- [ ] **Step 5: Run the rings and the hook e2e classes**

Run: `scripts/test-ring0 && scripts/test-ring1`, then `scripts/test-admin-e2e --filter-class '*ExpressionCheckScenarios'` and `scripts/test-admin-e2e --filter-class '*MutateEditingScenarios'`.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add test/MMLib.Alvo.Host.Tests/HooksEditorAgreementTests.cs
git commit -m "test(admin): pin the facet refusals the mutate row and apply now share

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

(The merge commit from Step 2 already exists; amend it only if Step 2's resolution needed a fix.)

---

### Task 1: qualified names — `math.abs` and `math.round`, receiver fixes, `math` reserved

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelParser.cs` (at C1 HEAD `3a3c31f`: `ParseIdentifierExpression` `:397`, `ParseCall` `:427`, `ParseCatalogCall(CelToken)` `:440`, `NestedAccessFix()` `:584`, `ParseFieldPath` `:597` with its second throw at `:613`)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs` (`Abs`, `Round` names)
- Modify: `src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs` (`ReservedReason` `:68`)
- Modify (sweep): every test naming `abs(`/`round(` as a function — `CelBuiltInFunctionTests`, `CelBuiltInPropertyTests`, `CelFunctionTypeCheckTests`, `CelFunctionCatalogTests`, `CelFunctionRegistrationTests`, `ManagementCelFunctionsTests`, `ExpressionCheckAgreementTests` — and `.claude/skills/alvo-descriptor-hooks/SKILL.md` (region)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelQualifiedNameParsingTests.cs`

**Interfaces:**
- Consumes: `CelFunctionCatalog.Contains/Names` (C1), `NameSuggestion.Closest` (core internal).
- Produces: a catalogued name may be `namespace.member`; `CelCall.Name` carries the dotted text. Built-in names `math.abs`, `math.round` (C1's `abs`/`round` are gone). Host name `math` reserved.

- [ ] **Step 1: Write the failing tests** (`CelQualifiedNameParsingTests.cs`):

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// cel-go's math functions are namespaced globals — <c>math.abs(x)</c> — and the catalog, not the grammar, decides
/// whether <c>a.b(</c> is one (spec §6.1, E2). Everything that was a field or a refusal before stays one.
/// </summary>
public sealed class CelQualifiedNameParsingTests
{
    private static readonly EntitySchema _withMath = TestCelFunctions.Items with
    {
        Fields = [.. TestCelFunctions.Items.Fields, new FieldSchema { Name = "math", Type = FieldType.Decimal, Precision = 18, Scale = 2, Nullable = true }],
    };

    [Theory]
    [InlineData("math.abs(qty)", "math.abs", 1)]
    [InlineData("math.round(price)", "math.round", 1)]
    [InlineData("math .round ( price )", "math.round", 1)]
    [InlineData("math.abs(math.round(price))", "math.abs", 1)]
    public void A_catalogued_qualified_name_parses_as_one_call(string source, string name, int arguments)
    {
        var call = CelParser.Parse(source).ShouldBeOfType<CelCall>();

        call.Name.ShouldBe(name);
        call.Arguments.Count.ShouldBe(arguments);
    }

    [Fact]
    public void A_field_named_like_the_namespace_is_still_a_field() =>
        new CelCompiler().Compile("math.round(math)", CelProfile.Mutate, _withMath).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("abs(qty)", "'abs' is not a recognized function.", "math.abs")]
    [InlineData("round(price)", "'round' is not a recognized function.", "math.round")]
    public void The_bare_names_are_gone_and_say_where_they_went(string source, string message, string suggestion)
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));

        refused.Message.ShouldBe(message);
        refused.FixSuggestion.ShouldNotBeNull().ShouldContain(suggestion);
    }

    [Fact]
    public void An_unknown_member_of_the_namespace_gets_did_you_mean()
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse("math.rond(price)"));

        refused.Message.ShouldStartWith("Alvo has no nested field access");
        refused.FixSuggestion.ShouldNotBeNull().ShouldContain("Did you mean 'math.round'?");
    }

    [Fact]
    public void Old_and_new_followed_by_a_function_name_are_still_field_paths() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("old.trim(qty)")).ShouldNotBeNull();

    [Theory]
    [InlineData("new.name.trim()", "Write trim(new.name)")]
    [InlineData("old.name.upperAscii()", null)]
    public void A_receiver_call_after_an_image_is_told_the_global_form(string source, string? fix)
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));

        refused.Message.ShouldBe("Alvo has no nested field access beyond old./new.; use a single field name.");
        if (fix is null)
        {
            refused.FixSuggestion.ShouldBeNull("upperAscii is not catalogued until Task 2");
        }
        else
        {
            refused.FixSuggestion.ShouldNotBeNull().ShouldStartWith(fix);
        }
    }

    [Fact]
    public void A_host_cannot_take_the_namespace_word() =>
        Should.Throw<ArgumentException>(() => HostCelFunction.Create("math", (string s) => s, null))
            .Message.ShouldContain("namespace of CEL's math functions");
}
```

(Task 2 flips the `old.name.upperAscii()` row's expectation to a fix, once `upperAscii` is catalogued.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelQualifiedNameParsingTests'`
Expected: FAIL — `math.abs(qty)` is refused ("no nested field access").

- [ ] **Step 3: Rename the two built-ins** — in `CelBuiltInFunctions.cs`, `Abs(type)` and `Round(type)` pass `"math.abs"` / `"math.round"` as the name, and their `CelFunctionException` reasons name `math.abs`. Summaries unchanged.

- [ ] **Step 4: Teach the parser qualified names** — in `CelParser.cs` replace `ParseIdentifierExpression` and `ParseCatalogCall`, and add `QualifiedName`:

```csharp
        private CelNode ParseIdentifierExpression()
        {
            var identifierToken = Expect(CelTokenKind.Identifier);

            if (Current.Kind == CelTokenKind.LeftParen)
            {
                return ParseCall(identifierToken);
            }

            if (QualifiedName(identifierToken) is { } qualified)
            {
                _index += 2;
                return ParseCatalogCall(qualified);
            }

            return ResolveFieldReference(identifierToken);
        }

        /// <summary>
        /// <c>namespace.member</c> when the next three tokens are <c>. member (</c> and the catalog knows the dotted name
        /// (spec §6.1). The catalog decides, never the grammar: <c>new.total</c>, a field named <c>math</c>, and an
        /// uncatalogued <c>a.b(</c> all fall through to field resolution exactly as before.
        /// </summary>
        private string? QualifiedName(CelToken first)
        {
            if (Current.Kind != CelTokenKind.Dot || _index + 2 >= tokens.Count
                || tokens[_index + 1].Kind != CelTokenKind.Identifier || tokens[_index + 2].Kind != CelTokenKind.LeftParen)
            {
                return null;
            }

            var name = $"{first.Text}.{tokens[_index + 1].Text}";
            return catalog.Contains(name) ? name : null;
        }

        /// <summary>
        /// Parses <c>name(argument, …)</c> for a catalogued function. Each argument is a whole expression parsed as one
        /// nested level, so call nesting counts against <see cref="MaxDepth"/>; arity is the type checker's question.
        /// </summary>
        private CelCall ParseCatalogCall(string name)
        {
            Expect(CelTokenKind.LeftParen);
            IReadOnlyList<CelNode> arguments = Current.Kind == CelTokenKind.RightParen ? [] : ParseArguments();
            Expect(CelTokenKind.RightParen);
            return new CelCall(name, arguments);
        }
```

and change the catalog arm of `ParseCall` to `var name when catalog.Contains(name) => ParseCatalogCall(name),` (C1's `ParseCatalogCall` takes the `CelToken`; it now takes the name, so a qualified call and a plain one share it).

- [ ] **Step 5: The two receiver fixes and the namespace "did you mean"** — replace `NestedAccessFix()` with a version that takes the first token, and give `ParseFieldPath`'s second refusal a fix:

```csharp
        /// <summary>
        /// The fix for <c>x.trim()</c>, <c>math.rond(x)</c> or another dotted spelling (deviation F1/F11): a catalogued
        /// member gets the global form; a member of a catalogued namespace gets "did you mean" over the known names.
        /// </summary>
        private string NestedAccessFix(CelToken first) => ReceiverCallName() switch
        {
            { } member when catalog.Contains(member) =>
                $"Write {member}(...) with the value as an argument: Alvo calls a function as {member}(x), never as x.{member}().",
            { } member when IsNamespace(first.Text) => KnownFunctionsSuggestion($"{first.Text}.{member}"),
            _ => MacroNotSupportedSuggestion,
        };

        private bool IsNamespace(string text) => catalog.Names.Any(name => name.StartsWith(text + ".", StringComparison.Ordinal));

        /// <summary>The fix for <c>new.title.trim()</c>: the global call over the same image and field (spec §6.2).</summary>
        private string? ImageReceiverFix(CelToken image, CelToken field) =>
            ReceiverCallName() is { } member && catalog.Contains(member)
                ? $"Write {member}({image.Text}.{field.Text}): Alvo calls a function with the value as its first argument, "
                    + $"never as {image.Text}.{field.Text}.{member}()."
                : null;
```

In `ParseFieldPath`, pass `identifierToken` to `NestedAccessFix(identifierToken)`, and make the second throw
`new CelSyntaxException("Alvo has no nested field access beyond old./new.; use a single field name.", Current.Position, ImageReceiverFix(identifierToken, fieldToken))`.
`KnownFunctionsSuggestion(name)` (C1) already computes "Did you mean" via `NameSuggestion.Closest` over `catalog.Names` (within 2 edits), which covers `math.rond`. `abs` → `math.abs` is 5 edits, so the bare names need their own arm: in `UnrecognizedFunctionFix`, before the default arm, add
`var bare when catalog.Contains($"math.{bare}") => $"Did you mean 'math.{bare}'? " + KnownFunctionsSuggestion(string.Empty),`
(`KnownFunctionsSuggestion("")` adds no second "Did you mean": no catalogued name is within 2 edits of the empty string — the shortest, `int`, is 3).

- [ ] **Step 6: Reserve `math`** — in `HostCelFunction.ReservedReason`, add the arm `"math" => "it is the namespace of CEL's math functions",` before the built-in arm.

- [ ] **Step 7: Sweep the claim, not the file** — `grep -rnE "\babs\(|\bround\(|'abs'|'round'|\"abs\"|\"round\"" src test .claude docs/architecture eval --include=*.cs --include=*.md --include=*.json --include=*.jsonl` and change every **function** use to `math.abs`/`math.round` (leave fields named `round`, e.g. `TestCelFunctions.Items.round`, and English prose). Update the `<!-- gen:mutate-functions -->` region of `.claude/skills/alvo-descriptor-hooks/SKILL.md` to the catalog's ordinal order: `` `lowerAscii` `math.abs` `math.round` `now` `replace` `size` `trim` ``. The corpus has no `abs(`/`round(` row (verified: `grep -c` = 0), so it does not move.

- [ ] **Step 8: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelQualifiedNameParsingTests' --filter-class '*CelCatalogCallParsingTests' --filter-class '*CelBuiltIn*' --filter-class '*CelFunction*' --filter-class '*CelAcceptanceCorpusTests'`, then `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests'`, then `scripts/test-ring0`.
Expected: PASS. `CelCatalogCallParsingTests.A_receiver_or_namespaced_spelling_of_a_function_is_told_the_call_shape` keeps passing (its fix text still names `echo(x)`); if its `math.echo(title)` row now reads the namespace arm, keep the assertion `ShouldContain("echo")` true by leaving `echo` in the suggestion list.

- [ ] **Step 9: Commit**

```bash
git add src/MMLib.Alvo/Expressions/Internal/CelParser.cs src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs test/MMLib.Alvo.Tests/Expressions/CelQualifiedNameParsingTests.cs .claude/skills/alvo-descriptor-hooks/SKILL.md <every swept test file, by name>
git commit -m "feat(cel)!: math.abs and math.round take cel-go's namespaced names

The parser reads a catalogued namespace.member call; the bare names are
refused with the new ones as the fix, and a host cannot register 'math'.
Nothing using abs or round has shipped (spec E2).

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 2: `lowerAscii` becomes an ordinary built-in; `upperAscii` joins it

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs` (`LowerAscii`, new `UpperAscii`, `All`, the two fold helpers)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelParser.cs` (remove the `CelCall.LowerAscii` arm of `ParseCall`, `ParseLowerAsciiCall`, the `lowerAscii` branch of `FieldOnlyCallFix`)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` (`CheckCall`, `CheckLegacyCall` → `now` only, delete `CheckLowerAsciiCall`; the `Call` row's comment)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs` (at C1 HEAD: `EvaluateCall` name arm `:257`, `LowerAscii` `:279`, `FoldAsciiUpperCase` `:301` move to `CelBuiltInFunctions`)
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl` (the `lowerAscii` rows only)
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelCatalogCallParsingTests.cs` (the legacy-grammar and field-only facts), `CelMutateFunctionTests.cs`, `CelQualifiedNameParsingTests.cs` (the `upperAscii` row), `.claude/skills/alvo-descriptor-hooks/SKILL.md` (region, the `lowerAscii takes a field only` sentence, the `cel-condition` refused example)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelCaseFoldTests.cs`

**Not breaking:** `lowerAscii` already ships on `main` (field-only, Mutate-only; `examples/complex-crm/crm.alvo.json:109` uses it). This task only **widens** it: every source `main` accepts — `lowerAscii(field)` in a mutate — must compile and evaluate to the same value afterwards (`Lower_ascii_folds_a_to_z_and_nothing_else` is that proof, and `test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs` and `SchemaMigrationRunnerTests` apply `examples/complex-crm` — run them in Step 8). The parameter's name changes from `value` to `text`, which only the signature text shows.

**Interfaces:**
- Consumes: `CelBuiltInFunctions.InProcess` (C1), `CelFunctionCatalog`.
- Produces: `lowerAscii(text: String) -> String` and `upperAscii(text: String) -> String`, Condition + Mutate, catalogued with bodies; `internal static string CelBuiltInFunctions.LowerAsciiText(string)`, `UpperAsciiText(string)`. `CheckLegacyCall` handles `now()` alone.

- [ ] **Step 1: Write the failing tests** (`CelCaseFoldTests.cs`):

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The two ASCII folds are ordinary built-ins: any String argument, Condition and Mutate (spec E4).</summary>
public sealed class CelCaseFoldTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    [Theory]
    [InlineData("ABC def", "abc def")]
    [InlineData("ÄBC", "Äbc")]
    [InlineData("ẞ", "ẞ")]
    [InlineData("", "")]
    public void Lower_ascii_folds_a_to_z_and_nothing_else(string text, string expected) =>
        Mutate("lowerAscii(name)", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("abc DEF", "ABC DEF")]
    [InlineData("äbc", "äBC")]
    [InlineData("ß", "ß")]
    public void Upper_ascii_folds_a_to_z_and_nothing_else(string text, string expected) =>
        Mutate("upperAscii(name)", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("lowerAscii(trim(name))", "  AB ", "ab")]
    [InlineData("upperAscii(replace(name, ' ', ''))", "ab cd", "ABCD")]
    [InlineData("lowerAscii('ABC')", null, "abc")]
    public void A_fold_takes_any_string_expression(string source, string? name, string expected) =>
        Mutate(source, ("name", name)).ShouldBe(expected);

    [Fact]
    public void A_null_argument_folds_to_null() => Mutate("lowerAscii(name)", ("name", null)).ShouldBeNull();

    [Theory]
    [InlineData("lowerAscii(new.name) == 'x'")]
    [InlineData("upperAscii(old.name) != 'X'")]
    public void A_fold_is_legal_in_a_condition(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("lowerAscii(name) == 'x'", CelProfile.Rule)]
    [InlineData("lowerAscii(name)", CelProfile.Computed)]
    public void A_fold_is_refused_where_sql_renders_with_the_function_gate(string source, CelProfile profile) =>
        TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).Errors[0].Message
            .ShouldStartWith($"'lowerAscii(...)' is not available in the {profile} profile");

    [Fact]
    public void Lower_ascii_takes_one_argument() =>
        TestCelFunctions.Compiler().Compile("lowerAscii()", CelProfile.Mutate, TestCelFunctions.Items).Errors[0].Message
            .ShouldBe("'lowerAscii' takes 1 argument; this call passes 0.");

    [Fact]
    public void Now_is_still_the_one_legacy_call_and_mutate_only() =>
        TestCelFunctions.Compiler().Compile("now() == now()", CelProfile.Condition, TestCelFunctions.Items).Errors[0].Message
            .ShouldStartWith("'now(...)' is legal only in the Mutate profile");
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelCaseFoldTests'`
Expected: FAIL — `upperAscii` is not a recognized function; `lowerAscii(trim(name))` is a syntax error.

- [ ] **Step 3: The bodies** — in `CelBuiltInFunctions.cs`, replace the `LowerAscii` property and add `UpperAscii` and the folds; add both to `All` (keep `All`'s order irrelevant: the catalog sorts):

```csharp
    private static CelFunction LowerAscii => InProcess(
        CelCall.LowerAscii, CelValueType.String,
        "Folds A-Z to a-z and changes nothing else: accented and other non-ASCII letters stay as they are.",
        arguments => LowerAsciiText((string)arguments[0]!), Parameter("text", CelValueType.String));

    private static CelFunction UpperAscii => InProcess(
        "upperAscii", CelValueType.String,
        "Folds a-z to A-Z and changes nothing else: accented and other non-ASCII letters stay as they are.",
        arguments => UpperAsciiText((string)arguments[0]!), Parameter("text", CelValueType.String));

    /// <summary><c>lowerAscii</c>: an explicit A–Z loop, never <c>ToLowerInvariant</c>, which folds non-ASCII letters too.</summary>
    /// <param name="text">The text to fold.</param>
    /// <returns>The folded text.</returns>
    internal static string LowerAsciiText(string text) => Fold(text, 'A', 'Z', 'a' - 'A');

    /// <summary><c>upperAscii</c>: an explicit a–z loop, never <c>ToUpperInvariant</c>.</summary>
    /// <param name="text">The text to fold.</param>
    /// <returns>The folded text.</returns>
    internal static string UpperAsciiText(string text) => Fold(text, 'a', 'z', 'A' - 'a');

    private static string Fold(string text, char from, char to, int shift) =>
        string.Create(text.Length, (text, from, to, shift), static (span, state) =>
        {
            for (var index = 0; index < state.text.Length; index++)
            {
                var character = state.text[index];
                span[index] = character >= state.from && character <= state.to ? (char)(character + state.shift) : character;
            }
        });
```

Delete the old `LowerAscii` entry with `Body: null` and `MutateOnly`; `MutateOnly` stays for `Now`.

- [ ] **Step 4: Remove the legacy path** —
  - `CelParser.ParseCall`: delete the `CelCall.LowerAscii => ParseLowerAsciiCall(),` arm and the `ParseLowerAsciiCall` method; in `FieldOnlyCallFix` delete the `_ =>` lowerAscii branch (only `has`/`changed` reach it now — make `changed` the `_` arm) and in `ExpectFieldArgumentEnd`'s callers nothing else changes. `FieldOnlyCallFix`'s `nestable` test drops `CelCall.LowerAscii` from the excluded pair (it is nestable now).
  - `CelTypeChecker.CheckCall`: `call.Name is CelCall.Now ? CheckLegacyCall(call) : CheckCatalogCall(call)`. `CheckLegacyCall`: delete the `lowerAscii` switch arm; delete `CheckLowerAsciiCall`. Rewrite the `_allowedProfiles` remarks that say Mutate holds "the allow-listed legacy call" so they name `now()` only.
  - `CelInterpreter.EvaluateCall`: delete the `CelCall.LowerAscii` arm; delete `LowerAscii(object?)` and `FoldAsciiUpperCase` (now `CelBuiltInFunctions.LowerAsciiText`). `EvaluateMutation`'s remarks: drop "`lowerAscii` of a non-string is null".
  - `grep -rn "CelCall.LowerAscii\|FoldAsciiUpperCase\|ParseLowerAsciiCall\|CheckLowerAsciiCall" src` must print only the constant's declaration and the `LowerAscii` built-in.

- [ ] **Step 5: Re-judge the corpus rows that name `lowerAscii`, and only those** — add a temporary fact to `CelAcceptanceCorpusTests` (deleted again in this step):

```csharp
    [Fact]
    public void Regenerate_lowerAscii_rows()
    {
        var lines = Baseline().Select(expected => expected.Source.Contains("lowerAscii", StringComparison.Ordinal)
            ? Judge(_compiler, expected.Source, Enum.Parse<CelProfile>(expected.Profile))
            : expected);
        File.WriteAllLines(
            Path.Combine(RepositoryRoot.Find(), "artifacts", "lowerAscii-baseline.jsonl"),
            lines.Select(outcome => JsonSerializer.Serialize(outcome)));
    }
```

Run `dotnet test --project test/MMLib.Alvo.Tests --filter-method '*Regenerate_lowerAscii_rows'`, then `diff <(sed 's/\r$//' test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl) artifacts/lowerAscii-baseline.jsonl`. **Every differing line must have `lowerAscii` in its `Source`** — if any other line differs, the serializer options differ from the original file's: match them (the file escapes `'` as `'`, so use `JsonSerializer`'s default encoder as the original did) before going on. Copy the file over the baseline keeping the original line endings, delete the temporary fact and `artifacts/lowerAscii-baseline.jsonl`, and paste the list of moved rows (`Profile: Source — old first message → new first message`) into the commit body. (`RepositoryRoot.Find` is the e2e helper; if the unit project lacks it, write to `Path.GetTempPath()` instead.)

- [ ] **Step 6: Update the facts that pinned the narrow grammar** —
  - `CelCatalogCallParsingTests.The_legacy_calls_keep_their_narrow_grammar`: keep only `now(title)`; rename to `Now_keeps_its_narrow_grammar`.
  - `CelCatalogCallParsingTests.A_call_inside_a_field_only_call_is_refused_with_a_fix`: delete the two `lowerAscii(...)` rows (they parse now).
  - `CelCatalogCallParsingTests.Lower_keeps_the_lower_ascii_fix`: the fix may now say `lowerAscii(x)`; assert `ShouldContain("lowerAscii(")` and update `LowerAsciiSuggestion`'s text from `write lowerAscii(field)` to `write lowerAscii(text)`.
  - `CelMutateFunctionTests` facts asserting a `lowerAscii` refusal in Condition or of a non-field argument: invert them to acceptance with the reason "spec E4" in their summary.
  - `CelQualifiedNameParsingTests.A_receiver_call_after_an_image_is_told_the_global_form`: the `old.name.upperAscii()` row now expects `Write upperAscii(old.name)`.

- [ ] **Step 7: The skill follows** — in `.claude/skills/alvo-descriptor-hooks/SKILL.md`: region `` `lowerAscii` `math.abs` `math.round` `now` `replace` `size` `trim` `upperAscii` ``; replace "`lowerAscii` takes a field only; the others take any value of the right type" with "every function takes any value of the right type"; in `<!-- gen:cel-condition -->` move `lowerAscii(description)` out of *refused* and add `` `lowerAscii(new.description) == 'brake pads'` `` to *allowed*.

- [ ] **Step 8: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelCaseFoldTests' --filter-class '*CelCatalogCallParsingTests' --filter-class '*CelMutateFunctionTests' --filter-class '*CelQualifiedNameParsingTests' --filter-class '*CelAcceptanceCorpusTests' --filter-class '*CelProfileTests' --filter-class '*CelParser*'`, `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests'`, `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*AddAlvoIntegrationTests'`, `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*SchemaMigrationRunnerTests'`, then `scripts/test-ring0` and `scripts/test-ring1`.
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs src/MMLib.Alvo/Expressions/Internal/CelParser.cs src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl test/MMLib.Alvo.Tests/Expressions/CelCaseFoldTests.cs test/MMLib.Alvo.Tests/Expressions/CelCatalogCallParsingTests.cs test/MMLib.Alvo.Tests/Expressions/CelMutateFunctionTests.cs test/MMLib.Alvo.Tests/Expressions/CelQualifiedNameParsingTests.cs .claude/skills/alvo-descriptor-hooks/SKILL.md
git commit -m "feat(cel): lowerAscii is an ordinary built-in, and upperAscii joins it

Both take any String expression in a hook condition or mutate value; now()
is the one call left with its own grammar. Corpus rows moved (only rows
naming lowerAscii; old first message -> new):
<paste the list>

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 3: the text built-ins — `substring`, `contains`, `startsWith`, `endsWith`

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs` (declare the class `partial`; add the new entries to `All`)
- Create: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Text.cs`
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelBuiltInPropertyTests.cs`, `.claude/skills/alvo-descriptor-hooks/SKILL.md` (region)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelTextBuiltInTests.cs`

**Interfaces:**
- Consumes: `InProcess`, `Parameter`, `CelFunctionException(string name, string reason)` (C1).
- Produces: built-ins `substring` (2 overloads), `contains`, `startsWith`, `endsWith` (spec §5.1); `internal static string SubstringText(string text, long start, long? end)`.

- [ ] **Step 1: Write the failing tests** (`CelTextBuiltInTests.cs`):

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The text built-ins of spec §5.1, each against the edge cases C2's SQL must reproduce.</summary>
public sealed class CelTextBuiltInTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    private static bool Condition(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluatePredicate(TestCelFunctions.Compile(source, CelProfile.Condition), CelFixtures.Row(row), previous: null, CelFixtures.Anonymous);

    [Theory]
    [InlineData("héllo", 1L, 3L, "él")]
    [InlineData("😀ab", 1L, 3L, "ab")]
    [InlineData("abc", 0L, 3L, "abc")]
    [InlineData("abc", 3L, 3L, "")]
    [InlineData("abc", 1L, 1L, "")]
    public void Substring_cuts_code_points_start_inclusive_end_exclusive(string text, long start, long end, string expected) =>
        Mutate($"substring(name, {start}, {end})", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData("😀ab", 1L, "ab")]
    [InlineData("abc", 3L, "")]
    public void Substring_without_an_end_runs_to_the_end(string text, long start, string expected) =>
        Mutate($"substring(name, {start})", ("name", text)).ShouldBe(expected);

    [Fact]
    public void Substring_keeps_a_lone_surrogate_as_it_was() =>
        CelBuiltInFunctions.SubstringText("a\ud800b", 1, 2).ShouldBe("\ud800");

    [Theory]
    [InlineData(-1L, 2L)]
    [InlineData(2L, 1L)]
    [InlineData(0L, 4L)]
    [InlineData(4L, 4L)]
    public void Substring_outside_the_text_fails_closed_without_naming_a_position(long start, long end)
    {
        var failure = Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.SubstringText("abc", start, end));

        failure.FunctionName.ShouldBe("substring");
        failure.Reason.ShouldBe("a position is outside the text");
    }

    [Fact]
    public void The_truncation_recipe_cuts_only_what_is_too_long()
    {
        const string recipe = "substring(name, 0, math.least(size(name), 5))";

        Mutate(recipe, ("name", "abcdefgh")).ShouldBe("abcde");
        Mutate(recipe, ("name", "abc")).ShouldBe("abc");
    }

    [Theory]
    [InlineData("contains(new.name, 'b')", "abc", true)]
    [InlineData("contains(new.name, 'B')", "abc", false)]
    [InlineData("contains(new.name, '')", "abc", true)]
    [InlineData("startsWith(new.name, 'ab')", "abc", true)]
    [InlineData("startsWith(new.name, '')", "", true)]
    [InlineData("startsWith(new.name, 'e')", "é", true)]
    [InlineData("endsWith(new.name, '@kros.sk')", "a@kros.sk", true)]
    [InlineData("endsWith(new.name, '@KROS.SK')", "a@kros.sk", false)]
    [InlineData("endsWith(lowerAscii(new.name), '@kros.sk')", "A@KROS.SK", true)]
    public void A_text_test_is_ordinal_and_an_empty_search_matches(string source, string name, bool expected) =>
        Condition(source, ("name", name)).ShouldBe(expected);

    [Theory]
    [InlineData("contains(new.name, 'x')")]
    [InlineData("startsWith(new.name, 'x')")]
    [InlineData("endsWith(new.name, 'x')")]
    public void A_text_test_over_an_empty_field_does_not_fire(string source) =>
        Condition(source, ("name", null)).ShouldBeFalse("null in, null out; a condition reading null does not fire");

    /// <summary>
    /// The other half, and the reason the guided form offers no negated text tests (spec §10, D-2): '!' applies to the
    /// already-collapsed boolean (<c>CelInterpreter.AsBoolean</c>), so the negation of an empty test is <c>true</c>.
    /// </summary>
    [Fact]
    public void A_negated_text_test_over_an_empty_field_fires() =>
        Condition("!endsWith(new.name, 'x')", ("name", null)).ShouldBeTrue("!null collapses to !false, which is true");
}
```

`CelFixtures.Anonymous` is whatever `AlvoContext` the existing condition facts pass (`CelProfileTests` / `EventSubscriptionsTests`); use that name. The `math.least` row depends on Task 4: write it now and mark it `[Fact(Skip = "math.least arrives in Task 4")]`; Task 4 removes the skip.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelTextBuiltInTests'`
Expected: FAIL — `'substring' is not a recognized function.`

- [ ] **Step 3: Implement** — mark `CelBuiltInFunctions` `internal static partial class`, add `Substring(withEnd: false)`, `Substring(withEnd: true)`, `Contains`, `StartsWith`, `EndsWith` to `All`, and create `CelBuiltInFunctions.Text.cs`:

```csharp
using System.Text;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>The text built-ins of spec §5.1 beyond C1's: cutting a text, and testing what it holds.</summary>
internal static partial class CelBuiltInFunctions
{
    private static CelFunction Substring(bool withEnd) => withEnd
        ? InProcess(
            "substring", CelValueType.String, SubstringSummary,
            arguments => SubstringText((string)arguments[0]!, (long)arguments[1]!, (long)arguments[2]!),
            Parameter("text", CelValueType.String), Parameter("start", CelValueType.Int), Parameter("end", CelValueType.Int))
        : InProcess(
            "substring", CelValueType.String, SubstringSummary,
            arguments => SubstringText((string)arguments[0]!, (long)arguments[1]!, end: null),
            Parameter("text", CelValueType.String), Parameter("start", CelValueType.Int));

    private const string SubstringSummary =
        "The code points of text from start (counted from 0) up to, not including, end — or to the end of text; a position outside text fails the write.";

    private static CelFunction Contains => InProcess(
        "contains", CelValueType.Bool, "Whether text holds search anywhere, comparing characters exactly; an empty search is always held.",
        arguments => ((string)arguments[0]!).Contains((string)arguments[1]!, StringComparison.Ordinal),
        Parameter("text", CelValueType.String), Parameter("search", CelValueType.String));

    private static CelFunction StartsWith => InProcess(
        "startsWith", CelValueType.Bool, "Whether text begins with prefix, comparing characters exactly; every text begins with an empty prefix.",
        arguments => ((string)arguments[0]!).StartsWith((string)arguments[1]!, StringComparison.Ordinal),
        Parameter("text", CelValueType.String), Parameter("prefix", CelValueType.String));

    private static CelFunction EndsWith => InProcess(
        "endsWith", CelValueType.Bool, "Whether text ends with suffix, comparing characters exactly; every text ends with an empty suffix.",
        arguments => ((string)arguments[0]!).EndsWith((string)arguments[1]!, StringComparison.Ordinal),
        Parameter("text", CelValueType.String), Parameter("suffix", CelValueType.String));

    /// <summary>
    /// <c>substring</c>: code points, 0-based, end exclusive (cel-go's semantics). The cut copies the original UTF-16
    /// code units, so a lone surrogate inside the range survives as itself, and counts as one, as <see cref="SizeOf"/> does.
    /// </summary>
    /// <param name="text">The text to cut.</param>
    /// <param name="start">The first code point kept.</param>
    /// <param name="end">The first code point not kept, or <see langword="null"/> for the end of the text.</param>
    /// <returns>The cut text.</returns>
    /// <exception cref="CelFunctionException">A position is negative, reversed or past the end.</exception>
    internal static string SubstringText(string text, long start, long? end)
    {
        var offsets = CodePointOffsets(text);
        var count = offsets.Count - 1;
        var last = end ?? count;
        if (start < 0 || last < start || last > count)
        {
            throw new CelFunctionException("substring", "a position is outside the text");
        }

        return text[offsets[(int)start]..offsets[(int)last]];
    }

    /// <summary>The UTF-16 offset where each code point starts, and the text's length last.</summary>
    private static List<int> CodePointOffsets(string text)
    {
        var offsets = new List<int>(text.Length + 1);
        for (var at = 0; at < text.Length;)
        {
            offsets.Add(at);
            Rune.DecodeFromUtf16(text.AsSpan(at), out _, out var consumed);
            at += consumed;
        }

        offsets.Add(text.Length);
        return offsets;
    }
}
```

- [ ] **Step 4: Property test** — append to `CelBuiltInPropertyTests` (house CsCheck style, 2,000 iterations as its neighbours):

```csharp
    [Fact]
    public void Substring_agrees_with_a_code_point_list_for_every_range_inside_the_text() =>
        Gen.String.SelectMany(text =>
            {
                var size = (int)CelBuiltInFunctions.SizeOf(text);
                return Gen.Int[0, size].SelectMany(start => Gen.Int[start, size].Select(end => (text, start, end)));
            })
            .Sample(sample =>
            {
                var runes = sample.text.EnumerateRunes().Select(rune => rune.ToString()).ToList();
                var expected = string.Concat(runes.Skip(sample.start).Take(sample.end - sample.start));
                return SameCodePoints(CelBuiltInFunctions.SubstringText(sample.text, sample.start, sample.end), expected);
            }, iter: 2_000);

    private static bool SameCodePoints(string actual, string expected) =>
        actual.EnumerateRunes().Select(rune => rune.Value).SequenceEqual(expected.EnumerateRunes().Select(rune => rune.Value));
```

(`EnumerateRunes` maps a lone surrogate to U+FFFD on both sides, so the comparison is by code point as `size` counts them.)

- [ ] **Step 5: The skill follows** — region now `` `contains` `endsWith` `lowerAscii` `math.abs` `math.round` `now` `replace` `size` `startsWith` `substring` `trim` `upperAscii` ``; in `<!-- gen:cel-condition -->` *allowed* add `` `startsWith(new.description, 'Brake')` ``.

- [ ] **Step 6: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelTextBuiltInTests' --filter-class '*CelBuiltInPropertyTests' --filter-class '*CelFunctionCatalogTests' --filter-class '*CelAcceptanceCorpusTests'`, `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests'`, `scripts/test-ring0`.
Expected: PASS (the `math.least` fact skipped).

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Text.cs test/MMLib.Alvo.Tests/Expressions/CelTextBuiltInTests.cs test/MMLib.Alvo.Tests/Expressions/CelBuiltInPropertyTests.cs .claude/skills/alvo-descriptor-hooks/SKILL.md
git commit -m "feat(cel): substring, contains, startsWith and endsWith for hook conditions and mutate values

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 4: the math built-ins — `math.ceil`, `math.floor`, `math.greatest`, `math.least`, `math.round(x, digits)`

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Math.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs` (`All`), `test/MMLib.Alvo.Tests/Expressions/CelTextBuiltInTests.cs` (un-skip the recipe), `.claude/skills/alvo-descriptor-hooks/SKILL.md` (region)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelMathBuiltInTests.cs`

**Interfaces:**
- Consumes: `InProcess`, `Parameter` (C1); C1 F7 Int→Decimal widening in `ResolveOverload`.
- Produces: nine overloads (spec §5.2, E17): the eight above and `math.round(x: Decimal, digits: Int) -> Decimal`; `internal static decimal CelBuiltInFunctions.RoundTo(decimal value, long digits)`; `internal const string CelBuiltInFunctions.DigitsReason`. The apply-time refusal of a literal `digits` is Task 6's.

- [ ] **Step 1: Write the failing tests** (`CelMathBuiltInTests.cs`):

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The math built-ins of spec §5.2: Decimal stays Decimal, Int stays Int, null in is null out.</summary>
public sealed class CelMathBuiltInTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    [Theory]
    [InlineData("1.2", "2")]
    [InlineData("-1.5", "-1")]
    [InlineData("2.0", "2")]
    public void Ceil_is_the_smallest_whole_number_not_below(string value, string expected) =>
        Mutate("math.ceil(price)", ("price", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)))
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("1.8", "1")]
    [InlineData("-1.2", "-2")]
    public void Floor_is_the_largest_whole_number_not_above(string value, string expected) =>
        Mutate("math.floor(price)", ("price", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)))
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("math.ceil(qty)")]
    [InlineData("math.floor(qty)")]
    public void Ceil_and_floor_of_an_int_are_the_int(string source) => Mutate(source, ("qty", 7L)).ShouldBe(7L);

    [Fact]
    public void Greatest_and_least_of_two_ints_are_ints()
    {
        Mutate("math.greatest(qty, 3)", ("qty", 7L)).ShouldBe(7L);
        Mutate("math.least(qty, 3)", ("qty", 7L)).ShouldBe(3L);
    }

    [Fact]
    public void An_int_and_a_decimal_bind_the_decimal_overload() =>
        Mutate("math.greatest(qty, 2.5)", ("qty", 1L)).ShouldBe(2.5m);

    [Fact]
    public void Equal_values_answer_the_first_argument()
    {
        var answer = Mutate("math.greatest(price, 1.00)", ("price", 1.0m)).ShouldBeOfType<decimal>();

        answer.Scale.ShouldBe((byte)1, "the first argument's 1.0, not the second's 1.00");
    }

    [Theory]
    [InlineData("math.greatest(qty, 3)")]
    [InlineData("math.least(3, qty)")]
    [InlineData("math.ceil(price)")]
    public void A_null_argument_makes_the_call_null(string source) =>
        Mutate(source, ("qty", null), ("price", null)).ShouldBeNull();

    [Theory]
    [InlineData("2.345", 2L, "2.35")]
    [InlineData("-2.345", 2L, "-2.35")]
    [InlineData("2.5", 0L, "3")]
    [InlineData("0.125", 2L, "0.13")]
    public void Round_to_digits_halves_away_from_zero(string value, long digits, string expected) =>
        Mutate($"math.round(price, {digits})", ("price", decimal.Parse(value, CultureInfo.InvariantCulture)))
            .ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));

    [Fact]
    public void Round_to_more_digits_than_the_value_has_pads_nothing() =>
        Mutate("math.round(price, 5)", ("price", 1.2m)).ShouldBeOfType<decimal>().Scale.ShouldBe((byte)1);

    [Fact]
    public void Round_to_digits_widens_an_int() => Mutate("math.round(qty, 2)", ("qty", 7L)).ShouldBe(7m);

    [Theory]
    [InlineData(29L)]
    [InlineData(-1L)]
    public void Round_to_digits_outside_0_to_28_fails_closed_when_computed(long digits)
    {
        var failure = Should.Throw<CelFunctionException>(() => Mutate("math.round(price, qty)", ("price", 1.5m), ("qty", digits)));

        failure.FunctionName.ShouldBe("math.round");
        failure.Reason.ShouldBe("digits must be from 0 to 28");
    }

    [Fact]
    public void Round_to_digits_of_a_null_is_null() => Mutate("math.round(price, 2)", ("price", null)).ShouldBeNull();

    [Fact]
    public void Greatest_takes_exactly_two_arguments() =>
        TestCelFunctions.Compiler().Compile("math.greatest(qty, 1, 2)", CelProfile.Mutate, TestCelFunctions.Items).Errors[0].Message
            .ShouldBe("'math.greatest' takes 2 arguments; this call passes 3.");
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelMathBuiltInTests'`
Expected: FAIL — `math.ceil` is not catalogued.

- [ ] **Step 3: Implement** (`CelBuiltInFunctions.Math.cs`), and add `Ceil(Int)`, `Ceil(Decimal)`, `Floor(Int)`, `Floor(Decimal)`, `Greatest(Int)`, `Greatest(Decimal)`, `Least(Int)`, `Least(Decimal)` to `All` — **Int overload first**, as C1 orders `Abs`/`Round`, so an Int argument binds it exactly — and `RoundToDigits` right after `Round(CelValueType.Decimal)` (arity alone selects it; its parameters are named `x`, `digits`, so the overloads of `math.round` share `x` by position):

```csharp
namespace MMLib.Alvo.Expressions.Internal;

/// <summary>The math built-ins of spec §5.2 beyond C1's: whole numbers either way, and the larger or smaller of two.</summary>
internal static partial class CelBuiltInFunctions
{
    private static CelFunction Ceil(CelValueType type) => InProcess(
        "math.ceil", type, "The smallest whole number not below x (1.2 is 2, -1.5 is -1), of the same numeric type.",
        arguments => type == CelValueType.Int ? arguments[0] : Math.Ceiling((decimal)arguments[0]!), Parameter("x", type));

    private static CelFunction Floor(CelValueType type) => InProcess(
        "math.floor", type, "The largest whole number not above x (1.8 is 1, -1.2 is -2), of the same numeric type.",
        arguments => type == CelValueType.Int ? arguments[0] : Math.Floor((decimal)arguments[0]!), Parameter("x", type));

    private static CelFunction Greatest(CelValueType type) => InProcess(
        "math.greatest", type, "The larger of a and b; a when they are equal.",
        arguments => type == CelValueType.Int
            ? ((long)arguments[0]! >= (long)arguments[1]! ? arguments[0] : arguments[1])
            : ((decimal)arguments[0]! >= (decimal)arguments[1]! ? arguments[0] : arguments[1]),
        Parameter("a", type), Parameter("b", type));

    private static CelFunction Least(CelValueType type) => InProcess(
        "math.least", type, "The smaller of a and b; a when they are equal.",
        arguments => type == CelValueType.Int
            ? ((long)arguments[0]! <= (long)arguments[1]! ? arguments[0] : arguments[1])
            : ((decimal)arguments[0]! <= (decimal)arguments[1]! ? arguments[0] : arguments[1]),
        Parameter("a", type), Parameter("b", type));

    /// <summary>Why <c>math.round(x, digits)</c> refuses a <c>digits</c> outside what a <see cref="decimal"/> can hold (spec §5.5).</summary>
    internal const string DigitsReason = "digits must be from 0 to 28";

    private static CelFunction RoundToDigits => InProcess(
        "math.round", CelValueType.Decimal,
        "x rounded to digits places after the point, halves away from zero (2.345 to 2 places is 2.35); digits is from 0 to 28.",
        arguments => RoundTo((decimal)arguments[0]!, (long)arguments[1]!),
        Parameter("x", CelValueType.Decimal), Parameter("digits", CelValueType.Int));

    /// <summary>
    /// <c>math.round(x, digits)</c>: halves away from zero, like the one-argument form (deviation F17 — cel-go has no
    /// <c>digits</c> overload; rounding a price to cents is why Alvo does). Never pads: 1.2 to 5 places stays 1.2.
    /// </summary>
    /// <param name="value">The number to round.</param>
    /// <param name="digits">How many places after the point to keep, 0 to 28.</param>
    /// <returns>The rounded number.</returns>
    /// <exception cref="CelFunctionException"><paramref name="digits"/> is outside 0 to 28.</exception>
    internal static decimal RoundTo(decimal value, long digits) => digits is >= 0 and <= 28
        ? Math.Round(value, (int)digits, MidpointRounding.AwayFromZero)
        : throw new CelFunctionException("math.round", DigitsReason);
}
```

- [ ] **Step 4: Un-skip** `CelTextBuiltInTests.The_truncation_recipe_cuts_only_what_is_too_long`. Skill region gains `` `math.ceil` `math.floor` `math.greatest` `math.least` `` in ordinal place (`math.round` is already there; the region lists names, not overloads). If `CelFunctionCatalogTests` or `ManagementCelFunctionsTests` pin C1's overload count, raise it by nine here and say so in the commit; Task 6 pins the final 32.

- [ ] **Step 5: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelMathBuiltInTests' --filter-class '*CelTextBuiltInTests' --filter-class '*CelFunctionTypeCheckTests' --filter-class '*CelFunctionCatalogTests'`, `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests'`, `scripts/test-ring0`.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Math.cs test/MMLib.Alvo.Tests/Expressions/CelMathBuiltInTests.cs test/MMLib.Alvo.Tests/Expressions/CelTextBuiltInTests.cs .claude/skills/alvo-descriptor-hooks/SKILL.md
git commit -m "feat(cel): math.ceil, math.floor, math.greatest, math.least, and math.round to digits

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 5: the conversions — `string`, `int`, `timestamp`; `string()` refuses a `date` field; built-ins first among reserved names

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Conversions.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs` (`All`), `test/MMLib.Alvo.Tests/Expressions/CelBuiltInPropertyTests.cs`, `.claude/skills/alvo-descriptor-hooks/SKILL.md` (region)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` (`Bind` `:973` at C1 HEAD, new `RefusesDateText`) — spec §6.5, E18, D-9
- Modify: `src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs` (`ReservedReason` `:68`: the built-in arm first — D-14), `test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs` (rows whose expected reason moves)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelConversionBuiltInTests.cs`

**Interfaces:**
- Consumes: `CelArgumentMarshaller` (C1: a `date` column's `DateOnly` becomes midnight UTC for a Timestamp parameter).
- Produces: `string` ×5, `int` ×2, `timestamp` ×1 (spec §5.3); `internal static string StringOf(object)`, `long IntOf(decimal)`, `long IntOf(string)`, `DateTimeOffset TimestampOf(string)`; the compile error of spec §8 row "`string()` over a `date` field".

- [ ] **Step 1: Write the failing tests** (`CelConversionBuiltInTests.cs`):

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The conversions of spec §5.3: CEL's standard names, one engine-agnostic text form per type.</summary>
public sealed class CelConversionBuiltInTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    [Theory]
    [InlineData(-42L, "-42")]
    [InlineData(0L, "0")]
    public void String_of_an_int_is_its_invariant_digits(long value, string expected) =>
        Mutate("string(qty)", ("qty", value)).ShouldBe(expected);

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("2.0", "2")]
    [InlineData("-0.0", "0")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("12345678901234.5678", "12345678901234.5678")]
    public void String_of_a_decimal_is_its_shortest_form(string value, string expected) =>
        Mutate("string(price)", ("price", decimal.Parse(value, CultureInfo.InvariantCulture))).ShouldBe(expected);

    [Fact]
    public void String_of_a_uuid_is_lower_case_with_hyphens() =>
        Mutate("string(ref_id)", ("ref_id", Guid.Parse("0F8FAD5B-D9CB-469F-A165-70867728950E"))).ShouldBe("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void String_of_a_timestamp_is_rfc_3339_in_utc_without_trailing_zeros()
    {
        CelBuiltInFunctions.StringOf(new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(2))).ShouldBe("2026-10-05T12:00:00Z");
        CelBuiltInFunctions.StringOf(new DateTimeOffset(2026, 10, 5, 12, 0, 0, 500, TimeSpan.Zero)).ShouldBe("2026-10-05T12:00:00.5Z");
    }

    /// <summary>
    /// A <c>date</c> reaches CEL as midnight UTC, so its text would pin <c>…T00:00:00Z</c> — a form a later Date type would
    /// want to change, which a stored value forbids. Refused until then (spec E18, D-9); a <c>datetime</c> is fine.
    /// </summary>
    [Theory]
    [InlineData("string(due)", CelProfile.Mutate)]
    [InlineData("string(new.due)", CelProfile.Mutate)]
    [InlineData("string(old.due) == 'x'", CelProfile.Condition)]
    public void String_of_a_date_field_is_refused_at_apply(string source, CelProfile profile)
    {
        var error = TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(
            "'string(...)' cannot take the date field 'due' yet: its text form is not settled, and a hook that stored one could not change it later.");
        error.FixSuggestion.ShouldBe("Store the date's text from the client, or make 'due' a datetime field, whose text is an RFC 3339 instant.");
        error.Position.ShouldBe(source.IndexOf("string", StringComparison.Ordinal));
    }

    [Fact]
    public void String_of_a_datetime_field_compiles()
    {
        var withMoment = TestCelFunctions.Items with
        {
            Fields = [.. TestCelFunctions.Items.Fields, new FieldSchema { Name = "moment", Type = FieldType.DateTime, Nullable = true }],
        };

        TestCelFunctions.Compiler().Compile("string(moment)", CelProfile.Mutate, withMoment).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void String_of_a_bool_is_true_or_false()
    {
        CelBuiltInFunctions.StringOf(true).ShouldBe("true");
        CelBuiltInFunctions.StringOf(false).ShouldBe("false");
    }

    [Theory]
    [InlineData("2.9", 2L)]
    [InlineData("-2.9", -2L)]
    public void Int_of_a_decimal_truncates_toward_zero(string value, long expected) =>
        Mutate("int(price)", ("price", decimal.Parse(value, CultureInfo.InvariantCulture))).ShouldBe(expected);

    [Theory]
    [InlineData("+7", 7L)]
    [InlineData("-7", -7L)]
    [InlineData("007", 7L)]
    public void Int_of_a_text_reads_base_ten_with_a_sign(string text, long expected) =>
        Mutate("int(name)", ("name", text)).ShouldBe(expected);

    [Theory]
    [InlineData(" 7")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("")]
    [InlineData("9223372036854775808")]
    [InlineData("٣")]
    public void Int_of_any_other_text_fails_closed(string text) =>
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.IntOf(text)).Reason.ShouldBe("the text is not a whole number such as 42 or -7");

    [Fact]
    public void Int_of_a_decimal_past_the_int_range_fails_closed() =>
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.IntOf(1e28m)).Reason.ShouldBe("the value is outside the range of an Int");

    [Theory]
    [InlineData("2026-10-05T12:00:00Z")]
    [InlineData("2026-10-05T14:00:00+02:00")]
    [InlineData("2026-10-05T12:00:00.000000000Z")]
    public void Timestamp_reads_rfc_3339(string text) =>
        CelBuiltInFunctions.TimestampOf(text).ShouldBe(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Timestamp_keeps_a_hundred_nanoseconds_and_drops_the_rest() =>
        CelBuiltInFunctions.TimestampOf("2026-10-05T12:00:00.123456789Z").Ticks.ShouldBe(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero).Ticks + 1_234_567);

    [Theory]
    [InlineData("2026-10-05")]
    [InlineData("2026-10-05 12:00:00Z")]
    [InlineData("2026-10-05t12:00:00z")]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-10-05T12:00:00")]
    [InlineData("0000-01-01T00:00:00Z")]
    [InlineData("2026-10-05T12:00:00.1234567890Z")]
    [InlineData("２０２６-10-05T12:00:00Z")]
    public void Timestamp_of_anything_else_fails_closed(string text) =>
        Should.Throw<CelFunctionException>(() => CelBuiltInFunctions.TimestampOf(text)).Reason
            .ShouldBe("the text is not an RFC 3339 timestamp such as 2026-10-05T12:00:00Z");

    [Fact]
    public void A_date_field_compares_with_a_fixed_instant_in_a_condition() =>
        CelInterpreter.EvaluatePredicate(
            TestCelFunctions.Compile("new.due < timestamp('2026-12-01T00:00:00Z')", CelProfile.Condition),
            CelFixtures.Row(("due", new DateOnly(2026, 10, 5))), previous: null, CelFixtures.Anonymous).ShouldBeTrue();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelConversionBuiltInTests'`
Expected: FAIL — `'string' is not a recognized function.`

- [ ] **Step 3: Implement** (`CelBuiltInFunctions.Conversions.cs`), and add `String(t)` for `Int, Decimal, Bool, Uuid, Timestamp`, `Int(Decimal)`, `Int(String)`, `Timestamp` to `All`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>CEL's standard conversions <c>string</c>, <c>int</c> and <c>timestamp</c>, with one text form per type (spec §5.3).</summary>
internal static partial class CelBuiltInFunctions
{
    private const string TimestampFailure = "the text is not an RFC 3339 timestamp such as 2026-10-05T12:00:00Z";

    private static readonly string[] _rfc3339Formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    private static CelFunction String(CelValueType type) => InProcess(
        "string", CelValueType.String,
        "value as text: digits for a number (no trailing zeros), true or false, a lower-case id, or an RFC 3339 instant in UTC.",
        arguments => StringOf(arguments[0]!), Parameter("value", type));

    private static CelFunction Int(CelValueType type) => InProcess(
        "int", CelValueType.Int,
        "value as a whole number: a decimal cut toward zero (2.9 is 2), or a text of digits with an optional sign; anything else fails the write.",
        arguments => type == CelValueType.Decimal ? IntOf((decimal)arguments[0]!) : IntOf((string)arguments[0]!), Parameter("value", type));

    private static CelFunction Timestamp => InProcess(
        "timestamp", CelValueType.Timestamp,
        "text read as an RFC 3339 instant, such as 2026-10-05T12:00:00Z or 2026-10-05T14:00:00+02:00; anything else fails the write.",
        arguments => TimestampOf((string)arguments[0]!), Parameter("text", CelValueType.String));

    /// <summary><c>string</c>: the one text form per type spec §5.3 pins, culture-free.</summary>
    /// <param name="value">An Int (<see cref="long"/>), Decimal, Bool, Uuid or Timestamp.</param>
    /// <returns>Its text.</returns>
    internal static string StringOf(object value) => value switch
    {
        long number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => number == 0m ? "0" : number.ToString("0.############################", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        Guid id => id.ToString("D"),
        DateTimeOffset instant => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + "Z",
        _ => throw new CelFunctionException("string", "the value has no text form"),
    };

    /// <summary><c>int(Decimal)</c>: cut toward zero.</summary>
    /// <param name="value">The decimal.</param>
    /// <returns>The whole number.</returns>
    /// <exception cref="CelFunctionException">The result does not fit an Int.</exception>
    internal static long IntOf(decimal value)
    {
        var truncated = decimal.Truncate(value);
        return truncated is >= long.MinValue and <= long.MaxValue
            ? (long)truncated
            : throw new CelFunctionException("int", "the value is outside the range of an Int");
    }

    /// <summary><c>int(String)</c>: <c>[+-]?[0-9]+</c>, base 10 — Go's <c>ParseInt(s, 10, 64)</c> (deviation F14).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The whole number.</returns>
    /// <exception cref="CelFunctionException">The text is anything else, or out of range.</exception>
    internal static long IntOf(string text)
    {
        var digits = text.AsSpan((text.Length > 0 && text[0] is '+' or '-') ? 1 : 0);
        return digits.Length > 0 && !digits.ContainsAnyExceptInRange('0', '9')
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new CelFunctionException("int", "the text is not a whole number such as 42 or -7");
    }

    /// <summary>
    /// <c>timestamp(String)</c>: RFC 3339 with upper-case <c>T</c> and <c>Z</c>, at most nine fraction digits, of which
    /// the first seven (100 ns, .NET's resolution) are kept (deviation F13).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The instant, with the offset the text gave.</returns>
    /// <exception cref="CelFunctionException">The text is not such a timestamp, or names a day that does not exist.</exception>
    internal static DateTimeOffset TimestampOf(string text)
    {
        var match = Rfc3339().Match(text);
        var fraction = match.Groups["fraction"].Value;
        var kept = $"{match.Groups["main"].Value}{(fraction.Length > 0 ? "." + fraction[..Math.Min(7, fraction.Length)] : string.Empty)}{match.Groups["zone"].Value}";
        return match.Success && DateTimeOffset.TryParseExact(kept, _rfc3339Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant
            : throw new CelFunctionException("timestamp", TimestampFailure);
    }

    /// <summary>RFC 3339 §5.6, ASCII digits only (<c>\d</c> would admit every Unicode digit).</summary>
    [GeneratedRegex(@"^(?<main>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\.(?<fraction>[0-9]{1,9}))?(?<zone>Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Rfc3339();
}
```

`String`, `Int` and `Timestamp` as member names shadow nothing the class uses (`string`/`int` keywords are unaffected); if the analyzer objects, name them `ToStringFunction`, `ToIntFunction`, `ToTimestampFunction`. `0000-01-01` fails `TryParseExact` (year 1 is the minimum).

- [ ] **Step 3b: Refuse `string()` over a `date` field** — in `CelTypeChecker.Visitor`, replace `Bind` (C1's is expression-bodied at `:973`) and add `RefusesDateText`:

```csharp
        private (CelNode, CelValueType, bool, int) Bind(CelCall call, CelFunction? overload, bool profileBad, int position)
        {
            if (overload is null)
            {
                return Unbound(call, position);
            }

            var refused = !profileBad && RefusesDateText(call, overload, position);
            return (call with { ResultType = overload.ResultType, Function = overload }, overload.ResultType, profileBad || refused, position);
        }

        /// <summary>
        /// <c>string()</c> over a <c>date</c> field (spec §6.5, E18): its value reaches CEL as midnight UTC, and the text
        /// that would pin is one a later Date type would want to change — after a hook had stored it. A field reference is
        /// the only way a <c>date</c> reaches a call in Condition or Mutate, so it is the whole surface.
        /// </summary>
        private bool RefusesDateText(CelCall call, CelFunction overload, int position)
        {
            if (overload is not { Name: "string", IsHost: false, Parameters: [{ Type: CelValueType.Timestamp }] }
                || call.Arguments is not [CelFieldRef fieldRef]
                || ResolveField(fieldRef.FieldName) is not { Type: FieldType.Date })
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"'string(...)' cannot take the date field '{fieldRef.FieldName}' yet: its text form is not settled, and a hook "
                + "that stored one could not change it later.",
                $"Store the date's text from the client, or make '{fieldRef.FieldName}' a datetime field, whose text is an RFC "
                + "3339 instant.",
                position));
            return true;
        }
```

`ResolveField(string)` is the visitor's existing field lookup (`IsNeverNull` uses it); `old.due` and `new.due` carry `FieldName` `due`. If the file lacks `using MMLib.Alvo.Schema;` for `FieldType`, add it.

- [ ] **Step 3c: Built-ins first among reserved names** (D-14) — in `HostCelFunction.ReservedReason`, move the arm `_ when CelFunctionCatalog.BuiltIns.Contains(name) => "it is a built-in function",` to the top of the switch, so `contains`, `startsWith`, `endsWith`, `int`, `string` and `timestamp` — built-ins now — are refused as built-ins rather than as "a standard CEL type or function name" (that arm keeps `uint double bool bytes list duration dyn type matches`, still unbuilt). Run `grep -n "standard CEL type or function name" test/MMLib.Alvo.Tests/Expressions/*.cs` and change each row naming one of the six to expect `"it is a built-in function"`.

- [ ] **Step 3d: Check cel-go's timestamp text** — read cel-go's `common/types/timestamp.go` (`ConvertToType` to `StringType`) and `stringToTimestamp`. If cel-go keeps the offset a timestamp was parsed with when it writes it back as text, add one sentence to spec §16 F12 ("cel-go keeps the parsed offset; Alvo writes UTC because it stores instants, not offsets") and carry it into cel.md's deviation 28 in Task 17; if it writes UTC, delete the "*unverified*" sentence from F12. Either way the code does not change.

- [ ] **Step 4: Property tests** — append to `CelBuiltInPropertyTests`:

```csharp
    [Fact]
    public void String_then_timestamp_is_the_same_instant() =>
        Gen.DateTimeOffset.Where(value => value.Year >= 1).Sample(
            value => CelBuiltInFunctions.TimestampOf(CelBuiltInFunctions.StringOf(value)) == value, iter: 2_000);

    [Fact]
    public void String_then_int_is_the_same_whole_number() =>
        Gen.Long.Sample(value => CelBuiltInFunctions.IntOf(CelBuiltInFunctions.StringOf(value)) == value, iter: 2_000);
```

- [ ] **Step 5: The skill follows** — region gains `` `int` `string` `timestamp` `` in ordinal place (the full list is now the 19 names of spec §5).

- [ ] **Step 6: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelConversionBuiltInTests' --filter-class '*CelBuiltInPropertyTests' --filter-class '*CelFunctionCatalogTests' --filter-class '*CelArgumentConversionTests' --filter-class '*CelFunctionRegistrationTests' --filter-class '*CelAcceptanceCorpusTests'`, `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests'`, `scripts/test-ring0`.
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.cs src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Conversions.cs src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs src/MMLib.Alvo/Expressions/Internal/HostCelFunction.cs test/MMLib.Alvo.Tests/Expressions/CelConversionBuiltInTests.cs test/MMLib.Alvo.Tests/Expressions/CelBuiltInPropertyTests.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionRegistrationTests.cs .claude/skills/alvo-descriptor-hooks/SKILL.md docs/superpowers/specs/2026-10-06-f5-hook-functions-end-to-end-design.md
git commit -m "feat(cel): string, int and timestamp conversions with one text form per type

string() refuses a date field at apply until a Date type settles its text
(spec E18); a host naming a built-in hears that it is one.

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 6: a built-in call its literal arguments make fail is refused at apply

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` (`Bind` as Task 5 left it, new `FailsWithConstants`, `ConstantReason`)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelFunction.cs` (an internal `ConstantCheck` init property), `CelBuiltInFunctions.Math.cs` (`RoundToDigits` declares one)
- Modify: `test/MMLib.Alvo.Api.Tests/Management/ExpressionCheckAgreementTests.cs` (`_cases`), `test/MMLib.Alvo.Tests/Expressions/CelFunctionCatalogTests.cs` (catalog-wide facts)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelConstantCallTests.cs`

**Interfaces:**
- Consumes: `CelFunction.Invoke`, `IsHost`, `IsLegacy` (C1); `CelLiteral.Value`; `CelBuiltInFunctions.DigitsReason` (Task 4).
- Produces: `public Func<IReadOnlyList<object?>, string?>? ConstantCheck { get; init; }` on the internal `CelFunction` record; the compile errors of spec §8 rows "constant call that always fails" and "`math.round` with a literal `digits` out of range".

- [ ] **Step 1: Write the failing tests** (`CelConstantCallTests.cs`):

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A built-in call over literals only is evaluated once at apply: if it can only fail, the descriptor is refused there
/// rather than answering function-failed on every write (spec E5, deviation F15). Host code never runs at apply.
/// </summary>
public sealed class CelConstantCallTests
{
    [Theory]
    [InlineData("new.due < timestamp('2026-13-01T00:00:00Z')", "timestamp", "the text is not an RFC 3339 timestamp")]
    [InlineData("substring('abc', 0, 5) == 'x'", "substring", "a position is outside the text")]
    [InlineData("int('seven') == 7", "int", "the text is not a whole number")]
    public void A_constant_call_that_always_fails_is_refused_with_its_reason(string source, string function, string reason)
    {
        var error = TestCelFunctions.Compiler().Compile(source, CelProfile.Condition, TestCelFunctions.Items).Errors.ShouldHaveSingleItem();

        error.Message.ShouldStartWith($"'{function}(...)' always fails with these constant arguments: {reason}");
        error.FixSuggestion.ShouldBe("Correct the constant, or pass a field instead of a literal.");
        error.Position.ShouldBe(source.IndexOf(function, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("new.due < timestamp('2026-10-05T12:00:00Z')")]
    [InlineData("substring(new.name, 0, 5) == 'x'")]
    [InlineData("math.ceil(1.5) == 2")]
    [InlineData("startsWith('abc', 'a')")]
    public void A_constant_call_that_succeeds_or_reads_a_field_compiles(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_host_function_with_literal_arguments_is_never_run_at_apply()
    {
        var calls = 0;
        var counting = TestCelFunctions.Host("count", CelValueType.Bool, _ => ++calls > 0, TestCelFunctions.Parameter("s", CelValueType.String));

        TestCelFunctions.Compiler(counting).Compile("count('x')", CelProfile.Condition, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

        calls.ShouldBe(0);
    }

    /// <summary>
    /// A declared constant check runs although another argument is a field (spec §6.4, E17): <c>digits</c> 30 can only
    /// ever fail, whatever the price. <c>-1</c> is a negation, not a literal, so it fails at run time instead (Task 4).
    /// </summary>
    [Theory]
    [InlineData("math.round(price, 29)")]
    [InlineData("math.round(new.price, 30)")]
    [InlineData("math.round(2.5, 100)")]
    public void A_literal_digits_out_of_range_is_refused_although_x_may_be_a_field(string source)
    {
        var error = TestCelFunctions.Compiler().Compile(source, CelProfile.Mutate, TestCelFunctions.Items).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'math.round(...)' always fails with these constant arguments: digits must be from 0 to 28.");
        error.Position.ShouldBe(0);
    }

    [Theory]
    [InlineData("math.round(price, 2)")]
    [InlineData("math.round(price, 28)")]
    [InlineData("math.round(price, qty)")]
    public void Digits_in_range_or_computed_compile(string source) =>
        TestCelFunctions.Compiler().Compile(source, CelProfile.Mutate, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_call_refused_by_its_profile_is_not_evaluated_too() =>
        TestCelFunctions.Compiler().Compile("int('seven') == 7", CelProfile.Rule, TestCelFunctions.Items).Errors.ShouldHaveSingleItem()
            .Message.ShouldStartWith("'int(...)' is not available in the Rule profile");
}
```

And in `CelFunctionCatalogTests` add the catalog-wide facts spec §5 pins:

```csharp
    [Fact]
    public void The_built_ins_are_the_nineteen_names_of_the_design() =>
        CelFunctionCatalog.BuiltIns.Names.ShouldBe(
        [
            "contains", "endsWith", "int", "lowerAscii", "math.abs", "math.ceil", "math.floor", "math.greatest", "math.least",
            "math.round", "now", "replace", "size", "startsWith", "string", "substring", "timestamp", "trim", "upperAscii",
        ]);

    [Fact]
    public void There_are_thirty_two_built_in_overloads() => CelFunctionCatalog.BuiltIns.Functions.Count.ShouldBe(32);

    [Fact]
    public void Only_the_digits_overload_declares_a_constant_check() =>
        CelFunctionCatalog.BuiltIns.Functions.Where(function => function.ConstantCheck is not null)
            .ShouldHaveSingleItem().Signature().ShouldBe("math.round(x: Decimal, digits: Int) -> Decimal");

    [Fact]
    public void Every_built_in_but_now_is_offered_in_condition_and_mutate_only() =>
        CelFunctionCatalog.BuiltIns.Functions.Where(function => function.Name != CelCall.Now)
            .ShouldAllBe(function => function.Profiles.SetEquals(new[] { CelProfile.Condition, CelProfile.Mutate }));

    [Fact]
    public void Overloads_of_one_name_share_their_parameter_names_by_position() =>
        CelFunctionCatalog.BuiltIns.Functions.GroupBy(function => function.Name).ShouldAllBe(group =>
            group.All(overload => overload.Parameters.Select(p => p.Name)
                .SequenceEqual(group.OrderByDescending(o => o.Parameters.Count).First().Parameters.Take(overload.Parameters.Count).Select(p => p.Name))));

    [Fact]
    public void Every_built_in_summary_is_one_sentence() =>
        CelFunctionCatalog.BuiltIns.Functions.ShouldAllBe(function => function.Summary.EndsWith('.') && function.Summary.Length <= 200);
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelConstantCallTests' --filter-class '*CelFunctionCatalogTests'`
Expected: build error — `CelFunction.ConstantCheck` does not exist; once it does, FAIL — the always-failing sources compile.

- [ ] **Step 3: Implement** — first the declaration, in `CelFunction.cs`, inside the record:

```csharp
    /// <summary>
    /// A check of the literal arguments alone, run at apply (spec §6.4): given each argument's literal value — or
    /// <see langword="null"/> where the argument is not a literal — it answers why the call would always fail, or
    /// <see langword="null"/>. Built-ins only; a host function never declares one, because host code never runs at apply.
    /// </summary>
    public Func<IReadOnlyList<object?>, string?>? ConstantCheck { get; init; }
```

then in `CelBuiltInFunctions.Math.cs`, `RoundToDigits` declares it (`InProcess(...) with { ConstantCheck = DigitsCheck }`):

```csharp
    /// <summary><c>math.round(x, digits)</c>'s constant check: a literal <c>digits</c> outside 0 to 28 can only fail.</summary>
    private static string? DigitsCheck(IReadOnlyList<object?> literals) =>
        literals[1] is long digits && digits is < 0 or > 28 ? DigitsReason : null;
```

then in `CelTypeChecker.Visitor`, replace `Bind` (Task 5's) and add `FailsWithConstants` and `ConstantReason`:

```csharp
        private (CelNode, CelValueType, bool, int) Bind(CelCall call, CelFunction? overload, bool profileBad, int position)
        {
            if (overload is null)
            {
                return Unbound(call, position);
            }

            var refused = !profileBad && (RefusesDateText(call, overload, position) || FailsWithConstants(call, overload, position));
            return (call with { ResultType = overload.ResultType, Function = overload }, overload.ResultType, profileBad || refused, position);
        }

        /// <summary>
        /// A built-in call whose literal arguments make it fail (spec E5, §6.4) is one error here instead of a
        /// function-failed answer on every write. The tree is not rewritten. Host functions are never run at apply —
        /// purity is their contract, not a guarantee (C1 X7).
        /// </summary>
        private bool FailsWithConstants(CelCall call, CelFunction overload, int position)
        {
            if (overload.IsHost || overload.IsLegacy || !call.Arguments.Any(argument => argument is CelLiteral)
                || ConstantReason(call, overload) is not { } reason)
            {
                return false;
            }

            Errors.Add(new CelCompilationError(
                $"'{call.Name}(...)' always fails with these constant arguments: {reason}.",
                "Correct the constant, or pass a field instead of a literal.",
                position));
            return true;
        }

        /// <summary>
        /// Why the literal arguments make the call fail, or <see langword="null"/>: the overload's declared check first
        /// (it sees a non-literal argument as <see langword="null"/>), then — only when every argument is a literal — one
        /// evaluation with exactly those values.
        /// </summary>
        private static string? ConstantReason(CelCall call, CelFunction overload)
        {
            object?[] literals = [.. call.Arguments.Select(argument => argument is CelLiteral literal ? literal.Value : null)];
            if (overload.ConstantCheck?.Invoke(literals) is { } declared)
            {
                return declared;
            }

            if (!call.Arguments.All(argument => argument is CelLiteral))
            {
                return null;
            }

            try
            {
                overload.Invoke(literals);
                return null;
            }
            catch (CelFunctionException failure)
            {
                return failure.Reason ?? "it failed";
            }
        }
```

- [ ] **Step 4: Parity with `cel/check`** — add to `ExpressionCheckAgreementTests._cases`, next to the existing `beforeHook` rows (the fixture entity's text field is `note`):

```csharp
        ("beforeHook", BeforeCreate, "startsWith(new.note, 'x')"),
        ("beforeHook", BeforeCreate, "new.quantity < int('seven')"),
        ("beforeHook", BeforeCreate, "math.greatest(new.quantity, 3) > 4"),
        ("beforeHook", BeforeCreate, "math.round(new.quantity, 30) > 1"),
```

plus one mutate row and one after-hook row in the same form as their neighbours, using `upperAscii(trim(new.note))` (mutate) and `endsWith(new.note, '@x')` (after-hook condition).

- [ ] **Step 5: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelConstantCallTests' --filter-class '*CelFunctionCatalogTests' --filter-class '*CelAcceptanceCorpusTests'`, `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ExpressionCheckAgreementTests'`, `scripts/test-ring0`, `scripts/test-ring1`.
Expected: PASS. `ExpressionCheckAgreementTests.The_corpus_reaches_both_outcomes_for_every_slot_kind` still holds.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs src/MMLib.Alvo/Expressions/Internal/CelFunction.cs src/MMLib.Alvo/Expressions/Internal/CelBuiltInFunctions.Math.cs test/MMLib.Alvo.Tests/Expressions/CelConstantCallTests.cs test/MMLib.Alvo.Tests/Expressions/CelFunctionCatalogTests.cs test/MMLib.Alvo.Api.Tests/Management/ExpressionCheckAgreementTests.cs
git commit -m "feat(cel): refuse at apply a built-in call its constant arguments can only make fail

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 7: fail-closed arithmetic in hook conditions and mutate values

**Files:**
- Create: `src/MMLib.Alvo/Expressions/Internal/CelHookArithmetic.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs` (at C1 HEAD: `EvaluatePredicate` `:103`, `EvaluateMutation` `:216` and its remarks, `EvaluateUnary` `:337`, `EvaluateBinary` `:344`, `EvaluateAdd` `:359`, `EvaluateArithmetic` `:607`, `Negate` `:647`, `EvalState` `:651`)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` (`_allowedProfiles` `:147-165` and its remarks, a new `_computedConditionAndMutate` set, the messages in `CheckNegate` `:405` and `CheckArithmetic` `:473`)
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl` (rows carrying an arithmetic gate message only)
- Modify: `.claude/skills/alvo-descriptor-hooks/SKILL.md` (only if an example it lists as *refused* is now admitted)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelHookArithmeticTests.cs`

**Interfaces:**
- Consumes: `CompiledExpression.Profile` (Abstractions); `CelFunctionException(string functionName, string reason)` (C1); `CelInterpreter.TryToDecimal` (internal, `:569`).
- Produces: arithmetic (`+ - * /`, unary `-`) legal in Condition and Mutate; in those two profiles — and only those — Int arithmetic is checked 64-bit with `/` truncating toward zero, and an overflow or division by zero throws `CelFunctionException` named `_+_`, `_-_`, `_*_`, `_/_` or `-_` with a spec §5.5 reason. `internal static class CelHookArithmetic` with `Apply(CelBinaryOperator, object?, object?)` and `Negate(object?)`.

Why (spec E6, §5.6, D-7): today's arithmetic answers `null` on overflow and division by zero (`CelInterpreter.cs:607-622`, "a generated column must never make a write crash"). In a before-hook condition that `null` reads as `false` and turns a `reject` off — the fail-open C1 §17 closed for functions. One flag makes the hook profiles fail closed; Computed keeps its `null`.

- [ ] **Step 1: Write the failing tests** (`CelHookArithmeticTests.cs`):

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Arithmetic in a hook condition and a mutate value (spec §5.6, D-7): CEL's semantics — Int stays Int, '/' truncates
/// toward zero, an overflow or a division by zero is an error — and the error fails closed like a function's. A
/// computed field keeps answering null for the same inputs.
/// </summary>
public sealed class CelHookArithmeticTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    private static bool Condition(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluatePredicate(TestCelFunctions.Compile(source, CelProfile.Condition), CelFixtures.Row(row), previous: null, CelFixtures.Anonymous);

    private static object? Computed(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateScalar(TestCelFunctions.Compile(source, CelProfile.Computed), CelFixtures.Row(row));

    [Theory]
    [InlineData("qty * 2", 7L, 14L)]
    [InlineData("qty / 2", 7L, 3L)]
    [InlineData("qty / 2", -7L, -3L)]
    [InlineData("qty - 10", 7L, -3L)]
    [InlineData("-qty", 7L, -7L)]
    public void Int_arithmetic_stays_int_and_division_truncates_toward_zero(string source, long qty, long expected) =>
        Mutate(source, ("qty", qty)).ShouldBe(expected);

    [Fact]
    public void An_int_and_a_decimal_make_a_decimal() => Mutate("qty + 0.5", ("qty", 1L)).ShouldBe(1.5m);

    [Fact]
    public void Money_to_cents_needs_no_more_than_this() =>
        Mutate("math.round(price * 1.2, 2)", ("price", 10.25m)).ShouldBe(12.30m);

    [Fact]
    public void A_null_operand_makes_the_result_null() => Mutate("qty * 2", ("qty", null)).ShouldBeNull();

    [Theory]
    [InlineData("qty + 9223372036854775807", 1L, "_+_", "the result is outside the range of an Int")]
    [InlineData("qty - 9223372036854775807", -2L, "_-_", "the result is outside the range of an Int")]
    [InlineData("qty * 9223372036854775807", 2L, "_*_", "the result is outside the range of an Int")]
    [InlineData("qty / 0", 7L, "_/_", "the divisor is zero")]
    [InlineData("qty / -1", long.MinValue, "_/_", "the result is outside the range of an Int")]
    [InlineData("-qty", long.MinValue, "-_", "the result is outside the range of an Int")]
    public void An_int_failure_in_a_mutate_fails_closed(string source, long qty, string name, string reason)
    {
        var failure = Should.Throw<CelFunctionException>(() => Mutate(source, ("qty", qty)));

        failure.FunctionName.ShouldBe(name);
        failure.Reason.ShouldBe(reason);
    }

    [Theory]
    [InlineData("price * price", "_*_", "the result is outside the range of a Decimal")]
    [InlineData("price / 0", "_/_", "the divisor is zero")]
    [InlineData("price / 0.0", "_/_", "the divisor is zero")]
    public void A_decimal_failure_in_a_mutate_fails_closed(string source, string name, string reason)
    {
        var failure = Should.Throw<CelFunctionException>(() => Mutate(source, ("price", decimal.MaxValue)));

        failure.FunctionName.ShouldBe(name);
        failure.Reason.ShouldBe(reason);
    }

    /// <summary>The fail-open this task closes: a reject whose condition divides by zero refuses, never skips.</summary>
    [Fact]
    public void A_division_by_zero_in_a_condition_fails_closed_rather_than_reading_false() =>
        Should.Throw<CelFunctionException>(() => Condition("new.price / new.qty > 1", ("price", 5m), ("qty", 0L)))
            .FunctionName.ShouldBe("_/_");

    [Fact]
    public void A_condition_compares_arithmetic() => Condition("new.qty * 2 > 10", ("qty", 6L)).ShouldBeTrue();

    [Fact]
    public void A_null_operand_in_a_condition_does_not_fire() => Condition("new.qty * 2 > 10", ("qty", null)).ShouldBeFalse();

    /// <summary>Gated on the profile: a generated column still never makes a write crash (spec §5.6, D-7).</summary>
    [Theory]
    [InlineData("price / qty", 5, 0L)]
    [InlineData("qty + 9223372036854775807", 0, 1L)]
    public void A_computed_field_still_answers_null_rather_than_failing(string source, int price, long qty) =>
        Should.NotThrow(() => Computed(source, ("price", (decimal)price), ("qty", qty)));

    [Fact]
    public void A_computed_division_by_zero_is_still_null() => Computed("price / qty", ("price", 5m), ("qty", 0L)).ShouldBeNull();

    [Theory]
    [InlineData("new.qty + 1 > 1", CelProfile.Condition)]
    [InlineData("-new.qty", CelProfile.Mutate)]
    [InlineData("new.price * new.qty", CelProfile.Mutate)]
    public void Arithmetic_compiles_in_both_hook_profiles(string source, CelProfile profile) =>
        TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("qty + 1 > 1", CelProfile.Rule, "Arithmetic is legal only in the Computed, Condition and Mutate profiles; '+' is not allowed here.")]
    [InlineData("-qty > 1", CelProfile.Rule, "Arithmetic negation ('-') is legal only in the Computed, Condition and Mutate profiles.")]
    public void Arithmetic_elsewhere_names_the_profiles_that_admit_it(string source, CelProfile profile, string message) =>
        TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items).Errors.ShouldContain(error => error.Message == message);
}
```

`CelFixtures.Anonymous` is the context Task 3's facts use. If `Computed` refuses `price / qty` for a nullable operand or an operand-shape rule, take the computed sources from `CelProfileTests`' Computed facts over `CelFixtures.Orders` instead — what matters is that the same overflow and zero divisor answer `null` there.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelHookArithmeticTests'`
Expected: FAIL — `qty * 2` is refused in Mutate ("Arithmetic is legal only in the Computed profile"), and the Rule messages still say "only in the Computed profile".

- [ ] **Step 3: The arithmetic** (`CelHookArithmetic.cs`):

```csharp
using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Arithmetic as a hook condition or a mutate value evaluates it (spec §5.6, D-7): CEL's semantics — Int stays Int and
/// '/' truncates toward zero; an overflow or a division by zero is an error — and the error fails closed as a function
/// failure does (rollback, <c>function-failed</c>), never as <see langword="null"/>. Computed never reaches this class:
/// a generated column must never make a write crash.
/// </summary>
internal static class CelHookArithmetic
{
    private const string IntRange = "the result is outside the range of an Int";
    private const string DecimalRange = "the result is outside the range of a Decimal";
    private const string ZeroDivisor = "the divisor is zero";

    /// <summary>
    /// Applies <c>+ - * /</c>. Two integral operands take the checked 64-bit path; any other two numbers the
    /// <see cref="decimal"/> path; a null or non-numeric operand answers <see langword="null"/>, as everywhere else.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns>A <see cref="long"/>, a <see cref="decimal"/>, or <see langword="null"/>.</returns>
    /// <exception cref="CelFunctionException">The result overflows, or the divisor is zero.</exception>
    internal static object? Apply(CelBinaryOperator op, object? left, object? right)
    {
        if (TryInt(left, out var leftInt) && TryInt(right, out var rightInt))
        {
            return ApplyInt(op, leftInt, rightInt);
        }

        return left is not null && right is not null
            && CelInterpreter.TryToDecimal(left, out var leftDecimal) && CelInterpreter.TryToDecimal(right, out var rightDecimal)
                ? ApplyDecimal(op, leftDecimal, rightDecimal)
                : null;
    }

    /// <summary>Unary minus. The smallest Int has no negation; a decimal always has one.</summary>
    /// <param name="value">The operand.</param>
    /// <returns>The negated value, or <see langword="null"/>.</returns>
    /// <exception cref="CelFunctionException">The operand is the smallest Int.</exception>
    internal static object? Negate(object? value)
    {
        if (TryInt(value, out var number))
        {
            return number == long.MinValue ? throw new CelFunctionException("-_", IntRange) : -number;
        }

        return value is not null && CelInterpreter.TryToDecimal(value, out var amount) ? -amount : null;
    }

    private static long ApplyInt(CelBinaryOperator op, long left, long right)
    {
        try
        {
            return op switch
            {
                CelBinaryOperator.Add => checked(left + right),
                CelBinaryOperator.Subtract => checked(left - right),
                CelBinaryOperator.Multiply => checked(left * right),
                CelBinaryOperator.Divide => right == 0 ? throw Failure(op, ZeroDivisor) : checked(left / right),
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an arithmetic operator."),
            };
        }
        catch (OverflowException)
        {
            throw Failure(op, IntRange);
        }
    }

    private static decimal ApplyDecimal(CelBinaryOperator op, decimal left, decimal right)
    {
        try
        {
            return op switch
            {
                CelBinaryOperator.Add => left + right,
                CelBinaryOperator.Subtract => left - right,
                CelBinaryOperator.Multiply => left * right,
                CelBinaryOperator.Divide => right == 0m ? throw Failure(op, ZeroDivisor) : left / right,
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an arithmetic operator."),
            };
        }
        catch (OverflowException)
        {
            throw Failure(op, DecimalRange);
        }
    }

    /// <summary>An integral CLR value as an Int (a record's Integer column, an Int literal); anything else is not one.</summary>
    private static bool TryInt(object? value, out long number)
    {
        switch (value)
        {
            case long whole:
                number = whole;
                return true;
            case int or short or byte or sbyte or ushort or uint:
                number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            default:
                number = 0;
                return false;
        }
    }

    /// <summary>The failure, named by CEL's own overload name for the operator (<c>_/_</c>), as spec §5.5 words it.</summary>
    private static CelFunctionException Failure(CelBinaryOperator op, string reason) => new(op switch
    {
        CelBinaryOperator.Add => "_+_",
        CelBinaryOperator.Subtract => "_-_",
        CelBinaryOperator.Multiply => "_*_",
        _ => "_/_",
    }, reason);
}
```

(`long.MinValue / -1` raises `OverflowException` on .NET, which the catch turns into the Int-range reason; the fact pins it.)

- [ ] **Step 4: The flag** — in `CelInterpreter.cs`:
  - `EvalState` gains a last constructor parameter `bool failClosed = false` and a property `public bool FailClosed { get; } = failClosed;` with the XML doc "Whether arithmetic fails closed (a hook condition or mutate value, spec §5.6) rather than answering null (a computed column)".
  - A helper `private static bool FailsClosed(CelProfile profile) => profile is CelProfile.Condition or CelProfile.Mutate;` — the one place the profile is read.
  - `EvaluatePredicate`: `new EvalState(current, previous, context, failClosed: FailsClosed(expression.Profile))`. `EvaluateMutation`: `new EvalState(current, previous, null, now, FailsClosed(expression.Profile))`. `EvaluateScalar` and `EvaluateMask` are unchanged (no flag).
  - `EvaluateUnary`: `CelUnaryOperator.Negate => state.FailClosed ? CelHookArithmetic.Negate(Evaluate(unary.Operand, state)) : Negate(Evaluate(unary.Operand, state)),`.
  - `EvaluateBinary`: pass `state.FailClosed` to `EvaluateAdd` and to `EvaluateArithmetic`; `EvaluateAdd(object? left, object? right, bool failClosed)` keeps its string arm and calls `EvaluateArithmetic(CelBinaryOperator.Add, left, right, failClosed)`.
  - Rename today's `EvaluateArithmetic` to `ComputedArithmetic` (body unchanged) and add `private static object? EvaluateArithmetic(CelBinaryOperator op, object? left, object? right, bool failClosed) => failClosed ? CelHookArithmetic.Apply(op, left, right) : ComputedArithmetic(op, left, right);`.
  - `EvaluateScalar`'s summary keeps "arithmetic … yield null"; add "— only here: a hook condition or mutate value fails closed instead (spec §5.6)". `EvaluateMutation`'s remarks: replace "Apart from a catalogued function, nothing in a Mutate tree can throw … `lowerAscii` of a non-string is null" with "Apart from a catalogued function and an operator's overflow or division by zero (spec §5.6), nothing in a Mutate tree can throw". The class remarks' paragraph on `CelFunctionException` gains "— or an operator's, in a hook profile".

- [ ] **Step 5: The profile table** — in `CelTypeChecker.cs`:

```csharp
    /// <summary>
    /// Arithmetic's profiles: a computed column (which renders it to SQL and answers null on failure) and the two hook
    /// slots (interpreter-only, where an overflow or a zero divisor fails closed — spec §5.6, D-7).
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _computedConditionAndMutate =
        new HashSet<CelProfile> { CelProfile.Computed, CelProfile.Condition, CelProfile.Mutate };
```

`[CelConstructKind.Arithmetic] = _computedConditionAndMutate,`. `CheckNegate`'s message becomes `"Arithmetic negation ('-') is legal only in the Computed, Condition and Mutate profiles."`; `CheckArithmetic`'s becomes `$"Arithmetic is legal only in the Computed, Condition and Mutate profiles; '{OperatorText(binary.Operator)}' is not allowed here."`. Fixes unchanged (they are only shown in Rule and Access now). Rewrite `_allowedProfiles`' summary: Mutate holds literals, field references, the legacy call, catalogued calls and arithmetic; Condition also arithmetic; concatenation joins Mutate in Task 8.

- [ ] **Step 6: Re-judge the corpus rows that carry an arithmetic gate message, and only those** — the same temporary-fact procedure as Task 2 Step 5, with this predicate (an old row's errors, not its source, decide):

```csharp
    [Fact]
    public void Regenerate_arithmetic_rows()
    {
        string[] gates = ["Arithmetic is legal only in the Computed profile", "Arithmetic negation ('-') is legal only in the Computed profile"];
        var lines = Baseline().Select(expected => expected.Errors.Any(error => gates.Any(gate => error.Message.StartsWith(gate, StringComparison.Ordinal)))
            ? Judge(_compiler, expected.Source, Enum.Parse<CelProfile>(expected.Profile))
            : expected);
        File.WriteAllLines(
            Path.Combine(Path.GetTempPath(), "arithmetic-baseline.jsonl"),
            lines.Select(outcome => JsonSerializer.Serialize(outcome)));
    }
```

(`Errors`/`Message` are the baseline row's own members — the JSONL's shape; use their real names.) Diff as in Task 2: **every differing line must be one whose old errors carried a gate message** — 269 candidate rows at C1 HEAD (Rule 67, Condition 67, Mutate 67, Access 68); Rule and Access rows change only their message text, Condition and Mutate rows lose the error and some become accepted (16 Mutate rows such as `total * 2`, `-total`, `1 + total`). Any other differing line is a defect: stop and find it. Copy the file over the baseline keeping its line endings, delete the temporary fact, and paste the moved rows (`Profile: Source — old first message → new first message, or "accepted"`) into the commit body.

- [ ] **Step 7: Facts and examples that pinned the old gate** — run `dotnet test --project test/MMLib.Alvo.Tests` and `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillCoreClaimsTests' --filter-class '*SkillConformanceTests'`. A fact that asserted arithmetic refused **in Condition or Mutate** is inverted to acceptance with "spec D-7" in its summary; a fact about Rule, Access or Computed stays (`CelProfileTests.Arithmetic_is_computed_only` asserts Rule refusal and Computed acceptance — rename it `Arithmetic_is_refused_in_a_rule` and keep both halves; `CelTypeCheckerErrorShapeTests.Arithmetic_refused_outside_computed_names_the_operator` reads Rule and stays green). A skill example listed as *refused* that is now admitted moves to *allowed*.

- [ ] **Step 8: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelHookArithmeticTests' --filter-class '*CelAcceptanceCorpusTests' --filter-class '*CelProfileTests' --filter-class '*CelTypeChecker*' --filter-class '*CelInterpreter*' --filter-class '*SqlPredicateRenderer*'`, then `scripts/test-ring0` and `scripts/test-ring1`.
Expected: PASS. `SqlPredicateRenderer*` and every differential fact are unchanged: Computed's semantics did not move.

- [ ] **Step 9: Commit**

```bash
git add src/MMLib.Alvo/Expressions/Internal/CelHookArithmetic.cs src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs test/MMLib.Alvo.Tests/Expressions/CelHookArithmeticTests.cs test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl <every inverted test file, by name> <the skill, if it moved>
git commit -m "feat(cel): arithmetic in hook conditions and mutate values fails closed

Int stays Int and '/' truncates toward zero (CEL); an overflow or a zero
divisor throws the function failure, so a reject can no longer be
switched off by a null. Computed still answers null. Corpus rows moved
(only rows carrying an arithmetic gate message; old first message -> new):
<paste the list>

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 8: string `+` in mutate values

**Files:**
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` (`_allowedProfiles[Concatenation]`, a new `_computedAndMutate` set, `CheckConcatenation` `:523`, `RequireTwoStrings` `:538`)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs` (`EvaluateAdd`'s summary only)
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl` (rows carrying the concatenation gate message only)
- Modify: `test/MMLib.Alvo.Tests/Expressions/CelStringConcatenationTests.cs` (`Concatenation_outside_the_computed_profile_is_refused` keeps Rule and Condition; its summary says Mutate admits it now)
- Test: `test/MMLib.Alvo.Tests/Expressions/CelMutateConcatenationTests.cs`

**Interfaces:**
- Consumes: Task 7's `EvalState.FailClosed` path (`EvaluateAdd` → `CelHookArithmetic.Apply` answers `null` for a string and a null); `_sqlRenderedProfiles` (`CelTypeChecker.cs:173`); `SqlPredicateRenderer.IsStringJoin` (from `main`, `bc8e9f2`) — unchanged, Computed only.
- Produces: string `+` legal in Mutate, null in → null out (spec §5.6, F18, D-6); in Condition and Mutate the mixed-type fix names `string(x)`; Computed's nullable-operand refusal unchanged.

Why: prefixing a phone (`'+421' + new.phone`) and building a code (`'FR-' + upperAscii(new.code)`) have no fail-open path; a null operand gives a null value (C1 F3's rule for every function, and what SQL's `||` answers — the rendering C2 would use), and Ruling V refuses an overlong stored value with 403.

- [ ] **Step 1: Write the failing tests** (`CelMutateConcatenationTests.cs`):

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// String '+' in a mutate value (spec §5.6, F18, D-6): it joins two strings, a null operand makes the value null, and a
/// number is joined only through string(). A computed field keeps refusing a nullable operand.
/// </summary>
public sealed class CelMutateConcatenationTests
{
    private static readonly DateTimeOffset _now = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static object? Mutate(string source, params (string Field, object? Value)[] row) =>
        CelInterpreter.EvaluateMutation(TestCelFunctions.Compile(source, CelProfile.Mutate), CelFixtures.Row(row), previous: null, _now);

    private static CelCompilationResult Compile(string source, CelProfile profile) =>
        TestCelFunctions.Compiler().Compile(source, profile, TestCelFunctions.Items);

    [Theory]
    [InlineData("'+421' + name", "905100200", "+421905100200")]
    [InlineData("name + '-' + name", "ab", "ab-ab")]
    [InlineData("'FR-' + upperAscii(trim(name))", "  wtu1 ", "FR-WTU1")]
    [InlineData("'#' + string(qty)", null, null)]
    public void A_mutate_joins_strings(string source, string? name, string? expected) =>
        Mutate(source, ("name", name), ("qty", null)).ShouldBe(expected);

    [Fact]
    public void A_number_joins_through_string() => Mutate("'#' + string(qty)", ("qty", 7L)).ShouldBe("#7");

    [Fact]
    public void A_null_operand_makes_the_value_null() => Mutate("'+421' + name", ("name", null)).ShouldBeNull();

    [Fact]
    public void A_nullable_field_is_admitted_in_a_mutate() => Compile("name + 'x'", CelProfile.Mutate).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_number_without_string_is_refused_with_string_as_the_fix()
    {
        var error = Compile("'#' + qty", CelProfile.Mutate).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'+' joins two strings or adds two numbers; found String and Int, and CEL converts neither implicitly.");
        error.FixSuggestion.ShouldBe("Write string(x) to join a number, a flag, an id or an instant.");
    }

    [Fact]
    public void Concatenation_stays_refused_in_a_condition() =>
        Compile("name + 'x' == 'ax'", CelProfile.Condition).Errors
            .ShouldContain(error => error.Message == "String concatenation ('+' over two strings) is legal only in the Computed and Mutate profiles.");
}
```

(`CelCompilationResult` is whatever `CelCompiler.Compile` returns — use its real name. `CelStringConcatenationTests.A_nullable_operand_is_refused_with_the_explicit_fallback_as_the_fix` is the Computed half and must stay green unchanged.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelMutateConcatenationTests'`
Expected: FAIL — "String concatenation ('+' over two strings) is legal only in the Computed profile."

- [ ] **Step 3: Implement** — in `CelTypeChecker.cs`:

```csharp
    /// <summary>
    /// Concatenation's profiles: a computed column (SQL's <c>||</c>, a nullable operand refused at compile) and a mutate
    /// value (interpreter-only, a null operand makes the value null — spec §5.6, F18).
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _computedAndMutate =
        new HashSet<CelProfile> { CelProfile.Computed, CelProfile.Mutate };
```

`[CelConstructKind.Concatenation] = _computedAndMutate,`. In `CheckConcatenation`, the gate message becomes `"String concatenation ('+' over two strings) is legal only in the Computed and Mutate profiles."` (fix unchanged), and the null rule runs only where SQL renders the join:

```csharp
            var nullBad = !profileBad && !mismatch && _sqlRenderedProfiles.Contains(profile)
                && (RequireNeverNull(binary.Left, leftError, leftPosition) | RequireNeverNull(binary.Right, rightError, rightPosition));
```

`RequireTwoStrings` takes its fix from a new member:

```csharp
        /// <summary>
        /// The fix for a string joined with a non-string: the hook profiles have <c>string()</c> (spec §5.3); a computed
        /// field does not, so it keeps its own advice.
        /// </summary>
        private string JoinConversionFix => profile is CelProfile.Condition or CelProfile.Mutate
            ? "Write string(x) to join a number, a flag, an id or an instant."
            : "Join two string fields or string constants (first_name + ' ' + last_name). A computed field has no "
                + "string() conversion, so keep the number in a field of its own.";
```

Update `CheckConcatenation`'s remarks: "The null rule is a refusal" applies to the SQL-rendered profiles; in Mutate a null operand makes the value null (F18). In `CelInterpreter.EvaluateAdd`'s summary, replace "unreachable for a concatenation the compiler admitted, since it refuses an operand that can be null" with "reachable in a mutate value, where it is the answer (spec F18); in a computed field the compiler refuses an operand that can be null". The code of `EvaluateAdd` does not change: a string and a null fall to `EvaluateArithmetic`, which answers `null` on both paths.

- [ ] **Step 4: Re-judge the corpus rows that carry the concatenation gate message, and only those** — Task 7 Step 6's procedure with `gates = ["String concatenation ('+' over two strings) is legal only in the Computed profile"]`. 51 candidate rows at C1 HEAD (Rule 13, Condition 13, Mutate 13, Access 12); 48 also carried an arithmetic message and were re-judged by Task 7 — they move again here only in this message. Rule, Condition and Access rows change their message text; Mutate rows lose it (most stay refused: the corpus entity `orders` lacks `first_name`, `middle_name`, `last_name`). **Every differing line must be one whose current errors carry the gate message**; anything else is a defect. Paste the moved rows into the commit body.

- [ ] **Step 5: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelMutateConcatenationTests' --filter-class '*CelStringConcatenationTests' --filter-class '*SqlPredicateRendererConcatenationTests' --filter-class '*CelAcceptanceCorpusTests' --filter-class '*CelHookArithmeticTests'`, `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*SkillConformanceTests'`, then `scripts/test-ring0` and `scripts/test-ring1`.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs test/MMLib.Alvo.Tests/Expressions/CelMutateConcatenationTests.cs test/MMLib.Alvo.Tests/Expressions/CelStringConcatenationTests.cs test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl
git commit -m "feat(cel): join strings with + in a mutate value

A null operand makes the value null, as every function's argument does
and as SQL's || answers; a computed field still refuses a nullable
operand. Corpus rows moved (only rows carrying the concatenation gate
message; old first message -> new):
<paste the list>

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 9: the new built-ins and the hook operators over HTTP

**Files:**
- Create: `test/MMLib.Alvo.Api.Tests/descriptors/built-in-functions.alvo.json`
- Test: `test/MMLib.Alvo.Api.Tests/CelBuiltInWriteTests.cs`

**Interfaces:**
- Consumes: `AlvoApiWorld.FromDescriptorAsync(file, keys, setup)`, `TestApiKey`, `SendAsync`, `ReadJsonObjectAsync` (`test/_shared/api`, used by C1's `CelFunctionWriteTests`).
- Produces: proof that the Data API's write path evaluates the built-ins and the operators in conditions and mutate values, refuses with 403 on a `reject`, and answers 500 `function-failed` — writing nothing — for a data-dependent failure of a function or an operator.

- [ ] **Step 1: The descriptor** (`built-in-functions.alvo.json`, LF, no BOM) — one entity `frames` with `code` (string, maxLength 32), `label` (string, maxLength 12), `tag` (string, maxLength 40), `owner_email` (string, maxLength 160, format email), `price` and `gross` (decimal, precision 10, scale 2), `ratio` and `divisor` (integer); every field optional; rules all `true` for a `writer` role as in `cel-functions.alvo.json`; hooks:

```json
"hooks": {
  "beforeCreate": [
    { "condition": "endsWith(lowerAscii(new.owner_email), '@example.com')",
      "action": { "reject": "Use the owner's real email address." } },
    { "condition": "new.ratio / new.divisor > 10",
      "action": { "reject": "The ratio is too high." } },
    { "action": { "mutate": {
        "code": { "$cel": "upperAscii(replace(trim(new.code), ' ', ''))" },
        "label": { "$cel": "upperAscii(substring(replace(trim(new.code), ' ', ''), 0, math.least(size(replace(trim(new.code), ' ', '')), 12)))" },
        "tag": { "$cel": "'FR-' + upperAscii(trim(new.code))" },
        "gross": { "$cel": "math.round(new.price * 1.2, 2)" } } } }
  ],
  "beforeUpdate": [
    { "action": { "mutate": { "label": { "$cel": "substring(new.code, 20)" } } } }
  ]
}
```

- [ ] **Step 2: Write the failing tests** (`CelBuiltInWriteTests.cs`):

```csharp
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>The built-ins of spec §5, evaluated by the Data API's own write path.</summary>
public sealed class CelBuiltInWriteTests
{
    private static TestApiKey Writer { get; } = new("frames-writer", ["writer"], ["frames:read", "frames:write"]);

    private static Task<AlvoApiWorld> StartAsync() =>
        AlvoApiWorld.FromDescriptorAsync("built-in-functions.alvo.json", [Writer], new AlvoApiWorldSetup(MapAlvoProblemDetails: true));

    [Fact]
    public async Task A_mutate_normalises_and_cuts_the_written_text()
    {
        await using var world = await StartAsync();

        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["code"] = "  wtu 123 456 789x  ", ["owner_email"] = "jana@kros.sk" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var row = await created.ReadJsonObjectAsync();
        row["code"]!.GetValue<string>().ShouldBe("WTU123456789X");
        row["label"]!.GetValue<string>().ShouldBe("WTU123456789", "cut to the label's 12 characters");
    }

    [Fact]
    public async Task A_mutate_rounds_money_to_cents_and_prefixes_a_code()
    {
        await using var world = await StartAsync();

        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["code"] = " wtu 1 ", ["price"] = 10.25m });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var row = await created.ReadJsonObjectAsync();
        row["gross"]!.GetValue<decimal>().ShouldBe(12.30m, "10.25 * 1.2 = 12.300, rounded to cents");
        row["tag"]!.GetValue<string>().ShouldBe("FR-WTU 1");
    }

    [Fact]
    public async Task A_null_operand_leaves_the_value_null_and_the_condition_quiet()
    {
        await using var world = await StartAsync();

        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer, body: new JsonObject { ["ratio"] = 50 });

        created.StatusCode.ShouldBe(HttpStatusCode.Created, "50 / null is null, and a condition reading null does not fire");
        var row = await created.ReadJsonObjectAsync();
        row["gross"].ShouldBeNull("null price, null gross");
        row["tag"].ShouldBeNull("null code, null tag");
    }

    /// <summary>The fail-open spec D-7 closes: a zero divisor in a reject condition refuses the write, never skips the reject.</summary>
    [Fact]
    public async Task A_division_by_zero_in_a_condition_fails_the_write_closed()
    {
        await using var world = await StartAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["code"] = "DIV0", ["ratio"] = 50, ["divisor"] = 0 });

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldEndWith("/errors/function-failed");
        problem["detail"]!.GetValue<string>().ShouldBe("The CEL function '_/_' failed: the divisor is zero. Nothing was written.");

        using var listed = await world.SendAsync(HttpMethod.Get, "/api/frames", Writer);
        (await listed.Content.ReadAsStringAsync()).ShouldNotContain("DIV0", Case.Sensitive, "nothing was written");
    }

    [Fact]
    public async Task A_condition_with_a_text_test_refuses_the_write()
    {
        await using var world = await StartAsync();

        using var refused = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer,
            body: new JsonObject { ["code"] = "A1", ["owner_email"] = "Someone@EXAMPLE.com" });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("Use the owner's real email address.");
    }

    [Fact]
    public async Task A_substring_past_the_end_of_the_data_fails_the_write_closed()
    {
        await using var world = await StartAsync();
        using var created = await world.SendAsync(HttpMethod.Post, "/api/frames", Writer, body: new JsonObject { ["code"] = "A1" });
        var id = (await created.ReadJsonObjectAsync())["id"]!.ToString();

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/frames/{id}", Writer, body: new JsonObject { ["code"] = "B2" });

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var problem = await refused.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldEndWith("/errors/function-failed");
        problem["detail"]!.GetValue<string>().ShouldContain("substring");
        problem["detail"]!.GetValue<string>().ShouldNotContain("B2", Case.Sensitive, "never the caller's data");
    }
}
```

(If the PATCH route or method name differs, copy it from `CelFunctionWriteTests.A_failing_function_in_an_update_reject_condition_refuses_the_update_and_keeps_the_row`.)

- [ ] **Step 3: Run**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*CelBuiltInWriteTests'`
Expected: PASS on the first run (the behaviour landed in Tasks 2–8) — this task is the HTTP proof. If a fact fails, the defect is in Tasks 2–8: fix it there, in this task's commit, and say so. If `null` fields are omitted from the answer rather than written as `null`, `row["gross"].ShouldBeNull()` still holds (a missing key reads as `null`).

- [ ] **Step 4: Commit**

```bash
git add test/MMLib.Alvo.Api.Tests/descriptors/built-in-functions.alvo.json test/MMLib.Alvo.Api.Tests/CelBuiltInWriteTests.cs
git commit -m "test(api): the new built-ins and hook operators shape, refuse and fail writes over HTTP

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 10: the dashboard reads the catalog — `CelFunctionsAsync` and `FunctionOffer`

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs` (slot, `CelFunctionsAsync`, `Invalidate`)
- Create: `src/MMLib.Alvo.Admin/Components/Schema/FunctionOffer.cs`
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/FunctionOfferTests.cs`, `test/MMLib.Alvo.Host.Tests/FunctionOfferAgreementTests.cs`

**Interfaces:**
- Consumes: `IAlvoManagement.GetCelFunctionsAsync` → `ManagementCelFunctions.Functions` (C1, Abstractions); `CelFunctionInfo`, `CelFunctionParameter`, `CelFunctionProvenance`, `CelFieldType.Of` (Abstractions); `ConditionTable.HasNew(string)` (from B Task 1).
- Produces (all internal):
  - `ManagementGateway.CelFunctionsAsync(CancellationToken) → ValueTask<IReadOnlyList<CelFunctionInfo>>`
  - `sealed record OfferedFunction(string Name, IReadOnlyList<string> Signatures, string Summary, bool IsHost, IReadOnlyList<CelFunctionParameter> Parameters)`
  - `sealed record Insertion(string Text, int SelectFrom, int SelectLength)`
  - `static class FunctionOffer` — `For(IReadOnlyList<CelFunctionInfo>, CelProfile)`, `Signature(CelFunctionInfo)`, `Template(OfferedFunction, string? firstArgument)`, `FirstArgument(OfferedFunction, string point, FieldSchema? field)`, `Insert(string text, int start, int end, Insertion template)`.

- [ ] **Step 1: Write the failing unit tests** (`FunctionOfferTests.cs`):

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What the hook editor offers under a CEL box, and what Insert writes (spec §9).</summary>
public class FunctionOfferTests
{
    private static CelFunctionInfo Info(string name, CelValueType result, CelFunctionProvenance provenance, CelProfile[] profiles, params (string Name, CelValueType Type)[] parameters) => new()
    {
        Name = name,
        Parameters = [.. parameters.Select(p => new CelFunctionParameter { Name = p.Name, Type = p.Type, AcceptsNull = false })],
        Result = result,
        ResultMayBeNull = false,
        Summary = $"{name} does a thing.",
        Provenance = provenance,
        Profiles = profiles,
    };

    private static readonly CelProfile[] _both = [CelProfile.Condition, CelProfile.Mutate];

    private static readonly IReadOnlyList<CelFunctionInfo> _catalog =
    [
        Info("math.round", CelValueType.Int, CelFunctionProvenance.BuiltIn, _both, ("x", CelValueType.Int)),
        Info("math.round", CelValueType.Decimal, CelFunctionProvenance.BuiltIn, _both, ("x", CelValueType.Decimal)),
        Info("now", CelValueType.Timestamp, CelFunctionProvenance.BuiltIn, [CelProfile.Mutate]),
        Info("normalizeFrameNumber", CelValueType.String, CelFunctionProvenance.Host, _both, ("value", CelValueType.String)),
        Info("substring", CelValueType.String, CelFunctionProvenance.BuiltIn, _both, ("text", CelValueType.String), ("start", CelValueType.Int)),
        Info("substring", CelValueType.String, CelFunctionProvenance.BuiltIn, _both, ("text", CelValueType.String), ("start", CelValueType.Int), ("end", CelValueType.Int)),
    ];

    [Fact]
    public void A_profile_is_offered_its_functions_one_row_per_name_in_name_order()
    {
        FunctionOffer.For(_catalog, CelProfile.Mutate).Select(f => f.Name).ShouldBe(["math.round", "normalizeFrameNumber", "now", "substring"]);
        FunctionOffer.For(_catalog, CelProfile.Condition).Select(f => f.Name).ShouldBe(["math.round", "normalizeFrameNumber", "substring"]);
    }

    [Fact]
    public void A_row_lists_every_overload_and_says_whether_the_host_wrote_it()
    {
        var round = FunctionOffer.For(_catalog, CelProfile.Mutate)[0];
        round.Signatures.ShouldBe(["math.round(x: Int) -> Int", "math.round(x: Decimal) -> Decimal"]);
        round.IsHost.ShouldBeFalse();
        FunctionOffer.For(_catalog, CelProfile.Mutate)[1].IsHost.ShouldBeTrue();
    }

    [Fact]
    public void The_template_has_every_parameter_of_the_longest_overload_and_selects_the_first()
    {
        var substring = FunctionOffer.For(_catalog, CelProfile.Mutate)[3];

        FunctionOffer.Template(substring, firstArgument: null).ShouldBe(new Insertion("substring(text, start, end)", 10, 4));
    }

    [Fact]
    public void A_first_argument_fills_the_first_place_and_the_next_placeholder_is_selected()
    {
        var substring = FunctionOffer.For(_catalog, CelProfile.Mutate)[3];

        FunctionOffer.Template(substring, "new.title").ShouldBe(new Insertion("substring(new.title, start, end)", 21, 5));
    }

    [Fact]
    public void A_complete_call_leaves_the_caret_after_it()
    {
        var normalize = FunctionOffer.For(_catalog, CelProfile.Mutate)[1];
        var now = FunctionOffer.For(_catalog, CelProfile.Mutate)[2];

        FunctionOffer.Template(normalize, "new.frame_number").ShouldBe(new Insertion("normalizeFrameNumber(new.frame_number)", 38, 0));
        FunctionOffer.Template(now, null).ShouldBe(new Insertion("now()", 5, 0));
    }

    [Theory]
    [InlineData(FieldType.String, "beforeCreate", "new.code")]
    [InlineData(FieldType.Integer, "beforeUpdate", null)]
    [InlineData(FieldType.String, "beforeDelete", null)]
    public void The_first_argument_is_the_row_s_field_only_when_its_type_fits_and_the_point_has_new(FieldType type, string point, string? expected)
    {
        var normalize = FunctionOffer.For(_catalog, CelProfile.Mutate)[1];

        FunctionOffer.FirstArgument(normalize, point, new FieldSchema { Name = "code", Type = type }).ShouldBe(expected);
    }

    [Fact]
    public void An_int_field_fills_a_decimal_parameter()
    {
        var round = FunctionOffer.For(_catalog, CelProfile.Mutate)[0] with
        {
            Parameters = [new CelFunctionParameter { Name = "x", Type = CelValueType.Decimal, AcceptsNull = false }],
        };

        FunctionOffer.FirstArgument(round, "beforeCreate", new FieldSchema { Name = "qty", Type = FieldType.Integer }).ShouldBe("new.qty");
    }

    [Theory]
    [InlineData("", 0, 0, "trim(text)", 5)]
    [InlineData(" > 3", 0, 0, "trim(text) > 3", 5)]
    [InlineData("x == ", 5, 5, "x == trim(text)", 10)]
    [InlineData("čaj OLD", 4, 7, "čaj trim(text)", 9)]
    [InlineData("abc", 9, 2, "abctrim(text)", 8)]
    public void Insert_replaces_the_selection_and_moves_the_placeholder_with_it(string text, int start, int end, string expected, int selectFrom)
    {
        var inserted = FunctionOffer.Insert(text, start, end, new Insertion("trim(text)", 5, 4));

        inserted.Text.ShouldBe(expected);
        inserted.SelectFrom.ShouldBe(selectFrom);
        inserted.SelectLength.ShouldBe(4);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*FunctionOfferTests'`
Expected: build error — `FunctionOffer` does not exist.

- [ ] **Step 3: Implement** `FunctionOffer.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One function as the hook editor offers it: every overload's signature, its summary, and who wrote it.</summary>
/// <param name="Name">The function's CEL spelling, e.g. <c>math.round</c>.</param>
/// <param name="Signatures">Every overload, as the core's refusals quote it.</param>
/// <param name="Summary">The one-sentence summary — host-authored text for a host function, rendered as text.</param>
/// <param name="IsHost">Whether the embedding host registered it.</param>
/// <param name="Parameters">The parameters of the overload with the most, which the insert template spells.</param>
internal sealed record OfferedFunction(
    string Name, IReadOnlyList<string> Signatures, string Summary, bool IsHost, IReadOnlyList<CelFunctionParameter> Parameters);

/// <summary>A text to write and the range in it to select next.</summary>
/// <param name="Text">The text.</param>
/// <param name="SelectFrom">Where the selection starts, in UTF-16 code units (the browser's unit too).</param>
/// <param name="SelectLength">How long it is; 0 puts the caret at <paramref name="SelectFrom"/>.</param>
internal sealed record Insertion(string Text, int SelectFrom, int SelectLength);

/// <summary>
/// What the hook editor offers under a CEL box and what Insert writes (spec §9). It evaluates nothing: it writes text
/// the live check and apply judge.
/// </summary>
internal static class FunctionOffer
{
    /// <summary>The functions a profile admits, one per name, in name order (ordinal, as the catalog lists them).</summary>
    /// <param name="functions">Every overload, as <c>cel/functions</c> answers.</param>
    /// <param name="profile">The box's profile: <see cref="CelProfile.Mutate"/> or <see cref="CelProfile.Condition"/>.</param>
    /// <returns>The offered functions.</returns>
    public static IReadOnlyList<OfferedFunction> For(IReadOnlyList<CelFunctionInfo> functions, CelProfile profile) =>
        [.. functions
            .Where(function => function.Profiles.Contains(profile))
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(Offer)];

    /// <summary>
    /// An overload as one line — <c>name(p: T, …) -> R</c>, <c>?</c> where null is accepted or may come back. The same text
    /// the core's <c>CelFunction.Signature()</c> writes (Host.Tests <c>FunctionOfferAgreementTests</c> pins it).
    /// </summary>
    /// <param name="function">One overload.</param>
    /// <returns>Its signature.</returns>
    public static string Signature(CelFunctionInfo function) =>
        $"{function.Name}({string.Join(", ", function.Parameters.Select(p => $"{p.Name}: {p.Type}{(p.AcceptsNull ? "?" : string.Empty)}"))})"
        + $" -> {function.Result}{(function.ResultMayBeNull ? "?" : string.Empty)}";

    /// <summary>
    /// The call Insert writes: the parameter names as placeholders, the first one selected — or, with
    /// <paramref name="firstArgument"/>, that in the first place and the next placeholder selected (spec §9.2).
    /// </summary>
    /// <param name="function">The function.</param>
    /// <param name="firstArgument">A ready first argument (<c>new.field</c>), or <see langword="null"/>.</param>
    /// <returns>The template and its selection.</returns>
    public static Insertion Template(OfferedFunction function, string? firstArgument)
    {
        var arguments = function.Parameters.Select((parameter, index) => index == 0 && firstArgument is not null ? firstArgument : parameter.Name).ToList();
        var text = $"{function.Name}({string.Join(", ", arguments)})";
        var placeholder = firstArgument is null ? 0 : 1;
        if (placeholder >= arguments.Count)
        {
            return new Insertion(text, text.Length, 0);
        }

        var from = function.Name.Length + 1 + arguments.Take(placeholder).Sum(argument => argument.Length + 2);
        return new Insertion(text, from, arguments[placeholder].Length);
    }

    /// <summary>
    /// <c>new.{field}</c> when a mutate row's field can be the function's first argument: the point has a <c>new.</c>
    /// image and the first parameter takes the field's CEL type (an Int also fills a Decimal, C1 F7).
    /// </summary>
    /// <param name="function">The function.</param>
    /// <param name="point">The hook's point.</param>
    /// <param name="field">The row's field, or <see langword="null"/> when none is chosen.</param>
    /// <returns>The argument, or <see langword="null"/>.</returns>
    public static string? FirstArgument(OfferedFunction function, string point, FieldSchema? field) =>
        field is not null && ConditionTable.HasNew(point) && function.Parameters is [var first, ..] && Takes(first.Type, CelFieldType.Of(field))
            ? $"new.{field.Name}"
            : null;

    /// <summary>Writes <paramref name="template"/> over the selection <c>[start, end)</c> of <paramref name="text"/>.</summary>
    /// <param name="text">The box's text.</param>
    /// <param name="start">The selection's start (clamped into the text).</param>
    /// <param name="end">Its end (clamped to at least <paramref name="start"/>).</param>
    /// <param name="template">What to write.</param>
    /// <returns>The new text and the range to select in it.</returns>
    public static Insertion Insert(string text, int start, int end, Insertion template)
    {
        var from = Math.Clamp(start, 0, text.Length);
        var to = Math.Clamp(end, from, text.Length);
        var written = string.Concat(text.AsSpan(0, from), template.Text, text.AsSpan(to));
        return new Insertion(written, from + template.SelectFrom, template.SelectLength);
    }

    private static OfferedFunction Offer(IGrouping<string, CelFunctionInfo> overloads)
    {
        var first = overloads.First();
        var longest = overloads.OrderByDescending(overload => overload.Parameters.Count).First();
        return new OfferedFunction(
            overloads.Key, [.. overloads.Select(Signature)], first.Summary, first.Provenance == CelFunctionProvenance.Host, longest.Parameters);
    }

    private static bool Takes(CelValueType parameter, CelValueType argument) =>
        parameter == argument || (parameter == CelValueType.Decimal && argument == CelValueType.Int);
}
```

(`Insert_replaces_the_selection…` row 5 — a caret past the text, start 9 > end 2 — clamps to the end.)

- [ ] **Step 4: The gateway** — in `ManagementGateway.cs` add the slot beside `_capabilities`, the read after `CapabilitiesAsync`, and `_functions.Value = null;` in `Invalidate()`:

```csharp
    private readonly Slot<ManagementCelFunctions> _functions = new();

    /// <summary>Every CEL function a descriptor may call on this instance — built-ins and the host's (C1 <c>cel/functions</c>).</summary>
    /// <remarks>
    /// Cached like capabilities. A failure to ask answers an empty list and caches nothing: the editor then simply offers
    /// no list, and a helper must never be the reason an operator cannot edit (as <see cref="CheckExpressionAsync"/>).
    /// </remarks>
    public async ValueTask<IReadOnlyList<CelFunctionInfo>> CelFunctionsAsync(CancellationToken ct)
    {
        try
        {
            var project = await ProjectAsync(ct).ConfigureAwait(false);
            var answer = await CachedAsync(_functions, () => management.GetCelFunctionsAsync(project, ct), ct).ConfigureAwait(false);
            return answer.Functions;
        }
        catch (Exception ex) when (ex is ManagementRequestException or ManagementForbiddenException
            or OperationCanceledException or HttpRequestException)
        {
            return [];
        }
    }
```

If `CapabilitiesAsync` wraps its call in `AsOperatorAsync`, do the same here — read the method and copy its shape exactly.

- [ ] **Step 5: The agreement** (`test/MMLib.Alvo.Host.Tests/FunctionOfferAgreementTests.cs`):

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Host.Tests;

/// <summary>The dashboard's restatements of the catalog against the core (spec E13).</summary>
public sealed class FunctionOfferAgreementTests
{
    public static TheoryData<string> BuiltIns() => [.. CelFunctionCatalog.BuiltIns.Names];

    [Fact]
    public void The_dashboard_writes_every_signature_as_the_core_does() =>
        CelFunctionCatalog.BuiltIns.Functions.ShouldAllBe(function => FunctionOffer.Signature(function.Describe()) == function.Signature());

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void Every_template_compiles_once_its_placeholders_are_values(string name)
    {
        var offered = FunctionOffer.For(CelFunctionCatalog.BuiltIns.Describe(), CelProfile.Mutate).Single(f => f.Name == name);
        var source = offered.Parameters.Aggregate(
            FunctionOffer.Template(offered, firstArgument: null).Text,
            (text, parameter) => ReplaceFirst(text, parameter.Name, Value(parameter.Type)));

        new CelCompiler().Compile(source, CelProfile.Mutate, Probe).Errors.ShouldBeEmpty(source);
    }

    private static string Value(CelValueType type) => type switch
    {
        CelValueType.String => "new.text_field",
        CelValueType.Int => "1",
        CelValueType.Decimal => "new.money",
        CelValueType.Bool => "true",
        CelValueType.Timestamp => "new.moment",
        CelValueType.Uuid => "new.ident",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static string ReplaceFirst(string text, string placeholder, string value)
    {
        var at = text.IndexOf(placeholder, text.IndexOf('(', StringComparison.Ordinal), StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), value, text.AsSpan(at + placeholder.Length));
    }

    private static EntitySchema Probe { get; } = new()
    {
        Name = "probes",
        Fields =
        [
            new FieldSchema { Name = "text_field", Type = FieldType.String, MaxLength = 200, Nullable = true },
            new FieldSchema { Name = "money", Type = FieldType.Decimal, Precision = 18, Scale = 2, Nullable = true },
            new FieldSchema { Name = "moment", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "ident", Type = FieldType.Uuid, Nullable = true },
        ],
    };
}
```

(`substring`'s template with `start` = `1`, `end` = `1` compiles — `substring` declares no constant check and not every argument is a literal, so Task 6 evaluates nothing; `math.round`'s longest template becomes `math.round(new.money, 1)`, inside its `digits` range; `int(value)` binds the Decimal or String overload by the first value given: `new.text_field` is String — fine. If `Describe()`/`Signature()` are not reachable from Host.Tests, check the core's `InternalsVisibleTo` list includes `MMLib.Alvo.Host.Tests`, as the C1 skill facts already rely on.)

- [ ] **Step 6: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*FunctionOfferTests'`, `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*FunctionOfferAgreementTests'`, `scripts/test-ring0`.
Expected: PASS; `PublicApi.MMLib.Alvo.Admin.verified.txt` unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs src/MMLib.Alvo.Admin/Components/Schema/FunctionOffer.cs test/MMLib.Alvo.Admin.Tests/Schema/FunctionOfferTests.cs test/MMLib.Alvo.Host.Tests/FunctionOfferAgreementTests.cs
git commit -m "feat(admin): read the CEL function catalog and shape what a box offers and inserts

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 11: a name followed by `.` is never a column in a rename

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/CelNames.cs` (`IsColumn`)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/CelNamesTests.cs` (exists; add facts)

**Interfaces:**
- Consumes/produces: `CelNames.Rename(string cel, string from, string to)` — unchanged signature; `FieldReferences` (`FieldReferences.cs:264`) uses it, so a field rename and "where is it used" both follow.

- [ ] **Step 1: Write the failing facts** — append to `CelNamesTests`:

```csharp
    [Theory]
    [InlineData("math.round(new.math) > 1", "math.round(new.amount) > 1")]
    [InlineData("math.round(math)", "math.round(amount)")]
    [InlineData("math . round (math)", "math . round (amount)")]
    public void A_namespace_before_a_dot_is_never_renamed_as_a_field(string cel, string renamed) =>
        CelNames.Rename(cel, "math", "amount").ShouldBe(renamed);

    [Fact]
    public void A_function_member_after_the_namespace_is_never_renamed() =>
        CelNames.Rename("math.round(new.round)", "round", "rounded").ShouldBe("math.round(new.rounded)");
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*CelNamesTests'`
Expected: FAIL — `math.round(new.math)` becomes `amount.round(new.amount)`.

- [ ] **Step 3: Implement** — at the top of `IsColumn`, before the `(` test:

```csharp
        /* A name followed by '.' is a namespace (math.round) or an image (new., old.), never a column: Alvo has no other
           dotted field path (cel.md deviation 8), so renaming it could only rewrite a call (spec §13). */
        if (NextNonSpace(cel, end) == '.')
        {
            return false;
        }
```

`IsImageBefore` already keeps `round` after `math.` from being read as a column (`math` is not an image).

- [ ] **Step 4: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*CelNamesTests' --filter-class '*FieldReferences*'`, `scripts/test-ring0`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/CelNames.cs test/MMLib.Alvo.Admin.Tests/Schema/CelNamesTests.cs
git commit -m "fix(admin): a field rename never rewrites a namespaced function call

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 12: the hook editor offers the functions under each CEL box

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Functions.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor` (`@using MMLib.Alvo.Expressions`; a `FunctionList` fragment; one call in `MutateValue(row, i)`'s expression branch — from B Task 9; one in `ConditionField`'s text branch — from B Task 19)
- Modify: `src/MMLib.Alvo.Admin/Internal/AdminInterop.cs` (`CaretAsync`, `SelectRangeAsync`), `src/MMLib.Alvo.Admin/wwwroot/admin.js` (`caret`, `selectRange`), `src/MMLib.Alvo.Admin/wwwroot/alvo.css` (`.a-fn*`)
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/RecordingWorld.cs`
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/FunctionOfferScenarios.cs`

**Interfaces:**
- Consumes: `FunctionOffer`, `OfferedFunction`, `Insertion`, `ManagementGateway.CelFunctionsAsync` (Task 10); **from B Task 8:** `Current` (`.Point`, `.Condition`, `.MutateRows`), `TypeCondition`, `Findings`; **from B Task 9:** `MutateValue(MutateRow, int)`, `TypeMutateText(int, string?)`, `FieldOf(MutateRow)`, `MutateValueId(int)`, ids `hook-mutate-value-{i}`, `hook-mutate-mode-{i}` (radios "a value" | "an expression"), label "Field {n}"; **from B Task 19:** `ConditionField`'s text branch (`#hook-condition`, label "Condition (CEL)"), `_guided`, mode radios `hook-condition-mode` ("Guided" | "Text"); the check sentences `check-{inputId}` (`HooksTab.razor`'s `Findings`, as built: `check-hook-condition`, `check-hook-mutate-value-{i}`); `AdminSession.WaitForFocusOnAsync(id)`; `ManagementDecorator.Around`.
- Produces: test ids `fn-list-{inputId}`, `fn-{name}`, `fn-insert-{name}`; JS exports `caret(id)`, `selectRange(id, start, length)`; the e2e `public class RecordingWorld : AdminWorld` (bike workshop; `CheckedAsync(source)`, `FunctionListsAnswered`, `FunctionListAnsweredAsync(seen)`, `protected virtual ListFunctionsAsync(Func<Task<ManagementCelFunctions>> shipped)`) that Tasks 13 and 15 build on; helpers `FunctionOfferScenarios.NewHookAsync`, `NewMutateExpressionAsync`, `OpenListAsync`, `TextModeAsync`, `Selection`, `Combobox`, `AssertCheckKeepsFocusAsync`.

- [ ] **Step 1: The recording world** (`RecordingWorld.cs`) — every scenario of this slice waits for what it asserts (spec D-10), so the world records the dashboard's verdicts by source and counts its function-list requests:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The bike-workshop example with a watch on what the dashboard asks the management surface: every expression verdict,
/// by its source, and every function-list request. A scenario waits for exactly what it asserts (spec D-10) — the verdict
/// on the text it typed, or proof the list was asked for before it asserts that no list is drawn.
/// </summary>
public class RecordingWorld : AdminWorld
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ManagementExpressionVerdict>> _checks = new(StringComparer.Ordinal);

    private int _functionLists;

    /// <summary>How many function-list requests have finished, answered or refused.</summary>
    internal int FunctionListsAnswered => Volatile.Read(ref _functionLists);

    /// <inheritdoc/>
    protected override string Descriptor => Descriptors.BikeWorkshop;

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services) =>
        ManagementDecorator.Around(services, shipped => new Watching(shipped, this));

    /// <summary>The first verdict the dashboard was given for <paramref name="source"/>, waiting up to ten seconds for it.</summary>
    /// <param name="source">The exact expression text.</param>
    public Task<ManagementExpressionVerdict> CheckedAsync(string source) => Check(source).Task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Waits until more than <paramref name="seen"/> function-list requests have finished.</summary>
    /// <param name="seen">The count read before the step that should ask.</param>
    public async Task FunctionListAnsweredAsync(int seen)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (FunctionListsAnswered <= seen)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the dashboard never asked for the function list");
            await Task.Delay(50);
        }
    }

    /// <summary>What this world answers for <c>cel/functions</c>; a world may refuse it instead.</summary>
    /// <param name="shipped">The shipped management's answer.</param>
    protected virtual Task<ManagementCelFunctions> ListFunctionsAsync(Func<Task<ManagementCelFunctions>> shipped) => shipped();

    private TaskCompletionSource<ManagementExpressionVerdict> Check(string source) =>
        _checks.GetOrAdd(source, _ => new TaskCompletionSource<ManagementExpressionVerdict>(TaskCreationOptions.RunContinuationsAsynchronously));

    private sealed class Watching(IAlvoManagement inner, RecordingWorld world) : ManagementDecorator(inner)
    {
        public override async Task<ManagementExpressionVerdict> CheckExpressionAsync(
            string project, ManagementExpressionCheck request, CancellationToken ct = default)
        {
            var verdict = await base.CheckExpressionAsync(project, request, ct);
            world.Check(request.Source).TrySetResult(verdict);
            return verdict;
        }

        public override async Task<ManagementCelFunctions> GetCelFunctionsAsync(string project, CancellationToken ct = default)
        {
            try
            {
                return await world.ListFunctionsAsync(() => base.GetCelFunctionsAsync(project, ct));
            }
            finally
            {
                Interlocked.Increment(ref world._functionLists);
            }
        }
    }
}
```

(`ManagementExpressionCheck`'s source member is the one the check request carries — use its real name. If `ManagementDecorator`'s members are not `virtual … = default`, match their declared signatures exactly, as `KeptFollowScenarios`' decorator does.)

- [ ] **Step 2: Write the failing e2e scenarios** (`FunctionOfferScenarios.cs`):

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The hook editor offers the CEL functions a box's slot admits, and Insert writes a call at the caret with its first
/// placeholder selected (spec §9). Built-ins only — the host-function proof is <see cref="HostFunctionScenarios"/>.
/// Each scenario works on a different entity: the class shares one working copy and nothing here is added.
/// </summary>
/// <param name="world">The running host and browser, recording what the dashboard asks.</param>
public sealed class FunctionOfferScenarios(RecordingWorld world) : IClassFixture<RecordingWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_mutate_value_offers_mutate_functions_and_insert_selects_the_first_placeholder()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewMutateExpressionAsync(session, "service_orders", "beforeUpdate", "work_notes");

        var list = await OpenListAsync(session, "hook-mutate-value-0");
        (await list.GetByTestId("fn-now").CountAsync()).ShouldBe(1, "now() is a Mutate function");
        (await list.GetByTestId("fn-math.round").InnerTextAsync()).ShouldContain("math.round(x: Decimal, digits: Int) -> Decimal");
        (await list.GetByTestId("fn-math.round").InnerTextAsync()).ShouldContain("built-in");

        await list.GetByTestId("fn-insert-replace").ClickAsync();

        (await session.Page.InputValueAsync("#hook-mutate-value-0")).ShouldBe("replace(new.work_notes, search, replacement)");
        await session.WaitForFocusOnAsync("hook-mutate-value-0");
        (await Selection(session)).ShouldBe("search");
        await AssertCheckKeepsFocusAsync(session, "hook-mutate-value-0", "search");
        await session.Page.Keyboard.TypeAsync("'-'");
        (await session.Page.InputValueAsync("#hook-mutate-value-0")).ShouldBe("replace(new.work_notes, '-', replacement)");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_condition_offers_condition_functions_and_insert_lands_at_the_caret()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await NewHookAsync(session, "customers", "beforeCreate", "reject");
        await TextModeAsync(session);
        await session.Page.FillAsync("#hook-condition", " > 3");
        await session.Page.FocusAsync("#hook-condition");
        await session.Page.Keyboard.PressAsync("Home");

        var list = await OpenListAsync(session, "hook-condition");
        (await list.GetByTestId("fn-now").CountAsync()).ShouldBe(0, "now() is not a Condition function — the list has loaded, so this absence means it");
        await list.GetByTestId("fn-insert-size").ClickAsync();

        (await session.Page.InputValueAsync("#hook-condition")).ShouldBe("size(text) > 3");
        await session.WaitForFocusOnAsync("hook-condition");
        (await Selection(session)).ShouldBe("text");
        await AssertCheckKeepsFocusAsync(session, "hook-condition", "text");
        await session.Page.Keyboard.TypeAsync("new.last_name");

        (await session.Page.InputValueAsync("#hook-condition")).ShouldBe("size(new.last_name) > 3");
        (await world.CheckedAsync("size(new.last_name) > 3")).Findings.ShouldBeEmpty("the verdict on exactly this text, not a count read right after typing");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Spec AC 4: the inserted placeholder is not a field, so the live check speaks — within 3 s — and focus and the
    /// selection stay where Insert put them. A check that took focus would make the operator click back before typing.
    /// </summary>
    internal static async Task AssertCheckKeepsFocusAsync(AdminSession session, string inputId, string selected)
    {
        await session.Page.GetByTestId($"check-{inputId}").First.WaitForAsync(new() { Timeout = 3_000 });

        (await session.Page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''")).ShouldBe(inputId, "the check sentence never takes focus");
        (await Selection(session)).ShouldBe(selected, "nor the selection");
    }

    internal static async Task<ILocator> OpenListAsync(AdminSession session, string inputId)
    {
        var list = session.Page.GetByTestId($"fn-list-{inputId}");
        await list.Locator("summary").ClickAsync();
        await list.Locator("[data-testid^='fn-insert-']").First.WaitForAsync();
        return list;
    }

    internal static async Task NewHookAsync(AdminSession session, string entity, string point, string kind)
    {
        await HookEditInPlaceScenarios.OnWriteAsync(session, entity);
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = point, Exact = true }).ClickAsync();
        await session.Page.GetByTestId("hook-actions").GetByRole(AriaRole.Radio, new() { Name = kind, Exact = true }).ClickAsync();
    }

    internal static async Task NewMutateExpressionAsync(AdminSession session, string entity, string point, string field)
    {
        await NewHookAsync(session, entity, point, "mutate");
        await session.ChooseAsync(Combobox(session, "Field 1"), field);
        await MutateModeAsync(session, 0, "an expression");
        await session.Page.Locator("#hook-mutate-value-0").WaitForAsync();
    }

    internal static Task MutateModeAsync(AdminSession session, int index, string mode) =>
        session.Page.GetByTestId($"hook-mutate-mode-{index}").GetByRole(AriaRole.Radio, new() { Name = mode, Exact = true }).ClickAsync();

    internal static Task TextModeAsync(AdminSession session) => ConditionModeAsync(session, "Text");

    internal static Task ConditionModeAsync(AdminSession session, string mode) =>
        session.Page.GetByTestId("hook-condition-mode").GetByRole(AriaRole.Radio, new() { Name = mode, Exact = true }).ClickAsync();

    internal static Task<string> Selection(AdminSession session) =>
        session.Page.EvaluateAsync<string>("() => { const e = document.activeElement; return e.value.substring(e.selectionStart, e.selectionEnd); }");

    internal static ILocator Combobox(AdminSession session, string name)
        => session.Page.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });
}
```

The `fn-…` test ids are only ever reached through `GetByTestId` (an attribute match): `fn-math.round` holds a dot, which a CSS `#id` or class selector would read as a class (spec D-16).

- [ ] **Step 2b: Run to verify failure**

Run: `scripts/test-admin-e2e --filter-class '*FunctionOfferScenarios'`
Expected: FAIL — no `fn-list-hook-mutate-value-0`.

- [ ] **Step 3: The browser half** — append to `wwwroot/admin.js` (LF, no BOM):

```js
/**
 * The caret of a text box as [start, end] — kept by the input after a button takes focus — or null when the box is
 * not on the page. Insert writes a function call there (spec §9.2).
 */
export function caret(id) {
  const box = document.getElementById(id);
  return box && typeof box.selectionStart === 'number' ? [box.selectionStart, box.selectionEnd] : null;
}

/** Focuses a text box and selects a range in it: the placeholder an inserted call asks the operator to replace. */
export function selectRange(id, start, length) {
  const box = document.getElementById(id);
  if (!box) {
    return;
  }
  box.focus();
  box.setSelectionRange(start, start + length);
}
```

and to `AdminInterop.cs`, beside `CopyAsync`:

```csharp
    /// <summary>The caret of the text box <paramref name="id"/> as <c>[start, end]</c>, or <see langword="null"/>.</summary>
    public Task<int[]?> CaretAsync(string id) => QuietlyAsync<int[]?>(module => module.InvokeAsync<int[]?>("caret", id));

    /// <summary>Focuses the text box <paramref name="id"/> and selects <paramref name="length"/> characters from <paramref name="start"/>.</summary>
    public Task SelectRangeAsync(string id, int start, int length)
        => QuietlyAsync(module => module.InvokeVoidAsync("selectRange", id, start, length));
```

- [ ] **Step 4: The component half** (`HooksTab.Functions.cs`):

```csharp
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The functions a CEL box may call, offered under it, and Insert (spec §9, E8). */
public partial class HooksTab
{
    /// <summary>Every function this instance knows, read once per tab; empty when the list could not be read.</summary>
    private IReadOnlyList<CelFunctionInfo> _functions = [];

    /// <summary>The range to select once the inserted text is on the page; consumed by <see cref="SelectInsertedAsync"/>.</summary>
    private (string Id, int From, int Length)? _selectAfterRender;

    /// <summary>Reads the catalog through the gateway (cached per circuit) and draws the lists it fills.</summary>
    private async Task LoadFunctionsAsync()
    {
        _functions = await Gateway.CelFunctionsAsync(CancellationToken.None);
        StateHasChanged();
    }

    private IReadOnlyList<OfferedFunction> Offered(CelProfile profile) => FunctionOffer.For(_functions, profile);

    /// <summary>Inserts into a mutate row's expression, with <c>new.{field}</c> first when the box is empty and the type fits.</summary>
    private async Task InsertIntoMutateAsync(int index, OfferedFunction function)
    {
        var row = Current.MutateRows[index];
        var first = row.Text.Length == 0 ? FunctionOffer.FirstArgument(function, Current.Point, FieldOf(row)) : null;
        var inserted = await InsertAsync(MutateValueId(index), row.Text, FunctionOffer.Template(function, first));
        TypeMutateText(index, inserted.Text);
    }

    /// <summary>Inserts into the condition (text mode only offers the list).</summary>
    private async Task InsertIntoConditionAsync(OfferedFunction function)
    {
        var inserted = await InsertAsync(ConditionInputId, Current.Condition, FunctionOffer.Template(function, null));
        await TypeConditionFromInsertAsync(inserted.Text);
    }

    /// <summary>Writes the template at the box's caret (its end when the caret is unknown) and remembers what to select.</summary>
    private async Task<Insertion> InsertAsync(string id, string text, Insertion template)
    {
        var caret = await Interop.CaretAsync(id);
        var inserted = caret is [var start, var end]
            ? FunctionOffer.Insert(text, start, end, template)
            : FunctionOffer.Insert(text, text.Length, text.Length, template);
        _selectAfterRender = (id, inserted.SelectFrom, inserted.SelectLength);
        return inserted;
    }

    /// <summary>After the render that put the inserted text on the page: focus the box and select the placeholder.</summary>
    private async Task SelectInsertedAsync()
    {
        if (_selectAfterRender is { } select)
        {
            _selectAfterRender = null;
            await Interop.SelectRangeAsync(select.Id, select.From, select.Length);
        }
    }

    private const string ConditionInputId = "hook-condition";
}
```

`TypeConditionFromInsertAsync(string)` is a two-line adapter over B Task 8's `TypeCondition` — call it exactly as the condition box's `ValueChanged` does (`await TypeCondition(text)` if it returns a `Task`, plain call otherwise). Wire the lifecycle: if `HooksTab` already overrides `OnAfterRenderAsync`, add at its start `if (firstRender) { await LoadFunctionsAsync(); }` and at its end `await SelectInsertedAsync();`; otherwise add the override in this file with those two statements.

- [ ] **Step 5: The fragment** — in `HooksTab.razor`'s `@code`, after `Findings`:

```razor
    /// <summary>The functions a CEL box may call, under it: signatures, summary, provenance and Insert (spec §9.1).</summary>
    private RenderFragment FunctionList(string inputId, string boxLabel, CelProfile profile, Func<OfferedFunction, Task> insert) => @<text>
        @if (Offered(profile) is { Count: > 0 } offered)
        {
            <details class="a-disclosure a-fn" data-testid="@($"fn-list-{inputId}")">
                <summary>Functions you can call here (@offered.Count)</summary>
                <p class="a-section__sub">
                    A built-in works in every Alvo build; a host function exists only in this host, and an import into another
                    build refuses it. Insert writes the call with its parameter names — replace each with a field or a value;
                    the check under the box says what is still wrong.
                </p>
                @foreach (var function in offered)
                {
                    <ListRow class="a-fn__row" data-testid="@($"fn-{function.Name}")">
                        <span class="a-listrow__rest a-listrow__rest--stacked">
                            @foreach (var signature in function.Signatures)
                            {
                                <code class="a-mono a-fn__signature">@signature</code>
                            }
                            <span class="a-section__sub">@function.Summary</span>
                        </span>
                        <span class="a-fn__actions">
                            <span class="@(function.IsHost ? "a-badge a-badge--accent" : "a-badge")">@(function.IsHost ? "this host" : "built-in")</span>
                            <AlvoButton Tone="AlvoButton.ButtonTone.Ghost" Small="true" data-testid="@($"fn-insert-{function.Name}")"
                                        aria-label="@($"Insert {function.Name} into {boxLabel}")" OnClick="_ => insert(function)">Insert</AlvoButton>
                        </span>
                    </ListRow>
                }
            </details>
        }
    </text>;
```

Place the calls: in `MutateValue(row, i)` (from B Task 9), right after the expression branch's closing `</Field>`:
`@FunctionList(MutateValueId(i), $"Set field {i + 1} to", CelProfile.Mutate, function => InsertIntoMutateAsync(i, function))`;
in `ConditionField` (from B Task 19), right after the **text** branch's closing `</Field>` (never in the guided branch):
`@FunctionList("hook-condition", "Condition (CEL)", CelProfile.Condition, InsertIntoConditionAsync)`.
Add `@using MMLib.Alvo.Expressions` at the top of `HooksTab.razor` if `_Imports.razor` lacks it. If `ListRow` does not pass `class` through, use its own attribute splatting (`CssClass.Of`) as `Refusal.razor` does, or drop `class` and style `.a-fn .a-listrow`.

- [ ] **Step 6: The styles** — append to `wwwroot/alvo.css` (no BOM; inside the same layer/block the `.a-disclosure` rules sit in). Written for `AssertNoVerticalTextAsync` (Task 13, spec §9.5, D-11): no `overflow-wrap: anywhere` element may sit in a flex row unless its container holds `min-width: 0` and stretches it to the row's width — otherwise the flex item shrinks to one character and the signature runs down the sheet as a column of letters. `ListRow`'s root is the flex `.a-listrow`, and `.a-listrow__rest--stacked` aligns its children to `flex-start` (their content width), so the function row overrides that:

```css
  /* A function row (spec §9.1): the signatures and the summary take their column's whole width. Left at their content
     width inside a flex column, an `overflow-wrap: anywhere` signature could shrink to a character per line — the defect
     AssertNoVerticalTextAsync exists for — so the column stretches them and holds min-width: 0. */
  .a-fn__row > .a-listrow__rest {
    min-width: 0;
    align-items: stretch;
  }

  .a-fn__signature {
    display: block;
    min-width: 0;
    overflow-wrap: anywhere;
  }

  .a-fn__actions {
    display: flex;
    flex: none;
    gap: var(--space-2);
    align-items: center;
  }

  @media (max-width: 599px) {
    .a-fn__row {
      flex-direction: column;
      align-items: stretch;
    }

    .a-fn__actions {
      justify-content: space-between;
    }
  }
```

(If `ListRow` does not pass `class` through `CssClass.Of`, use its attribute splatting as `Refusal.razor` does; the selectors above need `.a-fn__row` on the row's root.)

- [ ] **Step 7: Run, normalise, run again**

Run: `dotnet build`, `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*PatternLanguageTests' --filter-class '*FieldConventionTests'`, then `scripts/test-admin-e2e --filter-class '*FunctionOfferScenarios'`, then `scripts/test-admin-e2e --filter-class '*MutateEditingScenarios'` and `--filter-class '*GuidedConditionScenarios'` (B's, unchanged), then `scripts/test-ring0`.
Expected: PASS; `PublicApi.MMLib.Alvo.Admin.verified.txt` unchanged (fragments and a partial class add no public symbol — if it moved, a member became public: make it private).

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/HooksTab.Functions.cs src/MMLib.Alvo.Admin/Components/Schema/HooksTab.razor src/MMLib.Alvo.Admin/Internal/AdminInterop.cs src/MMLib.Alvo.Admin/wwwroot/admin.js src/MMLib.Alvo.Admin/wwwroot/alvo.css test/MMLib.Alvo.Admin.Tests.EndToEnd/RecordingWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/FunctionOfferScenarios.cs
git commit -m "feat(admin): the hook editor offers the CEL functions a box may call, and inserts one at the caret

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 13: the function list at phone width, where it is absent, and when it cannot be read

**Files:**
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs` (new `AssertSheetFitsAsync`, beside `AssertNoHorizontalScrollAsync`)
- Test: `test/MMLib.Alvo.Admin.Tests.EndToEnd/FunctionListLayoutScenarios.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/FunctionListUnavailableScenarios.cs`
- Modify (only if an assertion finds a defect): `src/MMLib.Alvo.Admin/wwwroot/alvo.css` (`.a-fn*`)

**Interfaces:**
- Consumes: `RecordingWorld` and `FunctionOfferScenarios`' helpers (Task 12); `AdminSession.AssertNoHorizontalScrollAsync`, `AssertNoVerticalTextAsync`, `SettleAsync` (existing); `.a-editor` / `.a-editor__body` (`AlvoEditor.razor:31`, `alvo.css` `.a-editor__body { overflow: auto; }`).
- Produces: `public Task AdminSession.AssertSheetFitsAsync()`; `FunctionsRefusedWorld : RecordingWorld`.

Why (spec §9.5, AC 5, D-10, D-11): `AssertNoHorizontalScrollAsync` measures the document and `main.a-content` — never the `AlvoEditor` sheet, a dialog outside both with its own scroll container — and it cannot see a word broken one character per line. And an absence asserted before the list could have loaded passes for the wrong reason.

- [ ] **Step 1: The sheet measurement** — add to `AdminSession.cs`, after `AssertNoHorizontalScrollAsync`:

```csharp
    /// <summary>
    /// Fails when the open editor sheet scrolls sideways, or reaches past the window's edge.
    /// </summary>
    /// <remarks>
    /// <see cref="AssertNoHorizontalScrollAsync"/> measures the document and <c>main.a-content</c>. A sheet is a dialog
    /// outside both, with its own scroll container (<c>.a-editor__body</c>, <c>overflow: auto</c>), so content wider
    /// than a phone scrolled the sheet while that check stayed green (spec §9.5). It fails, too, when no sheet is open:
    /// a check that measured nothing must not pass.
    /// </remarks>
    public async Task AssertSheetFitsAsync()
    {
        await SettleAsync().ConfigureAwait(false);
        var overflow = await Page.EvaluateAsync<int>(
            "() => { const body = document.querySelector('.a-editor__body'), sheet = document.querySelector('.a-editor');"
            + " if (!body || !sheet) return -1;"
            + " return Math.max(body.scrollWidth - body.clientWidth,"
            + "   Math.ceil(sheet.getBoundingClientRect().right - window.innerWidth)); }").ConfigureAwait(false);

        overflow.ShouldNotBe(-1, "no editor sheet is open, so there was nothing to measure");
        overflow.ShouldBeLessThanOrEqualTo(1, "the editor sheet scrolls sideways at this width");
    }
```

- [ ] **Step 2: Write the scenarios** (`FunctionListLayoutScenarios.cs`):

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Where the function list is drawn and how it fits (spec §9.1, §9.5): at phone width under both boxes — the document,
/// the sheet and every word — and absent where the box writes no CEL, asserted only after the list was shown in the same
/// session, so the absence cannot be a list that had not loaded yet (D-10).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class FunctionListLayoutScenarios(RecordingWorld world) : IClassFixture<RecordingWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_function_list_fits_a_phone()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 375);
        await FunctionOfferScenarios.NewMutateExpressionAsync(session, "rentals", "beforeUpdate", "damage_report");
        await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0");
        await AssertFitsAsync(session);

        await FunctionOfferScenarios.TextModeAsync(session);
        await FunctionOfferScenarios.OpenListAsync(session, "hook-condition");
        await AssertFitsAsync(session);
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Guided_rows_and_literal_values_offer_no_list()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await FunctionOfferScenarios.NewHookAsync(session, "parts", "beforeCreate", "mutate");

        await FunctionOfferScenarios.TextModeAsync(session);
        await FunctionOfferScenarios.OpenListAsync(session, "hook-condition");
        await FunctionOfferScenarios.ConditionModeAsync(session, "Guided");
        await Gone(session, "fn-list-hook-condition", "guided mode writes its own CEL");

        await session.ChooseAsync(FunctionOfferScenarios.Combobox(session, "Field 1"), "name");
        await FunctionOfferScenarios.MutateModeAsync(session, 0, "an expression");
        await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0");
        await FunctionOfferScenarios.MutateModeAsync(session, 0, "a value");
        await Gone(session, "fn-list-hook-mutate-value-0", "a literal value is no CEL");
        session.AssertConsoleClean();
    }

    private static async Task AssertFitsAsync(AdminSession session)
    {
        await session.AssertNoHorizontalScrollAsync();
        await session.AssertSheetFitsAsync();
        await session.AssertNoVerticalTextAsync();
    }

    /// <summary>The list was on the page a moment ago; wait until it is detached rather than counting right after a click.</summary>
    private static async Task Gone(AdminSession session, string testId, string because)
    {
        await session.Page.GetByTestId(testId).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.Page.GetByTestId(testId).CountAsync()).ShouldBe(0, because);
    }
}
```

and `FunctionListUnavailableScenarios.cs` — a world whose `cel/functions` is refused:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A deployment whose function list cannot be read: no list is offered, and the editor still works (spec §9.3).</summary>
public sealed class FunctionsRefusedWorld : RecordingWorld
{
    /// <inheritdoc/>
    protected override Task<ManagementCelFunctions> ListFunctionsAsync(Func<Task<ManagementCelFunctions>> shipped) =>
        throw new ManagementForbiddenException();
}

/// <summary>
/// When <c>cel/functions</c> is refused the hook editor offers no list and still adds the hook. The absence is asserted
/// only after the world recorded the refused request, so it cannot be a list that had not loaded yet (D-10).
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class FunctionListUnavailableScenarios(FunctionsRefusedWorld world) : IClassFixture<FunctionsRefusedWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_refused_function_list_leaves_the_editor_working()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var seen = world.FunctionListsAnswered;
        await FunctionOfferScenarios.NewMutateExpressionAsync(session, "parts", "beforeCreate", "name");
        await world.FunctionListAnsweredAsync(seen);

        await session.Page.FillAsync("#hook-mutate-value-0", "trim(new.name)");
        (await world.CheckedAsync("trim(new.name)")).Findings.ShouldBeEmpty("the check still works without the list");
        (await session.Page.GetByTestId("fn-list-hook-mutate-value-0").CountAsync()).ShouldBe(0, "the refused request was answered, and nothing was drawn");

        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached });
        session.AssertConsoleClean();
    }
}
```

(`ManagementForbiddenException` has a parameterless constructor — `AlvoManagementService.EnsureMayPerform` throws it so. The gateway caches nothing on a failure, so each sign-in asks again; `seen` is read before the step that asks.)

- [ ] **Step 3: Run**

Run: `scripts/test-admin-e2e --filter-class '*FunctionListLayoutScenarios'`, then `scripts/test-admin-e2e --filter-class '*FunctionListUnavailableScenarios'`.
Expected: PASS. If `AssertNoVerticalTextAsync` names an element, it is a flex item carrying `overflow-wrap: anywhere` without a `min-width: 0` container that stretches it: fix that rule in `alvo.css`'s `.a-fn*` block (never by dropping `overflow-wrap`), and say so in the commit. If `AssertSheetFitsAsync` fails, the culprit is inside `.a-editor__body`: find it with the same widest-element probe `AssertNoHorizontalScrollAsync` uses, pointed at `.a-editor__body`.

- [ ] **Step 4: The rest of the suite still fits** — `AssertSheetFitsAsync` is new and only these scenarios call it; run `scripts/test-admin-e2e --filter-class '*MutateEditingScenarios'` to confirm `AdminSession` still builds and B's scenarios are untouched, then `scripts/test-ring0`.

- [ ] **Step 5: Commit**

```bash
git add test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminSession.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/FunctionListLayoutScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/FunctionListUnavailableScenarios.cs <alvo.css, only if Step 3 changed it>
git commit -m "test(admin): the function list fits a phone sheet, is absent where no CEL is written, and its refusal leaves the editor working

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 14: three guided operators — starts with, ends with, contains

**Files:**
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/ConditionTable.cs` (from B Task 1: `ConditionOperator` members, three `Rows`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/ConditionText.cs` (from B Task 16: `RowRefusal`)
- Modify: `test/MMLib.Alvo.Admin.Tests/Schema/ConditionTableTests.cs`, `ConditionTextTests.cs`, `ConditionTextRecognitionTests.cs` (from B Tasks 1, 16, 17)
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/GuidedConditionScenarios.cs` (from B Task 19)

**Interfaces:**
- Consumes: `OperatorSpec`, `ConditionFieldKind`, `OperandKind.Literal` (from B Task 1); `ConditionText.Row/Refusal/Recognize` (from B Tasks 16–17); `GuidedConditionConformanceTests.Every_combination_the_form_offers_is_accepted_by_apply` (from B Task 18) — unchanged code, larger count.
- Produces: `ConditionOperator.StartsWith`, `EndsWith`, `Contains`.

- [ ] **Step 1: Write the failing tests** — add to `ConditionTextTests`:

```csharp
    [Theory]
    [InlineData(ConditionOperator.StartsWith, "code", "WTU", "startsWith(new.code, 'WTU')")]
    [InlineData(ConditionOperator.EndsWith, "email", "@example.com", "endsWith(new.email, '@example.com')")]
    [InlineData(ConditionOperator.Contains, "note", "it's", "contains(new.note, 'it\\'s')")]
    public void A_text_test_row_writes_the_standard_function(ConditionOperator relation, string field, string value, string cel)
        => ConditionText.Row(new ConditionRow(relation, RowImage.New, field, ConditionFieldKind.Text, value)).ShouldBe(cel);

    [Theory]
    [InlineData(ConditionOperator.StartsWith, "starts with nothing")]
    [InlineData(ConditionOperator.EndsWith, "ends with nothing")]
    [InlineData(ConditionOperator.Contains, "contains nothing")]
    public void A_text_test_needs_a_value(ConditionOperator relation, string says)
        => ConditionText.Refusal(new GuidedCondition(true, [new ConditionRow(relation, RowImage.New, "note", ConditionFieldKind.Text, string.Empty)]))
            .ShouldNotBeNull().ShouldContain(says);
```

to `ConditionTableTests`:

```csharp
    [Theory]
    [InlineData(ConditionOperator.StartsWith)]
    [InlineData(ConditionOperator.EndsWith)]
    [InlineData(ConditionOperator.Contains)]
    public void A_text_test_is_offered_for_text_only_at_every_image_point(ConditionOperator relation)
    {
        foreach (var point in HookBuilder.Points.Where(point => ConditionTable.ImagesAt(point).Count > 0))
        {
            ConditionTable.For(point, ConditionFieldKind.Text, nullable: true).ShouldContain(spec => spec.Operator == relation);
            ConditionTable.For(point, ConditionFieldKind.Choice, nullable: true).ShouldNotContain(spec => spec.Operator == relation);
        }
    }
```

and to `ConditionTextRecognitionTests` one canonical-read row per operator in its existing theory (e.g. `("beforeCreate", "endsWith(new.email, '@example.com')")`) and one stays-text row: `("beforeCreate", "endsWith(new.email,'@x')", "not canonical spacing")`. Use the entity/field set that file's scope already declares (add a nullable text field `email` to it if none exists).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ConditionTableTests' --filter-class '*ConditionText*'`
Expected: build error — `ConditionOperator.StartsWith` does not exist.

- [ ] **Step 3: Implement** — `ConditionOperator` gains `StartsWith`, `EndsWith`, `Contains` (each with an XML doc naming its CEL function); `ConditionTable` gains `private static readonly ConditionFieldKind[] _text = [ConditionFieldKind.Text];` and, after `IsNotOrEmpty`'s row:

```csharp
        new(ConditionOperator.StartsWith, "starts with", "startsWith({f}, {v})", OperandKind.Literal, _text, false, false, false, "false: a test of an empty value is empty, and an empty condition does not fire"),
        new(ConditionOperator.EndsWith, "ends with", "endsWith({f}, {v})", OperandKind.Literal, _text, false, false, false, "false: a test of an empty value is empty, and an empty condition does not fire"),
        new(ConditionOperator.Contains, "contains", "contains({f}, {v})", OperandKind.Literal, _text, false, false, false, "false: a test of an empty value is empty, and an empty condition does not fire"),
```

`ConditionText.RowRefusal` gains, before the `_` arm:

```csharp
        OperandKind.Literal when row.Value.Length == 0 && row.Operator is ConditionOperator.StartsWith or ConditionOperator.EndsWith or ConditionOperator.Contains
            => $"Every text {ConditionTable.Of(row.Operator).Words} nothing; write the text it {ConditionTable.Of(row.Operator).Words}.",
```

(e.g. "Every text starts with nothing; write the text it starts with."). The generator, recognizer patterns (`PatternOf` escapes the parentheses) and the conformance fact read the table: no other code changes.

- [ ] **Step 4: The guided e2e** — append to `GuidedConditionScenarios` (from B Task 19; it uses `BikeWorkshopWorld`; `customers` beforeCreate is used by no other scenario in that class — if it is, pick an unused entity with a nullable text field):

```csharp
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Ends_with_is_a_guided_row_that_writes_the_built_in()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await HookEditInPlaceScenarios.OnWriteAsync(session, "customers");
        await session.Page.GetByTestId("hook-new").ClickAsync();
        await session.Page.GetByTestId("hook-points").GetByRole(AriaRole.Radio, new() { Name = "beforeCreate", Exact = true }).ClickAsync();

        await session.Page.GetByTestId("condition-add").ClickAsync();
        await session.ChooseAsync(Combobox(session, "Condition 1 field"), "email");
        await session.ChooseAsync(Combobox(session, "Condition 1 operator"), "ends with");
        await session.Page.GetByRole(AriaRole.Textbox, new() { Name = "Condition 1 value", Exact = true }).FillAsync("@example.com");

        (await session.Page.GetByTestId("hook-condition-readout").InnerTextAsync()).ShouldBe("endsWith(new.email, '@example.com')");
        await session.Page.FillAsync("#hook-reject", "Use the customer's real email address.");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        await session.GoAsync("/changes");
        await session.WaitForPlanAsync();
        await session.Page.Locator("#apply-reason").WaitForAsync();
        (await session.Page.GetByTestId("error-panel").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }
```

- [ ] **Step 5: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*ConditionTableTests' --filter-class '*ConditionText*'` (the round-trip property runs its 2,000 cases over the new rows), `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*GuidedConditionConformanceTests'` (the printed count rises by 3 rows × Text kinds × image points), `scripts/test-admin-e2e --filter-class '*GuidedConditionScenarios'`, `scripts/test-ring0`, `scripts/test-ring1`.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/ConditionTable.cs src/MMLib.Alvo.Admin/Components/Schema/ConditionText.cs test/MMLib.Alvo.Admin.Tests/Schema/ConditionTableTests.cs test/MMLib.Alvo.Admin.Tests/Schema/ConditionTextTests.cs test/MMLib.Alvo.Admin.Tests/Schema/ConditionTextRecognitionTests.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/GuidedConditionScenarios.cs
git commit -m "feat(admin): starts with, ends with and contains as guided condition rows

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 15: set up from code, end to end — a host function in a real browser over the shipped host

**Files:**
- Modify: `test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminWorld.cs` (the `IAlvoBuilder` seam)
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/HostFunctionWorld.cs`, `HostFunctionScenarios.cs`, `BuiltInConditionScenarios.cs`

**Interfaces:**
- Consumes: `AddCelFunction` (C1, core); `RecordingWorld` (its `CheckedAsync`, Task 12); `FunctionOfferScenarios.NewHookAsync/NewMutateExpressionAsync/OpenListAsync/TextModeAsync/Selection/AssertCheckKeepsFocusAsync` (Task 12); `AdminSession.PreviewPendingAsync`, `Button`, `Content`.
- Produces: `protected virtual void AdminWorld.Configure(IAlvoBuilder alvo)`; `HostFunctionWorld : RecordingWorld` (`Api()`; `CheckedAsync(source)` inherited).

- [ ] **Step 1: The seam** — in `AdminWorld.InitializeAsync`, right after `Configure(builder.Services);`:

```csharp
        /* The second seam: the host developer's own registrations — a CEL function (spec §11.1). The shipped host has
           already called AddAlvo, so this adds to its collection exactly as AddAlvo's callback would have: every
           IAlvoBuilder extension used here only adds services (C1's CelFunctionsWorld registers the same way). */
        Configure(new AlvoServices(builder.Services));
```

and add:

```csharp
    /// <summary>Registers this world's own Alvo extensions — a host's CEL functions — on the shipped host.</summary>
    /// <remarks>Empty by default, so the ordinary suite runs exactly what the container runs, which knows built-ins only.</remarks>
    /// <param name="alvo">The shipped host's Alvo builder.</param>
    protected virtual void Configure(IAlvoBuilder alvo)
    {
    }

    /// <summary>The shipped host's service collection, as the builder <c>AddAlvo</c> hands its callback.</summary>
    private sealed class AlvoServices(IServiceCollection services) : IAlvoBuilder
    {
        /// <inheritdoc/>
        public IServiceCollection Services { get; } = services;
    }
```

- [ ] **Step 2: The world** (`HostFunctionWorld.cs`) — Task 12's `RecordingWorld` already boots the bike workshop and records every verdict by source; this world adds what a host developer adds in C#, and a dev key:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The bike-workshop example on the shipped host, plus what an embedded host developer adds in C#: two CEL functions
/// (spec §11.1) and a dev key so a scenario can write through the HTTP Data API (E11). Every expression verdict is
/// recorded by <see cref="RecordingWorld"/>, so a scenario waits for the verdict on exactly the text it inserted.
/// </summary>
public sealed class HostFunctionWorld : RecordingWorld
{
    private const string KeyId = "e2e-functions";
    private const string KeySecret = "4f1d9c2b7a6e5d3c8b0a9f8e7d6c5b4a";

    /// <summary>The summary the frame-number function is registered with — the text the editor must show.</summary>
    internal const string FrameNumberSummary = "Upper-cases a frame number and drops every character that is not a letter or a digit.";

    /// <summary>A summary carrying markup, which the editor must render as text.</summary>
    internal const string MarkupSummary = "Adds <b>bold</b> emphasis: an exclamation mark at the end.";

    /// <inheritdoc/>
    protected override void Configure(IDictionary<string, string?> settings)
    {
        settings["Alvo:Auth:DevKeys:0:KeyId"] = KeyId;
        settings["Alvo:Auth:DevKeys:0:Secret"] = KeySecret;
        settings["Alvo:Auth:DevKeys:0:User"] = "5eed0000-0000-4000-8000-0000000000f1";
        settings["Alvo:Auth:DevKeys:0:Roles:0"] = "authenticated";
        settings["Alvo:Auth:DevKeys:0:Roles:1"] = "admin";
    }

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .AddCelFunction(
            "normalizeFrameNumber",
            (string value) => new string([.. value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]),
            FrameNumberSummary)
        .AddCelFunction("shout", (string value) => value + "!", MarkupSummary);

    /// <summary>A client of the HTTP Data API, authenticated with this world's dev key.</summary>
    public HttpClient Api()
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        client.DefaultRequestHeaders.Add("X-Alvo-Api-Key", $"{KeyId}.{KeySecret}");
        return client;
    }
}
```

(`AddCelFunction`'s namespace is the one `CelFunctionsWorld.cs` imports (`Microsoft.Extensions.DependencyInjection`). The dev-key secret must satisfy `AlvoAuthOptionsValidator`'s length rule — 32 characters as `scripts/demo-admin` uses. `RecordingWorld.Configure(IServiceCollection)` is not overridden: the recording decorator is the world's.)

- [ ] **Step 3: Write the scenarios** (`HostFunctionScenarios.cs`):

```csharp
using Microsoft.Playwright;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The maintainer's goal, end to end (spec §11.2): a function registered in C# is listed by the hook editor with its
/// summary, inserted into a mutate expression the check finds clean, applied, and evaluated by a Data API write.
/// </summary>
/// <param name="world">The shipped host with two host functions, and a browser.</param>
public sealed class HostFunctionScenarios(HostFunctionWorld world) : IClassFixture<HostFunctionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_host_function_is_offered_inserted_applied_and_stores_its_result()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await FunctionOfferScenarios.NewMutateExpressionAsync(session, "bikes", "beforeCreate", "frame_number");

        var list = await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0");
        var row = list.GetByTestId("fn-normalizeFrameNumber");
        (await row.InnerTextAsync()).ShouldContain(HostFunctionWorld.FrameNumberSummary);
        (await row.InnerTextAsync()).ShouldContain("this host");
        await list.GetByTestId("fn-insert-normalizeFrameNumber").ClickAsync();

        const string call = "normalizeFrameNumber(new.frame_number)";
        (await session.Page.InputValueAsync("#hook-mutate-value-0")).ShouldBe(call);
        (await world.CheckedAsync(call)).Findings.ShouldBeEmpty("the build knows the host's function");

        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await ApplyAsync(session, "Normalise frame numbers as bikes are created");

        using var api = world.Api();
        var customer = await CreateAsync(api, "customers", new { first_name = "Jana", last_name = "Nováková", phone = "+421 905 100 200", loyalty_tier = "none" });
        using var created = await api.PostAsJsonAsync("/api/bikes", new
        {
            customer_id = customer.GetProperty("id").GetString(), brand = "Kellys", model = "Spider 10",
            category = "mtb_hardtail", frame_number = "wtu 123-456 x",
        });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var bike = await created.Content.ReadFromJsonAsync<JsonElement>();
        bike.GetProperty("frame_number").GetString().ShouldBe("WTU123456X");
        var read = await api.GetFromJsonAsync<JsonElement>($"/api/bikes/{bike.GetProperty("id").GetString()}");
        read.GetProperty("frame_number").GetString().ShouldBe("WTU123456X", "stored, not only answered");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_host_summary_is_text_never_markup()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await FunctionOfferScenarios.NewMutateExpressionAsync(session, "rental_fleet", "beforeUpdate", "model");

        var row = (await FunctionOfferScenarios.OpenListAsync(session, "hook-mutate-value-0")).GetByTestId("fn-shout");
        (await row.InnerTextAsync()).ShouldContain("<b>bold</b>");
        (await row.Locator("b").CountAsync()).ShouldBe(0);
        session.AssertConsoleClean();
    }

    internal static async Task ApplyAsync(AdminSession session, string reason)
    {
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", reason);
        await session.Button("Apply these changes").ClickAsync();
        await session.Content.GetByText("Applied as revision").First.WaitForAsync();
    }

    internal static async Task<JsonElement> CreateAsync(HttpClient api, string entity, object body)
    {
        using var response = await api.PostAsJsonAsync($"/api/{entity}", body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
```

and `BuiltInConditionScenarios.cs` (its own `HostFunctionWorld` instance — it applies too; the phone check of the condition list is Task 13's):

```csharp
using Microsoft.Playwright;
using System.Net;
using System.Net.Http.Json;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>A built-in inserted into a condition in text mode refuses exactly the writes it names (spec §11.2).</summary>
/// <param name="world">The shipped host and a browser.</param>
public sealed class BuiltInConditionScenarios(HostFunctionWorld world) : IClassFixture<HostFunctionWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_built_in_inserted_into_a_condition_refuses_the_write_it_names()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await FunctionOfferScenarios.NewHookAsync(session, "customers", "beforeCreate", "reject");
        await FunctionOfferScenarios.TextModeAsync(session);

        var list = await FunctionOfferScenarios.OpenListAsync(session, "hook-condition");
        (await list.GetByTestId("fn-now").CountAsync()).ShouldBe(0);
        await list.GetByTestId("fn-insert-endsWith").ClickAsync();
        (await session.Page.InputValueAsync("#hook-condition")).ShouldBe("endsWith(text, suffix)");
        (await FunctionOfferScenarios.Selection(session)).ShouldBe("text");
        await FunctionOfferScenarios.AssertCheckKeepsFocusAsync(session, "hook-condition", "text");
        await session.Page.Keyboard.TypeAsync("new.email");
        (await session.Page.InputValueAsync("#hook-condition")).ShouldBe("endsWith(new.email, suffix)");

        const string condition = "endsWith(new.email, '@example.com')";
        await session.Page.FillAsync("#hook-condition", condition);
        (await world.CheckedAsync(condition)).Findings.ShouldBeEmpty();
        await session.Page.FillAsync("#hook-reject", "Use the customer's real email address.");
        var editor = session.Dialog("hook-editor");
        await editor.GetByTestId("hook-add").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await HostFunctionScenarios.ApplyAsync(session, "Refuse placeholder customer emails");

        using var api = world.Api();
        using var refused = await api.PostAsJsonAsync("/api/customers", Customer("ana@example.com"));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("Use the customer's real email address.");
        await HostFunctionScenarios.CreateAsync(api, "customers", Customer("ana@kros.sk"));
        session.AssertConsoleClean();
    }

    private static object Customer(string email) =>
        new { first_name = "Ana", last_name = "Horváthová", phone = "+421 905 100 300", loyalty_tier = "none", email };
}
```

- [ ] **Step 4: Run**

Run: `scripts/test-admin-e2e --filter-class '*HostFunctionScenarios'`, then `scripts/test-admin-e2e --filter-class '*BuiltInConditionScenarios'`.
Expected: PASS. If the Data API route differs from `/api/{entity}` (Host's `AlvoApiOptions.RoutePrefix` default `/api`), use the route `scripts/demo-admin`'s `api()` calls. If `phone` fails the bike-workshop `phone` format, take a phone from `examples/bike-workshop/seed/bike-workshop.seed.json`.

- [ ] **Step 5: Commit**

```bash
git add test/MMLib.Alvo.Admin.Tests.EndToEnd/AdminWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/HostFunctionWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/HostFunctionScenarios.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/BuiltInConditionScenarios.cs
git commit -m "test(admin): a host function registered in C# is listed, inserted, applied and stored end to end

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 16: the embedded sample registers a host function

**Files:**
- Modify: `samples/MMLib.Alvo.Samples.EmbeddedHost/SampleHost.cs` (the `AddAlvo` chain `:94-107`; a `NormalizeVinSummary` constant and a `NormalizeVin` method)
- Modify: `samples/MMLib.Alvo.Samples.EmbeddedHost/README.md` (a section "Registering a CEL function"; a row in "What each part demonstrates")
- Modify: `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/EmbeddedSampleTests.cs` (one fact; `SampleWorld.StartAsync` gains two optional parameters; `SampleWorld.AsBootstrapAdmin()`)
- Never modify: `examples/vehicle-registry/vehicles.alvo.json`

**Interfaces:**
- Consumes: `AddCelFunction(this IAlvoBuilder, string, Delegate, string?)` (C1, `AlvoBuilderExtensions.cs:97`); `IAlvoManagement.GetCelFunctionsAsync` → `ManagementCelFunctions.Functions` with `CelFunctionProvenance.Host` (C1, Abstractions); `IAlvoBootstrapAdmin` (Abstractions, namespace `MMLib.Alvo`; the core registers `NoBootstrapAdmin` with `TryAddSingleton`); `IAlvoContextAccessor.Principal`, `AlvoPrincipal`, `ApiKeyScope` (`MMLib.Alvo.Auth`), published exactly as `ManagementInProcessAccessTests.Publish` does; `IRoleCatalogProvider.DeclaredRoles`.
- Produces: `public const string SampleHost.NormalizeVinSummary`; the CEL function `normalizeVin(vin: String) -> String` on the sample's host.

Why (spec E16, §11.3, D-3): the e2e adapter (Task 15) is test code nobody copies; the sample is the documented answer to "how do I embed Alvo". Its descriptor is the one the standalone image serves (`Both_modes_generate_the_same_routes` pins that), and the image knows built-ins only (C1 G5) — so the shared file must never call the function. The fact uses a test-local copy derived from it at run time.

- [ ] **Step 1: Write the failing fact** — in `EmbeddedSampleTests`, add (with `using Microsoft.Extensions.DependencyInjection.Extensions;`, `using MMLib.Alvo.Auth;`, `using MMLib.Alvo.Expressions;`, `using MMLib.Alvo.Management;`):

```csharp
    /// <summary>The user the in-process management call acts as: this suite's bootstrap administrator.</summary>
    private static readonly Guid _administrator = Guid.Parse("0b5e7a1c-2d3f-4a5b-8c6d-7e8f9a0b1c2d");

    /// <summary>
    /// The sample registers a CEL function of its own, in its one <c>AddAlvo</c> call (spec E16): the catalog lists it
    /// as the host's, and a descriptor hook that calls it shapes a write through the generated Data API.
    /// </summary>
    /// <remarks>
    /// <b>Over a test-local descriptor, never the shared one.</b> <c>vehicles.alvo.json</c> is what the standalone image
    /// serves, and the image knows built-in functions only — so the hook that calls <c>normalizeVin</c> is added to a copy
    /// read from that file at run time, which can never drift from it.
    /// </remarks>
    [Fact]
    public async Task The_sample_registers_a_cel_function_a_hook_can_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var descriptor = DescriptorCallingNormalizeVin();
        try
        {
            await using var sample = await SampleWorld.StartAsync(descriptorPath: descriptor, bootstrapAdministrator: _administrator);
            var agent = sample.AsAgent();
            using var owner = await agent.PostAsJsonAsync(
                "/api/alvo/owners", new Dictionary<string, object?> { ["name"] = "Fleet Desk Ltd" }, ct);
            var ownerId = (await owner.Content.ReadFromJsonAsync<JsonObject>(ct))!["id"]!.GetValue<Guid>();

            using var vehicle = await agent.PostAsJsonAsync(
                "/api/alvo/vehicles",
                new Dictionary<string, object?>
                {
                    ["vin"] = "1hgcm82633a004352", ["plate"] = "BA-777AB", ["make"] = "Skoda", ["model"] = "Fabia", ["year"] = 2020,
                    ["owner_id"] = ownerId,
                },
                ct);

            vehicle.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await vehicle.Content.ReadFromJsonAsync<JsonObject>(ct))!["vin"]!.GetValue<string>()
                .ShouldBe("1HGCM82633A004352", "the descriptor's mutate called the sample's normalizeVin");

            var listed = (await sample.AsBootstrapAdmin(_administrator).GetCelFunctionsAsync("vehicle-registry", ct)).Functions
                .Single(function => function.Name == "normalizeVin");
            listed.Provenance.ShouldBe(CelFunctionProvenance.Host);
            listed.Summary.ShouldBe(SampleHost.NormalizeVinSummary);
        }
        finally
        {
            File.Delete(descriptor);
        }
    }

    /// <summary>
    /// <c>vehicles.alvo.json</c> with one before-create hook that calls <c>normalizeVin</c>, written to a temp file.
    /// </summary>
    private static string DescriptorCallingNormalizeVin()
    {
        var descriptor = JsonNode.Parse(File.ReadAllText(DescriptorPath))!.AsObject();
        descriptor["entities"]!["vehicles"]!["hooks"] = JsonNode.Parse(
            """{ "beforeCreate": [ { "action": { "mutate": { "vin": { "$cel": "normalizeVin(new.vin)" } } } } ] }""");
        var path = Path.Combine(Path.GetTempPath(), $"alvo-embedded-sample-{Guid.NewGuid():N}.alvo.json");
        File.WriteAllText(path, descriptor.ToJsonString());
        return path;
    }
```

The write goes first and the management read second: publishing an in-process principal sets an ambient value, which must not be in place while the Data API's own filter resolves the agent's key. The VIN is 17 characters already (`maxLength` 17), so it fits whether the payload is validated before or after the hook.

Then in `SampleWorld`: `StartAsync(string environment = "Development", string? descriptorPath = null, Guid? bootstrapAdministrator = null)`; the configuration uses `["FleetDesk:DescriptorPath"] = descriptorPath ?? DescriptorPath`; right after `builder.WebHost.UseTestServer();`:

```csharp
            if (bootstrapAdministrator is { } administrator)
            {
                // Management answers a caller it can place; this suite's in-process caller is a bootstrap administrator,
                // the one identity a descriptor's access block does not govern (vehicles.alvo.json declares none).
                builder.Services.Replace(ServiceDescriptor.Singleton<IAlvoBootstrapAdmin>(new OneBootstrapAdmin(new UserId(administrator))));
            }
```

and add to `SampleWorld`:

```csharp
        /// <summary>
        /// Publishes the bootstrap administrator as the in-process caller and hands back the management contract —
        /// the same seam <c>ManagementInProcessAccessTests.Publish</c> uses, because there is no second one.
        /// </summary>
        /// <param name="administrator">The user <see cref="StartAsync"/> was given as the bootstrap administrator.</param>
        internal IAlvoManagement AsBootstrapAdmin(Guid administrator)
        {
            var catalog = _app.Services.GetRequiredService<IRoleCatalogProvider>().DeclaredRoles!;
            _app.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
            {
                Context = new AlvoContext { User = new UserId(administrator), Roles = catalog.Resolve(["authenticated"]) },
                Scopes = new HashSet<ApiKeyScope>(),
                KeyId = "in-process",
            };

            return _app.Services.GetRequiredService<IAlvoManagement>();
        }

        /// <summary>A bootstrap administrator who is one fixed user.</summary>
        private sealed class OneBootstrapAdmin(UserId user) : IAlvoBootstrapAdmin
        {
            public bool IsBootstrapAdmin(UserId candidate) => candidate == user;
        }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration --filter-class '*EmbeddedSampleTests'`
Expected: FAIL — the sample does not start over the test-local descriptor: apply refuses `normalizeVin` as "not a recognized function", so `/health/ready` never turns green (or `StartAsync` throws); every other fact PASSES.

- [ ] **Step 3: Register the function** — in `SampleHost.CreateBuilder`, end the `AddAlvo` chain with the registration (after `.AddDataApi(api => { … })`, before the closing `);`):

```csharp
            .AddDataApi(api =>
            {
                // … unchanged …
            })

            // A function of this app's own, callable from the descriptor's hook conditions and mutate values
            // (docs/architecture/extensibility.md, "Registering a CEL function"). vehicles.alvo.json calls it nowhere,
            // on purpose: the standalone image serves that same file and knows built-in functions only — see README.md.
            .AddCelFunction("normalizeVin", NormalizeVin, NormalizeVinSummary));
```

and add to `SampleHost`:

```csharp
    /// <summary>What <c>normalizeVin</c> says about itself — the text <c>cel/functions</c> and the dashboard show.</summary>
    public const string NormalizeVinSummary =
        "Upper-cases a vehicle identification number and drops every character that is not a letter or a digit.";

    /// <summary>
    /// <c>normalizeVin</c>: pure, fast and thread-safe, as every CEL function must be — it runs inside the write's
    /// transaction, on any request thread, with no time budget and no cancellation.
    /// </summary>
    /// <param name="vin">The VIN as written.</param>
    /// <returns>The VIN upper-cased, with only ASCII letters and digits kept.</returns>
    private static string NormalizeVin(string vin) =>
        new string([.. vin.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);
```

(A method group converts to `Delegate` through its natural type `Func<string, string>`; if the compiler asks for it, write `(Func<string, string>)NormalizeVin`.) The class remarks' paragraph "Both serve `examples/vehicle-registry/vehicles.alvo.json`" gains one sentence: "The host also registers a CEL function, `normalizeVin`, which that shared descriptor deliberately never calls."

- [ ] **Step 4: The README** — after "What each part demonstrates", add a row to its table:

`| A function of your own | `.AddCelFunction("normalizeVin", NormalizeVin, NormalizeVinSummary)` at the end of the `AddAlvo` chain | `extensibility.md`, "Registering a CEL function" — pure, fast, thread-safe; listed by `cel/functions` as the host's |`

and a section before "One thing embedded cannot do yet":

````markdown
## Registering a CEL function

A host can give its descriptor's hooks a function of its own. This sample registers one at the end of its `AddAlvo`
chain:

```csharp
.AddCelFunction("normalizeVin", NormalizeVin, NormalizeVinSummary)
```

A descriptor then calls it from a hook condition or a before-hook mutate value:

```json
"hooks": { "beforeCreate": [
  { "action": { "mutate": { "vin": { "$cel": "normalizeVin(new.vin)" } } } } ] }
```

**`vehicles.alvo.json` does not carry that hook, on purpose.** It is the descriptor the standalone image serves too, and
the image knows only the built-in functions — a hook calling `normalizeVin` would make the image refuse the file, and
the two modes would stop serving the same backend. `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration` adds the hook
to a copy at run time and proves the function is listed as the host's and shapes a write.

What you promise, because Alvo cannot check it: the function is pure, fast and thread-safe. The rules, the parameter
types it may take and what a failure answers are in `docs/architecture/extensibility.md`, "Registering a CEL function".
````

- [ ] **Step 5: Run, normalise, run again**

Run: `dotnet test --project test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration --filter-class '*EmbeddedSampleTests'`, `git diff --exit-code examples/vehicle-registry/vehicles.alvo.json`, `scripts/test-ring0`.
Expected: PASS, every fact — `Both_modes_generate_the_same_routes` included (a function adds no route); the diff prints nothing.

- [ ] **Step 6: Commit**

```bash
git add samples/MMLib.Alvo.Samples.EmbeddedHost/SampleHost.cs samples/MMLib.Alvo.Samples.EmbeddedHost/README.md test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/EmbeddedSampleTests.cs
git commit -m "feat(samples): the embedded sample registers a CEL function a hook can call

The shared vehicles.alvo.json stays hook-free, because the standalone image
serves it and knows built-in functions only; the integration fact adds the
hook to a copy at run time.

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 17: docs, the skill's prose, and the reference drift fact

**Files:**
- Modify: `docs/architecture/cel.md` (construct table rows: legacy call, Arithmetic, Concatenation; "The legacy call"; the built-in reference replacing "The five built-ins"; a section on operators in hook slots; reserved names; deviation 8's note; deviations 25–36)
- Modify: `docs/architecture/extensibility.md` (new section "Registering a CEL function")
- Modify: `docs/architecture/management-api.md` (`cel/functions` row: "read by the hook editor")
- Modify: `docs/todo-admin.md` (§8a: a row "Hook functions — offered in the mutate expression and the condition text box" → edit; the guided-condition row names the three text tests)
- Modify: `.claude/skills/alvo-descriptor-hooks/SKILL.md` (prose: the truncation recipe, cents and prefix in one line; `cel-mutate` allowed examples)
- Modify: `docs/superpowers/specs/2026-10-05-f5-cel-functions-design.md` §16, `docs/superpowers/specs/2026-10-05-f5-hooks-editor-design.md` §16 (one line each: the follow-up is delivered by slice D, with the spec link) — no other edit to either
- Test: `test/MMLib.Alvo.Host.Tests/CelReferenceDocTests.cs`

**Interfaces:**
- Consumes: `CelFunctionCatalog.BuiltIns` (core internal, visible to Host.Tests).
- Produces: the documentation spec §18 AC 9 names.

- [ ] **Step 1: Write the failing drift fact** (`CelReferenceDocTests.cs`):

```csharp
using MMLib.Alvo.Expressions.Internal;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Host.Tests;

/// <summary>cel.md's built-in reference names exactly the catalog's built-ins (spec E15).</summary>
public sealed partial class CelReferenceDocTests
{
    [Fact]
    public void The_reference_table_lists_every_built_in_once()
    {
        var doc = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "architecture", "cel.md"));
        var section = doc[doc.IndexOf("#### Built-in functions", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n#### ", 5, StringComparison.Ordinal)];

        var named = Row().Matches(section).Select(match => match.Groups["name"].Value).ToList();

        named.ShouldBe(CelFunctionCatalog.BuiltIns.Names);
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [GeneratedRegex(@"^\| `(?<name>[a-zA-Z.]+)` \|", RegexOptions.Multiline)]
    private static partial Regex Row();
}
```

(If Host.Tests already has a repository-root helper — `SkillCoreClaimsTests` reads `.claude/skills` — use it instead of `CallerFilePath`.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*CelReferenceDocTests'`
Expected: FAIL — `cel.md` has no "#### Built-in functions" section.

- [ ] **Step 3: `cel.md`** — replace "#### The five built-ins" with "#### Built-in functions": one table, one row per **name** in ordinal order (`| `name` | signature(s) | semantics |`), the 19 names of spec §5 with spec §5's semantics column condensed; the truncation recipe under it; a sentence that SQL translation is slice C2. Rename "#### The two legacy calls" → "#### The legacy call" and keep only `now()`'s row and paragraphs; move the `lowerAscii` fold paragraph ("explicit A–Z loop … permanently wrong row") under the new table's `lowerAscii` row. Construct table: "Legacy call (`now()`)"; the Arithmetic row's profiles become Computed, Condition, Mutate and the Concatenation row's Computed, Mutate (spec §7). A new "#### Operators in hook slots" section after the built-in reference: spec §5.6's table and its four bullets, with the failure detail example (`The CEL function '_/_' failed: the divisor is zero. Nothing was written.`) and the guard recipe (`new.qty != 0 && new.total / new.qty > 100`). Host-functions limits table: the reserved set adds `math`, and a built-in name is refused as a built-in. Deviation 8 gains one sentence (D-15): "`CelNames` (Admin) relies on this: a name followed by `.` is never a column, so if JSON paths ever land, a rename would silently skip `meta.x` — revisit `CelNames.IsColumn` with them." Deviations: rewrite 17 to say "`trim` and `replace`" (the math names are conformant now) and add 25–36 = spec §16 F9–F20, same one-paragraph style as 17–24 (F12's paragraph carries Task 5 Step 3d's finding about cel-go's timestamp text).

- [ ] **Step 4: `extensibility.md`** — a new section "Registering a CEL function" (≈ 40 lines), for a host developer:

````markdown
## Registering a CEL function

An embedded host can give its descriptor's hooks a function of its own. Register it where you call `AddAlvo`:

```csharp
builder.Services.AddAlvo(alvo => alvo
    .UseSqlite(connectionString)
    .FromDescriptor("app.alvo.json")
    .AddCelFunction(
        "normalizeFrameNumber",
        (string value) => new string([.. value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]),
        "Upper-cases a frame number and drops every character that is not a letter or a digit."));
```

Then call it from a hook — a `condition`, or a before-hook `mutate` value:

```json
"hooks": { "beforeCreate": [
  { "action": { "mutate": { "frame_number": { "$cel": "normalizeFrameNumber(new.frame_number)" } } } } ] }
```

What the registration checks, at the call (an `ArgumentException`): the name (`^[a-z][a-zA-Z0-9_]*`, at most 64
characters, not a built-in or a reserved CEL word), at most four parameters of `string long int decimal bool
DateTimeOffset Guid` or their nullable forms, the same set for the result, no `Task`, `ref`, `out` or multicast delegate.

What you promise, because Alvo cannot check it: the function is **pure, fast and thread-safe**. It runs inside the
write's transaction, once per evaluation, on any request thread, with no time budget and no cancellation; it captures
singletons only. A function that reads stored data must filter by the tenant itself — Alvo's tenant filter does not
reach inside — so give it the tenant as an argument: in a `condition`, pass `@tenant.id`; in a `mutate`, pass
`new.tenant_id` (a column of a tenant-scoped row), because a mutate cannot read `@tenant` or `@user` (the refusal's
wording is tracked in #310). A function that throws refuses the write with `500 …/errors/function-failed`, naming the
function and never your exception text (that goes to the log).

Where it shows up: `GET {m}/projects/{p}/cel/functions` lists it with provenance `Host`; the dashboard's hook editor
offers it under the mutate value and condition boxes, marked "this host"; the assistant's `get_cel_functions` tool lists
it. It works in `condition` and `mutate` only — a rule or a computed field refuses it (store the value with a `mutate`,
then compare the field). The standalone image and `alvo validate` know only the built-ins, so a descriptor that calls
your function is portable to hosts that register it. A function whose meaning changes gets a new name.

A runnable registration is in the embedded sample: `samples/MMLib.Alvo.Samples.EmbeddedHost/SampleHost.cs` registers
`normalizeVin`, and its README says why the shared `vehicles.alvo.json` never calls it.

The built-in functions and their exact semantics are in [cel.md](cel.md#built-in-functions). A value derived from other
fields of the same row (a total, a full name) belongs in a **computed field**, which stays true on every write; a
`mutate` stamps a value once, when its hook runs.
````

- [ ] **Step 5: The rest** — `management-api.md`'s `cel/functions` section: one sentence, "The hook editor reads it to offer functions under a mutate value and a condition (slice D)". `todo-admin.md` §8a rows as listed in Files. The hooks skill: after the `mutate-functions` region add two sentences — "To fit a field's `maxLength`, cut: `substring(new.title, 0, math.least(size(new.title), 40))`. Arithmetic works in a condition and a mutate (a zero divisor or an overflow refuses the write; `7 / 2` is `3`), and `+` joins strings in a mutate: `math.round(new.price * 1.2, 2)`, `'+421' + new.phone`." — and in `<!-- gen:cel-mutate -->` *allowed* add `` `upperAscii(trim(new.description))` `` and `` `math.round(new.price * 1.2, 2)` `` (use a decimal field the skill's example entity declares; if it has none, use `` `'#' + upperAscii(new.description)` `` instead). cel.md's built-in reference, under the truncation recipe, gains the line "A value derived from other fields of the row (a total, a full name) belongs in a computed field, which stays true on every write; a mutate stamps a value once (D-17)." Check the size: `wc -c .claude/skills/alvo-descriptor-hooks/SKILL.md` ≤ 6,144. The two prior specs' §16: append "(delivered by slice D: [design](2026-10-06-f5-hook-functions-end-to-end-design.md))" to the "the dashboard offering functions (slice B)" item (C1) and to "functions in the guided table once Condition admits calls (slice C)" (B).

- [ ] **Step 6: Run**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*CelReferenceDocTests' --filter-class '*SkillCoreClaimsTests'`, `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*SkillConformanceTests' --filter-class '*SkillRegionTests' --filter-class '*AlvoAssistantTests'`, `scripts/check-brief-freshness` (no product doc changed, so it must stay fresh), `scripts/test-ring0`.
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add docs/architecture/cel.md docs/architecture/extensibility.md docs/architecture/management-api.md docs/todo-admin.md .claude/skills/alvo-descriptor-hooks/SKILL.md docs/superpowers/specs/2026-10-05-f5-cel-functions-design.md docs/superpowers/specs/2026-10-05-f5-hooks-editor-design.md test/MMLib.Alvo.Host.Tests/CelReferenceDocTests.cs
git commit -m "docs: register a CEL function from a host, and the built-in reference the catalog is held to

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

### Task 18: whole-slice verification

**Files:**
- Modify: `docs/superpowers/specs/2026-10-06-f5-hook-functions-end-to-end-design.md` §19 "As built"

- [ ] **Step 1: Sweeps that must print nothing**

```bash
grep -rnE "\babs\(|\bround\(" src .claude docs/architecture --include=*.cs --include=*.md | grep -v "math\.\(abs\|round\)("
grep -rn "CheckLowerAsciiCall\|ParseLowerAsciiCall\|FoldAsciiUpperCase" src
git diff --stat $(git merge-base HEAD feat/hooks-editor) -- 'test/**/PublicApi.*.verified.txt'
git diff --exit-code $(git merge-base HEAD feat/hooks-editor) -- examples/vehicle-registry/vehicles.alvo.json
git show feat/cel-functions:test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl > "$TMPDIR/c1-baseline.jsonl"
python3 - <<'PY'
import json, os
def rows(path):
    return [json.loads(line) for line in open(path, encoding='utf-8-sig') if line.strip()]
old = rows(os.path.join(os.environ['TMPDIR'], 'c1-baseline.jsonl'))
new = rows('test/MMLib.Alvo.Tests/Expressions/CelAcceptanceBaseline.jsonl')
gates = ("Arithmetic is legal only in the Computed profile",
         "Arithmetic negation ('-') is legal only in the Computed profile",
         "String concatenation ('+' over two strings) is legal only in the Computed profile")
assert len(old) == len(new), (len(old), len(new))
moved = [(o, n) for o, n in zip(old, new) if o != n]
unexplained = [o['Profile'] + ': ' + o['Source'] for o, n in moved
               if 'lowerAscii' not in o['Source'] and not any(e['Message'].startswith(gates) for e in o['Errors'])]
print(len(moved), 'moved;', len(unexplained), 'unexplained')
print(*unexplained, sep='\n')
PY
```

The `PublicApi` diff must print nothing **beyond what B and C1 already moved** (compare against each branch's own tip with `git diff feat/hooks-editor -- …` and `git diff feat/cel-functions -- …`: D adds no line to any baseline); the `vehicles.alvo.json` diff must print nothing; the corpus check must print `0 unexplained` (the moved count is Task 2's rows + 272 at most, and goes into §19).

- [ ] **Step 2: Rings and builds**

```bash
scripts/test-ring0 && scripts/test-ring1 && scripts/test-ring2
dotnet build -c Release
```

Expected: all green, 0 warnings in Release. If the standalone image's code moved (it did not by design), also `docker build -f src/MMLib.Alvo.Host/Dockerfile .`.

- [ ] **Step 3: The whole admin e2e suite, and the outside-ring gates**

```bash
scripts/test-admin-e2e
scripts/gen-prototype-fixtures --check
scripts/check-brief-freshness
scripts/test-hooks
```

Also `dotnet test --project test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration` (whole, if ring2's affected scope did not include it).

Expected: PASS. `gen-prototype-fixtures --check` stays green because no Management route or capability moved; if it does not, regenerate with `scripts/gen-prototype-fixtures` and say why in the commit.

- [ ] **Step 4: The assistant eval** — if an AI connection is configured (`ALVO_DEMO_AI_*` or `Alvo__Ai__*`), run `scripts/eval-assistant` and publish its table per model in `docs/assistant-evals.md`; otherwise record "not run: no model configured" in §19 with the reason it matters (the skill's names and examples changed; spec E12).

- [ ] **Step 5: As built** — write spec §19: `git log --oneline --first-parent $(git merge-base HEAD feat/hooks-editor)..HEAD` (minus the C1 merge), every deviation from this plan with its ruling, the measured numbers (corpus rows moved per regeneration — Task 2, Task 7, Task 8 — conformance count before/after, overload count 32, skill bytes, e2e duration of the new classes), and what was not run.

- [ ] **Step 6: Before the PR** (Hard rules, `CLAUDE.md`): dispatch `alvo-plan-guard`; run the `alvo-security-core-review` checklist (the type checker's profile table, the apply-time constant check, and the interpreter's failure path changed: Tasks 6, 7, 8 — check above all that the fail-closed flag is read from `CompiledExpression.Profile` in one place and that Rule and Computed answer exactly what they did) and a reviewer subagent labelled as the `/code-review medium` + `/security-review` substitute (they are user-only); then `alvo-pr-report`. Fix findings before opening the PR. The PR targets `feat/hooks-editor` while B is unmerged, otherwise `main`; it carries `needs-deep-review`.

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-10-06-f5-hook-functions-end-to-end-design.md
git commit -m "docs(f5): the hook functions slice as built

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Self-review

- **Spec coverage:** §4 E1 → Tasks 1–5 (+ 6's catalog facts); E2 → 1; E3 → 1 (fixes only); E4 → 2; E5 → 6; E6 → 7 (arithmetic), 8 (concatenation), 9 (over HTTP); E7 → nothing built, recorded in spec §12; E8 → 10, 12, 13; E9 → 14; E10/E11 → 15; E12 → 1–5 (region), 17 (prose), 18 (eval); E13 → 10; E14 → 1; E15 → 17; E16 → 16; E17 → 4, 6; E18 → 5; E19 → nothing to build (Q7 for the maintainer). §11.2's scenarios → Tasks 12 (mutate and condition insert, AC 4), 13 (phone, absence, refusal), 14 (guided), 15 (host function, markup, built-in condition); §11.3 → 16. §18 AC 1 → 6; 2 → 2, 7, 8, 18; 3 → 5, 6; 3a → 7, 9; 4 → 12; 5 → 13; 6 → 15; 7 → 14; 8 → 18; 9 → 17; 10 → 16. Rulings D-1…D-17 (spec §20): D-1/D-8 are spec text and Q6/Q7; D-2 → 3; D-3 → 16; D-4 → 17; D-5 → 4, 6; D-6 → 8; D-7 → 7; D-9 → 5; D-10 → 12, 13, 15; D-11 → 12 (CSS), 13; D-12 → every Files line; D-13 → 12/13; D-14 → 5; D-15, D-17 → 17; D-16 → 12.
- **Order:** the skill region follows the catalog in Tasks 1–5, so ring0 is green after each; Task 3's `math.least` fact is skipped until Task 4; `math.round(x, digits)` lands in Task 4 and its apply-time check in Task 6 (Task 6 needs Task 5's `Bind`). Task 7's money fact needs Task 4's `digits` overload; Task 8 needs Task 7's flag (a string and a null fall to the hook arithmetic, which answers `null`). The three corpus regenerations are disjoint by rule except the 48 rows Tasks 7 and 8 both touch, each time in a different message. Task 12 needs B Tasks 8, 9, 19 on the branch (Task 0's prerequisite) and creates `RecordingWorld`, which Tasks 13 and 15 derive from. Task 16 is independent of the dashboard and lands before the docs, which link it.
- **Types:** `OfferedFunction`, `Insertion` are used with the same members in Tasks 10 and 12; `CelBuiltInFunctions` becomes `partial` in Task 3 before Tasks 4–5 add files; `CelFunction.ConstantCheck` is declared in Task 6 and set only on `RoundToDigits`; `CelHookArithmetic` is created in Task 7 and used by `CelInterpreter` alone; `ConditionOperator` members added in Task 14 are referenced only there; `RecordingWorld.ListFunctionsAsync` is overridden only by `FunctionsRefusedWorld`.
- **Public API:** no task adds a public symbol in a shipped package (the sample's `NormalizeVinSummary` is in a non-packable sample); Tasks 10, 12, 18 check the baselines.
- **Size:** each task is one implementer's sitting; the largest are Task 7 (one class, one flag, one corpus regeneration) and Task 12 (the UI, with the layout and failure scenarios moved to Task 13).
