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

## File Structure

Locked before the tasks, because the decomposition decisions live here.

**New — the Host (`src/MMLib.Alvo.Host/`)**

| File | Responsibility |
|---|---|
| `MMLib.Alvo.Host.csproj` | Web SDK, `IsPackable=false`, references the core + both providers, `Scalar.AspNetCore`. |
| `Program.cs` | Three lines. `AlvoHost.CreateBuilder(args)` → `AlvoHost.Build(...)` → `RunAsync()`. Nothing testable lives here. |
| `AlvoHost.cs` | The public composition seam: `CreateBuilder` (configuration + options + `AddAlvo` + provider selection) and `Build` (apply, path base, exception handler, `MapAlvoDataApi`, health, Scalar). Two short methods, everything else extracted. |
| `AlvoHostOptions.cs` | The `Alvo` configuration section as typed, validated options: `DescriptorPath`, `Database`, `PathBase`, `Docs`. |
| `Internal/AlvoDatabaseSelector.cs` | `sqlite` / `postgresql` → the matching `Use*` call, and a structured refusal for anything else. |
| `Internal/AlvoHostEndpoints.cs` | `MapAlvoLiveness` — the one endpoint the Host owns. |
| `appsettings.json` | The container defaults: descriptor path `/alvo/descriptor.json`, provider `sqlite`, docs on. |

**New — the Host's tests (`test/MMLib.Alvo.Host.Tests/`)**

| File | Responsibility |
|---|---|
| `MMLib.Alvo.Host.Tests.csproj` | References the Host, `Microsoft.AspNetCore.TestHost`; keeps the shared arch + public-API gate **on**. |
| `AlvoHostWorld.cs` | One running Host over `TestServer`, built through `AlvoHost.CreateBuilder`/`Build` — never a hand-rolled `WebApplication`. |
| `AlvoHostBootTests.cs` | Boot facts: a mounted descriptor becomes reachable routes; an unknown entity 404s; a broken descriptor refuses to start; no default credential exists. |
| `AlvoHostLoggingTests.cs` | Deviation 34's cost, made observable: the unhonoured-subsystem warning reaches a configured provider. |
| `AlvoHostProblemDetailsTests.cs` | #119 in the standalone pipeline. |
| `AlvoHostPathBaseTests.cs` | #121 in the standalone pipeline. |
| `AlvoHostDocsTests.cs` | Scalar + the document, served by the Host. |
| `PublicApi.MMLib.Alvo.Host.verified.txt` | The Host's public surface, under approval like every other assembly's. |
| `descriptors/host-boot.alvo.json` | A descriptor with exactly one entity, whose name appears nowhere else in the repo — so "the descriptor drove it" is falsifiable. |

**New — the container and the e2e (repo root and `deploy/`, `tests/teapie/`)**

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Host/Dockerfile` | Multi-stage SDK → `aspnet:10.0-alpine` runtime, non-root, port 8080. |
| `.dockerignore` | Keeps `bin`/`obj`/`.git` out of the build context. |
| `docker-compose.yml` | `alvo` + `postgres:16-alpine`, descriptor mounted at `/alvo/descriptor.json`, healthcheck on `/health/live`. |
| `tests/teapie/env.json` | The `compose` environment (base URL, the dev key). |
| `tests/teapie/**/*.http`, `*-test.csx` | The black-box suite over the running stack. |
| `scripts/test-e2e` | Bring the stack up, run `teapie test`, tear down, always dump logs on failure. |
| `.github/workflows/ci.yml` | The new `e2e` job, wired into the existing `Build & test` required gate. |

**Modified — the core (`src/MMLib.Alvo/`)**

| File | Change |
|---|---|
| `Migrations/AlvoDescriptorApplyExtensions.cs` (new) | The public code-first apply seam a host outside the assembly can call. |
| `Api/AlvoProblemTypes.cs` | `Internal` slug + `All` entry (#119). |
| `Api/AlvoProblemDetailsExtensions.cs` (new) | `AddAlvoProblemDetails()` — the opt-in registration of the handler (#119). |
| `Api/Internal/AlvoExceptionHandler.cs` (new) | Logs, then renders `alvo.dev/errors/internal` (#119). |
| `Api/Internal/ProblemResultFactory.cs` | `Internal(string detail)` entry point (#119). |
| `Api/Internal/DataApiEndpoints.cs:905-924` | `RecordResult` prefixes `HttpRequest.PathBase` onto `Location` (#121). |
| `Api/Internal/AlvoDocumentTransformer.cs` | A `servers` entry carrying the request's path base (#121). |

**Modified — docs**

`docs/architecture/package-boundary.md` (Task 2) · `docs/architecture/host.md` (new, Task 2, extended by 3–7) · `docs/architecture/data-api.md` (Tasks 3, 4 — retire the "known gap, for PR4" notes) · the design doc's *Deviations added by PR4* + `docs/PLAN.md` (Task 8).

---

### Task 1: The core gains the one public seam a host outside its assembly needs to apply a descriptor

`SchemaMigrationRunner` is `internal sealed` and no public surface exposes it — `src/MMLib.Alvo/Properties/AssemblyInfo.cs` says so in as many words: *"it needs two internals that no public surface exposes: SchemaMigrationRunner (the code-first apply…)"*. `MMLib.Alvo.Host` is a **separate assembly** with no `InternalsVisibleTo` grant and must never get one (`AlvoDataSeed`'s remarks explain why an unsigned grant is forgeable). So without this task PR4 cannot exist at all: the Host can register Alvo and map routes, but `MapAlvoDataApi` reads route literals off the *applied* schema and would map nothing.

**Files:**
- Create: `src/MMLib.Alvo/Migrations/AlvoDescriptorApplyExtensions.cs`
- Modify: `test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs` (add two facts; leave the existing three untouched)
- Modify: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt` (baseline moves — the snapshot-judge gate will fire)
- Modify: `docs/architecture/extensibility.md` (rule 4's verb taxonomy gains `Apply{Thing}`)

**Interfaces:**
- Consumes: `internal sealed class MMLib.Alvo.Migrations.SchemaMigrationRunner` with `public async Task<MigrationResult> RunAsync(MigrationOptions options, CancellationToken ct = default)`; registered by `AddAlvo` as `services.TryAddSingleton<SchemaMigrationRunner>()`. `public sealed record MigrationOptions { bool AllowDestructive; bool DryRun; string? Author; string? Reason; }` and `public sealed record MigrationResult(bool Applied, MigrationPlan Plan, bool WasDryRun)`, both in namespace `MMLib.Alvo.Migrations` (declared in `Abstractions`).
- Produces: `public static Task<MigrationResult> Microsoft.Extensions.DependencyInjection.AlvoDescriptorApplyExtensions.ApplyAlvoDescriptorAsync(this IServiceProvider services, MigrationOptions? options = null, CancellationToken ct = default)`. Task 2's `AlvoHost.Build` is its only in-repo production caller.

- [ ] **Step 1: Write the failing facts**

Append to `test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs`, inside the existing class:

```csharp
    /// <summary>
    /// The apply seam a host in another assembly actually has. <c>SchemaMigrationRunner</c> is
    /// <see langword="internal"/> to the core, so <c>MMLib.Alvo.Host</c> cannot resolve it — this extension is
    /// the whole reason a standalone host can bring a descriptor up, and it is asserted through the physical
    /// tables it produced rather than through the result flag alone.
    /// </summary>
    [Fact]
    public async Task The_public_apply_extension_creates_the_descriptors_tables()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo
            .UseSqlite($"Data Source={_databasePath}")
            .FromDescriptor(VehicleRegistryDescriptorPath()));

        using var sp = services.BuildServiceProvider();

        var result = await sp.ApplyAlvoDescriptorAsync(ct: TestContext.Current.CancellationToken);

        result.Applied.ShouldBeTrue("a host that cannot apply maps no route at all");
        result.WasDryRun.ShouldBeFalse();

        var introspected = await sp.GetRequiredService<ISchemaIntrospector>()
            .IntrospectAsync(TestContext.Current.CancellationToken);
        introspected.Entities.Select(entity => entity.Name)
            .ShouldContain("vehicles", "the descriptor's entities must exist as real tables, not merely validate");
    }

    /// <summary>
    /// The options argument reaches the runner. Without this the parameter could be dropped and every
    /// existing fact would stay green, because they all pass the default.
    /// </summary>
    [Fact]
    public async Task The_public_apply_extension_honours_a_dry_run()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo
            .UseSqlite($"Data Source={_databasePath}")
            .FromDescriptor(VehicleRegistryDescriptorPath()));

        using var sp = services.BuildServiceProvider();

        var result = await sp.ApplyAlvoDescriptorAsync(
            new MigrationOptions { DryRun = true }, TestContext.Current.CancellationToken);

        result.WasDryRun.ShouldBeTrue();

        var introspected = await sp.GetRequiredService<ISchemaIntrospector>()
            .IntrospectAsync(TestContext.Current.CancellationToken);
        introspected.Entities.ShouldBeEmpty("a dry run must plan and write nothing");
    }
```

Add the path helper beside the file's existing `DescriptorPath()` helper (read that method first and mirror its `RepositoryRoot.Find()` usage exactly):

```csharp
    private static string VehicleRegistryDescriptorPath() =>
        Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json");
```

> If `AddAlvoIntegrationTests` already resolves the vehicle-registry path in its third fact, reuse that helper instead of adding a second one — two spellings of one path is the duplication this repo's reviews reject.

*Discrimination:* `The_public_apply_extension_creates_the_descriptors_tables` fails if the extension returns `MigrationResult(false, …)`, swallows the call, or resolves a different runner — the introspection assertion cannot be satisfied without real DDL. `The_public_apply_extension_honours_a_dry_run` fails if the `options` parameter is ignored.

- [ ] **Step 2: Run the facts and watch them fail to compile**

```bash
dotnet build MMLib.Alvo.slnx
```

Expected: `CS1061 'IServiceProvider' does not contain a definition for 'ApplyAlvoDescriptorAsync'`.

- [ ] **Step 3: Write the extension**

Create `src/MMLib.Alvo/Migrations/AlvoDescriptorApplyExtensions.cs`:

```csharp
using MMLib.Alvo.Migrations;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The code-first apply, as the one public operation a host performs on a built container.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator itself (<c>SchemaMigrationRunner</c>) is deliberately <see langword="internal"/>: it
/// takes six collaborators, and publishing it would freeze that constructor as a contract. What a host
/// genuinely needs is one verb — <em>bring the configured descriptor up</em> — so that is what is public.
/// </para>
/// <para>
/// <b>Call it before mapping endpoints.</b> <c>MapAlvoDataApi</c> reads entity-name literals off the applied
/// schema, so a host that maps first maps nothing at all. It is also what primes the policy catalog, and an
/// unprimed catalog denies every operation (fail-closed) — see <c>RuntimeSchemaService</c>'s remarks.
/// </para>
/// <para>
/// A new verb in <c>docs/architecture/extensibility.md</c>'s taxonomy: <c>Apply{Thing}</c> is a runtime
/// operation on a built provider, not a registration, so none of <c>Use</c>/<c>Add</c>/<c>Enable</c>/<c>From</c>
/// fits it. It takes <see cref="IServiceProvider"/> rather than <c>IHost</c> so a plain console host, a
/// scope and a <c>WebApplication</c> all reach it through the same member.
/// </para>
/// </remarks>
public static class AlvoDescriptorApplyExtensions
{
    /// <summary>Applies the configured project descriptor, creating or migrating the schema it declares.</summary>
    /// <param name="services">A built service provider Alvo was registered in.</param>
    /// <param name="options">How to apply — destructive changes, dry run, audit provenance. Defaults to <see cref="MigrationOptions"/>'s own defaults.</param>
    /// <param name="ct">Cancels the apply.</param>
    /// <returns>What was planned and whether it was applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Alvo is not registered in <paramref name="services"/>.</exception>
    public static Task<MigrationResult> ApplyAlvoDescriptorAsync(
        this IServiceProvider services,
        MigrationOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetRequiredService<SchemaMigrationRunner>()
            .RunAsync(options ?? new MigrationOptions(), ct);
    }
}
```

- [ ] **Step 4: Run the two new facts and watch them pass**

```bash
dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj --filter-method '*The_public_apply_extension*'
```

Expected: PASS, 2 tests.

- [ ] **Step 5: Accept the public-API baseline move**

```bash
scripts/test-ring0
```

Expected: `PublicApiApprovalTests.Public_api_has_not_changed` FAILS for `MMLib.Alvo` with a diff adding

```text
    public static class AlvoDescriptorApplyExtensions
    {
        public static System.Threading.Tasks.Task<MMLib.Alvo.Migrations.MigrationResult> ApplyAlvoDescriptorAsync(this System.IServiceProvider services, MMLib.Alvo.Migrations.MigrationOptions? options = null, System.Threading.CancellationToken ct = default) { }
    }
```

Copy `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.received.txt` over `PublicApi.MMLib.Alvo.verified.txt`, then re-run `scripts/test-ring0` — green. The Stop hook will require `alvo-snapshot-judge`; the justification to give it is *"the plan's Task 1 adds one deliberate public member; the Host cannot apply a descriptor without it."*

- [ ] **Step 6: Record the new verb**

In `docs/architecture/extensibility.md`, rule 4's list, after the `From{Source}` bullet:

```markdown
   - `Apply{Thing}` — a runtime operation on a built container, not a registration
     (`ApplyAlvoDescriptorAsync` on `IServiceProvider`). Added in PR4 because a host
     outside the core assembly cannot reach the `internal` migration orchestrator, and
     the operation is not a registration so no existing verb fits it.
```

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo/Migrations/AlvoDescriptorApplyExtensions.cs \
        test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt \
        docs/architecture/extensibility.md
git commit -m "feat(core): let a host outside the assembly apply the descriptor"
```
