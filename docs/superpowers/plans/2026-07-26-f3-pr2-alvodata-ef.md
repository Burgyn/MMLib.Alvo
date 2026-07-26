# F3 PR2 — `IAlvoData` on EF Core property bags (SQLite + PostgreSQL) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn PR1's security core into a real data path — an `IAlvoData` implementation over EF Core
property-bag entity types, with the resolved policy predicate composed into the `WHERE` clause of one
statement, per-engine SQL renderers for SQLite and PostgreSQL, and the adversarial + differential
suites green on both engines against real databases.

**Architecture:** One runtime `DbContext` (`AlvoDataContext`) whose model is built from the applied
`SchemaModel` as `SharedTypeEntity<Dictionary<string, object>>` property bags with **every property
optional**, so a `hidden` column can be NULL-projected out of the `SELECT` list. Reads are
`FromSqlRaw` over a statement whose `WHERE` carries the `USING` predicate, the synthesized tenant
scope, the caller's filter and the keyset cursor — all rendered through the driver's
`IFieldSqlRenderer` and bound through EF's own `IRelationalTypeMappingSource`, never formatted into
text. Writes never go through a tracked `SaveChanges` except the insert: `update`/`delete` run as
`ExecuteUpdate`/`ExecuteDelete` **over the same `FromSql` root that carries the policy predicate**, so
`rows affected == 0` is the not-found signal. `WITH CHECK` is evaluated in memory over the merged
post-image inside one transaction. The `DbContext`, its `DbSet`s and its change tracker are
unreachable from outside the implementation, and that is an architecture test, not a convention.

**Tech Stack:** .NET `net10.0`; EF Core 10.0.10 (`Microsoft.EntityFrameworkCore.Relational`,
`Microsoft.EntityFrameworkCore.Sqlite`), `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3; xUnit v3
3.2.2 on Microsoft.Testing.Platform; Shouldly 4.3.0, NSubstitute 6.0.0, CsCheck 4.7.0,
Verify.XunitV3 31.27.0, NetArchTest.Rules 1.3.2, PublicApiGenerator 11.5.4,
Testcontainers.PostgreSql 4.13.0 (`postgres:16-alpine`).

**Source of truth:** the de-risking spike's verdict,
[`docs/superpowers/specs/2026-07-26-f3-pr2-spike-verdict.md`](../specs/2026-07-26-f3-pr2-spike-verdict.md) —
**read it before Task 1**, especially *What this means for the plan*. It answers the eight questions
this PR's mechanism depends on with real generated SQL on both engines; where it settled a question
this plan cites the probe id (`Q4g`, `Q5d`, `Q6`, `Q8`, `X2`) and **the decision is not to be
re-litigated**. Second source: the milestone design
[`docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md`](../specs/2026-07-25-f3-crud-vertical-slice-design.md) —
sections *The core compiles, the provider renders*, *Null semantics*, *Two backends*,
*Field-level `hidden`/`readOnly`*, *Ports*, *Testing strategy*, and its *Deviations* 6, 7 and 9.
Numeric acceptance criteria come from `docs/product/baas-analyza.md` §2.1 and §2.4 and are quoted
verbatim under *Definition of Done*. **Closes issue #20.**

---

## Global Constraints

- Target framework `net10.0`, SDK pinned in `global.json`. Tests run on **Microsoft.Testing.Platform
  (MTP)**, not VSTest. `TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true` — a warning is
  a build failure.
- **`MMLib.Alvo.Abstractions` stays EF-free, ADO.NET-free and ASP.NET-free.** **`MMLib.Alvo` (the
  core) references only `MMLib.Alvo.Abstractions` among family assemblies and never EF Core or
  Npgsql** — enforced by `test/_shared/SharedArchitectureRules.cs`
  (`Core_depends_only_on_Abstractions`). **EF may be referenced only from
  `src/MMLib.Alvo.Data.EntityFrameworkCore`, `src/MMLib.Alvo.Data.Sqlite` and
  `src/MMLib.Alvo.Data.PostgreSql`.** Every type this PR adds that touches EF lands in one of those
  three projects. No new project is created — no package is earned here
  (`docs/architecture/package-boundary.md`).
- **Three disjoint parameter prefixes for the three predicates a `PolicyDecision` carries:**
  `alvo_u` (`Using`), `alvo_c` (`WithCheck`, reserved — see *Deviations* 14), `alvo_t`
  (`TenantScope`). Statement-level values this PR binds itself use the fixed names `alvo_id` (the row
  id) and the families `alvo_f<n>` (caller filter) and `alvo_k<n>` (keyset cursor). **None of them
  starts with `p`**: spike `Q6` proved a `p` prefix collides with EF's own `p0`, and EF then *silently
  renames* the bound parameter while the SQL text still says `@p0` — on SQLite with no error at all,
  substituting the caller's value into the security predicate. **PR1 already acted on that finding:**
  `IPredicateRenderer.Render`'s default is now `"alvo_p"` (commit `54d612c`, pinned by the fact
  `The_default_parameter_prefix_cannot_collide_with_an_orms_own_parameter_names`), so this PR changes no
  default and moves no baseline for it. What it must still do is **pass an explicit prefix at every call
  site**: a `PolicyDecision` carries three predicates, each render numbers its parameters from zero, and
  one shared default would bind two values to one name.
- **Every parameter value is bound through EF's own relational type mapping** —
  `IRelationalTypeMappingSource.FindMapping(clrType)` → `mapping.CreateParameter(command, name, value,
  nullable: true)`. **Never format a value into a string.** Spike `Q6d`/`X2c`: EF's SQLite `Guid`
  mapping writes upper-case `TEXT`, so a hand-formatted lower-case Guid returns zero rows with no
  error.
- **No `SaveChanges` on a tracked, mutated row, ever.** Spike `Q5d`: a tracked `Attach` + set +
  `SaveChanges` emits `UPDATE … WHERE id = @p1` with **no policy predicate** — the shortest, most
  idiomatic EF code available, and a complete authorization bypass. `update`/`delete` go through
  `ExecuteUpdateAsync`/`ExecuteDeleteAsync` over a `FromSql` root that carries the `USING` predicate;
  `SaveChangesAsync` is reached from exactly one production file (the create path) plus the
  test-only seeding seam. `AlvoDataContext` sets
  `ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking` in its constructor, so a
  returned row can never become a tracked entity.
- **The `DbContext`, its `DbSet`s and its change tracker must be unreachable outside the `IAlvoData`
  implementation.** `AlvoDataContext` is `internal sealed`; no public or protected member of any of
  the three EF projects mentions `DbContext`, `DbSet<>` or `ChangeTracker`. Task 10 makes this an
  architecture test.
- **No SQL identifier is emitted unquoted, and `ISqlGenerationHelper.DelimitIdentifier` is never
  used.** Spike `Q8`: `NpgsqlSqlGenerationHelper` returns identifiers *unquoted* when it deems quoting
  unnecessary (PostgreSQL then case-folds them), and SQLite's helper *silently drops* the schema
  argument. **PR2 introduces no database schema**; `AlvoOptions.SchemaPrefix` stays a framework-table
  name prefix, and a qualified table name is produced by the driver, never assembled by shared code.
- **PostgreSQL tests run against a real container, never a fake** —
  `Testcontainers.PostgreSql` `postgres:16-alpine`, in
  `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration`, self-skipping on Windows exactly as
  `PostgresFixture` already does. SQLite tests run against a real temp-file database, never
  `:memory:` shared state.
- Central package versions live in `Directory.Packages.props`; `.csproj` `PackageReference` entries
  carry **no** `Version=`.
- Public API members of shipped projects carry `/// <summary>` XML docs. Methods stay short and
  single-purpose (**~25-line ceiling**); extract aggressively. **Zero inline comments** — name things
  instead (`.claude/skills/alvo-dotnet-conventions`).
- DI registration is idempotent (`TryAdd*`).
- Every shipped-package change updates its `PublicApi.<assembly>.verified.txt` baseline in
  `test/_shared/`. A moved `*.verified.*` baseline trips `.claude/hooks/turn-review-gate` → dispatch
  `alvo-snapshot-judge`. That is expected in this PR (per-engine golden SQL, public-API baselines),
  not a problem to route around.
- Run **`scripts/test-ring0`** after each implementation step, **`scripts/test-ring1`** at the end of
  each task, **`scripts/test-ring2`** before the PR. Conventional Commits, one commit per task.
- Branch `f3/pr2-alvodata-ef`, stacked on `f3/pr1-security-core`. **Never push to `main`.**

---

## Definition of Done (issue #20, quoted, not invented)

Every task below maps onto one of these. The numeric criteria are lifted verbatim from
`docs/product/baas-analyza.md`; §2's own preamble warns that its "musí obsahovať" lists describe a
mature platform and are **not** MVP requirements — the **acceptance criteria** are what binds.

| # | Criterion | Source | Task |
|---|---|---|---|
| 1 | *"Two-user adversarial suite: user A nikdy nevidí/nezmení dáta usera B"* — user A **never** sees or changes user B's data | §2.4 | 10 (SQLite), 11 (PostgreSQL) |
| 2 | default-deny — *"default = deny"*, *"nič nie je exposed, kým nemá politiku"* (nothing is exposed until it has a policy) | §2.4 | 7, 8, 9, 10 |
| 3 | *"Pravidlo odkazujúce na neexistujúci stĺpec zlyhá pri uložení (compile-time), nie pri requeste"* — a rule naming a nonexistent column fails **at save**, not at request time | §2.4 | 2 (holds through the real path) |
| 4 | *"Property-based testy dokazujú, že preklad pravidla → SQL **nikdy neinterpoluje** užívateľský vstup"* | §2.4 | 6 |
| 5 | *"Adversarial test suite (injection cez každý operátor, malformed hodnoty, unicode) prechádza; fuzzing filtra bez pádu"* — injection through **every** operator, malformed values, unicode; the filter fuzzed **without a crash** | §2.1 | 6 |
| 6 | application-side rules that *"kompilujú do SQL predikátov (**nikdy post-filter v pamäti**)"* — a snapshot proves the policy predicate is in the `WHERE`, not a post-filter | §2.4 | 5, 7 |
| 7 | a query issued **without** a context throws | design *Verification*, §4 | 7 |
| 8 | golden CEL→SQL snapshots **per engine** | design *Testing strategy* | 1 |
| 9 | green on SQLite **and** PostgreSQL | §2.1 crit. 3, design | 10, 11 |
| 10 | *"Rovnaký adversarial a policy test suite (§2.4) prechádza identicky nad fyzickou aj virtuálnou entitou"* — F7's obligation; PR2 must leave the mechanism dynamic-capable | §2.1 | 1 (the `IAlvoSqlDialect` seam), 12 (documented) |

Two §2.1 criteria are explicitly **not** PR2's**:** *"p95 latencia filtrovaného listu nad 100k
riadkov (indexovaný stĺpec) < 50 ms lokálne; keyset pagination stabilná nad 1M riadkov"* and the
`Idempotency-Key` criterion belong to #19 (PR3/PR4). See *Deviations* 4 and 6 for the seams PR2
leaves them.

---

## Deliberate decisions and deviations (record before you start)

Each is a decision, not an oversight. If the maintainer disagrees, these are the vetoable spots.

1. **The applied schema reaches `IAlvoData` through the `ISchemaRegistry` *port*, implemented by the
   policy catalog provider — not through a public member on `PolicyCatalog`, and not from a second
   primed holder.** `AlvoServiceCollectionExtensions` carries `TODO(#19): register ISchemaRegistry once
   the Data API needs it`; PR2 is that first consumer, so PR2 registers it.
   `IPolicyCatalogProvider` also derives from `MMLib.Alvo.Schema.ISchemaRegistry`, one instance is
   registered as both, and `PolicyCatalog.Schema` is **`internal`** — read only by the provider in the
   same assembly.

   *Why one primed source at all:* a schema the rules were never compiled against is exactly the
   mismatch `IAlvoData`'s own remarks forbid being the one path an unvalidated payload reaches storage
   on ("a mismatch between the policy catalog and the implementation's schema must not be the one path
   on which an unvalidated payload reaches storage"), and a second independently primed holder is how
   two sources come to drift.

   *Why a port rather than a public property:* **this follows the precedent PR1's final review already
   set for the sibling case.** PR1 briefly hung the descriptor's `RoleCatalog` off the catalog as a
   public `PolicyCatalog.Roles`; review rejected it and the shape it landed with is
   `IRoleCatalogProvider` — a role-shaped port in `Abstractions` that `IPolicyCatalogProvider` derives
   from, with `PolicyCatalog.Roles` kept `internal`
   (see `src/MMLib.Alvo.Abstractions/Identity/IRoleCatalogProvider.cs`, whose remarks state the
   argument in full). The reasoning transfers unchanged: a public `PolicyCatalog.Schema` would make the
   *policy* catalog the authoritative source of the *applied schema* and foreclose any other source —
   F7's dynamic-entity registry being the obvious next one — without either routing the data path
   through the rule engine or reintroducing the second holder. Applying the same rule here means a
   later reader sees one consistent principle, not two similar-looking decisions made differently.

   *Two deliberate differences from the role precedent, each with its reason.* `ISchemaRegistry` already
   exists in `Abstractions` and is the design's named port for this, so no new port is invented. And its
   `GetSchema()` returns a non-nullable `SchemaModel` (it shipped in F2 and narrowing it would be a
   breaking change), so the unprimed value is `new SchemaModel([])` rather than `null`: that is the
   fail-closed value here — no entity declared means every entity name and every field name is refused —
   where `IRoleCatalogProvider` needs `null` because an empty role catalog and an undeclared one are
   different things.

   **Cost, stated plainly:** an implementer of `IPolicyCatalogProvider` must now also answer "what
   schema is applied", exactly the cost the design's *Deviations* 10 already accepted for roles. A host
   with its own schema source registers its own `ISchemaRegistry` and takes it over.
   **Consequence:** #19's runtime-apply path re-primes rules and schema in one step for free.
2. **The caller's filter, the keyset cursor predicate and the row id are rendered into the raw
   `FromSql` root through `IFieldSqlRenderer`, not composed as LINQ.** Two reasons, either alone
   sufficient. (a) EF translates C# `==`/`!=` with **C# null semantics**, adding
   `OR x IS NULL` compensation — which would make `neq` match a `NULL` field and directly violate
   `AlvoFilterOperator`'s documented three-valued contract ("a `null` column never satisfies `neq`
   either"). (b) It gives `IFieldSqlRenderer.RenderCaseInsensitiveLike` its consumer, which the
   design's *Deviations* 7 filed as an open debt of PR1. **Consequence:** the only surviving LINQ is
   the sort chain, `Take`, and the write path's id `Where`.
3. **Both drivers enable `UseRelationalNulls(true)`** so the residual LINQ carries SQL's semantics
   too, not C#'s. It is set inside each driver's existing
   `RelationalProviderRegistration.ConfigureProvider`, so the migrator's throwaway context inherits
   it as well — harmless, it issues no queries. Cost, stated plainly: a future LINQ query in this
   package must be written against SQL semantics; the option's own docs warn "your LINQ queries no
   longer have the same meaning as they do in C#", which here is the *desired* meaning.
4. **`AlvoSort.Nulls` uses the portable `ORDER BY CASE WHEN <key> IS NULL THEN 0 ELSE 1 END, <key>`
   emulation on both engines. Native `NULLS FIRST`/`NULLS LAST` is not adopted.** Reason: `ORDER BY`
   must be composed in LINQ, because EF wraps a `FromSql` root in a derived table and a derived
   table's row order is not guaranteed to survive — so a raw-root `ORDER BY` is not merely
   redundant, it is unreliable. Spike `Q3c` proved the emulation translates identically on both
   engines and **also** proved it defeats an index on the sort key. **Consequence, named:** #19's
   *"p95 … < 50 ms locally"* on an indexed nullable sort column is **PR3's problem**, and PR3's option
   if it fails is to move `ORDER BY` + paging into the raw root and stop composing them in LINQ. PR2
   leaves that seam intact by keeping the whole statement text in one composer
   (`ReadStatementComposer`). Recorded as spike open-risk 3.
5. **`ILike` renders through `RenderCaseInsensitiveLike`** — native `ILIKE` on PostgreSQL,
   `UPPER(a) LIKE UPPER(b)` on SQLite. **A caller-supplied pattern's `%` and `_` are meaningful and
   are not escaped**, which is PostgREST's own `like`/`ilike` semantics and therefore the shape an
   agent expects; the value is always a bind parameter, so this is an expressiveness decision, not an
   injection one. This closes the spike's open `RenderCaseInsensitiveLike` question and the design's
   *Deviations* 7 debt.
6. **`AlvoQuery.After` encodes only the row id.** The anchor row's sort-key values are re-read
   **under the same policy predicate**, then the nested-OR keyset predicate is built from them
   (spike `Q3d` proved the shape translates on both engines). This is deliberately better than
   serializing the sort tuple into the cursor: a forged, stale or cross-tenant cursor finds no anchor
   and yields an **empty page** rather than an oracle. Cost: one extra round trip per page.
   **Consequence:** PR3 owns the query-string surface, the offset mode, the server-enforced max page
   size and the *"keyset pagination stabilná nad 1M riadkov"* test.
7. **The new `IAlvoSqlDialect` port lives in `MMLib.Alvo.Data.EntityFrameworkCore`, not in
   `Abstractions`, and `IFieldSqlRenderer` is *not* extended.** The spike suggested a
   `RenderTable(EntitySchema)` member on `IFieldSqlRenderer`; rejected, because that port renders
   *expressions* (CEL → SQL) and statement shape is a relational/EF concern `Abstractions` must not
   grow — and because adding a required member would break the shipped port, `TestFieldSqlRenderer`
   and `TSqlFieldSqlRenderer`. F7's dynamic driver implements `IAlvoSqlDialect` too; that is the seam
   §2.1's "same suite over a physical and a virtual entity" criterion runs through.
8. **`RelationalProviderRegistration` gains two required members (`Fields`, `Dialect`).** A breaking
   change to a public authoring contract, accepted: no version has shipped, and a provider that
   cannot render SQL cannot serve `IAlvoData` at all, so a nullable member would only move the
   failure to runtime.
9. **The read model is all-optional.** Every `IndexerProperty` is the nullable CLR type with
   `IsRequired(false)`, because that is the only way a `hidden` **`NOT NULL`** column can be
   NULL-projected out of the `SELECT` list (spike `Q4g`; `Q4f` shows the schema-faithful model throws
   *two different exception types per engine*, which principle 3 forbids). Required-ness is still
   enforced — by the physical `NOT NULL` (spike `Q4h`) and, later, by PR3's schema-derived
   validation. Rejected alternative, from the spike: one entity type per (entity, visible-field-set)
   mapped to the same table — legal, but the visible set is per-caller-role, so it multiplies the
   model cache by the policy matrix.
10. **`WITH CHECK` is merge-then-check, never write-then-rollback.** Inside one transaction: read the
    pre-image under `USING` (with the driver's row-lock hint where it exists), merge `values` over it,
    evaluate `WithCheck` + `TenantScope` through `IPredicateEvaluator`, then `ExecuteUpdate` still
    constrained by `USING`. Spike `Q5e` proved write-then-read-then-rollback also works; it is
    rejected because it uses a rollback as control flow and moves the decision out of the
    engine-agnostic core.
11. **The engine test subclasses seed through an `internal` EF-package seam**
    (`AlvoDataSeed`, with `InternalsVisibleTo` for the two engine test assemblies) rather than
    hand-rolled ADO.NET. The spike's own first false negative came from hand-formatting a `Guid`
    (`Q6d`), and hand-binding a `decimal`/`DateTimeOffset` on SQLite risks the same class of
    disagreement. `AlvoDataSeed` is excluded from Stryker mutation.
12. **The read model configures no foreign keys or navigations.** F2's `DescriptorModelBuilder` owns
    the physical FKs; here a `Ref` field is just a `uuid` column. A navigation would add a shape the
    property-bag query path never uses and PR3's relation embedding is not in scope.
13. **`EntityStorage.Dynamic` entities are refused** by this implementation with
    `AlvoAuthorizationException` — fail-closed and indistinguishable from an unknown entity. F7
    registers a dynamic `IAlvoSqlDialect` + `IFieldSqlRenderer` pair and the same `EfAlvoData` serves
    both (spike `X2` rehearsed it and found one trap: **a `uuid` JSON path silently returns zero rows
    on SQLite**, because EF stores `Guid` as upper-case `TEXT` while `json_extract` returns whatever
    case the payload holds. Task 12 records that in `docs/architecture/data-path.md` as a named F7
    test, so it is a discovery already made rather than one waiting to happen).
14. **`alvo_c` is reserved but never rendered in PR2.** `WithCheck` is evaluated in memory, so no SQL
    carries it today. The prefix is declared and a test asserts all prefixes are pairwise disjoint, so
    a future SQL-side `WITH CHECK` (a `RETURNING`-based write, say) inherits a name that cannot
    collide.

---

## File Structure

**`src/MMLib.Alvo.Abstractions/`** — ports + pure model (stays EF-, ADO- and ASP.NET-free)

- Unchanged. `IPredicateRenderer`'s `alvo_p` default and `ISchemaRegistry` both already ship; this PR
  adds no port here.

**`src/MMLib.Alvo/`** — core (still EF-free)

- Modify `Rules/PolicyCatalog.cs` — add **`internal`** `SchemaModel Schema { get; }`, set from
  `TryBuild`'s own `schema` argument. Internal, exactly like the sibling `Roles`.
- Modify `Rules/IPolicyCatalogProvider.cs` — also derive from `MMLib.Alvo.Schema.ISchemaRegistry`.
- Modify `Rules/Internal/PolicyCatalogProvider.cs` — implement `GetSchema()` off the same volatile
  `Current`, beside the existing `DeclaredRoles`.
- Modify `Rules/Setup.cs` — register `ISchemaRegistry` as the *same instance* as
  `IPolicyCatalogProvider`, exactly as `IRoleCatalogProvider` already is.
- Modify `AlvoServiceCollectionExtensions.cs` — delete the `TODO(#19)` and the remark saying
  `ISchemaRegistry` is deliberately unregistered.

**`src/MMLib.Alvo.Data.EntityFrameworkCore/`** — the shared EF layer (every EF type this PR adds)

- Create `IAlvoSqlDialect.cs` — public port: statement-shape rendering a driver owns.
- Create `AlvoSqlIdentifier.cs` — public `Quote(string)`, the one quote-doubling implementation.
- Create `Internal/FieldClrTypeMap.cs` — the single `FieldSchema` → CLR type mapping (`Exact` for the
  migrator model, `Optional` for the read model).
- Create `Internal/AlvoDataContext.cs` — the runtime property-bag `DbContext`, all-optional, no-tracking.
- Create `Internal/AlvoModelCacheKeyFactory.cs` — keys the EF model cache on the schema token.
- Create `Internal/AlvoDataContextFactory.cs` — one context per operation; mints a new model token
  when the applied `SchemaModel` reference changes.
- Create `Internal/PredicateParameterBinder.cs` — every value through `IRelationalTypeMappingSource`.
- Create `Internal/PolicyParameterPrefix.cs` — the five reserved names/prefixes.
- Create `Internal/ReadProjection.cs` — the `SELECT` list, `CAST(NULL AS <type>) AS <col>` per hidden field.
- Create `Internal/QueryFieldGuard.cs` — rejects a filter/sort key that is hidden or undeclared.
- Create `Internal/FilterSqlRenderer.cs` — `AlvoFilter` → SQL + bound values, every operator.
- Create `Internal/KeysetSqlRenderer.cs` — the nested-OR cursor predicate.
- Create `Internal/ReadStatementComposer.cs` — assembles the whole `SELECT … FROM … WHERE …` text.
- Create `Internal/SortComposer.cs` — the LINQ `OrderBy`/`ThenBy` chain with null placement.
- Create `Internal/RecordMaterializer.cs` — property-bag row → `AlvoRecord`, hidden keys dropped.
- Create `Internal/UpdateSetterFactory.cs` — the runtime `Action<UpdateSettersBuilder<…>>`.
- Create `Internal/EfAlvoData.cs` — the `IAlvoData` implementation.
- Create `Internal/AlvoDataSeed.cs` — the `internal` out-of-band seeding seam the adversarial suite needs.
- Modify `Internal/DescriptorModelBuilder.cs` — delegate `ClrType` to `FieldClrTypeMap.Exact`.
- Modify `RelationalProviderRegistration.cs` — add required `Fields` and `Dialect`.
- Modify `AlvoEfCoreProvider.cs` — register `IFieldSqlRenderer`, `IAlvoSqlDialect`,
  `AlvoDataContextFactory` and `IAlvoData`.
- Modify `Properties/AssemblyInfo.cs` — `InternalsVisibleTo` the two engine test assemblies.
- Modify `MMLib.Alvo.Data.EntityFrameworkCore.csproj` — no new package reference is needed
  (`Microsoft.EntityFrameworkCore.Relational` already carries `ExecuteUpdate` and the type-mapping source).

**`src/MMLib.Alvo.Data.Sqlite/`**

- Create `SqliteFieldSqlRenderer.cs`, `SqliteSqlDialect.cs`.
- Modify `AlvoSqliteBuilderExtensions.cs` — supply both to the registration; `UseRelationalNulls()`.

**`src/MMLib.Alvo.Data.PostgreSql/`**

- Create `PostgreSqlFieldSqlRenderer.cs`, `PostgreSqlSqlDialect.cs`.
- Modify `AlvoPostgreSqlBuilderExtensions.cs` — same two changes.

**`src/MMLib.Alvo.Testing/`** — shipped fakes + inherited contract suites (Abstractions-only)

- Create `Data/AlvoDataSqlSnapshotTests.cs` — the abstract per-engine golden-SQL suite (CEL→SQL and
  the whole read statement).
- Create `Data/AlvoDataDifferentialTests.cs` + `Data/IDifferentialProbe.cs` — the abstract suite that
  replays `DifferentialRuleCases.All` over a **real engine**.

**`test/`**

- `MMLib.Alvo.Data.EntityFrameworkCore.Tests/*` — unit tests for the binder, projection, filter
  renderer, keyset renderer, statement composer, field guard, model cache key, and the encapsulation
  architecture tests.
- `MMLib.Alvo.Data.Sqlite.Tests/*` — `SqliteAlvoDataAdversarialTests`,
  `SqliteAlvoDataDifferentialTests`, `SqliteAlvoDataSqlSnapshotTests`, `SqliteAlvoDataFixture`.
- `MMLib.Alvo.Data.PostgreSql.Tests.Integration/*` — the same three, over `PostgresFixture`.
- `MMLib.Alvo.Tests/Rules/PolicyCatalogProviderSchemaTests.cs` and a fact added to
  `MMLib.Alvo.Tests/Rules/RulesSetupTests.cs` — the core half of Task 2.
- `test/_shared/PublicApi.*.verified.txt` — updated baselines for `MMLib.Alvo` (one line:
  `IPolicyCatalogProvider`'s base list), `MMLib.Alvo.Data.EntityFrameworkCore`,
  `MMLib.Alvo.Data.Sqlite`, `MMLib.Alvo.Data.PostgreSql`, `MMLib.Alvo.Testing`.
  **`MMLib.Alvo.Abstractions` does not move** — this PR adds no port and changes no default there.

**Repo files**

- Delete `spike/MMLib.Alvo.Data.Spike/` **entirely** (Task 1) and its `<Project>` entry in
  `MMLib.Alvo.slnx` under the `/spike/` folder; remove the now-empty folder element.
  `SolutionConventionTests.Every_project_is_registered_in_the_solution` covers the inverse direction,
  so a stale `.slnx` entry fails the build.
- Modify `stryker-config.data-ef.json` — mutate the new `Internal/*` data-path files; exclude
  `Internal/AlvoDataSeed.cs` and `AlvoEfCoreProvider.cs`.
- Create `docs/architecture/data-path.md` — the mechanism, the three prefixes, the no-`SaveChanges`
  rule, the all-optional read model, and the F7 notes.

---
## Task 1: The per-engine renderers, the `IAlvoSqlDialect` seam, and the spike's exit

The spike is throwaway and must not survive this task. What survives is its two `IFieldSqlRenderer`
implementations — proven against real generated SQL on both engines — plus the one thing it did not
have: a driver-owned way to name a table and to spell a typed SQL `NULL`. `IFieldSqlRenderer` is
**not** extended for that (*Deviations* 7). Neither renderer delegates to
`ISqlGenerationHelper.DelimitIdentifier`: spike `Q8` showed Npgsql returns identifiers unquoted when
it judges quoting unnecessary, which PostgreSQL then case-folds.

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoSqlIdentifier.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/RelationalProviderRegistration.cs`
- Create: `src/MMLib.Alvo.Data.Sqlite/SqliteFieldSqlRenderer.cs`
- Create: `src/MMLib.Alvo.Data.Sqlite/SqliteSqlDialect.cs`
- Create: `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlFieldSqlRenderer.cs`
- Create: `src/MMLib.Alvo.Data.PostgreSql/PostgreSqlSqlDialect.cs`
- Modify: `src/MMLib.Alvo.Data.Sqlite/AlvoSqliteBuilderExtensions.cs`
- Modify: `src/MMLib.Alvo.Data.PostgreSql/AlvoPostgreSqlBuilderExtensions.cs`
- Create: `src/MMLib.Alvo.Testing/Data/AlvoDataSqlSnapshotTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataSqlSnapshotTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/AlvoSqlIdentifierTests.cs`
- Delete: `spike/MMLib.Alvo.Data.Spike/` (whole directory) and its `MMLib.Alvo.slnx` entry
- Modify: `test/_shared/PublicApi.MMLib.Alvo.Data.EntityFrameworkCore.verified.txt`,
  `PublicApi.MMLib.Alvo.Data.Sqlite.verified.txt`,
  `PublicApi.MMLib.Alvo.Data.PostgreSql.verified.txt`,
  `PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Consumes: `MMLib.Alvo.Expressions.IFieldSqlRenderer` (shipped by PR1 — `RenderField(EntitySchema,
  string)`, `RenderParameter(string)`, `TrueLiteral`, `FalseLiteral`,
  `RenderCaseInsensitiveLike(string left, string right)`, plus three **default interface members**
  `RenderTwoValued`, `RenderBooleanFieldAsPredicate`, `RenderBooleanPredicate` whose defaults are
  already the PostgreSQL/SQLite shape — **do not override them**, only T-SQL would);
  `MMLib.Alvo.Expressions.IPredicateRenderer.Render(CompiledExpression, AlvoContext, IFieldSqlRenderer,
  string parameterPrefix = "alvo_p")` — **already shipped by PR1 with that default; do not change it,
  and always pass an explicit prefix anyway**; `MMLib.Alvo.Schema.EntitySchema` / `FieldSchema` /
  `FieldType`.
- Produces:
  - `public interface MMLib.Alvo.Data.EntityFrameworkCore.IAlvoSqlDialect`
    - `string RenderTable(EntitySchema entity)`
    - `string RenderColumn(string columnName)`
    - `string RenderNullProjection(string storeType)`
    - `string RowLockClause(PreImageMutation mutation)`
  - `public static class MMLib.Alvo.Data.EntityFrameworkCore.AlvoSqlIdentifier` with
    `public static string Quote(string identifier)`.
  - `public sealed class MMLib.Alvo.Data.Sqlite.SqliteFieldSqlRenderer : IFieldSqlRenderer`
  - `public sealed class MMLib.Alvo.Data.Sqlite.SqliteSqlDialect : IAlvoSqlDialect`
  - `public sealed class MMLib.Alvo.Data.PostgreSql.PostgreSqlFieldSqlRenderer : IFieldSqlRenderer`
  - `public sealed class MMLib.Alvo.Data.PostgreSql.PostgreSqlSqlDialect : IAlvoSqlDialect`
  - `RelationalProviderRegistration` gains `public required IFieldSqlRenderer Fields { get; init; }`
    and `public required IAlvoSqlDialect Dialect { get; init; }`.
  - `public abstract class MMLib.Alvo.Testing.Data.AlvoDataSqlSnapshotTests` with
    `protected abstract string EngineName { get; }`, `protected abstract ICelCompiler Compiler { get; }`,
    `protected abstract IPredicateRenderer Renderer { get; }`,
    `protected abstract IFieldSqlRenderer Fields { get; }`.

> **AMENDMENT (slice 1 review, findings I1 + I2 — binding on every later task).**
> `RenderNullProjection` takes the **EF-resolved store type**, not a `FieldSchema`:
> `string RenderNullProjection(string storeType)`. The per-dialect `FieldType` → type tables below are
> **deleted**; a dialect decides only the cast syntax (`CAST(NULL AS {storeType})` on both in-repo
> drivers). Reason: the hand-written tables were a second authority for a column's store type and already
> disagreed with the DDL the migrator actually emits in three places on PostgreSQL — `Json` → `jsonb`
> where the column is `text`, a `MaxLength` string → `text` where the column is `character varying(N)`,
> and `Decimal` → a fixed `numeric(18,2)` regardless of declared precision. The single authority is EF's
> own `IRelationalTypeMappingSource`, reached through the mapped property's `GetColumnType()`. **Any code
> block further down this plan that still reads `RenderNullProjection(FieldSchema)` means the new
> signature**, and its caller must obtain the store type from the EF model rather than pass the
> `FieldSchema` through.

> **AMENDMENT (slice 1 review, finding I3 — binding on every later task; the member was renamed by the
> next amendment).** `RowLockHint` returns the
> clause **with no separator of its own**: `"FOR NO KEY UPDATE"` on PostgreSQL (not `" FOR UPDATE"`),
> `string.Empty` on SQLite. Two changes, each with its reason.
> - *No leading space.* A value that must carry its own separator is a trap a third-party driver author
>   trips silently — return `"FOR UPDATE"` under the old convention and every pre-image read becomes
>   `… WHERE <predicate>FOR UPDATE`. **The composer inserts the space**, and only when the hint is
>   non-empty, so the call site at *Task 6* becomes a small helper rather than a bare `.Append(hint)`.
> - *`FOR NO KEY UPDATE`, not `FOR UPDATE`.* PostgreSQL documents the "no key" mode for a locking read
>   that precedes an update not touching the row's key (SELECT reference, "The Locking Clause"; Explicit
>   Locking §13.3.2). Alvo's update path provably never changes a key — a caller-supplied `id` is rejected
>   before the pre-image read — and the weaker lock does not block the `FOR KEY SHARE` a concurrent
>   foreign-key check needs on this row, which matters because `Ref` fields carry real FKs.

> **AMENDMENT (slice 2, item 0 — binding on every later task).** The row lock is a **method taking the
> operation**, not a property:
>
> ```csharp
> string RowLockClause(PreImageMutation mutation);
> ```
>
> The argument is a **two-member enum owned by the EF package**,
> `public enum PreImageMutation { Update, Delete }` — not `MMLib.Alvo.Rules.DataOperation`, three of whose
> five members would be illegal here. PostgreSQL returns `FOR NO KEY UPDATE` for `Update` and
> `FOR UPDATE` for `Delete`; SQLite returns `string.Empty` for both. No dialect needs a throw arm: on an
> engine whose real answer is the empty string a silent `""` for a list or a create would make a composer
> bug read as a legitimate answer, and a two-member enum makes that mistake a compile error instead — in
> every dialect, including ones Alvo will never see. The separator-free convention from the AMENDMENT
> above is unchanged.
>
> *`Delete` has no caller in PR2*, because `DeleteAsync` carries no `WITH CHECK` and therefore reads no
> pre-image (Task 9); the arm exists so that PR5's before-delete hooks — the first code that will read a
> row it is about to remove — inherit the right lock instead of the update's.
>
> *Reason (correctness, not style).* `FOR NO KEY UPDATE` is right before an **update** — PostgreSQL
> documents it as the weaker mode for an update that does not change the key, and a caller-supplied `id`
> is rejected before the pre-image read, so that read provably never precedes a key change. It is wrong
> before a **delete**: a delete removes the key, so it needs `FOR UPDATE`, which is precisely the lock
> `FOR NO KEY UPDATE` declines to take (it does not block `FOR KEY SHARE`). One property cannot express
> both. Done inside PR2 because it is a breaking change to a driver-facing seam afterwards.
>
> **Consequence for Task 5/6/7.** `ReadStatementOptions.LockRows` (a `bool`) becomes
> `ReadStatementOptions.LockFor` (a `PreImageMutation?`): `null` for a read that takes no lock, the
> mutation the pre-image precedes otherwise. `LockClause` becomes
> `options.LockFor is { } operation && _dialect.RowLockClause(operation) is { Length: > 0 } clause ? " " + clause : string.Empty`,
> and `EfAlvoData.SingleAsync`'s `bool lockRow` parameter becomes `DataOperation? lockFor`.

- [ ] **Step 1: Write the identifier-quoting test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/AlvoSqlIdentifierTests.cs`:

```csharp
namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class AlvoSqlIdentifierTests
{
    [Fact]
    public void An_ordinary_identifier_is_still_quoted()
        => AlvoSqlIdentifier.Quote("plate").ShouldBe("\"plate\"");

    [Fact]
    public void An_embedded_quote_is_doubled()
        => AlvoSqlIdentifier.Quote("we\"ird").ShouldBe("\"we\"\"ird\"");

    [Fact]
    public void A_quote_breaking_payload_cannot_escape_the_quoted_identifier()
        => AlvoSqlIdentifier.Quote("title\"; DROP TABLE items; --")
            .ShouldBe("\"title\"\"; DROP TABLE items; --\"");

    [Fact]
    public void An_empty_or_whitespace_identifier_is_refused()
        => Should.Throw<ArgumentException>(() => AlvoSqlIdentifier.Quote("  "));
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`
Expected: FAIL — `AlvoSqlIdentifier` does not exist.

- [ ] **Step 2: Implement the quoting helper and the dialect port**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoSqlIdentifier.cs`:

```csharp
namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The one implementation of SQL's double-quote identifier escaping, shared by every Alvo storage
/// driver. Deliberately <b>not</b> a call to EF's <c>ISqlGenerationHelper.DelimitIdentifier</c>: the
/// Npgsql helper returns an identifier <em>unquoted</em> whenever it judges quoting unnecessary — which
/// PostgreSQL then case-folds, so the same field renders differently per driver — and the SQLite helper
/// silently discards a schema argument. A driver always quotes, because a field or entity name may have
/// been assembled programmatically by a host and is therefore untrusted (see
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer.RenderField"/>'s own remarks).
/// </summary>
public static class AlvoSqlIdentifier
{
    /// <summary>Quotes <paramref name="identifier"/>, doubling every embedded double quote.</summary>
    /// <param name="identifier">The identifier to quote.</param>
    /// <exception cref="ArgumentException"><paramref name="identifier"/> is null, empty or whitespace.</exception>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
```

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs`:

```csharp
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The storage driver's half of <b>statement</b> shape, as
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/> is its half of <b>expression</b> shape. The
/// shared EF data path composes only structure — a <c>SELECT</c> list, a <c>FROM</c>, the
/// <c>AND</c>-joined <c>WHERE</c> — and asks this interface for everything a dialect owns: how a table
/// is named, how a column is quoted, how a typed SQL <c>NULL</c> is spelled for a masked field, and
/// whether a pre-image read can be locked.
/// </summary>
/// <remarks>
/// <para>
/// This lives beside the EF data path rather than in <c>Abstractions</c> on purpose: statement shape is
/// a relational concern, and <c>Abstractions</c> is required to stay free of one. It is also why
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/> was not extended with a table-rendering
/// member — that port renders expressions, and every existing implementation (including
/// <c>MMLib.Alvo.Testing.TestFieldSqlRenderer</c>) would have had to grow a member it has no table for.
/// </para>
/// <para>
/// F7's dynamic-entity driver implements this interface too: there <see cref="RenderTable"/> returns a
/// JSON-projecting sub-select over the one shared partitioned store rather than a table name, which is
/// what makes "the same adversarial suite passes over a physical and a virtual entity" a matter of
/// registering another dialect instead of rewriting the data path.
/// </para>
/// </remarks>
public interface IAlvoSqlDialect
{
    /// <summary>
    /// Renders the table source <paramref name="entity"/>'s rows are read from — a quoted table name on
    /// a physical entity.
    /// </summary>
    /// <remarks>
    /// A driver must not qualify the name with a database schema unless it actually has one: SQLite has
    /// no schemas at all, and <c>AlvoOptions.SchemaPrefix</c> is a framework-<em>table</em> name prefix,
    /// not a schema. Both in-repo drivers return the bare quoted entity name, matching the
    /// <c>ToTable(entity.Name)</c> the migration model already uses.
    /// </remarks>
    /// <param name="entity">The entity being read.</param>
    string RenderTable(EntitySchema entity);

    /// <summary>Renders a column reference in a <c>SELECT</c> list or an <c>ORDER BY</c>.</summary>
    /// <param name="columnName">The column's name.</param>
    string RenderColumn(string columnName);

    /// <summary>
    /// Renders a typed SQL <c>NULL</c> standing in for a masked field's value — the mechanism that keeps
    /// a <c>hidden</c> field's data inside the table. An untyped bare <c>NULL</c> is not enough: the
    /// result set has to satisfy the mapped property's store type, so the cast names
    /// <paramref name="storeType"/>. A dialect decides only the cast syntax; the type is EF's to resolve
    /// (see the AMENDMENT above). The result is a bare expression — no `AS <column>` alias, no comma.
    /// </summary>
    /// <param name="storeType">The masked column's EF-resolved store type.</param>
    string RenderNullProjection(string storeType);

    /// <summary>
    /// Renders the clause appended to the pre-image read that precedes <paramref name="mutation"/>, so a
    /// concurrent writer cannot change the row between the decision and the write —
    /// <c>FOR NO KEY UPDATE</c> before an update and <c>FOR UPDATE</c> before a delete on PostgreSQL, the
    /// empty string where the engine has no such clause and serializes write transactions instead
    /// (SQLite). <c>PreImageMutation</c> has exactly the two members that have a pre-image, so no other
    /// operation is representable (see the third AMENDMENT above).
    /// </summary>
    string RowLockClause(PreImageMutation mutation);
}
```

- [ ] **Step 3: Write the failing golden CEL→SQL snapshot suite**

Create `src/MMLib.Alvo.Testing/Data/AlvoDataSqlSnapshotTests.cs`. It renders one fixed table of rules
through the engine's own `IFieldSqlRenderer` and `Verify`s the result, so each engine's dialect is
frozen next to the core's `cel-to-sql-core` baseline:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The per-engine half of the golden CEL→SQL snapshots: the same rule table the core freezes against
/// <c>TestFieldSqlRenderer</c>, re-rendered through a real driver's <see cref="IFieldSqlRenderer"/>, so
/// a dialect change (a boolean literal, an <c>ILIKE</c> spelling, a quoting rule) shows up as a moved
/// baseline rather than as a behaviour difference discovered on one engine only.
/// </summary>
/// <remarks>
/// The compiler, renderer and field renderer arrive as abstract members because this library
/// deliberately references <c>MMLib.Alvo.Abstractions</c> alone — a subclass in an engine's own test
/// project resolves them from <c>AddAlvo()</c> and its driver package.
/// </remarks>
public abstract class AlvoDataSqlSnapshotTests
{
    /// <summary>Gets the engine's snapshot file suffix (<c>sqlite</c>, <c>postgresql</c>).</summary>
    protected abstract string EngineName { get; }

    /// <summary>Gets the CEL compiler, resolved from <c>AddAlvo()</c>.</summary>
    protected abstract ICelCompiler Compiler { get; }

    /// <summary>Gets the predicate renderer, resolved from <c>AddAlvo()</c>.</summary>
    protected abstract IPredicateRenderer Renderer { get; }

    /// <summary>Gets the driver's own field/dialect renderer.</summary>
    protected abstract IFieldSqlRenderer Fields { get; }

    private static readonly string[] _rules =
    [
        "true",
        "owner_id == @user.id",
        "owner_id != @user.id",
        "!(owner_id == @user.id)",
        "tenant_id == @tenant.id",
        "has(owner_id)",
        "!has(owner_id)",
        "is_public",
        "is_public || owner_id == @user.id",
        "'admin' in @user.roles",
        "status == 'approved'",
        "status in @user.roles",
        "(owner_id == @user.id || status == 'approved') && !is_public",
    ];

    [Fact]
    public Task Cel_renders_to_this_engines_sql()
    {
        var rendered = _rules.Select(rule => new
        {
            Rule = rule,
            Sql = Render(rule).Sql,
            Parameters = Render(rule).Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}:{p.Value?.GetType().Name ?? "null"}")
                .ToArray(),
        });

        return Verify(rendered).UseFileName($"cel-to-sql-{EngineName}");
    }

    private SqlPredicate Render(string rule)
    {
        var compiled = Compiler.Compile(rule, CelProfile.Rule, SnapshotEntity);
        if (!compiled.IsSuccess)
        {
            throw new InvalidOperationException(
                $"'{rule}' did not compile: {string.Join("; ", compiled.Errors.Select(e => e.Message))}");
        }

        return Renderer.Render(compiled.Expression!, SnapshotCaller, Fields, "alvo_u");
    }

    /// <summary>The caller every snapshot renders against — a fixed, tenanted, admin-holding identity.</summary>
    protected static AlvoContext SnapshotCaller { get; } = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated, Role.Admin },
        Tenant = new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")),
    };

    /// <summary>The entity every snapshot rule is compiled against.</summary>
    protected static EntitySchema SnapshotEntity { get; } = new()
    {
        Name = "vehicle",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "plate", Type = FieldType.String, Required = true, MaxLength = 32 },
            new FieldSchema { Name = "status", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "secret_note", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "mileage", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "price", Type = FieldType.Decimal, Nullable = true, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "is_public", Type = FieldType.Boolean, Nullable = true },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = true },
        ],
    };
}
```

`SnapshotEntity`/`SnapshotCaller` are lifted from the spike's `Fixture.Entity` — one column of every
awkward type plus one `hidden` candidate (`secret_note`) — and every later task reuses them.

- [ ] **Step 4: Implement the two renderers and the two dialects**

`src/MMLib.Alvo.Data.Sqlite/SqliteFieldSqlRenderer.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>
/// SQLite's <see cref="IFieldSqlRenderer"/>. The three two-valued members come from the port's default
/// interface members, whose defaults already carry the <c>COALESCE(…, 0)</c> shape SQLite accepts in
/// boolean position — a dialect only overrides them when it has no boolean type (T-SQL).
/// </summary>
public sealed class SqliteFieldSqlRenderer : IFieldSqlRenderer
{
    /// <inheritdoc/>
    public string TrueLiteral => "1";

    /// <inheritdoc/>
    public string FalseLiteral => "0";

    /// <inheritdoc/>
    public string RenderField(EntitySchema entity, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(fieldName);
    }

    /// <inheritdoc/>
    public string RenderParameter(string parameterName) => "@" + parameterName;

    /// <inheritdoc/>
    public string RenderCaseInsensitiveLike(string left, string right) => $"UPPER({left}) LIKE UPPER({right})";
}
```

`src/MMLib.Alvo.Data.Sqlite/SqliteSqlDialect.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>SQLite's <see cref="IAlvoSqlDialect"/>: unqualified quoted tables, SQLite storage classes, no row lock.</summary>
public sealed class SqliteSqlDialect : IAlvoSqlDialect
{
    /// <inheritdoc/>
    public string RowLockClause(PreImageMutation mutation) => string.Empty;

    /// <inheritdoc/>
    public string RenderTable(EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(entity.Name);
    }

    /// <inheritdoc/>
    public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

    /// <inheritdoc/>
    public string RenderNullProjection(string storeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        return $"CAST(NULL AS {storeType})";
    }
}
```

`src/MMLib.Alvo.Data.PostgreSql/PostgreSqlFieldSqlRenderer.cs` — identical shape, with
`TrueLiteral => "TRUE"`, `FalseLiteral => "FALSE"`, and
`RenderCaseInsensitiveLike(left, right) => $"{left} ILIKE {right}"`.

`src/MMLib.Alvo.Data.PostgreSql/PostgreSqlSqlDialect.cs` — identical shape, with
`RowLockClause` answering `FOR NO KEY UPDATE` for `Update` and `FOR UPDATE` for `Delete` (see the second
and third AMENDMENTs above) and the same
`RenderNullProjection(string storeType) => $"CAST(NULL AS {storeType})"`.

Neither dialect carries a `FieldType` → type table: per the AMENDMENT above, the store type is EF's to
resolve and the spike's `SpikeEngine.ColumnType` switches are **not** carried over — in the spike the same
switch also created the tables, so it could not diverge from the DDL by construction, and in PR2 the DDL
comes from `DescriptorModelBuilder` + EF's type mapping instead.

- [ ] **Step 5: Add the two members to the registration and pass them from both drivers**

In `RelationalProviderRegistration.cs`, add:

```csharp
    /// <summary>
    /// The driver's <see cref="IFieldSqlRenderer"/> — how a field, a bind parameter, a boolean literal
    /// and a case-insensitive <c>LIKE</c> are spelled in this dialect. Required rather than optional: a
    /// provider that cannot render an expression cannot serve <c>IAlvoData</c> at all, so a nullable
    /// member would only move the failure from registration time to request time.
    /// </summary>
    public required IFieldSqlRenderer Fields { get; init; }

    /// <summary>The driver's <see cref="IAlvoSqlDialect"/> — how a table, a column, a typed SQL <c>NULL</c> and a row lock are spelled.</summary>
    public required IAlvoSqlDialect Dialect { get; init; }
```

In `AlvoSqliteBuilderExtensions.AddSqliteProvider`, add the two members and turn on relational nulls:

```csharp
    private static IAlvoBuilder AddSqliteProvider(IAlvoBuilder builder) =>
        builder.AddRelationalProvider(new RelationalProviderRegistration
        {
            ConnectionString = ResolveConnectionString,
            ConfigureProvider = static (options, connectionString) =>
                options.UseSqlite(connectionString, static sqlite => sqlite.UseRelationalNulls()),
            CreateModelBuilder = static () => new ModelBuilder(SqliteConventionSetBuilder.Build()),
            CreateDatabaseModelFactory = CreateDatabaseModelFactory,
            CreateConnection = static connectionString => new SqliteConnection(WithoutPooling(connectionString)),
            Fields = new SqliteFieldSqlRenderer(),
            Dialect = new SqliteSqlDialect(),
        });
```

Mirror it in `AlvoPostgreSqlBuilderExtensions` (`options.UseNpgsql(connectionString, static npgsql =>
npgsql.UseRelationalNulls())`, `new PostgreSqlFieldSqlRenderer()`, `new PostgreSqlSqlDialect()`).

`UseRelationalNulls` makes EF translate comparisons with SQL's three-valued semantics instead of
compensating for C#'s — which is what `AlvoFilterOperator`'s documented contract requires. See
*Deviations* 3.

- [ ] **Step 6: Add the SQLite snapshot subclass**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataSqlSnapshotTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteAlvoDataSqlSnapshotTests : AlvoDataSqlSnapshotTests, IDisposable
{
    private readonly ServiceProvider _services = BuildServices();

    protected override string EngineName => "sqlite";

    protected override ICelCompiler Compiler => _services.GetRequiredService<ICelCompiler>();

    protected override IPredicateRenderer Renderer => _services.GetRequiredService<IPredicateRenderer>();

    protected override IFieldSqlRenderer Fields { get; } = new SqliteFieldSqlRenderer();

    private static ServiceProvider BuildServices() => new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

`AddAlvo()` returns an `IAlvoBuilder`, so `.Services` is how the collection comes back out; no database
provider is attached because nothing here touches a database.

- [ ] **Step 7: Delete the spike**

```bash
rm -rf spike/MMLib.Alvo.Data.Spike
rmdir spike 2>/dev/null || true
```

Then remove the whole `/spike/` folder element from `MMLib.Alvo.slnx`:

```xml
  <Folder Name="/spike/">
    <Project Path="spike/MMLib.Alvo.Data.Spike/MMLib.Alvo.Data.Spike.csproj" />
  </Folder>
```

The spike's verdict document stays — it is the citable evidence this plan rests on. Nothing else of the
spike survives except the code this task lifted into the two drivers and `AlvoDataSqlSnapshotTests`.

- [ ] **Step 8: Run ring1, accept baselines, commit**

Run: `scripts/test-ring1`. Accept `cel-to-sql-sqlite.verified.txt` and the four moved public-API
baselines (the three data assemblies plus `MMLib.Alvo.Testing`). `PublicApi.MMLib.Alvo.Abstractions`
must **not** move — this task changes no port. The turn gate will fire on the moved `*.verified.*`
files; dispatch `alvo-snapshot-judge`.

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore src/MMLib.Alvo.Data.Sqlite src/MMLib.Alvo.Data.PostgreSql src/MMLib.Alvo.Testing test MMLib.Alvo.slnx
git add -A spike
git commit -m "feat(data): add the per-engine SQL renderers and the IAlvoSqlDialect seam"
```

---

## Task 2: The applied schema — `ISchemaRegistry` implemented by the policy catalog provider

`AlvoServiceCollectionExtensions` carries `TODO(#19): register ISchemaRegistry once the Data API needs
it`. PR2 is that consumer, and it must not get its schema from a second, independently primed holder:
a schema the rules were never compiled against is precisely the catalog/schema mismatch `IAlvoData`'s
remarks forbid being the one path an unvalidated payload reaches storage on.

**Follow PR1's precedent exactly, and read it first.** The identical question was settled for the
descriptor's `RoleCatalog` in PR1's final review: not a public member on `PolicyCatalog`, but a
role-shaped **port** (`IRoleCatalogProvider`) that `IPolicyCatalogProvider` derives from, with
`PolicyCatalog.Roles` kept `internal`. Read
`src/MMLib.Alvo.Abstractions/Identity/IRoleCatalogProvider.cs` (its remarks carry the whole argument),
`src/MMLib.Alvo/Rules/IPolicyCatalogProvider.cs` and
`src/MMLib.Alvo/Rules/Internal/PolicyCatalogProvider.cs` before writing a line. This task does the same
thing for the schema, through the `ISchemaRegistry` port that already ships. See *Deviations* 1 for why
the two differences from that precedent (an existing port, a non-nullable return) are deliberate.

**Files:**
- Modify: `src/MMLib.Alvo/Rules/PolicyCatalog.cs`
- Modify: `src/MMLib.Alvo/Rules/IPolicyCatalogProvider.cs`
- Modify: `src/MMLib.Alvo/Rules/Internal/PolicyCatalogProvider.cs`
- Modify: `src/MMLib.Alvo/Rules/Setup.cs`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`
- Test: `test/MMLib.Alvo.Tests/Rules/PolicyCatalogProviderSchemaTests.cs`
- Modify: `test/MMLib.Alvo.Tests/Rules/RulesSetupTests.cs`
- Modify: `test/_shared/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `MMLib.Alvo.Schema.ISchemaRegistry` (`SchemaModel GetSchema()` — already shipped in
  `Abstractions`, do not change its shape); `MMLib.Alvo.IRoleCatalogProvider` (`RoleCatalog?
  DeclaredRoles { get; }`) — the precedent to mirror; `MMLib.Alvo.Rules.IPolicyCatalogProvider`
  (`PolicyCatalog? Current { get; }`, `void SetCurrent(string project, PolicyCatalog catalog)`);
  `PolicyCatalog.Build(AlvoDescriptor, SchemaModel, ICelCompiler)`.
- Produces:
  - `PolicyCatalog` gains **`internal`** `SchemaModel Schema { get; }` (not public — see *Deviations* 1).
  - `IPolicyCatalogProvider` now derives from `IRoleCatalogProvider` **and**
    `MMLib.Alvo.Schema.ISchemaRegistry`.
  - `PolicyCatalogProvider` implements `SchemaModel GetSchema()` off the same `Current` reference as
    `DeclaredRoles`.
  - `RulesSetup.AddAlvoRules` registers `ISchemaRegistry` as the same singleton instance as
    `IPolicyCatalogProvider`.
  - After this task, `ISchemaRegistry` resolves from `AddAlvo()` and returns the applied `SchemaModel`
    for whichever descriptor last primed the policy catalog.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Tests/Rules/PolicyCatalogProviderSchemaTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using DescField = MMLib.Alvo.Descriptor.FieldType;

namespace MMLib.Alvo.Tests.Rules;

public class PolicyCatalogProviderSchemaTests
{
    /// <summary>
    /// The invariant the port exists for: one instance answers both questions, so the schema a data port
    /// validates against can never be a different apply's from the rules that judge the same request.
    /// </summary>
    [Fact]
    public void The_schema_registry_and_the_policy_catalog_provider_are_one_instance()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

        services.GetRequiredService<ISchemaRegistry>()
            .ShouldBeSameAs(services.GetRequiredService<IPolicyCatalogProvider>());
    }

    /// <summary>
    /// A host with its own schema source registers its own <see cref="ISchemaRegistry"/> and takes it
    /// over — the same escape hatch <see cref="IRoleCatalogProvider"/> gives an external identity source.
    /// </summary>
    [Fact]
    public void A_host_can_replace_the_schema_registry_without_touching_the_policy_catalog()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<ISchemaRegistry>(new FixedSchemaRegistry(new SchemaModel([])));
        collection.AddAlvo();
        using var services = collection.BuildServiceProvider();

        services.GetRequiredService<ISchemaRegistry>().ShouldBeOfType<FixedSchemaRegistry>();
        services.GetRequiredService<IPolicyCatalogProvider>().ShouldNotBeNull();
    }

    [Fact]
    public void An_unprimed_registry_declares_no_entity_rather_than_throwing()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

        services.GetRequiredService<ISchemaRegistry>().GetSchema().Entities.ShouldBeEmpty();
    }

    [Fact]
    public void Priming_the_policy_catalog_also_publishes_the_schema_it_was_compiled_against()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        var (descriptor, schema) = Fixture("vehicle");

        services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(
            descriptor.Name, PolicyCatalog.Build(descriptor, schema, services.GetRequiredService<ICelCompiler>()));

        var published = services.GetRequiredService<ISchemaRegistry>().GetSchema();
        published.ShouldBeSameAs(schema);
    }

    [Fact]
    public void Re_priming_publishes_the_new_schema_not_the_previous_one()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        var compiler = services.GetRequiredService<ICelCompiler>();
        var catalogs = services.GetRequiredService<IPolicyCatalogProvider>();
        var (first, firstSchema) = Fixture("vehicle");
        var (second, secondSchema) = Fixture("vehicle", extraField: "colour");

        catalogs.SetCurrent(first.Name, PolicyCatalog.Build(first, firstSchema, compiler));
        catalogs.SetCurrent(second.Name, PolicyCatalog.Build(second, secondSchema, compiler));

        var published = services.GetRequiredService<ISchemaRegistry>().GetSchema();
        published.Entities[0].Fields.Select(f => f.Name).ShouldContain("colour");
    }

    private static (AlvoDescriptor Descriptor, SchemaModel Schema) Fixture(string entity, string? extraField = null)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["plate"] = new() { Type = DescField.String },
        };
        if (extraField is not null)
        {
            fields[extraField] = new FieldDescriptor { Type = DescField.String };
        }

        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "primed-registry",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [entity] = new() { Fields = fields, Rules = new AccessRules { List = "true" } },
            },
        };

        return (descriptor, DescriptorToSchemaMapper.Map(descriptor));
    }

    private sealed class FixedSchemaRegistry(SchemaModel schema) : ISchemaRegistry
    {
        public SchemaModel GetSchema() => schema;
    }
}
```

If `DescriptorToSchemaMapper.Map` is not reachable as written, use the exact call shape
`test/MMLib.Alvo.Tests/Descriptor/DescriptorToSchemaMapperTests.cs` already uses — it is the mapper
whose output `PolicyCatalog.Build`'s own remarks require callers to pass, so do not hand-build the
`SchemaModel` here.

- [ ] **Step 2: Run it, expect failure**

Run: `dotnet test --project test/MMLib.Alvo.Tests`
Expected: FAIL — `No service for type 'MMLib.Alvo.Schema.ISchemaRegistry' has been registered.`

- [ ] **Step 3: Add the internal `PolicyCatalog.Schema`**

In `src/MMLib.Alvo/Rules/PolicyCatalog.cs`, add the property directly beneath the existing `Roles`, so
the two siblings read as one rule:

```csharp
    /// <summary>
    /// Gets the schema every rule in this catalog was compiled and type-checked against — the same
    /// <see cref="SchemaModel"/> handed to <see cref="Build"/>.
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> for exactly the reason <see cref="Roles"/> is: a consumer reads the
    /// applied schema through <see cref="Schema.ISchemaRegistry"/>, which
    /// <see cref="IPolicyCatalogProvider"/> implements, so nothing above the engine has to know that the
    /// authoritative schema currently happens to arrive with a policy catalog. A public member here would
    /// make the <em>policy</em> catalog the authoritative source of the <em>applied schema</em> and
    /// foreclose any other source — F7's dynamic-entity registry being the obvious next one.
    /// </remarks>
    internal SchemaModel Schema { get; }
```

Constructor: add a `SchemaModel schema` parameter (the constructor is already `internal`, so this is not
a public break), `ArgumentNullException.ThrowIfNull(schema)`, `Schema = schema;`. Update the single
`new PolicyCatalog(...)` call inside `TryBuild` to pass its own `schema` argument.

- [ ] **Step 4: Derive the provider port from `ISchemaRegistry` and implement it**

In `src/MMLib.Alvo/Rules/IPolicyCatalogProvider.cs`, widen the base list and add the paragraph that
explains why, mirroring the `IRoleCatalogProvider` paragraph already there:

```csharp
public interface IPolicyCatalogProvider : IRoleCatalogProvider, MMLib.Alvo.Schema.ISchemaRegistry
```

```csharp
/// <para>
/// It likewise serves as the default <see cref="Schema.ISchemaRegistry"/>. A data port has to validate a
/// caller's filter and sort keys, and a write payload, against the entity's declared fields — and it must
/// be the <em>same</em> schema the rules were compiled against, or the one path on which an unvalidated
/// payload reaches storage is a mismatch between two independently primed holders. One instance
/// registered as both means the rules that judge a request and the schema that validates it always come
/// from one apply. A host with its own schema source registers its own
/// <see cref="Schema.ISchemaRegistry"/> and takes it over, exactly as an external identity source does
/// for <see cref="IRoleCatalogProvider"/>.
/// </para>
```

In `src/MMLib.Alvo/Rules/Internal/PolicyCatalogProvider.cs`, add the member beside `DeclaredRoles`:

```csharp
    /// <inheritdoc/>
    /// <remarks>
    /// The same single volatile read <see cref="Current"/> and <see cref="DeclaredRoles"/> take, so a
    /// request's rules, its role set and the schema validating its field names always come from one
    /// applied descriptor. An unprimed provider reports an <em>empty</em> model rather than
    /// <see langword="null"/> — unlike <see cref="DeclaredRoles"/>, whose port distinguishes "no set
    /// declared" from "an empty set". Empty is the fail-closed value here: no entity declared means every
    /// entity name and every field name a caller supplies is refused, and <c>IPolicyEngine</c> has already
    /// denied the operation one layer earlier anyway.
    /// </remarks>
    public SchemaModel GetSchema() => Current?.Schema ?? _unprimedSchema;

    private static readonly SchemaModel _unprimedSchema = new([]);
```

In `src/MMLib.Alvo/Rules/Setup.cs`, add `using MMLib.Alvo.Schema;` and register the port to the same
instance, immediately after the existing `IRoleCatalogProvider` line and for the same stated reason:

```csharp
        services.TryAddSingleton<ISchemaRegistry>(
            provider => provider.GetRequiredService<IPolicyCatalogProvider>());
```

Extend `AddAlvoRules`'s `<remarks>` with one sentence naming the second port and why it resolves to the
same instance rather than to a second registration.

In `AlvoServiceCollectionExtensions.AddAlvo`, delete the `// TODO(#19): register ISchemaRegistry …` line
and replace the `<remarks>` paragraph saying `ISchemaRegistry` is deliberately not registered with one
stating that it now arrives with the policy catalog provider and reads empty until a descriptor is
applied.

- [ ] **Step 5: Add the wiring fact and run ring1, accept the baseline, commit**

Add one fact to `test/MMLib.Alvo.Tests/Rules/RulesSetupTests.cs`, beside the existing
`IRoleCatalogProvider` same-instance fact if there is one:

```csharp
    [Fact]
    public void The_schema_registry_resolves_to_the_policy_catalog_provider_instance()
    {
        var services = new ServiceCollection();
        services.AddAlvoRules();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISchemaRegistry>()
            .ShouldBeSameAs(provider.GetRequiredService<IPolicyCatalogProvider>());
    }
```

Run: `scripts/test-ring1`. Accept the moved `PublicApi.MMLib.Alvo.verified.txt` — the only public change
is `IPolicyCatalogProvider`'s base list gaining `ISchemaRegistry`. `PolicyCatalog.Schema` and
`PolicyCatalogProvider` are internal, so **neither appears in the baseline**; if `Schema` shows up there,
it was made public by mistake.

```bash
git add src/MMLib.Alvo test/MMLib.Alvo.Tests test/_shared
git commit -m "feat(schema): serve ISchemaRegistry from the policy catalog provider"
```

---

## Task 3: The runtime read model, the model cache key, and SQL null semantics

Records have no CLR types, so the read model is a property bag: `SharedTypeEntity<Dictionary<string,
object>>` per entity with one `IndexerProperty` per field (spike `Q0`/`Q1`). Two things are not
obvious. **Every property is optional**, because that is the only way a `hidden` `NOT NULL` column can
be NULL-projected out of the `SELECT` list (`Q4g`; the schema-faithful model throws two *different*
exception types per engine, `Q4f`). And EF caches **one model per `DbContext` CLR type**, so a
descriptor re-apply would silently keep serving the old model — the spike's first-named open risk, and
the first thing this task tests.

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/FieldClrTypeMap.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AlvoDataContext.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AlvoModelCacheKeyFactory.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AlvoDataContextFactory.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DescriptorModelBuilder.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FieldClrTypeMapTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataModelTests.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Properties/AssemblyInfo.cs`

**Interfaces:**
- Consumes: `MMLib.Alvo.Schema.ISchemaRegistry`, `SchemaModel`, `EntitySchema`, `FieldSchema`,
  `FieldType`; `RelationalProviderRegistration.ConfigureProvider` and `.ConnectionString`.
- Produces:
  - `internal static class FieldClrTypeMap` — `internal static Type Exact(FieldSchema field)` (the
    migration model's mapping: value types nullable only when `field.Nullable`) and
    `internal static Type Optional(FieldSchema field)` (always nullable — the read model's mapping).
  - `internal sealed class AlvoDataContext : DbContext` — constructor
    `AlvoDataContext(DbContextOptions options, SchemaModel schema, Guid modelToken)`;
    `internal Guid ModelToken { get; }`;
    `internal DbSet<Dictionary<string, object>> Rows(string entity)`;
    `internal const string IdColumn = "id"`; `internal const string TenantIdColumn = "tenant_id"`.
  - `internal sealed class AlvoModelCacheKeyFactory : IModelCacheKeyFactory`.
  - `internal sealed class AlvoDataContextFactory` — constructor
    `AlvoDataContextFactory(ISchemaRegistry schemas, Action<DbContextOptionsBuilder> configureProvider)`;
    `internal AlvoDataContext Create()`; `internal SchemaModel Schema { get; }` (the model the last
    `Create()` observed).

- [ ] **Step 1: Write the failing CLR-type-map test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FieldClrTypeMapTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class FieldClrTypeMapTests
{
    [Theory]
    [InlineData(FieldType.Uuid, typeof(Guid))]
    [InlineData(FieldType.Ref, typeof(Guid))]
    [InlineData(FieldType.Integer, typeof(long))]
    [InlineData(FieldType.Decimal, typeof(decimal))]
    [InlineData(FieldType.Boolean, typeof(bool))]
    [InlineData(FieldType.Date, typeof(DateOnly))]
    [InlineData(FieldType.DateTime, typeof(DateTimeOffset))]
    public void A_non_nullable_value_field_maps_exactly(FieldType type, Type expected)
        => FieldClrTypeMap.Exact(Field(type, nullable: false)).ShouldBe(expected);

    [Theory]
    [InlineData(FieldType.Uuid, typeof(Guid?))]
    [InlineData(FieldType.Integer, typeof(long?))]
    [InlineData(FieldType.DateTime, typeof(DateTimeOffset?))]
    public void The_read_model_makes_every_value_field_nullable_even_when_the_column_is_not(FieldType type, Type expected)
        => FieldClrTypeMap.Optional(Field(type, nullable: false)).ShouldBe(expected);

    [Theory]
    [InlineData(FieldType.String)]
    [InlineData(FieldType.Text)]
    [InlineData(FieldType.Json)]
    [InlineData(FieldType.Enum)]
    public void A_string_backed_field_is_a_string_in_both_models(FieldType type)
    {
        FieldClrTypeMap.Exact(Field(type, nullable: false)).ShouldBe(typeof(string));
        FieldClrTypeMap.Optional(Field(type, nullable: false)).ShouldBe(typeof(string));
    }

    private static FieldSchema Field(FieldType type, bool nullable) =>
        new() { Name = "f", Type = type, Nullable = nullable };
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`
Expected: FAIL — `FieldClrTypeMap` does not exist.

- [ ] **Step 2: Implement the map and make `DescriptorModelBuilder` use it**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/FieldClrTypeMap.cs`:

```csharp
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The single <see cref="FieldSchema"/> → CLR type mapping in the framework, in the two shapes the two
/// EF models need: <see cref="Exact"/> for the migration model, whose column nullability must match the
/// schema, and <see cref="Optional"/> for the read model, where every property is nullable so a masked
/// field can be projected as a typed SQL <c>NULL</c> without the shaper throwing.
/// </summary>
/// <remarks>
/// One mapping, not two, because it is also the contract <c>IAlvoData</c> publishes to callers — a
/// <c>uuid</c> field reads back as a <see cref="Guid"/>, a timestamp as a
/// <see cref="DateTimeOffset"/>, a decimal as a <see cref="decimal"/> — and a second copy is how the
/// read path and the migration path come to disagree about what a column holds.
/// </remarks>
internal static class FieldClrTypeMap
{
    internal static Type Exact(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Wrap(Bare(field.Type), field.Nullable);
    }

    internal static Type Optional(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Wrap(Bare(field.Type), nullable: true);
    }

    private static Type Bare(FieldType type) => type switch
    {
        FieldType.Uuid or FieldType.Ref => typeof(Guid),
        FieldType.String or FieldType.Text or FieldType.Json or FieldType.Enum => typeof(string),
        FieldType.Integer => typeof(long),
        FieldType.Decimal => typeof(decimal),
        FieldType.Boolean => typeof(bool),
        FieldType.Date => typeof(DateOnly),
        FieldType.DateTime => typeof(DateTimeOffset),
        _ => throw new NotSupportedException($"Unsupported field type '{type}'."),
    };

    private static Type Wrap(Type type, bool nullable) =>
        nullable && type.IsValueType ? typeof(Nullable<>).MakeGenericType(type) : type;
}
```

In `DescriptorModelBuilder.cs`, delete its private `ClrType` and `NullableIfNeeded` and call
`FieldClrTypeMap.Exact(field)` from `ConfigureField`. `DescriptorModelBuilderTests` must stay green
unchanged — the mapping is identical; if it moves, you changed behaviour, not just location.

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests` → PASS.

- [ ] **Step 3: Write the failing read-model test over a real SQLite database**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataModelTests.cs`. This is the spike's
first-named open risk (*model invalidation on descriptor re-apply is not proven*) plus the two
null-semantics facts, all over a real engine:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteAlvoDataModelTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task Re_applying_a_descriptor_with_a_new_field_invalidates_the_cached_model()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();
        using (var before = factory.Create())
        {
            Properties(before, "vehicle").ShouldNotContain("colour");
        }

        await host.RePrimeAsync(SchemaWith("plate", "colour"));

        using var after = factory.Create();
        Properties(after, "vehicle").ShouldContain("colour");
    }

    [Fact]
    public async Task Every_read_model_property_is_optional_even_for_a_not_null_column()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();

        var plate = context.Model.FindEntityType("vehicle")!.FindProperty("plate")!;
        plate.IsNullable.ShouldBeTrue();
    }

    [Fact]
    public async Task Queries_do_not_track_so_a_returned_row_can_never_be_written_back()
    {
        var host = await _fixture.StartAsync(SchemaWith("plate"));
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();

        context.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.NoTracking);
    }

    private static IReadOnlyList<string> Properties(DbContext context, string entity) =>
        [.. context.Model.FindEntityType(entity)!.GetProperties().Select(p => p.Name)];

    private static SchemaModel SchemaWith(params string[] extraStringFields) => /* see Step 4 */ null!;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

- [ ] **Step 4: Write the shared SQLite fixture the rest of the PR reuses**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataFixture.cs`. Every SQLite data-path test in
this PR goes through it, so it is written once, here, with **per-call isolation** — a fresh temp
database file and a fresh container per `StartAsync`, which
`AlvoDataAdversarialTests`'s own remarks require:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Stands up one isolated, real SQLite database per <see cref="StartAsync"/> call: a fresh temp file, a
/// fresh service provider wired through the public <c>UseSqlite</c> entry point, the physical tables
/// created by the production <see cref="ISchemaMigrator"/>, and the policy catalog primed from the same
/// descriptor. Per-call isolation is not optional — several adversarial facts assert exact row counts
/// over entities with no row-scoping predicate at all.
/// </summary>
public sealed class SqliteAlvoDataFixture : IAsyncDisposable
{
    private readonly List<string> _files = [];
    private readonly List<ServiceProvider> _providers = [];

    public async Task<AlvoDataHost> StartAsync(SchemaModel schema, AlvoDescriptor? descriptor = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-data-{Guid.NewGuid():N}.db");
        _files.Add(path);

        var builder = new FixtureAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={path}");
        builder.Services.AddAlvo();
        var services = builder.Services.BuildServiceProvider();
        _providers.Add(services);

        var migrator = services.GetRequiredService<ISchemaMigrator>();
        await migrator.ApplyAsync(
            await migrator.PlanAsync(new SchemaModel([]), schema, new MigrationOptions()),
            new MigrationOptions());

        var host = new AlvoDataHost(services, descriptor ?? MinimalDescriptor(schema));
        await host.RePrimeAsync(schema);
        return host;
    }

    private static AlvoDescriptor MinimalDescriptor(SchemaModel schema) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "data-path-fixture",
        Entities = schema.Entities.ToDictionary(
            entity => entity.Name,
            entity => new EntityDescriptor
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                Rules = new AccessRules { List = "true", Get = "true" },
            },
            StringComparer.Ordinal),
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        foreach (var file in _files.Where(File.Exists))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FixtureAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}

/// <summary>One started database plus the descriptor whose policy is primed against it.</summary>
public sealed class AlvoDataHost(ServiceProvider services, AlvoDescriptor descriptor)
{
    public ServiceProvider Services => services;

    /// <summary>Re-primes the policy catalog (and therefore the applied schema) from <paramref name="schema"/>.</summary>
    public Task RePrimeAsync(SchemaModel schema)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, services.GetRequiredService<ICelCompiler>());
        services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(descriptor.Name, catalog);
        return Task.CompletedTask;
    }
}
```

Fill in `SchemaWith` in the test from `AlvoDataSqlSnapshotTests.SnapshotEntity`, trimmed to `id`,
`plate` and the requested extra string fields. **`ApplyAsync`/`PlanAsync` call shape:** mirror
`src/MMLib.Alvo.Testing/Migrations/SchemaMigratorContractTests.cs` lines 56–57, which is the same
`Empty() → desired` sequence.

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests` → FAIL (`AlvoDataContextFactory` does
not exist).

- [ ] **Step 5: Implement the context, the cache key and the factory**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AlvoDataContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The one <see cref="DbContext"/> the Alvo data path uses, whose model is built at request time from
/// the applied <see cref="SchemaModel"/> as property-bag entity types — records have no CLR types, so
/// there is no entity class to map. <see langword="internal"/> and never handed out: a tracked,
/// mutated property bag saved through the change tracker emits <c>UPDATE … WHERE id = @p</c> with
/// <b>no policy predicate at all</b>, so reachability of this type from outside the data path is an
/// authorization bypass, not a style question.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every property is optional</b>, regardless of the column's own nullability. A <c>hidden</c> field
/// is removed from a response by projecting a typed SQL <c>NULL</c> in its place (the column itself is
/// never read), and a required property would make the shaper throw on that <c>NULL</c> — with a
/// different exception type on each engine. Required-ness is enforced where it belongs: by the
/// database's own <c>NOT NULL</c> on the write path, and by schema-derived request validation above
/// this layer.
/// </para>
/// <para>
/// Queries do not track. That is set once here rather than as an <c>AsNoTracking()</c> per call site,
/// because one forgotten call site is enough to turn a returned row into a tracked entity that a later
/// <c>SaveChanges</c> would write back around policy. Inserts still work — tracking behaviour governs
/// queries, not <see cref="DbContext.Add(object)"/>.
/// </para>
/// <para>
/// No foreign key or navigation is configured. The migration model owns the physical relationships; a
/// <c>Ref</c> field is a <c>uuid</c> column here, and relation embedding is not part of this query path.
/// </para>
/// </remarks>
internal sealed class AlvoDataContext : DbContext
{
    internal const string IdColumn = "id";
    internal const string TenantIdColumn = "tenant_id";

    private readonly SchemaModel _schema;

    internal AlvoDataContext(DbContextOptions options, SchemaModel schema, Guid modelToken)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        ModelToken = modelToken;
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <summary>
    /// Gets the token identifying the applied schema this context's model was built from — the value
    /// <see cref="AlvoModelCacheKeyFactory"/> puts in the model cache key, so a descriptor re-apply gets
    /// a freshly built model instead of silently reusing the previous one.
    /// </summary>
    internal Guid ModelToken { get; }

    internal DbSet<Dictionary<string, object>> Rows(string entity) => Set<Dictionary<string, object>>(entity);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var entity in _schema.Entities.Where(entity => entity.Storage == EntityStorage.Physical))
        {
            ConfigureEntity(modelBuilder, entity);
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

    private static void ConfigureEntity(ModelBuilder modelBuilder, EntitySchema entity)
    {
        var builder = modelBuilder.SharedTypeEntity<Dictionary<string, object>>(entity.Name);
        builder.ToTable(entity.Name);

        foreach (var field in entity.Fields)
        {
            ConfigureField(builder, field);
        }

        builder.HasKey(IdColumn);
    }

    private static void ConfigureField(EntityTypeBuilder<Dictionary<string, object>> builder, FieldSchema field)
    {
        var property = builder.IndexerProperty(FieldClrTypeMap.Optional(field), field.Name).IsRequired(false);

        if (field.MaxLength is { } maxLength)
        {
            property.HasMaxLength(maxLength);
        }

        if (field.Precision is { } precision)
        {
            property = field.Scale is { } scale ? property.HasPrecision(precision, scale) : property.HasPrecision(precision);
        }
    }
}
```

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AlvoModelCacheKeyFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Keys EF's model cache on the applied schema as well as the context type. EF caches exactly one model
/// per <see cref="DbContext"/> CLR type, and Alvo's model is built from a descriptor that changes at
/// runtime — without this, the first schema a process ever saw would be served forever, so a field added
/// by a runtime apply would be invisible and a removed one would still be queried.
/// </summary>
internal sealed class AlvoModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc/>
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.GetType(), (context as AlvoDataContext)?.ModelToken ?? Guid.Empty, designTime);
    }
}
```

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AlvoDataContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Builds one <see cref="AlvoDataContext"/> per data operation and mints a fresh model token whenever
/// the applied <see cref="SchemaModel"/> changes. A context per operation rather than a scoped shared
/// one: the data path opens its own transaction for a write and never hands a context out, so there is
/// nothing to share, and a long-lived context would keep a change tracker alive next to a code path
/// whose whole design is that no change tracker exists.
/// </summary>
internal sealed class AlvoDataContextFactory
{
    private readonly ISchemaRegistry _schemas;
    private readonly Action<DbContextOptionsBuilder> _configureProvider;
    private readonly Lock _gate = new();
    private SchemaModel? _observed;
    private Guid _token;

    internal AlvoDataContextFactory(ISchemaRegistry schemas, Action<DbContextOptionsBuilder> configureProvider)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(configureProvider);
        _schemas = schemas;
        _configureProvider = configureProvider;
    }

    internal AlvoDataContext Create()
    {
        var schema = _schemas.GetSchema();
        var token = TokenFor(schema);
        var options = new DbContextOptionsBuilder();
        _configureProvider(options);
        options.ReplaceService<IModelCacheKeyFactory, AlvoModelCacheKeyFactory>();

        return new AlvoDataContext(options.Options, schema, token);
    }

    /// <summary>
    /// The token for <paramref name="schema"/>: a new <see cref="Guid"/> the first time a given applied
    /// model instance is seen, and the same one thereafter. Keyed on reference identity rather than on a
    /// content hash because the applied model is replaced wholesale on every apply — a new object is
    /// exactly the signal a new model is needed, and a deep hash of every entity and field on every
    /// operation would not be.
    /// </summary>
    private Guid TokenFor(SchemaModel schema)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_observed, schema))
            {
                _observed = schema;
                _token = Guid.NewGuid();
            }

            return _token;
        }
    }
}
```

- [ ] **Step 6: Register the factory and open internals to the engine test projects**

In `AlvoEfCoreProvider.AddRelationalProvider`, add:

```csharp
        builder.Services.TryAddSingleton(services => new AlvoDataContextFactory(
            services.GetRequiredService<ISchemaRegistry>(),
            options => registration.ConfigureProvider(options, registration.ConnectionString(services))));
```

In `src/MMLib.Alvo.Data.EntityFrameworkCore/Properties/AssemblyInfo.cs`, add the two engine test
assemblies — the data-path tests must reach `AlvoDataContextFactory` and `AlvoDataSeed`, and the repo
already uses this pattern (`MMLib.Alvo` opens itself to `MMLib.Alvo.Tests` and
`MMLib.Alvo.Data.Sqlite.Tests`):

```csharp
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.EntityFrameworkCore.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.PostgreSql.Tests.Integration")]
```

- [ ] **Step 7: Run ring1 and commit**

Run: `scripts/test-ring1`. All three `SqliteAlvoDataModelTests` facts must pass — in particular the
re-apply one, which is the spike's own first open risk closed.

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests test/MMLib.Alvo.Data.Sqlite.Tests
git commit -m "feat(data): build the runtime property-bag read model with a schema-keyed model cache"
```

---
## Task 4: The parameter binder — every value through EF's own type mapping

This is the spike's single most load-bearing finding and it cost it an hour: EF's SQLite `Guid` mapping
stores an **upper-case** `TEXT`, so a hand-formatted lower-case Guid in a `WHERE` returns zero rows
**with no error** (`Q6d`, `X2c`). The rule is absolute — every value reaches ADO.NET through
`IRelationalTypeMappingSource.FindMapping(clrType).CreateParameter(...)`, so the comparison is made in
exactly the representation EF wrote.

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/PredicateParameterBinder.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/PolicyParameterPrefix.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/PolicyParameterPrefixTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteParameterBindingTests.cs`

**Interfaces:**
- Consumes: `AlvoDataContext` (Task 3) for `Database.GetDbConnection()` and
  `this.GetService<IRelationalTypeMappingSource>()`; `MMLib.Alvo.Expressions.SqlPredicate`
  (`string Sql`, `IReadOnlyDictionary<string, object?> Parameters`).
- Produces:
  - `internal static class PolicyParameterPrefix` — `internal const string Using = "alvo_u"`,
    `WithCheck = "alvo_c"`, `TenantScope = "alvo_t"`, `Filter = "alvo_f"`, `Keyset = "alvo_k"`,
    `RowId = "alvo_id"`; and `internal static IReadOnlyList<string> All { get; }`.
  - `internal sealed class PredicateParameterBinder` — constructor
    `PredicateParameterBinder(AlvoDataContext context)`;
    `internal DbParameter[] Bind(params IReadOnlyDictionary<string, object?>[] bags)`;
    `internal DbParameter Bind(string name, object? value)`.

- [ ] **Step 1: Write the failing prefix-disjointness test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/PolicyParameterPrefixTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class PolicyParameterPrefixTests
{
    [Fact]
    public void No_reserved_name_starts_with_ef_cores_own_parameter_letter()
        => PolicyParameterPrefix.All.ShouldAllBe(name => !name.StartsWith('p'));

    [Fact]
    public void Every_reserved_name_starts_with_the_reserved_alvo_word()
        => PolicyParameterPrefix.All.ShouldAllBe(name => name.StartsWith("alvo_", StringComparison.Ordinal));

    /// <summary>
    /// Generated names are a prefix plus an ordinal, so one prefix being a prefix of another would make
    /// <c>alvo_f1</c> and <c>alvo_f</c>+<c>1</c> collide across two independently numbered families.
    /// </summary>
    [Fact]
    public void No_reserved_name_is_a_prefix_of_another()
    {
        foreach (var name in PolicyParameterPrefix.All)
        {
            PolicyParameterPrefix.All
                .Where(other => !string.Equals(other, name, StringComparison.Ordinal))
                .ShouldAllBe(other => !other.StartsWith(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_three_policy_predicates_have_three_distinct_prefixes()
        => new[] { PolicyParameterPrefix.Using, PolicyParameterPrefix.WithCheck, PolicyParameterPrefix.TenantScope }
            .Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests`
Expected: FAIL — `PolicyParameterPrefix` does not exist.

- [ ] **Step 2: Implement the reserved names**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/PolicyParameterPrefix.cs`:

```csharp
namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Every bind-parameter name family the data path generates, kept disjoint from each other and from the
/// <c>pN</c> names EF Core mints for its own positional <c>FromSql</c> arguments and
/// <c>ExecuteUpdate</c> setters.
/// </summary>
/// <remarks>
/// The collision this exists to prevent does not raise an error: given a name EF also wants, EF renames
/// the caller's parameter while the SQL text still reads the original name, so the other value is
/// substituted into the security predicate — on PostgreSQL that usually surfaces as a type error, on
/// SQLite it returns the wrong rows silently. A <c>PolicyDecision</c> carries three predicates, so it
/// needs three prefixes, and the two statement-level families and the row id need names of their own.
/// </remarks>
internal static class PolicyParameterPrefix
{
    /// <summary>The prefix for the <c>USING</c> predicate's parameters.</summary>
    internal const string Using = "alvo_u";

    /// <summary>
    /// The prefix reserved for the <c>WITH CHECK</c> predicate's parameters. Unused today — the check is
    /// evaluated in memory over the merged post-image, which SQL cannot see before the write — and
    /// reserved so a future SQL-side check (a <c>RETURNING</c>-based write, say) inherits a name that
    /// already cannot collide with the other two.
    /// </summary>
    internal const string WithCheck = "alvo_c";

    /// <summary>The prefix for the synthesized tenant scope's parameters.</summary>
    internal const string TenantScope = "alvo_t";

    /// <summary>The prefix for the caller filter's bound values.</summary>
    internal const string Filter = "alvo_f";

    /// <summary>The prefix for the keyset cursor predicate's bound values.</summary>
    internal const string Keyset = "alvo_k";

    /// <summary>The single name a row id binds to.</summary>
    internal const string RowId = "alvo_id";

    /// <summary>Every reserved name, for the disjointness invariant.</summary>
    internal static IReadOnlyList<string> All { get; } = [Using, WithCheck, TenantScope, Filter, Keyset, RowId];
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests` → PASS.

- [ ] **Step 3: Write the failing binding test — the differential the spike hand-ran**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteParameterBindingTests.cs`. The second fact is the
regression test for the spike's own false negative, and it is written so that it fails if the binder
ever starts formatting values itself:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Globalization;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteParameterBindingTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task A_guid_bound_through_efs_mapping_finds_the_row_it_wrote()
    {
        var host = await _fixture.StartAsync(new SchemaModel([AlvoDataSqlSnapshotTests.SnapshotEntity]));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();
        var ownerId = Guid.NewGuid();
        await AlvoDataSeed.SeedAsync(factory, Seed(ownerId));

        using var context = factory.Create();
        var binder = new PredicateParameterBinder(context);
        var matched = await CountAsync(context, "SELECT COUNT(*) FROM \"vehicle\" WHERE \"owner_id\" = @alvo_u0",
            binder.Bind(PolicyParameterPrefix.Using + "0", ownerId));

        matched.ShouldBe(1);
    }

    /// <summary>
    /// The spike's own first false negative, kept as a regression: EF's SQLite <c>Guid</c> mapping stores
    /// an upper-case <c>TEXT</c>, so the same value hand-formatted lower-case matches nothing — and
    /// matches nothing <em>silently</em>, which under a negated predicate would fail open.
    /// </summary>
    [Fact]
    public async Task The_same_guid_hand_formatted_as_lower_case_text_matches_nothing()
    {
        var host = await _fixture.StartAsync(new SchemaModel([AlvoDataSqlSnapshotTests.SnapshotEntity]));
        var factory = host.Services.GetRequiredService<AlvoDataContextFactory>();
        var ownerId = Guid.NewGuid();
        await AlvoDataSeed.SeedAsync(factory, Seed(ownerId));

        using var context = factory.Create();
        var handFormatted = new SqliteParameter("@alvo_u0", ownerId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
        var matched = await CountAsync(context, "SELECT COUNT(*) FROM \"vehicle\" WHERE \"owner_id\" = @alvo_u0", handFormatted);

        matched.ShouldBe(0);
    }

    [Fact]
    public async Task Every_awkward_clr_type_binds_with_a_real_db_type()
    {
        var host = await _fixture.StartAsync(new SchemaModel([AlvoDataSqlSnapshotTests.SnapshotEntity]));
        using var context = host.Services.GetRequiredService<AlvoDataContextFactory>().Create();
        var binder = new PredicateParameterBinder(context);

        object?[] values = [Guid.NewGuid(), "text", 42L, 12.34m, true, DateTimeOffset.UnixEpoch, new DateOnly(2026, 7, 26)];

        foreach (var value in values)
        {
            binder.Bind("alvo_f0", value).Value.ShouldNotBeNull();
        }

        binder.Bind("alvo_f0", null).Value.ShouldBe(DBNull.Value);
    }

    private static Dictionary<string, IReadOnlyList<Data.AlvoRecord>> Seed(Guid ownerId) =>
        new(StringComparer.Ordinal)
        {
            ["vehicle"] =
            [
                new Data.AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = Guid.NewGuid(),
                    ["tenant_id"] = Guid.NewGuid(),
                    ["owner_id"] = ownerId,
                    ["plate"] = "ACME-001",
                }),
            ],
        };

    private static async Task<long> CountAsync(DbContext context, string sql, params System.Data.Common.DbParameter[] parameters)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

`AlvoDataSeed` is written in Task 8's step for the write path; write it now as the small seam it is (see
Step 5 below) so this test can run.

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests` → FAIL.

- [ ] **Step 4: Implement the binder**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/PredicateParameterBinder.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Turns a rendered predicate's parameter bag into real <see cref="DbParameter"/>s through <b>EF Core's
/// own relational type mapping</b> — the only binding guaranteed to agree with the representation EF used
/// when it wrote the column.
/// </summary>
/// <remarks>
/// Formatting a value into text instead is not a style choice with a cosmetic cost: EF's SQLite
/// <c>Guid</c> mapping stores an upper-case <c>TEXT</c>, so a lower-case hand-formatted Guid in a
/// <c>WHERE</c> clause matches no row and raises nothing at all. Under an equality predicate that fails
/// closed; under a negated one it fails open. <c>decimal</c>, <c>bool</c>, <c>DateTimeOffset</c> and
/// <c>DateOnly</c> are all stored as <c>TEXT</c>/<c>INTEGER</c> on SQLite by mappings only EF knows, so
/// the same argument applies to every type, not only to <see cref="Guid"/>.
/// </remarks>
internal sealed class PredicateParameterBinder
{
    private readonly IRelationalTypeMappingSource _mappings;
    private readonly DbConnection _connection;

    internal PredicateParameterBinder(AlvoDataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _mappings = context.GetService<IRelationalTypeMappingSource>();
        _connection = context.Database.GetDbConnection();
    }

    internal DbParameter[] Bind(params IReadOnlyDictionary<string, object?>[] bags)
    {
        ArgumentNullException.ThrowIfNull(bags);
        using var command = _connection.CreateCommand();
        return [.. bags.SelectMany(bag => bag).Select(pair => Bind(command, pair.Key, pair.Value))];
    }

    internal DbParameter Bind(string name, object? value)
    {
        using var command = _connection.CreateCommand();
        return Bind(command, name, value);
    }

    private DbParameter Bind(DbCommand command, string name, object? value)
    {
        var mapping = value is null ? null : _mappings.FindMapping(value.GetType());
        if (mapping is not null)
        {
            return mapping.CreateParameter(command, "@" + name, value, nullable: true);
        }

        return Untyped(command, name, value);
    }

    /// <summary>
    /// The two cases with no CLR type to map: a <see langword="null"/> value, and a value whose type the
    /// provider has no mapping for. The second is a bug in whoever produced it, not something to guess
    /// at — a value that reaches ADO.NET with a provider-inferred type is exactly the silent
    /// misrepresentation this class exists to prevent.
    /// </summary>
    private static DbParameter Untyped(DbCommand command, string name, object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(
                $"No relational type mapping exists for '{value.GetType()}', so parameter '{name}' cannot be bound safely.");
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = DBNull.Value;
        return parameter;
    }
}
```

Creating the parameters from a `using`-scoped command is deliberate and is what the spike did: both
`SqliteParameter` and `NpgsqlParameter` are standalone objects once created, so disposing the factory
command does not invalidate them, and the binder never needs an open connection.

- [ ] **Step 5: Write the seeding seam the tests need**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AlvoDataSeed.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Inserts rows <b>bypassing policy entirely</b>, through the property-bag change tracker so every value
/// is stored in exactly the representation EF's own type mapping produces. Exists for the inherited
/// adversarial suite, whose fixtures deliberately seed rows a policy-respecting write could never
/// produce — two owners in one call, entities that declare no <c>create</c> rule at all.
/// </summary>
/// <remarks>
/// <see langword="internal"/>, and visible only to this package's own tests: it is the one code path here
/// that writes without consulting <c>IPolicyEngine</c>, so it must not be reachable from a host. Seeding
/// through hand-rolled ADO.NET instead is what produced the de-risking spike's first false negative —
/// a hand-formatted <c>Guid</c> that no query could then match.
/// </remarks>
internal static class AlvoDataSeed
{
    internal static async Task SeedAsync(
        AlvoDataContextFactory contexts,
        IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(rows);

        using var context = contexts.Create();
        foreach (var (entity, records) in rows)
        {
            foreach (var record in records)
            {
                context.Rows(entity).Add(new Dictionary<string, object>(
                    record.Values.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!),
                    StringComparer.Ordinal));
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
```

- [ ] **Step 6: Run ring1 and commit**

Run: `scripts/test-ring1`

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests test/MMLib.Alvo.Data.Sqlite.Tests
git commit -m "feat(data): bind every predicate value through EF's own relational type mapping"
```

---

## Task 5: The read statement — projection, hidden-field NULL casts, and the field guard

The read statement is the whole security mechanism in one string: `SELECT <projection> FROM <table>
WHERE (<USING>) AND (<tenant scope>) [AND …]`. Two subtleties. A `hidden` field cannot simply be left
out of the `SELECT` list — EF refuses a `FromSql` result set missing a mapped property
(`The required column 'secret_note' was not present…`, spike `Q4a`, identical on both engines) — so it
is projected as `CAST(NULL AS <type>) AS <col>` (`Q4e`/`Q4g`). And a filter or sort key is the one
caller-supplied string that reaches SQL as an **identifier**, so it is validated against the entity's
declared fields *and* the caller's mask before any statement is composed, never by relying on the
engine's own unknown-column error, which happens too late and echoes schema internals (`X1`).

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ReadProjection.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/QueryFieldGuard.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ReadStatementComposer.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ReadProjectionTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/QueryFieldGuardTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ReadStatementComposerTests.cs`

**Interfaces:**
- Consumes: `IAlvoSqlDialect` (Task 1), `IFieldSqlRenderer`, `IPredicateRenderer`,
  `PolicyParameterPrefix` (Task 4), `MMLib.Alvo.Rules.PolicyDecision`
  (`Using`, `WithCheck`, `TenantScope`, `HiddenFields`, `ReadOnlyFields`, `IsDenied`, `DenyReason`),
  `MMLib.Alvo.Data.AlvoQuery` / `AlvoFilter` / `AlvoSort`.
- Produces:
  - `internal static class ReadProjection` — `internal static string Compose(EntitySchema entity,
    IReadOnlySet<string> hiddenFields, IAlvoSqlDialect dialect)`.
  - `internal static class QueryFieldGuard` — `internal static void EnsureAvailable(IEnumerable<string>
    fields, EntitySchema? entity, IReadOnlySet<string> hiddenFields)`, throwing
    `AlvoAuthorizationException` with one fixed message; `internal static void
    EnsureDeclared(IReadOnlyDictionary<string, object?> values, EntitySchema? entity)`.
  - `internal sealed record ReadStatement(string Sql, IReadOnlyDictionary<string, object?> Parameters)`.
  - `internal sealed class ReadStatementComposer` — constructor
    `ReadStatementComposer(IPredicateRenderer predicates, IFieldSqlRenderer fields, IAlvoSqlDialect dialect)`;
    `internal ReadStatement Compose(EntitySchema entity, PolicyDecision decision, AlvoContext context,
    ReadStatementOptions options)`; and
    `internal sealed record ReadStatementOptions { AlvoFilter? Filter; Guid? RowId; KeysetAnchor? Anchor;
    IReadOnlyList<AlvoSort> Sort; PreImageMutation? LockFor; }`.

- [ ] **Step 1: Write the failing projection test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ReadProjectionTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class ReadProjectionTests
{
    private static readonly EntitySchema _entity = new()
    {
        Name = "accounts",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "title", Type = FieldType.String },
            new FieldSchema { Name = "secret", Type = FieldType.String },
            new FieldSchema { Name = "balance", Type = FieldType.Decimal },
        ],
    };

    private static readonly IAlvoSqlDialect _dialect = new TestSqlDialect();

    [Fact]
    public void Every_mapped_field_appears_in_the_select_list()
        => ReadProjection.Compose(_entity, Hidden(), _dialect)
            .ShouldBe("\"id\", \"title\", \"secret\", \"balance\"");

    /// <summary>
    /// EF refuses a <c>FromSql</c> result set that is missing a mapped property, so a masked field is
    /// still named — as a typed SQL <c>NULL</c>, which is what keeps its stored value inside the table.
    /// </summary>
    [Fact]
    public void A_hidden_field_is_projected_as_a_typed_null_under_its_own_alias()
        => ReadProjection.Compose(_entity, Hidden("secret"), _dialect)
            .ShouldBe("\"id\", \"title\", CAST(NULL AS test_text) AS \"secret\", \"balance\"");

    [Fact]
    public void Several_hidden_fields_are_all_masked_and_field_order_is_preserved()
        => ReadProjection.Compose(_entity, Hidden("secret", "balance"), _dialect)
            .ShouldBe("\"id\", \"title\", CAST(NULL AS test_text) AS \"secret\", CAST(NULL AS test_decimal) AS \"balance\"");

    private static IReadOnlySet<string> Hidden(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
}
```

> **AMENDMENT (slice 1, I1/I2).** `RenderNullProjection` now takes `string storeType`, so
> `TestSqlDialect` becomes `RenderNullProjection(string storeType) => $"CAST(NULL AS {storeType})"` and
> `ReadProjection.Compose` must be handed the masked column's EF-resolved store type
> (`IProperty.GetColumnType()` off the read model's entity type) instead of passing the `FieldSchema`
> through. How that is threaded — an extra `Func<string, string>` argument, an `IEntityType` parameter, or
> resolving inside the composer — is this task's call; the constraint is only that the type comes from EF,
> never from a `FieldType` switch. Adjust the expected strings in the tests above to whatever store types
> the test dialect is handed.

Add a shared `TestSqlDialect` to the same test project
(`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/TestSqlDialect.cs`) so the composer's tests do not need
a real engine:

```csharp
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

internal sealed class TestSqlDialect : IAlvoSqlDialect
{
    public string RowLockClause(PreImageMutation mutation) =>
        mutation == PreImageMutation.Delete ? "FOR TEST DELETE" : "FOR TEST";

    public string RenderTable(EntitySchema entity) => AlvoSqlIdentifier.Quote(entity.Name);

    public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

    public string RenderNullProjection(FieldSchema field) => field.Type switch
    {
        FieldType.Decimal => "CAST(NULL AS test_decimal)",
        _ => "CAST(NULL AS test_text)",
    };
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests` → FAIL.

- [ ] **Step 2: Implement the projection**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ReadProjection.cs`:

```csharp
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The <c>SELECT</c> list a policy-filtered read uses: every field the entity declares, in declaration
/// order, with each masked field replaced by a typed SQL <c>NULL</c> under the field's own alias.
/// </summary>
/// <remarks>
/// Omitting a masked field from the list is not an option — EF requires a <c>FromSql</c> result set to
/// contain every mapped property and fails with "The required column '…' was not present in the results
/// of a 'FromSql' operation", identically on both engines. Projecting the <c>NULL</c> instead means the
/// masked column is never read from the page at all, and the key is dropped again when the
/// <c>AlvoRecord</c> is assembled, so the value never leaves the table by either route.
/// </remarks>
internal static class ReadProjection
{
    internal static string Compose(EntitySchema entity, IReadOnlySet<string> hiddenFields, IAlvoSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(dialect);

        return string.Join(", ", entity.Fields.Select(field => Project(field, hiddenFields, dialect)));
    }

    private static string Project(FieldSchema field, IReadOnlySet<string> hiddenFields, IAlvoSqlDialect dialect) =>
        hiddenFields.Contains(field.Name)
            ? $"{dialect.RenderNullProjection(field)} AS {dialect.RenderColumn(field.Name)}"
            : dialect.RenderColumn(field.Name);
}
```

- [ ] **Step 3: Write the failing field-guard test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/QueryFieldGuardTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class QueryFieldGuardTests
{
    private static readonly EntitySchema _entity = new()
    {
        Name = "accounts",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "title", Type = FieldType.String },
            new FieldSchema { Name = "secret", Type = FieldType.String },
        ],
    };

    [Fact]
    public void A_declared_visible_field_is_allowed()
        => QueryFieldGuard.EnsureAvailable(["title"], _entity, Hidden());

    [Fact]
    public void A_hidden_field_is_refused()
        => Should.Throw<AlvoAuthorizationException>(() => QueryFieldGuard.EnsureAvailable(["secret"], _entity, Hidden("secret")));

    [Fact]
    public void An_undeclared_field_is_refused_with_the_identical_message()
    {
        var undeclared = Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureAvailable(["title\"; DROP TABLE items; --"], _entity, Hidden()));
        var hidden = Should.Throw<AlvoAuthorizationException>(
            () => QueryFieldGuard.EnsureAvailable(["secret"], _entity, Hidden("secret")));

        undeclared.Message.ShouldBe(hidden.Message);
        undeclared.Message.ShouldNotContain("DROP TABLE");
    }

    [Fact]
    public void An_unknown_entity_refuses_every_field_rather_than_waving_them_through()
        => Should.Throw<AlvoAuthorizationException>(() => QueryFieldGuard.EnsureAvailable(["title"], entity: null, Hidden()));

    [Fact]
    public void A_payload_naming_an_undeclared_field_is_refused()
        => Should.Throw<AlvoAuthorizationException>(() => QueryFieldGuard.EnsureDeclared(
            new Dictionary<string, object?> { ["nope"] = 1 }, _entity));

    [Fact]
    public void A_payload_may_name_a_hidden_field_because_writing_one_is_not_reading_it()
        => QueryFieldGuard.EnsureDeclared(new Dictionary<string, object?> { ["secret"] = "x" }, _entity);

    private static IReadOnlySet<string> Hidden(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
}
```

- [ ] **Step 4: Implement the guard**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/QueryFieldGuard.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The two field-name checks every EF-backed Alvo driver runs before composing a statement: a filter or
/// sort key must name a field the caller can actually read, and a write payload must name a field the
/// entity declares. Shared here rather than copied per driver — a per-driver copy of a security check is
/// how two engines come to disagree about what is refused.
/// </summary>
/// <remarks>
/// A field name is the one caller-supplied string that reaches SQL as an <b>identifier</b>, and SQL has
/// no bind-parameter form of a column name, so validating it against the schema here is what makes that
/// interpolation safe; the engine's own unknown-column error arrives after the statement is composed and
/// echoes schema internals. Both refusals carry the <em>same</em> message and name neither the field nor
/// the reason: a caller must not be able to tell "exists but hidden from you" from "does not exist", and
/// the name itself is attacker-controlled text this layer will not echo into a log.
/// </remarks>
internal static class QueryFieldGuard
{
    private const string UnavailableQueryFieldMessage = "The query references a field that is not available to this caller.";

    private const string UndeclaredPayloadFieldMessage = "The payload names a field that is not writable on this entity.";

    internal static void EnsureAvailable(IEnumerable<string> fields, EntitySchema? entity, IReadOnlySet<string> hiddenFields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(hiddenFields);

        var declared = DeclaredFields(entity);
        foreach (var field in fields)
        {
            if (hiddenFields.Contains(field) || !declared.Contains(field))
            {
                throw new AlvoAuthorizationException(UnavailableQueryFieldMessage);
            }
        }
    }

    internal static void EnsureDeclared(IReadOnlyDictionary<string, object?> values, EntitySchema? entity)
    {
        ArgumentNullException.ThrowIfNull(values);

        var declared = DeclaredFields(entity);
        foreach (var field in values.Keys)
        {
            if (!declared.Contains(field))
            {
                throw new AlvoAuthorizationException(UndeclaredPayloadFieldMessage);
            }
        }
    }

    /// <summary>
    /// An entity the applied schema does not know declares nothing, so every name fails closed. A
    /// mismatch between the policy catalog and the applied schema must not be the one path on which an
    /// unvalidated name reaches storage.
    /// </summary>
    private static HashSet<string> DeclaredFields(EntitySchema? entity) =>
        entity is null ? [] : [.. entity.Fields.Select(field => field.Name)];
}
```

- [ ] **Step 5: Write the failing statement-composer test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ReadStatementComposerTests.cs`. The facts that
matter are: the policy predicate and the tenant scope are both there, `AND`-joined, each parenthesised;
the three prefixes are disjoint in one statement; the row id and the lock hint appear only when asked
for; and no value from the predicate appears as text.

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class ReadStatementComposerTests
{
    [Fact]
    public void The_policy_predicate_and_the_tenant_scope_are_both_in_the_where_clause()
    {
        var statement = Compose(ListDecision(), new ReadStatementComposer.ReadStatementOptions());

        statement.Sql.ShouldStartWith("SELECT ");
        statement.Sql.ShouldContain(" FROM \"vehicle\" WHERE (");
        statement.Sql.ShouldContain("@alvo_u0");
        statement.Sql.ShouldContain("@alvo_t0");
        statement.Sql.ShouldContain(") AND (");
    }

    [Fact]
    public void The_three_predicate_parameter_families_never_share_a_name()
    {
        var statement = Compose(ListDecision(), new ReadStatementComposer.ReadStatementOptions());

        statement.Parameters.Keys.ShouldBeUnique();
        statement.Parameters.Keys.ShouldAllBe(name => name.StartsWith("alvo_", StringComparison.Ordinal));
    }

    [Fact]
    public void No_bound_value_appears_in_the_statement_text()
    {
        var statement = Compose(ListDecision(), new ReadStatementComposer.ReadStatementOptions());

        foreach (var value in statement.Parameters.Values.Where(value => value is not null))
        {
            statement.Sql.ShouldNotContain(value!.ToString()!, Case.Insensitive);
        }
    }

    [Fact]
    public void A_row_id_read_binds_the_id_and_can_take_the_dialects_row_lock()
    {
        var id = Guid.NewGuid();
        var statement = Compose(ListDecision(), new ReadStatementComposer.ReadStatementOptions
        {
            RowId = id,
            LockFor = PreImageMutation.Update,
        });

        statement.Sql.ShouldContain("\"id\" = @alvo_id");
        statement.Sql.ShouldEndWith(" FOR TEST");
        statement.Parameters[PolicyParameterPrefix.RowId].ShouldBe(id);
    }

    [Fact]
    public void A_list_read_takes_no_row_lock()
        => Compose(ListDecision(), new ReadStatementComposer.ReadStatementOptions()).Sql.ShouldNotContain("FOR TEST");

    private static ReadStatement Compose(PolicyDecision decision, ReadStatementComposer.ReadStatementOptions options)
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        var composer = new ReadStatementComposer(
            services.GetRequiredService<IPredicateRenderer>(), new TestFieldSqlRenderer(), new TestSqlDialect());

        return composer.Compose(
            AlvoDataSqlSnapshotTests.SnapshotEntity, decision, AlvoDataSqlSnapshotTests.SnapshotCaller, options);
    }

    private static PolicyDecision ListDecision() => /* built in Step 6 */ throw new NotImplementedException();
}
```

`PolicyDecision.Allow` is `internal` to `MMLib.Alvo`, so this test project cannot construct one. Build
the decision the way every other consumer does — resolve a real `IPolicyEngine` against a primed
catalog:

```csharp
    private static PolicyDecision ListDecision()
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        var (descriptor, schema) = SnapshotFixture.VehicleWith(list: "owner_id == @user.id");
        services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(
            descriptor.Name, PolicyCatalog.Build(descriptor, schema, services.GetRequiredService<ICelCompiler>()));

        return services.GetRequiredService<IPolicyEngine>()
            .Resolve("vehicle", DataOperation.List, AlvoDataSqlSnapshotTests.SnapshotCaller);
    }
```

Add `SnapshotFixture` to this test project: a `VehicleWith(string? list = null, string? get = null,
string? create = null, string? update = null, string? delete = null, params string[] hiddenFields)`
helper returning a `(AlvoDescriptor, SchemaModel)` pair via `DescriptorToSchemaMapper.Map`, shaped like
`AlvoDataSqlSnapshotTests.SnapshotEntity`. Tasks 6, 7, 8 and 9 reuse it, so it is worth naming here.

- [ ] **Step 6: Implement the composer**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ReadStatementComposer.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>One composed read statement and the values its named parameters bind.</summary>
/// <param name="Sql">The statement text.</param>
/// <param name="Parameters">The values <paramref name="Sql"/> references by name.</param>
internal sealed record ReadStatement(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Composes the one statement every Alvo read goes through: the resolved <c>USING</c> predicate and the
/// synthesized tenant scope, <c>AND</c>-joined in the <c>WHERE</c> clause of a single <c>SELECT</c>, plus
/// whatever the operation adds — the caller's filter, a row id, a keyset cursor, a row lock.
/// </summary>
/// <remarks>
/// <para>
/// Every term is composed here, in one place, so there is exactly one answer to "is the policy predicate
/// in the <c>WHERE</c> clause or applied afterwards" and a snapshot of this string is the proof. The
/// caller's own terms are only ever <c>AND</c>-ed onto a fully parenthesised policy predicate, so they
/// can only narrow the result; nothing a caller supplies can reach the same nesting level as the policy
/// term, let alone be <c>OR</c>-ed beside it.
/// </para>
/// <para>
/// Each of the three predicates a <see cref="PolicyDecision"/> carries is rendered with its own
/// parameter prefix. Renders number their parameters from zero independently, so two default-prefixed
/// predicates in one command would bind two values to one name — and whichever won would silently change
/// what the other predicate means.
/// </para>
/// </remarks>
internal sealed class ReadStatementComposer
{
    private readonly IPredicateRenderer _predicates;
    private readonly IFieldSqlRenderer _fields;
    private readonly IAlvoSqlDialect _dialect;

    internal ReadStatementComposer(IPredicateRenderer predicates, IFieldSqlRenderer fields, IAlvoSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(dialect);
        _predicates = predicates;
        _fields = fields;
        _dialect = dialect;
    }

    /// <summary>What one operation adds to the policy-filtered read.</summary>
    internal sealed record ReadStatementOptions
    {
        /// <summary>The caller's filter, or <see langword="null"/> for none.</summary>
        internal AlvoFilter? Filter { get; init; }

        /// <summary>A single row's id, for a get/pre-image read.</summary>
        internal Guid? RowId { get; init; }

        /// <summary>The keyset cursor anchor, for a page after the first.</summary>
        internal KeysetAnchor? Anchor { get; init; }

        /// <summary>
        /// The mutation this read's row is a pre-image for, or <see langword="null"/> for a read that takes
        /// no lock. It selects the lock <em>mode</em>, not merely whether to lock — an update's pre-image
        /// takes the weaker no-key lock and a delete's takes the full one.
        /// </summary>
        internal PreImageMutation? LockFor { get; init; }
    }

    internal ReadStatement Compose(
        EntitySchema entity, PolicyDecision decision, AlvoContext context, ReadStatementOptions options)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var terms = new List<string>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        AddPredicate(terms, parameters, decision.Using, context, PolicyParameterPrefix.Using);
        AddPredicate(terms, parameters, decision.TenantScope, context, PolicyParameterPrefix.TenantScope);
        AddRowId(terms, parameters, entity, options.RowId);
        AddFilter(terms, parameters, entity, options.Filter);
        AddAnchor(terms, parameters, entity, options.Anchor);

        var sql = new StringBuilder("SELECT ")
            .Append(ReadProjection.Compose(entity, decision.HiddenFields, _dialect))
            .Append(" FROM ")
            .Append(_dialect.RenderTable(entity))
            .Append(" WHERE ")
            .Append(string.Join(" AND ", terms.Select(term => $"({term})")))
            .Append(LockClause(options))
            .ToString();

        return new ReadStatement(sql, parameters);
    }

    // RowLockClause carries no separator of its own (see IAlvoSqlDialect.RowLockClause's remarks), so the
    // separating space is inserted here and only when there is a clause to separate.
    private string LockClause(ReadStatementOptions options) =>
        options.LockFor is { } operation && _dialect.RowLockClause(operation) is { Length: > 0 } clause
            ? " " + clause
            : string.Empty;

    /// <summary>
    /// A <see langword="null"/> predicate contributes the dialect's constant-true predicate rather than
    /// nothing: <c>create</c> carries no <c>USING</c> and a global entity carries no tenant scope, and a
    /// <c>WHERE</c> clause with no term at all is a syntax error. A constant true is safe here precisely
    /// because <see cref="IPolicyEngine"/> already denied every operation that has no predicate for a
    /// reason.
    /// </summary>
    private void AddPredicate(
        List<string> terms, Dictionary<string, object?> parameters,
        CompiledExpression? expression, AlvoContext context, string prefix)
    {
        if (expression is null)
        {
            terms.Add(_fields.RenderBooleanPredicate(true));
            return;
        }

        var predicate = _predicates.Render(expression, context, _fields, prefix);
        terms.Add(predicate.Sql);
        foreach (var (name, value) in predicate.Parameters)
        {
            parameters[name] = value;
        }
    }

    private void AddRowId(List<string> terms, Dictionary<string, object?> parameters, EntitySchema entity, Guid? rowId)
    {
        if (rowId is not { } id)
        {
            return;
        }

        terms.Add($"{_fields.RenderField(entity, AlvoDataContext.IdColumn)} = {_fields.RenderParameter(PolicyParameterPrefix.RowId)}");
        parameters[PolicyParameterPrefix.RowId] = id;
    }

    private void AddFilter(List<string> terms, Dictionary<string, object?> parameters, EntitySchema entity, AlvoFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        var rendered = FilterSqlRenderer.Render(filter, entity, _fields, PolicyParameterPrefix.Filter);
        terms.Add(rendered.Sql);
        foreach (var (name, value) in rendered.Parameters)
        {
            parameters[name] = value;
        }
    }

    private void AddAnchor(List<string> terms, Dictionary<string, object?> parameters, EntitySchema entity, KeysetAnchor? anchor)
    {
        if (anchor is null)
        {
            return;
        }

        var rendered = KeysetSqlRenderer.Render(anchor, entity, _fields, PolicyParameterPrefix.Keyset);
        terms.Add(rendered.Sql);
        foreach (var (name, value) in rendered.Parameters)
        {
            parameters[name] = value;
        }
    }
}
```

`FilterSqlRenderer`, `KeysetSqlRenderer` and `KeysetAnchor` land in Task 6; until then, stub the two
`Add*` calls out of `Compose` and the statement tests still pass on their own terms. Do not merge Task 6
into this one — a reviewer should be able to reject a filter-rendering bug without re-reviewing the
projection.

- [ ] **Step 7: Run ring1 and commit**

Run: `scripts/test-ring1`

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests
git commit -m "feat(data): compose the policy-filtered read statement with masked-field NULL casts"
```

---

## Task 6: The caller filter renderer — every operator, fuzzed and injection-tested

The caller's filter is rendered into SQL here rather than composed as a LINQ tree, for the reason in
*Deviations* 2: EF translates C# `==`/`!=` with **C# null semantics**, adding `OR x IS NULL`
compensation, which would make `neq` match a `NULL` field and break `AlvoFilterOperator`'s documented
three-valued contract outright. Rendering it means the semantics are SQL's own, by construction — and it
gives `IFieldSqlRenderer.RenderCaseInsensitiveLike` the consumer PR1 shipped it without.

This task carries two of #20's DoD items: *"property-based testy dokazujú, že preklad … nikdy
neinterpoluje užívateľský vstup"* and *"injection cez každý operátor … fuzzing filtra bez pádu"*.

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/FilterSqlRenderer.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/KeysetSqlRenderer.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/ReadStatementComposer.cs` (un-stub `AddFilter`/`AddAnchor`)
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererPropertyTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/KeysetSqlRendererTests.cs`
- Modify: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/MMLib.Alvo.Data.EntityFrameworkCore.Tests.csproj` (add `CsCheck`)

**Interfaces:**
- Consumes: `AlvoFilter` / `AlvoComparison` / `AlvoAnd` / `AlvoOr` / `AlvoNot` /
  `AlvoFilterOperator` (`Eq Neq Gt Gte Lt Lte Like ILike In Is`), `AlvoFilter.MaxDepth`,
  `AlvoFilter.EnsureWithinDepthLimit`, `AlvoFilter.ReferencedFields`; `IFieldSqlRenderer`;
  `PolicyParameterPrefix`.
- Produces:
  - `internal sealed record RenderedSql(string Sql, IReadOnlyDictionary<string, object?> Parameters)`.
  - `internal static class FilterSqlRenderer` — `internal static RenderedSql Render(AlvoFilter filter,
    EntitySchema entity, IFieldSqlRenderer fields, string parameterPrefix)`.
  - `internal sealed record KeysetAnchor(IReadOnlyList<AlvoSort> Sort, IReadOnlyList<object?> Values, Guid RowId)`.
  - `internal static class KeysetSqlRenderer` — `internal static RenderedSql Render(KeysetAnchor anchor,
    EntitySchema entity, IFieldSqlRenderer fields, string parameterPrefix)`.

- [ ] **Step 1: Write the failing per-operator test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Testing;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class FilterSqlRendererTests
{
    [Theory]
    [InlineData(AlvoFilterOperator.Eq, "\"status\" = @alvo_f0")]
    [InlineData(AlvoFilterOperator.Neq, "\"status\" <> @alvo_f0")]
    [InlineData(AlvoFilterOperator.Gt, "\"status\" > @alvo_f0")]
    [InlineData(AlvoFilterOperator.Gte, "\"status\" >= @alvo_f0")]
    [InlineData(AlvoFilterOperator.Lt, "\"status\" < @alvo_f0")]
    [InlineData(AlvoFilterOperator.Lte, "\"status\" <= @alvo_f0")]
    [InlineData(AlvoFilterOperator.Like, "\"status\" LIKE @alvo_f0")]
    public void Each_scalar_operator_renders_its_own_sql_operator(AlvoFilterOperator op, string expected)
        => Render(new AlvoComparison("status", op, "open")).Sql.ShouldBe(expected);

    [Fact]
    public void Case_insensitive_like_goes_through_the_drivers_own_seam()
        => Render(new AlvoComparison("status", AlvoFilterOperator.ILike, "op%")).Sql
            .ShouldBe("UPPER(\"status\") LIKE UPPER(@alvo_f0)");

    [Fact]
    public void Membership_renders_one_parameter_per_candidate()
    {
        var rendered = Render(new AlvoComparison("status", AlvoFilterOperator.In, new object?[] { "open", "closed" }));

        rendered.Sql.ShouldBe("\"status\" IN (@alvo_f0, @alvo_f1)");
        rendered.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void An_identity_test_renders_a_definite_two_valued_predicate_with_no_parameter()
    {
        Render(new AlvoComparison("status", AlvoFilterOperator.Is, null)).Sql.ShouldBe("\"status\" IS NULL");
        Render(new AlvoComparison("is_public", AlvoFilterOperator.Is, true)).Sql.ShouldBe("\"is_public\" IS TRUE");
        Render(new AlvoComparison("is_public", AlvoFilterOperator.Is, false)).Sql.ShouldBe("\"is_public\" IS FALSE");
        Render(new AlvoComparison("status", AlvoFilterOperator.Is, null)).Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void An_identity_test_against_anything_else_is_refused()
        => Should.Throw<AlvoAuthorizationException>(
            () => Render(new AlvoComparison("status", AlvoFilterOperator.Is, "open")));

    [Fact]
    public void Boolean_connectives_are_parenthesised_and_never_flattened_into_the_policy_term()
    {
        var tree = new AlvoNot(new AlvoAnd([
            new AlvoComparison("status", AlvoFilterOperator.Eq, "open"),
            new AlvoOr([
                new AlvoComparison("mileage", AlvoFilterOperator.Gt, 10L),
                new AlvoComparison("mileage", AlvoFilterOperator.Is, null),
            ]),
        ]));

        Render(tree).Sql.ShouldBe(
            "(NOT ((\"status\" = @alvo_f0) AND ((\"mileage\" > @alvo_f1) OR (\"mileage\" IS NULL))))");
    }

    [Fact]
    public void An_empty_conjunction_matches_every_row_and_an_empty_disjunction_matches_none()
    {
        Render(new AlvoAnd([])).Sql.ShouldBe("1");
        Render(new AlvoOr([])).Sql.ShouldBe("0");
    }

    [Fact]
    public void A_tree_deeper_than_the_cap_is_refused_rather_than_walked()
    {
        AlvoFilter node = new AlvoComparison("status", AlvoFilterOperator.Eq, "open");
        for (var level = 1; level <= AlvoFilter.MaxDepth; level++)
        {
            node = new AlvoNot(node);
        }

        Should.Throw<ArgumentException>(() => Render(node));
    }

    [Fact]
    public void An_undeclared_field_never_reaches_the_sql_text()
        => Should.Throw<AlvoAuthorizationException>(
            () => Render(new AlvoComparison("nope\"; DROP TABLE items; --", AlvoFilterOperator.Eq, "x")));

    private static RenderedSql Render(AlvoFilter filter) => FilterSqlRenderer.Render(
        filter, AlvoDataSqlSnapshotTests.SnapshotEntity, new TestFieldSqlRenderer(), PolicyParameterPrefix.Filter);
}
```

`TestFieldSqlRenderer` (shipped by PR1 in `MMLib.Alvo.Testing`) renders `"quoted"` identifiers, `@name`
parameters, `TRUE`/`FALSE` literals and `UPPER(a) LIKE UPPER(b)` — check its actual boolean literals
before fixing the two empty-connective expectations above; if it emits `TRUE`/`FALSE` rather than
`1`/`0`, use those. The renderer must take the literals from `IFieldSqlRenderer`, never hard-code them.

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests` → FAIL.

- [ ] **Step 2: Implement the filter renderer**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/FilterSqlRenderer.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using System.Collections;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>A rendered SQL fragment and the values its named parameters bind.</summary>
/// <param name="Sql">The fragment text.</param>
/// <param name="Parameters">The values <paramref name="Sql"/> references by name.</param>
internal sealed record RenderedSql(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Renders a caller's <see cref="AlvoFilter"/> tree to SQL: every field through the driver's
/// <see cref="IFieldSqlRenderer"/>, every value as a named bind parameter, and the operator taken from a
/// closed allow-list — never assembled from caller text.
/// </summary>
/// <remarks>
/// <para>
/// Rendered rather than composed as a LINQ tree on purpose. EF translates C# equality with C# null
/// semantics, adding an <c>OR x IS NULL</c> compensation term, which would make <c>neq</c> match a
/// <see langword="null"/> field — the opposite of what <see cref="AlvoFilterOperator"/> documents ("a
/// <see langword="null"/> column never satisfies <c>neq</c> either"). Rendering the fragment makes the
/// semantics SQL's own three-valued logic by construction, on every engine, with nothing to compensate
/// for.
/// </para>
/// <para>
/// A caller-supplied pattern's <c>%</c> and <c>_</c> are meaningful and are <b>not</b> escaped — that is
/// PostgREST's own <c>like</c>/<c>ilike</c> semantics, and therefore the semantics an agent expects. It
/// is not an injection surface: the pattern is always a bind parameter, never text in the statement.
/// </para>
/// </remarks>
internal static class FilterSqlRenderer
{
    internal static RenderedSql Render(
        AlvoFilter filter, EntitySchema entity, IFieldSqlRenderer fields, string parameterPrefix)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);
        AlvoFilter.EnsureWithinDepthLimit(filter);

        var bag = new ParameterBag(parameterPrefix);
        var sql = Node(filter, entity, fields, bag);
        return new RenderedSql(sql, bag.Values);
    }

    private static string Node(AlvoFilter node, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag) => node switch
    {
        AlvoComparison comparison => Comparison(comparison, entity, fields, bag),
        AlvoAnd and => Connective(and.Filters, entity, fields, bag, "AND", fields.RenderBooleanPredicate(true)),
        AlvoOr or => Connective(or.Filters, entity, fields, bag, "OR", fields.RenderBooleanPredicate(false)),
        AlvoNot not => $"(NOT {Node(not.Filter, entity, fields, bag)})",
        _ => throw new AlvoAuthorizationException(UnsupportedFilterMessage),
    };

    /// <summary>
    /// An empty conjunction is the identity of <c>AND</c> (match everything) and an empty disjunction the
    /// identity of <c>OR</c> (match nothing) — spelled out because a <c>WHERE</c> clause has no empty
    /// form, and because guessing the other way round for <c>OR</c> would silently widen a filter.
    /// </summary>
    private static string Connective(
        IReadOnlyList<AlvoFilter> children, EntitySchema entity, IFieldSqlRenderer fields,
        ParameterBag bag, string keyword, string identity)
        => children.Count == 0
            ? identity
            : "(" + string.Join($" {keyword} ", children.Select(child => Node(child, entity, fields, bag))) + ")";

    private static string Comparison(
        AlvoComparison comparison, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var field = fields.RenderField(entity, Declared(comparison.Field, entity));

        return comparison.Operator switch
        {
            AlvoFilterOperator.Eq => $"{field} = {bag.Add(fields, comparison.Value)}",
            AlvoFilterOperator.Neq => $"{field} <> {bag.Add(fields, comparison.Value)}",
            AlvoFilterOperator.Gt => $"{field} > {bag.Add(fields, comparison.Value)}",
            AlvoFilterOperator.Gte => $"{field} >= {bag.Add(fields, comparison.Value)}",
            AlvoFilterOperator.Lt => $"{field} < {bag.Add(fields, comparison.Value)}",
            AlvoFilterOperator.Lte => $"{field} <= {bag.Add(fields, comparison.Value)}",
            AlvoFilterOperator.Like => $"{field} LIKE {bag.Add(fields, comparison.Value)}",
            AlvoFilterOperator.ILike => fields.RenderCaseInsensitiveLike(field, bag.Add(fields, comparison.Value)),
            AlvoFilterOperator.In => Membership(field, comparison.Value, fields, bag),
            AlvoFilterOperator.Is => Identity(field, comparison.Value),
            _ => throw new AlvoAuthorizationException(UnsupportedFilterMessage),
        };
    }

    private static string Membership(string field, object? value, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var candidates = value as IEnumerable and not string
            ?? throw new AlvoAuthorizationException(UnsupportedFilterMessage);
        var names = candidates.Cast<object?>().Select(candidate => bag.Add(fields, candidate)).ToList();
        return names.Count == 0
            ? fields.RenderBooleanPredicate(false)
            : $"{field} IN ({string.Join(", ", names)})";
    }

    /// <summary>
    /// The one operator that is definitely true or false over a <see langword="null"/> field, so it takes
    /// no parameter and needs no collapse. Only the three values SQL's own <c>IS</c> accepts are
    /// permitted; anything else is refused rather than coerced.
    /// </summary>
    private static string Identity(string field, object? value) => value switch
    {
        null => $"{field} IS NULL",
        true => $"{field} IS TRUE",
        false => $"{field} IS FALSE",
        _ => throw new AlvoAuthorizationException(UnsupportedFilterMessage),
    };

    /// <summary>
    /// Resolves a caller-supplied field name against the entity's declared fields and returns the
    /// <b>declared</b> name, so the string that reaches the renderer is one the schema owns rather than
    /// the caller's own bytes. <see cref="QueryFieldGuard"/> has already refused an undeclared or masked
    /// name before a statement is composed; this is the second, local check that makes the renderer safe
    /// on its own terms, and it deliberately raises the same message.
    /// </summary>
    private static string Declared(string field, EntitySchema entity) =>
        entity.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal))?.Name
        ?? throw new AlvoAuthorizationException(UnavailableFieldMessage);

    private const string UnavailableFieldMessage = "The query references a field that is not available to this caller.";

    private const string UnsupportedFilterMessage = "The query uses a filter this provider cannot render.";

    private sealed class ParameterBag(string prefix)
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        internal IReadOnlyDictionary<string, object?> Values => _values;

        internal string Add(IFieldSqlRenderer fields, object? value)
        {
            var name = prefix + _values.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _values[name] = value;
            return fields.RenderParameter(name);
        }
    }
}
```

`Declared` returning the schema's own string rather than the caller's is the detail that makes the
"never interpolates user input" property in Step 3 provable rather than merely likely.

- [ ] **Step 3: Write the property and injection tests**

Add `<PackageReference Include="CsCheck" />` to
`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/MMLib.Alvo.Data.EntityFrameworkCore.Tests.csproj`, then
create `FilterSqlRendererPropertyTests.cs`:

```csharp
using CsCheck;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Testing;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// §2.4's *"property-based testy dokazujú, že preklad … nikdy neinterpoluje užívateľský vstup"* and
/// §2.1's *"injection cez každý operátor … fuzzing filtra bez pádu"*, over the caller-filter half of the
/// translation. The CEL half is proved by <c>NoInterpolationPropertyTests</c> in the core.
/// </summary>
public class FilterSqlRendererPropertyTests
{
    private static readonly AlvoFilterOperator[] _everyOperator = Enum.GetValues<AlvoFilterOperator>();

    private static readonly Gen<string> _hostileText =
        Gen.Char["abcXYZ01_ '\"%;-()\\é中"].Array[1, 24].Select(characters => new string(characters));

    [Fact]
    public void No_filter_value_ever_appears_in_the_rendered_sql()
    {
        Gen.Select(_hostileText, Gen.OneOfConst(_everyOperator)).Sample(
            (value, op) =>
            {
                if (!TryRender(new AlvoComparison("status", op, value), out var rendered))
                {
                    return true;
                }

                return !rendered!.Sql.Contains(value, StringComparison.Ordinal)
                    && rendered.Parameters.Values.Contains(value);
            },
            iter: 10_000);
    }

    [Fact]
    public void An_injection_attempt_through_every_operator_stays_inside_a_parameter()
    {
        const string Payload = "x'; DROP TABLE vehicle; --";

        foreach (var op in _everyOperator)
        {
            if (!TryRender(new AlvoComparison("status", op, Payload), out var rendered))
            {
                continue;
            }

            rendered!.Sql.ShouldNotContain("DROP", Case.Insensitive);
            rendered.Parameters.Values.ShouldContain(Payload);
        }
    }

    [Fact]
    public void An_injection_attempt_through_a_field_name_is_refused_for_every_operator()
    {
        foreach (var op in _everyOperator)
        {
            Should.Throw<AlvoAuthorizationException>(
                () => FilterSqlRenderer.Render(
                    new AlvoComparison("status\"; DROP TABLE vehicle; --", op, "x"),
                    AlvoDataSqlSnapshotTests.SnapshotEntity, new TestFieldSqlRenderer(), PolicyParameterPrefix.Filter));
        }
    }

    /// <summary>
    /// The fuzz arm: random trees of random shape over every operator and a hostile value alphabet must
    /// either render or raise one of the two documented refusals — never crash, never overflow the stack,
    /// and never let a value into the statement text.
    /// </summary>
    [Fact]
    public void A_randomly_generated_filter_tree_either_renders_or_is_refused_but_never_crashes()
    {
        var leaf = Gen.Select(_hostileText, Gen.OneOfConst(_everyOperator),
            (value, op) => (AlvoFilter)new AlvoComparison("status", op, value));
        var tree = Gen.Recursive<AlvoFilter>((depth, self) =>
            depth >= 6
                ? leaf
                : Gen.Frequency(
                    (4, leaf),
                    (2, self.Array[0, 4].Select(children => (AlvoFilter)new AlvoAnd(children))),
                    (2, self.Array[0, 4].Select(children => (AlvoFilter)new AlvoOr(children))),
                    (1, self.Select(child => (AlvoFilter)new AlvoNot(child)))));

        tree.Sample(
            filter =>
            {
                if (!TryRender(filter, out var rendered))
                {
                    return true;
                }

                return rendered!.Parameters.Values
                    .OfType<string>()
                    .All(value => !rendered.Sql.Contains(value, StringComparison.Ordinal));
            },
            iter: 5_000);
    }

    private static bool TryRender(AlvoFilter filter, out RenderedSql? rendered)
    {
        try
        {
            rendered = FilterSqlRenderer.Render(
                filter, AlvoDataSqlSnapshotTests.SnapshotEntity, new TestFieldSqlRenderer(), PolicyParameterPrefix.Filter);
            return true;
        }
        catch (AlvoAuthorizationException)
        {
            rendered = null;
            return false;
        }
        catch (ArgumentException)
        {
            rendered = null;
            return false;
        }
    }
}
```

`TryRender` catching exactly the two documented refusal types is what makes the fuzz arm meaningful: any
other exception escapes and fails the test, which is the *"bez pádu"* (no crash) criterion.

- [ ] **Step 4: Write the failing keyset test, then implement the keyset renderer**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/KeysetSqlRendererTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Testing;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class KeysetSqlRendererTests
{
    private static readonly Guid _anchorId = Guid.NewGuid();

    [Fact]
    public void With_no_sort_key_the_cursor_is_the_primary_key_alone()
        => Render([], []).Sql.ShouldBe("\"id\" > @alvo_k0");

    /// <summary>
    /// Row-value tuple comparison has no portable form, so the nested-OR expansion is what ships: a
    /// strictly greater leading key, or an equal leading key and a greater tail.
    /// </summary>
    [Fact]
    public void One_ascending_sort_key_expands_to_the_nested_or_form()
        => Render([new AlvoSort("plate")], ["ACME-001"]).Sql
            .ShouldBe("(\"plate\" > @alvo_k0 OR (\"plate\" = @alvo_k0 AND \"id\" > @alvo_k1))");

    [Fact]
    public void A_descending_sort_key_reverses_only_the_strict_comparison()
        => Render([new AlvoSort("plate", Descending: true)], ["ACME-001"]).Sql
            .ShouldBe("(\"plate\" < @alvo_k0 OR (\"plate\" = @alvo_k0 AND \"id\" > @alvo_k1))");

    [Fact]
    public void Two_sort_keys_nest_left_to_right()
        => Render([new AlvoSort("status"), new AlvoSort("plate")], ["open", "ACME-001"]).Sql
            .ShouldBe(
                "(\"status\" > @alvo_k0 OR (\"status\" = @alvo_k0 AND "
                + "(\"plate\" > @alvo_k1 OR (\"plate\" = @alvo_k1 AND \"id\" > @alvo_k2))))");

    [Fact]
    public void Every_anchor_value_is_a_bound_parameter()
        => Render([new AlvoSort("plate")], ["ACME-001"]).Parameters.Values.ShouldContain("ACME-001");

    private static RenderedSql Render(IReadOnlyList<AlvoSort> sort, IReadOnlyList<object?> values) =>
        KeysetSqlRenderer.Render(
            new KeysetAnchor(sort, values, _anchorId),
            AlvoDataSqlSnapshotTests.SnapshotEntity, new TestFieldSqlRenderer(), PolicyParameterPrefix.Keyset);
}
```

Then implement `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/KeysetSqlRenderer.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>The row a page continues after: the sort keys in effect, that row's values for them, and its primary key.</summary>
/// <param name="Sort">The sort keys the page is ordered by, outermost first.</param>
/// <param name="Values">That row's value for each key in <paramref name="Sort"/>, in the same order.</param>
/// <param name="RowId">The anchor row's primary key — the tie-breaker that makes the order total.</param>
internal sealed record KeysetAnchor(IReadOnlyList<AlvoSort> Sort, IReadOnlyList<object?> Values, Guid RowId);

/// <summary>
/// Renders a keyset-pagination predicate as the nested-OR expansion of a row-value tuple comparison:
/// <c>(k > @k0 OR (k = @k0 AND …))</c>, ending in the primary key so the order is total and a page can
/// neither skip nor repeat a row.
/// </summary>
/// <remarks>
/// The nested-OR form rather than SQL's <c>(a, b) &gt; (x, y)</c> row constructor, which has no portable
/// LINQ or SQLite equivalent. The tie-breaking <c>id</c> comparison is always ascending: it exists to
/// make the order deterministic, not to be sorted by, and flipping it with the last user key would make
/// two pages of the same query disagree about where the boundary is.
/// </remarks>
internal static class KeysetSqlRenderer
{
    internal static RenderedSql Render(
        KeysetAnchor anchor, EntitySchema entity, IFieldSqlRenderer fields, string parameterPrefix)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sql = Level(0, anchor, entity, fields, parameterPrefix, parameters);
        return new RenderedSql(sql, parameters);
    }

    private static string Level(
        int index, KeysetAnchor anchor, EntitySchema entity, IFieldSqlRenderer fields,
        string prefix, Dictionary<string, object?> parameters)
    {
        if (index == anchor.Sort.Count)
        {
            return $"{fields.RenderField(entity, AlvoDataContext.IdColumn)} > {Bind(anchor.RowId, fields, prefix, parameters)}";
        }

        var key = anchor.Sort[index];
        var column = fields.RenderField(entity, key.Field);
        var parameter = Bind(anchor.Values[index], fields, prefix, parameters);
        var strict = key.Descending ? "<" : ">";
        var tail = Level(index + 1, anchor, entity, fields, prefix, parameters);

        return $"({column} {strict} {parameter} OR ({column} = {parameter} AND {tail}))";
    }

    private static string Bind(object? value, IFieldSqlRenderer fields, string prefix, Dictionary<string, object?> parameters)
    {
        var name = prefix + parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters[name] = value;
        return fields.RenderParameter(name);
    }
}
```

- [ ] **Step 5: Un-stub the composer and run everything**

Restore the `AddFilter` and `AddAnchor` bodies in `ReadStatementComposer.Compose`, then run:
`scripts/test-ring1`.

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests
git commit -m "feat(data): render caller filters and keyset cursors to parameterized SQL"
```

---
## Task 7: `QueryAsync` and `GetAsync` — the read half of `EfAlvoData`

The first task where the whole mechanism runs against a real database. Order matters and is the security
property: resolve policy → deny or continue → check the filter depth → validate every filter/sort field
→ compose one statement whose `WHERE` carries the policy predicate → let the engine filter → mask on the
way out. Nothing is ever filtered in the application tier (§2.4: *"kompilujú do SQL predikátov (nikdy
post-filter v pamäti)"*), and `Limit` is applied by the engine **after** the predicate, never before it.

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RecordMaterializer.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SortComposer.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/KeysetCursor.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (read half only)
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RecordMaterializerTests.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/KeysetCursorTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataReadTests.cs`

**Interfaces:**
- Consumes: `IAlvoData` (`QueryAsync`, `GetAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync` — see
  `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` for the exact signatures and the failure contract);
  `IPolicyEngine.Resolve(string entity, DataOperation operation, AlvoContext context)`;
  `DataOperation.List | Get | Create | Update | Delete`; `ReadStatementComposer` +
  `ReadStatementOptions` (Task 5); `KeysetAnchor` (Task 6); `PredicateParameterBinder` (Task 4);
  `AlvoDataContextFactory` (Task 3); `AlvoRecordNotFoundException`, `AlvoAuthorizationException`.
- Produces:
  - `internal static class RecordMaterializer` — `internal static AlvoRecord ToRecord(
    IDictionary<string, object> row, IReadOnlySet<string> hiddenFields)`.
  - `internal static class SortComposer` — `internal static IQueryable<Dictionary<string, object>> Apply(
    IQueryable<Dictionary<string, object>> rows, EntitySchema entity, IReadOnlyList<AlvoSort> sort)`.
  - `internal static class KeysetCursor` — `internal static string Encode(Guid rowId)`,
    `internal static bool TryDecode(string? cursor, out Guid rowId)`.
  - `internal sealed class EfAlvoData : IAlvoData` — constructor
    `EfAlvoData(IPolicyEngine policy, IPredicateEvaluator evaluator, IPredicateRenderer predicates,
    IFieldSqlRenderer fields, IAlvoSqlDialect dialect, AlvoDataContextFactory contexts)`.

- [ ] **Step 1: Write the failing materializer and cursor tests**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/RecordMaterializerTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class RecordMaterializerTests
{
    [Fact]
    public void Every_projected_column_becomes_a_field_with_its_clr_value()
    {
        var id = Guid.NewGuid();
        var record = RecordMaterializer.ToRecord(Row(("id", id), ("mileage", 42L)), Hidden());

        record["id"].ShouldBe(id);
        record["mileage"].ShouldBe(42L);
    }

    /// <summary>
    /// The masked column arrives as a projected SQL <c>NULL</c>; the key is dropped as well, so a caller
    /// cannot tell a masked field from one the entity does not declare.
    /// </summary>
    [Fact]
    public void A_masked_field_is_absent_rather_than_present_and_null()
    {
        var record = RecordMaterializer.ToRecord(Row(("id", Guid.NewGuid()), ("secret", null)), Hidden("secret"));

        record.Values.ContainsKey("secret").ShouldBeFalse();
    }

    [Fact]
    public void A_genuinely_null_visible_field_stays_present()
    {
        var record = RecordMaterializer.ToRecord(Row(("id", Guid.NewGuid()), ("status", null)), Hidden());

        record.Values.ContainsKey("status").ShouldBeTrue();
        record["status"].ShouldBeNull();
    }

    private static Dictionary<string, object> Row(params (string Field, object? Value)[] fields)
        => fields.ToDictionary(pair => pair.Field, pair => pair.Value!, StringComparer.Ordinal);

    private static IReadOnlySet<string> Hidden(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);
}
```

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/KeysetCursorTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class KeysetCursorTests
{
    [Fact]
    public void A_cursor_round_trips()
    {
        var id = Guid.NewGuid();

        KeysetCursor.TryDecode(KeysetCursor.Encode(id), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(id);
    }

    [Fact]
    public void A_cursor_is_opaque_rather_than_the_bare_id()
        => KeysetCursor.Encode(Guid.NewGuid()).ShouldNotContain("-");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")]
    public void A_malformed_or_forged_cursor_is_rejected_rather_than_throwing(string? cursor)
        => KeysetCursor.TryDecode(cursor, out _).ShouldBeFalse();
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests` → FAIL.

- [ ] **Step 2: Implement the materializer, the cursor and the sort composer**

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RecordMaterializer.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Turns a property-bag row into the <see cref="AlvoRecord"/> the port returns, dropping every masked
/// field's key. The value the engine returned for a masked field is already a projected SQL <c>NULL</c> —
/// the column was never read — and dropping the key too means a masked field is indistinguishable from a
/// field the entity never declared.
/// </summary>
/// <remarks>
/// The values need no conversion: EF's own type mapping shapes them, so a <c>uuid</c> column arrives as a
/// <see cref="Guid"/>, a timestamp as a <see cref="DateTimeOffset"/> and a decimal as a
/// <see cref="decimal"/> — on both engines, which is the single strongest argument for reading through EF
/// rather than through a hand-rolled reader (a raw SQLite reader over the identical statement returns
/// <see cref="string"/> for all three).
/// </remarks>
internal static class RecordMaterializer
{
    internal static AlvoRecord ToRecord(IDictionary<string, object> row, IReadOnlySet<string> hiddenFields)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(hiddenFields);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in row)
        {
            if (!hiddenFields.Contains(field))
            {
                values[field] = value;
            }
        }

        return new AlvoRecord(values);
    }
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/KeysetCursor.cs`:

```csharp
using System.Buffers.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The opaque keyset cursor this provider issues and accepts: base64url over the anchor row's primary
/// key, and nothing else.
/// </summary>
/// <remarks>
/// Deliberately not a serialization of the sort tuple. The anchor row's sort-key values are re-read from
/// the database <b>under the same policy predicate</b> as the page itself, so a stale, forged or
/// cross-tenant cursor finds no anchor and yields an empty page rather than telling its holder anything
/// about a row they cannot see. The cost is one extra round trip per page; the benefit is that a cursor
/// carries no data and therefore cannot leak any, and that the encoding stays free to change because
/// only this provider ever reads it.
/// </remarks>
internal static class KeysetCursor
{
    internal static string Encode(Guid rowId) => Base64Url.EncodeToString(rowId.ToByteArray());

    internal static bool TryDecode(string? cursor, out Guid rowId)
    {
        rowId = default;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        Span<byte> raw = stackalloc byte[16];
        if (!Base64Url.TryDecodeFromChars(cursor, raw, out var written) || written != 16)
        {
            return false;
        }

        rowId = new Guid(raw);
        return true;
    }
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SortComposer.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Linq.Expressions;
using System.Reflection;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Builds the <c>ORDER BY</c> as a LINQ ordering chain over the property bag, with explicit null
/// placement and an always-ascending primary-key tie-breaker appended, so every page of every query has
/// a deterministic total order.
/// </summary>
/// <remarks>
/// <para>
/// This is the one clause that is <em>not</em> composed into the raw statement, and for a reason: EF
/// pushes a <c>FromSql</c> body into a derived table, and a derived table's row order is not guaranteed
/// to survive into the outer query — so an <c>ORDER BY</c> written inside the raw text is not merely
/// redundant, it is unreliable.
/// </para>
/// <para>
/// Null placement is the portable <c>CASE WHEN &lt;key&gt; IS NULL THEN 0 ELSE 1 END</c> emulation, which
/// translates identically on both engines. It is known to defeat an index on the sort key; PostgreSQL's
/// native <c>NULLS FIRST</c>/<c>NULLS LAST</c> is not reachable from LINQ, so recovering the index means
/// moving the whole <c>ORDER BY</c> into the raw statement — a change this data path leaves open (every
/// other clause is already composed there) and which belongs with the work that owns the latency target.
/// </para>
/// </remarks>
internal static class SortComposer
{
    private static readonly MethodInfo _efProperty =
        typeof(EF).GetMethod(nameof(EF.Property))!;

    internal static IQueryable<Dictionary<string, object>> Apply(
        IQueryable<Dictionary<string, object>> rows, EntitySchema entity, IReadOnlyList<AlvoSort> sort)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(sort);

        var ordered = rows;
        var isFirst = true;

        foreach (var key in sort)
        {
            ordered = ApplyNullPlacement(ordered, key, ref isFirst);
            ordered = ApplyKey(ordered, entity, key, ref isFirst);
        }

        return ApplyIdTieBreaker(ordered, isFirst);
    }

    /// <summary>
    /// Orders by "is this key null" first, so the placement is explicit rather than left to each engine's
    /// own default — SQLite and PostgreSQL disagree on where <c>NULL</c> sorts for a given direction, and
    /// an <see cref="AlvoQuery"/> must produce the same page on both.
    /// </summary>
    private static IQueryable<Dictionary<string, object>> ApplyNullPlacement(
        IQueryable<Dictionary<string, object>> rows, AlvoSort key, ref bool isFirst)
    {
        var nullsFirst = key.Nulls == AlvoNullPlacement.First;
        Expression<Func<Dictionary<string, object>, int>> rank = nullsFirst
            ? row => EF.Property<object>(row, key.Field) == null ? 0 : 1
            : row => EF.Property<object>(row, key.Field) == null ? 1 : 0;

        return Chain(rows, rank, descending: false, ref isFirst);
    }

    private static IQueryable<Dictionary<string, object>> ApplyKey(
        IQueryable<Dictionary<string, object>> rows, EntitySchema entity, AlvoSort key, ref bool isFirst)
    {
        var field = entity.Fields.Single(candidate => string.Equals(candidate.Name, key.Field, StringComparison.Ordinal));
        var clrType = FieldClrTypeMap.Optional(field);
        var selector = PropertySelector(clrType, key.Field);

        return Chain(rows, selector, clrType, key.Descending, ref isFirst);
    }

    private static IQueryable<Dictionary<string, object>> ApplyIdTieBreaker(
        IQueryable<Dictionary<string, object>> rows, bool isFirst)
    {
        var selector = PropertySelector(typeof(Guid?), AlvoDataContext.IdColumn);
        var stillFirst = isFirst;
        return Chain(rows, selector, typeof(Guid?), descending: false, ref stillFirst);
    }

    private static LambdaExpression PropertySelector(Type clrType, string field)
    {
        var row = Expression.Parameter(typeof(Dictionary<string, object>), "row");
        var call = Expression.Call(_efProperty.MakeGenericMethod(clrType), row, Expression.Constant(field));
        return Expression.Lambda(
            typeof(Func<,>).MakeGenericType(typeof(Dictionary<string, object>), clrType), call, row);
    }

    private static IQueryable<Dictionary<string, object>> Chain<TKey>(
        IQueryable<Dictionary<string, object>> rows,
        Expression<Func<Dictionary<string, object>, TKey>> selector, bool descending, ref bool isFirst)
        => Chain(rows, selector, typeof(TKey), descending, ref isFirst);

    private static IQueryable<Dictionary<string, object>> Chain(
        IQueryable<Dictionary<string, object>> rows, LambdaExpression selector, Type keyType, bool descending, ref bool isFirst)
    {
        var name = isFirst
            ? descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy)
            : descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy);
        isFirst = false;

        var method = typeof(Queryable).GetMethods()
            .Single(candidate => candidate.Name == name
                && candidate.GetParameters().Length == 2
                && candidate.GetGenericArguments().Length == 2)
            .MakeGenericMethod(typeof(Dictionary<string, object>), keyType);

        return (IQueryable<Dictionary<string, object>>)method.Invoke(null, [rows, selector])!;
    }
}
```

If `EF.Property<object>` does not translate for the null-rank expression, build the rank selector with
the field's own nullable CLR type through `PropertySelector` and an `Expression.Equal(..., null)` body
instead — the shape the spike proved was `EF.Property<string>(e, "status") == null ? 0 : 1`, i.e. the
key's *own* type, not `object`. Prefer that form from the start if the typed version compiles cleanly.

- [ ] **Step 3: Write the failing read test over a real SQLite database**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataReadTests.cs`. These are the read-path facts the
inherited adversarial suite does not pin precisely enough — the *statement*, not just the outcome:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteAlvoDataReadTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task A_list_returns_only_the_rows_the_policy_predicate_admits()
    {
        var world = await NotesWorldAsync();

        var mine = await world.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, world.Alice);

        mine.Count.ShouldBe(2);
        mine.ShouldAllBe(row => Equals(row["owner_id"], world.Alice.User.Value));
    }

    [Fact]
    public async Task A_query_with_no_context_throws_rather_than_defaulting_to_anyone()
    {
        var world = await NotesWorldAsync();

        await Should.ThrowAsync<ArgumentNullException>(
            () => world.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, context: null!));
        await Should.ThrowAsync<ArgumentNullException>(
            () => world.Data.GetAsync("notes", Guid.NewGuid(), context: null!));
    }

    [Fact]
    public async Task A_hidden_field_is_absent_from_every_returned_row_and_its_value_never_leaves_the_table()
    {
        var world = await AccountsWorldAsync();

        var rows = await world.Data.QueryAsync(new AlvoQuery { Entity = "accounts" }, world.Member);

        rows.ShouldAllBe(row => !row.Values.ContainsKey("secret"));
        world.LastStatement.ShouldContain("CAST(NULL AS TEXT) AS \"secret\"");
    }

    [Fact]
    public async Task The_policy_predicate_is_in_the_where_clause_of_exactly_one_statement()
    {
        var world = await NotesWorldAsync();

        await world.Data.QueryAsync(
            new AlvoQuery
            {
                Entity = "notes",
                Filter = new AlvoComparison("title", AlvoFilterOperator.Like, "Alice%"),
                Sort = [new AlvoSort("title", Descending: true)],
                Limit = 1,
            },
            world.Alice);

        world.Statements.Count.ShouldBe(1);
        world.LastStatement.ShouldContain("\"owner_id\" = @alvo_u0");
        world.LastStatement.ShouldContain("\"title\" LIKE @alvo_f0");
    }

    /// <summary>
    /// EF's default C# null semantics would compensate a <c>&lt;&gt;</c> with <c>OR … IS NULL</c> and
    /// return the null row. <see cref="AlvoFilterOperator"/> documents SQL's three-valued behaviour, and
    /// rendering the filter rather than composing it as LINQ is what delivers it.
    /// </summary>
    [Fact]
    public async Task A_neq_filter_does_not_match_a_null_field()
    {
        var world = await NotesWorldAsync(includeNullTitleRow: true);

        var rows = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Filter = new AlvoComparison("title", AlvoFilterOperator.Neq, "Alice-1") },
            world.Alice);

        rows.ShouldAllBe(row => row["title"] is not null);
    }

    [Fact]
    public async Task A_page_after_a_cursor_continues_where_the_previous_page_stopped()
    {
        var world = await NotesWorldAsync();
        var sort = new[] { new AlvoSort("title") };

        var first = await world.Data.QueryAsync(new AlvoQuery { Entity = "notes", Sort = sort, Limit = 1 }, world.Alice);
        var cursor = world.CursorOf(first[^1]);
        var second = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", Sort = sort, Limit = 1, After = cursor }, world.Alice);

        first.Count.ShouldBe(1);
        second.Count.ShouldBe(1);
        second[0]["id"].ShouldNotBe(first[0]["id"]);
        ((string)second[0]["title"]!).ShouldBeGreaterThan((string)first[0]["title"]!);
    }

    [Fact]
    public async Task A_forged_cursor_yields_an_empty_page_rather_than_the_first_one()
    {
        var world = await NotesWorldAsync();

        var rows = await world.Data.QueryAsync(
            new AlvoQuery { Entity = "notes", After = KeysetCursorFor(Guid.NewGuid()) }, world.Alice);

        rows.ShouldBeEmpty();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

The `NotesWorldAsync`/`AccountsWorldAsync` helpers, the intercepted `Statements` list and `CursorOf`
belong on `SqliteAlvoDataFixture` — extend it in this step with an `AlvoDataHost.Statements` list fed by
a `DbCommandInterceptor` registered through `ConfigureProvider`'s options, and a
`Task<World> NotesWorldAsync(...)` that builds the `notes`/`accounts` descriptors from `SnapshotFixture`.
Reuse the descriptors the adversarial suite itself uses (`owner_id == @user.id` on all five operations
for `notes`; `hidden`/`readOnly` flags for `accounts`) so Task 10's subclass adds no new fixtures.
`CursorOf` calls the internal `KeysetCursor.Encode((Guid)row["id"]!)`.

- [ ] **Step 4: Implement the read half of `EfAlvoData`**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` with the constructor, the two
read members and the shared helpers. Keep every method inside the ~25-line ceiling:

```csharp
using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The EF Core implementation of <see cref="IAlvoData"/>: policy is enforced <em>inside</em> this type,
/// as a predicate the database evaluates, never as a filter this process applies to rows it already
/// fetched.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="DbContext"/> this class creates never escapes it, and that is a security boundary
/// rather than encapsulation taste: a tracked, mutated property bag saved through the change tracker
/// emits <c>UPDATE … WHERE id = @p</c> with no policy predicate at all — the shortest and most idiomatic
/// EF code available, which compiles, passes a naive test, and bypasses authorization completely. Writes
/// here therefore run as <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> over the same <c>FromSql</c> root that
/// carries the <c>USING</c> predicate, and queries never track.
/// </para>
/// <para>
/// The order of the checks is the contract. Policy first, so a denied operation reveals nothing about the
/// entity's shape. Then the filter depth cap, because every backend walks a filter recursively. Then the
/// filter and sort field names, because those are the only caller-supplied strings that reach SQL as
/// identifiers. Only then is a statement composed.
/// </para>
/// </remarks>
internal sealed class EfAlvoData : IAlvoData
{
    private readonly IPolicyEngine _policy;
    private readonly IPredicateEvaluator _evaluator;
    private readonly ReadStatementComposer _statements;
    private readonly AlvoDataContextFactory _contexts;

    internal EfAlvoData(
        IPolicyEngine policy,
        IPredicateEvaluator evaluator,
        IPredicateRenderer predicates,
        IFieldSqlRenderer fields,
        IAlvoSqlDialect dialect,
        AlvoDataContextFactory contexts)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(contexts);
        _policy = policy;
        _evaluator = evaluator;
        _statements = new ReadStatementComposer(predicates, fields, dialect);
        _contexts = contexts;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AlvoRecord>> QueryAsync(
        AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(query.Entity, DataOperation.List, context);
        AlvoFilter.EnsureWithinDepthLimit(query.Filter);

        using var db = _contexts.Create();
        var entity = Entity(db, query.Entity);
        QueryFieldGuard.EnsureAvailable(QueryFields(query), entity, decision.HiddenFields);

        var anchor = await AnchorAsync(db, entity, decision, context, query, cancellationToken);
        if (query.After is not null && anchor is null)
        {
            return [];
        }

        var rows = await PageAsync(db, entity, decision, context, query, anchor, cancellationToken);
        return [.. rows.Select(row => RecordMaterializer.ToRecord(row, decision.HiddenFields))];
    }

    /// <inheritdoc/>
    public async Task<AlvoRecord?> GetAsync(
        string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Get, context);

        using var db = _contexts.Create();
        var row = await SingleAsync(db, Entity(db, entity), decision, context, id, lockFor: null, cancellationToken);
        return row is null ? null : RecordMaterializer.ToRecord(row, decision.HiddenFields);
    }

    private PolicyDecision Resolve(string entity, DataOperation operation, AlvoContext context)
    {
        var decision = _policy.Resolve(entity, operation, context);
        return decision.IsDenied
            ? throw new AlvoAuthorizationException(decision.DenyReason ?? "The operation was not authorized.")
            : decision;
    }

    /// <summary>
    /// Resolves the entity from the <b>applied schema this context's model was built from</b>. A dynamic
    /// entity resolves to <see langword="null"/> here, so it is refused exactly like an unknown one — the
    /// dynamic driver is a different <see cref="IAlvoSqlDialect"/>, registered later, not a branch in this
    /// class.
    /// </summary>
    private static EntitySchema? Entity(AlvoDataContext db, string entity) => db.AppliedSchema.Entities
        .FirstOrDefault(candidate =>
            string.Equals(candidate.Name, entity, StringComparison.Ordinal)
            && candidate.Storage == EntityStorage.Physical);

    private static IEnumerable<string> QueryFields(AlvoQuery query) =>
        AlvoFilter.ReferencedFields(query.Filter).Concat(query.Sort.Select(sort => sort.Field));

    private async Task<Dictionary<string, object>?> SingleAsync(
        AlvoDataContext db, EntitySchema? entity, PolicyDecision decision, AlvoContext context,
        Guid id, PreImageMutation? lockFor, CancellationToken cancellationToken)
    {
        if (entity is null)
        {
            throw new AlvoAuthorizationException(UnknownEntityMessage);
        }

        var statement = _statements.Compose(entity, decision, context, new ReadStatementComposer.ReadStatementOptions
        {
            RowId = id,
            LockFor = lockFor,
        });

        var rows = await Materialize(db, entity, statement).SingleOrDefaultAsync(cancellationToken);
        return rows;
    }

    private static IQueryable<Dictionary<string, object>> Materialize(
        AlvoDataContext db, EntitySchema entity, ReadStatement statement)
        => db.Rows(entity.Name).FromSqlRaw(statement.Sql, new PredicateParameterBinder(db).Bind(statement.Parameters));

    private const string UnknownEntityMessage = "The operation was not authorized.";
}
```

`AppliedSchema` is a one-line addition to `AlvoDataContext`
(`internal SchemaModel AppliedSchema => _schema;`) — add it now. `AnchorAsync` re-reads the cursor's
anchor row under the same decision and builds a `KeysetAnchor` from its sort-key values; `PageAsync`
composes the statement with the filter and the anchor, applies `SortComposer.Apply`, then `Take` when
`query.Limit` is set, and materializes. Write both as small private methods:

```csharp
    private async Task<KeysetAnchor?> AnchorAsync(
        AlvoDataContext db, EntitySchema? entity, PolicyDecision decision, AlvoContext context,
        AlvoQuery query, CancellationToken cancellationToken)
    {
        if (query.After is null || !KeysetCursor.TryDecode(query.After, out var anchorId))
        {
            return null;
        }

        var row = await SingleAsync(db, entity, decision, context, anchorId, lockFor: null, cancellationToken);
        return row is null
            ? null
            : new KeysetAnchor(query.Sort, [.. query.Sort.Select(key => row.GetValueOrDefault(key.Field))], anchorId);
    }

    private async Task<List<Dictionary<string, object>>> PageAsync(
        AlvoDataContext db, EntitySchema? entity, PolicyDecision decision, AlvoContext context,
        AlvoQuery query, KeysetAnchor? anchor, CancellationToken cancellationToken)
    {
        if (entity is null)
        {
            throw new AlvoAuthorizationException(UnknownEntityMessage);
        }

        var statement = _statements.Compose(entity, decision, context, new ReadStatementComposer.ReadStatementOptions
        {
            Filter = query.Filter,
            Anchor = anchor,
        });

        var rows = SortComposer.Apply(Materialize(db, entity, statement), entity, query.Sort);
        return await (query.Limit is int limit ? rows.Take(limit) : rows).ToListAsync(cancellationToken);
    }
```

`Take` after the ordering is what makes `Limit` apply to the policy-filtered, ordered set — the
adversarial suite's `A_query_limit_is_applied_after_the_policy_predicate_not_before` fact is the one that
catches getting this backwards.

- [ ] **Step 5: Run ring1 and commit**

Run: `scripts/test-ring1`

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests test/MMLib.Alvo.Data.Sqlite.Tests
git commit -m "feat(data): read rows through a policy-filtered FromSql root with masked projection"
```

---

## Task 8: `CreateAsync` — the change-tracker insert and the in-memory `WITH CHECK`

`create` is the one operation with no stored row to filter, so it has no `USING` predicate and its whole
authorization is `WITH CHECK` over the candidate row — evaluated in memory, because SQL cannot see a row
that does not exist yet. It is also the one operation that legitimately uses the change tracker: spike
`Q5a` shows the insert parameterizes every value with the right `DbType` on both engines.

The three payload rejections are asymmetric and the asymmetry is the contract: `id` is refused on create
*and* update; `tenant_id` is **allowed** on create (the tenant scope guards it, exactly like every other
field the check constrains) and refused on update; a key naming no declared field is refused outright.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WritePayloadGuard.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePayloadGuardTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataCreateTests.cs`

**Interfaces:**
- Consumes: `IPredicateEvaluator.Evaluate(CompiledExpression, AlvoRecord current, AlvoRecord? previous,
  AlvoContext)`; `PolicyDecision.WithCheck`, `.TenantScope`, `.ReadOnlyFields`, `.HiddenFields`;
  `QueryFieldGuard.EnsureDeclared` (Task 5); `AlvoDataContext.IdColumn` / `.TenantIdColumn`.
- Produces:
  - `internal static class WritePayloadGuard` — `internal static void EnsureWritable(
    IReadOnlyDictionary<string, object?> values, EntitySchema? entity, PolicyDecision decision,
    bool isUpdate)`.
  - `EfAlvoData.CreateAsync` implemented.

- [ ] **Step 1: Write the failing payload-guard test**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePayloadGuardTests.cs`, covering the
asymmetry explicitly (a `Decision(readOnly: …)` helper resolving a real `PolicyDecision` the way
`ReadStatementComposerTests` does):

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class WritePayloadGuardTests
{
    [Fact]
    public void An_ordinary_field_is_writable_on_both_paths()
    {
        EnsureWritable(Payload(("plate", "ACME-001")), isUpdate: false);
        EnsureWritable(Payload(("plate", "ACME-001")), isUpdate: true);
    }

    [Fact]
    public void The_row_id_is_refused_on_both_paths()
    {
        Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("id", Guid.NewGuid())), isUpdate: false));
        Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("id", Guid.NewGuid())), isUpdate: true));
    }

    /// <summary>
    /// Deliberately asymmetric: a create legitimately places a row in a tenant, and the synthesized tenant
    /// scope over the candidate row is what decides whether it may. An update can never move a row
    /// between tenants at all, so there the key is refused before any row is looked up.
    /// </summary>
    [Fact]
    public void The_tenant_id_is_writable_on_create_and_refused_on_update()
    {
        EnsureWritable(Payload(("tenant_id", Guid.NewGuid())), isUpdate: false);
        Should.Throw<AlvoAuthorizationException>(() => EnsureWritable(Payload(("tenant_id", Guid.NewGuid())), isUpdate: true));
    }

    [Fact]
    public void A_read_only_field_is_refused_and_the_message_names_it()
    {
        var refused = Should.Throw<AlvoAuthorizationException>(
            () => EnsureWritable(Payload(("status", "closed")), isUpdate: true, readOnly: "status"));

        refused.Message.ShouldContain("status");
    }

    [Fact]
    public void An_undeclared_key_is_refused_without_being_echoed()
    {
        var refused = Should.Throw<AlvoAuthorizationException>(
            () => EnsureWritable(Payload(("nope\"; DROP TABLE vehicle; --", 1)), isUpdate: false));

        refused.Message.ShouldNotContain("DROP TABLE");
    }

    private static Dictionary<string, object?> Payload(params (string Field, object? Value)[] fields)
        => fields.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal);

    private static void EnsureWritable(Dictionary<string, object?> payload, bool isUpdate, string? readOnly = null)
        => WritePayloadGuard.EnsureWritable(
            payload, AlvoDataSqlSnapshotTests.SnapshotEntity, SnapshotFixture.UpdateDecision(readOnly), isUpdate);
}
```

`SnapshotFixture.UpdateDecision(string? readOnlyField)` resolves a real `PolicyDecision` for
`DataOperation.Update` from a primed catalog whose descriptor marks that field `readOnly: true` — add it
beside `SnapshotFixture.VehicleWith` from Task 5.

- [ ] **Step 2: Implement the guard**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WritePayloadGuard.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Refuses a write payload before any row is looked up: a key the entity does not declare, a field the
/// policy marks read-only, and the framework-managed columns a caller may never set.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal here is <see cref="AlvoAuthorizationException"/> and every one of them is decided from
/// the payload alone, so a caller cannot use "was my write rejected" to learn whether a row id exists —
/// the row was never consulted.
/// </para>
/// <para>
/// The two framework columns are handled asymmetrically on purpose. <c>id</c> is assigned once, by this
/// provider, and rewriting it would corrupt row identity — two rows sharing one id, and the row whose id
/// was taken becoming unreachable. <c>tenant_id</c> is legitimately caller-supplied on a create, where
/// the synthesized tenant scope over the candidate row decides whether that tenant is allowed; on an
/// update it is refused outright, because a row can never move to another tenant once created. Neither
/// column is ever a descriptor-declared field, so neither can appear in
/// <see cref="PolicyDecision.ReadOnlyFields"/> — the read-only check alone would let both through.
/// </para>
/// </remarks>
internal static class WritePayloadGuard
{
    internal static void EnsureWritable(
        IReadOnlyDictionary<string, object?> values, EntitySchema? entity, PolicyDecision decision, bool isUpdate)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(decision);

        QueryFieldGuard.EnsureDeclared(values, entity);
        Refuse(values, AlvoDataContext.IdColumn, IdReason(isUpdate));
        if (isUpdate)
        {
            Refuse(values, AlvoDataContext.TenantIdColumn, TenantReason);
        }

        EnsureNoReadOnlyWrite(values, decision.ReadOnlyFields);
    }

    private static void EnsureNoReadOnlyWrite(IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> readOnlyFields)
    {
        foreach (var field in values.Keys.Where(readOnlyFields.Contains))
        {
            throw new AlvoAuthorizationException($"Field '{field}' is read-only and cannot be written.");
        }
    }

    private static void Refuse(IReadOnlyDictionary<string, object?> values, string field, string reason)
    {
        if (values.ContainsKey(field))
        {
            throw new AlvoAuthorizationException($"Field '{field}' {reason}.");
        }
    }

    private static string IdReason(bool isUpdate) => isUpdate
        ? "is assigned once at creation and can never be rewritten"
        : "is assigned by the store and cannot be supplied on create";

    private const string TenantReason = "is fixed at creation and a row can never move to another tenant";
}
```

The messages are word-for-word `InMemoryAlvoData`'s, so the reference implementation and this one produce
the same text for the same refusal — the adversarial suite asserts on `status` appearing in the
read-only message, and a divergence there would be a real inconsistency between two implementations of
one port.

- [ ] **Step 3: Write the failing create test**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataCreateTests.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteAlvoDataCreateTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task An_allowed_create_persists_and_is_readable_with_a_store_assigned_id()
    {
        var world = await NotesWorldAsync();
        var payload = new Dictionary<string, object?>
        {
            ["owner_id"] = world.Alice.User.Value,
            ["tenant_id"] = world.Tenant.Value,
            ["title"] = "brand new",
        };

        var created = await world.Data.CreateAsync("notes", payload, world.Alice);

        created["id"].ShouldBeOfType<Guid>();
        created["title"].ShouldBe("brand new");
        var reread = await world.Data.GetAsync("notes", (Guid)created["id"]!, world.Alice);
        reread!["title"].ShouldBe("brand new");
    }

    [Fact]
    public async Task A_create_whose_post_image_fails_the_check_writes_nothing()
    {
        var world = await NotesWorldAsync();
        var payload = new Dictionary<string, object?>
        {
            ["owner_id"] = world.Bob.User.Value,
            ["tenant_id"] = world.Tenant.Value,
            ["title"] = "smuggled",
        };

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.CreateAsync("notes", payload, world.Alice));

        var rows = await world.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, world.Bob);
        rows.ShouldAllBe(row => !Equals(row["title"], "smuggled"));
    }

    [Fact]
    public async Task A_hidden_field_is_absent_from_the_record_the_create_returns()
    {
        var world = await AccountsWorldAsync();

        var created = await world.Data.CreateAsync(
            "accounts", new Dictionary<string, object?> { ["title"] = "New", ["secret"] = "shh" }, world.Member);

        created.Values.ContainsKey("secret").ShouldBeFalse();
    }

    /// <summary>
    /// The database's own <c>NOT NULL</c> is still the required-ness gate, even though the read model
    /// marks every property optional. A missing required value must surface as a refusal, not as a row.
    /// </summary>
    [Fact]
    public async Task A_missing_required_value_is_refused_by_the_database_constraint()
    {
        var world = await VehicleWorldAsync();

        await Should.ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => world.Data.CreateAsync(
            "vehicle", new Dictionary<string, object?> { ["tenant_id"] = world.Tenant.Value }, world.Alice));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

The last fact is a deliberate, documented rough edge: `DbUpdateException` is not one of `IAlvoData`'s
declared exceptions, because **schema-derived validation is PR3's** and this port does not promise to
pre-validate types or required-ness. Assert the raw exception here and record it in
`docs/architecture/data-path.md` (Task 12) as the seam PR3's RFC 7807 layer closes.

- [ ] **Step 4: Implement `CreateAsync`**

Add to `EfAlvoData`:

```csharp
    /// <inheritdoc/>
    public async Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Create, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: false);

        var candidate = new Dictionary<string, object?>(values, StringComparer.Ordinal)
        {
            [AlvoDataContext.IdColumn] = Guid.NewGuid(),
        };
        EnsureWriteAllowed(decision, new AlvoRecord(candidate), previous: null, context);

        db.Rows(entity).Add(NonNull(candidate));
        await db.SaveChangesAsync(cancellationToken);

        return RecordMaterializer.ToRecord(NonNull(candidate), decision.HiddenFields);
    }

    /// <summary>
    /// Evaluates <c>WITH CHECK</c> and the synthesized tenant scope over the <b>complete post-image</b>,
    /// never over the payload alone — a field the caller did not mention has to read as its stored value,
    /// or an update touching one unrelated field would be denied by its own ownership rule. Evaluating the
    /// tenant scope here, and not only on the read side, is what stops a caller placing or moving a row
    /// into another tenant.
    /// </summary>
    private void EnsureWriteAllowed(PolicyDecision decision, AlvoRecord postImage, AlvoRecord? previous, AlvoContext context)
    {
        var passesCheck = decision.WithCheck is null || _evaluator.Evaluate(decision.WithCheck, postImage, previous, context);
        var passesTenantScope = decision.TenantScope is null
            || _evaluator.Evaluate(decision.TenantScope, postImage, previous, context);

        if (!passesCheck || !passesTenantScope)
        {
            throw new AlvoAuthorizationException("The write was rejected by policy.");
        }
    }

    /// <summary>
    /// A property bag cannot hold a <see langword="null"/> value (its value type is
    /// <see cref="object"/>), so an explicit <see langword="null"/> in a payload means "leave the column
    /// at its database default", which for a nullable column is <c>NULL</c>.
    /// </summary>
    private static Dictionary<string, object> NonNull(IReadOnlyDictionary<string, object?> values) =>
        new(values.Where(pair => pair.Value is not null).Select(pair => KeyValuePair.Create(pair.Key, pair.Value!)),
            StringComparer.Ordinal);
```

Note the `NonNull` limitation and record it in Task 12: on **create** an explicit `null` is
indistinguishable from an omitted key, which is correct for a fresh row (both leave the column `NULL`)
but would be wrong on an update — which is exactly why `UpdateAsync` in Task 9 uses `ExecuteUpdate`
setters, where a `null` setter value is a real `SET col = NULL`.

- [ ] **Step 5: Run ring1 and commit**

Run: `scripts/test-ring1`

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests test/MMLib.Alvo.Data.Sqlite.Tests
git commit -m "feat(data): create rows under an in-memory WITH CHECK over the candidate post-image"
```

---

## Task 9: `UpdateAsync` and `DeleteAsync` — `ExecuteUpdate`/`ExecuteDelete` over the policy root

The most dangerous task in the PR. Spike `Q5d` is the reason: a tracked update emits
`UPDATE … WHERE id = @p1` with **no policy predicate**, and it is the code an EF-fluent developer writes
by reflex. Every write here goes through `ExecuteUpdateAsync`/`ExecuteDeleteAsync` composed over the
`FromSql` root that carries `USING` (`Q5b`, `Q5g`), so the predicate is *inside* the statement and
`rows affected == 0` is the `AlvoRecordNotFoundException` signal — indistinguishable, as the contract
requires, from a row that never existed (`Q5c`).

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UpdateSetterFactory.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataWriteTests.cs`

**Interfaces:**
- Consumes: `Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Dictionary<string, object>>`;
  `RelationalQueryableExtensions.ExecuteUpdateAsync` /
  `ExecuteDeleteAsync`; `db.Database.BeginTransactionAsync`; `FieldClrTypeMap.Optional` (Task 3);
  `WritePayloadGuard` (Task 8); `ReadStatementComposer.ReadStatementOptions.LockFor` (Task 5).
- Produces:
  - `internal static class UpdateSetterFactory` — `internal static
    Action<UpdateSettersBuilder<Dictionary<string, object>>> For(EntitySchema entity,
    IReadOnlyDictionary<string, object?> values)`.
  - `EfAlvoData.UpdateAsync` and `EfAlvoData.DeleteAsync` implemented.

- [ ] **Step 1: Write the failing write test**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataWriteTests.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteAlvoDataWriteTests : IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    [Fact]
    public async Task An_update_of_an_unrelated_field_succeeds_because_the_post_image_still_satisfies_the_rule()
    {
        var world = await NotesWorldAsync();

        var updated = await world.Data.UpdateAsync(
            "notes", world.AliceRowId, new Dictionary<string, object?> { ["title"] = "renamed" }, world.Alice);

        updated["title"].ShouldBe("renamed");
        updated["owner_id"].ShouldBe(world.Alice.User.Value);
    }

    /// <summary>
    /// The <c>USING</c> predicate lives inside the update statement, so another caller's row is not
    /// updated and the outcome is indistinguishable from a row that never existed.
    /// </summary>
    [Fact]
    public async Task An_update_of_another_callers_row_reports_not_found_and_changes_nothing()
    {
        var world = await NotesWorldAsync();

        await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.UpdateAsync(
            "notes", world.BobRowId, new Dictionary<string, object?> { ["title"] = "hacked" }, world.Alice));

        var bobsRow = await world.Data.GetAsync("notes", world.BobRowId, world.Bob);
        bobsRow!["title"].ShouldNotBe("hacked");
    }

    [Fact]
    public async Task An_absent_row_and_an_invisible_row_report_the_same_failure()
    {
        var world = await NotesWorldAsync();

        var invisible = await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.DeleteAsync(
            "notes", world.BobRowId, world.Alice));
        var absent = await Should.ThrowAsync<AlvoRecordNotFoundException>(() => world.Data.DeleteAsync(
            "notes", Guid.NewGuid(), world.Alice));

        invisible.Message.ShouldBe(absent.Message);
    }

    [Fact]
    public async Task An_update_that_would_move_the_row_out_of_the_callers_scope_is_denied_and_the_row_is_unchanged()
    {
        var world = await NotesWorldAsync();

        await Should.ThrowAsync<AlvoAuthorizationException>(() => world.Data.UpdateAsync(
            "notes", world.AliceRowId, new Dictionary<string, object?> { ["owner_id"] = world.Bob.User.Value }, world.Alice));

        var stillHers = await world.Data.GetAsync("notes", world.AliceRowId, world.Alice);
        stillHers!["owner_id"].ShouldBe(world.Alice.User.Value);
    }

    [Fact]
    public async Task A_multi_field_patch_of_several_clr_types_lands_in_one_statement()
    {
        var world = await VehicleWorldAsync();
        var patch = new Dictionary<string, object?>
        {
            ["status"] = "closed",
            ["mileage"] = 4242L,
            ["price"] = 1234.56m,
            ["is_public"] = false,
            ["created_at"] = DateTimeOffset.UnixEpoch,
        };

        var updated = await world.Data.UpdateAsync("vehicle", world.RowId, patch, world.Alice);

        updated["mileage"].ShouldBe(4242L);
        updated["price"].ShouldBe(1234.56m);
        updated["is_public"].ShouldBe(false);
        world.Statements.Count(statement => statement.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact]
    public async Task A_patch_setting_a_nullable_field_to_null_really_clears_it()
    {
        var world = await VehicleWorldAsync();

        var updated = await world.Data.UpdateAsync(
            "vehicle", world.RowId, new Dictionary<string, object?> { ["status"] = null }, world.Alice);

        updated["status"].ShouldBeNull();
    }

    [Fact]
    public async Task A_delete_of_the_callers_own_row_removes_it()
    {
        var world = await NotesWorldAsync();

        await world.Data.DeleteAsync("notes", world.AliceRowId, world.Alice);

        (await world.Data.GetAsync("notes", world.AliceRowId, world.Alice)).ShouldBeNull();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests` → FAIL.

- [ ] **Step 2: Implement the runtime setter factory**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UpdateSetterFactory.cs`. This is the spike's
`Q5h` code, cleaned up — EF Core 10's non-expression `ExecuteUpdateAsync(Action<UpdateSettersBuilder<T>>)`
overload, reached by reflection because the property type varies per field:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MMLib.Alvo.Schema;
using System.Linq.Expressions;
using System.Reflection;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Builds an <c>ExecuteUpdate</c> setter list at request time from a field/value patch. The field names
/// and their CLR types are only known once a request arrives, so the statically-typed
/// <c>SetProperty</c> chain is unavailable and EF Core 10's non-expression
/// <see cref="UpdateSettersBuilder{TSource}"/> overload is driven by reflection instead.
/// </summary>
/// <remarks>
/// The overload selected is the one whose second parameter <em>is</em> the generic method parameter —
/// <c>SetProperty&lt;TProperty&gt;(Func&lt;T, TProperty&gt; selector, TProperty value)</c> — not its
/// sibling that takes a second selector. Getting that wrong compiles and then binds the value as an
/// expression, which is why the discriminator is spelled out rather than left to overload order.
/// </remarks>
internal static class UpdateSetterFactory
{
    private static readonly MethodInfo _setProperty = typeof(UpdateSettersBuilder<Dictionary<string, object>>)
        .GetMethods()
        .Single(method => method.Name == "SetProperty"
            && method.GetGenericArguments().Length == 1
            && method.GetParameters().Length == 2
            && method.GetParameters()[1].ParameterType.IsGenericMethodParameter);

    private static readonly MethodInfo _efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;

    internal static Action<UpdateSettersBuilder<Dictionary<string, object>>> For(
        EntitySchema entity, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(values);

        var setters = values
            .Select(pair => (pair.Key, ClrType: ClrTypeOf(entity, pair.Key), pair.Value))
            .ToList();

        return builder =>
        {
            foreach (var (field, clrType, value) in setters)
            {
                _setProperty.MakeGenericMethod(clrType).Invoke(builder, [Selector(clrType, field), value]);
            }
        };
    }

    private static LambdaExpression Selector(Type clrType, string field)
    {
        var row = Expression.Parameter(typeof(Dictionary<string, object>), "row");
        var call = Expression.Call(_efProperty.MakeGenericMethod(clrType), row, Expression.Constant(field));
        return Expression.Lambda(
            typeof(Func<,>).MakeGenericType(typeof(Dictionary<string, object>), clrType), call, row);
    }

    /// <summary>
    /// The setter's type is the <b>read model's</b> nullable type for the field, so a patch clearing a
    /// nullable column to <see langword="null"/> binds a real <c>SET col = NULL</c> rather than failing to
    /// box.
    /// </summary>
    private static Type ClrTypeOf(EntitySchema entity, string field) => FieldClrTypeMap.Optional(
        entity.Fields.Single(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal)));
}
```

- [ ] **Step 3: Implement `UpdateAsync` and `DeleteAsync`**

Add to `EfAlvoData`. The `WITH CHECK` sequence is merge-then-check inside one transaction, with the
driver's row lock on the pre-image read where it exists:

```csharp
    /// <inheritdoc/>
    public async Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Update, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        WritePayloadGuard.EnsureWritable(values, schema, decision, isUpdate: true);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stored = await SingleAsync(db, schema, decision, context, id, lockFor: PreImageMutation.Update, cancellationToken)
            ?? throw new AlvoRecordNotFoundException();

        var preImage = RecordMaterializer.ToRecord(stored, NoMask);
        EnsureWriteAllowed(decision, Merge(preImage, values), preImage, context);

        if (await AffectedAsync(db, schema, decision, context, id, values, cancellationToken) == 0)
        {
            throw new AlvoRecordNotFoundException();
        }

        var postImage = await SingleAsync(db, schema, decision, context, id, lockFor: null, cancellationToken)
            ?? throw new AlvoRecordNotFoundException();
        await transaction.CommitAsync(cancellationToken);

        return RecordMaterializer.ToRecord(postImage, decision.HiddenFields);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);

        var decision = Resolve(entity, DataOperation.Delete, context);

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var root = PolicyRoot(db, schema, decision, context);

        var affected = await root
            .Where(row => EF.Property<Guid?>(row, AlvoDataContext.IdColumn) == id)
            .ExecuteDeleteAsync(cancellationToken);

        if (affected == 0)
        {
            throw new AlvoRecordNotFoundException();
        }
    }

    private async Task<int> AffectedAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context,
        Guid id, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken)
        => await PolicyRoot(db, schema, decision, context)
            .Where(row => EF.Property<Guid?>(row, AlvoDataContext.IdColumn) == id)
            .ExecuteUpdateAsync(UpdateSetterFactory.For(schema, values), cancellationToken);

    /// <summary>
    /// The queryable a write is composed over: a <c>FromSql</c> root whose <c>WHERE</c> already carries the
    /// <c>USING</c> predicate and the tenant scope, so the emitted <c>UPDATE</c>/<c>DELETE</c> constrains
    /// the row through a subquery the caller cannot influence and <c>rows affected == 0</c> means "no such
    /// visible row".
    /// </summary>
    /// <remarks>
    /// The row id is matched with a LINQ <c>Where</c> here rather than composed into the raw text: that is
    /// the exact shape proved to emit one statement on both engines
    /// (<c>UPDATE … FROM (SELECT id FROM (&lt;root&gt;) WHERE id = @p) …</c>). The comparison is written
    /// against <c>Guid?</c> because every read-model property is nullable.
    /// </remarks>
    private IQueryable<Dictionary<string, object>> PolicyRoot(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context)
    {
        var statement = _statements.Compose(
            schema, decision, context, new ReadStatementComposer.ReadStatementOptions());
        return Materialize(db, schema, statement);
    }

    private static AlvoRecord Merge(AlvoRecord stored, IReadOnlyDictionary<string, object?> values)
    {
        var merged = stored;
        foreach (var (field, value) in values)
        {
            merged = merged.With(field, value);
        }

        return merged;
    }

    /// <summary>
    /// The pre-image a <c>WITH CHECK</c> decision is based on is read <b>unmasked</b>: the check evaluates
    /// over the complete stored row, and a masked field read as <see langword="null"/> would silently
    /// change what a rule referencing it decides. Masking is applied only to what is returned.
    /// </summary>
    private static readonly IReadOnlySet<string> NoMask = new HashSet<string>(StringComparer.Ordinal);
```

**`NoMask` is load-bearing and easy to get wrong.** `SingleAsync` composes its projection from
`decision.HiddenFields`, so a masked field arrives as a projected `NULL` even on the pre-image read.
Fix it by giving `ReadStatementOptions` a `bool Unmasked { get; init; }` and having `Compose` pass
`Unmasked ? EmptySet : decision.HiddenFields` to `ReadProjection` — then
`SingleAsync(lockFor: PreImageMutation.Update)`
for the pre-image asks for `Unmasked = true`, and `GetAsync` does not. Add that flag and a test:
*"the pre-image a check is evaluated over carries a hidden field's real value"* — build it with a rule
`secret == 'shh'` over a `hidden` field and assert the update succeeds.

- [ ] **Step 4: Run ring1 and commit**

Run: `scripts/test-ring1`

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.Sqlite.Tests
git commit -m "feat(data): write through ExecuteUpdate/ExecuteDelete over the policy-carrying root"
```

---
## Task 10: Wiring, the adversarial suite green on SQLite, and the unreachable-`DbContext` arch test

The spike's closing insistence: *"the one thing this spike would insist a reviewer check"* is that the
`DbContext` cannot be reached. That is made a test here, three ways — a reflection check that no public
surface mentions it, a source scan confining `SaveChanges`, and a behavioural check that a queried row
cannot be written back. Then the inherited adversarial suite runs over a real SQLite database, unchanged.

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/AlvoDataEncapsulationArchitectureTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataAdversarialTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataRegistrationTests.cs`
- Modify: `test/_shared/PublicApi.MMLib.Alvo.Data.EntityFrameworkCore.verified.txt`

**Interfaces:**
- Consumes: `AlvoEfCoreProvider.AddRelationalProvider(IAlvoBuilder, RelationalProviderRegistration)`;
  `MMLib.Alvo.Testing.Data.AlvoDataAdversarialTests` with the single abstract member
  `protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor,
  IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)`; `RepositoryRoot.Find()` from
  `MMLib.Alvo.Testing`.
- Produces:
  - `IAlvoData` resolvable from any host that called `AddAlvo(alvo => alvo.UseSqlite(...))` or
    `UsePostgreSql(...)`.
  - `public sealed class SqliteAlvoDataAdversarialTests : AlvoDataAdversarialTests` — the second
    implementation of the milestone's central security suite, after PR1's in-memory one.

- [ ] **Step 1: Register `IAlvoData` and its collaborators**

In `AlvoEfCoreProvider.AddRelationalProvider`, add the registrations after the existing ones:

```csharp
        builder.Services.TryAddSingleton(registration.Fields);
        builder.Services.TryAddSingleton(registration.Dialect);
        builder.Services.TryAddSingleton<IAlvoData>(services => new EfAlvoData(
            services.GetRequiredService<IPolicyEngine>(),
            services.GetRequiredService<IPredicateEvaluator>(),
            services.GetRequiredService<IPredicateRenderer>(),
            services.GetRequiredService<IFieldSqlRenderer>(),
            services.GetRequiredService<IAlvoSqlDialect>(),
            services.GetRequiredService<AlvoDataContextFactory>()));
```

Extend the method's `<remarks>` with a paragraph naming what this adds and why `IAlvoData` is a singleton:
it holds no per-request state, creates a `DbContext` per operation, and takes the caller's
`AlvoContext` as a parameter on every member precisely so that no ambient scope is involved.

- [ ] **Step 2: Write the registration test**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public class SqliteAlvoDataRegistrationTests
{
    [Fact]
    public void The_public_entry_point_alone_yields_a_resolvable_data_port()
    {
        using var services = Build();

        services.GetRequiredService<IAlvoData>().ShouldNotBeNull();
    }

    [Fact]
    public void The_driver_supplies_its_own_field_renderer_and_dialect()
    {
        using var services = Build();

        services.GetRequiredService<IFieldSqlRenderer>().ShouldBeOfType<SqliteFieldSqlRenderer>();
        services.GetRequiredService<EntityFrameworkCore.IAlvoSqlDialect>().ShouldBeOfType<SqliteSqlDialect>();
    }

    /// <summary>
    /// Registration is idempotent, so a host that attaches the provider twice (or attaches one and then
    /// overrides a service) does not end up with two data ports disagreeing about the dialect.
    /// </summary>
    [Fact]
    public void Attaching_the_provider_twice_registers_one_data_port()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:").UseSqlite("Data Source=:memory:"));

        collection.Count(service => service.ServiceType == typeof(IAlvoData)).ShouldBe(1);
    }

    private static ServiceProvider Build()
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));
        return collection.BuildServiceProvider();
    }
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests` → PASS after Step 1.

- [ ] **Step 3: Write the encapsulation architecture tests**

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/AlvoDataEncapsulationArchitectureTests.cs`. This is
the spike's parting instruction turned into a gate:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Testing;
using System.Reflection;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The invariant the de-risking spike said a reviewer must check, as a test instead of a convention: EF's
/// change tracker must be unreachable from outside the data path. A tracked, mutated property bag saved
/// through <c>SaveChanges</c> emits <c>UPDATE … WHERE id = @p</c> with no policy predicate — it compiles,
/// it is the shortest EF code available, and it bypasses authorization entirely.
/// </summary>
public class AlvoDataEncapsulationArchitectureTests
{
    private static readonly Type[] _forbiddenInPublicSurface =
        [typeof(DbContext), typeof(ChangeTracker), typeof(DbSet<>), typeof(AlvoDataContext)];

    private static readonly string[] _scannedProjects =
        ["MMLib.Alvo.Data.EntityFrameworkCore", "MMLib.Alvo.Data.Sqlite", "MMLib.Alvo.Data.PostgreSql"];

    /// <summary>The two files allowed to reach the change tracker: the create path, and the test-only seeding seam.</summary>
    private static readonly string[] _saveChangesAllowList = ["EfAlvoData.cs", "AlvoDataSeed.cs"];

    [Fact]
    public void The_data_context_is_internal_and_sealed()
    {
        typeof(AlvoDataContext).IsPublic.ShouldBeFalse();
        typeof(AlvoDataContext).IsSealed.ShouldBeTrue();
    }

    [Fact]
    public void No_publicly_visible_member_of_any_data_assembly_mentions_a_context_or_a_change_tracker()
    {
        var offenders = _scannedProjects
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(PublicSignatureTypes)
            .Where(type => _forbiddenInPublicSurface.Contains(Normalize(type)))
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "No public member of an Alvo data assembly may expose EF's DbContext, DbSet or ChangeTracker — "
            + $"a caller holding one can write around policy. Offenders: {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void Save_changes_is_reached_from_exactly_the_two_files_allowed_to_reach_it()
    {
        var root = RepositoryRoot.Find();
        var directory = Path.Combine(root, "src", "MMLib.Alvo.Data.EntityFrameworkCore");

        var offenders = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !_saveChangesAllowList.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Where(CallsSaveChanges)
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        offenders.ShouldBeEmpty(
            $"Only {string.Join(" and ", _saveChangesAllowList)} may call SaveChanges — every other write goes "
            + $"through ExecuteUpdate/ExecuteDelete over the policy-carrying root. Offenders: {string.Join(", ", offenders)}.");
    }

    private static bool CallsSaveChanges(string path) => File.ReadAllLines(path)
        .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
        .Any(line => line.Contains("SaveChanges", StringComparison.Ordinal));

    private static IEnumerable<Type> PublicSignatureTypes(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(IsVisibleOutsideTheAssembly)
            .SelectMany(SignatureTypes);

    private static bool IsVisibleOutsideTheAssembly(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily,
        PropertyInfo property => property.GetMethod is { } getter && (getter.IsPublic || getter.IsFamily),
        FieldInfo field => field.IsPublic || field.IsFamily,
        _ => false,
    };

    private static IEnumerable<Type> SignatureTypes(MemberInfo member) => member switch
    {
        MethodInfo method => [method.ReturnType, .. method.GetParameters().Select(p => p.ParameterType)],
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        _ => [],
    };

    private static Type Normalize(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : type;
}
```

Add one behavioural fact to `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataReadTests.cs`:

```csharp
    /// <summary>
    /// The invariant behind the arch test, observed rather than inspected: a row this port returned is a
    /// detached <see cref="AlvoRecord"/>, so there is nothing for a later <c>SaveChanges</c> to write back
    /// around policy — and a fresh context sees the row unchanged.
    /// </summary>
    [Fact]
    public async Task A_returned_row_is_detached_so_mutating_it_changes_nothing()
    {
        var world = await NotesWorldAsync();
        var row = await world.Data.GetAsync("notes", world.AliceRowId, world.Alice);

        var mutated = row!.With("title", "mutated locally");

        mutated["title"].ShouldBe("mutated locally");
        var reread = await world.Data.GetAsync("notes", world.AliceRowId, world.Alice);
        reread!["title"].ShouldNotBe("mutated locally");
    }
```

- [ ] **Step 4: Run the adversarial suite over a real SQLite database**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataAdversarialTests.cs`. Nothing in
`AlvoDataAdversarialTests` changes — the whole point is that the suite is inherited unmodified:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The milestone's central security suite, run over a real SQLite database — the second implementation
/// held to it, after PR1's in-memory reference. Every fact is inherited unchanged: this class supplies a
/// store and nothing else, so a fact cannot be weakened to make a provider pass.
/// </summary>
public sealed class SqliteAlvoDataAdversarialTests : AlvoDataAdversarialTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var host = await _fixture.StartAsync(schema, descriptor);
        await AlvoDataSeed.SeedAsync(host.Services.GetRequiredService<AlvoDataContextFactory>(), seed);
        return host.Services.GetRequiredService<IAlvoData>();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests`

**Expect several failures on the first run, and treat each as a real finding, not as a suite to bend.**
The ones to anticipate, with the fix in each case:

- *`An_entity_with_no_rule_denies_every_operation` / `A_query_with_no_tenant_context_fails…`* — should pass
  already; if not, the policy resolution happens after something else. Fix the ordering in `EfAlvoData`.
- *`A_filter_tree_deeper_than_the_cap_is_rejected_rather_than_walked`* — `EnsureWithinDepthLimit` must run
  before `QueryFieldGuard`, and both before a statement is composed.
- *`A_hidden_expression_that_cannot_resolve_still_masks_the_field`* — this is `IPolicyEngine`'s behaviour,
  already correct in PR1; if it fails here, the projection is reading the wrong set.
- *`Create_of_an_allowed_row_persists_and_is_subsequently_readable`* — the suite's fixtures build entities
  with no `created_at`/audit columns, so the seed and the create must not assume any.
- *A schema/descriptor mismatch* — `BuildFixture` in the suite mirrors `DescriptorToSchemaMapper` by hand.
  If `PolicyCatalog.Build` reports an entity with no schema entry, the fixture's `SchemaModel` is what the
  migrator must create; pass it to `StartAsync` untouched.

Fix the implementation until all facts pass. If a fact looks wrong, that is a finding to raise, **not** an
edit to make — the suite is the specification.

- [ ] **Step 5: Run ring1, accept the baseline, commit**

Run: `scripts/test-ring1`. Accept the moved
`PublicApi.MMLib.Alvo.Data.EntityFrameworkCore.verified.txt`.

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore test/MMLib.Alvo.Data.EntityFrameworkCore.Tests test/MMLib.Alvo.Data.Sqlite.Tests test/_shared
git commit -m "feat(data): register IAlvoData and run the adversarial suite green on SQLite"
```

---

## Task 11: The differential suite over real engines, and PostgreSQL green on both suites

PR1 proved the two Rule backends agree by evaluating the rendered SQL with an in-process three-valued
evaluator (`SqlVerdict`). That proves the *renderer*; it does not prove that a **real engine** agrees.
This task closes that: the same `DifferentialRuleCases.All` matrix, the rendered predicate executed by
SQLite and by PostgreSQL against a real one-row table, compared with `IPredicateEvaluator`. Then the whole
adversarial suite runs over a real PostgreSQL container.

This is also where the engines are allowed to disagree and must not: spike `Q4f` showed a NULL-projected
`NOT NULL` column throws `InvalidOperationException` on SQLite and `InvalidCastException` on PostgreSQL,
and `Q6` showed a prefix collision is loud on PostgreSQL and silent on SQLite. Both are already designed
away; this task is what proves it.

**Files:**
- Create: `src/MMLib.Alvo.Testing/Data/AlvoDataDifferentialTests.cs`
- Create: `src/MMLib.Alvo.Testing/Data/IDifferentialProbe.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataDifferentialTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataFixture.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataAdversarialTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataDifferentialTests.cs`
- Test: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataSqlSnapshotTests.cs`
- Modify: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/MMLib.Alvo.Data.PostgreSql.Tests.Integration.csproj`
- Modify: `test/_shared/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Consumes: `MMLib.Alvo.Testing.DifferentialRuleCases` — `All` (an
  `IReadOnlyList<DifferentialRuleCase(string Rule, AlvoRecord Row, string ContextName)>`) and
  `ContextFor(string name)`; `IPredicateEvaluator`; `IPredicateRenderer`; `ICelCompiler`;
  `IFieldSqlRenderer`; `PostgresFixture` (already present, self-skips on Windows).
- Produces:
  - `public interface MMLib.Alvo.Testing.Data.IDifferentialProbe : IAsyncDisposable` —
    `Task<bool> MatchesAsync(AlvoRecord row, SqlPredicate predicate)`.
  - `public abstract class MMLib.Alvo.Testing.Data.AlvoDataDifferentialTests` — abstract members
    `Task<IDifferentialProbe> CreateProbeAsync(EntitySchema entity)`, `ICelCompiler Compiler { get; }`,
    `IPredicateRenderer Renderer { get; }`, `IPredicateEvaluator Evaluator { get; }`,
    `IFieldSqlRenderer Fields { get; }`; and `public static EntitySchema DifferentialEntity { get; }`.
  - `public sealed class PostgreSqlAlvoDataFixture` — the PostgreSQL twin of `SqliteAlvoDataFixture`,
    with the same `StartAsync(SchemaModel, AlvoDescriptor?)` → `AlvoDataHost` shape.

- [ ] **Step 1: Write the differential suite**

Create `src/MMLib.Alvo.Testing/Data/IDifferentialProbe.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A real database that can be asked one question: does this engine's own <c>WHERE</c> evaluation admit
/// this row under this rendered predicate? Implemented per engine, so the differential suite compares an
/// actual engine's three-valued logic against the in-memory backend rather than a model of it.
/// </summary>
public interface IDifferentialProbe : IAsyncDisposable
{
    /// <summary>
    /// Stores <paramref name="row"/> as the table's only row and answers whether
    /// <paramref name="predicate"/> selects it.
    /// </summary>
    /// <param name="row">The single candidate row.</param>
    /// <param name="predicate">The rendered predicate to use as the whole <c>WHERE</c> clause.</param>
    Task<bool> MatchesAsync(AlvoRecord row, SqlPredicate predicate);
}
```

Create `src/MMLib.Alvo.Testing/Data/AlvoDataDifferentialTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The null-semantics proof obligation, over a real engine. An <c>update</c> rule is enforced twice — as a
/// SQL <c>USING</c> predicate over the stored row and as an in-memory <c>WITH CHECK</c> delegate over the
/// candidate one — so if the two could disagree, one half would permit what the other denies. PR1 proved
/// the renderer against an in-process three-valued evaluator; this proves the same matrix against SQLite's
/// and PostgreSQL's own evaluation, where a dialect's boolean handling, a type mapping, or a collation can
/// still make them differ.
/// </summary>
/// <remarks>
/// The compiler, renderer, evaluator and field renderer arrive as abstract members because this library
/// references <c>MMLib.Alvo.Abstractions</c> alone; an engine's own test project resolves them from
/// <c>AddAlvo()</c>.
/// </remarks>
public abstract class AlvoDataDifferentialTests
{
    /// <summary>Creates a probe over a freshly created table shaped like <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity to create a table for.</param>
    protected abstract Task<IDifferentialProbe> CreateProbeAsync(EntitySchema entity);

    /// <summary>Gets the CEL compiler.</summary>
    protected abstract ICelCompiler Compiler { get; }

    /// <summary>Gets the SQL predicate renderer.</summary>
    protected abstract IPredicateRenderer Renderer { get; }

    /// <summary>Gets the in-memory predicate evaluator.</summary>
    protected abstract IPredicateEvaluator Evaluator { get; }

    /// <summary>Gets the engine's own field/dialect renderer.</summary>
    protected abstract IFieldSqlRenderer Fields { get; }

    /// <summary>
    /// The entity every case is compiled against — the field names
    /// <see cref="DifferentialRuleCases"/> documents, with the nullability the matrix needs (every field
    /// but <c>id</c> is nullable, because half the cases are about a <see langword="null"/> operand).
    /// </summary>
    public static EntitySchema DifferentialEntity { get; } = new()
    {
        Name = "orders",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "status", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, Nullable = true, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "approved_at", Type = FieldType.DateTime, Nullable = true },
            new FieldSchema { Name = "is_public", Type = FieldType.Boolean, Nullable = true },
        ],
    };

    /// <summary>Every entry of the shared matrix as a theory row: the rule, the caller's name, and the case index.</summary>
    public static IEnumerable<object[]> Cases()
    {
        for (var index = 0; index < DifferentialRuleCases.All.Count; index++)
        {
            var testCase = DifferentialRuleCases.All[index];
            yield return [testCase.Rule, testCase.ContextName, index];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task This_engine_and_the_in_memory_backend_agree(string rule, string contextName, int caseIndex)
    {
        var row = DifferentialRuleCases.All[caseIndex].Row;
        var context = DifferentialRuleCases.ContextFor(contextName);
        var compiled = Compile(rule);
        var predicate = Renderer.Render(compiled, context, Fields, "alvo_u");

        var inMemory = Evaluator.Evaluate(compiled, row, previous: null, context);
        await using var probe = await CreateProbeAsync(DifferentialEntity);
        var viaEngine = await probe.MatchesAsync(row, predicate);

        viaEngine.ShouldBe(inMemory, Divergence(rule, contextName, row, predicate, inMemory, viaEngine));
    }

    private CompiledExpression Compile(string rule)
    {
        var result = Compiler.Compile(rule, CelProfile.Rule, DifferentialEntity);
        return result.IsSuccess
            ? result.Expression!
            : throw new InvalidOperationException(
                $"'{rule}' did not compile against the differential entity: "
                + string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    private static string Divergence(
        string rule, string contextName, AlvoRecord row, SqlPredicate predicate, bool inMemory, bool viaEngine)
    {
        var parameters = string.Join(", ", predicate.Parameters.Select(pair => $"{pair.Key}={pair.Value ?? "null"}"));
        var fields = string.Join(", ", row.Values.Select(pair => $"{pair.Key}={pair.Value ?? "null"}"));
        return $"""
            Rule '{rule}' disagreed between this engine and the in-memory backend for caller '{contextName}'.
            Rendered SQL: {predicate.Sql}
            Parameters: {parameters}
            Row: {fields}
            In-memory verdict: {inMemory}
            Engine verdict: {viaEngine}
            """;
    }
}
```

- [ ] **Step 2: Implement the SQLite probe and subclass**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataDifferentialTests.cs`. The probe creates the table
through the production migrator, seeds the one row through `AlvoDataSeed` (so every stored value carries
EF's own mapping — the whole point), then counts rows under the rendered predicate with parameters bound
through `PredicateParameterBinder`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Globalization;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteAlvoDataDifferentialTests : AlvoDataDifferentialTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private readonly ServiceProvider _core = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    protected override ICelCompiler Compiler => _core.GetRequiredService<ICelCompiler>();

    protected override IPredicateRenderer Renderer => _core.GetRequiredService<IPredicateRenderer>();

    protected override IPredicateEvaluator Evaluator => _core.GetRequiredService<IPredicateEvaluator>();

    protected override IFieldSqlRenderer Fields { get; } = new SqliteFieldSqlRenderer();

    protected override async Task<IDifferentialProbe> CreateProbeAsync(EntitySchema entity)
    {
        var host = await _fixture.StartAsync(new SchemaModel([entity]));
        return new Probe(host.Services.GetRequiredService<AlvoDataContextFactory>(), entity, new SqliteSqlDialect());
    }

    public async ValueTask DisposeAsync()
    {
        await _core.DisposeAsync();
        await _fixture.DisposeAsync();
    }

    private sealed class Probe(AlvoDataContextFactory contexts, EntitySchema entity, EntityFrameworkCore.IAlvoSqlDialect dialect)
        : IDifferentialProbe
    {
        public async Task<bool> MatchesAsync(AlvoRecord row, SqlPredicate predicate)
        {
            var withId = row.With("id", Guid.NewGuid());
            await AlvoDataSeed.SeedAsync(
                contexts, new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { [entity.Name] = [withId] });

            using var db = contexts.Create();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {dialect.RenderTable(entity)} WHERE {predicate.Sql}";
            command.Parameters.AddRange(new PredicateParameterBinder(db).Bind(predicate.Parameters));

            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 1;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

**One divergence to expect and to handle deliberately:** `DifferentialRuleCases` builds rows with
`DateTime` values (`created_at`, `approved_at`) while `FieldType.DateTime` maps to `DateTimeOffset`. If
`AlvoDataSeed` cannot store a `DateTime` into a `DateTimeOffset` property, normalize in the probe — a
`DateTime` with `Kind == Unspecified` becomes `new DateTimeOffset(DateTime.SpecifyKind(value,
DateTimeKind.Utc))`, which is exactly the convention `CelInterpreter` and `SqlVerdict` both document. Do
the normalization in the **probe**, not in the shared matrix: the matrix is PR1's and both PRs replay it.

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests`
Expected: every case agrees. A disagreement is a real renderer or mapping bug — debug it, do not exclude
the case.

- [ ] **Step 3: Write the PostgreSQL fixture**

Create `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataFixture.cs` — the twin of
`SqliteAlvoDataFixture`, over one shared container (starting a container per fact is too slow), with a
**fresh database per `StartAsync`** so per-fact isolation still holds:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// One real PostgreSQL container for the class, with a fresh database per <see cref="StartAsync"/> call.
/// A container per fact would be prohibitively slow; a shared database would break the adversarial suite's
/// per-fact isolation requirement, since several facts assert exact row counts over entities with no
/// row-scoping predicate.
/// </summary>
public sealed class PostgreSqlAlvoDataFixture : IAsyncLifetime
{
    // Built inside InitializeAsync, never in a field initializer. Testcontainers' Build() itself talks
    // to the Docker daemon, so on a host with no reachable daemon it throws while the fixture is being
    // *constructed*, which xUnit reports as every test in the sharing class failing before any of them
    // reaches its own skip. PostgresFixture was fixed for exactly this; do not reintroduce it here.
    private PostgreSqlContainer? _container;
    private readonly List<ServiceProvider> _providers = [];

    public bool Available => _container is not null;

    public async ValueTask InitializeAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        _container = container;
    }

    public async Task<AlvoDataHost> StartAsync(SchemaModel schema, AlvoDescriptor? descriptor = null)
    {
        Assert.SkipUnless(Available, "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var connectionString = await CreateDatabaseAsync();
        var builder = new FixtureAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(connectionString);
        builder.Services.AddAlvo();
        var services = builder.Services.BuildServiceProvider();
        _providers.Add(services);

        var migrator = services.GetRequiredService<ISchemaMigrator>();
        await migrator.ApplyAsync(
            await migrator.PlanAsync(new SchemaModel([]), schema, new MigrationOptions()), new MigrationOptions());

        var host = new AlvoDataHost(services, descriptor ?? MinimalDescriptor(schema));
        await host.RePrimeAsync(schema);
        return host;
    }

    /// <summary>
    /// A fresh database per call, created off the container's own admin connection. The name is a
    /// <see cref="Guid"/>, so it cannot collide and needs no quoting beyond the identifier quotes.
    /// </summary>
    private async Task<string> CreateDatabaseAsync()
    {
        var adminConnectionString = _container!.GetConnectionString();
        var name = $"alvo_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{name}\"";
        await create.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static AlvoDescriptor MinimalDescriptor(SchemaModel schema) => /* same as SqliteAlvoDataFixture */ null!;

    private sealed class FixtureAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
```

`AlvoDataHost` is the same type Task 3 introduced. Since it now has two consumers in two assemblies, move
it out of `SqliteAlvoDataFixture.cs` into `src/MMLib.Alvo.Testing/Data/AlvoDataHost.cs` — but keep its
`ServiceProvider`/`PolicyCatalog` dependencies out of `MMLib.Alvo.Testing` (which is Abstractions-only)
by declaring it in **each** test project instead. Two ~20-line twins in two test assemblies is the right
trade against making the shipped Testing library depend on the core.

Copy `MinimalDescriptor` verbatim from `SqliteAlvoDataFixture`.

- [ ] **Step 4: Add the three PostgreSQL subclasses**

`PostgreSqlAlvoDataAdversarialTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The milestone's central security suite over a real PostgreSQL engine — the criterion "green on SQLite
/// and PostgreSQL", proved rather than assumed. Inherits every fact unchanged.
/// </summary>
public sealed class PostgreSqlAlvoDataAdversarialTests : AlvoDataAdversarialTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var host = await _fixture.StartAsync(schema, descriptor);
        await AlvoDataSeed.SeedAsync(host.Services.GetRequiredService<AlvoDataContextFactory>(), seed);
        return host.Services.GetRequiredService<IAlvoData>();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

`PostgreSqlAlvoDataDifferentialTests.cs` and `PostgreSqlAlvoDataSqlSnapshotTests.cs` mirror their SQLite
twins with `PostgreSqlFieldSqlRenderer`, `PostgreSqlSqlDialect`, `EngineName => "postgresql"`, and
`NpgsqlParameter`-free code (the binder handles it). Add `<PackageReference Include="CsCheck" />` only if
a property test lands here — it does not; the property tests are engine-independent and live in
Task 6.

Register the new class-level fixture requirement in the csproj only if `PostgresFixture` is not already
referenced — it is, so no project change is needed beyond what already exists.

- [ ] **Step 5: Run the PostgreSQL leg**

```bash
dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration
```

Expect the first run to surface engine differences. The three to anticipate, all already designed for —
if one of them actually fires, the design was not implemented, so fix the implementation:

- A masked `NOT NULL` column throwing `InvalidCastException` ⇒ the read model is not all-optional.
- `42883: operator does not exist: uuid = text` ⇒ a value reached the statement without going through
  `PredicateParameterBinder`, or a prefix collided with EF's own.
- `numeric` precision differences on `total` ⇒ the model's `HasPrecision` is not being applied. Since
  slice 1's I1/I2 fix, the masked-column cast takes the store type EF resolved
  (`IProperty.GetColumnType()`), so a mismatch here means the read model dropped `HasPrecision`, not that
  a dialect hardcoded the wrong `numeric(p,s)` — that hardcoded table no longer exists.

- [ ] **Step 6: Run ring2, accept baselines, commit**

`scripts/test-ring2` is what runs the integration project (it globs `*.Tests.Integration.csproj`), so use
it here rather than ring1.

```bash
scripts/test-ring2
git add src/MMLib.Alvo.Testing test/MMLib.Alvo.Data.Sqlite.Tests test/MMLib.Alvo.Data.PostgreSql.Tests.Integration test/_shared
git commit -m "test(data): prove both engines agree with the in-memory backend and pass the adversarial suite"
```

Accept `cel-to-sql-postgresql.verified.txt` and the moved `PublicApi.MMLib.Alvo.Testing.verified.txt`;
dispatch `alvo-snapshot-judge` when the turn gate fires.

---

## Task 12: Hardening, docs, mutation config and the PR gate

**Files:**
- Modify: `stryker-config.data-ef.json`
- Create: `docs/architecture/data-path.md`
- Modify: `docs/PLAN.md` (only if `alvo-plan-guard` proposes it)
- Modify: `docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md` (deviation cross-reference)

**Interfaces:**
- Consumes: everything the previous eleven tasks produced.
- Produces: a PR that closes #20.

- [ ] **Step 1: Bring the new data path under mutation**

`stryker-config.data-ef.json` currently excludes every file in the package. Replace its `mutate` list so
the new data-path files **are** mutated and only the pure-wiring and test-only ones are not:

```json
    "mutate": [
      "**/*.cs",
      "!**/AssemblyInfo.cs",
      "!Internal/AppliedSchemaJsonContext.cs",
      "!Internal/RelationalConnectionFactory.cs",
      "!Internal/RelationalSqlBatch.cs",
      "!Internal/SystemSchemaInitializer.cs",
      "!Internal/VersionRowWriter.cs",
      "!Internal/AlvoDataSeed.cs",
      "!EfCoreDescriptorVersionStore.cs",
      "!EfCoreRuntimeSchemaWriter.cs",
      "!EfCoreSchemaIntrospector.cs",
      "!EfCoreSchemaMigrator.cs",
      "!AlvoEfCoreProvider.cs",
      "!RelationalProviderRegistration.cs"
    ],
```

`Internal/AlvoDataSeed.cs` is excluded because it is the test-only seeding seam — mutating it measures the
test harness, not the product. Everything else new (`EfAlvoData`, the composer, the renderers, the guards,
the binder, the setter factory) is deliberately **in** scope: this is the security core, and *"is the
suite still adversarial?"* is the question mutation answers. Note that the two per-engine driver packages
have no Stryker config; the SQL renderers and dialects are covered indirectly by the golden snapshots and
the adversarial suites, and adding two more mutation configs is not earned here.

- [ ] **Step 2: Write the architecture note**

Create `docs/architecture/data-path.md`. It is short and it exists so the next reader does not have to
re-derive the mechanism from the spike:

- **The statement.** `SELECT <projection> FROM <table> WHERE (<USING>) AND (<tenant scope>) [AND <id>]
  [AND <filter>] [AND <keyset>]`, then LINQ `ORDER BY` and `LIMIT`. One statement; the policy predicate
  innermost.
- **The three prefixes** (`alvo_u` / `alvo_c` / `alvo_t`) and the two families (`alvo_f`, `alvo_k`) plus
  `alvo_id`, and the one-paragraph reason: a `p`-prefixed render collides with EF's own `pN` and EF
  renames *our* parameter while the text keeps the old name — silently, on SQLite.
- **No `SaveChanges` on a tracked row.** Writes are `ExecuteUpdate`/`ExecuteDelete` over the policy root;
  `rows affected == 0` is not-found; queries never track; the `DbContext` is internal, and there are three
  tests holding that.
- **The all-optional read model** and why (masked `NOT NULL` columns), plus what still enforces
  required-ness (the physical `NOT NULL`; PR3's validation).
- **Identifier quoting** never goes through `ISqlGenerationHelper`, and no DB schema is introduced.
- **`AlvoSort.Nulls`** uses the portable `CASE WHEN` emulation, which defeats an index; the latency
  criterion and the option to move `ORDER BY` into the raw root belong to the work that owns the target.
- **Two named rough edges for PR3:** a missing required value surfaces as `DbUpdateException`, not RFC
  7807; and on a **create** an explicit `null` is indistinguishable from an omitted key (on update it is
  a real `SET col = NULL`).
- **Two named F7 obligations:** the dynamic driver is a different `IAlvoSqlDialect` + `IFieldSqlRenderer`
  pair, not a different data path — and **a `uuid` JSON path silently returns zero rows on SQLite**,
  because EF stores a `Guid` as upper-case `TEXT` while `json_extract` returns the payload's own case. The
  dynamic driver must normalize `uuid`-typed JSON paths per engine, and that must be a named test in F7's
  plan rather than a discovery.
- **One named PR5 seam:** `context.Database.BeginTransactionAsync()` →
  `transaction.GetDbTransaction()` yields the real provider transaction, so an outbox insert rides the
  same `DbTransaction` as the data change; and because `ExecuteUpdate`/`ExecuteDelete` do **not** go
  through the change tracker, they fire no `SaveChanges` interceptor — PR5's hooks and outbox must be
  sequenced explicitly on the transaction, never hung off `SaveChanges`.

Link it from `docs/PLAN.md`'s architecture-notes list if that list exists; do not otherwise touch
`PLAN.md` in this step.

- [ ] **Step 3: Run the full local gate**

```bash
dotnet format --verify-no-changes
scripts/test-ring2
```

Both must be clean. `scripts/test-ring2` runs the SQLite suites plus the PostgreSQL integration project
(affected-scoped). Never run the mutation suite locally.

- [ ] **Step 4: Reviews before the PR**

1. `/code-review high` — the diff is large and security-relevant. Fix findings.
2. `/security-review` **with** the `alvo-security-core-review` checklist — this PR *is* the security core.
   Pay attention to, in this order:
   - every parameter on every path goes through `PredicateParameterBinder`, and no value is formatted
     into SQL text;
   - the policy predicate is in the `WHERE` of the *same* statement on every one of the five operations,
     including both writes;
   - `SaveChanges` appears only where the arch test permits it, and no `DbSet`/`DbContext` leaks;
   - the field guard runs before any statement is composed, and its two refusals are textually identical;
   - no error message names an entity, a field, a row id, or reveals whether a row exists — including the
     `DbUpdateException` path, which is a documented gap, not an accident;
   - the `WITH CHECK` pre-image is read **unmasked** and under the row lock where the driver has one;
   - `alvo_c` really is unused, and every other prefix family is disjoint.
3. Dispatch `alvo-plan-guard` — drift from `docs/PLAN.md`, §0 principle violations, shortcuts in the
   security core. It is advisory and read-only; act on what it reports.
4. Fix everything the three raise, then re-run `scripts/test-ring2`.

- [ ] **Step 5: Open the PR**

```bash
git push -u origin f3/pr2-alvodata-ef
gh pr create --title "feat(data): IAlvoData on EF Core property bags, green on SQLite and PostgreSQL" --body-file - <<'BODY'
Closes #20.

PR2 of the F3 vertical slice. Turns PR1's security core into a running data path: an `IAlvoData`
implementation over EF Core property-bag entity types, with the resolved policy predicate composed into
the `WHERE` clause of one statement, per-engine `IFieldSqlRenderer`/`IAlvoSqlDialect` pairs for SQLite and
PostgreSQL, and the adversarial + differential suites green on both engines against real databases
(PostgreSQL via Testcontainers).

The mechanism was de-risked by a throwaway spike, whose verdict is committed as
`docs/superpowers/specs/2026-07-26-f3-pr2-spike-verdict.md` and whose code this PR deletes. Three of its
findings shape the implementation and are worth a reviewer's attention:

- a tracked `SaveChanges` emits an `UPDATE` with **no policy predicate**, so writes go through
  `ExecuteUpdate`/`ExecuteDelete` over the policy-carrying `FromSql` root and the `DbContext` is
  unreachable from outside the port — enforced by three tests, not a convention;
- a `p` parameter prefix collides with EF's own and EF then renames *our* parameter while the SQL text
  keeps the old name, silently substituting the caller's value into the security predicate on SQLite.
  That finding went back into PR1, which changed the renderer's default to `alvo_p` (`54d612c`); this PR
  additionally passes an explicit, distinct prefix for each of the three predicates a `PolicyDecision`
  carries;
- a `hidden` field is removed by projecting `CAST(NULL AS <type>)` over an all-optional runtime read
  model, because omitting the column throws and NULL-projecting a `NOT NULL` one throws differently per
  engine.

Deliberate deviations (14 of them, each with its reason) are recorded in the plan's
*Deliberate decisions and deviations* section — most consequentially: `ISchemaRegistry` is implemented by
the policy catalog provider, following the `IRoleCatalogProvider` shape PR1's review settled, so there is
one primed source and no public member on `PolicyCatalog`; the caller filter is rendered to SQL rather than
composed as LINQ, because EF's C# null semantics would break `AlvoFilterOperator`'s documented
three-valued contract; and `AlvoSort.Nulls` keeps the portable `CASE WHEN` emulation, leaving #19's
p95-on-an-indexed-column target to PR3 with the seam named.

Design: `docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md`
Spike verdict: `docs/superpowers/specs/2026-07-26-f3-pr2-spike-verdict.md`
Plan: `docs/superpowers/plans/2026-07-26-f3-pr2-alvodata-ef.md`
Architecture note: `docs/architecture/data-path.md`

**Security core.** Reviewed with `/code-review high`, `/security-review` + the
`alvo-security-core-review` checklist, and `alvo-plan-guard`. Mutation runs post-merge on `main`, so it
was also dispatched on this branch before requesting review (see the next step).
BODY
```

- [ ] **Step 6: Trigger the mutation run and report**

Mutation is post-merge on `main`, so for a security-core PR it is dispatched explicitly first, as
`CLAUDE.md` prescribes for a risky core merge:

```bash
gh workflow run mutation.yml --ref f3/pr2-alvodata-ef
gh run list --workflow=mutation.yml --branch f3/pr2-alvodata-ef --limit 1
```

Report the score and any surviving mutant in the security core as a PR comment. A surviving mutant on
`EfAlvoData`, `ReadStatementComposer`, `FilterSqlRenderer` or `WritePayloadGuard` is a missing test, not a
revert — add the test.

---

## Self-review checklist (run before declaring the plan done)

**Spec coverage — every #20 DoD item maps to a task.**

| DoD item | Task |
|---|---|
| two-user / two-tenant / default-deny adversarial suite, green on **both** engines | 10 (SQLite), 11 (PostgreSQL) — inherited unchanged from PR1 |
| a rule naming a nonexistent column fails at save, not at request time | 2 (the applied schema and the compiled catalog are one primed source, so the apply-time compile is what the data path sees) |
| a CsCheck property test proves the translation never interpolates user input | 6 (`FilterSqlRendererPropertyTests`), plus PR1's `NoInterpolationPropertyTests` for the CEL half |
| the filter parser survives fuzzing; injection attempted through **every** operator | 6 (both arms, over `Enum.GetValues<AlvoFilterOperator>()`) |
| a snapshot proves the policy predicate is in the `WHERE`, not a post-filter | 5 (composer unit snapshot), 7 (`The_policy_predicate_is_in_the_where_clause_of_exactly_one_statement`), 11 (per-engine golden SQL) |
| a query issued without a context throws | 7 |
| golden CEL→SQL snapshots per engine | 1 (SQLite), 11 (PostgreSQL) |
| green on SQLite + PostgreSQL | 10, 11 |
| three disjoint non-`p` prefixes, every value bound through `FindMapping(...).CreateParameter(...)` | 4 (the prefixes, their disjointness test, and the binder). The renderer's `alvo_p` **default** was already landed by PR1 (`54d612c`) in response to the spike, so this PR only ever passes explicit prefixes |
| a tracked `SaveChanges` bypasses policy ⇒ writes via `ExecuteUpdate`/`ExecuteDelete`, `DbContext` unreachable, **as an architecture test** | 9 (the writes), 10 (the three tests) |
| hidden fields ⇒ `CAST(NULL AS <type>)` over an all-optional read model + a custom `IModelCacheKeyFactory` | 3 (model + cache key), 5 (projection) |
| `ISqlGenerationHelper` drops the schema / returns unquoted ⇒ what `IFieldSqlRenderer` does about it | 1 (always quote, never delegate; no DB schema) |
| `AlvoSort.Nulls` has no native translation and the emulation defeats the index — decide and record | 7 (the emulation ships), *Deviations* 4 (PR3 owns the latency target, with the reason) |
| F7-compatible, with the `uuid`-JSON-path trap noted | 1 (`IAlvoSqlDialect` is the seam), 12 (`data-path.md` records the trap as a named F7 test) |
| the spike's throwaway code is deleted or absorbed | 1, Step 9 (deleted; the two renderers, the snapshot fixture, `Q5h`'s setter factory and the binder are absorbed) |

**Deferred on purpose, with owners.** HTTP, PostgREST query-string parsing, offset paging, max page size,
schema-derived validation, RFC 7807, `Idempotency-Key`, `ETag`/`If-Match`, OpenAPI → **PR3**. The
p95-over-100k and keyset-over-1M criteria → **PR3/PR4** (#19), with the `ORDER BY`-into-the-raw-root
option named. Outbox, hooks, the `GetDbTransaction()` seam → **PR5** (#22). `computed`, `rollup` →
**PR6** (#21). The dynamic-entity dialect and the SQLite `uuid`-JSON-path normalization → **F7**. The
`DbUpdateException`-instead-of-RFC-7807 gap and create-time `null`-versus-omitted → **PR3**, both recorded
in `data-path.md`.

**Type consistency.** The names used across tasks, declared exactly once: `IAlvoSqlDialect`
(`RenderTable`, `RenderColumn`, `RenderNullProjection`, `RowLockClause`, `PreImageMutation`) — T1;
`AlvoSqlIdentifier.Quote` —
T1; `FieldClrTypeMap.Exact`/`.Optional` — T3; `AlvoDataContext` (`IdColumn`, `TenantIdColumn`,
`ModelToken`, `AppliedSchema`, `Rows`) — T3/T7; `AlvoModelCacheKeyFactory`, `AlvoDataContextFactory.Create`
— T3; `PolicyParameterPrefix` (`Using`, `WithCheck`, `TenantScope`, `Filter`, `Keyset`, `RowId`, `All`) —
T4; `PredicateParameterBinder.Bind` — T4; `AlvoDataSeed.SeedAsync` — T4; `ReadProjection.Compose`,
`QueryFieldGuard.EnsureAvailable`/`.EnsureDeclared`, `ReadStatement`, `ReadStatementComposer` +
`ReadStatementOptions` (`Filter`, `RowId`, `Anchor`, `Sort`, `LockFor`, `Unmasked`) — T5/T9;
`RenderedSql`, `FilterSqlRenderer.Render`, `KeysetAnchor`, `KeysetSqlRenderer.Render` — T6;
`RecordMaterializer.ToRecord`, `SortComposer.Apply`, `KeysetCursor.Encode`/`.TryDecode`, `EfAlvoData` —
T7; `WritePayloadGuard.EnsureWritable` — T8; `UpdateSetterFactory.For` — T9; `AlvoDataSqlSnapshotTests`
(`EngineName`, `Compiler`, `Renderer`, `Fields`, `SnapshotEntity`, `SnapshotCaller`) — T1;
`AlvoDataDifferentialTests` (`CreateProbeAsync`, `Compiler`, `Renderer`, `Evaluator`, `Fields`,
`DifferentialEntity`, `Cases`) and `IDifferentialProbe.MatchesAsync` — T11; `SqliteAlvoDataFixture` /
`PostgreSqlAlvoDataFixture` / `AlvoDataHost` (`Services`, `RePrimeAsync`) — T3/T11;
`SnapshotFixture.VehicleWith`/`.UpdateDecision` — T5/T8.

Two names appear in a task before the task that creates them, deliberately, and each says so inline:
`FilterSqlRenderer`/`KeysetSqlRenderer` are referenced by T5's composer and created in T6 (T5 stubs the
two call sites), and `AlvoDataSeed` is created in T4's Step 5 although its main consumer is T10.
`ReadStatementOptions.Unmasked` is added in T9 with its reason; T5 declares the record without it.
