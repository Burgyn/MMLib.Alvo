# `access` enforcement: the fifth CEL profile, and the management gate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the descriptor's `access` block do what its name promises — a fifth `CelProfile`
(`Access`) that compiles the three levels at apply with their role literals validated against
`auth.roles`, a gate that maps every Management API operation onto a required level and answers
`403` for a caller who matches none, and the removal of `access` from `UnhonouredSubsystems.All`,
which that file's own doc comment demanded the day the surface landed.

**Architecture:** Three layers, each already having a place to live. **Compile:** `CelProfile`
gains an `Access` member and `CelTypeChecker._allowedProfiles` gains a fifth column — deny by
default, so a construct missing from the table compiles in no profile rather than every profile;
`PolicyCatalogBuilder` compiles the three levels in the same pass as every rule, against the same
`RoleCatalog`, so an undeclared role literal is refused at apply exactly as a rule's already is.
**Hold:** the compiled levels ride on `PolicyCatalog` beside the entity policies, so one apply
primes one holder and the levels can never come from a different descriptor revision than the
rules. **Enforce:** a vertical slice at `src/MMLib.Alvo/Management/Access/` turns an
`AlvoContext` into a `ManagementLevel` — all three predicates evaluated, highest match wins,
bootstrap admin above all of them — and an endpoint filter #212 attaches to each route.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core minimal APIs, Microsoft.Testing.Platform (MTP),
xUnit v3, **Shouldly** (FluentAssertions is banned), NSubstitute, Verify (snapshots),
`PublicApiGenerator` (public-API approval), NetArchTest, Central Package Management.

**Spec:** `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` — §3.1 (the frozen
schema already settled the role-based question), §3.2 (the fifth profile and its truth table),
§3.3 (what a level governs), §3.5 (the bootstrap admin bypasses `access`), §3.6 (the
warning-to-enforcement transition), §6.1 rows *"`access` is actually enforced"* and *"The `Access`
profile is closed"*. Issue: **#146**. Depends on:
`docs/superpowers/plans/2026-09-18-f5-identity.md` (**#248**).

## Global Constraints

- **This plan changes the security core** — CEL compilation and authorization. It must carry the
  `needs-deep-review` label, run the `alvo-security-core-review` checklist, and pair it with a
  `/security-review` pass (or a dispatched reviewer subagent labelled plainly as its substitute)
  **before** the PR opens, not after.
- **Depends on Plan 1.** `IAlvoBootstrapAdmin.IsBootstrapAdmin(UserId user)` must already exist in
  `MMLib.Alvo.Abstractions`, with the core's `NoBootstrapAdmin` default registered. Do not start
  Task 3 until it does.
- **Never merge or push to `main`.** Branch → PR → a human merges.
- **Interface-first, TDD throughout.** Every task writes the failing test, runs it, sees the exact
  failure, then implements the minimum.
- **`docs/architecture/cel.md`'s truth table is the single positive list.** The doc and
  `_allowedProfiles` are one statement in two places; a change to either without the other is a
  defect. Task 7 moves the doc, and Task 1's facts are what make the doc true.
- **Deny by default.** A construct kind missing from `_allowedProfiles` compiles in **no** profile.
  Never add a profile to a row without a fact that exercises it there — that is the rule the
  `Mutate` column already follows, stated in its own remarks.
- **Central Package Management.** No new dependency is needed for this plan; if one appears, its
  version goes in `Directory.Packages.props` and never inline.
- **No FluentAssertions.** **Shouldly** for every assertion.
- **`public` is the contract.** Everything this plan adds is `internal` except the
  `CelProfile.Access` enum member, which must be public because `CelProfile` is. Use the existing
  `InternalsVisibleTo("MMLib.Alvo.Tests")` grant rather than widening anything else. #212 makes
  the management types public on the day it needs them.
- **Never hand-edit a `*.verified.*` baseline.** Let Verify write the `.received.` file and accept
  it with the repo's usual mechanism; the Stop hook then requires `alvo-snapshot-judge`.
- **`.gitattributes` pins `*.cs` to CRLF + UTF-8 BOM.** Create and edit `.cs` files with the
  editing tools, not a shell heredoc or a Python script; if one was written from a shell, run
  `dotnet format MMLib.Alvo.slnx --no-restore` and confirm with `git diff --stat` before
  committing, or the pre-commit `dotnet format` check fails on line endings.
- **Short, single-purpose methods** — ~25-line ceiling, extract aggressively. **Zero inline
  comments explaining what a line does**; rename or extract instead.
- **Running one test class — the MTP invocation.** `dotnet test <proj> --filter X` does **not**
  work on this stack. Use
  `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*SomeTests*'`.
- **Assert `Build succeeded` before reading any test result.**
- **Rings:** `scripts/test-ring0` after every task, `scripts/test-ring2` before the PR.
  `scripts/test-e2e` is required by Task 6 (an example descriptor changes and the API invariant
  suite boots the examples).
- **Commit after every task**, Conventional Commits subject.

## File Structure

**New — `src/MMLib.Alvo` (core)**

| File | Responsibility |
|---|---|
| `Rules/ManagementAccessCatalog.cs` | The three compiled levels, or `null` each. Internal; rides on `PolicyCatalog`. |
| `Rules/Internal/AccessCatalogBuilder.cs` | Compiles the three levels in `PolicyCatalogBuilder`'s pass, validating role literals against the same `RoleCatalog`. Internal. |
| `Management/Access/ManagementLevel.cs` | `None`/`Viewer`/`Developer`/`Admin`, explicitly numbered so `Max()` means "highest". Internal. |
| `Management/Access/ManagementOperation.cs` | Every operation the Management API surface (§2.2) exposes. Internal. |
| `Management/Access/ManagementOperations.cs` | The one operation→level table, and the lookup. Internal. |
| `Management/Access/Internal/ManagementAccessEvaluator.cs` | `AlvoContext` → `ManagementLevel`, and `Allows(operation, context)`. Internal. |
| `Management/Access/Internal/ManagementAccessEndpointFilter.cs` | The minimal-API filter #212 attaches; `403` with the published `forbidden` slug. Internal. |
| `Management/Access/ManagementAccessRouteBuilderExtensions.cs` | `RequireAlvoManagementAccess(this RouteHandlerBuilder, ManagementOperation)`. Internal. |
| `Management/Setup.cs` | `AddAlvoManagement()` — registers the evaluator. Internal. |

**Modified**

| File | Change |
|---|---|
| `src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs` | The `Access` member, appended so existing values stay stable. |
| `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs` | Split `ContextRef` into user/tenant kinds; the fifth column; the "no field references at all" early return; the profile-aware refusal wordings. |
| `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs` | `Access` must evaluate to `Bool`. |
| `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs` | `RefuseMutate` generalised to `RefuseInterpreterOnlyProfile`. |
| `src/MMLib.Alvo/Rules/PolicyCatalog.cs` | `internal ManagementAccessCatalog ManagementAccess { get; }`. |
| `src/MMLib.Alvo/Rules/Internal/PolicyCatalogBuilder.cs` | Call `AccessCatalogBuilder` in `TryBuild`. |
| `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` | `ManagementForbidden()`. |
| `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` | `AddAlvoManagement()`. |
| `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs` | The `access` entry leaves; the doc comment stops describing a warning that no longer exists. |
| `test/MMLib.Alvo.Tests/Descriptor/UnhonouredSubsystemsTests.cs` | Five names, not six; the `access` fact goes; the `branding` fact's reason is rewritten. |
| `examples/complex-crm/crm.alvo.json` | The `access` block says one honest thing. |
| `examples/complex-crm/NOT-RUNNABLE.md` | Five warned blocks; the `access` section rewritten. |
| `docs/architecture/cel.md` | The fifth column, the profile's own section, and deviation 1's closing paragraph. |

---

### Task 1: The fifth column — a profile that is closed by construction

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/AccessProfileTests.cs`

**Interfaces:**
- Consumes: `ICelCompiler.Compile(string source, CelProfile profile, EntitySchema entity)`,
  `CelCompilationResult` (`IsSuccess`, `Expression`, `Errors`), `CelCompilationError`
  (`Message`, `FixSuggestion`, `Position`), `EntitySchema` (`Name`, `Fields`).
- Produces:
  ```csharp
  public enum CelProfile { Rule, Computed, Condition, Mutate, Access }
  ```
  and, inside `CelTypeChecker`, the construct kinds `ContextRefUser` / `ContextRefTenant`
  replacing `ContextRef`.

> **Why a fifth profile rather than reusing `Rule`.** §3.2: `Rule` sees the current row **and**
> `@user`, so `"owner_id == @user.id"` would compile under `Rule` and then have nothing to evaluate
> against — the silent failure `_allowedProfiles` exists to prevent. `@tenant` is excluded
> deliberately: `access` is **project-scoped** by its own schema description, and admitting
> `@tenant` would make a project-level predicate answer differently per request.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Expressions/AccessProfileTests.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The <see cref="CelProfile.Access"/> profile, one fact per construct.
/// </summary>
/// <remarks>
/// <b>The point is that the profile is closed, not that these particular eight constructs were
/// thought of.</b> <c>docs/architecture/cel.md</c> states the rule the whole table rests on: a
/// construct kind missing from <c>_allowedProfiles</c> compiles in <em>no</em> profile rather than in
/// every profile. So each refusal below is one cell of that table asserted from outside it, and a
/// widening — a later PR adding <see cref="CelProfile.Access"/> to a row — fails exactly the fact
/// that named the cell.
/// </remarks>
public class AccessProfileTests
{
    private static readonly EntitySchema _project = new() { Name = "<project>", Fields = [] };

    private static readonly EntitySchema _deals = new()
    {
        Name = "deals",
        Fields =
        [
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid },
            new FieldSchema { Name = "amount", Type = FieldType.Integer },
            new FieldSchema { Name = "stage", Type = FieldType.String },
        ],
    };

    // ---- what the profile admits -------------------------------------------------------------

    /// <summary>
    /// Every construct the fifth column admits.
    /// </summary>
    /// <remarks>
    /// <b>The two equality cases compare literals, and that is the whole of what equality can do here
    /// today</b> — <c>@user.id</c> has no <c>Uuid</c>-typed operand to meet (see
    /// <see cref="A_level_cannot_yet_compare_the_callers_id_to_a_literal"/>) and there is no row. It is
    /// admitted anyway, deliberately: the frozen schema promises a level may read <c>@user.id</c>, and
    /// widening <c>@user</c> is additive, so admitting the operator now is what makes a level written
    /// against a future typed claim compile without the table changing. Deny-by-default costs nothing
    /// here because the operator can express nothing a role membership could not.
    /// </remarks>
    [Theory]
    [InlineData("true")]
    [InlineData("'manager' in @user.roles")]
    [InlineData("!('guest' in @user.roles)")]
    [InlineData("'manager' in @user.roles && 'finance' in @user.roles")]
    [InlineData("'sales' in @user.roles || 'manager' in @user.roles")]
    [InlineData("true == true")]
    [InlineData("true != false")]
    public void The_profile_admits_the_constructs_a_level_is_written_from(string source)
    {
        var result = Compile(source, _project);

        result.IsSuccess.ShouldBeTrue(Report(result));
        result.Expression.ShouldNotBeNull().ResultType.ShouldBe(CelValueType.Bool);
    }

    /// <summary>
    /// <b><c>@user.id</c> is admitted by the profile and has nothing to compare against — a grammar gap,
    /// not a profile refusal, and the distinction is the point.</b> The frozen schema names
    /// <c>@user.id</c> as one of the two members a level may read, so the table admits it; but Alvo's
    /// grammar has no <c>uuid</c> literal (deviation 13 limits numeric literals to decimal digit runs and
    /// a quoted value is always <c>String</c>), and an access level sees no row, so there is no
    /// <c>Uuid</c>-typed operand in scope to compare it to. The refusal an author gets therefore comes
    /// from the type checker's comparison rule, not from the profile table, and it names both types.
    /// </summary>
    /// <remarks>
    /// Recorded as a fact rather than as a comment because the fix is additive — a <c>uuid</c> literal, or
    /// #37's typed claims — and on the day either lands this fact fails, which is exactly when the
    /// limitation should be re-read. Refusing <c>@user.id</c> in the profile instead would have been the
    /// wrong closure: the schema promises it, and a level would then be refused for naming something the
    /// schema says it may name.
    /// </remarks>
    [Fact]
    public void A_level_cannot_yet_compare_the_callers_id_to_a_literal()
    {
        var result = Compile("@user.id == '00000000-0000-0000-0000-000000000001'", _project);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(
            message => message.Contains("Cannot compare Uuid to String", StringComparison.Ordinal),
            "the refusal must come from the comparison rule, naming both types — not from the profile "
            + "table, which admits @user.id because the frozen schema names it");
    }

    // ---- what the profile refuses, one fact per construct ------------------------------------

    [Fact]
    public void A_current_row_field_reference_does_not_compile()
    {
        var result = Compile("owner_id == @user.id", _deals);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(
            message => message.Contains("field reference", StringComparison.OrdinalIgnoreCase),
            "an access level has no row to read, so a field reference would compile and then have "
            + "nothing to evaluate against — the silent failure the profile table exists to prevent");
    }

    [Fact]
    public void An_old_or_new_qualified_field_reference_does_not_compile()
    {
        Compile("new.stage == 'won'", _deals).IsSuccess.ShouldBeFalse();
        Compile("old.stage == 'won'", _deals).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Has_does_not_compile()
    {
        var result = Compile("has(owner_id)", _deals);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(message => message.Contains("has(", StringComparison.Ordinal));
    }

    [Fact]
    public void Changed_does_not_compile()
    {
        var result = Compile("changed(stage)", _deals);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(message => message.Contains("changed(", StringComparison.Ordinal));
    }

    [Fact]
    public void Arithmetic_does_not_compile()
    {
        Compile("1 + 1 == 2", _project).IsSuccess.ShouldBeFalse();
        Compile("-1 == -1", _project).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void The_ternary_does_not_compile()
    {
        var result = Compile("'manager' in @user.roles ? true : false", _project);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(message => message.Contains("ternary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_allow_listed_function_call_does_not_compile()
    {
        Compile("lowerAscii('X') == 'x'", _project).IsSuccess.ShouldBeFalse();
        Compile("now() == now()", _project).IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// <b><c>@tenant</c> is the one refusal that is a decision rather than an absence.</b> An access
    /// level is project-scoped by the frozen schema's own description, so admitting <c>@tenant</c>
    /// would make a project-level predicate answer differently per request — which is neither what the
    /// block says nor something an operator could reason about.
    /// </summary>
    [Fact]
    public void A_tenant_context_reference_does_not_compile()
    {
        var result = Compile("@tenant.id == @user.id", _project);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(
            message => message.Contains("project-scoped", StringComparison.Ordinal),
            "the refusal has to say why, or an author reads it as an oversight and files an issue");
    }

    [Fact]
    public void A_level_that_is_not_a_predicate_does_not_compile()
    {
        var result = Compile("'manager'", _project);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(message => message.Contains("boolean", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The profile is interpreter-only, exactly as <see cref="CelProfile.Mutate"/> is, and the renderer
    /// refuses it <b>by profile</b> rather than by node kind — every construct an access level can
    /// contain is one the renderer would otherwise render perfectly well, into SQL over a row that does
    /// not exist.
    /// </summary>
    [Fact]
    public void The_sql_renderer_refuses_an_access_expression_by_name()
    {
        var expression = Compile("'manager' in @user.roles", _project).Expression.ShouldNotBeNull();
        var renderer = new SqlPredicateRenderer();

        var refusal = Should.Throw<NotSupportedException>(
            () => renderer.Render(expression, AlvoContext.Anonymous, new SqliteFieldSqlRendererStub()));

        refusal.Message.ShouldContain(nameof(CelProfile.Access));
    }

    private static CelCompilationResult Compile(string source, EntitySchema entity) =>
        new CelCompiler().Compile(source, CelProfile.Access, entity);

    private static IReadOnlyList<string> Messages(CelCompilationResult result) =>
        [.. result.Errors.Select(error => error.Message)];

    private static string Report(CelCompilationResult result) =>
        string.Join(" | ", Messages(result));
}
```

> `SqliteFieldSqlRendererStub` is whatever `IFieldSqlRenderer` fake this suite already uses for
> renderer facts (the T-SQL seam suite has one, and the core suite has one beside
> `SqlPredicateRendererTests`). **Read `test/MMLib.Alvo.Tests/Expressions/` first and reuse the
> existing fake by its real name** rather than adding a second one; a second field renderer is how
> two renderer suites come to disagree about the dialect they describe.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AccessProfileTests*'`
Expected: FAIL at build — `error CS0117: 'CelProfile' does not contain a definition for 'Access'`.

- [ ] **Step 3: Add the enum member**

In `src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs`, append after `Mutate` (appending keeps
every existing numeric value stable, which matters because the value is persisted nowhere but is
compared in snapshots):

```csharp
    /// <summary>
    /// A management-access level: one of the descriptor's <c>access.admin</c> /
    /// <c>access.developer</c> / <c>access.viewer</c> predicates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It has no row, and that is why it is a profile of its own rather than
    /// <see cref="Rule"/>.</b> <see cref="Rule"/> sees the current row <em>and</em> <c>@user</c>, so an
    /// access level written as <c>owner_id == @user.id</c> would compile under it and then have nothing
    /// to evaluate against. The table refuses every row-shaped construct here instead: field references
    /// of either state, <c>has(...)</c> and <c>changed(...)</c>.
    /// </para>
    /// <para>
    /// <b><c>@tenant</c> is excluded deliberately.</b> An access level is <em>project</em>-scoped by the
    /// frozen schema's own description, so admitting <c>@tenant</c> would make a project-level predicate
    /// answer differently per request — neither what the block says nor something an operator could
    /// reason about. <c>@user.id</c> and <c>@user.roles</c> are the whole of the context it sees, which
    /// is the closed set the schema already promised, and widening <c>@user</c> (#37) is additive, so a
    /// role-based level keeps compiling once typed claims land.
    /// </para>
    /// <para>
    /// <b>Interpreter-only</b>, for <see cref="Mutate"/>'s reason one subsystem over: there is no row,
    /// so there is nothing to push a predicate into. <c>SqlPredicateRenderer</c> refuses this profile by
    /// name rather than falling through, because — unlike <see cref="Mutate"/> — every construct an
    /// access level can contain is one it would otherwise render perfectly well.
    /// </para>
    /// </remarks>
    Access,
```

- [ ] **Step 4: Split the context-reference kind and add the fifth column**

In `src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs`:

1. In `CelConstructKind`, replace `ContextRef` with two members:

```csharp
        ContextRefUser,
        ContextRefTenant,
```

2. Replace the profile-set fields. `_everyProfile` keeps its name and its meaning — literally every
   profile — and a new `_rowProfiles` carries the four that see a row:

```csharp
    private static readonly IReadOnlySet<CelProfile> _everyProfile = new HashSet<CelProfile>
    {
        CelProfile.Rule, CelProfile.Computed, CelProfile.Condition, CelProfile.Mutate, CelProfile.Access,
    };

    /// <summary>
    /// The four profiles evaluated against a row. <see cref="CelProfile.Access"/> is deliberately absent:
    /// an access level is a predicate over the caller alone, so a field reference there would compile and
    /// then have nothing to read.
    /// </summary>
    private static readonly IReadOnlySet<CelProfile> _rowProfiles = new HashSet<CelProfile>
    {
        CelProfile.Rule, CelProfile.Computed, CelProfile.Condition, CelProfile.Mutate,
    };

    private static readonly IReadOnlySet<CelProfile> _ruleComputedCondition =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Computed, CelProfile.Condition };

    private static readonly IReadOnlySet<CelProfile> _ruleComputedConditionAndAccess =
        new HashSet<CelProfile>
        {
            CelProfile.Rule, CelProfile.Computed, CelProfile.Condition, CelProfile.Access,
        };

    private static readonly IReadOnlySet<CelProfile> _ruleAndCondition =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Condition };

    private static readonly IReadOnlySet<CelProfile> _ruleConditionAndAccess =
        new HashSet<CelProfile> { CelProfile.Rule, CelProfile.Condition, CelProfile.Access };
```

   Leave `_computedOnly`, `_conditionOnly`, `_mutateOnly` and `_conditionAndMutate` unchanged.

3. Replace the table:

```csharp
    private static readonly Dictionary<CelConstructKind, IReadOnlySet<CelProfile>> _allowedProfiles =
        new()
        {
            [CelConstructKind.Literal] = _everyProfile,
            [CelConstructKind.FieldRefCurrent] = _rowProfiles,
            [CelConstructKind.FieldRefPastFuture] = _conditionAndMutate,
            [CelConstructKind.ContextRefUser] = _ruleConditionAndAccess,
            [CelConstructKind.ContextRefTenant] = _ruleAndCondition,
            [CelConstructKind.Logical] = _ruleComputedConditionAndAccess,
            [CelConstructKind.Comparison] = _ruleComputedConditionAndAccess,
            [CelConstructKind.In] = _ruleConditionAndAccess,
            [CelConstructKind.Has] = _ruleComputedCondition,
            [CelConstructKind.Arithmetic] = _computedOnly,
            [CelConstructKind.Conditional] = _computedOnly,
            [CelConstructKind.Changed] = _conditionOnly,
            [CelConstructKind.Call] = _mutateOnly,
        };
```

- [ ] **Step 5: Make the refusal wordings profile-aware, and stop resolving fields with no entity**

Still in `CelTypeChecker.Visitor`. Add two members beside the existing constants:

```csharp
        private const string AccessNoRowMessage =
            "An access level is a predicate over the caller alone; a field reference has no row to read here.";

        private const string AccessProjectScopedMessage =
            "'@tenant.id' is not legal in an access level: an access level is project-scoped, not tenant-scoped.";

        /// <summary>
        /// Whether this profile admits a field reference in <em>any</em> state. Only
        /// <see cref="CelProfile.Access"/> admits none, and that is what lets the field-reference and
        /// <c>changed(...)</c> arms stop before resolving a name against an entity there is none of —
        /// which would otherwise add a second, misleading "not a field of entity '&lt;project&gt;'" error
        /// to every access level that names a column.
        /// </summary>
        private bool ProfileReadsARow =>
            IsAllowed(profile, CelConstructKind.FieldRefCurrent)
            || IsAllowed(profile, CelConstructKind.FieldRefPastFuture);
```

Rewrite `CheckFieldRef`'s opening so the refusal names the right thing and returns early when the
profile reads no row at all:

```csharp
        private (CelNode, CelValueType, bool, int) CheckFieldRef(CelFieldRef fieldRef)
        {
            var position = FindPosition(fieldRef.FieldName);
            var kind = fieldRef.State == CelRecordState.Current
                ? CelConstructKind.FieldRefCurrent
                : CelConstructKind.FieldRefPastFuture;
            var stateBad = CheckConstruct(kind, FieldRefRefusal(fieldRef), FieldRefFix(), position);

            if (!ProfileReadsARow)
            {
                return (fieldRef, CelValueType.Null, true, position);
            }

            var field = ResolveField(fieldRef.FieldName);
            // …the rest of the method is unchanged…
        }

        private string FieldRefRefusal(CelFieldRef fieldRef) =>
            ProfileReadsARow
                ? $"'{StatePrefix(fieldRef.State)}{fieldRef.FieldName}' is legal only in the "
                    + $"{CelProfile.Condition} and {CelProfile.Mutate} profiles (a hook condition and a "
                    + "before-hook mutate value) — the two slots evaluated against a candidate row."
                : AccessNoRowMessage;

        private string FieldRefFix() =>
            ProfileReadsARow
                ? "Reference the current row instead, or move this into a hook condition or a before-hook mutate."
                : "Test the caller instead, e.g. 'manager' in @user.roles, or @user.id == '<uuid>'.";
```

Rewrite `CheckContextRef` to pick the kind and the wording:

```csharp
        private (CelNode, CelValueType, bool, int) CheckContextRef(CelContextRef contextRef)
        {
            var position = FindPosition(ContextRefText(contextRef));
            var kind = contextRef.Value == CelContextValue.TenantId
                ? CelConstructKind.ContextRefTenant
                : CelConstructKind.ContextRefUser;
            var profileBad = CheckConstruct(kind, ContextRefRefusal(), ContextRefFix(), position);

            return (contextRef, contextRef.Type, profileBad, position);
        }

        private string ContextRefRefusal() =>
            profile == CelProfile.Access ? AccessProjectScopedMessage : ComputedNoContextMessage;

        private string ContextRefFix() =>
            profile == CelProfile.Access
                ? "Test role membership or the caller's identity instead, e.g. 'manager' in @user.roles."
                : "Move the caller-dependent check into a rule or a hook condition.";
```

And guard `CheckChanged`'s field resolution with the same predicate — one condition, so only
`Access` takes the new branch:

```csharp
            if (!ProfileReadsARow || ResolveField(changed.FieldName) is not null)
            {
                return (changed, CelValueType.Bool, profileBad, position);
            }
```

- [ ] **Step 6: Require a boolean result, and refuse the profile at the renderer**

In `src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs`, extend the predicate arm of
`ValidateResultType`:

```csharp
        if (IsPredicateProfile(profile) && resultType != CelValueType.Bool)
        {
            return new CelCompilationError(
                $"A {profile} expression must evaluate to a boolean; this expression evaluates to {resultType}.",
                "Add a comparison, e.g. field == value, so the expression yields true/false.",
                position);
        }
```

with

```csharp
    /// <summary>
    /// The profiles whose whole expression is a verdict rather than a value. <see cref="CelProfile.Access"/>
    /// joins them: an access level that is not a predicate can never answer "may this caller manage the
    /// project", and a bare string there would otherwise be accepted and then never match.
    /// </summary>
    private static bool IsPredicateProfile(CelProfile profile) => profile is
        CelProfile.Rule or CelProfile.Condition or CelProfile.Access;
```

In `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs`, rename `RefuseMutate` to
`RefuseInterpreterOnlyProfile` and update its two call sites — `RequirePredicateProfile` (the
`Render(expression, context, fields)` entry point) and `RequireScalarProfile` (the
`Render(expression, fields)` one). Both must keep calling it, for the reason its existing remarks
give: the scalar guard's own message sends a caller to the predicate entry point, which would then
refuse them. Widen the body:

```csharp
    private static void RefuseInterpreterOnlyProfile(CompiledExpression expression)
    {
        if (expression.Profile == CelProfile.Mutate)
        {
            throw new NotSupportedException(
                $"'{expression.Source}' was compiled for the {CelProfile.Mutate} profile, which is evaluated "
                + "by the in-memory interpreter inside the write transaction and is never rendered to SQL. "
                + "Rendering it would bring the two-valued null fold and the string-collation caveat back "
                + $"into scope, and '{CelCall.Now}()' would answer with the engine's own clock — "
                + "PostgreSQL's transaction-start time, SQLite's second-precision text — instead of the "
                + "instant the write bound once.");
        }

        if (expression.Profile != CelProfile.Access)
        {
            return;
        }

        throw new NotSupportedException(
            $"'{expression.Source}' was compiled for the {CelProfile.Access} profile, which is a predicate "
            + "over the caller alone and is evaluated in memory. There is no row to push it into, so "
            + "rendering it would produce a WHERE clause over a table an access level never names.");
    }
```

- [ ] **Step 7: Run to verify they pass, and that nothing else moved**

```bash
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AccessProfileTests*'
dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Expressions'
```
Expected: PASS for both. The second is the one that matters: the four existing profiles' facts —
including every refusal wording they assert — must be untouched, because `_rowProfiles` and
`_ruleComputedCondition` reproduce exactly what those rows held before.

- [ ] **Step 8: Accept the public-API baseline and ring0 + commit**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*PublicApi*'`
Expected: FAIL once — one added enum member. Accept via Verify, re-run, PASS, dispatch
`alvo-snapshot-judge`.

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs \
        src/MMLib.Alvo/Expressions/Internal/CelTypeChecker.cs \
        src/MMLib.Alvo/Expressions/Internal/CelCompiler.cs \
        src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs \
        test/MMLib.Alvo.Tests/Expressions/AccessProfileTests.cs \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(access): add the Access CEL profile, closed to every row-shaped construct"
```

---

### Task 2: Compile the three levels at apply, with their role literals validated

**Files:**
- Create: `src/MMLib.Alvo/Rules/ManagementAccessCatalog.cs`
- Create: `src/MMLib.Alvo/Rules/Internal/AccessCatalogBuilder.cs`
- Modify: `src/MMLib.Alvo/Rules/PolicyCatalog.cs`
- Modify: `src/MMLib.Alvo/Rules/Internal/PolicyCatalogBuilder.cs`
- Test: `test/MMLib.Alvo.Tests/Rules/AccessCatalogBuilderTests.cs`

**Interfaces:**
- Consumes: `CelProfile.Access` (Task 1); `AlvoDescriptor.Access` (`Access` record with
  `Admin`/`Developer`/`Viewer`, each `string?`); `RoleCatalog`; `ICelCompiler`;
  `DescriptorValidationError(string path, string message, string? fix, DescriptorValidationSeverity severity)`;
  `PolicyCatalogBuilder.RoleLiterals`/`UndeclaredRoleError` (existing private members — lift them
  to `internal static` on `PolicyCatalogBuilder` so `AccessCatalogBuilder` reuses them instead of
  owning a second walk).
- Produces:
  ```csharp
  namespace MMLib.Alvo.Rules;

  internal sealed record ManagementAccessCatalog(
      CompiledExpression? Admin, CompiledExpression? Developer, CompiledExpression? Viewer)
  {
      internal static ManagementAccessCatalog Empty { get; }
      internal bool IsEmpty { get; }
  }

  namespace MMLib.Alvo.Rules.Internal;

  internal static class AccessCatalogBuilder
  {
      internal static EntitySchema ProjectScope { get; }
      internal static ManagementAccessCatalog Build(
          MMLib.Alvo.Descriptor.Access? access,
          ICelCompiler compiler,
          RoleCatalog roles,
          List<DescriptorValidationError> errors);
  }
  ```
  and, on `PolicyCatalog`, `internal ManagementAccessCatalog ManagementAccess { get; }`.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Rules/AccessCatalogBuilderTests.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// The descriptor's <c>access</c> block, compiled in the same pass as every rule and judged against
/// the same declared roles.
/// </summary>
/// <remarks>
/// <b>One pass, one catalogue, one <see cref="RoleCatalog"/>.</b> Compiling the levels separately —
/// lazily, or from a second holder — is how the levels and the rules would come to be judged against
/// two descriptor revisions, which is the inconsistency <c>IRoleCatalogProvider</c>'s remarks call
/// "one descriptor, one catalog, one guard".
/// </remarks>
public class AccessCatalogBuilderTests
{
    [Fact]
    public void Every_declared_level_compiles()
    {
        var catalog = Build(AccessBlock(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'editor' in @user.roles || 'manager' in @user.roles"));

        catalog.ManagementAccess.Admin.ShouldNotBeNull();
        catalog.ManagementAccess.Developer.ShouldNotBeNull();
        catalog.ManagementAccess.Viewer.ShouldNotBeNull();
    }

    [Fact]
    public void A_descriptor_with_no_access_block_compiles_to_an_empty_catalogue()
    {
        var catalog = Build(access: null);

        catalog.ManagementAccess.IsEmpty.ShouldBeTrue(
            "no access block means only the bootstrap admin can manage the project — default-deny, "
            + "and usable: the first-run wizard works and nobody else gets in until the descriptor says so");
    }

    [Fact]
    public void A_level_the_descriptor_declares_nothing_for_stays_null()
    {
        var catalog = Build(AccessBlock(admin: "'manager' in @user.roles"));

        catalog.ManagementAccess.Admin.ShouldNotBeNull();
        catalog.ManagementAccess.Developer.ShouldBeNull();
        catalog.ManagementAccess.Viewer.ShouldBeNull();
        catalog.ManagementAccess.IsEmpty.ShouldBeFalse();
    }

    /// <summary>
    /// <b>The second half of #146.</b> A typo'd literal compiles and type-checks perfectly and then
    /// simply never matches, so a level written to admit managers admits nobody — and, negated,
    /// everybody. Rules already get this check; levels now get the same one, from the same walk.
    /// </summary>
    [Fact]
    public void An_undeclared_role_literal_is_refused_at_apply_naming_the_level()
    {
        var errors = Refuse(AccessBlock(admin: "'amdin' in @user.roles"));

        errors.ShouldContain(error => error.Path == "/access/admin");
        errors.ShouldContain(error => error.Message.Contains("'amdin'", StringComparison.Ordinal));
        errors.ShouldContain(error => error.FixSuggestion!.Contains("manager", StringComparison.Ordinal),
            "the same 'did you mean' shape an unknown field, enum value or role literal already gets");
    }

    [Fact]
    public void An_undeclared_role_literal_is_refused_on_every_level_not_only_admin()
    {
        Refuse(AccessBlock(developer: "'amdin' in @user.roles"))
            .ShouldContain(error => error.Path == "/access/developer");
        Refuse(AccessBlock(viewer: "'amdin' in @user.roles"))
            .ShouldContain(error => error.Path == "/access/viewer");
    }

    [Fact]
    public void A_level_naming_a_row_field_is_refused_at_apply()
    {
        var errors = Refuse(AccessBlock(admin: "owner_id == @user.id"));

        errors.ShouldContain(error => error.Path == "/access/admin");
        errors.ShouldContain(error => error.Message.Contains("no row", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_level_that_is_not_a_predicate_is_refused_at_apply()
        => Refuse(AccessBlock(viewer: "'manager'"))
            .ShouldContain(error => error.Path == "/access/viewer");

    [Fact]
    public void Every_level_is_reported_rather_than_only_the_first()
    {
        var errors = Refuse(AccessBlock(
            admin: "'amdin' in @user.roles",
            developer: "'edtior' in @user.roles",
            viewer: "'salse' in @user.roles"));

        errors.Select(error => error.Path).Distinct()
            .ShouldBe(["/access/admin", "/access/developer", "/access/viewer"], ignoreOrder: true,
                "an agent fixing a descriptor sees every fix it needs in one round trip");
    }

    [Fact]
    public void A_built_in_role_literal_is_declared_without_appearing_in_auth_roles()
        => Build(AccessBlock(admin: "'admin' in @user.roles")).ManagementAccess.Admin.ShouldNotBeNull();

    private static Access AccessBlock(string? admin = null, string? developer = null, string? viewer = null) =>
        new() { Admin = admin, Developer = developer, Viewer = viewer };

    private static PolicyCatalog Build(Access? access)
    {
        PolicyCatalogBuilderProbe.TryBuild(access, out var catalog, out var errors).ShouldBeTrue(
            string.Join(" | ", errors.Select(error => $"{error.Path}: {error.Message}")));
        return catalog!;
    }

    private static IReadOnlyList<DescriptorValidationError> Refuse(Access? access)
    {
        PolicyCatalogBuilderProbe.TryBuild(access, out _, out var errors).ShouldBeFalse();
        return errors;
    }
}
```

and, beside it, the probe that assembles the smallest descriptor a catalogue needs:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// Builds a <see cref="PolicyCatalog"/> from an <c>access</c> block and nothing else — one entity, three
/// declared roles — so an access fact is about the access block and not about a descriptor fixture.
/// </summary>
internal static class PolicyCatalogBuilderProbe
{
    internal static bool TryBuild(
        Access? access,
        out PolicyCatalog? catalog,
        out IReadOnlyList<DescriptorValidationError> errors)
    {
        var json = $$"""
        {
          "name": "access-probe",
          "version": "1.0.0",
          "auth": { "roles": ["manager", "editor", "sales"] },
          "entities": {
            "deals": { "fields": { "owner_id": { "type": "uuid" }, "stage": { "type": "string" } } }
          }
        }
        """;

        var descriptor = AlvoDescriptor.Parse(json) with { Access = access };
        var schema = DescriptorToSchemaMapper.Map(descriptor);

        return PolicyCatalogBuilder.TryBuild(descriptor, schema, new CelCompiler(), out catalog, out errors);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AccessCatalogBuilderTests*'`
Expected: FAIL at build — `error CS1061: 'PolicyCatalog' does not contain a definition for
'ManagementAccess'`.

- [ ] **Step 3: Write the catalogue**

`src/MMLib.Alvo/Rules/ManagementAccessCatalog.cs`:

```csharp
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Rules;

/// <summary>
/// The descriptor's <c>access</c> block, compiled: three independent predicates over the caller, each
/// <see langword="null"/> when the descriptor declares nothing for that level.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three independent predicates, not a hierarchy.</b> A caller matching <see cref="Admin"/> and not
/// <see cref="Viewer"/> is an administrator; a caller matching <see cref="Viewer"/> alone is a viewer.
/// The hierarchy lives one layer up, in what a <c>ManagementLevel</c> is allowed to <em>do</em> — never
/// in whether one predicate implies another.
/// </para>
/// <para>
/// <b>A <see langword="null"/> level matches nobody</b>, exactly as a rule not configured for an
/// operation denies that operation rather than meaning "no restriction". A descriptor with no
/// <c>access</c> block at all therefore leaves only the bootstrap administrator, which is default-deny
/// and is usable: the first-run wizard works, and nobody else gets in until the descriptor says so.
/// </para>
/// </remarks>
/// <param name="Admin">Who may fully administer this project, including its settings.</param>
/// <param name="Developer">Who may edit this project's schema, rules and automation.</param>
/// <param name="Viewer">Who may read this project's data and configuration.</param>
internal sealed record ManagementAccessCatalog(
    CompiledExpression? Admin,
    CompiledExpression? Developer,
    CompiledExpression? Viewer)
{
    /// <summary>The catalogue of a descriptor that declares no <c>access</c> block.</summary>
    internal static ManagementAccessCatalog Empty { get; } = new(null, null, null);

    /// <summary>Whether this descriptor grants no management access to anyone but the bootstrap admin.</summary>
    internal bool IsEmpty => Admin is null && Developer is null && Viewer is null;
}
```

- [ ] **Step 4: Write the builder**

First, in `PolicyCatalogBuilder`, change `RoleLiterals` and `UndeclaredRoleError` from `private
static` to `internal static` — one walk and one wording, reused rather than repeated, which is the
same reason `CelTree.Children` exists.

`src/MMLib.Alvo/Rules/Internal/AccessCatalogBuilder.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Compiles the descriptor's three <c>access</c> levels, in <see cref="PolicyCatalogBuilder"/>'s own
/// pass and against its own <see cref="RoleCatalog"/>.
/// </summary>
/// <remarks>
/// <b>Here rather than in its own pass, and that is the point.</b> One apply compiles the rules and the
/// levels together, validates every role literal in both against one declared set, and publishes one
/// catalogue — so a level and a rule can never be judged against two descriptor revisions, and a role
/// removed from <c>auth.roles</c> is refused in both places at once.
/// </remarks>
internal static class AccessCatalogBuilder
{
    /// <summary>
    /// The entity an access level is type-checked against: one with no fields, because an access level
    /// reads no row.
    /// </summary>
    /// <remarks>
    /// The <see cref="CelProfile.Access"/> profile refuses every field reference before the checker
    /// resolves a name, so this schema's emptiness is never the thing that produces the error — the
    /// profile is. It exists because <see cref="ICelCompiler.Compile"/> takes an entity, and inventing
    /// an overload that does not would widen a published port for one caller.
    /// </remarks>
    internal static EntitySchema ProjectScope { get; } = new() { Name = "<project>", Fields = [] };

    /// <summary>Compiles every declared level, appending every problem found rather than the first.</summary>
    /// <param name="access">The descriptor's <c>access</c> block, or <see langword="null"/>.</param>
    /// <param name="compiler">The CEL compiler every level goes through.</param>
    /// <param name="roles">The project's declared roles, for validating role literals.</param>
    /// <param name="errors">The shared accumulator every problem is appended to.</param>
    internal static ManagementAccessCatalog Build(
        MMLib.Alvo.Descriptor.Access? access,
        ICelCompiler compiler,
        RoleCatalog roles,
        List<DescriptorValidationError> errors)
    {
        if (access is null)
        {
            return ManagementAccessCatalog.Empty;
        }

        return new ManagementAccessCatalog(
            CompileLevel(access.Admin, "admin", compiler, roles, errors),
            CompileLevel(access.Developer, "developer", compiler, roles, errors),
            CompileLevel(access.Viewer, "viewer", compiler, roles, errors));
    }

    private static CompiledExpression? CompileLevel(
        string? source,
        string level,
        ICelCompiler compiler,
        RoleCatalog roles,
        List<DescriptorValidationError> errors)
    {
        if (source is null)
        {
            return null;
        }

        var path = $"/access/{level}";
        var result = compiler.Compile(source, CelProfile.Access, ProjectScope);
        if (!result.IsSuccess)
        {
            errors.AddRange(result.Errors.Select(
                error => new DescriptorValidationError(
                    path, error.Message, error.FixSuggestion, DescriptorValidationSeverity.Error)));
            return null;
        }

        return HasDeclaredRoles(result.Expression!, path, roles, errors) ? result.Expression : null;
    }

    private static bool HasDeclaredRoles(
        CompiledExpression expression, string path, RoleCatalog roles, List<DescriptorValidationError> errors)
    {
        var undeclared = PolicyCatalogBuilder.RoleLiterals(expression.Root)
            .Where(role => !roles.TryGet(role, out _))
            .ToList();

        errors.AddRange(undeclared.Select(
            role => PolicyCatalogBuilder.UndeclaredRoleError(path, role, roles)));
        return undeclared.Count == 0;
    }
}
```

- [ ] **Step 5: Hang it off the one apply**

In `PolicyCatalogBuilder.TryBuild`, after the entity loop and **before** the `errorList.Count > 0`
check, add one line:

```csharp
        var managementAccess = AccessCatalogBuilder.Build(descriptor.Access, compiler, roles, errorList);
```

and pass it to the constructor:

```csharp
        catalog = new PolicyCatalog(entities, roles, schema, managementAccess);
```

In `src/MMLib.Alvo/Rules/PolicyCatalog.cs`, add the constructor parameter and the property:

```csharp
    internal PolicyCatalog(
        IReadOnlyDictionary<string, EntityPolicy> entities,
        RoleCatalog roles,
        SchemaModel schema,
        ManagementAccessCatalog managementAccess)
    {
        // …existing null guards…
        ArgumentNullException.ThrowIfNull(managementAccess);
        ManagementAccess = managementAccess;
    }

    /// <summary>
    /// Gets the compiled <c>access</c> levels this project declares.
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> for the reason <see cref="Roles"/> and <see cref="Schema"/> are: the
    /// management gate reads it through <see cref="IPolicyCatalogProvider.Current"/>, so nothing above
    /// the rule engine has to know that the authoritative access levels currently happen to arrive with
    /// a policy catalog.
    /// </remarks>
    internal ManagementAccessCatalog ManagementAccess { get; }
```

Fix the other `new PolicyCatalog(...)` call sites the compiler names, passing
`ManagementAccessCatalog.Empty` where a test or a helper builds a catalogue by hand.

- [ ] **Step 6: Run to verify they pass**

```bash
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AccessCatalogBuilderTests*'
dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Rules'
```
Expected: PASS for both, 9 new facts plus every existing rule fact unchanged.

- [ ] **Step 7: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Rules/ManagementAccessCatalog.cs \
        src/MMLib.Alvo/Rules/Internal/AccessCatalogBuilder.cs \
        src/MMLib.Alvo/Rules/PolicyCatalog.cs \
        src/MMLib.Alvo/Rules/Internal/PolicyCatalogBuilder.cs \
        test/MMLib.Alvo.Tests/Rules/AccessCatalogBuilderTests.cs \
        test/MMLib.Alvo.Tests/Rules/PolicyCatalogBuilderProbe.cs
git commit -m "feat(access): compile the three access levels at apply, validating their role literals"
```

---

### Task 3: The levels, the operation table, and the evaluator

**Files:**
- Create: `src/MMLib.Alvo/Management/Access/ManagementLevel.cs`
- Create: `src/MMLib.Alvo/Management/Access/ManagementOperation.cs`
- Create: `src/MMLib.Alvo/Management/Access/ManagementOperations.cs`
- Create: `src/MMLib.Alvo/Management/Access/Internal/ManagementAccessEvaluator.cs`
- Create: `src/MMLib.Alvo/Management/Setup.cs`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`
- Test: `test/MMLib.Alvo.Tests/Management/ManagementAccessEvaluatorTests.cs`
- Test: `test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs`
- Test: `test/MMLib.Alvo.Tests/Management/ManagementCallers.cs` (the fixture Task 4 reuses)

**Interfaces:**
- Consumes: `ManagementAccessCatalog`, `PolicyCatalog.ManagementAccess` (Task 2);
  `IPolicyCatalogProvider.Current`; `IPredicateEvaluator.Evaluate(CompiledExpression, AlvoRecord, AlvoRecord?, AlvoContext)`;
  `AlvoRecord.Empty`; **`IAlvoBootstrapAdmin.IsBootstrapAdmin(UserId user)` — from Plan 1, Task 1,
  spelled exactly so.**
- Produces:
  ```csharp
  namespace MMLib.Alvo.Management;

  internal enum ManagementLevel { None = 0, Viewer = 1, Developer = 2, Admin = 3 }

  internal enum ManagementOperation
  {
      ListProjects, GetDescriptor, ListRevisions, GetRevision, GetSchema,
      GetCapabilities, GetInfo, SimulatePolicy,
      ApplyDescriptor, RollbackRevision,
      ManageApiKeys, ManageUsers, DeleteProject,
  }

  internal static class ManagementOperations
  {
      internal static ManagementLevel RequiredLevel(ManagementOperation operation);
  }

  namespace MMLib.Alvo.Management.Internal;

  internal sealed class ManagementAccessEvaluator(
      IPolicyCatalogProvider catalogs, IPredicateEvaluator evaluator, IAlvoBootstrapAdmin bootstrapAdmin)
  {
      internal ManagementLevel Resolve(AlvoContext context);
      internal bool Allows(ManagementOperation operation, AlvoContext context);
  }
  ```

- [ ] **Step 1: Write the failing operation-table tests**

Create `test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The one table that says which level each management operation needs.
/// </summary>
/// <remarks>
/// <b>The completeness fact is the one that cannot go stale.</b> A table missing an operation would
/// leave that operation ungoverned, and an operation added without a level is exactly the change a
/// reviewer skims past — so the level is looked up for <em>every</em> enum member rather than for the
/// members somebody remembered to list.
/// </remarks>
public class ManagementOperationsTests
{
    [Fact]
    public void Every_operation_has_a_required_level()
    {
        foreach (var operation in Enum.GetValues<ManagementOperation>())
        {
            ManagementOperations.RequiredLevel(operation).ShouldNotBe(
                ManagementLevel.None,
                $"'{operation}' would otherwise be reachable by a caller who matched no access level");
        }
    }

    [Theory]
    [InlineData(ManagementOperation.ListProjects)]
    [InlineData(ManagementOperation.GetDescriptor)]
    [InlineData(ManagementOperation.ListRevisions)]
    [InlineData(ManagementOperation.GetRevision)]
    [InlineData(ManagementOperation.GetSchema)]
    [InlineData(ManagementOperation.GetCapabilities)]
    [InlineData(ManagementOperation.GetInfo)]
    [InlineData(ManagementOperation.SimulatePolicy)]
    public void A_viewer_may_read_everything_including_the_simulator(ManagementOperation operation)
        => ManagementOperations.RequiredLevel(operation).ShouldBe(ManagementLevel.Viewer);

    /// <summary>
    /// <b>Applying a descriptor is one operation whether or not <c>?dryRun=true</c> is set</b>, and that
    /// is deliberate: a dry run returns the migration plan and the guardrail verdict for a descriptor
    /// the caller supplied, which is the same disclosure the real apply makes. A separate, cheaper level
    /// for it would let a viewer read a plan over a descriptor they may not write.
    /// </summary>
    [Theory]
    [InlineData(ManagementOperation.ApplyDescriptor)]
    [InlineData(ManagementOperation.RollbackRevision)]
    public void A_developer_may_write_configuration(ManagementOperation operation)
        => ManagementOperations.RequiredLevel(operation).ShouldBe(ManagementLevel.Developer);

    /// <summary>
    /// "Settings" is named rather than left to reading: it is the set <c>developer</c> is excluded from
    /// — API keys, user and role administration, and the danger zone. A developer edits <em>what the
    /// backend is</em>; an admin also decides <em>who may reach it</em>.
    /// </summary>
    [Theory]
    [InlineData(ManagementOperation.ManageApiKeys)]
    [InlineData(ManagementOperation.ManageUsers)]
    [InlineData(ManagementOperation.DeleteProject)]
    public void Only_an_admin_may_reach_settings(ManagementOperation operation)
        => ManagementOperations.RequiredLevel(operation).ShouldBe(ManagementLevel.Admin);

    /// <summary>
    /// The surface is thirteen operations. A count rather than a comment, so adding a fourteenth fails
    /// here until somebody decides its level — which is the decision this table exists to force.
    /// </summary>
    [Fact]
    public void The_management_surface_is_thirteen_operations()
        => Enum.GetValues<ManagementOperation>().Length.ShouldBe(13);

    [Fact]
    public void The_levels_are_ordered_so_the_highest_match_is_the_greatest_value()
    {
        ManagementLevel.None.ShouldBeLessThan(ManagementLevel.Viewer);
        ManagementLevel.Viewer.ShouldBeLessThan(ManagementLevel.Developer);
        ManagementLevel.Developer.ShouldBeLessThan(ManagementLevel.Admin);
        ((int)ManagementLevel.None).ShouldBe(0, "an unset or mis-bound value must land on 'no access'");
    }
}
```

- [ ] **Step 2: Write the failing evaluator tests**

Create `test/MMLib.Alvo.Tests/Management/ManagementAccessEvaluatorTests.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Rules;
using NSubstitute;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// Turning a caller into a management level: three independent predicates, the highest match, and the
/// bootstrap administrator above all of them.
/// </summary>
public class ManagementAccessEvaluatorTests
{
    /// <summary>
    /// <b>Independent predicates, not a hierarchy.</b> A caller who matches <c>admin</c> and matches
    /// neither <c>developer</c> nor <c>viewer</c> is an administrator — nothing requires a level to
    /// imply the one below it, and a descriptor may well name three disjoint role sets.
    /// </summary>
    [Fact]
    public void A_caller_matching_only_the_admin_level_is_an_admin()
        => Resolve(Caller("manager"), Levels(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'sales' in @user.roles"))
            .ShouldBe(ManagementLevel.Admin);

    [Fact]
    public void A_caller_matching_only_the_viewer_level_is_a_viewer()
        => Resolve(Caller("sales"), Levels(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'sales' in @user.roles"))
            .ShouldBe(ManagementLevel.Viewer);

    [Fact]
    public void A_caller_matching_two_levels_gets_the_higher_one()
        => Resolve(Caller("editor", "sales"), Levels(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'sales' in @user.roles"))
            .ShouldBe(ManagementLevel.Developer);

    [Fact]
    public void A_caller_matching_no_level_has_none()
        => Resolve(Caller("sales"), Levels(admin: "'manager' in @user.roles"))
            .ShouldBe(ManagementLevel.None);

    [Fact]
    public void A_descriptor_with_no_access_block_admits_nobody()
        => Resolve(Caller("manager"), Levels()).ShouldBe(ManagementLevel.None);

    /// <summary>
    /// <b>Before a descriptor is applied there is no catalogue at all</b>, and that must deny rather
    /// than throw or widen — the same fail-closed direction <c>IPolicyEngine</c> takes for an unprimed
    /// policy catalog.
    /// </summary>
    [Fact]
    public void An_unprimed_host_admits_nobody()
    {
        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns((PolicyCatalog?)null);

        Evaluator(catalogs).Resolve(Caller("manager")).ShouldBe(ManagementLevel.None);
    }

    /// <summary>
    /// <b>The bootstrap administrator bypasses <c>access</c> entirely</b>, and that is a boundary rather
    /// than a hole: it is infrastructure configuration, never the descriptor, so a descriptor with no
    /// <c>access</c> block still has exactly one person who can fix it.
    /// </summary>
    [Fact]
    public void The_bootstrap_admin_is_an_admin_whatever_the_descriptor_says()
    {
        var caller = new AlvoContext
        {
            User = ManagementCallers.Bootstrap,
            Roles = new HashSet<Role> { Role.Anon },
        };

        Resolve(caller, Levels()).ShouldBe(ManagementLevel.Admin);
        Resolve(caller, Levels(admin: "'manager' in @user.roles")).ShouldBe(ManagementLevel.Admin);
    }

    [Fact]
    public void The_anonymous_caller_is_never_an_admin()
        => Resolve(AlvoContext.Anonymous, Levels(admin: "'manager' in @user.roles"))
            .ShouldBe(ManagementLevel.None);

    /// <summary>
    /// A negated level is evaluated as written, including for a caller who holds nothing — the direction
    /// that catches a gate treating "no roles" as "no answer" instead of as an evaluated <c>true</c>.
    /// </summary>
    [Fact]
    public void A_negated_level_is_evaluated_as_written()
    {
        Resolve(Caller("sales"), Levels(viewer: "!('manager' in @user.roles)"))
            .ShouldBe(ManagementLevel.Viewer);
        Resolve(Caller("manager"), Levels(viewer: "!('manager' in @user.roles)"))
            .ShouldBe(ManagementLevel.None);
    }

    /// <summary>
    /// A level naming two roles admits only a caller holding both — the conjunction is evaluated, not
    /// approximated by "holds any of the named roles".
    /// </summary>
    [Fact]
    public void A_conjunction_requires_every_named_role()
    {
        var levels = Levels(admin: "'manager' in @user.roles && 'editor' in @user.roles");

        Resolve(Caller("manager", "editor"), levels).ShouldBe(ManagementLevel.Admin);
        Resolve(Caller("manager"), levels).ShouldBe(ManagementLevel.None);
    }

    // ---- Allows, the layer where levels DO form a hierarchy -----------------------------------

    /// <summary>
    /// The predicates are independent; what a resolved level may <em>do</em> is ordered. An admin may do
    /// everything a developer may, because <c>admin</c> is defined as "everything, plus settings".
    /// </summary>
    [Fact]
    public void An_admin_may_do_everything_a_developer_and_a_viewer_may()
    {
        var subject = Evaluator(Primed(Levels(admin: "'manager' in @user.roles")));
        var caller = Caller("manager");

        foreach (var operation in Enum.GetValues<ManagementOperation>())
        {
            subject.Allows(operation, caller).ShouldBeTrue(operation.ToString());
        }
    }

    [Fact]
    public void A_developer_may_apply_a_descriptor_but_not_reach_settings()
    {
        var subject = Evaluator(Primed(Levels(developer: "'editor' in @user.roles")));
        var caller = Caller("editor");

        subject.Allows(ManagementOperation.ApplyDescriptor, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.RollbackRevision, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.GetDescriptor, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.ManageApiKeys, caller).ShouldBeFalse();
        subject.Allows(ManagementOperation.ManageUsers, caller).ShouldBeFalse();
        subject.Allows(ManagementOperation.DeleteProject, caller).ShouldBeFalse();
    }

    [Fact]
    public void A_viewer_may_read_and_simulate_but_never_write()
    {
        var subject = Evaluator(Primed(Levels(viewer: "'sales' in @user.roles")));
        var caller = Caller("sales");

        subject.Allows(ManagementOperation.GetSchema, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.SimulatePolicy, caller).ShouldBeTrue();
        subject.Allows(ManagementOperation.ApplyDescriptor, caller).ShouldBeFalse();
        subject.Allows(ManagementOperation.RollbackRevision, caller).ShouldBeFalse();
    }

    [Fact]
    public void A_caller_matching_no_level_may_do_nothing_at_all()
    {
        var subject = Evaluator(Primed(Levels(admin: "'manager' in @user.roles")));
        var caller = Caller("sales");

        foreach (var operation in Enum.GetValues<ManagementOperation>())
        {
            subject.Allows(operation, caller).ShouldBeFalse(operation.ToString());
        }
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static AlvoContext Caller(params string[] roleNames) => ManagementCallers.Caller(roleNames);

    private static Access Levels(string? admin = null, string? developer = null, string? viewer = null) =>
        ManagementCallers.Levels(admin, developer, viewer);

    private static ManagementLevel Resolve(AlvoContext caller, Access levels) =>
        ManagementCallers.Evaluator(levels).Resolve(caller);

    private static IPolicyCatalogProvider Primed(Access levels) => ManagementCallers.Primed(levels);

    private static ManagementAccessEvaluator Evaluator(IPolicyCatalogProvider catalogs) =>
        ManagementCallers.Evaluator(catalogs);
}
```

and, in the same step, the fixture both management suites share —
`test/MMLib.Alvo.Tests/Management/ManagementCallers.cs`. It is one type rather than a private
helper per suite because two fixtures that each build "a caller holding these roles" is how the
evaluator facts and the filter facts (Task 4) would come to describe two different callers:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Rules;
using NSubstitute;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The callers, the access blocks and the evaluator both management suites are written against.</summary>
internal static class ManagementCallers
{
    /// <summary>The bootstrap administrator every fixture here recognises.</summary>
    internal static UserId Bootstrap { get; } = UserId.New();

    internal static AlvoContext Caller(params string[] roleNames) => new()
    {
        User = UserId.New(),
        Roles = RoleCatalog.Create(["manager", "editor", "sales"]).Resolve([.. roleNames, "authenticated"]),
    };

    internal static Access Levels(string? admin = null, string? developer = null, string? viewer = null) =>
        new() { Admin = admin, Developer = developer, Viewer = viewer };

    internal static ManagementAccessEvaluator Evaluator(Access levels) => Evaluator(Primed(levels));

    internal static ManagementAccessEvaluator Evaluator(IPolicyCatalogProvider catalogs)
    {
        var bootstrap = Substitute.For<IAlvoBootstrapAdmin>();
        bootstrap.IsBootstrapAdmin(Arg.Any<UserId>()).Returns(call => call.Arg<UserId>() == Bootstrap);

        return new ManagementAccessEvaluator(catalogs, new PredicateEvaluator(), bootstrap);
    }

    internal static IPolicyCatalogProvider Primed(Access levels)
    {
        PolicyCatalogBuilderProbe.TryBuild(levels, out var catalog, out var errors).ShouldBeTrue(
            string.Join(" | ", errors.Select(error => $"{error.Path}: {error.Message}")));

        var catalogs = Substitute.For<IPolicyCatalogProvider>();
        catalogs.Current.Returns(catalog);
        return catalogs;
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Management'`
Expected: FAIL at build — `error CS0246: The type or namespace name 'ManagementOperation' could
not be found`.

- [ ] **Step 4: Write the two enums and the table**

`src/MMLib.Alvo/Management/Access/ManagementLevel.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// What a caller may do with the Management API, as the descriptor's <c>access</c> block resolved it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Numbered so that "the highest match wins" is the language's own <c>Max</c>.</b> The three
/// <em>predicates</em> are independent — a descriptor may name three disjoint role sets — but what a
/// resolved level may <em>do</em> is ordered, because <c>admin</c> is defined by the schema as
/// "everything, plus settings".
/// </para>
/// <para>
/// <b><see cref="None"/> is zero</b>, so an unset or mis-bound value lands on "no access" — the same
/// property <c>default(Role)</c> has for <c>anon</c>.
/// </para>
/// <para>
/// <b>Management levels govern the Management API only.</b> Data access is governed by the descriptor's
/// <c>entities.*.rules</c>, through the ordinary Data API, identically for the dashboard and for
/// everyone else. No second authorization system for data exists.
/// </para>
/// </remarks>
internal enum ManagementLevel
{
    /// <summary>No management access at all.</summary>
    None = 0,

    /// <summary>May read this project's data and configuration, and simulate a policy.</summary>
    Viewer = 1,

    /// <summary>May additionally edit this project's schema, rules and automation.</summary>
    Developer = 2,

    /// <summary>May do everything, including the settings surface.</summary>
    Admin = 3,
}
```

`src/MMLib.Alvo/Management/Access/ManagementOperation.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// Every operation the Management API exposes, as the surface in the F5 design §2.2 lists them.
/// </summary>
/// <remarks>
/// <b>An operation, not a route.</b> The same operation is reachable in-process (the Admin package
/// through <c>IAlvoManagement</c>) and over HTTP, and both go through the same gate — which is what
/// makes "one path, two transports" an authorization fact and not only a composition one.
/// </remarks>
internal enum ManagementOperation
{
    /// <summary>List the projects this instance serves.</summary>
    ListProjects,

    /// <summary>Read the current descriptor and its revision — this is also the export.</summary>
    GetDescriptor,

    /// <summary>Read the append-only revision history.</summary>
    ListRevisions,

    /// <summary>Read one past revision.</summary>
    GetRevision,

    /// <summary>Read the resolved schema the Data API actually serves.</summary>
    GetSchema,

    /// <summary>Read what this build honours, warns about and refuses.</summary>
    GetCapabilities,

    /// <summary>Read the build, mode, engine and startup mode.</summary>
    GetInfo,

    /// <summary>Evaluate a policy for a simulated caller. Writes nothing.</summary>
    SimulatePolicy,

    /// <summary>Apply a descriptor — including a <c>?dryRun=true</c> plan, which discloses the same thing.</summary>
    ApplyDescriptor,

    /// <summary>Roll back to a past revision.</summary>
    RollbackRevision,

    /// <summary>Issue or revoke an API key.</summary>
    ManageApiKeys,

    /// <summary>Administer users and their role memberships.</summary>
    ManageUsers,

    /// <summary>The danger zone: delete the project.</summary>
    DeleteProject,
}
```

`src/MMLib.Alvo/Management/Access/ManagementOperations.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// The one table mapping a <see cref="ManagementOperation"/> onto the <see cref="ManagementLevel"/> it
/// needs.
/// </summary>
/// <remarks>
/// <b>Deny by default, like <c>_allowedProfiles</c>.</b> An operation missing from the table resolves to
/// <see cref="ManagementLevel.Admin"/> — the most restrictive answer, not the most convenient one — so a
/// future operation added without a decision is refused for everyone but an administrator rather than
/// opened to every viewer. A fact additionally asserts that every enum member really is listed, so the
/// fallback is unreachable in a correct build.
/// </remarks>
internal static class ManagementOperations
{
    private static readonly Dictionary<ManagementOperation, ManagementLevel> _requiredLevels = new()
    {
        [ManagementOperation.ListProjects] = ManagementLevel.Viewer,
        [ManagementOperation.GetDescriptor] = ManagementLevel.Viewer,
        [ManagementOperation.ListRevisions] = ManagementLevel.Viewer,
        [ManagementOperation.GetRevision] = ManagementLevel.Viewer,
        [ManagementOperation.GetSchema] = ManagementLevel.Viewer,
        [ManagementOperation.GetCapabilities] = ManagementLevel.Viewer,
        [ManagementOperation.GetInfo] = ManagementLevel.Viewer,
        [ManagementOperation.SimulatePolicy] = ManagementLevel.Viewer,
        [ManagementOperation.ApplyDescriptor] = ManagementLevel.Developer,
        [ManagementOperation.RollbackRevision] = ManagementLevel.Developer,
        [ManagementOperation.ManageApiKeys] = ManagementLevel.Admin,
        [ManagementOperation.ManageUsers] = ManagementLevel.Admin,
        [ManagementOperation.DeleteProject] = ManagementLevel.Admin,
    };

    /// <summary>The level <paramref name="operation"/> requires.</summary>
    /// <param name="operation">The operation about to be performed.</param>
    internal static ManagementLevel RequiredLevel(ManagementOperation operation) =>
        _requiredLevels.TryGetValue(operation, out var level) ? level : ManagementLevel.Admin;
}
```

- [ ] **Step 5: Write the evaluator**

`src/MMLib.Alvo/Management/Access/Internal/ManagementAccessEvaluator.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Resolves a caller to a <see cref="ManagementLevel"/>, and answers whether that level reaches an
/// operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>All three predicates are evaluated, and the highest match wins.</b> Not short-circuited from the
/// top: the levels are independent by design, and evaluating them all is what makes that structural
/// rather than argued. The cost is two extra evaluations of a context-only expression, which is
/// nothing beside a management call.
/// </para>
/// <para>
/// <b>Everything fails closed.</b> An unprimed catalog, a descriptor with no <c>access</c> block, a
/// level the descriptor declares nothing for, and an expression that throws all resolve to
/// <see cref="ManagementLevel.None"/> — <c>IPredicateEvaluator</c> already collapses a failed
/// evaluation to <see langword="false"/>, which is deny for a predicate.
/// </para>
/// <para>
/// <b>The bootstrap administrator is above the descriptor, deliberately.</b> It is infrastructure
/// configuration, never the descriptor (<c>docs/PLAN.md</c> invariant 4), so a project whose
/// <c>access</c> block locks everyone out still has exactly one person who can fix it.
/// </para>
/// </remarks>
internal sealed class ManagementAccessEvaluator(
    IPolicyCatalogProvider catalogs,
    IPredicateEvaluator evaluator,
    IAlvoBootstrapAdmin bootstrapAdmin)
{
    /// <summary>Whether <paramref name="context"/> may perform <paramref name="operation"/>.</summary>
    /// <param name="operation">The operation about to be performed.</param>
    /// <param name="context">The caller.</param>
    internal bool Allows(ManagementOperation operation, AlvoContext context) =>
        Resolve(context) >= ManagementOperations.RequiredLevel(operation);

    /// <summary>The highest level <paramref name="context"/> matches.</summary>
    /// <param name="context">The caller.</param>
    internal ManagementLevel Resolve(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return bootstrapAdmin.IsBootstrapAdmin(context.User)
            ? ManagementLevel.Admin
            : HighestMatch(context);
    }

    private ManagementLevel HighestMatch(AlvoContext context)
    {
        if (catalogs.Current?.ManagementAccess is not { } access)
        {
            return ManagementLevel.None;
        }

        var matched = new List<ManagementLevel>(3);
        AddWhenMatched(matched, ManagementLevel.Viewer, access.Viewer, context);
        AddWhenMatched(matched, ManagementLevel.Developer, access.Developer, context);
        AddWhenMatched(matched, ManagementLevel.Admin, access.Admin, context);

        return matched.Count == 0 ? ManagementLevel.None : matched.Max();
    }

    private void AddWhenMatched(
        List<ManagementLevel> matched, ManagementLevel level, CompiledExpression? predicate, AlvoContext context)
    {
        if (Matches(predicate, context))
        {
            matched.Add(level);
        }
    }

    /// <summary>
    /// An access level is a predicate over the caller alone, so it is evaluated against
    /// <see cref="AlvoRecord.Empty"/> and no previous row — the same shape a <c>hidden</c>/<c>readOnly</c>
    /// mask uses, in the opposite fail-safe direction: a mask fails closed to "masked", an authorization
    /// predicate fails closed to "deny".
    /// </summary>
    private bool Matches(CompiledExpression? predicate, AlvoContext context) =>
        predicate is not null && evaluator.Evaluate(predicate, AlvoRecord.Empty, null, context);
}
```

- [ ] **Step 6: Register it**

`src/MMLib.Alvo/Management/Setup.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Management;

/// <summary>Registers the Management API's authorization gate.</summary>
internal static class ManagementSetup
{
    /// <summary>Adds <see cref="ManagementAccessEvaluator"/>.</summary>
    /// <param name="services">The service collection to add the management services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoManagement(this IServiceCollection services)
    {
        services.TryAddSingleton<ManagementAccessEvaluator>();
        return services;
    }
}
```

and call `services.AddAlvoManagement();` in `AddAlvo`, beside the existing `AddAlvoAuth()` and
`AddAlvoExpressions()` calls.

- [ ] **Step 7: Run to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Management'`
Expected: PASS — 16 test cases from `ManagementOperationsTests` (three theories expand to 13 of
them) and 14 facts from `ManagementAccessEvaluatorTests`.

- [ ] **Step 8: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Management/ src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs \
        test/MMLib.Alvo.Tests/Management/
git commit -m "feat(access): resolve a caller to a management level, bootstrap admin above the descriptor"
```

---

### Task 4: The gate an endpoint attaches

**Files:**
- Create: `src/MMLib.Alvo/Management/Access/Internal/ManagementAccessEndpointFilter.cs`
- Create: `src/MMLib.Alvo/Management/Access/ManagementAccessRouteBuilderExtensions.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`
- Test: `test/MMLib.Alvo.Tests/Management/ManagementAccessEndpointFilterTests.cs`

**Interfaces:**
- Consumes: `ManagementAccessEvaluator`, `ManagementOperation` (Task 3);
  `IAlvoContextAccessor.Principal`; `AlvoProblemTypes.Forbidden`;
  `ProblemResultFactory.Problem(int, string, string)` (existing private).
- Produces:
  ```csharp
  namespace MMLib.Alvo.Management.Internal;

  internal sealed class ManagementAccessEndpointFilter(
      ManagementOperation operation,
      ManagementAccessEvaluator access,
      IAlvoContextAccessor callers) : IEndpointFilter
  {
      public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next);
  }

  namespace MMLib.Alvo.Management;

  internal static class ManagementAccessRouteBuilderExtensions
  {
      internal static RouteHandlerBuilder RequireAlvoManagementAccess(
          this RouteHandlerBuilder builder, ManagementOperation operation);
  }
  ```
  and `internal static IResult ManagementForbidden()` on `ProblemResultFactory`.

> **Why a filter and not a route.** #212 (the Management API) is a separate issue and its routes do
> not exist yet. What #146 owes is **enforcement that is attached, not enforcement that is
> optional**: the filter and the one-call extension are what #212 hangs off every route it adds,
> and Task 7 records the obligation on that issue so the gate cannot be shipped unused.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Management/ManagementAccessEndpointFilterTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The filter every management route carries: it runs the gate, and a caller who matches no level
/// never reaches the handler.
/// </summary>
public class ManagementAccessEndpointFilterTests
{
    [Fact]
    public async Task A_caller_who_matches_no_level_is_refused_and_the_handler_never_runs()
    {
        var handlerRan = false;

        var outcome = await InvokeAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("sales"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => { handlerRan = true; return "ok"; });

        handlerRan.ShouldBeFalse("a gate that runs after the handler has already disclosed the answer");
        (await StatusOfAsync(outcome)).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        var outcome = await InvokeAsync(
            ManagementOperation.GetSchema,
            caller: null,
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => "ok");

        (await StatusOfAsync(outcome)).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_caller_at_the_required_level_reaches_the_handler()
    {
        var handlerRan = false;

        var outcome = await InvokeAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("manager"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => { handlerRan = true; return "ok"; });

        handlerRan.ShouldBeTrue();
        outcome.ShouldBe("ok");
    }

    [Fact]
    public async Task A_developer_is_refused_the_settings_surface()
    {
        var outcome = await InvokeAsync(
            ManagementOperation.ManageApiKeys,
            ManagementCallers.Caller("editor"),
            ManagementCallers.Levels(developer: "'editor' in @user.roles"),
            () => "ok");

        (await StatusOfAsync(outcome)).ShouldBe(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// <b>The slug is the published one every policy refusal carries.</b> <c>data-api.md</c> names
    /// <c>forbidden</c> as that slug, and minting a management-only spelling would give an agent two
    /// types to branch on for one class of refusal.
    /// </summary>
    [Fact]
    public async Task The_refusal_carries_the_published_forbidden_slug()
    {
        var outcome = await InvokeAsync(
            ManagementOperation.GetSchema,
            ManagementCallers.Caller("sales"),
            ManagementCallers.Levels(admin: "'manager' in @user.roles"),
            () => "ok");

        var document = await DocumentWrittenByAsync((IResult)outcome!);
        document["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.UriOf(AlvoProblemTypes.Forbidden));
    }

    private static async ValueTask<object?> InvokeAsync(
        ManagementOperation operation, AlvoContext? caller, Access levels, Func<object?> next)
    {
        var filter = new ManagementAccessEndpointFilter(
            operation, ManagementCallers.Evaluator(levels), new PublishedCaller(caller));

        var context = EndpointFilterInvocationContext.Create(new DefaultHttpContext());
        return await filter.InvokeAsync(context, _ => ValueTask.FromResult(next()));
    }

    private static async Task<int> StatusOfAsync(object? outcome)
    {
        var context = await ExecutedAsync((IResult)outcome!);
        return context.Response.StatusCode;
    }

    private static async Task<JsonObject> DocumentWrittenByAsync(IResult result)
    {
        var context = await ExecutedAsync(result);
        context.Response.Body.Position = 0;

        return (await JsonSerializer.DeserializeAsync<JsonObject>(
            context.Response.Body, cancellationToken: TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Executes the result the way a live endpoint would.
    /// </summary>
    /// <remarks>
    /// Against a container rather than a bare context, and that is the point of executing it at all:
    /// ASP.NET Core's own <c>ProblemHttpResult</c> resolves <c>IProblemDetailsService</c> and the JSON
    /// options from the request's services, so this measures the bytes a caller receives, written by the
    /// same writer every live endpoint uses. The same shape
    /// <c>MMLib.Alvo.Api.Tests.ProblemDetailsTests.SlugWrittenByAsync</c> uses, written locally because
    /// that is a different assembly.
    /// </remarks>
    private static async Task<DefaultHttpContext> ExecutedAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };

        await result.ExecuteAsync(context);
        return context;
    }

    /// <summary>
    /// The caller this invocation runs as, or none.
    /// </summary>
    /// <remarks>
    /// A stub rather than the framework's <c>AlvoContextAccessor</c>, which holds its value in a
    /// <see cref="AsyncLocal{T}"/> shared by every instance — correct in a request pipeline, and a way for
    /// two facts running in parallel to publish over each other here.
    /// </remarks>
    private sealed class PublishedCaller(AlvoContext? caller) : IAlvoContextAccessor
    {
        public AlvoPrincipal? Principal { get; set; } = caller is null ? null : new AlvoPrincipal
        {
            Context = caller,
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "probe",
        };
    }
}
```

> `ManagementCallers` is Task 3's shared fixture — `Caller`, `Levels`, `Evaluator(Access)` and
> `Bootstrap` — reused here unchanged, so the filter facts and the evaluator facts describe the same
> caller. Do not add a second copy.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*ManagementAccessEndpointFilterTests*'`
Expected: FAIL at build — `error CS0246: The type or namespace name
'ManagementAccessEndpointFilter' could not be found`.

- [ ] **Step 3: Add the refusal to the problem factory**

In `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`, beside `ScopeRefused`:

```csharp
    /// <summary>
    /// The 403 for a caller the descriptor's <c>access</c> block does not admit to this operation.
    /// </summary>
    /// <remarks>
    /// <b>The wording names neither the level the caller holds nor the level the operation needs</b>,
    /// for <see cref="ScopeRefused"/>'s reason one subsystem over: a message naming the gap would let a
    /// caller map the project's whole access block one request at a time, which is a fingerprint of the
    /// configuration rather than of the data. The fix is the one an operator can act on — ask whoever
    /// administers the project.
    /// </remarks>
    internal static IResult ManagementForbidden() => Problem(
        StatusCodes.Status403Forbidden,
        AlvoProblemTypes.Forbidden,
        "This caller is not admitted to the project's management surface. The project's access block "
            + "decides who is; ask whoever administers it.");
```

- [ ] **Step 4: Write the filter and its extension**

`src/MMLib.Alvo/Management/Access/Internal/ManagementAccessEndpointFilter.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Runs the management gate before the handler, and answers <c>403</c> when the caller matches no level.
/// </summary>
/// <remarks>
/// <b>Before the handler, never after.</b> A gate that filters a handler's output has already let the
/// handler read what it was refused; this returns instead of calling <c>next</c>, which is the same rule
/// the security-core checklist states for a data query — the predicate goes in the WHERE, not in a
/// post-filter.
/// </remarks>
internal sealed class ManagementAccessEndpointFilter(
    ManagementOperation operation,
    ManagementAccessEvaluator access,
    IAlvoContextAccessor callers) : IEndpointFilter
{
    /// <inheritdoc/>
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        var caller = callers.Principal?.Context ?? AlvoContext.Anonymous;
        return access.Allows(operation, caller)
            ? next(context)
            : ValueTask.FromResult<object?>(ProblemResultFactory.ManagementForbidden());
    }
}
```

`src/MMLib.Alvo/Management/Access/ManagementAccessRouteBuilderExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Management;

/// <summary>Attaches the management gate to a route.</summary>
internal static class ManagementAccessRouteBuilderExtensions
{
    /// <summary>
    /// Requires that the caller's resolved <see cref="ManagementLevel"/> reaches
    /// <paramref name="operation"/>.
    /// </summary>
    /// <remarks>
    /// Every management route carries this, and the contract test that pins "every
    /// <c>IAlvoManagement</c> member has an HTTP route" is the other half: a route without the gate and
    /// an operation without a route are the two ways this surface can quietly open.
    /// </remarks>
    /// <param name="builder">The route being built.</param>
    /// <param name="operation">The operation that route performs.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    internal static RouteHandlerBuilder RequireAlvoManagementAccess(
        this RouteHandlerBuilder builder, ManagementOperation operation)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var services = factoryContext.ApplicationServices;
            var filter = new ManagementAccessEndpointFilter(
                operation,
                services.GetRequiredService<ManagementAccessEvaluator>(),
                services.GetRequiredService<IAlvoContextAccessor>());

            return invocation => filter.InvokeAsync(invocation, next);
        });
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*ManagementAccessEndpointFilterTests*'`
Expected: PASS, 5 tests.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Management/ src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs \
        test/MMLib.Alvo.Tests/Management/
git commit -m "feat(access): gate a management route on the caller's resolved level"
```

---

### Task 5: `access` leaves `UnhonouredSubsystems` — the table, the prose, and the facts

**Files:**
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs`
- Modify: `test/MMLib.Alvo.Tests/Descriptor/UnhonouredSubsystemsTests.cs`

**Interfaces:**
- Consumes: nothing new. Produces: `UnhonouredSubsystems.All` with **five** entries —
  `dynamicEntities`, `automation`, `templates`, `webhooks`, `functions`.

> The file asked for this itself, in as many words: *"the day the surface lands, `access` is either
> honoured or refused — never warned about."* It is now honoured, so the entry goes — and the
> paragraphs that argued for it must go with it, or the file describes a warning it no longer emits.

- [ ] **Step 1: Change the tests first**

In `test/MMLib.Alvo.Tests/Descriptor/UnhonouredSubsystemsTests.cs`:

1. Drop `"access"` from the literal:

```csharp
    private static readonly string[] _blocksComplexCrmDeclares =
        ["dynamicEntities", "automation", "templates", "webhooks", "functions"];
```

   and update the field's doc comment, which currently says "The six blocks the format showcase
   declares", to say five.

2. **Delete** `The_access_line_names_the_unenforced_restriction_and_the_uncompiled_rule` entirely.
   It asserts the wording of an entry that no longer exists, and keeping it green by pointing it
   somewhere else would be the vacuity this whole file exists to refuse.

3. Add the fact that replaces it — the direction that catches the entry being put back:

```csharp
    /// <summary>
    /// <b><c>access</c> is off the table because it is <em>honoured</em> now</b> — the transition the
    /// file itself demanded: <em>"the day the surface lands, <c>access</c> is either honoured or refused
    /// — never warned about."</em>
    /// </summary>
    /// <remarks>
    /// This is not the same claim as <see cref="Branding_is_not_on_the_table_because_its_absence_is_merely_visible"/>.
    /// <c>branding</c> is off the table because it earns no entry under either limb of the rule;
    /// <c>access</c> is off it because there is nothing left to warn about — the three levels are
    /// compiled at apply, their role literals are validated against <c>auth.roles</c>, and a caller who
    /// matches no level is refused. A warning beside working enforcement would tell an author their
    /// restriction does nothing, which is now false and is the worse of the two directions.
    /// </remarks>
    [Fact]
    public void Access_is_not_on_the_table_because_it_is_enforced()
    {
        UnhonouredSubsystems.All
            .Select(subsystem => subsystem.Block)
            .ShouldNotContain(
                "access",
                "the three levels are compiled at apply and a caller matching none is refused, so a "
                + "warning saying the block does nothing would now be false");
    }
```

4. Rewrite the `Branding_is_not_on_the_table_because_its_absence_is_merely_visible` **message**,
   which currently reads *"unlike 'access', where nothing happening is exactly what a working
   restriction looks like"* — a contrast with a table entry that is gone. Replace the `ShouldNotContain`
   reason with:

```csharp
                "an unstyled dashboard is an unmet expectation the author sees the moment they look, "
                + "so it earns an entry under neither limb of the rule — not limb one (nothing to "
                + "misattribute) and not limb two (its name promises styling, and no styling is what "
                + "the author gets)");
```

   and update that fact's own remarks paragraph the same way: the sentence that positioned
   `branding` against `access` must now stand on `branding`'s own reasons.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*UnhonouredSubsystemsTests*'`
Expected: FAIL — `Access_is_not_on_the_table_because_it_is_enforced` reports that the set contains
"access", and `The_showcase_declares_exactly_the_blocks_the_expected_set_names` reports the extra
element, because the table still carries the entry.

- [ ] **Step 3: Remove the entry**

In `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs`, delete the whole `new("access", …)`
element from `All`.

- [ ] **Step 4: Rewrite the two paragraphs that describe a warning that no longer exists**

This is not optional tidying: the file's remarks are the argument for the table's contents, and
two of them are now about an entry that is gone.

1. Replace the paragraph beginning *"`access` is on the table under limb two; `branding` is on
   neither and stays out"* with:

```
/// <para>
/// <b>Both <c>access</c> and <c>branding</c> are off the table, for two different reasons, and the
/// difference is worth keeping.</b> An earlier version of this file carried <c>access</c> under limb two
/// — its <em>name</em> promised a restriction it did not enforce, and nothing happening is exactly what
/// a working restriction looks like, so there was no looking that found it out. #146 ended that: the
/// three levels are compiled at apply against the <see cref="CelProfile.Access"/> profile, their role
/// literals are validated against <c>auth.roles</c> exactly as a rule's are, and a caller who matches no
/// level is refused. The entry left with the warning, which is the transition this file demanded of
/// itself. <c>branding</c> is off the table on the original argument and stays there: an author who
/// writes it and sees no logo has looked and found out, so it qualifies under neither limb — and the day
/// the dashboard renders it, nothing changes here either.
/// </para>
```

2. Replace the paragraph beginning *"Why `access` is warned about rather than refused"* with a
   paragraph that states the general rule it was a worked example of, since the example is gone:

```
/// <para>
/// <b>Where the line between this table and <see cref="UnhonouredFeatures"/> falls, now that its
/// sharpest case has left.</b> That table refuses what silently produces wrong data on a live write
/// path — the three <c>before*</c> hooks are refused because "a write the author believes is vetted is
/// neither". This one warns about a subsystem whose absence permits nothing <em>now</em>: refusing it
/// would refuse a descriptor whose only defect is being ahead of the implementation, and the descriptor
/// is meant to outlive any one build. <c>access</c> was the case where the two arguments nearly met, and
/// it was resolved the way the rule predicts: it stayed warned about while the administration surface
/// did not exist, and it left the table the moment enforcement landed rather than being refused.
/// </para>
```

3. The `[LoggerMessage]` remark beginning *"The preamble no longer offers 'because their absence is
   observable' as the blanket reason"* argued from `access` being the one entry whose absence was
   *not* observable. Every remaining entry is on limb one again, so replace it with:

```
    /// <remarks>
    /// <b>The preamble still does not offer "because their absence is observable" as the reason</b>, even
    /// though every remaining entry is once again on limb one. It was removed when <c>access</c> joined,
    /// because it pointed the reassuring way about the one security-relevant block the line named; putting
    /// it back would re-create a sentence that is true only for as long as no limb-two entry is added, and
    /// the next one would have to remember to remove it again. What is there instead holds for both limbs
    /// and says nothing about observability.
    /// </remarks>
```

- [ ] **Step 5: Run to verify they pass**

```bash
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*UnhonouredSubsystemsTests*'
dotnet test --project test/MMLib.Alvo.Tests -- --filter-namespace 'MMLib.Alvo.Tests.Migrations'
```
Expected: PASS for both. The second matters because
`DescriptorBootPlanTests.A_declared_but_unhonoured_block_warns_on_every_boot_naming_it` and
`SchemaMigrationRunnerTests.Applying_a_descriptor_that_declares_an_unhonoured_block_warns_naming_it`
drive the warning from the apply path; if either names `access`, update it to name a block that is
still on the table.

- [ ] **Step 6: ring0 + commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs \
        test/MMLib.Alvo.Tests/Descriptor/UnhonouredSubsystemsTests.cs \
        test/MMLib.Alvo.Tests/Migrations/
git commit -m "feat(access): take access off the unhonoured table, now that it is honoured"
```

---

### Task 6: The example says one honest thing

**Files:**
- Modify: `examples/complex-crm/crm.alvo.json`
- Modify: `examples/complex-crm/NOT-RUNNABLE.md`
- Modify: `test/MMLib.Alvo.Schema.Tests/canonical-complex-crm.verified.txt` *(via Verify, never by hand)*

**Interfaces:** none — this task changes a fixture and the prose that explains it.

**The decision, recorded here because #146 §3 asked for one and the plan is where it is taken.**

`crm.alvo.json` reads today:

```json
"access": {
  "admin": "'manager' in @user.roles",
  "developer": "'manager' in @user.roles",
  "viewer": "'sales' in @user.roles || 'manager' in @user.roles || 'finance' in @user.roles"
}
```

`admin` and `developer` are the **same predicate**. `NOT-RUNNABLE.md` already records why — the
domain gate that distinguished them (`@user.email.endsWith('@firma.sk')`) is inexpressible under a
role-only `@user`, and inventing a role to keep them apart would assert an org shape this CRM never
had. That was the right call while the block was inert. **It stops being the right call the moment
the block is enforced**, because with the levels resolved highest-match-wins, a `developer`
predicate identical to `admin` can never be the highest match for anyone: it is a level the
descriptor declares and no caller can ever hold. The showcase's job is to exercise the schema's
shape, and a level that is dead by construction teaches the wrong shape.

**The choice: delete `developer` from the example, leave `admin` and `viewer`, and say so.** Two
levels the CRM really has, no invented role, no dead configuration. The alternative — keeping a
provably unreachable level for coverage's sake — trades a true example for a schema-key tick, and
Task 6 step 4 covers the key a different way.

- [ ] **Step 1: Write the failing test**

Add to `test/MMLib.Alvo.Tests/Rules/AccessCatalogBuilderTests.cs`:

```csharp
    /// <summary>
    /// <b>The format showcase's <c>access</c> block declares no level that is dead by construction.</b>
    /// Levels resolve highest-match-wins, so a level whose predicate is identical to a higher one can
    /// never be the highest match for anybody — it is configuration the descriptor declares and no
    /// caller can ever hold, in the one file whose job is to show the schema's shape correctly.
    /// </summary>
    [Fact]
    public void The_showcase_declares_no_unreachable_access_level()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "examples", "complex-crm", "crm.alvo.json");
        var access = AlvoDescriptor.Parse(File.ReadAllText(path)).Access.ShouldNotBeNull();

        var declared = new[] { access.Admin, access.Developer, access.Viewer }
            .Where(level => level is not null)
            .ToList();

        declared.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            declared.Count,
            "two levels with the same predicate mean the lower one can never be the highest match");
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AccessCatalogBuilderTests*'`
Expected: FAIL — `declared.Distinct().Count()` is 2 against `declared.Count` of 3.

- [ ] **Step 3: Change the example**

In `examples/complex-crm/crm.alvo.json`, replace the `access` block with:

```json
  "access": {
    "admin": "'manager' in @user.roles",
    "viewer": "'sales' in @user.roles || 'manager' in @user.roles || 'finance' in @user.roles"
  },
```

- [ ] **Step 4: Keep the third schema key covered somewhere true**

Run: `dotnet test --project test/MMLib.Alvo.Schema.Tests`
If the corpus only reached `access.developer` through this file, add a minimal positive sample that
declares all three levels with three **distinct** predicates to the schema suite's own valid-sample
corpus — `access` is a schema key and must stay covered by the schema tests, which is a different
job from the showcase's. Record in the commit message which corpus file took it.

- [ ] **Step 5: Rewrite the prose**

In `examples/complex-crm/NOT-RUNNABLE.md`:

1. The heading *"It also declares six blocks that are *warned about*, not refused"* becomes
   **five**, and the `access` row leaves the table.
2. Delete the paragraph beginning *"**`access` is on that list and `branding` is not**"* — the two
   blocks are no longer on opposite sides of anything — and replace it with:

> **Neither `access` nor `branding` is on that list any more, for two different reasons.** `access`
> left it when #146 landed: the three levels are compiled at apply, their role literals are
> validated against `auth.roles` exactly as a rule's are, and a caller who matches no level is
> refused — so a warning saying the block does nothing would now be false. `branding` was never on
> it: an author who writes it and sees no logo has looked and found out, which earns an entry under
> neither limb of the rule.

3. Rewrite the section *"`access` is **role-based only**, and what this file lost to that is
   `admin`"*. Keep the whole history — the original expression, why `@user.email` is inexpressible,
   why inventing a role was refused — and replace its closing (*"So `admin` and `developer` are now
   the same predicate, and that is the finding rather than a mistake to tidy"*) with:

> **`developer` is now gone from this file, and that is the enforcement landing rather than the
> finding changing.** While the block was inert, two identical predicates were an honest record of
> what the narrowing cost. Once the levels are enforced, they resolve highest-match-wins — so a
> `developer` predicate identical to `admin` is a level no caller can ever hold, and a showcase
> whose job is the schema's shape would be teaching a shape that cannot work. What is left is the
> two levels this CRM really has: managers administer it, and everyone in sales, management or
> finance can look. The third key stays covered by the schema suite's own corpus, which is where a
> key's coverage belongs.
>
> Attribute-based rules — typed claims, `@user.teams` — are #37's scope, and widening `@user` is
> additive, so these expressions keep compiling on the day they land, and `developer` can come back
> with the distinguishing half it originally had.

- [ ] **Step 6: Run, accept the canonical snapshot, and run e2e**

```bash
dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AccessCatalogBuilderTests*'
dotnet test --project test/MMLib.Alvo.Schema.Tests
```
Expected: the first PASSes; the second FAILs on
`test/MMLib.Alvo.Schema.Tests/canonical-complex-crm.verified.txt`, which canonicalises this very
file. Accept the `.received.` with Verify — **never by hand** — re-run, PASS, and dispatch
`alvo-snapshot-judge`: a canonical snapshot moving because the source descriptor changed is exactly
the case it exists to rule on.

Then, because an example descriptor changed and the API invariant suite boots the examples:

```bash
scripts/test-ring2
scripts/test-e2e
```

- [ ] **Step 7: Commit**

```bash
git add examples/complex-crm/crm.alvo.json examples/complex-crm/NOT-RUNNABLE.md \
        test/MMLib.Alvo.Schema.Tests/ test/MMLib.Alvo.Tests/Rules/AccessCatalogBuilderTests.cs
git commit -m "fix(access): make the showcase's access block say one honest thing"
```

---

### Task 7: The documents, and the issues

**Files:**
- Modify: `docs/architecture/cel.md`
- Modify: `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` *(the §8 checklist only)*

**Interfaces:** none.

- [ ] **Step 1: Add the fifth column to `cel.md`**

Rename the section *"## The four profiles"* to *"## The five profiles"*, change its opening
sentence accordingly, and add an `Access` column to the truth table. The table's rows, verbatim,
become:

| Construct | Rule | Computed | Condition | Mutate | Access |
|---|---|---|---|---|---|
| Literal | ✓ | ✓ | ✓ | ✓ | ✓ |
| Field ref, current row (`owner_id`) | ✓ | ✓ | ✓ | ✓ | ✗ |
| Field ref, `old.`/`new.` | ✗ | ✗ | ✓ | ✓ | ✗ |
| `@user` context ref | ✓ | ✗ | ✓ | ✗ | ✓ |
| `@tenant` context ref | ✓ | ✗ | ✓ | ✗ | ✗ |
| `&&` / `\|\|` / `!` | ✓ | ✓ | ✓ | ✗ | ✓ |
| Comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`) | ✓ | ✓ | ✓ | ✗ | ✓ |
| `in` (role membership) | ✓ | ✗ | ✓ | ✗ | ✓ |
| `has(field)` | ✓ | ✓ | ✓ | ✗ | ✗ |
| Arithmetic (`+ - * /`, unary `-`) | ✗ | ✓ | ✗ | ✗ | ✗ |
| Ternary conditional | ✗ | ✓ | ✗ | ✗ | ✗ |
| `changed(field)` | ✗ | ✗ | ✓ | ✗ | ✗ |
| Allow-listed function call (`lowerAscii`, `now`) | ✗ | ✗ | ✗ | ✓ | ✗ |

> **Note the row that split.** `@user` and `@tenant` were one row (`@user`/`@tenant` context ref)
> because no profile had ever wanted one without the other. `Access` does, so the row is two rows
> and `_allowedProfiles` has two kinds. Say so in the prose under the table, or a reader comparing
> this file to an older revision reads the split as a transcription error.

Add a bullet to the per-profile list beneath it:

> - **Access** — a management-access level (`access.admin` / `access.developer` / `access.viewer`).
>   Must evaluate to `Bool`. Sees `@user` and **nothing else**: no row, so no field reference of
>   either state and no `has()`/`changed()`; and no `@tenant`, because an access level is
>   *project*-scoped by the frozen schema's own description and admitting `@tenant` would make a
>   project-level predicate answer differently per request. **Interpreter-only**, like `Mutate` —
>   and refused at the renderer by profile rather than by node kind, because unlike `Mutate` every
>   construct it can contain is one the renderer would otherwise render perfectly well, into a
>   `WHERE` clause over a table an access level never names. Role literals are validated at apply
>   against `auth.roles`, by the same walk that validates a rule's.
>
>   **What `@user.id` can and cannot do here, stated so it is not read as an oversight.** The column
>   admits it, because the frozen schema names it as one of the two members a level may read — but
>   Alvo's grammar has no `uuid` literal (deviation 13) and an access level sees no row, so there is
>   no `Uuid`-typed operand in scope for it to meet. A level written as
>   `@user.id == '…'` is therefore refused by the *comparison* rule ("Cannot compare Uuid to
>   String"), not by the profile table. Both the member and the comparison operator stay admitted
>   deliberately: widening `@user` is additive, so a level written against a future typed claim
>   compiles without this table changing, and admitting the operator today expresses nothing a role
>   membership could not.

- [ ] **Step 2: Correct deviation 1's closing paragraphs**

Deviation 1 in `cel.md` currently ends with: *"**This is a grammar narrowing, not an enforcement
one.** Nothing compiles the `access` block yet — `PolicyCatalogBuilder` walks `schema.Entities`
only, so a level naming a role `auth.roles` does not declare is not reported today.
`UnhonouredSubsystems` warns at apply that the block is inert, which is the whole of what happens
to it (`#146`)."* Every sentence of that is now false. Replace it with:

> **It is now an enforcement narrowing too, and that is #146 landing.** `PolicyCatalogBuilder`
> compiles the three `access` levels in the same pass as every rule, against the same
> `RoleCatalog`, so a level naming a role `auth.roles` does not declare is refused at apply with the
> same "did you mean" suggestion a rule's typo gets. `UnhonouredSubsystems` no longer carries an
> `access` entry — the file's own doc comment demanded that transition — and a caller who matches no
> level is refused `403` on every management operation.

Also correct the paragraph beginning *"What it costs now is visible in `examples/complex-crm`"*: it
states that *"the two collapse to one predicate"*, which Task 6 resolved by removing `developer`.
Point it at the example's current shape and at `NOT-RUNNABLE.md`'s rewritten section.

- [ ] **Step 3: Tick the design's §8 list**

In `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` §8, mark the two entries this
plan delivers — `docs/architecture/cel.md` (the fifth profile) and
`src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs` (the `access` entry leaves) — as done,
naming this plan. Leave the rest for their own issues.

- [ ] **Step 4: Update the issues**

- **#146** → closed by this plan. Comment with the three answers it asked for:
  (1) `access` is **role-based only**, which the frozen schema had already settled and `cel.md`
  deviation 1 now records as enforced rather than narrowed;
  (2) the warning is gone because the block is **honoured**, not because it was tidied — the entry
  and both arguing paragraphs left together;
  (3) the example says one honest thing: `developer` was removed as unreachable-by-construction,
  with the reasoning recorded in `NOT-RUNNABLE.md` rather than only in a commit message.
- **#212** → comment the obligation this plan creates: every route it adds must carry
  `RequireAlvoManagementAccess(<operation>)`, and `ManagementOperation` already names the thirteen
  operations §2.2 lists. A route without the gate is the way this surface quietly opens, and the
  count fact in `ManagementOperationsTests` is what forces a decision for a fourteenth.
- **#37** → note that `@user` is now read by a fifth profile, so widening it with typed claims
  additively widens `access` too, and a role-based level keeps compiling.
- **#227** → note that its two blockers (#248, #146) are cleared.

- [ ] **Step 5: ring2 + commit**

```bash
scripts/test-ring2
git add -u docs/
git commit -m "docs(access): record the fifth CEL profile and correct deviation 1's enforcement claim"
```

---

## Before opening the PR

- [ ] `scripts/test-ring2` green; `scripts/test-e2e` green (Task 6 changed an example descriptor).
- [ ] `dotnet format --verify-no-changes` clean — especially if any `.cs` file was written from a
      shell; `.gitattributes` pins `*.cs` to CRLF + BOM.
- [ ] **This change is the security core.** Invoke the `alvo-security-core-review` skill and answer
      its checklist explicitly:
      - *Fail-fast compile* — a level naming a nonexistent role, or a row field, is refused at
        **apply**, never at request time (Task 2).
      - *Default-deny* — no `access` block, an unprimed catalog, a `null` level and a thrown
        evaluation all resolve to `ManagementLevel.None` (Task 3).
      - *Authorization is not a post-filter* — the gate returns instead of calling `next`, so the
        handler never runs for a refused caller (Task 4).
      - *No user input is interpolated* — an access level is evaluated in memory and is **never**
        rendered to SQL; the renderer refuses the profile by name (Task 1).
      - State plainly which items do **not** apply: cross-tenant isolation and the dynamic-entity
        predicate, because `access` governs the Management API and never a data query, and
        `@tenant` is refused by the profile precisely so it cannot.
- [ ] Run `/security-review` over the diff, or dispatch a reviewer subagent and label it plainly as
      the substitute. Fix findings **before** the PR opens.
- [ ] Run `/code-review high` (or its dispatched substitute) — this is a large diff touching the
      CEL type checker, which is the one file where an off-by-one in a profile table is invisible.
- [ ] **Label the PR `needs-deep-review`.**
- [ ] Dispatch `alvo-plan-guard` (read-only, advisory) as the last check.
- [ ] Dispatch `alvo-snapshot-judge` for every moved `*.verified.*` baseline
      (`PublicApi.MMLib.Alvo.Abstractions`, `canonical-complex-crm`).
- [ ] Justify the one symbol added to `PublicApi.MMLib.Alvo.Abstractions.verified.txt`
      (`CelProfile.Access`) against the `alvo-architecture-rules` *"public is the contract"* rule:
      `CelProfile` is public because `ICelCompiler.Compile` takes it, so a new member cannot be
      `internal`. Everything else this plan adds is `internal`.
- [ ] Build the PR report with the `alvo-pr-report` skill; the PR body is the five-line pointer.

## Self-review of this plan

**Spec coverage.** §3.1 (role-based only, recorded rather than decided) → Task 7 steps 2 and 4.
§3.2 (the fifth profile, its truth table, `@tenant` excluded deliberately, role literals validated
at apply) → Tasks 1, 2, 7. §3.3 (what each level governs; "settings" named; levels are independent
predicates, highest match wins; no match and not the bootstrap admin → 403) → Tasks 3, 4. §3.5 (the
bootstrap admin bypasses `access`; no `access` block means only the bootstrap admin) → Task 3
(`The_bootstrap_admin_is_an_admin_whatever_the_descriptor_says`,
`A_descriptor_with_no_access_block_admits_nobody`). §3.6 (the warning-to-enforcement transition) →
Task 5. §6.1 rows: *"The `Access` profile is closed — unit per construct, ring0"* → Task 1's eight
refusal facts; *"`access` is actually enforced — a caller matching no level gets 403"* → Task 4
step 1 (at the filter, in ring0, because #212's routes do not exist yet to carry a ring2
integration fact — recorded on #212 in Task 7 step 4 as an obligation it inherits). #146's own
three asks → Task 7 step 4.

**Gap found and closed.** The spec's §6.1 wants the 403 pinned by an *integration* test over "every
management route", and there are no management routes: #212 is a separate issue. Rather than write
a test that cannot exist, Task 4 pins the same claim at the filter and Task 7 records the
obligation on #212 — the route-level integration fact belongs with the routes. Stating the
substitution here is what keeps it from reading as an omission.

**Placeholder scan.** No step says "add validation", "handle edge cases" or "similar to Task N".
Every code step carries the code; every run step carries the exact command and the exact expected
failure text. Three steps instruct the executor to *read an existing file and reuse its helper by
its real name* (the `IFieldSqlRenderer` fake in Task 1, the existing `Validate`-style helpers where a
suite already has one, and the schema corpus in Task 6 step 4) rather than inventing a second one — a
naming instruction, not a placeholder, and the surrounding fact bodies are complete. Task 4's result
reader is written out in full rather than borrowed, because the one that exists
(`MMLib.Alvo.Api.Tests.ProblemDetailsTests.SlugWrittenByAsync`) is in a different assembly; the plan
says so at the helper rather than leaving the duplication unexplained.

**Type consistency, within this plan.** `CelProfile.Access` (Task 1) is consumed by
`AccessCatalogBuilder.CompileLevel` (Task 2). `ManagementAccessCatalog(Admin, Developer, Viewer)`
and `PolicyCatalog.ManagementAccess` (Task 2) are consumed by `ManagementAccessEvaluator.HighestMatch`
(Task 3) — same three property names, same `CompiledExpression?` type. `ManagementLevel` and
`ManagementOperation` (Task 3) are consumed by `ManagementOperations.RequiredLevel`,
`ManagementAccessEvaluator.Allows` and `ManagementAccessEndpointFilter` (Tasks 3, 4), with
`RequireAlvoManagementAccess(RouteHandlerBuilder, ManagementOperation)` taking the same enum.
`ProblemResultFactory.ManagementForbidden()` (Task 4) is called only from the filter.
`PolicyCatalogBuilderProbe.TryBuild(Access?, out PolicyCatalog?, out IReadOnlyList<DescriptorValidationError>)`
(Task 2) is reused unchanged by Task 3's fixtures.

**Type consistency, across both plans.** This plan consumes exactly one signature from
`docs/superpowers/plans/2026-09-18-f5-identity.md`:
`IAlvoBootstrapAdmin.IsBootstrapAdmin(UserId user)`, in `namespace MMLib.Alvo`, injected into
`ManagementAccessEvaluator`'s constructor (Task 3) and substituted in its facts. Plan 1 Task 1
defines it with that exact name, namespace, parameter type and return type, and registers the
`NoBootstrapAdmin` default in `AddAlvoAuth` — which is what makes `ManagementAccessEvaluator`
resolvable in a host that has not installed `MMLib.Alvo.Identity`. Nothing else crosses the two
plans: `IAlvoUserStore` and `AlvoUser` are Plan 1's alone, and `ManagementLevel` /
`ManagementOperation` are this plan's alone.
