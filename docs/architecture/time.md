# Time: timers, schedules and data migrations

How Alvo gives host code durable time (HX-13; spec `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`
§4.10 and its *HX-13 settled details*). Three things, one port, one rule: **every firing is one transaction** — the
fenced change to the time row and the outbox append commit together, or neither does. What fires is an event; what
runs is a delivery, with the retries, history, dead letter and run-again every handler already has.

| Thing | Registered by | Fires | Runs as |
|---|---|---|---|
| Timer | `IAlvoScheduler.ScheduleAsync(key, at, …)` at run time | its custom event, once, at its latest `at` | every handler of that event |
| Schedule | `AddSchedule<TJob>(id, cron, timeZone)` at start | `alvo.schedule.<id>` per occurrence (per tenant on `EachTenant`) | a `schedule` delivery of the job |
| Data migration | `AddDataMigration<T>(id, afterRevision)` at start | `alvo.migration.<id>` once per revision (per tenant on a scoped schema) | a `migration` delivery |

## The port

`ITimerStore` (Abstractions, `MMLib.Alvo.Scheduling`) owns three tables on the **same database as `alvo_outbox`** — a
provider that implements `IOutboxStore` implements `ITimerStore` over the same connection, and
`TimerStoreContractTests` (`MMLib.Alvo.Testing`) asserts both rows commit together. It was defined complete in the first
slice (timers, schedules, migrations, run now, counts): an interface member added later breaks every implementer. The
EF Core store, `EfCoreTimerStore`, is `internal`, as `IDeliveryStore`'s is.

```sql
CREATE TABLE alvo_timers (
    scope TEXT NOT NULL,            -- the tenant, or '' for none
    timer_key TEXT NOT NULL,        -- not "key": reserved in T-SQL
    generation TEXT NOT NULL,       -- a fresh Guid v7 per upsert
    due_at TEXT NOT NULL,
    message_type TEXT NOT NULL,
    payload TEXT NOT NULL,
    provenance TEXT NOT NULL,
    lease_until TEXT NULL,
    PRIMARY KEY (scope, timer_key));
CREATE INDEX ix_alvo_timers_due ON alvo_timers (due_at);

CREATE TABLE alvo_schedules (
    id TEXT NOT NULL PRIMARY KEY,
    cron TEXT NOT NULL,
    time_zone TEXT NOT NULL,
    next_run_at TEXT NOT NULL,
    lease_until TEXT NULL,
    leased_by TEXT NULL,
    last_run_at TEXT NULL);         -- no last_outcome: it is the newest delivery's status

CREATE TABLE alvo_data_migrations (
    id TEXT NOT NULL PRIMARY KEY,
    revision INTEGER NOT NULL,
    started_at TEXT NOT NULL);
```

(PostgreSQL uses its own column types; the shape is the same.)

## Timers

**Arm.** `ScheduleAsync` upserts `(scope, key)` with a new `generation`, `due_at` (stored UTC), the event type and
payload, and the calling flow's **provenance** — correlation, causation and depth (refused when the flow is already
past the chain-depth limit). The same key again is a move — one row, a new generation. `CancelAsync` deletes the row.

**Inside a unit.** Called while `TransactAsync` (or a write hook's transaction) is open on the flow, the upsert and the
cancel write through the unit's own transaction: a rollback leaves no timer. HX-18's declarative timers take the same
table, SQL and transaction, but are written by the driver at the emit site rather than through this call — see
*Declarative timers* below.

**Claim.** `TimerPump` claims due rows with a one-minute lease, by the outbox's EvalPlanQual-safe shape:

```sql
UPDATE alvo_timers SET lease_until = @leaseUntil
WHERE <claimable> AND (scope, timer_key) IN (SELECT scope, timer_key FROM alvo_timers WHERE <claimable>
                                             ORDER BY due_at LIMIT @batch)
RETURNING …
```

`<claimable>` (`due_at <= @now AND (lease_until IS NULL OR lease_until <= @now)`) is in **both halves** because under
PostgreSQL READ COMMITTED a row another claimant just changed is re-checked against the outer `WHERE` only
(EvalPlanQual) — never against the subquery. Without the outer copy two pumps could both claim it.

**Fire.** `FireAsync(scope, key, generation, event)` in **one transaction**: `DELETE … WHERE scope AND timer_key AND
generation = @claimed`, and the outbox append only when exactly one row went. A move or a cancel made after the claim
changed or removed the row (an upsert also clears `lease_until`, so a moved timer is claimable at its new time), so the
stale fire finds nothing and appends nothing (acceptance (1), (2)). A fire whose
append fails leaves the timer in place for the next claim (acceptance (8)).

**Generation is a token, not a counter** (Decision 1). `generation + 1` restarts at 1 after a cancel, so a stale claim
of the cancelled row would match the re-armed one and fire it early. A fresh `Guid.CreateVersion7()` per upsert never
repeats. The same fence guards the pump's own clean-up: an unbuildable row is deleted only at the generation it
claimed, so a re-arm made meanwhile survives.

**Exactly-once fire, at-least-once handlers.** The timer fires one event; its handlers then run as deliveries, which
are at least once.

## Declarative timers (HX-18)

An entity's descriptor `timers` are armed, moved and cancelled by the data path itself, in the write's own transaction,
and fired by this same pump — `docs/architecture/state-machines-and-timers.md` has the arming rule and its decisions.
Two things matter here:

- **Keys starting `alvo:` are the framework's.** A declarative timer's key is `alvo:<entity>:<timer>:<id>`;
  `IAlvoScheduler.ScheduleAsync` and `CancelAsync` refuse any key with that prefix, so host code cannot move or cancel one.
  `ITimerStore` is public and does **not** refuse it: a host writing the store directly can still touch an `alvo:` row,
  and is on its own when it does.
- **Existing rows are re-planned by a framework data migration** that this same starter runs, one per entity and timer
  definition, spanning tenants — see *Re-planning existing rows* in `state-machines-and-timers.md`.
- **Written at the emit site.** The driver upserts or cancels the row through `TimerWrites` — the SQL `EfCoreTimerStore`
  uses — on the write's own connection and transaction, beside the outbox append, not through `ScheduleAsync`.

## Schedules

**Sync at start (Decision 8, as amended by Ruling R5).** Each process upserts the schedules it registers:
a new id is inserted with its next occurrence, a changed cron or zone recomputes `next_run_at`, an unchanged one keeps
it. **Rows are never pruned by absence.** A row is deleted only for an id the host declares retired
(`AddRetiredExtension`); a process claims only the ids it has bound. Multi-host safety over tidiness:

- a schedule removed from code without `AddRetiredExtension` leaves an inert row that nothing runs and the catalog does
  not list — declare it retired to delete it;
- a process that retires id X while another process still registers X deletes X; the survivor's row returns only at
  its next restart (sync runs once per process);
- two processes registering the same schedule id with a different cron or time zone overwrite each other at start —
  keep schedule definitions identical across hosts.

The sync runs when the pump starts if the boot is `Ready` and a catalog is primed; otherwise — a boot not yet ready,
or a start-time sync that failed (logged) — the first pump round does it. Running it in a round at all is the
deviation from Decision 8's "once at host start": a boot that is not ready has no catalog to sync from.

**Claim and advance.** `SchedulePump` claims due rows (`next_run_at <= now`, lease free, id among the bound ones) with a
one-minute lease and its own `leased_by`, by the same both-halves shape. `AdvanceScheduleAsync` then, in one
transaction, moves the row from the claimed occurrence to the next one — fenced by `leased_by` **and** the occurrence
being advanced — and appends `alvo.schedule.<id>` (one per tenant on `EachTenant`). Three dispatchers racing one
occurrence produce one run (acceptance (4), a PostgreSQL fact).

**Cron and zone (Decision 9).** Cronos `CronFormat.Standard` (five fields), the zone by IANA id; both are checked at
boot, every problem reported at once. Cronos's DST rules apply: a time in the spring-forward gap runs at the end of
the gap; in the fall-back overlap a fixed time runs once and an hourly or wildcard-hour schedule runs in both hours.

**Missed occurrences fire once (Decision 7).** The next occurrence is computed from `max(now, scheduledFor)`: a host
down for a week runs a daily job once, not seven times.

**The job.** A schedule is a `HandlerRegistration` with a `JobSpec` (Decision 6): one id, one delivery engine,
`AlvoOrdering.None` so a dead run never blocks the next occurrence. Jobs claim from a separate pool,
`Alvo:Deliveries:MaxJobConcurrency` (default 2), so a long job never starves event handlers; a run's default timeout
is 15 minutes. **Run now** (`POST …/schedules/{id}/run`, developer role) appends one occurrence outside the cron with
`manual: true` and fans it out at once; the stored next occurrence does not move. On `EachTenant` it makes one run per
tenant and answers the first tenant's delivery (Decision 12).

## Data migrations

`SchedulePump` polls the applied revision; the first revision a round reads at or after a migration's `afterRevision`
is the one it starts at (plan Decision 16 — "a revision number reached", not "a revision with this shape": a rollback that
lands before the start makes it start over the older shape). Its start row is inserted (`alvo_data_migrations`,
insert-if-absent) and `alvo.migration.<id>` appended in one transaction. A rollback below the revision neither undoes
nor re-runs it; a later re-apply finds the row and does nothing (acceptance (7)).

**Per tenant** (Decision 10, a deviation from §4.10's `System(null)` iterating tenants): when **the start revision's own
schema** (`AppliedSchema.Schema`) has a scoped entity — never the process's catalog, which a replica that did not make
the apply still holds — one run per tenant `IAlvoTenantCatalog` lists, each as the system **in that tenant**, so no run
reads another tenant's rows. A tenant created after the migration started never gets it. A scoped schema with **zero
tenants** marks the migration started with no runs and logs `MigrationStartedWithNoTenants` at Information.

**Failures (plan Decision 17).** Each migration's start fails on its own: a throwing tenant list or a rejected append is
logged with the migration's id, nothing is marked, and that migration alone is retried after 1 min, doubling to 15 min;
the others go on. Without a catalog the boot refuses — the process does not start — but only for migrations **not yet
started** (read from the store beside the orphaned-work check), so a finished migration's registration never refuses a
later boot. A schema that becomes scoped by a later apply, on a host with no catalog, is held at run time — logged
`TenantCatalogMissing`, skipped, not marked, retried each round.

## Framework envelopes (Decision 15)

`ITimerStore` never takes a bare `AlvoEvent`. A timer fire takes an `AlvoCustomEvent` — the custom door refuses the
`alvo.*` namespace. Schedule, migration and run-now writes take an `AlvoFrameworkEnvelope`, whose constructor is
`internal` and which only `AlvoFrameworkEvent.Create` mints (its return type changed to it; `CommandCompletion` and
`IAlvoCommandUnit` follow). `TimerEntry` is built only by the core's writer. So provenance and depth are never
caller-controlled, and a host cannot publish or schedule `alvo.schedule.*`. The pins: `TimerStorePortTests`
(no port member takes `AlvoEvent`) and `FrameworkEventMintingTests` (only the framework paths call `Create`).

## Provenance per hop

| Hop | Actor | Correlation / causation | Depth |
|---|---|---|---|
| handler → `ScheduleAsync` (stored on the row) | — | the calling flow's | the calling flow's (refused past the limit) |
| timer → fired event | `System(scope)`, subject `timers/<key>` | as stored | stored + 1 |
| schedule occurrence | `System(tenant)`, subject `schedules/<id>` | new | 0 |
| migration run | `System(tenant)`, subject `migrations/<id>` | new | 0 |

A timer that re-arms itself from its own handler climbs **two per cycle** — the handler runs one deeper than the event
it handles, and the fire adds one more — so at the default limit of 8 it fires about four times before fan-out stops
it with a dead `chain-depth-exceeded` delivery, as HX-03 stops event loops (Decision 13).

## The round

`IAlvoEventPump.DrainAsync` — the manual round — runs, in order: **timers → schedules (and migration starts) → outbox
→ deliveries**. A timer that falls due fires, fans out and runs in the same drain; `AlvoTestHost` with a
`FakeTimeProvider` therefore needs only `Advance` + `DrainEventsAsync`. In background mode there is no such order:
`TimerPump`, `SchedulePump`, the outbox dispatcher and the delivery dispatcher are independent loops, each polling on
its own.

## Settled decisions and their cost

1. **Generation = fresh `Guid.CreateVersion7` per upsert.** Cost: none functional.
2. **Column `timer_key`.** Cost: the spec's column list differs by one name.
3. **`ITimerStore` defined complete in slice A.** Cost: members without a caller for one PR.
4. **A timer's message is a custom event** (untyped + typed `ScheduleAsync<TMessage>` over `[AlvoEvent]`; fired as
   `System(scope)`, subject `timers/<key>`; `alvo.*` refused). Cost: no CLR-typed payload envelope.
5. **Tenant** = the argument, else the open unit's; required on a schema with a scoped entity; an argument differing
   from the open unit's tenant is refused. Cost: no cross-tenant timer from inside a unit.
6. **Schedule/migration = `HandlerRegistration` + `JobSpec`, `AlvoOrdering.None`.** Cost: a run that outlasts its
   period overlaps the next.
7. **Missed occurrences fire once.** Cost: no backlog.
8. **`alvo_schedules` gains `cron`, `time_zone`, drops `last_outcome`; sync at start** — as amended by R5 above
   (upsert own, delete only retired, claim only bound). Cost: an occurrence leased across a redeploy that changed the
   cron does not run; inert rows linger until retired.
9. **Cronos `CronFormat.Standard`, IANA zones, checked at boot.** Cost: no sub-minute schedules (use a timer).
10. **Migration: one run per tenant** (deviation), decided by the start revision's own schema. Cost: no cross-tenant
    view; tenants created later never get it; up to one poll interval between an apply and the start.
    - *Plan Decision 16:* a migration starts at the first applied revision a round reads at or after `afterRevision`.
      Cost: a migration that needs a shape must check for it itself.
    - *Plan Decision 17:* migration starts fail independently and back off (1 min doubling to 15 min); zero tenants
      mark it started; the boot refusal covers only migrations not yet started. Cost: a tenant list that is down
      delays a migration by up to 15 min after it recovers.
11. **Dashboard data rides appended init members** (`ManagementExtension.Cron…LastOutcome`, `AfterRevision`,
    `ManagementExtensionCatalog.Timers`). Cost: the catalog read does two more store reads.
12. **Run now on `EachTenant`** answers the first tenant's delivery. Cost: the answer names one of n runs.
13. **A self-re-arming loop is stopped by fan-out.** Cost: none.
14. **#376 closes with slice B; #390 with slice D.**
15. **Framework envelopes** — see above. Cost: a third-party provider reads these types but cannot build them outside
    its tests.

Enum wire values: `AlvoExtensionKind.Schedule = 9` (`schedule`), `AlvoExtensionKind.DataMigration = 10`
(`dataMigration`), `AlvoExtensionStage.Scheduled = 5` (`scheduled`) — the plan's 7/8/4 were taken by HX-16's
`Narrowing = 7` and `Read = 4`, and `Enricher = 8` is reserved.

A step's sleep is not a timer: it is the delivery's own `not_before` — see [`steps.md`](steps.md).
