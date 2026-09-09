# The embedded-run sample + the JSON `Content-Type` guard — design

**Issues:** [#24] `[20] Embedded run — a sample host that mounts Alvo into its own app`, narrowed by its
own third comment to *the embedded sample* · [#191] `The Data API requires no Content-Type, which is a
CSRF vector in an embedded host that authenticates by cookie`
**Milestone:** F4 — Demo from the start (the two items `docs/PLAN.md` §3a leaves in the phase)
**Date:** 2026-09-09
**Sources read before designing:** `alvo-specifikacia.md` §"Režim 2 — Embedded (NuGet vo vlastnom
hoste)" and §"Spoločné kontrakty medzi režimami (záväzné)" points 1–3 · `baas-analyza.md` §2.13 (the
Abstractions contract towards providers *and embedded hosts*) · §0 principles 1, 2, 4, 5 ·
`docs/architecture/extensibility.md` rules 1–11 · `docs/architecture/data-api.md` §`POST
{prefix}/{entity}/query` · `docs/architecture/host.md` *What is left of #24* · the frozen
`examples/vehicle-registry/vehicles.alvo.json` · WHATWG Fetch §"CORS-safelisted request-header" ·
RFC 9110 §15.5.16 (415) · RFC 5789 §3.1 (`Accept-Patch`) · W3C LDP 1.0 §7.1.2 (`Accept-Post`,
IANA-registered) · RFC 7396 (`application/merge-patch+json`) · RFC 9457 §3.1.1

---

## 1. What this closes, and what it deliberately does not

Two issues, one PR, because neither is finished without the other.

**#24's remaining half is the embedded sample.** `docs/architecture/extensibility.md` fixes the
`AddAlvo()` / `IAlvoBuilder` seam and `AddAlvoIntegrationTests` exercises it, but a grep for `AddAlvo`
across `src`, `examples` and `playground` finds no host `Program.cs`. Embedded is one of the two
declared distribution modes (spec §"Režim 2") and it has no runnable example. The issue's Definition of
Done — *"both modes start up the same functional backend from the same descriptor"* — is the binding
form of spec §"Spoločné kontrakty" point 2 (*mount do Dockera = CLI apply = Management API =
`FromDescriptor()` v embedded*), and today only the standalone half of it is demonstrated.

**#191 is the guard that sample must ship with.** The sample is the first thing in this repository that
puts Alvo inside somebody else's ASP.NET Core pipeline, which is the exact context in which a
body-taking route with no `Content-Type` requirement becomes a live CSRF vector.

**Out of scope, stated so a later reader can tell a decision from an oversight.** #24's original scope
listed three more things; its own third comment already struck or split two of them, and this design
records the third:

| Struck or split | Why |
|---|---|
| `alvo apply` via CLI | No CLI project exists and `ApplyAlvoDescriptorAsync` is the door it will go through. Its own issue when it has a purpose (`host.md` *What is left of #24*). |
| `minio` + `mailhog` in compose | Deliberately absent — object storage and mail have no component behind them. The root compose says so in its own header comment. Stale scope, not pending work. |
| The published `mmlib/alvo` image, dashboard, Management API, `ALVO_*` env vocabulary, the system-schema version contract | All already listed in `host.md` *What is left of #24* and all standalone-side. Untouched here. |

And one thing this design *discovers* and deliberately does not fix — §2.1 and §8.1: an embedded host
cannot publish its own principal to the generated Data API. That is a capability not yet earned, filed
for F7, and the sample documents the limit rather than working around it.

---

## 2. Five things the code says, read before any of this was designed

Every one of them changed the design. The first two change what #191 *is*.

### 2.1 `AlvoContextFilter` overwrites a host-published principal — so #191's own premise is wrong

`AlvoContextFilter` is attached to every generated route (`DataApiEndpoints.Protect`) and its
`Invoke` does:

```csharp
_accessor.Principal = principal;   // AlvoContextFilter.cs:133
try { return await next(context); }
finally { _accessor.Principal = null; }
```

`principal` is `null` whenever the credential header is absent. So an embedded host whose middleware
sets `IAlvoContextAccessor.Principal` from its cookie has that value **discarded before the endpoint
delegate runs**, on every request.

Both #191's body and `data-api.md`'s recording of it describe the threat as *"embedded mode inside a
host whose own auth is cookie-based **and which populates `IAlvoContextAccessor`**"*. That path does
not exist. The record is corrected in this PR (§7, deviation 1) rather than left as prose that a
reader would reasonably act on.

`IAlvoContextAccessor.Principal` is not useless — it is what the *endpoint delegates* read, and
`IAlvoContextAccessor`'s own remarks are careful to call it "availability, not enforcement". It is
simply not a seam a host can write through on a generated route.

### 2.2 The reachable cookie door: `HeaderName` plus a custom resolver — so the vector is live

`AlvoAuthOptions.HeaderName` is an `init` property with a public setter path through the options
pattern, and `IAlvoContextResolver` is registered with `TryAddSingleton` (`Auth/Setup.cs`), so a host
registering its own takes it over. An embedded host can therefore write:

```csharp
services.Configure<AlvoAuthOptions>(o => o.HeaderName = "Cookie");
services.AddSingleton<IAlvoContextResolver, SessionCookieResolver>();
```

`AlvoContextFilter.Presented(request, "Cookie")` reads the browser's `Cookie` header like any other
header and hands the raw text to the host's resolver, which parses its session out of it. That is
cookie-authenticated **writes** on the generated Data API, reachable through the public API only, with
no framework change. Every body-taking route is then a CORS *simple* request: no preflight, cookies
attached.

So #191 is not defence-in-depth against a hypothetical future seam. It is the guard for a door that is
open today. This is the single most important finding in this document, and it is why the two issues
ship together.

### 2.3 What embedded *does* support cleanly, and it is better than the accessor would be

`IAlvoData` takes `AlvoContext` as an explicit parameter — deliberately, per its own remarks, because
the post-commit paths run with no request scope. And every type that identity needs is public:

- `AlvoContext` — a `sealed record` with `required` init members, publicly constructible;
- `UserId` — `public readonly record struct UserId(Guid Value)`;
- `IRoleCatalogProvider.DeclaredRoles` → `RoleCatalog`, with public `Resolve(IEnumerable<string>)`
  and a documented contract to **fail closed on `null`**;
- `Role.Anon` / `Authenticated` / `Admin` as the built-ins, and application roles mintable *only*
  through the catalog the applied descriptor primed (`Role.Application` is `internal` on purpose).

So a host's own endpoint builds its own caller and calls the port:

```csharp
var catalog = roles.DeclaredRoles
    ?? throw new InvalidOperationException("Alvo has not applied a descriptor yet.");
var caller = new AlvoContext
{
    User = new UserId(userId),
    Roles = catalog.Resolve(["authenticated", "inspector"]),
};
var page = await data.QueryAsync(new AlvoQuery { Entity = "vehicles" }, caller, ct);
```

That is the embedded data-layer story in ten lines, it exercises four public ports, and it fails closed
on an unapplied descriptor. It is what the sample shows.

### 2.4 `AddAlvo` registers the boot service, so an embedded host applies its descriptor with no call

`AlvoServiceCollectionExtensions.cs:125` registers `AlvoBootService` as an `IHostedService` through
`TryAddEnumerable`; the implementation is an `IHostedLifecycleService` so it runs before every other
`StartAsync`. The sample therefore needs no `ApplyAlvoDescriptorAsync` call and no ordering ceremony:
`AddAlvo(...).FromDescriptor(path)` plus `app.Run()` brings the schema up, and `/health/ready` gates on
it. Stating this matters because the obvious mistake — a sample that calls
`ApplyAlvoDescriptorAsync` at startup "to be explicit" — would teach the wrong thing and duplicate the
boot.

### 2.5 A sample project is constrained by the convention suite, not by taste

`SolutionConventionTests` globs `**/*.csproj` across the whole repository, `bin`/`obj` excluded. Four
of its facts bind a new project wherever it lives:

- `All_projects_follow_the_family_naming` — the name must be `MMLib.Alvo` or `MMLib.Alvo.*`. A sample
  called `EmbeddedHost` fails; `MMLib.Alvo.Samples.EmbeddedHost` passes.
- `Every_project_is_registered_in_the_solution` — it must be in `MMLib.Alvo.slnx`.
- `No_project_redeclares_an_inherited_msbuild_property` — no `TargetFramework`, `Nullable`,
  `ImplicitUsings`, `LangVersion` in the csproj.
- `No_project_pins_an_inline_package_version` — CPM only.

`Every_packable_src_project_has_a_tests_project` is scoped to `TopLevelFolder == "src"`, so a
`samples/` project does not trip it — but it will be `IsPackable=false` anyway, for the same reason
`MMLib.Alvo.Host` is: a `.nupkg` of an entry point publishes a surface no consumer references.

And the ring scripts select by **name**: `test-ring0` runs `*.Tests.dll`, which by naming excludes
`*.Tests.Integration.dll`; `test-ring2` globs `*.Tests.Integration.csproj`. So naming the sample's
suite `.Tests.Integration` puts it in ring2 with **no script change** — the same lever
`MMLib.Alvo.Host.Tests.Integration` already pulls.

---

## 3. Part A — the embedded sample

### 3.1 Shape and location

```
samples/
  Directory.Build.props                             imports the parent; IsPackable=false
  MMLib.Alvo.Samples.EmbeddedHost/
    MMLib.Alvo.Samples.EmbeddedHost.csproj          Sdk=Microsoft.NET.Sdk.Web
    Program.cs                                      the whole sample, read top to bottom
    appsettings.json
    README.md                                       what each seam is, and the one limit
test/
  MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration/  the proof (ring2)
```

`samples/` is a new top-level folder and a new `MMLib.Alvo.slnx` solution folder. It is not `examples/`
— that directory holds **descriptors**, and its `README.md` table is about which of them apply. A
sample is a *project*, and mixing runnable C# into the descriptor directory would make both harder to
describe.

**One project, not two.** The temptation is a `Samples.EmbeddedHost` plus a
`Samples.EmbeddedHost.Contracts` or a shared `Samples.Common`. There is one sample, so there is one
project; `package-boundary.md`'s rule ("a package is earned") applies to samples with even less
tolerance, because a sample's whole value is being readable in one sitting.

**Reference, not `PackageReference`.** The sample references `MMLib.Alvo` and
`MMLib.Alvo.Data.Sqlite` by project path, so it compiles against the tree it lives in and a breaking
change to the seam breaks the sample in the same build. A published-package sample would rot silently
until someone ran it.

### 3.2 The descriptor is `examples/vehicle-registry/vehicles.alvo.json`, and that choice is the DoD

The root `docker-compose.yml` mounts exactly that file into the standalone image at
`/alvo/descriptor.json`. Running the *same* file in the embedded sample makes the Definition of Done —
"both modes start up the same functional backend from the same descriptor" — a **comparison**, not an
assertion (§6.3). Any other descriptor would leave the claim to prose.

It also happens to be well suited: three entities (`owners`, `vehicles`, `inspections`), no tenancy to
explain, and rules that key on `@user.roles` with an application role (`inspector`) beside the
built-ins — which is precisely what makes `RoleCatalog.Resolve` load-bearing in the sample rather than
decorative.

The store is SQLite (`UseSqlite`) into a file under the content root, so `dotnet run` needs no
container. The standalone stack's PostgreSQL is not part of the claim: spec §"Spoločné kontrakty" point
1 is *one code, one descriptor*, and §0 principle 3 (engine-agnostic core) is what makes the engine
irrelevant to it — a point `DataApiEngineTests` already proves on both engines.

### 3.3 Two surfaces, one backend

The sample is an ERP-shaped app (spec §"Režim 2": *ERP scenár*) that happens to keep its records in
Alvo. It has two kinds of caller and they reach the data two different ways — which is the single most
important thing an embedded reader needs to understand, and the thing no amount of `AddAlvo`
documentation conveys.

**`/app/*` — the host's own endpoints, for the host's own cookie users.**
Cookie authentication (`AddAuthentication().AddCookie()`), a dev `POST /app/login` that issues the
cookie, and two endpoints — `GET /app/vehicles`, `POST /app/vehicles` — that resolve `IAlvoData` and
`IRoleCatalogProvider`, build an `AlvoContext` from the cookie's claims, and call the port. Alvo's
policy engine still decides: `vehicles.create` admits `admin` or `inspector`, so the sample's
`inspector` user can create and its plain `authenticated` user cannot, without the sample writing a
single authorization line of its own.

**`/api/alvo/*` — Alvo's generated Data API, for agents and machines.**
`app.MapAlvoHealth()` then `app.MapAlvoDataApi()`, with Alvo's own API-key credential
(`X-Alvo-Api-Key`) resolved by the built-in `ApiKeyContextResolver` from a dev key. Health is mapped
first because the seam requires it (`MapAlvoDataApi` refuses a host whose Data API services are absent,
and an operator facing that refusal needs a container that can still be probed). The return value of
`MapAlvoDataApi()` is used for a real convention — an endpoint tag Alvo's routes carry and the host's
own do not — so #182's `IEndpointConventionBuilder` is demonstrated rather than discarded.

**No credential is committed.** The dev API key's secret comes from configuration
(`Alvo:Auth:DevKeys:0:Secret`) with **no default**; absent, `AlvoAuthOptionsValidator` fails the host
at startup. The sample's `README.md` gives the `dotnet user-secrets` line. This is spec §"Spoločné
kontrakty" point 5 (*žiadne default credentials*) applied to a sample, and it is the same posture the
compose stacks already take with `ALVO_DEMO_KEY_SECRET`.

### 3.4 The seam inventory — what each part of `Program.cs` is there to show

A sample earns its place by demonstrating something a reader cannot get from the reference docs. Each
line below is in the sample for a named reason, and the `README.md` says which:

| Seam | What the sample shows | Rule it comes from |
|---|---|---|
| `AddAlvo(alvo => alvo.UseSqlite(cs).FromDescriptor(path))` | one entry point, provider by `Use*`, descriptor by `From*` | extensibility rules 1, 4 |
| No `ApplyAlvoDescriptorAsync` call | the boot applies it; a host adds no lifecycle code | §2.4 |
| `MapAlvoHealth()` **then** `MapAlvoDataApi()` | the parts stay public and the order is not free | rule 10 |
| `MapAlvoDataApi().WithTags(...)` | the convention builder is the seam for host-owned concerns | rule 10 / #182 |
| `AlvoApiOptions.RoutePrefix = "/api/alvo"` | Alvo mounts *beside* the host's own routes | `AlvoApiOptions.RoutePrefix` remarks |
| **No** `AddAlvoProblemDetails()` | an embedded host owns its error rendering | rule 10 / #119 |
| `IRoleCatalogProvider` + `RoleCatalog.Resolve` | how a host maps its own roles into Alvo's closed set, failing closed on `null` | `IRoleCatalogProvider` contract |
| `new AlvoContext { … }` + `IAlvoData` | Alvo as a policy-enforcing data layer for the host's own endpoints | `IAlvoData` remarks |
| `RequireJsonContentType` left at its default | the guard is on in embedded mode, which is where it matters | §4 |

Spec §"Režim 2" sketches `AddModule<T>`, `AddAuthorizationHandler<T>`, `UseAdmin`, `Hooks(...)` and
`.Embedded(e => e.SchemaPrefix("alvo"))`. **None of them exists**, and the sample does not pretend
otherwise — the spec's own preamble to those blocks says they are *"nie kontrakt … ukazujú ambíciu
developer experience"*. The `README.md` names them as the not-yet-built extension points so a reader
who arrives from the spec is not left wondering whether they typed it wrong.

### 3.5 What the sample does not do, and says so

- **It does not federate the cookie user onto the generated Data API.** It cannot (§2.1). The
  `README.md` states this in one short section: cookie users reach Alvo through `/app/*`, the
  generated `/api/alvo/*` routes are for API-key callers, and the reason is that
  `AlvoContextFilter` publishes the principal it resolved from the credential header and clears it
  again. The follow-up issue is linked from there (§8.1).
- **It does not demonstrate `HeaderName = "Cookie"`.** It is the door §2.2 found and it works, but a
  sample is a thing people copy. Putting a raw-`Cookie`-parsing resolver in the one readable example
  would propagate a shape the framework should be *closing*, not teaching. It is documented in
  `data-api.md` beside the guard, as the reason the guard exists.
- **It has no UI.** A page would double the sample's size and demonstrate nothing about Alvo. The
  `README.md` carries the `curl` lines for both surfaces instead.

---

## 4. Part B — the JSON `Content-Type` guard

### 4.1 The threat, precisely

WHATWG Fetch defines a **CORS-safelisted request-header** for `Content-Type` as one whose value, after
parsing, is `application/x-www-form-urlencoded`, `multipart/form-data` or `text/plain`. A request
carrying only safelisted headers is a *simple* request: the browser sends it cross-origin with cookies
attached and **no preflight**, and the response being unreadable does not undo the side effect. A
request with no `Content-Type` at all is trivially safelisted.

Requiring `application/json` therefore moves every body-taking route behind a preflight
(`OPTIONS` + `Access-Control-Allow-Headers`), which an HTML form cannot generate and which a
cross-origin `fetch` cannot pass without the server opting in. That is the whole mechanism: it is a
property of the *browser*, not of Alvo's ordering, which is why §4.3's ordering decision costs the
defence nothing.

Today the guard is absent from every body-taking route. Seven of them, not the three #191 lists — the
issue predates `PUT` (create-or-replace, #105) and the batch trio (#26-era work):

`POST {prefix}/{entity}` · `PATCH {prefix}/{entity}/{id}` · `PUT {prefix}/{entity}/{id}` ·
`POST {prefix}/{entity}/query` · `POST|PATCH|DELETE {prefix}/{entity}/batch`

### 4.2 The four decisions #191 asked for

The issue deliberately stopped short of committing to these. All four are settled here.

**(a) Accepted media types: `application/json` and any `application/*+json`, parameters ignored.**
`application/json` is the document's declared request body. The `+json` structured suffix (RFC 6839)
is accepted because RFC 7396's `application/merge-patch+json` is the standard spelling a PATCH client
may reasonably send, and refusing it would refuse a *more* precise declaration of the same bytes.
Parameters (`; charset=utf-8`) are parsed and ignored: the readers are UTF-8-only by construction, and a
body actually encoded otherwise fails the JSON scan and earns the existing `malformed-json` 422 — a
diagnosis about the bytes, which is the accurate one. Everything else is refused, `text/plain` and
`application/x-www-form-urlencoded` included; the latter was already refused on the query route for
its own separate reason (`HttpRequest.Form`'s second set of bounds), and this makes the refusal
uniform.

**(b) A body with no `Content-Type` at all is refused.** It is the safe reading and it is the half that
actually closes the vector — a cross-site form can be made to send no `Content-Type`, and a request
without one is CORS-safelisted by omission. The cost is real and worth naming: a hand-rolled
`curl --data-binary @row.json` with no `-H` breaks. `curl -d` was already broken, because it sends
`application/x-www-form-urlencoded`. The fix is one flag and the 415's own `detail` says it.

**(c) The status is 415 with a new problem slug.** RFC 9110 §15.5.16 is the answer for "the content is
in an unsupported format", and `AlvoProblemTypes` gains
`UnsupportedMediaType = "unsupported-media-type"`. It keys on the refusal's **kind** — "the request did
not arrive as JSON" — which is what that catalogue's own rule demands, and it is distinct from
`MalformedQuery` (Alvo read the content and refused it) and from `UnreadableRequest` (the server
refused before Alvo looked).

**(d) The guard is on by default and a host may opt out.**
`AlvoApiOptions.RequireJsonContentType`, default `true`. Secure-by-default is §0 principle 5; the
opt-out exists because an embedded host owns its pipeline — a host running ASP.NET Core antiforgery,
or one whose Alvo routes are unreachable from a browser at all, should not be forced through Alvo's
version of a defence it already has. `false` restores today's behaviour exactly, including the
document (§4.5).

### 4.3 Where it is enforced, and why authorization still wins

**Beside the other pre-body guards, one line per delegate, and *first* among them.**
`DataApiEndpoints`' five body-taking delegates already open the same way — resolve the decision, then
guard the request's *headers* before touching its body. The media-type guard goes at the head of that
header block:

```csharp
var decision = EnsureOperationIsAllowed(policies, entity.Name, kind.ToDataOperation(), context);

if (JsonContentType.Refuse(http.Request, options) is { } unsupported) { return unsupported; }

EnsureUnconditional(http.Request);                       // the existing precondition guard
var key = IdempotencyKey(http.Request, context, options);
```

**First, and not merely somewhere in the block.** A request that is not even in the form this endpoint
reads should not be answered with advice about `If-Match` or `Idempotency-Key`. It also makes all five
delegates identical — decision, then media type, then the other header guards — where an
each-where-it-fell placement would leave an ordering asymmetry nothing asserts, in a change whose whole
argument is that the ordering is deliberate.

`JsonContentType` is one `internal static` class with one method returning `IResult?`; the five call
sites are `MapCreate`, `MapUpdate`, `MapReplace`, `MapQuery` and `BatchAsync`.

**Three enforcement points were considered; this one preserves the ordering rule.** The alternatives
were rejected for stated reasons rather than taste:

| Where | Why not |
|---|---|
| Inside `BoundedJsonBody.ReadAsync` — the one place all three readers funnel through | Those readers return a *violations list*, which every caller renders as a 422. A 415 would have to travel as a `BodyRefusal` member that each surface's violation catalogue must remember *not* to word as a violation, and `CodeOf` would need an arm that throws. One chokepoint, three places to get it wrong, and a refusal type smuggled through a channel built for another status. |
| An `IEndpointFilter` added in `Protect` for body-taking kinds | Genuinely the single registration point, and it would refuse before the delegate did any work. Rejected because it lands **between** the scope 403 and the *policy* 403 — the policy decision is resolved inside the delegate — so a policy-denied caller sending `text/plain` would be answered 415 instead of 403. That reverses a rule this layer states explicitly and treats as security-core, and `Protect` would still need an `if (kind is …)` that a future kind could miss. Not worth changing an authorization ordering as a side effect of a header check. |
| A `GuardAsync` arm on a thrown exception, like `EnsureUnconditional` | The exceptions `GuardAsync` catches are `MMLib.Alvo.Abstractions` port exceptions with meaning to a provider. 415 is a pure HTTP concern; minting an Abstractions exception for it would widen the port to describe a transport. |

**403 before 415, and 401 before both.** `AlvoContextFilter` answers the credential 401 and the scope
403 before the delegate runs at all; `EnsureOperationIsAllowed` answers the decision's 403 on the line
above the guard. So the existing rule holds unchanged — *"an unauthorized caller must be told they are
unauthorized, not that their body was malformed"* — and the guard still refuses before a byte of the
body is read, which is the resource half of the same rule.

**What "after the decision" does not buy, measured rather than assumed.** `PolicyEngine.ResolveOperation`
denies at the decision layer for four reasons only: no descriptor applied, an unconfigured operation, a
tenant-scoped entity with no tenant, and a predicate reading a caller value the caller lacks. A
*configured* rule always resolves to an **allow carrying a `USING` / `WITH CHECK` predicate the port
enforces per row** — so a caller whom `'admin' in @user.roles` will ultimately refuse is *not* denied
here, and for that caller the 415 comes first. This was found by a fact written the other way round,
which failed. It is not a regression and not new: `EnsureUnconditional`'s 412 already precedes that same
port 403 for the same caller, and a 415 names nothing about the entity, the row, or whether it exists —
its fix is knowable to the caller before they send anything. The ordering facts therefore measure the
three refusals that really do precede it: the credential 401, the scope 403, and a *decision* 403 (a
tenantless caller on a tenant-scoped entity).

Ordering costs the CSRF defence nothing: the mechanism is the browser's preflight, decided before the
request is sent (§4.1). Server-side precedence only decides which true thing a caller is told first.

**Exhaustiveness is proved by a test, not by the call sites.** Five call sites means a sixth
body-taking route could forget one. §6.2's fact enumerates every body-taking `DataApiEndpointKind` and
drives a `text/plain` request at each — the same construction this file already uses to prove `Protect`
is on every endpoint two ways.

### 4.4 The response

An RFC 9457 problem document, `application/problem+json`, `type:
https://alvo.dev/errors/unsupported-media-type`, status 415, no `violations` array, and a `detail` that
names the fix (§0 principle 4):

> `Send this request with 'Content-Type: application/json'.`

Plus, on the two methods that have a registered header for exactly this, the machine-readable form of
the same advice:

| Method | Header | Defined by |
|---|---|---|
| `POST` | `Accept-Post: application/json` | W3C LDP 1.0 §7.1.2, IANA-registered |
| `PATCH` | `Accept-Patch: application/json, application/merge-patch+json` | RFC 5789 §3.1 |
| `PUT` | *(none)* | no registered `Accept-Put` exists |
| `DELETE` | *(none)* | no registered `Accept-Delete` exists |

The asymmetry is deliberate and recorded: adopting two registered headers is §0 principle 4 served by
prior art, and inventing `Accept-Put` to make the table tidy would be exactly the "inventing a variant
of a standard" this project calls a defect. `ProblemResultFactory` already has the shape for this —
`UnauthenticatedResult` is a problem plus the `WWW-Authenticate` RFC 7235 requires — so this is a
second instance of an existing pattern, not a new one.

### 4.5 The document

`DataApiDocumentation` gains an `UnsupportedMediaType` response, added to `SharedRefusals` (published
once under `components.responses`, referenced from each operation) and listed by `ResponsesFor` on the
seven body-taking kinds. `List`, `Get` and `Delete` do not get it: they read no body, so the status is
unreachable from them, and `ResponsesFor`'s contract is that *each entry is a claim that a request can
reach it* — a claim `OpenApiDocumentTests.Every_documented_status_code_is_one_the_endpoint_can_actually_return`
drives a real request for.

**When `RequireJsonContentType` is `false` the 415 is omitted from the document.** That keeps the
reachability contract true, and it has a precedent in the same file: the 304 is listed only for an
entity whose rows carry a version, because otherwise the status is unreachable and a document listing
it would describe behaviour that does not exist.

### 4.6 Two behaviour changes, recorded rather than discovered

- **A stripped batch `DELETE` body may now be a 415 instead of a 422.** `data-api.md` documents that
  RFC 9110 §9.3.5 lets an intermediary strip a `DELETE` body, and that the empty-batch refusal turns
  that into a 422 rather than a silent success. If the intermediary strips `Content-Type` with the
  body, the answer becomes 415. That is not a regression — the request no longer arrives as JSON, and
  415 says so more precisely than "your batch is empty" — but it moves a documented case, so
  `data-api.md` is updated to describe both.
- **`AlvoProblemTypes.All` grows by one.** It is enumerated rather than reflected precisely so a fact
  can assert the catalogue and the code agree; the new slug joins the list and the `UriOf` guard keeps
  working unchanged.

---

## 5. Public API delta

Three additions, each a conscious SemVer act under extensibility rule 11. The
`PublicApi.MMLib.Alvo.verified.txt` baseline grows, which trips the turn-review-gate's
grown-baseline check — answered here rather than at commit time:

| Addition | Why it is `public` and not `internal` |
|---|---|
| `AlvoApiOptions.RequireJsonContentType` | An option is configuration; a host cannot opt out of something it cannot see. Same category as the other seven members. |
| `AlvoProblemTypes.UnsupportedMediaType` | That type's own remarks answer this: *"Public because it **is** the contract: an agent or an embedded host branching on a refusal needs the same constants the framework emits, and a copied string literal is how the two come to disagree."* |
| *(nothing else)* | `JsonContentType`, the `IResult` implementation and the document entry are all `internal`. |

`MMLib.Alvo.Abstractions` is untouched — there is no new port, and that is the point: the guard is a
property of the HTTP surface, not of the data layer, and an embedded host calling `IAlvoData` directly
never passes through it (nor needs to: there is no browser in that path).

The sample project is `IsPackable=false`, so it publishes no surface at all.

---

## 6. Testing

### 6.1 The tiers, and why each fact is where it is

| Suite | Tier | What it holds |
|---|---|---|
| `test/MMLib.Alvo.Api.Tests` (`AlvoApiWorld`) | ring0 | every media-type decision, the ordering, the headers, the option |
| `test/MMLib.Alvo.Api.Tests` (`OpenApiDocumentTests`) | ring0 | the document entry, its reachability, the snapshot |
| `test/MMLib.Alvo.Api.Invariants.Tests.Integration` | ring2 | the guard holds across generated descriptors, sabotage-provable |
| `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration` | ring2 | the sample boots, both surfaces work, and the DoD |

### 6.2 The guard, ring0

Over `AlvoApiWorld`, one fact per claim:

1. `application/json` is accepted on each of the seven body-taking routes.
2. `application/merge-patch+json` is accepted (the `+json` suffix rule, not a special case).
3. `application/json; charset=utf-8` is accepted (parameters ignored).
4. `text/plain` is refused with 415 — **driven over every body-taking `DataApiEndpointKind`**, which
   is the exhaustiveness fact §4.3 owes.
5. `application/x-www-form-urlencoded` is refused with 415.
6. A body with **no** `Content-Type` is refused with 415.
7. The 415 document carries `type: …/unsupported-media-type`, no `violations`, and a `detail` naming
   `Content-Type: application/json`.
8. `Accept-Post` is present on the POST refusals and `Accept-Patch` on the PATCH refusal, with the
   media types §4.4 lists; neither is present on the `PUT` or `DELETE` refusal.
9. `RequireJsonContentType = false` restores acceptance of `text/plain` on every one of the seven.
10. The default is `true` — a fact of its own, or the option is invisible to mutation: flipping the
    initializer would leave facts 1–8 green under a suite that always sent JSON.
11. A caller a policy denies gets **403, not 415**, for a `text/plain` body — the ordering rule.
12. A caller whose credential cannot be used gets **401, not 415** — the same rule one step earlier.
13. A read route is **unaffected**: `GET {entity}` and `GET {entity}/{id}` with a `text/plain`
    `Content-Type` and no body still answer 200. The guard must not spread to routes that parse no
    body, or it refuses a request that was fine.

### 6.3 The sample, ring2

`WebApplicationFactory<Program>` over the sample host (an `InternalsVisibleTo` in the sample's csproj,
which is what makes a top-level `Program` reachable — the same arrangement any minimal-API test uses).
`AlvoSharedArchTests=false`, because the suite maps to no production assembly.

1. **It boots.** `/health/ready` reports healthy, which means the descriptor applied — `AlvoBootService`
   ran and the schema is up (§2.4). A sample that does not start is worse than no sample.
2. **The DoD, mechanised.** The sample's served OpenAPI **path set** equals the standalone
   `MMLib.Alvo.Host`'s over the same `vehicles.alvo.json`, modulo the route prefix. This is the fact
   that turns *"both modes start up the same functional backend from the same descriptor"* from prose
   into a check, and it is the reason §3.2 picks that descriptor. Compared as a set, deliberately:
   memory of #26 is that the e2e suite pins the path set by equality and that is what catches a route
   quietly appearing or vanishing.
3. **The host's own surface works, and Alvo's policy decides.** A cookie user with `inspector` can
   `POST /app/vehicles`; a cookie user with only `authenticated` is refused by the *descriptor's*
   rule, not by the sample's code; an unauthenticated caller is refused by the cookie scheme.
4. **Alvo's own surface works and is default-deny.** An API-key caller reaches
   `GET /api/alvo/vehicles`; an anonymous caller does not.
5. **The guard is on in the sample.** A `text/plain` `POST /api/alvo/vehicles` earns 415. The sample is
   where #191's threat model lives, so the sample is where the guard is asserted end to end.

### 6.4 The invariant suite, ring2

The #26 suite exists for exactly this class of claim — a property that must hold for *N generated
projects*, not for one fixture. One behaviour invariant: **every body-taking route of every generated
descriptor refuses a non-JSON media type with 415**, with its counterpart in `Sabotage.cs` proving the
invariant can actually fail. Adding it there rather than only in ring0 is what stops the guard from
being correct for `vehicle-registry` and absent for an entity shape nobody tried.

### 6.5 What is deliberately not tested

- **No load run.** The guard is a header comparison on a path that then reads a body; there is nothing
  to measure and `test/load`'s gate is judged on `min`, which this cannot move.
- **No mutation run locally.** Post-merge on `main`, per the hard rule.
- **No e2e (TeaPie) change.** `test/teapie-field-service` sends `application/json` throughout, so it
  is unaffected — and its OpenAPI path pin is untouched because this adds a *response*, not a path.
  Verified rather than assumed, since that pin is equality-based.

---

## 7. Deviations, stated

1. **From #191's own text and `data-api.md`:** the threat is *not* reachable through
   `IAlvoContextAccessor`, which `AlvoContextFilter` overwrites (§2.1). It is reachable through
   `AlvoAuthOptions.HeaderName` plus a custom `IAlvoContextResolver` (§2.2). Both documents are
   corrected in this PR. The conclusion — require `application/json` — is unchanged and now rests on a
   door that exists.
2. **From spec §"Režim 2"'s illustrated API:** `AddModule<T>`, `AddAuthorizationHandler<T>`,
   `UseAdmin`, `Hooks(...)`, `.Embedded(e => e.SchemaPrefix(…))` and `app.MapAlvo("/api/alvo")` (a path
   argument) do not exist. The spec's own preamble marks those blocks as ambition rather than
   contract; the sample uses what is built and its `README.md` names the rest as not-yet-built. The
   route prefix is set through `AlvoApiOptions.RoutePrefix`, which is the shipped shape.
3. **From RFC 9110's `Content-Type` handling:** a `charset` parameter is parsed and **ignored** rather
   than validated. A non-UTF-8 charset lands on the existing `malformed-json` 422, which describes the
   bytes accurately. Validating it would add a second refusal for the same underlying failure.
4. **`Accept-Post` is a W3C LDP header, not an RFC 9110 one.** It is IANA-registered and means exactly
   what is needed; the alternative was inventing nothing and leaving POST without the machine-readable
   fix, or inventing `Accept-Put`, which is worse. `PUT` and `DELETE` therefore carry no `Accept-*`.
5. **From `baas-analyza.md` §328's `Content-Type` guidance:** that bullet is about *upload* content
   sniffing ("never trust the client's declared MIME; sniff magic bytes"). This guard does the
   opposite — it trusts the declared type as a *gate* and never as a description of the bytes, which
   the JSON scan still decides. The two are compatible; naming it here so a later reader does not read
   §328 as contradicting §4.2.

---

## 8. Follow-ups filed, not fixed

### 8.1 An embedded host cannot federate its own identity into the generated Data API

`AlvoContextFilter` publishes the principal it resolved from the credential header and clears it after
the delegate, so a host that resolves its own user in middleware has no way to hand that user to a
generated route (§2.1). The seam a host actually wants is either an `IAlvoContextResolver` that is
consulted even when no credential header is present, or an explicit "the host has already resolved
this caller" path the filter respects.

**Proposed milestone: F7.** It is a capability not yet earned — host-identity federation — rather than
a debt on shipped code, which is the triage question `docs/PLAN.md` §3a applies. It also touches the
security core, so it wants its own design and its own `alvo-security-core-review` pass, not a corner
of this PR.

### 8.2 Already-filed items this design touches and leaves alone

`#206` (the batch `DELETE` request body) is adjacent to §4.6's first bullet — if that verb moves to
`POST /{entity}/batch/delete`, the stripped-body case disappears with it. `#132` (a readable refusal
for a missing descriptor mount) and `#134` (Scalar behind a path base) are standalone-side and
untouched. `#191`'s own "which media types / missing type / opt-out" questions are answered here and
the issue closes.

---

## 9. Acceptance

This design is met when:

1. `samples/MMLib.Alvo.Samples.EmbeddedHost` runs with `dotnet run` plus one user-secret, serves
   `/app/vehicles` to its own cookie users and `/api/alvo/vehicles` to an API-key caller, both over
   `examples/vehicle-registry/vehicles.alvo.json`, and its `README.md` explains every seam in §3.4
   plus the limit in §3.5.
2. The sample's OpenAPI path set equals the standalone host's over the same descriptor, as a test.
3. Every body-taking route refuses a non-JSON or absent `Content-Type` with a 415 problem document,
   `RequireJsonContentType` defaults to `true` and turns it off, authorization still answers first, and
   the document lists the 415 exactly where a request can reach it.
4. The invariant suite holds the guard across the generated descriptors, and its sabotage counterpart
   proves the invariant can fail.
5. `data-api.md`, `extensibility.md` and `host.md` record what changed — including the correction in
   §7.1 — and `docs/PLAN.md` §3a reflects that F4's remaining work is done.
6. ring2 is green, `alvo-plan-guard` is dispatched, and the PR carries an `alvo-pr-report` page.
