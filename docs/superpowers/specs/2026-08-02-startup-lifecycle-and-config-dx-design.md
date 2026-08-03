# Startup lifecycle and configuration DX

> The design for how an Alvo host **boots** — what it registers, what runs when,
> what is allowed to touch the database, and how a failure surfaces — and for the
> **configuration surface** a host has to write to get there.
>
> Triggered by the maintainer's objection to `AlvoHost.BuildAsync`:
>
> ```csharp
> var migration = await app.Services.ApplyAlvoDescriptorAsync(ct: ct).ConfigureAwait(false);
> migration.EnsureApplied();
> ```
>
> They did not expect a **blocking apply in startup**; they expected Alvo to
> register what it needs and let a startup filter / hosted service drive it. They
> also find the configuration too complicated.

## Sources consulted

Read before designing, per `CLAUDE.md`. The line references are to the tree at
`018d47b`.

- `docs/product/baas-analyza.md` — §1 (provider config validated at startup,
  A:84/A:91), §2.1 (dynamic entities are DDL-free, A:166), §2.4, §2.12 (health,
  A:487), §2.13 (migrations, A:508–A:519), §2.14 (deployment, A:524–A:557), §4,
  §8.2, §9.2.
- `docs/product/alvo-specifikacia.md` — §0, S:51 (mounted descriptor boots a
  configured backend), S:85–S:95 (the two modes and the cross-mode contract),
  S:157 (the **binding** DX goals), S:164–S:169 (`AddAlvo()` / `app.MapAlvo()`),
  S:326, S:400–S:408 (Aspire).
- `docs/design-brief.en.md` — and, importantly, the five places it is **silent**
  where the full sources are not (see *What the brief omits*).
- Frozen artifacts: `src/MMLib.Alvo.Abstractions` (`IAppliedSchemaStore`,
  `ISchemaRegistry`, `MigrationOptions`/`MigrationResult`), `AlvoHost`,
  `SchemaMigrationRunner`, `RuntimeSchemaService`, `EntityRouteCatalog`.
- `docs/architecture/` — `extensibility.md` (the verb taxonomy and rule 5),
  `host.md`, `data-api.md` ("Route generation happens at mapping time"),
  `package-boundary.md`.
- The F3 design's **deviations 36, 38, 41, 48**, and issues **#103, #132, #133,
  #140, #141**.
- Prior art: ASP.NET Core's `IHostedLifecycleService` / `IStartupFilter` /
  `EndpointDataSource`, EF Core's position on migrating at startup, Kubernetes
  liveness-vs-readiness semantics, and the `AddControllers`/`MapControllers`,
  `AddAuthentication`/`UseAuthentication` and Aspire `AddServiceDefaults`
  registration precedents.

## What the brief omits, and why it matters here

`docs/design-brief.en.md` is lossy by design, but four of its gaps sit directly
in this design's blast radius, and designing from the brief alone would have
produced the wrong answer:

1. **Health is absent entirely.** The full source makes liveness **and**
   readiness a "must contain", naming DB / cache / message-bus reachability
   (A:487). A design that introduces a "not ready yet" state looks invented from
   the brief; it is in fact mandated.
2. **"Validate provider configuration at startup, fail fast with an actionable
   message, not at first use" is missing.** It is an acceptance criterion
   (A:91) and a named watch-out (A:84). Deviation 48 is therefore not a nicety
   deferred — it is an **unmet acceptance criterion**.
3. **The auto-migrate-on-boot licence, and its scope limit, are missing.** The
   sources *do* mandate automatic migration at startup — **for the framework's
   own system schema only** (A:508), on an independent chain that does not touch
   the host's tables (A:515), with a documented upgrade/downgrade contract
   (A:555). User entity schema is located at build/deploy (A:510) or an explicit
   runtime apply. This is the distinction the current code does not make.
4. **Every numeric criterion is gone**, including the one that constrains this
   design hardest: `docker run mmlib/alvo` must reach a working backend in
   **≤ 60 s with no configuration at all** (A:553).

## The objection is right, but the reason is not the one it looks like

Auto-migrate-on-boot *is* a production anti-pattern — replicas race, it needs DDL
rights at runtime, and a bad migration takes the deployment down. But the sources
do not forbid migrating at boot; they **partition** it. What is wrong with the
current code is not that it migrates during startup. It is that **one call does
five things at three different risk levels**, and the host has to opt into all
five or none.

`SchemaMigrationRunner.RunAsync` currently:

1. loads and JSON-Schema-validates the descriptor,
2. maps it to a desired `SchemaModel`,
3. compiles the CEL policy catalog and publishes it — **priming**,
4. diffs desired against applied and executes **DDL**,
5. saves the applied snapshot.

Steps 1–3 are pure CPU plus one descriptor read. They need **no database at
all**, they are what routes and authorization depend on, and they are safe on
every replica simultaneously. Step 4 is the dangerous one. Today a host that
wants (1–3) — which is every host that merely wants to *serve* an
already-migrated database — has no way to ask for them without also asking for
(4).

That conflation is also why `apply → map → listen` is forced: `MapAlvoDataApi`
needs step 3's output, so the host must run step 4 to get it.

And the gap is already named in the code. `RuntimeSchemaService`'s own remarks:

> **Nothing primes at startup.** This service is driven by a request, so a host
> that only ever applies descriptors at runtime comes back from a restart with an
> unprimed provider … That is the safe direction, but it is **a real gap rather
> than a design intent**.

So the design is not "stop applying at startup". It is **separate the five steps,
and let each run at the moment its risk justifies**.

## Measured facts

Three claims are load-bearing, so they were measured rather than assumed, on
.NET 10.0.0 / `Microsoft.AspNetCore.App` 10.0.0. Verbatim output:
`docs/superpowers/specs/evidence/2026-08-02-startup-lifecycle/spike.txt`.

| # | Question | Measured answer |
|---|---|---|
| 1 | Is an `EndpointDataSource` enumerated during `StartAsync`? | **No** — 0 enumerations before *and* after `StartAsync`. It is enumerated lazily, on the first request that builds the matcher. |
| 2 | Can the schema be primed **after** the app is listening and still produce routes? | **Yes** — primed post-`StartAsync`, `/api/orders` answers 200, and an undeclared `/api/nope` still 404s. Fail-closed is preserved. |
| 3 | Can a route be added after the matcher was already built? | **Yes**, with an `IChangeToken`: 404 before `Invalidate()`, 200 after. This is #103's resolution A, and it is ~15 lines. |
| 4 | Do hand-built `RouteEndpointBuilder` endpoints appear in the OpenAPI document? | **No.** A real `MapGet` control in the same app *does* appear. Hand-built endpoints lack the metadata ApiExplorer needs. |
| 5 | Does building through the real `Map*` helpers on a nested `IEndpointRouteBuilder` fix that? | **Yes** — `/api/orders` and `/api/customers` both appear in the document. |
| 6 | Does the OpenAPI document refresh when the data source invalidates? | **No.** After `Invalidate()` the new entity *routes* (200) but is still **absent from the document**. |
| 7 | Does `IHostedLifecycleService.StartingAsync` run before Kestrel listens? | **Yes** — `StartingAsync` and `StartAsync` both observe the port closed; only `StartedAsync` observes it open. |
| 8 | Does `ValidateOnStart` run before `StartingAsync`? | **Yes.** An invalid option threw `OptionsValidationException` and `StartingAsync` **never ran at all**. |

Fact 6 corrects a claim this repository currently makes. `data-api.md` says
resolution A "keeps the OpenAPI document able to list exactly what is mapped".
It does not — **not for free**. Routing and the document have independent caches,
and invalidating the endpoint data source refreshes only the former. #103's real
remaining cost is document-cache invalidation, which nothing has yet designed.

Fact 4 is the trap this design would otherwise have walked into: the obvious
implementation of a lazy data source silently empties the OpenAPI document that
PR3 built and snapshot-tests.

Fact 7 is what makes the whole thing safe. `StartingAsync` runs before **every**
`IHostedService.StartAsync`, including `GenericWebHostService`'s, so it does not
depend on registration order.

A plain `IHostedService` would *also* work today, but for a weaker reason:
`WebApplicationBuilder.Build()` deliberately **appends** the
`GenericWebHostService` descriptor after every user-registered hosted service
([aspnetcore#36122](https://github.com/dotnet/aspnetcore/pull/36122), .NET 7),
so user services start before Kestrel binds. That is a behaviour change with no
API-level guarantee, and it is sensitive to *when* `AddAlvo` was called relative
to `Build()`. `StartingAsync` is guaranteed by the documented lifecycle instead of
by a registration-order accident, so it is the one this design uses.

Fact 8 removes a step the first draft of this design had, and is worth recording
because it inverts a claim the current code is built on. `AlvoHost` calls
`IStartupValidator.Validate()` **by hand**, and its remarks explain why:
`ValidateOnStart` runs from `app.StartAsync()`, which is *after* `BuildAsync` has
already applied the descriptor. That reasoning is correct **for the current
shape** and stops being true once the apply moves into the host lifecycle:
`Host.StartAsync` runs the startup validator *before* any `StartingAsync`
([Host.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Hosting/src/Internal/Host.cs)),
measured in fact 8. So `AlvoHost.ValidateOptions` is **deleted**, not relocated,
and the ordering property it protects becomes a framework guarantee rather than a
line of host code that a future edit could drop.

## Prior art

Alvo adopts known mechanisms so an agent recognizes them (`CLAUDE.md`), so the
established answers are recorded here rather than re-derived.

### How comparable products split apply from serve

| | Serving process applies schema on boot? | Separate apply command | How the server learns of a change | Routes rebuilt at runtime? |
|---|---|---|---|---|
| **Supabase / PostgREST** | **never** — PostgREST only *reads* the catalog | `supabase db push` | `NOTIFY pgrst, 'reload schema'` / `SIGUSR1`, or a `ddl_command_end` trigger | yes, no restart |
| **Hasura** | no for `graphql-engine`; **yes** for the dedicated `cli-migrations-v3` image | `hasura migrate apply` | `POST /v1/metadata {"type":"reload_metadata"}`, returning `is_consistent` + `inconsistent_objects` | yes |
| **PocketBase** | **yes, always** (`--automigrate`, on by default; gated on `IsProbablyGoRun()` when embedded) | `pocketbase migrate` | in-process | **no rebuild needed** — routes are generic |
| **Directus** | no — CLI only | `directus bootstrap` | `schema/snapshot` → `schema/diff` → `schema/apply` on the live instance | **no rebuild needed** — routes are generic |
| **Strapi** | **yes, always** — and the sync *"will delete any unknown tables without warning"* | none | file change + **process restart** | fixed at boot |
| **K8s operators** | n/a — applying and reconciling are different processes by construction | `kubectl apply` | watch + reconcile, readiness *reported* via `status.observedGeneration` | n/a |

Three lessons, all of which this design takes:

- **Hasura is the closest precedent and it splits by image, not by flag.** The
  auto-applying variant is a *separate published artifact*. That is exactly the
  core/`Host` split A:530 already mandates.
- **Strapi is the counter-example, and its price is instructive.** Because it
  fuses apply and serve, it must forbid schema authorship in production outright
  — *"at this time and in the future there is no plan to allow model creating or
  updating while in a production environment."* Fusing the two forecloses the
  dashboard-first mode Alvo is committed to.
- **Kubernetes' `spec`/`status` split is the right vocabulary for readiness.**
  Desired state is submitted by one actor and realised by another, and
  `status.observedGeneration` is what makes "has my descriptor actually been
  applied?" answerable. Alvo's analogue is exact: the applied snapshot already
  carries a **revision**, so readiness is `appliedRevision` matching the
  descriptor the process actually primed from. That one comparison serves the
  probe, the CLI and a future dashboard identically.

### EF Core on migrating at startup

Worth quoting because it is the guidance the objection rests on, and because it
is narrower than "never migrate at boot":

> It's possible for the application itself to apply migrations programmatically,
> typically during startup. … this approach is **inappropriate for managing
> production databases**.

The four reasons that survive EF 9 are: an app accessing the database while
another migrates it *"can cause severe issues"*; the app *"must have elevated
access to modify the database schema"*, against good practice in production;
there is no rollback; and *"the SQL commands are applied directly by the program,
without giving the developer a chance to inspect or modify them"*. Only the
concurrent-instance reason was mitigated, by EF 9's database-wide migration lock
— and Microsoft did **not** upgrade the recommendation, restating in the same
release that *"we recommend applying migrations at deployment, rather than as part
of application startup."* The lock is serialisation, not safety.

**Alvo does not use EF Migrations**, so EF's lock does not apply to it: the
project schema goes through Alvo's own `ISchemaMigrator` + `IAppliedSchemaStore`.
The transferable part is the *reasoning*, and one concrete warning worth heeding
— EF's SQLite lock is a table row with **no timeout** that survives a killed
process, so an OOM-kill mid-migration wedges every subsequent boot until someone
drops it by hand. Whatever serialisation Alvo grows must not have that shape;
this design leans on the optimistic revision check instead, which has no lock to
leak.

### The routing seam

The ASP.NET Core routing docs' guidance for library authors endorses this design's
mechanism explicitly — *"**CONSIDER** writing your own `EndpointDataSource`.
`EndpointDataSource` is the low-level primitive for **declaring and updating** a
collection of endpoints"* — and constrains it in two ways this design obeys:

- *"**DO NOT** attempt to register an `EndpointDataSource` by default. … The
  philosophy of routing is that nothing is included by default."* So `MapAlvo()`
  stays **mandatory and explicit**; the DX pass below never auto-maps.
- *"**DO NOT** call `UseRouting` or `UseEndpoints` on the user's behalf."*

Two mechanical details from the framework source, both easy to get wrong:

- `WebApplicationBuilder` decides whether to wire `UseRouting`/`UseEndpoints` at
  all from `_builtApplication.DataSources.Count > 0` — it counts **data sources,
  not endpoints**. Registering one *empty* data source at `Map` time is therefore
  both necessary and sufficient; registering none means routing is never added.
- The change token must be swapped **before** the old one is cancelled (new list →
  new `CancellationTokenSource` → publish → *then* cancel the old). The reverse
  order re-enters and overflows the stack — a real bug fixed in
  [aspnetcore#44392](https://github.com/dotnet/aspnetcore/pull/44392).

YARP is the in-box precedent for the startup half, and its documented contract is
the one adopted here: a config provider may **throw** (prevent the application
from starting), **block** (delay startup until valid data is available), or
**return empty and signal later**. This design throws for stages 0–2 and does not
use the empty-and-signal-later option, which is what would let a host serve while
unreconciled.

### Why not generic routes

PocketBase (`/api/collections/{collection}/records`) and Directus
(`/items/{collection}`) add an entity with **zero** routing-table mutation,
because their record APIs are generic. That would dissolve this whole problem —
and #103's — into a schema-cache question, which is strictly cheaper. It is
rejected for Alvo, and the reason is already load-bearing elsewhere:
`data-api.md`'s Position A rests on **routing** answering "no such entity" before
authorization runs, and the OpenAPI document has to enumerate real paths per
entity (`[15b]`, and #26's contract linting assumes it). A catch-all can do
neither. Recorded as a considered alternative rather than an unexamined choice —
and note that for F7's *virtual* entities the trade may genuinely invert, which is
#103's resolution B.

## The design

### Five stages, each at its own risk level

The boot sequence runs in `IHostedLifecycleService.StartingAsync`, i.e. before
the server listens (fact 7).

| Stage | What it does | Touches the DB? | Governed by |
|---|---|---|---|
| **0 Validate** | load and JSON-Schema-validate the descriptor; map to the desired schema; compile CEL; run the reserved-name and format checks | no | always — failure is a configuration error |
| **1 System schema** | bring the framework's own `alvo.*` tables up (`descriptor_versions`, `alvo_idempotency`, PR5's outbox) on their own chain | yes, DDL | **always**, mandated by A:508/A:515/A:555 |
| | *(needs no code of its own today — see below)* | | |
| **2 Project schema** | diff the descriptor against `IAppliedSchemaStore`'s snapshot and decide | yes, reads always; DDL conditionally | the **startup mode** (below) |
| **3 Prime** | publish the policy catalog and the schema model for the descriptor stage 2 confirmed is the one in the database | no | always, if stage 2 succeeded |
| **4 Serve** | routes materialise lazily from the primed registry on the first request (facts 1, 2, 5) | no | — |

**Stage 1 needs no code of its own, and that is a finding rather than a
shortcut.** `SystemSchemaInitializer` is `internal` to
`MMLib.Alvo.Data.EntityFrameworkCore` and has **no port in `Abstractions`**, so
the core cannot call it without breaking `package-boundary.md`. It does not need
to: the system schema is owned by whichever driver implements
`IAppliedSchemaStore`, and that driver cannot answer a single call without it —
`EfCoreDescriptorVersionStore` routes *every* method through
`VersionRowWriter.EnsureReadyAsync`, which runs `SystemSchemaInitializer.EnsureAsync`
once, race-guarded. So **stage 2's read of the applied snapshot *is* stages 1 and
2**, unconditionally, in every mode. A:508/A:515 hold — automatic at startup, on a
chain independent of the host's own tables — with no abstraction invented for it.

A port is earned the moment a driver's system schema grows a table that no store
call touches. **PR5's outbox is the first candidate**, so this is the seam that
work should expect to add rather than discover.

One consequence, recorded rather than hidden: the design's own line that `Skip`
should *"not read the store"* is **wrong as written**. `Skip` does read it,
because stage 1 is unconditional and that read is the only thing that brings the
system schema up. The read is idempotent and touches exactly the tables stage 1
must create anyway; `Skip` still ignores what it found. Deviation 58.

Options validation is **not** a stage here, because it does not need to be
(fact 8). The framework runs every `ValidateOnStart` registration before any
`StartingAsync`, so the property the existing test
`A_credential_the_startup_validation_refuses_leaves_the_database_untouched`
pins — a mistyped `Alvo__Auth__DevKeys__0__Scopes__0` must not commit a migration
and *then* crash-loop, because rolling back does not recover — is guaranteed by
the host lifecycle rather than by a call the host has to remember to make.

That test must keep passing unchanged, which is the point: it is the fact that
proves the guarantee transferred, rather than being lost along with the line of
code that used to provide it.

### Stage 2's decision, and why "initialize" is not "migrate"

The hazard the sources warn about, and the hazard EF Core's guidance warns
about, is **migrating an existing database**: replicas race, DDL rights are
needed at runtime, a bad migration is hard to reverse. Creating a schema from
nothing is a different act with a different risk profile. Conflating the two is
what forces the choice between "unsafe in production" and "broken zero-config
dev".

So stage 2 branches on what it finds, not only on what it was told:

- **Unchanged** — the applied snapshot matches the descriptor. Prime and serve.
  This is the ordinary restart and the common case.
- **Uninitialized** — no applied snapshot exists at all. **Initialize, in every
  mode except `Skip`.** This is what makes `AddAlvo()` → `dotnet run` → a working
  backend in seconds (S:157/S:164/S:169) and `docker run` → a working backend in
  ≤ 60 s (A:553) both hold with no configuration.
- **Drifted** — a snapshot exists and differs from the descriptor. Governed by
  the mode.

**The destructive guardrail sits *before* this branch, not inside the `Apply`
arm — and that ordering is a data-loss fix, not tidiness.** The first draft of
this design put it inside `Apply`, which was wrong, because **"no applied
snapshot" does not mean "empty database."** It means *Alvo has recorded no schema
for this project*. `SchemaMigrationRunner` falls back to `ISchemaIntrospector` in
exactly that case, so an "initialize" plan computed against an **adopted**
database — one with pre-existing tables Alvo did not create — can legitimately
contain drops. With the guard inside the `Apply` arm, initialization would have
been *unguarded*, and the first boot of Alvo against someone's existing database
could have dropped their columns while `Verify` was still the configured mode.

So the evaluation order is: `Skip` → empty plan → **destructive gate** →
uninitialized → mode. A destructive plan is refused in *every* mode, including
during initialization, unless `AllowDestructive` is explicitly set. Found while
implementing Task 3; pinned by
`An_absent_snapshot_does_not_mean_an_empty_database_so_a_destructive_initialization_is_refused`.

The mode, `AlvoSchemaStartup`:

| Mode | On *drift* | Intended for |
|---|---|---|
| `Verify` *(core default)* | refuse, printing the structured diff | production; an embedded host inside someone else's application |
| `Apply` | apply it, still refusing a destructive plan unless `AllowDestructive` | the dev loop, and the standalone image |
| `Skip` | do not read the store, do not prime from it | a host whose schema is owned entirely by a migration job |

**`Verify` is the core's default, and `Apply` is the standalone image's setting**
— written visibly in the image's own `appsettings.json`, not hidden in code.
That is precisely A:530 / S:91's "the image is a pre-wired host over the same
NuGet, not a different product": the *mechanism* lives in the core, the *policy*
differs by distribution.

An **environment-gated** default (`Apply` in Development, `Verify` otherwise) was
considered and rejected. It reads well, but the container runs in Production by
default, so the image would silently take the `Verify` branch and fail A:553's
60-second criterion — the exact class of surprise the maintainer is objecting to.
Branching on *initialized vs drifted* achieves the same DX with no environment
magic and no hidden coupling to `ASPNETCORE_ENVIRONMENT`.

**Initialization under concurrency.** Three replicas starting against an empty
database all see "uninitialized". One wins; the losers **re-read, re-decide, and
converge**. That convergence is a required behaviour, not an accident — a loser
that throws would turn a normal cold start of a replica set into a crash loop.

Two things about that were wrong in this design's first draft, both found by
measuring rather than reasoning, and the second is a bug that predates this work.

**The write must be atomic, or there is nothing to converge on.** The first draft
assumed the losers are serialized by the applied-snapshot write. They were not:
the boot applied DDL through `ISchemaMigrator.ApplyAsync` and *then* saved the
snapshot — two transactions, DDL first — so a loser never reached the snapshot
write at all. It got `SQLite Error 1: 'table "depots" already exists'`. Worse,
DDL-first leaves a window in which the tables exist and no revision row explains
them, so a loser's re-read can legitimately see `null`.
`EfCoreRuntimeSchemaWriter`'s own remarks already named this ordering as the wrong
one — *"With insert-first, the only writer that reaches the DDL is the confirmed
winner."* So the boot writes through `IRuntimeSchemaWriter.ApplyAndAppendAsync`:
version row first as the gate, DDL and row in one transaction. The destructive
guardrail is **restated at that call site**, because that writer deliberately
re-evaluates no policy and dropping it would have been a silent data-loss
regression.

**`CREATE TABLE IF NOT EXISTS` is not concurrency-safe on PostgreSQL**, and stage 1
hits that before any project-schema race can happen. The catalog check and the
insert are not atomic, so every replica failed its *first* database call with
`23505 duplicate key value violates unique constraint "pg_type_typname_nsp_index"`
inside `SystemSchemaInitializer.EnsureAsync`. `IdempotencyTable`'s remarks already
knew this on the data path (*"a duplicate-relation error on PostgreSQL … is retried
by the caller"*); the boot had no such retry, and the one retry it did have was
being consumed here, leaving none for the race it was for. Fixed at source: run the
DDL, and on any `DbException` **probe whether the table now exists**
(`SELECT 1 FROM t WHERE 1 = 0` — the one question both engines answer identically)
and rethrow if it does not. No `SQLSTATE` and no `SqliteErrorCode` is decoded, which
is the same "re-read rather than classify" discipline `VersionRowWriter` uses.

Measured, once the ordering was atomic: the SQLite loser gets a clean
`DescriptorConcurrencyException`, never `SQLITE_BUSY` — its own post-conflict
re-read has to take the write lock, so it serializes behind the winner's commit.
A **different** descriptor (a rolling deploy mid-flight) is ordinary drift with a
non-null snapshot, so the mode governs and the default `Verify` **refuses**: a
loser never silently adopts the winner's schema.

### How a failure surfaces

A stage 0–2 failure throws out of `StartingAsync`, which fails `StartAsync`,
which stops the process before the server ever listens. That is today's
behaviour and it is deliberate — #132 states it plainly: *"Refusing to start is
the designed behaviour and the right one … Nothing here asks for a fallback."*

What changes is the **presentation**, which is what #132 actually asks for and
what A:91 requires as an acceptance criterion. Every refusal is a structured,
operator-readable message naming the thing and the fix, written before the
process exits with a deliberate code — not an unhandled `FileNotFoundException`
and exit 139:

```
Alvo cannot start: no project descriptor at /alvo/descriptor.json.

  Mount one:  docker run -v ./project.alvo.json:/alvo/descriptor.json mmlib/alvo
  Or set:     Alvo__DescriptorPath=/path/to/descriptor.json
```

and, for drift under `Verify`:

```
Alvo cannot start: the mounted descriptor does not match the schema applied to this database.

  add column  orders.discount (numeric, null)
  drop column orders.legacy_ref          <- destructive

  Apply it with a migration job, or set Alvo__Schema__Startup=Apply to apply on boot.
```

This closes **#132**, and it discharges **deviation 48** by giving
`AlvoHostOptions` the `IValidateOptions<T>` + `ValidateOnStart` that
`extensibility.md` rule 5 requires and that A:91 makes an acceptance criterion.

**Disposal must not regress.** `BuildAsync` today wraps composition in
`try/catch → DisposeAsync` because a refused start used to leak the whole
application — the service provider stayed alive holding the SQLite file open,
visible in this repository's own suite as a swallowed `IOException`. Moving the
failure into `StartingAsync` moves *where* that leak could reappear, so the
existing fact `A_refused_restart_disposes_the_application_it_had_already_built`
must keep holding against the new shape, and the host's `Program.cs` must
dispose the application when `RunAsync` throws.

### Readiness, and keeping deviation 38's guarantee

Deviation 38's guarantee is: **a host whose apply is refused must not report
healthy while serving nothing.** Today that holds by an accident of ordering —
the apply happens before `RunAsync`, so answering `/health/live` at all proves it
succeeded. Under this design it holds **structurally**, and from both ends:

- A refused boot never reaches `StartedAsync`, so the server never listens and
  nothing answers at all — the strong end, unchanged.
- The boot service publishes its state (`Pending` / `Ready` / `Failed`), and
  `/health/ready` reports it. So even if a future mode were to let a host start
  degraded, it could not report *ready* while serving nothing.

`/health/live` stays unconditional — the process is up. `/health/ready` becomes
the schema-applied signal, expressed the way Kubernetes expresses it: readiness is
`appliedRevision == the revision this process primed from`, the direct analogue of
`status.observedGeneration`.

This is the standard split and it is the correct one: a failing **liveness** probe
gets the container killed and restarted, which is the wrong response to "the
migration job has not run yet" — and the Kubernetes docs warn that exactly this
mistake causes *"cascading failures … restarting of container under high load"*. A
failing **readiness** probe merely removes the pod's address from the Service's
EndpointSlices, which is precisely right.

**One trap, and it would silently void the whole gate.** ASP.NET Core's default
`ResultStatusCodes` map `Healthy` → 200, **`Degraded` → 200**, `Unhealthy` → 503,
and Kubernetes treats any 200–399 as success. So "schema not applied" must be
reported **`Unhealthy`, never `Degraded`** — a degraded schema gate is invisible to
an `httpGet` probe and the pod would receive traffic anyway. The fact that pins
readiness must therefore assert the **status code**, not merely the reported
health string; asserting the string is how this passes review and fails in
production.

The two paths are also configured oppositely, deliberately: liveness registers
**zero** checks (`Predicate = _ => false`), so a future health check cannot
accidentally make liveness fail and start killing containers; readiness selects by
tag, so a check registered without thinking lands in readiness, where the
consequence is losing traffic rather than losing the process. Aspire's service
defaults make the same choice for the same reason.

### The readiness endpoint must not echo the failure reason

Found while implementing the boot service, and it is an information-disclosure
bug that would otherwise have shipped in the obvious implementation.

`AlvoBootState.Failure` records the reason a boot was refused, and
`/health/ready` is **unauthenticated by design** — a probe cannot hold a
credential, and the existing liveness fact
`Liveness_answers_an_unauthenticated_probe` pins that shape.

**The premise, measured and narrowed.** This design first claimed the reason is the
provider's message and "routinely carries a connection string". That is **not
evidenced for either shipped provider**: `SqliteException` reports
`"SQLite Error 14: 'unable to open database file'"` with no path, and Npgsql reports
a `SocketException` for an unreachable host and the offending *keyword* — never its
value — for a bad connection string. What **is** genuinely leaked today is a
**filesystem path**: a missing descriptor throws `FileNotFoundException` and
`AlvoBootState.Failure` then contains the descriptor's absolute path.

The guard is still right, for a reason that does not depend on the overclaim:
`Failed(failure.Message)` accepts *any* exception from *any* provider, interceptor
or third-party driver, and `IAppliedSchemaStore` is a public port precisely so a
third party can implement it. So the barrier is against a class of message, not
against a specific observed one — and the non-vacuity fact anchors on the path leak
that does exist rather than on a hypothetical.

So whatever answers readiness reports the **phase**, and never that text:

- the probe response carries `Pending` / `Ready` / `Failed` and nothing else;
- the reason goes to the **log**, where it is already governed by the host's own
  redaction, and to the operator-facing refusal written to stderr before the
  process exits;
- a fact must assert the negative — that a readiness body does **not** contain a
  connection-string fragment — because the positive assertions (503 while pending,
  200 when ready) all pass happily while the body leaks.

**There are two independent leak sites, so there are two facts.** The health
*check*'s description and the response *writer* are separate barriers: making the
check leak its reason does **not** turn the HTTP body fact red, because the writer
discards the report. That is exactly why the HTTP fact alone is insufficient, and
each barrier carries its own discriminating mutation.

**`Degraded` is deliberately left mapping to 200.** Remapping it to 503 in
`ResultStatusCodes` would hide the trap behind an option rather than pin it with a
fact — and would make the `Unhealthy` → `Degraded` mutation *stop* going red, which
is the one observation proving the gate works. A host's own checks may also mean
`Degraded` honestly.

This is why the change is labelled `needs-deep-review` and why
`alvo-security-core-review` is run on it: the boot path decides *when the policy
catalog is primed*, and an unprimed catalog denies everything, so the fail-closed
direction is a security property rather than an implementation detail.

This delivers the **schema-applied half** of §2.12's readiness bullet (A:487).
It does **not** deliver the continuing database-reachability probe — that needs
the port **#133** exists to design, and the core may not touch a provider
directly (§0 principle 2). So #133 stays open, narrowed: this design supplies
`/health/ready` and its registration seam; #133 supplies the reachability check
that plugs into it. Said plainly so a later reader does not read `/health/ready`
existing as §2.12 being met.

### Breaking `apply → map → listen`

`MapAlvoDataApi` currently enumerates `EntityRouteCatalog` **eagerly**, inside
the map call, so the schema has to be primed before the host maps. It is replaced
by an `EndpointDataSource` that reads the registry at **enumeration** time
(facts 1, 2), so:

```
register  →  map (declaratively, schema not yet known)  →  boot/prime  →  listen  →  first request materialises routes
```

Two things must be preserved through that move, and both are traps:

- **The OpenAPI document must not silently empty** (facts 4, 5). The data source
  builds its endpoints through the genuine minimal-API `Map*` helpers on a nested
  `IEndpointRouteBuilder`, never by hand — that is the only measured way the
  ApiExplorer metadata survives. A fact must pin the document's contents, or a
  future refactor to hand-built endpoints passes every routing test and empties
  the document.
- **The eager refusals must stay eager.** `MapAlvoDataApi` today throws at map
  time when an entity declares a field named after a reserved query key, and
  `DataApiQueryTests` pins that. Under a lazy data source that refusal would move
  to the first request — a startup error becoming a runtime 500. So the *checks*
  (`ReservedQueryKeys.EnsureNoneIsShadowed`, `FormatCatalog.Build`) move into
  **stage 0**, where they run before anything is durable and still fail the start.

Two mechanical requirements fall out of the framework's own behaviour, both
recorded above under *Prior art → The routing seam*: the data source must be
registered (empty) during `MapAlvo()`, because `WebApplicationBuilder` wires
`UseRouting`/`UseEndpoints` only when `DataSources.Count > 0` and counts *sources*
rather than endpoints; and if the mutable half is ever built, the change token must
be replaced before the old one is cancelled.

This delivers the lazy half of **#103** and leaves the mutable half measured but
unbuilt. #103 is updated, not closed: its remaining substance is (a) runtime
route addition for F7's virtual entities, and (b) the newly measured fact that
the OpenAPI document does **not** refresh on invalidation.

### The configuration surface

The briefing's list — `AddAlvo` + provider + descriptor + `AddAlvoApi` +
`AddAlvoAuth` + `AddAlvoProblemDetails` + `AddAlvoHostDocs` + `MapAlvoDataApi` —
overstates the problem, and saying so is more useful than inventing a
simplification. `AddAlvoApi` and `AddAlvoAuth` are **already internal**;
`AddAlvo` composes them. `AddAlvoHostDocs` is internal to the Host. The actual
public surface today is five members:

```csharp
services.AddAlvo(alvo => alvo.UseSqlite(cs).FromDescriptor(path).AddDataApi());
services.AddAlvoProblemDetails();
var r = await app.Services.ApplyAlvoDescriptorAsync(); r.EnsureApplied();
app.MapAlvoDataApi();
```

So the complexity is **not the call count — it is the ordering obligation**. A
host must know that the apply precedes the map, that the result must be checked
rather than discarded, and that `EnsureApplied` is the difference between serving
and 404-ing everything while reporting healthy. That is three pieces of
load-bearing folklore, and none of it is expressible in the type system.

The design removes the obligation rather than renaming the calls:

```csharp
builder.Services.AddAlvo(alvo => alvo
    .UseSqlite("Data Source=app.db")
    .FromDescriptor("project.alvo.json"));

app.MapAlvo();
```

- **`AddDataApi()` becomes configuration-only** — and the Data API was *already*
  registered by `AddAlvo`, which this design initially got wrong (deviation 56,
  withdrawn). So S:157's "**one entry point for the whole framework**" was already
  satisfied for registration; what this task removed was a redundant second
  `AddAlvoApi()` call inside `AddDataApi` and the docs that described it as
  load-bearing.
- **`MapAlvo()`** maps the Data API plus health. It is not an invention — it is
  the spec's own name (S:167). `MapAlvoDataApi()` and `MapAlvoHealth()` stay
  public for a host that wants the pieces, exactly as `MapControllers` coexists
  with finer-grained mapping. `MapAlvo()` is the composition, not a replacement.
- **The apply disappears from host code.** `ApplyAlvoDescriptorAsync` stays
  public — the CLI and the Management API need it, and it is the `Apply{Thing}`
  verb `extensibility.md` added for exactly that — but no host has to call it,
  and none of the ordering folklore survives.
- **`AddAlvoProblemDetails()` stays opt-in**, deliberately. Deviation 36's
  reasoning still holds: an embedded host has its own error handling, and
  silently taking over `UseExceptionHandler`'s document shape inside someone
  else's application is worse than one explicit call. Recorded rather than
  quietly "simplified".

`AlvoHost.BuildAsync` collapses to composition: no apply, no `EnsureApplied`, no
`try/catch` around a migration. It stays `async` only if something else needs it
to be.

### Not foreclosing #141

Serving several projects from one host is parked (#141). This design does not
build it and must not wall it off. Two cheap precautions, no more:

- The boot state is **keyed by project name**, over a collection that today has
  exactly one entry. `IPolicyCatalogProvider.SetCurrent(project, …)` and
  `IAppliedSchemaStore` are already project-keyed, so this matches the grain the
  data model already has.
- The endpoint data source is constructed **per project**, not as a process
  singleton reading one global registry.

`PolicyCatalogProvider`'s existing refusal of a second project stays. Keeping the
door closed but unlocked is the point; opening it is #141's work.

## Deviations from the sources

Numbering continues the F3 design's series, which ends at 51.

52. **Automatic migration at startup is kept, not removed — but partitioned.**
    A:508 mandates that the framework's **own system schema** migrates
    automatically at startup, and A:515 requires it on an independent chain that
    does not touch the host's tables. So stage 1 is unconditional. What becomes
    conditional is the **project** schema, which A:510 locates at build/deploy
    or at an explicit runtime apply. The maintainer's objection is honoured for
    the half the sources leave open, and the half they mandate is kept.
53. **`Verify` is the default, and initialization is exempt from it.** No source
    states this split; it is derived. Reason: A:553 (60 s, zero config) and
    S:157 (dev run with no configuration) are unreachable under a blanket
    `Verify`, while a blanket `Apply` is the production anti-pattern. Branching
    on *uninitialized vs drifted* satisfies both, because the hazard the sources
    and EF's guidance describe is migrating an existing database, not creating
    an empty one.
54. **`/health/ready` reports schema-applied, not reachability.** §2.12 (A:487)
    asks for readiness over DB, cache and message-bus reachability. This design
    supplies the endpoint, the state machine and the registration seam, and
    exactly one contributor to it. The reachability port stays **#133**.
    Deviation 38 is superseded in its liveness-only part and preserved in its
    guarantee.
55. **The upgrade/downgrade contract between the NuGet version and the system
    schema version (A:555) is not designed here.** Stage 1 creates the current
    system tables idempotently; it does not yet carry a version contract for
    downgrades. Recorded because A:555 is an acceptance criterion and this design
    touches the exact mechanism — deferred to the issue that publishes the image
    (#24), not silently skipped.
56. **~~`AddDataApi()` becoming default-on is a public-API behaviour change.~~
    WITHDRAWN — the premise was false.** Measured at Task 8: `AddAlvo` has **always**
    called `AddAlvoApi()` (`origin/main`, `AlvoServiceCollectionExtensions.cs:57`), so
    the Data API's services were registered by `AddAlvo` before this branch existed.
    Nothing became default-on; no host's container changes; the full fast suite shows
    no test changing behaviour. What actually changed is narrower: `AddDataApi` no
    longer *registers* anything, only configures. That can affect only a caller holding
    a hand-rolled `IAlvoBuilder`, and none can exist — `AlvoBuilder` is `internal`.
    Recorded as withdrawn rather than deleted, because this design asked the maintainer
    to ratify a breaking change that is not one.
58. **`Skip` reads the applied-schema store, contrary to this design's own
    earlier wording.** Stage 1 is unconditional (A:508/A:515), and the store read
    is what brings the system schema up, so there is nothing to skip. `Skip` still
    ignores what the read found. Recorded because the design previously said `Skip`
    would "not read the store", and a later reader would otherwise take the
    implementation for a shortcut.
59. **The readiness endpoint reports the phase only, never `AlvoBootState.Failure`.**
    §0 principle 4 wants structured errors with fix suggestions, and this
    deliberately withholds one from an HTTP response: a stage 1/2 failure reason is
    the provider's message and can carry a connection string, while `/health/ready`
    is unauthenticated by design. The operator gets the full reason on stderr and in
    the log; the probe gets a phase. Deviating from the agent-first error rule is
    correct here because the reader of a probe response is not the operator.

63. **Alvo's health check is registered through `IConfigureOptions<HealthCheckServiceOptions>`
    via `TryAddEnumerable`, not through `AddCheck`.** `AddCheck` is a plain `Configure`
    and is **additive**, so a host calling `AddAlvo` twice — which `AddBoot`'s own remarks
    explicitly support — would register two checks named `alvo-schema`, and
    `DefaultHealthCheckService` **refuses to be constructed at all** on a duplicate name.
    Both probes would then answer 500, which an orchestrator cannot distinguish from
    "not ready". `TryAddEnumerable` dedupes on implementation type, matching what
    `AddAlvoApi` already does. Recorded because it is a real trap for anyone adding a
    second Alvo health check.

60. **`IRuntimeSchemaWriter` becomes mandatory for every provider at boot.** It was
    previously resolved on demand, only by `RuntimeSchemaService` (the runtime
    dashboard-first path). The boot now needs it for the atomic version-row-then-DDL
    write, so a provider that implements `IAppliedSchemaStore` but not
    `IRuntimeSchemaWriter` can no longer boot. Both in-repo drivers implement it; the
    cost is borne by a future third-party provider, and it is a widening of the
    implicit provider contract that `package-boundary.md` should record.
61. **`SchemaMigrationRunner` keeps the non-atomic ordering the boot just abandoned.**
    The CLI / Management-API path still applies DDL and *then* saves the snapshot. Left
    deliberately: that path is a single writer by construction, so the race the boot has
    to survive cannot arise there, and changing it would alter behaviour the CLI's tests
    pin for no benefit. Recorded because the two apply paths now differ in a way a later
    reader would otherwise take for an oversight — and because if the Management API
    ever serves concurrent applies, this is the line that has to move.
62. **The concurrency fix to `SystemSchemaInitializer` is a bug fix outside this
    design's scope, shipped here because this design is what exposed it.** Multi-replica
    PostgreSQL deployments could not reliably bring the system schema up at all;
    nothing before this needed three hosts to start at once, so nothing had found it.
    Recorded so it is not mistaken for part of the lifecycle redesign.

57. **The destructive guardrail applies during *initialization*, not only during
    a mode-governed apply.** Stated as a deviation because it is stricter than
    A:513 literally requires — that criterion attaches the explicit flag to
    "DROP/column type change", without distinguishing a first apply from a later
    one. The reason is in *Stage 2's decision* above: an absent applied snapshot
    means Alvo recorded nothing, **not** that the database is empty, so an
    initialization plan against an adopted database can contain drops. The cost of
    being stricter is that adopting an existing database whose shape genuinely
    conflicts with the descriptor now requires `AllowDestructive` on the first
    boot — which is the right way round, because the alternative silently
    discards someone else's columns.

## Ratification needed from the maintainer

Flagged rather than decided silently, per the brief.

1. **`Verify` as the core default (deviation 53).** The alternative is `Apply`,
   which keeps today's behaviour for every caller and makes the standalone image
   need no setting — at the cost that an embedded Alvo inside someone's ERP would
   perform DDL on their database on boot by default. I recommend `Verify`; it is
   the reversible direction.
2. **Drift under `Verify` fails the start, rather than starting un-ready.**
   Failing the start keeps #132's stated contract and deviation 38's guarantee
   literally. Starting un-ready would give a rolling deployment a better story —
   the pod is inspectable rather than crash-looping, and the readiness probe
   already exists to express it. I recommend failing the start now, because it is
   the behaviour every existing test pins, and revisiting it when #133 lands.
3. ~~**`AddDataApi()` default-on (deviation 56).** Cheap now, breaking later.~~
   **Withdrawn — nothing to ratify.** Measured at Task 8: `AddAlvo` always registered
   the Data API, so this was never a behaviour change. See deviation 56.
4. **The `Alvo__Schema__Startup` environment name.** Deviation 39 already flagged
   that the whole `Alvo__*` spelling wants confirming before the image is
   published, and this adds one more key to that set.

## Definition of Done

- A host registers Alvo and maps it; **nothing in host code calls the apply**,
  and the ordering folklore is gone from the public surface.
- The boot sequence runs in `StartingAsync`, before the server listens — pinned
  by a fact that observes the port closed, not by reading the code.
- `AlvoHost.ValidateOptions` is **deleted**, and
  `A_credential_the_startup_validation_refuses_leaves_the_database_untouched`
  still passes — which is what proves the guarantee moved to the framework
  rather than being lost with the code that used to provide it.
- A missing descriptor produces a structured, operator-readable refusal naming
  the path and the fix, and a deliberate exit code — **#132 closed**, reproduced
  through the compose stack, not only a unit test.
- `AlvoHostOptions` is validated at startup with a structured error —
  **deviation 48 discharged**, A:91 met.
- Zero-config still holds: `AddAlvo()` + `UseSqlite()` + `FromDescriptor()` on an
  empty database serves a working backend, and `docker compose up` still passes
  the existing e2e suite unchanged.
- Drift under `Verify` refuses with a readable diff; under `Apply` it applies;
  a destructive plan is still refused without the explicit flag.
- Three replicas cold-starting against one empty database all converge —
  one initializes, the others observe "unchanged" and serve. A real test, not an
  argument.
- `/health/live` answers unconditionally; `/health/ready` returns **503** until
  the boot state is Ready, and the fact asserts the **status code** — not the
  reported health string, which would pass while `Degraded` silently served 200.
- **The OpenAPI document still lists exactly the mapped routes** — the fact that
  catches fact 4's trap.
- A reserved-field-name descriptor still fails the **start**, not the first
  request.
- A refused start still disposes the application it had already built.
- ring2 green; `scripts/test-e2e` green (the host and compose are touched);
  `alvo-plan-guard` dispatched; reviewer subagents run as substitutes for
  `/code-review` and `/security-review`.
