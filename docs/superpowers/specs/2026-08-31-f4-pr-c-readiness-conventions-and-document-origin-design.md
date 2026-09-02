# F4 PR-C — the document names its origin, readiness reaches the database, and a host can decorate Alvo's routes

Issues: **#130** (the OpenAPI document's `servers` behind a path base), **#119** (a standalone
500 carrying an Alvo `type` slug), **#133** (`/health/ready` and the database-reachability port),
plus **#182**, filed while this design was under review, for the item that had no issue:
**`MapAlvoDataApi` hands a host nothing it can attach a convention to**.

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

A consequence worth stating rather than discovering: with no production change, these facts pin a
*third-party package's* behaviour, so a Dependabot bump of `Microsoft.AspNetCore.OpenApi` is
henceforth gated by them. That is the point — they are the only thing standing between a framework
regression and a document whose every path is wrong by a prefix.

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
the core depends on `Abstractions` alone — but **`internal`**, not public; see deviation 10. It must therefore be expressible without a relational
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

**The three obligations are asserted, not merely written.** `AlvoDataReachabilityContractTests` in
`MMLib.Alvo.Testing` holds every implementation to them — unreachable is an answer, a cancelled probe
throws, and the probe is repeatable — which is what every other port in `Abstractions` already has (§0
principle 1) and what a third-party driver otherwise has nothing to verify against. The classification
`RelationalReachability` performs on top of them — which failures are an answer, which propagate, and
what a provider exception raised *after* the bound elapsed is reported as — is pinned directly over a
scripted connection, because no real engine can be driven into those branches on demand.

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
framework links a cancellation source per registration, so the bound is one property on the
registration Alvo already writes.

**It is a *cooperative* bound, and this section's first draft overclaimed it.**
`DefaultHealthCheckService` cancels the token it handed the check and then awaits it — so a probe that
**honours** its token is reported as the registration's `FailureStatus` (`Unhealthy`, therefore 503),
while one that ignores the token holds the request for as long as it likes. That matters precisely
because this port is designed to admit a third-party implementation. Two things answer it, and neither
is a hard deadline inside the check: honouring the token is stated as an obligation on
`IAlvoDataReachability.ProbeAsync` and asserted for every implementation by the contract suite; and for
a probe that breaks the obligation anyway, the backstop is the orchestrator's own probe timeout, which
is outside this process either way. A hard deadline — answering while the probe runs on — would abandon
a task holding a database connection, which is worse than the failure it prevents.

Two facts pin what can be pinned rather than the documentation being taken on trust: a probe that waits
on its token answers 503, and the contract suite refuses an implementation that answers "unreachable"
for a bound that has already elapsed.

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

The statement is one `const` in that implementation. The first draft made it a **default interface
member on `IAlvoSqlDialect`**, reasoning from `RowWindowClause` and from the standing rule that
per-engine SQL is a port member rather than an `if` in the shared path. That was wrong twice, and
deviation 10 records why: the rule is about *branching*, and one ANSI literal branches on nothing;
and measured after the fact, **no dialect overrode it** — SQLite, PostgreSQL and `TSqlSqlDialect` all
inherited the default, so the member's only effect was one more obligation on a public interface
every out-of-repo dialect author reads.

Opening a pooled connection alone is not the probe. A pool hands back a connection it believes is
live, so a round-trip is what distinguishes "the pool has an entry" from "the database answers".

### What the check does not answer

Whether the schema is applied. That is `AlvoSchemaHealthCheck`'s, already registered under
`alvo-schema`; the new one registers under `alvo-database`. Two checks, two names, one endpoint —
and the readiness body still publishes the boot phase and nothing else, so a reachability failure is
a 503 whose reason is in the log and not on the wire (design deviation 59, unchanged).

### One cost this creates, found by the security-core checklist

`/health/ready` used to do **no I/O**. It now opens a connection per request, from the pool the Data
API shares, on a route that is anonymous by construction. So an unauthenticated caller who can reach
the port spends pool slots at their chosen rate, and a saturated pool times the probe out and drains
the pod — availability loss caused by a request rate rather than by the database. Bounded by the
two-second registration timeout, disposed per probe, and ordinary for a reachability probe rather than
unique to Alvo. **Filed as #183 rather than fixed here**, because the fix is a design decision of its
own (a short probe-result cache is the likely answer, and it buys a staleness window) and bundling it
would put an unmeasured cache inside the PR that introduces the probe.

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
`RequireRateLimiting` is a rate limiter a host believes it has. The framework itself is *quiet* in
that situation — see deviation 7 — so this is a deliberate departure, and the message names the cause.

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
   the idiomatic, discoverable return type. Filed as **#182** rather than left unfiled, so the
   PLAN → issue → plan → PR chain holds; the non-breaking alternative
   (`MapAlvoDataApi(Action<IEndpointConventionBuilder>)`) is rejected because it would be permanent
   API debt — two ways to do one thing — in a package that has not shipped.
7. **Throwing on a late convention is a deviation from the framework, not conformance to it.** An
   earlier draft of this design claimed "this is what the framework's own convention builders do"; it
   is not. `RouteEndpointDataSource`/`RouteHandlerBuilder` silently ignore a convention added after
   the endpoint is built. Alvo throws because its table is frozen once materialised and a dropped
   `RequireRateLimiting` is a rate limiter a host believes it has. The cost is stated: a host applying
   conventions from an `IStartupFilter` or a hosted service now gets an exception where every other
   `Map*` is silent.
8. **A host convention that throws gets its own diagnosis, not the schema's.** Conventions run inside
   the data source's materialisation, where an `InvalidOperationException` already means "this applied
   schema cannot be routed". The consequence stays identical — empty table, readiness `Failed`,
   because an exception escaping an `EndpointDataSource` enumeration takes down the composite every
   probe is matched through — but the log record names `MapAlvoDataApi()` instead of sending an
   operator to their descriptor.
10. **The port is `internal`, and the dialect member is gone — both reversed after the fact.** The
    first version of this design shipped three new public members (`IAlvoDataReachability`,
    `AlvoReachability`, `IAlvoSqlDialect.ReachabilityProbeStatement`). None of them is a contract a
    consumer needs: the shared EF path implements the probe once, so every EF-backed driver — F7's
    dynamic one included, being a dialect under that same path — inherits it without implementing
    anything, and a driver that cannot answer cheaply opts out by not registering it. The dialect
    member was measured to be overridden by nobody. So the port and its answer are now `internal`
    with `InternalsVisibleTo` for the four in-family assemblies, on the precedent
    `AlvoFrameworkTables` set in the same file and for its stated reason, and the statement is a
    `const` in `RelationalReachability`. The asymmetry is what decides both: `internal → public` and
    "add a default interface member" are free, their reverses are breaking. **The whole PR's shipped
    API delta is consequently one signature** — `MapAlvoDataApi`'s return type.

    One cost, paid deliberately: `AlvoDataReachabilityContractTests` can no longer expose the port in
    its own signature (CS0050 — a public method cannot return a less-accessible type), so the suite
    takes `IServiceProvider` and resolves the probe inside its bodies. That is the better shape
    anyway: it asks what a host gets from the driver's public entry point rather than what a test can
    construct by hand.
11. **The authorization seam's wording moves with the seam.** "A marked endpoint is a gated endpoint"
   is a statement about this framework's construction, not a guarantee against host code: a convention
   receives the `EndpointBuilder` and can clear its filter factories. That was already true through
   `MapGroup("")`, and is anyway true of a host that substitutes `IPolicyEngine`, so nothing is
   weakened — but three rationales that read as unconditional are corrected rather than left to age
   (`DataApiRoutingTests`, `AlvoDataApiEndpointRouteBuilderExtensions`, and the new `data-api.md`
   section). Whether the invariant should be made *enforceable* again rather than only correctly
   worded — an Alvo `Finally` convention that verifies its own filter factory survived — is **#184**,
   filed rather than decided here.

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
| ~~`src/MMLib.Alvo.Data.EntityFrameworkCore/IAlvoSqlDialect.cs`~~ | **reverted, see deviation 10.** The member was added, then removed: no dialect overrode it. |
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
| ~~`test/MMLib.Alvo.Host.Tests/AlvoHealthTests.cs`~~ | **not changed.** "The provider really registers a probe" is covered by `SqliteReachabilityTests.The_public_entry_point_alone_yields_a_resolvable_reachability_port`, and the standalone host's existing `Readiness_answers_an_unauthenticated_probe_over_a_booted_host` now exercises the real probe transitively — a third fact would have measured the same thing a third time. |
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
- ring2 green; the public-API baselines moved deliberately and judged.
- The change is labelled **needs-deep-review** and run against `alvo-security-core-review`: it
  modifies `DataApiEndpoints.Protect`, where the authorization filter is attached, and
  `AlvoEndpointDataSource`, which carries the "no ungated path to `IAlvoData`" guarantee. The
  checklist is earned by the *area*, not by whether a defect was found.
- `CHANGELOG.md` records the breaking return type, the new port and dialect member, and — the sharp
  one operationally — that `/health/ready` can now answer 503 on a running host.
