# Management API (#212) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close #212 — ship the Management API as a vertical slice inside `MMLib.Alvo` (`src/MMLib.Alvo/Management/`): one operation contract `IAlvoManagement` in Abstractions, one implementation over the services that already exist, and minimal-API delegates over that same implementation, so the dashboard (in-process) and an agent/CLI/MCP (over HTTP) are two transports on one path.

**Architecture:** `IAlvoManagement` (Abstractions) is the single operation surface. `AlvoManagementService` (core, `internal`) implements it over `RuntimeSchemaService`, `IDescriptorVersionStore`, `ISchemaRegistry`, `IPolicyEngine` and `AlvoBootState` — it opens no connection and duplicates no logic. `ManagementEndpoints` maps one minimal-API delegate per interface member under `Alvo:Management:RoutePrefix` (default `/management`), each carrying a `ManagementOperation` metadata marker naming the interface member it stands for; a contract test enumerates the live endpoint table and requires that set to equal `typeof(IAlvoManagement)`'s methods. Nothing is reachable without an explicit grant: every route carries a filter that resolves the caller and then asks `IManagementAccessPolicy`, whose only implementation here returns `None` for everybody.

**Tech Stack:** .NET 10 (`net10.0`), minimal API + `RouteGroupBuilder`, xUnit v3 4.0.0 on Microsoft.Testing.Platform, Shouldly 4.3.0, NSubstitute 6.2.0, Verify.XunitV3 32.0.0, PublicApiGenerator 11.5.4, SQLite (`MMLib.Alvo.Data.Sqlite`) for the HTTP facts.

**Spec:** `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` (§1.2, §2, §6.1; deviations D2, D3, D4 are this plan's)

## Global Constraints

- **Branch, never `main`.** Work on `feat/212-management-api`. Branch → PR → a human merges.
- **Not a package.** `package-boundary.md` §Consequence lists "Management API" by name among what lives in the one large core package. Create no new `.csproj` in `src/`. Spec §1.1.
- **Configuration is spelled `Alvo:*`, not `ALVO_*`** — deviation **D2**. The section is `Alvo:Management`, the environment spelling `Alvo__Management__RoutePrefix`, matching `AlvoSchemaOptions.SectionName` (`Alvo:Schema`) and `AlvoEventOptions.SectionName` (`Alvo:Events`). #233 owns the vocabulary question globally; follow what exists.
- **`If-Match` carries `revision`, not an `ETag` over a row version** — deviation **D3**. `schema/project.schema.json:49` already froze `revision` as "used for optimistic concurrency during apply". Same header, different source. The Data API's `RowVersionETag` is not reused and not referenced.
- **No admin bypass** — deviation **D4**. Nothing in this plan reads or writes entity rows. The Management API has no data surface at all; the dashboard browses data through `/api/*` under the caller's own context (spec §2.4).
- **RFC 9457 problem documents, one catalogue.** Every refusal is minted through `MMLib.Alvo.Api.Internal.ProblemResultFactory` and carries a slug from `AlvoProblemTypes`. Two slugs are *added* to that catalogue (Task 10); no second catalogue is started, and `AlvoProblemTypes.All` stays the single authority `ProblemDetailsTests` asserts against.
- **`UnhonouredSubsystems.All` is not touched.** Its `access` entry stays, and its consequence text stays as written: this plan honours no `access` level. Spec §3.6 assigns that deletion to #146. Removing it here would delete a warning that is still true.
- **Out of scope, do not build:** authorization *by* `access` level (#146 — only the named seam `IManagementAccessPolicy` is created here), identity / `IAlvoUserStore` / bootstrap admin (#248), any UI or Blazor, the CLI (#213), the MCP adapter, multi-project management (spec §2.6 — `GET {m}/projects` returns one).
- **Central Package Management.** Every version lives in `Directory.Packages.props`; `PackageReference` carries no `Version`. This plan adds no new package.
- **Shouldly for every assertion. FluentAssertions is banned** (licence). NSubstitute for fakes, Verify for snapshots, CsCheck for properties.
- **C# files are written with CRLF + BOM** — the `dotnet format` pre-commit gate rejects otherwise. Write them with the Write tool, not with bash heredocs.
- **Methods stay short and single-purpose — roughly a 25-line ceiling.** Extract by default; a named private method replaces the comment you would otherwise write.
- **`public` is the contract.** Every symbol added to `MMLib.Alvo.Abstractions` or `MMLib.Alvo` that lands in `PublicApi.*.verified.txt` must be a thing `MMLib.Alvo.Admin`, a CLI or an embedded host genuinely calls. Everything else is `internal`; both assemblies already grant `InternalsVisibleTo` to the test projects used here (`MMLib.Alvo.Tests`, `MMLib.Alvo.Api.Tests`).
- **XML doc comments are required on every public member** of `Abstractions` and the core.
- **A grown `PublicApi.*.verified.txt` trips the Stop hook** and sends you to `alvo-architecture-rules`; each added symbol must be justified there. Expected growth is listed per task.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` | The one operation contract — ten members, one per route |
| `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` | The request/response records the contract speaks in |
| `src/MMLib.Alvo.Abstractions/Management/IManagementAccessPolicy.cs` | The named seam #146 fills; `ManagementAccessLevel` beside it |
| `src/MMLib.Alvo.Abstractions/Management/IManagementIdempotencyStore.cs` | The port a replayable management write is recorded through |
| `src/MMLib.Alvo/Management/AlvoManagementOptions.cs` | `Alvo:Management` — the route prefix and the mode label |
| `src/MMLib.Alvo/Management/AlvoManagementEndpointRouteBuilderExtensions.cs` | `MapAlvoManagementApi()` — the endpoint seam, separate from the DI seam |
| `src/MMLib.Alvo/Management/Setup.cs` | `AddAlvoManagement()` — options, service, deny-all policy |
| `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs` | The implementation, over services that already exist |
| `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` | One minimal-API delegate per contract member |
| `src/MMLib.Alvo/Management/Internal/ManagementOperation.cs` | The endpoint metadata marker naming the contract member |
| `src/MMLib.Alvo/Management/Internal/ManagementAccessFilter.cs` | Resolve the caller, then ask the policy; default-deny |
| `src/MMLib.Alvo/Management/Internal/ManagementProblems.cs` | Exception → `IResult`, through `ProblemResultFactory` |
| `src/MMLib.Alvo/Management/Internal/ManagementCapabilities.cs` | Projects the two unhonoured tables, verbatim |
| `src/MMLib.Alvo/Management/Internal/ManagementIdempotency.cs` | Fingerprint + replay for apply and rollback |
| `src/MMLib.Alvo/Management/Internal/AlvoManagementOptionsConfiguration.cs` | Binds and validates the section |
| `src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs` | +`PreviewAsync` — the plan-only operation its own `RejectDryRun` points at |
| `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` | +`EveryRefusal` — one enumeration of every refused slot |
| `src/MMLib.Alvo/Migrations/AlvoBootState.cs` | +`Projects` — the project names the boot already recorded |
| `src/MMLib.Alvo/Api/AlvoProblemTypes.cs` | +`destructive-change`, +`precondition-required` |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreManagementIdempotencyStore.cs` | The port's one shipped implementation, over the existing table |
| `test/_shared/api/AlvoApiWorld.cs` | +`MapManagementApi`, +`ManagementAccess` on the setup record |
| `test/MMLib.Alvo.Tests/Management/` | Service-level facts (ring0, no HTTP) |
| `test/MMLib.Alvo.Api.Tests/Management/` | HTTP facts, including the §6.1 contract test |
| `docs/architecture/management-api.md` | The surface, the conventions it adopts, and D3 |

---

### Task 1: `AlvoManagementOptions` — the route prefix, bound and validated at startup

**Files:**
- Create: `src/MMLib.Alvo/Management/AlvoManagementOptions.cs`
- Create: `src/MMLib.Alvo/Management/Internal/AlvoManagementOptionsConfiguration.cs`
- Create: `src/MMLib.Alvo/Management/Setup.cs`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (add `services.AddAlvoManagement();` beside `services.AddAlvoApi();`)
- Test: `test/MMLib.Alvo.Tests/Management/AlvoManagementOptionsTests.cs`

**Interfaces:**
- Consumes: `MMLib.Alvo.Api.Internal.RoutePrefix.Normalize(string) -> string` (existing, `internal`).
- Produces:
  - `public sealed class MMLib.Alvo.Management.AlvoManagementOptions` with `public const string SectionName = "Alvo:Management"`, `public string RoutePrefix { get; set; } = "/management"`, `public string? ModeLabel { get; set; }`.
  - `internal static IServiceCollection MMLib.Alvo.Management.ManagementSetup.AddAlvoManagement(this IServiceCollection services)`.
  - `internal sealed class MMLib.Alvo.Management.Internal.AlvoManagementOptionsConfiguration : IConfigureOptions<AlvoManagementOptions>, IValidateOptions<AlvoManagementOptions>` with `internal const string RoutePrefixKey = "Alvo:Management:RoutePrefix"`.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Tests/Management/AlvoManagementOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using Shouldly;

namespace MMLib.Alvo.Tests.Management;

public class AlvoManagementOptionsTests
{
    [Fact]
    public void A_host_that_says_nothing_mounts_management_under_slash_management() =>
        Resolve(settings: null).RoutePrefix.ShouldBe("/management");

    [Fact]
    public void The_prefix_binds_from_the_double_underscore_spelling_an_operator_writes() =>
        Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = "/admin-api" })
            .RoutePrefix.ShouldBe("/admin-api");

    [Fact]
    public void The_configuration_key_is_the_one_the_refusal_quotes() =>
        AlvoManagementOptionsConfiguration.RoutePrefixKey.ShouldBe(
            $"{AlvoManagementOptions.SectionName}:{nameof(AlvoManagementOptions.RoutePrefix)}");

    [Theory]
    [InlineData("/management//v1")]
    [InlineData("/management/{project}")]
    [InlineData("/management/*")]
    public void A_prefix_that_is_not_literal_path_text_is_refused_at_startup(string prefix)
    {
        var refusal = Should.Throw<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = prefix }));

        refusal.Message.ShouldContain(AlvoManagementOptionsConfiguration.RoutePrefixKey);
    }

    [Fact]
    public void The_management_prefix_may_not_collide_with_the_data_api_prefix()
    {
        var refusal = Should.Throw<OptionsValidationException>(
            () => Resolve(new Dictionary<string, string?> { ["Alvo:Management:RoutePrefix"] = "/api" }));

        refusal.Message.ShouldContain(
            "would sit under the Data API's own prefix",
            Case.Sensitive,
            "two surfaces on one prefix is a route table nobody can reason about");
    }

    private static AlvoManagementOptions Resolve(IDictionary<string, string?>? settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
        services.AddAlvo();

        return services.BuildServiceProvider().GetRequiredService<IOptions<AlvoManagementOptions>>().Value;
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj`
Expected: FAIL at compile — `error CS0234: The type or namespace name 'Management' does not exist in the namespace 'MMLib.Alvo'`.

- [ ] **Step 3: Write `AlvoManagementOptions`**

Create `src/MMLib.Alvo/Management/AlvoManagementOptions.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// Infrastructure configuration for the Management API — where it mounts, and how this deployment
/// describes itself. Never domain input: what a project is, and who may manage it, comes from the
/// descriptor.
/// </summary>
/// <remarks>
/// The spelling is <c>Alvo:*</c>, not the <c>ALVO_*</c> spec §X.1 sketches, and that is a deliberate
/// deviation (the F5 design's D2): <c>docs/architecture/host.md</c> already deviated once the same way,
/// and a third spelling would be worse than either. In an environment variable the key is
/// <c>Alvo__Management__RoutePrefix</c>. #233 owns the vocabulary question globally.
/// </remarks>
public sealed class AlvoManagementOptions
{
    /// <summary>The configuration section these options bind from: <c>Alvo:Management</c>.</summary>
    public const string SectionName = "Alvo:Management";

    /// <summary>The route prefix every management endpoint sits under. Default <c>/management</c>.</summary>
    /// <remarks>
    /// Normalized exactly as <see cref="Api.AlvoApiOptions.RoutePrefix"/> is, by the same single
    /// reduction — so <c>"management"</c>, <c>"/management"</c> and <c>"/management/"</c> mount in one
    /// place. Unlike the Data API's, it may not reduce to the empty string: the management surface owns
    /// literal path segments (<c>projects</c>, <c>info</c>) that would shadow an entity route at the root.
    /// </remarks>
    public string RoutePrefix { get; set; } = "/management";

    /// <summary>
    /// How <c>GET {prefix}/info</c> describes this deployment, when the host wants to say something other
    /// than the <see cref="AlvoMode"/> it registered. <see langword="null"/> reports the mode itself.
    /// </summary>
    public string? ModeLabel { get; set; }
}
```

- [ ] **Step 4: Write the configuration binder and validator**

Create `src/MMLib.Alvo/Management/Internal/AlvoManagementOptionsConfiguration.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using System.Buffers;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Binds <see cref="AlvoManagementOptions"/> from configuration and refuses, at startup, every prefix that
/// cannot become a route pattern — or that would sit inside the Data API's own.
/// </summary>
internal sealed class AlvoManagementOptionsConfiguration(IConfiguration? configuration, IOptions<AlvoApiOptions> api)
    : IConfigureOptions<AlvoManagementOptions>, IValidateOptions<AlvoManagementOptions>
{
    internal const string RoutePrefixKey =
        $"{AlvoManagementOptions.SectionName}:{nameof(AlvoManagementOptions.RoutePrefix)}";

    private const string ReservedInRoutePattern = "{}*?#:";

    private static readonly SearchValues<char> _reserved = SearchValues.Create(ReservedInRoutePattern);

    public void Configure(AlvoManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(AlvoManagementOptions.SectionName).Bind(options);
    }

    public ValidateOptionsResult Validate(string? name, AlvoManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> refusals = [.. Refusals(options)];

        return refusals.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(refusals);
    }

    private IEnumerable<string> Refusals(AlvoManagementOptions options)
    {
        var normalized = RoutePrefix.Normalize(options.RoutePrefix ?? string.Empty);
        if (normalized.Length == 0)
        {
            yield return $"{RoutePrefixKey} is empty. The management surface owns literal segments "
                + "('projects', 'info') that would shadow an entity route at the root; set it to a path "
                + "such as \"/management\".";
            yield break;
        }

        foreach (var refusal in ShapeRefusals(options.RoutePrefix, normalized[1..]))
        {
            yield return refusal;
        }

        if (SitsUnderTheDataApi(normalized))
        {
            yield return $"{RoutePrefixKey} '{options.RoutePrefix}' would sit under the Data API's own prefix "
                + $"'{api.Value.RoutePrefix}'. Mount the two surfaces apart, or move the Data API.";
        }
    }

    private static IEnumerable<string> ShapeRefusals(string prefix, string trimmed)
    {
        if (trimmed.Split('/').Any(string.IsNullOrWhiteSpace))
        {
            yield return $"{RoutePrefixKey} '{prefix}' has an empty path segment, which is not a legal route "
                + "pattern. Use a single slash between segments, e.g. \"/management/v1\".";
        }

        if (trimmed.AsSpan().ContainsAny(_reserved))
        {
            yield return $"{RoutePrefixKey} '{prefix}' contains a character a route pattern reserves (one of "
                + $"{ReservedInRoutePattern}). A prefix is literal path text.";
        }
    }

    private bool SitsUnderTheDataApi(string normalized)
    {
        var data = RoutePrefix.Normalize(api.Value.RoutePrefix ?? string.Empty);
        return data.Length != 0
            && (normalized.Equals(data, StringComparison.Ordinal)
                || normalized.StartsWith(data + "/", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 5: Write the DI seam and wire it into `AddAlvo`**

Create `src/MMLib.Alvo/Management/Setup.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Management;

/// <summary>Registers the Management API's options. Nothing here exposes an endpoint.</summary>
/// <remarks>
/// Called from <c>AddAlvo</c> and nowhere else, exactly as <c>AddAlvoApi</c> is: registering a service
/// exposes nothing, and the endpoint seam is <c>MapAlvoManagementApi()</c>
/// (<c>docs/architecture/extensibility.md</c> rule 10).
/// </remarks>
internal static class ManagementSetup
{
    internal static IServiceCollection AddAlvoManagement(this IServiceCollection services)
    {
        services.AddOptions<AlvoManagementOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoManagementOptions>, AlvoManagementOptionsConfiguration>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoManagementOptions>, AlvoManagementOptionsConfiguration>());
        return services;
    }
}
```

In `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`, add `using MMLib.Alvo.Management;` and insert one line immediately after `services.AddAlvoApi();`:

```csharp
        services.AddAlvoManagement();
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj`
Expected: PASS — six facts green.

- [ ] **Step 7: Accept the public-API baseline**

`AlvoManagementOptions` is the one new public symbol: an embedded host must be able to move the prefix, which is exactly why `AlvoApiOptions.RoutePrefix` is public. Run ring0, let the approval test write the received file, and copy it over the baseline.

Run: `scripts/test-ring0`
Expected: FAIL once — `PublicApiApprovalTests` reports `MMLib.Alvo` differs; then:

```bash
cp test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.received.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
scripts/test-ring0
```
Expected: PASS. The Stop hook will ask you to justify the growth against `alvo-architecture-rules` — the justification is the sentence above.

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo/Management src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs \
        test/MMLib.Alvo.Tests/Management test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(management): bind Alvo:Management:RoutePrefix, and refuse a prefix that cannot mount"
```

---

### Task 2: `IAlvoManagement` and its first member — `GetInfoAsync`

Interface-first: the contract exists, and a test against it exists, before any endpoint does.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs`
- Create: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs`
- Create: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Setup.cs` (register the service)
- Test: `test/MMLib.Alvo.Tests/Management/ManagementInfoTests.cs`

**Interfaces:**
- Consumes: `AlvoManagementOptions` (Task 1); `IOptions<AlvoOptions>` with `AlvoOptions.Mode -> AlvoMode`; `IAlvoData` (only its concrete type name is read).
- Produces:
  - `public interface MMLib.Alvo.Management.IAlvoManagement` with `Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default)`.
  - `public sealed record MMLib.Alvo.Management.ManagementInfo(string Version, string Mode, string DataProvider, string StartupMode)`.
  - `internal sealed class MMLib.Alvo.Management.Internal.AlvoManagementService : IAlvoManagement`.

**Note — `DataProvider`, not `engine`.** Spec §2.2 says `info` reports the engine. The core depends only on `Abstractions`, and `IAlvoSqlDialect` — the only port that knows an engine's name — lives in `MMLib.Alvo.Data.EntityFrameworkCore`, which the core may not reference. So `info` reports the registered `IAlvoData` implementation's type name (`EfAlvoData`, or a host's own). Recorded as a deviation in `docs/architecture/management-api.md` (Task 14); a true engine name waits for a port that carries one.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Tests/Management/ManagementInfoTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management;
using Shouldly;

namespace MMLib.Alvo.Tests.Management;

public class ManagementInfoTests
{
    [Fact]
    public async Task Info_reports_the_mode_the_host_registered()
    {
        var management = Resolve(alvo => alvo.Services.Configure<AlvoOptions>(o => o.Mode = AlvoMode.Embedded));

        var info = await management.GetInfoAsync(TestContext.Current.CancellationToken);

        info.Mode.ShouldBe("embedded");
    }

    [Fact]
    public async Task A_host_may_label_its_own_mode()
    {
        var management = Resolve(alvo =>
            alvo.Services.Configure<AlvoManagementOptions>(o => o.ModeLabel = "standalone"));

        var info = await management.GetInfoAsync(TestContext.Current.CancellationToken);

        info.Mode.ShouldBe("standalone", "the label the host set wins over the mode it registered");
    }

    [Fact]
    public async Task Info_reports_the_build_and_the_startup_mode()
    {
        var info = await Resolve(configure: null).GetInfoAsync(TestContext.Current.CancellationToken);

        info.Version.ShouldNotBeNullOrWhiteSpace();
        info.StartupMode.ShouldBe("apply", "AlvoSchemaOptions.Startup defaults to Apply");
        info.DataProvider.ShouldBe("none", "no driver is registered in this fixture, and that is said plainly");
    }

    private static IAlvoManagement Resolve(Action<IAlvoBuilder>? configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure ?? (_ => { }));

        return services.BuildServiceProvider().GetRequiredService<IAlvoManagement>();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj --filter-class MMLib.Alvo.Tests.Management.ManagementInfoTests`
Expected: FAIL at compile — `error CS0246: The type or namespace name 'IAlvoManagement' could not be found`.

- [ ] **Step 3: Write the contract and its first model**

Create `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// <b>The one operation surface for administering an Alvo project.</b> The admin dashboard resolves it from
/// DI and calls it in-process; an agent, the CLI and a later MCP adapter reach the same members over HTTP.
/// One path, two transports — spec §0.5 contract 4 forbids a divergent write <em>path</em>, not
/// serialisation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member is descriptor-shaped and idempotent</b>, so an MCP adapter is a mapping rather than a
/// translation: there is no HTTP-only affordance an adapter would have to fake. A write carries its own
/// expected revision (optimistic concurrency) and its own idempotency key, rather than reading either off a
/// request header, which is what keeps the in-process caller's semantics identical to the HTTP caller's.
/// </para>
/// <para>
/// <b>Data is deliberately absent.</b> Rows are read and written through the Data API under the caller's own
/// context, so no management privilege over data exists and none has to be audited (the F5 design's D4).
/// </para>
/// <para>
/// <b>Every member has an HTTP route, and a contract test holds that</b> — the drift this shape risks is an
/// operation reachable in-process and not over the wire.
/// </para>
/// </remarks>
public interface IAlvoManagement
{
    /// <summary>Describes this deployment: the build, the mode, the data provider and the startup mode.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What this instance is.</returns>
    Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default);
}
```

Create `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>What this Alvo instance is.</summary>
/// <param name="Version">The informational version of the running <c>MMLib.Alvo</c> assembly.</param>
/// <param name="Mode">
/// <c>standalone</c> or <c>embedded</c> — the host's own label when it set one, otherwise the registered
/// <see cref="AlvoMode"/>, lower-cased.
/// </param>
/// <param name="DataProvider">
/// The registered <c>IAlvoData</c> implementation's type name, or <c>none</c> when no driver is registered.
/// <b>Not the database engine:</b> the core may not reference the adapter that knows one.
/// </param>
/// <param name="StartupMode">The <c>Alvo:Schema:Startup</c> mode this process booted under, lower-cased.</param>
public sealed record ManagementInfo(string Version, string Mode, string DataProvider, string StartupMode);
```

- [ ] **Step 4: Write the service**

Create `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`:

```csharp
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Migrations;
using System.Reflection;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The one implementation of <see cref="IAlvoManagement"/>: it orchestrates services that already exist and
/// owns no connection, no SQL and no second copy of any rule.
/// </summary>
internal sealed class AlvoManagementService(
    IOptions<AlvoOptions> alvo,
    IOptions<AlvoManagementOptions> management,
    IOptions<AlvoSchemaOptions> schema,
    IAlvoData? data) : IAlvoManagement
{
    private const string NoDriverRegistered = "none";

    /// <inheritdoc/>
    public Task<ManagementInfo> GetInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new ManagementInfo(Version, Mode, DataProvider, StartupMode));

    private static string Version =>
        typeof(AlvoManagementService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AlvoManagementService).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private string Mode =>
        management.Value.ModeLabel ?? alvo.Value.Mode.ToString().ToLowerInvariant();

    private string DataProvider => data?.GetType().Name ?? NoDriverRegistered;

    private string StartupMode => schema.Value.Startup.ToString().ToLowerInvariant();
}
```

Register it in `src/MMLib.Alvo/Management/Setup.cs`, inside `AddAlvoManagement`, before the `return`:

```csharp
        services.TryAddSingleton<IAlvoManagement, AlvoManagementService>();
```

(add `using MMLib.Alvo.Management.Internal;` — already present — and `using MMLib.Alvo.Management;`).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj --filter-class MMLib.Alvo.Tests.Management.ManagementInfoTests`
Expected: PASS — three facts green.

- [ ] **Step 6: Accept both public-API baselines**

`IAlvoManagement` and `ManagementInfo` are public in `Abstractions` because `MMLib.Alvo.Admin` may hold no reference to `MMLib.Alvo` (spec §1.2) and therefore reaches the operation surface only here. Nothing new is public in the core — `AlvoManagementService` is `internal`.

Run: `scripts/test-ring0`
Expected: FAIL once — `PublicApi.MMLib.Alvo.Abstractions` differs; then:

```bash
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
scripts/test-ring0
```
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management \
        test/MMLib.Alvo.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): add IAlvoManagement and its first member, GetInfoAsync"
```

---

### Task 3: The endpoint seam, the default-deny gate, and the contract test that makes "one path, two transports" structural

This is the task the whole design rests on. After it, adding a member to `IAlvoManagement` without adding a route fails a test.

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Management/IManagementAccessPolicy.cs`
- Create: `src/MMLib.Alvo/Management/AlvoManagementEndpointRouteBuilderExtensions.cs`
- Create: `src/MMLib.Alvo/Management/Internal/ManagementOperation.cs`
- Create: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs`
- Create: `src/MMLib.Alvo/Management/Internal/ManagementAccessFilter.cs`
- Create: `src/MMLib.Alvo/Management/Internal/DenyAllManagementAccessPolicy.cs`
- Modify: `src/MMLib.Alvo/Management/Setup.cs` (register the deny-all policy and the filter factory)
- Modify: `test/_shared/api/AlvoApiWorld.cs` (map the management API when asked; register a granting policy)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementContractTests.cs`
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementAccessTests.cs`

**Interfaces:**
- Consumes: `IAlvoManagement.GetInfoAsync` (Task 2); `AlvoManagementOptions.RoutePrefix` (Task 1); `IAlvoContextResolver.ResolveAsync(string?, string?, CancellationToken) -> ValueTask<AlvoPrincipal?>`; `IAlvoContextAccessor.Principal`; `IOptions<AlvoAuthOptions>` for `HeaderName`/`TenantHeaderName`; `ProblemResultFactory.Unauthenticated(string) -> IResult`.
- Produces:
  - `public enum MMLib.Alvo.Management.ManagementAccessLevel { None = 0, Viewer = 1, Developer = 2, Admin = 3 }`.
  - `public interface MMLib.Alvo.Management.IManagementAccessPolicy { ManagementAccessLevel Evaluate(AlvoContext context); }`.
  - `public static IEndpointConventionBuilder Microsoft.AspNetCore.Builder.AlvoManagementEndpointRouteBuilderExtensions.MapAlvoManagementApi(this IEndpointRouteBuilder endpoints)`.
  - `internal sealed record MMLib.Alvo.Management.Internal.ManagementOperation(string Member, ManagementAccessLevel Required)`.
  - `internal static RouteGroupBuilder MMLib.Alvo.Management.Internal.ManagementEndpoints.Map(IEndpointRouteBuilder, AlvoManagementOptions)`.
  - `internal sealed record AlvoApiWorldSetup` gains `bool MapManagementApi = false` and `ManagementAccessLevel? ManagementAccess = null`.

**The seam, stated once.** `IManagementAccessPolicy` is where #146 plugs the `access` block in. This plan ships **only** `DenyAllManagementAccessPolicy`, which returns `None` for every caller including an authenticated one — so the surface is unreachable without an explicit grant, which is what default-deny means here. The level→route mapping is fixed now (spec §3.3) because it is the route table's own property; *deciding a caller's level* is #146's and is not implemented.

- [ ] **Step 1: Write the failing contract test**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementContractTests.cs`:

```csharp
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using Shouldly;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <b>One path, two transports</b> (the F5 design §1.2, §6.1). The risk in serving the dashboard in-process
/// is an operation reachable through <see cref="IAlvoManagement"/> and not over HTTP; this measures the
/// <em>live endpoint table</em> against the interface's own members, so neither side can move alone.
/// </summary>
public class ManagementContractTests
{
    [Fact]
    public async Task Every_member_of_IAlvoManagement_has_an_http_route()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Admin));

        var routed = world.ManagementOperations().Select(operation => operation.Member);

        routed.Order(StringComparer.Ordinal).ShouldBe(
            ContractMembers().Order(StringComparer.Ordinal),
            "a member with no route is reachable in-process only, and a route with no member is a second path");
    }

    [Fact]
    public async Task No_route_stands_for_two_members_and_no_member_for_two_routes()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Admin));

        var operations = world.ManagementOperations().ToList();

        operations.Select(operation => operation.Member).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(operations.Count, "two routes for one member is two paths wearing one name");
    }

    [Fact]
    public async Task Every_route_declares_the_level_it_requires()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Admin));

        world.ManagementOperations().ShouldAllBe(
            operation => operation.Required != ManagementAccessLevel.None,
            "a route requiring 'None' is a route no policy gates");
    }

    private static IEnumerable<string> ContractMembers() =>
        typeof(IAlvoManagement).GetMethods().Select(method => method.Name);
}
```

Add the reader to `test/_shared/api/AlvoApiWorld.cs`, beside `PublishedPrincipals`:

```csharp
    /// <summary>
    /// Every management operation this world's route table actually carries, read off the endpoints'
    /// metadata rather than off a table a test could copy.
    /// </summary>
    internal IEnumerable<MMLib.Alvo.Management.Internal.ManagementOperation> ManagementOperations() =>
        _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(endpoint => endpoint.Metadata.GetMetadata<MMLib.Alvo.Management.Internal.ManagementOperation>())
            .OfType<MMLib.Alvo.Management.Internal.ManagementOperation>();
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementContractTests`
Expected: FAIL at compile — `error CS1739: The best overload for 'AlvoApiWorldSetup' does not have a parameter named 'MapManagementApi'`, and `error CS0234: … 'Internal' does not exist in the namespace 'MMLib.Alvo.Management'`.

- [ ] **Step 3: Write the access seam**

Create `src/MMLib.Alvo.Abstractions/Management/IManagementAccessPolicy.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>What a caller may do to a project's <em>configuration</em>, ordered from nothing to everything.</summary>
/// <remarks>
/// <b>Management levels govern the Management API only.</b> What a caller may do to <em>data</em> is the
/// descriptor's <c>entities.*.rules</c>, answered through the ordinary Data API, identically for the
/// dashboard and for everyone else. No second authorization system for data exists (the F5 design §3.3).
/// </remarks>
public enum ManagementAccessLevel
{
    /// <summary>Nothing. The default for every caller no policy grants — this is what default-deny is.</summary>
    None = 0,

    /// <summary>Every read: projects, descriptor, revisions, schema, capabilities, info, policy simulation.</summary>
    Viewer = 1,

    /// <summary>Viewer, plus applying a descriptor and rolling one back.</summary>
    Developer = 2,

    /// <summary>Developer, plus the settings surface (not built in F5).</summary>
    Admin = 3,
}

/// <summary>
/// Decides a caller's <see cref="ManagementAccessLevel"/>. <b>The seam #146 fills</b>: the shipped
/// implementation grants nothing at all, so the management surface is unreachable until a host or #146
/// registers a policy that compiles the descriptor's <c>access</c> block.
/// </summary>
/// <remarks>
/// The three levels are <b>independent predicates, not a hierarchy</b> in the descriptor: all three are
/// evaluated and the highest match wins. That resolution belongs to the implementation; this port carries
/// only its answer.
/// </remarks>
public interface IManagementAccessPolicy
{
    /// <summary>The level <paramref name="context"/> holds over this project's configuration.</summary>
    /// <param name="context">The caller, as the ordinary context resolver produced them.</param>
    /// <returns>The granted level; <see cref="ManagementAccessLevel.None"/> denies.</returns>
    ManagementAccessLevel Evaluate(AlvoContext context);
}
```

Create `src/MMLib.Alvo/Management/Internal/DenyAllManagementAccessPolicy.cs`:

```csharp
namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The shipped policy: <b>nobody holds any management level.</b>
/// </summary>
/// <remarks>
/// <para>
/// This is not a placeholder standing in for a decision — it <em>is</em> the decision this build makes.
/// <c>access</c> is parsed and honoured nowhere (<c>UnhonouredSubsystems</c> says so, by name), so there is
/// no source of truth a level could be read from, and the only safe answer is none. #146 replaces this
/// registration with one that compiles the descriptor's <c>access</c> predicates.
/// </para>
/// <para>
/// Registered with <c>TryAddSingleton</c>, so a host — and the admin dashboard's own bootstrap — substitutes
/// it the ordinary way.
/// </para>
/// </remarks>
internal sealed class DenyAllManagementAccessPolicy : IManagementAccessPolicy
{
    /// <inheritdoc/>
    public ManagementAccessLevel Evaluate(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ManagementAccessLevel.None;
    }
}
```

- [ ] **Step 4: Write the endpoint metadata marker and the access filter**

Create `src/MMLib.Alvo/Management/Internal/ManagementOperation.cs`:

```csharp
namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Endpoint metadata naming the <see cref="IAlvoManagement"/> member a route stands for, and the level it
/// requires.
/// </summary>
/// <remarks>
/// <para>
/// <b><paramref name="Member"/> is always written with <c>nameof</c>.</b> That is what makes the contract
/// test structural rather than stringly-typed: renaming an interface member breaks the build at the mapping
/// site, and deleting one breaks the test.
/// </para>
/// </remarks>
/// <param name="Member">The interface member's name, from <c>nameof</c>.</param>
/// <param name="Required">The lowest level that may reach this route (the F5 design §3.3).</param>
internal sealed record ManagementOperation(string Member, ManagementAccessLevel Required);
```

Create `src/MMLib.Alvo/Management/Internal/ManagementAccessFilter.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The one gate every management route carries: resolve the presented credential, publish the caller, and
/// refuse unless <see cref="IManagementAccessPolicy"/> grants at least the level the route declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-deny, exactly as the Data API is.</b> An anonymous caller is evaluated too — as
/// <see cref="AlvoContext.Anonymous"/> — rather than short-circuited, because a host's own policy may
/// legitimately grant an anonymous level in a development deployment and inventing a special case here
/// would hide that from the policy that owns the decision.
/// </para>
/// <para>
/// The caller is published on <see cref="IAlvoContextAccessor"/> for the duration of the delegate and taken
/// away in a <c>finally</c>, on <see cref="AlvoContextFilter"/>'s precedent: a throwing endpoint must not
/// leave a caller published on the ambient context this request's thread later reuses.
/// </para>
/// </remarks>
internal sealed class ManagementAccessFilter(
    ManagementOperation operation,
    IAlvoContextResolver resolver,
    IAlvoContextAccessor accessor,
    IManagementAccessPolicy policy,
    IOptions<AlvoAuthOptions> authOptions) : IEndpointFilter
{
    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var options = authOptions.Value;
        var presented = Presented(context.HttpContext.Request, options.HeaderName);
        var principal = presented is null ? null : await Resolve(presented, context, options).ConfigureAwait(false);
        if (presented is not null && principal is null)
        {
            return ProblemResultFactory.Unauthenticated(options.HeaderName);
        }

        var caller = principal?.Context ?? AlvoContext.Anonymous;

        return policy.Evaluate(caller) >= operation.Required
            ? await Invoke(principal, context, next).ConfigureAwait(false)
            : ManagementProblems.Forbidden();
    }

    private ValueTask<AlvoPrincipal?> Resolve(
        string presented, EndpointFilterInvocationContext context, AlvoAuthOptions options) =>
        resolver.ResolveAsync(
            presented,
            Presented(context.HttpContext.Request, options.TenantHeaderName),
            context.HttpContext.RequestAborted);

    private async ValueTask<object?> Invoke(
        AlvoPrincipal? principal, EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        accessor.Principal = principal;
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        finally
        {
            accessor.Principal = null;
        }
    }

    private static string? Presented(HttpRequest request, string header)
    {
        if (!request.Headers.TryGetValue(header, out var values))
        {
            return null;
        }

        var value = values.Count == 1 ? values[0] : string.Join(',', values.ToArray());
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>Builds one <see cref="ManagementAccessFilter"/> per mapped endpoint, from mapping-time facts.</summary>
internal sealed class ManagementAccessFilterFactory(
    IAlvoContextResolver resolver,
    IAlvoContextAccessor accessor,
    IManagementAccessPolicy policy,
    IOptions<AlvoAuthOptions> authOptions)
{
    /// <summary>The gate for one management operation.</summary>
    /// <param name="operation">The operation the endpoint performs.</param>
    internal ManagementAccessFilter For(ManagementOperation operation) =>
        new(operation, resolver, accessor, policy, authOptions);
}
```

- [ ] **Step 5: Write the first refusal and the endpoint map**

Create `src/MMLib.Alvo/Management/Internal/ManagementProblems.cs` — for now it holds one member; Task 10 grows it:

```csharp
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The one place a management refusal becomes a status, a problem <c>type</c> and a body — minted through
/// <see cref="ProblemResultFactory"/> so the Management API and the Data API publish <b>one</b> catalogue.
/// </summary>
internal static class ManagementProblems
{
    /// <summary>
    /// The 403 for a caller whose granted level is below the route's. One wording for every level, because
    /// naming the level they lack maps out the project's own <c>access</c> block one request at a time.
    /// </summary>
    internal static IResult Forbidden() => ProblemResultFactory.Problem(
        StatusCodes.Status403Forbidden,
        AlvoProblemTypes.Forbidden,
        "This caller holds no management access to this project. Grant it in the project's 'access' block, "
        + "or use a credential that already has it.");
}
```

`ProblemResultFactory.Problem` is `private` today; change its accessibility to `internal static IResult Problem(int statusCode, string type, string detail, IReadOnlyList<AlvoViolation>? violations = null)` so the management slice mints through the same factory instead of copying it. It stays inside `Api.Internal`, so nothing public moves.

Create `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// One minimal-API delegate per <see cref="IAlvoManagement"/> member, mapped under the configured prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every delegate is a thin adapter over the same service the dashboard calls in-process</b> — it binds,
/// calls one member, and renders. No business rule lives here, which is what keeps the two transports on one
/// path.
/// </para>
/// <para>
/// <b>The routes are excluded from the OpenAPI document</b> (<c>ExcludeFromDescription</c>), deliberately and
/// temporarily: the document Alvo publishes is the <em>generated</em> Data API contract, pinned by
/// <c>OpenApiDocumentTests.The_document_is_stable</c>, linted by <c>scripts/lint-api</c> and pinned again by
/// the TeaPie e2e suite as a path-set equality. Mixing a hand-written admin surface into it would move all
/// three for a reason that has nothing to do with the Data API. A document of its own is the follow-on.
/// </para>
/// </remarks>
internal static class ManagementEndpoints
{
    internal static RouteGroupBuilder Map(
        IEndpointRouteBuilder endpoints, AlvoManagementOptions options, ManagementAccessFilterFactory filters)
    {
        var group = endpoints.MapGroup(RoutePrefix.Normalize(options.RoutePrefix));
        group.ExcludeFromDescription();

        MapInfo(group, filters);

        return group;
    }

    private static void MapInfo(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapGet("/info", (IAlvoManagement management, CancellationToken ct) => management.GetInfoAsync(ct)),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.GetInfoAsync), ManagementAccessLevel.Viewer));

    private static RouteHandlerBuilder Gate(
        RouteHandlerBuilder route, ManagementAccessFilterFactory filters, ManagementOperation operation) =>
        route.AddEndpointFilter(filters.For(operation))
            .WithMetadata(operation)
            .WithName($"Alvo.Management.{operation.Member}");
}
```

Create `src/MMLib.Alvo/Management/AlvoManagementEndpointRouteBuilderExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;

namespace Microsoft.AspNetCore.Builder;

/// <summary>The Management API's endpoint seam, separate from the DI seam by design.</summary>
public static class AlvoManagementEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps one minimal-API delegate per <see cref="IAlvoManagement"/> member under
    /// <see cref="AlvoManagementOptions.RoutePrefix"/>.
    /// </summary>
    /// <remarks>
    /// Nothing mapped here is reachable without an explicit grant: every route carries the access gate, and
    /// the shipped <see cref="IManagementAccessPolicy"/> grants nobody anything.
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>A convention builder over the management routes, and over nothing else.</returns>
    /// <exception cref="InvalidOperationException">Alvo is not registered in the application's services.</exception>
    public static IEndpointConventionBuilder MapAlvoManagementApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var filters = services.GetService<ManagementAccessFilterFactory>()
            ?? throw new InvalidOperationException(
                "The Alvo Management API is not registered. Call services.AddAlvo(...) before MapAlvoManagementApi().");

        return ManagementEndpoints.Map(
            endpoints, services.GetRequiredService<IOptions<AlvoManagementOptions>>().Value, filters);
    }
}
```

Register the two new services in `src/MMLib.Alvo/Management/Setup.cs`:

```csharp
        services.TryAddSingleton<IManagementAccessPolicy, DenyAllManagementAccessPolicy>();
        services.TryAddSingleton<ManagementAccessFilterFactory>();
```

- [ ] **Step 6: Teach the test world to map it**

In `test/_shared/api/AlvoApiWorld.cs`, add two parameters to `AlvoApiWorldSetup` (at the end, so no positional call site moves):

```csharp
    bool MapManagementApi = false,
    MMLib.Alvo.Management.ManagementAccessLevel? ManagementAccess = null);
```

with XML docs on the record saying: `MapManagementApi` maps `app.MapAlvoManagementApi()` beside the Data API; `ManagementAccess` registers a stub `IManagementAccessPolicy` granting that level to every caller, because the shipped policy grants nobody anything and a world that left it in place could only ever measure the 403.

In `BuildApp`, after `setup.ConfigureServices?.Invoke(builder.Services);`:

```csharp
        if (setup.ManagementAccess is { } level)
        {
            builder.Services.AddSingleton<MMLib.Alvo.Management.IManagementAccessPolicy>(
                new GrantingManagementAccessPolicy(level));
        }
```

In `StartAsync`, immediately after the `MapAlvoDataApi` block:

```csharp
        if (setup.MapManagementApi)
        {
            app.MapAlvoManagementApi();
        }
```

and add the stub as a private nested type of `AlvoApiWorld`:

```csharp
    /// <summary>
    /// Grants one level to every caller. The shipped policy grants nobody anything — that is the product's
    /// default-deny — so a world measuring anything past the gate has to supply its own, exactly as a host
    /// or #146 will.
    /// </summary>
    private sealed class GrantingManagementAccessPolicy(MMLib.Alvo.Management.ManagementAccessLevel level)
        : MMLib.Alvo.Management.IManagementAccessPolicy
    {
        public MMLib.Alvo.Management.ManagementAccessLevel Evaluate(AlvoContext context) => level;
    }
```

- [ ] **Step 7: Run the contract test to verify it passes**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementContractTests`
Expected: PASS — one member, one route, one level.

- [ ] **Step 8: Write the default-deny facts**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementAccessTests.cs`:

```csharp
using MMLib.Alvo.Management;
using Shouldly;
using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

public class ManagementAccessTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);

    [Fact]
    public async Task With_the_shipped_policy_every_management_route_is_403_even_for_an_admin_key()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ResponseReading.ProblemTypeAsync(response)).ShouldBe(
            AlvoProblemTypes.UriOf(AlvoProblemTypes.Forbidden));
    }

    [Fact]
    public async Task An_anonymous_caller_is_403_rather_than_401()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info");

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "nothing failed authentication: no credential was presented, and the policy refused");
    }

    [Fact]
    public async Task A_presented_credential_that_cannot_be_used_is_401_before_the_policy_is_consulted()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(
                RevokedKeyId: _admin.KeyId,
                MapManagementApi: true,
                ManagementAccess: ManagementAccessLevel.Admin));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_granted_viewer_reaches_info()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Viewer));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ResponseReading.JsonAsync(response))["mode"]!.GetValue<string>().ShouldBe("standalone");
    }

    [Fact]
    public async Task Mapping_the_management_api_adds_no_route_to_the_data_api_prefix()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            setup: new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Admin));

        (await world.SendAsync(HttpMethod.Get, "/api/info")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
```

If `ResponseReading` has no `ProblemTypeAsync`/`JsonAsync` under those names, use the readers it already publishes — read `test/_shared/api/ResponseReading.cs` first and call what is there rather than adding a second reader.

- [ ] **Step 9: Run them to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementAccessTests`
Expected: PASS — five facts green.

- [ ] **Step 10: Run ring0 and accept the baselines**

Run: `scripts/test-ring0`
Expected: FAIL once — both public-API baselines grew (`ManagementAccessLevel`, `IManagementAccessPolicy` in Abstractions; `AlvoManagementEndpointRouteBuilderExtensions.MapAlvoManagementApi` in the core). Justification for the Stop hook: the policy is the substitution point #146 and every host needs, and `MapAlvoManagementApi` is the endpoint seam — the exact pair `MapAlvoDataApi` already occupies.

```bash
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
cp test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.received.txt \
   test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
scripts/test-ring0
```
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs \
        test/_shared/api/AlvoApiWorld.cs test/MMLib.Alvo.Api.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(management): map the surface, gate it default-deny, and pin every member to a route"
```

---

### Task 4: `GET {m}/projects` and `GET {m}/projects/{p}/descriptor` — the export

**Files:**
- Modify: `src/MMLib.Alvo/Migrations/AlvoBootState.cs` (+`Projects`)
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (+2 members)
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (+2 records)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementProblems.cs` (+`NotFound`)
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (+2 routes)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementDescriptorReadTests.cs`

**Interfaces:**
- Consumes: `IDescriptorVersionStore.GetCurrentAsync(string, CancellationToken) -> Task<DescriptorVersion?>`; `AlvoBootState`.
- Produces:
  - `Task<IReadOnlyList<ManagementProject>> ListProjectsAsync(CancellationToken ct = default)`.
  - `Task<ManagementDescriptor> GetDescriptorAsync(string project, CancellationToken ct = default)`.
  - `public sealed record ManagementProject(string Name, int? Revision, string Phase)`.
  - `public sealed record ManagementDescriptor(string Project, int Revision, string DescriptorJson)`.
  - `public sealed class ManagementProjectNotFoundException(string project) : Exception` in Abstractions.
  - `internal IReadOnlyDictionary<string, AlvoBootPhase> AlvoBootState.Projects`.

**The export is `DescriptorVersion.DescriptorJson`, not a new serialiser** (spec §2.2). Nothing re-serialises an `AlvoDescriptor` here: what a caller gets back is byte-for-byte what was applied, which is the only shape that satisfies acceptance criterion 3 ("everything clickable is exportable as code, no drift").

**`GET {m}/projects` returns one** (spec §2.6). Multi-project standalone is not built; the boot records exactly the projects it primed, and in this build that is one.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementDescriptorReadTests.cs`:

```csharp
using MMLib.Alvo.Management;
using Shouldly;
using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

public class ManagementDescriptorReadTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);

    private static AlvoApiWorldSetup Managed =>
        new(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Viewer);

    [Fact]
    public async Task The_project_list_carries_the_one_project_this_build_serves()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var projects = (await ResponseReading.JsonArrayAsync(
            await world.SendAsync(HttpMethod.Get, "/management/projects", _admin))).ToList();

        projects.Count.ShouldBe(1, "multi-project standalone is not built; the switcher degrades to one row");
        projects[0]!["name"]!.GetValue<string>().ShouldBe("vehicle-registry");
        projects[0]!["revision"]!.GetValue<int>().ShouldBe(1);
        projects[0]!["phase"]!.GetValue<string>().ShouldBe("ready");
    }

    [Fact]
    public async Task The_descriptor_is_returned_verbatim_with_its_revision()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await world.SendAsync(
            HttpMethod.Get, "/management/projects/vehicle-registry/descriptor", _admin);
        var body = await ResponseReading.JsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body["revision"]!.GetValue<int>().ShouldBe(1);
        body["descriptorJson"]!.GetValue<string>().ShouldBe(
            await File.ReadAllTextAsync(
                Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json"),
                TestContext.Current.CancellationToken),
            "the export is the stored descriptor text, not a re-serialisation of a parsed model");
    }

    [Fact]
    public async Task A_project_this_instance_does_not_serve_is_404()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await world.SendAsync(HttpMethod.Get, "/management/projects/not-mine/descriptor", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ResponseReading.ProblemTypeAsync(response)).ShouldBe(
            AlvoProblemTypes.UriOf(AlvoProblemTypes.NotFound));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementDescriptorReadTests`
Expected: FAIL — all three 404, because no such route exists (`ManagementContractTests` still passes, since neither side has moved).

- [ ] **Step 3: Expose the projects the boot already recorded**

In `src/MMLib.Alvo/Migrations/AlvoBootState.cs`, add beside `AppliedRevision`:

```csharp
    /// <summary>
    /// Every project this process has booted, with the phase it reached — the Management API's project list.
    /// </summary>
    /// <remarks>
    /// <b>Internal, and read by one caller.</b> The boot is already the one authority on which projects this
    /// instance serves (it is what keys <c>IDescriptorVersionStore</c> and the policy catalog), so a second
    /// source would be a second answer. It is not public because nothing outside this assembly has been shown
    /// to need it, and the Management API publishes the same fact as <c>ManagementProject</c>.
    /// </remarks>
    internal IReadOnlyDictionary<string, AlvoBootPhase> Projects =>
        Current.Projects.ToDictionary(entry => entry.Key, entry => entry.Value.Phase, StringComparer.Ordinal);
```

and, on `BootSnapshot`, add the revision reader the list needs:

```csharp
        internal int? RevisionOf(string project) =>
            Projects.TryGetValue(project, out var state) ? state.AppliedRevision : null;
```

plus, on `AlvoBootState`:

```csharp
    /// <summary>The revision <paramref name="project"/> booted at, or null when it has not booted.</summary>
    internal int? RevisionOf(string project) => Current.RevisionOf(project);
```

- [ ] **Step 4: Add the two contract members and their models**

In `IAlvoManagement`:

```csharp
    /// <summary>Lists the projects this instance serves.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One entry per booted project; in this build, exactly one.</returns>
    Task<IReadOnlyList<ManagementProject>> ListProjectsAsync(CancellationToken ct = default);

    /// <summary>
    /// The project's current descriptor, exactly as it was applied, with the revision an apply must echo in
    /// <c>If-Match</c>. <b>This is the export</b> — no re-serialisation happens.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored descriptor text and its revision.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<ManagementDescriptor> GetDescriptorAsync(string project, CancellationToken ct = default);
```

In `ManagementModels.cs`:

```csharp
/// <summary>One project this instance serves.</summary>
/// <param name="Name">The project name — the descriptor's own <c>name</c>.</param>
/// <param name="Revision">The applied revision, or null when nothing has been applied yet.</param>
/// <param name="Phase">The boot phase, lower-cased: <c>pending</c>, <c>ready</c> or <c>failed</c>.</param>
public sealed record ManagementProject(string Name, int? Revision, string Phase);

/// <summary>A project's current descriptor and the revision an apply must echo.</summary>
/// <param name="Project">The project name.</param>
/// <param name="Revision">The current revision; 0 when nothing has been applied yet.</param>
/// <param name="DescriptorJson">The descriptor exactly as it was applied.</param>
public sealed record ManagementDescriptor(string Project, int Revision, string DescriptorJson);
```

Create `src/MMLib.Alvo.Abstractions/Management/ManagementProjectNotFoundException.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>This instance serves no project by that name.</summary>
/// <remarks>
/// Its own type rather than an <see cref="InvalidOperationException"/>, because the HTTP layer has nothing
/// but the exception type to map from, and family 5 ("an invariant Alvo relies on is broken") propagates as a
/// 500 — which is what an unknown project name would otherwise become.
/// </remarks>
public sealed class ManagementProjectNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementProjectNotFoundException"/> class.</summary>
    public ManagementProjectNotFoundException()
        : base("This instance serves no project by that name.")
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementProjectNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ManagementProjectNotFoundException(string message)
        : base(message)
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementProjectNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementProjectNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        Project = string.Empty;
    }

    /// <summary>Initializes a new instance naming the project that was asked for.</summary>
    /// <param name="project">The project name the caller asked for.</param>
    /// <param name="served">The projects this instance does serve.</param>
    public ManagementProjectNotFoundException(string project, IReadOnlyList<string> served)
        : base($"This instance serves no project '{project}'. It serves: {string.Join(", ", served ?? [])}.")
    {
        Project = project;
    }

    /// <summary>The project name the caller asked for.</summary>
    public string Project { get; }
}
```

- [ ] **Step 5: Implement the two members**

In `AlvoManagementService`, take `AlvoBootState boot` and `IDescriptorVersionStore versions` as further constructor parameters and add:

```csharp
    /// <inheritdoc/>
    public Task<IReadOnlyList<ManagementProject>> ListProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ManagementProject>>(
            [.. boot.Projects.Select(entry =>
                new ManagementProject(entry.Key, boot.RevisionOf(entry.Key), Lower(entry.Value)))]);

    /// <inheritdoc/>
    public async Task<ManagementDescriptor> GetDescriptorAsync(string project, CancellationToken ct = default)
    {
        EnsureServed(project);
        var current = await versions.GetCurrentAsync(project, ct).ConfigureAwait(false);

        return new ManagementDescriptor(project, current?.Revision ?? 0, current?.DescriptorJson ?? string.Empty);
    }

    private void EnsureServed(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        if (!boot.Projects.ContainsKey(project))
        {
            throw new ManagementProjectNotFoundException(project, [.. boot.Projects.Keys]);
        }
    }

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString()!.ToLowerInvariant();
```

- [ ] **Step 6: Add the 404 and map the two routes**

In `ManagementProblems`:

```csharp
    /// <summary>The 404 for a project this instance does not serve.</summary>
    /// <remarks>
    /// The detail names the project the caller asked for and the ones that exist. Unlike the Data API's 404,
    /// nothing is hidden by doing so: which projects an instance serves is not row-level data, and a caller
    /// who already holds a management level can read the list from <c>GET projects</c> anyway.
    /// </remarks>
    /// <param name="refusal">The refusal, whose message carries both halves.</param>
    internal static IResult NotFound(ManagementProjectNotFoundException refusal) => ProblemResultFactory.Problem(
        StatusCodes.Status404NotFound, AlvoProblemTypes.NotFound, refusal.Message);
```

In `ManagementEndpoints`, add to `Map` beside `MapInfo(group, filters);`:

```csharp
        MapProjects(group, filters);
        MapDescriptorRead(group, filters);
```

and the two mappers:

```csharp
    private static void MapProjects(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapGet("/projects", (IAlvoManagement management, CancellationToken ct) =>
                management.ListProjectsAsync(ct)),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.ListProjectsAsync), ManagementAccessLevel.Viewer));

    private static void MapDescriptorRead(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapGet("/projects/{project}/descriptor",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetDescriptorAsync(project, ct))),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.GetDescriptorAsync), ManagementAccessLevel.Viewer));

    /// <summary>
    /// Runs one contract member and turns its refusals into problem documents. Every delegate goes through
    /// here, so no endpoint decides a status of its own.
    /// </summary>
    private static async Task<IResult> Answer<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation().ConfigureAwait(false));
        }
        catch (ManagementProjectNotFoundException refusal)
        {
            return ManagementProblems.NotFound(refusal);
        }
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementDescriptorReadTests`
Expected: PASS — three facts green.

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementContractTests`
Expected: PASS — three members, three routes.

- [ ] **Step 8: Commit**

```bash
scripts/test-ring0
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management src/MMLib.Alvo/Migrations/AlvoBootState.cs \
        test/MMLib.Alvo.Api.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): list projects, and export the stored descriptor verbatim"
```

---

### Task 5: `GET {m}/projects/{p}/revisions` and `.../revisions/{n}` — the configuration history

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (+2 members)
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (+2 records)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (+2 routes)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementRevisionTests.cs`

**Interfaces:**
- Consumes: `IDescriptorVersionStore.ListAsync(string, CancellationToken) -> Task<IReadOnlyList<DescriptorVersion>>`; `IDescriptorVersionStore.GetAsync(string, int, CancellationToken) -> Task<DescriptorVersion?>`.
- Produces:
  - `Task<IReadOnlyList<ManagementRevision>> ListRevisionsAsync(string project, CancellationToken ct = default)`.
  - `Task<ManagementRevisionDetail> GetRevisionAsync(string project, int revision, CancellationToken ct = default)`.
  - `public sealed record ManagementRevision(int Revision, DateTimeOffset CreatedAt, string? Author, string? Reason, int? RolledBackFrom)`.
  - `public sealed record ManagementRevisionDetail(ManagementRevision Version, string DescriptorJson)`.
  - `public sealed class ManagementRevisionNotFoundException` (same four-constructor shape as `ManagementProjectNotFoundException`, carrying `Project` and `Revision`).

**This is the D5 screen's data source.** `DescriptorVersion` carries `Revision`, `CreatedAt`, `Author`, `Reason` and `RolledBackFrom` and has had no consumer anywhere in the product; these two routes are the first, and one revision's body is the export of a past state.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementRevisionTests.cs`:

```csharp
using MMLib.Alvo.Management;
using Shouldly;
using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

public class ManagementRevisionTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);

    private static AlvoApiWorldSetup Managed =>
        new(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Viewer);

    [Fact]
    public async Task The_history_is_append_only_and_ordered_oldest_first()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var revisions = (await ResponseReading.JsonArrayAsync(
            await world.SendAsync(HttpMethod.Get, "/management/projects/vehicle-registry/revisions", _admin)))
            .ToList();

        revisions.Count.ShouldBe(1);
        revisions[0]!["revision"]!.GetValue<int>().ShouldBe(1);
        revisions[0]!["rolledBackFrom"]!.GetValue<int?>().ShouldBeNull();
        revisions[0]!["createdAt"]!.GetValue<DateTimeOffset>().ShouldBeGreaterThan(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task One_revision_is_the_export_of_a_past_state()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var body = await ResponseReading.JsonAsync(
            await world.SendAsync(HttpMethod.Get, "/management/projects/vehicle-registry/revisions/1", _admin));

        body["version"]!["revision"]!.GetValue<int>().ShouldBe(1);
        body["descriptorJson"]!.GetValue<string>().ShouldContain("\"name\": \"vehicle-registry\"");
    }

    [Fact]
    public async Task A_revision_that_was_never_appended_is_404()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await world.SendAsync(
            HttpMethod.Get, "/management/projects/vehicle-registry/revisions/99", _admin);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ResponseReading.ProblemTypeAsync(response)).ShouldBe(
            AlvoProblemTypes.UriOf(AlvoProblemTypes.NotFound));
    }

    [Fact]
    public async Task A_revision_number_that_is_not_a_number_never_reaches_the_service()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await world.SendAsync(
            HttpMethod.Get, "/management/projects/vehicle-registry/revisions/latest", _admin);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "the route constraint refuses it at matching, so no delegate has to validate it");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementRevisionTests`
Expected: FAIL — the first three answer 404 with no problem document at all, because no route matches.

- [ ] **Step 3: Add the two contract members and their models**

In `IAlvoManagement`:

```csharp
    /// <summary>The project's append-only configuration history, oldest revision first.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Every appended revision's provenance, without its descriptor body.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<IReadOnlyList<ManagementRevision>> ListRevisionsAsync(string project, CancellationToken ct = default);

    /// <summary>One historical revision — the export of a past state.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="revision">The revision number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The revision's provenance and the descriptor it applied.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementRevisionNotFoundException">That revision was never appended.</exception>
    Task<ManagementRevisionDetail> GetRevisionAsync(string project, int revision, CancellationToken ct = default);
```

In `ManagementModels.cs`:

```csharp
/// <summary>One entry in a project's append-only configuration history.</summary>
/// <param name="Revision">The revision number; the first applied revision is 1.</param>
/// <param name="CreatedAt">When it was appended.</param>
/// <param name="Author">Who appended it; null for code-first or system.</param>
/// <param name="Reason">The human- or agent-supplied reason, when one was given.</param>
/// <param name="RolledBackFrom">The revision this one restored, when it was produced by a rollback.</param>
public sealed record ManagementRevision(
    int Revision, DateTimeOffset CreatedAt, string? Author, string? Reason, int? RolledBackFrom);

/// <summary>One revision with the descriptor it applied.</summary>
/// <param name="Version">The revision's provenance.</param>
/// <param name="DescriptorJson">The descriptor exactly as it was applied at that revision.</param>
public sealed record ManagementRevisionDetail(ManagementRevision Version, string DescriptorJson);
```

Create `src/MMLib.Alvo.Abstractions/Management/ManagementRevisionNotFoundException.cs` with the same four-constructor shape as `ManagementProjectNotFoundException`; the naming constructor is `public ManagementRevisionNotFoundException(string project, int revision)` with the message `$"Project '{project}' has no revision {revision}."` and it exposes `public string Project { get; }` and `public int Revision { get; }`.

- [ ] **Step 4: Implement the two members**

In `AlvoManagementService`:

```csharp
    /// <inheritdoc/>
    public async Task<IReadOnlyList<ManagementRevision>> ListRevisionsAsync(
        string project, CancellationToken ct = default)
    {
        EnsureServed(project);
        var history = await versions.ListAsync(project, ct).ConfigureAwait(false);

        return [.. history.Select(Provenance)];
    }

    /// <inheritdoc/>
    public async Task<ManagementRevisionDetail> GetRevisionAsync(
        string project, int revision, CancellationToken ct = default)
    {
        EnsureServed(project);
        var stored = await versions.GetAsync(project, revision, ct).ConfigureAwait(false)
            ?? throw new ManagementRevisionNotFoundException(project, revision);

        return new ManagementRevisionDetail(Provenance(stored), stored.DescriptorJson);
    }

    private static ManagementRevision Provenance(DescriptorVersion version) => new(
        version.Revision, version.CreatedAt, version.Author, version.Reason, version.RolledBackFrom);
```

- [ ] **Step 5: Map the two routes and extend `Answer`**

In `ManagementEndpoints.Answer`, add one arm:

```csharp
        catch (ManagementRevisionNotFoundException refusal)
        {
            return ManagementProblems.NotFound(refusal.Message);
        }
```

and change `ManagementProblems.NotFound` to take a `string detail`, with the project arm calling `NotFound(refusal.Message)` too — one 404 producer, two callers.

Add to `Map`:

```csharp
        MapRevisions(group, filters);
        MapRevision(group, filters);
```

```csharp
    private static void MapRevisions(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapGet("/projects/{project}/revisions",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.ListRevisionsAsync(project, ct))),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.ListRevisionsAsync), ManagementAccessLevel.Viewer));

    private static void MapRevision(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapGet("/projects/{project}/revisions/{revision:int}",
                (string project, int revision, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetRevisionAsync(project, revision, ct))),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.GetRevisionAsync), ManagementAccessLevel.Viewer));
```

The `:int` constraint is what makes the fourth fact pass without a delegate validating anything.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementRevisionTests`
Expected: PASS — four facts green.

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementContractTests`
Expected: PASS — five members, five routes.

- [ ] **Step 7: Commit**

```bash
scripts/test-ring0
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management test/MMLib.Alvo.Api.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): serve the append-only configuration history, and one revision as an export"
```

---

### Task 6: `GET {m}/projects/{p}/schema` — what the Data API actually serves

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (+1 member)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (+1 route)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementSchemaTests.cs`

**Interfaces:**
- Consumes: `ISchemaRegistry.GetSchema() -> SchemaModel`.
- Produces: `Task<SchemaModel> GetSchemaAsync(string project, CancellationToken ct = default)`.

**`descriptor` versus `schema` is the same idea one layer down** (spec §2.2): the descriptor is what the author wrote, `SchemaModel` is what survived. Where they differ is exactly where *declared but not honoured* lives, which is why `capabilities` (Task 7) sits beside this one. `SchemaModel` is already public in `Abstractions`, so nothing new is published by returning it.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementSchemaTests.cs`:

```csharp
using MMLib.Alvo.Management;
using Shouldly;

namespace MMLib.Alvo.Api.Tests.Management;

public class ManagementSchemaTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);

    [Fact]
    public async Task The_resolved_schema_is_what_the_data_api_routes_are_generated_from()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Viewer));

        var schema = await ResponseReading.JsonAsync(
            await world.SendAsync(HttpMethod.Get, "/management/projects/vehicle-registry/schema", _admin));

        var entities = schema["entities"]!.AsArray().Select(entity => entity!["name"]!.GetValue<string>());

        entities.Order(StringComparer.Ordinal).ShouldBe(
            ["owners", "vehicles"],
            "the schema endpoint answers with what is served, not with what was declared");
    }

    [Fact]
    public async Task A_project_this_instance_does_not_serve_is_404() =>
        (await Ask("/management/projects/not-mine/schema")).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);

    private static async Task<HttpResponseMessage> Ask(string path)
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Viewer));

        return await world.SendAsync(HttpMethod.Get, path, _admin);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementSchemaTests`
Expected: FAIL — the first fact 404s with no body, because no route matches.

- [ ] **Step 3: Add the member**

In `IAlvoManagement`:

```csharp
    /// <summary>The resolved schema — what the Data API actually serves for this project.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The applied <see cref="Schema.SchemaModel"/>.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<Schema.SchemaModel> GetSchemaAsync(string project, CancellationToken ct = default);
```

In `AlvoManagementService` (taking `ISchemaRegistry schemaRegistry` as a further constructor parameter):

```csharp
    /// <inheritdoc/>
    public Task<SchemaModel> GetSchemaAsync(string project, CancellationToken ct = default)
    {
        EnsureServed(project);
        return Task.FromResult(schemaRegistry.GetSchema());
    }
```

`ISchemaRegistry` carries no project parameter — one instance serves one project, which is the same constraint `GET {m}/projects` reports (spec §2.6). `EnsureServed` is still called, so an unknown name is a 404 rather than a different project's schema.

- [ ] **Step 4: Map the route**

In `ManagementEndpoints`, add `MapSchema(group, filters);` to `Map` and:

```csharp
    private static void MapSchema(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapGet("/projects/{project}/schema",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetSchemaAsync(project, ct))),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.GetSchemaAsync), ManagementAccessLevel.Viewer));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementSchemaTests`
Expected: PASS — two facts green.

Run: `scripts/test-ring0`
Expected: PASS after copying the Abstractions baseline (`GetSchemaAsync` is one added member).

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management test/MMLib.Alvo.Api.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): serve the resolved schema beside the descriptor that asked for it"
```

---

### Task 7: `GET {m}/projects/{p}/capabilities` — one source of truth for "not yet", served verbatim

**Files:**
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` (+`EveryRefusal`, +`UnhonouredRefusal`)
- Create: `src/MMLib.Alvo/Management/Internal/ManagementCapabilities.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (+1 member)
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (+3 records)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (+1 route)
- Test: `test/MMLib.Alvo.Tests/Management/ManagementCapabilitiesTests.cs`
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementCapabilitiesHttpTests.cs`

**Interfaces:**
- Consumes: `UnhonouredSubsystems.All -> IReadOnlyList<UnhonouredSubsystem>` with `.Block`, `.Consequence`; `UnhonouredFeatures.OnAField`, `.OnAnEntity`, `.EveryActionType`, `.UnhonouredAction(string)`, and the private `EveryDeclaredSlot()`.
- Produces:
  - `internal sealed record MMLib.Alvo.Descriptor.Internal.UnhonouredRefusal(string Slot, string Consequence, string Fix)`.
  - `internal static IReadOnlyList<UnhonouredRefusal> UnhonouredFeatures.EveryRefusal { get; }`.
  - `internal static ManagementCapabilities.Honoured -> IReadOnlyList<string>` and `ManagementCapabilities.Project() -> Management.ManagementCapabilities`.
  - `Task<ManagementCapabilities> GetCapabilitiesAsync(string project, CancellationToken ct = default)`.
  - `public sealed record ManagementCapabilities(IReadOnlyList<string> Honoured, IReadOnlyList<ManagementWarnedBlock> Warned, IReadOnlyList<ManagementRefusedFeature> Refused)`.
  - `public sealed record ManagementWarnedBlock(string Block, string Consequence)`.
  - `public sealed record ManagementRefusedFeature(string Slot, string Consequence, string Fix)`.

**The prose is served verbatim and never rewritten** (spec §2.3). Those sentences are deliberate, already covered by tests, and already asserted against the frozen schema; a second wording inside a Razor component would be a third spelling of one truth.

**Two deliberate departures from the spec's sketch, both because the code cannot honestly produce what the sketch shows.**
1. **No `issue` field.** The sketch shows `"issue": 28` on a warned block. `UnhonouredSubsystem` carries `Block`, `IsDeclaredBy` and `Consequence` and nothing else; some consequences name an issue inside the prose (`(#146)`, `(#152)`) and some name none. Minting an issue number here would be inventing data. The prose is served as written and carries whatever the table's author put in it.
2. **`honoured` is a written list, not a derivation.** Nothing in the code enumerates "the blocks this build does honour" — the two tables enumerate only what it does not. The list is written once in `ManagementCapabilities.Honoured` and held by a test to being **disjoint from `UnhonouredSubsystems.All`**, which is the invariant that can actually go wrong.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Tests/Management/ManagementCapabilitiesTests.cs`:

```csharp
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Management.Internal;
using Shouldly;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// <b>Capabilities may not lie.</b> The payload is compared against the two tables themselves, in the same
/// shape that already guards those tables against the frozen schema — so a subsystem that lands, and leaves
/// <c>UnhonouredSubsystems</c>, changes this endpoint with nobody editing it.
/// </summary>
public class ManagementCapabilitiesTests
{
    [Fact]
    public void Warned_is_every_unhonoured_subsystem_and_nothing_else()
    {
        var warned = ManagementCapabilities.Project().Warned;

        warned.Select(block => block.Block).ShouldBe(UnhonouredSubsystems.All.Select(block => block.Block));
    }

    [Fact]
    public void Every_warned_consequence_is_the_table_s_own_sentence_character_for_character()
    {
        foreach (var (served, declared) in ManagementCapabilities.Project().Warned.Zip(UnhonouredSubsystems.All))
        {
            served.Consequence.ShouldBe(
                declared.Consequence,
                Case.Sensitive,
                $"'{declared.Block}' must be served as written; a second wording is a third spelling of one truth");
        }
    }

    [Fact]
    public void Refused_is_every_unhonoured_feature_with_its_fix()
    {
        var refused = ManagementCapabilities.Project().Refused;

        refused.Select(feature => feature.Slot).ShouldBe(
            UnhonouredFeatures.EveryRefusal.Select(refusal => refusal.Slot));
        refused.ShouldAllBe(feature => feature.Fix.Length > 0, "a refusal without a fix is a dead end");
    }

    [Fact]
    public void Refused_names_the_slots_the_reference_drawing_already_uses()
    {
        var slots = ManagementCapabilities.Project().Refused.Select(feature => feature.Slot).ToList();

        slots.ShouldContain("field.default");
        slots.ShouldContain("field.validation");
        slots.ShouldContain("entity.softDelete");
    }

    [Fact]
    public void Honoured_and_warned_never_name_the_same_block()
    {
        var honoured = ManagementCapabilities.Honoured;

        honoured.Intersect(UnhonouredSubsystems.All.Select(block => block.Block), StringComparer.Ordinal)
            .ShouldBeEmpty("a block cannot be both honoured and warned about; one of the two lists is stale");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj --filter-class MMLib.Alvo.Tests.Management.ManagementCapabilitiesTests`
Expected: FAIL at compile — `error CS0117: 'UnhonouredFeatures' does not contain a definition for 'EveryRefusal'`.

- [ ] **Step 3: Give the refusal table one enumeration**

In `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs`, add immediately after `EveryFixSuggestion`:

```csharp
    /// <summary>
    /// Every refusal this build makes, as one flat list — the shape a reader outside this file needs, and the
    /// only one of them.
    /// </summary>
    /// <remarks>
    /// <b>The qualification lives here, not at the reader.</b> A field's <c>default</c> and an entity's
    /// <c>softDelete</c> are both <c>Path</c>s, and a consumer that prefixed them itself would be a second
    /// authority for how a refusal is named. <see cref="EveryFixSuggestion"/> already proved the shape: one
    /// enumeration, read by whoever needs it.
    /// </remarks>
    internal static IReadOnlyList<UnhonouredRefusal> EveryRefusal { get; } =
    [
        .. OnAField.Select(feature => new UnhonouredRefusal($"field.{feature.Path}", feature.Consequence, feature.Fix)),
        .. OnAnEntity.Select(feature => new UnhonouredRefusal($"entity.{feature.Path}", feature.Consequence, feature.Fix)),
        .. EveryDeclaredSlot().Select(slot => new UnhonouredRefusal(slot.Feature, slot.Consequence, slot.Fix)),
        .. EveryActionType.Select(type => Refusal(UnhonouredAction(type))),
    ];

    private static UnhonouredRefusal Refusal(UnhonouredSlot slot) =>
        new(slot.Feature, slot.Consequence, slot.Fix);
```

and, beside `UnhonouredSlot`'s own declaration:

```csharp
/// <summary>One refused feature, qualified by where it is declared.</summary>
/// <param name="Slot">The feature's qualified name — <c>field.default</c>, <c>entity.softDelete</c>, or the slot's own.</param>
/// <param name="Consequence">What silently happens instead, exactly as the table words it.</param>
/// <param name="Fix">What to do instead, exactly as the table words it.</param>
internal sealed record UnhonouredRefusal(string Slot, string Consequence, string Fix);
```

`EveryDeclaredSlot()` is already `private` on this type and `EveryRefusal` is on the same type, so nothing changes accessibility.

- [ ] **Step 4: Write the projection and the models**

Create `src/MMLib.Alvo/Management/Internal/ManagementCapabilities.cs`:

```csharp
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Projects the framework's two "not yet" tables into the capabilities payload — <b>and rewrites neither</b>.
/// </summary>
/// <remarks>
/// <para>
/// The consequence that makes this worth an endpoint: when the PR that implements automation deletes the
/// entry from <see cref="UnhonouredSubsystems"/>, the badge disappears from the dashboard <em>without anyone
/// touching the dashboard</em>.
/// </para>
/// <para>
/// <b>Two classes, and the UI must not make them look alike.</b> A <em>warned</em> block applies and does
/// nothing; a <em>refused</em> feature is rejected at apply, so a control for it is worse than a missing
/// control — its only possible output is a descriptor the apply will reject.
/// </para>
/// </remarks>
internal static class ManagementCapabilities
{
    /// <summary>
    /// The top-level and per-entity blocks this build does honour.
    /// </summary>
    /// <remarks>
    /// <b>Written, not derived, and that is stated rather than hidden.</b> Nothing in the framework enumerates
    /// what it <em>does</em> honour — the two tables enumerate only what it does not. What can actually go
    /// wrong is this list and <see cref="UnhonouredSubsystems.All"/> naming the same block, and a test holds
    /// the two disjoint.
    /// </remarks>
    internal static IReadOnlyList<string> Honoured { get; } = ["entities", "rules", "hooks", "tenancy", "auth"];

    /// <summary>What this build honours, warns about, and refuses.</summary>
    internal static Management.ManagementCapabilities Project() => new(
        Honoured,
        [.. UnhonouredSubsystems.All.Select(block => new ManagementWarnedBlock(block.Block, block.Consequence))],
        [.. UnhonouredFeatures.EveryRefusal.Select(refusal =>
            new ManagementRefusedFeature(refusal.Slot, refusal.Consequence, refusal.Fix))]);
}
```

In `ManagementModels.cs`:

```csharp
/// <summary>What this build honours, warns about, and refuses — one source of truth for "not yet".</summary>
/// <param name="Honoured">The blocks this build honours.</param>
/// <param name="Warned">Blocks that apply and then do nothing. The section exists; nothing runs.</param>
/// <param name="Refused">Features an apply rejects. A control for one of these must not exist.</param>
public sealed record ManagementCapabilities(
    IReadOnlyList<string> Honoured,
    IReadOnlyList<ManagementWarnedBlock> Warned,
    IReadOnlyList<ManagementRefusedFeature> Refused);

/// <summary>A declared block this build parses and then honours nowhere.</summary>
/// <param name="Block">The descriptor's top-level block name.</param>
/// <param name="Consequence">
/// What does not happen, concretely — <b>served verbatim.</b> Never rewrite it in a client.
/// </param>
public sealed record ManagementWarnedBlock(string Block, string Consequence);

/// <summary>A declared feature this build refuses at apply.</summary>
/// <param name="Slot">The feature's qualified name, e.g. <c>field.default</c>.</param>
/// <param name="Consequence">What would silently happen instead — <b>served verbatim.</b></param>
/// <param name="Fix">What to do instead, and where it is tracked — <b>served verbatim.</b></param>
public sealed record ManagementRefusedFeature(string Slot, string Consequence, string Fix);
```

- [ ] **Step 5: Add the member and the route**

In `IAlvoManagement`:

```csharp
    /// <summary>What this build honours, warns about and refuses for this project.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The capability report, whose prose is served verbatim.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<ManagementCapabilities> GetCapabilitiesAsync(string project, CancellationToken ct = default);
```

In `AlvoManagementService`:

```csharp
    /// <inheritdoc/>
    public Task<ManagementCapabilities> GetCapabilitiesAsync(string project, CancellationToken ct = default)
    {
        EnsureServed(project);
        return Task.FromResult(Internal.ManagementCapabilities.Project());
    }
```

In `ManagementEndpoints`, add `MapCapabilities(group, filters);` to `Map` and:

```csharp
    private static void MapCapabilities(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapGet("/projects/{project}/capabilities",
                (string project, IAlvoManagement management, CancellationToken ct) =>
                    Answer(() => management.GetCapabilitiesAsync(project, ct))),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.GetCapabilitiesAsync), ManagementAccessLevel.Viewer));
```

- [ ] **Step 6: Run the unit facts to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj --filter-class MMLib.Alvo.Tests.Management.ManagementCapabilitiesTests`
Expected: PASS — five facts green.

- [ ] **Step 7: Write and run the HTTP fact**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementCapabilitiesHttpTests.cs`:

```csharp
using MMLib.Alvo.Management;
using Shouldly;

namespace MMLib.Alvo.Api.Tests.Management;

public class ManagementCapabilitiesHttpTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);

    [Fact]
    public async Task The_wire_payload_carries_the_prose_unchanged()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Viewer));

        var body = await ResponseReading.JsonAsync(
            await world.SendAsync(HttpMethod.Get, "/management/projects/vehicle-registry/capabilities", _admin));

        var automation = body["warned"]!.AsArray()
            .Single(block => block!["block"]!.GetValue<string>() == "automation");

        automation["consequence"]!.GetValue<string>().ShouldBe(
            "no rule is ever evaluated, so no declared action runs — which looks exactly like a condition "
            + "that never matched",
            Case.Sensitive,
            "serialisation must not touch the sentence either — this is the whole point of the endpoint");

        body["honoured"]!.AsArray().Select(name => name!.GetValue<string>()).ShouldContain("entities");
        body["refused"]!.AsArray().ShouldNotBeEmpty();
    }
}
```

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementCapabilitiesHttpTests`
Expected: PASS.

If this fact fails on the em dash, the cause is the world's JSON serializer, not the table — fix the serializer setting, never the sentence.

- [ ] **Step 8: Commit**

```bash
scripts/test-ring0
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management \
        src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs \
        test/MMLib.Alvo.Tests/Management test/MMLib.Alvo.Api.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): serve capabilities from the two unhonoured tables, prose unchanged"
```

---

### Task 8: `POST {m}/projects/{p}/policy/simulate` — the same engine, never a copy

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (+1 member)
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (+3 records)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementProblems.cs` (+`Validation`)
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (+1 route)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementPolicySimulationTests.cs`

**Interfaces:**
- Consumes: `IPolicyEngine.Resolve(string entity, DataOperation operation, AlvoContext context) -> PolicyDecision` with `.IsDenied`, `.DenyReason`, `.Using`, `.WithCheck`, `.TenantScope` (each `CompiledExpression?` with `.Source`), `.HiddenFields`, `.ReadOnlyFields`; `IRoleCatalogProvider.DeclaredRoles -> RoleCatalog?`; `RoleCatalog.Resolve(IEnumerable<string>) -> IReadOnlySet<Role>` (throws `UnknownRoleException`).
- Produces:
  - `Task<ManagementPolicyVerdict> SimulatePolicyAsync(string project, ManagementPolicySimulation simulation, CancellationToken ct = default)`.
  - `public sealed record ManagementPolicySimulation(string Entity, string Operation, ManagementSimulatedCaller Caller)`.
  - `public sealed record ManagementSimulatedCaller(Guid? User, IReadOnlyList<string> Roles, Guid? Tenant)`.
  - `public sealed record ManagementPolicyVerdict(bool Allowed, string? DenyReason, string? Using, string? WithCheck, string? TenantScope, IReadOnlyList<string> HiddenFields, IReadOnlyList<string> ReadOnlyFields)`.

**The DoD says the simulator "answers identically to production". The only way that is not a promise is that it calls the same `IPolicyEngine`** — resolved from DI, never re-implemented, never handed a second catalog. That is the whole implementation: bind a context, call `Resolve`, render the decision.

**The record-id arm of spec §2.2 is deliberately not built.** Evaluating `USING` against a *stored row* would need a read, and a read through the Management API is exactly the data surface D4 refuses to create. The simulator answers the policy question — allow/deny plus the compiled predicates — and a caller who wants to know whether one row passes fetches it through `/api` under the simulated caller's own credential, which is the production answer by construction. Recorded in `docs/architecture/management-api.md`.

**`GET`, no — `POST`.** The simulation carries a body (a caller with roles), and `policy/simulate` writes nothing. It is `Viewer`-level for that reason (spec §3.3).

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementPolicySimulationTests.cs`:

```csharp
using MMLib.Alvo.Management;
using Shouldly;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The simulator is held to production's own answer: the same request, simulated and then actually made,
/// must agree. That is the acceptance criterion, not a unit test of a mapping.
/// </summary>
public class ManagementPolicySimulationTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);
    private static readonly TestApiKey _reader = new("mgmt-reader", ["authenticated"], ["*:read"]);

    private static AlvoApiWorldSetup Managed =>
        new(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Viewer);

    [Fact]
    public async Task The_verdict_carries_the_compiled_predicate_the_engine_resolved()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var verdict = await ResponseReading.JsonAsync(await Simulate(world, "vehicles", "list", ["admin"]));

        verdict["allowed"]!.GetValue<bool>().ShouldBeTrue();
        verdict["denyReason"]!.GetValue<string?>().ShouldBeNull();
        verdict["hiddenFields"]!.AsArray().ShouldNotBeNull();
    }

    [Fact]
    public async Task A_caller_no_rule_admits_is_denied_with_the_engine_s_own_reason()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var verdict = await ResponseReading.JsonAsync(await Simulate(world, "vehicles", "delete", ["anon"]));

        verdict["allowed"]!.GetValue<bool>().ShouldBeFalse();
        verdict["denyReason"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_simulator_agrees_with_what_the_data_api_actually_answers()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin, _reader], Managed);

        var simulated = await ResponseReading.JsonAsync(
            await Simulate(world, "vehicles", "delete", _reader.Roles));
        var real = await world.SendAsync(
            HttpMethod.Delete, $"/api/vehicles/{Guid.NewGuid()}", _reader);

        var deniedInProduction = real.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

        simulated["allowed"]!.GetValue<bool>().ShouldBe(
            !deniedInProduction,
            "the simulator calls the same IPolicyEngine, so a disagreement means it stopped doing so");
    }

    [Fact]
    public async Task A_role_the_descriptor_does_not_declare_is_422_rather_than_a_silent_deny()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await Simulate(world, "vehicles", "list", ["not-a-declared-role"]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ResponseReading.ProblemTypeAsync(response)).ShouldBe(
            AlvoProblemTypes.UriOf(AlvoProblemTypes.Validation));
    }

    [Fact]
    public async Task An_operation_name_the_framework_does_not_know_is_422()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        (await Simulate(world, "vehicles", "purge", ["admin"])).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private static Task<HttpResponseMessage> Simulate(
        AlvoApiWorld world, string entity, string operation, IReadOnlyList<string> roles) =>
        world.SendAsync(
            HttpMethod.Post,
            "/management/projects/vehicle-registry/policy/simulate",
            _admin,
            body: new JsonObject
            {
                ["entity"] = entity,
                ["operation"] = operation,
                ["caller"] = new JsonObject
                {
                    ["user"] = Guid.NewGuid().ToString(),
                    ["roles"] = new JsonArray([.. roles.Select(role => (JsonNode)role!)]),
                },
            });
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementPolicySimulationTests`
Expected: FAIL — every fact 404s, because no route matches.

- [ ] **Step 3: Add the member and its models**

In `IAlvoManagement`:

```csharp
    /// <summary>
    /// Answers what a named caller may do to an entity — <b>by calling the same <c>IPolicyEngine</c>
    /// production calls</b>, never a copy of it.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="simulation">The entity, the operation and the caller to simulate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The verdict and the predicates the engine resolved.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementSimulationException">The simulation names an operation or a role the framework cannot resolve.</exception>
    Task<ManagementPolicyVerdict> SimulatePolicyAsync(
        string project, ManagementPolicySimulation simulation, CancellationToken ct = default);
```

In `ManagementModels.cs`:

```csharp
/// <summary>One policy question.</summary>
/// <param name="Entity">The entity name, matched ordinally against the descriptor's own.</param>
/// <param name="Operation">The operation's wire name: <c>list</c>, <c>get</c>, <c>create</c>, <c>update</c> or <c>delete</c>.</param>
/// <param name="Caller">The caller to answer for.</param>
public sealed record ManagementPolicySimulation(string Entity, string Operation, ManagementSimulatedCaller Caller);

/// <summary>A caller to answer a policy question for.</summary>
/// <param name="User">The caller's id; null simulates the anonymous caller.</param>
/// <param name="Roles">The role names the caller holds; each must be declared in <c>auth.roles</c> or built in.</param>
/// <param name="Tenant">The tenant the caller acts in; null denies on a tenant-scoped entity, as production does.</param>
public sealed record ManagementSimulatedCaller(Guid? User, IReadOnlyList<string> Roles, Guid? Tenant = null);

/// <summary>What the policy engine answered, and the predicates it resolved.</summary>
/// <param name="Allowed">Whether the operation is permitted at all.</param>
/// <param name="DenyReason">The engine's own reason when it is not; null when it is.</param>
/// <param name="Using">The read predicate's CEL source, when one applies.</param>
/// <param name="WithCheck">The write predicate's CEL source, when one applies.</param>
/// <param name="TenantScope">The tenant-scope predicate's CEL source, when the entity is tenant-scoped.</param>
/// <param name="HiddenFields">Fields this caller may not read.</param>
/// <param name="ReadOnlyFields">Fields this caller may read and not write.</param>
public sealed record ManagementPolicyVerdict(
    bool Allowed,
    string? DenyReason,
    string? Using,
    string? WithCheck,
    string? TenantScope,
    IReadOnlyList<string> HiddenFields,
    IReadOnlyList<string> ReadOnlyFields);
```

Create `src/MMLib.Alvo.Abstractions/Management/ManagementSimulationException.cs` with the same four-constructor shape as `ManagementProjectNotFoundException`; the naming constructor is `public ManagementSimulationException(string detail)` — no, use the plain `(string message)` constructor for that, and expose nothing further. The message is what reaches the caller as the 422's `detail`, so it must be actionable: name what was sent and what is accepted.

- [ ] **Step 4: Implement the member**

In `AlvoManagementService` (taking `IPolicyEngine policies` and `IRoleCatalogProvider roles` as further constructor parameters):

```csharp
    /// <inheritdoc/>
    public Task<ManagementPolicyVerdict> SimulatePolicyAsync(
        string project, ManagementPolicySimulation simulation, CancellationToken ct = default)
    {
        EnsureServed(project);
        ArgumentNullException.ThrowIfNull(simulation);

        var decision = policies.Resolve(simulation.Entity, Operation(simulation.Operation), Caller(simulation.Caller));

        return Task.FromResult(Verdict(decision));
    }

    private static DataOperation Operation(string wireName) =>
        Enum.GetValues<DataOperation>().FirstOrDefault(
            operation => string.Equals(operation.ToString(), wireName, StringComparison.OrdinalIgnoreCase),
            defaultValue: (DataOperation)(-1)) is var resolved && Enum.IsDefined(resolved)
            ? resolved
            : throw new ManagementSimulationException(
                $"'{wireName}' is not an operation. Use one of: "
                + $"{string.Join(", ", Enum.GetNames<DataOperation>().Select(name => name.ToLowerInvariant()))}.");

    private AlvoContext Caller(ManagementSimulatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.User is null)
        {
            return AlvoContext.Anonymous;
        }

        var catalog = roles.DeclaredRoles ?? RoleCatalog.BuiltInOnly;
        try
        {
            return new AlvoContext
            {
                User = new UserId(caller.User.Value),
                Roles = catalog.Resolve(caller.Roles ?? []),
                Tenant = caller.Tenant is { } tenant ? new TenantId(tenant) : null,
            };
        }
        catch (UnknownRoleException refusal)
        {
            throw new ManagementSimulationException(refusal.Message, refusal);
        }
        catch (ArgumentException refusal)
        {
            throw new ManagementSimulationException(refusal.Message, refusal);
        }
    }

    private static ManagementPolicyVerdict Verdict(PolicyDecision decision) => new(
        !decision.IsDenied,
        decision.DenyReason,
        decision.Using?.Source,
        decision.WithCheck?.Source,
        decision.TenantScope?.Source,
        [.. decision.HiddenFields.Order(StringComparer.Ordinal)],
        [.. decision.ReadOnlyFields.Order(StringComparer.Ordinal)]);
```

`AlvoContext.Roles`'s own initializer throws `ArgumentException` for an empty set, which is why that arm exists — an empty `roles` array is a caller error, not a 500.

- [ ] **Step 5: Add the 422 and the route**

In `ManagementProblems`:

```csharp
    /// <summary>
    /// The 422 for a request this API read and refused — an unknown operation, an undeclared role.
    /// </summary>
    /// <remarks>
    /// The detail is the refusal's own message, which names what was sent and what is accepted.
    /// <c>UnknownRoleException</c> already lists the declared roles, and a simulator that hid them would send
    /// its caller to the descriptor for something the endpoint knows.
    /// </remarks>
    /// <param name="detail">The refusal's message.</param>
    internal static IResult Validation(string detail) => ProblemResultFactory.Problem(
        StatusCodes.Status422UnprocessableEntity, AlvoProblemTypes.Validation, detail);
```

In `ManagementEndpoints.Answer`, add:

```csharp
        catch (ManagementSimulationException refusal)
        {
            return ManagementProblems.Validation(refusal.Message);
        }
```

and map it:

```csharp
    private static void MapPolicySimulation(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapPost("/projects/{project}/policy/simulate",
                (string project, ManagementPolicySimulation simulation, IAlvoManagement management,
                    CancellationToken ct) =>
                    Answer(() => management.SimulatePolicyAsync(project, simulation, ct))),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.SimulatePolicyAsync), ManagementAccessLevel.Viewer));
```

with `MapPolicySimulation(group, filters);` added to `Map`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementPolicySimulationTests`
Expected: PASS — five facts green.

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementContractTests`
Expected: PASS — eight members, eight routes.

- [ ] **Step 7: Commit**

```bash
scripts/test-ring0
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management test/MMLib.Alvo.Api.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): simulate a policy through the engine production uses, not a copy of it"
```

---

### Task 9: `RuntimeSchemaService.PreviewAsync` — the plan-only operation the refusal already points at

`ApplyAsync` refuses `MigrationOptions.DryRun` outright today, and its own message says why and where to go instead: *"inspect the plan via a plan-only operation"*. That operation does not exist. Spec §2.1 lists `MigrationOptions.DryRun` as "already a member", which is true of the record and **not** true of the runtime path — so the spec's `?dryRun=true` needs this task before Task 10 can honour it.

**Files:**
- Create: `src/MMLib.Alvo/Migrations/DescriptorApplyPreview.cs`
- Modify: `src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs` (+`PreviewAsync`)
- Test: `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServicePreviewTests.cs`

**Interfaces:**
- Consumes: `IDescriptorValidator.Validate(string) -> DescriptorValidationResult`; `AlvoDescriptor.Parse(string)`; `DescriptorToSchemaMapper.Map(AlvoDescriptor) -> SchemaModel`; `IDescriptorVersionStore.GetCurrentAsync`; `ISchemaMigrator.PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions, CancellationToken) -> Task<MigrationPlan>`.
- Produces:
  - `public sealed record MMLib.Alvo.Migrations.DescriptorApplyPreview(MigrationPlan Plan, int CurrentRevision, bool AllowedByGuardrail)`.
  - `public Task<DescriptorApplyPreview> RuntimeSchemaService.PreviewAsync(string project, string descriptorJson, int expectedRevision, MigrationOptions options, CancellationToken ct = default)`.

**It touches no database and writes nothing.** It validates, parses, maps, reads the current version, plans against it, and reports whether the guardrail would let that plan through. `IRuntimeSchemaWriter` is not called and `IPolicyCatalogProvider` is not primed — a preview that primed the catalog would make a dry run change what the next request is allowed to do.

**It still checks the revision.** Planning against a base the caller never saw can misclassify a diff as destructive, which is exactly the reasoning `ApplyAsync`'s `DescriptorConcurrencyException` doc comment already gives; a preview built on the wrong base is a preview of something nobody asked for.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Tests/Migrations/RuntimeSchemaServicePreviewTests.cs`. Build the service exactly as `RuntimeSchemaServiceTests` already does — read that file first and reuse its fixture helpers rather than writing a second one.

```csharp
using MMLib.Alvo.Migrations;
using Shouldly;

namespace MMLib.Alvo.Tests.Migrations;

public class RuntimeSchemaServicePreviewTests
{
    [Fact]
    public async Task A_preview_reports_the_plan_and_writes_nothing()
    {
        var world = RuntimeSchemaWorld.WithApplied(TwoFieldDescriptor);

        var preview = await world.Service.PreviewAsync(
            "demo", ThreeFieldDescriptor, expectedRevision: 1, new MigrationOptions(),
            TestContext.Current.CancellationToken);

        preview.Plan.IsEmpty.ShouldBeFalse("a third field is a real step");
        preview.Plan.HasDestructiveChanges.ShouldBeFalse();
        preview.AllowedByGuardrail.ShouldBeTrue();
        preview.CurrentRevision.ShouldBe(1);
        world.Writer.Applies.ShouldBe(0, "a dry run that wrote would be an apply with a friendlier name");
        (await world.Versions.GetCurrentAsync("demo", TestContext.Current.CancellationToken))!.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task A_destructive_plan_is_reported_rather_than_thrown()
    {
        var world = RuntimeSchemaWorld.WithApplied(TwoFieldDescriptor);

        var preview = await world.Service.PreviewAsync(
            "demo", OneFieldDescriptor, expectedRevision: 1, new MigrationOptions(),
            TestContext.Current.CancellationToken);

        preview.Plan.HasDestructiveChanges.ShouldBeTrue();
        preview.AllowedByGuardrail.ShouldBeFalse(
            "a preview exists to show the verdict; throwing would leave the caller without the plan");
    }

    [Fact]
    public async Task The_same_destructive_plan_is_allowed_when_the_caller_asked_for_it()
    {
        var world = RuntimeSchemaWorld.WithApplied(TwoFieldDescriptor);

        var preview = await world.Service.PreviewAsync(
            "demo", OneFieldDescriptor, expectedRevision: 1, new MigrationOptions { AllowDestructive = true },
            TestContext.Current.CancellationToken);

        preview.AllowedByGuardrail.ShouldBeTrue();
    }

    [Fact]
    public async Task A_preview_against_a_base_the_caller_never_saw_is_refused()
    {
        var world = RuntimeSchemaWorld.WithApplied(TwoFieldDescriptor);

        await Should.ThrowAsync<DescriptorConcurrencyException>(() => world.Service.PreviewAsync(
            "demo", ThreeFieldDescriptor, expectedRevision: 0, new MigrationOptions(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_invalid_descriptor_is_refused_before_anything_is_planned()
    {
        var world = RuntimeSchemaWorld.WithApplied(TwoFieldDescriptor);

        await Should.ThrowAsync<DescriptorValidationException>(() => world.Service.PreviewAsync(
            "demo", "{ \"apiVersion\": \"alvo.dev/v1\" }", expectedRevision: 1, new MigrationOptions(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_preview_never_primes_the_policy_catalog()
    {
        var world = RuntimeSchemaWorld.WithApplied(TwoFieldDescriptor);

        await world.Service.PreviewAsync(
            "demo", ThreeFieldDescriptor, expectedRevision: 1, new MigrationOptions(),
            TestContext.Current.CancellationToken);

        world.PolicyCatalogs.SetCurrentCalls.ShouldBe(
            0, "a dry run that changed what the next request may do is not a dry run");
    }
}
```

`RuntimeSchemaWorld`, `TwoFieldDescriptor`, `ThreeFieldDescriptor` and `OneFieldDescriptor` are the fixtures `RuntimeSchemaServiceTests` already uses. If that file keeps them private, lift them into a `RuntimeSchemaWorld` type in the same folder and have both suites use it — one fixture, two suites, no copy. `world.PolicyCatalogs` is `test/_shared/api/CountingPolicyCatalogProvider.cs`, which already counts `SetCurrent`; link it into `MMLib.Alvo.Tests` if it is not there yet, rather than writing a second counter.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj --filter-class MMLib.Alvo.Tests.Migrations.RuntimeSchemaServicePreviewTests`
Expected: FAIL at compile — `error CS1061: 'RuntimeSchemaService' does not contain a definition for 'PreviewAsync'`.

- [ ] **Step 3: Write the result record**

Create `src/MMLib.Alvo/Migrations/DescriptorApplyPreview.cs`:

```csharp
namespace MMLib.Alvo.Migrations;

/// <summary>
/// What an apply <em>would</em> do — the migration plan, the base it was planned against, and the
/// destructive guardrail's verdict. Nothing is written to produce one.
/// </summary>
/// <remarks>
/// <b>The verdict is reported, not thrown.</b> <see cref="RuntimeSchemaService.ApplyAsync"/> throws
/// <see cref="DestructiveChangeNotAllowedException"/> because it must not proceed; a preview exists precisely
/// to show the caller the plan <em>and</em> the verdict, and an exception would hand them the second without
/// the first. The schema editor's diff, the rollback preview and the later AI proposal card are one mechanism
/// with three consumers (the F5 design §2.1).
/// </remarks>
/// <param name="Plan">The migration this descriptor would produce against the current applied schema.</param>
/// <param name="CurrentRevision">The revision the plan was built against; 0 when nothing is applied yet.</param>
/// <param name="AllowedByGuardrail">
/// Whether <see cref="ApplyAsync"/> would proceed: false exactly when the plan is destructive and
/// <see cref="MigrationOptions.AllowDestructive"/> is not set.
/// </param>
public sealed record DescriptorApplyPreview(MigrationPlan Plan, int CurrentRevision, bool AllowedByGuardrail);
```

- [ ] **Step 4: Write `PreviewAsync`**

In `RuntimeSchemaService`, add — and extract the three lines `ApplyAsync` and this one now share, so there is one reduction of "validate, parse, map":

```csharp
    /// <summary>
    /// Plans what <see cref="ApplyAsync"/> would do, <b>without touching the database</b> — the plan-only
    /// operation <see cref="ApplyAsync"/>'s dry-run refusal names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It neither writes nor primes.</b> <see cref="IRuntimeSchemaWriter"/> is not called and
    /// <see cref="IPolicyCatalogProvider.SetCurrent"/> is not: a preview that primed the catalog would let a
    /// dry run change what the very next request is allowed to do, which is the opposite of what a caller
    /// asking for a preview is asking for.
    /// </para>
    /// <para>
    /// <b>The revision is still checked.</b> Planning against a base the caller never saw can misclassify the
    /// diff as destructive — two unrelated field additions read as a drop plus an add — so a preview built on
    /// the wrong base previews something nobody asked for.
    /// </para>
    /// </remarks>
    /// <param name="project">The project the descriptor would be applied to.</param>
    /// <param name="descriptorJson">The untrusted descriptor JSON to validate, parse and plan.</param>
    /// <param name="expectedRevision">The revision the caller believes is latest (0 for a fresh project).</param>
    /// <param name="options">Migration options; only <see cref="MigrationOptions.AllowDestructive"/> affects the verdict.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plan, its base revision, and the guardrail's verdict.</returns>
    /// <exception cref="DescriptorValidationException"><paramref name="descriptorJson"/> is invalid.</exception>
    /// <exception cref="DescriptorConcurrencyException"><paramref name="expectedRevision"/> is not the latest revision.</exception>
    public async Task<DescriptorApplyPreview> PreviewAsync(
        string project, string descriptorJson, int expectedRevision, MigrationOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(options);

        var desired = DesiredSchema(descriptorJson);
        var current = await CurrentAtAsync(project, expectedRevision, ct).ConfigureAwait(false);
        var plan = await _migrator.PlanAsync(
            current?.Schema ?? new SchemaModel([]), desired, options, ct).ConfigureAwait(false);

        return new DescriptorApplyPreview(
            plan, current?.Revision ?? 0, !plan.HasDestructiveChanges || options.AllowDestructive);
    }

    private SchemaModel DesiredSchema(string descriptorJson)
    {
        Validate(descriptorJson);
        return DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptorJson));
    }

    private async Task<DescriptorVersion?> CurrentAtAsync(string project, int expectedRevision, CancellationToken ct)
    {
        var current = await _store.GetCurrentAsync(project, ct).ConfigureAwait(false);
        var currentRevision = current?.Revision ?? 0;
        if (currentRevision != expectedRevision)
        {
            throw new DescriptorConcurrencyException(project, expectedRevision, currentRevision);
        }

        return current;
    }
```

Then rewrite `ApplyAsync`'s opening to use the same two helpers, so the two paths cannot drift:

```csharp
        RejectDryRun(options);

        Validate(descriptorJson);
        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        var desired = DescriptorToSchemaMapper.Map(descriptor);
        var current = await CurrentAtAsync(project, expectedRevision, ct).ConfigureAwait(false);
        var currentSchema = current?.Schema ?? new SchemaModel([]);
```

(`ApplyAsync` needs the parsed `descriptor` as well as the schema, so it keeps its three lines and only the revision check is shared; do not force `DesiredSchema` on it and lose the descriptor.)

Finally, extend `RejectDryRun`'s message so it names the operation that now exists:

```csharp
                "Runtime schema apply does not support dry-run (MigrationOptions.DryRun). "
                + "Call RuntimeSchemaService.PreviewAsync for a plan-only preview, or use the code-first path.");
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj --filter-class MMLib.Alvo.Tests.Migrations.RuntimeSchemaServicePreviewTests`
Expected: PASS — six facts green.

Run: `dotnet test --project test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj --filter-class MMLib.Alvo.Tests.Migrations.RuntimeSchemaServiceTests`
Expected: PASS — the refactor moved no behaviour. If the dry-run refusal's text is asserted anywhere, update that assertion to the new sentence in the same commit.

- [ ] **Step 6: Commit**

```bash
scripts/test-ring0
cp test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.received.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
scripts/test-ring0
git add src/MMLib.Alvo/Migrations test/MMLib.Alvo.Tests/Migrations test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(migrations): add the plan-only preview the dry-run refusal already points callers at"
```

Public-API justification for the Stop hook: `ApplyAsync` tells its public callers to use a plan-only operation; that sentence is only true if the operation is reachable by the same callers.

---

### Task 10: `PUT {m}/projects/{p}/descriptor` — apply, `If-Match: revision` (D3), `?dryRun=true`, and two new slugs

**Files:**
- Modify: `src/MMLib.Alvo/Api/AlvoProblemTypes.cs` (+`DestructiveChange`, +`PreconditionRequired`)
- Modify: `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (+2 producers)
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (+1 member)
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (+3 records)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementProblems.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (+1 route, +`IfMatch`)
- Modify: `test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs` (two producers, two probes)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementApplyTests.cs`

**Interfaces:**
- Consumes: `RuntimeSchemaService.ApplyAsync(string, string, int, MigrationOptions, CancellationToken) -> Task<DescriptorVersion>`; `RuntimeSchemaService.PreviewAsync` (Task 9); `DescriptorConcurrencyException`; `DestructiveChangeNotAllowedException`; `DescriptorValidationException`.
- Produces:
  - `public const string AlvoProblemTypes.DestructiveChange = "destructive-change"` (409) and `public const string AlvoProblemTypes.PreconditionRequired = "precondition-required"` (428), both added to `All`.
  - `Task<ManagementApplyResult> ApplyDescriptorAsync(string project, ManagementApplyRequest request, CancellationToken ct = default)`.
  - `public sealed record ManagementApplyRequest(string DescriptorJson, int ExpectedRevision, bool AllowDestructive = false, bool DryRun = false, string? Author = null, string? Reason = null, string? IdempotencyKey = null)`.
  - `public sealed record ManagementApplyResult(bool Applied, int Revision, ManagementPlanSummary Plan)`.
  - `public sealed record ManagementPlanSummary(bool IsEmpty, bool HasDestructiveChanges, IReadOnlyList<string> Steps)`.
  - `internal static int? ManagementEndpoints.IfMatchRevision(HttpRequest)`.

**Deviation D3, and this is where it lands.** `If-Match` carries the descriptor's `revision` — an integer — not a strong `ETag` over a row version. `schema/project.schema.json:49` already froze `revision` as *"used for optimistic concurrency during apply"*; minting a second token beside a frozen one would leave two answers in the repo for one decision. Same header, same RFC 9110 semantics, different source. Nothing here touches `RowVersionETag`, and no `ETag` is emitted, because there is no row version to mint one from.

**`If-Match` is required, and its absence is 428, not a silent apply.** The Data API's own rule is that *every precondition this API cannot evaluate is refused, never ignored*; an apply with no expected revision is a lost update with nothing to detect it, on the one document that defines the whole backend. RFC 6585 §3 is the status for exactly this.

**Two new slugs, and no second catalogue.** `AlvoProblemTypes.All` remains the one list; `ProblemDetailsTests` asserts it against what the factory emits *and* against what is reachable over HTTP, so both new entries arrive with a producer and a probe or they fail that suite. Adding them moves `OpenApiDocumentTests.The_document_is_stable.verified.txt` by two lines of the problem `type` enum — that is the whole diff, and it must go through `alvo-snapshot-judge`.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementApplyTests.cs`:

```csharp
using MMLib.Alvo.Management;
using Shouldly;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

public class ManagementApplyTests
{
    private static readonly TestApiKey _admin = new("mgmt-admin", ["admin"], ["*:write"]);

    private const string Path = "/management/projects/vehicle-registry/descriptor";

    private static AlvoApiWorldSetup Managed =>
        new(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Developer);

    [Fact]
    public async Task An_apply_without_if_match_is_428_and_changes_nothing()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await world.SendAsync(HttpMethod.Put, Path, _admin, body: Body(await Current(world)));

        response.StatusCode.ShouldBe((HttpStatusCode)428);
        (await ResponseReading.ProblemTypeAsync(response)).ShouldBe(
            AlvoProblemTypes.UriOf(AlvoProblemTypes.PreconditionRequired));
        (await RevisionOf(world)).ShouldBe(1);
    }

    [Fact]
    public async Task An_if_match_naming_a_revision_that_is_not_current_is_412()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await Apply(world, await Current(world), ifMatch: "\"7\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ResponseReading.ProblemTypeAsync(response)).ShouldBe(
            AlvoProblemTypes.UriOf(AlvoProblemTypes.PreconditionFailed));
    }

    [Fact]
    public async Task An_if_match_that_is_not_a_revision_is_412_rather_than_ignored()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        (await Apply(world, await Current(world), ifMatch: "W/\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionFailed, "a weak tag names no revision this API can compare");
    }

    [Fact]
    public async Task Applying_a_new_field_appends_a_revision_and_reports_its_plan()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await Apply(world, WithExtraField(await Current(world)), ifMatch: "\"1\"");
        var body = await ResponseReading.JsonAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body["applied"]!.GetValue<bool>().ShouldBeTrue();
        body["revision"]!.GetValue<int>().ShouldBe(2);
        body["plan"]!["isEmpty"]!.GetValue<bool>().ShouldBeFalse();
        (await RevisionOf(world)).ShouldBe(2);
    }

    [Fact]
    public async Task A_dry_run_reports_the_plan_and_appends_nothing()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var body = await ResponseReading.JsonAsync(
            await Apply(world, WithExtraField(await Current(world)), ifMatch: "\"1\"", query: "?dryRun=true"));

        body["applied"]!.GetValue<bool>().ShouldBeFalse();
        body["revision"]!.GetValue<int>().ShouldBe(1, "a dry run reports the base it planned against");
        body["plan"]!["steps"]!.AsArray().ShouldNotBeEmpty();
        (await RevisionOf(world)).ShouldBe(1, "a dry run that appended would be an apply with a friendlier name");
    }

    [Fact]
    public async Task A_destructive_apply_is_409_destructive_change_unless_it_was_asked_for()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await Apply(world, WithoutAnEntity(await Current(world)), ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ResponseReading.ProblemTypeAsync(response)).ShouldBe(
            AlvoProblemTypes.UriOf(AlvoProblemTypes.DestructiveChange));
        (await RevisionOf(world)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_destructive_apply_lands_when_the_body_asks_for_it()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await Apply(
            world, WithoutAnEntity(await Current(world)), ifMatch: "\"1\"", allowDestructive: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RevisionOf(world)).ShouldBe(2);
    }

    [Fact]
    public async Task An_invalid_descriptor_is_422_with_the_validator_s_own_violations()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);

        var response = await Apply(world, "{ \"apiVersion\": \"alvo.dev/v1\" }", ifMatch: "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ResponseReading.JsonAsync(response))["violations"]!.AsArray().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_viewer_may_not_apply()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapManagementApi: true, ManagementAccess: ManagementAccessLevel.Viewer));

        (await Apply(world, await Current(world), ifMatch: "\"1\"")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden, "spec §3.3: applying is developer, not viewer");
    }

    [Fact]
    public async Task What_the_editor_sent_is_what_the_export_returns()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], Managed);
        var sent = WithExtraField(await Current(world));

        await Apply(world, sent, ifMatch: "\"1\"");

        (await ResponseReading.JsonAsync(await world.SendAsync(HttpMethod.Get, Path, _admin)))
            ["descriptorJson"]!.GetValue<string>().ShouldBe(sent, "F5 acceptance criterion 3: no config drift");
    }

    private static Task<HttpResponseMessage> Apply(
        AlvoApiWorld world, string descriptorJson, string ifMatch, bool allowDestructive = false,
        string query = "") =>
        world.SendAsync(
            HttpMethod.Put,
            Path + query,
            _admin,
            body: Body(descriptorJson, allowDestructive),
            headers: [new("If-Match", ifMatch)]);

    private static JsonObject Body(string descriptorJson, bool allowDestructive = false) => new()
    {
        ["descriptorJson"] = descriptorJson,
        ["allowDestructive"] = allowDestructive,
        ["author"] = "the-suite",
        ["reason"] = "a fact",
    };

    private static async Task<string> Current(AlvoApiWorld world) =>
        (await ResponseReading.JsonAsync(await world.SendAsync(HttpMethod.Get, Path, _admin)))
            ["descriptorJson"]!.GetValue<string>();

    private static async Task<int> RevisionOf(AlvoApiWorld world) =>
        (await ResponseReading.JsonAsync(await world.SendAsync(HttpMethod.Get, Path, _admin)))
            ["revision"]!.GetValue<int>();

    private static string WithExtraField(string descriptorJson) =>
        DescriptorEdits.AddOptionalTextField(descriptorJson, entity: "owners", field: "nickname");

    private static string WithoutAnEntity(string descriptorJson) =>
        DescriptorEdits.RemoveEntity(descriptorJson, entity: "vehicles");
}
```

Create `test/MMLib.Alvo.Api.Tests/Management/DescriptorEdits.cs` — a tiny, explicit editor over `JsonNode`, so the facts above say what they mean:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The two edits the apply facts make to a descriptor. Written over <see cref="JsonNode"/> rather than over
/// <c>AlvoDescriptor</c>, deliberately: an edit that round-tripped through the typed model would prove the
/// model's round trip, not the API's, and the apply facts assert the text comes back unchanged.
/// </summary>
internal static class DescriptorEdits
{
    internal static string AddOptionalTextField(string descriptorJson, string entity, string field)
    {
        var root = JsonNode.Parse(descriptorJson)!.AsObject();
        root["entities"]![entity]!["fields"]!.AsObject()[field] = new JsonObject { ["type"] = "text" };

        return Write(root);
    }

    internal static string RemoveEntity(string descriptorJson, string entity)
    {
        var root = JsonNode.Parse(descriptorJson)!.AsObject();
        root["entities"]!.AsObject().Remove(entity);

        return Write(root);
    }

    private static string Write(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
```

If `RemoveEntity` leaves a dangling `ref` from `vehicles` to `owners` (or the reverse), remove the referring entity instead — read `examples/vehicle-registry/vehicles.alvo.json` and drop whichever side is not referenced, so the descriptor still validates and the plan is destructive for the reason the fact claims.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementApplyTests`
Expected: FAIL — `PUT` answers 405 (the path matches `GET` only), so every status assertion fails.

- [ ] **Step 3: Extend the slug catalogue**

In `src/MMLib.Alvo/Api/AlvoProblemTypes.cs`, add two constants and list both in `All` (after `Conflict` and after `PreconditionFailed` respectively, so the list reads by family):

```csharp
    /// <summary>The change would discard data and the caller did not ask for that (409).</summary>
    /// <remarks>
    /// <para>
    /// <b>A third 409, and it is a different fix from both of the others.</b> An
    /// <see cref="IdempotencyConflict"/> is repaired with a fresh key, a <see cref="Conflict"/> with a
    /// different value — and this one with the same request plus an explicit destructive allowance, or with a
    /// descriptor that keeps what the plan would drop. A caller who cannot tell the three apart retries the
    /// wrong one.
    /// </para>
    /// <para>
    /// Not <see cref="Validation"/>: nothing about the descriptor is malformed. It is a well-formed request
    /// that collides with data already stored, which is what 409 means.
    /// </para>
    /// </remarks>
    public const string DestructiveChange = "destructive-change";

    /// <summary>The write requires a precondition and carried none (428).</summary>
    /// <remarks>
    /// RFC 6585 §3. Distinct from <see cref="PreconditionFailed"/>, which is a precondition that was sent and
    /// did not hold: this one is the header's <em>absence</em>, and the fix is to read the current revision
    /// and send it. Applying without one is a lost update with nothing to detect it, on the one document that
    /// defines the whole backend — so it is refused rather than defaulted.
    /// </remarks>
    public const string PreconditionRequired = "precondition-required";
```

In `ProblemResultFactory`, add the two producers:

```csharp
    /// <summary>The 409 for a plan that would discard data without an explicit allowance.</summary>
    /// <param name="detail">What would be discarded, and how to ask for it.</param>
    internal static IResult DestructiveChange(string detail) => Problem(
        StatusCodes.Status409Conflict, AlvoProblemTypes.DestructiveChange, detail);

    /// <summary>The 428 for a write that requires a precondition and carried none.</summary>
    /// <param name="detail">Which precondition is required, and where to read its value.</param>
    internal static IResult PreconditionRequired(string detail) => Problem(
        StatusCodes.Status428PreconditionRequired, AlvoProblemTypes.PreconditionRequired, detail);
```

- [ ] **Step 4: Add the contract member and its models**

In `IAlvoManagement`:

```csharp
    /// <summary>
    /// Applies a descriptor — <b>the one write path to a project's configuration.</b>
    /// </summary>
    /// <remarks>
    /// <paramref name="request"/> carries its own expected revision and its own idempotency key rather than
    /// reading either off a request header, which is what makes the in-process caller's semantics identical to
    /// the HTTP caller's. <see cref="ManagementApplyRequest.DryRun"/> plans and reports without writing.
    /// </remarks>
    /// <param name="project">The project name.</param>
    /// <param name="request">The descriptor, the expected revision, and the allowances.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What was applied, or what would be.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="Descriptor.DescriptorValidationException">The descriptor is invalid.</exception>
    /// <exception cref="Migrations.DescriptorConcurrencyException">The expected revision is not the current one.</exception>
    /// <exception cref="Migrations.DestructiveChangeNotAllowedException">The plan discards data and <see cref="ManagementApplyRequest.AllowDestructive"/> is not set.</exception>
    Task<ManagementApplyResult> ApplyDescriptorAsync(
        string project, ManagementApplyRequest request, CancellationToken ct = default);
```

In `ManagementModels.cs`:

```csharp
/// <summary>One apply.</summary>
/// <param name="DescriptorJson">The descriptor to apply, exactly as it should be stored.</param>
/// <param name="ExpectedRevision">
/// The revision the caller believes is current — over HTTP this is <c>If-Match</c> (the F5 design's D3), and
/// it is the same integer <c>schema/project.schema.json</c> froze as the optimistic-concurrency token.
/// </param>
/// <param name="AllowDestructive">Whether a plan that discards data may proceed. Never implied.</param>
/// <param name="DryRun">Plan and report without writing anything.</param>
/// <param name="Author">Who is applying, carried into the appended revision.</param>
/// <param name="Reason">Why, carried into the appended revision.</param>
/// <param name="IdempotencyKey">
/// A caller-chosen key making a retry a replay rather than a second apply. Scoped to the caller, exactly as
/// the Data API's is.
/// </param>
public sealed record ManagementApplyRequest(
    string DescriptorJson,
    int ExpectedRevision,
    bool AllowDestructive = false,
    bool DryRun = false,
    string? Author = null,
    string? Reason = null,
    string? IdempotencyKey = null);

/// <summary>What an apply or a rollback did, or would do.</summary>
/// <param name="Applied">False for a dry run, true when a revision was appended.</param>
/// <param name="Revision">The appended revision — or, for a dry run, the base it planned against.</param>
/// <param name="Plan">The migration, so the caller can show a diff without asking again.</param>
public sealed record ManagementApplyResult(bool Applied, int Revision, ManagementPlanSummary Plan);

/// <summary>A migration plan, in the shape a diff view needs.</summary>
/// <param name="IsEmpty">True when the descriptor changes nothing about the schema (a rules-only edit does).</param>
/// <param name="HasDestructiveChanges">True when at least one step discards data.</param>
/// <param name="Steps">One line per step, destructive steps marked — the framework's own summary wording.</param>
public sealed record ManagementPlanSummary(
    bool IsEmpty, bool HasDestructiveChanges, IReadOnlyList<string> Steps);
```

- [ ] **Step 5: Implement the member**

In `AlvoManagementService` (taking `RuntimeSchemaService runtime` as a further constructor parameter):

```csharp
    /// <inheritdoc/>
    public async Task<ManagementApplyResult> ApplyDescriptorAsync(
        string project, ManagementApplyRequest request, CancellationToken ct = default)
    {
        EnsureServed(project);
        ArgumentNullException.ThrowIfNull(request);

        var options = OptionsFor(request.AllowDestructive, request.Author, request.Reason);

        return request.DryRun
            ? Preview(await runtime.PreviewAsync(
                project, request.DescriptorJson, request.ExpectedRevision, options, ct).ConfigureAwait(false))
            : await ApplyAsync(project, request, options, ct).ConfigureAwait(false);
    }

    private async Task<ManagementApplyResult> ApplyAsync(
        string project, ManagementApplyRequest request, MigrationOptions options, CancellationToken ct)
    {
        var preview = await runtime.PreviewAsync(
            project, request.DescriptorJson, request.ExpectedRevision, options, ct).ConfigureAwait(false);
        var applied = await runtime.ApplyAsync(
            project, request.DescriptorJson, request.ExpectedRevision, options, ct).ConfigureAwait(false);

        return new ManagementApplyResult(Applied: true, applied.Revision, Summary(preview.Plan));
    }

    private static ManagementApplyResult Preview(DescriptorApplyPreview preview)
    {
        if (!preview.AllowedByGuardrail)
        {
            throw new DestructiveChangeNotAllowedException(string.Empty, preview.Plan);
        }

        return new ManagementApplyResult(Applied: false, preview.CurrentRevision, Summary(preview.Plan));
    }

    private static MigrationOptions OptionsFor(bool allowDestructive, string? author, string? reason) => new()
    {
        AllowDestructive = allowDestructive,
        Author = author,
        Reason = reason,
    };

    private static ManagementPlanSummary Summary(MigrationPlan plan) => new(
        plan.IsEmpty,
        plan.HasDestructiveChanges,
        [.. DestructiveChangeGuard.DescribeAllSteps(plan).Split(
            Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)]);
```

**The apply path plans twice, and that is deliberate.** `ApplyAsync` does not return its plan, and the response owes the caller a diff; planning is a pure function of the two schemas and reads no lock, so the second call costs a diff and buys the editor's confirmation view. A single-call shape would mean widening `RuntimeSchemaService.ApplyAsync`'s return type, which is a public breaking change made for a rendering convenience. Say so in `docs/architecture/management-api.md`; if the cost ever shows up in the load gate, widen the return type then.

`DestructiveChangeNotAllowedException(string.Empty, plan)` re-uses the framework's own refusal so the HTTP layer maps one exception type for both the dry-run and the real branch; pass the project name rather than `string.Empty` if that constructor's first parameter is the project.

- [ ] **Step 6: Map the route, read `If-Match`, and render the refusals**

In `ManagementProblems`, add three producers:

```csharp
    /// <summary>The 428 for an apply that carried no <c>If-Match</c>.</summary>
    internal static IResult PreconditionRequired() => ProblemResultFactory.PreconditionRequired(
        "This write requires 'If-Match' carrying the descriptor's current revision, e.g. If-Match: \"3\". "
        + "Read it from GET .../descriptor. Applying without one is a lost update nothing would detect.");

    /// <summary>The 412 for an <c>If-Match</c> this API cannot compare, or one that does not hold.</summary>
    /// <param name="detail">Which revision was expected and which is current, or why the tag is uncomparable.</param>
    internal static IResult PreconditionFailed(string detail) => ProblemResultFactory.Problem(
        StatusCodes.Status412PreconditionFailed, AlvoProblemTypes.PreconditionFailed, detail);

    /// <summary>The 409 for a plan that discards data without an explicit allowance.</summary>
    /// <param name="refusal">The framework's own refusal, whose message already lists the destructive steps.</param>
    internal static IResult DestructiveChange(DestructiveChangeNotAllowedException refusal) =>
        ProblemResultFactory.DestructiveChange(
            refusal.Message + " Send 'allowDestructive': true to proceed, or change the descriptor to keep it.");
```

and a 422 that carries the validator's violations:

```csharp
    /// <summary>The 422 for a descriptor the validator refused, with its per-pointer violations.</summary>
    /// <param name="refusal">The validation failure.</param>
    internal static IResult Validation(DescriptorValidationException refusal) => ProblemResultFactory.Problem(
        StatusCodes.Status422UnprocessableEntity,
        AlvoProblemTypes.Validation,
        "The descriptor is not valid and was not applied.",
        [.. refusal.Result.Errors.Select(error => new AlvoViolation(error.Pointer, "validation", error.Message))]);
```

Read `DescriptorValidationResult`'s actual member names before writing that projection and use what is there; the shape is "one violation per schema error, carrying its JSON pointer".

In `ManagementEndpoints`, add the header reader and the route:

```csharp
    /// <summary>
    /// The revision an <c>If-Match</c> names, or null when the header is absent or names nothing this API can
    /// compare.
    /// </summary>
    /// <remarks>
    /// <b>The two nulls are told apart by the caller</b>, which is why this returns a discriminated answer
    /// rather than an <c>int?</c>: an absent header is 428 and an uncomparable one is 412, and collapsing them
    /// would answer "you sent the wrong revision" to a caller who sent none.
    /// </remarks>
    internal static IfMatchRevision Revision(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.IfMatch, out var values) || values.Count == 0)
        {
            return IfMatchRevision.Absent;
        }

        var single = values.Count == 1 ? values[0] : null;

        return single is not null && int.TryParse(single.Trim('"'), out var revision)
            && !single.StartsWith("W/", StringComparison.Ordinal)
            ? IfMatchRevision.Of(revision)
            : IfMatchRevision.Uncomparable;
    }
```

with a small `internal readonly record struct IfMatchRevision(int? Value, bool Present)` carrying `Absent`, `Uncomparable` and `Of(int)` — three states, named, so the delegate reads as three arms.

```csharp
    private static void MapApply(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapPut("/projects/{project}/descriptor", ApplyAsync),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.ApplyDescriptorAsync), ManagementAccessLevel.Developer));

    private static Task<IResult> ApplyAsync(
        string project,
        ManagementApplyBody body,
        HttpRequest request,
        IAlvoManagement management,
        CancellationToken ct)
    {
        var expected = Revision(request);

        return expected switch
        {
            { Present: false } => Task.FromResult(ManagementProblems.PreconditionRequired()),
            { Value: null } => Task.FromResult(ManagementProblems.PreconditionFailed(
                "'If-Match' must carry the descriptor's revision as a strong tag, e.g. If-Match: \"3\". "
                + "A weak tag, a list of tags and '*' name no revision this API can compare.")),
            _ => Answer(() => management.ApplyDescriptorAsync(
                project, body.ToRequest(expected.Value!.Value, DryRunAsked(request)), ct)),
        };
    }

    private static bool DryRunAsked(HttpRequest request) =>
        request.Query.TryGetValue("dryRun", out var value)
        && (value.Count == 0 || string.Equals(value[0], "true", StringComparison.OrdinalIgnoreCase));
```

`ManagementApplyBody` is an `internal sealed record` in `ManagementEndpoints`' folder carrying `DescriptorJson`, `AllowDestructive`, `Author`, `Reason` — the wire shape, without `ExpectedRevision` and `DryRun`, because those come from the header and the query string. `ToRequest(int expectedRevision, bool dryRun)` builds the contract's `ManagementApplyRequest`. That split is what keeps one field from having two sources.

Extend `Answer` with the three new arms:

```csharp
        catch (DescriptorValidationException refusal)
        {
            return ManagementProblems.Validation(refusal);
        }
        catch (DescriptorConcurrencyException refusal)
        {
            return ManagementProblems.PreconditionFailed(refusal.Message);
        }
        catch (DestructiveChangeNotAllowedException refusal)
        {
            return ManagementProblems.DestructiveChange(refusal);
        }
```

and add `MapApply(group, filters);` to `Map`.

- [ ] **Step 7: Run the apply facts to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementApplyTests`
Expected: PASS — ten facts green.

- [ ] **Step 8: Give the two new slugs a producer probe and a reachability probe**

`ProblemDetailsTests` asserts set equality in both directions, so it is red right now. In `test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs`:

1. Add the two factory results to `EveryFactoryResult()`:

```csharp
        yield return ProblemResultFactory.DestructiveChange("a plan that discards data");
        yield return ProblemResultFactory.PreconditionRequired("send If-Match");
```

2. Add a second world for the two management probes, on the precedent `InternalSlugAnsweredByAFaultingStoreAsync` already sets, and union its answers into `reached`:

```csharp
    /// <summary>
    /// The two management slugs' probes. They need their own world because the Data API maps no route that
    /// can answer either: a destructive refusal and a required precondition are both properties of the
    /// descriptor write path, which is the Management API's.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ManagementSlugsAnsweredByTheDescriptorWriteAsync()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(
                MapManagementApi: true,
                ManagementAccess: MMLib.Alvo.Management.ManagementAccessLevel.Developer));

        const string path = "/management/projects/vehicle-registry/descriptor";
        var current = (await ResponseReading.JsonAsync(await world.SendAsync(HttpMethod.Get, path, _admin)))
            ["descriptorJson"]!.GetValue<string>();

        var required = await world.SendAsync(
            HttpMethod.Put, path, _admin, body: new JsonObject { ["descriptorJson"] = current });
        var destructive = await world.SendAsync(
            HttpMethod.Put, path, _admin,
            body: new JsonObject { ["descriptorJson"] = Management.DescriptorEdits.RemoveEntity(current, "vehicles") },
            headers: [new("If-Match", "\"1\"")]);

        return [await SlugOfAsync(required), await SlugOfAsync(destructive)];
    }
```

`SlugOfAsync` is whatever this file already uses to read a slug off a response — reuse it rather than adding a second reader.

- [ ] **Step 9: Run the catalogue suite and re-accept the OpenAPI snapshot**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.ProblemDetailsTests`
Expected: PASS — both directions of both facts hold with twelve slugs plus two.

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.OpenApiDocumentTests`
Expected: FAIL once — `The_document_is_stable` differs by exactly two entries in the problem `type` enum. Inspect the diff, confirm it is those two lines and nothing else, then:

```bash
cp test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.received.txt \
   test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt
dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.OpenApiDocumentTests
scripts/ensure-vacuum && scripts/lint-api
```
Expected: PASS, and the lint clean at every severity — two enum values add no rule violation.

The Stop hook will block on the moved snapshot and send you to `alvo-snapshot-judge`. The justification is the two constants and their two producers, both in this commit.

- [ ] **Step 10: Commit**

```bash
scripts/test-ring0
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
cp test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.received.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management src/MMLib.Alvo/Api \
        test/MMLib.Alvo.Api.Tests test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(management): apply a descriptor under If-Match revision, with a dry run and a destructive gate"
```

---

### Task 11: `IManagementIdempotencyStore` — the port, over the table the write path already has

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Management/IManagementIdempotencyStore.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/IdempotencyTable.cs` (+a raw find/insert pair)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreManagementIdempotencyStore.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` (register it)
- Create: `src/MMLib.Alvo.Testing/Management/ManagementIdempotencyStoreContractTests.cs`
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/Management/SqliteManagementIdempotencyStoreTests.cs`

**Interfaces:**
- Consumes: `IdempotencyTable.NameFor(string schemaPrefix)`, `.EnsureAsync`, `.FindAsync`, `.InsertAsync` (existing, `internal` to the EF adapter); `AlvoIdempotency.IdentityOf(AlvoContext) -> string`; `AlvoIdempotency.MaxKeyBytes`; `AlvoIdempotencyConflictException`.
- Produces:
  - `public interface MMLib.Alvo.Management.IManagementIdempotencyStore` with
    - `Task<int?> FindAsync(string key, string scope, string fingerprint, CancellationToken ct = default)`
    - `Task RecordAsync(string key, string scope, string fingerprint, int revision, CancellationToken ct = default)`
  - `internal static Task<(string Fingerprint, string RowId)?> IdempotencyTable.FindRecordedAsync(DbConnection, DbTransaction?, string tableName, string key, string scope, CancellationToken)`.
  - `internal static Task InsertRecordedAsync(DbConnection, DbTransaction?, string tableName, string key, string scope, string fingerprint, string rowId, DateTimeOffset createdAt, CancellationToken)`.
  - `public sealed class MMLib.Alvo.Data.EntityFrameworkCore.EfCoreManagementIdempotencyStore : IManagementIdempotencyStore`.
  - `public abstract class MMLib.Alvo.Testing.Management.ManagementIdempotencyStoreContractTests` — the suite every implementation inherits.

**It reuses `{prefix}_idempotency`, and that is correct rather than thrifty.** The table is keyed `(idempotency_key, scope)` with a free-text `row_id`; scope is `tenant/user` (`AlvoIdempotency.IdentityOf`), exactly as on the data path. So one caller reusing one key for a data create and a descriptor apply — two different fingerprints — gets `409 idempotency-conflict`, which is precisely what "the key was reused for a different request" means. No new table, no `AlvoFrameworkTables` entry, no change to `SystemSchemaInitializer`.

**`FindAsync` throws on a fingerprint mismatch.** It returns the recorded revision for a genuine replay and `null` when the key has never been seen; a reused key with a different fingerprint is `AlvoIdempotencyConflictException`, the same type the data path throws, so the HTTP layer maps one exception to one slug.

**The record is written after the commit, not inside it — and the window is stated, not hidden.** `IRuntimeSchemaWriter.ApplyAndAppendAsync` owns its transaction and exposes no seam to enlist in. A crash between that commit and `RecordAsync` leaves the replay unrecorded, so the retry sees a revision that has moved and answers 412 — the caller re-reads and re-sends, which is the same recovery the Data API documents for an ignored `PATCH` key (#102). It is a narrower window than the one it closes, and closing it fully needs a widened `IRuntimeSchemaWriter`. Recorded in `docs/architecture/management-api.md`.

- [ ] **Step 1: Write the failing contract suite**

Create `src/MMLib.Alvo.Testing/Management/ManagementIdempotencyStoreContractTests.cs` — the shape `DescriptorVersionStoreContractTests` already has; read it first and follow it:

```csharp
using MMLib.Alvo.Management;
using Shouldly;

namespace MMLib.Alvo.Testing.Management;

/// <summary>
/// The contract every <see cref="IManagementIdempotencyStore"/> implementation satisfies. Inherited by each
/// engine's suite, so a second driver cannot quietly implement a different replay rule.
/// </summary>
public abstract class ManagementIdempotencyStoreContractTests
{
    /// <summary>Creates a store over a fresh, empty database.</summary>
    protected abstract Task<IManagementIdempotencyStore> CreateAsync();

    [Fact]
    public async Task A_key_nobody_has_used_is_not_a_replay()
    {
        var store = await CreateAsync();

        (await store.FindAsync("k1", "t/u", "fp", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task A_recorded_key_replays_the_revision_it_recorded()
    {
        var store = await CreateAsync();
        await store.RecordAsync("k1", "t/u", "fp", 7, TestContext.Current.CancellationToken);

        (await store.FindAsync("k1", "t/u", "fp", TestContext.Current.CancellationToken)).ShouldBe(7);
    }

    [Fact]
    public async Task One_caller_s_key_never_reaches_another_s_record()
    {
        var store = await CreateAsync();
        await store.RecordAsync("k1", "t/u", "fp", 7, TestContext.Current.CancellationToken);

        (await store.FindAsync("k1", "t/other", "fp", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task A_key_reused_for_a_different_request_is_refused_rather_than_replayed()
    {
        var store = await CreateAsync();
        await store.RecordAsync("k1", "t/u", "fp", 7, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<Data.AlvoIdempotencyConflictException>(
            () => store.FindAsync("k1", "t/u", "a-different-body", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recording_the_same_key_twice_is_refused_rather_than_silently_overwriting()
    {
        var store = await CreateAsync();
        await store.RecordAsync("k1", "t/u", "fp", 7, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<Exception>(
            () => store.RecordAsync("k1", "t/u", "fp", 8, TestContext.Current.CancellationToken));
    }
}
```

Create `test/MMLib.Alvo.Data.Sqlite.Tests/Management/SqliteManagementIdempotencyStoreTests.cs` inheriting it, building the store over the same SQLite fixture the other store suites in that project use (`SqliteAlvoDataFixture`).

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj`
Expected: FAIL at compile — `error CS0246: The type or namespace name 'IManagementIdempotencyStore' could not be found`.

- [ ] **Step 3: Write the port**

Create `src/MMLib.Alvo.Abstractions/Management/IManagementIdempotencyStore.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// Records a management write under a caller-chosen key, so a retry after a lost response is a replay rather
/// than a second apply.
/// </summary>
/// <remarks>
/// <para>
/// <b>It stores the revision, never a rendered response</b> — the same decision the data path's idempotency
/// record makes. A replay re-reads the revision through the ordinary read path, so it can never hand back a
/// representation the caller's current access would not produce.
/// </para>
/// <para>
/// <b>The scope is part of the key.</b> Callers pass <c>AlvoIdempotency.IdentityOf(context)</c>, so one
/// caller's key can never reach another's record — and an anonymous caller, who has no identity to scope by,
/// cannot hold a key at all.
/// </para>
/// </remarks>
public interface IManagementIdempotencyStore
{
    /// <summary>The revision a previous identical request appended, or null when this key is new.</summary>
    /// <param name="key">The caller's idempotency key.</param>
    /// <param name="scope">The caller's identity, from <c>AlvoIdempotency.IdentityOf</c>.</param>
    /// <param name="fingerprint">A hash of the request this key is being used for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded revision, or null.</returns>
    /// <exception cref="Data.AlvoIdempotencyConflictException">
    /// The key exists for this scope under a <em>different</em> fingerprint: it is not a replay, and answering
    /// with the first request's revision would report success for an apply that never happened.
    /// </exception>
    Task<int?> FindAsync(string key, string scope, string fingerprint, CancellationToken ct = default);

    /// <summary>Records that this request appended this revision.</summary>
    /// <param name="key">The caller's idempotency key.</param>
    /// <param name="scope">The caller's identity.</param>
    /// <param name="fingerprint">A hash of the request.</param>
    /// <param name="revision">The revision that was appended.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordAsync(string key, string scope, string fingerprint, int revision, CancellationToken ct = default);
}
```

- [ ] **Step 4: Add the raw table accessors and the EF implementation**

In `IdempotencyTable`, extract the two raw halves the existing `FindAsync`/`InsertAsync` already contain — `FindRecordedAsync` returning the `(fingerprint, row_id)` pair with **no** `Decode`, and `InsertRecordedAsync` taking a `string rowId` with **no** `Encode` — and rewrite the existing pair to call them plus `Decode`/`Encode`. One statement per operation, two callers, no second copy of the SQL.

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreManagementIdempotencyStore.cs`, following `EfCoreDescriptorVersionStore`'s shape for opening a connection and ensuring the table exists (`IdempotencyTable.EnsureAsync`), and:

- `FindAsync` reads the record; returns `null` when absent; throws `AlvoIdempotencyConflictException` when the stored fingerprint differs; otherwise `int.Parse(rowId, CultureInfo.InvariantCulture)`.
- `RecordAsync` inserts with `rowId: revision.ToString(CultureInfo.InvariantCulture)` — the primary key is the concurrency control, so a duplicate insert throws from the engine, which is the fifth contract fact.

Register it in `AlvoEfCoreProvider` beside the descriptor version store, with `TryAdd` so a host can substitute it.

- [ ] **Step 5: Run the contract suite to verify it passes**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj`
Expected: PASS — five contract facts green on SQLite.

- [ ] **Step 6: Commit**

```bash
scripts/test-ring0
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
cp test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/PublicApi.MMLib.Alvo.Data.EntityFrameworkCore.received.txt \
   test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/PublicApi.MMLib.Alvo.Data.EntityFrameworkCore.verified.txt
cp test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.received.txt \
   test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo.Data.EntityFrameworkCore src/MMLib.Alvo.Testing \
        test/MMLib.Alvo.Data.Sqlite.Tests/Management test/*/PublicApi.*.verified.txt
git commit -m "feat(management): record a management write's key, over the table the data path already keeps"
```

---

### Task 12: `Idempotency-Key` honoured on apply

**Files:**
- Create: `src/MMLib.Alvo/Management/Internal/ManagementIdempotency.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (read the header)
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementProblems.cs` (+`IdempotencyConflict`, +the anonymous 422)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementIdempotencyTests.cs`

**Interfaces:**
- Consumes: `IManagementIdempotencyStore` (Task 11); `AlvoIdempotency.IdentityOf(AlvoContext)`, `.EnsureUsableKey(string?, AlvoContext)`, `.MaxKeyBytes`; `IAlvoContextAccessor.Principal`; `IdempotencyFingerprint` as the precedent for canonical hashing.
- Produces: `internal static string ManagementIdempotency.FingerprintOf(string operation, string project, int expectedRevision, bool allowDestructive, string payload)`.

**The existing write-path semantics, item by item:** the record stores the revision, never a body; the scope is `tenant/user`, so an anonymous caller cannot hold a key at all and gets **422**, not 401 (nothing failed authentication); a reused key with a different fingerprint is **409 `idempotency-conflict`**, refused before anything is planned; a key longer than `AlvoIdempotency.MaxKeyBytes` **UTF-8 bytes** is refused, never truncated.

**A dry run with a key is refused (422).** A dry run appends no revision, so there is nothing to replay and nothing to record; accepting the key would file a record for a request that changed nothing and turn the caller's later *real* apply into a replay of a preview.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementIdempotencyTests.cs` with these facts, built on `ManagementApplyTests`' helpers (lift `Apply`/`Current`/`RevisionOf`/`Body` into a shared `ManagementApplyWorld` helper in the same folder rather than copying them):

1. `A_retry_with_the_same_key_and_body_replays_the_first_apply` — two identical `PUT`s with `Idempotency-Key: k1` and `If-Match: "1"`; the second answers **200** with `revision: 2` and `applied: true`, and `GET descriptor` still reports revision **2**, not 3.
2. `A_retry_without_a_key_is_a_second_apply_that_loses_the_race` — the same two requests with no key; the second is **412**, because the revision moved. This is the hole the key closes, asserted so the first fact cannot pass vacuously.
3. `The_same_key_with_a_different_body_is_409_idempotency_conflict` — second request sends a different descriptor; **409**, slug `idempotency-conflict`, and the revision stays at 2.
4. `An_anonymous_caller_sending_a_key_is_422` — no credential presented, `ManagementAccess: Developer` so the gate admits anonymous; **422**, slug `validation`, and the detail says a key needs a stable identity to scope by.
5. `A_key_over_the_byte_bound_is_refused_rather_than_truncated` — a key of `AlvoIdempotency.MaxKeyBytes + 1` bytes; **422**.
6. `A_dry_run_may_not_carry_a_key` — `?dryRun=true` plus `Idempotency-Key`; **422**, and the detail says a dry run appends nothing to replay.
7. `Two_callers_may_use_the_same_key_without_colliding` — two keys, two users, same `Idempotency-Key`, both apply (the second with `If-Match: "2"`); revision reaches 3, so neither replayed the other.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementIdempotencyTests`
Expected: FAIL — fact 1 answers **412** on the retry (the header is inert today, so the retry is a second apply against a moved revision), and facts 3–6 answer 200 or 412 instead of 422/409.

- [ ] **Step 3: Write the fingerprint**

Create `src/MMLib.Alvo/Management/Internal/ManagementIdempotency.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// What makes two management writes "the same request" for replay purposes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every input that changes the outcome is in the hash</b>, on <c>IdempotencyFingerprint</c>'s precedent:
/// the operation, the project, the expected revision, the destructive allowance and the payload. A retry that
/// flipped <c>allowDestructive</c> is a different request and must not replay the refusal — or the success.
/// </para>
/// <para>
/// <b>The descriptor is hashed as sent, not canonicalised.</b> The Data API canonicalises a body because two
/// key orders are one record; a descriptor is stored verbatim and exported verbatim, so two spellings are two
/// different things to store and hashing them alike would replay the wrong text.
/// </para>
/// </remarks>
internal static class ManagementIdempotency
{
    internal static string FingerprintOf(
        string operation, string project, int expectedRevision, bool allowDestructive, string payload)
    {
        var input = new StringBuilder(operation)
            .Append('\n').Append(project)
            .Append('\n').Append(expectedRevision)
            .Append('\n').Append(allowDestructive)
            .Append('\n').Append(payload);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
    }
}
```

- [ ] **Step 4: Honour the key in the service**

In `AlvoManagementService`, take `IManagementIdempotencyStore? idempotency` and `IAlvoContextAccessor callers` as further constructor parameters, and wrap the apply:

```csharp
    private async Task<ManagementApplyResult> ApplyAsync(
        string project, ManagementApplyRequest request, MigrationOptions options, CancellationToken ct)
    {
        var token = TokenFor(request, project);
        if (token is { } key)
        {
            var replayed = await Store.FindAsync(key.Key, key.Scope, key.Fingerprint, ct).ConfigureAwait(false);
            if (replayed is { } revision)
            {
                return await ReplayAsync(project, revision, ct).ConfigureAwait(false);
            }
        }

        var applied = await ApplyAndSummariseAsync(project, request, options, ct).ConfigureAwait(false);
        if (token is { } recorded)
        {
            await Store.RecordAsync(
                recorded.Key, recorded.Scope, recorded.Fingerprint, applied.Revision, ct).ConfigureAwait(false);
        }

        return applied;
    }

    private async Task<ManagementApplyResult> ReplayAsync(string project, int revision, CancellationToken ct)
    {
        var stored = await versions.GetAsync(project, revision, ct).ConfigureAwait(false)
            ?? throw new ManagementRevisionNotFoundException(project, revision);

        return new ManagementApplyResult(
            Applied: true, stored.Revision, new ManagementPlanSummary(IsEmpty: true, false, []));
    }
```

with `TokenFor` doing the refusals, each as its own named guard so the method stays short:

```csharp
    private ManagementIdempotencyToken? TokenFor(ManagementApplyRequest request, string project)
    {
        if (request.IdempotencyKey is not { } key)
        {
            return null;
        }

        RefuseAKeyOnADryRun(request);
        var caller = callers.Principal?.Context ?? AlvoContext.Anonymous;
        AlvoIdempotency.EnsureUsableKey(key, caller);

        return new ManagementIdempotencyToken(
            key,
            AlvoIdempotency.IdentityOf(caller),
            ManagementIdempotency.FingerprintOf(
                nameof(ApplyDescriptorAsync), project, request.ExpectedRevision, request.AllowDestructive,
                request.DescriptorJson));
    }
```

`AlvoIdempotency.EnsureUsableKey` already refuses a blank key, an over-long one (in UTF-8 bytes) and an anonymous holder, with the wordings the Data API publishes — reuse it rather than re-deciding any of the three. `RefuseAKeyOnADryRun` throws `ManagementSimulationException`… no: introduce `ManagementRequestException` for "this request is well-formed and cannot be served", and map both it and the exception `EnsureUsableKey` throws to `ManagementProblems.Validation`. Read what `EnsureUsableKey` throws before writing the arm, and map exactly that type.

`Store` is `idempotency ?? throw new InvalidOperationException(...)` naming the missing registration — a host with a key and no store must fail loudly, not silently ignore the header, because ignoring a key is the lost-retry the header exists to prevent.

- [ ] **Step 5: Read the header and map the two refusals**

In `ManagementEndpoints.ApplyAsync`, read `Idempotency-Key` off the request (refusing a repeated header, which is an ambiguity, exactly as the Data API does) and pass it into `ManagementApplyBody.ToRequest`. In `ManagementProblems`, add:

```csharp
    /// <summary>The 409 for a key reused for a different request.</summary>
    internal static IResult IdempotencyConflict() => ProblemResultFactory.Problem(
        StatusCodes.Status409Conflict,
        AlvoProblemTypes.IdempotencyConflict,
        "This 'Idempotency-Key' was already used for a different request. Send a fresh key with this body, "
        + "or resend the original body to replay it.");
```

and extend `Answer` with `catch (AlvoIdempotencyConflictException)` → `IdempotencyConflict()` and `catch (ManagementRequestException refusal)` → `Validation(refusal.Message)`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementIdempotencyTests`
Expected: PASS — seven facts green.

- [ ] **Step 7: Commit**

```bash
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management test/MMLib.Alvo.Api.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): honour Idempotency-Key on apply, with the write path's own semantics"
```

---

### Task 13: `POST {m}/projects/{p}/revisions/{n}/rollback` — the tenth member, the tenth route

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Management/IAlvoManagement.cs` (+1 member)
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementModels.cs` (+1 record)
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (+1 route)
- Test: `test/MMLib.Alvo.Api.Tests/Management/ManagementRollbackTests.cs`

**Interfaces:**
- Consumes: `RuntimeSchemaService.RollbackAsync(string project, int targetRevision, MigrationOptions, CancellationToken) -> Task<DescriptorVersion>`; `IDescriptorVersionStore.GetAsync`; `RuntimeSchemaService.PreviewAsync` (for the plan summary and the dry run).
- Produces:
  - `Task<ManagementApplyResult> RollbackAsync(string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default)`.
  - `public sealed record ManagementRollbackRequest(int ExpectedRevision, bool AllowDestructive = false, bool DryRun = false, string? Author = null, string? Reason = null, string? IdempotencyKey = null)`.

**`allowDestructive` is explicit, never implied** (spec §2.2). A rollback generates a reverse migration, and a reverse migration routinely drops what the forward one added — so the same gate that guards an apply guards this, with the same 409 and the same slug.

**A rollback is an apply of a past descriptor**, which is why it reuses `PreviewAsync` for the plan summary and the same `If-Match` rule for the base: the target revision says *what* to restore, `If-Match` says *from where*. Two different numbers with two different jobs, and conflating them would let a caller roll back from a state they never saw.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/Management/ManagementRollbackTests.cs` with these facts (reusing `ManagementApplyWorld` from Task 12):

1. `A_rollback_restores_the_target_descriptor_and_appends_a_revision` — apply a second revision that adds a field, then `POST .../revisions/1/rollback` with `If-Match: "2"` and `allowDestructive: true`; **200**, `revision: 3`, and `GET descriptor` returns revision 3 whose text equals revision 1's.
2. `The_appended_revision_records_what_it_rolled_back_from` — `GET .../revisions/3` reports `rolledBackFrom: 1`.
3. `A_rollback_that_would_discard_data_is_409_unless_it_was_asked_for` — the same call without `allowDestructive`; **409**, slug `destructive-change`, and the current revision stays at 2.
4. `A_rollback_without_if_match_is_428` — no header; **428**, slug `precondition-required`.
5. `A_rollback_to_a_revision_that_does_not_exist_is_404` — `.../revisions/99/rollback` with a valid `If-Match`; **404**, slug `not-found`.
6. `A_viewer_may_not_roll_back` — `ManagementAccess: Viewer`; **403**.
7. `A_retried_rollback_with_the_same_key_replays_rather_than_rolling_back_twice` — two identical calls with `Idempotency-Key: r1`; the second answers the first's revision and `GET .../revisions` still has three entries.
8. `A_rollback_dry_run_reports_the_reverse_plan_and_appends_nothing` — `?dryRun=true`; `applied: false`, a non-empty `plan.steps`, and the revision unchanged.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementRollbackTests`
Expected: FAIL — every fact 404s, because no route matches `POST .../rollback`.

- [ ] **Step 3: Add the member and its request**

In `IAlvoManagement`:

```csharp
    /// <summary>
    /// Restores a past revision by appending the reverse migration as a new revision. History is never
    /// rewritten.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="targetRevision">The revision to restore.</param>
    /// <param name="request">The expected current revision, the allowances, and the provenance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended revision, or — for a dry run — the reverse plan and the base it was planned against.</returns>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementRevisionNotFoundException">That revision was never appended.</exception>
    /// <exception cref="Migrations.DescriptorConcurrencyException">The expected revision is not the current one.</exception>
    /// <exception cref="Migrations.DestructiveChangeNotAllowedException">The reverse plan discards data and it was not allowed.</exception>
    Task<ManagementApplyResult> RollbackAsync(
        string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default);
```

In `ManagementModels.cs`:

```csharp
/// <summary>One rollback.</summary>
/// <param name="ExpectedRevision">
/// The revision the caller believes is current — the base, from <c>If-Match</c>. Distinct from the target
/// revision, which is in the route: the target says what to restore, this says from where.
/// </param>
/// <param name="AllowDestructive">
/// Whether the reverse migration may discard data. A reverse migration routinely drops what the forward one
/// added, so this is explicit and never implied.
/// </param>
/// <param name="DryRun">Plan the reverse migration and report it without writing anything.</param>
/// <param name="Author">Who is rolling back, carried into the appended revision.</param>
/// <param name="Reason">Why; defaults to the framework's own "Rollback to revision N" when absent.</param>
/// <param name="IdempotencyKey">A caller-chosen key making a retry a replay rather than a second rollback.</param>
public sealed record ManagementRollbackRequest(
    int ExpectedRevision,
    bool AllowDestructive = false,
    bool DryRun = false,
    string? Author = null,
    string? Reason = null,
    string? IdempotencyKey = null);
```

- [ ] **Step 4: Implement it**

In `AlvoManagementService`, mirroring the apply path and reusing the same idempotency helper — the only differences are the target lookup, the plan's direction, and the fingerprint's operation name:

```csharp
    /// <inheritdoc/>
    public async Task<ManagementApplyResult> RollbackAsync(
        string project, int targetRevision, ManagementRollbackRequest request, CancellationToken ct = default)
    {
        EnsureServed(project);
        ArgumentNullException.ThrowIfNull(request);

        var target = await versions.GetAsync(project, targetRevision, ct).ConfigureAwait(false)
            ?? throw new ManagementRevisionNotFoundException(project, targetRevision);
        var options = OptionsFor(request.AllowDestructive, request.Author, request.Reason);

        return request.DryRun
            ? Preview(await runtime.PreviewAsync(
                project, target.DescriptorJson, request.ExpectedRevision, options, ct).ConfigureAwait(false))
            : await RollBackAsync(project, target, request, options, ct).ConfigureAwait(false);
    }
```

with a `RollBackAsync` that takes the same `TokenFor`-shaped guard (its fingerprint built from `nameof(RollbackAsync)`, the project, `request.ExpectedRevision`, `request.AllowDestructive` and `targetRevision.ToString(CultureInfo.InvariantCulture)` as the payload), previews for the summary, calls `runtime.RollbackAsync`, and records the key. Extract the shared replay/record shape so apply and rollback do not grow two copies of it.

- [ ] **Step 5: Map the route**

```csharp
    private static void MapRollback(RouteGroupBuilder group, ManagementAccessFilterFactory filters) =>
        Gate(
            group.MapPost("/projects/{project}/revisions/{revision:int}/rollback", RollbackAsync),
            filters,
            new ManagementOperation(nameof(IAlvoManagement.RollbackAsync), ManagementAccessLevel.Developer));
```

with a `RollbackAsync` delegate shaped exactly like the apply one: three arms on `Revision(request)` — absent → 428, uncomparable → 412, otherwise `Answer(...)`. Extract that three-arm dispatch into one `WithRequiredPrecondition(HttpRequest, Func<int, Task<IResult>>)` helper so the two write endpoints share it rather than repeating it.

Add `MapRollback(group, filters);` to `Map`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementRollbackTests`
Expected: PASS — eight facts green.

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj --filter-class MMLib.Alvo.Api.Tests.Management.ManagementContractTests`
Expected: PASS — **ten members, ten routes.** This is the surface spec §2.2 lists, complete.

- [ ] **Step 7: Commit**

```bash
scripts/test-ring0
cp test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt \
   test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management src/MMLib.Alvo/Management test/MMLib.Alvo.Api.Tests/Management \
        test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): roll back to a revision, with the destructive gate the reverse plan needs"
```

---

### Task 14: Wire it into the host, document it, and prove the whole thing in ring2

**Files:**
- Modify: `src/MMLib.Alvo/AlvoEndpointRouteBuilderExtensions.cs` (`MapAlvo` maps it)
- Modify: `src/MMLib.Alvo.Host/AlvoHost.cs` (set `ModeLabel`)
- Create: `docs/architecture/management-api.md`
- Modify: `docs/architecture/data-api.md` (the slug table gains two rows)
- Modify: `docs/architecture/package-boundary.md` (§Current projects — no new project; record why)
- Modify: `docs/architecture/host.md` (the management route prefix)
- Modify: `docs/PLAN.md` (`← YOU ARE HERE`)
- Test: `test/MMLib.Alvo.Host.Tests/ManagementRouteTests.cs`

**Interfaces:**
- Consumes: everything the previous thirteen tasks produced.
- Produces: no new type. `MapAlvo()` maps health, the Data API and the Management API.

**`MapAlvo` maps it, and it is still safe.** `MapAlvo` already maps the Data API, whose routes are all default-deny; the management routes are default-deny too, and harder — the shipped `IManagementAccessPolicy` grants nobody anything, so a host that calls `MapAlvo()` and configures no policy has a surface that answers 403 to everyone, including itself. That is the correct resting state for a build that honours no `access` block. `MapAlvo` keeps returning the route builder rather than a convention builder, for the reason its own remarks already give.

- [ ] **Step 1: Write the failing host fact**

Create `test/MMLib.Alvo.Host.Tests/ManagementRouteTests.cs`, following whatever shape that project already uses to stand up the host:

```csharp
using Shouldly;
using System.Net;

namespace MMLib.Alvo.Host.Tests;

public class ManagementRouteTests
{
    [Fact]
    public async Task MapAlvo_mounts_the_management_surface_and_it_is_closed_by_default()
    {
        await using var host = await AlvoHostWorld.StartAsync();

        var response = await host.Client.GetAsync("/management/info", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            "the surface is mounted and nobody may reach it, which is what this build honouring no 'access' means");
    }

    [Fact]
    public async Task The_standalone_host_says_it_is_standalone()
    {
        await using var host = await AlvoHostWorld.StartAsync(management: ManagementAccessLevel.Viewer);

        var info = await host.JsonAsync("/management/info");

        info["mode"]!.GetValue<string>().ShouldBe("standalone");
    }
}
```

If `MMLib.Alvo.Host.Tests` has no world that can substitute an `IManagementAccessPolicy`, add the one-line seam its existing world already has for services — do not add a second world.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj --filter-class MMLib.Alvo.Host.Tests.ManagementRouteTests`
Expected: FAIL — **404**, because `MapAlvo` does not map the management routes.

- [ ] **Step 3: Map it from `MapAlvo` and label the host's mode**

In `src/MMLib.Alvo/AlvoEndpointRouteBuilderExtensions.cs`:

```csharp
        endpoints.MapAlvoHealth();
        endpoints.MapAlvoDataApi();
        endpoints.MapAlvoManagementApi();
```

and extend the method's remarks with one paragraph: the management surface is mounted here and is closed by default, because the shipped `IManagementAccessPolicy` grants nobody anything; #146 is what opens it.

In `src/MMLib.Alvo.Host/AlvoHost.cs`, where the host configures Alvo, add:

```csharp
        builder.Services.Configure<AlvoManagementOptions>(options => options.ModeLabel = "standalone");
```

with a comment-free justification carried by the name: the core defaults to the registered `AlvoMode`, and the standalone image is the one deployment that knows its own answer without being told.

- [ ] **Step 4: Run the host facts to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj --filter-class MMLib.Alvo.Host.Tests.ManagementRouteTests`
Expected: PASS — two facts green.

- [ ] **Step 5: Write `docs/architecture/management-api.md`**

One document, in the house style of `docs/architecture/data-api.md` — claims first, costs stated where they are paid. It must contain, each as its own section:

- **The surface**, as a table: the ten routes, the `IAlvoManagement` member each stands for, and the level each requires. Say that the table is generated from nothing and held by `ManagementContractTests` instead.
- **One path, two transports** — why the dashboard calls the service and an agent calls HTTP, and why that is not a divergent path under spec §0.5 contract 4.
- **D3: `If-Match` carries `revision`** — the frozen `schema/project.schema.json:49`, the "two answers in the repo for one decision" argument, and that no `ETag` is emitted because there is no row version to mint one from.
- **`If-Match` is required, and 428 is why** — the Data API's "every precondition this API cannot evaluate is refused" rule, applied to the header's absence.
- **The two added slugs**, `destructive-change` and `precondition-required`, and why each is a different fix from every slug already in the catalogue.
- **`Idempotency-Key`** — what is stored (the revision, never a body), the scope, the 409, and **the post-commit window**, stated plainly: the record is written after `ApplyAndAppendAsync` commits, so a crash in between costs a replay and the retry answers 412.
- **Dry run is `PreviewAsync`, not `MigrationOptions.DryRun`** — the runtime path refuses that flag by design, and this is the plan-only operation its refusal names. Also: the apply path plans twice, and what that buys.
- **What is deliberately absent** — data (D4), `access` enforcement (#146), identity (#248), multi-project (§2.6), the record-id arm of the simulator, and an OpenAPI document of its own (with the reason: the published document is the generated Data API contract, pinned three ways).
- **`info` reports the data provider, not the engine** — the core may not reference the adapter that knows an engine's name.

- [ ] **Step 6: Update the four existing documents**

- `docs/architecture/data-api.md`, §"The status and `type`-slug catalogue": add two rows — `409 destructive-change` and `428 precondition-required` — each marked *Management API only*, because that section states the slugs are exactly `AlvoProblemTypes.All` and would otherwise be wrong.
- `docs/architecture/package-boundary.md`: no project is added; add one sentence to §Consequence confirming that the Management API landed inside the core exactly as that section already predicted, with the design's §1.1 table as the citation. The file's own instruction is *"Keep this list current."*
- `docs/architecture/host.md`, §Configuration: add `Alvo__Management__RoutePrefix` beside the schema keys, defaulting to `/management`, and note that the surface is mounted by `MapAlvo` and closed by default.
- `docs/PLAN.md`: move `← YOU ARE HERE` and record #212 as done. Let `alvo-plan-guard` propose the exact shift rather than guessing it.

- [ ] **Step 7: Run every ring**

```bash
scripts/test-ring0
scripts/test-ring1
scripts/test-ring2
```
Expected: PASS at each. `scripts/test-ring2` runs `scripts/ensure-vacuum` and `scripts/lint-api` over the document snapshot moved in Task 10; if the lint is not clean, fix the ruleset conformance rather than muting a rule.

- [ ] **Step 8: Dispatch the pre-PR checks**

In order, per CLAUDE.md's Hard rules:

1. `alvo-plan-guard` — read-only, advisory; fix what it flags before going on.
2. A reviewer subagent as the substitute for `/code-review medium` (those slash commands are user-only here), and a second as the substitute for `/security-review` — the diff touches the authorization seam and a write path, which is the security core's own trigger. Pair the latter with the `alvo-security-core-review` checklist.
3. Freeze the working tree before dispatching either: a reviewer reading a tree you are editing gives a verdict that does not cover the final diff.
4. The `alvo-pr-report` skill, after plan-guard and before `gh pr create`.

- [ ] **Step 9: Commit and open the PR**

```bash
git add src/MMLib.Alvo/AlvoEndpointRouteBuilderExtensions.cs src/MMLib.Alvo.Host \
        test/MMLib.Alvo.Host.Tests docs/
git commit -m "docs(management): document the surface, its two adopted conventions, and what it deliberately lacks"
git push -u origin feat/212-management-api
```

Confirm you are on `feat/212-management-api` before pushing — a read-only reviewer subagent can `git checkout` under you.

---

## Self-review

Run against the spec after the plan was written; findings fixed inline above.

**1. Spec coverage.**

| Spec | Where |
|---|---|
| §1.2 `IAlvoManagement` in Abstractions | Task 2 |
| §1.2 `AlvoManagementService` over RuntimeSchemaService / IDescriptorVersionStore / ISchemaRegistry / IPolicyEngine | Tasks 2, 4, 5, 6, 8, 10, 13 |
| §1.2 `ManagementEndpoints` as minimal-API delegates | Task 3, extended each task after |
| §2.2 `GET {m}/projects` | Task 4 |
| §2.2 `GET {m}/projects/{p}/descriptor` (the export) | Task 4 |
| §2.2 `PUT {m}/projects/{p}/descriptor`, `If-Match`, `?dryRun=true`, `Idempotency-Key` | Tasks 9, 10, 12 |
| §2.2 `GET {m}/projects/{p}/revisions` and `/{n}` | Task 5 |
| §2.2 `POST .../revisions/{n}/rollback`, `allowDestructive` explicit | Task 13 |
| §2.2 `GET {m}/projects/{p}/schema` | Task 6 |
| §2.2 `GET {m}/projects/{p}/capabilities` | Task 7 |
| §2.2 `POST {m}/projects/{p}/policy/simulate` | Task 8 |
| §2.2 `GET {m}/info` | Task 2, 3 |
| §2.3 `warned` from `UnhonouredSubsystems.All`, `refused` from `UnhonouredFeatures`, prose verbatim | Task 7 |
| §2.5 RFC 7807, one slug catalogue | Tasks 3, 4, 8, 10, 12 |
| §2.5 `Idempotency-Key`, existing write-path semantics | Tasks 11, 12 |
| §2.5 `If-Match` carrying `revision` (D3) | Task 10, documented Task 14 |
| §2.5 descriptor-shaped and idempotent, so MCP is a mapping | The contract takes its revision and its key in the request, not from headers — Tasks 10, 13 |
| §2.6 one project | Task 4 |
| §6.1 every `IAlvoManagement` member has an HTTP route | Task 3, re-run in Tasks 5, 8, 13 |
| §6.1 the simulator answers as production does | Task 8, fact 3 |
| §6.1 default-deny: no policy match → 403 everywhere | Task 3 |
| D2 `Alvo:*` spelling | Task 1 |
| D4 no admin bypass | No data surface exists; stated in Task 14's document |

**Gaps recorded rather than silently dropped**, each with its reason in the task that owns it: the simulator's record-id arm (Task 8), an OpenAPI document for the management surface (Task 3), `info`'s engine name (Task 2), and the post-commit idempotency window (Task 11). Out-of-scope items (#146 enforcement, identity, UI, CLI, MCP, multi-project) are in Global Constraints.

**2. Placeholder scan.** No "TBD", no "add appropriate error handling", no "similar to Task N". Every step carries either real code or a real command. Four steps describe a file's *contents as a list of required sections* rather than quoting them — Task 12 step 1, Task 13 step 1, Task 14 step 5, Task 14 step 6 — and each enumerates every fact or section by name, its status code, and its assertion, which is what an implementer needs to write it without a second decision. Three steps say "read the existing file first and reuse what is there" (`ResponseReading`, `RuntimeSchemaServiceTests`' fixtures, `DescriptorValidationResult`'s members): that is deliberate, because inventing a second reader or a second fixture is the failure those sentences prevent.

**3. Type consistency.** Checked across tasks: `IAlvoManagement` is referred to by that name throughout; `ManagementApplyResult` is the return of both `ApplyDescriptorAsync` and `RollbackAsync`; `ManagementPlanSummary` is built only by `Summary(MigrationPlan)`; `ManagementOperation(Member, Required)` is constructed only with `nameof`; `ManagementProblems.Validation` has two overloads (a `string` and a `DescriptorValidationException`) and both are introduced where first used; `DescriptorApplyPreview(Plan, CurrentRevision, AllowedByGuardrail)` is consumed in Tasks 10 and 13 exactly as Task 9 defines it; `IManagementIdempotencyStore.FindAsync/RecordAsync` signatures match between Tasks 11 and 12; `AlvoApiWorldSetup`'s two new members (`MapManagementApi`, `ManagementAccess`) are added once, in Task 3, and used by every later HTTP task under those names.
