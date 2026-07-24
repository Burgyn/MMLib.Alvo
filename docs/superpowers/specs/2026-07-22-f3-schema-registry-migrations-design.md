# Schema registry + migrations (#18) — Design

**Date:** 2026-07-22
**Milestone:** F3 — Vertical slice (CRUD) ([#4](https://github.com/Burgyn/MMLib.Alvo/milestone/4))
**Issue:** [#18](https://github.com/Burgyn/MMLib.Alvo/issues/18) `[14] Schema registry + migrations` (label: `ready`)
**Ships as:** two PRs under one issue — **PR-A** (code-first foundation) then **PR-B** (runtime / dashboard-first). One design doc covers both.

> **What this is.** The substrate of the whole F3 vertical slice: the Data API
> (#19), rule engine (#20), computed/rollup (#21) and events (#22) all stand on
> the schema model and the migration engine defined here. Today
> `MMLib.Alvo.Abstractions` is deliberately source-free — so this issue also
> **births the first real ports and the first core code**. It maps to spec
> Fáza 4 §325–326 and analysis §2.1 / §2.13.

## Goal

Turn the declarative descriptor (F2, `schema/project.schema.json`) into a real
database schema, and keep the two in sync through a **single declarative-diff
engine fed by two sources of the desired state** (analysis §2.13):

- **code-first** — the descriptor is a file in the repo (GitOps; git gives
  audit/history/rollback for free);
- **runtime / dashboard-first** — the descriptor is a DB record changed live via
  the Management API; git is absent, so versioning/rollback/locking are
  reimplemented in the DB.

Both paths run the **same engine** against the same guardrails. Physical driver =
EF Core (introspection + DDL). Works on **SQLite and PostgreSQL** from day one.

## The 9 principles this touches

- **Interface-first** — every port and its contract test exists before the
  implementation; the EF dependency lives *entirely behind* `ISchemaMigrator`.
- **Provider model everywhere / engine-agnostic core** — the same contract suite
  passes identically on SQLite and PostgreSQL; the core never references EF.
- **Schema registry = one model, two drivers** — the `SchemaModel` is
  driver-agnostic; the physical driver is one of two (dynamic driver = F7) and
  must never be baked in as the only one.
- **Secure-by-default** — destructive changes (DROP / narrowing) are gated
  behind an explicit flag + dry-run on both paths.
- **JSON, single descriptor format** — the typed descriptor model parses the one
  F2 schema; no second format.

## Definition of Done (from the issue, split across the two PRs)

**PR-A (code-first):** a descriptor produces a DB schema on SQLite and
PostgreSQL; changing the descriptor file regenerates a migration; destructive
changes require an explicit flag and offer a dry-run; re-apply is idempotent.

**PR-B (runtime):** a runtime change (descriptor as DB record) is append-only
versioned and rollback-able; a concurrent runtime change by two clients is a
conflict via the descriptor `revision`, not a silent overwrite; rollback of a
data-dropping change hits the guardrail.

## Architecture — layers and the EF shield

```
Abstractions  (ports + pure model, NO EF dependency)
   ├─ SchemaModel / EntitySchema / FieldSchema / IndexSchema / RefSchema (+ enums)
   ├─ ISchemaRegistry        read model consumed by Data API / rule engine
   ├─ ISchemaMigrator        THE EF SHIELD: Plan(current,desired) / Apply(plan)
   ├─ ISchemaIntrospector    DB -> SchemaModel (baseline adoption + drift)
   ├─ IDescriptorVersionStore append-only + optimistic lock            [PR-B]
   └─ MigrationPlan / MigrationStep / SchemaChange / MigrationOptions / MigrationResult

MMLib.Alvo  (core — depends ONLY on Abstractions; EF-free, enforced by an arch test)
   ├─ Descriptor/   AlvoDescriptor (typed model) + parser (System.Text.Json source-gen)
   │                + descriptor -> SchemaModel mapper (incl. framework-managed columns)
   ├─ Schema/       SchemaRegistry (physical driver: model from the applied descriptor) + Setup.cs
   ├─ Migrations/   migration-engine orchestration (load -> plan -> guardrail -> apply -> record)
   │                + Internal/ (guardrail policy, system-schema runner (alvo.* prefix), version-store impl)
   └─ AddAlvo() builder skeleton + FromDescriptor()

MMLib.Alvo.Data.EntityFrameworkCore  (EF base — dep: EFCore.Relational)
   ├─ EfCoreSchemaMigrator : ISchemaMigrator
   │     descriptor/SchemaModel -> runtime conventionless IModel
   │     rename pre-pass (renamedFrom -> Rename* operations)
   │     IMigrationsModelDiffer.GetDifferences -> List<MigrationOperation>
   │     guardrail post-scan (Drop*/narrowing -> destructive)
   │     IMigrationsSqlGenerator.Generate(ops, desiredModel) -> dialect SQL
   └─ EfCoreSchemaIntrospector : ISchemaIntrospector

MMLib.Alvo.Data.Sqlite        MMLib.Alvo.Data.PostgreSql   (thin dialect wiring)
```

**The shield, precisely.** The rename intent (`renamedFrom`), the guardrail
classification, and the `MigrationPlan` all live in *our* model. `MigrationPlan`
carries, per step, the semantic `SchemaChange`, the generated SQL as an opaque
string, an `IsDestructive` flag and a reason. Core/Abstractions never see
`MigrationOperation` or `IModel`. **Swapping the engine (hand-rolled diff, or
Atlas as an external tool) is a new `ISchemaMigrator` implementation with zero
core change** — this is the interface-first guarantee the maintainer asked for,
and it is verified by a NetArchTest rule (`MMLib.Alvo` has no reference to
EntityFrameworkCore), not left to discipline.

### Why reuse EF's differ (not just its SQL generator)

The .NET declarative-diff landscape (surveyed 2026): **Atlas** is the mature
option but is a Go binary — shelling out to an external process violates
.NET-native / engine-agnostic-core / provider-model and is poison for the
embedded-NuGet scenario; kept as a possible optional power-user tool, never the
core engine. **Bytebase** is a governance platform, not a library. No pure-.NET
declarative-diff library exists. The closest reusable .NET machinery is EF Core's
`IMigrationsModelDiffer` + per-provider `IMigrationsSqlGenerator`, so we reuse
**both** and hand-write only what EF structurally cannot do:

| Step | Owner | Why |
|---|---|---|
| descriptor -> EF `IModel` (runtime `ModelBuilder`) | **us** | it is *our* format; no library knows it |
| rename pre-pass (`renamedFrom` -> `Rename*`) | **us** | EF does **not** detect renames — sees drop+add (data loss) |
| the diff (add/drop/alter/index/FK/generated col) | **EF differ** | mature, tested; not worth re-writing |
| guardrail post-scan (`Drop*`/narrowing) | **us** | dry-run + explicit-confirm gate |
| operations -> dialect SQL (incl. SQLite rebuild) | **EF SqlGenerator** | SQLite's 12-step table-rebuild handled for free |
| versioning / optimistic lock / rollback | **us** | a runtime feature; no library does it |

**The deciding factor is SQLite.** SQLite cannot `DROP COLUMN`/`ALTER COLUMN`
generally; the correct move is the table-rebuild dance, which
`SqliteMigrationsSqlGenerator` already implements. Postgres alone would make
hand-rolling viable; SQLite as a first-class dev engine tips it decisively to EF.

**Rollback falls out for free:** the reverse plan is `Plan(current=newDescriptor,
desired=oldDescriptor)` — the same differ with swapped inputs; the DROP guardrail
fires automatically (reversing an added required column is a DROP).

### "Current state" for the diff — snapshot-primary

The diff is computed **desired-descriptor vs the last-applied descriptor
snapshot** (both our model — deterministic, cleanly reversible, git-like, makes
optimistic locking natural). Introspection (`ISchemaIntrospector`) keeps two real
jobs, not the per-apply diff:

- **baseline adoption** — first apply against a non-empty DB (embedded ERP, or a
  lost snapshot): introspect to seed the baseline so we don't try to CREATE
  existing tables;
- **drift detection** — if the real DB was hand-altered away from the snapshot,
  refuse/warn rather than emit a corrupting migration.

This refines the spec's literal "diff against DB introspection" while honouring
its intent (introspection is authoritative for reality checks and adoption).
PR-A persists the last-applied snapshot in a single system table; PR-B turns that
table append-only and adds version metadata + optimistic lock + rollback.

## Code organization — VSA vs. mechanism (a framework, not an app)

Vertical Slice Architecture (`docs/architecture/vertical-slice.md`) is
**request/operation-oriented** — it fits code *triggered* by a client/agent/event.
A framework is mostly the other kind of code: **mechanisms** (engines, ports,
mappers, registries) that other code *calls*. So the two coexist, chosen by the
**kind** of code — and the vertical-slice doc already says exactly this (the CRUD
generator + pipeline is "one feature", not a slice; slices are for Management-API
operations and host-written endpoints). The decision rule:

- **Triggered** through a single entry (route / message / schedule)? → **VSA
  slice** (endpoint + handler + validator + model together).
- **A mechanism** other code calls (engine, port, registry, mapper)? →
  **capability/subsystem namespace, the .NET-framework style** — public contract
  at the top, `Internal/` for the guts (exactly how the adjacent EF Core lays out
  `Migrations` / `Metadata` / `Storage` / `Update` + `Internal`).

**#18 is almost entirely mechanism code** — there is no HTTP endpoint yet (the
Management API arrives later), so there are **no VSA slices in this issue**.
"Apply a schema change" is a *mechanism* here (`ISchemaMigrator.ApplyAsync` +
orchestration), invoked by `FromDescriptor()` at startup; when the Management API
lands, a thin `ApplySchemaChangeEndpoint` slice will adapt that same mechanism to
a route (the doc's "handler called by the endpoint **and** any other entry
point"). So the code is organized by capability, not sliced per operation.

**Namespaces are feature-first, without `.Abstractions`** — the ports live in
`MMLib.Alvo.Schema` / `MMLib.Alvo.Migrations` (not `MMLib.Alvo.Abstractions.Schema`),
exactly as `Microsoft.Extensions.Logging.Abstractions.dll` puts `ILogger` in
`Microsoft.Extensions.Logging`. The core implementation shares the same feature
namespace across the assembly boundary, so a consumer needs one `using` and the
contract sits beside its implementation conceptually.

To make the folder-based default namespace match this, `MMLib.Alvo.Abstractions.csproj`
sets `<RootNamespace>MMLib.Alvo</RootNamespace>` — so a file under `Schema/`
defaults to `MMLib.Alvo.Schema`, not `MMLib.Alvo.Abstractions.Schema`. The
**assembly name (NuGet package id) stays `MMLib.Alvo.Abstractions`**; only the
root namespace is overridden. `RootNamespace` is not one of the inherited
properties the convention test forbids re-declaring (`TargetFramework(s)`,
`Nullable`, `ImplicitUsings`, `LangVersion`), so this override is clean.

```
src/MMLib.Alvo.Abstractions/            (contracts only, feature namespaces)
  Schema/       -> MMLib.Alvo.Schema      SchemaModel, EntitySchema, FieldSchema,
                                          IndexSchema, RefSchema, FieldType,
                                          ISchemaRegistry, ISchemaIntrospector
  Migrations/   -> MMLib.Alvo.Migrations  ISchemaMigrator, MigrationPlan, MigrationStep,
                                          SchemaChange, MigrationOptions, MigrationResult,
                                          IDescriptorVersionStore   (PR-B)

src/MMLib.Alvo/                          (same features; mechanisms + Internal)
  Descriptor/   -> MMLib.Alvo.Descriptor  typed model + parser + SchemaModel mapper (+ Internal/)
  Schema/       -> MMLib.Alvo.Schema      SchemaRegistry + Setup.cs (+ Internal/)
  Migrations/   -> MMLib.Alvo.Migrations  migration engine + Setup.cs
                                          Internal/ = guardrail policy, system-schema runner,
                                          version-store impl
```

`Data.*` packages legitimately carry `.Data` in the name — they are real
packages / provider swap points, not a type-layer inside the core.

## The `SchemaModel` (the "one model, two drivers" currency)

Pure POCO in `Abstractions`, EF-free and driver-agnostic:

- `SchemaModel` — the set of `EntitySchema` for a project.
- `EntitySchema` — name, `fields`, indexes, tenancy (scoped/global), `softDelete`,
  `audit`, `renamedFrom`, `storage` (physical | dynamic — dynamic implemented F7).
- `FieldSchema` — name, type (the F2 `fieldType` enum), required/nullable,
  unique, default, `maxLength`/`precision`/`scale`/`values`, `ref` target +
  `onDelete`, index flag, `computed`/`rollup` markers, `renamedFrom`.
- `IndexSchema`, `RefSchema` — composite indexes, FK metadata.

The descriptor -> `SchemaModel` mapper injects **framework-managed columns** so
they are part of the migrated schema:

- `id` (uuid PK) when not declared;
- `tenant_id` (+ index) on `tenancy: scoped` entities when `tenancy.enabled`;
- `created_at/created_by/updated_at/updated_by` when `audit: true`;
- `deleted_at` when `softDelete: true`;
- generated-column DDL for `computed` fields (the *mechanics* — trigger/rollup
  maintenance — are #21; #18 only emits valid `GENERATED ALWAYS AS … STORED`).

Non-schema descriptor blocks (`automation`, `webhooks`, `templates`, `functions`,
`dynamicEntities` governance) are **ignored by the mapper** — proving they create
no tables is itself a test (via `complex-crm`).

## Builder foundation (the public entry-point contract)

`AddAlvo()` is the framework's single entry point and a **public API** — its shape
is expensive to change later ("you can't grow a fluent API"), so the *extensibility
seam* is designed and locked now, while only the surface #18 exercises is
implemented. Grounded in Microsoft's own patterns: the options-pattern guidance
for library authors, the `AuthenticationBuilder`/`IHttpClientBuilder` builder-object
pattern (a builder carrying `Services`, extended by provider extension methods),
the Framework Design Guidelines ("avoid marker interfaces" — ours carries a member,
so it is not one), and the `Add{Group}` + `TryAdd*` service-registration convention.

**Strict rules (defined now, hold framework-wide):**

1. **One entry:** `AddAlvo(this IServiceCollection, Action<IAlvoBuilder>? configure = null)`
   returns `IAlvoBuilder`. Extension classes live in the `Microsoft.Extensions.DependencyInjection`
   namespace (ambient `using`, like `AddDbContext`/`AddHttpClient`).
2. **`IAlvoBuilder` in Abstractions** is an interface with a real member —
   `IServiceCollection Services { get; }` (not a marker). Concrete `AlvoBuilder`
   is `internal sealed` in core. Everything else flows through options + DI, never
   by accreting methods onto the interface.
3. **Providers attach via extension methods** on `IAlvoBuilder` (the
   Authentication/HttpClient pattern). Each `Use*`/`Add*`/`Enable*` lives in its
   own package/feature and registers itself into `builder.Services` via `TryAdd*`.
   **Core never references a provider.** This is the Open/Closed seam.
4. **Fixed verb taxonomy (so an agent never guesses a verb):** `Use{Provider/Infra}`
   (infra selection: `UseSqlite`, `UsePostgreSql`, `UseSchemaPrefix`), `Add{Thing}`
   (additive registration), `Enable{Feature}` (toggle: `EnableDynamicEntities`),
   `From{Source}` (descriptor source: `FromDescriptor`). Fluent methods return
   `IAlvoBuilder`.
5. **Config via the options pattern, validated at startup.** Infrastructure config
   = typed options (`AlvoOptions`: `Mode`, `SchemaPrefix`, …) with
   `ValidateDataAnnotations().ValidateOnStart()` / `IValidateOptions<T>` →
   fail-fast with a structured error + fix suggestion (agent-first).
6. **Descriptor ≠ options (hard).** The project descriptor does NOT go through the
   options pattern — it is domain input via `IDescriptorSource` (file now, DB record
   in PR-B). Options carry infrastructure only (upholds "descriptor ≠ infra config").
7. **Idempotent registration** — `TryAdd*` everywhere; a provider selected twice is
   not a duplicate.
8. **Fail-fast on a missing provider** — `AddAlvo()` with no database provider →
   a startup `IValidateOptions` throws "register a database provider: call
   UseSqlite() or UsePostgreSql()". No silent SQLite default in core (core must not
   drag a provider); zero-config SQLite is supplied by the standalone Host (#24).
9. **Explicit lifetimes** for ports (thread-safe singletons per the DI guidelines),
   decided per port in the plan.
10. **`MapAlvo` is a separate orthogonal seam** (`IEndpointRouteBuilder` extension) —
    not part of `AddAlvo`, not built in #18, reserved so endpoints stay additive.
11. **Public-API approval locks the builder surface** (`IAlvoBuilder`, `AddAlvo`,
    `AlvoOptions`, the verb taxonomy); any change is a conscious SemVer act. Concrete
    builder + registrations are `internal`.

**#18 builds:** `IAlvoBuilder`, `AddAlvo`, `AlvoOptions` (`Mode` + `SchemaPrefix`),
`FromDescriptor`/`IDescriptorSource` (file), `UseSqlite`/`UsePostgreSql` in the two
provider packages (the proof the seam holds), and the fail-fast validation.
**Reserved (the seam must not preclude them):** `UseTenancy`/`UseAuth`/
`EnableDynamicEntities`/`UseAzure`, `MapAlvo`, secret/storage/cache providers.

```csharp
// Abstractions — namespace MMLib.Alvo
public interface IAlvoBuilder { IServiceCollection Services { get; } }

// core — namespace Microsoft.Extensions.DependencyInjection
public static class AlvoServiceCollectionExtensions
{
    public static IAlvoBuilder AddAlvo(
        this IServiceCollection services, Action<IAlvoBuilder>? configure = null);
}
// FromDescriptor(this IAlvoBuilder, string path)  -> registers a file IDescriptorSource
// UseSchemaPrefix(this IAlvoBuilder, string prefix) -> AlvoOptions

// Data.Sqlite / Data.PostgreSql — namespace Microsoft.Extensions.DependencyInjection
//   UseSqlite / UsePostgreSql (this IAlvoBuilder, string connectionString)
//     -> TryAdd ISchemaMigrator / ISchemaIntrospector + connection
```

### Scaling to many providers (studied rules + pitfalls)

Alvo will grow dozens of extending packages (data, secrets, storage, cache,
email/sms/push, identity, AI, functions, telemetry — spec §1.2). The seam is
therefore designed against how mature frameworks scale, with one deliberate
divergence:

- **Follow the `AuthenticationBuilder` / `IHttpClientBuilder` model, NOT EF Core's
  `IDbContextOptionsExtension` machinery.** EF's immutable options-extension +
  `ExtensionInfo` (`GetServiceProviderHashCode` / `ShouldUseSameServiceProvider`)
  exists to cache EF's **internal** service provider per distinct configuration.
  Alvo registers into the **host** container and has no internal cached provider,
  so that machinery would be cargo-culted complexity. We borrow only EF's
  *disciplines*: immutable options and a centralized fail-fast provider-selection
  check ("exactly one database provider, else throw").
- **Provider self-registration:** each `Use*`/`Add*`/`Enable*` extension `TryAdd`s
  its own services into `builder.Services`. Core never enumerates providers.
- **Multiple implementations of one port → keyed services** (.NET 8+
  `AddKeyedSingleton<IPort, Impl>(key)` + `[FromKeyedServices(key)]`); a fixed
  provider-key convention is defined when the first multi-impl port lands.
  Single-impl ports (all of #18's) use plain `TryAdd`.
- **Options are per-feature and immutable after startup** — no god-options; each
  feature/provider owns its options class, bound and `ValidateOnStart`-validated.
- **Deliberate options interface:** `IOptions<T>` for start-fixed infra (default);
  `IOptionsMonitor<T>` only where live reload is a real requirement;
  `IOptionsSnapshot<T>` avoided on hot paths (scoped, slow).
- **Capability model** (spec §1.2): a provider may declare capabilities (e.g.
  transactional outbox, presigned upload); the framework degrades gracefully when
  one is absent. Named now as the pattern so it is not bolted on ad hoc; not
  implemented in #18 (its only port, the migrator, is mandatory).
- **Centralized fail-fast selection validation** — a startup `IValidateOptions`
  asserts required ports have a provider and rejects invalid combinations, with a
  structured error + fix suggestion.

**Pitfalls this bans (what to watch for):**

- **Never call `BuildServiceProvider()` during registration** — it builds a second
  container, duplicates singletons and leaks. Config that needs other services uses
  deferred `IConfigureOptions<T>` / `OptionsBuilder.Configure<TDep>(...)`.
- **No unvalidated config** silently defaulting to null/empty — hence mandatory
  `ValidateOnStart`.
- **Registration-order traps:** `TryAdd` is first-wins (defaults); a deliberate
  override is explicit, never a matter of `using` order.
- **`reloadOnChange` does not auto-propagate** unless consumed via
  `IOptionsMonitor<T>` — don't assume it does.

These are framework-wide rules; #18 merely obeys them with its minimal surface. A
follow-up should promote them into a dedicated architecture doc (see Follow-up).

## Data flow (code-first apply)

```
project.json
  -> validate against schema/project.schema.json   (F2, Corvus.Json.Validator)
  -> AlvoDescriptor (typed)
  -> SchemaModel (desired)
  -> current = last-applied snapshot  (or introspection: baseline / drift check)
  -> migrator.Plan(current, desired) -> MigrationPlan
  -> guardrail: destructive && !AllowDestructive
        -> emit dry-run report, refuse
  -> migrator.Apply(plan) in a transaction (EF9 migration lock)
  -> record the new applied snapshot
```

Two *when-to-apply* modes over the same `Apply`: **generate-and-review** (produce
a SQL script for inspection + CI/CD apply — the safe prod path) and
**apply-on-startup** (dev / embedded, guardrailed). The **system-schema runner**
(the framework's own fixed `alvo.*` tables — for #18 just the applied-snapshot /
version table; outbox etc. arrive with their own issues) runs separately at
startup as a small versioned-SQL runner — **not** the declarative engine and
**not** EF-as-ORM migrations (keeps the two migration concerns cleanly apart;
honours embedded cohabitation, §2.13).

## PR split

### PR-A — code-first foundation
**Step 0 — rename spike (first, before anything else).** Prove the EF-differ +
`Rename*` operations produce a data-preserving rename (not drop+add) on **both**
SQLite and PostgreSQL, given a desired `IModel` built from the descriptor. This is
the one real technical risk of the whole design (assumption 4); it is de-risked in
isolation before the rest of the slice is built. If it fails, fall back to the
own-semantic-diff variant (EF as SQL emitter only) — the `ISchemaMigrator` port
makes that swap non-breaking.

Then: ports + contract tests (red-first) · typed descriptor model + parser · descriptor
-> `SchemaModel` mapper (incl. managed columns) · `SchemaRegistry` physical driver
· `ISchemaMigrator` + `ISchemaIntrospector` (EF impl + SQLite + PostgreSQL) ·
guardrail + dry-run · applied-snapshot system table + runner · `AddAlvo()` /
`FromDescriptor()` skeleton. **DoD:** descriptor -> tables on both engines,
dry-run, idempotent re-apply.

### PR-B — runtime / dashboard-first
`IDescriptorVersionStore` (append-only) + impl · optimistic locking (revision ->
conflict) · rollback (reverse plan via swapped inputs) + DROP guardrail ·
two-client concurrency conflict · service-level runtime apply. **DoD:** runtime
change versioned + rollback-able; concurrent change conflicts via `revision`.
*(The HTTP Management-API endpoint that drives runtime apply belongs with the
Management API; PR-B delivers the service-level operation it will call.)*

## Package layout (pre-blessed by `docs/architecture/package-boundary.md`)

New `src` projects (PR-A): `MMLib.Alvo` (core),
`MMLib.Alvo.Data.EntityFrameworkCore` (EF base — earned: drags EFCore.Relational;
mirrors EF's own Relational+providers split), `MMLib.Alvo.Data.Sqlite`,
`MMLib.Alvo.Data.PostgreSql` (each earned: drags a driver **and** is the
provider-model swap point — an embedded host picks one engine and must not drag
the others). Dependency graph: core -> Abstractions; `Data.*` -> Abstractions
(+ EF); **core does not depend on `Data.*`** (the host wires a provider via DI);
no `Data.*` depends on another provider. Every shipped package gets a `*.Tests`
project + a committed public-API baseline. Ports for the whole issue are added to
`Abstractions`; PR-B adds `IDescriptorVersionStore` there.

## Testing strategy (the core of this design)

Philosophy: **interface-first / red-first** — the contract-test bases and snapshot
expectations are written first (they *are* the behavioural spec), then
implemented to green. **Dogfood** the real `examples/` descriptors as the primary
fixtures, never synthetic-only.

**1. Contract tests per port (spec type 1) — the engine-agnostic proof.**
One abstract base per port; each provider inherits and must pass *identically*:
```
SchemaMigratorContractTests (abstract)
  ├─ SqliteSchemaMigratorTests     : SchemaMigratorContractTests
  └─ PostgreSqlSchemaMigratorTests : SchemaMigratorContractTests
```
Cases: create-from-descriptor, add column, drop column (guarded), **rename via
`renamedFrom` preserves data**, add index, add FK, managed (audit/softDelete/
tenant_id/generated) columns, **double apply = empty plan (idempotency)**.
Likewise `SchemaIntrospectorContractTests`, and in PR-B
`DescriptorVersionStoreContractTests` (append-only, optimistic-lock conflict,
rollback). First implementation to satisfy them = **in-memory fakes**, which ship
in `MMLib.Alvo.Testing` (users get them to test their own apps).

**2. Snapshot tests (Verify, spec type 6) — the EF-drift shield.**
- descriptor -> `SchemaModel` mapping (feeds `simple-tasks` + `complex-crm`;
  freezes the mapping, incl. managed-column injection);
- **generated SQL per engine** — for a canonical change set (create, add/drop/
  rename column, type change, index, FK, generated column) snapshot the SQL
  separately for **SQLite and PostgreSQL**. Any EF upgrade that changes the SQL
  breaks the test visibly — this is what covers "the differ/generator is EF
  internal";
- dry-run report; structured rejection error for a destructive change; the
  reverse/rollback plan (PR-B).

**3. Integration tests (Testcontainers, spec type 4) — `*.Tests.Integration.csproj`**
(`scripts/test-ring2` already discovers these, affected-scoped).
Against real **PostgreSQL** (Testcontainers) + **SQLite** (in-proc file):
- apply a descriptor, then **introspect the live DB** and assert the actual
  schema (tables, column types, nullability, indexes, FKs, generated columns)
  equals the descriptor — the real proof, not fakes;
- idempotency (2× apply = no-op); **dry-run does not mutate** (introspect after
  = unchanged); **rename preserves rows** (seed, rename, assert data); code-first
  round-trip (v1 -> apply -> v2 -> apply -> introspect = v2);
- **builder/DI wiring** proven here: `AddAlvo(...).FromDescriptor(path)` against
  Testcontainers PG asserts the migration ran (this is why no runnable sample app
  is needed — see "Demo");
- PR-B: runtime change -> rollback -> introspect = prior schema; two concurrent
  version appends -> one wins, one conflicts.

**4. Property-based (CsCheck) — catches "works only for the demo"** (the
metadata-driven trap the analysis warns of, §2.1).
- generate random-but-valid field sets -> map -> diff vs empty -> apply on SQLite
  -> introspect -> every declared field exists with the right type;
- diff invariants: `diff(A,A)=∅` (no phantom changes — stability),
  `apply(diff(A,B))≈B`, **`reverse(diff(A,B))=diff(B,A)`** (rollback symmetry),
  a rename never emits DROP+ADD.

**5. Guardrail / adversarial tests.**
Destructive change without `AllowDestructive` -> refused, DB untouched; dry-run
always available; rollback that would drop data -> guardrail fires.
*(Migrations are not the security core — CEL/tenancy is — so the Stryker mutation
gate does not apply here; the local gate is `/code-review` + these guardrail
tests.)*

**6. Architecture tests (NetArchTest, spec type 3) — the shield as a TEST.**
- `MMLib.Alvo` (core) has **no reference to EntityFrameworkCore** — the key guard;
- `Abstractions` depends on nothing (existing rule); `Data.*` depends only on
  `Abstractions` (+ EF), not on core; `Data.PostgreSql` does not reference
  `Data.Sqlite`.

**7. Public-API approval (spec type 7)** — new baselines for `MMLib.Alvo`,
`Data.EntityFrameworkCore`, `Data.Sqlite`, `Data.PostgreSql`; additions to
`Abstractions`.

**8. Parity scaffold (physical vs dynamic).** The contract base is written so it
*can* run against a dynamic driver (F7); the dynamic leg is `[Fact(Skip="F7")]`
now — proof the model does not preclude parity (analysis §2.1 acceptance: the
same suite passes identically over a physical and a virtual entity).

**CI matrix.** Today the matrix is `os`-only. PR-A adds the **DB-engine
dimension**: SQLite in-proc in every leg (fast); **PostgreSQL via Testcontainers
gated to the ubuntu leg** (Testcontainers on macOS/Windows runners is flaky).
SQL Server is deferred to F4 (spec: enabled right after the green PG suite).

## Demo / fixtures — decision

**No runnable sample app or Dockerfile in #18.** The integration tests *are* the
end-to-end proof (`descriptor -> apply -> introspect real DB` on SQLite + PG);
a console/host that migrates-and-serves-nothing adds no verification value, and a
runnable backend's real payoff needs #19 CRUD — it belongs to **#24 (F4)**, whose
DoD is explicitly "a functional backend". Building it now would over-design the
`AddAlvo().FromDescriptor()`/host surface beyond the schema slice (gold-plating
into F4).

**Kept:** a new `examples/vehicle-registry/vehicles.alvo.json` descriptor — a pure
JSON fixture that serves three masters: the F2 examples corpus (valid vs the
schema), the #18 migration fixture, and the seed for the full #23 "Vehicle
Registry" demo later. Plus the existing `complex-crm` (a migration stress-fixture:
`renamedFrom`, `rollup.via`, `computed`, `tenancy.enabled` -> `tenant_id`,
`audit`/`softDelete`, `formats`) and `simple-tasks`.

## Docker — decision

The shipped standalone image (`mmlib/alvo`, `docker run -v project.json …`,
`docker-compose`) is **#24 [20] in F4**, whose DoD needs a functional backend
(the Data API, #19) — not this slice. #18 does not create the `MMLib.Alvo.Host`
package. Docker is orthogonal to the PR-A/PR-B split (it is not PR-B).

## Scope / YAGNI — explicitly out

Data API / CRUD (#19); rule engine (#20); computed/rollup *maintenance* mechanics
(#21 — #18 only emits the generated-column DDL); events / outbox (#22); SQL Server
provider (F4, right after green PG); the HTTP Management-API endpoint for runtime
apply (PR-B ships the service operation; the endpoint arrives with the Management
API); the dynamic-entity store (F7 — the model accommodates it, the store is not
built); a runnable demo app / Docker image (#23 / #24); Atlas as an optional tool.

## Follow-up

> **Sequencing decision.** The two doc changes below land **together in one
> doc-only PR that precedes #18's code PRs** — so the framework-wide rules exist
> and are loadable by `alvo-architecture-rules` before any provider code is
> written. Delivery order: **(1) doc-only PR** → **(2) PR-A** (spec + plan + code)
> → **(3) PR-B**.

- **Clarify `docs/architecture/vertical-slice.md` scope.** The doc currently
  *implies* — but does not state outright — that Vertical Slice Architecture (the
  **REPR** endpoint pattern) governs primarily **request-triggered operations**
  (HTTP / message / schedule), while framework **mechanisms** (engines, ports,
  registries, mappers) are organized by **capability/subsystem** with a public
  contract + `Internal/` (the .NET-framework style, à la EF Core's
  `Migrations`/`Metadata`/`Storage`). This is a durable framework-wide decision
  surfaced while designing #18, not specific to it. It should land as a **small,
  separate doc-only change** (2–3 sentences added to `vertical-slice.md`), kept
  out of the #18 code PRs so a doc-refactor never mixes with engine code. The
  decision rule and rationale are recorded in this spec's "Code organization"
  section; the doc edit only lifts them into the architecture doc itself.
- **Promote the builder/DI/options/provider rules into a dedicated architecture
  doc** (e.g. `docs/architecture/extensibility.md`), referenced by
  `alvo-architecture-rules`. The "Builder foundation" and "Scaling to many
  providers" sections here are the durable, framework-wide contract for every
  future extending package (data, secrets, storage, cache, messaging, identity,
  AI, functions, telemetry) — they should live where the arch-rules skill loads
  them, not buried in one issue's spec. #18 implements the minimal surface; the
  doc is where the rules become discoverable for the next 30 packages. Land as a
  **separate doc-only change** (may be bundled with the vertical-slice.md
  clarification above), out of the #18 code PRs.

## Assumptions (veto candidates)

1. `Data.EntityFrameworkCore` as a shared EF-base package (mirrors EF's own
   Relational+providers split) — not duplicated EF glue in each provider.
2. The framework's fixed `alvo.*` system tables migrate via a small dedicated
   versioned-SQL runner, not the declarative engine and not EF-as-ORM migrations.
3. "Current" for the diff = the last-applied descriptor snapshot; introspection
   only for baseline adoption + drift. PR-A persists a single applied snapshot;
   PR-B makes it append-only + versioned + locked + rollback-able.
4. Reuse EF's `IMigrationsModelDiffer` (not just the SQL generator); we own only
   descriptor->model, the rename pre-pass, the guardrail post-scan, and
   versioning. The exact rename mechanism (align current-model names + prepend
   `Rename*` ops) is the primary implementation risk to de-risk early in the plan.
5. The design doc + plan-A + code-A land in PR-A; plan-B + code-B in PR-B.
6. Spec is written in English, matching the existing specs.

## Verification (how we know it worked)

- PR-A: `examples/*` descriptors apply to real SQLite and PostgreSQL; introspection
  matches; re-apply is a no-op; dry-run refuses destructive changes without the
  flag and mutates nothing; the `MMLib.Alvo`-has-no-EF arch test is green;
  generated-SQL snapshots exist for both engines.
- PR-B: a runtime version append is append-only and audited; rollback restores the
  prior schema and trips the DROP guardrail; two concurrent appends conflict via
  `revision`.
- `alvo-plan-guard` (dispatched pre-PR, read-only) reports no drift from
  `docs/PLAN.md` and no violated §0 principle; `/code-review medium` on each PR.
