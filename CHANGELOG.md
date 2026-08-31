# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed (breaking)

- **`MapAlvoDataApi()` now returns `IEndpointConventionBuilder` instead of `IEndpointRouteBuilder`**
  (#182), so a host can attach `RequireRateLimiting`, an authorization policy, output caching or a
  telemetry tag to Alvo's generated routes and to nothing else — which is what every other ASP.NET
  Core `Map*` over a *set* of endpoints returns. A caller that discarded the result (every in-repo
  one, and the shape the docs show) is unaffected; one that chained a second `Map*` off it, or stored
  it in an `IEndpointRouteBuilder`, is a source and binary break. Two things about the seam are
  contract rather than implementation: conventions must be attached **before** the first request
  materialises the route table, and one attached after **throws** — a deliberate deviation from the
  framework, which ignores late conventions, because Alvo's table is frozen once built and a silently
  dropped `RequireRateLimiting` is a rate limiter a host believes it has. `MapAlvo()` still returns
  the route builder and `MapAlvoHealth()` is deliberately not chainable: one builder over the probes
  *and* the Data API would let an authorization policy reach `/health/live`, which is a container
  restart-looped by its own liveness gate.

- **`AlvoQuery.EnsureSortKeysCanBePaged` is removed** (#116). It refused a paged read sorted by a
  nullable field, because a keyset boundary could not express where nulls sort. That boundary now
  can, so the guard has nothing left to refuse — and keeping it as a no-op would leave a member every
  `IAlvoData` implementation goes on calling forever. Delete the call; nothing replaces it. The
  API-layer refusal it produced, the `unpageable-sort-key` violation code, is gone with it: a request
  that used to earn it is now answered.

- **The list response envelope gained a third member, `count`** (#110). It is always present and is
  `null` unless the request sent a recognised `Prefer: count` preference, exactly as `next` is always
  present and null
  on the last page — the envelope's members are a statement about the bytes. A client that rejects
  unknown members, or that pins the published schema's `required` list, sees the change.

- **A dev API key's `Secret` must now be at least 32 characters** (#125). `AlvoAuthOptionsValidator`
  required only that it be non-empty, so `Secret = "password"` was accepted — and `ApiKeyHash` is a
  single unsalted SHA-256 pass, which is only as strong as the assumption that the secret is random.
  A host configured with a short dev secret now fails at startup, naming the key and both lengths,
  rather than starting silently weak. Generate the secret rather than choosing it —
  `openssl rand -hex 16`, the recipe this repository already publishes in `scripts/test-e2e`,
  `playground/run` and the examples' READMEs, is 128 bits written as exactly 32 characters, which
  is why the floor is set *at* that length rather than above it. Length is a proxy for entropy and
  not a measure of it — which is why this remains a **dev** mechanism, and why the real issuance
  path (#36) must not inherit this hash.

- **An entity may no longer be named after one of Alvo's own tables** (#156). `alvo_descriptor_versions`,
  `alvo_idempotency` and `alvo_outbox` — or the same three under a non-default `AlvoOptions.SchemaPrefix`
  — were excluded from introspection but not *reserved*, so a descriptor could declare an entity that
  mapped straight onto one and the framework and the entity would share a table. Such a descriptor is
  now refused at apply, with the entity's JSON pointer and a fix. The names come from one internal
  authority both the provider and the core read; no public surface changed.

- **A field declared both `required` and unconditionally `readOnly` is now refused at apply** (#124).
  The combination made every create of its entity unsatisfiable — supplying the field was refused as
  read-only, omitting it as missing — while the published OpenAPI document described a create the API
  would not accept. An expression-valued `readOnly` is unaffected: it is legal for one role and
  impossible for another, and the request-time half below answers that caller.

- **Alvo applies the descriptor on boot by default, and the host no longer applies anything itself.**
  The boot sequence runs as part of the host lifecycle, before the server binds: it loads and
  validates the descriptor, brings the schema up as far as the startup mode allows, primes the policy
  catalog and publishes a boot state a readiness probe reads. Three consequences a consumer can see:
  - **`Alvo:Schema:Startup` (`Alvo__Schema__Startup`) defaults to `Apply`.** On *drift* — a descriptor
    that no longer matches the schema recorded for the database — the boot applies the difference
    instead of refusing. Initialization of a database Alvo has recorded nothing for was never
    governed by the mode and still is not, in every mode but `Skip`. The destructive gate is separate
    and always on, so no mode drops or narrows anything without
    `Alvo__Schema__AllowDestructive=true`. **The cost is real and is the reason a production
    deployment sets `Verify`:** every replica of a rolling deploy attempts the DDL, the application
    needs DDL rights against its own database, and — the sharp one — an additive deploy under `Apply`
    makes the *rollback* destructive, so redeploying the previous descriptor refuses and every pod
    crash-loops until someone sets `AllowDestructive` or applies the older descriptor from a
    migration job. `Skip` refuses to start in exactly one state: Alvo has recorded nothing **and** the
    live schema does not match, i.e. nothing has verified the schema exists.
    `docs/architecture/host.md` documents the posture.
  - **`AlvoHost.BuildAsync` no longer takes a `CancellationToken`.** There is nothing left in it to
    cancel — the apply it used to perform is the host lifecycle's now, cancelled by the token
    `StartAsync` already carries. A caller passing one no longer compiles; dropping the argument is
    the whole migration.
  - **`IRuntimeSchemaWriter` is now mandatory for a database provider.** It used to be resolved on
    demand by the runtime apply path only, so a provider could ship `IAppliedSchemaStore` plus
    `ISchemaMigrator` and boot without it. The boot writes every project-schema change through it,
    because that port inserts the version row *first* as the optimistic-lock gate and runs the DDL in
    the same transaction — which is what makes several replicas cold-starting against one empty
    database converge instead of crash-looping. Both in-repo drivers implement it; a third-party
    provider that does not can no longer boot. `docs/architecture/package-boundary.md` records the
    widened contract, including that an `IAppliedSchemaStore` must bring its own storage up
    idempotently and race-safely on first call.

- **A `unique` field on a `tenancy: "scoped"` entity is now unique *within* the tenant, not across the
  instance** (#137). It was a **cross-tenant existence oracle**: `DescriptorModelBuilder` emitted
  `HasIndex(field).IsUnique()` with no `tenant_id` — and the same for a declared `unique` index — so
  tenant B's create of a value only tenant A held was refused while the same create of a free value
  succeeded. The two requests differ in exactly one thing, so the difference between the answers *was*
  the disclosure: B learned whether A held the value, one request per candidate. That is the inference
  the 404-everywhere rule exists to prevent, and it contradicted §0's secure-by-default. A scoped
  entity's unique index now spans `(tenant_id, …)`; a non-scoped entity keeps instance-wide uniqueness,
  a non-unique index is unchanged, and a descriptor that already named `tenant_id` keeps its own column
  order. **Mapping the underlying refusal to a clean 409 did not fix this** — `409`-versus-`201` is the
  same one-bit signal as `500`-versus-`201` was — which is why the two were separate issues.

  **This changes emitted DDL.** `IX_<table>_<field>` becomes `IX_<table>_tenant_id_<field>` on a scoped
  entity, so the next apply drops one index and creates another (`DropIndex`/`AddIndex`, neither
  destructive, so no `AllowDestructive` is needed). The change is always in the *widening* direction —
  the new index forbids strictly less than the old one — so no existing row can violate it and no
  migration can fail on data. Nothing is released, which is why this was the cheap moment.

- **`IAlvoSqlDialect` gained an abstract member, `DecodeConstraintViolation`.** A driver outside this
  repo will no longer compile until it implements it. Abstract rather than a default interface member on
  purpose: `null` means "not a constraint violation", which is a legitimate answer for every other
  failure, so an inherited default would have a new driver silently answer `500` for every duplicate —
  indistinguishable from correct behaviour on an engine that really reported something else.


- **A descriptor may no longer name a field `order`, `limit`, `offset`, `after`, `select`,
  `or`, `and` or `not`.** The generated Data API's query string reserves each of these
  (`?limit=10`, `?or=(...)`, `?not.color=eq.red`), so a request could not tell a filter on
  such a field from the parameter itself. The descriptor is now **rejected when it is
  applied**, with an error naming the entity, the field, the full reserved list and
  `Rename the field`; previously such a descriptor applied and then failed when routes were
  mapped — or, for an embedded host that never maps the Data API, was never refused at all.
  `order` in particular is a plausible business field name (an `orders` entity with an
  `order` column is not exotic), so this will hit real descriptors. Rename the field; there
  is no opt-out, because the ambiguity has no correct per-request resolution.
  `schema/project.schema.json` documents the exclusion on the `fields` description — the
  JSON Schema pattern cannot express it, so it is stated there rather than validated.

- **A descriptor is now rejected at apply when it declares a feature this build does not honour**,
  rather than applying and silently dropping it. The rule: refuse what silently produces wrong data;
  tolerate what an author can observe the absence of. Each refusal names the entity, the field, the
  consequence and a fix.
  - `field.computed` — the expression is never evaluated, so the column stays null.
  - `field.rollup` — nothing maintains the aggregate, so it reads as permanently null *while looking
    like data*.
  - `field.validation` — the expression is not evaluated, so a value it forbids is accepted and the
    field is not constrained at all.
  - `field.default` — no column default is emitted and the value is dropped before any writer sees it,
    so the field is simply null. On a `required` field that is an INSERT of NULL into a NOT NULL
    column. This one has an immediate ergonomic cost and is the first thing to restore (#113).
  - `entity.softDelete` — a delete would remove the row outright and reads would not exclude it:
    irrecoverable data loss where the schema promises recoverability.
  - Each of the six `entity.hooks.*` points, refused **individually** so that implementing one lifts
    only its own refusal (#114) — a `before*` hook may reject or mutate inside the write transaction,
    so a write the author believes is vetted is neither; an `after*` effect simply never happens.

  Blocks that are **warned about instead of refused**, because their absence is observable:
  `dynamicEntities`, `automation`, `templates`, `webhooks`, `functions`. Applying a descriptor that
  declares any of them logs one warning naming each.

- **A descriptor may no longer declare a field named after a framework-managed column** — `id`,
  `tenant_id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `deleted_at` — on an entity
  whose traits carry it. The refusal is trait-scoped, so an entity that does not declare `audit` may
  still have its own `created_at`. Previously a declaration won, and two defects came out of that:
  an audited entity declaring `updated_at` as `{"type":"string"}` applied cleanly and then **failed
  every create** with an internal parameter name in the response body; and one declaring `updated_at`
  as `hidden` applied cleanly and **switched optimistic concurrency off in silence**, because the mask
  drops the key from every returned record so no `ETag` is ever minted. This breaks a descriptor that
  declares `updated_at`, and it also removes one capability: **`readOnly` on `tenant_id` as a
  narrowing is now forbidden** along with the declaration. Express that intent as a `create` rule
  instead — the synthesized tenant scope's `WITH CHECK` is already evaluated over the candidate row,
  so a rule can answer "which tenant may this row be placed in" per caller and a field flag cannot.

- **`MMLib.Alvo` is now an ASP.NET Core library.** §0 principle 8 makes every generated endpoint a
  minimal-API delegate, so the core carries `FrameworkReference Microsoft.AspNetCore.App` plus
  `Microsoft.AspNetCore.OpenApi`. **An embedded consumer of the core is therefore an ASP.NET consumer
  whether or not it maps the Data API** — that is the most consumer-visible change in this release for an
  embedded host. `MMLib.Alvo.Abstractions` deliberately stays free of both, and an architecture test holds
  that line, so the ports remain implementable by a host that is not an ASP.NET application at all.
  Side effect: the framework reference supplies `Microsoft.Extensions.Options`, whose explicit
  `PackageReference`s had to be removed, because NuGet's `NU1510` (an error in this repo) objects to a
  reference it will not prune.

- **`AddAlvo()` now calls `AddLogging()`.** The core writes at least one warning of its own, so it resolves
  `ILogger<T>` and must not require the host to have arranged that. It is idempotent (`TryAdd` throughout),
  so an ASP.NET host or one that already called it is unaffected; a plain console host embedding Alvo would
  otherwise fail to activate the migration runner at all. Note that with **no logging provider configured**
  the warning is dropped silently — a startup crash traded for a silent drop.

### Changed

- **A create whose caller cannot satisfy it now answers `read-only-required-field`, not `required`**
  (#124). When a field is `required` and this caller's own expression-valued `readOnly` mask froze it,
  telling them to supply it sends them to fix something no value of theirs can be stored in. The new
  violation says the create is impossible for these roles and names the two ways out. A caller who
  *writes* the frozen field still gets `read-only-field` — the new code narrows the missing-value case
  only.

- **`maxLength` is counted in Unicode code points, not UTF-16 code units** (#123). Ten astral-plane
  characters are twenty UTF-16 units, so a value well inside a `varchar(10)` was refused with a 422
  telling the caller to shorten something already short enough. Code points is the unit PostgreSQL's
  `varchar(n)` and JSON Schema's own `maxLength` keyword both use, so the validator, the column and the
  published document now bound the same thing on both shipped drivers. Grapheme clusters were rejected
  as the unit: they count *fewer* than the column does, which would have admitted values the engine
  refuses. The agreement is a two-engine guarantee and is recorded as one — T-SQL's `nvarchar(n)`
  bounds UTF-16 units, so a SQL Server dialect owes its own answer before it can honour this (#175).

- **A format check that times out is now its own violation code, `format-not-evaluated`, and no
  longer reported as `format`.** A client branching on the `format` code will no longer see the
  pattern-timeout case. This is a fix for a fail-*wrong*, not a cosmetic split: the old behaviour told
  a caller their value did not match a pattern that had in fact never finished being evaluated, and it
  was reachable on perfectly valid input — a valid `email` address was refused as malformed once in
  nine full suite runs, purely because a loaded machine lost the match timeout to scheduling. "I could
  not decide" and "your value is wrong" are different things to tell a caller, and only one of them is
  about the value. Both still refuse the request, because an unevaluable check must fail closed; the
  difference is that the new code's fix suggestion is **retry the request**, which is the one action
  that can succeed when nothing about the value was wrong.

- **A 201's `Location` header now honours `HttpRequest.PathBase`** (#121). A host mounted under a
  path base — `UsePathBase("/alvo")`, or a reverse proxy sending `X-Forwarded-Prefix` — used to
  advertise `/api/owners/<id>`, which 404s at the proxy edge; it now advertises
  `/alvo/api/owners/<id>`. This is a behaviour change for anyone already deploying under a path
  base, and the direction is that URLs which used to be wrong are now right: following the header
  works where it previously did not. No released version is affected — both the header and the fix
  land in this same unreleased cycle. A host with no path base is unaffected, byte for byte. The
  **OpenAPI document** names the origin its paths are resolved against, path base included — see
  #130 under *Fixed* — while the Scalar UI's own behaviour there is still unmeasured (#134).

### Added

- **`/health/ready` now reports whether the database can *still* be reached** (#133), so a store that
  goes away after boot drains the pod's traffic instead of being invisible. **This changes what an
  orchestrator does with a running host:** readiness answered 200 for the life of the process once
  the boot had primed the schema, and it can now answer 503 while the process keeps running and
  `/health/live` keeps answering 200 — which is the point, and which a deployment whose readiness
  probe gates traffic will notice. Liveness is unchanged and still evaluates no check at all.
  - The core opens no connection of its own: **`IAlvoDataReachability`** is a new port in
    `MMLib.Alvo.Abstractions`, answering **`AlvoReachability`** — reachable, or not plus the reason,
    which goes to the log at `Error` and never onto the anonymous probe's body. Unreachable is a
    return value rather than an exception, and a cancelled probe throws; both are asserted by
    `MMLib.Alvo.Testing.Data.AlvoDataReachabilityContractTests`, which every implementation inherits.
  - Both shipped drivers get one implementation, registered by `AddRelationalProvider`, over a fresh
    connection per probe and one dialect-owned statement: **`IAlvoSqlDialect.ReachabilityProbeStatement`**
    is a new default interface member answering `SELECT 1`, so an out-of-repo dialect for an engine
    that spells it differently (Oracle's `SELECT 1 FROM DUAL`) overrides it and every other one is
    unaffected.
  - A driver with nothing cheap to ask **opts out by not registering the port**, and readiness is then
    exactly what it was before. That is fail-open on purpose: readiness is an availability gate, not
    an authorization one.
  - The probe is bounded by `HealthCheckRegistration.Timeout` (two seconds). It is a *cooperative*
    bound — the framework cancels the token and awaits the check — so a probe that honours its token
    becomes a 503 and one that ignores it holds the request; honouring it is the port's documented
    obligation.
  - **It costs a database round trip per request, on a route that carries no credential.** Readiness was
    a pure in-memory read before; a caller who can reach the port now makes the process spend a connection
    from the pool the Data API shares, at their chosen rate, and a saturated pool times the probe out and
    has the pod drained. The assumed caller is a private orchestrator polling at an interval — which is
    what every readiness probe assumes and nothing here enforces. Bounded, disposed per probe, and tracked
    as **#183**, where caching the answer for a short window is the likely resolution.
  - Cache and message-bus reachability remain owed; the readiness tag is what makes each additive.

- **`?order=<nullable field>` works, and `nullsfirst`/`nullslast` finally do something** (#116).
  Every list over HTTP is paged, and a paged read sorted by a nullable field used to be refused with
  422 — so sorting by a `display_name` that may be null was impossible, and half the published sort
  grammar could not be reached. The keyset boundary now compares the same *(where the null sorts,
  then the value)* pair the `ORDER BY` ranks by, so a nullable key pages like any other and a cursor
  walks the null-keyed rows too. `nullslast` is the default when a key does not say otherwise; where
  a null sorts is never left to the database, because SQLite and PostgreSQL disagree on it.
  **The cost is real and worth knowing:** the null placement is emitted as a `CASE` expression over
  the key, which an index on that key cannot serve, so page by a required column where latency
  matters. Per-dialect native `NULLS FIRST`/`NULLS LAST` is the follow-up (#178).

- **`Prefer: count=exact` fills the page envelope's `count`** (#110), with the number of rows the
  query matches in total — narrowed by your policy and your filter, and *not* by `limit`, `offset`
  or `after`, so it does not shrink as you page. Opt-in, because an exact count is a second scan of
  the matching set on every request; a request that sends no preference costs exactly what it did
  before. `count=planned` and `count=estimated` are accepted and **degrade to an exact count** — a
  planner estimate exists on one supported engine and not the other, and this API answers identically
  on both — and `Preference-Applied: count=exact` (RFC 7240 §3) tells the caller what was done. Per
  RFC 7240 a preference this server does not recognise is ignored rather than refused; its absence
  from `Preference-Applied` is how that is reported. *Exact* means "not an estimate", not
  "atomically consistent with `items`": the count is a second statement, so a write landing between
  the two can make the number differ by one.

- **A standalone host you can run without writing any code.** `docker compose up` brings up a
  working backend defined entirely by a JSON descriptor mounted at `/alvo/descriptor.json` — no
  project, no migrations, no scaffolding. What you get:
  - **The descriptor is the whole backend.** Entities, fields, validation and per-operation rules
    from the mounted file become tables and a REST API. Edit the file and restart, and an
    **additive** change (a new entity, a new field) migrates on the way up. A **destructive** one
    does not: a plan that would drop a column or a table is refused in every startup mode unless
    `Alvo__Schema__AllowDestructive=true` is set, so the container fails to start rather than losing
    data on a restart. Note what that costs on the way *back*: rolling the descriptor back after an
    additive change plans a drop, which is refused, so a rollback needs either that setting or a
    migration job — see `docs/architecture/host.md`. An entity the file does not declare 404s, which
    is the point: nothing is baked in.
  - **Interactive documentation at `/scalar`**, rendering the OpenAPI document the host serves at
    `/openapi/v1.json`. It works with **no outbound network access** — the assets ship inside the
    image. `Alvo__Docs__Enabled=false` removes both routes.
  - **Two probes, configured oppositely.** `/health/live` evaluates **no** health check at all, so
    nothing anyone registers can make it fail and get the container killed; it means only "the
    process is up". `/health/ready` is the schema signal: **503** until Alvo's boot has applied the
    descriptor and primed the policy catalog, 200 after, with the boot phase as the whole body and
    nothing else in it — the reason a boot refused can carry a path or a connection string, and a
    probe is unauthenticated by design. A host whose boot refuses never listens at all and exits
    non-zero. The stack's `healthcheck` and both compose files probe **readiness**. What is still
    missing is the *continuing* database-reachability half, which needs a port (#133).
  - **Configuration is standard .NET environment binding** — `Alvo__DescriptorPath`,
    `Alvo__Database__Provider` (`sqlite` | `postgresql`), `ConnectionStrings__Alvo`,
    `Alvo__PathBase`, `Alvo__Docs__Enabled`, `Alvo__Auth__DevKeys__0__*`, plus
    `Alvo__Schema__Startup` (`Verify` | `Apply` | `Skip`, default `Apply`) and
    `Alvo__Schema__AllowDestructive`. SQLite is the zero-configuration default; an unknown provider
    name is refused rather than defaulted, an unknown startup mode is refused naming all three, and
    a PostgreSQL host with no connection string fails rather than quietly writing to a
    container-local file. Every refusal names the environment spelling an operator can type and what
    to set.
  - **Behind a reverse proxy**, `Alvo__PathBase` and — opt-in, off by default —
    `Alvo__ForwardedHeaders__Enabled` for `X-Forwarded-*`. Off by default deliberately:
    `X-Forwarded-Prefix` decides the URL a 201 advertises, so an untrusted caller honoured by
    default would choose where the next client is sent.
  - **No default credential, ever.** The image ships no API key and seeds none; the demo stack
    refuses to start until you supply one. A host with no key configured still starts and still
    refuses every write. The stack publishes its port on **`127.0.0.1` only**, so following the
    quickstart on a cloud VM does not put a read/write backend on the internet — Docker's
    `DOCKER-USER` chain sits ahead of a host firewall, so a `0.0.0.0` bind here would not be
    stopped by one.
  - **An end-to-end suite** (`scripts/test-e2e`) that builds the image, brings the stack up
    against PostgreSQL, runs TeaPie against the published port and asserts the created row is in
    the database. It runs in CI on every pull request.

- **A runnable complex demo, and an end-to-end suite that measures what F3 claims.**
  `examples/field-service` is a multi-tenant field-service backend — global reference data beside
  two tenant-scoped entities, one audited and one not, an optional hidden field, a required hidden
  field, a `readOnly` field, an unconfigured operation, both a caller-level and a row-level rule,
  every field type, both kinds of `format`, and indexes over the fields the tests filter and order
  on. Its README states, per construct, which behaviour it exists to let a test measure.
  `docker-compose.field-service.yml` runs it on `:8081` with five dev keys differing only in role
  and tenant; the repo-root `docker compose up` is unchanged.

  `test/teapie-field-service` drives 327 assertions against that container: the PostgREST query
  grammar including keyset paging over four pages, RFC 9457 problem documents carrying every
  violation at once, `ETag`/`If-Match` in both directions of the `audit` pair, `Idempotency-Key`
  measured by row count, field confidentiality compared as a whole refusal document, all three
  authorization shapes in one system state, tenant isolation, the published OpenAPI document, and
  six multi-step CRUD journeys that end by asserting the state of the world. `scripts/test-e2e`
  runs both stacks and adds two PostgreSQL assertions no HTTP check can make — that a hidden
  field's value really is stored, and that the two tenants' rows really are partitioned.

  **Three defects it found are recorded rather than fixed here**, each pinned by a labelled case
  that turns red when the defect is. They are **two independent problems**, and conflating them
  would let a fix for one be mistaken for a fix for the other:

  - *A database constraint violation is answered `500`, not `409`.* Two reachable shapes — a
    duplicate value on a `unique` field, and a delete blocked by an `onDelete: restrict` reference.
    Every other declared facet is validated and answered with a per-field 422; a database constraint
    is not mapped onto `IAlvoData`'s refusal families at all, so an agent gets no violation, no
    pointer and no field name. Pinned by `030-Problems/002` and `100-Scenarios/001`.
  - *A `unique` field on a `tenancy: "scoped"` entity is unique across **all** tenants.* The driver
    emits `HasIndex(field).IsUnique()` with no `tenant_id`, so tenant B's create collides with a
    value only tenant A holds — a **cross-tenant existence oracle**, one request per candidate, and
    the one channel through which the isolation the rest of the framework enforces leaks.
    **Mapping the violation to a clean `409` does not close this**: `409`-versus-`201` is the same
    signal to tenant B as `500`-versus-`201`. The fix is a tenant-scoped unique index. Pinned
    separately by `080-Tenancy/002`, which asserts *distinguishability* rather than a status
    precisely so a status-only change cannot be mistaken for a fix.

  Known limits, so this is honest: the image is **not published yet** — you build it from this
  repository — and there is no dashboard, no Management API and no CLI (#24, all F4). A mis-typed
  descriptor mount currently ends in a stack trace rather than a readable refusal (#132), and the
  docs UI's behaviour behind a path base is unmeasured (#134). `docs/architecture/host.md` records
  what the host is and what it deliberately is not.

- **`MapAlvo()` and `MapAlvoHealth()`, plus a boot state to read** — new public API in the core.
  `MapAlvo()` maps everything Alvo serves (the Data API and both probes) in one call, and
  `MapAlvoHealth()` maps the probes alone. **Neither needs the schema to exist yet, and neither does
  `MapAlvoDataApi()` any more**: route literals are read when the endpoint table is first
  enumerated, on the first request, so the old ordering rule "apply before you map" is gone —
  `register → map → boot → listen` is the sequence, and the boot runs before the server binds.
  `AlvoBootState` (with `AlvoBootPhase`) is what the boot publishes for a readiness probe, a CLI or a
  dashboard to read: the phase, and the applied revision it primed from.

- **`IServiceProvider.ApplyAlvoDescriptorAsync()`** — new public API in the core. The one verb a
  host performs on a built container: bring the configured descriptor up, creating or migrating the
  schema it declares. It is no longer the *startup* path — Alvo's own boot does that before the
  server binds, and it is also what primes the policy catalog (an unprimed catalog denies
  everything) — so this is the explicit runtime apply: a CLI, a migration job, a dashboard. The
  ordering rule it used to carry ("call it before mapping endpoints") no longer applies.
  Previously the orchestrator behind it was `internal`, so only code inside the
  core assembly could apply a descriptor at all. A **refusal is a return value, not an exception** —
  a caller doing a dry run wants to read the plan — so a host that wants a running backend calls
  **`MigrationResult.EnsureApplied()`** on the result, also new. It throws only on a plan that was
  neither applied, empty, nor a dry run, naming the destructive steps that were refused; an
  unchanged descriptor (empty plan) and a dry run pass through untouched.

- **`AddAlvoProblemDetails()`** — new public API in the core, and **opt-in**: `AddAlvo()` does not
  register it, so nothing changes for an existing host. Registering it, together with
  `UseExceptionHandler()`, makes an unhandled exception **on one of Alvo's generated routes** come
  back as Alvo's own problem document (`type: https://alvo.dev/errors/internal`) with a constant
  detail and nothing about the exception in the body, logged with its stack trace server-side —
  except when the caller simply hung up (an `OperationCanceledException` on an aborted request),
  which is not an error and is not logged as one. It is opt-in because an embedded host owns
  its own error rendering, and Alvo silently swallowing your exceptions would be the wrong default.
  It also calls `AddProblemDetails()` for you, because `UseExceptionHandler()` refuses to configure
  itself with neither a handler path nor a problem-details fallback. The `internal` slug joins
  `AlvoProblemTypes.All` and the published `problemDetails` schema's `type` enum.

  **The handler declines what is not Alvo's**, which is what makes it safe to add to a host that
  renders its own errors: an `IExceptionHandler` you register *after* it still runs — for your own
  endpoints, and for anything that failed before routing matched. And a request your **web server**
  refused before Alvo could read it (a body over Kestrel's `MaxRequestBodySize`, an upload the
  client truncated, a body arriving too slowly) is answered at *that* status — 413, 400 or 408 —
  under the new `https://alvo.dev/errors/unreadable-request` slug, and logged at `Warning` without a
  stack trace, rather than coming back as a 500 that tells an agent to retry a request whose size is
  the thing that has to change.

- **The HTTP Data API.** A host that calls `MapAlvoDataApi()` gets a REST API generated from its
  descriptor: five routes per declared entity (`GET` collection, `GET {id}`, `POST`, `PATCH`,
  `DELETE {id}`) under a configurable prefix, each one a minimal-API delegate gated by the entity's
  own rules. What comes with them:
  - **A PostgREST-shaped query string**, adopted rather than invented so an agent recognises it:
    ten operators (`eq neq gt gte lt lte like ilike in is`), `or=(…)`/`and=(…)` grouping, a `not.`
    prefix, `order=field.desc.nullslast`, `select=a,b`, and both paging modes — keyset via an opaque
    `after` cursor, plus `offset` as the opt-in second mode. Page size is server-enforced.
  - **Structured refusals.** Every error is an RFC 9457 problem document with an Alvo `type` slug
    (`https://alvo.dev/errors/…`) and a `violations` array carrying a JSON pointer, a machine-readable
    code, a message and a fix suggestion for *every* problem with the request — not just the first.
  - **Optimistic concurrency, on an entity that keeps a row version.** A single-row read and a write
    return a strong `ETag` over that version — **only where the entity declares `audit: true`**, which
    is what mints the version column; an entity without it gets no `ETag`, and a *list* never carries
    one. `If-Match` on a `PATCH`/`DELETE` is evaluated inside the write transaction against a
    row-locked pre-image. A precondition this API cannot evaluate is refused rather than ignored,
    because ignoring one is the lost update the header exists to prevent — and on a version-less
    entity the generated document does not offer `If-Match` at all, rather than inviting a header
    whose every value would be 412.
  - **`Idempotency-Key` on create.** A retried create returns the first one's result and never
    duplicates a row. The record stores the created row's id — never a rendered response — so a
    replay re-reads through the caller's *current* policy and can never hand back a representation
    that policy would no longer produce.
  - **`Cache-Control: no-store`** on every generated response. These are private, per-caller
    representations; the `ETag` exists for concurrency, not for a shared cache.
  - **An OpenAPI 3.1 document** enriched from the applied schema — per-entity request and response
    schemas, the query parameters with their real enforced bounds, the problem shape, and an API-key
    security scheme. §0 principle 4: the document *is* the contract an agent reads.

  Known limits, so the list is honest: no bulk operations, no `PUT`/upsert, no relation embedding, no
  aggregations or total count, and `Idempotency-Key` is ignored on `PATCH`/`DELETE`. Each is filed
  with its reason. `docs/architecture/data-api.md` records the decisions and the surprises — in
  particular that a *configured* rule which excludes a caller answers **200 with an empty page, not
  403**, because a rule compiles to a row-level predicate.

- Repository and solution skeleton: `MMLib.Alvo.Abstractions` (the interface-first
  root of the dependency graph) and its test project.
- Central Package Management, shared build settings, pinned .NET SDK, `.slnx` solution.
- First architectural guard-rail (NetArchTest): Abstractions depends on no other
  project in the solution.
- Apache-2.0 license and minimal pull-request CI (build + test).
- Contributor onboarding: `CONTRIBUTING.md` (build/test, PR process, transparent CLA
  explanation), Individual and Corporate CLAs (`docs/legal/`) based on the Project Harmony
  v1.0 templates that keep contributor copyright while allowing future relicensing, and a
  Contributor Covenant `CODE_OF_CONDUCT.md`.
- Central package management finished: shared assembly/NuGet metadata (author, product,
  license, repo link, tags, icon, readme), warnings-as-errors, deterministic builds, and
  SourceLink in `Directory.Build.props`; root `README.md` and package icon (`icon.png`,
  generated from `assets/alvo-logo.svg`).
- Repo tooling: CodeQL analysis, `Dependabot` version updates (NuGet + GitHub Actions),
  a Dependency Review check on pull requests (fails on moderate+ severity or
  non-allow-listed licenses), and a CodeRabbit config (`.coderabbit.yaml`) tuned to this
  project's conventions (Central Package Management, disallowed packages, XML doc and
  comment-style rules).

### Fixed

- **The OpenAPI document's advertised origin carries the request's path base** (#130) — and it always
  did. `Microsoft.AspNetCore.OpenApi` builds `servers[0].url` from the request's scheme, host **and
  `PathBase`**, per request rather than once per document name, so a client resolving a path key
  against it reaches the endpoint under `app.UsePathBase("/alvo")` and behind a proxy that sets
  `X-Forwarded-Prefix` for a host told to trust it. What was broken was the record: the defect was
  documented as open in two architecture notes and in this changelog, and **nothing measured the
  path-base half of that value** — the scheme and host halves were pinned, so deleting `PathBase`
  from the framework's own server-URL construction would have left the whole suite green while every
  path in the document became wrong by the prefix. Two facts now pin it, one per package. No
  production code changed; a bump of `Microsoft.AspNetCore.OpenApi` is henceforth gated by them.

- **A 500 from the standalone host carries `alvo.dev/errors/internal`** (#119) — closed by
  verification rather than by a change. The slug, the opt-in `AddAlvoProblemDetails()` registration,
  the handler that logs the exception with its stack trace *and* renders Alvo's document, and the
  standalone-pipeline facts that hold all of it were delivered with the host itself. The one thing
  left behind was a stale sentence in `docs/architecture/data-api.md` claiming nine problem-type
  slugs over an eleven-row table; the prose now names `AlvoProblemTypes.All` instead of a number.

- **A database constraint violation is now `409`, naming the field, instead of `500 internal`** (#138).
  A value another record already holds on a `unique` field, and a delete an `onDelete: "restrict"`
  reference refuses, both reached the host as the provider's own exception and rendered as
  `alvo.dev/errors/internal` — *"an invariant Alvo itself relies on is broken"*, which neither is: the
  caller's request conflicts with stored state, which is what `409` means. Three costs, each its own
  defect: an agent could not repair the request (no pointer, no field, no fix suggestion, in a framework
  whose principle 4 is structured errors *with* one); a `500` invites a retry that can never succeed;
  and the operator was paged, with a stack trace, for an ordinary caller mistake.

  A new slug, `conflict`, is a second `409` beside `idempotency-conflict` by the same rule `out-of-scope`
  is a second `403` by — the two have different fixes. One slug covers both constraint kinds, with the
  difference in `violations`: code `unique` and pointer `/<field>` for a collision, code `referenced` and
  the empty pointer for a `restrict` refusal (a `DELETE` has no field to change). The refusal still
  discloses no value, no engine message and no constraint or index name, and the `restrict` case names no
  entity either — which of the entities that may reference a row actually holds one is a fact about data
  the caller may not be able to read. A conflict confined to framework-managed columns keeps propagating
  as the broken invariant it is.

  The engine-specific decoding lives behind the driver's own SQL seam, never in a `catch` that matches a
  message: PostgreSQL reads SQLSTATE `23505`/`23503` plus the constraint name, SQLite the extended result
  code `2067`/`1555`/`787` plus the columns its message names. Both are held to one inherited suite
  (#139), which caught two real engine differences the PostgreSQL-only e2e could not have: `ExecuteDelete`
  on SQLite loses the extended result code, and Alvo's *runtime* EF model declared no indexes at all, so
  the constraint-name resolution PostgreSQL depends on always came back empty.

- **A duplicate in an *idempotent* create no longer costs ten transactions.** The retry that converges a
  lost key race caught any storage write failure, so a duplicate was re-attempted ten times — about
  450 ms — before surfacing. It is no longer a `DbException`, so it leaves on the first attempt. The
  idempotency record's own primary key is deliberately still untranslated, because losing that race is
  what the retry exists for. (Part of #127; the rest of #127 is still open.)
