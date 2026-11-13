# S0 — A scoped `ref` cannot name another tenant's row (#161) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Superseded in part (2026-11-13 ruling — Alvo has no deployments).** Everything below about *existing*
> databases — upgrading them through a catalog read, rows that already cross tenants refusing the plan, D3, D4,
> Review Focus 2–3, Task 7's upgrade wording, Task 8's scan and #137 follow-up — was dropped (Task 5 removed,
> Task 6 reduced to the PostgreSQL < 15 refusal). A database created before this change must be recreated. The
> shipped CHANGELOG and `docs/architecture/data-path.md` describe what was built.

**Goal:** A `ref` between two `tenancy: "scoped"` entities is enforced by the database as a key over
`(tenant_id, id)`, on both engines, for new and existing databases — and a write naming another tenant's row is
refused exactly as a write naming no row at all.

**Architecture:** `DescriptorModelBuilder.ConfigureReferences` gives every scoped → scoped reference a *tenant guard*:
a foreign key `(child.tenant_id, child.<ref>) → (target.tenant_id, target.id)` over an alternate key on the target. How
`onDelete: setNull` coexists with the guard is a dialect capability (measured, see Decisions). Existing databases are
upgraded by the ordinary plan: the migrator reads the catalog for references that lack their guard, builds the current
model without it, and lets EF's differ add it; rows that already point across tenants refuse the plan with their count.

**Tech Stack:** .NET 10, EF Core relational migrations (SQLite + Npgsql providers), xUnit v3 on MTP, Shouldly, Verify,
Testcontainers (`postgres:16-alpine`, and the existing `postgres:14` fixture).

**Spec:** `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` §8 and §11 (S0 row),
and issue **#161** (`gh issue view 161`) — the authoritative problem statement and acceptance bar. #198 (composite
*primary* key) is explicitly **not** in scope. Master plan: `docs/superpowers/plans/2026-11-13-accounts-00-master.md`.

## Decisions (read before Task 1 — each is a deliberate deviation from, or completion of, #161's sketch)

Measured on 2026-11-13 against SQLite 3.54 and `postgres:16-alpine` (throwaway container, raw DDL):

| shape | SQLite | PostgreSQL 16 |
|---|---|---|
| one composite FK, `CASCADE` / `RESTRICT` | correct | correct |
| one composite FK, `SET NULL` | **refuses the delete** (`NOT NULL constraint failed: child.tenant_id`) | **refuses the delete** (23502 on `tenant_id`) |
| one composite FK, `SET NULL (ref)` (PG 15+) | no syntax | correct — `ref` nulled, `tenant_id` kept |
| single FK with the action **plus** composite guard `NO ACTION` | correct for all three, either constraint order | **order-dependent**: when the guard's RI trigger fires first, `CASCADE` and `SET NULL` deletes fail with 23503 |

- **D1 — `restrict` / `cascade`:** the reference's FK *becomes* the composite one, carrying the declared action. The
  single-column FK is gone. This is #161's sketch.
- **D2 — `setNull`:** the dialect decides (`IAlvoSqlDialect.ScopedSetNull`, internal). PostgreSQL: one composite FK
  `ON DELETE SET NULL (<ref>)` — EF cannot spell the column list, so the PostgreSQL driver's migrations SQL generator
  appends it (Task 2). SQLite (the default): the single-column FK keeps `SET NULL` and a separate composite guard with
  `NO ACTION` is added. Prior art: PostgreSQL 15 added column-list `SET NULL` for exactly this multi-tenant composite-key
  case. A PostgreSQL server older than 15 is refused at plan, at `/entities/{child}/fields/{ref}/onDelete`, following
  the issue-477 precedent (`PostgreSqlOlderServerExclusionTests`).
- **D3 — existing databases need the catalog.** The plan's `current` is the *applied snapshot*, and both sides of the
  diff are built by the same `DescriptorModelBuilder`, so a builder-level DDL change is invisible to it: an unchanged
  descriptor plans nothing. The migrator therefore reads the catalog (`IDatabaseModelFactory`, already used by the
  introspector) for the scoped → scoped references of `current`, and builds the aligned current model **without** the
  guard for each one the database lacks. The differ then emits the upgrade. Every operation it emits
  (`DropForeignKey`, `DropIndex`, `AddUniqueConstraint`, `CreateIndex`, `AddForeignKey`) is **non-destructive** — no
  column or row is dropped. The catalog is read only when `current` holds at least one scoped → scoped reference, in
  counted and uncounted plans alike (an uncounted boot plan that missed it would report "unchanged" forever).
- **D4 — cross-tenant rows already stored refuse the plan, they are not grandfathered.** PostgreSQL validates
  `ADD CONSTRAINT … FOREIGN KEY` against existing rows; SQLite's table rebuild runs with `foreign_keys = 0` and keeps
  them silently. To keep §0 P3, a counted plan counts the rows each added guard would refuse
  (`CrossTenantReferenceScan`, HX-11's `CheckViolationScan` precedent) and refuses at `/entities/{child}/fields/{ref}`
  with the count, operator-facing only, naming no row. Nothing is ever deleted or re-pointed to make the guard hold.
- **D5 — the refusal is the existing one.** At the port both cases raise `AlvoConstraintViolationException(Referenced, [])`
  (both engines decode every FK failure to `Referenced`, and the translator names nothing for it) → the existing 409.
  Over HTTP, `RecordValidator.IsResolvableAsync` already answers both with the same 422 `unresolved reference` before
  the port is reached. No new problem type. The hole #161 describes is the **port** (host code, automation, hook
  `mutate`): that is where the acceptance fact lives.
- **D6 — mixed cases stay single-column:** scoped child → global target, and global child → scoped target (the child
  has no `tenant_id`; a global entity is cross-tenant by definition — not this slice's question).
- **D7 — dynamic entities: out of scope, recorded.** `storage: dynamic` has no driver (F7, #41); the mapper drops it.
  The shared `entity_records` store will have no FK at all, so the F7 driver must check a reference's
  `(tenant_id, id)` in the data port. Task 7 writes that obligation into `docs/architecture/data-path.md`'s F7 section.
- **D8 — four rollup facts lose their setup.** `AlvoDataComputedRollupTests` creates a Globex child naming Acme's parent
  in four facts; that write is now refused. Each keeps its name (so the Testing public-API baseline does not churn) and
  asserts the refusal instead; the rollup's tenant predicate becomes defence in depth, still pinned by
  `RollupTenantPredicateTests` and `RollupBackfillTenancyTests`.
- **Finding, not fixed here:** #137's CHANGELOG entry says "the next apply drops one index and creates another". By D3's
  reasoning that is false for any snapshot-backed database — an unchanged descriptor plans nothing, so a pre-#137
  database kept its instance-wide unique index. The same catalog read could close it; it is a separate issue (Task 8
  files it).

## Global Constraints

- English in everything committed. CRLF + BOM in every `.cs` (re-normalise after any Bash/Python edit, or the
  pre-commit `dotnet format` check fails). Short single-purpose methods (~25-line ceiling); zero inline comments by
  default (`alvo-dotnet-conventions`).
- Public API is the contract (`alvo-architecture-rules`): every new type and member is `internal`; the dialect grows only
  `internal` members (the `ApplyLockTimeoutStatement` precedent), so `PublicApi.MMLib.Alvo.Data.*.verified.txt` must not
  move. The only public growth is the new facts on `AlvoDataConstraintTests` (Task 3).
- Per-engine DDL is a dialect port member, never an `if` in the core (memory: engine scope). No new project or package.
- §0 P3: every behavioural fact lives in a suite **both** engines inherit (`AlvoDataConstraintTests`,
  `DataApiEngineTests`, `test/_shared/ef/TenantGuardMigrationFacts.cs`). Only the PG<15 refusal is engine-specific.
- Snapshots: no existing `*.verified.*` moves (every existing DDL fixture is non-scoped). New baselines: four DDL files
  (Tasks 4, 5) and the grown `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt` (Task 3). The Stop hook
  will demand `alvo-snapshot-judge` for each; the grown public-API file also needs a per-symbol justification against
  `alvo-architecture-rules` (*public is the contract*: an xUnit fact on an inherited contract suite must be public).
- **Test discipline (maintainer's instruction):** inner loop = only the affected project filtered to the class,
  `dotnet test --project <proj> -- --filter-class '*<Class>*'`; ring0 only through the pre-commit hook (never by hand);
  ring1 once, ring2 once, both in Task 8. PostgreSQL integration classes need Docker (Testcontainers).
- Never push to `main`; do not open the PR (the controller does, after `alvo-plan-guard` and `alvo-pr-report`).

## Review Focus

1. **Deleting a parent under each `onDelete`, with the guard in place** — restrict refuses (409 `Referenced`),
   cascade removes the children, setNull clears the ref and keeps the child's `tenant_id`. The measured PG trigger-order
   hazard lives exactly here. Pinned by Task 3 (three delete facts, both engines).
2. **An existing database holding a cross-tenant row** — a counted plan is refused with the count and applies nothing;
   an uncounted plan still reports the upgrade. Pinned by Task 6.
3. **A legacy reference renamed (field or entity) in the same apply as its upgrade** — the guard lands under the new
   names and a second plan is empty. Pinned by Task 5.
4. **`onDelete: setNull` on PostgreSQL 14** — a structured refusal at the field's `onDelete`, never a raw syntax error.
   Pinned by Task 6.
5. **Reference shapes beyond the simple pair** — a self-reference on a scoped entity, two refs from one child, a scoped
   child of a global target, a global child of a scoped target. Pinned by Task 1 (model facts).

---

### Task 1: The tenant guard in the migration model

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TenantGuards.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/ScopedSetNullShape.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DescriptorModelBuilder.cs:32-53,326-350`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs` (internal member beside `ApplyLockTimeoutStatement`)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs:661-667` (`BuildInitializedModel` passes the dialect's shape)
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DescriptorModelBuilderTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/TenantGuardsTests.cs` (new)

**Interfaces:**
- Produces:
  - `internal readonly record struct TenantGuardSite(string Child, string Field, string Target);`
  - `internal static class TenantGuards` with
    `static bool Applies(EntitySchema child, EntitySchema target)` (both `Tenancy == TenancyMode.Scoped`),
    `static IReadOnlyList<TenantGuardSite> Sites(SchemaModel schema)` (every `ref` field whose child and known target both apply),
    `static string? GuardedField(IReadOnlyList<string> columns)` (`[tenant_id, x]` → `x`, otherwise `null`),
    `static bool IsGuard(AddForeignKeyOperation op)` (columns `[tenant_id, x]`, principal columns `[tenant_id, id]`),
    `static bool IsGuardKey(AddUniqueConstraintOperation op)` (columns `[tenant_id, id]`).
  - `internal enum ScopedSetNullShape { SeparateGuard, NamedColumns }` (EF project root, beside `IAlvoSqlDialect`).
  - `internal sealed record TenantGuardShape(ScopedSetNullShape SetNull, IReadOnlySet<TenantGuardSite> Unguarded)` with
    `static TenantGuardShape For(ScopedSetNullShape setNull)` (empty `Unguarded`) — in `TenantGuards.cs`.
  - `DescriptorModelBuilder.Build(SchemaModel model, Func<ModelBuilder> newBuilder, ComputedColumnSql? computed = null, CheckConstraintSql? checks = null, IndexFilterSql? filters = null, TenantGuardShape? guards = null)` — `null` means `TenantGuardShape.For(SeparateGuard)`.
  - `IAlvoSqlDialect`: `internal ScopedSetNullShape ScopedSetNull => ScopedSetNullShape.SeparateGuard;`

- [ ] **Step 1: Write the failing model facts** in `DescriptorModelBuilderTests` (SQLite convention set, as the file does).
  A helper `Pair(OnDelete onDelete, TenancyMode? child, TenancyMode? target)` builds `work_orders` (target) and `visits`
  (child, ref `work_order_id`), each carrying `id` and — when scoped — a trailing `tenant_id` uuid field.

```csharp
[Theory]
[InlineData(OnDelete.Restrict, DeleteBehavior.Restrict)]
[InlineData(OnDelete.Cascade, DeleteBehavior.Cascade)]
public void A_scoped_reference_to_a_scoped_target_spans_the_tenant(OnDelete onDelete, DeleteBehavior expected)
{
    var fk = Visits(Build(Pair(onDelete, Scoped, Scoped))).GetForeignKeys().ShouldHaveSingleItem();
    fk.Properties.Select(p => p.Name).ShouldBe(["tenant_id", "work_order_id"]);
    fk.PrincipalKey.Properties.Select(p => p.Name).ShouldBe(["tenant_id", "id"]);
    fk.PrincipalKey.IsPrimaryKey().ShouldBeFalse();
    fk.DeleteBehavior.ShouldBe(expected);
}

[Fact] // SeparateGuard (the default shape)
public void A_scoped_set_null_reference_keeps_its_action_on_the_field_and_guards_separately()
{
    var fks = Visits(Build(Pair(OnDelete.SetNull, Scoped, Scoped))).GetForeignKeys().ToList();
    fks.Count.ShouldBe(2);
    fks.Single(fk => fk.Properties.Count == 1).DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);
    fks.Single(fk => fk.Properties.Count == 2).DeleteBehavior.ShouldBe(DeleteBehavior.NoAction);
}

[Fact]
public void A_scoped_set_null_reference_is_one_composite_key_where_the_engine_names_set_null_columns()
{
    var fk = Visits(Build(Pair(OnDelete.SetNull, Scoped, Scoped), TenantGuardShape.For(ScopedSetNullShape.NamedColumns)))
        .GetForeignKeys().ShouldHaveSingleItem();
    fk.Properties.Count.ShouldBe(2);
    fk.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);
}

[Theory]
[InlineData(TenancyMode.Scoped, TenancyMode.Global)]
[InlineData(TenancyMode.Global, TenancyMode.Scoped)]
[InlineData(null, TenancyMode.Scoped)]
public void A_reference_that_is_not_scoped_on_both_ends_keeps_a_single_column_key(TenancyMode? child, TenancyMode? target)
{
    var fk = Visits(Build(Pair(OnDelete.Restrict, child, target))).GetForeignKeys().ShouldHaveSingleItem();
    fk.Properties.Select(p => p.Name).ShouldBe(["work_order_id"]);
    fk.PrincipalKey.IsPrimaryKey().ShouldBeTrue();
}

[Fact]
public void An_unguarded_site_builds_the_single_column_key_and_no_alternate_key()
// Build(..., new TenantGuardShape(SeparateGuard, {new("visits","work_order_id","work_orders")}))
// → visits has one FK over [work_order_id]; work_orders.GetKeys() is the primary key only.

[Fact]
public void A_self_reference_and_a_second_reference_on_a_scoped_child_are_each_guarded()
// visits gains `follows_id` (ref → visits) and `backup_order_id` (ref → work_orders), both Restrict:
// three composite FKs, every one leading with tenant_id; work_orders and visits each carry one (tenant_id, id) alternate key.
```

  `TenantGuardsTests`: `Sites_lists_only_references_scoped_on_both_ends`,
  `GuardedField_is_the_non_tenant_column_of_a_guard_and_null_otherwise` (`["tenant_id","x"]` → `"x"`, `["x"]` → `null`,
  `["x","tenant_id"]` → `null`).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*DescriptorModelBuilderTests*' --filter-class '*TenantGuardsTests*'`
Expected: FAIL — compile errors (`TenantGuardShape`, `ScopedSetNullShape`, `TenantGuards` do not exist).

- [ ] **Step 3: Implement.** `Build` keeps its two-pass shape; `ConfigureReferences` receives the entity map and the
  shape and, per `ref` field, chooses one of three private helpers (each under the ceiling):
  `ConfigureSingle` (today's body), `ConfigureGuard(builder, child, field, target, DeleteBehavior behavior)` —
  `HasOne(target).WithMany().HasForeignKey(TenantId, field).HasPrincipalKey(TenantId, Id).OnDelete(behavior)` — and the
  rule: not `Applies` or site in `Unguarded` → single; `SetNull` with `SeparateGuard` → single **and** guard
  (`DeleteBehavior.NoAction`); otherwise → guard with the declared action. Use `AlvoManagedColumns.TenantId`/`.Id`, never
  literals. Extend `DescriptorModelBuilder`'s remarks with D1/D2 and the measurement table's two failing rows (the reason
  the shape is not one line). `EfCoreSchemaMigrator.BuildInitializedModel(SchemaModel schema, TenantGuardShape guards)`
  passes `TenantGuardShape.For(_dialect.ScopedSetNull)` for every call this task touches; `RenameGuessSplitter`'s
  delegate becomes `schema => BuildInitializedModel(schema, TenantGuardShape.For(_dialect.ScopedSetNull))`.

- [ ] **Step 4: Run them to see them pass**

Run: the Step 2 command. Expected: PASS, 0 failed.

- [ ] **Step 5: Commit** (the pre-commit hook runs ring0 on the staged scope)

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests
git commit -m "feat(data-ef): a scoped reference between scoped entities spans (tenant_id, id)"
```

### Task 2: PostgreSQL names the column `SET NULL` clears

**Files:**
- Create: `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlMigrationsSqlGenerator.cs`
- Modify: `src/MMLib.Alvo.Data.PostgreSql/AlvoPostgreSqlBuilderExtensions.cs:144-153` (`ConfigureProvider` adds `.ReplaceService<IMigrationsSqlGenerator, PostgreSqlMigrationsSqlGenerator>()`)
- Modify: `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlSqlDialect.cs` (`ScopedSetNull => NamedColumns`)
- Create: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTenantGuardMigrationTests.cs` (the concrete class Task 4 extends; this task adds its engine-only fact)

**Interfaces:**
- Consumes: `TenantGuards.IsGuard(AddForeignKeyOperation)`, `TenantGuards.GuardedField`, `ScopedSetNullShape` (Task 1).
- Produces: `internal sealed class PostgreSqlMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator` overriding
  `protected override void ForeignKeyConstraint(AddForeignKeyOperation operation, IModel? model, MigrationCommandListBuilder builder)`.

- [ ] **Step 1: Write the failing fact** in `PostgreSqlTenantGuardMigrationTests` (services built as
  `PostgreSqlGeneratedSqlSnapshotTests` builds them, over the `PostgresFixture`):

```csharp
[Fact]
public async Task A_scoped_set_null_reference_names_the_column_it_clears()
{
    var plan = await Migrator.PlanAsync(new SchemaModel([]), TenantGuardSchemas.Pair(OnDelete.SetNull), new MigrationOptions(), Ct);
    var sql = string.Join("\n", plan.Sql);
    sql.ShouldContain("ON DELETE SET NULL (work_order_id)");
    sql.ShouldNotContain("FOREIGN KEY (work_order_id) REFERENCES");
}
```

  `TenantGuardSchemas` is a static fixture class in `test/_shared/ef/TenantGuardSchemas.cs` (linked into both engine
  projects by `<Compile Include=… Link=…>`, as `RelationEngineFacts.cs` is): `Pair(OnDelete)` = scoped `work_orders` +
  scoped `visits` with ref `work_order_id`; `Legacy(OnDelete)` = the same tables with `work_orders.Tenancy = null` (the
  physical shape of a pre-S0 database: identical columns, no guard, no alternate key); `AllActions()` = scoped
  `work_orders` plus scoped `visits` (restrict), `tasks` (cascade) and `notes` (setNull, nullable).

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlTenantGuardMigrationTests*'`
Expected: FAIL — the PostgreSQL dialect still answers the default `SeparateGuard`, so the plan carries a single-column
`SET NULL` key and a composite guard with no column list.

- [ ] **Step 3: Implement.** The override delegates to `base` for everything except a guard whose `OnDelete` is
  `SetNull`: it calls `base` with a copy whose `OnDelete` is `NoAction`, then appends
  ` ON DELETE SET NULL (` + `Dependencies.SqlGenerationHelper.DelimitIdentifier(field)` + `)`. The base constructor's
  `INpgsqlSingletonOptions` is EF-internal: `#pragma warning disable EF1001` with the one-line justification the file's
  `CreateDatabaseModelFactory` already uses. The dialect answers `ScopedSetNullShape.NamedColumns`.

- [ ] **Step 4: Run it to see it pass** — the Step 2 command; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Data.PostgreSql test/_shared/ef/TenantGuardSchemas.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj
git commit -m "feat(postgresql): a scoped setNull reference names the column it clears"
```

### Task 3: The two-tenant acceptance facts (port and HTTP), and the rollup facts that relied on the hole

**Files:**
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataConstraintTests.cs` (new facts; `Schema`/`Descriptor` gain `visits`, `tasks`, `notes` as in `TenantGuardSchemas.AllActions`)
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataComputedRollupTests.cs:475-536` and the two `Globex` facts near 590 and 680 (D8)
- Modify: `test/_shared/api/DataApiEngineTests.Relations.cs` (one HTTP fact over `relations-desk`)
- Modify (baseline): `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Consumes: Tasks 1–2's DDL, through the ordinary driver fixtures. Nothing new produced.

- [ ] **Step 1: Write the failing facts** in `AlvoDataConstraintTests` (`north` and `south` are two `TenantId.New()` contexts):

```csharp
[Theory, InlineData("visits"), InlineData("tasks"), InlineData("notes")]
public async Task A_reference_to_another_tenants_row_is_refused_exactly_as_one_to_no_row(string child)
{
    var data = await CreateAsync(Schema, Descriptor);
    var theirs = await CreateWorkOrderAsync(data, south, "WO-SOUTH");
    var crossTenant = await Should.ThrowAsync<AlvoConstraintViolationException>(() => CreateChildAsync(data, north, child, (Guid)theirs["id"]!));
    var nowhere = await Should.ThrowAsync<AlvoConstraintViolationException>(() => CreateChildAsync(data, north, child, Guid.NewGuid()));
    crossTenant.Kind.ShouldBe(AlvoConstraintKind.Referenced);
    crossTenant.GetType().ShouldBe(nowhere.GetType());
    crossTenant.Kind.ShouldBe(nowhere.Kind);
    crossTenant.Fields.ShouldBe(nowhere.Fields);
    crossTenant.Message.ShouldBe(nowhere.Message);
    (await ListChildrenAsync(data, north, child)).ShouldBeEmpty("a refused write stores nothing");
}

[Theory, InlineData("visits"), InlineData("tasks"), InlineData("notes")]
public async Task An_update_pointing_a_reference_at_another_tenants_row_is_refused_exactly_as_one_to_no_row(string child)
// north creates its own work order and a child naming it; UpdateAsync sets work_order_id to south's id, then to Guid.NewGuid():
// same five assertions; the child still names north's own work order afterwards.

[Theory, InlineData("visits"), InlineData("tasks"), InlineData("notes")]
public async Task A_reference_to_a_row_of_the_callers_own_tenant_is_accepted(string child)
// the counterweight: north's child naming north's work order is stored and reads back with that id.

[Fact] public async Task Deleting_a_row_a_restricting_scoped_reference_names_is_refused()
// Kind Referenced, Fields empty, the work order still reads back.
[Fact] public async Task Deleting_a_row_cascades_to_its_scoped_children()
// the `tasks` child reads back null.
[Fact] public async Task Deleting_a_row_clears_its_scoped_set_null_references_and_keeps_their_tenant()
// the `notes` child reads back under north with work_order_id null.
```

  HTTP fact in `DataApiEngineTests.Relations.cs`, over `relations-desk` (`order_lines.product_id → products`, both scoped):

```csharp
[Fact]
public async Task A_reference_to_another_tenants_row_answers_exactly_as_one_to_no_row_on_every_engine()
// a clerk of tenant B creates a product; a member of tenant A POSTs an order_line naming it, then one naming Guid.NewGuid().
// Both: the same status (422); the problem bodies equal once `traceId` is removed.
```

  D8 — in each of the four rollup facts, the Globex write naming Acme's parent becomes
  `(await Should.ThrowAsync<AlvoConstraintViolationException>(() => …)).Kind.ShouldBe(AlvoConstraintKind.Referenced);`;
  every assertion on Acme's stored numbers stays; names stay; each `<remarks>` says the write is now refused by the
  tenant guard (#161) and that the predicate is defence in depth pinned by `RollupTenantPredicateTests` /
  `RollupBackfillTenancyTests`.

- [ ] **Step 2: Prove the facts can fail, then that they pass**

The guard already exists (Tasks 1–2), so first make the facts red on purpose: locally route every reference through
`ConfigureSingle`, run the command below, and see the three `…exactly_as_one_to_no_row` theories fail (`crossTenant` is
not thrown) and the four D8 rollup facts fail. Restore `ConfigureReferences`, rerun. Record the red run in the commit body.

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAlvoDataConstraintTests*' --filter-class '*SqliteAlvoDataComputedRollupTests*'`
Expected (restored): PASS, 0 failed.

- [ ] **Step 3: Run the PostgreSQL leg and the HTTP legs**

Run: `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlAlvoDataConstraintTests*' --filter-class '*PostgreSqlAlvoDataComputedRollupTests*'`
Run: `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*SqliteDataApiTests*'`
Run: `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration -- --filter-class '*DataApiOnPostgresTests*'`
Expected: PASS on all four (the three delete facts on PostgreSQL are the trigger-order pin).

- [ ] **Step 4: Accept the public-API baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*PublicApiApprovalTests*'`
Expected: FAIL with a `PublicApi.MMLib.Alvo.Testing.received.txt` adding exactly the six new members of
`AlvoDataConstraintTests` and nothing else. Move it over the `.verified.txt`, rerun → PASS. Dispatch `alvo-snapshot-judge`
when the Stop hook asks; justify each symbol in the commit body: a fact on an inherited contract suite is public because
xUnit discovers it, and the suite is what each driver inherits (`alvo-architecture-rules`).

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Testing test/_shared/api/DataApiEngineTests.Relations.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "test(data): a scoped reference to another tenant's row is refused exactly as one to no row"
```

### Task 4: Freeze the DDL, and introspect a guard as the field it guards

**Files:**
- Create: `test/_shared/ef/TenantGuardMigrationFacts.cs` (abstract; linked into both engine projects)
- Create: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteTenantGuardMigrationTests.cs`; Modify: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTenantGuardMigrationTests.cs` (now derives from the facts class)
- Modify: both engine test `.csproj` files (link the two `_shared/ef` files)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaIntrospector.cs:96-110` (`BuildForeignKeyMap`)
- New baselines: `SqliteTenantGuardMigrationTests.A_scoped_pair_is_created_with_its_tenant_guard.verified.txt`, `PostgreSqlTenantGuardMigrationTests.A_scoped_pair_is_created_with_its_tenant_guard.verified.txt`

**Interfaces:**
- Produces: `public abstract class TenantGuardMigrationFacts` (test-only) with `protected abstract IServiceProvider Services { get; }`,
  `protected ISchemaMigrator Migrator`, `protected Task MigrateAsync(SchemaModel from, SchemaModel to)` (the
  `SqliteCheckDdlTests.MigrateAsync` shape, `AllowDestructive = true`), `protected Task<Exception?> RawAsync(string sql)`
  (`OutOfBandStatement.ExecuteAsync` over the container's `AlvoDataContextFactory`). Tasks 5–6 add facts to it.

- [ ] **Step 1: Write the failing facts**

```csharp
[Fact]
public async Task A_scoped_pair_is_created_with_its_tenant_guard()
{
    var plan = await Migrator.PlanAsync(new SchemaModel([]), TenantGuardSchemas.AllActions(), new MigrationOptions(), Ct);
    await Verify(string.Join("\n;\n", plan.Sql));
}

[Fact]
public async Task Introspecting_a_guarded_reference_yields_the_field_and_never_the_tenant_column()
{
    await MigrateAsync(new SchemaModel([]), TenantGuardSchemas.AllActions());
    var live = await Services.GetRequiredService<ISchemaIntrospector>().IntrospectAsync(Ct);
    foreach (var (child, onDelete) in new[] { ("visits", OnDelete.Restrict), ("tasks", OnDelete.Cascade), ("notes", OnDelete.SetNull) })
    {
        var entity = live.Entities.Single(e => e.Name == child);
        entity.Fields.Single(f => f.Name == "work_order_id").Reference.ShouldBe(new RefSchema("work_orders", onDelete));
        entity.Fields.Single(f => f.Name == "tenant_id").Type.ShouldNotBe(FieldType.Ref);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteTenantGuardMigrationTests*'`
Expected: FAIL — no `.verified.txt` yet (Verify writes `.received.txt`), and the introspection fact reports
`tenant_id` as `Ref` (the per-column map marks every column of every FK).

- [ ] **Step 3: Implement** `BuildForeignKeyMap`: a foreign key maps to its *referencing field* — its only column, or
  `TenantGuards.GuardedField` of a guard; `tenant_id` is never mapped; when a field has both (SQLite's setNull shape), the
  single-column key wins because it carries the declared action. Update the remark ("Alvo references are single-column")
  to say a reference is one field, guarded or not.

- [ ] **Step 4: Accept the snapshots and rerun both engines**

Read each `.received.txt`: it must show, per engine, the `work_orders` alternate key `UNIQUE (tenant_id, id)`, a composite
FK for `visits` (RESTRICT) and `tasks` (CASCADE), and for `notes` — PostgreSQL: one composite FK `ON DELETE SET NULL (work_order_id)`;
SQLite: a single-column `ON DELETE SET NULL` FK **and** a composite FK with no `ON DELETE`. Anything else is a bug in Tasks
1–2, not a baseline to accept. Move each over its `.verified.txt`; dispatch `alvo-snapshot-judge` when asked.
Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteTenantGuardMigrationTests*'`
Run: `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlTenantGuardMigrationTests*'`
Expected: PASS on both.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaIntrospector.cs test/_shared/ef test/MMLib.Alvo.Data.Sqlite.Tests test/MMLib.Alvo.Data.PostgreSql.Tests.Integration
git commit -m "test(data-ef): freeze the tenant guard's DDL and introspect it as its reference"
```

### Task 5: (dropped — no existing deployments)

**Ruling (2026-11-13, the maintainer):** Alvo has no users yet, so there is no pre-S0 database to upgrade. The
catalog read, rename alignment and upgrade snapshots this task carried are not built. A database created after S0
gets the guard from the model (Task 4 proves the DDL); a descriptor change that *adds* a scoped ref is an ordinary
model diff and gets the guard the same way. `TenantGuardShape`'s "missing sites" input (if Task 1 built one) stays
unused-but-harmless or is removed by Task 6's implementer if nothing reads it. The CHANGELOG (Task 7) says: emitted
DDL changes; databases created before this change must be recreated.

### Task 6: Refusal — `setNull` between scoped entities on PostgreSQL < 15

The cross-tenant row scan this task also carried is **dropped** (no existing data — see Task 5's ruling). Only the
version refusal stays: it is product behaviour on a supported server (the repo tests PostgreSQL 14 already).

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs`, `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlSqlDialect.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs` (`PlanAsync`)
- Create: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlOlderServerTenantGuardTests.cs`
- If Task 1 built a "missing sites" input to `TenantGuardShape` that nothing now reads, remove it (YAGNI).

**Interfaces:**
- `IAlvoSqlDialect`: `internal ValueTask<string?> ScopedSetNullRefusalAsync(DbConnection connection, CancellationToken ct) => ValueTask.FromResult<string?>(null);`
  PostgreSQL returns, below `server_version_num` 150000,
  `"This PostgreSQL server is version {x}; onDelete: setNull between two scoped entities needs PostgreSQL 15 or later. Use restrict or cascade, or upgrade the server."`;
  the migrator refuses at `/entities/{child}/fields/{field}/onDelete` when the plan adds a guard with
  `ReferentialAction.SetNull` and the dialect's shape is `NamedColumns` — in counted and uncounted plans.

- [ ] **Step 1: Write the failing facts** — `PostgreSqlOlderServerTenantGuardTests : IClassFixture<PostgreSqlOlderServerExclusionTests.Postgres14>`:
  `A_scoped_set_null_reference_is_refused_at_its_on_delete_on_postgresql_14` → `DescriptorValidationException`, single
  error, `Path == "/entities/visits/fields/work_order_id/onDelete"`, message contains `"PostgreSQL 15"`; and
  `A_scoped_restrict_reference_still_applies_on_postgresql_14` (plan + apply succeed).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlOlderServerTenantGuardTests*'`; Expected: FAIL (raw syntax error, not a `DescriptorValidationException`).
- [ ] **Step 3: Implement** the dialect member and the refusal in `PlanAsync`.
- [ ] **Step 4: Run to see them pass** — same command; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(data-postgres): refuse a scoped setNull ref on PostgreSQL older than 15`

### Task 7: Say it where it is read

**Files:**
- Modify: `docs/architecture/data-path.md` — new section `## A scoped ref is a key over (tenant_id, id) (#161)` after
  *Exclusions* (D1–D5 and the measurement table, verbatim); rewrite the rollup bullet at ~985–999; one row in *What is
  proved on a real engine, and where*; the F7 section gains D7's obligation.
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RollupRecompute.cs:100-107,360` — item 3 and the
  `TenantPredicate` remark: the guard closes it for scoped pairs; the predicate stays as defence in depth.
- Modify: `docs/superpowers/specs/2026-08-04-f3-pr6-computed-rollup-design.md` — the *Open* item (~450) and Dev-14 (~354)
  each gain one line: closed by S0 (#161), pointer to the new data-path section.
- Modify: `CHANGELOG.md` — `[Unreleased]` → *Changed (breaking)*: emitted DDL changes for every scoped → scoped ref; an
  existing database is upgraded by the next plan (non-destructive; the boot applies it under the default `Apply` mode, a
  `Verify` host refuses until its migration job runs, a dashboard-first host on its next apply); stored cross-tenant
  rows refuse it with their count; `setNull` between scoped entities needs PostgreSQL 15.

- [ ] **Step 1: Sweep for the claim, not the file** (memory: *sweep for the claim*)

Run: `grep -rn -i "id\` alone\|#161\|parent's \`id\`\|single-column\|(tenant_id, id)" docs website/src/content plugins/alvo/skills src --include='*.md' --include='*.mdx' --include='*.cs'`
Expected: every hit that still says a `ref` is a key on `id` alone is either rewritten or is a historical record
(a design or plan dated before today) left as written. `docs/PLAN.md` is S6's to change.

- [ ] **Step 2: Make the edits; check the docs gate**

Run: `scripts/check-brief-freshness` — Expected: OK (no spec/analysis file is touched).

- [ ] **Step 3: Commit**

```bash
git add docs CHANGELOG.md src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RollupRecompute.cs
git commit -m "docs(data-ef): a scoped ref is a key over (tenant_id, id)"
```

### Task 8: Slice gate — ring1, ring2, security-core checklist

**Files:** none new (fixes only, if a gate finds something).

- [ ] **Step 1: ring1**

Run: `scripts/test-ring1` — Expected: `[ring1] OK`. A failure in a suite this plan did not touch that creates a
cross-tenant `ref` is D8 again: rewrite it to assert the refusal, never weaken the guard. Any moved `*.verified.*` →
`alvo-snapshot-judge`.

- [ ] **Step 2: ring2**

Run: `scripts/test-ring2` — Expected: exit 0 (integration, API invariants, Vacuum). Docker must be up.

- [ ] **Step 3: `alvo-security-core-review` checklist**, item by item, recorded for the PR report:
  SQL — the scan interpolates identifiers only, through `AlvoSqlIdentifier.Quote`, never a value; authorization in the
  `WHERE` — unchanged (the guard is a constraint, not a filter); fail-fast — the PG<15 refusal is at plan; cross-tenant —
  Task 3's two-tenant facts on both engines, both legs of the indistinguishability (port and HTTP); dynamic entities — D7,
  out of scope with the obligation written down; default-deny — unchanged. Close with **mark this change
  `needs-deep-review`**.

- [ ] **Step 4: File the follow-up** for the #137 finding (Decisions, last bullet): `gh issue create` titled
  "A pre-#137 database keeps its instance-wide unique index: an unchanged descriptor plans nothing", body citing D3 and
  `TenantGuardCatalog` as the mechanism that would close it. Label `bug`. Do not fix it on this branch.

- [ ] **Step 5: Hand back** to the controller (plan-guard, PR report, PR) — no push, no PR from this task.
