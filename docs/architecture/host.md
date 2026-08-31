# The standalone host

> The surviving detailed record for `MMLib.Alvo.Host`, in the same role
> `data-path.md` plays for the port and `data-api.md` for the HTTP layer. PR4's
> Superpowers plan is discarded once merged; what outlives it is here, and the
> deviations it introduced are in the F3 design doc's *Deviations added by PR4*.

## What the host is, and is not

It is a `WebApplication` over the core's public seams and nothing more: configuration
binding and validation, one driver registration, `MapAlvo` (the two probes plus the
generated Data API), and a docs UI. **Nothing here applies the descriptor** — that is the
framework's boot, described below — so the standalone host is now the same shape as an
embedded one: `AddAlvo(…)`, then `MapAlvo()`. It is **not** the full standalone story — the
dashboard, the Management API, the CLI and the published image are #24's remainder, in F4.

## The five boot stages, and why nothing in the host sequences them

The boot is `AlvoBootService`, an `IHostedLifecycleService` in the **core**, and it runs in
`StartingAsync` — before *every* `IHostedService.StartAsync`, including the one that binds
the socket (measured: design fact 7). So the host composes a pipeline and never sequences a
database against a route table.

| Stage | What it does | Risk |
|---|---|---|
| 0 | Load the descriptor, JSON-Schema-validate, parse, map to a `SchemaModel`, compile the policy catalog and the reserved-name/format checks | no database access at all |
| 1 | Bring the framework's own `alvo.*` tables up, **unconditionally** (A:508/A:515) | idempotent DDL on Alvo's own chain |
| 2 | Compare the descriptor against `IAppliedSchemaStore` and branch on *uninitialized* / *unchanged* / *drifted*; only *drifted* is governed by `Alvo:Schema:Startup` | the only stage that may touch the host's tables |
| 3 | Publish the compiled policy catalog and the schema registry, and publish `AlvoBootState` | none |
| — | Routes materialise from the primed registry at **first enumeration**, after the boot has finished | none |

Stage 1 has **no port of its own**, deliberately: the system schema is owned by whichever
driver implements `IAppliedSchemaStore`, and that driver cannot answer a single call without
it, so stage 2's read *is* stages 1 and 2 at once, in every mode — `Skip` included. A port is
earned the moment a driver's system schema grows a table no store call touches, and PR5a paid
it: `alvo_outbox` is the first such table and **`IOutboxStore`** is the port it earned —
mandatory at boot from that commit, per `package-boundary.md`.

**What waits on stage 3, besides the routes.** PR5a's outbox dispatcher is a
`BackgroundService`, and on .NET 10 the whole of `ExecuteAsync` runs off the startup thread, so
"not before the schema is primed" **cannot** be expressed by hosted-service registration order.
It therefore awaits `AlvoBootState` explicitly, and a boot that refused leaves the pump claiming
nothing. That is not decoration: an unprimed policy catalog knows no entity, so every event
would match no hook, count as filtered and be *retired* — silent, permanent loss. Details in
[`events.md`](./events.md), *The dispatcher*.

**The apply→map coupling is gone, and so is the ordering folklore that used to live here.**
`MapAlvo` may be called before or after the schema exists: the Data API's endpoints are read
off the applied schema at *enumeration* time rather than at map time (`data-api.md`, *Route
generation happens at enumeration time*). Priming is the boot's job on every boot, including
the unchanged restart, so the case that used to serve zero routes while reporting healthy
cannot arise from ordering at all. `MigrationResult.EnsureApplied()` still exists and is
still right — the host simply no longer calls it; it belongs to the explicit
`ApplyAlvoDescriptorAsync` path the CLI and the Management API will use.

**Options validation runs before the boot, and that is now a framework guarantee rather than
a line of host code.** `Host.StartAsync` runs every `ValidateOnStart` registration before the
first `StartingAsync` (measured: design fact 8), so a mis-typed mount path or driver name
fails the start with the database exactly as it was found. `AlvoHost.ValidateOptions` — which
called `IStartupValidator` by hand precisely because the old apply ran *before* validation —
was therefore **deleted**, and
`A_credential_the_startup_validation_refuses_leaves_the_database_untouched` still passes,
which is what proves the guarantee moved rather than went missing.

A boot that refuses throws out of `StartingAsync`, so the server **never binds**: there is no
window in which the container answers anything at all with no schema. What the operator reads
and what the process exits with is the next section.

## How the process ends, and why the exit code is 78

`Program.cs` is one line — `return await AlvoHost.RunAsync(args);` — because everything worth
a test lives in `AlvoHost`: `CreateBuilder` registers, `BuildAsync` maps, and `RunAsync` is
the process, including the refusal an operator reads and the code they get.

- **A recognised configuration failure prints a sentence on stderr and exits `78`.** Exactly
  two shapes are recognised (`AlvoHostExit.IsConfigurationFailure`):
  `OptionsValidationException` — every option value the host or the framework refused,
  whether by `ValidateOnStart` or by the driver selection that runs during registration — and
  `AlvoStartupRefusedException`, the boot's own refusal (drift under `Verify`, a plan that
  would discard data). `OptionsValidationException.Failures` are printed one per paragraph
  rather than through `Message`, which joins them with `"; "` and runs two multi-line
  refusals into one unreadable line: a container with two things wrong is fixable in one
  restart.
- **78 is `EX_CONFIG` from `sysexits.h`**, the established code for "something was found in
  an unconfigured or misconfigured state". A bare `1` would be indistinguishable from every
  other failure, which is exactly the information #132 says was lost; 78 lets a deployment
  script or an orchestrator hook branch on "an operator has to change something" versus
  "retrying might help". It also sits below the shell's reserved range (126, 127, 128+n), so
  it cannot be misread as a signal the way the observed **139** was misread as a
  segmentation fault.
- **Everything else still propagates unhandled**, deliberately. A blanket `catch` would take
  the runtime's own report and whatever crash dump the deployment configured away from every
  genuine defect, so a named predicate is used instead and a fact asserts an unrelated
  exception is *not* one of the two shapes.
- **The application is disposed either way.** `BuildAsync` owns the application until it
  returns one (a refused `Compose` disposes it, because reading
  `IOptions<AlvoHostOptions>` is what runs the validation and a live service provider behind
  it holds the connection pool and the database file); `RunAsync` owns it afterwards with
  `await using`. A refused *boot* is disposed there, and since the apply moved into the host
  lifecycle that is the only place it can be.

That closes **#132**: a mis-typed mount now reads
`Alvo cannot start: no project descriptor at /alvo/descriptor.json.` followed by the two
fixes (`docker run -v ./project.alvo.json:/alvo/descriptor.json mmlib/alvo`, or
`Alvo__DescriptorPath=…`), and exits 78. The refusals are written in one place
(`AlvoHostConfiguration`) because the same wording has to be raised from two moments — the
driver is chosen while the container is still being built, and every value is validated again
on the built container.

## Configuration

The framework's options (`AlvoOptions`, `AlvoApiOptions`, `AlvoAuthOptions`) are bound from
`Alvo:*`, `Alvo:Api:*` and `Alvo:Auth:*`; the host's own decisions live in
`AlvoHostOptions` (`Alvo:DescriptorPath`, `Alvo:Database:*`, `Alvo:PathBase`,
`Alvo:ForwardedHeaders:Enabled`, `Alvo:Docs:*`).
The container form is the standard .NET double-underscore spelling
(`Alvo__Database__Provider`), not the `ALVO_*` names spec §X.1 sketches — see the design's
*Deviations added by PR4*.

`AlvoDevApiKey`'s collection members (`DevKeys`, `Roles`, `Scopes`) are getter-only, and
`ConfigurationBinder` populates them anyway — it binds into an existing non-null
`ICollection<T>` rather than needing a setter. Measured, not assumed: the boot facts
configure the whole credential through `Alvo:Auth:DevKeys:0:*` and a create that needs
`'admin' in @user.roles` succeeds, which is only possible if the getter-only `Roles` list
was filled from configuration. So no shape had to change to make the container's
environment a usable credential source.

The database is chosen by name, and an unknown name is refused rather than defaulted
(`AlvoDatabaseSelector`). A missing connection string is defaulted **only** for SQLite: a
PostgreSQL host with none must fail, because the alternative is quietly writing rows to a
container-local file that vanishes with the container.

**`AlvoHostOptions` is validated at startup, which discharges deviation 48 and meets A:91.**
`AlvoHostOptionsValidation` is an `IValidateOptions<AlvoHostOptions>` registered with
`ValidateOnStart` (through `TryAddEnumerable`, so composing twice validates once), and it reports
*every* refusal rather than the first — a container with two things wrong is fixable in one restart.
It checks that the descriptor is actually **at** the configured path, not merely that the path is
non-empty: a time-of-check/time-of-use window microseconds wide, against the single likeliest way a
first `docker run` goes wrong. The refusals name the environment spelling an operator can type
(`Alvo__DescriptorPath`, not `Alvo:DescriptorPath`) and live as `internal` members of
`AlvoHostConfiguration`, because the driver refusal is raised while the container is still being
*built* and the same wording has to come out of both moments.

**No default credential.** §2.14's acceptance criterion is that the image never ships a
preset login, so the host seeds no API key. A host with none configured still starts and
still refuses every operation, because an anonymous caller is judged by the same
default-deny policy as any other (deviation 23). Two facts hold that line: an anonymous
*write* is refused (a *read* would be an honest 200 with zero rows and would prove nothing),
and every `appsettings*.json` the image publishes is asserted to declare no `Alvo:Auth`
section — the realistic way a preset login reaches an operator is a dev key added there
for convenience, which no runtime fact can tell apart from one the deployment configured.
That assertion reads the files **through `ConfigurationBuilder.AddJsonFile`**, not through
`JsonNode`: the binder is case-insensitive and a `JsonNode` indexer is not, so a lowercase
`"alvo"` or `"auth"` would otherwise bind a working credential past a green fact.

## The startup mode, and what production should set

`Alvo:Schema:Startup` (`Alvo__Schema__Startup`) decides what a boot may do when the
mounted descriptor no longer matches the schema already applied to the database.
**It defaults to `Apply`**, in the core and therefore in this image too — the image
sets nothing, because there is no longer a policy of its own to state.

That default is for the loop the product exists for: edit the descriptor, restart,
it works. Initialization is exempt from the mode in every mode but `Skip`, so a
bare `docker run` against an empty database works whatever this is set to; the
default is what makes the *second* run — the one after the first edit — work too.
It never means "lose data on boot": a plan that drops or narrows anything is
refused in every mode, including during initialization, unless
`Alvo__Schema__AllowDestructive=true`.

**A production deployment should set `Verify` and apply the descriptor from a
migration job**, and this is an **opt-out rather than an opt-in** — stated plainly
because the cost is real:

| Mode | On drift | What it costs |
|---|---|---|
| `Apply` *(default)* | applies the plan | every replica of a rolling deploy attempts the DDL, and the application needs DDL rights against its own database — what EF Core's guidance advises against. Plus a descriptor **rollback** that cannot be applied — though a pod holding an older descriptor now stands down as not-ready instead of crash-looping (**#145**) — both below |
| `Verify` | refuses, printing the steps and the fix | a descriptor edit does not take effect until the migration job runs |
| `Skip` | reads the applied snapshot — that read is also what brings the framework's own tables up — and ignores whatever drift it found | the schema is entirely somebody else's business. Refused in one state only: Alvo has recorded nothing **and** the live schema does not match the descriptor, i.e. nothing has verified the schema exists |

Replicas racing the same DDL **converge** rather than crash-looping (the boot's
write is a version row first, then the DDL, in one transaction, with one bounded
retry), so `Apply` on a replica set is not an outage.

**The cost the table understates, and it is the sharper one: a rollback may not be
able to boot.** A forward deploy under `Apply` advances the applied snapshot with no
operator action and no decision. Rolling the descriptor *back* — redeploying the
previous artifact, the first thing anyone does — then plans a `DropField` against the
schema the forward deploy wrote, the always-on destructive gate refuses it, and **the
rollback cannot be applied.** Under `Verify` the operator chose that forward apply
knowingly and can plan the way back; under `Apply` they never chose it. Recovering means
bumping the older descriptor's `revision` above the applied one — which makes it an
artifact the history has not seen — **and** `Alvo__Schema__AllowDestructive=true` if the
plan back discards data, accepting the loss of whatever the new column now holds; or
applying the older descriptor from a migration job. The flag **on its own is no longer
enough**, deliberately: it means "I accept losing data", never "I accept serving an
older descriptor than the database" (#145, deviation 74). This
is pinned (`AlvoHostRestartTests.A_descriptor_that_drops_a_field_fails_the_restart_and_names_the_step`)
and `AlvoHostBootTests` states the shape in its own words: *"the previous descriptor is
destructive relative to the schema the failed start wrote, so rolling the deployment back
does not recover. One typo in an environment variable, one unbootable database."* It does
not change the default; it is the cost the default carries, and it is the strongest
argument on the record for setting `Verify` in production.

**A rolling deploy holding two descriptors is the other cost, and the rest is #145.**
Two replicas holding *different* descriptors — old and new pods overlapping — each diff
against what the other just applied. What that actually produces was **measured**, and an
earlier version of this section got it wrong:

- **A descriptor that only adds over what the other applied wins the database.** The
  history becomes `[1, 2]` and the schema is the newer descriptor's; both pods report
  `Ready`, and the one holding the older descriptor serves rules compiled against a
  schema the database now has more than (`ConcurrentColdStart.DriftedDescriptor`, both
  engines).
- **The pod holding the older descriptor cannot take its turn back.** Reverting means
  dropping the newer column, and the destructive gate refuses that in every mode. So the
  schema does not oscillate; the subset pod simply cannot serve, by exactly the mechanism
  the rollback paragraph above describes. It used to *crash-loop* over that refusal; it
  now stands down as not-ready with a diagnostic naming both revisions (#145, below).
- **The additive-vs-additive case is refused, not merged.** A adds `region`, B adds
  `city`: B's plan against A's applied snapshot *drops* `region`, which is destructive, so
  B refuses and the database ends on one descriptor's schema. Measured by
  `ConcurrentBootTests.Two_replicas_adding_different_fields_end_on_one_descriptors_schema_not_the_union`,
  which asserts the applied field list and not merely the revision history. **This
  corrects a claim published here and in #145** that the database ends up with *both*
  columns — a schema no deployed descriptor declares. That was theorised, and it is wrong:
  nothing can reach it, because reaching it needs a plan that adds without dropping and no
  such plan exists for either descriptor.

**#145 is the resolution, and it has landed**: the apply is now ordered from
`IDescriptorVersionStore`'s append-only history. Before it decides anything a boot would
change the schema with, it asks whether *this* descriptor's canonical content is in the
history at a revision older than the current one — and if it is, the process **stands
down**: it starts, primes nothing, reports **not ready**, and logs a refusal naming the
revision it is against the revision the database is at. So an orchestrator drains the pod
rather than restart-looping it, and the two costs above change shape:

- the pod holding the older descriptor no longer crash-loops, and no longer reports the
  wrong problem (it used to say "destructive change refused", which sends an operator to
  discard data to recover from being one deploy behind);
- the changes the destructive gate cannot see — an index or constraint added one way and
  dropped the other, a pair of declared renames pointing at each other — no longer
  oscillate, because the older pod is stopped before it reaches the DDL at all.

It does **not** make a rollback appliable: the plan back is still a drop and the
always-on destructive gate still refuses it. What it fixes is which of the two true
things the operator is told. The declared `revision`, if a repository maintains it, is
honoured as an override in one direction only — it can say "you are older", never "you
are newer" — so a decorative counter nobody bumps changes nothing. Design:
`docs/superpowers/specs/2026-08-03-apply-ordering-from-history-design.md`.

One writer (a migration job plus `Verify` on the pods) remains the shape that cannot get
into any of this in the first place.

```yaml
# production: the schema is the migration job's, and the pods only serve it
environment:
  Alvo__Schema__Startup: Verify
```

A value the mode cannot be read as fails the start naming all three modes, and an
empty value reads as "not set" rather than as a typo, because an environment
variable set to nothing is a shell accident.

## Behind a reverse proxy

`Alvo:PathBase` calls `UsePathBase`; `Alvo:ForwardedHeaders:Enabled` calls `UseForwardedHeaders` with
`XForwardedFor|Proto|Host|Prefix` and cleared `KnownIPNetworks`/`KnownProxies`. Both run after
`UseExceptionHandler` and before the mapping, because both decide the request's `PathBase` and that is the URL
a 201's `Location` advertises (#121). `KnownIPNetworks`, not the obsolete `KnownNetworks` — the latter is
`ASPDEPR005` on .NET 10 and this repository builds warnings as errors. Both lists are cleared because a
container cannot know its proxy's address, and their IPv6-loopback defaults would drop every header an ingress
or a sidecar sends. Both `Clear()` calls are individually measured: leaving either one in place fails
`A_trusted_proxys_forwarded_prefix_becomes_the_path_base`, which is why the host's test world stamps a routable
`RemoteIpAddress` onto the connection — TestServer leaves it unset, and `ForwardedHeadersMiddleware` skips its
known-address check entirely for a remote address it does not know.

**Forwarded headers are off by default**, and that is a security decision rather than a conservative one:
`X-Forwarded-Prefix` chooses the URL a 201 advertises, so an untrusted caller honoured by default would choose
where the next client is sent. `An_untrusted_forwarded_prefix_is_ignored` is the fact that holds it.

**The flags are *registered* only when the switch is on, not merely *used* only when it is on.** ASP.NET Core
has a forwarded-headers switch of its own — `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, the recipe every
container guide gives — and it registers a `ForwardedHeadersStartupFilter` that calls `UseForwardedHeaders`
against the *same* `ForwardedHeadersOptions` instance the host configures. A host that configured its flags
unconditionally therefore handed that filter Alvo's permissive set, `X-Forwarded-Prefix` included and both
known-address lists cleared, while `Alvo:ForwardedHeaders:Enabled` was still `false`: an internet client sent
`X-Forwarded-Prefix: /evil` and got `201 Location: /evil/api/...`. Alvo's `Configure` runs after the
framework's, so its values won. Two switches, one options object, and only one of them documented as the
trust decision — the conditional registration is what makes the documented one true.
`The_frameworks_own_forwarded_headers_switch_does_not_grant_alvos_trust` holds it.

**There is deliberately no explicit `UseRouting()`.** The widely cited rule — Microsoft Learn still states it —
that `WebApplication` needs `UseRouting` *after* `UsePathBase`, or routes match before the path is rewritten,
no longer holds: `UsePathBaseMiddleware` re-runs matching over the rewritten path itself. Measured under this
runtime, not assumed: a probe answers 200 for `UsePathBase` and 404 for the same rewrite performed by hand, and
removing the call from the pipeline leaves every path-base fact green. `UseForwardedHeaders` does not touch
`Path` at all, so it never raised the question.

## Docs

`AddOpenApi` is called by the **host**, never by the core (`ApiSetup.AddAlvoApi` says why: serving a document
is a hosting decision), and `Scalar.AspNetCore` renders it at `/scalar` from `/openapi/v1.json`. **One
ordering is load-bearing, and it is the registration one:** the host's document transformer registers
**before** `AddAlvo`, because registration order is transformer order and Alvo appends to `info.description`
rather than replacing it. The docs **routes** still map after `MapAlvo`, but only because that is the order
they read in — the document is generated per request by enumerating the endpoint data sources, so nothing
about its content depends on when its own route was registered. (It used to be presented as load-bearing for
the opposite reason, back when the endpoints existed only if the apply had already run.)
`Alvo:Docs:Enabled=false` removes both routes — the UI *and* the document, because the switch is about
publishing the API's shape at all, and a page without its document renders an error.

Scalar is the only reason the Host carries a third-party package, and it is why `package-boundary.md` records
rule (a) alongside rule (c) for this project. `Microsoft.AspNetCore.OpenApi` is referenced **directly** here
even though the core already brings it: a package's build targets do not travel through a `ProjectReference`,
and that package's target is what sets `InterceptorsNamespaces` for the XML-comment source generator
`AddOpenApi` switches on. Without the direct reference the generated file is emitted and then refused with
`CS9137`.

**What "Scalar renders it" is asserted as.** Not a 200 with HTML: Scalar's page is a static shell that fetches
the document in the browser, so a page aimed at a route nothing serves still answers 200 and fails only in
front of the reader. `Scalar_renders_the_document_the_host_serves` therefore reads the document URL *out of*
the rendered page, resolves it the way `scalar.aspnetcore.js` does, requires it to be the route this host
mapped, and follows it to a document containing the descriptor's own routes. The route Scalar fetches is also
pinned explicitly (`AddDocument(..., routePattern)`) rather than left to Scalar's default pattern, so the page
and `MapOpenApi` cannot drift apart.

**Behind a path base this is unverified.** Scalar emits the document URL *relative* and resolves it in the
browser against `window.location.pathname` minus the prefix it was initialised with — so under `Alvo:PathBase`
the resolution happens in JavaScript this suite does not run. The compose stack is therefore deliberately
path-base-free, and the gap is filed as **#134** rather than guessed at with a test that could not fail for
its stated reason. The issue records the resolution rule and says plainly that its outcome is *unmeasured* —
not working, and not known-broken. It is the third of the path-base family: #121 (`Location`, fixed here),
#130 (the document's `servers`, closed — it carries the request's path base), #134 (this).

## Health — two probes, and they are configured oppositely

Both routes are the **core's** (`MapAlvoHealth`, composed by `MapAlvo`), so an embedded host gets
exactly what the container gets. The host mirrors only `/health/live` as `AlvoHost.LivenessPath`,
forwarding to the core's constant; readiness is deliberately not re-spelled here.

| Route | Evaluates | Answers |
|---|---|---|
| `/health/live` | **no** health check at all | 200 for any process that is up |
| `/health/ready` | every check tagged `ready` | **503** until Alvo's boot published `Ready`, 200 after |

**Liveness evaluates nothing on purpose.** A failing liveness probe has the container killed and
restarted, which is the wrong answer to "the migration job has not run yet" — the Kubernetes
documentation warns that exactly this mistake cascades under load. A failing readiness probe only
removes the pod's address from the service, which is the right consequence, so everything
conditional lives there. The asymmetry also means a health check somebody adds later without much
thought lands where being wrong costs traffic rather than the process.

**Alvo's own contributor reports `Unhealthy`, never `Degraded`, and the fact asserts the status
code.** The framework maps `Degraded` to **HTTP 200** and Kubernetes counts any 2xx as success, so a
degraded gate is no gate at all — an assertion about the reported health *string* would stay green
while the probe served 200 to a container with no schema. `Degraded` is deliberately left mapping to
200 rather than remapped behind an option, because remapping it would make that mutation stop going
red.

**What readiness is for, given that a refused boot never listens.** The refusal is the strong end of
the guarantee and it is not going anywhere, so readiness earns its place on the states published
*after* a boot succeeded. There is a reachable one today: an applied schema the Data API refuses to
route — a substituted `ISchemaRegistry`, a schema applied by an older build, F7's dynamic entities —
is recorded on `AlvoBootState` when the endpoint table materialises, which is *after* the server is
listening. Readiness reports `Failed`, the orchestrator drains the pod, and `/health/live` keeps
answering 200 so nothing kills a container for a schema no restart can fix. That refusal used to be
*thrown* out of `EndpointDataSource.Endpoints`, which the matcher enumerates through the composite of
every source in the application: liveness answered 500 too, permanently.

One host option was checked as a *second* candidate and does **not** qualify:
`HostOptions.ServicesStartConcurrently` flips the host's `abortOnFirstException` to `false`, which
reads like "the start continues and binds the socket", but `Host.StartAsync` rethrows after **each**
phase, so a refused `StartingAsync` still aborts before the web host service is started. Measured
(`AlvoBootServiceTests.A_refused_boot_binds_no_socket_even_when_services_start_concurrently`) and kept
as a fact, because it is the only composition that could have broken the strong end.

**Readiness publishes the phase and nothing else** (`Pending` / `Ready` / `Failed`), and the
`HealthReport` handed to the response writer is discarded. `AlvoBootState.Failure` is the provider's
own message for a stage-1 or stage-2 failure — measured to carry a filesystem path today, and able
to carry a connection string from any third-party `IAppliedSchemaStore` — and this route is
unauthenticated by construction, because a container probe presents nothing to authenticate with.
The operator gets the full reason on stderr and in the log (design deviation 59). The check's
*description* and the HTTP *body* are two independent barriers, so two facts hold the line: mutating
either one turns exactly one of them red.

**The body is `text/plain` carrying the bare phase word, and that is a decision rather than an
omission** — this framework publishes RFC 7807 for every other refusal, so the shape is worth
recording. One writer answers both the 200 and the 503, and a *problem* document describes a problem,
so `application/problem+json` would be wrong for half of what it answers. A JSON object
(`{"phase":"Pending"}`) leaks no more than the word does, but it advertises a contract for a body
whose only consumer — an orchestrator's `httpGet` probe — reads the status code and ignores the body
entirely. A probe response is not an API response; if a dashboard ever needs the phase structurally,
that is an authenticated diagnostic endpoint and not this one.

The check is registered through `IConfigureOptions<HealthCheckServiceOptions>` with
`TryAddEnumerable`, not `AddCheck`: `AddCheck` is additive, a host calling `AddAlvo` twice would
register two checks named `alvo-schema`, and `DefaultHealthCheckService` refuses to be *constructed*
on a duplicate name — both probes would then answer 500, which an orchestrator cannot tell from "not
ready" (design deviation 63). Neither response is cacheable, and that costs no configuration:
`AllowCachingResponses` already defaults to `false`, which is what sends
`Cache-Control: no-store, no-cache` on both.

**`/health/ready` now answers the database half of §2.12 (#133).** Two checks contribute, under two
names. `alvo-schema` reports what the boot decided — "the descriptor applied and the policy catalog
is primed" — and `alvo-database` reports whether the store can *still* be reached, which is the
**continuing** answer neither route had: the boot ran once, before the server bound, so a database
that went away afterwards was invisible to both.

The core opens no connection. `IAlvoDataReachability` is a port in `MMLib.Alvo.Abstractions`,
answering `AlvoReachability` — reachable, or not plus the reason — and it is implemented **once**, at
the shared EF seam, over the same `RelationalConnectionFactory` every other store here uses plus one
dialect-owned statement (`IAlvoSqlDialect.ReachabilityProbeStatement`, `SELECT 1` by default). So
every EF-backed driver inherits a correct probe, §0 principle 2 holds, and per-engine SQL stays a port
member rather than an `if` in the shared path. A fresh connection per probe, deliberately: a pool
hands back a connection it believes is live, and only a round trip distinguishes "the pool has an
entry" from "the database is answering".

Four decisions inside that are worth stating:

- **Unreachable is an answer, not an exception**, and the reason travels to the log at `Error` while
  the probe still reads the boot phase and nothing else. A driver's message for an unreachable store
  carries a connection string, and this route is unauthenticated by construction (design
  deviation 59, unchanged).
- **The bound is `HealthCheckRegistration.Timeout`, two seconds** — carried by the registration
  rather than by the check, so the framework's own linked cancellation source enforces it. It is a
  *cooperative* bound: a probe that honours its token becomes a 503, one that ignores it holds the
  request. Honouring it is the port's documented obligation and the reachability contract suite
  asserts it; the backstop for a probe that breaks it is the orchestrator's own probe timeout.
- **A driver with nothing cheap to ask opts out by not registering the port**, and readiness is then
  exactly what it was before. Fail-open on purpose: readiness is an availability gate, not an
  authorization one, and a third-party driver shipping without a probe must not make every pod
  permanently unready. Both in-repo drivers register one.
- **`/health/live` is untouched** and still evaluates no check at all. A database outage must drain
  the pod's traffic, never restart-loop the container.

Deviation 38 is **superseded in its liveness-only part** and preserved in its guarantee: a boot that
refuses never binds the socket, so nothing ever answers healthy with no schema. **Cache and
message-bus reachability remain owed** — neither subsystem exists, and the readiness tag is what
makes each additive when it lands.

## A 500 is Alvo's own refusal here (#119)

The host calls `AddAlvoProblemDetails()` and `UseExceptionHandler()`, so an unhandled failure **on one of
Alvo's generated routes** is logged with its stack trace and answered with
`type: https://alvo.dev/errors/internal` and a **constant** detail. Nothing about the exception reaches the
caller. Embedded hosts register neither and keep answering their own way, which is why the registration is
opt-in rather than part of `AddAlvo`.

The scope matters here too, even though this host is almost all Alvo: a failure on either probe, on the
docs routes, or before routing matched anything is declined by the handler and rendered by the framework's
own problem-details writer. And a request this host's web server would not read — a body over Kestrel's
`MaxRequestBodySize`, an upload the client truncated — is answered at *that* status under
`https://alvo.dev/errors/unreadable-request`, not as a 500.

`UseExceptionHandler` is the **first** middleware in `Compose`, before `MapAlvo` and therefore before both the
probes and the Data API: a
middleware only sees what runs after it, and a failure that got past that line would be rendered by the
framework with an RFC 9110 status-code URI in `type`.

The handler itself lives in the core, not here — `ProblemResultFactory` is `internal`, so a Host-side one
would be a second hand-written copy of the problem-document shape. Recorded as **deviation 36**;
`data-api.md` carries the mode-by-mode table.

## The image

`src/MMLib.Alvo.Host/Dockerfile` builds from the **repository root**, not from its own directory: Central
Package Management, `Directory.Build.props` and `.editorconfig` all live there, and a project compiled without
them is a different build from the one CI gates. It therefore copies the four root build files, `.editorconfig`,
`schema/` and `src/`, and nothing else. `schema/` is not documentation in that list — `MMLib.Alvo` compiles
`project.schema.json` as an `AdditionalFile` and generates the descriptor validator's types from it, so a
context without it fails to compile. `test/` is never copied, because restoring the Host project alone pulls
only its own graph.

The image runs as the non-root `$APP_UID` the .NET base images define, owns `/alvo` so the mounted descriptor
and the SQLite default path are readable and writable, exposes **8080** and ends at `dotnet
MMLib.Alvo.Host.dll`. Alpine costs nothing here because `InvariantGlobalization` is already on — and it is
on because **this csproj turns it on**; nothing else in the repo sets it. So the standalone image runs
**without ICU**: culture-sensitive comparison and formatting fall back to the invariant culture. That is
the right default for an API surface that speaks JSON and ordinals, but it is a property of the image a
reader should not have to infer from a build flag.

**The image builds under the repository's own bar, and with no warnings at all.** `TreatWarningsAsErrors` is
inherited unchanged — nothing in the Dockerfile relaxes it. Three things the build would otherwise go looking
for outside the context are *told* rather than discovered, as environment properties so they reach restore and
every referenced project:

| Property | Why |
| --- | --- |
| `MinVerVersionOverride=$VERSION` (`ARG VERSION=0.0.0-docker`) | The context carries no `.git`, so MinVer has no tags to read. |
| `EnableSourceControlManagerQueries=false` | Same reason: `Microsoft.Build.Tasks.Git` cannot locate a repository. |
| `EnableSourceLink=false` | Source-link metadata serves symbol servers for published packages; this image publishes none. |

Measured rather than assumed. Without them the build **succeeds** but emits 17 warnings (5× `MINVER1001`, 6×
`Microsoft.Build.Tasks.Git`, 6× `Microsoft.SourceLink.Common`): none is promoted by `TreatWarningsAsErrors`,
because that property governs the C# compiler and these are MSBuild *task* warnings. `-p:MinVerSkip=true` also
builds clean and warns about nothing — but it leaves every assembly stamped `1.0.0`, a version this pre-1.0
project has never released, so the version is stated instead of skipped. The `ARG` is how F4's publish
pipeline hands in the real MinVer version once #24 ships an image.

## The compose stack

`docker-compose.yml` runs the host against `postgres:16-alpine`, with
`examples/vehicle-registry/vehicles.alvo.json` mounted at `/alvo/descriptor.json:ro` and port 8080 published.
The image ships **no** credential: `ALVO_DEMO_KEY_SECRET` is required with compose's `:?` form, so the stack
refuses to start rather than inventing one. `docker compose up --wait --wait-timeout 60` is the acceptance form
of §2.14's "working backend within 60 s", and it means something because the stack's `healthcheck` probes
**`/health/ready`**: healthy is the boot's own "descriptor applied, catalog primed" signal rather than "a
process is listening". Both stacks probe readiness for that reason. Liveness would also be *true* only after
the boot today — the boot runs before the socket binds — but it is documented as unconditional, so building a
deployment gate on it would be building on a coincidence.

The cost of `:?` is that **every** compose command interpolates the file, `down` included: tearing the stack
down in a shell that has forgotten the variable fails the same way starting it does. Keep it exported, or put
it in a root `.env` (already git-ignored) — the point of the `:?` is that the secret is somewhere an operator
chose, never inside the image.

MinIO and MailHog are absent on purpose: object storage and email do not exist in F3, and a service nothing
talks to is a stack that proves less, not more. The published image, the dashboard, the Management API and the
CLI are #24's remainder in F4. The stack is also deliberately **path-base-free** — Scalar's behaviour behind a
path base is unverified (see *Docs* above), and a demo that quietly exercised it would be advertising something
nobody measured.

### What "a working backend from the descriptor alone" is checked as

A container that starts is not the claim, and neither is a health check that answers. Five checks make it one,
each of which has been observed to fail under a mutation of the stack:

| Check | Mutation that breaks it |
| --- | --- |
| `POST /api/owners` → 201 with `Location: /api/owners/<guid>`, and following it → 200 | Mount `simple-tasks` instead: `owners` 404s. |
| `/api/vehicles` and `/api/inspections` → 200 | Mount `simple-tasks` instead: both 404. |
| `/api/warehouses` → 404 | Only the Host test project's descriptor declares it; a host with a baked-in schema, or a catch-all route, answers otherwise. |
| `select count(*) from owners where name = 'TeaPie Ltd'` in PostgreSQL is **exactly 1** | Set `Alvo__Database__Provider: sqlite`: the first three checks still pass, and this one reports `relation "owners" does not exist`. |
| `docker compose config` fails without `ALVO_DEMO_KEY_SECRET` | Put a literal secret in the file: it exits 0. |

Removing the volume mount altogether is the sixth, and it is the one that proves the descriptor is load-bearing
rather than decorative: the container never reports healthy and `docker compose up --wait` exits non-zero. The
contract was always right; what an operator saw was not — an unhandled `FileNotFoundException` and a 139 exit.
**#132 is closed**: the refusal now names the path and the two fixes and the process exits **78**
(`EX_CONFIG`) — see *How the process ends*.

One thing the mutations turned up that is not about the stack at all: mounting `simple-tasks` answers **401**
on `/api/tasks` with the same dev key that works against `vehicle-registry`. The key names `admin` and
`inspector`, that descriptor declares no `auth.roles`, and `ApiKeyContextResolver` fails the **whole**
credential as soon as one role name is undeclared — with no diagnostic anywhere. Filed as **#131**; it is a
framework DX gap the compose stack merely happened to expose.

These are now run unattended rather than by hand — see *The e2e, and which ring it is in*.

## The e2e, and which ring it is in

**None.** `scripts/test-e2e` tears any previous stack down, builds the image, brings the compose stack up
with a 60-second budget, runs `teapie test test/teapie -e compose`, asserts the row TeaPie created is in
PostgreSQL, writes a JUnit report to `artifacts/teapie/report.xml`, dumps container logs on failure and always
tears down. CI runs it as the `e2e` job, which the `Build & test` aggregate depends on — so it is a required
check without touching the branch ruleset.

It is deliberately outside every ring: ring0 must stay Docker-free (its own comment says so), ring2's Docker
use is one self-skipping Testcontainers image rather than an image build plus a multi-service stack, and the F3
design's testing table already places the full e2e at "CI on the PR, never locally". A human runs
`scripts/test-e2e` on purpose; nothing runs it by accident.

The credential is generated per run (`openssl rand -hex 16`) into `ALVO_DEMO_KEY_SECRET` and into the
generated `artifacts/teapie/env.json`, so the stack's secret and TeaPie's have one source and neither is
committed. `test/teapie/env.example.json` is the secret-free shape a human copies to run the suite by hand.

### What the suite proves, and what it cannot

Liveness alone would pass against any container, so it is not the gate. Three facts are:

| Fact | Where | Mutation it fails under |
| --- | --- | --- |
| A real row created over the published port and re-read through the `Location` the server advertised | `002-Owners` | Mount `simple-tasks`: `/api/owners` 404s and the suite fails at the create. |
| `/api/warehouses` 404s, in the API **and** absent from the document's `paths` | `003-Descriptor/001`, `004-Docs/001` | Point the request at `/api/owners` instead: 200, and the suite fails. |
| Exactly one `owners` row named `TeaPie Ltd` in **PostgreSQL** | `scripts/test-e2e` | Set `Alvo__Database__Provider: sqlite`: **every TeaPie test still passes** and only this fails. |

`001-Health` carries both probes now: `001-liveness` asserts 200 and claims nothing more, and
`002-readiness` asserts `/health/ready` is 200 **and** that the body is the phase — `Ready`, with no
connection-string fragment in it. Readiness is also what the stack's `healthcheck` waits on, so `up --wait`
returning at all is itself the deployment-level form of the same assertion.

**The e2e is the only gate that builds the image, and it is the only one that would have caught the defect it
did.** The `sdk:10.0-alpine` tag resolves to a *newer* SDK than `global.json` pins locally (10.0.302 against
10.0.100), and the newer analyzer set refused `AlvoBootService`'s boot log line under `CA1873` — an argument
evaluated whether or not the level is enabled. Every ring was green; the image would not compile, so no stack
could start at all. The lesson is recorded rather than only fixed: a Release publish under a rolled-forward SDK
is a different build from `dotnet build`, and this script is where the difference surfaces.

The third one is why it is a shell assertion rather than a TeaPie test: PostgreSQL publishes no host port, so
it is `docker compose exec postgres psql`, not HTTP. **A suite without it is decorative** — the SQLite mutation
is invisible to every HTTP check, and "the app answered" is not "the app used the database compose gave it".
It asserts *exactly* one, not at least one, because the script starts from a torn-down stack and the suite
performs exactly one successful create: any other count means the row is missing or the database is not the one
this run started.

`005-Auth` asserts the deployment form of default-deny that `AlvoHostBootTests` asserts in-process: an
anonymous **write** is 403 with Alvo's own problem document, naming neither the entity nor a row. It has to be
a write. An anonymous *read* is `200 {"items":[]}` — a configured rule is a row-level filter, not an
operation-level gate (`data-api.md`, *The RLS surprise*) — and `005-Auth/002` pins that too, so nobody later
"fixes" the 200 into a 403.

One honest limit: TeaPie halts the run when a request needs a variable an earlier failing test never set, so
the descriptor-swap mutation reports four failures and stops rather than reporting every fact that would have
failed. The gate is still correct (non-zero either way); the report is just less complete on a red run.

The suite lives at `test/teapie`, not the TeaPie skill's default `tests/teapie`: `CLAUDE.md`'s repo map
documents `test/` as this repository's test root, the skill permits a custom path, and `test/` beside `tests/`
is a navigation hazard for exactly the agent-first reader this project optimises for.

## What is left of #24

PR4 starts `[20] Standalone run (Docker) + embedded run`; F4 finishes it. Still owed:

- the **published multi-arch image** (`mmlib/alvo`, amd64 + arm64) and the release pipeline that pushes it —
  the Dockerfile's `ARG VERSION` is where that pipeline hands in the real MinVer version;
- the **dashboard** and the **Management API**, and with them the dashboard-first source of truth;
- the **`alvo` CLI** (`alvo apply vehicles.alvo.json`) — one of the descriptor's doors that PR4 does not open
  (`PLAN.md` §2: Docker mount = CLI apply = Management API = `FromDescriptor()` = admin UI export);
- the rest of **§2.12** — OpenTelemetry, rate limiting (**#112**), usage metering. The **database half of
  readiness** landed with `IAlvoDataReachability` and the `alvo-database` check (#133); **cache and
  message-bus reachability** are still owed, and each brings its own probe when its subsystem lands;
- the **full compose stack** (MinIO, MailHog) once storage and email exist;
- an operator-facing **`ALVO_*` environment vocabulary**, if the CLI work shows it earns its keep — and it
  has to be settled **before the image is published**, because after that the env names are a breaking
  change (deviation 39). `Alvo__Schema__Startup` and `Alvo__Schema__AllowDestructive` join that set;
- the **upgrade/downgrade contract between the NuGet version and the system-schema version** (A:555). Stage 1
  creates the current `alvo.*` tables idempotently and carries no version contract, so a container rolled back
  to an older image against a newer system schema is undefined. Recorded as design deviation 55, deferred here
  deliberately — it has no task in the startup-lifecycle plan.

Not #24's, but on the same deployment path: **#134** (Scalar behind a path base) still has to be answered
before "run it behind your ingress" is a claim this project can make. **#130** is closed — the document names
the origin its path keys are resolved against, path base and trusted forwarded prefix included, and two facts
pin it.
