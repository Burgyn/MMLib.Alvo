# HX-14 — Steps and flow control — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An event handler (or a scheduled job) can split a side effect into named steps that run at most once to completion each, even when a later step fails and the delivery retries (`IAlvoSteps.RunAsync`); it can wait durably (`SleepUntilAsync`); a handler can cap how many of its deliveries run at once per tenant or per record (`Concurrency = AlvoConcurrency.PerKey(...)`); and it can debounce a burst of events on one record into one delivery of the latest (`Debounce`).

**Architecture:** Everything rides the existing delivery engine (`IDeliveryStore`, `alvo_deliveries`), nothing new runs beside it. **Steps** are rows in `alvo_steps` keyed `(event_id, subscriber, step)` (C11), read once per attempt and written first-writer-wins after each step's work. **A sleep is the delivery itself**: the attempt ends `skipped`, the row goes back to `pending` with `not_before = at`, and it does not count as an attempt. **Debounce** is a fan-out-time fact: the new row waits `debounce`, and in the same transaction every older waiting delivery of the same subscriber, record and tenant becomes `superseded`. **Concurrency** is a counting semaphore made of `limit` lockable rows in `alvo_concurrency_slots`: a limited subscriber's delivery is claimed only together with a free slot, in one transaction, by fenced conditional `UPDATE … RETURNING`s. There is never a `COUNT` predicate (M2). An answer releases the slot only if its own fence held.

**Tech Stack:** .NET 10 (C# 14), Microsoft.Data.Sqlite, Npgsql (PostgreSQL 16 Testcontainers), System.Text.Json, xUnit v3 on MTP, Shouldly, `Microsoft.Extensions.TimeProvider.Testing`, Verify + PublicApiGenerator, Blazor/MudBlazor, Microsoft.Playwright, Astro/Starlight. No new package.

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`. Scope comes from §7 *HX-14* (scope, docs, dashboard, acceptance (1)–(4)), with:
- §4.10 *Steps (HX-14)* and *HX-13 settled details* (1)–(24), which HX-14 builds on.
- §4.8: the `alvo_steps`/`alvo_concurrency_slots` DDL; the status `superseded`; the claim, fence and lease (*HX-03 settled details* (11), (24)); `GET …/deliveries/{id}` "+ steps from HX-14".
- §4.13: `ManagementExtension.Concurrency`; *HX-04 settled details* (1).
- §5 row `14`; §6.1 row *slots claim*; §2.2 C11; Appendix A M2, minor 14/15.

Issue **#391** (epic #370; blocked-by #390; related #374 — long waits go to a durable-workflow adapter, not here).

Frozen artifacts read:
- Abstractions: `src/MMLib.Alvo.Abstractions/Events/{IDeliveryStore,DeliveryOperations}.cs`, `Management/{ManagementOperationsModels,ManagementExtension,IAlvoOperations}.cs`, `AlvoFrameworkTables.cs`.
- Core: `src/MMLib.Alvo/Events/{AlvoEventContext,Internal/DeliveryRunner,Internal/DeliveryDispatcher,Internal/DeliveryFanOut,Internal/DeliveryVocabulary}.cs`, `Extensions/{AlvoHandlerOptions,AlvoOrdering,Internal/HandlerBinding,Internal/HandlerDescription,Internal/ExtensionRegistration}.cs`, `Scheduling/AlvoJobContext.cs`.
- EF driver: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{DeliveryTable,DeliveryOperationsTable,EfCoreDeliveryStore}.cs`.
- `docs/architecture/time.md`.

`schema/project.schema.json` does not move: this is a host-code feature, so `scripts/gen-prototype-fixtures` is not needed.

**Prior art** (CLAUDE.md *Study the problem*):
- **Inngest** comes closest; the spec's shape mirrors it. `step.run(id, fn)` memoizes each step's JSON result per run, so a retry replays the completed steps from their stored results. `step.sleepUntil` ends the invocation and re-invokes it later. `concurrency: { limit, key }` caps the number of running steps per key expression. `debounce: { period, key }` is *trailing*: the function runs once, `period` after the last event, with the **latest** event.
- **Temporal / Durable Functions:** completed activity results live in history and are never re-executed on replay. Each activity stays at-least-once in itself: a crash between the side effect and its record repeats it.
- **PostgreSQL READ COMMITTED `UPDATE`:** when a row is blocked, only the outer `WHERE` is re-checked against that row (EvalPlanQual), never a subquery — the outbox's `ClaimSql` remarks.
- **Counting semaphore as N lockable rows:** a slot is taken by a conditional `UPDATE` of one row. Concurrent takers serialize on that row's lock and re-check its condition. A `COUNT(*) < limit` predicate, by contrast, passes for every taker that read the same snapshot.

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx14`. One writer per worktree; never `git add -A` (stage by path); `git branch --show-current` before every commit and push; never `git clean`; use `/bin/ls` or `find` (bare `ls` hides ignored files).
- **Two slices — each its own PR to `main`, each leaves `main` green and shippable:**
  - **A `hx/14a-steps-engine`** (Tasks 1–6): steps, sleep, debounce, concurrency slots, the catalog and operations read. Before Task 1: `git branch -m hx/14-steps-flow-control hx/14a-steps-engine`. The plan commit is on it and lands in this PR. If HX-13's slices have not all merged to `main` when A ships, the PR targets `main` anyway and lands after them: rebase onto `main` once they merge, never merge `main` in.
  - **B `hx/14b-steps-surfaces`** (Tasks 7–8): dashboard, docs page, architecture doc, skill line, CHANGELOG, spec, PLAN. `git fetch origin && git switch -c hx/14b-steps-surfaces origin/main` once A merged; otherwise branch from A and retarget after it merges.
- **Parallel work:** other HX steps (HX-12, HX-15, HX-16) are being built at the same time. **Shared files** — `AlvoFrameworkTables`, `CHANGELOG.md`, `docs/PLAN.md`, the spec, `PublicApi.*.verified.txt`, `plugins/alvo/skills/**` (size-capped), `ManagementExtension.cs`, `ManagementOperationsModels.cs`:
  - Edit them small and **append-only**: append members and constants at the end.
  - On a rebase conflict, keep both sides.
  - Enum values already taken: `AlvoExtensionKind` `Narrowing = 7`, `Enricher = 8`, `Schedule = 9`, `DataMigration = 10`; `AlvoExtensionStage` `Read = 4`, `Scheduled = 5`. **HX-14 adds no enum value.**
- **Public surface added is exactly** (each item beyond §5 row `14` is a Decision below):
  - Core `MMLib.Alvo.Events` (A): `IAlvoSteps`; `AlvoEventContext.Steps`.
  - Core `MMLib.Alvo.Extensions` (A): `AlvoConcurrency`; `AlvoHandlerOptions.Concurrency`, `.Debounce`.
  - Core `MMLib.Alvo.Scheduling` (A): `AlvoJobContext.Steps`.
  - Abstractions `MMLib.Alvo.Events` (A): the `IDeliveryStore` members `StepsAsync`, `RecordStepAsync`, `SleepAsync`; `DeliveryStep`; `DeliveryLimit`; `DeliveryRequest.Debounce`; `DeliveryClaim.Limits`.
  - Abstractions `MMLib.Alvo.Management` (A): `ManagementExtension.Concurrency`; `ManagementDelivery.Steps`; `ManagementDeliveryStep`.
  - Testing (A): new `[Fact]`s in `DeliveryStoreContractTests` (new partial `DeliveryStoreContractTests.Steps.cs`), plus a default-implemented world member `IDeliveryStoreWorld.ReadSlotsAsync` (see Task 5).
  - Everything else is `internal`.
- **Wire constants (verbatim):**
  - Tables: `<prefix>_steps`, `<prefix>_concurrency_slots`.
  - Statuses: delivery status `superseded`; attempt outcome `skipped`.
  - Concurrency keys `tenant`, `record`. The slot key is `<subscriber>\u001f<tenant|record>\u001f<tenant id or "">\u001f<partition key or "">`, joined with U+001F. `PerKey("tenant", n)` leaves the partition key part empty.
  - The catalog renders concurrency as `tenant:2` / `record:1`.
  - Limits:

    | Thing | Allowed |
    |---|---|
    | step name | 1–200 chars, no control characters, unique within one run |
    | steps per delivery | 1 000 |
    | step result | at most 256 KiB of UTF-8 JSON |
    | `Debounce` | > 0 and ≤ 1 day |
    | `PerKey` limit | 1–100 |

- **Engine parity:** every data-path fact runs on SQLite and on PostgreSQL:
  - Contract facts: `DeliveryStoreContractTests` (`SqliteDeliveryStoreTests`, `PostgreSqlDeliveryStoreTests`).
  - Pipeline facts: the new abstract `test/_shared/testhost/StepFacts.cs`, run as `SqliteStepTests` in `test/MMLib.Alvo.TestHost.Tests` and `PostgreSqlStepTests` in `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration` (the `TimeFacts` pattern, one database per fact).
  - Acceptance (3) is the PostgreSQL-only fact: SQLite serialises its writers, so the race does not exist there. **Every new PostgreSQL test class carries the skip guard** `Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, …")` in its database helper (the Windows leg has no Docker).
- **Deterministic tests:** `FakeTimeProvider` everywhere; no `Task.Delay`/`Thread.Sleep`. Races are forced by ordering calls, or by running `Task.WhenAll` over racing claimants and asserting outcome counts, never timings.
- **Security core:** HX-14 is not on §6.2's deep-review list. It does touch tenant isolation (the supersede and slot keys carry the tenant — Review Focus 1–2). The stack-level security review runs once after the stack, outside this plan; no per-PR `/security-review`.
- C# style per `alvo-dotnet-conventions`:
  - No inline comments; methods ≲ 25 lines.
  - `ConfigureAwait(false)` in `src/` only.
  - `TestContext.Current.CancellationToken` in every test call.
  - XML docs on every public member, citing no `§` or `#issue`.
  - `.cs` files are UTF-8 BOM + CRLF: after editing, run `dotnet format whitespace MMLib.Alvo.slnx --include <file> …`.
- **Testing per task:**
  - Every task: the affected test projects only (`dotnet test --project <proj> --filter-class '*<Class>'`), plus pre-commit's scoped ring0.
  - **[slice end]** tasks: `scripts/test-ring1`, then `dotnet build MMLib.Alvo.slnx -c Release` (CI builds Release; analyzers differ from Debug).
  - Before each slice's PR: `scripts/test-ring2`.
  - Admin e2e only in Task 7: `scripts/test-admin-e2e --filter DeliverySteps`.
  - No Stryker, no load run.
- **Public-API baselines:** `test/MMLib.Alvo.Abstractions.Tests` (Abstractions) and `test/MMLib.Alvo.Tests` (core, Testing).
  - Accept a `*.received.txt` only when its diff adds exactly the symbols above.
  - The turn-review gate then asks for `alvo-snapshot-judge`, plus an `alvo-architecture-rules` justification per symbol.
  - The Management OpenAPI snapshot moves in Task 6 (`ManagementDelivery.steps`). Judge it the same way.
- **Commits:** Conventional Commits, one per task, each ending `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes`/`Fixes` keyword in any commit.
- **PR bodies:** A → `Part of #391`; B → `Closes #391`. Each slice PR gets ring2, `alvo-plan-guard` and the PR report (`alvo-pr-report`).

## Decisions (the spec does not settle these)

1. **Deviation from §4.10 — the sleep uses the delivery row, not an `alvo_timers` row.** §4.10 calls for a timer `step:{delivery}:{name}` that re-queues the delivery when it fires. Instead, `SleepUntilAsync` sets `status = pending`, `not_before = at`, `attempts = attempts − 1` (fenced) and records the attempt as `skipped`. Why: a timer fire appends to the outbox and cannot reset a delivery without a new `ITimerStore` member (HX-13 (3)), and the row already has a due time and a claim. Cost if wrong: a sleeping delivery is absent from the *Timers* tile; it shows as pending with a future due time.
2. **A sleep is detected even if the handler swallows it.** `SleepUntilAsync` throws an internal `StepSleep` signal *and* flags the run. The runner answers "sleep" whenever the flag is set, whether the handler returned or threw. Cost: none — a handler cannot turn a sleep into a success.
3. **A sleep's step row stores the wake instant.** On resume, if `now ≥ at` the call returns at once; if a redeliver woke the delivery early, it sleeps again. Cost: one row per sleep.
4. **Each step is at-least-once; completed steps are never re-run.** The result is recorded after `work` returns, outside any unit. A crash in between repeats that one step. Two runners racing one step keep the first record, and both continue with the stored result. Cost: the step body must be idempotent, which the docs say first.
5. **Results are System.Text.Json (`JsonSerializerOptions.Web`).** The non-generic `RunAsync` stores `null`. The result never leaves the store through the Management API: `ManagementDeliveryStep` carries name and time only. Cost: an operator cannot see a step's value in the dashboard (intended — it can be PII).
6. **Steps are reached through the context** (`AlvoEventContext.Steps`, `AlvoJobContext.Steps`), not through DI: a resolvable `IAlvoSteps` outside a delivery would be a trap. A context from `AlvoEventContext.Create`, or a `DeliverAsync` fixture run, gets a pass-through: it runs `work` every time and `SleepUntilAsync` returns at once. Cost: unit tests do not exercise memoization (`AlvoTestHost` does).
7. **Redeliver or replay of a `succeeded` delivery deletes its steps** in the reset's transaction. A `dead` or `cancelled` one keeps them, so fixing a bug and redelivering resumes after the last good step. Cost: an operator who wants a full re-run of a dead delivery cannot get one (documented).
8. **Debounce is trailing and scoped to (subscriber, partition key, tenant).** The new row is due `now + Debounce`. In the same transaction, every `pending`/`retrying` row of that lane with an older `event_id` than the lane's newest becomes `superseded` (`completed_at = now`). `running` is never superseded. Cost: a steady stream faster than the debounce period delays delivery indefinitely (Inngest caps this with a timeout; we do not — a debounce is capped at 1 day).
9. **`superseded` is finished, but not replayable or redeliverable** (like `chain-depth-exceeded`): the newer delivery carries the record's latest state. It neither blocks a lane nor holds a slot. Cost: an operator cannot force a stale event through.
10. **Concurrency is per subscriber.** The slot key includes the subscriber, and `record` includes the tenant too. Slot rows are inserted lazily (`ON CONFLICT DO NOTHING`), and only slots `0 … limit−1` are tried. Cost: after a limit is lowered, deliveries already holding higher slots run on until they finish — a transient overshoot.
11. **A slot's lease is the delivery's claim lease.** It is released by the fenced answer (complete, fail, sleep) in the same transaction, and only when that fence held. A crashed runner's slot frees when its lease expires. Cost: a crash costs one slot for up to the lease (90 s at the defaults).
12. **`IDeliveryStore` gains three abstract members**, breaking for implementers like HX-06's nine and HX-09's two (CHANGELOG *Changed (breaking)*). `DeliveryRequest.Debounce` and `DeliveryClaim.Limits` are init members a provider must honour; the contract suite pins them. Cost: a third-party store fails to compile until it implements them.
13. **#391 closes with slice B.**

## Review Focus

1. **Tenant isolation of debounce:** a custom event with the same `partitionkey` in two tenants supersedes nothing across them. Pinned by `DeliveryStoreContractTests.A_debounced_fan_out_supersedes_only_its_own_tenants_lane` (Task 4).
2. **The slot fence:** a late finisher whose lease was reclaimed must not release the new holder's slot. Pinned by `DeliveryStoreContractTests.A_late_finisher_releases_no_slot_it_no_longer_holds` (Task 5).
3. **Lock order in the slot claim** (keys ascending, slots ascending, then the delivery row), so two claimants cannot deadlock. Pinned by `PostgreSqlConcurrencyRaceTests.Racing_claimants_never_hold_more_slots_than_the_limit` (Task 5), with 20 rounds × 4 claimants and no `40P01`.
4. **A handler that catches `Exception` around `SleepUntilAsync`** still sleeps. Pinned by `StepFacts.A_handler_that_swallows_the_sleep_still_sleeps` (Task 3).
5. **A sleep does not burn an attempt:** five sleeps followed by a failure still have the policy's full retries left. Pinned by `DeliveryStoreContractTests.A_sleep_returns_the_delivery_due_at_its_wake_time_without_spending_an_attempt` (Task 3).

## File structure

**Abstractions:**
- `src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs`: 3 members, plus `DeliveryRequest.Debounce` and `DeliveryClaim.Limits`.
- `Events/DeliveryStepRecords.cs` (new): `DeliveryStep`, `DeliveryLimit`.
- `AlvoFrameworkTables.cs`: +2 suffixes and `NamesFor`.
- `Management/{ManagementExtension,ManagementOperationsModels}.cs`.

**Core:**
- `src/MMLib.Alvo/Events/IAlvoSteps.cs` (new).
- `Events/Internal/{DeliverySteps,PassThroughSteps,StepSleep,StepNames}.cs` (new).
- `Events/Internal/{DeliveryRunner,DeliveryDispatcher,DeliveryFanOut,DeliveryVocabulary}.cs`.
- `Events/AlvoEventContext.cs`.
- `Extensions/AlvoConcurrency.cs` (new).
- `Extensions/{AlvoHandlerOptions}.cs`, `Extensions/Internal/{ExtensionRegistration,HandlerBinding,HandlerDescription}.cs`.
- `Scheduling/AlvoJobContext.cs`.
- `Management/Internal/{AlvoOperationsService,DeliveryViews}.cs`.

**EF driver:**
- `Internal/StepTable.cs`, `Internal/ConcurrencySlotTable.cs` (new).
- `Internal/{DeliveryTable,DeliveryOperationsTable,EfCoreDeliveryStore,EfCoreDeliveryStore.Operations}.cs`.
- `Internal/EfCoreDeliveryStore.Steps.cs`, `Internal/EfCoreDeliveryStore.Slots.cs` (new partials).

**Testing:** `src/MMLib.Alvo.Testing/Events/{DeliveryStoreContractTests.Steps.cs (new),IDeliveryStoreWorld.cs}`.

**Tests:**
- `test/_shared/ef/DeliveryStoreWorld.cs`.
- `test/_shared/testhost/StepFacts.cs` (new), with the engine subclasses `test/MMLib.Alvo.TestHost.Tests/SqliteStepTests.cs` and `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/{PostgreSqlStepTests,PostgreSqlConcurrencyRaceTests}.cs`.
- `test/MMLib.Alvo.Tests/Events/{DeliveryStepsTests,AlvoConcurrencyTests}.cs`.
- `test/MMLib.Alvo.Admin.Tests.EndToEnd/DeliveryStepsScenarios.cs` (B).

**Admin (B):**
- `Components/Operations/{Operations.razor,Operations.razor.cs,OperationsView.cs}`.
- `Internal/OperationsGateway.cs` (only if the delivery read needs no change, leave it).

**Docs (B):**
- `website/src/content/docs/extending/steps.mdx`.
- `website/src/snippets/extending/steps/{BookingConfirmation.cs,BookingConfirmationTests.cs}` (reuses `booking-desk.alvo.json` from `time/`).
- `docs/architecture/steps.md` (new), and `docs/architecture/time.md` (one cross-link line).
- `plugins/alvo/skills/alvo-descriptor-hooks/SKILL.md` (one sentence).
- `CHANGELOG.md`, the spec, `docs/PLAN.md`.

---

# Slice A — `hx/14a-steps-engine`

## Task 1: `alvo_steps` and the store's step members

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/DeliveryStepRecords.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{StepTable,EfCoreDeliveryStore.Steps}.cs`, `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Steps.cs`.
- Modify: `IDeliveryStore.cs` (the `StepsAsync`/`RecordStepAsync` members; `SleepAsync` is declared here too and implemented in Task 3, so the port moves once), `AlvoFrameworkTables.cs` (append `StepsSuffix = "_steps"`, `ConcurrencySlotsSuffix = "_concurrency_slots"`, both in `NamesFor`), `DeliveryTable.EnsureAsync` (creates both tables), and the Abstractions/Testing baselines.

**Interfaces — Produces** (namespace `MMLib.Alvo.Events`):

```csharp
public sealed record DeliveryStep
{
    public required Guid EventId { get; init; }
    public required string Subscriber { get; init; }
    public required string Name { get; init; }
    public required string Result { get; init; }          // JSON text, "null" for a step with no value
    public required DateTimeOffset CompletedAt { get; init; }
}
public sealed record DeliveryLimit                         // Task 5 uses it; declared with the port's other records
{
    public required string Subscriber { get; init; }
    public required string Key { get; init; }              // "tenant" | "record"
    public required int Limit { get; init; }
}
// IDeliveryStore — appended:
Task<IReadOnlyList<DeliveryStep>> StepsAsync(Guid eventId, string subscriber, CancellationToken cancellationToken = default);
Task<DeliveryStep> RecordStepAsync(DeliveryStep step, CancellationToken cancellationToken = default);   // first writer wins; returns the stored row
Task<bool> SleepAsync(DeliveryAttempt attempt, DateTimeOffset wakeAt, CancellationToken cancellationToken = default);
```

**DDL** (`StepTable`, identical on both engines; `step` is not reserved in T-SQL):

```csharp
internal static string Ddl(string table) =>
    $"""
    CREATE TABLE IF NOT EXISTS {table} (
        event_id TEXT NOT NULL,
        subscriber TEXT NOT NULL,
        step TEXT NOT NULL,
        result TEXT NOT NULL,
        completed_at TEXT NOT NULL,
        PRIMARY KEY (event_id, subscriber, step)
    )
    """;

internal static string RecordSql(string table) =>
    $"""
    INSERT INTO {table} (event_id, subscriber, step, result, completed_at)
    VALUES (@event_id, @subscriber, @step, @result, @completed_at)
    ON CONFLICT (event_id, subscriber, step) DO NOTHING
    """;

internal static string ReadOneSql(string table) =>
    $"SELECT result, completed_at FROM {table} WHERE event_id = @event_id AND subscriber = @subscriber AND step = @step";

internal static string ReadSql(string table) =>
    $"SELECT step, result, completed_at FROM {table} WHERE event_id = @event_id AND subscriber = @subscriber ORDER BY completed_at, step";
```

`RecordStepAsync` runs the insert and then `ReadOneSql` in one write-gated transaction, and returns what is stored. On PostgreSQL a concurrent insert of the same key waits on the unique index, then does nothing. Instants go through `StoredInstant`, as the delivery table's do.

- [ ] **Step 1: Failing contract facts** (`DeliveryStoreContractTests.Steps.cs`):
  - `A_recorded_step_is_read_back_with_its_result`
  - `A_second_record_of_one_step_keeps_the_first_result` (record `"1"`, then `"2"` → both calls return `"1"`)
  - `Steps_are_kept_per_subscriber_of_one_event` (C11: two subscribers, same step name, two rows)
  - `Steps_of_another_event_are_not_read`

  Add the two engine overrides' runs.
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteDeliveryStoreTests'`; FAIL (does not compile, then fails).
- [ ] **Step 3: Implement** `StepTable` and `EfCoreDeliveryStore.Steps.cs`. Until Task 3, `SleepAsync` throws `NotSupportedException("Task 3")` and has no caller yet.
- [ ] **Step 4: Run** the SQLite class and `--project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlDeliveryStoreTests'`; then `--project test/MMLib.Alvo.Abstractions.Tests` (baseline: the two records and three members only). PASS.
- [ ] **Step 5: Commit** — `feat(events): alvo_steps and the delivery store's step members`.

## Task 2: `IAlvoSteps.RunAsync` — memoized steps in a delivery

**Files:**
- Create: `src/MMLib.Alvo/Events/IAlvoSteps.cs`, `Events/Internal/{DeliverySteps,PassThroughSteps,StepNames}.cs`, `test/_shared/testhost/StepFacts.cs`, `test/MMLib.Alvo.TestHost.Tests/SqliteStepTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlStepTests.cs`, `test/MMLib.Alvo.Tests/Events/DeliveryStepsTests.cs`.
- Modify: `Events/AlvoEventContext.cs` (`Steps`; the internal constructor takes an `IAlvoSteps`; `Create` passes `PassThroughSteps.Instance`), `Scheduling/AlvoJobContext.cs` (`Steps`), `Events/Internal/DeliveryRunner.cs` (builds a `DeliverySteps` per attempt; `InvokeOnceAsync` for fixtures uses the pass-through), and the core baseline.

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Events;
public interface IAlvoSteps
{
    Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
    Task RunAsync(string name, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);
    Task SleepUntilAsync(string name, DateTimeOffset at, CancellationToken cancellationToken = default);
}
// AlvoEventContext: public IAlvoSteps Steps { get; }    AlvoJobContext: public IAlvoSteps Steps { get; }
```

`DeliverySteps(IDeliveryStore store, Guid eventId, string subscriber, TimeProvider time)`:
- It loads `StepsAsync` once, lazily, into a `Dictionary<string, DeliveryStep>`.
- It tracks the names used in this run in a `HashSet`: a name reused in one run is an `InvalidOperationException` naming it. The 1 000-step cap is enforced the same way.
- `StepNames.Check(name)` validates length and control characters.
- The core:

```csharp
public async Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(work);
    Claim(name);
    var done = await CompletedAsync(cancellationToken).ConfigureAwait(false);
    if (done.TryGetValue(name, out var stored))
    {
        return JsonSerializer.Deserialize<T>(stored.Result, JsonSerializerOptions.Web)!;
    }

    var value = await work(cancellationToken).ConfigureAwait(false);
    var recorded = await store.RecordStepAsync(Step(name, Serialize(value)), CancellationToken.None).ConfigureAwait(false);
    done[name] = recorded;

    return JsonSerializer.Deserialize<T>(recorded.Result, JsonSerializerOptions.Web)!;
}
```

`Serialize` refuses a result over 256 KiB with an `InvalidOperationException`, and the attempt fails as any exception does. The record uses `CancellationToken.None`: a step whose work finished is recorded even when the attempt's token fired.

- [ ] **Step 1: Failing facts.** `StepFacts`, on both engines, uses the `booking-desk.alvo.json` descriptor that `TimeFacts` uses, and a singleton `StepLog` (a counter per step name):

```csharp
[Fact]
public async Task A_failure_at_step_three_does_not_re_run_steps_one_and_two()
{
    var log = new StepLog { FailStepThreeTimes = 1 };
    await using var alvo = await StartAsync(log, handler: ThreeSteps);
    await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), Ct);

    await alvo.DrainEventsAsync(Ct);
    Time.Advance(TimeSpan.FromMinutes(5));
    await alvo.DrainEventsAsync(Ct);

    (log.Runs("one"), log.Runs("two"), log.Runs("three")).ShouldBe((1, 1, 2));
    log.SeenByThree.ShouldBe(["deposit-1:email-1", "deposit-1:email-1"]);
    (await DeliveriesAsync(alvo)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
}
```

`ThreeSteps`:
1. `RunAsync("one", …)` returns `"deposit-1"`.
2. `RunAsync("two", …)` returns `"email-1"`.
3. `RunAsync("three", …)` throws while `FailStepThreeTimes > 0`, and records `$"{one}:{two}"`.

The fact proves that a re-run step sees the memoized values.

Also in `StepFacts`:
- `A_step_name_used_twice_in_one_run_fails_the_attempt`.
- `Two_handlers_of_one_event_keep_their_own_steps` (C11).

Unit tests, `DeliveryStepsTests` (in-memory fake `IDeliveryStore` over a dictionary):
- `A_completed_step_returns_its_stored_value_without_running`
- `A_racing_record_returns_the_winners_value`
- `A_result_over_the_cap_is_refused`
- `A_name_with_a_control_character_is_refused`
- `The_pass_through_runs_work_every_time_and_never_sleeps`
- `A_fixture_run_uses_the_pass_through` (`AlvoTestHost.DeliverAsync` twice → the step ran twice, no rows)
- `A_job_context_carries_steps`
- [ ] **Step 2: Run** `--project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteStepTests'` and `--project test/MMLib.Alvo.Tests --filter-class '*DeliveryStepsTests'`; FAIL.
- [ ] **Step 3: Implement** as above. `DeliveryRunner.RunHandlerAsync` builds `new DeliverySteps(store, delivery.EventId, delivery.Subscriber, time)` and passes it to the context. The job path (`HandlerInvoker.ForJob`) reads it from the same context.
- [ ] **Step 4: Run** both, plus `'*PostgreSqlStepTests'` and `test/MMLib.Alvo.TestHost.Tests` whole (the context changed). PASS.
- [ ] **Step 5: Commit** — `feat(events): IAlvoSteps memoizes a delivery's steps across retries`.

## Task 3: `SleepUntilAsync` — the delivery sleeps and resumes

**Files:**
- Create: `Events/Internal/StepSleep.cs`.
- Modify: `DeliverySteps.cs`, `DeliveryRunner.cs` (verdict `Sleep`; a `DeliveryRunOutcome.Slept`), `DeliveryTable.cs` (`SleepSql`), `EfCoreDeliveryStore.cs` (`SleepAsync`), `DeliveryOperationsTable.cs` (Decision 7: delete steps on a `succeeded` reset), `DeliveryStoreContractTests.Steps.cs`, `StepFacts.cs`.

**Store — the fenced sleep** (one write-gated transaction: this update, then the attempt row with outcome `skipped`):

```csharp
internal static string SleepSql(string table) =>
    $"""
    UPDATE {table} SET status = 'pending', not_before = @wake_at, attempts = attempts - 1,
                       lease_until = NULL, claimed_by = NULL, last_error_type = NULL
     WHERE {Fence}
    """;
```

`AttemptSql` numbers the history after its last row, so a lowered `attempts` does not collide with the history. The attempt row is written only when the fence matched one row, as `AnswerAsync` already does.

**Decision 7 — clearing steps on a `succeeded` reset.** `RedeliverAsync` and `ReplayAsync` run, **before** their reset `UPDATE` and in its transaction:

```sql
DELETE FROM {steps} WHERE EXISTS (SELECT 1 FROM {table} d WHERE d.id = @id AND d.status = 'succeeded'
  AND d.event_id = {steps}.event_id AND d.subscriber = {steps}.subscriber)
```

The replay form joins on the replay's selected ids.

**Core:**
- `DeliverySteps.SleepUntilAsync(name, at)`:
  - The step's stored result is the wake instant, as ISO-8601 UTC JSON.
  - First call: record `at` and set `SleepRequested = at`.
  - Stored and `now ≥ stored`: return at once.
  - Stored and still ahead: set `SleepRequested = stored`.
  - Every path that sleeps then throws `StepSleep` (internal, sealed, `Exception`).
- `DeliveryRunner.AttemptAsync` checks `steps.SleepRequested` **after** the invocation ends, however it ended (Decision 2), and answers through `store.SleepAsync(attempt, wakeAt)`. Telemetry outcome `skipped`; a lost fence is `LeaseLost`, as today.

- [ ] **Step 1: Failing facts.**

```csharp
[Fact]
public async Task Sleep_until_resumes_the_delivery_after_the_fake_clock_passes()
{
    var log = new StepLog();
    await using var alvo = await StartAsync(log, handler: RemindADayBefore);   // charge, SleepUntil(starts − 24 h), send
    await alvo.Data.CreateAsync("bookings", Booking("b1", startsAt: Now.AddDays(3)), Staff(Tenant), Ct);

    await alvo.DrainEventsAsync(Ct);
    (log.Runs("charge"), log.Runs("send")).ShouldBe((1, 0));
    var sleeping = (await DeliveriesAsync(alvo)).ShouldHaveSingleItem();
    (sleeping.Status, sleeping.NotBefore, sleeping.Attempts).ShouldBe(("pending", Now.AddDays(2), 0));

    Time.Advance(TimeSpan.FromDays(2));
    await alvo.DrainEventsAsync(Ct);

    (log.Runs("charge"), log.Runs("send")).ShouldBe((1, 1));
    (await HistoryAsync(alvo)).Select(a => a.Outcome).ShouldBe(["skipped", "succeeded"]);
}
```

Also in `StepFacts`:
- `A_handler_that_swallows_the_sleep_still_sleeps`.
- `A_sleep_whose_time_has_passed_does_not_end_the_attempt`.
- `A_redelivered_succeeded_delivery_runs_its_steps_again_and_a_redelivered_dead_one_resumes` (Decision 7).

Contract facts:
- `A_sleep_returns_the_delivery_due_at_its_wake_time_without_spending_an_attempt`.
- `A_sleep_by_a_claimant_that_lost_its_lease_changes_nothing`.
- `A_sleeping_ordered_delivery_holds_its_lane` (pending blocks a later event of the record — documented behaviour).
- [ ] **Step 2: Run** `'*SqliteStepTests'`, `'*SqliteDeliveryStoreTests'`; FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both, plus the PostgreSQL legs (`'*PostgreSqlStepTests'`, `'*PostgreSqlDeliveryStoreTests'`, `'*PostgreSqlDeliveryOperationsTests'`) and `'*SqliteDeliveryOperationsTests'` (`DeliveryOperationsFacts`). PASS.
- [ ] **Step 5: Commit** — `feat(events): SleepUntilAsync ends the attempt and resumes the delivery at its wake time`.

## Task 4: `Debounce` — the newest waits, the older are superseded

**Files:**
- Modify: `Extensions/AlvoHandlerOptions.cs` (`Debounce`), `Extensions/Internal/{ExtensionRegistration,HandlerBinding}.cs` (`HandlerSettings.Debounce`; `CheckDebounce`), `Events/Internal/{DeliveryFanOut,DeliveryVocabulary}.cs`, `IDeliveryStore.cs` (`DeliveryRequest.Debounce`), `DeliveryTable.cs` (`FanOutSql` takes a per-row `not_before`; `SupersedeSql`; `Superseded` const), `EfCoreDeliveryStore.cs` (`FanOutAsync` becomes insert + supersede in one transaction), `DeliveryOperationsTable.cs` (`superseded` stays out of `Finished` and `ReplayableTerm`, Decision 9), the status docs in `ManagementOperationsModels.cs` and `DeliveryOperations.cs`, `DeliveryStoreContractTests.Steps.cs`, `StepFacts.cs`.

**Interfaces:** `AlvoHandlerOptions.Debounce` (`TimeSpan?`, default `null`); `DeliveryRequest.Debounce` (`TimeSpan?`, init). `HandlerBinding.CheckDebounce` refuses `≤ 0` or `> 1 day` with a fix ("Set Debounce between 1 second and 1 day, or leave it unset").

**SQL** (`FanOutRow` binds `@not_before{row}` = `now + Debounce`, or `now`):

```csharp
/// Supersedes every waiting delivery of one lane older than the lane's newest — run after the insert, in its
/// transaction, once per debounced request. The tenant term keeps a custom event's shared partition key per tenant.
internal static string SupersedeSql(string table) =>
    $"""
    UPDATE {table} SET status = 'superseded', completed_at = @now, lease_until = NULL
     WHERE subscriber = @subscriber AND partition_key = @partition_key
       AND COALESCE(tenant_id, '') = COALESCE(@tenant_id, '')
       AND status IN ('pending', 'retrying')
       AND event_id < (SELECT MAX(newest.event_id) FROM {table} newest
                        WHERE newest.subscriber = @subscriber AND newest.partition_key = @partition_key
                          AND COALESCE(newest.tenant_id, '') = COALESCE(@tenant_id, ''))
    """;
```

The status term sits in the **outer** `WHERE`. A claim that turned the row `running` first therefore wins: the blocked supersede re-checks the row and skips it. Supersede first, and the claim's outer `Claimable` re-check fails. A re-fan-out of an older event, or a held-back one, is superseded by the same statement.

- [ ] **Step 1: Failing facts.**

```csharp
[Fact]
public async Task Ten_rapid_updates_with_a_five_second_debounce_deliver_once_the_latest()
{
    var log = new StepLog();
    await using var alvo = await StartAsync(log, handler: RecordSeats, configure: o => o.Debounce = TimeSpan.FromSeconds(5));
    var booking = await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), Ct);
    await alvo.DrainEventsAsync(Ct);
    log.Clear();

    for (var seats = 1; seats <= 10; seats++)
    {
        await alvo.Data.UpdateAsync("bookings", booking.Id, Seats(seats), Staff(Tenant), Ct);
        Time.Advance(TimeSpan.FromMilliseconds(200));
        await alvo.DrainEventsAsync(Ct);
    }

    log.Seats.ShouldBeEmpty();
    Time.Advance(TimeSpan.FromSeconds(5));
    await alvo.DrainEventsAsync(Ct);

    log.Seats.ShouldBe([10]);
    (await DeliveriesAsync(alvo)).Count(d => d.Status == "superseded").ShouldBe(9);
}
```

The handler subscribes to `updated` only, so the create's delivery is not part of the lane's count. Contract facts:
- `A_debounced_fan_out_is_due_after_its_debounce`.
- `A_debounced_fan_out_supersedes_the_lanes_waiting_delivery`.
- `A_debounced_fan_out_never_supersedes_a_running_delivery`.
- `A_debounced_fan_out_supersedes_only_its_own_tenants_lane` (Review Focus 1).
- `A_superseded_delivery_holds_no_lane_and_is_not_replayable`.

`ExtensionBinderTests.A_debounce_outside_one_second_to_one_day_refuses_the_start` (existing class; its timeout facts are the model).
- [ ] **Step 2: Run** `'*SqliteStepTests'`, `'*SqliteDeliveryStoreTests'`, `--project test/MMLib.Alvo.Tests --filter-class '*ExtensionBinderTests'`; FAIL.
- [ ] **Step 3: Implement.** `DeliveryFanOut.Request` copies `handler.Settings.Debounce`.
- [ ] **Step 4: Run** the three, plus the PostgreSQL legs. PASS.
- [ ] **Step 5: Commit** — `feat(events): Debounce supersedes a lane's waiting delivery with the newest`.

## Task 5: `AlvoConcurrency.PerKey` — slot rows claimed with the delivery

**Files:**
- Create: `src/MMLib.Alvo/Extensions/AlvoConcurrency.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{ConcurrencySlotTable,EfCoreDeliveryStore.Slots}.cs`, `test/MMLib.Alvo.Tests/Events/AlvoConcurrencyTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlConcurrencyRaceTests.cs`.
- Modify: `AlvoHandlerOptions.cs` (`Concurrency`), `HandlerSettings`/`HandlerBinding` (`CheckConcurrency`: limit 1–100; key `tenant`|`record`), `IDeliveryStore.cs` (`DeliveryClaim.Limits`, default `[]`), `DeliveryDispatcher.ClaimOf` (fills `Limits` from the pool's handlers), `EfCoreDeliveryStore.cs` (`ClaimRowsAsync` runs the slot path in the same transaction; `AnswerAsync` and `SleepAsync` release the slot), `IDeliveryStoreWorld.cs` (DIM `ReadSlotsAsync` returning `[]` by default, so existing worlds still compile; the EF world overrides it), `test/_shared/ef/DeliveryStoreWorld.cs`, `DeliveryStoreContractTests.Steps.cs`.

**Interfaces — Produces:**

```csharp
namespace MMLib.Alvo.Extensions;
public sealed class AlvoConcurrency
{
    private AlvoConcurrency(string key, int limit) { Key = key; Limit = limit; }
    public string Key { get; }      // "tenant" | "record"
    public int Limit { get; }
    public static AlvoConcurrency PerKey(string key, int limit);   // ArgumentException / ArgumentOutOfRangeException on bad input
    public override string ToString() => $"{Key}:{Limit.ToString(CultureInfo.InvariantCulture)}";
}
// AlvoHandlerOptions: public AlvoConcurrency? Concurrency { get; set; }
```

**DDL and SQL** (`ConcurrencySlotTable`; `slot_key`, because `key` is reserved in T-SQL — HX-13 (2)):

```csharp
internal static string Ddl(string table) =>
    $"""
    CREATE TABLE IF NOT EXISTS {table} (
        slot_key TEXT NOT NULL,
        slot INTEGER NOT NULL,
        delivery_id TEXT NULL,
        lease_until TEXT NULL,
        PRIMARY KEY (slot_key, slot)
    )
    """;

internal static string EnsureSlotsSql(string table, int limit) =>
    $"INSERT INTO {table} (slot_key, slot) VALUES {string.Join(", ", Enumerable.Range(0, limit).Select(s => $"(@slot_key, {s})"))} "
    + "ON CONFLICT (slot_key, slot) DO NOTHING";

internal static string TakeSql(string table) =>
    $"""
    UPDATE {table} SET delivery_id = @delivery_id, lease_until = @lease_until
     WHERE slot_key = @slot_key AND slot = @slot AND (delivery_id IS NULL OR lease_until < @now)
    RETURNING slot
    """;

internal static string GiveBackSql(string table) =>
    $"UPDATE {table} SET delivery_id = NULL, lease_until = NULL WHERE slot_key = @slot_key AND slot = @slot AND delivery_id = @delivery_id";

internal static string ReleaseSql(string table) =>
    $"UPDATE {table} SET delivery_id = NULL, lease_until = NULL WHERE delivery_id = @id";
```

`DeliveryTable` gains two statements:
- `CandidatesSql(table, outbox, controls, limited)`: a plain `SELECT id, event_id, subscriber, partition_key, tenant_id … WHERE {Claimable} AND subscriber IN (…) AND {NotPaused} AND {LaneHead} ORDER BY event_id, subscriber LIMIT @candidates`. It is a hint, never the enforcement.
- `ClaimOneSql`: the existing claim `SET … RETURNING`, restricted to `id = @id AND {Claimable} AND {NotPaused} AND {LaneHead}`. Its outer `WHERE` repeats the whole condition, as the batch claim does.

**The slot path** (`EfCoreDeliveryStore.Slots.cs`; it runs inside `ClaimRowsAsync`'s transaction, after the existing batch `UPDATE`, which now names only unlimited subscribers):

```csharp
private async Task<List<ClaimedDelivery>> ClaimLimitedAsync(
    DbConnection connection, DbTransaction transaction, DeliveryClaim claim, int room, CancellationToken ct)
{
    var limits = claim.Limits.ToDictionary(limit => limit.Subscriber, StringComparer.Ordinal);
    var candidates = await CandidatesAsync(connection, transaction, claim, limits.Keys, room * 4, ct).ConfigureAwait(false);
    var claimed = new List<ClaimedDelivery>();
    foreach (var candidate in candidates.OrderBy(c => SlotKey(c, limits), StringComparer.Ordinal).ThenBy(c => c.EventId))
    {
        if (claimed.Count == room)
        {
            break;
        }

        if (await ClaimWithSlotAsync(connection, transaction, claim, candidate, limits[candidate.Subscriber], ct).ConfigureAwait(false) is { } won)
        {
            claimed.Add(won);
        }
    }

    return claimed;
}

private async Task<ClaimedDelivery?> ClaimWithSlotAsync(
    DbConnection connection, DbTransaction transaction, DeliveryClaim claim, Candidate candidate, DeliveryLimit limit, CancellationToken ct)
{
    var key = SlotKey(candidate, limit);
    await EnsureSlotsAsync(connection, transaction, key, limit.Limit, ct).ConfigureAwait(false);
    if (await TakeSlotAsync(connection, transaction, key, limit.Limit, candidate.Id, claim, ct).ConfigureAwait(false) is not { } slot)
    {
        return null;
    }

    var won = await ClaimOneAsync(connection, transaction, claim, candidate.Id, ct).ConfigureAwait(false);
    if (won is null)
    {
        await GiveBackAsync(connection, transaction, key, slot, candidate.Id, ct).ConfigureAwait(false);
    }

    return won;
}
```

`TakeSlotAsync` tries `slot = 0 … limit − 1` in ascending order and returns the first that `TakeSql` returned a row for. The slot's `lease_until` is the claim's (`now + claim.Lease`).

**Lock order.** Candidates are processed by slot key ascending, slots ascending, and the delivery row last. Two claimants therefore take locks in the same global order and cannot deadlock (Review Focus 3).

**Why the limit holds on PostgreSQL READ COMMITTED.** A second claimant that reaches a slot row the first one just took waits on that row's lock. Once the first transaction commits, the second re-checks the outer `delivery_id IS NULL OR lease_until < @now` against the committed row, fails, and moves on to the next slot. Once all `limit` slots are held, no claimant gets one, whatever snapshot it read its candidates from.

**Release.** `AnswerAsync` (complete, fail) and `SleepAsync` run `ReleaseSql` in their transaction, and only when the fenced update matched one row (Decision 11). An unlimited delivery holds no slot row, so the statement touches nothing.

- [ ] **Step 1: Failing facts.** Contract facts (both engines):
  - `A_claim_takes_no_more_deliveries_of_one_tenant_than_its_limit` (5 pending in one tenant, limit 2 → claim takes 2; a second claim takes 0).
  - `A_completed_delivery_frees_its_slot_for_the_next_claim`.
  - `A_slot_whose_lease_expired_is_taken_again`.
  - `A_late_finisher_releases_no_slot_it_no_longer_holds` (Review Focus 2).
  - `The_record_key_limits_each_record_of_each_tenant_on_its_own`.
  - `A_saturated_key_does_not_starve_another_keys_deliveries` (10 pending in tenant A, 1 in tenant B, limit 1, batch 2 → one of each).

PostgreSQL only:

```csharp
[Fact]
public async Task Racing_claimants_never_hold_more_slots_than_the_limit()
{
    var world = await WorldAsync();
    for (var round = 0; round < 20; round++)
    {
        await FanOutAsync(world, tenant: "t1", deliveries: 6);
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => world.Store.ClaimAsync(Claim($"c{round}-{i}", limit: 2), Ct), Ct)));
        claims.Sum(c => c.Count).ShouldBe(2);
        (await world.ReadSlotsAsync()).Count(s => s.DeliveryId is not null).ShouldBe(2);
        await CompleteAllAsync(world, claims);
    }
}

[Fact]
public async Task Two_dispatchers_never_run_a_third_concurrent_delivery_of_one_tenant()
{
    // Two AlvoTestHosts on one database, handler options Concurrency = PerKey("tenant", 2).
    // The handler records, on entry, the number of running deliveries of its own subscriber and tenant
    // (SELECT COUNT(*) … status = 'running'): an observation, not a gate.
    // Create 8 bookings in one tenant, run both hosts' DrainEventsAsync concurrently (Task.WhenAll), drain until quiet.
    log.MaxRunning.ShouldBeLessThanOrEqualTo(2);
    log.Handled.ShouldBe(8);
}
```

The second fact is the acceptance fact; the first makes it adversarial and deterministic in its assertion. Unit facts, `AlvoConcurrencyTests`:
- `PerKey_accepts_tenant_and_record`.
- `PerKey_refuses_another_key_and_a_limit_outside_one_to_one_hundred`.
- `Its_text_is_key_colon_limit`.
- [ ] **Step 2: Run** `'*SqliteDeliveryStoreTests'`, `'*AlvoConcurrencyTests'`; FAIL.
- [ ] **Step 3: Implement.** `room` is `claim.BatchSize` minus what the batch `UPDATE` claimed. The candidate over-read (`room * 4`, at most 200) keeps one saturated key from hiding every other key's candidates.
- [ ] **Step 4: Run** the SQLite legs, then `--project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlConcurrencyRaceTests'` and `'*PostgreSqlDeliveryStoreTests'`. PASS, with no `40P01` in the output.
- [ ] **Step 5: Commit** — `feat(events): per-tenant and per-record concurrency limits claimed as slot rows`.

## Task 6: The catalog's `Concurrency`, steps on the delivery read **[slice end]**

**Files:**
- Modify: `Abstractions/Management/ManagementExtension.cs` (append `Concurrency`), `ManagementOperationsModels.cs` (append `ManagementDelivery.Steps`, new `ManagementDeliveryStep`), `Extensions/Internal/HandlerDescription.cs`, `Management/Internal/{AlvoOperationsService,DeliveryViews}.cs`, `test/MMLib.Alvo.Tests/` catalog and operations tests, the Management OpenAPI snapshot, all three baselines.

**Interfaces:**

```csharp
public sealed record ManagementDeliveryStep
{
    public required string Name { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
// ManagementDelivery: public IReadOnlyList<ManagementDeliveryStep> Steps { get; init; } = [];
// ManagementExtension: public string? Concurrency { get; init; }     // "tenant:2"; null when unlimited
```

`GetDeliveryAsync` reads `StepsAsync(row.EventId, row.Subscriber)` beside the attempts. `ListDeliveriesAsync` does not read steps (no N+1).

- [ ] **Step 1: Failing tests:**
  - `AlvoOperationsServiceTests.A_delivery_read_lists_its_steps_by_name_and_time_but_never_their_results`: asserts the serialized JSON has no `result` property.
  - `ExtensionCatalogTests.A_limited_handler_shows_its_concurrency`.
  - The OpenAPI snapshot test, which goes red.
- [ ] **Step 2: Run** `--project test/MMLib.Alvo.Tests --filter-class '*AlvoOperationsServiceTests'` and `'*ExtensionCatalogTests'`; FAIL.
- [ ] **Step 3: Implement.** Accept the baselines and the OpenAPI snapshot only with exactly the slice-A symbols, then follow the judge flow.
- [ ] **Step 4: Run** `scripts/test-ring1`, then `dotnet build MMLib.Alvo.slnx -c Release`. PASS, with 0 warnings.
- [ ] **Step 5: Commit** — `feat(management): a delivery read lists its steps; the catalog shows a handler's concurrency`.
- [ ] **Step 6: Ship slice A:**
  1. `scripts/test-ring2`.
  2. Dispatch `alvo-plan-guard`.
  3. Build the PR report (`alvo-pr-report`).
  4. Push `hx/14a-steps-engine`.
  5. `gh pr create --base main`, body `Part of #391` + the report pointer.

---

# Slice B — `hx/14b-steps-surfaces`

## Task 7: Dashboard — delivery detail lists steps; superseded, sleeping, concurrency

**Files:**
- Create: `test/MMLib.Alvo.Admin.Tests.EndToEnd/DeliveryStepsScenarios.cs`.
- Modify: `src/MMLib.Alvo.Admin/Components/Operations/{Operations.razor,Operations.razor.cs,OperationsView.cs}`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsWorld.cs` (a stepped handler, a debounced handler, a limited handler), `test/MMLib.Alvo.Admin.Tests/` `OperationsViewTests`.

**Interfaces:** consumes `ManagementDelivery.Steps`, `.Status`, `.NotBefore`, `.History`, and `ManagementExtension.Concurrency`. Produces:
- `OperationsView.StatusLabel(ManagementDelivery, DateTimeOffset now)`: returns `"sleeping until 6 Nov 09:00"` for a `pending` delivery whose last attempt is `skipped` and whose `NotBefore` is ahead, `"superseded"` for that status, else the status.
- `OperationsView.Steps(ManagementDelivery)`: rows of `name · completed at`.

- [ ] **Step 1: Failing tests.**
  - Unit: `OperationsViewTests.A_sleeping_delivery_reads_sleeping_until_its_wake_time`, `A_superseded_delivery_has_its_own_chip_and_no_redeliver`, `A_limited_handler_row_shows_its_concurrency`.
  - E2E:

```csharp
[Fact]
public async Task A_delivery_that_failed_at_its_third_step_lists_the_two_done_steps()
{
    await using var world = await OperationsWorld.StartAsync(Ct);
    await world.CreateBookingAsync("b1", failThirdStep: true, Ct);
    await world.DrainAsync(Ct);
    var page = await world.SignedInAsync(AdminRole.Viewer, "/operations");

    await page.GetByRole(AriaRole.Row, new() { Name = "confirm-booking" }).ClickAsync();
    var steps = page.GetByRole(AriaRole.List, new() { Name = "Steps" });
    await Expect(steps.GetByRole(AriaRole.Listitem)).ToHaveCountAsync(2);
    await Expect(steps).ToContainTextAsync("charge-deposit");
    await Expect(steps).ToContainTextAsync("send-confirmation");
}
```

  Adapt the helper names to `OperationsScenarios`' existing ones.
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*OperationsViewTests'` and `scripts/test-admin-e2e --filter DeliverySteps`; FAIL.
- [ ] **Step 3: Implement.** The delivery pane gains a *Steps* list (`aria-label="Steps"`) under *Attempts*; it reads "No steps recorded" when empty. Status chips:
  - `superseded`: neutral tone. *Redeliver* is hidden for it, exactly as for any status the port refuses — drawn through the existing refusal path (*HX-06 settled details* (23)).
  - Sleeping: info tone, with a clock icon from the existing `Icons`.
  - The handler row shows `per tenant · 2` when `Concurrency` is set.
  - Phone width stacks as the attempts list does (`The_page_fits_a_phone…` keeps passing).
- [ ] **Step 4: Run** both again; PASS.
- [ ] **Step 5: Commit** — `feat(admin): delivery detail lists steps; superseded and sleeping deliveries read as such`.

## Task 8: Docs, architecture doc, skill, CHANGELOG, spec, PLAN **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/steps.mdx`, `website/src/snippets/extending/steps/{BookingConfirmation.cs,BookingConfirmationTests.cs}`, `docs/architecture/steps.md`.
- Modify: the sidebar entry after *Time: timers and schedules*; `docs/architecture/time.md` (one line: "a step's sleep is the delivery's own `not_before` — `steps.md`"); `plugins/alvo/skills/alvo-descriptor-hooks/SKILL.md`; `test/_shared/ai/SkillCaseAnswers.cs` if a pinned answer quotes the changed line; `CHANGELOG.md`; the spec; `docs/PLAN.md`.

- [ ] **Step 1: Failing test** — `BookingConfirmationTests.The_deposit_is_charged_once_though_the_email_failed_and_the_reminder_waits_for_its_day`. It runs the snippet through `AlvoTestHost` on the `time/booking-desk.alvo.json` descriptor:
  1. A create triggers `charge-deposit` (a fake gateway counter), then `send-confirmation`, which fails once, then `SleepUntilAsync("remind", starts − 24 h)`, then `send-reminder`.
  2. Drain, advance, drain again: the gateway is charged once and the reminder is sent once, at the right time.
  3. `Debounce = 5 s` on a second handler collapses three edits into one.

  `SnippetCoverageTests` fails until the page references the snippet.
- [ ] **Step 2: Run** `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests`; FAIL.
- [ ] **Step 3: Write.**
  - **Docs page *Multi-step side effects*.** Excerpts come from `SourceExcerpt` anchors. Sections:
    - Why: retries re-run the handler.
    - `RunAsync`: name each step; a step is at-least-once and must be idempotent; completed steps return their stored value; results are JSON.
    - `SleepUntilAsync`: the delivery sleeps and does not use an attempt; an ordered lane waits; for waits longer than days, see #374's adapter (link the issue).
    - `Debounce`: trailing, latest wins, per record and tenant, at most 1 day.
    - `Concurrency`: `PerKey("tenant" | "record", n)`, per handler, slot release on lease expiry.
    - Redeliver semantics (Decision 7).
    - Testing with a fake clock and `DrainEventsAsync`.
    - The dashboard's Steps list.
    - Limits: name, count, result size, debounce cap.
  - **`docs/architecture/steps.md`:**
    - The two tables' DDL; the step read/record flow; the sleep as a fenced reset.
    - The supersede statement and its EvalPlanQual argument.
    - The slot semaphore: the take/claim/give-back transaction, lock order, why `COUNT` is wrong, release under the fence.
    - Decisions 1–12 as *settled*, with their cost.
  - **Skill:** in `alvo-descriptor-hooks`, after the sentence about timed jobs, append one sentence: `A side effect in several steps that must not repeat (charge, then email) is host code — a handler with \`IAlvoSteps\`; a descriptor hook is one call.` Run `dotnet test --project test/MMLib.Alvo.Ai.Tests` (region and byte caps); if the cap trips, shorten the sentence rather than raising the cap.
  - **CHANGELOG** `[Unreleased]`, append-only:
    - *Added:* `IAlvoSteps` (steps, sleep), `AlvoHandlerOptions.Debounce`, `.Concurrency` / `AlvoConcurrency`, the delivery status `superseded`, the attempt outcome `skipped`, steps on `GET …/deliveries/{id}`, and the dashboard Steps list.
    - *Changed (breaking):* `IDeliveryStore` gains `StepsAsync`, `RecordStepAsync`, `SleepAsync`; a provider must also honour `DeliveryRequest.Debounce` and `DeliveryClaim.Limits`, as pinned by the contract suite.
  - **Spec:** under §4.10, after *HX-13 settled details*, append *HX-14 settled details*. Content:
    - Decisions 1–12, one line each. Decision 1 is recorded as a **deviation** from §4.10's timer `step:{delivery}:{name}`, with its cost.
    - Plus anything the build changed.
    - Amend §5 row `14` with the symbols beyond it (Global Constraints list).
  - **PLAN.md:** append `; HX-14 (steps, sleep, debounce, concurrency limits) the fourteenth` to the HX line.
- [ ] **Step 4: Run** `test/MMLib.Alvo.DocsSnippets.Tests`, `test/MMLib.Alvo.Ai.Tests`, `test/MMLib.Alvo.DocsGen.Tests`, then `scripts/test-ring1` and `dotnet build MMLib.Alvo.slnx -c Release`. PASS.
- [ ] **Step 5: Commit** — `docs(events): Multi-step side effects; the steps architecture doc, skill line, CHANGELOG and spec`.
- [ ] **Step 6: Ship slice B:**
  1. `scripts/test-ring2`.
  2. `scripts/test-admin-e2e --filter DeliverySteps` once more.
  3. Dispatch `alvo-plan-guard`.
  4. Build the PR report.
  5. Push.
  6. Open the PR with `Closes #391`.

  The stack-level security review follows outside this plan.

---

## Acceptance criteria → tests

| AC | Test (task) |
|---|---|
| (1) a failure at step 3 does not re-run steps 1–2, both engines | `StepFacts.A_failure_at_step_three_does_not_re_run_steps_one_and_two` (T2; `SqliteStepTests` + `PostgreSqlStepTests`); store pins `DeliveryStoreContractTests.A_recorded_step_is_read_back_with_its_result`, `…A_second_record_of_one_step_keeps_the_first_result` (T1, both) |
| (2) `SleepUntilAsync` resumes after the fake clock passes | `StepFacts.Sleep_until_resumes_the_delivery_after_the_fake_clock_passes` (T3, both); `DeliveryStoreContractTests.A_sleep_returns_the_delivery_due_at_its_wake_time_without_spending_an_attempt` (T3, both) |
| (3) `PerKey("tenant", 2)` never runs a third concurrent delivery with two dispatchers on PostgreSQL | `PostgreSqlConcurrencyRaceTests.Two_dispatchers_never_run_a_third_concurrent_delivery_of_one_tenant` (T5); adversarial store race `PostgreSqlConcurrencyRaceTests.Racing_claimants_never_hold_more_slots_than_the_limit` (T5); `DeliveryStoreContractTests.A_claim_takes_no_more_deliveries_of_one_tenant_than_its_limit` (T5, both) |
| (4) 10 rapid updates with 5 s debounce deliver once, latest | `StepFacts.Ten_rapid_updates_with_a_five_second_debounce_deliver_once_the_latest` (T4, both); `DeliveryStoreContractTests.A_debounced_fan_out_supersedes_the_lanes_waiting_delivery` (T4, both) |
| Docs with executed snippets | `BookingConfirmationTests.The_deposit_is_charged_once_though_the_email_failed_and_the_reminder_waits_for_its_day` (T8) |
| Dashboard: delivery detail lists steps | `DeliveryStepsScenarios.A_delivery_that_failed_at_its_third_step_lists_the_two_done_steps` (T7) |
