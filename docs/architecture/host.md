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
path-base-free, and the gap is filed as an issue rather than guessed at with a test that could not fail for
its stated reason.

## Health

Liveness only (`/health/live`). §2.12 asks for readiness with database, cache and message-bus
reachability; none of those probes exists as a port today, and inventing one is a port
widening PR4 has no mandate for. Recorded as a deviation with an issue rather than
approximated.

## A 500 is Alvo's own refusal here (#119)

The host calls `AddAlvoProblemDetails()` and `UseExceptionHandler()`, so an unhandled failure is logged with
its stack trace and answered with `type: https://alvo.dev/errors/internal` and a **constant** detail. Nothing
about the exception reaches the caller. Embedded hosts register neither and keep answering their own way,
which is why the registration is opt-in rather than part of `AddAlvo`.

`UseExceptionHandler` is the **first** middleware in `BuildAsync`, before liveness and before the Data API: a
middleware only sees what runs after it, and a failure that got past that line would be rendered by the
framework with an RFC 9110 status-code URI in `type`.

The handler itself lives in the core, not here — `ProblemResultFactory` is `internal`, so a Host-side one
would be a second hand-written copy of the problem-document shape. Recorded as deviation D1; `data-api.md`
carries the mode-by-mode table.

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
container never reports healthy, and `docker compose up --wait` exits non-zero.

Task 7 turns these into `teapie test` against the same stack, run by `scripts/test-e2e` in CI. They are
deliberately **not** in ring0–ring2: ring0 must stay Docker-free.
