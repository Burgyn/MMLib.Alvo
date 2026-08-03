# Package boundary

> The rule that decides what becomes a separate NuGet package in the
> `MMLib.Alvo.*` family. Source: spec `docs/product/alvo-specifikacia.md` §1.1.
> Counterpart: [`vertical-slice.md`](./vertical-slice.md) decides how code is
> organized *inside* a package — a different axis. Neither justifies the other's
> answer: a vertical slice is never a reason to split a package, and a package
> split is never a reason to organize by technical layer inside one.

## Current projects

- `src/MMLib.Alvo.Abstractions` — interface-first root of the dependency
  graph: the ports (`ISchemaMigrator`, `ISchemaIntrospector`, `ISchemaRegistry`,
  `IAppliedSchemaStore`, `IDescriptorSource`) + the driver-agnostic schema model
  + the `IAlvoBuilder`/`AlvoOptions` builder contract. Depends only on
  `Microsoft.Extensions.DependencyInjection.Abstractions` (the one exception —
  see Hard dependency rules).
- `src/MMLib.Alvo` — the core: descriptor parse/map, schema registry, the
  migration orchestration (`SchemaMigrationRunner`) + guardrail, the rule engine
  and CEL compiler, caller context/auth, and the `AddAlvo()` builder — plus, from
  PR3, the generated **HTTP Data API** and its OpenAPI enrichment. EF-free
  (enforced by an arch test), but **an ASP.NET Core library** — see Hard
  dependency rules.
- `src/MMLib.Alvo.Data.EntityFrameworkCore` — shared EF-based host (the
  descriptor→`IModel` builder, the EF-differ migrator, the introspector, the
  applied-schema store); drags `Microsoft.EntityFrameworkCore.Relational`.
- `src/MMLib.Alvo.Data.Sqlite` / `src/MMLib.Alvo.Data.PostgreSql` — thin
  provider packages (`UseSqlite`/`UsePostgreSql`); each drags its EF driver and
  is a real swap point.
- `src/MMLib.Alvo.Testing` — test-support library (`ArchTargetAttribute`,
  `RepositoryRoot`, the `ISchemaMigrator` contract suite + in-memory fake);
  Abstractions-only, `IsPackable=false` until external provider authors need it.
- `src/MMLib.Alvo.Testing.EntityFrameworkCore` — the relational half of the
  test-support library, split out in PR2 so an EF dependency is not handed to
  every consumer of the adversarial and differential suites. An **earned** split
  by the rule below: a real dependency boundary appeared.
- `src/MMLib.Alvo.Host` — the standalone host (spec §2.14 mode 1): a `WebApplication`
  that turns a mounted project descriptor into a running backend, plus Scalar as its
  docs UI. **Earned by rule (c)** — a different distribution: it ships as the
  `mmlib/alvo` container image, not as a NuGet package, so it is
  `IsPackable=false`. Rule (a) applies to its Scalar dependency as well: a docs UI is
  a hosting decision, and most embedded consumers do not want the package. It is the
  only project allowed to reference more than one `MMLib.Alvo.Data.*` provider — it
  ships both drivers and registers exactly one, chosen by configuration. That pair of
  facts is also why it cannot be a slice inside the core, which is the default this
  document otherwise insists on: an entry point living in `MMLib.Alvo` would hand every
  embedded consumer Scalar *and* both database drivers, to run a `Program.cs` none of
  them reference. It also takes `Microsoft.AspNetCore.OpenApi` directly rather than
  transitively, because a package's build targets do not travel through a
  `ProjectReference`. Details in [`host.md`](./host.md).
- `test/` — one `*.Tests` per shipped project (arch + public-API approval
  auto-linked), `MMLib.Alvo.Conventions.Tests` (solution-structure checks),
  `MMLib.Alvo.Api.Tests`, and the `*.Tests.Integration` projects
  (Testcontainers).

Keep this list current — update it whenever a project is added or removed.

## The rule (hard)

A standalone NuGet package is justified only when a component meets **at least one**:

- **(a) Foreign / heavy dependency** — it drags in a dependency most consumers don't
  want: a database driver, the Azure SDK, Roslyn, Blazor, etc.
- **(b) Real swap point** — someone genuinely replaces it: the database engine, a
  secret store, an object store.
- **(c) Different distribution / license policy** — e.g. a commercial
  `Alvo.Enterprise.*` add-on versus the Apache-2.0 core.

Anything else lives as a **namespace / vertical slice inside the core**, not as its
own project. Conceptual neatness is **not** a reason to split.

## Consequence

The core is **one large package** (schema registry, data API, rule engine, events,
auth, rbac, realtime, automation, tenancy, audit, caching, Management API, plus the
in-core default providers as vertical slices). Packages exist only where the rule
above applies — roughly **~10 packages for v0.1, not 30+**. Start conservative:
extracting a namespace into a package later is cheap; merging too many packages back
is a breaking change.

## Illustrative example (non-binding)

- `MMLib.Alvo.Abstractions` (ports, no dependencies) · `MMLib.Alvo` (core + builder)
- data providers as separate packages (each drags a driver): SQLite (dev),
  PostgreSQL, SQL Server
- `MMLib.Alvo.Admin` (Blazor — heavy dep) · `MMLib.Alvo.Host` (Docker) ·
  `MMLib.Alvo.Cli`
- `MMLib.Alvo.Testing` (contract suite + fakes) · `MMLib.Alvo.Templates`
- later, when the feature lands: Scripting (Roslyn), Functions.ContainerApps (Azure),
  Azure/Kubernetes provider bundles, Aspire, client codegen, MCP adapter — each
  justified by a foreign dependency. Concrete provider adapters (SendGrid, S3, …) are
  added **on demand**, not preemptively.

## Hard dependency rules

- `MMLib.Alvo.Abstractions` depends on no other `MMLib.Alvo.*` package and no
  provider. The **one foundational exception** is
  `Microsoft.Extensions.DependencyInjection.Abstractions` — the DI contract the
  whole framework builds on (needed by `IAlvoBuilder.Services`). It is the DI
  *abstraction*, not a concrete container or provider, and taking it keeps the
  clean `Data.* → Abstractions` graph (the alternative — moving the builder into
  the core — would force every provider to reference the whole core). No other
  external dependency may be added to `Abstractions`.
- The core depends only on `Abstractions` **among `MMLib.Alvo.*` packages**, and
  in particular never on a provider (`SharedArchitectureRules.Core_depends_only_on_Abstractions`
  asserts both). Its permitted external dependencies are named here, on the same
  precedent as the `Abstractions` exception above:
  - `FrameworkReference Microsoft.AspNetCore.App`. §0 principle 8 makes every
    generated endpoint a minimal-API delegate, so the core **is** an ASP.NET Core
    library from PR3 on. `Abstractions` deliberately stays free of it — the ports
    must stay implementable by a host that is not an ASP.NET application at all,
    and an arch test holds that line.
  - `Microsoft.AspNetCore.OpenApi`. First-party tooling for a product promise: the
    OpenAPI document *is* the contract an agent reads (§0 principle 4). A docs
    **UI** is a hosting decision, so Scalar sits in `MMLib.Alvo.Host` instead.
  - `Corvus.Json.SourceGenerator` (build-time only, `PrivateAssets=all`) and its
    `Corvus.Json.ExtendedTypes` runtime support.

  The cost, stated: an embedded consumer of the core is an ASP.NET consumer
  whether or not it maps the Data API. That is the price of principle 8, and it is
  why the *ports* were kept free of it — a non-ASP.NET host implements
  `IAlvoData` against `Abstractions` alone.
- **No package depends on another port's provider.**
- Lockstep SemVer: everything is versioned and released together as one version.

## What a database provider must implement to boot

The ports are the contract on paper; what a provider must supply to get a *running* host is
narrower than the port list and wider than it used to be, so it is recorded here rather than
left to be discovered by a third party.

- **`IRuntimeSchemaWriter` is now mandatory, not optional.** It used to be resolved on
  demand and only by `RuntimeSchemaService` — the runtime, dashboard-first apply — so a
  provider could implement `IAppliedSchemaStore` and `ISchemaMigrator` and boot without it.
  The boot now writes every project-schema change through it, because that port inserts the
  version row **first** as the optimistic-lock gate and then runs the DDL in the same
  transaction, which is what makes several replicas cold-starting against one empty database
  converge instead of crash-looping. So a provider without it can no longer boot at all.
  Both in-repo drivers implement it; the cost falls on a future third-party provider, and it
  is a widening of the **implicit** provider contract (startup-lifecycle design deviation 60).
- **Stage 1 — the framework's own `alvo.*` tables — has no port**, deliberately. The system
  schema is owned by whichever driver implements `IAppliedSchemaStore`, and that driver cannot
  answer a single call without it, so the boot's applied-snapshot read *is* stage 1. A port is
  **earned** the moment a driver's system schema grows a table no store call touches — PR5's
  outbox is the first candidate.
- **The two apply paths differ on purpose.** `SchemaMigrationRunner` (the CLI /
  Management-API path) keeps applying the DDL and *then* saving the snapshot: that path is a
  single writer by construction, so the race the boot has to survive cannot arise there, and
  changing it would alter behaviour its tests pin for no benefit. Recorded (deviation 61) so a
  later reader does not take the asymmetry for an oversight — and because if the Management API
  ever serves concurrent applies, that is the line that has to move.
