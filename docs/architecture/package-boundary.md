# Package boundary

> The rule that decides what becomes a separate NuGet package in the
> `MMLib.Alvo.*` family. Source: spec `docs/product/alvo-specifikacia.md` §1.1.
> Counterpart: [`vertical-slice.md`](./vertical-slice.md) decides how code is
> organized *inside* a package — a different axis. Neither justifies the other's
> answer: a vertical slice is never a reason to split a package, and a package
> split is never a reason to organize by technical layer inside one.

## Current projects

- `src/MMLib.Alvo.Abstractions` — interface-first root of the dependency
  graph; currently source-free, waiting for ports (phase 1, spec §1.2).
- `src/MMLib.Alvo.Testing` — test-support library (`ArchTargetAttribute`,
  `RepositoryRoot`); grows into the shipped contract-suite + fakes package.
- `test/MMLib.Alvo.Abstractions.Tests` — xUnit v3 (MTP) tests; holds the
  NetArchTest architecture guard enforcing the rule below.
- `test/MMLib.Alvo.Conventions.Tests` — solution-structure convention tests
  (the file-scanning "os A" checks).

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
- The core depends only on `Abstractions`.
- **No package depends on another port's provider.**
- Lockstep SemVer: everything is versioned and released together as one version.
