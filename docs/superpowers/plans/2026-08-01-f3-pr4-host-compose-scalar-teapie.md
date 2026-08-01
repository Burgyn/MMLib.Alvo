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
- `MMLib.Alvo.Host.AlvoHost.CreateBuilder(string[] args, Action<IConfigurationBuilder>? configureConfiguration = null) → WebApplicationBuilder` and `MMLib.Alvo.Host.AlvoHost.BuildAsync(WebApplicationBuilder builder, CancellationToken ct = default) → Task<WebApplication>` (Host, **public**).
- `MMLib.Alvo.Host.AlvoHostOptions` with `DescriptorPath`, `Database`, `PathBase`, `Docs`; `MMLib.Alvo.Host.AlvoHostDatabaseOptions` with `Provider`, `SqliteConnectionString`; `MMLib.Alvo.Host.AlvoHostDocsOptions` with `Enabled` (Host, **public**, bound from configuration section `Alvo`).
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

---

### Task 2: `MMLib.Alvo.Host` boots a working backend from a mounted descriptor, and refuses to boot without one

The project is **earned** under `package-boundary.md` rule (c) — a different distribution: it is a container image, not a library, and rule (a) applies too once Scalar lands in Task 5. It is **`IsPackable=false`**: it ships as an image (spec §X.1, F4), and packing an entry-point host as a nupkg would publish a surface nobody consumes.

**Files:**
- Create: `src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj`, `Program.cs`, `AlvoHost.cs`, `AlvoHostOptions.cs`, `Internal/AlvoDatabaseSelector.cs`, `Internal/AlvoHostEndpoints.cs`, `appsettings.json`
- Create: `test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj`, `AlvoHostWorld.cs`, `AlvoHostBootTests.cs`, `AlvoHostLoggingTests.cs`, `descriptors/host-boot.alvo.json`, `PublicApi.MMLib.Alvo.Host.verified.txt`
- Create: `docs/architecture/host.md`
- Modify: `MMLib.Alvo.slnx`, `docs/architecture/package-boundary.md`

**Interfaces:**
- Consumes: `IServiceCollection.AddAlvo(Action<IAlvoBuilder>?) → IAlvoBuilder`; `IAlvoBuilder.UseSqlite(string connectionString)`, `IAlvoBuilder.UsePostgreSql(string connectionString)`, `IAlvoBuilder.FromDescriptor(string path)`, `IAlvoBuilder.AddDataApi(Action<AlvoApiOptions>?)`; `IEndpointRouteBuilder.MapAlvoDataApi() → IEndpointRouteBuilder`; `IServiceProvider.ApplyAlvoDescriptorAsync(MigrationOptions?, CancellationToken)` from Task 1; `MMLib.Alvo.Auth.AlvoAuthOptions` (`HeaderName` default `"X-Alvo-Api-Key"`, `TenantHeaderName` default `"X-Alvo-Tenant"`, `IList<AlvoDevApiKey> DevKeys`); `MMLib.Alvo.Api.AlvoApiOptions` (`RoutePrefix` default `"/api"`).
- Produces: `AlvoHost.CreateBuilder(string[] args, Action<IConfigurationBuilder>? configureConfiguration = null) → WebApplicationBuilder`, `AlvoHost.BuildAsync(WebApplicationBuilder builder, CancellationToken ct = default) → Task<WebApplication>`, `AlvoHost.ConfigurationSection = "Alvo"`, `AlvoHost.LivenessPath = "/health/live"`; `AlvoHostOptions { string DescriptorPath; AlvoHostDatabaseOptions Database; string? PathBase; AlvoHostDocsOptions Docs }`; `AlvoHostDatabaseOptions { string Provider; string SqliteConnectionString }`; `AlvoHostDocsOptions { bool Enabled }`. Tasks 3, 4 and 5 each add exactly one call inside `BuildAsync`. The test-side `AlvoHostWorld` (`internal sealed class`, `StartAsync(...)`, `HttpClient Client`, `IReadOnlyList<string> Warnings`) is what Tasks 3–5's facts drive.

- [ ] **Step 1: Create the two projects and register them**

```bash
dotnet new web -o src/MMLib.Alvo.Host -n MMLib.Alvo.Host --no-https
dotnet new classlib -o test/MMLib.Alvo.Host.Tests -n MMLib.Alvo.Host.Tests
rm -f test/MMLib.Alvo.Host.Tests/Class1.cs src/MMLib.Alvo.Host/Properties/launchSettings.json
dotnet sln MMLib.Alvo.slnx add src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj --solution-folder src
dotnet sln MMLib.Alvo.slnx add test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj --solution-folder test
```

Then **replace** both generated `.csproj` files wholesale (the templates emit inherited properties, which `SolutionConventionTests.No_project_redeclares_an_inherited_msbuild_property` rejects).

`src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!--
    The standalone host (spec §2.14 mode 1, §X.1). Not packable: it is distributed as the
    `mmlib/alvo` container image, not as a NuGet package, so a .nupkg of an entry point would
    publish a surface no consumer references. package-boundary.md records the project and the
    rule that earns it.
  -->
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <UserSecretsId>mmlib-alvo-host</UserSecretsId>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../MMLib.Alvo/MMLib.Alvo.csproj" />
    <!-- Both drivers ship in the image: SQLite is the zero-configuration default the deployment
         acceptance criterion names, PostgreSQL is what docker-compose runs. Exactly one is
         *registered*, chosen by configuration in AlvoDatabaseSelector. -->
    <ProjectReference Include="../MMLib.Alvo.Data.Sqlite/MMLib.Alvo.Data.Sqlite.csproj" />
    <ProjectReference Include="../MMLib.Alvo.Data.PostgreSql/MMLib.Alvo.Data.PostgreSql.csproj" />
  </ItemGroup>

</Project>
```

`test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="../../src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj" />
  </ItemGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.TestHost" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="descriptors/*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

> The shared architecture + public-API tests stay **on** here (no `AlvoSharedArchTests=false`): the Host is a real sibling assembly, so `TestTarget.Resolve()` finds it and its small public surface becomes a reviewed baseline — which is the point, since Tasks 3–5 drive it.

- [ ] **Step 2: Write the descriptor the boot facts stand on**

`test/MMLib.Alvo.Host.Tests/descriptors/host-boot.alvo.json` — one entity whose name appears **nowhere else in the repo**, so "the mounted descriptor drove the routes" is falsifiable:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "host-boot",
  "description": "One entity, used only to prove the standalone host maps what the mounted descriptor declares.",
  "auth": {
    "providers": ["local"],
    "roles": ["admin"]
  },
  "entities": {
    "warehouses": {
      "description": "A storage location.",
      "fields": {
        "code": { "type": "string", "required": true, "unique": true, "maxLength": 20 },
        "city": { "type": "string", "maxLength": 60 }
      },
      "rules": {
        "list": "'authenticated' in @user.roles",
        "get": "'authenticated' in @user.roles",
        "create": "'admin' in @user.roles",
        "update": "'admin' in @user.roles",
        "delete": "'admin' in @user.roles"
      }
    }
  }
}
```

- [ ] **Step 3: Write the test world**

`test/MMLib.Alvo.Host.Tests/AlvoHostWorld.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// One running standalone host, started through <see cref="AlvoHost"/>'s own two methods over
/// <see cref="TestServer"/> — never a hand-rolled <c>WebApplication</c>.
/// </summary>
/// <remarks>
/// The composition <em>is</em> the thing under test: a fixture that assembled its own pipeline would go on
/// passing after <see cref="AlvoHost.BuildAsync"/> stopped applying the descriptor, stopped mapping the Data
/// API, or stopped registering the exception handler. Configuration arrives as an in-memory source keyed
/// exactly as the container's environment variables are, so a fact about <c>Alvo:Database:Provider</c> is a
/// fact about <c>Alvo__Database__Provider</c>.
/// </remarks>
internal sealed class AlvoHostWorld : IAsyncDisposable
{
    internal const string AdminKeyId = "host-admin";
    internal const string AdminSecret = "host-admin-secret";
    internal const string ApiKeyHeader = "X-Alvo-Api-Key";

    private readonly WebApplication _app;
    private readonly string _databasePath;

    private AlvoHostWorld(WebApplication app, string databasePath, CapturingLoggerProvider logs)
    {
        _app = app;
        _databasePath = databasePath;
        Logs = logs;
        Client = app.GetTestClient();
    }

    internal HttpClient Client { get; }

    internal CapturingLoggerProvider Logs { get; }

    internal static Task<AlvoHostWorld> StartAsync(
        string descriptorFileName = "host-boot.alvo.json",
        IReadOnlyDictionary<string, string?>? overrides = null) =>
        StartAsync(DescriptorPath(descriptorFileName), overrides);

    internal static async Task<AlvoHostWorld> StartAsync(
        string descriptorPath,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"alvo-host-tests-{Guid.NewGuid():N}.db");
        var logs = new CapturingLoggerProvider();
        var settings = Settings(descriptorPath, databasePath, overrides);

        var builder = AlvoHost.CreateBuilder(
            [], configuration => configuration.AddInMemoryCollection(settings));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.WebHost.UseTestServer();

        var app = await AlvoHost.BuildAsync(builder, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new AlvoHostWorld(app, databasePath, logs);
    }
```

> **Why the callback exists at all.** `AddAlvo`'s `configure` delegate runs **eagerly**, inside `CreateBuilder` — the descriptor path and the driver are therefore read from configuration *there*, before any caller could add a source to `builder.Configuration`. The callback is the seam that lets a container's environment, a test's in-memory collection and a future `alvo` CLI all reach the same composition. It is applied to `builder.Configuration` before `AddAlvo`, and nowhere else.

```csharp
    private static Dictionary<string, string?> Settings(
        string descriptorPath,
        string databasePath,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Alvo:DescriptorPath"] = descriptorPath,
            ["Alvo:Database:Provider"] = "sqlite",
            ["Alvo:Database:SqliteConnectionString"] = $"Data Source={databasePath}",
            ["Alvo:Auth:DevKeys:0:KeyId"] = AdminKeyId,
            ["Alvo:Auth:DevKeys:0:Secret"] = AdminSecret,
            ["Alvo:Auth:DevKeys:0:User"] = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            ["Alvo:Auth:DevKeys:0:Roles:0"] = "admin",
            ["Alvo:Auth:DevKeys:0:Roles:1"] = "authenticated",
            ["Alvo:Auth:DevKeys:0:Scopes:0"] = "*:read",
            ["Alvo:Auth:DevKeys:0:Scopes:1"] = "*:write",
        };

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>(StringComparer.Ordinal))
        {
            settings[key] = value;
        }

        return settings;
    }

    internal static string DescriptorPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "descriptors", fileName);

    internal Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, body: null);

    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, $"{AdminKeyId}.{AdminSecret}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal Task<HttpResponseMessage> SendAnonymouslyAsync(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
        TryDeleteDatabase();
    }

    private void TryDeleteDatabase()
    {
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Every log record the host wrote, so a fact can assert a warning was actually delivered.</summary>
/// <remarks>
/// Deviation 34's stated cost is that "with no logging <em>provider</em> configured the warning is dropped
/// silently". A standalone host configures providers, so that cost is observable here and nowhere else —
/// which is why this is a provider rather than an assertion on <c>ILogger</c> being resolvable.
/// </remarks>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _records = [];

    internal IReadOnlyList<string> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void Record(LogLevel level, string message)
    {
        lock (_records)
        {
            _records.Add($"{level}: {message}");
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Record(logLevel, formatter(state, exception));
        }
    }
}
```

- [ ] **Step 4: Write the failing boot facts**

`test/MMLib.Alvo.Host.Tests/AlvoHostBootTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The standalone host's own definition of done: a mounted descriptor, and nothing else, becomes a
/// working backend.
/// </summary>
public class AlvoHostBootTests
{
    /// <summary>
    /// A row round-trips through the routes the mounted descriptor declared. "The host started" is not this
    /// fact — the create and the read-back are.
    /// </summary>
    [Fact]
    public async Task A_row_round_trips_through_the_entity_the_mounted_descriptor_declares()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var created = await world.SendAsync(
            HttpMethod.Post, "/api/warehouses", new JsonObject { ["code"] = "W-1", ["city"] = "Košice" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var location = created.Headers.Location!.ToString();

        using var read = await world.GetAsync(location);

        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        body["code"]!.GetValue<string>().ShouldBe("W-1");
    }

    /// <summary>
    /// The non-vacuity control for the fact above: the host maps the descriptor's entities and only those, so
    /// a name it does not declare has no route. Without this, a host that mapped a catch-all would pass.
    /// </summary>
    [Fact]
    public async Task An_entity_the_descriptor_does_not_declare_has_no_route()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var response = await world.GetAsync("/api/pallets");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The host does not listen until the descriptor applied, so a bad descriptor is a failed start rather
    /// than a running backend with no tables. This is also what makes the container's liveness probe
    /// meaningful: answering at all proves the apply succeeded.
    /// </summary>
    [Fact]
    public async Task A_descriptor_that_cannot_apply_stops_the_host_from_starting()
    {
        var missing = AlvoHostWorld.DescriptorPath("no-such-descriptor.alvo.json");

        var failure = await Should.ThrowAsync<Exception>(
            () => AlvoHostWorld.StartAsync(missing, overrides: null));

        failure.ShouldNotBeOfType<ShouldAssertException>();
    }

    /// <summary>
    /// §2.14's acceptance criterion — "image nikdy nedodáva prednastavené prihlásenie" — as a fact: a host
    /// with no configured credential exposes no way in. An anonymous caller is a context, not a 401
    /// (deviation 23), so the descriptor's own default-deny answers 403.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_configured_key_grants_nobody_anything()
    {
        await using var world = await AlvoHostWorld.StartAsync(overrides: NoDevKeys());

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, "/api/warehouses");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>Liveness answers without a credential — a probe cannot present one.</summary>
    [Fact]
    public async Task Liveness_answers_an_unauthenticated_probe()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, "/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>An unknown provider name is refused by name, with the two that exist listed.</summary>
    [Fact]
    public async Task An_unknown_database_provider_is_refused_with_the_choices_named()
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Alvo:Database:Provider"] = "cosmos",
        };

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => AlvoHostWorld.StartAsync(overrides: overrides));

        failure.Message.ShouldContain("cosmos");
        failure.Message.ShouldContain("sqlite");
        failure.Message.ShouldContain("postgresql");
    }

    private static Dictionary<string, string?> NoDevKeys() =>
        new(StringComparer.Ordinal)
        {
            ["Alvo:Auth:DevKeys:0:KeyId"] = null,
            ["Alvo:Auth:DevKeys:0:Secret"] = null,
            ["Alvo:Auth:DevKeys:0:User"] = null,
            ["Alvo:Auth:DevKeys:0:Roles:0"] = null,
            ["Alvo:Auth:DevKeys:0:Roles:1"] = null,
            ["Alvo:Auth:DevKeys:0:Scopes:0"] = null,
            ["Alvo:Auth:DevKeys:0:Scopes:1"] = null,
        };
}
```

`test/MMLib.Alvo.Host.Tests/AlvoHostLoggingTests.cs`:

```csharp
namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// Deviation 34's cost, made observable. <c>AddAlvo()</c> calls <c>AddLogging()</c> so the core can write its
/// declared-but-unhonoured-subsystems warning, and the deviation states plainly that "with no logging
/// <em>provider</em> configured the warning is dropped silently". A standalone host configures providers, so
/// this is the first place the warning can be shown to actually arrive.
/// </summary>
public class AlvoHostLoggingTests
{
    [Fact]
    public async Task The_unhonoured_subsystem_warning_reaches_the_hosts_logging_provider()
    {
        var descriptor = AlvoHostWorld.DescriptorPath("host-boot-with-webhooks.alvo.json");

        await using var world = await AlvoHostWorld.StartAsync(descriptor, overrides: null);

        world.Logs.Records.ShouldContain(
            record => record.StartsWith("Warning: ", StringComparison.Ordinal)
                && record.Contains("webhooks", StringComparison.Ordinal),
            "an operator who declares a subsystem Alvo does not honour must be told, and a dropped warning "
            + "is indistinguishable from an honoured subsystem");
    }
}
```

Add the second descriptor, `test/MMLib.Alvo.Host.Tests/descriptors/host-boot-with-webhooks.alvo.json`: a byte-for-byte copy of `host-boot.alvo.json` with `"name": "host-boot-webhooks"` and one extra top-level block, whose exact shape you must copy from `schema/project.schema.json`'s `webhooks` definition (read it — do not guess the member names). Before writing it, confirm the warning's wording:

```bash
grep -rn "UnhonouredSubsystems\|unhonoured" src/MMLib.Alvo/ | head
```

and match `record.Contains(...)` to the substring that code actually writes.

*Discrimination, fact by fact:*
- `A_row_round_trips_…` fails if `BuildAsync` skips `ApplyAlvoDescriptorAsync` (no table → 500/404), skips `MapAlvoDataApi` (404), or if auth is misbound (403). Mutation: delete either call.
- `An_entity_the_descriptor_does_not_declare_has_no_route` fails if a future change maps a catch-all route. Mutation: map `/api/{entity}` generically.
- `A_descriptor_that_cannot_apply_stops_the_host_from_starting` fails if the apply is wrapped in a `try`/`catch` that logs and continues — which is the tempting "resilient startup" mistake, and the one that would make the container report healthy with no schema.
- `A_host_with_no_configured_key_grants_nobody_anything` fails if the Host ever seeds a default key, or if a missing credential were treated as an implicit admin.
- `An_unknown_database_provider_is_refused_…` fails if the selector silently falls back to SQLite — the failure mode that would have a compose stack quietly ignore PostgreSQL and write to a container-local file.
- `The_unhonoured_subsystem_warning_reaches_…` fails if the Host clears providers without adding one, or if the core's warning is emitted before any provider is attached.

- [ ] **Step 5: Run the facts and watch them fail**

```bash
dotnet build MMLib.Alvo.slnx
```

Expected: `CS0103 The name 'AlvoHost' does not exist` (and friends) — nothing in `src/MMLib.Alvo.Host` exists yet beyond the template's `Program.cs`.

- [ ] **Step 6: Write the options**

`src/MMLib.Alvo.Host/AlvoHostOptions.cs`:

```csharp
namespace MMLib.Alvo.Host;

/// <summary>The standalone host's own configuration, bound from the <c>Alvo</c> section.</summary>
/// <remarks>
/// Distinct from <c>AlvoOptions</c> and <c>AlvoApiOptions</c> on purpose: those are the framework's options,
/// which an embedded host configures too. These are decisions only a <em>container</em> makes — where the
/// mounted descriptor is, which driver to register, what path base it is served under, whether to serve a
/// docs UI. Bound from <c>Alvo:*</c>, so the container's environment form is the standard .NET
/// <c>Alvo__DescriptorPath</c> / <c>Alvo__Database__Provider</c>.
/// </remarks>
public sealed class AlvoHostOptions
{
    /// <summary>Gets or sets the project descriptor's path (default <c>/alvo/descriptor.json</c>, the image's mount point).</summary>
    public string DescriptorPath { get; set; } = "/alvo/descriptor.json";

    /// <summary>Gets or sets which database driver to register and how to reach it.</summary>
    public AlvoHostDatabaseOptions Database { get; set; } = new();

    /// <summary>Gets or sets the path base the host is served under, for a deployment behind a reverse proxy that does not rewrite (default none).</summary>
    public string? PathBase { get; set; }

    /// <summary>Gets or sets whether the interactive API documentation is served.</summary>
    public AlvoHostDocsOptions Docs { get; set; } = new();
}

/// <summary>Which database the standalone host registers, and how to reach it.</summary>
public sealed class AlvoHostDatabaseOptions
{
    /// <summary>The SQLite driver's configuration value.</summary>
    public const string Sqlite = "sqlite";

    /// <summary>The PostgreSQL driver's configuration value.</summary>
    public const string PostgreSql = "postgresql";

    /// <summary>Gets or sets the driver to register — <see cref="Sqlite"/> or <see cref="PostgreSql"/>.</summary>
    /// <remarks>
    /// SQLite is the default because the deployment acceptance criterion is a working backend with
    /// <em>no</em> configuration at all; PostgreSQL is what <c>docker-compose.yml</c> selects.
    /// </remarks>
    public string Provider { get; set; } = Sqlite;

    /// <summary>
    /// Gets or sets the connection string used when <see cref="Provider"/> is <see cref="Sqlite"/> and
    /// <c>ConnectionStrings:Alvo</c> is not set.
    /// </summary>
    /// <remarks>
    /// The one place a database location is defaulted, and only for SQLite: a PostgreSQL host with no
    /// connection string must fail rather than silently write to a container-local file that vanishes with
    /// the container.
    /// </remarks>
    public string SqliteConnectionString { get; set; } = "Data Source=/alvo/data/alvo.db";
}

/// <summary>Whether the standalone host serves interactive API documentation.</summary>
public sealed class AlvoHostDocsOptions
{
    /// <summary>Gets or sets whether the docs UI and the OpenAPI document are served (default <see langword="true"/>).</summary>
    /// <remarks>
    /// On by default because the document <em>is</em> the contract an agent reads (§0 principle 4), and
    /// because the design already commits to the declared, non-hidden schema shape being public
    /// (deviation 27). A deployment that disagrees turns it off with one setting.
    /// </remarks>
    public bool Enabled { get; set; } = true;
}
```

- [ ] **Step 7: Write the driver selection**

`src/MMLib.Alvo.Host/Internal/AlvoDatabaseSelector.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Host.Internal;

/// <summary>Registers exactly one database driver, named by configuration.</summary>
internal static class AlvoDatabaseSelector
{
    private const string ConnectionName = "Alvo";

    internal static void Select(IAlvoBuilder builder, AlvoHostDatabaseOptions database, IConfiguration configuration)
    {
        if (Is(database.Provider, AlvoHostDatabaseOptions.Sqlite))
        {
            builder.UseSqlite(ConnectionString(configuration) ?? database.SqliteConnectionString);
            return;
        }

        if (Is(database.Provider, AlvoHostDatabaseOptions.PostgreSql))
        {
            builder.UsePostgreSql(configuration);
            return;
        }

        throw new InvalidOperationException(UnknownProviderMessage(database.Provider));
    }

    private static bool Is(string configured, string known) =>
        string.Equals(configured, known, StringComparison.OrdinalIgnoreCase);

    private static string? ConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionName) is { Length: > 0 } configured ? configured : null;

    private static string UnknownProviderMessage(string configured) =>
        $"'{configured}' is not a database provider this host can register. Set Alvo:Database:Provider "
        + $"(env Alvo__Database__Provider) to '{AlvoHostDatabaseOptions.Sqlite}' or "
        + $"'{AlvoHostDatabaseOptions.PostgreSql}'.";
}
```

- [ ] **Step 8: Write the liveness endpoint**

`src/MMLib.Alvo.Host/Internal/AlvoHostEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace MMLib.Alvo.Host.Internal;

/// <summary>The endpoints the host itself owns, as opposed to the ones the descriptor generates.</summary>
internal static class AlvoHostEndpoints
{
    /// <summary>
    /// Maps liveness. Unauthenticated by construction — a container probe presents no credential, and only
    /// <c>MapAlvoDataApi</c>'s endpoints carry the API-key filter.
    /// </summary>
    /// <remarks>
    /// <b>Answering at all proves the descriptor applied</b>, because <see cref="AlvoHost.BuildAsync"/>
    /// applies before the server ever listens: a host whose apply failed never reaches this route. That is
    /// what lets <c>docker compose up --wait</c> mean "the backend is up", not "a process is running".
    /// Readiness with database / cache / bus reachability (§2.12) is F4's — see <c>docs/architecture/host.md</c>.
    /// </remarks>
    internal static void MapAlvoLiveness(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHealthChecks(AlvoHost.LivenessPath);
}
```

- [ ] **Step 9: Write the composition**

`src/MMLib.Alvo.Host/AlvoHost.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Host.Internal;

namespace MMLib.Alvo.Host;

/// <summary>
/// The standalone host's composition, as two methods so a test can start the real pipeline over a
/// <c>TestServer</c> instead of re-assembling an approximation of it.
/// </summary>
/// <remarks>
/// <c>Program.cs</c> is deliberately three lines: everything worth a test lives here.
/// <see cref="CreateBuilder"/> registers, <see cref="BuildAsync"/> applies and maps — the two seams
/// <c>docs/architecture/extensibility.md</c> rule 10 keeps orthogonal, in the one order that works
/// (<c>MapAlvoDataApi</c> reads route literals off the applied schema).
/// </remarks>
public static class AlvoHost
{
    /// <summary>The configuration section the host's own options are bound from.</summary>
    public const string ConfigurationSection = "Alvo";

    /// <summary>The route a container's liveness probe calls.</summary>
    public const string LivenessPath = "/health/live";

    private const string AuthSection = $"{ConfigurationSection}:Auth";
    private const string ApiSection = $"{ConfigurationSection}:Api";

    /// <summary>
    /// Registers everything the standalone host needs.
    /// </summary>
    /// <remarks>
    /// <paramref name="configureConfiguration"/> runs <em>before</em> Alvo is registered, because
    /// <c>AddAlvo</c>'s callback is eager: the descriptor path and the driver are read here, so a caller with
    /// its own configuration source has to contribute it before that read. A container passes nothing (the
    /// environment is already a source); a test passes its own collection.
    /// </remarks>
    /// <param name="args">The process arguments, bound as a configuration source by ASP.NET Core.</param>
    /// <param name="configureConfiguration">Adds configuration sources before Alvo is registered.</param>
    /// <returns>The builder, for a caller that wants to add logging or a test server.</returns>
    public static WebApplicationBuilder CreateBuilder(
        string[] args, Action<IConfigurationBuilder>? configureConfiguration = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configureConfiguration?.Invoke(builder.Configuration);

        builder.Services.Configure<AlvoHostOptions>(builder.Configuration.GetSection(ConfigurationSection));
        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection(AuthSection));
        builder.Services.AddHealthChecks();
        builder.Services.AddAlvo(alvo => Configure(alvo, builder.Configuration));

        return builder;
    }

    /// <summary>
    /// Builds the application, applies the mounted descriptor, and maps the generated Data API.
    /// </summary>
    /// <param name="builder">The builder <see cref="CreateBuilder"/> returned.</param>
    /// <param name="ct">Cancels the descriptor apply.</param>
    /// <returns>The started-but-not-yet-running application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static async Task<WebApplication> BuildAsync(
        WebApplicationBuilder builder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = builder.Build();
        app.MapAlvoLiveness();

        await app.Services.ApplyAlvoDescriptorAsync(ct: ct).ConfigureAwait(false);

        app.MapAlvoDataApi();
        return app;
    }

    private static void Configure(IAlvoBuilder alvo, IConfiguration configuration)
    {
        var options = HostOptions(configuration);
        AlvoDatabaseSelector.Select(alvo, options.Database, configuration);
        alvo.FromDescriptor(options.DescriptorPath)
            .AddDataApi(api => configuration.GetSection(ApiSection).Bind(api));
    }

    private static AlvoHostOptions HostOptions(IConfiguration configuration) =>
        configuration.GetSection(ConfigurationSection).Get<AlvoHostOptions>() ?? new AlvoHostOptions();
}
```

> `HostOptions` reads the section a second time, beside the `Configure<AlvoHostOptions>` registration, and that is deliberate rather than an oversight: the driver has to be chosen while the container is still being *built*, and `IOptions<T>` is only resolvable after. The registration exists for `BuildAsync` and for Tasks 4–5, which read the same options from the built container. One binder, one section, two moments — not two spellings.

`src/MMLib.Alvo.Host/Program.cs` (replace the template's contents entirely):

```csharp
using MMLib.Alvo.Host;

var app = await AlvoHost.BuildAsync(AlvoHost.CreateBuilder(args));
await app.RunAsync();
```

`src/MMLib.Alvo.Host/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Alvo": {
    "DescriptorPath": "/alvo/descriptor.json",
    "Database": {
      "Provider": "sqlite",
      "SqliteConnectionString": "Data Source=/alvo/data/alvo.db"
    },
    "Docs": {
      "Enabled": true
    }
  }
}
```

- [ ] **Step 10: Run the facts and watch them pass**

```bash
dotnet test --project test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj
```

Expected: PASS. `PublicApiApprovalTests.Public_api_has_not_changed` fails first with a `received` file — copy it to `test/MMLib.Alvo.Host.Tests/PublicApi.MMLib.Alvo.Host.verified.txt` and re-run. The Stop hook will ask for `alvo-snapshot-judge`; the justification is *"Task 2 introduces the Host's public composition surface; this is its first baseline."*

- [ ] **Step 11: Run ring1**

```bash
scripts/test-ring1
```

Expected: green. If ring0 reports a module-count mismatch, `MMLib.Alvo.Host.Tests` is missing from `MMLib.Alvo.slnx` — fix that, not the script.

- [ ] **Step 12: Record the project and start the host's architecture note**

In `docs/architecture/package-boundary.md`, under *Current projects*, after the `MMLib.Alvo.Testing.EntityFrameworkCore` bullet:

```markdown
- `src/MMLib.Alvo.Host` — the standalone host (spec §2.14 mode 1): a `WebApplication`
  that turns a mounted project descriptor into a running backend, plus Scalar as its
  docs UI. **Earned by rule (c)** — a different distribution: it ships as the
  `mmlib/alvo` container image, not as a NuGet package, so it is
  `IsPackable=false`. Rule (a) applies to its Scalar dependency as well: a docs UI is
  a hosting decision, and most embedded consumers do not want the package. It is the
  only project allowed to reference more than one `MMLib.Alvo.Data.*` provider — it
  ships both drivers and registers exactly one, chosen by configuration. Details in
  [`host.md`](./host.md).
```

Create `docs/architecture/host.md` with the sections Tasks 3–7 will extend:

```markdown
# The standalone host

> The surviving detailed record for `MMLib.Alvo.Host`, in the same role
> `data-path.md` plays for the port and `data-api.md` for the HTTP layer. PR4's
> Superpowers plan is discarded once merged; what outlives it is here, and the
> deviations it introduced are in the F3 design doc's *Deviations added by PR4*.

## What the host is, and is not

It is a `WebApplication` over the core's public seams and nothing more: configuration
binding, one driver registration, the code-first apply, `MapAlvoDataApi`, liveness, and
a docs UI. It is **not** the full standalone story — the dashboard, the Management API,
the CLI and the published image are #24's remainder, in F4.

## The order in `BuildAsync` is load-bearing

`MapAlvoDataApi` reads entity-name **literals** off the applied schema, so the apply must
precede the mapping or the host maps nothing at all. The apply also primes the policy
catalog, and an unprimed catalog denies every operation. Liveness is mapped before the
apply so the route exists on the endpoint table either way, but the server does not listen
until `RunAsync`, which is *after* `BuildAsync` returned — so **answering liveness proves
the descriptor applied**. A host whose apply throws never listens, and the container exits
non-zero. That is deliberate: a container reporting healthy with no schema is worse than
one that fails to start.

## Configuration

The framework's options (`AlvoOptions`, `AlvoApiOptions`, `AlvoAuthOptions`) are bound from
`Alvo:*`, `Alvo:Api:*` and `Alvo:Auth:*`; the host's own decisions live in
`AlvoHostOptions` (`Alvo:DescriptorPath`, `Alvo:Database:*`, `Alvo:PathBase`, `Alvo:Docs:*`).
The container form is the standard .NET double-underscore spelling
(`Alvo__Database__Provider`), not the `ALVO_*` names spec §X.1 sketches — see the design's
*Deviations added by PR4*.

**No default credential.** §2.14's acceptance criterion is that the image never ships a
preset login, so the host seeds no API key. A host with none configured still starts and
still refuses every operation, because an anonymous caller is judged by the same
default-deny policy as any other (deviation 23).

## Health

Liveness only (`/health/live`). §2.12 asks for readiness with database, cache and message-bus
reachability; none of those probes exists as a port today, and inventing one is a port
widening PR4 has no mandate for. Recorded as a deviation with an issue rather than
approximated.
```

- [ ] **Step 13: Commit**

```bash
git add src/MMLib.Alvo.Host test/MMLib.Alvo.Host.Tests MMLib.Alvo.slnx \
        docs/architecture/package-boundary.md docs/architecture/host.md
git commit -m "feat(host): boot a backend from a mounted descriptor, or refuse to start"
```
