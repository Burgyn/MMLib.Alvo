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

**No default credential.** §2.14's acceptance criterion is that the image never ships a
preset login, so the host seeds no API key. A host with none configured still starts and
still refuses every operation, because an anonymous caller is judged by the same
default-deny policy as any other (deviation 23). Two facts hold that line: an anonymous
*write* is refused (a *read* would be an honest 200 with zero rows and would prove nothing),
and the host's own `appsettings.json` is asserted to declare no `Alvo:Auth` section — the
realistic way a preset login reaches an operator is a dev key added there for convenience,
which no runtime fact can tell apart from one the deployment configured.

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

**There is deliberately no explicit `UseRouting()`.** The widely cited rule — Microsoft Learn still states it —
that `WebApplication` needs `UseRouting` *after* `UsePathBase`, or routes match before the path is rewritten,
no longer holds: `UsePathBaseMiddleware` re-runs matching over the rewritten path itself. Measured under this
runtime, not assumed: a probe answers 200 for `UsePathBase` and 404 for the same rewrite performed by hand, and
removing the call from the pipeline leaves every path-base fact green. `UseForwardedHeaders` does not touch
`Path` at all, so it never raised the question.

## Docs

`AddOpenApi` is called by the **host**, never by the core (`ApiSetup.AddAlvoApi` says why: serving a document
is a hosting decision), and `Scalar.AspNetCore` renders it at `/scalar` from `/openapi/v1.json`. Two orderings
are load-bearing and opposite: the host's document transformer registers **before** `AddAlvo`, because
registration order is transformer order and Alvo appends to `info.description` rather than replacing it; the
docs **routes** map **after** `MapAlvoDataApi`, because the document is generated from the endpoints actually
mapped. `Alvo:Docs:Enabled=false` removes both routes — the UI *and* the document, because the switch is about
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
#130 (the document's `servers`, open), #134 (this).

## Health

Liveness only (`/health/live`). §2.12 asks for readiness with database, cache and message-bus
reachability; none of those probes exists as a port today, and inventing one is a port
widening PR4 has no mandate for. Recorded as deviation 38 and filed as **#133** rather than
approximated — the core may not touch a provider directly (§0 principle 2), so "can you reach the
database" is a port to design, not a health check to write.

What liveness already proves is more than its name suggests, and what it does not is the point of
#133: the descriptor applies *before* the server listens, so an answer here means the schema is up
and the database was reachable **at startup** — but a database that goes away afterwards is
invisible to it.

## A 500 is Alvo's own refusal here (#119)

The host calls `AddAlvoProblemDetails()` and `UseExceptionHandler()`, so an unhandled failure is logged with
its stack trace and answered with `type: https://alvo.dev/errors/internal` and a **constant** detail. Nothing
about the exception reaches the caller. Embedded hosts register neither and keep answering their own way,
which is why the registration is opt-in rather than part of `AddAlvo`.

`UseExceptionHandler` is the **first** middleware in `BuildAsync`, before liveness and before the Data API: a
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
MMLib.Alvo.Host.dll`. Alpine costs nothing here because `InvariantGlobalization` is already on.

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
of §2.14's "working backend within 60 s", and it means something because the host does not listen until the
descriptor has applied.

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
| `select count(*) from owners` in PostgreSQL ≥ 2 | Set `Alvo__Database__Provider: sqlite`: the first three checks still pass, and this one reports `relation "owners" does not exist`. |
| `docker compose config` fails without `ALVO_DEMO_KEY_SECRET` | Put a literal secret in the file: it exits 0. |

Removing the volume mount altogether is the sixth, and it is the one that proves the descriptor is load-bearing
rather than decorative: the host refuses to start with `Could not find file '/alvo/descriptor.json'`, the
container never reports healthy, and `docker compose up --wait` exits non-zero. The *contract* there is right
and stays; what an operator sees is not — an unhandled `FileNotFoundException` and a 139 exit, filed as
**#132** against #24, because a mis-typed mount is the likeliest way a first `docker run` goes wrong.

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
| Exactly one `owners` row named `TeaPie Ltd` in **PostgreSQL** | `scripts/test-e2e` | Set `Alvo__Database__Provider: sqlite`: **all 20 TeaPie tests still pass** and only this fails. |

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
- **readiness** with database / cache / message-bus reachability (§2.12, **#133**), and the rest of §2.12 —
  OpenTelemetry, rate limiting (**#112**), usage metering;
- the **full compose stack** (MinIO, MailHog) once storage and email exist;
- an operator-facing **`ALVO_*` environment vocabulary**, if the CLI work shows it earns its keep — and it
  has to be settled **before the image is published**, because after that the env names are a breaking
  change (deviation 39);
- a first-run experience worth the name: **#132** (a mis-typed descriptor mount) is the first thing an
  operator hits, and it is currently a stack trace.

Not #24's, but on the same deployment path: **#130** (the document's `servers`) and **#134** (Scalar behind a
path base) both have to be answered before "run it behind your ingress" is a claim this project can make.
