# F4 PR-C — the document names its origin, readiness reaches the database, and a host can decorate Alvo's routes

Issues: **#130** (the OpenAPI document's `servers` behind a path base), **#119** (a standalone
500 carrying an Alvo `type` slug), **#133** (`/health/ready` and the database-reachability port),
plus one item that had no issue: **`MapAlvoDataApi` hands a host nothing it can attach a
convention to**.

Grouped because all four sit on the *deployment* surface rather than on the data path: what a
document advertises to the next client, what a 500 says, what an orchestrator is told, and what a
host may attach to Alvo's routes. None of them touches the rule engine, CEL, tenancy or the
authorization filter, so **this is not a security-core change**. #133 adds one public port and one
health check; the rest is a return type, tests, and the record.

---

## What was measured before anything was designed

Three of the four issues describe a defect that has since moved. Each was measured against the
build on `main` at `ce3f44c` (SDK `10.0.100`, `Microsoft.AspNetCore.OpenApi` `10.0.11`) with a
throwaway probe before a line of design was written, because designing against the issue text
alone would have produced work with nothing to fix.

### #130 — `Microsoft.AspNetCore.OpenApi` already emits `servers`, and it is per request

The issue's premise is that the document "still lists `/api/owners` as a path key with no `servers`
entry", and its stated reason is that `OpenApiDocumentTransformerContext` carries no `HttpContext`
and the document is cached per document name. Measured, all three parts of that are false of this
runtime:

| Mount | `servers[0].url` | first path key | resolves? |
|---|---|---|---|
| root | `http://localhost/` | `/api/owners` | yes |
| `UsePathBase("/alvo")`, requested at `/alvo/openapi/v1.json` | `http://localhost/alvo` | `/api/owners` | yes |
| `UsePathBase("/alvo")`, requested at `/openapi/v1.json` | `http://localhost/` | `/api/owners` | yes |
| `MapGroup("/backend").MapAlvoDataApi()` | `http://localhost/` | `/backend/api/owners` | yes |

The framework builds `servers` from `Request.Scheme`, `Request.Host` and **`Request.PathBase`**, so
the path-base component is already there; and it is **not** frozen by a first request — asking for
the document with a path base and then without it, in either order and repeatedly, returns the
right origin each time. Rows 2 and 3 are consistent rather than contradictory: `UsePathBase`
*strips* a prefix when the request carries one, so a host under a path base genuinely serves two
origins and the document truthfully names whichever one the caller reached.

`AlvoDocumentTransformer` never touches `document.Servers`, and nothing else in Alvo does either.

**So #130 needs no production code.** What it needs is the thing whose absence let the issue stand
for a release: a fact. The origin's *scheme* and *host* halves are already pinned
(`AlvoHostForwardedOriginTests`); its **path-base half is pinned by nothing**, in either package.
Deleting the `PathBase` argument from the framework's own server-URL construction, or a future Alvo
transformer that overwrote `Servers`, would leave the whole suite green while every path in the
document became wrong by the prefix — which is exactly what the issue describes and exactly what it
turns out nobody could observe.

### #119 — delivered by PR4, in both pipelines

`AlvoProblemTypes.Internal` exists and is in `All`; `AlvoExceptionHandler` logs the exception with
its stack trace and renders `https://alvo.dev/errors/internal`; `AddAlvoProblemDetails()` is the
opt-in registration, and `AlvoHost.CreateBuilder` calls it with `UseExceptionHandler()` first in
`Compose`. `AlvoHostProblemDetailsTests` measures the standalone pipeline — the half the issue said
a fact over an embedded fixture cannot see — including the log half.

**#119 is therefore verified and closed in this PR with no code change.** The one thing it leaves
behind is a stale sentence: `docs/architecture/data-api.md` still says "the nine slugs are
`AlvoProblemTypes.All`" while the catalogue declares eleven, having gained `unreadable-request` and
`internal` since. A count in prose that disagrees with the code it points at is the kind of drift
this repository fixes on sight.

### "D" — the capability is reachable today; the seam is not

`MapAlvoDataApi` returns `IEndpointRouteBuilder`, so there is nothing for a host to chain
`RequireRateLimiting`, `RequireAuthorization`, `CacheOutput` or a telemetry tag onto. The framing
"the host has only global middleware" is, however, wrong — measured:

```csharp
var group = app.MapGroup(string.Empty);
group.MapAlvoDataApi();
group.WithMetadata(new Marker());     // reaches every generated endpoint
```

works. `AlvoEndpointDataSource.GetGroupedEndpoints` forwards to the nested minimal-API sources with
the `RouteGroupContext`, and those apply the group's conventions — so an **empty route group** is a
working, undocumented workaround, and a probe over a data source of exactly that shape confirms the
convention lands on the endpoint.

So "D" is a **discoverability and idiom** fix rather than a new capability, and it is designed as
one: no new mechanism, the return type every ASP.NET Core `Map*` already has. That distinction is
worth stating, because it is the difference between "hosts cannot rate-limit Alvo" (false) and
"hosts have to know that `MapGroup("")` is legal and that Alvo forwards grouped endpoints" (true,
and enough of a defect to fix).

---

## Part 1 — #130: pin the document's origin, including its path base

Two facts, one per package, mirroring how #121 was pinned.

**Core (`MMLib.Alvo.Api.Tests`).** Under `app.UsePathBase("/alvo")`, request the document at
`/alvo/openapi/v1.json`, read `servers[0].url`, resolve a path key against it the way a generated
client does, and follow the result. Pinning `servers[0].url` **whole** is load-bearing for the same
reason `PathBaseTests` pins `Location` whole: in-process the host answers the unprefixed URL too,
so "resolve and get 200" alone cannot tell a correct origin from a missing prefix. The follow-up
runs anyway, because a URL that resolves nowhere is the failure a client actually meets, and a
string comparison passes for one.

**Host (`MMLib.Alvo.Host.Tests`).** Behind a trusted proxy's `X-Forwarded-Prefix`, the served
document's origin must carry the prefix, and a client resolving `server + path key` must reach the
row **through a model of the proxy** — `AlvoHostPathBaseTests.FollowThroughTheProxyAsync`'s exact
device, and for its exact reason: that is where the 404 lives, and the host cannot produce it.
The untrusted control comes for free from the same helper set: with the switch off, the origin must
*not* carry a caller-supplied prefix, or an anonymous caller chooses the base URL the document
hands the next client.

Neither fact asserts anything about `paths`, and that is deliberate — the path keys are already
covered by `Every_mapped_route_appears_in_the_document_and_nothing_else_does`, and #130 is about
what those keys are resolved *against*.

## Part 2 — #119: verify, correct the record, close

No code. Run the two suites that own the claim, fix the "nine slugs" count, and close the issue
naming the facts that hold it.

## Part 3 — #133: the database-reachability port

### What is actually owed

`/health/ready` exists, with the tag-based registration seam and one contributor
(`AlvoSchemaHealthCheck`: "the boot decided the schema and primed the policy catalog"), and
`docker-compose.yml`'s healthcheck already points at it rather than at liveness. The issue's own
scope list is therefore two-thirds already done; what is missing is the **continuing** answer. A
database that goes away after boot is invisible to both routes, so an orchestrator has nothing to
drain traffic on — and the core may not open a connection itself (§0 principle 2).

### Where the port lives, and what it may depend on

`MMLib.Alvo.Abstractions`, beside `IAlvoData`, because the **core's** health check consumes it and
the core depends on `Abstractions` alone. It must therefore be expressible without a relational
connection, a `DbConnection` or EF — which it is: "can you still be reached" is a question a
document store or F7's dynamic driver answers as readily as a relational one.

Not `IAlvoSqlDialect`: that port is relational statement shape and lives in the EF package, which
the core cannot reference. Not a member on `IAlvoData`: that is the record-CRUD contract, and every
implementation — `InMemoryAlvoData` included — would grow a member it has no store behind.

### Two states, and why there is no third

```csharp
public interface IAlvoDataReachability
{
    ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default);
}
```

`AlvoReachability` carries `IsReachable` and, when it is not, the `Failure` — an `Exception`, for
**the log only**. That is #119's trade applied to a probe: the operator needs the reason, the
anonymous caller may not have it, and a port that returned a bare `bool` would have to swallow the
exception inside an implementation that has no logger and no business having one. `HealthCheckResult`
itself carries an exception for the same reason, so the shape has precedent in the same domain.

**There is no `Unknown`.** The issue asks whether a provider that cannot answer cheaply may opt out;
the answer is *yes, by not registering the port at all* — which needs no enum member. A third state
would have to be mapped to a status: `Healthy` is fail-open and `Unhealthy` is a pod that never
receives traffic, so every mapping is wrong for somebody and the state exists only to be
mis-handled. Absence of a registration is the same opt-out with nothing to get wrong.

Consequently: **no port registered → readiness is exactly what it is today.** That is fail-open, and
it is the right direction here rather than a lapse. Readiness is an availability gate, not an
authorization one; a third-party provider that ships without a probe should not make every pod
permanently unready. Both in-repo drivers register one, so the opt-out is reachable only
deliberately.

### Who imposes the bound

`HealthCheckRegistration.Timeout`, not a timeout inside the check and not a new options type. The
framework already links a cancellation source per registration, so the bound is one property on the
registration Alvo already writes, and a probe that hangs is reported as the registration's
`FailureStatus` — `Unhealthy`, and therefore 503 — rather than holding the request. A fact pins that
rather than the documentation being taken on trust: a probe that never returns must answer 503.

Two seconds. A refused connection fails in milliseconds; the case a bound exists for is a *hanging*
one — packet loss to a database whose driver would otherwise wait out its own 15-second connect
timeout — and a readiness answer that arrives after the orchestrator's own probe timeout is a
failure with extra steps. It is a constant on `AlvoHealth` rather than configuration because no
operator has asked for a knob, and the value that would need tuning is the *orchestrator's* probe
timeout, which lives outside this process. Making it configurable is a follow-up with a real
request behind it, not a default.

### One implementation, at the EF seam

`AlvoEfCoreProvider.AddRelationalProvider` — the single funnel `UseSqlite`, `UsePostgreSql` and any
out-of-repo EF driver already pass through — registers it once, over the
`RelationalConnectionFactory` every other store here uses. The issue says "implemented once per
`MMLib.Alvo.Data.*` package"; that was written before the shared EF path existed as the place
`IAlvoData`, `IOutboxStore` and the three schema services are all composed. One implementation is
strictly better than two identical ones, and it means a third relational driver inherits a correct
probe rather than owing one.

The engine-specific half is one statement, and it *is* engine-specific — `SELECT 1` is right for
both engines Alvo ships and for T-SQL, and wrong for Oracle (`SELECT 1 FROM DUAL`). So it is a
**default interface member on `IAlvoSqlDialect`**, exactly like `RowWindowClause`: the majority
spelling is the default, only a dialect that genuinely differs overrides it, and adding it breaks no
existing implementation. Per the standing rule, per-engine SQL is a port member and never an `if` in
the shared path.

Opening a pooled connection alone is not the probe. A pool hands back a connection it believes is
live, so a round-trip is what distinguishes "the pool has an entry" from "the database answers".

### What the check does not answer

Whether the schema is applied. That is `AlvoSchemaHealthCheck`'s, already registered under
`alvo-schema`; the new one registers under `alvo-database`. Two checks, two names, one endpoint —
and the readiness body still publishes the boot phase and nothing else, so a reachability failure is
a 503 whose reason is in the log and not on the wire (design deviation 59, unchanged).

Cache and message-bus reachability stay out of scope, as the issue says: neither subsystem exists,
and each should bring its own probe when it lands. The tag is what makes that additive.

## Part 4 — "D": `MapAlvoDataApi` returns an `IEndpointConventionBuilder`

`MapHealthChecks` returns one; `MapControllers` returns a convention builder; `MapGet` returns
`RouteHandlerBuilder`. Returning `IEndpointRouteBuilder` from a `Map*` that maps a *set* of
endpoints is the one shape in that family that hands a host nothing.

The mechanism is the conventions list ASP.NET Core uses itself: `MapAlvoDataApi` returns a small
builder that appends to the data source's own convention and finally-convention lists, and
`AlvoEndpointDataSource.Build()` applies them to each route it maps, before the endpoints are built.
Because materialisation is lazy — the first request that builds the matcher — a host's conventions
are always complete by the time they are needed, and no ordering obligation is added to a call whose
whole design was to have none.

A convention added **after** materialisation must throw. It cannot be honoured (the table is frozen
by design, for the reason `AlvoEndpointDataSource` records), and silently dropping a
`RequireRateLimiting` is a rate limiter a host believes it has. This is what the framework's own
convention builders do, and the message names the cause.

**`MapAlvo()` keeps returning `IEndpointRouteBuilder`, and `MapAlvoHealth()` is not made
chainable.** The umbrella maps the probes *and* the Data API, so one convention builder over both
would let a host attach `RequireAuthorization` to `/health/live` — a probe presents no credential, so
that is a container killed and restart-looped by its own liveness gate. A host that wants
conventions calls the parts, which is already the documented composition and already how
`MapAlvoHealth`/`MapAlvoDataApi` are described. The umbrella-equivalence fact is unaffected: it
compares endpoint data sources, not return types.

---

## Deviations from the issues' own text

1. **#130 ships no production code.** Its stated cause — no `servers`, a request-less transformer
   context, a per-name document cache — is not true of this runtime; measured above. The work is the
   acceptance fact the issue asks for and the correction of two documents that record the defect as
   open.
2. **#119 ships no production code.** PR4 delivered it, including the standalone fact the issue said
   was owed. Only the stale slug count is fixed.
3. **#133's port is implemented once at the EF seam, not once per provider package.** The shared EF
   path is where every other store service is composed; two identical implementations would be the
   drift the seam exists to prevent.
4. **#133's port has no "cannot answer" state.** Opting out is not registering it. A third state
   would have no correct mapping to a health status.
5. **#133's timeout is a constant, not configuration.** `HealthCheckRegistration.Timeout` carries it;
   the tunable that matters is the orchestrator's probe timeout, outside this process.
6. **"D" is framed as DX, not capability.** `app.MapGroup("")` already works; measured. The fix is
   the idiomatic, discoverable return type.

## What this PR does not do

- **#134** (Scalar behind a path base) — a different resolution rule, the docs UI's own fetch, and
  its outcome is unmeasured. It stays open and is not approximated here.
- **Cache / message-bus readiness** — neither subsystem exists.
- **A configurable reachability timeout, or a reachability probe on liveness.** Liveness evaluates
  no check at all, by design, and nothing here changes that.
- **#179** (a cap on `Prefer: count`) — decided, shaped, and deliberately not in this PR's surface.
- **Any change to `MapAlvoHealth`'s or `MapAlvo`'s signature.**

## Files this touches

| File | Why |
|---|---|
| `src/MMLib.Alvo.Abstractions/Data/IAlvoDataReachability.cs` (new) | the port (#133) |
| `src/MMLib.Alvo.Abstractions/Data/AlvoReachability.cs` (new) | its two-state answer (#133) |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs` | `ReachabilityProbeStatement` default member (#133) |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/RelationalReachability.cs` (new) | the one implementation (#133) |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` | registers it for every EF driver (#133) |
| `src/MMLib.Alvo/Api/AlvoHealth.cs` | the check's name and the probe bound (#133) |
| `src/MMLib.Alvo/Api/HealthSetup.cs` | registers the check, tagged ready, with its timeout (#133) |
| `src/MMLib.Alvo/Api/Internal/AlvoReachabilityHealthCheck.cs` (new) | maps a probe to a status, logs the reason (#133) |
| `src/MMLib.Alvo/Api/AlvoDataApiEndpointRouteBuilderExtensions.cs` | the new return type ("D") |
| `src/MMLib.Alvo/Api/Internal/AlvoEndpointDataSource.cs` | holds and applies the conventions ("D") |
| `src/MMLib.Alvo/Api/Internal/AlvoDataApiConventions.cs` (new) | the returned builder ("D") |
| `test/_shared/api/AlvoApiWorld.cs` | one setup knob: the conventions a host attaches to `MapAlvoDataApi()`. The document under a path base needs none — `SendAsync` already takes the path. |
| `test/MMLib.Alvo.Api.Tests/OpenApiServersTests.cs` (new) | #130, core leg |
| `test/MMLib.Alvo.Host.Tests/AlvoHostPathBaseTests.cs` | #130, proxy leg |
| `test/MMLib.Alvo.Api.Tests/AlvoHealthTests.cs` | reachability → 200/503, absent port, hanging probe (#133) |
| `test/MMLib.Alvo.Host.Tests/AlvoHealthTests.cs` | the provider really registers one (#133) |
| `test/MMLib.Alvo.Data.Sqlite.Tests/*` | reachable and unreachable on a real engine (#133) |
| `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/*` | the same on PostgreSQL (#133) |
| `test/MMLib.Alvo.Api.Tests/DataApiConventionTests.cs` (new) | "D" |
| `test/**/PublicApi.*.verified.txt` | Abstractions, the EF package and the core all move |
| `docs/architecture/data-api.md` | the slug count; #130's paragraph; the new return type |
| `docs/architecture/host.md` | #133 and #130 are no longer owed |

## Definition of Done

- `servers[0].url` carries the request's path base, pinned whole, in the core and behind a modelled
  proxy in the host; a client resolving a path key against it reaches the endpoint.
- `/health/ready` answers 503 when the database cannot be reached and 200 when it can, on both
  engines, and a probe that hangs is a 503 rather than a held request.
- A host chains `RequireRateLimiting` onto `MapAlvoDataApi()` and the limit is enforced on a
  generated route; a convention added after the first request throws.
- `MapAlvo()` and `MapAlvoHealth()` are unchanged, and the umbrella-equivalence fact still passes.
- #119 and #130 are closed with the facts that hold them named; #133 is closed; #134 stays open.
- ring2 green; the three public-API baselines moved deliberately and judged.
