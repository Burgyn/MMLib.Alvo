# HX-13 — Timers, schedules, data migrations — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Host code gets durable time: keyed timers (`IAlvoScheduler.ScheduleAsync`/`CancelAsync`) that fire an event exactly once at their latest due time, cron schedules (`AddSchedule<TJob>`) that run one occurrence once across replicas — once per tenant with `EachTenant` — and data migrations (`AddDataMigration<T>`) that run once per revision; all three run as deliveries (retries, history, dead letter, run now).

**Architecture:** One provider port, `ITimerStore` (Abstractions, defined complete in slice A), owns `alvo_timers`, `alvo_schedules` and `alvo_data_migrations` on the same database as `alvo_outbox`, so every firing is **one transaction**: a fenced delete/advance/insert plus the outbox append, or nothing. Claims use the outbox's EvalPlanQual-safe `UPDATE … WHERE <claimable> AND key IN (SELECT … WHERE <claimable> …) RETURNING` with the claimable predicate in both halves; timers are fenced by a per-upsert generation token, schedules by `leased_by` + the occurrence being advanced. `TimerPump` and `SchedulePump` are background dispatchers (and steps of `IAlvoEventPump.DrainAsync`); a schedule or migration is a `HandlerRegistration` carrying a job spec, subscribed to its framework event (`alvo.schedule.<id>`, `alvo.migration.<id>`, minted only by `AlvoFrameworkEvent.Create`), claimed from a separate job pool.

**Tech Stack:** .NET 10 (C# 14), `Cronos` 0.13.0 (MIT, no dependencies — new core dependency, E5), Microsoft.Data.Sqlite, Npgsql (PostgreSQL 16 Testcontainers), xUnit v3 on MTP, Shouldly, `Microsoft.Extensions.TimeProvider.Testing`, Verify + PublicApiGenerator, Blazor/MudBlazor, Microsoft.Playwright, Astro/Starlight.

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-13* (scope, docs, dashboard, acceptance (1)–(13)); §4.10 (timers, framework events, schedules, data migrations — not steps); §4.6 (who adds +1, enforcement sites, the hop table rows *handler → timer → fired event* and *schedule run*); §4.8 (delivery `kind` `schedule|migration`, job pool `MaxJobConcurrency` 2, job timeout 15 min, `POST …/schedules/{id}/run`; *HX-03 settled details* (12), *HX-06 settled details* (10), (23)); §4.13 (`Cron`/`TimeZone`, `Schedule`/`DataMigration`, `Scheduled`; *HX-04 settled details* (1)); §4.15 (*HX-02 settled details*: HX-13 adds its dispatchers as steps of one round); §5 row `13`; §6.1 row *deliveries / timers / schedules / slots claim*; §3.3 E3, E5, E12; X16; C4; Appendix A M3, M4, R-7, R-8, R-11, R-M5; *HX-10 settled details* (39) (`IAlvoScheduler` on the analyzer's await allow-list). Issue **#390** (epic #370); **#376** (N-6). Frozen artifacts read: `src/MMLib.Alvo.Abstractions/Events/{IOutboxStore,IDeliveryStore,AlvoFrameworkEvent,AlvoEventProvenance}.cs`, `Identity/{AlvoFlow,IAlvoFlowUnit,AlvoContext}.cs`, `Management/{IAlvoOperations,ManagementExtension,AlvoExtensionKind,AlvoExtensionStage}.cs`; `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{OutboxTable,DeliveryTable,WriteUnit,EfAlvoData.Units}.cs`, `EfCoreCommandLedger.cs`. `schema/project.schema.json` does not move (host-code feature).

**Prior art** (CLAUDE.md *Study the problem*): **Cronos** README, *Daylight Saving Time* — a time in the spring-forward gap runs at the end of the gap; in the fall-back overlap a non-interval expression fires once (the first, DST offset), an interval/wildcard-hour expression fires in both; `GetNextOccurrence(from, zone)` is exclusive. **PostgreSQL READ COMMITTED** `UPDATE`: a blocked row is re-checked against the outer `WHERE` only (EvalPlanQual), never the subquery — the outbox's `ClaimSql` remarks. **Transactional outbox**: the state change and the message commit in one local transaction. **Durable timers** (Durable Functions `CreateTimer`, Temporal timers): keyed, cancellable, re-armable; a fired timer is a message, not a callback. **Misfire policy** (Quartz `FireOnceNow`, Hangfire's default): an occurrence missed while nothing ran fires once, the backlog does not.

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx13`. One writer per worktree; never `git add -A` (stage by path); run `git branch --show-current` before every commit and push. **Never `git clean`.** A bare `ls` hides git-ignored files — use `/bin/ls` or `find`.
- **Slices — each its own PR to `main`, each leaves `main` green and shippable:**
  - **A `hx/13a-timers`** (Tasks 1–4): the store, `IAlvoScheduler`, `TimerPump`. Before Task 1: `git branch -m hx/13-timers-schedules hx/13a-timers` (the plan commit is on it and lands in this PR).
  - **B `hx/13b-schedules`** (Tasks 5–7): `AddSchedule`, `SchedulePump`, job pool, `IAlvoTenantCatalog`, run now, the #42 repoint.
  - **C `hx/13c-data-migrations`** (Task 8).
  - **D `hx/13d-time-surfaces`** (Tasks 9–10): dashboard, docs page, skill, architecture doc, CHANGELOG, spec amendment, PLAN.md.
  - Each next slice: `git fetch origin && git switch -c hx/13<x>-… origin/main` once the previous PR merged; if it has not, branch from the previous slice and retarget the PR to `main` after it merges (rebase, never merge `main` in).
- **Parallel work:** HX-12 (`alvo_sequences`, exclusion/partial indexes) and HX-16 (reads) land independently. Do not use their symbols. Shared-file edits stay small and **append-only**: `AlvoFrameworkTables` (append the three suffixes at the end of the constants), `Directory.Packages.props` (one line), `CHANGELOG.md`, `docs/PLAN.md`, the spec, `PublicApi.*.verified.txt`, skills. On a rebase conflict in those files keep both sides.
- **Public surface added is exactly** (each item beyond §5 row `13` is a Decision below):
  - Abstractions `MMLib.Alvo.Scheduling` (slice A): `IAlvoScheduler`, `ITimerStore`, `TimerEntry` (sealed class, **internal** factory), `ClaimedTimer`, `TimerCounts`, `ScheduleDefinition`, `ClaimedSchedule`, `ScheduleAdvance`, `StoredSchedule`; (slice B) `IAlvoTenantCatalog`. Abstractions `MMLib.Alvo.Events` (slice A): `AlvoFrameworkEnvelope` (sealed, **internal** constructor — minted only by `AlvoFrameworkEvent.Create`).
  - Abstractions `MMLib.Alvo.Management`: (B) `AlvoExtensionKind.Schedule = 7`, `AlvoExtensionStage.Scheduled = 4`, `ManagementExtension.Cron`, `.TimeZone`, `.NextRunAt`, `.LastRunAt`, `.LastOutcome`, `ManagementExtensionCatalog.Timers`, `ManagementTimerCounts`; (C) `AlvoExtensionKind.DataMigration = 8`, `ManagementExtension.AfterRevision`.
  - Core `MMLib.Alvo.Scheduling`: (A) `AlvoSchedulerExtensions.ScheduleAsync<TMessage>`; (B) `IAlvoScheduledJob`, `AlvoJobContext`, `AlvoScheduleOptions`, `AlvoScheduleScope`; (C) `IAlvoDataMigration`. Core `MMLib.Alvo.Extensions`: (B) `AlvoDeliveryOptions.MaxJobConcurrency`. DI namespace `AlvoExtensionBuilderExtensions`: (B) `AddSchedule<TJob>`, (C) `AddDataMigration<TMigration>`.
  - Testing (A): `MMLib.Alvo.Testing.Scheduling.TimerStoreContractTests`, `ITimerStoreWorld`.
  - Everything else is `internal` — `EfCoreTimerStore` included (the `IDeliveryStore` precedent).
- **Wire constants (verbatim):** tables `<prefix>_timers`, `<prefix>_schedules`, `<prefix>_data_migrations`; event types `alvo.schedule.<id>` / `alvo.migration.<id>` with `-` → `_`; subjects `schedules/<id>`, `migrations/<id>`, `timers/<key>`; payload keys `schedule`/`migration`, `scheduledFor` (ISO-8601 UTC), `manual` (run now only); delivery kinds `schedule`, `migration`; timer key 1–200 chars, no control characters; claim leases: timers 1 min, schedules 1 min; job default timeout 15 min; `Alvo:Deliveries:MaxJobConcurrency` default 2.
- **Engine parity:** every data-path fact runs on SQLite and PostgreSQL — `TimerStoreContractTests` (`SqliteTimerStoreTests` in `test/MMLib.Alvo.Data.Sqlite.Tests`, `PostgreSqlTimerStoreTests` in `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration`) and the abstract `test/_shared/testhost/TimeFacts.cs` (`SqliteTimeTests` in `test/MMLib.Alvo.TestHost.Tests`, `PostgreSqlTimeTests` in the PostgreSQL integration project — the `TransactionFacts` pattern, one database per fact). The one PostgreSQL-only fact is acceptance (4) (SQLite serialises writers; the race does not exist there).
- **Deterministic tests:** `FakeTimeProvider` everywhere; no `Task.Delay`/`Thread.Sleep`; races are forced by ordering calls (claim, then reschedule, then fire), or by `Task.WhenAll` over a `Barrier` with outcome counts asserted, never timings.
- **Security core** (tenancy, depth, framework-event minting): the stack-level security review (checklist, `/security-review` substitute, `needs-deep-review`) runs once after slice D, outside this plan. Label each slice PR `needs-deep-review`.
- C# style per `alvo-dotnet-conventions`: no inline comments, methods ≲ 25 lines, `ConfigureAwait(false)` in `src/` only, `TestContext.Current.CancellationToken` in every test call, XML docs on every public member citing no `§`/`#issue`. `.cs` files are UTF-8 BOM + CRLF: after editing run `dotnet format whitespace MMLib.Alvo.slnx --include <file> …`.
- **Testing per task:** the affected test projects only — `dotnet test --project <proj> --filter-class '*<Class>'` — plus pre-commit's scoped ring0. `scripts/test-ring1` on every **[slice end]** task; `scripts/test-ring2` before each slice's PR. Admin e2e only in Task 9: `scripts/test-admin-e2e --filter Schedule`. No Stryker, no load run.
- Public-API baselines: `test/MMLib.Alvo.Abstractions.Tests` (Abstractions), `test/MMLib.Alvo.Tests` (core, Testing). Accept a `*.received.txt` only when its diff adds exactly the symbols above for that slice; the turn-review gate then asks for `alvo-snapshot-judge` and an `alvo-architecture-rules` justification per symbol.
- Commits: Conventional Commits, one per task, ending `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes`/`Fixes` keyword in any commit. PR bodies: A and C → `Part of #390`; **B** → `Part of #390` plus, on its own line, `Closes #376`; **D** → `Closes #390`.
- Each slice PR: ring2, `alvo-plan-guard`, the PR report (`alvo-pr-report`), label `needs-deep-review`.

## Decisions (the spec does not settle these)

1. **Generation is a fresh `Guid.CreateVersion7` per upsert**, not `generation + 1`: a cancel (delete) then re-schedule would restart at 1 and let a stale claim of the old row fire the new one early. Cost if wrong: none functional.
2. **Column `timer_key`**, not `key` (reserved in T-SQL; `TSqlSqlDialect` double). Cost: the spec's column list differs by one name.
3. **`ITimerStore` is defined complete in slice A** (timers, schedules, migrations, run now, counts) — an interface member added later breaks implementers (*HX-06 settled details* (10)). Cost: schedule/migration members have no caller for one PR.
4. **A timer's message is a custom event**: untyped `ScheduleAsync(key, at, eventType, data, tenant?)` + typed `ScheduleAsync<TMessage>` over `[AlvoEvent]`; fired as actor `System(scope)`, subject `timers/<key>`; an `alvo.*` type is refused (`EnsureCustom`). Cost: no CLR-typed payload envelope.
5. **Tenant** = the argument, else the open unit's caller tenant; required when the current schema has a `Scoped` entity; an argument differing from the open unit's tenant is refused. Cost: a cross-tenant timer from inside a unit is impossible (intended).
6. **A schedule/migration is a `HandlerRegistration` with a `JobSpec`** (one id, one delivery engine), `AlvoOrdering.None` so a dead run never blocks the next occurrence. Cost: two runs overlap if one outlasts the period.
7. **Missed occurrences fire once, then resume from `max(now, scheduledFor)`** (no backlog). Cost: a host down for a week runs a daily job once, not seven times.
8. **`alvo_schedules` gains `cron`, `time_zone`; drops `last_outcome`** (read from the newest delivery). Sync runs once at host start: a changed cron/zone recomputes `next_run_at`, an id no longer registered is deleted. Cost: an occurrence leased across a redeploy that changed the cron does not run; during a rolling deploy a replica still registering a removed schedule re-creates its row until it stops.
   - **Amended by ruling R5 (slice B review, I-1):** a schedule row is never pruned by absence. Each process upserts only the schedules it binds, deletes only ids it declares with `AddRetiredExtension`, and claims only the ids it binds (`ITimerStore.SyncSchedulesAsync(schedules, retired)`, `ClaimSchedulesAsync(claimant, ids, lease)`), so a web host without schedules neither wipes nor runs a worker's, and an orphaned row runs nowhere. Cost: a schedule removed from the code without `AddRetiredExtension` leaves its row behind (it never runs, and the catalog does not list it).
   - **Deviation (slice B, Task 6):** "sync runs once at host start" became "once per process, at start or on the first round". `StartAsync` reads the boot phase rather than awaiting it (awaiting hung hosts whose boot never settles), and a start that is not `Ready` with a primed catalog, or whose sync throws, leaves the sync to the first round that has a catalog. Cost: none functional; a host primed later by an apply syncs on its first round instead of never.
9. **Cron = Cronos `CronFormat.Standard` (5 fields)**, zone = `TimeZoneInfo.FindSystemTimeZoneById` (IANA), both checked at boot with every problem at once. Cost: no sub-minute schedules (use a timer).
10. **Data migration — deviation from §4.10** ("as `System(null)` iterating tenants"): event `alvo.migration.<id>` (E12 extended), **one run per tenant** listed by `IAlvoTenantCatalog` when the schema has a `Scoped` entity, else one run with `Tenant = null`, so no run reads another tenant; tenants created after the start never get the migration. No catalog: refused at boot when the boot schema is scoped; a schema that becomes scoped by a later apply is guarded at run time (logged `TenantCatalogMissing`, skipped, not marked, retried each round). No `configure` overload (15 min, default retry); started by `SchedulePump` polling the applied revision — no apply hook. Cost: a migration needing a cross-tenant view cannot have one; up to one poll interval between an apply and the start.
11. **Dashboard data rides appended init members** (`ManagementExtension.Cron…LastOutcome`, `ManagementExtensionCatalog.Timers`) — no `IAlvoOperations` member. Cost: the catalog read does two more store reads.
12. **Run now on `EachTenant`** creates one run per tenant and returns the first tenant's delivery; fan-out is immediate (`IDeliveryStore.FanOutAsync`, idempotent with the dispatcher's later fan-out). Cost: the answer names one of n runs.
13. **A self-re-arming loop is stopped by fan-out** (dead `chain-depth-exceeded`, as HX-03 stops event loops); handlers never run above depth 8, so `ScheduleAsync`'s own refusal is pinned at an ambient depth of 9. Cost: none.
14. **#376 closes with slice B; #390 with slice D.**
15. **`ITimerStore` never takes a bare `AlvoEvent`** (R-7, E12): timer fires take an `AlvoCustomEvent` (the custom door refuses `alvo.*`); schedule/migration/run-now writes take `AlvoFrameworkEnvelope`, which only `AlvoFrameworkEvent.Create` can mint (its return type changes; `CommandCompletion`/`IAlvoCommandUnit` follow); `TimerEntry` is built only by the core's writer, so provenance and depth are never caller-controlled. Cost: a third-party provider reads these types but cannot build them outside its tests.

## Review Focus

1. **Cancel, then re-schedule the same key while a stale claim is in flight** — the stale `FireAsync` must find nothing. `TimerStoreContractTests.A_stale_claim_cannot_fire_a_timer_cancelled_and_rescheduled_under_the_same_key` (Task 1).
2. **The same key in two tenants is two timers**; tenant A's cancel leaves B's. `TimerStoreContractTests.The_same_key_in_two_scopes_is_two_timers` (Task 1).
3. **A timer whose `at` is already past** (or carries a non-UTC offset) fires on the next round, stored as UTC. `TimeFacts.A_timer_due_in_the_past_fires_on_the_next_drain` (Task 4).
4. **A redeploy with a changed cron** moves the stored next run. `TimerStoreContractTests.Syncing_a_changed_cron_moves_the_next_run_and_an_unchanged_one_keeps_it` (Task 2).
5. **A failing scheduled job** retries, dead-letters, and the next occurrence still runs. `TimeFacts.A_failing_run_does_not_block_the_next_occurrence` (Task 6).

## File structure

**Abstractions** `src/MMLib.Alvo.Abstractions/Scheduling/` (new): `IAlvoScheduler.cs`, `ITimerStore.cs`, `TimerStoreRecords.cs`, `IAlvoTenantCatalog.cs` (B). `AlvoFrameworkTables.cs` (+3 suffixes). `Management/{AlvoExtensionKind,AlvoExtensionStage,ManagementExtension,ManagementExtensionCatalog}.cs`, `Management/ManagementTimerCounts.cs` (B).
**Core** `src/MMLib.Alvo/Scheduling/` (new slice): `Setup.cs`, `AlvoScheduler.cs`, `TimerPump.cs`, `AlvoSchedulerExtensions.cs`; (B) `IAlvoScheduledJob.cs`, `AlvoJobContext.cs`, `AlvoScheduleOptions.cs`, `AlvoScheduleScope.cs`, `CronSchedule.cs`, `JobSpec.cs`, `SchedulePump.cs`, `ScheduleRuns.cs`, `ScheduleChecks.cs`; (C) `IAlvoDataMigration.cs`, `DataMigrationStarter.cs`. Modified: `Events/Internal/{EventPump,DeliveryDispatcher,DeliveryFanOut,DeliveryVocabulary}.cs`, `Extensions/Internal/{ExtensionRegistration,ExtensionBindingCompiler,ExtensionCatalog,HandlerInvoker}.cs`, `Extensions/{AlvoExtensionBuilderExtensions,AlvoDeliveryOptions}.cs`, `Management/Internal/AlvoOperationsService.Writes.cs`, `AlvoServiceCollectionExtensions.cs`, `MMLib.Alvo.csproj`.
**EF driver**: `Internal/{TimerTable,ScheduleTable,DataMigrationTable}.cs`, `Internal/EfCoreTimerStore.cs` (new); `AlvoEfCoreProvider.cs`, `Internal/EfAlvoData.Units.cs` (one internal entry).
**Analyzers**: `AlvoSymbols.cs` (allow `IAlvoScheduler`).
**Testing**: `src/MMLib.Alvo.Testing/Scheduling/{TimerStoreContractTests,TimerStoreContractTests.Schedules,ITimerStoreWorld}.cs`.
**Tests**: `test/_shared/ef/TimerStoreWorld.cs`; `test/_shared/testhost/TimeFacts.cs` (+ `TimeFacts.Schedules.cs` B, `TimeFacts.Migrations.cs` C); engine subclasses; `test/MMLib.Alvo.Tests/Scheduling/{AlvoSchedulerTests,CronScheduleTests,ScheduleRegistrationTests,FrameworkEventMintingTests}.cs`; `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlScheduleRaceTests.cs`; `test/MMLib.Alvo.Admin.Tests.EndToEnd/ScheduleScenarios.cs` (D).
**Admin** (D): `Components/Operations/{Operations.razor,Operations.razor.cs,OperationsLoader.cs,OperationsView.cs}`, `Internal/OperationsGateway.cs`.
**Docs** (D): `website/src/content/docs/extending/time.mdx`, `website/src/snippets/extending/time/{booking-reminders.alvo.json,BookingReminders.cs,BookingRemindersTests.cs}`; `docs/architecture/time.md` (new), `docs/architecture/{analyzers,package-boundary,extensibility}.md`; `plugins/alvo/skills/alvo-descriptor-hooks/SKILL.md`; `CHANGELOG.md`; spec; `docs/PLAN.md`.

---

# Slice A — `hx/13a-timers`

## Task 1: `ITimerStore` and the timer table — upsert, cancel, claim, fire in one commit

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Scheduling/{ITimerStore,TimerStoreRecords,TimerEntry,TimerProvenance}.cs`, `src/MMLib.Alvo.Abstractions/Events/AlvoFrameworkEnvelope.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{TimerTable,EfCoreTimerStore}.cs`, `src/MMLib.Alvo.Testing/Scheduling/{ITimerStoreWorld,TimerStoreContractTests}.cs`, `test/_shared/ef/TimerStoreWorld.cs`, `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteTimerStoreTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTimerStoreTests.cs`, `test/MMLib.Alvo.Abstractions.Tests/Scheduling/TimerStorePortTests.cs`
- Modify: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs`, `src/MMLib.Alvo.Abstractions/Events/AlvoFrameworkEvent.cs` (returns `AlvoFrameworkEnvelope`), `src/MMLib.Alvo.Abstractions/Data/IAlvoCommandUnit.cs` (`PublishFrameworkAsync(AlvoFrameworkEnvelope, …)`), `src/MMLib.Alvo/Commands/Internal/CommandCompletion.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{EfAlvoTransaction,EfAlvoData.Units,SystemSchemaInitializer}.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`, `test/MMLib.Alvo.Tests/Events/AlvoFrameworkEventTests.cs`, `test/_shared/ef/LedgerFacts.cs` (`.Envelope` where they read the event), both engine test `.csproj` (link `TimerStoreWorld.cs`), the Abstractions and Testing public-API baselines.

**Interfaces — Produces** (namespace `MMLib.Alvo.Scheduling`; the schedule/migration members are declared here and implemented in Task 2):

```csharp
namespace MMLib.Alvo.Events;
public sealed class AlvoFrameworkEnvelope            // an event of the reserved alvo namespace, built only by Alvo
{
    internal AlvoFrameworkEnvelope(AlvoEvent envelope) => Envelope = envelope;
    public AlvoEvent Envelope { get; }
}
// AlvoFrameworkEvent.Create(...) now returns AlvoFrameworkEnvelope (it is the only caller of the constructor).

namespace MMLib.Alvo.Scheduling;
public sealed class TimerEntry                       // built only by the core's scheduler (IVT); a provider reads it
{
    internal static TimerEntry Of(string scope, string key, DateTimeOffset dueAt, AlvoCustomEvent message, AlvoProvenance provenance);
    public string Scope { get; }          // tenant id text, or "" for none
    public string Key { get; }
    public DateTimeOffset DueAt { get; }  // UTC
    public string MessageType { get; }    // message.Envelope.Type — custom only, by construction
    public string Payload { get; }        // the message's data through AlvoEventJson
    public string Provenance { get; }     // TimerProvenance.Write(provenance), opaque to the store
}
internal static class TimerProvenance { internal static string Write(AlvoProvenance p); internal static AlvoProvenance Read(string json); } // malformed → None
public sealed record ClaimedTimer
{
    public required string Scope { get; init; }  public required string Key { get; init; }
    public required Guid Generation { get; init; }  public required DateTimeOffset DueAt { get; init; }
    public required string MessageType { get; init; }  public required string Payload { get; init; }
    public required string Provenance { get; init; }
}
public sealed record TimerCounts { public required int Pending { get; init; } public required int Due { get; init; } }
public sealed record ScheduleDefinition
{ public required string Id { get; init; } public required string Cron { get; init; } public required string TimeZone { get; init; } public required DateTimeOffset NextRunAt { get; init; } }
public sealed record ClaimedSchedule { public required string Id { get; init; } public required DateTimeOffset ScheduledFor { get; init; } }
public sealed record ScheduleAdvance
{
    public required string Id { get; init; }  public required string Claimant { get; init; }
    public required DateTimeOffset ScheduledFor { get; init; }  public required DateTimeOffset NextRunAt { get; init; }
    public required IReadOnlyList<AlvoFrameworkEnvelope> Events { get; init; }
}
public sealed record StoredSchedule
{ public required string Id { get; init; } public required string Cron { get; init; } public required string TimeZone { get; init; } public required DateTimeOffset NextRunAt { get; init; } public DateTimeOffset? LastRunAt { get; init; } }

public interface ITimerStore
{
    Task EnsureAsync(CancellationToken cancellationToken = default);
    Task<Guid> UpsertAsync(TimerEntry timer, CancellationToken cancellationToken = default);       // enlists in the flow's unit when it owns it
    Task<bool> CancelAsync(string scope, string key, CancellationToken cancellationToken = default); // enlists likewise
    Task<IReadOnlyList<ClaimedTimer>> ClaimTimersAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<bool> FireAsync(string scope, string key, Guid generation, AlvoCustomEvent @event, CancellationToken cancellationToken = default);
    Task<TimerCounts> CountTimersAsync(CancellationToken cancellationToken = default);
    Task SyncSchedulesAsync(IReadOnlyList<ScheduleDefinition> schedules, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClaimedSchedule>> ClaimSchedulesAsync(string claimant, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<bool> AdvanceScheduleAsync(ScheduleAdvance advance, CancellationToken cancellationToken = default);
    Task AppendRunAsync(IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredSchedule>> SchedulesAsync(CancellationToken cancellationToken = default);
    Task<bool> StartMigrationAsync(string id, int revision, IReadOnlyList<AlvoFrameworkEnvelope> events, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> StartedMigrationsAsync(CancellationToken cancellationToken = default);
}
```

`SyncSchedulesAsync` upserts the given schedules **and deletes every stored id not among them** (an empty list empties the table). XML docs state the contract each test below pins: `FireAsync` deletes the row only at that generation and appends the event in the same transaction, returning `false` (and appending nothing) when the row moved, was cancelled, or was already fired; a provider implements `ITimerStore` and `IOutboxStore` over one database.

`ITimerStoreWorld` (Testing): `ITimerStore Store`, `DateTimeOffset Now`, `void Advance(TimeSpan)`, `Task<IReadOnlyList<string>> OutboxTypesAsync()` (types appended, in id order), `Task BreakOutboxAsync()` (drops the outbox table so the next append fails), `Task<IReadOnlyList<(string Id, int Revision)>> MigrationRowsAsync()`.

- [ ] **Step 1: Write the failing contract tests** in `TimerStoreContractTests` (abstract, `protected abstract Task<ITimerStoreWorld> WorldAsync()`, `protected virtual void EnsureEngineAvailable()` — the `DeliveryStoreContractTests` shape):

```csharp
[Fact]
public async Task A_due_timer_is_claimed_once_and_fired_once()
{
    EnsureEngineAvailable();
    await using var world = await WorldAsync();
    await world.Store.UpsertAsync(Timer("t-1", world.Now.AddMinutes(5)), Ct);

    (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldBeEmpty();
    world.Advance(TimeSpan.FromMinutes(5));
    var claimed = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();
    (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldBeEmpty();

    (await world.Store.FireAsync(claimed.Scope, claimed.Key, claimed.Generation, Event("reminders.due"), Ct)).ShouldBeTrue();
    (await world.Store.FireAsync(claimed.Scope, claimed.Key, claimed.Generation, Event("reminders.due"), Ct)).ShouldBeFalse();
    (await world.OutboxTypesAsync()).ShouldBe(["reminders.due"]);
}

[Fact]
public async Task A_reschedule_during_a_claim_fences_the_stale_fire_and_the_new_generation_fires_once()
{
    EnsureEngineAvailable();
    await using var world = await WorldAsync();
    await world.Store.UpsertAsync(Timer("t-1", world.Now), Ct);
    var stale = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();

    await world.Store.UpsertAsync(Timer("t-1", world.Now.AddHours(1)), Ct);
    (await world.Store.FireAsync(stale.Scope, stale.Key, stale.Generation, Event("reminders.due"), Ct)).ShouldBeFalse();
    (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldBeEmpty();
    world.Advance(TimeSpan.FromHours(1));
    var fresh = (await world.Store.ClaimTimersAsync(10, _lease, Ct)).ShouldHaveSingleItem();

    fresh.DueAt.ShouldBe(world.Now);
    (await world.Store.FireAsync(fresh.Scope, fresh.Key, fresh.Generation, Event("reminders.due"), Ct)).ShouldBeTrue();
    (await world.OutboxTypesAsync()).ShouldHaveSingleItem();
}
```

Also, each in the same style: `A_cancel_during_a_claim_leaves_the_fire_nothing_to_append` (claim, `CancelAsync` → `true`, fire → `false`, outbox empty); `A_stale_claim_cannot_fire_a_timer_cancelled_and_rescheduled_under_the_same_key` (claim gen g1, cancel, upsert same key due in 1 h, fire(g1) → `false`, the row is still there: `CountTimersAsync().Pending == 1`); `The_same_key_in_two_scopes_is_two_timers` (scopes `"a"`, `"b"`; cancel `"a"` → `b` still claimable); `A_fire_removes_the_timer_and_appends_its_event_in_one_commit` (after fire: `Pending == 0`, outbox has the event with its id); `A_fire_whose_outbox_append_fails_leaves_the_timer_in_place` (`BreakOutboxAsync`, fire throws, then `Pending == 1` and after the lease runs out the timer is claimable again with the same generation); `An_expired_lease_makes_a_timer_claimable_again` (advance past `_lease`); `A_claim_takes_the_earliest_due_first_and_at_most_the_batch` (3 due, batch 2 → the two earliest); `Counts_split_pending_from_due` (`Pending` = all rows, `Due` = `due_at <= now`); `A_cancel_of_an_unknown_key_answers_false`. Helpers: `Timer(key, due)` = `TimerEntry.Of("", key, due, Custom("reminders.due"), AlvoProvenance.None)`; `Event(type)` = an `AlvoCustomEvent` built through `AlvoCustomEnvelope.Build`; `Framework(type)` = `AlvoFrameworkEvent.Create(…)` — the Testing package reaches these through Abstractions' existing `InternalsVisibleTo("MMLib.Alvo.Testing")`.

And the port pin (`TimerStorePortTests`, Abstractions.Tests, ruling R-7):

```csharp
[Fact]
public void A_host_built_schedule_event_cannot_reach_the_outbox_through_ITimerStore()
{
    var eventParameters = typeof(ITimerStore).GetMethods().SelectMany(method => method.GetParameters())
        .Select(parameter => parameter.ParameterType).Concat(typeof(ScheduleAdvance).GetProperties().Select(property => property.PropertyType));

    eventParameters.ShouldNotContain(typeof(AlvoEvent));
    eventParameters.ShouldNotContain(typeof(IReadOnlyList<AlvoEvent>));
    typeof(AlvoFrameworkEnvelope).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
    typeof(AlvoFrameworkEnvelope).GetMethods(BindingFlags.Public | BindingFlags.Static).ShouldBeEmpty();
    typeof(TimerEntry).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
    Should.Throw<ArgumentException>(() => AlvoCustomEvent.Create(HostBuilt("alvo.schedule.nightly_roll")));
}
```

(`HostBuilt` = a public `new AlvoEvent { … }` with that type, the way a host would build one.)

- [ ] **Step 2: Run to verify they fail** — `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteTimerStoreTests'`. Expected: compile failure (`ITimerStore` missing).

- [ ] **Step 3: Implement.** `AlvoFrameworkTables`: append `TimersSuffix = "_timers"`, `SchedulesSuffix = "_schedules"`, `DataMigrationsSuffix = "_data_migrations"` (and add them wherever `NamesFor` enumerates the reserved names). `TimerTable` — identical SQL on both engines; every instant is `StoredInstant.Text` (sortable text):

```csharp
internal static class TimerTable
{
    private const string Claimable = "(due_at <= @now AND (lease_until IS NULL OR lease_until <= @now))";

    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.TimersSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            scope TEXT NOT NULL,
            timer_key TEXT NOT NULL,
            generation TEXT NOT NULL,
            due_at TEXT NOT NULL,
            message_type TEXT NOT NULL,
            payload TEXT NOT NULL,
            provenance TEXT NOT NULL,
            lease_until TEXT NULL,
            PRIMARY KEY (scope, timer_key)
        )
        """;

    internal static string DueIndexDdl(string table) => $"CREATE INDEX IF NOT EXISTS ix_{table}_due ON {table} (due_at)";

    internal static string UpsertSql(string table) =>
        $"""
        INSERT INTO {table} (scope, timer_key, generation, due_at, message_type, payload, provenance, lease_until)
        VALUES (@scope, @key, @generation, @due_at, @message_type, @payload, @provenance, NULL)
        ON CONFLICT (scope, timer_key) DO UPDATE SET
            generation = excluded.generation, due_at = excluded.due_at, message_type = excluded.message_type,
            payload = excluded.payload, provenance = excluded.provenance, lease_until = NULL
        """;

    internal static string CancelSql(string table) => $"DELETE FROM {table} WHERE scope = @scope AND timer_key = @key";

    internal static string ClaimSql(string table) =>
        $"""
        UPDATE {table} SET lease_until = @lease_until
         WHERE {Claimable}
           AND (scope, timer_key) IN (SELECT scope, timer_key FROM {table}
                                       WHERE {Claimable}
                                       ORDER BY due_at, scope, timer_key
                                       LIMIT @batch)
        RETURNING scope, timer_key, generation, due_at, message_type, payload, provenance
        """;

    internal static string FireSql(string table) =>
        $"DELETE FROM {table} WHERE scope = @scope AND timer_key = @key AND generation = @generation";

    internal static string CountSql(string table) =>
        $"SELECT COUNT(*), COALESCE(SUM(CASE WHEN due_at <= @now THEN 1 ELSE 0 END), 0) FROM {table}";
}
```

`ClaimSql`'s remark (XML doc on the method) repeats the outbox's argument: the outer `WHERE` repeats `Claimable` because PostgreSQL's EvalPlanQual re-check runs only the outer predicate, so a second claimant woken on a row another claimant just leased sees `lease_until > @now` and skips it. An upsert clears `lease_until`, so a moved timer is claimable at its new time even while the old claim's lease runs.

**The tables are created at boot**, beside the deliveries: `SystemSchemaInitializer.EnsureAsync` creates `TimerTable.Ddl` through `IfNotExistsDdl.CreateTableAsync` and `DueIndexDdl` through `CreateIndexIfMissingAsync` (Task 2 adds `ScheduleTable`/`DataMigrationTable` there). `EfCoreTimerStore` (internal, `ITimerStore`): constructor `(RelationalConnectionFactory connections, AlvoOptions options, TimeProvider time, Func<EfAlvoData> data)`; table names from `options.SchemaPrefix`; `EnsureAsync` re-runs the same `IfNotExistsDdl` calls inside the write gate — idempotent, a no-op once boot created them, there for a store started without a boot (the contract worlds). Every write runs through one gated-transaction helper copied from `EfCoreCommandLedger` (open → `WriteGate.EnterAsync` → `BeginTransactionAsync` → work → commit when it says so). The fire:

```csharp
public Task<bool> FireAsync(string scope, string key, Guid generation, AlvoCustomEvent @event, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(@event);
    return InTransactionAsync(
        async (connection, transaction, ct) =>
            await ExecuteAsync(connection, transaction, TimerTable.FireSql(_timers), command => BindKey(command, scope, key, generation), ct).ConfigureAwait(false) == 1
            && await AppendedAsync(connection, transaction, [@event.Envelope], ct).ConfigureAwait(false),
        committed => committed,
        cancellationToken);
}

private async Task<bool> AppendedAsync(DbConnection connection, DbTransaction transaction, IEnumerable<AlvoEvent> events, CancellationToken ct)
{
    foreach (var @event in events)
    {
        await OutboxTable.InsertAsync(connection, transaction, _outbox, @event, ct).ConfigureAwait(false);
    }

    return true;
}
```

`AlvoFrameworkEnvelope` and the `AlvoFrameworkEvent.Create` return change land here (callers read `.Envelope`; `IAlvoCommandUnit.PublishFrameworkAsync` and `EfAlvoData.PublishFrameworkInUnitAsync` take the envelope type). `TimerEntry.Of` checks the key (1–200 chars, no control characters), takes `MessageType`/`Payload` from the custom event (payload = its data written by `AlvoEventJson`, the writer `AlvoCustomEnvelope.EnsureWritable` probes with) and `Provenance` from `TimerProvenance.Write`. `UpsertAsync` mints `Guid.CreateVersion7(_time.GetUtcNow())`, binds `due_at = StoredInstant.Text(timer.DueAt)`, and routes through `WriteAsync(write)` — which Task 3 teaches to enlist; here it is the gated transaction. `ClaimTimersAsync` binds `@now`, `@lease_until = now + lease`, `@batch` and reads with `RelationalSqlBatch.ReadWrittenRowsAsync`. Register in `AlvoEfCoreProvider`: `builder.Services.TryAddSingleton<ITimerStore>(CreateTimerStore)` beside `IDeliveryStore` (`Func<EfAlvoData>` = `services.GetRequiredService<EfAlvoData>`). Schedule/migration members throw `NotImplementedException` until Task 2 (nothing calls them yet).

`TimerStoreWorld` (`test/_shared/ef/`, the `DeliveryStoreWorld` shape) builds the store over a SQLite file or a PostgreSQL database, a `FakeTimeProvider` at `2026-11-06T09:00Z`; `BreakOutboxAsync` runs `DROP TABLE <outbox>`. Engine subclasses follow `SqliteDeliveryStoreTests` / `PostgreSqlDeliveryStoreTests` (skip when Docker is unavailable).

- [ ] **Step 4: Run** both engine classes, `'*TimerStorePortTests'`, `'*AlvoFrameworkEventTests'`, and the ledger/command classes that read framework events (`'*LedgerTests'` on both engines, `test/MMLib.Alvo.Tests --filter-class '*Command*'`); expected PASS. Then `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'` and the Testing baseline in `test/MMLib.Alvo.Tests`; accept only the Task-1 symbols.
- [ ] **Step 5: Commit** — `feat(scheduling): ITimerStore with a fenced, single-commit timer fire on both engines`.

## Task 2: Schedules and data migrations in the store — sync, lease, advance once, start once

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{ScheduleTable,DataMigrationTable}.cs`, `src/MMLib.Alvo.Testing/Scheduling/TimerStoreContractTests.Schedules.cs`
- Modify: `EfCoreTimerStore.cs`, `Internal/SystemSchemaInitializer.cs` (the two tables via `IfNotExistsDdl`), `test/_shared/ef/TimerStoreWorld.cs`

**Interfaces:** Consumes Task 1's records. Produces working `SyncSchedulesAsync`, `ClaimSchedulesAsync`, `AdvanceScheduleAsync`, `AppendRunAsync`, `SchedulesAsync`, `StartMigrationAsync`, `StartedMigrationsAsync`.

- [ ] **Step 1: Failing contract tests** (partial class `TimerStoreContractTests`):

```csharp
[Fact]
public async Task Racing_claimants_advance_one_occurrence_once()
{
    EnsureEngineAvailable();
    await using var world = await WorldAsync();
    await world.Store.SyncSchedulesAsync([Schedule("nightly", world.Now)], Ct);
    using var start = new Barrier(3);

    var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(index => Task.Run(async () =>
    {
        start.SignalAndWait(Ct);
        var claimed = await world.Store.ClaimSchedulesAsync($"worker-{index}", _lease, Ct);
        return claimed.Count == 1 && await world.Store.AdvanceScheduleAsync(Advance($"worker-{index}", claimed[0], world.Now.AddDays(1)), Ct);
    }, Ct)));

    outcomes.Count(advanced => advanced).ShouldBe(1);
    (await world.OutboxTypesAsync()).ShouldBe(["alvo.schedule.nightly"]);
    (await world.Store.SchedulesAsync(Ct)).ShouldHaveSingleItem().NextRunAt.ShouldBe(world.Now.AddDays(1));
}
```

Also: `An_advance_by_a_claimant_whose_lease_was_taken_over_changes_nothing` (claim by w1, advance past lease, claim by w2, w1's advance → `false`, w2's → `true`, one event); `An_advance_whose_outbox_append_fails_leaves_the_occurrence_due` (`BreakOutboxAsync`, advance throws, `NextRunAt` unchanged); `An_advance_appends_one_event_per_tenant_in_one_commit` (`Events` of three → three outbox rows, `LastRunAt == ScheduledFor`); `Syncing_a_changed_cron_moves_the_next_run_and_an_unchanged_one_keeps_it` (sync `0 2 * * *`/next t1, sync same with next t2 → stays t1; sync `0 3 * * *`/t3 → t3); `Syncing_drops_schedules_no_longer_registered` (sync `a`, `b`; sync `a` → `SchedulesAsync` lists `a` only; sync `[]` → empty); `A_schedule_not_yet_due_is_not_claimed`; `A_run_now_appends_its_events_without_moving_the_schedule`; `A_migration_starts_once_and_appends_its_events_with_its_mark` (second `StartMigrationAsync` same id, any revision → `false`, one event; `StartedMigrationsAsync() == ["backfill"]`; `MigrationRowsAsync() == [("backfill", 3)]`); `A_migration_whose_outbox_append_fails_is_not_marked`.

- [ ] **Step 2: Run** — expected FAIL (`NotImplementedException`).

- [ ] **Step 3: Implement** — `ScheduleTable`:

```csharp
internal static string Ddl(string table) =>
    $"""
    CREATE TABLE IF NOT EXISTS {table} (
        id TEXT NOT NULL PRIMARY KEY,
        cron TEXT NOT NULL,
        time_zone TEXT NOT NULL,
        next_run_at TEXT NOT NULL,
        lease_until TEXT NULL,
        leased_by TEXT NULL,
        last_run_at TEXT NULL
    )
    """;

internal static string SyncSql(string table) =>
    $"""
    INSERT INTO {table} (id, cron, time_zone, next_run_at) VALUES (@id, @cron, @time_zone, @next_run_at)
    ON CONFLICT (id) DO UPDATE SET cron = excluded.cron, time_zone = excluded.time_zone, next_run_at = excluded.next_run_at
     WHERE {table}.cron <> excluded.cron OR {table}.time_zone <> excluded.time_zone
    """;

private const string Claimable = "(next_run_at <= @now AND (lease_until IS NULL OR lease_until <= @now))";

internal static string ClaimSql(string table) =>
    $"""
    UPDATE {table} SET lease_until = @lease_until, leased_by = @claimant
     WHERE {Claimable}
       AND id IN (SELECT id FROM {table} WHERE {Claimable} ORDER BY next_run_at, id)
    RETURNING id, next_run_at
    """;

internal static string AdvanceSql(string table) =>
    $"""
    UPDATE {table} SET next_run_at = @next_run_at, last_run_at = @scheduled_for, lease_until = NULL, leased_by = NULL
     WHERE id = @id AND leased_by = @claimant AND next_run_at = @scheduled_for
    """;
```

`SyncSchedulesAsync`, in one gated transaction: `SyncSql` per definition, then `DELETE FROM {t} WHERE id NOT IN (@id0, @id1, …)` — or `DELETE FROM {t}` when the list is empty. `AdvanceScheduleAsync` = the Task 1 helper: `AdvanceSql` == 1 → `AppendedAsync(advance.Events.Select(e => e.Envelope))` → commit; else no commit. The fence is `leased_by` **and** `next_run_at = @scheduled_for`: a claimant whose lease expired and was re-claimed fails on `leased_by`; a sync that moved the occurrence fails on `next_run_at`. `DataMigrationTable`: `CREATE TABLE IF NOT EXISTS {t} (id TEXT NOT NULL PRIMARY KEY, revision INTEGER NOT NULL, started_at TEXT NOT NULL)`; `StartSql` = `INSERT INTO {t} (id, revision, started_at) VALUES (@id, @revision, @started_at) ON CONFLICT (id) DO NOTHING`; `StartMigrationAsync` = insert == 1 → append → commit. `AppendRunAsync` = append only, one transaction. `SystemSchemaInitializer.EnsureAsync` and `EfCoreTimerStore.EnsureAsync` now create all three tables.

- [ ] **Step 4: Run** both engines; PASS.
- [ ] **Step 5: Commit** — `feat(scheduling): schedules advance one occurrence once and migrations start once, in the outbox's commit`.

## Task 3: `IAlvoScheduler` — tenant, provenance, depth, and joining the open unit

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Scheduling/IAlvoScheduler.cs`, `src/MMLib.Alvo/Scheduling/{Setup,AlvoScheduler,AlvoSchedulerExtensions}.cs`, `test/MMLib.Alvo.Tests/Scheduling/AlvoSchedulerTests.cs`, `test/_shared/testhost/TimeFacts.cs`, `test/MMLib.Alvo.TestHost.Tests/SqliteTimeTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTimeTests.cs`, `test/_shared/testhost/descriptors/booking-desk.alvo.json` (entity `bookings`: `starts_at` datetime, `note` text, tenancy scoped; rule `true` for role `staff`)
- Modify: `src/MMLib.Alvo.Abstractions/Identity/IAlvoFlowUnit.cs` (internal — gains `AlvoContext Caller { get; }`; `WriteUnit` already has it) and its four test doubles: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WriteGateUnitRefusalTests.cs` (`OpenUnit`), `…/UnitSavepointJoinTests.cs` (`OtherUnit`), `…/EfAlvoDataUnitRefusalTests.cs` (`OpenUnit`), `test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs`; `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{EfAlvoData.Units,EfCoreTimerStore}.cs`, `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs`, `src/MMLib.Alvo.Analyzers/AlvoSymbols.cs`, `test/MMLib.Alvo.Analyzers.Tests/Alvo1001AwaitTests.cs`, public-API baselines.

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Scheduling;
public interface IAlvoScheduler
{
    Task ScheduleAsync(string key, DateTimeOffset at, string eventType, IReadOnlyDictionary<string, object?>? data,
        TenantId? tenant = null, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(string key, TenantId? tenant = null, CancellationToken cancellationToken = default);
}
public static class AlvoSchedulerExtensions   // core
{
    public static Task ScheduleAsync<TMessage>(this IAlvoScheduler scheduler, string key, DateTimeOffset at, TMessage message,
        TenantId? tenant = null, CancellationToken cancellationToken = default) where TMessage : class;
}
internal Task EfAlvoData.InFlowUnitAsync(WriteUnit unit, Func<DbConnection, DbTransaction, CancellationToken, Task> write, CancellationToken ct);
```

- [ ] **Step 1: Failing tests.** `AlvoSchedulerTests` (core, `AlvoScheduler` over a recording `ITimerStore` double and a `FakeTimeProvider`): `A_framework_event_type_cannot_be_scheduled` (`"alvo.schedule.x"` → `ArgumentException`, nothing stored); `Scheduling_above_the_depth_limit_is_refused` (`using var flow = AlvoFlow.Enter(new AlvoProvenance { Depth = 9 })` → `AlvoChainDepthExceededException`; at depth 8 it stores); `A_timer_outside_any_flow_records_depth_zero_and_a_fresh_correlation`; `A_scoped_schema_requires_the_tenant_outside_a_unit` (catalog with a `Scoped` entity, no tenant → `ArgumentException` naming the fix *pass tenant:*); `A_key_must_be_one_to_two_hundred_printable_characters`; `The_typed_overload_takes_the_event_type_from_AlvoEvent_and_the_payload_from_the_record_shape`. In `TimeFacts` (abstract, `protected abstract Task<TimeDatabase> DatabaseAsync()`, the `TransactionFacts` pattern), the unit facts:

```csharp
[Fact]
public async Task A_timer_scheduled_in_a_rolled_back_unit_leaves_no_row()
{
    await using var alvo = await StartAsync();
    var staff = Staff(Tenant);

    await Should.ThrowAsync<InvalidOperationException>(() => Transactions(alvo).TransactAsync(staff, async (unit, ct) =>
    {
        await Scheduler(alvo).ScheduleAsync("remind-1", _now.AddHours(24), "bookings.reminder_due", null, cancellationToken: ct);
        throw new InvalidOperationException("the host changed its mind");
    }, cancellationToken: Ct));

    (await TimerStore(alvo).CountTimersAsync(Ct)).Pending.ShouldBe(0);
}
```

and `A_timer_scheduled_in_a_committed_unit_takes_the_units_tenant` (the stored scope is the unit caller's tenant; a different `tenant:` argument inside the unit → `ArgumentException` and the unit rolls back). In `Alvo1001AwaitTests`: `Awaiting_the_scheduler_in_a_before_write_hook_is_not_reported`.

- [ ] **Step 2: Run** `--filter-class '*AlvoSchedulerTests'` (core), `'*SqliteTimeTests'`; expected compile FAIL.

- [ ] **Step 3: Implement.** `AlvoScheduler(ITimerStore store, IPolicyCatalogProvider catalogs, AlvoChainDepthLimit depth, TimeProvider time)` — registered singleton by `Scheduling/Setup.cs`, called from `AlvoServiceCollectionExtensions` beside the other slices' `Setup`:

```csharp
public async Task ScheduleAsync(string key, DateTimeOffset at, string eventType, IReadOnlyDictionary<string, object?>? data,
    TenantId? tenant = null, CancellationToken cancellationToken = default)
{
    var context = AlvoContext.System(ScopeOf(tenant));
    depth.EnsureWithin(context);
    var provenance = AlvoEventProvenance.Effective(context);
    var message = AlvoCustomEnvelope.Build(eventType, $"timers/{key}", data, context, time.GetUtcNow());
    await store.UpsertAsync(TimerEntry.Of(
        context.Tenant?.ToString() ?? string.Empty, key, at.ToUniversalTime(), message,
        provenance with { CorrelationId = provenance.CorrelationId ?? Guid.NewGuid().ToString("N") }), cancellationToken).ConfigureAwait(false);
}

private TenantId? ScopeOf(TenantId? tenant)
{
    var unitTenant = AlvoFlow.Unit?.Caller.Tenant;
    if (tenant is { } stated && unitTenant is { } open && stated != open)
    {
        throw TenantMismatch(stated, open);
    }

    return tenant ?? unitTenant ?? (TenancyOn() ? throw TenantRequired() : null);
}
```

(`AlvoCustomEnvelope.Build` runs `EnsureCustom` — an `alvo.*` type is refused — and `EnsureWritable`; the key is checked by `TimerEntry.Of`; provenance and depth come only from this writer, never from the caller. `TenancyOn()` = `catalogs.Current?` schema has an entity with `Tenancy == TenancyMode.Scoped`.) `CancelAsync` resolves the scope the same way. The typed extension reads `RecordShape.Of(typeof(TMessage)).EventType` (else `ArgumentException`, the `AddEventHandler<TMessage, THandler>` wording) and the record's values through the shape's existing writer.

**Joining the unit** (R-B1's one exception, R-8). `EfAlvoData.Units.cs` gains:

```csharp
internal Task InFlowUnitAsync(WriteUnit unit, Func<DbConnection, DbTransaction, CancellationToken, Task> write, CancellationToken cancellationToken) =>
    unit.RunAsync(token => InUnitAsync(unit, write, token), cancellationToken);
```

and `EfCoreTimerStore.WriteAsync`:

```csharp
private Task WriteAsync(Func<DbConnection, DbTransaction, CancellationToken, Task> write, CancellationToken ct) =>
    AlvoFlow.Unit is WriteUnit unit
        ? _data().InFlowUnitAsync(unit, write, ct)
        : InTransactionAsync(async (connection, transaction, token) => { await write(connection, transaction, token).ConfigureAwait(false); return true; }, _ => true, ct);
```

`UpsertAsync` and `CancelAsync` go through it, so the unit's savepoint, one-call-at-a-time rule and poisoning apply unchanged; a rolled-back unit takes the row with it. **Analyzer** (*HX-10 settled details* (39)): `AlvoSymbols` resolves `MMLib.Alvo.Scheduling.IAlvoScheduler` and `IsTransactionCall` also answers `true` for its members and for `AlvoSchedulerExtensions.ScheduleAsync`.

- [ ] **Step 4: Run** the three classes plus `Alvo1001AwaitTests`, `'*AlvoFlowUnitTests'`, and the doubles' classes in `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests` (`'*WriteGateUnitRefusalTests'`, `'*UnitSavepointJoinTests'`, `'*EfAlvoDataUnitRefusalTests'`); PASS. Accept the baselines (Abstractions: `IAlvoScheduler`; core: `AlvoSchedulerExtensions`).
- [ ] **Step 5: Commit** — `feat(scheduling): IAlvoScheduler records tenant, provenance and depth, and joins the open unit`.

## Task 4: `TimerPump` — fire due timers, in the drain and in the background **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Scheduling/TimerPump.cs`, `test/_shared/testhost/descriptors/` handler fixtures as needed
- Modify: `src/MMLib.Alvo/Events/Internal/{EventPump,DeliveryDispatcher}.cs` (extract the gate sequence into `Events/Internal/DispatcherGates.cs`), `Scheduling/Setup.cs`, `test/_shared/testhost/TimeFacts.cs`

**Interfaces:** Consumes `ITimerStore.ClaimTimersAsync/FireAsync/CancelAsync`, `TimerProvenance.Read` (Abstractions, internal). Produces `internal sealed class TimerPump : BackgroundService` with `internal Task<AlvoDrainResult> ManualRoundAsync(CancellationToken)`; `EventPump(TimerPump timers, OutboxDispatcher outbox, DeliveryDispatcher deliveries, …)` whose round is **timers → outbox → deliveries**, results summed; `internal static class DispatcherGates { Task<bool> OpenAsync(AlvoEventOptions, AlvoBootState, IPolicyCatalogProvider, Action logManual, Action logUnprimed, CancellationToken) }`.

- [ ] **Step 1: Failing facts** in `TimeFacts` (both engines). The handler records what it received into a singleton `ReminderLog`:

```csharp
[Fact]
public async Task A_fake_clock_and_a_drain_deliver_a_24_hour_reminder()
{
    var log = new ReminderLog();
    await using var alvo = await StartAsync(log);
    await Scheduler(alvo).ScheduleAsync("remind-b1", _now.AddHours(24), Reminder, Data("b1"), Tenant, Ct);

    await alvo.DrainEventsAsync(Ct);
    log.Received.ShouldBeEmpty();
    Time.Advance(TimeSpan.FromHours(24));
    await alvo.DrainEventsAsync(Ct);

    log.Received.ShouldHaveSingleItem().Data["booking"].ShouldBe("b1");
}

[Fact]
public async Task A_timer_scheduled_in_a_handler_fires_with_the_handlers_correlation_causation_and_depth()
{
    var log = new ReminderLog();
    await using var alvo = await StartAsync(log, armOnCreate: true);
    var created = await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), Ct);
    await alvo.DrainEventsAsync(Ct);
    Time.Advance(TimeSpan.FromHours(24));
    await alvo.DrainEventsAsync(Ct);

    var origin = log.Created.ShouldHaveSingleItem();
    var fired = log.Received.ShouldHaveSingleItem();
    (fired.CorrelationId, fired.CausationId, fired.ChainDepth).ShouldBe((origin.CorrelationId, origin.Id.ToString(), origin.ChainDepth + 2));
}
```

Also: `A_rescheduled_reminder_fires_once_at_its_new_time_even_when_it_races_a_claim` (claim through the store with a 1-min lease, `ScheduleAsync` same key +2 h, drain — nothing; advance 2 h; drain → one reminder, at the new time); `A_reminder_cancelled_while_claimed_never_fires`; `A_timer_survives_a_restart_of_the_host` (schedule, dispose host, start a second host over the same database, advance, drain → one reminder); `A_typed_reminder_round_trips_a_guid_and_a_timestamp` (`ScheduleAsync<BookingReminderDue>` with a `Guid BookingId` and a `DateTimeOffset StartsAt`; the typed handler `AddEventHandler<BookingReminderDue, …>` receives both equal to what was sent); `A_self_re_arming_timer_stops_at_the_depth_limit` (the first timer is armed **outside any flow**, at depth 0; the handler on `Reminder` re-arms the same key +1 h; loop drains/advances 10 times; the handler ran exactly 4 times — at depths 2, 4, 6, 8 — and the deliveries list one `dead` with `last_error_type = chain-depth-exceeded`); `A_timer_due_in_the_past_fires_on_the_next_drain` (`at` = now − 1 h with a `+02:00` offset).

- [ ] **Step 2: Run** `'*SqliteTimeTests'`; FAIL (nothing fires).

- [ ] **Step 3: Implement.** `TimerPump(ITimerStore? store, AlvoBootState boot, IPolicyCatalogProvider catalogs, IOptions<AlvoEventOptions> events, TimeProvider time, ILogger<TimerPump> logger)` (`ITimerStore` optional: no provider → the loop logs `TimerStoreAbsent` once and stands down; a manual round answers an empty result). One round:

```csharp
private async Task<AlvoDrainResult> RoundAsync(CancellationToken ct)
{
    await EnsureStoreAsync(ct).ConfigureAwait(false);
    var claimed = await store!.ClaimTimersAsync(events.Value.BatchSize, Lease, ct).ConfigureAwait(false);
    var fired = 0;
    foreach (var timer in claimed)
    {
        fired += await FireOneAsync(timer, ct).ConfigureAwait(false) ? 1 : 0;
    }

    return new AlvoDrainResult { Claimed = fired, Failed = 0, Subscribers = [] };
}

private async Task<bool> FireOneAsync(ClaimedTimer timer, CancellationToken ct)
{
    if (EventOf(timer) is not { } @event)
    {
        await store!.CancelAsync(timer.Scope, timer.Key, ct).ConfigureAwait(false);
        return false;
    }

    return await store!.FireAsync(timer.Scope, timer.Key, timer.Generation, @event, ct).ConfigureAwait(false);
}

private AlvoCustomEvent? EventOf(ClaimedTimer timer)
{
    try
    {
        var stored = TimerProvenance.Read(timer.Provenance);
        var context = AlvoContext.System(TenantOf(timer.Scope)) with { Provenance = stored with { Depth = stored.Depth + 1 } };
        return AlvoCustomEnvelope.Build(timer.MessageType, $"timers/{timer.Key}", PayloadOf(timer.Payload), context, time.GetUtcNow());
    }
    catch (Exception failure) when (failure is ArgumentException or JsonException or FormatException)
    {
        TimerLog.TimerUnbuildable(logger, timer.Scope, timer.Key, failure.GetType().Name);
        return null;
    }
}
```

(`PayloadOf` reads the stored data back through `AlvoEventJson`. `Build` does not re-check depth — fan-out owns the refusal for fired events, Decision 13. **A stored row that cannot be built** — a malformed payload, a scope that is not a tenant id, a type the custom door now refuses — is logged once and cancelled, never thrown every round.) A `FireAsync` that throws (storage) is logged (`TimerFireFailed`, the exception type only) and left for the lease to expire. Add `A_stored_timer_that_cannot_be_built_is_cancelled_not_retried` to `TimeFacts` (corrupt the row's payload with a raw `UPDATE`, drain twice → no event, `Pending == 0`, one log line). The background loop is `DeliveryDispatcher`'s (gates via `DispatcherGates`, `PollInterval`, backoff on failure). `EventPump.DrainAsync` sums three steps.

- [ ] **Step 4: Run** `'*SqliteTimeTests'`, `'*PostgreSqlTimeTests'`, `test/MMLib.Alvo.TestHost.Tests` whole (the drain changed); PASS. `scripts/test-ring1`.
- [ ] **Step 5: Commit** — `feat(scheduling): TimerPump fires due timers as events, in the drain and in the background`.
- [ ] **Step 6: Ship slice A** — `scripts/test-ring2`; dispatch `alvo-plan-guard`; build the PR report (`alvo-pr-report`); push `hx/13a-timers`; `gh pr create --base main` with body `Part of #390` + the report pointer; label `needs-deep-review`.

---

# Slice B — `hx/13b-schedules`

## Task 5: `AddSchedule<TJob>` — registration, Cronos, boot checks, catalog kind

**Files:**
- Create: `src/MMLib.Alvo/Scheduling/{IAlvoScheduledJob,AlvoJobContext,AlvoScheduleOptions,AlvoScheduleScope,CronSchedule,JobSpec,ScheduleChecks}.cs`, `src/MMLib.Alvo.Abstractions/Scheduling/IAlvoTenantCatalog.cs`, `test/MMLib.Alvo.Tests/Scheduling/{CronScheduleTests,ScheduleRegistrationTests}.cs`
- Modify: `Directory.Packages.props` (`<PackageVersion Include="Cronos" Version="0.13.0" />`), `src/MMLib.Alvo/MMLib.Alvo.csproj`, `docs/architecture/package-boundary.md` (Cronos beside Corvus: MIT, no dependencies, why), `Extensions/{AlvoExtensionBuilderExtensions,AlvoDeliveryOptions}.cs`, `Extensions/Internal/{ExtensionRegistration,ExtensionRegistrations,ExtensionBindingCompiler,ExtensionCatalog,AlvoDeliveryOptionsConfiguration,HandlerInvoker}.cs` (`HandlerInvoker` gains the `ForJob<TJob>()` factory), `Abstractions/Management/{AlvoExtensionKind,AlvoExtensionStage,ManagementExtension}.cs`, `test/MMLib.Alvo.Tests/ExtensionCatalogTests.*.verified.txt` if it moves, baselines.

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Scheduling;
public interface IAlvoScheduledJob { Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken); }
public sealed class AlvoJobContext
{
    public static AlvoJobContext Create(string id, DateTimeOffset scheduledFor, TenantId? tenant, IAlvoSession data, int attempt = 1, int? revision = null);
    public string Id { get; }  public DateTimeOffset ScheduledFor { get; }  public TenantId? Tenant { get; }
    public IAlvoSession Data { get; }  public int Attempt { get; }
    public int? Revision { get; }      // the revision a data migration started at; null for a schedule
}
public enum AlvoScheduleScope { Global = 0, EachTenant = 1 }
public sealed class AlvoScheduleOptions
{
    public AlvoScheduleScope Scope { get; set; }            // Global
    public TimeSpan Timeout { get; set; }                    // 15 min
    public AlvoRetryPolicy Retry { get; set; }               // AlvoRetryPolicy default
    public string? Description { get; set; }
}
public interface IAlvoTenantCatalog { Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default); }  // Abstractions
// DI namespace:
public static IAlvoBuilder AddSchedule<TJob>(this IAlvoBuilder builder, string id, string cron, string timeZone = "UTC",
    Action<AlvoScheduleOptions>? configure = null) where TJob : class, IAlvoScheduledJob;
// AlvoDeliveryOptions: public int MaxJobConcurrency { get; set; } = 2;   (validated ≥ 1)
internal sealed record JobSpec(string DeliveryKind, string? Cron, string? TimeZone, AlvoScheduleScope Scope, int? AfterRevision);
// HandlerRegistration gains: JobSpec? Job = null  (appended optional parameter)
internal sealed class CronSchedule
{
    internal static bool TryParse(string cron, string timeZone, DateTimeOffset now, out CronSchedule? schedule, out string? problem);
    internal DateTimeOffset? Next(DateTimeOffset after);   // exclusive, in the zone, UTC result
}
// HandlerInvoker: internal static HandlerInvoker ForJob<TJob>() where TJob : class   (TJob is IAlvoScheduledJob or, from Task 8, IAlvoDataMigration)
internal static class ScheduleEvents { internal static string TypeFor(string id); internal static string MigrationTypeFor(string id); }  // "alvo.schedule." + id.Replace('-', '_')
```

`AlvoExtensionKind.Schedule = 7` (`"schedule"`), `AlvoExtensionStage.Scheduled = 4` (`"scheduled"`); `ManagementExtension` appends `string? Cron`, `string? TimeZone`, `DateTimeOffset? NextRunAt`, `DateTimeOffset? LastRunAt`, `string? LastOutcome` (init; the last three filled in Task 7).

- [ ] **Step 1: Failing tests.** `CronScheduleTests` (acceptance 5 — pins Cronos's documented DST behaviour; if Cronos answers otherwise, stop and report rather than change the expectation):

```csharp
[Fact]
public void Bratislava_spring_forward_runs_a_skipped_time_at_the_end_of_the_gap()
{
    var schedule = Parsed("0 2 * * *", "Europe/Bratislava");

    schedule.Next(Utc(2026, 3, 28, 12, 0)).ShouldBe(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)));
    schedule.Next(Utc(2026, 3, 29, 1, 0)).ShouldBe(new DateTimeOffset(2026, 3, 30, 2, 0, 0, TimeSpan.FromHours(2)));
}

[Fact]
public void Bratislava_fall_back_runs_a_fixed_time_once()
{
    var schedule = Parsed("30 2 * * *", "Europe/Bratislava");

    var first = schedule.Next(Utc(2026, 10, 24, 12, 0))!.Value;
    first.ShouldBe(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)));
    schedule.Next(first).ShouldBe(new DateTimeOffset(2026, 10, 26, 2, 30, 0, TimeSpan.FromHours(1)));
}

[Fact]
public void Bratislava_fall_back_runs_an_hourly_schedule_in_both_two_oclocks()
{
    var schedule = Parsed("0 * * * *", "Europe/Bratislava");
    var from = Utc(2026, 10, 24, 23, 30);

    Occurrences(schedule, from, 3).ShouldBe([Utc(2026, 10, 25, 0, 0), Utc(2026, 10, 25, 1, 0), Utc(2026, 10, 25, 2, 0)]);
}
```

plus `A_seconds_field_an_unknown_zone_and_a_malformed_cron_are_problems_not_exceptions` and `A_cron_with_no_next_occurrence_is_a_problem` (`0 0 30 2 *` — 30 February). `ScheduleRegistrationTests`: `An_each_tenant_schedule_without_a_tenant_catalog_refuses_boot` (acceptance 6a — `AlvoTestHost.StartAsync` throws `AlvoStartupRefusedException` whose message names `extensions/nightly-roll/scope` and whose fix names `IAlvoTenantCatalog`); `Every_cron_and_zone_problem_is_refused_at_boot_at_once` (two bad schedules → one refusal, two locators `extensions/<id>/cron`, `extensions/<id>/timeZone`); `A_schedule_id_follows_the_extension_grammar_and_collides_with_a_handler_id`; `The_catalog_lists_a_schedule_with_its_kind_stage_cron_and_zone` (Kind `Schedule`, Stage `Scheduled`, `Event = "alvo.schedule.nightly_roll"`, `Cron`, `TimeZone`, `Timeout = 15 min`, `RunAs = "system"`).

- [ ] **Step 2: Run** `--filter-class '*CronScheduleTests'` and `'*ScheduleRegistrationTests'`; FAIL.

- [ ] **Step 3: Implement.** `CronSchedule` wraps `CronExpression.TryParse(cron, CronFormat.Standard, out var expression)` and `TimeZoneInfo.FindSystemTimeZoneById(zone)` (catching `TimeZoneNotFoundException`/`InvalidTimeZoneException` into `problem`), and reports `problem` when `Next(now)` is `null` (the expression never fires); `Next(after) => expression.GetNextOccurrence(after, zone)?.ToUniversalTime()`. `AddSchedule<TJob>`:

```csharp
ArgumentNullException.ThrowIfNull(builder);
ArgumentException.ThrowIfNullOrWhiteSpace(cron);
ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
var options = new AlvoScheduleOptions();
configure?.Invoke(options);
builder.Services.TryAddTransient<TJob>();
ExtensionRegistrations.AddHandler(
    builder.Services, id, new NamedEvent(ScheduleEvents.TypeFor(id)), when: null,
    handler => { handler.Timeout = options.Timeout; handler.Retry = options.Retry; handler.Description = options.Description; handler.Ordering = AlvoOrdering.None; },
    HandlerInvoker.ForJob<TJob>(), typed: null, job: new JobSpec(DeliveryKind.Schedule, cron, timeZone, options.Scope, AfterRevision: null));
```

(`ExtensionRegistrations.AddHandler` gains the optional `JobSpec? job` and passes it to `HandlerRegistration`; `id` is checked there against E3's grammar as every handler's is.) `HandlerInvoker.ForJob<TJob>()` is a `HandlerInvoker` over `AlvoRecord` that resolves `TJob` from the delivery scope and calls `RunAsync(AlvoJobContext.Create(context.Subscriber, scheduledFor, @event.TenantId, context.Data, context.Attempt, revision), ct)`, `scheduledFor` read from the payload key `scheduledFor`, `revision` from `revision` when present; its `Unresolvable` check is `ClassUnresolvable(typeof(TJob), …)`. `ScheduleChecks.Check(registry, IServiceProviderIsService)` — called from `ExtensionBindingCompiler.Check` — returns one `ExtensionProblem` per bad cron, bad zone, and `EachTenant` with `!isService.IsService(typeof(IAlvoTenantCatalog))` (message `"… runs once per tenant, and this host registers no IAlvoTenantCatalog to list them."`, fix `"Register an IAlvoTenantCatalog in DI, or use AlvoScheduleScope.Global."`). `DeliveryVocabulary.DeliveryKind` gains `Schedule = "schedule"`, `Migration = "migration"`; `DeliveryFanOut.Request` takes `handler.Registration.Job?.DeliveryKind ?? DeliveryKind.Handler`. `ExtensionCatalog.Handlers` excludes registrations with a `Job`; a new `Jobs(registry, exposeTypeNames)` lists them with the members above.

- [ ] **Step 4: Run** the two classes plus `'*ExtensionCatalogTests'` and `'*ExtensionBindingCompilerTests'`; PASS. Accept baselines (Abstractions: `IAlvoTenantCatalog`, the enum values, the `ManagementExtension` members; core: the job types, `AddSchedule`, `MaxJobConcurrency`).
- [ ] **Step 5: Commit** — `feat(scheduling): AddSchedule registers a cron job, checked with Cronos at boot`.

## Task 6: `SchedulePump`, the job pool, `EachTenant`

**Files:**
- Create: `src/MMLib.Alvo/Scheduling/{SchedulePump,ScheduleRuns}.cs`, `test/_shared/testhost/TimeFacts.Schedules.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlScheduleRaceTests.cs`, `test/MMLib.Alvo.Tests/Scheduling/FrameworkEventMintingTests.cs`
- Modify: `Events/Internal/{EventPump,DeliveryDispatcher}.cs`, `Scheduling/Setup.cs`

**Interfaces:** Consumes `ITimerStore` schedule members, `CronSchedule`, `IAlvoTenantCatalog`, `AlvoFrameworkEvent.Create`. Produces `internal sealed class SchedulePump : BackgroundService` (`ManualRoundAsync`), round order now **timers → schedules → outbox → deliveries**; `internal sealed class ScheduleRuns(IAlvoTenantCatalog? tenants, TimeProvider time) { Task<IReadOnlyList<AlvoFrameworkEnvelope>> EventsAsync(BoundHandler job, DateTimeOffset scheduledFor, bool manual, CancellationToken ct); }`; `SchedulePump.StartAsync` override that syncs once the binding is primed.

- [ ] **Step 1: Failing tests.** In `TimeFacts.Schedules.cs` (both engines): `A_due_occurrence_runs_once_as_a_schedule_delivery` (`0 2 * * *` UTC, clock 01:00, advance 1 h, drain → one run with `ScheduledFor == 02:00Z`; the delivery's `kind` is `schedule`); `A_missed_occurrence_fires_once_and_resumes_from_now` (advance 3 days, drain → one run; next run = the next 02:00 after now); `A_failing_run_does_not_block_the_next_occurrence` (job throws on its first scheduled time with `Retry` 1 attempt → dead; next day's occurrence runs); and acceptance 6b:

```csharp
[Fact]
public async Task An_each_tenant_schedule_runs_once_per_tenant_and_reads_only_its_own_rows()
{
    var seen = new ConcurrentBag<string>();
    await using var alvo = await StartAsync(alvo => alvo
        .AddSchedule<NoteCountingJob>("count-notes", "0 2 * * *", configure: o => o.Scope = AlvoScheduleScope.EachTenant)
        .Services.AddSingleton<IAlvoTenantCatalog>(new FixedTenants(TenantA, TenantB)).AddSingleton(seen));
    await SeedBookingAsync(alvo, TenantA, "a-note");
    await SeedBookingAsync(alvo, TenantB, "b-note");
    await alvo.DrainEventsAsync(Ct);

    Time.Advance(TimeSpan.FromHours(1));
    await alvo.DrainEventsAsync(Ct);

    seen.ShouldBe([$"{TenantA}:a-note", $"{TenantB}:b-note"], ignoreOrder: true);
}
```

(`NoteCountingJob` queries `bookings` through `context.Data` and adds `$"{context.Tenant}:{note}"` per row, so a run that read another tenant's row shows up as an extra string.) `PostgreSqlScheduleRaceTests` (acceptance 4):

```csharp
[Fact]
public async Task Three_dispatchers_fire_one_cron_occurrence_exactly_once()
{
    var connectionString = await DatabaseAsync();
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 1, 0, 0, TimeSpan.Zero));
    var runs = new ConcurrentQueue<DateTimeOffset>();
    var hosts = new List<AlvoTestHost>();
    for (var index = 0; index < 3; index++)
    {
        hosts.Add(await AlvoTestHost.StartAsync(alvo => alvo.UsePostgreSql(connectionString).FromDescriptor(Desk)
            .AddSchedule<RecordingJob>("nightly-roll", "0 2 * * *").Services.AddSingleton(runs), time, cancellationToken: Ct));
    }

    await hosts[0].DrainEventsAsync(Ct);
    time.Advance(TimeSpan.FromHours(1));
    await Task.WhenAll(hosts.Select(host => Task.Run(() => host.DrainEventsAsync(Ct), Ct)));
    await hosts[0].DrainEventsAsync(Ct);

    runs.ShouldHaveSingleItem().ShouldBe(new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero));
    foreach (var host in hosts) { await host.DisposeAsync(); }
}
```

`FrameworkEventMintingTests.Only_the_framework_paths_call_AlvoFrameworkEvent_Create` (acceptance 13 — over `ShippedSources`: the files containing `AlvoFrameworkEvent.Create(` are exactly `CommandCompletion.cs` and `ScheduleRuns.cs`; Task 8 adds `DataMigrationStarter.cs`) and `A_host_cannot_publish_or_schedule_a_schedule_run` (`IAlvoEvents.PublishAsync("alvo.schedule.nightly_roll", …)` and `IAlvoScheduler.ScheduleAsync(…, "alvo.schedule.nightly_roll", …)` both throw `ArgumentException`).

- [ ] **Step 2: Run** `'*SqliteTimeTests'`, `'*FrameworkEventMintingTests'`; FAIL.

- [ ] **Step 3: Implement.** `SchedulePump(ITimerStore? store, ExtensionBinder binder, ScheduleRuns runs, AlvoBootState boot, IPolicyCatalogProvider catalogs, IOptions<AlvoEventOptions> events, TimeProvider time, ILogger<SchedulePump> logger)`. **Sync at start, not on a round:** `StartAsync` (override, calling `base.StartAsync` after) awaits `boot.SettledAsync`; when the boot is `Ready` with a primed catalog — in both dispatch modes — it binds and calls `SyncSchedulesAsync` once with `NextRunAt = CronSchedule.Next(now)` for every bound job with a `Cron` (a job whose `Next` is `null` is left out and logged `ScheduleNeverFires`; boot already refused it, so this is defence only). Unregistered ids are deleted by the sync (Decision 8). `_claimant` is a per-instance field, `$"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"`, so two pumps in one process (the race test's three hosts) never share a fence. Facts that advance the clock **drain once before `Advance`** so the start-time sync is observable as quiet. Per round:

```csharp
var claimed = await store.ClaimSchedulesAsync(_claimant, Lease, ct).ConfigureAwait(false);
foreach (var due in claimed)
{
    if (bindings.Handler(due.Id) is not { Registration.Job.Cron: { } } job || !CronSchedule.TryParse(job.Registration.Job.Cron, job.Registration.Job.TimeZone!, time.GetUtcNow(), out var cron, out _))
    {
        continue;
    }

    var events = await runs.EventsAsync(job, due.ScheduledFor, manual: false, ct).ConfigureAwait(false);
    if (cron!.Next(Later(time.GetUtcNow(), due.ScheduledFor)) is not { } next)
    {
        continue;
    }

    advanced += await store.AdvanceScheduleAsync(new ScheduleAdvance
    { Id = due.Id, Claimant = _claimant, ScheduledFor = due.ScheduledFor, NextRunAt = next, Events = events }, ct).ConfigureAwait(false) ? 1 : 0;
}
```

(extract the loop body into a method to stay under 25 lines; a claimed id no longer bound is left to its lease.) `ScheduleRuns.EventsAsync` builds one event per tenant for `EachTenant` (`tenants!.ListTenantsAsync`) else one with no tenant — the only place besides `CommandCompletion` and `DataMigrationStarter` that mints an `AlvoFrameworkEnvelope`: `AlvoFrameworkEvent.Create(((NamedEvent)job.Registration.Source).Type, $"schedules/{id}", { schedule = id, scheduledFor = iso, manual? }, AlvoContext.System(tenant) with { Provenance = new AlvoProvenance { CorrelationId = Guid.NewGuid().ToString("N"), Depth = 0 } }, now)` — the hop table's *schedule run: new correlation, depth 0*. **Job pool:** `DeliveryDispatcher` claims twice per tick — handlers (`Job is null`) up to `MaxConcurrency − running handlers`, jobs up to `MaxJobConcurrency − running jobs`, each with its own lease (`longest timeout of that pool + 60 s`) — two `ClaimAsync` calls over disjoint subscriber lists, two running lists; the manual round does the same. `RunAs` is `System` for jobs (fan-out's run-as is the event's tenant, so a run reads only its tenant).

- [ ] **Step 4: Run** `'*SqliteTimeTests'`, `'*PostgreSqlTimeTests'`, `'*PostgreSqlScheduleRaceTests'`, `'*FrameworkEventMintingTests'`, `'*EventHandlerFacts*'` (the dispatcher changed); PASS.
- [ ] **Step 5: Commit** — `feat(scheduling): SchedulePump runs each occurrence once, per tenant on EachTenant, from a separate job pool`.

## Task 7: Run now, schedule state in the catalog, the #42 repoint **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Management/ManagementTimerCounts.cs`
- Modify: `Management/Internal/AlvoOperationsService.Writes.cs`, `Extensions/Internal/ExtensionCatalog.cs` (+ its caller in `AlvoManagementService`), `Abstractions/Management/ManagementExtensionCatalog.cs`; `src/MMLib.Alvo/Auth/TenantResolver.cs:10`, `src/MMLib.Alvo.Identity/Internal/AlvoIdentityContextResolver.cs:117`, `src/MMLib.Alvo.Admin/AlvoAdminClaims.cs:26`, `src/MMLib.Alvo.Abstractions/Identity/AlvoUser.cs:54`; `test/MMLib.Alvo.Tests/Management/` operations tests; `test/_shared/testhost/TimeFacts.Schedules.cs`

**Interfaces — Produces:** `public sealed record ManagementTimerCounts { public required int Pending { get; init; } public required int Due { get; init; } }`; `ManagementExtensionCatalog.Timers` (`ManagementTimerCounts?`, init, null when no `ITimerStore`); `ExtensionCatalog.CatalogAsync(IDeliveryStore?, ITimerStore?, CancellationToken)` fills `NextRunAt`/`LastRunAt` from `SchedulesAsync`, `LastOutcome` from the newest delivery of that subscriber (`IDeliveryStore.ListAsync(new DeliveryFilter { Subscriber = id, Limit = 1 })` — its `Status`), `Timers` from `CountTimersAsync`.

- [ ] **Step 1: Failing tests.** `TimeFacts.Schedules`: `Run_now_creates_a_delivery_that_runs_without_moving_the_next_occurrence` (`IAlvoOperations.RunScheduleAsync(project, "nightly-roll")` answers a `ManagementDelivery` with `Status = "pending"`, `Subscriber = "nightly-roll"`; drain → one run with `ScheduledFor == now`; `NextRunAt` unchanged); `Run_now_of_an_unknown_schedule_is_404` (`ManagementExtensionNotFoundException`); `The_catalog_shows_next_and_last_run_last_outcome_and_timer_counts`; `Run_now_on_each_tenant_answers_the_delivery_of_the_first_tenants_event` (two tenants; the answer's `EventId` is the first event's, though the second tenant's delivery is newer). Also add `Run_now_needs_developer` (viewer → `ManagementForbiddenException`) to the operations suite — it **passes already** (the gate exists since HX-06) and is a regression pin, not part of the FAIL claim below.
- [ ] **Step 2: Run** — the new `TimeFacts.Schedules` facts FAIL (404 for every id); `Run_now_needs_developer` passes.
- [ ] **Step 3: Implement.** `RunScheduleAsync`: gate as today; resolve the bound job (`Kind` `schedule`) or throw `ManagementExtensionNotFoundException` (message unchanged in shape: `This host registers no schedule '<id>'.`); `events = ScheduleRuns.EventsAsync(job, now, manual: true)`; `ITimerStore.AppendRunAsync(events)`; `IDeliveryStore.FanOutAsync` one `DeliveryRequest { Kind = "schedule", TenantId = event's, PartitionKey = event's }` per event; write the operations audit line (`OperationsAudit.Write`, operation `run-schedule`); return the `ManagementDelivery` **of the first event** — bounded by its id, never "newest first": `ListAsync(new DeliveryFilter { Subscriber = id, FromEventId = first.Envelope.Id, Limit = 1 })`, then assert the row's `EventId == first.Envelope.Id` (else `InvalidOperationException` — the fan-out above just wrote it). Repoint C4: replace `#42` with `#376` in the four comments (wording otherwise unchanged).
- [ ] **Step 4: Run** the affected classes, then `scripts/test-ring1`; PASS. Accept the Abstractions baseline (`ManagementTimerCounts`, `ManagementExtensionCatalog.Timers`). If the Management OpenAPI snapshot moves (new response members), dispatch `alvo-snapshot-judge`.
- [ ] **Step 5: Commit** — `feat(operations): run a schedule now, and show its next and last run in the catalog`.
- [ ] **Step 6: Ship slice B** — ring2, `alvo-plan-guard`, PR report, push `hx/13b-schedules`, PR to `main` with `Part of #390` and, on its own line, `Closes #376`; label `needs-deep-review`.

---

# Slice C — `hx/13c-data-migrations`

## Task 8: `AddDataMigration<T>` — once per revision, across restarts and rollbacks **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Scheduling/{IAlvoDataMigration,DataMigrationStarter}.cs`, `test/_shared/testhost/TimeFacts.Migrations.cs`
- Modify: `Extensions/AlvoExtensionBuilderExtensions.cs`, `Scheduling/{SchedulePump,ScheduleChecks,DataMigrationStarter}.cs`, `Extensions/Internal/HandlerInvoker.cs`, `Extensions/Internal/ExtensionCatalog.cs`, `Abstractions/Management/{AlvoExtensionKind,ManagementExtension}.cs`, `test/MMLib.Alvo.Tests/Scheduling/{ScheduleRegistrationTests,FrameworkEventMintingTests}.cs`, baselines

**Interfaces — Produces:**

```csharp
public interface IAlvoDataMigration { Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken); }
public static IAlvoBuilder AddDataMigration<TMigration>(this IAlvoBuilder builder, string id, int afterRevision)
    where TMigration : class, IAlvoDataMigration;
internal sealed class DataMigrationStarter(ITimerStore store, IAppliedSchemaStore applied, IAlvoTenantCatalog? tenants, IPolicyCatalogProvider catalogs, TimeProvider time, ILogger<DataMigrationStarter> logger)
{ internal Task<int> StartDueAsync(ExtensionBindings bindings, string project, CancellationToken ct); }
```

`AlvoExtensionKind.DataMigration = 8` (`"dataMigration"`), `ManagementExtension.AfterRevision` (`int?`).

- [ ] **Step 1: Failing tests** — acceptance 7, both engines:

```csharp
[Fact]
public async Task A_data_migration_runs_once_per_revision_across_restarts_rollback_and_reapply()
{
    var database = await DatabaseAsync();
    var runs = new ConcurrentQueue<int>();
    await using (var first = await StartAsync(database, runs, afterRevision: 2))
    {
        await first.DrainEventsAsync(Ct);
        runs.ShouldBeEmpty();
        await ApplyAsync(first, DeskWithNotes);
        await first.DrainEventsAsync(Ct);
    }

    await using var second = await StartAsync(database, runs, afterRevision: 2);
    await second.DrainEventsAsync(Ct);
    await RollbackAsync(second, toRevision: 1);
    await second.DrainEventsAsync(Ct);
    await ApplyAsync(second, DeskWithNotes);
    await second.DrainEventsAsync(Ct);

    runs.ShouldBe([2]);
}
```

(`runs` records `context.Revision`; `ApplyAsync`/`RollbackAsync` go through `IAlvoManagement`.) Also `A_scoped_schema_runs_the_migration_once_per_tenant` (catalog of two → two runs, each `context.Tenant` set); `A_migration_on_a_scoped_schema_without_a_tenant_catalog_refuses_boot`; `A_schema_scoped_by_a_later_apply_holds_the_migration_until_a_catalog_exists` (boot unscoped with no catalog and `afterRevision: 2`; apply a scoped revision 2 → drain → no run, `MigrationRowsAsync` empty, one `TenantCatalogMissing` log; restart with a catalog of two → one run per tenant, both with `Revision == 2`); `The_catalog_lists_a_migration_with_its_revision`. `FrameworkEventMintingTests` adds `DataMigrationStarter.cs` to the allowed set.

- [ ] **Step 2: Run** `'*SqliteTimeTests'`; FAIL.
- [ ] **Step 3: Implement.** `AddDataMigration` registers like `AddSchedule` with `NamedEvent(ScheduleEvents.MigrationTypeFor(id))`, `JobSpec(DeliveryKind.Migration, null, null, AlvoScheduleScope.Global, afterRevision)`, default job options, `HandlerInvoker.ForJob<TMigration>()` (it accepts `IAlvoDataMigration` too; `scheduledFor` = the start instant, `AlvoJobContext.Revision` = the payload's `revision`). `afterRevision < 1` → `ArgumentOutOfRangeException`. `ScheduleChecks` adds the tenant-catalog problem for migrations when the **boot** catalog's schema has a `Scoped` entity (checked in `Compile`, which sees the catalog). **Scoped after boot** (an apply adds the first scoped entity, no catalog registered): `DataMigrationStarter` checks again at run time — logs `TenantCatalogMissing` (migration id, revision) and skips without marking, so it starts on the first round after a catalog exists (Decision 10). `ScheduleRuns`/`EachTenant` needs no runtime guard: boot refuses it without a catalog whatever the schema. `DataMigrationStarter.StartDueAsync`: caches `StartedMigrationsAsync` once per process (adds to the cache on each start); for each bound migration not started, reads `applied.GetCurrentAsync(project)` (once per call, only when one is pending); when `Revision >= AfterRevision`, builds the events (one per tenant on a scoped schema via `IAlvoTenantCatalog`, else one, payload `{ migration = id, revision = applied.Revision }`, subject `migrations/<id>`) with `AlvoFrameworkEvent.Create`, and calls `StartMigrationAsync(id, applied.Revision, events)`. `SchedulePump`'s round calls it after the schedules. Rollback creates a new, higher revision (`RollbackAsync` is append-only) and the started row is permanent, so neither a rollback nor a re-apply re-runs it.
- [ ] **Step 4: Run** both engines' `TimeFacts`, `'*ScheduleRegistrationTests'`, `'*FrameworkEventMintingTests'`; then `scripts/test-ring1`. Accept baselines.
- [ ] **Step 5: Commit** — `feat(scheduling): AddDataMigration runs once per revision, per tenant on a scoped schema`.
- [ ] **Step 6: Ship slice C** — ring2, plan-guard, PR report, push, PR (`Part of #390`), `needs-deep-review`.

---

# Slice D — `hx/13d-time-surfaces`

## Task 9: Dashboard — Operations → Schedules, timer counts

**Files:**
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/ScheduleScenarios.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Operations/{Operations.razor,Operations.razor.cs,OperationsLoader.cs,OperationsView.cs}`, `src/MMLib.Alvo.Admin/Internal/OperationsGateway.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsWorld.cs`, `test/MMLib.Alvo.Admin.Tests/` component tests for `OperationsView`

**Interfaces:** Consumes `ManagementExtensionCatalog.Timers`, `ManagementExtension.{Kind, Cron, TimeZone, NextRunAt, LastRunAt, LastOutcome, AfterRevision}`, `IAlvoOperations.RunScheduleAsync`. Produces `OperationsSnapshot` gaining `IReadOnlyList<ManagementExtension> Jobs` and `ManagementTimerCounts? Timers`; `OperationsView.NextRun(ManagementExtension, DateTimeOffset now)` → `"in 3 h (02:00 Europe/Bratislava)"`-style text.

- [ ] **Step 1: Failing tests.** Component/unit: `OperationsViewTests.A_schedule_row_reads_next_run_in_its_zone_and_last_outcome`; `A_migration_row_reads_its_revision_and_never_offers_run_now`. E2E (`OperationsWorld` registers `.AddSchedule<NightlyRoll>("nightly-roll", "0 2 * * *", "Europe/Bratislava", o => o.Description = "Rolls the next day's slots.")` and schedules one timer at start):

```csharp
[Fact]
public async Task A_schedule_runs_now_and_shows_its_last_run()
{
    await using var world = await OperationsWorld.StartAsync(Ct);
    var page = await world.SignedInAsync(AdminRole.Developer, "/operations");

    var row = page.GetByRole(AriaRole.Row, new() { Name = "nightly-roll" });
    await Expect(row).ToContainTextAsync("0 2 * * *");
    await row.GetByRole(AriaRole.Button, new() { Name = "Run now" }).ClickAsync();
    await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("nightly-roll will run");
    await world.DrainAsync(Ct);
    await page.ReloadAsync();

    await Expect(row).ToContainTextAsync("succeeded");
    await Expect(page.GetByTestId("timer-counts")).ToContainTextAsync("1 pending");
}
```

(adapt helper names to `OperationsScenarios`' existing ones.)
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*OperationsViewTests'` and `scripts/test-admin-e2e --filter Schedule`; FAIL.
- [ ] **Step 3: Implement.** A *Schedules* section under the handlers on the Operations page: one row per job (id, description, cron + zone or `after revision n`, next run, last run, last outcome chip, **Run now** for `schedule` rows — confirm dialog, then the returned delivery opens in the existing delivery pane); a *Timers* tile (`n pending · m due`, `data-testid="timer-counts"`). The refusals the port answers (403, 404) are drawn as the page draws every refusal (*HX-06 settled details* (23)) — no level-aware hiding. Phone width: the section stacks like the handler list (`The_page_fits_a_phone…` keeps passing).
- [ ] **Step 4: Run** the two commands again; PASS.
- [ ] **Step 5: Commit** — `feat(admin): Operations shows schedules with run now, and timer counts`.

## Task 10: Docs, skill, architecture doc, CHANGELOG, spec, PLAN **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/time.mdx`, `website/src/snippets/extending/time/{booking-reminders.alvo.json,BookingReminders.cs,BookingRemindersTests.cs}`, `docs/architecture/time.md`
- Modify: `website` sidebar entry beside *Integrity in the descriptor*; `docs/architecture/analyzers.md` (the *Owed* paragraph becomes *Done in HX-13*), `docs/architecture/extensibility.md` (rule 4's `Add*` list already names `AddSchedule`/`AddDataMigration` — verify), `plugins/alvo/skills/alvo-descriptor-hooks/SKILL.md`, `test/_shared/ai/SkillCaseAnswers.cs` if a pinned answer quotes the changed line, `CHANGELOG.md`, the spec, `docs/PLAN.md`

- [ ] **Step 1: Failing test** — `BookingRemindersTests.The_reminder_arrives_a_day_before_and_the_nightly_roll_runs_once` (runs the snippet through `AlvoTestHost`: a booking create arms `ScheduleAsync<BookingReminderDue>("reminder:<id>", starts_at − 24 h, …)`; moving `starts_at` re-arms the same key; drain + advance proves one reminder at the new time; `AddSchedule<RollSlots>("roll-slots", "0 2 * * *", "Europe/Bratislava")` runs once at 02:00 local). `SnippetCoverageTests` fails until the page references the snippet.
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests`; FAIL.
- [ ] **Step 3: Write.**
  - **Docs page *Time: timers and schedules*** (excerpts by `SourceExcerpt` anchors): a keyed timer (schedule, move = same key, cancel; at-least-once handler, exactly-once fire); inside a transaction (joins the unit, a rollback leaves no timer — the HX-18 path); tenants (argument or the unit's; required on a scoped schema); depth (a timer that re-arms itself stops at 8 — the dead `chain-depth-exceeded` delivery); schedules (5-field cron, IANA zone, DST rules quoted from Cronos, one run across replicas, missed occurrences once, `EachTenant` + `IAlvoTenantCatalog`, job pool and 15-min timeout, run now); data migrations (once per revision, never re-run by rollback); testing with a fake clock + `DrainEventsAsync`; the dashboard's *Schedules* section; limits (no sub-minute cron, no backlog, a run that outlasts its period overlaps the next).
  - **`docs/architecture/time.md`**: the three tables (DDL), the claim/fence/fire transactions with the EvalPlanQual argument, generation-as-token (Decision 1), unit enlistment, provenance per hop, the pump round order, the job pool, Decisions 1–15 as *settled* with their cost; the framework-envelope rule (Decision 15) with the port pin that holds it.
  - **Skill**: in `alvo-descriptor-hooks`, replace `a timed job is \`automation\`, which this build does not run.` with `a timed job is host code — a schedule (\`AddSchedule\`) or a timer (\`IAlvoScheduler\`); \`get_extensions\` lists the schedules already registered. The descriptor's \`automation\` does not run in this build.` Run `dotnet test --project test/MMLib.Alvo.Ai.Tests` (region/size caps).
  - **CHANGELOG** `[Unreleased]`: *Added* — timers, schedules, data migrations, run now, the Operations *Schedules* section, `Cronos` dependency, `Alvo:Deliveries:MaxJobConcurrency`; *Changed (breaking)* — `ITimerStore` is a new provider obligation (a provider implementing `IOutboxStore` implements it over the same database), `AlvoExtensionKind`/`AlvoExtensionStage` gain members (an exhaustive `switch` gains cases).
  - **Spec**: under §4.10 append *HX-13 settled details* — Decisions 1–15 verbatim, one line each (Decision 10 is recorded as a **deviation** from §4.10's `System(null)` iterating tenants, with its cost: tenants created later never get the migration), and amend §5 row `13` with the symbols beyond it (Global Constraints list).
  - **PLAN.md**: append HX-13 to the HX line (`HX-13 (timers, cron schedules, data migrations) the thirteenth`).
- [ ] **Step 4: Run** `test/MMLib.Alvo.DocsSnippets.Tests`, `test/MMLib.Alvo.Ai.Tests`, `test/MMLib.Alvo.DocsGen.Tests`, then `scripts/test-ring1`; PASS.
- [ ] **Step 5: Commit** — `docs(scheduling): Time — timers and schedules; the architecture doc, skill, CHANGELOG and spec`.
- [ ] **Step 6: Ship slice D** — ring2, `scripts/test-admin-e2e --filter Schedule` once more, plan-guard, PR report, push, PR with `Closes #390`, `needs-deep-review`. The stack-level security review follows outside this plan.

---

## Acceptance criteria → tests

| AC | Test (task) |
|---|---|
| (1) same-key reschedule fires once at the new time, racing a claim | `TimerStoreContractTests.A_reschedule_during_a_claim_fences_the_stale_fire_and_the_new_generation_fires_once` (T1, both engines); `TimeFacts.A_rescheduled_reminder_fires_once_at_its_new_time_even_when_it_races_a_claim` (T4, both) |
| (2) cancel during a claim prevents firing | `TimerStoreContractTests.A_cancel_during_a_claim_leaves_the_fire_nothing_to_append` (T1); `TimeFacts.A_reminder_cancelled_while_claimed_never_fires` (T4) |
| (3) a timer survives restart | `TimeFacts.A_timer_survives_a_restart_of_the_host` (T4, both) |
| (4) three dispatchers, one occurrence, once (PostgreSQL) | `PostgreSqlScheduleRaceTests.Three_dispatchers_fire_one_cron_occurrence_exactly_once` (T6); store-level `TimerStoreContractTests.Racing_claimants_advance_one_occurrence_once` (T2, both) |
| (5) `Europe/Bratislava` DST (unit) | `CronScheduleTests.Bratislava_spring_forward_runs_a_skipped_time_at_the_end_of_the_gap`, `…fall_back_runs_a_fixed_time_once`, `…fall_back_runs_an_hourly_schedule_in_both_two_oclocks` (T5) |
| (6) `EachTenant` without a catalog refuses boot; with one, one run per tenant, none reading another | `ScheduleRegistrationTests.An_each_tenant_schedule_without_a_tenant_catalog_refuses_boot` (T5); `TimeFacts.An_each_tenant_schedule_runs_once_per_tenant_and_reads_only_its_own_rows` (T6, both) |
| (7) a migration runs once per revision across restarts, not again after rollback + re-apply | `TimeFacts.A_data_migration_runs_once_per_revision_across_restarts_rollback_and_reapply` (T8, both) |
| (8) fire + outbox append commit together (contract suite) | `TimerStoreContractTests.A_fire_removes_the_timer_and_appends_its_event_in_one_commit`, `…A_fire_whose_outbox_append_fails_leaves_the_timer_in_place` (T1); `…An_advance_whose_outbox_append_fails_leaves_the_occurrence_due`, `…A_migration_whose_outbox_append_fails_is_not_marked` (T2) |
| (9) a timer scheduled in a handler carries depth and causation | `TimeFacts.A_timer_scheduled_in_a_handler_fires_with_the_handlers_correlation_causation_and_depth` (T4, both) |
| (10) fake clock + drain delivers a 24 h reminder | `TimeFacts.A_fake_clock_and_a_drain_deliver_a_24_hour_reminder` (T4, both) |
| (11) self-re-arming timer stops at depth 8; `ScheduleAsync` refused above it | `TimeFacts.A_self_re_arming_timer_stops_at_the_depth_limit` (T4, both); `AlvoSchedulerTests.Scheduling_above_the_depth_limit_is_refused` (T3) |
| (12) `ScheduleAsync` in a rolled-back `TransactAsync` leaves no row | `TimeFacts.A_timer_scheduled_in_a_rolled_back_unit_leaves_no_row` (T3, both) |
| (13) `alvo.schedule.*` minted only through the framework path | `TimerStorePortTests.A_host_built_schedule_event_cannot_reach_the_outbox_through_ITimerStore` (T1); `FrameworkEventMintingTests.Only_the_framework_paths_call_AlvoFrameworkEvent_Create`, `…A_host_cannot_publish_or_schedule_a_schedule_run` (T6); `AlvoSchedulerTests.A_framework_event_type_cannot_be_scheduled` (T3) |
