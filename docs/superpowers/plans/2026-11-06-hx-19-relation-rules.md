# HX-19 — Relation rules and lookup `checks` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A rule may read a related row. A `ref` field navigates forward (`customer_id.owner_id == @user.id`), and a child entity that references this one is reached with CEL's standard `exists` macro (`project_members.exists(m, m.user_id == @user.id)`). Each such term compiles to a correlated `EXISTS` subquery that ANDs in the related entity's own `list` rule and tenant scope, so a relation never matches a row the caller could not list. A cycle among `list` rules is refused at apply. `entities.<e>.checks: [{ lookup, expr, message }]` refuses a write whose looked-up row fails `expr`, with 422, the author's message and a pointer at the lookup field.

**Architecture:** There is one internal AST node, `CelRelation` (Abstractions, `InternalsVisibleTo`), plus `CelOuterFieldRef` for a reference to the outer row from inside it. The parser builds relation syntax only when a compile is given a `CelRelationCatalog`, and only the rule and check compiles in `PolicyCatalogBuilder` pass one. Every other CEL slot therefore keeps today's refusal byte for byte. After type checking, a lifter turns each forward leaf comparison into a `CelRelation`. The `exists` macro yields one directly. `PolicyEngine.Resolve` *binds* every relation to the related entity's `list` decision for the same caller: the related `USING` and tenant scope, or a constant-false scope when that decision is a denial. It caches the result per catalog, entity, operation and caller-presence bits. The decision's predicates are therefore self-contained. `SqlPredicateRenderer` renders a bound relation as `EXISTS (SELECT 1 FROM <related> AS alvo_rN WHERE alvo_rN.<key> = <outer key> AND (<body>) AND (<related USING>) AND (<related tenant scope>))`. The outer row is qualified by its table name in a read. In a write probe it is bound from the post-image. `WITH CHECK` and lookup checks run in memory today. For both, the EF driver evaluates each relation term with one in-transaction `SELECT CASE WHEN EXISTS(…) THEN 1 ELSE 0 END`, substitutes the boolean into the tree, and hands the rest to the interpreter.

**Tech Stack:** .NET 10 (C# 14), EF Core 10 (SQLite, Npgsql with PostgreSQL 16 in Testcontainers), xUnit v3 on MTP, Shouldly, Verify + PublicApiGenerator, CsCheck, Blazor (dashboard), Microsoft.Playwright (admin e2e), Astro/Starlight.

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`:
- §7 *HX-19* (scope, acceptance)
- §1.1 goal 7
- §1.2 (dynamic entities refused, #41)
- §5 row `19`
- §6.1 row *relation rules* ("correlated `EXISTS` … related `USING` ANDed inside")
- §6.2 rows *injection via … relation rules* and *reads*
- Appendix A minor 17 (cycle detection)
- inherited: *HX-11 settled details* on `rollup.where` and `via` resolution (`RollupResolver.ResolveVia`, reused verbatim); §4.12 and *HX-16* Decision 3 ("a narrowing is a view, not a boundary")

Issue **#396** (epic #370). Analysis `baas-analyza.md` §2.4 says rules compile to SQL predicates (never a post-filter), aggregations and exists must run over the policy-filtered set ("information leak cez agregácie"), and property tests must prove no interpolation. Frozen artifacts read: `schema/project.schema.json` (`$defs/rules`, `$defs/entity`, `field.rollup.via`), `CelNode.cs`, `CompiledExpression.cs`, `IPredicateRenderer.cs`, `IFieldSqlRenderer.cs`, `PolicyDecision.cs`, `AlvoConstraintViolationException.cs`, `ManagementModels.cs` (`ManagementPolicyVerdict`).

**Prior art.**
- PostgreSQL RLS: a policy's `USING` may hold an `EXISTS` sub-select, and that sub-select is itself filtered by the referenced table's policies. That is exactly "related `USING` ANDed inside".
- CEL spec: `e.exists(x, p)` is the standard macro, and field selection is `a.b`.
- PocketBase API rules: `relation.field` forward navigation and `collection_via_field` back-relations. The `_via_` spelling is used only when the rollup-style resolution is ambiguous.
- Firebase `get()`/`exists()`: a lookup by id inside a rule.
- PostgreSQL `RI_FKey_check_upd`: a foreign key is re-checked on update only when the key changed. Lookup checks follow this.
- SQL `CHECK`: `UNKNOWN` passes, so a null lookup skips its check.

## Global Constraints

**Worktree and branches**
- Worktree: `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx19`. This plan is committed on `hx/19-relation-rules`, off `origin/main` `d35941b8` (HX-16a/b merged).
- Slice A starts with `git branch -m hx/19a-relation-rules`. Slice B branches from `main` **after A merged**: `git switch -c hx/19b-relation-surfaces origin/main`.
- One writer per worktree. Stage files by path, never `git add -A`. Run `git branch --show-current` before every commit and push. Never `git clean`.

**Two PRs to `main`, each green and shippable alone:**
- **A** `hx/19a-relation-rules`: Tasks 1–8. The whole engine, the `checks` schema and model, both-engine facts, and this plan.
- **B** `hx/19b-relation-surfaces`: Tasks 9–11. Simulator relations, dashboard, docs page, skill, architecture doc, spec, PLAN, CHANGELOG.
- A's PR body says `Part of #396`. B's says `Closes #396`. No `Closes`/`Fixes` keyword in any commit message.

**Parallel work:** HX-12, HX-13 and HX-16c merge independently. HX-19 appends **no** enum value: not to `AlvoExtensionKind`, `AlvoExtensionStage` or `AlvoConstraintKind`. HX-12 appends `Exclusion` to that last one, which is why Decision 13 reuses `Check`. The shared files get small additive edits only: `schema/project.schema.json` (one `entity.checks` property plus the `rules` description), the spec, `docs/PLAN.md`, `CHANGELOG.md`, `PublicApi.*.verified.txt`, and `plugins/alvo/skills/*` (6 144-byte and 200-line caps, `SkillConformanceTests`).

**Public surface added is exactly** (each item is justified in Decisions):
- Abstractions `MMLib.Alvo.Descriptor`: `EntityDescriptor.Checks` (`IReadOnlyList<EntityCheck>?`, init) and `EntityCheck` (sealed record; `required string Lookup`, `required string Expr`, `required string Message`, all init). *Slice A.*
- Abstractions `MMLib.Alvo.Management`: `ManagementPolicyVerdict.Relations` (`IReadOnlyList<ManagementRelationVerdict>`, init, default `[]`) and `ManagementRelationVerdict(string Via, string Entity, bool Allowed, string? Using, string? TenantScope)`. *Slice B.*
- Everything else is `internal`:
  - `CelRelation`, `CelRelationKind`, `CelOuterFieldRef`, `RelationScope`, `CelRelationCatalog`, `CelRewriter`
  - `RelationSql` and `RowSource`
  - the two internal `IPredicateRenderer` members and the internal `ICelCompiler` overload
  - `PolicyDecision.Checks`, `DescriptorCheck`, `AlvoConstraintViolationException.DescriptorMessage`
  - `PolicyParameterPrefix.Probe`/`.Outer`
  - every core type: `CelHop`, `CelPath`, `CelExistsMacro`, `RelationLifter`, `RelationGraph`, `RelationBinder`, `RelationCatalogBuilder`, `CheckCompiler`
  - every driver type: `RelationProbe`

**Wire constants (verbatim):**
- Apply refusals, at `/entities/<e>/rules/<op>` or `/entities/<e>/checks/<i>/expr`. Each has `fix` = the sentence after "—":
  - `Rule cycle: the list rules of '<a>' → '<b>' → '<a>' read each other, so no caller's rows can be decided.` — "Move the condition into one of the two rules, or replace one relation with a rollup or a stored field."
  - `'<e>.<path>' reads relations <n> levels deep; at most 3 are allowed.` — "Read a stored field or a rollup on a nearer entity."
  - `A relation is negated here. A rule may only narrow as related rows become invisible, so a relation cannot sit under '!'.` — "Say what must hold instead, or count the rows with a rollup and compare the count."
  - `One comparison reads two relations ('<a>', '<b>').` — "Compare each relation with a value or the caller, in its own clause."
  - `'<ref>' is not a reference on '<e>', so '<ref>.<field>' has nothing to follow.` — "Follow a 'ref' field of '<e>' (<list>), or reach a child with <child>.exists(x, …)."
  - `'<target>' is <the built-in users entity | dynamic (#41) | not declared>, so a rule cannot read its rows.` — "Compare the ref itself, e.g. '<ref> == @user.id'."
  - Ambiguous reverse relation: the `RollupResolver.ResolveVia` text with "rolls up from" → "reaches", plus the fix "Name the reference: <child>_via_<field>.exists(…)".
  - `A lookup check must read '<lookup>': write the condition over '<lookup>.<field>'.` — "Use field 'validation' for a condition over this row alone."
  - `A lookup check reads no caller: '<@user|@tenant>' belongs in rules.` — "Move the condition into the entity's rules."
- Apply warning (`Warning`, same path): `'<related>' has no list rule, so this relation never matches any row.`, fix "Give '<related>' a list rule."
- Data API refusal: 422 `validation`, violation `{ pointer: "/<lookup>" (or "" when the lookup field is hidden from the caller), code: "check", message: <the check's message>, fixSuggestion: "Point '<lookup>' at a record that satisfies this check, or change the values the check compares." }`.
- Port refusal: `AlvoConstraintViolationException`, `Kind = Check`, `Fields = [lookup]` (or `[]` when hidden), `Message = <the check's message>`.

**Engine parity.** Every data-path fact runs on SQLite and PostgreSQL:
- `src/MMLib.Alvo.Testing/Data/AlvoDataRelationRuleTests.cs` is abstract, the `AlvoDataComputedRollupTests` pattern. It is derived by `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataRelationRuleTests.cs` and `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataRelationRuleTests.cs`.
- `test/_shared/api/DataApiEngineTests.Relations.cs` runs on SQLite in `SqliteDataApiTests` and on PostgreSQL in `DataApiOnPostgresTests`.
- **Every new PostgreSQL test class carries the Windows skip guard** (`Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.")` in `InitializeAsync`, the `PostgreSqlTimerStoreTests` precedent).

**Security core (§6.2 lists HX-19).** The stack-level security review comes after the stack, so no per-PR `/security-review` is scheduled. Both PRs carry the `needs-deep-review` label.

**C# style** follows `alvo-dotnet-conventions`: no inline comments, methods of about 25 lines at most, `ConfigureAwait(false)` in `src/`, `TestContext.Current.CancellationToken` in tests, and XML docs on every public member that cite no `§` or `#issue`. **`.cs` files are UTF-8 with BOM and CRLF.** After editing, run `dotnet format whitespace MMLib.Alvo.slnx --include <file> …`.

**Testing per task:**
- Run only the affected projects, by class: `dotnet test --project <proj> --filter-class '*<Class>'`, plus the pre-commit hook's scoped ring0.
- On each **[slice end]** task: `scripts/test-ring1`, then `dotnet build -c Release MMLib.Alvo.slnx` (CI is Release).
- Admin e2e runs filtered (`scripts/test-admin-e2e --filter RelationRuleScenarios`), and only in Task 9.
- Never run Stryker or `scripts/test-load`.

**Schema:** after editing `schema/project.schema.json`, run `python3 scripts/gen-prototype-fixtures` and commit the regenerated `docs/design/f5-admin/generated/*`.

**Baselines:** a grown `PublicApi.*.verified.txt` or any moved `*.verified.*` trips the turn-review gate. Dispatch `alvo-snapshot-judge`, and justify each added symbol against the list above (`alvo-architecture-rules`, *public is the contract*).

**Commits** are Conventional Commits, one per task, each message ending `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. On each **[slice end]**: run `alvo-plan-guard`, then the `alvo-pr-report` skill, then `gh pr create --label needs-deep-review`.

## Decisions (not settled by the spec)

Recorded in the spec as *HX-19 settled details* (Task 11). Each line is the decision, then the cost if it is wrong.

1. **Syntax.** Forward is `<ref>.<field>`. Reverse is CEL's `<child>.exists(x, p)`, with `<child>_via_<field>` when the child references this entity more than once (rollup `via` resolution, PocketBase spelling). — Renaming syntax later is a breaking descriptor change.
2. **One hop per term.** Deeper reach composes through the related entity's own `list` rule, which is ANDed in. A chain deeper than 3 is refused at apply. — A 4-deep chain needs a stored field or a rollup.
3. **A relation term is refused under `!` in rules,** so a rule only narrows as related rows vanish. `!=` inside a term is fine. — "No blocking row" needs a rollup count.
4. **`EXISTS` carries the related `list` USING plus its tenant scope.** A denied related `list` is a constant `FALSE`. **Host narrowings are not ANDed in**: a narrowing is a view, writes are not narrowed (§4.12, HX-16 D3), and AC (2)'s "cannot list" means *by rules*. — A host using a narrowing as security sees relations match rows it hides; the fix is to put that condition into the rule.
5. **Relations are legal only in `rules.*` and `checks[].expr`.** Hook conditions, `validation`, `rollup.where`, `hidden`/`readOnly`, command access and `computed` keep today's refusal unchanged, because the parser builds relation nodes only on request. — None; widening a slot later is additive.
6. **Internal nodes.** A tree holding `CelRelation` is refused by the public `IPredicateRenderer.Render`, the interpreter and `InMemoryAlvoData`, each with `NotSupportedException`. §5's "no CEL AST node is added" holds for the public API. — A third-party renderer or the in-memory double cannot run relation rules.
7. **Binding happens in `PolicyEngine.Resolve`,** cached per (catalog, entity, operation, has-user, has-tenant). Those are the only inputs a related decision's allow or deny depends on; role literals render later. — A wrong key binds a wrong scope; `PolicyEngineRelationTests` pins all four bit combinations.
8. **`WITH CHECK` relation terms are probed in SQL inside the write's transaction,** with the outer values bound from the post-image. The rest stays interpreted. — One round trip per relation term per written row.
9. **A cycle means a cycle among `list` rules, self-loops included.** Other operations and checks only consume `list`. A relation to an entity with no `list` rule draws an apply warning. — Hierarchies cannot write a self-relation in `list`; they use `get`/`update` or a stored field.
10. **Targets are declared physical entities only.** `users` and `storage: dynamic` (#41) are refused. Tenancy may cross: nothing is materialised, so each caller's own related scope applies, unlike rollups. — A global outer row is visible to a tenant through that tenant's related rows only.
11. **A check is forward over `lookup` only, and its whole `expr` is the `EXISTS` body,** so negation inside is allowed and fails closed when the row is invisible. A check refuses `@user`/`@tenant` and allows `now()`. A null lookup skips the check (SQL `CHECK`). — A check cannot read a child or the caller.
12. **Checks run after `WITH CHECK`,** so authorization comes first and a forbidden caller learns nothing. They run on create and batch create, and on update or replace only when a field the check reads changed (`RI_FKey_check_upd`). The first failing check in declaration order refuses. — A related row changed later is not re-validated, as with a foreign key, and an agent fixes one check per round trip.
13. **The refusal is `AlvoConstraintViolationException(Check, [lookup])` plus an internal `DescriptorMessage`.** The author's text is server-owned, not caller-supplied. No `AlvoConstraintKind` value is added, because HX-12 appends in parallel. — A host can tell a lookup check from a column `CHECK` only by `Fields` and `Message`.
14. **Public additions are `EntityCheck` and `EntityDescriptor.Checks`, not "schema-model members" as §5 row 19 says.** Rules and checks live in the descriptor model, not `EntitySchema`. `ManagementPolicyVerdict.Relations` is added so the simulator can name bound scopes. — Two public types more than §5.
15. **The interpreter's `_ => null` default stays, and `CelRelation`/`CelOuterFieldRef` get explicit throwing arms.** A `null` under `!` reads as true, so a missing arm would fail open. — None.

## Review Focus

1. **A self-reference correlates the outer row, not the inner one.** `employees.get` reading `manager_id.region == 'eu'` uses an aliased inner `employees AS alvo_r1` against outer `"employees"."manager_id"`. Covered by `AlvoDataRelationRuleTests.A_self_reference_reads_the_outer_row_s_key` (Task 6, both engines).
2. **Two tenants, a global outer row, a scoped child.** The related tenant scope inside the `EXISTS` is the only isolation. Covered by `AlvoDataRelationRuleTests.A_global_row_is_visible_through_the_caller_s_tenant_children_only` (Task 6).
3. **Interpreter fail-open.** A bound or unbound `CelRelation` reaching `CelInterpreter`, `InMemoryAlvoData` or the public `Render` throws, and never evaluates to `null`/`!null`. Covered by `CelRelationRefusalTests` (Task 1).
4. **Parameter families inside nested `EXISTS`.** Every related scope renders into the same bag as its parent (one prefix per top-level predicate), and `alvo_o`/`alvo_k` join `PolicyParameterPrefix.All`. Covered by `RelationSqlRenderTests.Nested_scopes_share_the_parent_bag_and_never_reuse_a_name` (Task 4).
5. **The cache key.** A caller with no tenant must not get the scope cached for a caller with one. Covered by `PolicyEngineRelationTests.The_bound_scope_is_cached_per_presence_of_user_and_tenant` (Task 3).

## File structure

| Area | Files |
|---|---|
| Abstractions (internal) | `Expressions/CelRelation.cs` (new: `CelRelation`, `CelRelationKind`, `CelOuterFieldRef`, `RelationScope`), `Expressions/CelRewriter.cs`, `Expressions/CelRelationCatalog.cs`, `Expressions/RelationSql.cs` (`RelationSql`, `RowSource`), `Expressions/IPredicateRenderer.cs` (two internal members), `Expressions/ICelCompiler.cs` (internal overload), `Rules/PolicyDecision.cs` (`Checks`), `Rules/DescriptorCheck.cs`, `Data/AlvoConstraintViolationException.cs` (`DescriptorMessage`) |
| Abstractions (public) | `Descriptor/EntityDescriptor.cs` (`Checks`, `EntityCheck`), `Management/ManagementModels.cs` (B) |
| Core | `Expressions/Internal/CelParser.cs`, `CelTypeChecker.cs`, `CelCompiler.cs`, `CelTree.cs`, `CelInterpreter.cs`, `RelationLifter.cs` (new), `CelPath.cs` (new: `CelPath`, `CelExistsMacro`, `CelHop`), `SqlPredicateRenderer.cs` + `SqlPredicateRenderer.Relations.cs` (new); `Rules/Internal/PolicyCatalogBuilder.cs`, `RelationCatalogBuilder.cs`, `RelationGraph.cs`, `CheckCompiler.cs`, `RelationBinder.cs` (new), `PolicyEngine.cs`; `Rules/PolicyCatalog.cs` (`EntityPolicy.Checks`, `OperationPolicy.HasRelations`); `Api/Internal/ProblemResultFactory.cs`; `Management/Internal/AlvoManagementService.cs` (B) |
| Driver | `Internal/ReadStatementComposer.cs`, `PolicyParameterPrefix.cs`, `RelationProbe.cs` (new), `EfAlvoData.cs` (`WriteRefusal` → async, checks) |
| Testing | `Data/AlvoDataRelationRuleTests.cs` (new), `Data/InMemoryAlvoData.cs` (refusal) |
| Schema | `schema/project.schema.json` (`$defs/entity/properties/checks`, `$defs/rules` description) |
| Dashboard (B) | `Components/Rules/PolicyVerdictView.razor`, `RulesTab.razor`, `RelationHints.cs` (new) |
| Docs/skills (B) | `website/src/content/docs/extending/relation-rules.mdx`, `website/src/snippets/extending/relation-rules/*`, `plugins/alvo/skills/alvo-descriptor-rules-and-cel/SKILL.md`, `docs/architecture/relation-rules.md` (new), `docs/architecture/cel.md`, `CHANGELOG.md`, spec, `docs/PLAN.md` |

---

## Task 1: The relation in CEL — nodes, parser flag, type check, lifter, refusals

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Expressions/CelRelation.cs`, `CelRewriter.cs`, `CelRelationCatalog.cs`; `src/MMLib.Alvo/Expressions/Internal/CelPath.cs`, `RelationLifter.cs`
- Modify: `CelParser.cs` (ctor flag `allowRelations`), `CelTypeChecker.cs` (construct kind `Relation`, Rule only), `CelCompiler.cs` (internal overload), `CelTree.cs`, `CelInterpreter.cs`, `ICelCompiler.cs`
- Test: `test/MMLib.Alvo.Tests/Expressions/CelRelationCompileTests.cs`, `CelRelationRefusalTests.cs`; `CelTreeChildrenTests` and `CelAcceptanceCorpusTests` must stay green **with no baseline edit**

**Interfaces:** Produces the nodes and `ICelCompiler.Compile(source, profile, entity, CelRelationCatalog)` (internal DIM; the default ignores the catalog, so it fails closed). `CelRelationCatalog` is built by Task 2.

```csharp
// Abstractions/Expressions/CelRelation.cs — internal; the core, the EF driver and Testing see it.
internal enum CelRelationKind { Forward, Reverse }

/// A correlated read of one related entity: true when a row of Related exists whose RelatedKey equals the outer
/// row's OuterKey, Body holds over it, and the caller may list it (Scope). Body is checked against Related; a
/// CelOuterFieldRef in it reads the outer row. Scope is null until PolicyEngine binds it.
internal sealed record CelRelation(
    CelRelationKind Kind, EntitySchema Related, string RelatedKey, string OuterKey, string Via, CelNode Body,
    RelationScope? Scope = null) : CelNode;

internal sealed record CelOuterFieldRef(string FieldName, CelValueType Type) : CelNode;

/// The related entity's list decision for one caller: Denied renders FALSE; otherwise both predicates are ANDed
/// inside the EXISTS (each may itself hold bound relations).
internal sealed record RelationScope(bool Denied, CompiledExpression? Using, CompiledExpression? TenantScope)
{
    internal static RelationScope Deny { get; } = new(true, null, null);
}

/// What a rule or check compile may follow: forward refs and reverse children of each entity.
internal sealed class CelRelationCatalog(
    IReadOnlyDictionary<string, EntitySchema> entities,
    IReadOnlyDictionary<(string Entity, string Name), (EntitySchema Child, string Via)> reverse,
    Func<string, string, string?> refusal)
{
    internal EntitySchema? Entity(string name) => entities.GetValueOrDefault(name);
    internal (EntitySchema Child, string Via)? Reverse(string entity, string name) =>
        reverse.TryGetValue((entity, name), out var hit) ? hit : null;
    internal string? Refusal(string entity, string member) => refusal(entity, member);
}
```

```csharp
// Abstractions/Expressions/CelRewriter.cs — the one tree rewrite (binder, probe substitution, lifter).
internal static class CelRewriter
{
    internal static CelNode Replace(CelNode node, Func<CelNode, CelNode?> replace) => replace(node) ?? node switch
    {
        CelUnary unary => unary with { Operand = Replace(unary.Operand, replace) },
        CelBinary binary => binary with { Left = Replace(binary.Left, replace), Right = Replace(binary.Right, replace) },
        CelConditional c => c with
        {
            Condition = Replace(c.Condition, replace), WhenTrue = Replace(c.WhenTrue, replace),
            WhenFalse = Replace(c.WhenFalse, replace),
        },
        CelCall call => call with { Arguments = [.. call.Arguments.Select(argument => Replace(argument, replace))] },
        CelRelation relation => relation with { Body = Replace(relation.Body, replace) },
        _ => node,
    };

    internal static bool Any(CelNode node, Func<CelNode, bool> match) => match(node) || node switch
    {
        CelUnary unary => Any(unary.Operand, match),
        CelBinary binary => Any(binary.Left, match) || Any(binary.Right, match),
        CelConditional c => Any(c.Condition, match) || Any(c.WhenTrue, match) || Any(c.WhenFalse, match),
        CelCall call => call.Arguments.Any(argument => Any(argument, match)),
        CelRelation relation => Any(relation.Body, match),
        _ => false,
    };

    internal static bool HasRelation(CelNode node) => Any(node, static n => n is CelRelation);
}
```

**Parser (`allowRelations: true` only).** `ParseIdentifierExpression` handles an identifier that is not `old`/`new` and is followed by `.`:
- `.exists(` + identifier + `,` + conditional + `)` builds `CelExistsMacro(Receiver, Variable, Body, Position)`.
- `.member` not followed by `(` builds `CelPath(Receiver, Member, Position)`.
- A second `.` raises today's "Alvo has no nested field access …" text.

With the flag off, every code path is byte-for-byte today's. `CelTree.Children`: `CelPath` → `[]`; `CelExistsMacro` → `[Body]`; `CelHop` → `[Field]`; `CelRelation` → `[Body]`; `CelOuterFieldRef` → `[]`.

**Type checker.** A new `CelConstructKind.Relation`, allowed in `Rule` only. With a catalog:
- `CelPath(r, f)` where `r` is the current macro variable becomes the child's `CelFieldRef`.
- When `r` is a `ref` field whose target `catalog.Entity` knows, it becomes `CelHop(Via: r, Related, Field: CelFieldRef typed against the target)`.
- Otherwise it is refused with `catalog.Refusal` (wire constants).
- `CelExistsMacro(child, v, body)` resolves `catalog.Reverse(entity, child)`. The body is checked against the child schema with `v` bound. A bare identifier in the body is an outer field and becomes `CelOuterFieldRef`. The result is `CelRelation(Reverse, child, via, "id", child, body)`.
- A path or macro inside an `exists` body, or a `has()` over a path, is refused ("one hop").

**Lifter (rules).** After the check, `RelationLifter.Lift(root)`:

```csharp
internal static CelNode Lift(CelNode node, List<CelCompilationError> errors) => node switch
{
    CelBinary { Operator: CelBinaryOperator.And or CelBinaryOperator.Or } b =>
        b with { Left = Lift(b.Left, errors), Right = Lift(b.Right, errors) },
    CelUnary { Operator: CelUnaryOperator.Not } u when Reads(u.Operand) => Refuse(u, errors, Negated),
    CelRelation r => r,
    _ when Hops(node) is { Count: 1 } hops => Forward(node, hops[0]),
    _ when Hops(node) is { Count: > 1 } hops => Refuse(node, errors, TwoRelations(hops)),
    _ => node,
};

private static CelRelation Forward(CelNode leaf, CelHop hop) => new(
    CelRelationKind.Forward, hop.Related, AlvoManagedColumns.Id, hop.Via, hop.Via,
    CelRewriter.Replace(leaf, n => n switch
    {
        CelHop h => h.Field,
        CelFieldRef outer => new CelOuterFieldRef(outer.FieldName, outer.Type),
        _ => null,
    }));
```

`Hops(node)` is the distinct `Via` of every `CelHop` in the leaf. `Reads(n)` is true when `n` holds a `CelHop` or `CelRelation`. Checks never lift (Task 7).

**Interpreter.** `EvaluateNode` gains `CelRelation => throw RelationNotInterpretable()` and `CelOuterFieldRef => throw …` before `_ => null`. The message is `A relation term is evaluated by the database driver; this evaluator cannot read related rows.`.

- [ ] **Step 1: Write the failing tests.**

  `CelRelationCompileTests` (a two-entity `CelRelationCatalog` from `CelFixtures`: `orders.customer_id → customers`, `customers` ← `orders`, `follows.follower/followee → users`, `project_members.project_id → projects`):
  - `A_forward_comparison_lifts_into_one_relation_whose_body_reads_the_related_row`: `customer_id.owner_id == @user.id` gives `CelRelation(Forward, customers, "id", "customer_id", …)` with body `owner_id == @user.id`.
  - `An_outer_field_beside_a_forward_path_becomes_an_outer_reference`: `customer_id.region == region`.
  - `Two_forward_comparisons_on_one_ref_lift_separately_under_and_and_or`.
  - `A_reverse_exists_reads_the_child_through_its_single_reference`.
  - `An_ambiguous_reverse_relation_is_refused_with_the_via_spellings`, with `follows_via_followee.exists(f, f.follower == @user.id)` accepted.
  - `A_relation_under_not_is_refused` (both kinds).
  - `One_comparison_over_two_relations_is_refused`.
  - `A_second_hop_or_a_nested_exists_is_refused`.
  - `A_path_over_a_non_ref_a_users_ref_or_a_missing_field_is_refused_with_its_fix`.

  `CelRelationRefusalTests`:
  - `Every_slot_compiled_without_a_catalog_refuses_a_path_exactly_as_before`: the theory runs `customer_id.owner_id == 'x'` and `members.exists(m, true)` through Condition, Computed, Mutate, Access, and Rule-without-catalog, comparing message and position against a pre-HX-19 literal.
  - `The_interpreter_throws_on_a_relation_and_never_reads_it_as_null`: also `!<relation>`.
  - `The_public_renderer_refuses_a_relation_tree`.
- [ ] **Step 2:** `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*CelRelation*'` fails, because the nodes are missing.
- [ ] **Step 3:** Implement the code above.
- [ ] **Step 4:** Tests pass. Also `--filter-class '*CelAcceptanceCorpusTests'`, `'*CelTreeChildrenTests'`, `'*CelParser*'`, `'*NoInterpolationPropertyTests'` must be green with no baseline moved.
- [ ] **Step 5: Commit** `feat(cel): relation terms in the rule profile, behind a catalog`.

## Task 2: The catalog — rule compile with relations, cycle and depth, missing `list` warning

**Files:**
- Create: `src/MMLib.Alvo/Rules/Internal/RelationCatalogBuilder.cs`, `RelationGraph.cs`
- Modify: `PolicyCatalogBuilder.cs` (rule compiles pass the catalog; `ReferencesContextValue` descends `CelRelation.Body`), `PolicyCatalog.cs` (`OperationPolicy.HasRelations`, `EntityPolicy.Relations` = the related entity names its `list` reads)
- Test: `test/MMLib.Alvo.Tests/Rules/RelationGraphTests.cs`, `PolicyCatalogBuilderTests.cs` (new facts)

`RelationCatalogBuilder.From(AlvoDescriptor, SchemaModel)` does the following:
- Forward targets are the schema's entities.
- The refusal names `users`, a `storage: dynamic` descriptor entity (#41 text), or an undeclared one.
- Reverse keys are `(parent, child)` when the child has exactly one `ref` to the parent, and `(parent, "<child>_via_<field>")` for every `ref` from child to parent.
- `(parent, child)` is absent and its refusal is the ambiguity text when there are several refs.
- The via walk is `RollupResolver`'s `ReferencesTo`, extracted to `internal static` and shared, so there is one walk.

```csharp
internal static class RelationGraph
{
    internal const int MaxDepth = 3;

    /// Edges: entity → the entities its list rule reads. Refuses every cycle (self-loops too) and every chain
    /// longer than MaxDepth, at the list rule that starts it.
    internal static void Check(IReadOnlyDictionary<string, IReadOnlyList<string>> listReads, List<DescriptorValidationError> errors)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in listReads.Keys.Order(StringComparer.Ordinal))
        {
            Visit(entity, [], listReads, state, errors);
        }
    }

    private static int Visit(string entity, List<string> path, IReadOnlyDictionary<string, IReadOnlyList<string>> edges,
        Dictionary<string, int> depth, List<DescriptorValidationError> errors)
    {
        if (path.Contains(entity, StringComparer.Ordinal))
        {
            errors.Add(Cycle([.. path.SkipWhile(e => e != entity), entity]));
            return 0;
        }

        if (depth.TryGetValue(entity, out var known))
        {
            return known;
        }

        path.Add(entity);
        var deepest = edges.GetValueOrDefault(entity, []).Select(next => 1 + Visit(next, path, edges, depth, errors))
            .DefaultIfEmpty(0).Max();
        path.RemoveAt(path.Count - 1);
        depth[entity] = deepest;
        if (deepest > MaxDepth && path.Count == 0)
        {
            errors.Add(TooDeep(entity, deepest));
        }

        return deepest;
    }
}
```

`Cycle` points at `/entities/<first>/rules/list`. A cycle is reported once: the ordinal-first entry starts the walk, and `depth` memoises. `PolicyCatalogBuilder.TryBuild` calls `RelationGraph.Check` after the entity loop with the `list` edges. A missing related `list` rule adds the warning. Warnings go through the validator's existing severity channel, not `errorList`.

- [ ] **Step 1: Write the failing tests.**

  `RelationGraphTests`:
  - `A_list_rule_cycle_between_two_entities_is_refused_at_apply` (AC 4: `projects.list` reads `tasks`, `tasks.list` reads `projects`, through `DescriptorValidator.Validate`, asserting path and message).
  - `A_list_rule_reading_its_own_entity_is_refused`.
  - `A_get_rule_reading_its_own_entity_is_not_a_cycle`.
  - `A_three_deep_chain_applies_and_a_four_deep_one_is_refused`.
  - `A_relation_to_an_entity_with_no_list_rule_is_a_warning`.

  `PolicyCatalogBuilderTests`:
  - `A_rule_relation_marks_its_operation_and_its_required_context_reads_the_body` (`project_members.exists(m, m.user_id == @user.id)` → `RequiredContext.UserId`).
  - `A_hook_condition_validation_rollup_where_and_hidden_flag_refuse_a_relation`.
  - `A_relation_into_a_dynamic_entity_names_41`.
- [ ] **Step 2:** Run `--filter-class '*RelationGraphTests'` and `'*PolicyCatalogBuilderTests'`; they fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Tests pass. Also `'*DescriptorValidator*'` and `'*RollupLadderTests'` (the shared `ReferencesTo`).
- [ ] **Step 5: Commit** `feat(rules): compile relation rules and refuse list-rule cycles at apply`.

## Task 3: Binding — the related `list` decision per caller, cached

**Files:**
- Create: `src/MMLib.Alvo/Rules/Internal/RelationBinder.cs`
- Modify: `PolicyEngine.cs`
- Test: `test/MMLib.Alvo.Tests/Rules/PolicyEngineRelationTests.cs`

```csharp
internal sealed class RelationBinder(Func<string, AlvoContext, int, PolicyDecision> resolveList)
{
    private readonly ConditionalWeakTable<PolicyCatalog, ConcurrentDictionary<Key, CompiledExpression?>> _bound = new();

    private readonly record struct Key(string Entity, DataOperation Operation, string Slot, bool User, bool Tenant);

    internal CompiledExpression? Bind(PolicyCatalog catalog, string entity, DataOperation operation, string slot,
        CompiledExpression? expression, AlvoContext context, int depth)
    {
        if (expression is null || !CelRewriter.HasRelation(expression.Root))
        {
            return expression;
        }

        var key = new Key(entity, operation, slot, context.User is not null, context.Tenant is not null);
        return _bound.GetOrCreateValue(catalog).GetOrAdd(key, _ => Bound(expression, context, depth));
    }

    private CompiledExpression Bound(CompiledExpression expression, AlvoContext context, int depth) =>
        new(CelRewriter.Replace(expression.Root, node => node is CelRelation { Scope: null } relation
                ? relation with { Scope = Scope(relation, context, depth) }
                : null),
            expression.Profile, expression.ResultType, expression.Source, expression.Entity);

    private RelationScope Scope(CelRelation relation, AlvoContext context, int depth)
    {
        if (depth >= RelationGraph.MaxDepth)
        {
            return RelationScope.Deny;
        }

        var related = resolveList(relation.Related.Name, context, depth + 1);
        return related.IsDenied ? RelationScope.Deny : new RelationScope(false, related.Using, related.TenantScope);
    }
}
```

`CelRewriter.Replace` does not descend into a node it replaced. A bound relation's scope is the related decision's already-bound `Using`, produced by recursion through `resolveList`. `PolicyEngine` gains `internal PolicyDecision Resolve(string, DataOperation, AlvoContext, int depth)`; the public `Resolve` calls it with `0`. `Allow` binds `Using`, `WithCheck` and each check (Task 7) through `RelationBinder` with slots `"using"`, `"check"` and `"checks/<i>"`. The cache is keyed on the catalog instance, so a re-apply starts empty (`CompiledExpression`'s cache warning).

- [ ] **Step 1: Write the failing tests.** All in `PolicyEngineRelationTests`:
  - `A_relation_is_bound_to_the_related_list_using_and_tenant_scope`.
  - `A_denied_related_list_binds_a_constant_false_scope`: theory over no list rule, a tenant-scoped related entity with a tenantless caller, and a related rule reading `@user.id` with an anonymous caller.
  - `A_related_rule_s_own_relation_is_bound_recursively`.
  - `The_bound_scope_is_cached_per_presence_of_user_and_tenant`: all four combinations; same instance on a repeat, a different scope across bits.
  - `A_reapplied_catalog_binds_afresh`.
  - `Depth_beyond_the_limit_binds_deny_even_if_apply_was_bypassed`: a hand-built catalog.
- [ ] **Step 2:** `--filter-class '*PolicyEngineRelationTests'` fails.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Passes, plus `'*PolicyEngineTests'` and `'*PolicyEngineContractTestsOverCatalog'`.
- [ ] **Step 5: Commit** `feat(rules): bind each relation to the caller's list decision of the related entity`.

## Task 4: SQL — the correlated `EXISTS`, row sources, the read composer, the injection property

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Expressions/RelationSql.cs`; `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.Relations.cs`
- Modify: `IPredicateRenderer.cs` (two internal members, both throwing `NotSupportedException` by default), `SqlPredicateRenderer.cs` (behaviour-neutral refactor: the threaded `entity, context, fields, bag` become one `RenderScope`, which also carries `Row`, `Outer`, `Relations` and an alias counter), `ReadStatementComposer.cs` (`AddPredicate` always calls the internal overload with `RowSource.Columns(dialect.RenderTable(entity, null))` as the outer qualifier), `PolicyParameterPrefix.cs` (`Probe = "alvo_k"`, `Outer = "alvo_o"`, added to `All`)
- Test: `test/MMLib.Alvo.Tests/Expressions/RelationSqlRenderTests.cs` + `RelationSqlRenderTests.*.verified.txt`; `RelationNoInterpolationPropertyTests.cs`; `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ReadStatementComposerTests.cs` (one snapshot fact); `cel-to-sql-core.verified.txt` **must not move**

```csharp
// Abstractions/Expressions/RelationSql.cs
internal abstract record RowSource
{
    internal sealed record Unqualified : RowSource;                     // the top-level row of a read, as today
    internal sealed record Columns(string Qualifier) : RowSource;       // "orders" (outer of a read) or alvo_r1
    internal sealed record Image(AlvoRecord Row) : RowSource;           // a write probe's post-image, bound as parameters
}

/// The driver's half of a relation: an unlocked table source per entity. The core composes the EXISTS around it.
internal sealed record RelationSql(Func<EntitySchema, string> Table, RowSource Outer);

// IPredicateRenderer — both internal, default NotSupportedException (the RenderConstraint precedent).
internal SqlPredicate Render(CompiledExpression expression, AlvoContext context, IFieldSqlRenderer fields,
    string parameterPrefix, RelationSql relations) => throw new NotSupportedException(…);
internal SqlPredicate RenderRelation(CelRelation relation, AlvoContext context, IFieldSqlRenderer fields,
    string parameterPrefix, RelationSql relations) => throw new NotSupportedException(…);
```

```csharp
// SqlPredicateRenderer.Relations.cs
private Rendered RenderRelation(CelRelation relation, RenderScope scope)
{
    var bound = relation.Scope ?? throw new InvalidOperationException(
        $"The relation to '{relation.Related.Name}' is not bound to a caller; only a decision IPolicyEngine.Resolve returned can be rendered.");
    if (bound.Denied || scope.Relations is null)
    {
        return Rendered.TwoValued(scope.Fields.RenderBooleanPredicate(false));
    }

    var alias = scope.NextAlias();
    var inner = scope.Into(relation.Related, new RowSource.Columns(alias));
    List<string> terms =
    [
        $"{Column(inner, inner.Row, relation.Related, relation.RelatedKey)} = {Column(scope, scope.Row, scope.Entity, relation.OuterKey)}",
        RenderPredicate(relation.Body, inner).Sql,
    ];
    AddScope(terms, bound.Using, inner);
    AddScope(terms, bound.TenantScope, inner);

    return Rendered.TwoValued(
        $"EXISTS (SELECT 1 FROM {scope.Relations.Table(relation.Related)} AS {alias} WHERE "
        + string.Join(" AND ", terms.Select(term => $"({term})")) + ")");
}

/// Unqualified is today's bare column. Columns(q) prefixes the driver's quoted field. Image binds the value as a
/// parameter of this render's bag; it never formats it.
private static string Column(RenderScope scope, RowSource row, EntitySchema entity, string field) => row switch
{
    RowSource.Unqualified => scope.Fields.RenderField(entity, field),
    RowSource.Columns columns => $"{columns.Qualifier}.{scope.Fields.RenderField(entity, field)}",
    RowSource.Image image => scope.Fields.RenderParameter(scope.Bag.Add(image.Row.Values.GetValueOrDefault(field))),
    _ => throw new InvalidOperationException("Unmapped row source."),
};

private void AddScope(List<string> terms, CompiledExpression? predicate, RenderScope inner)
{
    if (predicate is not null)
    {
        terms.Add(RenderPredicate(predicate.Root, inner with { Entity = predicate.Entity }).Sql);
    }
}
```

**Scope mechanics:**
- `RenderScope.Into(related, row)` returns a scope with `Entity = related`, `Row = row`, and `Outer = this.Row`, except that an `Unqualified` outer becomes `Columns(relations.Outer qualifier)`.
- A `CelOuterFieldRef` renders `Column(scope, scope.Outer!, …)`.
- A `CelFieldRef` renders `Column(scope, scope.Row, …)`.
- Aliases are `alvo_r1`, `alvo_r2`, … per top-level render. They are unquoted: lower-case ASCII, valid bare on PostgreSQL, SQLite and T-SQL.
- A related `USING` reading `@user`/`@tenant` renders against the same `context`. Role membership folds to a literal as today.
- In the public `Render`, a tree holding a relation throws `NotSupportedException` (Decision 6).

`ReadStatementComposer.AddPredicate` passes `new RelationSql(TableOf, new RowSource.Columns(TableOf(entity)))`, where `TableOf = e => _dialect.RenderTable(e, lockedPreImageFor: null)`. Only the outer `FROM` keeps the lock form.

- [ ] **Step 1: Write the failing tests.**

  `RelationSqlRenderTests` (Verify, over `TestFieldSqlRenderer` and `TSqlFieldSqlRenderer`):
  - `A_forward_relation_renders_one_exists_with_the_related_using_and_tenant_scope`.
  - `A_reverse_relation_correlates_the_child_s_via_with_the_outer_id`.
  - `An_outer_reference_inside_the_body_is_qualified_by_the_outer_table`.
  - `A_denied_scope_renders_false_and_reads_no_table`.
  - `Nested_scopes_share_the_parent_bag_and_never_reuse_a_name`.
  - `An_image_outer_binds_every_outer_value_as_a_parameter`.
  - `An_unbound_relation_is_refused`.

  `RelationNoInterpolationPropertyTests` (AC 3; the `FilterSqlRendererPropertyTests` technique): CsCheck 10 000 iterations of hostile text (`'`, `;`, `--`, `\`, `é`, `中`, `"`). The value is placed as the string literal in a forward body (`customer_id.name == '<v>'`), in a reverse body (`notes.exists(n, n.text == '<v>')`), as a literal inside the *related* `USING`, and as an outer post-image value under `RowSource.Image`. The SQL text must be **byte-identical** to the baseline rendered with a benign value, and the value must appear in `Parameters`. Non-vacuity counters cover per placement rendered and refused, more than 1 000 rendered in total.

  `ReadStatementComposerTests.A_relation_rule_is_an_exists_term_inside_the_using_term` (Verify snapshot).
- [ ] **Step 2:** Run `--filter-class '*RelationSql*'` and `'*RelationNoInterpolation*'` (core), and `'*ReadStatementComposerTests'` (EF); they fail.
- [ ] **Step 3:** Do the refactor first and run `'*CelToSql*'` and `'*SqlPredicateRenderer*'` (`cel-to-sql-core.verified.txt` unchanged). Then implement the relation rendering.
- [ ] **Step 4:** Passes. The snapshot judge runs on the new `.verified.txt` files.
- [ ] **Step 5: Commit** `feat(rules): render a bound relation as a correlated EXISTS`.

## Task 5: Writes — `WITH CHECK` relation probes, `InMemoryAlvoData` refusal

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RelationProbe.cs`
- Modify: `EfAlvoData.cs` (`WriteRefusal(decision, …)` becomes `WriteRefusalAsync(db, decision, …)`, and `EnsureWriteAllowed` becomes `EnsureWriteAllowedAsync` at every call site: create, update, replace, the batch's per-row collect); `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs`
- Test: `src/MMLib.Alvo.Testing/Data/AlvoDataRelationRuleTests.cs` (new, with its write facts); `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataRelationRuleTests.cs`; `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataRelationRuleTests.cs`; `test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataTests.cs`

```csharp
internal sealed class RelationProbe(IPredicateRenderer predicates, IFieldSqlRenderer fields, IAlvoSqlDialect dialect)
{
    /// One relation term over a row that is not stored yet: true when a related row the caller may list holds.
    internal async Task<bool> HoldsAsync(AlvoDataContext db, CelRelation relation, AlvoContext context, AlvoRecord image,
        CancellationToken cancellationToken)
    {
        var sql = new RelationSql(e => dialect.RenderTable(e, lockedPreImageFor: null), new RowSource.Image(image));
        var exists = predicates.RenderRelation(relation, context, fields, PolicyParameterPrefix.Probe, sql);
        var statement = $"SELECT CASE WHEN {exists.Sql} THEN 1 ELSE 0 END";
        return await ScalarAsync(db, statement, exists.Parameters, cancellationToken).ConfigureAwait(false) == 1;
    }

    /// The expression with every relation replaced by the probe's verdict — the interpreter then judges the rest.
    internal async Task<CompiledExpression> ResolvedAsync(AlvoDataContext db, CompiledExpression expression,
        AlvoContext context, AlvoRecord image, CancellationToken cancellationToken)
    {
        var verdicts = new Dictionary<CelRelation, bool>(ReferenceEqualityComparer.Instance);
        foreach (var relation in Relations(expression.Root))
        {
            verdicts[relation] = await HoldsAsync(db, relation, context, image, cancellationToken).ConfigureAwait(false);
        }

        var root = CelRewriter.Replace(expression.Root, node => node is CelRelation relation
            ? new CelLiteral(CelValueType.Bool, verdicts[relation])
            : null);
        return new CompiledExpression(root, expression.Profile, expression.ResultType, expression.Source, expression.Entity);
    }
}
```

`ScalarAsync` binds through `PredicateParameterBinder` with `BoundValue.FromPolicyPredicate`, on the context's current connection and transaction. This is the path `ReadStatement` executes through: the write gate's turn is already held, and nothing re-enters. `Relations(root)` collects the top-level `CelRelation` nodes; it does not descend into a relation. `WriteRefusalAsync` resolves `WithCheck` only when `CelRewriter.HasRelation` is true, so the fast path has no probe and no allocation. It then evaluates as before. The tenant scope never holds a relation.

`InMemoryAlvoData` throws `NotSupportedException("Relation rules and lookup checks need a relational driver; InMemoryAlvoData is the policy-contract double.")` when a decision's `Using`/`WithCheck` holds a relation or `Checks` is non-empty. It throws instead of evaluating.

**`AlvoDataRelationRuleTests` world** (`RelationWorld.Schema()` / `.Descriptor()`, tenancy on):
- `customers`: scoped. `owner_id` refs `users`; `active` boolean; `region` string. `list`/`get`: `owner_id == @user.id`.
- `orders`: scoped. `customer_id` refs `customers`; `note` string. `list`/`get`/`update`/`delete`: `customer_id.active == true`; `create`: `customer_id.owner_id == @user.id`.
- `projects`: scoped. `list`: `project_members.exists(m, m.user_id == @user.id)`.
- `project_members`: scoped. `project_id` refs `projects`; `user_id` refs `users`. `list`: `user_id == @user.id`; `create`: `'authenticated' in @user.roles`.
- `regions`: **global**. `list`: `sites.exists(s, s.manager_id == @user.id)`.
- `sites`: scoped. `region_id` refs `regions`; `manager_id` refs `users`. `list`: `manager_id == @user.id`.
- `employees`: scoped. `manager_id` refs `employees`; `region` string. `list`: `'authenticated' in @user.roles`; `get`: `manager_id.region == 'eu'`.
- `order_lines`: scoped. `order_id` refs `orders`; `quantity` integer. Its rules and checks come in Task 8.

Rows are seeded through a system context with roles that pass every rule (the `AlvoDataComputedRollupTests` seeding helper).

- [ ] **Step 1: Write the failing facts** in `AlvoDataRelationRuleTests`:
  - `A_create_whose_relation_the_caller_cannot_satisfy_is_refused`: B creates an order on A's customer → `AlvoAuthorizationException`.
  - `A_create_through_a_relation_the_caller_satisfies_succeeds`.
  - `An_update_moving_a_row_to_a_related_row_the_caller_cannot_list_is_refused`: the post-image probe.
  - `A_batch_create_reports_each_row_whose_relation_fails_at_its_index`.
  - `A_null_reference_never_satisfies_a_forward_relation`.

  `InMemoryAlvoDataTests.A_relation_rule_is_refused_not_ignored`.

  Subclasses: the SQLite one copies `SqliteAlvoDataComputedRollupTests`. The PostgreSQL one copies `PostgreSqlAlvoDataComputedRollupTests` and **adds the Windows skip guard** in `InitializeAsync`.
- [ ] **Step 2:** Run `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteAlvoDataRelationRuleTests'`; it fails.
- [ ] **Step 3:** Implement the probe and wire it in.
- [ ] **Step 4:** Passes on SQLite. Then `--project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlAlvoDataRelationRuleTests'`, plus `'*AlvoDataBatchTests'` and `'*AlvoDataAdversarialTests'` on SQLite (no regression).
- [ ] **Step 5: Commit** `feat(data): judge WITH CHECK relation terms by an in-transaction probe`.

## Task 6: The adversarial suites — two users, two tenants, list/get/count/write, both engines

**Files:**
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataRelationRuleTests.cs`
- Create: `test/_shared/api/DataApiEngineTests.Relations.cs`, `test/_shared/api/descriptors/relations-desk.alvo.json` (the Task 5 world as a descriptor)
- Test: the two derived classes from Task 5, `SqliteDataApiTests`, `DataApiOnPostgresTests`

No production code is expected. A failing fact is a defect in Tasks 1–5 and is fixed there, in this commit.

```csharp
[Fact]
public async Task Two_users_list_get_count_and_write_only_through_their_own_related_rows()
{
    var (data, world) = await RelationWorldAsync();
    var a = world.User("a"); var b = world.User("b");
    var aOrder = await world.SeedOrderAsync(owner: a, active: true);
    var bOrder = await world.SeedOrderAsync(owner: b, active: true);

    var page = await data.QueryAsync(new AlvoQuery { Entity = "orders", IncludeTotalCount = true }, a, Ct);

    page.Items.Select(row => row["id"]).ShouldBe([aOrder]);
    page.TotalCount.ShouldBe(1);
    (await data.GetAsync("orders", bOrder, a, Ct)).ShouldBeNull();
    await ShouldBeRefusedLikeAnOwnerMissAsync(() => data.UpdateAsync("orders", bOrder, Patch("note", "x"), a, Ct));
    await ShouldBeRefusedLikeAnOwnerMissAsync(() => data.DeleteAsync("orders", bOrder, a, Ct));
    (await data.GetAsync("orders", bOrder, b, Ct)).ShouldNotBeNull();
}

[Fact]
public async Task A_global_row_is_visible_through_the_caller_s_tenant_children_only()
{
    var (data, world) = await RelationWorldAsync();
    var user = Guid.NewGuid();
    var region = await world.SeedRegionAsync();
    await world.SeedSiteAsync(world.Tenant1, region, manager: user);

    var inTenant1 = await data.QueryAsync(new AlvoQuery { Entity = "regions", IncludeTotalCount = true }, world.Caller(user, world.Tenant1), Ct);
    var inTenant2 = await data.QueryAsync(new AlvoQuery { Entity = "regions", IncludeTotalCount = true }, world.Caller(user, world.Tenant2), Ct);

    (inTenant1.TotalCount, inTenant2.TotalCount).ShouldBe((1L, 0L));
    (await data.GetAsync("regions", region, world.Caller(user, world.Tenant2), Ct)).ShouldBeNull();
}
```

`ShouldBeRefusedLikeAnOwnerMissAsync` asserts the exact refusal the port gives today for a row an ordinary owner rule hides. It compares against the same call on a non-relation entity in the world, so the fact does not hard-code which exception that is.

- [ ] **Step 1: Write the facts:**
  - The two above.
  - `Two_tenants_with_the_same_user_id_never_see_each_other_s_rows_through_a_relation`: same user GUID in T1 and T2, orders and projects; list, get, count and update.
  - `An_exists_never_matches_a_related_row_the_caller_cannot_list` (AC 2). A theory over four related rows that all satisfy the body: one owned by another user, one under a related entity with no `list` rule (a world variant), one in another tenant, and one behind a role the caller lacks. Each is checked through list, count, get and a create probe.
  - `A_reverse_relation_admits_a_project_only_to_its_members`.
  - `A_self_reference_reads_the_outer_row_s_key` (Review Focus 1).
  - `A_related_row_turning_invisible_hides_the_outer_row_on_the_next_read`.

  `DataApiEngineTests.Relations.cs`: `A_relation_rule_scopes_list_get_count_and_patch_over_http_on_every_engine` (two users, `GET /orders`, `GET /orders/{id}`, `?count`, `PATCH` → the owner-miss status).
- [ ] **Step 2:** Run the four classes (SQLite and PostgreSQL port, SQLite and PostgreSQL HTTP). Red means a defect, which is fixed with a regression fact in the task it belongs to.
- [ ] **Step 3: Commit** `test(data): two-user and two-tenant relation-rule suites on both engines`.

## Task 7: `checks` — schema, model, compile, apply refusals

**Files:**
- Modify: `schema/project.schema.json`, `src/MMLib.Alvo.Abstractions/Descriptor/EntityDescriptor.cs` (`Checks`, `EntityCheck`)
- Create: `src/MMLib.Alvo.Abstractions/Rules/DescriptorCheck.cs`, `src/MMLib.Alvo/Rules/Internal/CheckCompiler.cs`
- Modify: `PolicyCatalogBuilder.cs` (compile checks into `EntityPolicy.Checks`; a check's lookup target adds a consumer edge only, never a `list` edge), `PolicyDecision.cs` (`internal IReadOnlyList<DescriptorCheck> Checks { get; init; } = []`), `PolicyEngine.cs` (create and update decisions carry checks bound by Task 3's binder), `docs/design/f5-admin/generated/*` (via `python3 scripts/gen-prototype-fixtures`)
- Test: `test/MMLib.Alvo.Tests/Rules/CheckCompilerTests.cs`; `test/MMLib.Alvo.Schema.Tests/DescriptorRoundTripTests.cs` (a `checks` case); `ExamplesTests` and `MetaValidationTests` must stay green; `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` (grows by `EntityCheck` and `EntityDescriptor.Checks`)

Schema, added to `$defs/entity/properties`:

```json
"checks": {
  "type": "array",
  "description": "Write-time conditions over a related row, e.g. a line may only reference an active product. Each check follows its `lookup` ref field to the row it names (one the caller may list) and refuses the write with 422 and `message` when `expr` is false for it. `expr` reads this row by field name and the related row as `<lookup>.<field>`; it never reads @user/@tenant. A null `lookup` skips the check. Runs on create, and on update only when a field it reads changed.",
  "items": {
    "type": "object", "additionalProperties": false, "required": ["lookup", "expr", "message"],
    "properties": {
      "lookup": { "$ref": "#/$defs/identifier", "description": "A ref field of this entity; the check reads the row it points at." },
      "expr": { "$ref": "#/$defs/cel", "description": "CEL over this row and `<lookup>.<field>`; true lets the write through." },
      "message": { "type": "string", "minLength": 1, "maxLength": 300, "description": "Shown to the caller when the check fails." }
    }
  }
}
```

`$defs/rules.description` gains one sentence: "A rule may read a related row: `<ref>.<field>` follows a ref field, `<child>.exists(x, x.<field> …)` reaches the records that reference this one; the related entity's own list rule always applies."

```csharp
// Abstractions/Rules/DescriptorCheck.cs
/// One compiled lookup check: Relation is a Forward CelRelation over Lookup whose Body is the whole expr;
/// Reads is every field of this entity the check reads (Lookup included) — the update trigger set.
internal sealed record DescriptorCheck(string Lookup, CelRelation Relation, string Message, IReadOnlySet<string> Reads);
```

`CheckCompiler.Compile(entity, index, EntityCheck, catalog, errors)` does the following:
- The lookup must be a `ref` of the entity to a declared physical entity (Task 2's refusal texts) on a non-dynamic entity.
- It compiles `expr` in the Rule profile with a single-relation catalog (forward over `lookup` only) and **no lift**.
- It refuses a check that reads no `<lookup>.` path, and one that reads `@user`/`@tenant` (walk for `CelContextRef`).
- It wraps the result as `CelRelation(Forward, target, "id", lookup, lookup, body)`. `CelHop` becomes the related field and every other field becomes `CelOuterFieldRef`.
- `Reads` is `lookup` plus every `CelOuterFieldRef` name.

- [ ] **Step 1: Write the failing tests.** All in `CheckCompilerTests`:
  - `A_check_compiles_to_one_forward_relation_whose_body_is_the_whole_expression`.
  - `A_negation_inside_a_check_is_allowed_because_it_stays_inside_the_exists`.
  - `A_check_reading_a_caller_or_tenant_is_refused`.
  - `A_check_that_reads_no_related_field_is_refused_with_the_validation_fix`.
  - `A_lookup_that_is_not_a_ref_or_points_at_users_is_refused`.
  - `A_check_on_a_dynamic_entity_names_41`.
  - `The_reads_set_is_the_lookup_and_every_outer_field`.

  `DescriptorRoundTripTests` gets a descriptor with two checks that round-trips byte-equal.
- [ ] **Step 2:** `--filter-class '*CheckCompilerTests'` fails.
- [ ] **Step 3:** Implement. Then run `python3 scripts/gen-prototype-fixtures`, followed by `python3 scripts/gen-prototype-fixtures --check`.
- [ ] **Step 4:** Passes. Also `--project test/MMLib.Alvo.Schema.Tests` and `--project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApi*'`; justify the two symbols for the snapshot judge.
- [ ] **Step 5: Commit** `feat(descriptor): entities.<e>.checks — lookup checks compiled at apply`.

## Task 8: `checks` at write time — the probe, 422 with message and pointer, both engines **[slice end]**

**Files:**
- Modify: `EfAlvoData.cs` (`EnsureChecksHoldAsync` after `EnsureWriteAllowedAsync` in create, update, replace and batch), `src/MMLib.Alvo.Abstractions/Data/AlvoConstraintViolationException.cs` (`internal string? DescriptorMessage { get; init; }` and an internal ctor `(string field, string message)` that sets `Kind = Check`, `Fields`, and passes `message` to `base`), `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (`CheckViolations`: a `DescriptorMessage` yields the wire violation), `CHANGELOG.md` (one *Added* line)
- Test: `AlvoDataRelationRuleTests` (facts below), `DataApiEngineTests.Relations.cs`, `test/MMLib.Alvo.Api.Tests/ProblemResultFactoryTests.cs` (one fact)

```csharp
private async Task EnsureChecksHoldAsync(AlvoDataContext db, PolicyDecision decision, AlvoRecord postImage,
    AlvoRecord? previous, AlvoContext context, CancellationToken cancellationToken)
{
    foreach (var check in decision.Checks)
    {
        if (!Applies(check, postImage, previous) || postImage.Values.GetValueOrDefault(check.Lookup) is null)
        {
            continue;
        }

        if (!await _relations.HoldsAsync(db, check.Relation, context, postImage, cancellationToken).ConfigureAwait(false))
        {
            var pointer = decision.Unnameable.Contains(check.Lookup) ? [] : new[] { check.Lookup };
            throw new AlvoConstraintViolationException(pointer, check.Message);
        }
    }
}

private static bool Applies(DescriptorCheck check, AlvoRecord postImage, AlvoRecord? previous) =>
    previous is null || check.Reads.Any(field =>
        !Equals(postImage.Values.GetValueOrDefault(field), previous.Values.GetValueOrDefault(field)));
```

A batch row's refusal is reported at its index through the `IRowRefusal` the exception already implements. If it does not, add that implementation here: `At(index)` gives `(index, "check", Message, null)`.

World additions: `products` (scoped; `active` boolean, `stock` integer; `list`: `'authenticated' in @user.roles`; one product in a second role-gated variant). `order_lines.checks`: `[{ "lookup": "product_id", "expr": "product_id.active && quantity <= product_id.stock", "message": "This product is not available in that quantity." }]`.

- [ ] **Step 1: Write the failing facts.**

  In `AlvoDataRelationRuleTests`:
  - `A_failing_lookup_check_refuses_with_its_message_and_the_lookup_pointer` (AC 5): `Kind == Check`, `Fields == ["product_id"]`, `Message` is the author's text, nothing written, no outbox row.
  - `A_passing_lookup_check_lets_the_write_through`.
  - `A_null_lookup_skips_its_check`.
  - `A_lookup_check_over_a_row_the_caller_cannot_list_refuses`.
  - `An_update_that_changes_no_field_a_check_reads_skips_it`: the product is deactivated, then `note` is updated, and the write succeeds.
  - `An_update_changing_quantity_re_runs_the_check`.
  - `A_hidden_lookup_field_is_not_named_by_the_refusal`.
  - `A_batch_reports_the_failing_row_s_check_at_its_index`.

  In `DataApiEngineTests.Relations.cs`: `A_failing_lookup_check_answers_422_with_its_message_and_pointer_on_every_engine` (status 422, `type` `validation`, one violation `{ pointer: "/product_id", code: "check", message: <author text>, fixSuggestion: <wire text> }`).

  In `ProblemResultFactoryTests`: `A_descriptor_check_refusal_carries_the_author_s_message_and_never_the_fixed_text`.
- [ ] **Step 2:** Run the classes on SQLite; they fail.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Passes on SQLite. Then on PostgreSQL: `PostgreSqlAlvoDataRelationRuleTests` and `DataApiOnPostgresTests --filter-method '*lookup_check*'`.
- [ ] **Step 5: Commit** `feat(data): lookup checks refuse a write with 422, the author's message and the lookup pointer`.
- [ ] **Step 6 (slice end):**
  1. `scripts/test-ring1`, then `dotnet build -c Release MMLib.Alvo.slnx`.
  2. The four relation classes on PostgreSQL once more on the final tree.
  3. `alvo-plan-guard`, then the `alvo-pr-report` skill.
  4. Push `hx/19a-relation-rules` and run `gh pr create --label needs-deep-review` with body = the report pointer and `Part of #396`.

## Task 9: Simulator and dashboard — relation scopes in the verdict, relation hints in the rules editor

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (`Relations`, `ManagementRelationVerdict`), `AlvoManagementService.cs` (`Verdict` walks `Using`/`WithCheck`/`Checks` for bound relations: `Via` = the ref name, or `<child>.<via>` for a reverse relation; `Allowed = !Scope.Denied`; the two sources), `Components/Rules/PolicyVerdictView.razor` (a *Reads through* list: each relation's via, entity, and its bound `list` predicate or "denied for this caller — never matches"), `Components/Rules/RulesTab.razor` (under each rule editor, a hint line listing `RelationHints.For(descriptorJson, entity)`: `customer_id.<field>` per forward ref, `<child>.exists(x, x.<field> …)` per reverse relation, using the `_via_` form when ambiguous)
- Create: `src/MMLib.Alvo.Admin/Components/Rules/RelationHints.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/RelationRuleScenarios.cs`
- Test: `test/MMLib.Alvo.Tests/Management/ManagementOperationsTests.cs` (simulate fact), `test/MMLib.Alvo.Admin.Tests/Rules/RelationHintsTests.cs`, PublicApi baselines (Abstractions and Admin if a component type is added: none planned)

No new Razor component type: the hint list is markup inside `RulesTab`, and `RelationHints` is an internal static class.

- [ ] **Step 1: Write the failing tests:**
  - `ManagementOperationsTests.Simulate_names_each_relation_its_bound_list_rule_and_a_denied_one`.
  - `RelationHintsTests.Every_forward_ref_and_reverse_child_is_offered_and_an_ambiguous_child_only_in_via_form`.
  - `RelationRuleScenarios.A_relation_rule_is_checked_live_and_the_simulator_names_the_related_rule`: type `customer_id.owner_id == @user.id` into a list rule of the admin world's entity that refs `customers`; the live check reports no problem; *Simulate* shows "Reads through customer_id → customers" with the related predicate.
  - `RelationRuleScenarios.A_negated_relation_is_refused_live_with_its_fix`.
- [ ] **Step 2:** The tests fail. Run them with `--filter-class` on the two unit projects, then `scripts/test-admin-e2e --filter RelationRuleScenarios`.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Tests pass, plus `'*PolicyVerdictView*'` and `'*PublicApi*'` (Abstractions grows by `Relations` and `ManagementRelationVerdict`; justify both).
- [ ] **Step 5: Commit** `feat(admin): the simulator names relation scopes; the rules editor offers relation terms`.

## Task 10: Docs — *Rules over relations*, executed snippets, the skill

**Files:**
- Create: `website/src/content/docs/extending/relation-rules.mdx`; `website/src/snippets/extending/relation-rules/projects.alvo.json`, `RelationRulesTests.cs`
- Modify: `website/src/content/docs/extending/index.mdx` (link), `where-logic-goes.mdx` (one row: "who may see a row depends on a related row → a relation rule; a write must agree with a related row → a lookup check"), `plugins/alvo/skills/alvo-descriptor-rules-and-cel/SKILL.md`
- Test: `test/MMLib.Alvo.DocsSnippets.Tests` (`SnippetCoverageTests` + the new snippet test), `test/MMLib.Alvo.Ai.Tests --filter-class '*SkillConformanceTests'`

The page, in order:
1. *Follow a reference*: forward syntax and the generated SQL shape.
2. *Reach the records that point here*: `exists`, plus `_via_`.
3. *The related rule always applies*: the related `list` rule and tenant scope are ANDed in, a missing `list` rule never matches, and narrowings are not included (Decision 4).
4. *What a relation cannot do*: one hop, depth 3, not under `!`, no cycles among `list` rules, not in hooks/validation/rollups/masks; each with its apply message.
5. *Lookup checks*: shape, 422 body, null skips, when it re-runs.
6. *Cost*: one `EXISTS` per term; index the reverse `ref`; one probe per term per written row.

A `:::caution` says that a reverse relation in a `create` rule can never hold, because the new row has no children yet.

The snippet descriptor holds `projects`, `project_members`, `tasks` (`project_id.archived == false` beside a membership rule on `projects`) and `order_lines` with a check. `RelationRulesTests` boots `AlvoTestHost` over it and asserts:
- `The_snippet_admits_a_member_and_hides_the_project_from_a_stranger`
- `The_snippet_s_check_refuses_with_its_message`

Skill edit (additive, under the caps):
- Two `allowed` examples, `customer_id.owner_id == @user.id` and `project_members.exists(m, m.user_id == @user.id)`, go in the prose *outside* the `gen:cel-rule` region, whose content is generated.
- One sentence: `A rule may follow a ref (\`customer_id.active == true\`) or reach the rows that reference this one (\`members.exists(m, m.user_id == @user.id)\`); the related entity's own list rule always applies, never under \`!\`, one hop.`
- One sentence for `checks`.

- [ ] **Step 1:** Write the failing snippet test. **Step 2:** `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests` fails. **Step 3:** Write the snippet, the page and the skill. **Step 4:** Tests pass, plus `SkillConformanceTests`, and `cd website && npm run build` (links validate).
- [ ] **Step 5: Commit** `docs(site): rules over relations and lookup checks`.

## Task 11: Architecture doc, spec amendment, PLAN, CHANGELOG, the PR **[slice end]**

**Files:**
- Create: `docs/architecture/relation-rules.md`
- Modify: `docs/architecture/cel.md` (a *Relation term* row: Rule ✓, others ✗; the parser-flag sentence)
- Modify the spec:
  - §7 *HX-19* gains *HX-19 settled details* = Decisions 1–15 verbatim, plus the AC (2) reading from Decision 4.
  - §5 row `19` gets "Amended (HX-19): `EntityCheck`, `EntityDescriptor.Checks`, `ManagementPolicyVerdict.Relations`, `ManagementRelationVerdict`; no `Abstractions/Schema` member".
- Modify `docs/PLAN.md` (HX-19 done in the HX paragraph) and `CHANGELOG.md` (*Added*: dashboard relations line; *Docs*: the page).

`relation-rules.md` sections:
- *The term and its SQL*: forward, reverse, the `EXISTS` template, aliases, outer qualification, the parameter families `alvo_u`/`alvo_k`/`alvo_o`.
- *Binding* (Decision 7): why it happens at `Resolve`, the cache key.
- *Writes*: the probe (Decision 8), the same isolation as PostgreSQL RLS sub-selects (statement snapshot under READ COMMITTED; SQLite serializes writers).
- *Apply-time refusals*: cycle, depth, negation, slots (Decisions 2, 3, 5, 9).
- *Lookup checks* (Decisions 11–13).
- *Not done*: narrowings inside `EXISTS`, `has()` over a path, `softDelete` on a related entity (refused today by `UnhonouredFeatures`; when it lands, the scope must carry `deleted_at IS NULL`), dynamic entities (#41), and `InMemoryAlvoData`.

- [ ] **Step 1:** Write the docs. Run `scripts/check-brief-freshness`; it must stay green, since the spec is not a brief source.
- [ ] **Step 2:** `scripts/test-ring1`, then `dotnet build -c Release MMLib.Alvo.slnx`. Then `--project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlAlvoDataRelationRuleTests'` and `--project test/MMLib.Alvo.Api.Tests.Integration --filter-class '*DataApiOnPostgresTests'`.
- [ ] **Step 3: Commit** `docs(architecture): relation rules and lookup checks, and HX-19's settled details in the spec`.
- [ ] **Step 4:**
  1. `alvo-plan-guard`, then the `alvo-pr-report` skill.
  2. Push `hx/19b-relation-surfaces` and run `gh pr create --label needs-deep-review` with body = the report pointer and `Closes #396`.
  3. After merge, read back #396 (the closing keyword fires once).

---

## Acceptance criteria → tests

| # | Criterion (issue #396) | Test (task) | Engines |
|---|---|---|---|
| 1 | Two-user and two-tenant adversarial suites (list, get, count, write) | `AlvoDataRelationRuleTests.Two_users_list_get_count_and_write_only_through_their_own_related_rows`, `…Two_tenants_with_the_same_user_id_never_see_each_other_s_rows_through_a_relation`, `…A_global_row_is_visible_through_the_caller_s_tenant_children_only` (T6); write half: `…A_create_whose_relation_the_caller_cannot_satisfy_is_refused`, `…An_update_moving_a_row_to_a_related_row_the_caller_cannot_list_is_refused` (T5); HTTP: `DataApiEngineTests.A_relation_rule_scopes_list_get_count_and_patch_over_http_on_every_engine` (T6) | SQLite + PostgreSQL |
| 2 | An `EXISTS` never matches a related row the caller cannot list | `AlvoDataRelationRuleTests.An_exists_never_matches_a_related_row_the_caller_cannot_list` ×4 (T6); `PolicyEngineRelationTests.A_denied_related_list_binds_a_constant_false_scope` ×3 (T3); `RelationSqlRenderTests.A_denied_scope_renders_false_and_reads_no_table` (T4) | SQLite + PostgreSQL; engine-free |
| 3 | CsCheck injection property | `RelationNoInterpolationPropertyTests.No_value_in_a_relation_term_or_an_outer_image_reaches_the_sql_text` (T4) | renderer (both dialect renderers) |
| 4 | A rule cycle (A's `list` uses B, B's uses A) refused at apply | `RelationGraphTests.A_list_rule_cycle_between_two_entities_is_refused_at_apply`, `…A_list_rule_reading_its_own_entity_is_refused` (T2) | engine-free |
| 5 | A lookup check refuses with message and pointer | `AlvoDataRelationRuleTests.A_failing_lookup_check_refuses_with_its_message_and_the_lookup_pointer` (T8); `DataApiEngineTests.A_failing_lookup_check_answers_422_with_its_message_and_pointer_on_every_engine` (T8) | SQLite + PostgreSQL |

## Slices → tasks

| Slice | Branch | Tasks | Ships |
|---|---|---|---|
| A | `hx/19a-relation-rules` | 1–8 | relation rules end to end (compile, cycle, bind, SQL, writes), lookup checks with the schema and model, both-engine suites, this plan |
| B | `hx/19b-relation-surfaces` | 9, 10, 11 | simulator relations, dashboard hints, the docs page and skill, the architecture doc, the spec amendment, PLAN, CHANGELOG |
