# F4 PR-C — readiness reachability, endpoint conventions, and the document's origin: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close #130 and #119 by pinning behaviour that is already correct and correcting the record; close #133 by adding the database-reachability port, its one EF implementation and a readiness contributor; and give a host the idiomatic seam to attach conventions to Alvo's generated routes.

**Architecture:** One new port in `Abstractions` (`IAlvoDataReachability` + `AlvoReachability`), one implementation at the shared EF seam over `RelationalConnectionFactory` and a new `IAlvoSqlDialect` default member, one `IHealthCheck` in the core tagged `AlvoHealth.ReadyTag` with `HealthCheckRegistration.Timeout`. `MapAlvoDataApi` returns an `IEndpointConventionBuilder` whose conventions are threaded through `DataApiEndpoints.Protect`, so a route that carries the authorization filter and the marker carries the host's conventions by the same construction.

**Tech Stack:** .NET 10 (`net10.0`), SDK `10.0.100`, xUnit v3 on Microsoft.Testing.Platform, Shouldly, Verify, NSubstitute, PublicApiGenerator, Testcontainers (PostgreSQL leg only).

**Spec:** `docs/superpowers/specs/2026-08-31-f4-pr-c-readiness-conventions-and-document-origin-design.md`

## Global Constraints

- **Every `.cs` file is UTF-8 **with BOM** and **CRLF**** (`.editorconfig`: `charset = utf-8-bom`, `end_of_line = crlf`). Editing a `.cs` through Bash heredoc / `sed` / Python produces LF without BOM; build and tests still pass and the commit fails on Husky's `dotnet format` with `error ENDOFLINE` / `error CHARSET`. Normalize every touched `.cs` before committing:
  ```python
  t = io.open(p,'rb').read().decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
  io.open(p,'w',encoding='utf-8-sig',newline='').write(t)
  ```
  `.md`, `.csx` and `.http` are UTF-8 **without** BOM and LF — do not normalize those.
- **Warnings are errors**, `CA1848` included: every log call is a source-generated `[LoggerMessage]` partial, never `logger.LogError(...)`.
- **XML doc comments are required on every public member** of `Abstractions` and the core.
- **Zero inline comments by default.** A `//` explaining *what* or *why* is the signal to extract a named method or lift a named constant. Rationale that a name cannot carry goes in `/// <remarks>`.
- **Methods stay short and single-purpose** — rough ceiling ~25 lines; extract by default.
- **Assertions are Shouldly.** No FluentAssertions.
- **Never push to `main`.** Branch `f4/pr-c-readiness-conventions-and-document-origin` → PR → a human merges.
- **`scripts/test-ring0`** after every task; **`scripts/test-ring2`** before the PR. Never run mutation or e2e locally.
- A moved `*.verified.txt` baseline triggers the Stop-hook gate: dispatch the read-only `alvo-snapshot-judge` when it fires.

---

### Task 1: #119 — verify, correct the slug count, and record the verification

**Files:**
- Modify: `docs/architecture/data-api.md` (the "The status and `type`-slug catalogue" paragraph)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Run the two suites that own #119's claim**

```bash
dotnet build MMLib.Alvo.slnx -c Debug
dotnet test --test-modules "test/MMLib.Alvo.Host.Tests/bin/Debug/net10.0/MMLib.Alvo.Host.Tests.dll" \
  --root-directory . --filter-class "*AlvoHostProblemDetailsTests"
dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/net10.0/MMLib.Alvo.Api.Tests.dll" \
  --root-directory . --filter-class "*ProblemDetailsTests"
```

Expected: both PASS. Record the fact names in the PR notes; they are what closes #119.

If either fails, **stop** — #119 is not delivered after all and this task becomes real work rather than a verification.

- [ ] **Step 2: Confirm the catalogue's real size, from the code**

```bash
grep -c '^        [A-Z][A-Za-z]*,$' src/MMLib.Alvo/Api/AlvoProblemTypes.cs
```

Expected: `11`. The prose in `docs/architecture/data-api.md` says nine.

- [ ] **Step 3: Correct the sentence**

In `docs/architecture/data-api.md`, find:

```
Every `type` is `https://alvo.dev/errors/<slug>`; the nine slugs are `AlvoProblemTypes.All`.
```

Replace with:

```
Every `type` is `https://alvo.dev/errors/<slug>`; the slugs are exactly `AlvoProblemTypes.All`, and the
table below is that list. Two of them — `unreadable-request` and `internal` — are emitted only by
`AlvoExceptionHandler`, so only a host that called `AddAlvoProblemDetails()` can produce one, which is why
neither is documented on any operation.
```

A count in prose is what drifted; naming the property and dropping the number is what stops it drifting again.

- [ ] **Step 4: Commit**

```bash
git add docs/architecture/data-api.md docs/superpowers/specs docs/superpowers/plans
git commit -m "docs(api): the slug catalogue is AlvoProblemTypes.All, not a number in prose (#119)"
```

---

### Task 2: #130 — the core fact: the document's origin carries the request's path base

**Files:**
- Create: `test/MMLib.Alvo.Api.Tests/OpenApiServersTests.cs`
- Test: the same file

**Interfaces:**
- Consumes: `AlvoApiWorld.VehicleRegistryAsync(keys, setup)`, `AlvoApiWorldSetup(MapOpenApiDocument:, PathBase:, RouteGroupPrefix:)`, `AlvoApiWorld.SendAsync(method, path, key)`, `ResponseReading.ReadTextAsync`.
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/OpenApiServersTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// #130: what a client resolves the document's path keys <em>against</em>. The path keys themselves are
/// <see cref="OpenApiDocumentTests.Every_mapped_route_appears_in_the_document_and_nothing_else_does"/>'s;
/// this file owns <c>servers[0].url</c>, which is what makes those keys reachable or wrong by a prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>The origin is pinned whole and then followed, and both halves are needed.</b> In-process a host under a
/// path base answers the unprefixed URL too — <c>UsePathBase</c> strips a prefix when the request carries one
/// rather than requiring one — so "resolve a path key and get 200" passes for a document that advertises
/// <c>http://localhost/</c> while every path in it is wrong by the prefix at the edge. That is #121's own
/// lesson, and <c>PathBaseTests</c> pins its <c>Location</c> whole for the same reason. The follow-up runs
/// anyway, because a URL that resolves nowhere is the failure a client actually meets and a string comparison
/// passes for one.
/// </para>
/// <para>
/// The scheme and host halves of the same value are pinned by
/// <c>MMLib.Alvo.Host.Tests.AlvoHostForwardedOriginTests</c>; the path-base half was pinned by nothing, which
/// is how #130 stood open through a release describing a defect this runtime does not have.
/// </para>
/// </remarks>
public class OpenApiServersTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    private const string PathBase = "/alvo";

    /// <summary>With no path base the origin is the bare root, so the fix below is additive.</summary>
    [Fact]
    public async Task With_no_path_base_the_document_advertises_the_bare_origin()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true));

        var origin = await OriginAsync(world, "/openapi/v1.json");

        origin.ShouldBe("http://localhost/");
    }

    /// <summary>
    /// The shape #130 names: served under <c>app.UsePathBase("/alvo")</c> and fetched under it, the origin
    /// carries the prefix and a path key resolved against it reaches the endpoint.
    /// </summary>
    [Fact]
    public async Task Behind_a_path_base_the_documents_origin_carries_it_and_a_path_key_resolves()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true, PathBase: PathBase));

        var document = await DocumentAsync(world, $"{PathBase}/openapi/v1.json");
        var origin = Origin(document);
        var resolved = Resolve(origin, CollectionPathKey(document));

        await FollowingItAnswersOkAsync(world, resolved);
        origin.ShouldBe($"http://localhost{PathBase}");
        resolved.ShouldBe($"http://localhost{PathBase}/api/owners");
    }

    /// <summary>
    /// The other supported mount: a route group's prefix belongs to the <em>route</em>, so it is in the path
    /// key and the origin stays bare. Named because the opposite mistake — putting a group prefix into
    /// <c>servers</c> — would double it for every client that resolves.
    /// </summary>
    [Fact]
    public async Task Under_a_route_group_the_prefix_is_in_the_path_key_and_not_in_the_origin()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin], new AlvoApiWorldSetup(MapOpenApiDocument: true, RouteGroupPrefix: "/backend"));

        var document = await DocumentAsync(world, "/openapi/v1.json");
        var resolved = Resolve(Origin(document), CollectionPathKey(document));

        await FollowingItAnswersOkAsync(world, resolved);
        Origin(document).ShouldBe("http://localhost/");
        resolved.ShouldBe("http://localhost/backend/api/owners");
    }

    private static async Task<string> OriginAsync(AlvoApiWorld world, string path) =>
        Origin(await DocumentAsync(world, path));

    private static async Task<JsonObject> DocumentAsync(AlvoApiWorld world, string path)
    {
        using var response = await world.SendAsync(HttpMethod.Get, path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());

        return JsonNode.Parse(await response.ReadTextAsync())!.AsObject();
    }

    /// <summary>The one origin the document advertises, read the way a generated client reads it.</summary>
    private static string Origin(JsonObject document)
    {
        var servers = document["servers"]!.AsArray();

        servers.Count.ShouldBe(
            1, $"the document must advertise exactly one server; it advertised {servers.Count}");

        return servers[0]!["url"]!.GetValue<string>();
    }

    /// <summary>The <c>owners</c> collection path key, taken from the document rather than written here.</summary>
    private static string CollectionPathKey(JsonObject document) =>
        document["paths"]!.AsObject()
            .Select(path => path.Key)
            .First(key => key.EndsWith("/api/owners", StringComparison.Ordinal));

    /// <summary>
    /// One path key resolved against the advertised origin, exactly as RFC 3986 and every generated client do
    /// it — which is what makes a missing prefix in the origin observable rather than a matter of taste.
    /// </summary>
    private static string Resolve(string origin, string pathKey) =>
        new Uri(new Uri(origin.EndsWith('/') ? origin : origin + "/"), pathKey.TrimStart('/')).ToString();

    private static async Task FollowingItAnswersOkAsync(AlvoApiWorld world, string resolved)
    {
        using var response = await world.SendAsync(HttpMethod.Get, resolved, _admin);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"a client that resolves a path key against the advertised origin must reach the endpoint; "
                + $"'{resolved}' did not");
    }
}
```

- [ ] **Step 2: Run it and read what it says**

```bash
dotnet build test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj -c Debug
dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/net10.0/MMLib.Alvo.Api.Tests.dll" \
  --root-directory . --filter-class "*OpenApiServersTests"
```

Expected: **PASS**, all three. That is the whole point of the task — the behaviour is already right (measured in the spec) and was pinned by nothing.

**If any fails, stop and report the actual origin.** A failure here means the measurement in the spec does not reproduce, and #130 becomes production work rather than a fact.

- [ ] **Step 3: Prove the fact is not vacuous**

Temporarily add, at the top of `AlvoDocumentTransformer.TransformAsync` (after the null guards):

```csharp
document.Servers = [];
```

Re-run the three facts. Expected: the path-base fact and the no-path-base fact both FAIL on `Origin`'s
"advertised exactly one server" message. Then **revert the temporary line** (`git checkout -- src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs`) and re-run to confirm green.

Record the observed failure text in the PR notes: a fact whose non-vacuity was demonstrated is worth
more than one that merely passed.

- [ ] **Step 4: Normalize and commit**

```bash
python3 - <<'PY'
import io
p='test/MMLib.Alvo.Api.Tests/OpenApiServersTests.cs'
t=io.open(p,'rb').read().decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
io.open(p,'w',encoding='utf-8-sig',newline='').write(t)
PY
git status --short
git add test/MMLib.Alvo.Api.Tests/OpenApiServersTests.cs
git commit -m "test(api): pin the OpenAPI document's advertised origin, path base included (#130)"
```

---

### Task 3: #130 — the host fact: a trusted proxy's prefix reaches the document's origin

**Files:**
- Modify: `test/MMLib.Alvo.Host.Tests/AlvoHostPathBaseTests.cs` (add two facts and one helper)

**Interfaces:**
- Consumes: `AlvoHostWorld.StartAsync(overrides:)`, `AlvoHostWorld.SendAsync(method, path, body, headers)`, `AlvoHost.OpenApiDocumentPath`, the file's existing `Prefix`, `ForwardedPrefix()`, `ForwardedHeadersEnabled()`.
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Write the failing tests**

Append to `AlvoHostPathBaseTests`, before the private helpers:

```csharp
    /// <summary>
    /// #130 in the pipeline it matters in: behind a trusted proxy the served document advertises the
    /// prefix, and a client that resolves a path key against it reaches the row <b>through the proxy</b>.
    /// </summary>
    /// <remarks>
    /// The document's <c>servers[0].url</c> is built by ASP.NET Core from <c>Request.Scheme</c>,
    /// <c>Request.Host</c> and <c>Request.PathBase</c>; the first two halves are pinned by
    /// <see cref="AlvoHostForwardedOriginTests"/> and the third by nothing until now. The follow-up goes
    /// through <see cref="FollowThroughTheProxyAsync"/> for the reason this file's header gives: the 404 an
    /// unprefixed URL produces happens at the proxy, and the host cannot produce it.
    /// </remarks>
    [Fact]
    public async Task A_trusted_proxys_forwarded_prefix_reaches_the_documents_advertised_origin()
    {
        await using var world = await AlvoHostWorld.StartAsync(overrides: ForwardedHeadersEnabled());

        var document = await DocumentAsync(world, ForwardedPrefix());
        var origin = Origin(document);
        var resolved = Resolve(origin, CollectionPathKey(document));

        var followed = await FollowThroughTheProxyAsync(world, LocalPathOf(resolved));

        followed.ShouldBe(
            HttpStatusCode.OK,
            $"a client behind the proxy resolves '{resolved}' from the document and must reach the collection");
        origin.ShouldBe($"http://localhost{Prefix}");
    }

    /// <summary>
    /// The control, and the security half: with the switch off — the default — a caller cannot talk the host
    /// into advertising a base URL of their choosing to the next client that reads the document.
    /// </summary>
    [Fact]
    public async Task An_untrusted_forwarded_prefix_does_not_reach_the_documents_advertised_origin()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        var origin = Origin(await DocumentAsync(world, ForwardedPrefix()));

        origin.ShouldBe(
            "http://localhost/",
            "an untrusted caller must not choose the base URL the document hands the next client");
    }
```

and these helpers beside the existing private ones:

```csharp
    private static async Task<JsonObject> DocumentAsync(
        AlvoHostWorld world, IReadOnlyDictionary<string, string> headers)
    {
        using var response = await world.SendAsync(
            HttpMethod.Get, AlvoHost.OpenApiDocumentPath, body: null, headers);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string Origin(JsonObject document)
    {
        var servers = document["servers"]!.AsArray();

        servers.Count.ShouldBe(
            1, $"the document must advertise exactly one server; it advertised {servers.Count}");

        return servers[0]!["url"]!.GetValue<string>();
    }

    private static string CollectionPathKey(JsonObject document) =>
        document["paths"]!.AsObject()
            .Select(path => path.Key)
            .First(key => key.EndsWith("/api/warehouses", StringComparison.Ordinal));

    private static string Resolve(string origin, string pathKey) =>
        new Uri(new Uri(origin.EndsWith('/') ? origin : origin + "/"), pathKey.TrimStart('/')).ToString();

    /// <summary>The path-and-query a client would send, taken off an absolute URL the document produced.</summary>
    private static string LocalPathOf(string absolute) => new Uri(absolute).PathAndQuery;
```

- [ ] **Step 2: Run them**

```bash
dotnet build test/MMLib.Alvo.Host.Tests/MMLib.Alvo.Host.Tests.csproj -c Debug
dotnet test --test-modules "test/MMLib.Alvo.Host.Tests/bin/Debug/net10.0/MMLib.Alvo.Host.Tests.dll" \
  --root-directory . --filter-class "*AlvoHostPathBaseTests"
```

Expected: PASS, all six facts in the class.

If `A_trusted_proxys_forwarded_prefix_reaches_the_documents_advertised_origin` fails on the origin,
report the value: it means the framework does not fold `X-Forwarded-Prefix` into `Request.PathBase`
before the document is built, and #130 becomes production work.

- [ ] **Step 3: Add the missing usings if the build complains**

The file already has `System.Net` and `System.Text.Json.Nodes`. `AlvoHost` is in the same namespace root
(`MMLib.Alvo.Host`), so no using is needed for `AlvoHost.OpenApiDocumentPath`.

- [ ] **Step 4: Normalize and commit**

```bash
python3 - <<'PY'
import io
p='test/MMLib.Alvo.Host.Tests/AlvoHostPathBaseTests.cs'
t=io.open(p,'rb').read().decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
io.open(p,'w',encoding='utf-8-sig',newline='').write(t)
PY
git add test/MMLib.Alvo.Host.Tests/AlvoHostPathBaseTests.cs
git commit -m "test(host): a trusted proxy's prefix reaches the document's advertised origin (#130)"
```

---

### Task 4: #133 — the reachability port, its dialect statement, and the one EF implementation

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/IAlvoDataReachability.cs`
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoReachability.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs` (one default interface member)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RelationalReachability.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` (one registration)
- Modify: `src/MMLib.Alvo.Testing.EntityFrameworkCore/AlvoSqlDialectContractTests.cs` (one obligation)
- Modify: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`
- Modify: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/PublicApi.MMLib.Alvo.Data.EntityFrameworkCore.verified.txt`
- Modify: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/PublicApi.MMLib.Alvo.Testing.EntityFrameworkCore.verified.txt` (only if the contract-suite change alters its public surface — it should not)
- Test: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteReachabilityTests.cs` (new)

**Interfaces:**
- Consumes: `RelationalConnectionFactory.Create()`, `IAlvoSqlDialect`.
- Produces:
  - `MMLib.Alvo.Data.IAlvoDataReachability.ProbeAsync(CancellationToken) → ValueTask<AlvoReachability>`
  - `MMLib.Alvo.Data.AlvoReachability.Reachable` (static property), `AlvoReachability.Unreachable(Exception)` (static method), `.IsReachable` (bool), `.Failure` (Exception?)
  - `IAlvoSqlDialect.ReachabilityProbeStatement → string` (default `"SELECT 1"`)
  - `MMLib.Alvo.Data.EntityFrameworkCore.Internal.RelationalReachability` (internal)

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteReachabilityTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// #133's port over a real engine: a database that answers, one that cannot be opened, and the rule that
/// makes it usable from a health check at all — unreachable is an <em>answer</em>, not an exception.
/// </summary>
/// <remarks>
/// Written against SQLite because the port's implementation is shared by every EF driver
/// (<c>AlvoEfCoreProvider.AddRelationalProvider</c> registers one), so the engine here is the cheap one that
/// needs no container. The PostgreSQL leg asserts the same three claims against a real server, which is where
/// "unreachable" means a refused TCP connection rather than a file that cannot be created.
/// </remarks>
public class SqliteReachabilityTests
{
    /// <summary>A file-backed database that exists answers reachable.</summary>
    [Fact]
    public async Task A_reachable_database_answers_reachable()
    {
        using var services = Build($"Data Source={Path.Combine(Path.GetTempPath(), $"alvo-reach-{Guid.NewGuid():N}.db")}");

        var reachability = await services.GetRequiredService<IAlvoDataReachability>()
            .ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeTrue();
        reachability.Failure.ShouldBeNull("a reachable store has no failure to report");
    }

    /// <summary>
    /// A database that cannot be opened answers <em>unreachable</em> and carries the reason — it does not
    /// throw.
    /// </summary>
    /// <remarks>
    /// The no-throw half is the load-bearing one. A health check whose port throws is reported by the
    /// framework as the registration's failure status too, so the status alone cannot tell the two apart —
    /// but only an <em>answer</em> lets Alvo's own check log the reason at the level an operator reads and
    /// keep it off an anonymous probe's body.
    /// </remarks>
    [Fact]
    public async Task An_unreachable_database_answers_unreachable_and_carries_the_reason()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"alvo-no-such-{Guid.NewGuid():N}", "alvo.db");
        using var services = Build($"Data Source={missingDirectory}");

        var reachability = await services.GetRequiredService<IAlvoDataReachability>()
            .ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeFalse();
        reachability.Failure.ShouldNotBeNull("an operator needs the reason, even though a probe never sees it");
    }

    /// <summary>The driver's public entry point alone yields a resolvable probe, as it does a data port.</summary>
    [Fact]
    public void The_public_entry_point_alone_yields_a_resolvable_reachability_port()
    {
        using var services = Build("Data Source=:memory:");

        services.GetRequiredService<IAlvoDataReachability>().ShouldNotBeNull();
    }

    /// <summary>
    /// A host that registered its own probe keeps it — <c>TryAdd</c> means the driver supplies a default,
    /// not an override, exactly as it does for the dialect.
    /// </summary>
    [Fact]
    public void A_host_supplied_probe_wins_over_the_drivers_default()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IAlvoDataReachability>(new AlwaysReachable());
        collection.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));

        collection.Count(service => service.ServiceType == typeof(IAlvoDataReachability)).ShouldBe(1);
    }

    /// <summary>SQLite inherits the dialect's default probe statement, which is the ANSI one.</summary>
    [Fact]
    public void The_dialects_probe_statement_is_a_bare_select()
    {
        new SqliteSqlDialect().ReachabilityProbeStatement.ShouldBe("SELECT 1");
    }

    private sealed class AlwaysReachable : IAlvoDataReachability
    {
        public ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AlvoReachability.Reachable);
    }

    private static ServiceProvider Build(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UseSqlite(connectionString));
        return collection.BuildServiceProvider();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet build test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj -c Debug
```

Expected: FAIL to compile — `IAlvoDataReachability`, `AlvoReachability` and `ReachabilityProbeStatement` do not exist.

- [ ] **Step 3: Write the port**

Create `src/MMLib.Alvo.Abstractions/Data/IAlvoDataReachability.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// A cheap "can this process still reach its store" probe — the port a readiness check asks, so the core
/// never opens a connection of its own (§0 principle 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="IAlvoData"/>, and deliberately so.</b> That port is the record contract, and
/// every implementation of it — <c>MMLib.Alvo.Testing.Data.InMemoryAlvoData</c> included — would have to grow
/// a member it has no store behind. This one is implemented by whatever ships a store: the shared EF data
/// path registers one for every relational driver, and a driver with nothing cheap to ask simply does not
/// register it.
/// </para>
/// <para>
/// <b>Not registering it is the supported way to opt out</b>, and it is the reason
/// <see cref="AlvoReachability"/> has two states rather than three. A "cannot answer" state would have to be
/// mapped to a health status: <c>Healthy</c> is fail-open and <c>Unhealthy</c> is a pod that never receives
/// traffic, so every mapping is wrong for somebody and the state exists only to be mis-handled. A container
/// with no probe registered reports exactly the readiness it reported before this port existed.
/// </para>
/// <para>
/// <b>What it must not do.</b> It must not read or write a record, must not apply or inspect the schema —
/// "the descriptor applied and the policy catalog is primed" is a different question with its own contributor
/// — and must not take longer than a probe can wait. The caller bounds it with the token; an implementation
/// that ignores the token is one a readiness endpoint cannot use.
/// </para>
/// </remarks>
public interface IAlvoDataReachability
{
    /// <summary>Asks the store whether it can still be reached.</summary>
    /// <remarks>
    /// <para>
    /// <b>Unreachable is a return value, not an exception.</b> A store being away is the expected condition
    /// this port exists to report — during a rolling restart of the database it is the *normal* answer — and
    /// making the normal answer exceptional would push every caller into a <c>catch</c> that cannot
    /// distinguish it from a defect in the probe itself.
    /// </para>
    /// <para>
    /// <b>A cancelled probe still throws.</b> <see cref="OperationCanceledException"/> means the caller's
    /// bound elapsed, which is a different diagnosis from "the store answered that it is away", and an
    /// implementation that reported it as unreachable would hide a probe that is simply too slow.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">The caller's bound on how long the probe may take.</param>
    /// <returns>Whether the store can be reached, and — when it cannot — why.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default);
}
```

Create `src/MMLib.Alvo.Abstractions/Data/AlvoReachability.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>What <see cref="IAlvoDataReachability.ProbeAsync"/> answered: reachable, or not and why.</summary>
/// <remarks>
/// <para>
/// <b><see cref="Failure"/> is for the log and never for a response.</b> An unreachable store's exception is
/// the driver's own message and can carry a connection string or a filesystem path, while the readiness
/// endpoint that consumes this is anonymous by construction — a container probe presents nothing to
/// authenticate with. So the reason travels to the operator's log and the probe learns only that the pod is
/// not ready, which is the same split <c>AlvoProblemTypes.Internal</c> makes for a 500 (design deviation 59).
/// </para>
/// <para>
/// <b>Two states, and there is no third.</b> See <see cref="IAlvoDataReachability"/> for why "cannot answer"
/// is expressed by not registering the port rather than by a value here.
/// </para>
/// </remarks>
public sealed class AlvoReachability
{
    private AlvoReachability(bool isReachable, Exception? failure)
    {
        IsReachable = isReachable;
        Failure = failure;
    }

    /// <summary>The store answered.</summary>
    public static AlvoReachability Reachable { get; } = new(isReachable: true, failure: null);

    /// <summary>The store could not be reached, for the reason an operator has to read.</summary>
    /// <remarks>
    /// The failure is required rather than optional: an implementation that has determined unreachability has
    /// something that told it so, and a probe that reports "not reachable" with no reason leaves an operator
    /// with a drained pod and nothing to act on.
    /// </remarks>
    /// <param name="failure">Why the store could not be reached.</param>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    public static AlvoReachability Unreachable(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AlvoReachability(isReachable: false, failure);
    }

    /// <summary>Whether the store answered.</summary>
    public bool IsReachable { get; }

    /// <summary>
    /// Why the store could not be reached, or <see langword="null"/> when it could. For logging only — see
    /// this type's remarks.
    /// </summary>
    public Exception? Failure { get; }
}
```

- [ ] **Step 4: Add the dialect's probe statement**

In `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs`, after `RowWindowClause`'s default member,
insert:

```csharp
    /// <summary>
    /// The cheapest statement that proves this engine <em>answered</em> — the round trip a readiness probe
    /// makes, and nothing more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>default interface member</b>, like <see cref="RowWindowClause"/> and for the same reason: the
    /// default is right for both engines Alvo ships and for T-SQL, so only a dialect that genuinely differs
    /// implements it (Oracle's <c>SELECT 1 FROM DUAL</c>) and adding it breaks no existing implementation.
    /// It is a port member rather than a literal in the shared data path because per-engine SQL is always a
    /// port member here — the shared path never branches on the engine.
    /// </para>
    /// <para>
    /// <b>Opening a connection is not the probe.</b> A pool hands back a connection it believes is live, so
    /// only a round trip distinguishes "the pool has an entry" from "the database is answering". The
    /// statement therefore has to be one the engine really executes, and must touch no table: a probe over
    /// <c>alvo.*</c> would report a schema problem as unreachability, which is a different question with its
    /// own health check.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> One complete, self-contained statement: no terminator, no surrounding
    /// whitespace, no parameters, and nothing a caller could influence.
    /// </para>
    /// </remarks>
    string ReachabilityProbeStatement => "SELECT 1";
```

- [ ] **Step 5: Write the one implementation**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RelationalReachability.cs`:

```csharp
using MMLib.Alvo.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// #133's port for every EF-backed driver at once: open a connection this instance owns, make the dialect's
/// one round trip, and dispose it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation rather than one per provider package.</b> The issue's scope said "implemented once
/// per <c>MMLib.Alvo.Data.*</c> package"; it was written before the shared EF path became the place
/// <see cref="IAlvoData"/>, <see cref="MMLib.Alvo.Events.IOutboxStore"/> and the three schema services are
/// all composed. Two identical implementations are the drift that seam exists to prevent, and one means a
/// third relational driver inherits a correct probe instead of owing one. The engine-specific half is
/// <see cref="IAlvoSqlDialect.ReachabilityProbeStatement"/>.
/// </para>
/// <para>
/// <b>A fresh connection per probe, from the same factory every other store here uses.</b> A held connection
/// would be the one thing a probe must not have: a socket that died silently answers from a cached client
/// object until something writes to it, which is the exact false "reachable" this check exists to refuse.
/// </para>
/// <para>
/// <b>What is caught, and what is deliberately not.</b> Only the engine's own failure to answer — a
/// <see cref="DbException"/> or a <see cref="TimeoutException"/> — becomes
/// <see cref="AlvoReachability.Unreachable"/>. Anything else propagates: a misconfiguration is not
/// unreachability, and the health-check service reports a check that threw as its registration's failure
/// status anyway, with the framework's own log record. So the narrow catch costs no availability signal and
/// keeps a defect from being reported as a database outage.
/// </para>
/// <para>
/// <b>A cancelled probe is never reported as unreachable.</b> The filter re-reads the token, because a driver
/// may surface cancellation as its own <see cref="DbException"/> — and "the bound elapsed" is a different
/// diagnosis from "the store said it is away". Letting it throw is what lets the health-check service report
/// its own timeout.
/// </para>
/// </remarks>
/// <param name="connections">The factory every other store in this package opens through.</param>
/// <param name="dialect">The driver whose one probe statement this executes.</param>
internal sealed class RelationalReachability(RelationalConnectionFactory connections, IAlvoSqlDialect dialect)
    : IAlvoDataReachability
{
    /// <inheritdoc/>
    public async ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RoundTripAsync(cancellationToken).ConfigureAwait(false);

            return AlvoReachability.Reachable;
        }
        catch (Exception failure) when (TheStoreDidNotAnswer(failure, cancellationToken))
        {
            return AlvoReachability.Unreachable(failure);
        }
    }

    private async Task RoundTripAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = dialect.ReachabilityProbeStatement;

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TheStoreDidNotAnswer(Exception failure, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && failure is DbException or TimeoutException;
}
```

- [ ] **Step 6: Register it for every EF driver**

In `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`, inside `AddRelationalProvider`, after
the `IAlvoData` registration, add:

```csharp
        builder.Services.TryAddSingleton<IAlvoDataReachability>(CreateReachability);
```

and beside `CreateOutboxStore`:

```csharp
    /// <summary>Creates the readiness probe every EF-backed driver shares (#133).</summary>
    /// <remarks>
    /// A singleton beside the other stores, and holding no connection of its own: it opens one per probe
    /// through <see cref="RelationalConnectionFactory"/>, for the reason
    /// <see cref="RelationalReachability"/>'s own remarks give.
    /// </remarks>
    private static RelationalReachability CreateReachability(IServiceProvider services) => new(
        services.GetRequiredService<RelationalConnectionFactory>(),
        services.GetRequiredService<IAlvoSqlDialect>());
```

- [ ] **Step 7: Add the generic contract obligation**

In `src/MMLib.Alvo.Testing.EntityFrameworkCore/AlvoSqlDialectContractTests.cs`, after the row-window
obligation, add:

```csharp
    /// <summary>
    /// The probe statement is a complete statement and carries no terminator, so the data path can execute it
    /// verbatim.
    /// </summary>
    /// <remarks>
    /// It cannot be asserted by value — a dialect for an engine with no bare <c>SELECT</c> answers something
    /// else — so what is pinned is the grammar every caller depends on: non-empty, trimmed, and unterminated.
    /// A dialect that answered <c>"SELECT 1;"</c> would break a driver that batches it, and one that answered
    /// whitespace would send an empty command the engine may or may not refuse.
    /// </remarks>
    [Fact]
    public void A_reachability_probe_statement_is_complete_and_unterminated()
    {
        var statement = CreateDialect().ReachabilityProbeStatement;

        statement.ShouldNotBeNullOrWhiteSpace();
        statement.ShouldBe(statement.Trim(), "a probe statement carries no surrounding whitespace");
        statement.ShouldNotEndWith(";", Case.Sensitive, "a probe statement carries no terminator");
    }
```

- [ ] **Step 8: Run the tests**

```bash
dotnet build MMLib.Alvo.slnx -c Debug
dotnet test --test-modules "test/MMLib.Alvo.Data.Sqlite.Tests/bin/Debug/net10.0/MMLib.Alvo.Data.Sqlite.Tests.dll" \
  --root-directory . --filter-class "*SqliteReachabilityTests"
```

Expected: PASS, all five.

- [ ] **Step 9: Update the public-API baselines**

```bash
scripts/test-ring0 2>&1 | tail -40
```

Expected: `PublicApiApprovalTests` FAIL in `MMLib.Alvo.Abstractions.Tests` and
`MMLib.Alvo.Data.EntityFrameworkCore.Tests`. Verify each `*.received.txt` diff shows **only** the intended
additions (`IAlvoDataReachability`, `AlvoReachability`, `ReachabilityProbeStatement`), then accept:

```bash
for received in $(find test -name "PublicApi.*.received.txt"); do
  verified="${received%.received.txt}.verified.txt"
  diff -u "$verified" "$received" || true
done
```

Accept only after reading each diff:

```bash
for received in $(find test -name "PublicApi.*.received.txt"); do
  mv "$received" "${received%.received.txt}.verified.txt"
done
scripts/test-ring0
```

Expected: `[ring0] OK`.

The Stop hook will report a moved baseline and require the read-only `alvo-snapshot-judge`; dispatch it and
record its verdict.

- [ ] **Step 10: Normalize and commit**

```bash
python3 - <<'PY'
import io, subprocess
files = subprocess.run(['git','diff','--name-only','HEAD'], capture_output=True, text=True).stdout.split()
files += subprocess.run(['git','ls-files','--others','--exclude-standard'], capture_output=True, text=True).stdout.split()
for p in {f for f in files if f.endswith('.cs')}:
    t = io.open(p,'rb').read().decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
    io.open(p,'w',encoding='utf-8-sig',newline='').write(t)
    print('normalized', p)
PY
git add -A src test
git commit -m "feat(data): a database-reachability port, implemented once at the EF seam (#133)"
```

---

### Task 5: #133 — the readiness contributor and its facts

**Files:**
- Modify: `src/MMLib.Alvo/Api/AlvoHealth.cs` (the check's name and the probe bound)
- Create: `src/MMLib.Alvo/Api/Internal/AlvoReachabilityHealthCheck.cs`
- Modify: `src/MMLib.Alvo/Api/HealthSetup.cs` (register it)
- Test: `test/MMLib.Alvo.Api.Tests/AlvoHealthReachabilityTests.cs` (new)

**Interfaces:**
- Consumes: `IAlvoDataReachability`, `AlvoReachability` (Task 4), `AlvoHealth.ReadyTag`, `AlvoHealthWorld.StartAsync(setup)`, `AlvoHealthWorldSetup(Register:)`.
- Produces: `AlvoHealth.DatabaseCheckName` (internal const `"alvo-database"`), `AlvoHealth.DatabaseProbeTimeout` (internal static readonly `TimeSpan`, 2 seconds).

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/AlvoHealthReachabilityTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Data;
using MMLib.Alvo.Migrations;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// #133 over the readiness endpoint: a database that has gone away after boot drains the pod, and the reason
/// stays off the wire.
/// </summary>
/// <remarks>
/// <b>Every fact here substitutes the port rather than breaking a real database</b>, and that is the point of
/// the port existing: "the store is away" is a state no in-process fixture can produce on demand without one.
/// The real-engine legs — a database that genuinely answers, and one that genuinely cannot be opened — are
/// <c>SqliteReachabilityTests</c>' and the PostgreSQL integration suite's.
/// </remarks>
public class AlvoHealthReachabilityTests
{
    /// <summary>A store that cannot be reached is 503, even though the boot is Ready.</summary>
    /// <remarks>
    /// The boot phase in the body is asserted too, and it is the discriminating half: it is <c>Ready</c>, so
    /// the 503 can only have come from the reachability contributor. A fact that asserted the status alone
    /// would pass just as well over a host whose boot never ran.
    /// </remarks>
    [Fact]
    public async Task Readiness_is_503_when_the_database_cannot_be_reached()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(Away)));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        readiness.Body.ShouldBe(nameof(AlvoBootPhase.Ready), "the boot is ready; only reachability is not");
    }

    /// <summary>The control: a store that answers leaves readiness at 200.</summary>
    [Fact]
    public async Task Readiness_is_200_when_the_database_can_be_reached()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(Answering)));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A store that cannot be reached does not take <b>liveness</b> down. The whole reason readiness is the
    /// route this contributes to: a database outage must drain the pod's traffic, never restart-loop the
    /// container.
    /// </summary>
    [Fact]
    public async Task An_unreachable_database_does_not_take_liveness_down()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(Away)));

        var liveness = await world.ProbeAsync(AlvoHealth.LivenessPath);

        liveness.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A probe that never returns is answered <b>503</b> rather than held — the registration's own timeout,
    /// which is why the check carries no timeout of its own.
    /// </summary>
    /// <remarks>
    /// This is the fact that pins <c>HealthCheckRegistration.Timeout</c> being honoured, rather than the
    /// documentation being taken on trust. Deleting <c>AlvoHealth.DatabaseProbeTimeout</c> from the
    /// registration turns it from a 503 into a request that hangs until the fixture's own cancellation, which
    /// is a failing fact rather than a slow one.
    /// </remarks>
    [Fact]
    public async Task A_probe_that_hangs_is_a_503_and_not_a_held_request()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(Hanging)));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// With <b>no</b> probe registered, readiness is exactly what it was before this port existed — the
    /// supported opt-out for a driver with nothing cheap to ask.
    /// </summary>
    /// <remarks>
    /// Fail-open, deliberately: readiness is an availability gate rather than an authorization one, and a
    /// third-party driver that ships without a probe must not make every pod permanently unready. Both
    /// in-repo drivers register one, so this state is reachable only on purpose.
    /// </remarks>
    [Fact]
    public async Task With_no_probe_registered_readiness_is_unchanged()
    {
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: services => services.RemoveAll<IAlvoDataReachability>()));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The readiness body reports the boot phase and <b>never</b> the reason the store gave — which really
    /// does carry a path, so the guard is not vacuous.
    /// </summary>
    [Fact]
    public async Task A_readiness_body_never_carries_the_reason_the_store_gave()
    {
        const string secret = "Host=db.internal;Password=hunter2";
        await using var world = await AlvoHealthWorld.StartAsync(
            new AlvoHealthWorldSetup(Register: Probe(() => Unreachable(secret))));

        var readiness = await world.ProbeAsync(AlvoHealth.ReadinessPath);

        readiness.Body.ShouldNotContain("hunter2", Case.Sensitive);
        readiness.Body.ShouldBe(nameof(AlvoBootPhase.Ready));
    }

    private static Action<IServiceCollection> Probe(Func<ValueTask<AlvoReachability>> answer) =>
        services => services.AddSingleton<IAlvoDataReachability>(new StubReachability(answer));

    private static ValueTask<AlvoReachability> Answering() =>
        ValueTask.FromResult(AlvoReachability.Reachable);

    private static ValueTask<AlvoReachability> Away() => Unreachable("the store is away");

    private static ValueTask<AlvoReachability> Unreachable(string reason) =>
        ValueTask.FromResult(AlvoReachability.Unreachable(new InvalidOperationException(reason)));

    /// <summary>A probe that never answers, so the registration's timeout is the only thing that can.</summary>
    private static async ValueTask<AlvoReachability> Hanging()
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);

        return AlvoReachability.Reachable;
    }

    private sealed class StubReachability(Func<ValueTask<AlvoReachability>> answer) : IAlvoDataReachability
    {
        public ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default) =>
            answer();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet build test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj -c Debug
dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/net10.0/MMLib.Alvo.Api.Tests.dll" \
  --root-directory . --filter-class "*AlvoHealthReachabilityTests"
```

Expected: `Readiness_is_503_when_the_database_cannot_be_reached`,
`A_probe_that_hangs_is_a_503_and_not_a_held_request` and
`A_readiness_body_never_carries_the_reason_the_store_gave` FAIL — no check consumes the port yet, so
readiness is 200 throughout. The other three PASS, and that is expected: they are controls.

- [ ] **Step 3: Name the check and its bound**

In `src/MMLib.Alvo/Api/AlvoHealth.cs`, after `SchemaCheckName`, add:

```csharp
    /// <summary>The name Alvo's own database-reachability check is registered under.</summary>
    internal const string DatabaseCheckName = "alvo-database";

    /// <summary>
    /// How long <see cref="DatabaseCheckName"/> may take before the health-check service reports it as
    /// failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Carried by <c>HealthCheckRegistration.Timeout</c> rather than by the check</b>, so the framework's
    /// own linked cancellation source is what enforces it and a probe that hangs is a 503 instead of a held
    /// request.
    /// </para>
    /// <para>
    /// <b>Two seconds, and a constant rather than configuration.</b> A refused connection fails in
    /// milliseconds; the case a bound exists for is a <em>hanging</em> one — packet loss to a database whose
    /// driver would otherwise wait out its own connect timeout, fifteen seconds on Npgsql — and a readiness
    /// answer that arrives after the orchestrator's own probe timeout is a failure with extra steps. The value
    /// that would actually need tuning is that orchestrator's timeout, which lives outside this process, so a
    /// knob here would configure the wrong end.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan DatabaseProbeTimeout = TimeSpan.FromSeconds(2);
```

- [ ] **Step 4: Write the check**

Create `src/MMLib.Alvo/Api/Internal/AlvoReachabilityHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The second contributor to <see cref="AlvoHealth.ReadinessPath"/>: can this process still reach the store
/// it serves from (#133).
/// </summary>
/// <remarks>
/// <para>
/// <b>It answers the <em>continuing</em> question, which is the one nothing else asked.</b>
/// <see cref="AlvoSchemaHealthCheck"/> reports what the boot decided, and the boot ran once, before the
/// server bound — so a database that goes away afterwards was invisible to both probes and an orchestrator
/// had nothing to drain traffic on.
/// </para>
/// <para>
/// <b><see cref="HealthStatus.Unhealthy"/>, never <see cref="HealthStatus.Degraded"/></b>, for the reason
/// <see cref="AlvoSchemaHealthCheck"/> gives: the framework maps <c>Degraded</c> to <b>200</b> and Kubernetes
/// counts any 2xx as a passing probe, so a degraded gate is no gate at all.
/// </para>
/// <para>
/// <b>The description is constant and the reason goes to the log.</b> A check's description reaches
/// <c>DefaultHealthCheckService</c>'s log, every <see cref="IHealthCheckPublisher"/>, and any verbose
/// response writer a host maps of its own — while the driver's message for an unreachable store can carry a
/// connection string. The exception is not passed to <see cref="HealthCheckResult"/> either, for the same
/// reason and not as an economy. The operator reads it in the log; the probe reads a status code (design
/// deviation 59).
/// </para>
/// <para>
/// <b>No probe registered is <see cref="HealthStatus.Healthy"/>, and it is honest about it.</b> Not
/// registering <see cref="IAlvoDataReachability"/> is the supported opt-out for a driver with nothing cheap
/// to ask, so a container without one reports exactly the readiness it reported before this check existed.
/// The description says which of the two happened, because "healthy" for a question nobody asked is the one
/// answer here that could mislead a reader of the log.
/// </para>
/// <para>
/// <b>Nothing is caught.</b> An unreachable store is a return value, not an exception
/// (<see cref="IAlvoDataReachability.ProbeAsync"/> says so), and anything a probe does throw is either the
/// registration's timeout or a defect — both of which the health-check service reports as this
/// registration's failure status, with its own log record. A <c>catch</c> here would flatten the two into one
/// diagnosis.
/// </para>
/// </remarks>
/// <param name="reachability">The store's own probe, or <see langword="null"/> when no driver registered one.</param>
/// <param name="logger">Where an unreachable store's reason is written for the operator who has to read it.</param>
internal sealed partial class AlvoReachabilityHealthCheck(
    IAlvoDataReachability? reachability, ILogger<AlvoReachabilityHealthCheck> logger) : IHealthCheck
{
    private const string NoProbe = "No Alvo data-reachability probe is registered.";
    private const string Answering = "Alvo can reach its store.";
    private const string Away = "Alvo cannot reach its store.";

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (reachability is null)
        {
            return HealthCheckResult.Healthy(NoProbe);
        }

        var probed = await reachability.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probed.IsReachable)
        {
            return HealthCheckResult.Healthy(Answering);
        }

        TheStoreCannotBeReached(logger, probed.Failure);

        return HealthCheckResult.Unhealthy(Away);
    }

    /// <summary>The one log record, carrying the reason the probe's answer withholds from the wire.</summary>
    /// <remarks>
    /// Source-generated because <c>CA1848</c> is an error in this repository. The reason is passed as the
    /// record's exception rather than formatted into the message, so its type and inner exceptions survive
    /// into whatever the host's logging does with it.
    /// </remarks>
    /// <param name="logger">The logger this check writes through.</param>
    /// <param name="failure">Why the store could not be reached.</param>
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo cannot reach its store, so readiness reports Unhealthy and this pod should be drained.")]
    private static partial void TheStoreCannotBeReached(ILogger logger, Exception? failure);
}
```

- [ ] **Step 5: Register it**

In `src/MMLib.Alvo/Api/HealthSetup.cs`, inside `AddAlvoHealth`, after the schema registration, add:

```csharp
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IConfigureOptions<HealthCheckServiceOptions>, AlvoReachabilityHealthCheckRegistration>());
```

and, after `AlvoSchemaHealthCheckRegistration`, add:

```csharp
/// <summary>
/// Puts <see cref="AlvoReachabilityHealthCheck"/> into the health-check registry under
/// <see cref="AlvoHealth.ReadyTag"/>, bounded by <see cref="AlvoHealth.DatabaseProbeTimeout"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetService</c> rather than <c>GetRequiredService</c> for the probe, because not registering one is the
/// supported opt-out — see <see cref="MMLib.Alvo.Data.IAlvoDataReachability"/>. Resolving it inside the
/// factory rather than here keeps a driver whose probe cannot be constructed from failing the health-check
/// service's own construction, which would answer <b>500</b> on both probes: the one failure a readiness
/// endpoint must not have.
/// </para>
/// <para>
/// <see cref="HealthCheckRegistration.FailureStatus"/> is stated rather than defaulted for the reason
/// <see cref="AlvoSchemaHealthCheckRegistration"/> gives, and it is also what a probe that <em>timed out</em>
/// is reported as.
/// </para>
/// </remarks>
internal sealed class AlvoReachabilityHealthCheckRegistration : IConfigureOptions<HealthCheckServiceOptions>
{
    /// <inheritdoc/>
    public void Configure(HealthCheckServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Registrations.Add(new HealthCheckRegistration(
            AlvoHealth.DatabaseCheckName,
            CreateCheck,
            failureStatus: HealthStatus.Unhealthy,
            tags: [AlvoHealth.ReadyTag],
            timeout: AlvoHealth.DatabaseProbeTimeout));
    }

    private static AlvoReachabilityHealthCheck CreateCheck(IServiceProvider services) => new(
        services.GetService<MMLib.Alvo.Data.IAlvoDataReachability>(),
        services.GetRequiredService<ILogger<AlvoReachabilityHealthCheck>>());
}
```

Add `using Microsoft.Extensions.Logging;` to `HealthSetup.cs` if it is not already there.

- [ ] **Step 6: Run the tests**

```bash
dotnet build MMLib.Alvo.slnx -c Debug
dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/net10.0/MMLib.Alvo.Api.Tests.dll" \
  --root-directory . --filter-class "*AlvoHealthReachabilityTests"
dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/net10.0/MMLib.Alvo.Api.Tests.dll" \
  --root-directory . --filter-class "*AlvoHealthTests"
```

Expected: both classes PASS in full. `AlvoHealthTests` is the regression guard — its
`Readiness_is_200_once_the_boot_is_ready` now runs with a real SQLite probe behind it, so a probe that
answers wrongly over a live database fails there too.

- [ ] **Step 7: Run ring0**

```bash
scripts/test-ring0
```

Expected: `[ring0] OK`. If a `PublicApi.MMLib.Alvo.verified.txt` diff appears, **stop and read it** —
everything added in this task is `internal`, so the core's public surface must not have moved.

- [ ] **Step 8: Normalize and commit**

```bash
python3 - <<'PY'
import io, subprocess
files = subprocess.run(['git','diff','--name-only','HEAD'], capture_output=True, text=True).stdout.split()
files += subprocess.run(['git','ls-files','--others','--exclude-standard'], capture_output=True, text=True).stdout.split()
for p in {f for f in files if f.endswith('.cs')}:
    t = io.open(p,'rb').read().decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
    io.open(p,'w',encoding='utf-8-sig',newline='').write(t)
    print('normalized', p)
PY
git add -A src test
git commit -m "feat(api): /health/ready reports whether the store can still be reached (#133)"
```

---

### Task 6: #133 — the engine legs and the standalone host leg

**Files:**
- Create: `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlReachabilityTests.cs`
- Modify: `test/MMLib.Alvo.Host.Tests/AlvoHealthTests.cs` (one fact)

**Interfaces:**
- Consumes: `PostgresFixture` (read it first for the collection attribute and the connection string it exposes), `AlvoHostWorld.StartAsync()`, `AlvoHealth.ReadinessPath`, `AlvoHealth.DatabaseCheckName` — the last is `internal`, so the host fact asserts through `HealthCheckService` only if the Host test project has access; otherwise assert the endpoint.

- [ ] **Step 1: Read the PostgreSQL fixture, so the new file matches it**

```bash
sed -n '1,80p' test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgresFixture.cs
sed -n '1,60p' test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDescriptorVersionStoreTests.cs
```

Use whatever collection/fixture attribute that file uses; do not invent one.

- [ ] **Step 2: Write the PostgreSQL facts**

Create `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlReachabilityTests.cs`, using the
fixture pattern read in Step 1, with exactly two facts:

```csharp
    /// <summary>A server that is up answers reachable, over a real TCP connection.</summary>
    [Fact]
    public async Task A_reachable_server_answers_reachable()
    {
        using var services = Build(_fixture.ConnectionString);

        var reachability = await services.GetRequiredService<IAlvoDataReachability>()
            .ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeTrue();
    }

    /// <summary>
    /// A port nothing listens on answers <b>unreachable</b> rather than throwing — the claim SQLite cannot
    /// make, because its "unreachable" is a file that cannot be created rather than a refused connection.
    /// </summary>
    /// <remarks>
    /// The connection string is the fixture's own with the port replaced, so the host and credentials are
    /// real and only reachability differs — a fact that changed the host name too would pass for a DNS
    /// failure, which is a different diagnosis.
    /// </remarks>
    [Fact]
    public async Task A_port_nothing_listens_on_answers_unreachable()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Port = 1,
            Timeout = 2,
        }.ToString();
        using var services = Build(unreachable);

        var reachability = await services.GetRequiredService<IAlvoDataReachability>()
            .ProbeAsync(TestContext.Current.CancellationToken);

        reachability.IsReachable.ShouldBeFalse();
        reachability.Failure.ShouldNotBeNull();
    }

    private static ServiceProvider Build(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddAlvo(alvo => alvo.UsePostgreSql(connectionString));
        return collection.BuildServiceProvider();
    }
```

with the file header:

```csharp
/// <summary>
/// #133's port against a real PostgreSQL server: the engine leg SQLite cannot supply, because a refused TCP
/// connection and a file that cannot be created are different failures behind one answer.
/// </summary>
```

and the usings `Microsoft.Extensions.DependencyInjection`, `MMLib.Alvo.Data`, `Npgsql`.

If `PostgresFixture` exposes the connection string under a different member name, use that name.

- [ ] **Step 3: Write the standalone host fact**

Append to `test/MMLib.Alvo.Host.Tests/AlvoHealthTests.cs`:

```csharp
    /// <summary>
    /// The standalone host's readiness really does evaluate the reachability probe — the half a core fixture
    /// cannot see, because it is the host's provider selection that registers one at all (#133).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 200 alone would pass over a host with no probe registered, so the fact reads the report: the
    /// reachability check has to be <em>present</em> and <em>healthy</em>. That is the same reason
    /// <c>AlvoHostProblemDetailsTests.The_host_registers_alvos_exception_handler</c> asserts a registration
    /// rather than only a response — a registration silently dropped from the host is exactly how #119 and
    /// #133 came to be filed.
    /// </para>
    /// <para>
    /// Both entries are named from a literal rather than from the report being measured: "every entry is
    /// healthy" over a report with no entries passes trivially.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Readiness_over_the_standalone_host_evaluates_a_real_reachability_probe()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        var report = await world.ReadinessReportAsync();

        report.Keys.OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(["alvo-database", "alvo-schema"]);
        report["alvo-database"].ShouldBe(HealthStatus.Healthy);
    }
```

and add to `AlvoHostWorld`, beside `ExceptionHandlerTypeNames`:

```csharp
    /// <summary>
    /// Every check readiness evaluates, by name and status — for the facts whose claim is that a contributor
    /// was <em>registered</em>, which a status code cannot carry.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, HealthStatus>> ReadinessReportAsync()
    {
        var checks = _app.Services.GetRequiredService<HealthCheckService>();
        var report = await checks.CheckHealthAsync(
            registration => registration.Tags.Contains(AlvoHealth.ReadyTag),
            TestContext.Current.CancellationToken);

        return report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status, StringComparer.Ordinal);
    }
```

with `using Microsoft.Extensions.Diagnostics.HealthChecks;` and `using MMLib.Alvo.Api;` added to
`AlvoHostWorld.cs` if absent, and `using Microsoft.Extensions.Diagnostics.HealthChecks;` added to
`AlvoHealthTests.cs`.

The check names are string literals here on purpose: `AlvoHealth.DatabaseCheckName` is `internal` to the
core, and a fact that read the constant it is testing would pass after a rename that broke every
`docker-compose` and Kubernetes probe pointing at the old name — the same "pin it from outside" rule the
OpenAPI suite states for its counts.

- [ ] **Step 4: Run them**

```bash
dotnet build MMLib.Alvo.slnx -c Debug
dotnet test --test-modules "test/MMLib.Alvo.Host.Tests/bin/Debug/net10.0/MMLib.Alvo.Host.Tests.dll" \
  --root-directory . --filter-class "*AlvoHealthTests"
```

Expected: PASS. Then the PostgreSQL leg (needs Docker, so it is ring2 work — run it explicitly here
because the code is new):

```bash
dotnet test --test-modules "test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/bin/Debug/net10.0/*.dll" \
  --root-directory . --filter-class "*PostgreSqlReachabilityTests"
```

Expected: PASS, both facts. `A_port_nothing_listens_on_answers_unreachable` should complete in a couple of
seconds thanks to `Timeout = 2`; if it takes fifteen, the timeout is not reaching the driver and the
connection-string member name is wrong.

- [ ] **Step 5: Normalize and commit**

```bash
python3 - <<'PY'
import io, subprocess
files = subprocess.run(['git','diff','--name-only','HEAD'], capture_output=True, text=True).stdout.split()
files += subprocess.run(['git','ls-files','--others','--exclude-standard'], capture_output=True, text=True).stdout.split()
for p in {f for f in files if f.endswith('.cs')}:
    t = io.open(p,'rb').read().decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
    io.open(p,'w',encoding='utf-8-sig',newline='').write(t)
    print('normalized', p)
PY
git add -A test
git commit -m "test(data): reachability on both engines, and the standalone host really registers one (#133)"
```

---

### Task 7: "D" — `MapAlvoDataApi` returns an `IEndpointConventionBuilder`

**Files:**
- Create: `src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs` (own one, seal it, hand it out)
- Modify: `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (thread it through `Map` → the five `Map*` → `Protect`)
- Modify: `src/MMLib.Alvo/Api/AlvoDataApiEndpointRouteBuilderExtensions.cs` (the return type)
- Modify: `test/_shared/api/AlvoApiWorld.cs` (three knobs)
- Modify: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`
- Test: `test/MMLib.Alvo.Api.Tests/DataApiConventionTests.cs` (new)

**Interfaces:**
- Consumes: `AlvoEndpointDataSource`, `DataApiEndpoints.Map`, `RouteHandlerBuilder`.
- Produces:
  - `MapAlvoDataApi(this IEndpointRouteBuilder) → IEndpointConventionBuilder` (public, **changed** return type)
  - `AlvoDataApiConventions` (internal) with `Add`, `Finally`, `internal void ApplyTo(IEndpointConventionBuilder route)`, `internal void Seal()`
  - `AlvoApiWorldSetup(ConfigureServices:, ConfigureApp:, ConfigureDataApiRoutes:)`

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Api.Tests/DataApiConventionTests.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using System.Net;
using System.Threading.RateLimiting;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// What a host may attach to Alvo's generated routes. <c>MapAlvoDataApi</c> now returns the
/// <see cref="IEndpointConventionBuilder"/> every ASP.NET Core <c>Map*</c> returns, so rate limiting, an
/// authorization policy, output caching and telemetry tags land on Alvo's routes and nowhere else.
/// </summary>
/// <remarks>
/// <b>The capability was reachable before this, and that is worth stating rather than overclaiming.</b>
/// <c>app.MapGroup("").MapAlvoDataApi()</c> plus conventions on the group already worked, because
/// <see cref="AlvoEndpointDataSource.GetGroupedEndpoints"/> forwards the group's context to the nested
/// minimal-API sources. What was missing was the discoverable seam: a host had to know that an empty
/// <c>MapGroup</c> is legal and that Alvo forwards grouped endpoints. The facts below are written against the
/// seam, not against the workaround.
/// </remarks>
public class DataApiConventionTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    private const string OnePerWindow = "one-per-window";

    /// <summary>
    /// The acceptance: a host rate-limits Alvo's routes, and the limit is <b>enforced</b> — the second
    /// request is refused by the framework's own middleware reading metadata a convention put there.
    /// </summary>
    [Fact]
    public async Task A_host_can_rate_limit_the_generated_routes_and_the_limit_is_enforced()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureServices: AddOnePerWindowLimiter,
            ConfigureApp: app => app.UseRateLimiter(),
            ConfigureDataApiRoutes: routes => routes.RequireRateLimiting(OnePerWindow)));

        using var first = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);
        using var second = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.ReadTextAsync());
        second.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the host's rate-limiting convention must reach the generated endpoint");
    }

    /// <summary>
    /// A convention reaches <b>every</b> generated endpoint, not the first one mapped — counted against the
    /// route table rather than against the walk being measured.
    /// </summary>
    [Fact]
    public async Task A_convention_reaches_every_generated_endpoint()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes => routes.WithMetadata(new HostMarker())));

        var generated = world.Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<DataApiOperationMetadata>() is not null)
            .ToList();

        generated.ShouldNotBeEmpty("a fact about every endpoint needs there to be some");
        generated.Count(endpoint => endpoint.Metadata.GetMetadata<HostMarker>() is not null)
            .ShouldBe(generated.Count);
    }

    /// <summary>
    /// A <c>Finally</c> convention runs too, and runs after the ordinary ones — the half of
    /// <see cref="IEndpointConventionBuilder"/> a hand-rolled implementation silently drops.
    /// </summary>
    [Fact]
    public async Task A_finally_convention_runs_after_the_ordinary_ones()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: routes =>
            {
                routes.WithMetadata(new HostMarker());
                routes.Finally(endpoint => endpoint.Metadata.Add(new FinallyMarker(
                    endpoint.Metadata.OfType<HostMarker>().Any())));
            }));

        var markers = world.Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<DataApiOperationMetadata>() is not null)
            .Select(endpoint => endpoint.Metadata.GetMetadata<FinallyMarker>())
            .ToList();

        markers.ShouldAllBe(marker => marker != null && marker.SawTheOrdinaryOne);
    }

    /// <summary>
    /// A convention added after the route table has materialised <b>throws</b>. It cannot be honoured — the
    /// table is frozen by design — and silently dropping a <c>RequireRateLimiting</c> is a rate limiter a
    /// host believes it has.
    /// </summary>
    [Fact]
    public async Task A_convention_added_after_the_first_request_is_refused()
    {
        IEndpointConventionBuilder? routes = null;
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(
            ConfigureDataApiRoutes: builder => routes = builder));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/owners", _admin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the route table has to have materialised first");

        var refusal = Should.Throw<InvalidOperationException>(
            () => routes!.WithMetadata(new HostMarker()));

        refusal.Message.ShouldContain("MapAlvoDataApi");
    }

    private static void AddOnePerWindowLimiter(IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddFixedWindowLimiter(OnePerWindow, window =>
            {
                window.PermitLimit = 1;
                window.Window = TimeSpan.FromMinutes(5);
                window.QueueLimit = 0;
                window.AutoReplenishment = false;
            });
        });

    private sealed record HostMarker;

    private sealed record FinallyMarker(bool SawTheOrdinaryOne);
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet build test/MMLib.Alvo.Api.Tests/MMLib.Alvo.Api.Tests.csproj -c Debug
```

Expected: FAIL to compile — `AlvoApiWorldSetup` has no `ConfigureServices`, `ConfigureApp` or
`ConfigureDataApiRoutes`.

- [ ] **Step 3: Write the conventions holder**

Create `src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The conventions a host attached to <c>MapAlvoDataApi()</c>, collected until the route table materialises
/// and then applied to every route Alvo maps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Collected rather than applied immediately, because the routes do not exist yet.</b>
/// <see cref="AlvoEndpointDataSource"/> reads the applied schema on the first <em>enumeration</em>, so at the
/// moment a host writes <c>.RequireRateLimiting(…)</c> there is no endpoint to decorate. Collecting adds no
/// ordering obligation to a call whose whole design was to have none: a host's conventions are always
/// complete by the time the first request builds the matcher.
/// </para>
/// <para>
/// <b>After that they are refused, not dropped.</b> The table is frozen once materialised — for the reason
/// <see cref="AlvoEndpointDataSource"/> records — so a late convention cannot be honoured, and a
/// <c>RequireRateLimiting</c> that silently did nothing would be a rate limiter a host believes it has. This
/// is what the framework's own convention builders do, and the message names the call to move.
/// </para>
/// <para>
/// <b><see cref="Finally"/> is implemented rather than inherited.</b> Its default interface implementation
/// throws, and forwarding it to the route's own <see cref="IEndpointConventionBuilder.Finally"/> is what
/// keeps the framework's ordering guarantee — a finally-convention observes every ordinary one, including
/// Alvo's own metadata.
/// </para>
/// <para>
/// <b>Guarded by a lock, because the two sides run on different threads.</b> A host adds conventions on the
/// startup thread and the first request materialises the table on a request thread, and
/// <see cref="Seal"/> is what publishes the transition between them.
/// </para>
/// </remarks>
internal sealed class AlvoDataApiConventions : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> _conventions = [];
    private readonly List<Action<EndpointBuilder>> _finallyConventions = [];
    private readonly Lock _gate = new();
    private bool _sealed;

    /// <inheritdoc/>
    public void Add(Action<EndpointBuilder> convention) => Collect(_conventions, convention);

    /// <inheritdoc/>
    public void Finally(Action<EndpointBuilder> finallyConvention) =>
        Collect(_finallyConventions, finallyConvention);

    /// <summary>Applies everything collected to one route Alvo has just mapped.</summary>
    /// <remarks>
    /// Called from inside the data source's materialisation, after Alvo's own filters and metadata, so a
    /// host's convention observes them and can override what it means to.
    /// </remarks>
    /// <param name="route">The route just mapped.</param>
    internal void ApplyTo(IEndpointConventionBuilder route)
    {
        foreach (var convention in _conventions)
        {
            route.Add(convention);
        }

        foreach (var convention in _finallyConventions)
        {
            route.Finally(convention);
        }
    }

    /// <summary>Closes the collection, so a later addition is refused instead of ignored.</summary>
    internal void Seal()
    {
        lock (_gate)
        {
            _sealed = true;
        }
    }

    private void Collect(List<Action<EndpointBuilder>> conventions, Action<EndpointBuilder> convention)
    {
        ArgumentNullException.ThrowIfNull(convention);

        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "Alvo's Data API routes have already been built, so this convention cannot be applied. "
                    + "Attach conventions to the builder MapAlvoDataApi() returns before the first request "
                    + "reaches the application.");
            }

            conventions.Add(convention);
        }
    }
}
```

- [ ] **Step 4: Thread it through the data source**

In `AlvoEndpointDataSource`:

- add a field and an accessor:

```csharp
    private readonly AlvoDataApiConventions _conventions = new();
```

```csharp
    /// <summary>
    /// The conventions seam <c>MapAlvoDataApi</c> hands back, so a host can decorate the routes this source
    /// materialises.
    /// </summary>
    internal AlvoDataApiConventions Conventions => _conventions;
```

- in `Build()`, seal before mapping and apply per route:

```csharp
    private RouteTable Build()
    {
        var entities = _catalog.Entities;
        ReservedQueryKeys.EnsureNoneIsShadowed(entities);

        var formats = FormatCatalog.Build(entities);
        var inner = new NestedRouteBuilder(_services);
        _conventions.Seal();
        foreach (var entity in entities)
        {
            DataApiEndpoints.Map(inner, entity, _prefix, _options, _filters, formats, _conventions);
        }

        return RouteTable.Of(inner);
    }
```

- extend the type's `<remarks>` with one paragraph:

```csharp
/// <para>
/// <b>A host's conventions are collected here and applied at materialisation.</b>
/// <c>MapAlvoDataApi</c> returns <see cref="Conventions"/>, and <see cref="Build"/> seals it before mapping
/// so a convention arriving after the table is frozen is refused rather than dropped. They are applied inside
/// <c>DataApiEndpoints.Protect</c>, in the same call that attaches the authorization filter and the marker —
/// so a generated route carrying one of the three carries all three, which is the same construction argument
/// the filter and the marker already rest on.
/// </para>
```

- add the `<param name="..."/>` documentation only if the constructor changes; it does not.

- [ ] **Step 5: Thread it through the endpoints**

In `DataApiEndpoints`:

- `Map` gains a parameter and passes it to all five:

```csharp
    /// <param name="conventions">The conventions the host attached to <c>MapAlvoDataApi()</c>.</param>
    internal static void Map(
        IEndpointRouteBuilder endpoints,
        EntitySchema entity,
        string prefix,
        AlvoApiOptions options,
        AlvoContextFilterFactory filters,
        FormatCatalog formats,
        AlvoDataApiConventions conventions)
    {
        var collection = $"{prefix}/{entity.Name}";
        var item = $"{collection}/{{id:guid}}";

        MapList(endpoints, entity, collection, options, filters, conventions);
        MapGet(endpoints, entity, item, filters, conventions);
        MapCreate(endpoints, entity, collection, options, filters, formats, conventions);
        MapUpdate(endpoints, entity, item, options, filters, formats, conventions);
        MapDelete(endpoints, entity, item, filters, conventions);
    }
```

- each of the five `Map*` methods gains `AlvoDataApiConventions conventions` as its last parameter and
  passes it as `Protect`'s last argument.
- `Protect` gains the parameter and applies it last:

```csharp
    private static RouteHandlerBuilder Protect(
        this RouteHandlerBuilder builder,
        EntitySchema entity,
        DataOperation operation,
        AlvoContextFilterFactory filters,
        AlvoDataApiConventions conventions)
    {
        var route = builder
            .AddEndpointFilter(NoStoreResponseFilter.Instance)
            .AddEndpointFilter(filters.For(entity.Name, operation))
            .WithMetadata(new DataApiOperationMetadata(entity.Name, operation))
            .Documenting(entity, operation);

        conventions.ApplyTo(route);

        return route;
    }
```

Add to `Protect`'s `<remarks>`:

```csharp
    /// <para>
    /// <b>The host's conventions are applied here, last, and in the same call as the filter and the marker.</b>
    /// A route that is gated therefore also carries whatever the host attached to <c>MapAlvoDataApi()</c>, so
    /// "some endpoints were rate-limited and some were not" is unrepresentable. Last, so a host's convention
    /// observes Alvo's own metadata and can override what it means to.
    /// </para>
```

- keep the original `// First, so it wraps the authorization filter…` comment where it is; it is existing
  rationale a name cannot carry.

- [ ] **Step 6: Change the return type**

In `src/MMLib.Alvo/Api/AlvoDataApiEndpointRouteBuilderExtensions.cs`:

```csharp
    /// <returns>
    /// A convention builder over the routes this call will materialise, so a host can attach
    /// <c>RequireRateLimiting</c>, an authorization policy, output caching or a telemetry tag to Alvo's
    /// generated endpoints and to nothing else.
    /// </returns>
    public static IEndpointConventionBuilder MapAlvoDataApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var catalog = services.GetService<EntityRouteCatalog>()
            ?? throw new InvalidOperationException(
                "The Alvo Data API is not registered. Call services.AddAlvo(...) — optionally with "
                + "AddDataApi(...) to configure it — before MapAlvoDataApi().");

        var source = new AlvoEndpointDataSource(
            catalog,
            services.GetRequiredService<IOptions<AlvoApiOptions>>().Value,
            services.GetRequiredService<AlvoContextFilterFactory>(),
            services,
            services.GetRequiredService<AlvoBootState>(),
            services.GetRequiredService<ILogger<AlvoEndpointDataSource>>());

        endpoints.DataSources.Add(source);

        return source.Conventions;
    }
```

and add one `<remarks>` paragraph:

```csharp
    /// <para>
    /// <b>It returns a convention builder rather than the route builder it was given, which is what every
    /// ASP.NET Core <c>Map*</c> does</b> — <c>MapHealthChecks</c> and <c>MapControllers</c> included. The
    /// capability was reachable before: <c>app.MapGroup("").MapAlvoDataApi()</c> plus conventions on the group
    /// worked, because <see cref="AlvoEndpointDataSource.GetGroupedEndpoints"/> forwards the group's context.
    /// What it was not, was discoverable. Conventions must be attached before the first request materialises
    /// the route table; one attached after is refused, because it could not be honoured.
    /// </para>
    /// <para>
    /// <c>MapAlvo()</c> deliberately still returns the route builder, and <c>MapAlvoHealth()</c> is not
    /// chainable at all: one convention builder over the probes and the Data API together would let a host
    /// attach an authorization policy to <c>/health/live</c>, and a probe presents no credential — that is a
    /// container killed and restart-looped by its own liveness gate. A host that wants conventions calls the
    /// parts, which is already the documented composition.
    /// </para>
```

- [ ] **Step 7: Add the three harness knobs**

In `test/_shared/api/AlvoApiWorld.cs`:

- `AlvoApiWorldSetup` gains three parameters at the end, with XML docs:

```csharp
/// <param name="ConfigureServices">
/// Anything registered on the builder <em>before</em> <c>AddAlvo</c> — a rate limiter, an authorization
/// policy. General-purpose rather than one fact's knob: it is the seam a host's own registrations occupy.
/// </param>
/// <param name="ConfigureApp">
/// Middleware added before the Data API is mapped — <c>UseRateLimiter</c>, <c>UseOutputCache</c>. Runs after
/// the path-base block, so it sits where a host would write it.
/// </param>
/// <param name="ConfigureDataApiRoutes">
/// Conventions attached to the builder <c>MapAlvoDataApi()</c> returns, which is the seam this exists to
/// measure.
/// </param>
```

```csharp
    Action<IServiceCollection>? ConfigureServices = null,
    Action<WebApplication>? ConfigureApp = null,
    Action<IEndpointConventionBuilder>? ConfigureDataApiRoutes = null);
```

- in `BuildApp`, right after `builder.Services.AddSingleton<IAlvoContextAccessor>(...)`:

```csharp
        setup.ConfigureServices?.Invoke(builder.Services);
```

- in `StartAsync`, after the `ServerBodyLimitBytes` block:

```csharp
        setup.ConfigureApp?.Invoke(app);
```

- and replace the mapping block with:

```csharp
        var routes = setup.RouteGroupPrefix is { } groupPrefix
            ? app.MapGroup(groupPrefix).MapAlvoDataApi()
            : app.MapAlvoDataApi();

        setup.ConfigureDataApiRoutes?.Invoke(routes);
```

keeping the existing comment above it.

- [ ] **Step 8: Run the tests**

```bash
dotnet build MMLib.Alvo.slnx -c Debug
dotnet test --test-modules "test/MMLib.Alvo.Api.Tests/bin/Debug/net10.0/MMLib.Alvo.Api.Tests.dll" \
  --root-directory . --filter-class "*DataApiConventionTests"
```

Expected: PASS, all four. If `A_finally_convention_runs_after_the_ordinary_ones` fails,
`IEndpointConventionBuilder.Finally` is not being forwarded; if
`A_convention_added_after_the_first_request_is_refused` fails, `Seal()` is not being called or is called
too late.

- [ ] **Step 9: Run ring0 and accept the core's baseline**

```bash
scripts/test-ring0 2>&1 | tail -40
```

Expected: `PublicApi.MMLib.Alvo` FAIL. Read the diff — it must show **only** `MapAlvoDataApi`'s return
type changing from `Microsoft.AspNetCore.Routing.IEndpointRouteBuilder` to
`Microsoft.AspNetCore.Builder.IEndpointConventionBuilder`:

```bash
diff -u test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt \
        test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.received.txt
```

Then accept and re-run:

```bash
mv test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.received.txt \
   test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
scripts/test-ring0
```

Expected: `[ring0] OK`. Dispatch `alvo-snapshot-judge` when the Stop hook fires.

- [ ] **Step 10: Normalize and commit**

```bash
python3 - <<'PY'
import io, subprocess
files = subprocess.run(['git','diff','--name-only','HEAD'], capture_output=True, text=True).stdout.split()
files += subprocess.run(['git','ls-files','--others','--exclude-standard'], capture_output=True, text=True).stdout.split()
for p in {f for f in files if f.endswith('.cs')}:
    t = io.open(p,'rb').read().decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
    io.open(p,'w',encoding='utf-8-sig',newline='').write(t)
    print('normalized', p)
PY
git add -A src test
git commit -m "feat(api): MapAlvoDataApi returns a convention builder a host can decorate"
```

---

### Task 8: the record, and the gates

**Files:**
- Modify: `docs/architecture/host.md` (#133 and #130 sections, and "What is left of #24")
- Modify: `docs/architecture/data-api.md` (the #130 paragraph, and the new return type)

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Correct `docs/architecture/data-api.md`'s #130 paragraph**

Find the paragraph beginning "The **OpenAPI document's path keys still have the original shape**" and
replace it with:

```
The **OpenAPI document's path keys keep their mapped shape, and the document names the origin they are
resolved against.** `Microsoft.AspNetCore.OpenApi` builds `servers[0].url` from the request's `Scheme`,
`Host` and **`PathBase`**, per request — measured, including the case that made #130 look unfixable: asking
for the document with a path base and then without it returns the right origin each time, so the document is
not frozen by a first request. Alvo's transformer never touches `Servers`. Under `app.UsePathBase("/alvo")`
the origin is `http://localhost/alvo` and the keys stay `/api/owners`; under
`app.MapGroup("/backend").MapAlvoDataApi()` the origin stays bare and the prefix is in the key, because a
group prefix belongs to the route. Both are pinned by `OpenApiServersTests`, and the forwarded-prefix leg by
`AlvoHostPathBaseTests` through a model of the proxy. **#130 closed with no production change**; what it was
missing was any fact at all — the scheme and host halves were pinned, the path-base half by nothing.

The docs UI's own document fetch under a path base is a separate question and stays **#134**.
```

- [ ] **Step 2: Record the new return type in `docs/architecture/data-api.md`**

Add, in the section that describes the mapping seam (search for `MapAlvoDataApi` and place it after the
first paragraph that describes the call):

```
`MapAlvoDataApi()` returns an `IEndpointConventionBuilder`, so a host attaches `RequireRateLimiting`, an
authorization policy, output caching or a telemetry tag to Alvo's generated routes and to nothing else. The
conventions are applied in `DataApiEndpoints.Protect` — the same call that attaches the authorization filter
and the operation marker — so a gated route carries them all or none. They must be attached before the first
request materialises the route table; one attached after is refused, because a frozen table cannot honour it
and a silently dropped `RequireRateLimiting` is a rate limiter a host believes it has. `MapAlvo()` still
returns the route builder and `MapAlvoHealth()` is not chainable: one builder over the probes and the Data API
would let an authorization policy reach `/health/live`, which is a container restart-looped by its own
liveness gate.
```

- [ ] **Step 3: Rewrite `docs/architecture/host.md`'s "#133 is owed" paragraph**

Replace the paragraph beginning "**`/health/ready` existing is not §2.12 being met.**" with:

```
**`/health/ready` now answers the database half of §2.12.** Two checks contribute, under two names:
`alvo-schema` reports what the boot decided ("the descriptor applied and the policy catalog is primed"), and
`alvo-database` reports whether the store can *still* be reached — the continuing answer neither route had,
so a database that went away after boot used to be invisible to both. The core does not open a connection:
`IAlvoDataReachability` is a port in `Abstractions`, implemented once at the shared EF seam over
`RelationalConnectionFactory` and one dialect-owned statement (`IAlvoSqlDialect.ReachabilityProbeStatement`,
`SELECT 1` by default), so every EF-backed driver inherits a correct probe and §0 principle 2 holds. The
bound is `HealthCheckRegistration.Timeout` (two seconds), so a probe that hangs is a 503 rather than a held
request. A driver with nothing cheap to ask opts out by not registering the port, and readiness is then
exactly what it was before — fail-open on purpose, because readiness is an availability gate and a
third-party driver should not make every pod permanently unready. The reason an unreachable store gave goes
to the log at `Error`; the probe still reads the boot phase and nothing else (design deviation 59).
Deviation 38 is **superseded in its liveness-only part** and preserved in its guarantee: a boot that refuses
never binds the socket. **Cache and message-bus reachability remain owed** — neither subsystem exists, and
the readiness tag is what makes each additive.
```

- [ ] **Step 4: Update `docs/architecture/host.md`'s "What is left of #24"**

Replace the reachability bullet with:

```
- the rest of **§2.12** — OpenTelemetry, rate limiting (**#112**), usage metering. The **database half of
  readiness** landed with `IAlvoDataReachability` and the `alvo-database` check; **cache and message-bus
  reachability** are still owed, and each brings its own probe when its subsystem lands;
```

and replace the trailing "Not #24's…" paragraph with:

```
Not #24's, but on the same deployment path: **#134** (Scalar behind a path base) still has to be answered
before "run it behind your ingress" is a claim this project can make. **#130** is closed: the document names
the origin its path keys are resolved against, path base and forwarded prefix included, and two facts pin it.
```

- [ ] **Step 5: Also correct `docs/architecture/host.md`'s #130 reference near the Scalar note**

Find the line containing "#130 (the document's `servers`, open), #134 (this)." and replace with:

```
#130 (the document's `servers`, closed — it carries the request's path base), #134 (this).
```

- [ ] **Step 6: Run ring2**

```bash
scripts/test-ring2 2>&1 | tail -60
```

Expected: green. This is the gate the PR depends on; do not open the PR without it.

- [ ] **Step 7: Commit**

```bash
git add docs
git commit -m "docs: record the reachability port, the convention seam and #130's real state"
```

---

## After the plan: the gates, in order

1. **`scripts/test-ring2`** — green, with the output kept for the PR notes.
2. **Reviewer subagents as the local inner loop**, since `/code-review` and `/security-review` are
   user-only commands here: dispatch `csharp-reviewer` over the diff, **and run
   `alvo-security-core-review` rather than skipping it** — the diff modifies
   `DataApiEndpoints.Protect`, where the authorization filter is attached, and `AlvoEndpointDataSource`,
   which carries the "no ungated path to `IAlvoData`" guarantee. The checklist is earned by the area,
   not by whether a defect is found; label the change `needs-deep-review`. Fix findings before
   opening the PR.
3. **`alvo-plan-guard`** — the read-only pre-PR check for drift from `docs/PLAN.md` and the §0 principles.
4. **`alvo-pr-report`** — the fixed 8-section Artifact; the PR body is a five-line pointer to it.
5. **`gh pr create`**, with the closing keyword repeated per issue:
   `Closes #130`, `Closes #119`, `Closes #133` on separate lines — one keyword closes only the first
   issue in a comma list. Verify each issue's state after the merge.
6. **After the merge**: `main` lives in a worktree, so `gh pr merge` may exit 1 on its local step while
   the server-side merge succeeded. Confirm with `gh pr view --json state,mergeCommit` and delete the
   remote branch by hand.

## Amendments after `alvo-plan-guard`

The plan above is kept as written; this section records what the read-only pre-implementation review
found and what was done about it, so a later reader can tell a decision from an oversight.

1. **`AlvoHealthTests.Registering_Alvo_twice_leaves_one_readiness_check` pins the readiness registry
   to `[alvo-schema]`** and was in no task's Files list, while Task 5 Step 6 claimed the class passes
   in full. It is now `Registering_Alvo_twice_leaves_one_of_each_readiness_check`, expecting
   `[alvo-database, alvo-schema]` written out — not relaxed to a count or a "contains", because the
   fact's real claim is "no *duplicate* under one name", and a count would let a second registration
   of one check hide behind the arrival of another.
2. **Task 4 Step 1's `new SqliteSqlDialect().ReachabilityProbeStatement` cannot compile** — a default
   interface member is not a member of the implementing class. Read through the interface, as
   `test/_shared/sqlite/LockRecordingSqlDialect.cs` already does for `RowWindowClause`.
3. **`PublicApi.MMLib.Alvo.Testing.EntityFrameworkCore.verified.txt` does move**, contrary to the
   plan's "it should not": `AlvoSqlDialectContractTests` is a `public abstract class` in a shipped
   package and the added fact is public. Four baselines move, not three.
4. **`HealthCheckRegistration.Timeout` is a cooperative bound, not a hard deadline.** The framework
   cancels the token it handed the check and then awaits it, so a probe that ignores its token holds
   the request. The plan's own stub did exactly that and had to thread the token. The spec's "who
   imposes the bound" section and `AlvoHealth.DatabaseProbeTimeout`'s remarks are corrected rather
   than the escape hatch taken: a hard deadline would answer while the probe runs on, abandoning a
   task that holds a database connection. Honouring the token is instead an obligation on the port,
   asserted for every implementation by the new contract suite.
5. **A host convention that throws was mis-diagnosed as an unroutable schema.** Conventions run inside
   `Build()`, inside `catch (InvalidOperationException)`, so a host's own broken
   `RequireRateLimiting` was logged at `Critical` as "Alvo cannot route the applied schema".
   `AlvoDataApiConventions` now wraps each convention and raises
   `AlvoDataApiConventionException`, which the data source catches first and logs with its own
   message. The consequence is unchanged and has to be — an exception escaping an
   `EndpointDataSource` enumeration takes down the composite every probe is matched through.
   Pinned by `A_convention_that_throws_leaves_no_routes_and_blames_the_host_not_the_schema`.
6. **Three authorization-seam rationales went stale** and are corrected: `DataApiRoutingTests`'
   "a marker without a filter cannot be written", `AlvoDataApiEndpointRouteBuilderExtensions`' "no
   path to `IAlvoData` that skips the authorization seam", and the new `data-api.md` section. Each
   now says what it means — a statement about *this framework's* construction, not a guarantee
   against host code, which could already clear filter factories through `MapGroup("")`.
7. **`alvo-security-core-review` is not skipped.** The diff modifies `DataApiEndpoints.Protect` and
   `AlvoEndpointDataSource`; the checklist is earned by the area. Gate 2 below is corrected.
8. **Item "D" is filed as #182** and `#130` was given the F4 milestone, so the
   PLAN → issue → plan → PR chain holds. The non-breaking overload alternative is rejected in the
   spec's deviation 6, with its reason.
9. **`CHANGELOG.md` is updated** — the breaking return type, the new port and dialect member, and the
   operationally sharpest line: `/health/ready` can now answer 503 on a running host.
10. **The port gets a reusable contract suite** (`AlvoDataReachabilityContractTests` in
    `MMLib.Alvo.Testing`), as every other `Abstractions` port has, inherited by the SQLite and
    PostgreSQL legs. Its four obligations replace the per-engine duplicates the plan had written.
11. **`RelationalReachability`'s classification is pinned directly** by
    `RelationalReachabilityTests` over a scripted `DbConnection`: which failures are an answer, which
    propagate, and — the branch no real engine reaches on demand, and the guaranteed surviving
    mutant — that a provider exception raised after the bound elapsed is reported as cancellation.
    The guard also moved from a `when` filter to
    `cancellationToken.ThrowIfCancellationRequested()` inside the catch, so the *type* thrown is
    `OperationCanceledException` rather than the driver's own exception.
12. **Stale prose corrected**: `AlvoSchemaHealthCheck`'s "the one contributor",
    `AlvoHealthEndpointRouteBuilderExtensions`' "Alvo's own contributor", `AlvoHealth.ReadinessPath`'s
    remark, `docs/architecture/extensibility.md` rule 10, and the `CHANGELOG` line that still said
    the document declares no `servers` entry.
13. **Task 5 Step 2's red-state prediction is wrong** for
    `A_readiness_body_never_carries_the_reason_the_store_gave`: it passes before implementation,
    because the body is already the boot phase. It is a guard, not a driver.
14. **`RelationalReachability.cs` had to be added to `ChangeTrackerReachTests`' SQL-composing
    allow-list**, which the plan did not anticipate. It earns its place for a stated reason: it
    executes a per-dialect constant that names no table, carries no `WHERE` and binds no parameter.
15. **Staging is explicit, never `git add -A src test`.**

## Amendments after `csharp-reviewer`

16. **A real bug both earlier passes missed: `Seal()` did not run when the schema was refused.** It sat
    inside `Build()`, after `ReservedQueryKeys.EnsureNoneIsShadowed` and `FormatCatalog.Build` — so a
    schema those guards refuse installs the empty route table permanently, `Build()` never runs again,
    and the conventions stay open forever, silently collecting into a list nothing will ever read. That
    is exactly the "refused, not dropped" contract the type's own docs claim. Sealing moved to the top
    of `BuildOrRefuseToRoute`, outside the `try`, and pinned by
    `A_convention_added_after_a_refused_schema_is_refused_too` — verified non-vacuous by reverting the
    fix and watching that one fact go red.
17. **`RegistryShadowingAReservedKey` is now shared** rather than copied: the new fact needs the same
    refused-schema substitute `AlvoHealthTests` had privately, and two copies is how the two suites
    would come to be refused for different reasons.
18. **`A_host_supplied_probe_wins_over_the_drivers_default` now resolves the instance**, not only the
    descriptor count — a count of one proves the claim only by an argument about `TryAdd` semantics and
    would survive a refactor that registered the driver's probe first.
19. **The probe's per-request connection cost is stated in `AlvoReachabilityHealthCheck`'s own
    remarks**, not only in `host.md` and the spec — that file is where a reader of the check lands.

## Amendments after the final `alvo-plan-guard` pass

20. **Six documentation inconsistencies, all real, all fixed.** The retracted "this is what the
    framework's own convention builders do" claim survived in the shipped XML doc and in the spec's
    own Part 4 while deviation 7 retracted it two sections later; `AlvoEndpointDataSource`'s remarks
    still credited `Build()` with sealing after the fix moved it; the contract suite said "three
    obligations" over four facts — the same prose-vs-code count drift this PR fixes for the problem
    slugs; the `CHANGELOG` omitted the per-anonymous-request database I/O that `host.md`, the spec and
    the check's own remarks all state; the spec's "Files this touches" listed a Host test file that
    was never changed; and `extensibility.md` had an unwrapped line.
21. **The enforceable-invariant question is filed as #184**, not left as a caveat. Making "a marked
    endpoint is a gated endpoint" true by construction again (an Alvo `Finally` convention that
    verifies its own filter factory survived the host's) is a decision with open questions —
    `Finally` ordering, what the identity check is over — and it belongs in an issue rather than
    bundled into the PR that created the seam.

## Self-review notes

- **Spec coverage.** #130 → Tasks 2, 3, 8. #119 → Task 1. #133 → Tasks 4, 5, 6, 8. "D" → Tasks 7, 8. Every
  "Files this touches" row in the spec appears in a task, except `PublicApi.MMLib.Alvo.Testing.EntityFrameworkCore.verified.txt`,
  which moves only if adding a contract fact changes that assembly's public surface — Task 4 Step 9 reads
  every diff before accepting, so an unexpected one is caught rather than assumed.
- **Type consistency.** `IAlvoDataReachability.ProbeAsync` is spelled identically in the port, the EF
  implementation, the health check, the two stubs and all six facts. `AlvoReachability.Reachable` is a
  property and `Unreachable(Exception)` a method throughout. `AlvoDataApiConventions` (not
  `AlvoDataApiConventionBuilder`; the spec's file table was corrected to match) exposes `Add`, `Finally`,
  `ApplyTo`, `Seal`.
- **The one thing a task may discover and must report rather than route around**: if
  `HealthCheckRegistration.Timeout` turns out not to be enforced by `DefaultHealthCheckService`,
  Task 5's hanging-probe fact fails. The fix is then a `CancellationTokenSource` inside
  `AlvoReachabilityHealthCheck` with `AlvoHealth.DatabaseProbeTimeout`, linked to the caller's token — and
  the spec's "who imposes the bound" section has to be corrected to say so.
