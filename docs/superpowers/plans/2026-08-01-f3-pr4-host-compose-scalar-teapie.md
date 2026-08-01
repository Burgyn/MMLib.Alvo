# F3 PR4 — `MMLib.Alvo.Host` + docker-compose + Scalar + TeaPie Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the standalone host — one new project `MMLib.Alvo.Host` — so that `docker compose up` yields a working backend from the descriptor alone, `teapie test` is green against it in CI, and Scalar renders the OpenAPI document; closing **#19** and **#75** and starting (not closing) **#24**.

**Architecture:** The Host is a thin ASP.NET Core entry-point over the core's existing public seams (`AddAlvo` → `UseSqlite`/`UsePostgreSql` → `FromDescriptor` → `AddDataApi`, then `MapAlvoDataApi`). Everything it needs must therefore *be* public: the code-first apply currently lives on the `internal SchemaMigrationRunner`, so Task 1 adds the one public seam (`IServiceProvider.ApplyAlvoDescriptorAsync`) that a host outside the core assembly can call. The Host adds only what is genuinely a *hosting* decision: configuration binding, provider selection, a liveness endpoint, forwarded headers / path base, Scalar as the docs UI, and — per **#119** — the exception handler that renders Alvo's own `internal` problem type instead of ASP.NET's RFC 9110 status-code URI. Two core defects that only bite a standalone host (**#119**, **#121**) are fixed here, in the core, where the behaviour lives; the Host is what makes them observable. `docker compose` runs the Host against real PostgreSQL, and TeaPie drives it as a black box from a new CI job.

**Tech Stack:** .NET 10 (`net10.0`, SDK pinned in `global.json`), ASP.NET Core minimal APIs, `Microsoft.AspNetCore.OpenApi` 10.0.10 (already in the core), **`Scalar.AspNetCore` 2.16.17** (MIT — new, Host-only), `Microsoft.AspNetCore.TestHost` 10.0.10 (test-only, already in CPM), xUnit v3 + Shouldly + Verify (MTP, not VSTest), Docker + Docker Compose (`postgres:16-alpine`), **TeaPie.Tool 1.7.0** (already a local dotnet tool).

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from the sources named.

**Sources of truth, in precedence order**

- `docs/superpowers/specs/2026-07-25-f3-crud-vertical-slice-design.md` — the approved design. **Do not contradict its Assumptions or Deviations 19–34.** Its PR-split row for PR4 is: `MMLib.Alvo.Host + docker-compose + Scalar + TeaPie` → closes `#19, [15b]` (= #75).
- `docs/architecture/data-api.md` — PR3's surviving detailed record for the HTTP layer. `docs/architecture/data-path.md` — the same for the port.
- `docs/architecture/package-boundary.md` — "A standalone NuGet package is justified only when a component meets **at least one**: (a) Foreign / heavy dependency … (b) Real swap point … (c) Different distribution / license policy". It also says: "`docs/architecture/package-boundary.md` is updated with `MMLib.Alvo.Host` when PR4 lands (the doc asks to be kept current)."
- `docs/architecture/extensibility.md` — rule 1 (extension classes live in namespace `Microsoft.Extensions.DependencyInjection`), rule 4 (fixed verb taxonomy), rule 10 (endpoints are a separate seam), rule 11 (the builder surface is under public-API approval).

**PR4's Definition of Done, verbatim from the design's *Verification* section**

> **PR4:** `docker compose up` yields a working backend from the descriptor alone; `teapie test` is green against it; Scalar renders the document.

**#19's DoD clause PR4 owns** (the rest was delivered by PR3): "TeaPie tests against the docker-compose demo."

**#75's DoD clauses PR4 owns** (the rest was delivered by PR3): "**Scalar renders it** from `MMLib.Alvo.Host`, reachable in the docker-compose demo." and "`Abstractions` gains no ASP.NET dependency; the arch test stays green."

**#24 is *started*, not closed.** From the design: "PR4 starts #24 but does not close it — the published image and the full standalone story stay in F4." Do **not** publish an image, add a dashboard, a Management API, a CLI, MinIO or MailHog.

**Numeric acceptance criteria** (lifted, not invented)

- `baas-analyza.md` §2.14 acceptance: "`docker run mmlib/alvo` = funkčný backend s dashboardom **do 60 s** bez akejkoľvek konfigurácie (SQLite)". PR4's compose form of it: `docker compose up --wait` must reach healthy within **60 s** (`--wait-timeout 60`).
- `alvo-specifikacia.md` §X.1: the container's published port is **8080** (`docker run -p 8080:8080`).
- `alvo-specifikacia.md` §X.1: the descriptor mount point is **`/alvo/descriptor.json`** (`-v ./projekt.alvo.json:/alvo/descriptor.json`).
- `alvo-specifikacia.md` line 418, the maintainer's standing instruction: "**TeaPie v pipeline:** po build+spustení demo image krok `teapie test` proti bežiacemu kontajneru (docker-compose alebo Aspire), **JUnit XML report do CI**; e2e smoke gate pred publikovaním Docker image."
- `baas-analyza.md` §2.14 acceptance: "**Admin bootstrap bez default credentials**: heslo cez env/secret alebo first-run wizard; **image nikdy nedodáva prednastavené prihlásenie**." → the Host must ship **no** default API key.
- `baas-analyza.md` §2.12: "**Health & SLO:** liveness + readiness (DB, cache, message bus reachability)". PR4 ships **liveness only** — see *Deviations anticipated*, D6.

**Exact identifiers this plan introduces or moves** (use these spellings, nothing else)

- `Microsoft.Extensions.DependencyInjection.AlvoDescriptorApplyExtensions.ApplyAlvoDescriptorAsync(this IServiceProvider services, MigrationOptions? options = null, CancellationToken ct = default) → Task<MigrationResult>` (core, **public**).
- `MMLib.Alvo.Host.AlvoHost.CreateBuilder(string[] args) → WebApplicationBuilder` and `MMLib.Alvo.Host.AlvoHost.Build(WebApplicationBuilder builder) → WebApplication` (Host, **public**).
- `MMLib.Alvo.Host.AlvoHostOptions` with `DescriptorPath`, `Database`, `PathBase`, `Docs` (Host, **public**, bound from configuration section `Alvo`).
- `MMLib.Alvo.Api.AlvoProblemTypes.Internal = "internal"` (core, **public**, added to `All`).
- `Microsoft.Extensions.DependencyInjection.AlvoProblemDetailsExtensions.AddAlvoProblemDetails(this IServiceCollection services) → IServiceCollection` (core, **public**, opt-in).
- Configuration keys (standard .NET `Section__Key` env binding): `Alvo__DescriptorPath`, `Alvo__Database__Provider` (`sqlite` | `postgresql`), `Alvo__PathBase`, `Alvo__Docs__Enabled`, `ConnectionStrings__Alvo`, `Alvo__Auth__DevKeys__0__*`, `Alvo__Api__*`.

**Repo rules that bind every task**

- **Central Package Management only.** Versions live in `Directory.Packages.props`; a `PackageReference` in a `.csproj` carries **no** `Version`/`VersionOverride` attribute (`SolutionConventionTests.No_project_pins_an_inline_package_version`).
- **Do not redeclare** `TargetFramework`, `TargetFrameworks`, `Nullable`, `ImplicitUsings`, `LangVersion` in a new `.csproj` (`SolutionConventionTests.No_project_redeclares_an_inherited_msbuild_property`).
- **Register every new project in `MMLib.Alvo.slnx`** under the matching `/src/` or `/test/` folder (`SolutionConventionTests.Every_project_is_registered_in_the_solution`).
- **License policy is Apache-2.0-compatible only.** `Scalar.AspNetCore` is **MIT** — verify with `dotnet package search Scalar.AspNetCore --exact-match` before adding.
- **Code style** (`alvo-dotnet-conventions`): default to **zero inline comments** — rename or extract instead; a comment survives only for non-obvious rationale a name cannot carry. **~25-line ceiling per method**; extract by default. English only. **XML doc comments are required on every public member** of a shipped library project. Assertions are **Shouldly**, never FluentAssertions.
- **`dotnet format --verify-no-changes` must pass** — CI runs it as a gate after Build.
- **Rings:** `scripts/test-ring0` after every small step, `scripts/test-ring1` after a slice, `scripts/test-ring2` before the PR. **ring0 must stay Docker-free.** ring0 counts fast test modules against the projects registered in `MMLib.Alvo.slnx`, so a new `*.Tests` project must be in the solution or ring0 fails with a module-count mismatch.
- **Never push or merge to `main`.** Branch `f3/pr4-host` → PR → a human merges.
- **The `alvo-snapshot-judge` turn gate will fire** whenever a `*.verified.*` baseline moves (Tasks 1, 3, 4 move one). That is expected; dispatch the judge when the Stop hook says so.
- **Before opening the PR:** `/code-review medium`, then `alvo-plan-guard`. `/security-review` is **not** required — PR4 touches the security core only through Task 4's URL construction; run it anyway if Task 3 or 4 grows beyond what is written here.

**Test discrimination rule (Alvo-specific, enforced in review)**

Every test must be able to fail for the reason its name claims. PR3 rejected thirteen-plus tests that could not. Each task below states, per significant fact, **what mutation would prove it discriminates**. Two forms are explicitly banned in this PR:

- "a container started" is **not** a test that the backend works — assert a row round-trips through the API.
- "compose came up" is **not** a test that the descriptor drove it — assert an entity that exists **only** in the mounted descriptor is reachable, and that a name absent from it 404s.

---
