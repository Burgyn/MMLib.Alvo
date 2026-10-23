# HX-03 — Event handlers on the delivery engine (untyped core) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An embedded host can react to committed changes in C# — `Entity("tickets", t => t.OnUpdated("id", handler, when: "…"))`, `Entities([...])`, `Entity("*")`, `AddEventHandler(id, "orders.approved", …)` — and each matching event becomes a durable, per-subscriber **delivery** row that is claimed under a lease, run at-least-once with the same `EventId` every attempt, retried on the X5 schedule, dead-lettered after attempt 7, ordered per (subscriber, record) lane, fenced against late finishers, bounded by the chain-depth counter, traced and metered.

**Architecture:** Registration verbs add internal `ExtensionRegistration` singletons (the `CelFunctionRegistration` precedent). An `ExtensionBinder` compiles them against each primed `PolicyCatalog` (CEL `when` in `CelProfile.Condition`, the #325 image rule per verb, `events.exclude`, id grammars and collisions, DI resolvability) — strictly at boot, where every problem is refused at once in one `AlvoStartupRefusedException`. The outbox dispatcher runs `DeliveryFanOut` **first** for each claimed entry: it judges each bound handler's `when` over the envelope and inserts one `alvo_deliveries` row per match (`UNIQUE (event_id, subscriber)` makes re-fan-out a no-op; a throwing `when` is a `dead` `when-failed` row; `chaindepth + 1 > MaxDepth` is a `dead` `chain-depth-exceeded` row); a storage failure stops only that entry's partition key. A new `DeliveryDispatcher` claims lane heads of registered subscribers through the new `IDeliveryStore` port (one `UPDATE … RETURNING`, EvalPlanQual-safe), and `DeliveryRunner` runs each in its own DI scope under `AlvoFlow` (depth = envelope depth + 1, causation = event id) as `AlvoContext.System(event tenant)` or the event's actor, completing or failing **fenced** on `(claimed_by, attempts)`. `IAlvoEventPump` gains the delivery step in the same round and refuses a re-entrant drain.

**Tech Stack:** .NET 10, C#, xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, Verify + PublicApiGenerator, Testcontainers (PostgreSQL), `System.Diagnostics.ActivitySource` / `Meter`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.TimeProvider.Testing` (via `MMLib.Alvo.TestHost`), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-03* (scope + acceptance (1)–(19)); §4.2 (registration, boot validation); §4.6 (provenance, *who adds +1*, fan-out refusal R-M5); §4.8 (tables, fan-out first, claim, worker model, semantics, metrics); §4.14 (span link); §5 rows marked `03`; §3.1 Q7; §3.3 E3, E8, E12; §2.3 X5, X6, X14; §4.15 *HX-02 settled details* (round steps, stub `SuppressFlow`, the source); §6.2 (HX-03 is security-core); Appendix A B2, M1, M5, M14, M20, M22, M24, R-M2, R-M5, R-M6, R-6, R-10, R-11. Issue **#380** (`[HX-03]`, sub-issue of epic #370). Base: `main` (HX-01 and HX-02 are merged; the branch carries a merge of `origin/main`, `9c51a6a0`).

## Global Constraints

- Branch `hx/03-event-handlers`, on `main` (HX-01 and HX-02 merged; `origin/main` merged in at `9c51a6a0`); the PR targets `main`, and every review diff is `origin/main...HEAD`. One writer per worktree; never `git add -A` (stage each task's files by path); verify `git branch --show-current` is `hx/03-event-handlers` before every commit.
- **Scope is §7 HX-03 only.** No catalog/Management API/dashboard (HX-04), no typed layer, lambdas, `EventCondition<T>`, `AlvoEventAttribute` or `AddEventHandler<TMessage,THandler>` (HX-05), no redeliver/replay/pause (HX-06), no write unit or `IAlvoTransactions` (HX-07), no write hooks or `AlvoHookOptions` (HX-09), no timers/schedules or job pool (HX-13), no steps/`Concurrency`/`Debounce` (HX-14). Each deferral is recorded in the spec amendment (Task 14).
- **Public surface added is exactly:** core / `MMLib.Alvo.Extensions` — `AlvoEntityBuilder`, `AlvoHandlerOptions`, `AlvoRetryPolicy`, `AlvoRetirement`, `AlvoRunAs`, `AlvoOrdering`, `AlvoDeliveryOptions`; core / `Microsoft.Extensions.DependencyInjection` — `AlvoExtensionBuilderExtensions` (`Entity`, `Entities`, `AddEventHandler(id, eventType, Delegate, when, configure)`, `AddEventHandler<THandler>(id, eventType, when, configure)`, `AddRetiredExtension`); core / `MMLib.Alvo.Events` — `IAlvoEventHandler<TMessage>`, `AlvoCreated<T>`, `AlvoUpdated<T>`, `AlvoDeleted<T>`, `AlvoSaved<T>`, `AlvoEventContext`; Abstractions / `MMLib.Alvo.Data` — `IAlvoSession`, `IAlvoSessions`; Abstractions / `MMLib.Alvo.Events` — `IDeliveryStore`, `DeliveryRequest`, `DeliveryClaim`, `ClaimedDelivery`, `DeliveryAttempt`, `DeliveryFailure` (all `required init`, none positional), `IOutboxStore.HoldBackAsync`, and `"alvo"` in `AlvoEventName.ReservedNamespaces`; Testing — `DeliveryStoreContractTests`, `IDeliveryStoreWorld`. Everything else `internal` — including `EfCoreDeliveryStore`.
- **Delivery numbers, verbatim from the spec:** retry delays 5 s, 30 s, 2 min, 15 min, 1 h, 6 h, ±20 % jitter, `dead` after attempt 7 (X5); `Alvo:Deliveries:MaxConcurrency` default 16; handler timeout default 30 s per registration; lease = timeout + 30 s; statuses `pending|running|succeeded|retrying|dead|superseded|cancelled`; error types `when-failed`, `chain-depth-exceeded`, `timeout`, `event-missing`, `lease-expired`, `retired`, else the exception's type name (never its message).
- **Tables (identical DDL on both engines, names via `AlvoFrameworkTables`):** `alvo_deliveries (id, event_id, subscriber, partition_key, tenant_id, kind, status, attempts, not_before, lease_until, claimed_by, completed_at, last_error_type, UNIQUE (event_id, subscriber))`, `ix_<table>_claim (status, not_before)`, `ix_<table>_lane (subscriber, partition_key, event_id)`, `alvo_delivery_attempts (delivery_id, attempt, started_at, duration_ms, outcome, error_type, PRIMARY KEY (delivery_id, attempt))`. No bodies, URLs or exception messages are stored; the envelope stays in `alvo_outbox`.
- **Depth (§4.6):** `DeliveryRunner` enters `AlvoFlow` with `Depth = chaindepth + 1`, `CausationId = event id`, `CorrelationId = the event's`, and **no** `TraceParent` (so writes inside a handler are traced under the delivery span). Fan-out refuses `chaindepth + 1 > MaxDepth` with a `dead` `chain-depth-exceeded` row per matching subscriber and an Error log `ChainDepthExceeded`. `AlvoFlow.Enter` is called only from `MMLib.Alvo` (HX-01's arch fact; this PR is its first production setter).
- **Spans (§4.14, controller carry):** `alvo.delivery` keeps the **ambient** parent (the test host's drain, or none) and carries the envelope's `traceparent` as an `ActivityLink` — never as its parent, or `AlvoHookRecorder` (scoped by the drain's trace id) counts zero.
- **The R-12 loop fact stays.** `test/_shared/testhost/StubWebhookReceiver.cs` keeps `ExecutionContext.SuppressFlow()`; do not delete or weaken `A_receiver_that_writes_back_is_reported_as_a_loop`. The outbox dispatcher's declarative after-hook actions do **not** enter `AlvoFlow` in this PR (§4.6 names `DeliveryDispatcher`; declarative actions move onto deliveries in HX-15).
- **SQL text scan.** `SqlTextConfinedToRendererArchitectureTests` scans `src/MMLib.Alvo/{Expressions,Rules,Events}` and `src/MMLib.Alvo.Abstractions/{Expressions,Rules,Events}`: no file there (code, strings or XML docs) may contain `SELECT `, `WHERE `, `ILIKE`, `COALESCE(`, `IS NOT NULL`, `CASE WHEN `, ` IN (` or the whole upper-case words `AND`, `OR`, `NOT`. All delivery SQL lives in the EF driver.
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). `.cs` files are **UTF-8 with BOM + CRLF**: after creating or editing `.cs` files with any tool run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` naming **file paths** (a directory formats 0 files). `.md`/`.mdx`/`.json`/`.csproj` stay LF, no BOM.
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR.
- A moved `*.verified.*` snapshot or a grown `PublicApi.*.verified.txt` trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added public symbol against `alvo-architecture-rules` (*public is the contract*) and the public-surface line above.
- **Security core:** spec §6.2 lists HX-03 — the PR carries `needs-deep-review`, the `alvo-security-core-review` checklist and a `/security-review` substitute (Task 15).
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`.

## Review Focus

1. **A delivery span parented on the envelope's `traceparent`** would move every handler run out of the test host's drain trace, and `Hooks.AssertRan` would count 0 — tests `The_delivery_span_links_the_envelope_trace_and_keeps_the_ambient_parent` (Task 9) and `A_handler_on_an_event_with_a_foreign_trace_still_counts_in_the_recorder` (Task 12).
2. **A handler that drains** (`IAlvoEventPump.DrainAsync` / `AlvoTestHost.DrainEventsAsync` from inside a round) must be refused immediately with a sentence, not hang on the round gate — test `A_handler_that_drains_is_refused_instead_of_hanging` (Task 10).
3. **Two dispatchers in one process** (two test hosts over one database, or a reclaimed lease of the same process) must not complete each other's claims — the claimant is per dispatcher instance and completion is fenced on `attempts` — tests `A_late_finisher_whose_lease_was_reclaimed_changes_nothing` (Task 3) and `A_handler_killed_mid_delivery_runs_again_with_the_same_event_and_the_late_finisher_changes_nothing` (Task 12).
4. **A host stopping while a handler runs** must record no attempt and leave the claim for its lease — not a failed attempt, not a dead letter — test `A_shutdown_mid_handler_records_nothing_and_leaves_the_claim` (Task 9).
5. **A runtime apply (or a dashboard-first host's first catalog) that a handler no longer fits** must not crash the pump, bind against a stale catalog, or drop the handler's events silently: the handler is unbound with an Error log and every event it would select is recorded as a dead `unbound` delivery — tests `A_catalog_that_unbinds_a_handler_logs_it_and_binds_the_rest` (Task 7) and `An_event_a_handler_unbound_from_the_catalog_would_select_records_a_dead_unbound_delivery` (Task 8).

---

## File structure

Created:

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs` | public: `IAlvoSession`, `IAlvoSessions` — data access bound to one caller |
| `src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs` | public port + its five records |
| `src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs`, `DeliveryStoreContractTests.cs` | the suite every delivery store inherits |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs` | DDL and every delivery statement |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs` | internal relational `IDeliveryStore` |
| `src/MMLib.Alvo/Extensions/AlvoRunAs.cs`, `AlvoOrdering.cs`, `AlvoRetirement.cs`, `AlvoRetryPolicy.cs`, `AlvoHandlerOptions.cs`, `AlvoDeliveryOptions.cs`, `AlvoEntityBuilder.cs` | public builder surface |
| `src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs` | public verbs (namespace `Microsoft.Extensions.DependencyInjection`) |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs` | registrations, sources, settings |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionRegistrations.cs`, `ExtensionRegistry.cs`, `ExtensionIds.cs`, `HandledEventType.cs` | registration helpers |
| `src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs` | delegate / class handler binding and invocation |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs` | bound handlers and selection |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionBindingCompiler.cs` | boot validation (all problems at once) |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs`, `ExtensionRefusal.cs`, `ExtensionLog.cs` | per-catalog cache, refusal, logs |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionStateGuard.cs` | boot: retire, refuse orphaned pending work |
| `src/MMLib.Alvo/Extensions/Internal/AlvoSessions.cs`, `AlvoDeliveryOptionsConfiguration.cs` | session impl, options binding |
| `src/MMLib.Alvo/Events/IAlvoEventHandler.cs`, `AlvoEventMessages.cs`, `AlvoEventContext.cs` | public handler port and messages |
| `src/MMLib.Alvo/Events/Internal/EnvelopeReading.cs` | data-event type, caller-as-at-the-event, change-aware evaluation (extracted from `EventSubscriptions`) |
| `src/MMLib.Alvo/Events/Internal/HandlerCondition.cs`, `DeliveryFanOut.cs`, `DeliveryVocabulary.cs` | fan-out |
| `src/MMLib.Alvo/Events/Internal/EventActor.cs`, `HandlerMessage.cs`, `DeliveryBackoff.cs`, `DeliveryRunner.cs`, `AlvoDeliveryMetrics.cs`, `DeliveryLog.cs` | one delivery |
| `src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs` | loop, pool, manual round |
| `test/_shared/ef/DeliveryStoreWorld.cs`; `test/_shared/testhost/EventHandlerFacts.cs`, `DeliveryRows.cs`, `descriptors/handler-desk.alvo.json`, `descriptors/tenant-desk.alvo.json`; `test/_shared/deliveries/DeliveryChaosCriteriaTests.cs` | shared engine facts |
| `website/src/content/docs/extending/event-handlers.mdx`; `website/src/snippets/extending/handlers/*` | *React to changes (event handlers)* |

Modified: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs`, `Events/AlvoEventName.cs`; `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs`, `AlvoEfCoreProvider.cs`; `src/MMLib.Alvo/Extensions/Setup.cs`, `Events/Setup.cs`, `Events/Internal/{EventSubscriptions,OutboxDispatcher,EventPump,DrainRound,AlvoHookTelemetry}.cs`, `Events/IAlvoEventPump.cs` (remarks), `AlvoServiceCollectionExtensions.cs`, `Migrations/Internal/DescriptorBootPlan.cs`; tests named per task; `tools/MMLib.Alvo.DocsGen/CSharp/ApiScope.cs`; `docs/architecture/{events,extensibility,package-boundary}.md`; `CHANGELOG.md`; `docs/PLAN.md`; the spec.

---

## Task 1: The reserved `alvo` namespace (E12)

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Events/AlvoEventName.cs`
- Modify: `test/MMLib.Alvo.Tests/Events/EventPatternTests.cs`, `test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs`
- Modify: `docs/architecture/events.md` (two lines)
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` (unchanged — a set's content is not API)

**Interfaces:**
- Produces: `AlvoEventName.ReservedNamespaces` = `{entity, auth, storage, alvo}`; `internal const string AlvoEventName.FrameworkNamespace = "alvo"`; `internal static bool AlvoEventName.IsWellFormed(string type)` (used by Task 6's `HandledEventType`).

- [ ] **Step 1: Write the failing tests**

In `test/MMLib.Alvo.Tests/Events/EventPatternTests.cs`, replace the fact `The_reserved_namespaces_are_the_schema_s_own` (keep its remarks, change the summary's first line to "**The reserved set is the schema's own first alternation, plus the framework's `alvo`.**") with:

```csharp
    [Fact]
    public void The_reserved_namespaces_are_the_schema_s_own_plus_the_framework_s()
        => AlvoEventName.ReservedNamespaces.Order(StringComparer.Ordinal).ToList().ShouldBe(
            [.. NamespacesTheSchemaAdmits().Append("alvo").Order(StringComparer.Ordinal)],
            customMessage:
                "A host is refused the namespaces $defs/eventPattern admits, so it cannot forge an event a descriptor "
                + "rule subscribes to, and 'alvo', where the framework mints schedule runs and command completions");

    [Fact]
    public void No_descriptor_rule_can_subscribe_to_the_framework_namespace()
        => NamespacesTheSchemaAdmits().ShouldNotContain(
            "alvo", "framework events are for host handlers; the frozen descriptor grammar does not reach them");
```

In `test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs`, add two `InlineData` rows to `Publish_refuses_a_reserved_namespace`:

```csharp
    [InlineData("alvo.schedule.roll_slots")]
    [InlineData("alvo.command.issue_invoice.completed")]
```

and add after that theory:

```csharp
    /// <summary>
    /// A framework event cannot be published by a host (spec 2026-10-14 E12): it would start every handler of a schedule or
    /// a command as though it had really run.
    /// </summary>
    [Fact]
    public async Task Publish_refuses_a_framework_event_naming_what_it_would_forge()
    {
        var store = new RecordingOutboxStore();

        var refusal = await Should.ThrowAsync<ArgumentException>(() => Publisher(store).PublishAsync(
            "alvo.schedule.x", "jobs/1", null, Caller, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("reserved 'alvo' namespace");
        refusal.Message.ShouldContain("schedule");
        store.Appended.ShouldBeEmpty();
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EventPatternTests' --filter-class '*AlvoEventsTests'`
Expected: FAIL — the set has three members; `alvo.schedule.x` is published.

- [ ] **Step 3: Implement**

In `src/MMLib.Alvo.Abstractions/Events/AlvoEventName.cs`:

Replace the `<summary>` of `ReservedNamespaces` with `The namespaces only Alvo itself emits into: the three the descriptor's event grammar subscribes to, and <c>alvo</c>, where the framework mints the events a registration produces.`; in its first remarks paragraph replace "These are exactly the namespaces …" sentence's fact name `The_reserved_namespaces_are_the_schema_s_own` with `The_reserved_namespaces_are_the_schema_s_own_plus_the_framework_s` and append to that paragraph: `<c>alvo</c> is the one addition: a schedule's run and a command's completion are minted there, so a host must not mint them, and no descriptor rule can subscribe to them.` Then replace the property and add the constant and the predicate:

```csharp
    public static FrozenSet<string> ReservedNamespaces { get; } =
        new[] { "entity", "auth", "storage", FrameworkNamespace }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The namespace of the events Alvo mints from a registration — a schedule's run, a command's completion.</summary>
    internal const string FrameworkNamespace = "alvo";

    /// <summary>Whether <paramref name="type"/> is two or more lower-case dot-separated segments, and nothing else.</summary>
    internal static bool IsWellFormed(string type) => CustomName().IsMatch(type);
```

Replace the body of `EnsureNotReserved` after the early `return` and add the two message builders:

```csharp
        throw new ArgumentException(
            first == FrameworkNamespace ? FrameworkNamespaceRefusal(type) : DataNamespaceRefusal(type, first),
            parameterName);
    }

    private static string DataNamespaceRefusal(string type, string first) =>
        $"Event name '{type}' is in the framework's reserved '{first}' namespace, which only Alvo itself "
        + $"may publish into. A published '{first}.' event would be indistinguishable from a real data "
        + "change: every descriptor rule and after-hook subscribing to that name would fire on it, with a "
        + "partition key and provenance for a record nobody wrote. Publish it under a name of your own — "
        + $"'orders.approved' rather than '{type}' — and note that no descriptor rule can subscribe to "
        + "such a name yet, because $defs/eventPattern is frozen to the reserved namespaces.";

    private static string FrameworkNamespaceRefusal(string type) =>
        $"Event name '{type}' is in the framework's reserved '{FrameworkNamespace}' namespace, where Alvo mints the "
        + "events a registration produces — a schedule's run, a command's completion. A published "
        + $"'{FrameworkNamespace}.' event would start every handler of that schedule or command as though it had "
        + $"really run. Publish it under a name of your own — 'orders.approved' rather than '{type}'.";
```

In `EnsureWellFormed`, replace `if (CustomName().IsMatch(type))` with `if (IsWellFormed(type))`.

In `docs/architecture/events.md`: in the *Publishing a custom application event* diagram change `(entity | auth | storage)` to `(entity | auth | storage | alvo)`; at the end of *The wildcard ruling*'s paragraph that names `EventPatternTests.The_reserved_namespaces_are_the_schema_s_own`, rename the fact to `…_plus_the_framework_s` and add: "`alvo` is reserved beside the schema's three (HX-03, spec 2026-10-14 E12): the framework mints `alvo.schedule.<id>` and `alvo.command.<name>.completed` there, a host may not, and `$defs/eventPattern` does not admit it — framework events reach host handlers only."

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Events/AlvoEventName.cs test/MMLib.Alvo.Tests/Events/EventPatternTests.cs test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EventPatternTests' --filter-class '*AlvoEventsTests' --filter-class '*SqlTextConfinedToRendererArchitectureTests'` and `dotnet test --project test/MMLib.Alvo.Abstractions.Tests`
Expected: PASS; the Abstractions public-API baseline is unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Events/AlvoEventName.cs test/MMLib.Alvo.Tests/Events/EventPatternTests.cs test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs docs/architecture/events.md
git commit -m "feat(events): reserve the alvo namespace for framework-minted events

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: `IAlvoSession` and `IAlvoSessions`

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs`
- Create: `src/MMLib.Alvo/Extensions/Internal/AlvoSessions.cs`
- Modify: `src/MMLib.Alvo/Extensions/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/AlvoSessionsTests.cs`
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Produces: `public interface IAlvoSession { AlvoContext Context { get; } QueryAsync(AlvoQuery, ct); GetAsync(string, Guid, ct); CreateAsync(string, IReadOnlyDictionary<string, object?>, ct); UpdateAsync(string, Guid, values, AlvoPrecondition? = null, ct); ReplaceAsync(string, Guid, values, AlvoPrecondition? = null, ct); DeleteAsync(string, Guid, AlvoPrecondition? = null, ct) }`; `public interface IAlvoSessions { IAlvoSession For(AlvoContext context); }`; internal `AlvoSessions(IAlvoData)` registered `TryAddSingleton<IAlvoSessions, AlvoSessions>()`.

- [ ] **Step 1: Write the failing test**

`test/MMLib.Alvo.Tests/Extensions/AlvoSessionsTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

using NSubstitute;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// A session is <see cref="IAlvoData"/> with the caller fixed: every call runs as the bound context, and no call can
/// carry an idempotency key of its own (spec 2026-10-14 §5, M12).
/// </summary>
public sealed class AlvoSessionsTests
{
    private static readonly AlvoContext _caller = AlvoContext.System(new TenantId(Guid.Parse("11111111-1111-1111-1111-111111111111")));
    private static readonly Guid _id = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal) { ["title"] = "x" };

    private readonly IAlvoData _data = Substitute.For<IAlvoData>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_session_reports_the_caller_it_was_bound_to() =>
        new AlvoSessions(_data).For(_caller).Context.ShouldBeSameAs(_caller);

    [Fact]
    public async Task Reads_run_as_the_bound_caller()
    {
        var session = new AlvoSessions(_data).For(_caller);
        var query = new AlvoQuery { Entity = "tickets" };

        await session.QueryAsync(query, Ct);
        await session.GetAsync("tickets", _id, Ct);

        await _data.Received(1).QueryAsync(query, _caller, Ct);
        await _data.Received(1).GetAsync("tickets", _id, _caller, Ct);
    }

    [Fact]
    public async Task Writes_run_as_the_bound_caller_with_no_idempotency_key()
    {
        var session = new AlvoSessions(_data).For(_caller);
        var precondition = new AlvoPrecondition(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));

        await session.CreateAsync("tickets", _values, Ct);
        await session.UpdateAsync("tickets", _id, _values, precondition, Ct);
        await session.ReplaceAsync("tickets", _id, _values, cancellationToken: Ct);
        await session.DeleteAsync("tickets", _id, cancellationToken: Ct);

        await _data.Received(1).CreateAsync("tickets", _values, _caller, null, Ct);
        await _data.Received(1).UpdateAsync("tickets", _id, _values, _caller, precondition, null, Ct);
        await _data.Received(1).ReplaceAsync("tickets", _id, _values, _caller, null, null, Ct);
        await _data.Received(1).DeleteAsync("tickets", _id, _caller, null, null, Ct);
    }

    [Fact]
    public void A_session_needs_a_caller() =>
        Should.Throw<ArgumentNullException>(() => new AlvoSessions(_data).For(null!));
}
```

`AlvoPrecondition` is a `readonly record struct (DateTimeOffset Version)`, so NSubstitute matches it by value.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoSessionsTests'`
Expected: FAIL — `IAlvoSession`, `AlvoSessions` do not exist (build error).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// <see cref="IAlvoData"/> bound to one caller: every read and write runs as <see cref="Context"/>, through the same
/// policy, tenancy and chain-depth checks, and each call commits on its own.
/// </summary>
/// <remarks>
/// <para>
/// What host code running on Alvo's behalf is handed as its data access — an event handler's
/// <c>AlvoEventContext.Data</c> — so it cannot pass a different caller by accident. There is no transaction spanning two
/// calls, and no call takes an idempotency key: a handler deduplicates on its event's id.
/// </para>
/// <para>
/// The exceptions are <see cref="IAlvoData"/>'s, family for family.
/// </para>
/// </remarks>
public interface IAlvoSession
{
    /// <summary>Gets the caller every call runs as.</summary>
    AlvoContext Context { get; }

    /// <summary>Lists one entity's records visible to <see cref="Context"/>.</summary>
    /// <param name="query">The entity, filter, sort and page.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The page.</returns>
    Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reads one record visible to <see cref="Context"/>, or <see langword="null"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The record, or <see langword="null"/> when it does not exist or is not visible.</returns>
    Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates a record as <see cref="Context"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="values">The fields to write.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The stored record.</returns>
    Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);

    /// <summary>Updates the named fields of a record as <see cref="Context"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="values">The fields to change.</param>
    /// <param name="precondition">An optional version the record must still have.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The stored record.</returns>
    Task<AlvoRecord> UpdateAsync(
        string entity,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a record, or creates it under <paramref name="id"/>, as <see cref="Context"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="values">The complete record.</param>
    /// <param name="precondition">An optional version the record must still have.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>Whether the record was created or replaced, and its stored form.</returns>
    Task<AlvoReplaceResult> ReplaceAsync(
        string entity,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a record as <see cref="Context"/>.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="id">The record's id.</param>
    /// <param name="precondition">An optional version the record must still have.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the record is gone.</returns>
    Task DeleteAsync(
        string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default);
}

/// <summary>Hands out <see cref="IAlvoSession"/>s, one per caller.</summary>
public interface IAlvoSessions
{
    /// <summary>Binds a session to <paramref name="context"/>.</summary>
    /// <param name="context">The caller every call of the session runs as.</param>
    /// <returns>The session.</returns>
    IAlvoSession For(AlvoContext context);
}
```

`src/MMLib.Alvo/Extensions/Internal/AlvoSessions.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>The one <see cref="IAlvoSessions"/>: a thin binding over the registered <see cref="IAlvoData"/>.</summary>
internal sealed class AlvoSessions(IAlvoData data) : IAlvoSessions
{
    /// <inheritdoc/>
    public IAlvoSession For(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AlvoSession(data, context);
    }
}

/// <summary>One caller's view of <see cref="IAlvoData"/>; every call passes the bound context and no idempotency key.</summary>
internal sealed class AlvoSession(IAlvoData data, AlvoContext context) : IAlvoSession
{
    public AlvoContext Context => context;

    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) =>
        data.QueryAsync(query, context, cancellationToken);

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) =>
        data.GetAsync(entity, id, context, cancellationToken);

    public Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        data.CreateAsync(entity, values, context, idempotency: null, cancellationToken);

    public Task<AlvoRecord> UpdateAsync(
        string entity,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default) =>
        data.UpdateAsync(entity, id, values, context, precondition, idempotency: null, cancellationToken);

    public Task<AlvoReplaceResult> ReplaceAsync(
        string entity,
        Guid id,
        IReadOnlyDictionary<string, object?> values,
        AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default) =>
        data.ReplaceAsync(entity, id, values, context, precondition, idempotency: null, cancellationToken);

    public Task DeleteAsync(
        string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        data.DeleteAsync(entity, id, context, precondition, idempotency: null, cancellationToken);
}
```

In `src/MMLib.Alvo/Extensions/Setup.cs`, add `using MMLib.Alvo.Data;` is already present; add before `return services;` in `AddAlvoExtensions`:

```csharp
        services.TryAddSingleton<IAlvoSessions, AlvoSessions>();
```

- [ ] **Step 4: Run tests and accept the baseline**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs src/MMLib.Alvo/Extensions/Internal/AlvoSessions.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/AlvoSessionsTests.cs`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoSessionsTests'` (PASS) and `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'` — FAIL with a diff adding exactly `IAlvoSession` and `IAlvoSessions`; accept it by copying the `.received.txt` over the `.verified.txt`, re-run (PASS). The turn-review gate will ask for `alvo-snapshot-judge` and the architecture-rules justification: both interfaces are §5's HX-03 row.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs src/MMLib.Alvo/Extensions/Internal/AlvoSessions.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/AlvoSessionsTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(data): IAlvoSession, data access bound to one caller

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---
## Task 3: The `IDeliveryStore` port and its contract suite

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs`
- Create: `src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs`, `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.cs`
- Create: `test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs`, `test/MMLib.Alvo.Tests/Events/FakeDeliveryStoreTests.cs`
- Baselines: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`, `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Produces (public, `MMLib.Alvo.Events`) — every record is `sealed` with `required init` properties, never positional, so a field can be added without breaking a provider that builds or reads one: `DeliveryRequest { required Guid EventId; required string Subscriber; required string PartitionKey; string? TenantId; required string Kind; string? DeadReason }`; `DeliveryClaim { required string Claimant; required IReadOnlyCollection<string> Ordered; IReadOnlyCollection<string> Unordered = []; required int BatchSize; required TimeSpan Lease; required int OutboxMaxAttempts }`; `ClaimedDelivery { required Guid Id; required Guid EventId; required string Subscriber; required string PartitionKey; required int Attempts; string? Payload }`; `DeliveryAttempt { required ClaimedDelivery Delivery; required string Claimant; required DateTimeOffset StartedAt; required TimeSpan Duration }`; `DeliveryFailure { required string ErrorType; TimeSpan? RetryAfter; required int MaxAttempts }`; `IDeliveryStore { EnsureAsync; Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest>, ct); Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim, ct); Task<bool> CompleteAsync(DeliveryAttempt, ct); Task<bool> FailAsync(DeliveryAttempt, DeliveryFailure, ct); Task<IReadOnlyList<string>> PendingSubscribersAsync(ct); Task<int> DeadLetterPendingAsync(string subscriber, string reason, ct); Task<int> CancelPendingAsync(string subscriber, ct) }`.
- Produces (public, `MMLib.Alvo.Testing.Events`): `IDeliveryStoreWorld { IDeliveryStore Store; void Advance(TimeSpan); Task<Guid> AppendEventAsync(string partitionKey); Task<Guid> AppendUnfannedEventAsync(string partitionKey, int attempts = 0); Task MarkDispatchedAsync(Guid eventId); }`, `abstract class DeliveryStoreContractTests { protected abstract Task<IDeliveryStoreWorld> WorldAsync(); protected virtual void EnsureEngineAvailable(); }`.
- Produces (test-internal, `MMLib.Alvo.Tests.Events`): `FakeDeliveryStore(TimeProvider time)` implementing the port, plus `void Publish(AlvoEvent @event, bool dispatched = true, int attempts = 0)`, `void MarkDispatched(Guid eventId)`, `int FanOutFailures { get; set; }` (throw that many times), `IReadOnlyList<FakeDeliveryStore.Row> Rows`, `IReadOnlyList<(Guid Delivery, int Attempt, string Outcome)> Attempts`; `ManualClock : TimeProvider` with `Advance(TimeSpan)`.

- [ ] **Step 1: Write the port** (the contract suite needs it to compile)

`src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs`:

```csharp
namespace MMLib.Alvo.Events;

/// <summary>One subscriber's copy of one event, as the fan-out asks the store to record it.</summary>
/// <remarks>
/// Init-only properties rather than a positional constructor, here and on every record of this port: a provider builds
/// and reads these, and a member added later must not change a constructor or a <c>Deconstruct</c> it compiled against.
/// </remarks>
public sealed record DeliveryRequest
{
    /// <summary>Gets the outbox entry the delivery is for; its envelope stays in the outbox.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the registration's id.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets the event's partition key — the lane the delivery is ordered in.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Gets the event's tenant, or <see langword="null"/> for a global row or a tenantless event.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets what runs the delivery: <c>handler</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets why the delivery is recorded <c>dead</c> instead of <c>pending</c> — a condition that threw, a chain too
    /// deep, a handler not bound — or <see langword="null"/> for a delivery to run.
    /// </summary>
    public string? DeadReason { get; init; }
}

/// <summary>What one claim asks for: whose deliveries, how many, and for how long.</summary>
public sealed record DeliveryClaim
{
    /// <summary>Gets who claims; recorded on each row and checked again when the attempt is answered for.</summary>
    public required string Claimant { get; init; }

    /// <summary>
    /// Gets the subscribers whose lanes are ordered: a delivery is claimable only when no earlier delivery of its lane is
    /// unfinished or dead, and no earlier outbox entry of its partition key is still waiting to be fanned out.
    /// </summary>
    public required IReadOnlyCollection<string> Ordered { get; init; }

    /// <summary>Gets the subscribers that opted out of per-record order; their deliveries are claimable whatever came before.</summary>
    public IReadOnlyCollection<string> Unordered { get; init; } = [];

    /// <summary>Gets the most deliveries this claim takes.</summary>
    public required int BatchSize { get; init; }

    /// <summary>Gets how long the claim holds before another claimant may take the delivery back.</summary>
    public required TimeSpan Lease { get; init; }

    /// <summary>
    /// Gets the outbox's attempt ceiling: an earlier outbox entry that reached it will never be fanned out, so it no longer
    /// holds an ordered lane back.
    /// </summary>
    public required int OutboxMaxAttempts { get; init; }
}

/// <summary>One claimed delivery, with the envelope it is for.</summary>
public sealed record ClaimedDelivery
{
    /// <summary>Gets the delivery's own id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the event it delivers.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the registration that runs it.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets its lane.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Gets how many times it has been claimed, <b>including</b> this claim.</summary>
    public required int Attempts { get; init; }

    /// <summary>Gets the stored envelope, or <see langword="null"/> when the outbox no longer holds it.</summary>
    public string? Payload { get; init; }
}

/// <summary>One finished attempt, as the claimant answers for it.</summary>
public sealed record DeliveryAttempt
{
    /// <summary>Gets the delivery as it was claimed.</summary>
    public required ClaimedDelivery Delivery { get; init; }

    /// <summary>Gets who claimed it; the store records nothing unless the row is still this claimant's, at this attempt.</summary>
    public required string Claimant { get; init; }

    /// <summary>Gets when the attempt started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets how long it ran.</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>Why an attempt failed and what happens next.</summary>
public sealed record DeliveryFailure
{
    /// <summary>Gets a type name or a fixed reason — never an exception message.</summary>
    public required string ErrorType { get; init; }

    /// <summary>Gets how long until the next attempt, or <see langword="null"/> when the failure is not worth retrying.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Gets the attempt ceiling: a failure at this attempt or later is dead whatever <see cref="RetryAfter"/> says.</summary>
    public required int MaxAttempts { get; init; }
}

/// <summary>
/// The durable per-subscriber delivery state behind event handlers: one row per (event, subscriber), claimed under a
/// lease, ordered per lane, retried, dead-lettered, and answered for only by the claimant that still holds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One database with the outbox.</b> A claim returns each delivery's envelope from the outbox it was fanned out of, so
/// an implementation serves this port and <see cref="IOutboxStore"/> over one store.
/// </para>
/// <para>
/// <b>Each member is one statement, or a write-first transaction</b> — never a read followed by a write in one
/// transaction, the shape <see cref="IOutboxStore"/> measured to fail on SQLite. A claim is a single conditional update
/// whose outer condition repeats the claimability test, so a second claimant blocked behind the first claims nothing
/// rather than the same rows.
/// </para>
/// <para>
/// <b>Lanes.</b> A delivery is claimable when it is pending, or retrying and due, or running with its lease expired; its
/// subscriber is one the claim names; and — for an ordered subscriber — no delivery of the same subscriber and partition
/// key with an earlier event id is pending, running, retrying or dead, <b>and no outbox entry of the same partition key
/// with an earlier id is still undispatched below the outbox's attempt ceiling</b>. The second half is what keeps a record's
/// order across outbox batches, a failed fan-out and a dispatcher that died between claiming an entry and fanning it out:
/// a later event's delivery cannot overtake an earlier event that has not been fanned out yet. A dead delivery blocks its
/// lane until it is redelivered or retired.
/// </para>
/// <para>
/// <b>Fencing.</b> <see cref="CompleteAsync"/> and <see cref="FailAsync"/> change the row only while it is still running
/// under the same claimant and the same attempt count, and answer whether they did. A late finisher whose lease was
/// reclaimed changes nothing.
/// </para>
/// </remarks>
public interface IDeliveryStore
{
    /// <summary>Brings the delivery storage up; idempotent and safe from several processes at once.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the storage exists.</returns>
    Task EnsureAsync(CancellationToken cancellationToken = default);

    /// <summary>Records one delivery per request; a request whose (event, subscriber) pair already exists is skipped.</summary>
    /// <param name="requests">The deliveries one event fans out to, all recorded together or not at all.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>How many rows were new.</returns>
    Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>Claims up to <see cref="DeliveryClaim.BatchSize"/> lane heads for the claimant.</summary>
    /// <param name="claim">Whose deliveries, how many, for how long.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The claimed deliveries, ascending by event id and then subscriber.</returns>
    Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default);

    /// <summary>Marks the attempt succeeded and records it, if the claimant still holds the delivery.</summary>
    /// <param name="attempt">The finished attempt.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the claim still held; <see langword="false"/> changed nothing.</returns>
    Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the attempt failed and records it, if the claimant still holds the delivery: retrying after
    /// <see cref="DeliveryFailure.RetryAfter"/>, or dead when there is none or the attempt reached the ceiling.
    /// </summary>
    /// <param name="attempt">The finished attempt.</param>
    /// <param name="failure">Why it failed and what happens next.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the claim still held; <see langword="false"/> changed nothing.</returns>
    Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default);

    /// <summary>The subscribers with work left — a delivery pending, running or retrying — in ordinal order.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The subscriber ids.</returns>
    Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default);

    /// <summary>Ends a subscriber's unfinished deliveries as dead with <paramref name="reason"/>, so they stay inspectable.</summary>
    /// <param name="subscriber">The subscriber id.</param>
    /// <param name="reason">The reason recorded as each delivery's error type.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>How many deliveries were ended.</returns>
    Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default);

    /// <summary>Cancels a subscriber's unfinished deliveries.</summary>
    /// <param name="subscriber">The subscriber id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>How many deliveries were cancelled.</returns>
    Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Write the contract suite (the failing tests)**

`src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Testing.Events;

/// <summary>An <see cref="IDeliveryStore"/> over live storage, plus what its own surface cannot do: append an outbox entry, move time.</summary>
public interface IDeliveryStoreWorld : IAsyncDisposable
{
    /// <summary>Gets the store under test, over storage that already exists.</summary>
    IDeliveryStore Store { get; }

    /// <summary>Moves the store's clock forward by <paramref name="duration"/>.</summary>
    /// <param name="duration">How far time moves.</param>
    void Advance(TimeSpan duration);

    /// <summary>
    /// Appends one outbox entry under <paramref name="partitionKey"/>, already dispatched — the state an event is in once
    /// it was fanned out — and returns its id; a later call returns a later id.
    /// </summary>
    /// <param name="partitionKey">The entry's partition key.</param>
    /// <returns>The event id.</returns>
    Task<Guid> AppendEventAsync(string partitionKey);

    /// <summary>
    /// Appends one outbox entry that has not been fanned out yet — undispatched, with <paramref name="attempts"/> claims
    /// behind it — and returns its id.
    /// </summary>
    /// <param name="partitionKey">The entry's partition key.</param>
    /// <param name="attempts">How many times the outbox already claimed it.</param>
    /// <returns>The event id.</returns>
    Task<Guid> AppendUnfannedEventAsync(string partitionKey, int attempts = 0);

    /// <summary>Marks an outbox entry dispatched, as the outbox does once it fanned the entry out.</summary>
    /// <param name="eventId">The entry's id.</param>
    /// <returns>A task that completes when the entry is marked.</returns>
    Task MarkDispatchedAsync(Guid eventId);
}
```

`src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.cs`:

```csharp
using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// The delivery protocol every <see cref="IDeliveryStore"/> implements identically on every engine: fan-out, claim,
/// per-lane order, retry, the attempt ceiling, lease recovery and fenced completion.
/// </summary>
/// <remarks>Inherited unchanged by both shipped drivers and by any external one; everything engine-specific arrives through <see cref="WorldAsync"/>.</remarks>
public abstract class DeliveryStoreContractTests
{
    private const string Kind = "handler";
    private const string Claimant = "worker-1";
    private const string Confirm = "send-confirmation";
    private const string Audit = "audit-trail";
    private const string KeyA = "tickets:a";
    private const string KeyB = "tickets:b";
    private const int MaxAttempts = 7;
    private const int OutboxCeiling = 10;

    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _retry = TimeSpan.FromSeconds(5);

    /// <summary>Builds a store over fresh, already-created storage.</summary>
    /// <returns>The world.</returns>
    protected abstract Task<IDeliveryStoreWorld> WorldAsync();

    /// <summary>Skips the test when the engine is unavailable on this runner; a no-op for an in-process engine.</summary>
    protected virtual void EnsureEngineAvailable()
    {
    }

    /// <summary>A fan-out records one pending delivery per subscriber; a claim takes them with their envelope.</summary>
    [Fact]
    public async Task A_fan_out_records_one_delivery_per_subscriber_and_a_claim_takes_them_with_their_envelope()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA), Request(@event, Audit, KeyA)], Ct)).ShouldBe(2);
        var claimed = await world.Store.ClaimAsync(Claim(Confirm, Audit), Ct);

        claimed.Select(delivery => delivery.Subscriber).ShouldBe([Audit, Confirm]);
        claimed.ShouldAllBe(delivery => delivery.EventId == @event && delivery.Attempts == 1);
        claimed.ShouldAllBe(delivery => delivery.Payload != null && delivery.Payload.Contains(@event.ToString()));
    }

    /// <summary>Re-fanning one event out records nothing: the (event, subscriber) pair is unique.</summary>
    [Fact]
    public async Task A_second_fan_out_of_one_event_records_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct)).ShouldBe(0);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
    }

    /// <summary>A claim takes only the subscribers it names — the replica's registered ones.</summary>
    [Fact]
    public async Task A_claim_takes_only_the_subscribers_it_names()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA), Request(@event, Audit, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().Subscriber.ShouldBe(Confirm);
    }

    /// <summary>A lane is claimed one event at a time, in event order.</summary>
    [Fact]
    public async Task A_lane_is_claimed_one_event_at_a_time_in_event_order()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        var second = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(second, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(first, Confirm, KeyA)], Ct);

        var head = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        head.EventId.ShouldBe(first);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("the head is running, so the lane waits");

        (await world.Store.CompleteAsync(Attempt(head), Ct)).ShouldBeTrue();
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(second);
    }

    /// <summary>A dead delivery blocks the later deliveries of its lane, and of its lane only.</summary>
    [Fact]
    public async Task A_dead_delivery_blocks_its_own_lane_and_no_other()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var deadA = await world.AppendEventAsync(KeyA);
        var laterA = await world.AppendEventAsync(KeyA);
        var laterB = await world.AppendEventAsync(KeyB);
        await world.Store.FanOutAsync([Request(deadA, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(laterA, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(laterB, Confirm, KeyB)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(laterB);
    }

    /// <summary>An unordered subscriber is not blocked by a dead earlier delivery of its lane.</summary>
    [Fact]
    public async Task An_unordered_subscriber_is_not_blocked_by_a_dead_earlier_delivery()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var dead = await world.AppendEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(dead, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        var claim = new DeliveryClaim { Claimant = Claimant, Ordered = [], Unordered = [Confirm], BatchSize = 10, Lease = _lease, OutboxMaxAttempts = OutboxCeiling };

        (await world.Store.ClaimAsync(claim, Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>A failed delivery waits for its retry; once due it is claimed again, with the attempt counted.</summary>
    [Fact]
    public async Task A_failed_delivery_waits_for_its_retry_before_it_is_claimed_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var first = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.FailAsync(Attempt(first), Failure("System.TimeoutException", _retry), Ct)).ShouldBeTrue();

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("not before its retry is due");
        world.Advance(_retry + TimeSpan.FromSeconds(1));
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().Attempts.ShouldBe(2);
    }

    /// <summary>A delivery whose last attempt fails is dead, never claimed again, and still blocks its lane.</summary>
    [Fact]
    public async Task A_delivery_is_dead_once_its_last_attempt_fails()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
            claimed.EventId.ShouldBe(@event);
            claimed.Attempts.ShouldBe(attempt);
            await world.Store.FailAsync(Attempt(claimed), Failure("boom", _retry), Ct);
            world.Advance(_retry + TimeSpan.FromSeconds(1));
        }

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("dead after attempt 7, and the dead head blocks its lane");
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBe([Confirm], "the later event still waits");
    }

    /// <summary>A failure with no retry is dead at once.</summary>
    [Fact]
    public async Task A_failure_without_a_retry_is_dead_at_once()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var claimed = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        await world.Store.FailAsync(Attempt(claimed), Failure("chain-depth-exceeded", null), Ct);
        world.Advance(TimeSpan.FromDays(1));

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>A dead request is recorded dead: never claimed, and not pending work.</summary>
    [Fact]
    public async Task A_dead_request_is_recorded_dead()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);

        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA) with { DeadReason = "chain-depth-exceeded" }], Ct)).ShouldBe(1);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>A running delivery whose lease expired is claimed again — the crash-recovery path.</summary>
    [Fact]
    public async Task A_running_delivery_whose_lease_expired_is_claimed_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        await world.Store.ClaimAsync(Claim(Confirm) with { Claimant = "dead-worker" }, Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("the lease still holds");
        world.Advance(_lease + TimeSpan.FromSeconds(1));

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().Attempts.ShouldBe(2);
    }

    /// <summary>A late finisher whose lease was reclaimed changes nothing; the reclaiming claimant's answer stands.</summary>
    [Fact]
    public async Task A_late_finisher_whose_lease_was_reclaimed_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var late = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        world.Advance(_lease + TimeSpan.FromSeconds(1));
        var current = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.CompleteAsync(Attempt(late), Ct)).ShouldBeFalse("same claimant, older attempt");
        (await world.Store.FailAsync(Attempt(late), Failure("boom", null), Ct)).ShouldBeFalse();
        (await world.Store.CompleteAsync(Attempt(current), Ct)).ShouldBeTrue();
        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
    }

    /// <summary>A completed delivery is final.</summary>
    [Fact]
    public async Task A_completed_delivery_is_never_claimed_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        await world.Store.CompleteAsync(Attempt((await world.Store.ClaimAsync(Claim(Confirm), Ct)).Single()), Ct);

        world.Advance(TimeSpan.FromDays(1));

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
    }

    /// <summary>A claim returns lane heads in event order, and takes no more than its batch.</summary>
    [Fact]
    public async Task A_claim_returns_lane_heads_in_event_order_up_to_its_batch()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var events = new List<Guid>();
        foreach (var key in new[] { "tickets:1", "tickets:2", "tickets:3" })
        {
            events.Add(await world.AppendEventAsync(key));
        }

        for (var index = events.Count - 1; index >= 0; index--)
        {
            await world.Store.FanOutAsync([Request(events[index], Confirm, $"tickets:{index + 1}")], Ct);
        }

        (await world.Store.ClaimAsync(Claim(Confirm) with { BatchSize = 2 }, Ct)).Select(delivery => delivery.EventId)
            .ShouldBe(events.Take(2));
    }

    /// <summary>Retiring ends a subscriber's unfinished work: dead-lettered or cancelled, and no longer pending.</summary>
    [Fact]
    public async Task Retiring_ends_a_subscriber_s_unfinished_work()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA), Request(@event, Audit, KeyA)], Ct);

        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBe([Audit, Confirm]);
        (await world.Store.DeadLetterPendingAsync(Confirm, "retired", Ct)).ShouldBe(1);
        (await world.Store.CancelPendingAsync(Audit, Ct)).ShouldBe(1);

        (await world.Store.PendingSubscribersAsync(Ct)).ShouldBeEmpty();
        (await world.Store.ClaimAsync(Claim(Confirm, Audit), Ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// An ordered delivery waits for every earlier event of its record the outbox has not fanned out yet — a failed fan-out,
    /// a later batch, a dispatcher that died between claim and fan-out — and goes once that event is dispatched.
    /// </summary>
    [Fact]
    public async Task An_ordered_delivery_waits_for_an_earlier_event_of_its_record_that_is_not_fanned_out_yet()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var earlier = await world.AppendUnfannedEventAsync(KeyA, attempts: 1);
        var later = await world.AppendEventAsync(KeyA);
        var otherKey = await world.AppendEventAsync(KeyB);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(otherKey, Confirm, KeyB)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(otherKey, "only the other record's lane is free");
        await world.MarkDispatchedAsync(earlier);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>An earlier event the outbox gave up on (at its attempt ceiling) will never be fanned out, so it blocks nothing.</summary>
    [Fact]
    public async Task An_earlier_event_past_the_outbox_ceiling_holds_no_lane_back()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.AppendUnfannedEventAsync(KeyA, attempts: OutboxCeiling);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>An unordered subscriber does not wait for an earlier event that is not fanned out.</summary>
    [Fact]
    public async Task An_unordered_subscriber_does_not_wait_for_an_earlier_unfanned_event()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.AppendUnfannedEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);

        var claim = Claim() with { Unordered = [Confirm] };

        (await world.Store.ClaimAsync(claim, Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>A delivery whose envelope the outbox no longer holds is still claimed — with no payload, for the runner to dead-letter.</summary>
    [Fact]
    public async Task A_delivery_whose_outbox_entry_is_gone_is_claimed_without_a_payload()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.FanOutAsync([Request(Guid.NewGuid(), Confirm, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().Payload.ShouldBeNull();
    }

    /// <summary>A cancelled delivery is finished: it holds its lane back no more than a succeeded one does.</summary>
    [Fact]
    public async Task A_cancelled_delivery_does_not_block_its_lane()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var cancelled = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(cancelled, Audit, KeyA)], Ct);
        await world.Store.CancelPendingAsync(Audit, Ct);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(later, Audit, KeyA)], Ct);

        (await world.Store.ClaimAsync(Claim(Audit), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>A dead request stays dead: fanning the same (event, subscriber) out again records nothing.</summary>
    [Fact]
    public async Task A_second_fan_out_does_not_revive_a_dead_delivery()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);

        (await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct)).ShouldBe(0);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty();
    }

    private static DeliveryRequest Request(Guid @event, string subscriber, string key) =>
        new() { EventId = @event, Subscriber = subscriber, PartitionKey = key, Kind = Kind };

    private static DeliveryClaim Claim(params string[] ordered) =>
        new() { Claimant = Claimant, Ordered = ordered, BatchSize = 10, Lease = _lease, OutboxMaxAttempts = OutboxCeiling };

    private static DeliveryAttempt Attempt(ClaimedDelivery delivery) =>
        new() { Delivery = delivery, Claimant = Claimant, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMilliseconds(5) };

    private static DeliveryFailure Failure(string errorType, TimeSpan? retryAfter) =>
        new() { ErrorType = errorType, RetryAfter = retryAfter, MaxAttempts = MaxAttempts };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
```

Note the lease-expiry fact claims as `dead-worker`, then reclaims as `worker-1`; the fencing fact reclaims with the **same** claimant, so the fence that matters is the attempt count.

- [ ] **Step 3: Write the in-memory fake and run it against the suite**

`test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The delivery store in memory, held to <see cref="Testing.Events.DeliveryStoreContractTests"/> by
/// <see cref="FakeDeliveryStoreTests"/>, so the core's dispatcher facts run against a store with the real semantics.
/// </summary>
internal sealed class FakeDeliveryStore(TimeProvider time) : IDeliveryStore
{
    private static readonly string[] _unfinished = ["pending", "running", "retrying"];
    private static readonly string[] _blocking = ["pending", "running", "retrying", "dead"];

    private readonly Lock _gate = new();
    private readonly List<Row> _rows = [];
    private readonly Dictionary<Guid, string> _payloads = [];
    private readonly Dictionary<Guid, (string Key, bool Dispatched, int Attempts)> _outbox = [];
    private readonly List<(Guid Delivery, int Attempt, string Outcome)> _attempts = [];

    internal int FanOutFailures { get; set; }

    internal IReadOnlyList<Row> Rows
    {
        get
        {
            lock (_gate)
            {
                return [.. _rows.Select(row => row with { })];
            }
        }
    }

    internal IReadOnlyList<(Guid Delivery, int Attempt, string Outcome)> Attempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _attempts];
            }
        }
    }

    internal void Publish(AlvoEvent @event, bool dispatched = true, int attempts = 0)
    {
        lock (_gate)
        {
            _payloads[@event.Id] = AlvoEventJson.Write(@event);
            _outbox[@event.Id] = (@event.PartitionKey, dispatched, attempts);
        }
    }

    internal void MarkDispatched(Guid eventId)
    {
        lock (_gate)
        {
            _outbox[eventId] = _outbox[eventId] with { Dispatched = true };
        }
    }

    public Task EnsureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (FanOutFailures > 0)
            {
                FanOutFailures--;
                throw new InvalidOperationException("The fake delivery store refused this fan-out.");
            }

            var fresh = requests.Where(request => !_rows.Exists(row => row.EventId == request.EventId && row.Subscriber == request.Subscriber)).ToList();
            _rows.AddRange(fresh.Select(NewRow));

            return Task.FromResult(fresh.Count);
        }
    }

    public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var heads = _rows.Where(row => Claimable(row, claim, now))
                .OrderBy(row => row.EventId.ToString(), StringComparer.Ordinal)
                .ThenBy(row => row.Subscriber, StringComparer.Ordinal)
                .Take(claim.BatchSize)
                .ToList();

            return Task.FromResult<IReadOnlyList<ClaimedDelivery>>([.. heads.Select(row => Claim(row, claim, now))]);
        }
    }

    public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default) =>
        Task.FromResult(Answer(attempt, row => row.Status = "succeeded", "succeeded"));

    public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default)
    {
        var dead = failure.RetryAfter is null || attempt.Delivery.Attempts >= failure.MaxAttempts;

        return Task.FromResult(Answer(attempt, row => Fail(row, failure, dead), dead ? "dead" : "failed"));
    }

    public Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                [.. _rows.Where(row => _unfinished.Contains(row.Status)).Select(row => row.Subscriber).Distinct().Order(StringComparer.Ordinal)]);
        }
    }

    public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(End(subscriber, "dead", reason));

    public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
        Task.FromResult(End(subscriber, "cancelled", "retired"));

    private Row NewRow(DeliveryRequest request) => new()
    {
        Id = AlvoEventId.Create(time.GetUtcNow()),
        EventId = request.EventId,
        Subscriber = request.Subscriber,
        PartitionKey = request.PartitionKey,
        TenantId = request.TenantId,
        Status = request.DeadReason is null ? "pending" : "dead",
        LastErrorType = request.DeadReason,
        NotBefore = time.GetUtcNow(),
    };

    private bool Claimable(Row row, DeliveryClaim claim, DateTimeOffset now) =>
        (claim.Ordered.Contains(row.Subscriber) || claim.Unordered.Contains(row.Subscriber))
        && Due(row, now)
        && (claim.Unordered.Contains(row.Subscriber) || !Blocked(row, claim));

    private static bool Due(Row row, DateTimeOffset now) =>
        (row.Status is "pending" or "retrying" && row.NotBefore <= now)
        || (row.Status == "running" && row.LeaseUntil < now);

    private bool Blocked(Row row, DeliveryClaim claim) =>
        _rows.Exists(earlier =>
            earlier.Subscriber == row.Subscriber
            && earlier.PartitionKey == row.PartitionKey
            && Before(earlier.EventId, row.EventId)
            && _blocking.Contains(earlier.Status))
        || _outbox.Any(entry =>
            entry.Value.Key == row.PartitionKey
            && Before(entry.Key, row.EventId)
            && !entry.Value.Dispatched
            && entry.Value.Attempts < claim.OutboxMaxAttempts);

    private static bool Before(Guid earlier, Guid later) => string.CompareOrdinal(earlier.ToString(), later.ToString()) < 0;

    private ClaimedDelivery Claim(Row row, DeliveryClaim claim, DateTimeOffset now)
    {
        row.Status = "running";
        row.ClaimedBy = claim.Claimant;
        row.Attempts++;
        row.LeaseUntil = now + claim.Lease;

        return new ClaimedDelivery
        {
            Id = row.Id,
            EventId = row.EventId,
            Subscriber = row.Subscriber,
            PartitionKey = row.PartitionKey,
            Attempts = row.Attempts,
            Payload = _payloads.GetValueOrDefault(row.EventId),
        };
    }

    private bool Answer(DeliveryAttempt attempt, Action<Row> change, string outcome)
    {
        lock (_gate)
        {
            var row = _rows.SingleOrDefault(candidate => candidate.Id == attempt.Delivery.Id
                && candidate.ClaimedBy == attempt.Claimant
                && candidate.Attempts == attempt.Delivery.Attempts
                && candidate.Status == "running");
            if (row is null)
            {
                return false;
            }

            change(row);
            row.LeaseUntil = null;
            _attempts.Add((row.Id, row.Attempts, outcome));

            return true;
        }
    }

    private void Fail(Row row, DeliveryFailure failure, bool dead)
    {
        row.Status = dead ? "dead" : "retrying";
        row.LastErrorType = failure.ErrorType;
        row.NotBefore = time.GetUtcNow() + (failure.RetryAfter ?? TimeSpan.Zero);
    }

    private int End(string subscriber, string status, string reason)
    {
        lock (_gate)
        {
            var ended = _rows.Where(row => row.Subscriber == subscriber && _unfinished.Contains(row.Status)).ToList();
            foreach (var row in ended)
            {
                row.Status = status;
                row.LastErrorType = reason;
                row.LeaseUntil = null;
            }

            return ended.Count;
        }
    }

    internal sealed record Row
    {
        public Guid Id { get; init; }

        public Guid EventId { get; init; }

        public string Subscriber { get; init; } = string.Empty;

        public string PartitionKey { get; init; } = string.Empty;

        public string? TenantId { get; init; }

        public string Status { get; set; } = "pending";

        public int Attempts { get; set; }

        public DateTimeOffset NotBefore { get; set; }

        public DateTimeOffset? LeaseUntil { get; set; }

        public string? ClaimedBy { get; set; }

        public string? LastErrorType { get; set; }
    }
}

/// <summary>A clock a fact moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    internal void Advance(TimeSpan duration) => _now += duration;
}
```

`test/MMLib.Alvo.Tests/Events/FakeDeliveryStoreTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>Holds the core's in-memory delivery store to the same contract the engines keep.</summary>
public sealed class FakeDeliveryStoreTests : DeliveryStoreContractTests
{
    protected override Task<IDeliveryStoreWorld> WorldAsync() => Task.FromResult<IDeliveryStoreWorld>(new World());

    private sealed class World : IDeliveryStoreWorld
    {
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        private readonly FakeDeliveryStore _store;

        internal World() => _store = new FakeDeliveryStore(_clock);

        public IDeliveryStore Store => _store;

        public void Advance(TimeSpan duration) => _clock.Advance(duration);

        public Task<Guid> AppendEventAsync(string partitionKey) => AppendAsync(partitionKey, dispatched: true, attempts: 0);

        public Task<Guid> AppendUnfannedEventAsync(string partitionKey, int attempts = 0) => AppendAsync(partitionKey, dispatched: false, attempts);

        public Task MarkDispatchedAsync(Guid eventId)
        {
            _store.MarkDispatched(eventId);
            return Task.CompletedTask;
        }

        private Task<Guid> AppendAsync(string partitionKey, bool dispatched, int attempts)
        {
            var @event = new AlvoEvent
            {
                Id = AlvoEventId.Create(_clock.GetUtcNow()),
                Source = AlvoEvent.DefaultSource,
                Type = "entity.tickets.updated",
                Time = _clock.GetUtcNow(),
                Subject = partitionKey.Replace(':', '/'),
                PartitionKey = partitionKey,
                AuthType = AlvoEventAuthType.ApiKey,
                CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
                Data = new AlvoEventData { Record = AlvoRecord.Empty },
            };
            _store.Publish(@event, dispatched, attempts);

            return Task.FromResult(@event.Id);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Run, then accept the two baselines**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStoreTests.cs`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FakeDeliveryStoreTests' --filter-class '*SqlTextConfinedToRendererArchitectureTests'`
Expected: 21 contract facts PASS (fix the fake, never the suite, if one fails — the suite is the contract). Then `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'` and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`: each diff adds exactly the port + five records (Abstractions) and `DeliveryStoreContractTests` + `IDeliveryStoreWorld` (Testing); accept both, re-run, PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStoreTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "feat(events): IDeliveryStore and the delivery contract suite

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 4: The delivery tables and `EfCoreDeliveryStore`, on both engines **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs` (two suffixes, `NamesFor`)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`
- Create: `test/_shared/ef/DeliveryStoreWorld.cs`, `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDeliveryStoreTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryStoreTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs`
- Modify: `test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/MMLib.Alvo.Data.PostgreSql.Tests.Integration.csproj` (link the world), `test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs`
- Modify: `docs/architecture/package-boundary.md` (*What a database provider must implement to boot*)

**Interfaces:**
- Consumes: Task 3's port and suite.
- Produces: `internal const string AlvoFrameworkTables.DeliveriesSuffix = "_deliveries"`, `DeliveryAttemptsSuffix = "_delivery_attempts"`; `internal static class DeliveryTable` (`NameFor`, `AttemptsNameFor`, `EnsureAsync(DbConnection, string table, string attempts, string outbox, ct)`, `ClaimSql(string table, string outbox, int ordered, int unordered)`, `OutboxKeyIndexDdl(string outbox)`, the status constants); `internal sealed class EfCoreDeliveryStore(RelationalConnectionFactory, AlvoOptions, TimeProvider) : IDeliveryStore` registered `TryAddSingleton<IDeliveryStore>` by every relational provider.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs`: rename the second fact to `The_reserved_set_is_the_six_bookkeeping_tables_plus_the_seven_identity_ones` and change `ShouldBe(11)` to `ShouldBe(13)`; add:

```csharp
    [Fact]
    public void The_delivery_tables_are_reserved()
    {
        var names = AlvoFrameworkTables.NamesFor("alvo");

        names.ShouldContain("alvo_deliveries");
        names.ShouldContain("alvo_delivery_attempts");
    }
```

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using System.Text.RegularExpressions;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The delivery statements' shape where behaviour cannot reach it: the claim repeats its claimability test outside the
/// subquery, which is what makes a second PostgreSQL claimant blocked behind the first claim nothing (EvalPlanQual).
/// </summary>
public sealed class DeliveryTableTests
{
    private const string Table = "alvo_deliveries";
    private const string Outbox = "alvo_outbox";

    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void The_tables_are_named_from_the_framework_table_list()
    {
        DeliveryTable.NameFor("alvo").ShouldBe("alvo_deliveries");
        DeliveryTable.AttemptsNameFor("alvo").ShouldBe("alvo_delivery_attempts");
        SystemSchemaInitializer.FrameworkTableNames("alvo").ShouldContain(DeliveryTable.NameFor("alvo"));
    }

    [Fact]
    public void One_event_is_delivered_once_per_subscriber() =>
        DeliveryTable.Ddl(Table).ShouldContain("UNIQUE (event_id, subscriber)");

    [Fact]
    public void The_claim_repeats_its_claimability_outside_the_subquery()
    {
        var sql = DeliveryTable.ClaimSql(Table, Outbox, ordered: 2, unordered: 1);

        Regex.Matches(sql, Regex.Escape("status IN ('pending', 'retrying')"), RegexOptions.None, _matchTimeout).Count.ShouldBe(2);
        Regex.Matches(sql, "NOT EXISTS", RegexOptions.None, _matchTimeout).Count.ShouldBe(4, "two lane checks in each half");
        sql.ShouldContain("subscriber IN (@s0, @s1, @s2)");
        sql.ShouldContain("subscriber IN (@s2) OR (NOT EXISTS");
        sql.ShouldContain("LIMIT @batch");
    }

    [Fact]
    public void An_ordered_lane_waits_for_earlier_entries_of_its_key_the_outbox_has_not_dispatched() =>
        DeliveryTable.ClaimSql(Table, Outbox, ordered: 1, unordered: 0)
            .ShouldContain($"FROM {Outbox} unfanned WHERE unfanned.partition_key = {Table}.partition_key");

    [Fact]
    public void A_claim_with_no_unordered_subscriber_orders_every_lane() =>
        DeliveryTable.ClaimSql(Table, Outbox, ordered: 1, unordered: 0).ShouldNotContain("OR (NOT EXISTS");
}
```

`test/_shared/ef/DeliveryStoreWorld.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;

using System.Data.Common;
using System.Globalization;

using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// One <see cref="EfCoreDeliveryStore"/> over a started database, the outbox it reads envelopes from, a hand-moved clock,
/// and the two-claimant race only PostgreSQL's leg runs. Linked into both driver test projects.
/// </summary>
internal sealed class DeliveryStoreWorld : IDeliveryStoreWorld
{
    private const string SchemaPrefix = "alvo";

    private static readonly DateTimeOffset _start = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _blockObservationWindow = TimeSpan.FromMilliseconds(500);

    private readonly Func<DbConnection> _connect;
    private readonly WorldClock _clock = new(_start);
    private readonly string _outbox = OutboxTable.NameFor(SchemaPrefix);
    private readonly string _table = DeliveryTable.NameFor(SchemaPrefix);

    private DeliveryStoreWorld(Func<DbConnection> connect)
    {
        _connect = connect;
        Store = new EfCoreDeliveryStore(new RelationalConnectionFactory(connect), new AlvoOptions { SchemaPrefix = SchemaPrefix }, _clock);
    }

    public IDeliveryStore Store { get; }

    internal static async Task<DeliveryStoreWorld> StartAsync(Func<DbConnection> connect)
    {
        var world = new DeliveryStoreWorld(connect);
        await world.Store.EnsureAsync(Ct);

        return world;
    }

    public void Advance(TimeSpan duration) => _clock.Advance(duration);

    public async Task<Guid> AppendEventAsync(string partitionKey)
    {
        var id = await AppendUnfannedEventAsync(partitionKey);
        await MarkDispatchedAsync(id);

        return id;
    }

    public async Task<Guid> AppendUnfannedEventAsync(string partitionKey, int attempts = 0)
    {
        var id = AlvoEventId.Create(_clock.GetUtcNow());
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            await OutboxTable.InsertAsync(connection, transaction: null, _outbox, EventWith(id, partitionKey), Ct);
            await ExecuteAsync(connection, $"UPDATE {_outbox} SET attempts = @value WHERE id = @id", id, attempts);
        }

        return id;
    }

    public async Task MarkDispatchedAsync(Guid eventId)
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            await ExecuteAsync(connection, $"UPDATE {_outbox} SET dispatched_at = @value WHERE id = @id", eventId, StoredInstant.Text(_clock.GetUtcNow()));
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, Guid id, object value)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            RelationalSqlBatch.AddParameter(command, "@value", value);
            RelationalSqlBatch.AddParameter(command, "@id", id.ToString());
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    /// <summary>
    /// Two claimants racing one lane head with no <c>SKIP LOCKED</c>: A claims inside an open transaction, B's claim
    /// blocks on A's row lock, A commits, and B — which re-checks only the outer condition — must then claim nothing.
    /// </summary>
    internal async Task<(int First, int Second)> TwoConcurrentClaimsAsync(string subscriber)
    {
        var winner = _connect();
        await using (winner.ConfigureAwait(false))
        {
            var loser = _connect();
            await using (loser.ConfigureAwait(false))
            {
                await winner.OpenAsync(Ct);
                await loser.OpenAsync(Ct);

                return await RaceAsync(winner, loser, subscriber);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(int First, int Second)> RaceAsync(DbConnection winner, DbConnection loser, string subscriber)
    {
        var winning = await winner.BeginTransactionAsync(Ct);
        await using (winning.ConfigureAwait(false))
        {
            var first = await ClaimOnAsync(winner, winning, subscriber);
            var losing = await loser.BeginTransactionAsync(Ct);
            await using (losing.ConfigureAwait(false))
            {
                var blocked = ClaimOnAsync(loser, losing, subscriber);
                await Task.Delay(_blockObservationWindow, Ct);
                if (blocked.IsCompleted)
                {
                    throw new InvalidOperationException("The second claimant finished before the first committed; no race was constructed.");
                }

                await winning.CommitAsync(Ct);
                var second = await blocked;
                await losing.CommitAsync(Ct);

                return (first, second);
            }
        }
    }

    private async Task<int> ClaimOnAsync(DbConnection connection, DbTransaction transaction, string subscriber)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = DeliveryTable.ClaimSql(_table, _outbox, ordered: 1, unordered: 0);
            RelationalSqlBatch.AddParameter(command, "@now", StoredInstant.Text(_clock.GetUtcNow()));
            RelationalSqlBatch.AddParameter(command, "@lease_until", StoredInstant.Text(_clock.GetUtcNow() + TimeSpan.FromMinutes(1)));
            RelationalSqlBatch.AddParameter(command, "@claimed_by", "racer");
            RelationalSqlBatch.AddParameter(command, "@batch", 10);
            RelationalSqlBatch.AddParameter(command, "@outbox_max_attempts", 10);
            RelationalSqlBatch.AddParameter(command, "@s0", subscriber);

            return await CountRowsAsync(command);
        }
    }

    private static async Task<int> CountRowsAsync(DbCommand command)
    {
        var reader = await command.ExecuteReaderAsync(Ct);
        await using (reader.ConfigureAwait(false))
        {
            var rows = 0;
            while (await reader.ReadAsync(Ct))
            {
                rows++;
            }

            return rows;
        }
    }

    private AlvoEvent EventWith(Guid id, string partitionKey) => new()
    {
        Id = id,
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.updated",
        Time = _clock.GetUtcNow(),
        Subject = partitionKey.Replace(':', '/'),
        PartitionKey = partitionKey,
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = id.ToString("N", CultureInfo.InvariantCulture),
        Data = new AlvoEventData { Record = new(new Dictionary<string, object?>(StringComparer.Ordinal)) },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class WorldClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
```

`test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDeliveryStoreTests.cs`:

```csharp
using Microsoft.Data.Sqlite;

using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>The whole <see cref="DeliveryStoreContractTests"/> suite against a real SQLite database file, on the shipped connection string.</summary>
public sealed class SqliteDeliveryStoreTests : DeliveryStoreContractTests, IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-delivery-store-tests-{Guid.NewGuid():N}.db");

    protected override async Task<IDeliveryStoreWorld> WorldAsync() =>
        await DeliveryStoreWorld.StartAsync(() => new SqliteConnection($"Data Source={_databasePath};Pooling=False"));

    public void Dispose()
    {
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
```

`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryStoreTests.cs`:

```csharp
using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Data;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The whole <see cref="DeliveryStoreContractTests"/> suite against PostgreSQL, a fresh database per test instance, and
/// the one fact a shape assertion cannot reach: two claimants racing one lane head.
/// </summary>
public sealed class PostgreSqlDeliveryStoreTests : DeliveryStoreContractTests, IClassFixture<PostgresFixture>
{
    private readonly string _connectionString = string.Empty;

    public PostgreSqlDeliveryStoreTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = $"alvo_delivery_store_{Guid.NewGuid():N}";
        using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            command.ExecuteNonQuery();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    [Fact]
    public async Task A_second_claimant_claims_nothing_rather_than_the_same_lane_head()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        var @event = await world.AppendEventAsync("tickets:a");
        await world.Store.FanOutAsync(
            [new DeliveryRequest { EventId = @event, Subscriber = "send-confirmation", PartitionKey = "tickets:a", Kind = "handler" }],
            TestContext.Current.CancellationToken);

        var (first, second) = await world.TwoConcurrentClaimsAsync("send-confirmation");

        first.ShouldBe(1);
        second.ShouldBe(0, "the outer condition re-checks status after the lock clears; without it the row is claimed twice");
    }

    protected override async Task<IDeliveryStoreWorld> WorldAsync() => await ConcreteWorldAsync();

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");

    private Task<DeliveryStoreWorld> ConcreteWorldAsync() =>
        DeliveryStoreWorld.StartAsync(() => new NpgsqlConnection(_connectionString));
}
```

Link the world into both driver projects, next to the existing `OutboxStoreWorld.cs` line in each csproj:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)../_shared/ef/DeliveryStoreWorld.cs" Link="_shared/DeliveryStoreWorld.cs" />
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*DeliveryTableTests'`
Expected: FAIL — `DeliveryTable`, `EfCoreDeliveryStore` do not exist (build error).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs` — after `SecretsSuffix` add:

```csharp
    /// <summary>The suffix of the table holding one row per (event, subscriber) delivery.</summary>
    internal const string DeliveriesSuffix = "_deliveries";

    /// <summary>The suffix of the table holding one row per delivery attempt.</summary>
    internal const string DeliveryAttemptsSuffix = "_delivery_attempts";
```

and in `NamesFor` after `schemaPrefix + SecretsSuffix,` add `schemaPrefix + DeliveriesSuffix,` and `schemaPrefix + DeliveryAttemptsSuffix,`.

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs`:

```csharp
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The delivery tables' DDL and every statement over them — identical on both shipped engines, as the outbox's are.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim's outer condition repeats the subquery's</b> for the outbox's measured reason (spike Q4): under
/// PostgreSQL's <c>READ COMMITTED</c>, a claimant blocked behind another re-checks the outer <c>WHERE</c> once the lock
/// clears, and nothing else. Without the repetition a lane head is claimed twice.
/// </para>
/// <para>
/// <b><c>ON CONFLICT … DO NOTHING</c>, <c>LIMIT</c> and <c>RETURNING</c> are SQLite's and PostgreSQL's spelling, not
/// ANSI.</b> The outbox already spells its claim the same way; the T-SQL dialect question for Alvo's own tables belongs
/// to the Azure SQL provider (#92).
/// </para>
/// </remarks>
internal static class DeliveryTable
{
    internal const string Pending = "pending";
    internal const string Running = "running";
    internal const string Succeeded = "succeeded";
    internal const string Retrying = "retrying";
    internal const string Dead = "dead";
    internal const string Cancelled = "cancelled";
    internal const string FailedOutcome = "failed";

    private const string Claimable =
        "((status IN ('pending', 'retrying') AND not_before <= @now) OR (status = 'running' AND lease_until < @now))";

    private const string Unfinished = "status IN ('pending', 'running', 'retrying')";

    private const string Fence = "id = @id AND claimed_by = @claimed_by AND attempts = @attempts AND status = 'running'";

    internal static string NameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.DeliveriesSuffix;

    internal static string AttemptsNameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.DeliveryAttemptsSuffix;

    internal static string Ddl(string table) =>
        $"""
        CREATE TABLE IF NOT EXISTS {table} (
            id TEXT NOT NULL PRIMARY KEY,
            event_id TEXT NOT NULL,
            subscriber TEXT NOT NULL,
            partition_key TEXT NOT NULL,
            tenant_id TEXT NULL,
            kind TEXT NOT NULL,
            status TEXT NOT NULL,
            attempts INTEGER NOT NULL,
            not_before TEXT NOT NULL,
            lease_until TEXT NULL,
            claimed_by TEXT NULL,
            completed_at TEXT NULL,
            last_error_type TEXT NULL,
            UNIQUE (event_id, subscriber)
        )
        """;

    internal static string ClaimIndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_claim ON {table} (status, not_before)";

    internal static string LaneIndexDdl(string table) =>
        $"CREATE INDEX IF NOT EXISTS ix_{table}_lane ON {table} (subscriber, partition_key, event_id)";

    internal static string AttemptsDdl(string attempts) =>
        $"""
        CREATE TABLE IF NOT EXISTS {attempts} (
            delivery_id TEXT NOT NULL,
            attempt INTEGER NOT NULL,
            started_at TEXT NOT NULL,
            duration_ms INTEGER NOT NULL,
            outcome TEXT NOT NULL,
            error_type TEXT NULL,
            PRIMARY KEY (delivery_id, attempt)
        )
        """;

    /// <summary>
    /// An index on the outbox's (partition key, id), so the lane condition's look at earlier undispatched entries of a key
    /// is a seek — an index, not an <c>ALTER</c> of <c>alvo_outbox</c> (spec E10).
    /// </summary>
    internal static string OutboxKeyIndexDdl(string outbox) =>
        $"CREATE INDEX IF NOT EXISTS ix_{outbox}_key ON {outbox} (partition_key, id)";

    internal static IReadOnlyList<string> Statements(string table, string attempts, string outbox) =>
        [Ddl(table), ClaimIndexDdl(table), LaneIndexDdl(table), AttemptsDdl(attempts), OutboxKeyIndexDdl(outbox)];

    internal static async Task EnsureAsync(DbConnection connection, string table, string attempts, string outbox, CancellationToken ct)
    {
        foreach (var statement in Statements(table, attempts, outbox))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    internal static string FanOutSql(string table, int rows) =>
        $"""
        INSERT INTO {table} (id, event_id, subscriber, partition_key, tenant_id, kind, status, attempts, not_before, completed_at, last_error_type)
        VALUES {string.Join(", ", Enumerable.Range(0, rows).Select(FanOutRow))}
        ON CONFLICT (event_id, subscriber) DO NOTHING
        """;

    private static string FanOutRow(int row) =>
        $"(@id{row}, @event_id{row}, @subscriber{row}, @partition_key{row}, @tenant_id{row}, @kind{row}, @status{row}, 0, @now, @completed_at{row}, @last_error_type{row})";

    internal static string ClaimSql(string table, string outbox, int ordered, int unordered)
    {
        var condition = $"{Claimable} AND {Registered(ordered + unordered)} AND {LaneHead(table, outbox, ordered, unordered)}";

        return
            $"""
            UPDATE {table} SET status = 'running', claimed_by = @claimed_by, attempts = attempts + 1, lease_until = @lease_until
             WHERE {condition}
               AND id IN (SELECT id FROM {table}
                           WHERE {condition}
                           ORDER BY event_id, subscriber
                           LIMIT @batch)
            RETURNING id, event_id, subscriber, partition_key, attempts
            """;
    }

    private static string Registered(int subscribers) => $"subscriber IN ({Parameters(0, subscribers)})";

    private static string LaneHead(string table, string outbox, int ordered, int unordered)
    {
        var noEarlier =
            $"NOT EXISTS (SELECT 1 FROM {table} earlier WHERE earlier.subscriber = {table}.subscriber "
            + $"AND earlier.partition_key = {table}.partition_key AND earlier.event_id < {table}.event_id "
            + "AND earlier.status IN ('pending', 'running', 'retrying', 'dead'))";
        var noUnfanned =
            $"NOT EXISTS (SELECT 1 FROM {outbox} unfanned WHERE unfanned.partition_key = {table}.partition_key "
            + $"AND unfanned.id < {table}.event_id AND unfanned.dispatched_at IS NULL "
            + "AND unfanned.attempts < @outbox_max_attempts)";
        var laneHead = $"{noEarlier} AND {noUnfanned}";

        return unordered == 0 ? laneHead : $"(subscriber IN ({Parameters(ordered, unordered)}) OR ({laneHead}))";
    }

    private static string Parameters(int from, int count) =>
        string.Join(", ", Enumerable.Range(from, count).Select(index => $"@s{index}"));

    internal static string PayloadsSql(string outbox, int events) =>
        $"SELECT id, payload FROM {outbox} WHERE id IN ({string.Join(", ", Enumerable.Range(0, events).Select(index => $"@e{index}"))})";

    internal static string CompleteSql(string table) =>
        $"UPDATE {table} SET status = 'succeeded', completed_at = @now, lease_until = NULL, last_error_type = NULL WHERE {Fence}";

    internal static string FailSql(string table) =>
        $"""
        UPDATE {table} SET status = CASE WHEN @retryable = 0 OR attempts >= @max_attempts THEN 'dead' ELSE 'retrying' END,
                           completed_at = CASE WHEN @retryable = 0 OR attempts >= @max_attempts THEN @now ELSE NULL END,
                           not_before = @not_before, last_error_type = @error_type, lease_until = NULL
         WHERE {Fence}
        """;

    internal static string AttemptSql(string attempts) =>
        $"""
        INSERT INTO {attempts} (delivery_id, attempt, started_at, duration_ms, outcome, error_type)
        VALUES (@id, @attempts, @started_at, @duration_ms, @outcome, @error_type)
        """;

    internal static string PendingSubscribersSql(string table) =>
        $"SELECT DISTINCT subscriber FROM {table} WHERE {Unfinished} ORDER BY subscriber";

    internal static string EndPendingSql(string table) =>
        $"UPDATE {table} SET status = @status, last_error_type = @reason, completed_at = @now, lease_until = NULL WHERE subscriber = @subscriber AND {Unfinished}";
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs`:

```csharp
using MMLib.Alvo.Events;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The relational <see cref="IDeliveryStore"/>: one connection per call, one statement — or, to answer for an attempt, a
/// write-first transaction (the fenced update, then the attempt row) — over <see cref="DeliveryTable"/>'s SQL.
/// </summary>
/// <remarks>
/// <b>Internal</b>: unlike <see cref="EfCoreOutboxStore"/> it is not part of what a host resolves by type; the port is the
/// contract, and a provider author implements that.
/// </remarks>
internal sealed class EfCoreDeliveryStore : IDeliveryStore
{
    private readonly RelationalConnectionFactory _connections;
    private readonly TimeProvider _time;
    private readonly string _table;
    private readonly string _attempts;
    private readonly string _outbox;

    internal EfCoreDeliveryStore(RelationalConnectionFactory connections, AlvoOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _connections = connections;
        _time = time;
        _table = DeliveryTable.NameFor(options.SchemaPrefix);
        _attempts = DeliveryTable.AttemptsNameFor(options.SchemaPrefix);
        _outbox = OutboxTable.NameFor(options.SchemaPrefix);
    }

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            await OutboxTable.EnsureAsync(connection, _outbox, cancellationToken).ConfigureAwait(false);
            await DeliveryTable.EnsureAsync(connection, _table, _attempts, _outbox, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        return requests.Count == 0
            ? Task.FromResult(0)
            : ExecuteAsync(DeliveryTable.FanOutSql(_table, requests.Count), command => BindRequests(command, requests), cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(claim.BatchSize);
        if (claim.Ordered.Count + claim.Unordered.Count == 0)
        {
            return [];
        }

        var claimed = await ClaimRowsAsync(claim, cancellationToken).ConfigureAwait(false);
        var payloads = await PayloadsAsync([.. claimed.Select(delivery => delivery.EventId).Distinct()], cancellationToken).ConfigureAwait(false);

        return [.. claimed
            .Select(delivery => delivery with { Payload = payloads.GetValueOrDefault(delivery.EventId) })
            .OrderBy(delivery => delivery.EventId.ToString(), StringComparer.Ordinal)
            .ThenBy(delivery => delivery.Subscriber, StringComparer.Ordinal)];
    }

    public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        return AnswerAsync(DeliveryTable.CompleteSql(_table), _ => { }, attempt, DeliveryTable.Succeeded, errorType: null, cancellationToken);
    }

    public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(failure);
        var dead = failure.RetryAfter is null || attempt.Delivery.Attempts >= failure.MaxAttempts;

        return AnswerAsync(
            DeliveryTable.FailSql(_table),
            command => BindFailure(command, failure),
            attempt,
            dead ? DeliveryTable.Dead : DeliveryTable.FailedOutcome,
            failure.ErrorType,
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default)
    {
        var subscribers = new List<string>();
        await ReadAsync(DeliveryTable.PendingSubscribersSql(_table), _ => { }, reader => subscribers.Add(reader.GetString(0)), cancellationToken)
            .ConfigureAwait(false);

        return subscribers;
    }

    public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
        EndPendingAsync(subscriber, DeliveryTable.Dead, reason, cancellationToken);

    public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
        EndPendingAsync(subscriber, DeliveryTable.Cancelled, "retired", cancellationToken);

    private Task<int> EndPendingAsync(string subscriber, string status, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return ExecuteAsync(DeliveryTable.EndPendingSql(_table), command =>
        {
            RelationalSqlBatch.AddParameter(command, "@status", status);
            RelationalSqlBatch.AddParameter(command, "@reason", reason);
            RelationalSqlBatch.AddParameter(command, "@now", Now);
            RelationalSqlBatch.AddParameter(command, "@subscriber", subscriber);
        }, ct);
    }

    private async Task<List<ClaimedDelivery>> ClaimRowsAsync(DeliveryClaim claim, CancellationToken ct)
    {
        var claimed = new List<ClaimedDelivery>();
        await ReadAsync(
            DeliveryTable.ClaimSql(_table, _outbox, claim.Ordered.Count, claim.Unordered.Count),
            command => BindClaim(command, claim),
            reader => claimed.Add(ReadClaimed(reader)),
            ct).ConfigureAwait(false);

        return claimed;
    }

    private void BindClaim(DbCommand command, DeliveryClaim claim)
    {
        var now = _time.GetUtcNow();
        RelationalSqlBatch.AddParameter(command, "@now", StoredInstant.Text(now));
        RelationalSqlBatch.AddParameter(command, "@lease_until", StoredInstant.Text(now + claim.Lease));
        RelationalSqlBatch.AddParameter(command, "@claimed_by", claim.Claimant);
        RelationalSqlBatch.AddParameter(command, "@batch", claim.BatchSize);
        RelationalSqlBatch.AddParameter(command, "@outbox_max_attempts", claim.OutboxMaxAttempts);
        var index = 0;
        foreach (var subscriber in claim.Ordered.Concat(claim.Unordered))
        {
            RelationalSqlBatch.AddParameter(command, $"@s{index++}", subscriber);
        }
    }

    private static ClaimedDelivery ReadClaimed(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        EventId = Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        Subscriber = reader.GetString(2),
        PartitionKey = reader.GetString(3),
        Attempts = reader.GetInt32(4),
    };

    private async Task<Dictionary<Guid, string>> PayloadsAsync(IReadOnlyList<Guid> events, CancellationToken ct)
    {
        var payloads = new Dictionary<Guid, string>();
        if (events.Count == 0)
        {
            return payloads;
        }

        await ReadAsync(
            DeliveryTable.PayloadsSql(_outbox, events.Count),
            command => BindEvents(command, events),
            reader => payloads[Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture)] = reader.GetString(1),
            ct).ConfigureAwait(false);

        return payloads;
    }

    private static void BindEvents(DbCommand command, IReadOnlyList<Guid> events)
    {
        for (var index = 0; index < events.Count; index++)
        {
            RelationalSqlBatch.AddParameter(command, $"@e{index}", events[index].ToString());
        }
    }

    private void BindRequests(DbCommand command, IReadOnlyList<DeliveryRequest> requests)
    {
        RelationalSqlBatch.AddParameter(command, "@now", Now);
        for (var row = 0; row < requests.Count; row++)
        {
            BindRequest(command, row, requests[row]);
        }
    }

    private void BindRequest(DbCommand command, int row, DeliveryRequest request)
    {
        var dead = request.DeadReason is not null;
        RelationalSqlBatch.AddParameter(command, $"@id{row}", AlvoEventId.Create(_time.GetUtcNow()).ToString());
        RelationalSqlBatch.AddParameter(command, $"@event_id{row}", request.EventId.ToString());
        RelationalSqlBatch.AddParameter(command, $"@subscriber{row}", request.Subscriber);
        RelationalSqlBatch.AddParameter(command, $"@partition_key{row}", request.PartitionKey);
        RelationalSqlBatch.AddParameter(command, $"@tenant_id{row}", (object?)request.TenantId ?? DBNull.Value);
        RelationalSqlBatch.AddParameter(command, $"@kind{row}", request.Kind);
        RelationalSqlBatch.AddParameter(command, $"@status{row}", dead ? DeliveryTable.Dead : DeliveryTable.Pending);
        RelationalSqlBatch.AddParameter(command, $"@completed_at{row}", dead ? Now : DBNull.Value as object);
        RelationalSqlBatch.AddParameter(command, $"@last_error_type{row}", (object?)request.DeadReason ?? DBNull.Value);
    }

    private void BindFailure(DbCommand command, DeliveryFailure failure)
    {
        RelationalSqlBatch.AddParameter(command, "@retryable", failure.RetryAfter is null ? 0 : 1);
        RelationalSqlBatch.AddParameter(command, "@max_attempts", failure.MaxAttempts);
        RelationalSqlBatch.AddParameter(command, "@not_before", StoredInstant.Text(_time.GetUtcNow() + (failure.RetryAfter ?? TimeSpan.Zero)));
        RelationalSqlBatch.AddParameter(command, "@error_type", failure.ErrorType);
    }

    private async Task<bool> AnswerAsync(
        string sql, Action<DbCommand> bind, DeliveryAttempt attempt, string outcome, string? errorType, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var held = await FencedUpdateAsync(connection, transaction, sql, bind, attempt, ct).ConfigureAwait(false) == 1;
                if (held)
                {
                    await InsertAttemptAsync(connection, transaction, attempt, outcome, errorType, ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return held;
            }
        }
    }

    private async Task<int> FencedUpdateAsync(
        DbConnection connection, DbTransaction transaction, string sql, Action<DbCommand> bind, DeliveryAttempt attempt, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            bind(command);
            BindFence(command, attempt);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private void BindFence(DbCommand command, DeliveryAttempt attempt)
    {
        RelationalSqlBatch.AddParameter(command, "@id", attempt.Delivery.Id.ToString());
        RelationalSqlBatch.AddParameter(command, "@claimed_by", attempt.Claimant);
        RelationalSqlBatch.AddParameter(command, "@attempts", attempt.Delivery.Attempts);
        RelationalSqlBatch.AddParameter(command, "@now", Now);
    }

    private async Task InsertAttemptAsync(
        DbConnection connection, DbTransaction transaction, DeliveryAttempt attempt, string outcome, string? errorType, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = DeliveryTable.AttemptSql(_attempts);
            RelationalSqlBatch.AddParameter(command, "@id", attempt.Delivery.Id.ToString());
            RelationalSqlBatch.AddParameter(command, "@attempts", attempt.Delivery.Attempts);
            RelationalSqlBatch.AddParameter(command, "@started_at", StoredInstant.Text(attempt.StartedAt));
            RelationalSqlBatch.AddParameter(command, "@duration_ms", (int)Math.Min(int.MaxValue, attempt.Duration.TotalMilliseconds));
            RelationalSqlBatch.AddParameter(command, "@outcome", outcome);
            RelationalSqlBatch.AddParameter(command, "@error_type", (object?)errorType ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<int> ExecuteAsync(string sql, Action<DbCommand> bind, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                bind(command);

                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadAsync(string sql, Action<DbCommand> bind, Action<DbDataReader> row, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = sql;
                bind(command);
                await ReadRowsAsync(command, row, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReadRowsAsync(DbCommand command, Action<DbDataReader> row, CancellationToken ct)
    {
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                row(reader);
            }
        }
    }

    private string Now => StoredInstant.Text(_time.GetUtcNow());
}
```

`SystemSchemaInitializer`: add fields beside the others,

```csharp
    private readonly string _deliveriesTableName;
    private readonly string _deliveryAttemptsTableName;
```

set them in the constructor (`_deliveriesTableName = DeliveryTable.NameFor(schemaPrefix); _deliveryAttemptsTableName = DeliveryTable.AttemptsNameFor(schemaPrefix);`), and at the end of `EnsureAsync`:

```csharp
        await CreateIfMissingAsync(
            _deliveriesTableName, DeliveryTable.Ddl(_deliveriesTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _deliveriesTableName, DeliveryTable.ClaimIndexDdl(_deliveriesTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _deliveriesTableName, DeliveryTable.LaneIndexDdl(_deliveriesTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _deliveryAttemptsTableName, DeliveryTable.AttemptsDdl(_deliveryAttemptsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _outboxTableName, DeliveryTable.OutboxKeyIndexDdl(_outboxTableName), ct).ConfigureAwait(false);
```

`AlvoEfCoreProvider.cs`: after `builder.Services.TryAddSingleton<IOutboxStore>(CreateOutboxStore);` add `builder.Services.TryAddSingleton<IDeliveryStore>(CreateDeliveryStore);` and beside `CreateOutboxStore`:

```csharp
    private static EfCoreDeliveryStore CreateDeliveryStore(IServiceProvider services) => new(
        services.GetRequiredService<RelationalConnectionFactory>(),
        services.GetRequiredService<IOptions<AlvoOptions>>().Value,
        services.GetRequiredService<TimeProvider>());
```

`docs/architecture/package-boundary.md`, *What a database provider must implement to boot*, add a bullet:

```markdown
- **`IDeliveryStore` is mandatory from HX-03, over the same database as `IOutboxStore`.** The core's
  `DeliveryDispatcher`, `DeliveryFanOut` and boot guard take it as a constructor dependency and are always registered,
  so a provider without it cannot boot — even a host with no event handler, because the boot refuses pending work an
  unregistered subscriber left behind. A claim returns each delivery's envelope from the outbox, which is why the two
  ports share one store. `AddRelationalProvider` registers it (the internal `EfCoreDeliveryStore`), so no shipped
  provider is affected; `DeliveryStoreContractTests` is what a third-party provider inherits. **The registration is
  `TryAdd`, and `AddRelationalProvider` registers `EfCoreDeliveryStore` for every relational dialect**, whose claim is
  SQLite/PostgreSQL SQL (`ON CONFLICT`, `LIMIT`, `RETURNING`). A dialect that cannot run it — a T-SQL provider — must
  register its own `IDeliveryStore` before calling `AddRelationalProvider`, so its registration wins; the same holds for
  the outbox store. Expect the port to grow before 1.0 (HX-06 reads, HX-13 job kinds, HX-14 steps).
```

- [ ] **Step 4: Run all four suites**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs test/_shared/ef/DeliveryStoreWorld.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDeliveryStoreTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryStoreTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs`, then:
- `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*DeliveryTableTests'` — PASS
- `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FrameworkTableReservationTests'` — PASS
- `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteDeliveryStoreTests' --filter-class '*AddAlvoIntegrationTests'` — PASS (the latter proves both new tables are created at boot and excluded from introspection)
- `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlDeliveryStoreTests'` — PASS, including the race (Docker required)

If a fact fails on one engine only, the statement is the defect: fix `DeliveryTable`, never the suite.

- [ ] **Step 5: Rings and commit**

Run `scripts/test-ring0` then `scripts/test-ring1` (slice end). Expected: green; `PublicApi.MMLib.Alvo.Data.EntityFrameworkCore.verified.txt` unchanged (the store is internal). If a snapshot listing framework tables moved (introspection or migration snapshots), dispatch `alvo-snapshot-judge`: the two added names are this task's.

```bash
git add src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs test/_shared/ef/DeliveryStoreWorld.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDeliveryStoreTests.cs test/MMLib.Alvo.Data.Sqlite.Tests/MMLib.Alvo.Data.Sqlite.Tests.csproj test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryStoreTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/MMLib.Alvo.Data.PostgreSql.Tests.Integration.csproj test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs docs/architecture/package-boundary.md
git commit -m "feat(data): alvo_deliveries and the relational delivery store

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 5: Handler options, messages, the handler port and `AlvoDeliveryOptions`

**Files:**
- Create: `src/MMLib.Alvo/Extensions/AlvoRunAs.cs`, `AlvoOrdering.cs`, `AlvoRetirement.cs`, `AlvoRetryPolicy.cs`, `AlvoHandlerOptions.cs`, `AlvoDeliveryOptions.cs`
- Create: `src/MMLib.Alvo/Extensions/Internal/AlvoDeliveryOptionsConfiguration.cs`
- Create: `src/MMLib.Alvo/Events/IAlvoEventHandler.cs`, `src/MMLib.Alvo/Events/AlvoEventMessages.cs`, `src/MMLib.Alvo/Events/AlvoEventContext.cs`
- Modify: `src/MMLib.Alvo/Extensions/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/AlvoRetryPolicyTests.cs`, `AlvoHandlerOptionsTests.cs`, `AlvoDeliveryOptionsTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `IAlvoSession` (Task 2).
- Produces (public): `enum AlvoRunAs { System = 0, EventActor = 1 }`, `enum AlvoOrdering { PerRecord = 0, None = 1 }`, `enum AlvoRetirement { DeadLetter = 0, Discard = 1 }`; `sealed class AlvoRetryPolicy { static Default; static None; static From(params TimeSpan[]); IReadOnlyList<TimeSpan> Delays; int MaxAttempts; internal TimeSpan? DelayAfter(int attempt) }`; `sealed class AlvoHandlerOptions { string? Description; int Order; TimeSpan Timeout = 30 s; AlvoRunAs RunAs; AlvoOrdering Ordering; AlvoRetryPolicy Retry; internal bool OrderStated; internal static TimeSpan DefaultTimeout }`; `sealed class AlvoDeliveryOptions { const SectionName = "Alvo:Deliveries"; int MaxConcurrency = 16 }`; `interface IAlvoEventHandler<in TMessage> where TMessage : class { Task HandleAsync(TMessage message, AlvoEventContext context, CancellationToken cancellationToken); }`; `sealed record AlvoCreated<T> { required T New }`, `AlvoUpdated<T> { required T Old; required T New }`, `AlvoDeleted<T> { required T Old }`, `AlvoSaved<T> { required T New; T? Old }` (all `where T : class`); `sealed class AlvoEventContext { Guid EventId; AlvoEvent Event; string Subscriber; int Attempt; AlvoContext Caller; IAlvoSession Data; internal AlvoEventContext(AlvoEvent, string, int, IAlvoSession) }`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Extensions/AlvoRetryPolicyTests.cs`:

```csharp
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>The handler retry schedule (spec 2026-10-14 X5): six waits, so the seventh attempt is the last.</summary>
public sealed class AlvoRetryPolicyTests
{
    [Fact]
    public void The_default_waits_are_the_specified_six_and_the_seventh_attempt_is_the_last()
    {
        AlvoRetryPolicy.Default.Delays.ShouldBe([
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)]);
        AlvoRetryPolicy.Default.MaxAttempts.ShouldBe(7);
    }

    [Fact]
    public void No_retry_means_one_attempt() => AlvoRetryPolicy.None.MaxAttempts.ShouldBe(1);

    [Theory]
    [InlineData(1, 5.0)]
    [InlineData(6, 21600.0)]
    public void The_wait_after_an_attempt_is_its_own_delay(int attempt, double seconds) =>
        AlvoRetryPolicy.Default.DelayAfter(attempt).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void There_is_no_wait_after_the_last_attempt() => AlvoRetryPolicy.Default.DelayAfter(7).ShouldBeNull();

    [Fact]
    public void A_policy_keeps_its_own_copy_of_the_delays()
    {
        TimeSpan[] delays = [TimeSpan.FromSeconds(1)];
        var policy = AlvoRetryPolicy.From(delays);

        delays[0] = TimeSpan.FromHours(1);

        policy.Delays.ShouldBe([TimeSpan.FromSeconds(1)]);
    }

    [Fact]
    public void A_negative_delay_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => AlvoRetryPolicy.From(TimeSpan.FromSeconds(-1)));
}
```

`test/MMLib.Alvo.Tests/Extensions/AlvoHandlerOptionsTests.cs`:

```csharp
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Extensions;

public sealed class AlvoHandlerOptionsTests
{
    [Fact]
    public void A_handler_runs_as_the_system_in_order_for_thirty_seconds_with_the_default_retries_unless_told_otherwise()
    {
        var options = new AlvoHandlerOptions();

        options.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.RunAs.ShouldBe(AlvoRunAs.System);
        options.Ordering.ShouldBe(AlvoOrdering.PerRecord);
        options.Retry.ShouldBeSameAs(AlvoRetryPolicy.Default);
        options.Order.ShouldBe(0);
        options.OrderStated.ShouldBeFalse();
    }

    [Fact]
    public void Setting_the_order_states_it_even_when_it_is_zero() =>
        new AlvoHandlerOptions { Order = 0 }.OrderStated.ShouldBeTrue();

    [Fact]
    public void A_handler_cannot_go_without_a_retry_policy() =>
        Should.Throw<ArgumentNullException>(() => new AlvoHandlerOptions { Retry = null! });
}
```

`test/MMLib.Alvo.Tests/Extensions/AlvoDeliveryOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Extensions;

public sealed class AlvoDeliveryOptionsTests
{
    [Fact]
    public void Sixteen_deliveries_run_at_once_by_default()
    {
        using var services = Container(new());

        services.GetRequiredService<IOptions<AlvoDeliveryOptions>>().Value.MaxConcurrency.ShouldBe(16);
    }

    [Fact]
    public void The_concurrency_is_bound_from_its_section()
    {
        using var services = Container(new() { ["Alvo:Deliveries:MaxConcurrency"] = "4" });

        services.GetRequiredService<IOptions<AlvoDeliveryOptions>>().Value.MaxConcurrency.ShouldBe(4);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("257")]
    public void A_concurrency_outside_one_to_256_is_refused_naming_the_key_and_its_variable(string configured)
    {
        using var services = Container(new() { ["Alvo:Deliveries:MaxConcurrency"] = configured });

        var refused = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<AlvoDeliveryOptions>>().Value);

        refused.Message.ShouldContain("Alvo:Deliveries:MaxConcurrency");
        refused.Message.ShouldContain("Alvo__Deliveries__MaxConcurrency");
    }

    private static ServiceProvider Container(Dictionary<string, string?> settings) => new ServiceCollection()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
        .AddAlvoExtensions()
        .BuildServiceProvider();
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoRetryPolicyTests' --filter-class '*AlvoHandlerOptionsTests' --filter-class '*AlvoDeliveryOptionsTests'`
Expected: FAIL (build error: the types do not exist).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Extensions/AlvoRunAs.cs`:

```csharp
namespace MMLib.Alvo.Extensions;

/// <summary>Who an event handler acts as when it reads and writes.</summary>
public enum AlvoRunAs
{
    /// <summary>The system, in the event's tenant — the built-in <c>admin</c> role, never another tenant.</summary>
    System = 0,

    /// <summary>The caller who made the change, as recorded on the event: its id, tenant and the roles it held then.</summary>
    EventActor = 1,
}
```

`src/MMLib.Alvo/Extensions/AlvoOrdering.cs`:

```csharp
namespace MMLib.Alvo.Extensions;

/// <summary>Whether a handler sees one record's events strictly in order.</summary>
public enum AlvoOrdering
{
    /// <summary>One record's events reach the handler in order; a delivery that is dead holds the record's later ones back until it is redelivered or retired.</summary>
    PerRecord = 0,

    /// <summary>Every delivery runs as soon as it is due, whatever happened to the record's earlier ones.</summary>
    None = 1,
}
```

`src/MMLib.Alvo/Extensions/AlvoRetirement.cs`:

```csharp
namespace MMLib.Alvo.Extensions;

/// <summary>What happens, at start-up, to the unfinished work of an extension that is no longer registered.</summary>
public enum AlvoRetirement
{
    /// <summary>Its deliveries become dead with the reason <c>retired</c>, so they stay inspectable.</summary>
    DeadLetter = 0,

    /// <summary>Its deliveries are cancelled.</summary>
    Discard = 1,
}
```

`src/MMLib.Alvo/Extensions/AlvoRetryPolicy.cs`:

```csharp
namespace MMLib.Alvo.Extensions;

/// <summary>How long a failed delivery waits before each retry; one attempt more than there are waits.</summary>
/// <remarks>Each wait is spread by up to a fifth either way, so many deliveries failing together do not retry together.</remarks>
public sealed class AlvoRetryPolicy
{
    private AlvoRetryPolicy(IReadOnlyList<TimeSpan> delays) => Delays = delays;

    /// <summary>Gets the default: 5 s, 30 s, 2 min, 15 min, 1 h and 6 h — seven attempts across about seven hours.</summary>
    public static AlvoRetryPolicy Default { get; } = new([
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)]);

    /// <summary>Gets a policy with no retry: the first failure is final.</summary>
    public static AlvoRetryPolicy None { get; } = new([]);

    /// <summary>Gets the waits, the first after attempt 1.</summary>
    public IReadOnlyList<TimeSpan> Delays { get; }

    /// <summary>Gets the attempt after which a failing delivery is dead.</summary>
    public int MaxAttempts => Delays.Count + 1;

    /// <summary>Builds a policy waiting <paramref name="delays"/> between attempts.</summary>
    /// <param name="delays">The waits, the first after attempt 1; none negative.</param>
    /// <returns>The policy.</returns>
    public static AlvoRetryPolicy From(params TimeSpan[] delays)
    {
        ArgumentNullException.ThrowIfNull(delays);
        foreach (var delay in delays)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero, nameof(delays));
        }

        return new([.. delays]);
    }

    internal TimeSpan? DelayAfter(int attempt) => attempt >= 1 && attempt <= Delays.Count ? Delays[attempt - 1] : null;
}
```

`src/MMLib.Alvo/Extensions/AlvoHandlerOptions.cs`:

```csharp
namespace MMLib.Alvo.Extensions;

/// <summary>How one event handler runs.</summary>
public sealed class AlvoHandlerOptions
{
    private int _order;
    private AlvoRetryPolicy _retry = AlvoRetryPolicy.Default;

    /// <summary>Gets or sets what the handler does, in a sentence, for the dashboard and the docs.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets where the handler sits among those of the same entity and operation; lower first, then registration
    /// order. Deliveries of different handlers still run independently — this orders their fan-out and their listing.
    /// </summary>
    public int Order
    {
        get => _order;
        set
        {
            _order = value;
            OrderStated = true;
        }
    }

    /// <summary>Gets or sets how long one attempt may run before it is cancelled and counted as failed; default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Gets or sets who the handler acts as; default <see cref="AlvoRunAs.System"/>.</summary>
    public AlvoRunAs RunAs { get; set; } = AlvoRunAs.System;

    /// <summary>Gets or sets whether one record's events reach the handler in order; default <see cref="AlvoOrdering.PerRecord"/>.</summary>
    public AlvoOrdering Ordering { get; set; } = AlvoOrdering.PerRecord;

    /// <summary>Gets or sets the retry schedule; default <see cref="AlvoRetryPolicy.Default"/>.</summary>
    public AlvoRetryPolicy Retry
    {
        get => _retry;
        set => _retry = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal bool OrderStated { get; private set; }

    internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);
}
```

`src/MMLib.Alvo/Extensions/AlvoDeliveryOptions.cs`:

```csharp
namespace MMLib.Alvo.Extensions;

/// <summary>How this process runs event-handler deliveries (<c>Alvo:Deliveries</c>), validated when the host starts.</summary>
public sealed class AlvoDeliveryOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Alvo:Deliveries";

    /// <summary>Gets or sets how many deliveries this process runs at once; default 16, from 1 to 256.</summary>
    public int MaxConcurrency { get; set; } = 16;
}
```

`src/MMLib.Alvo/Extensions/Internal/AlvoDeliveryOptionsConfiguration.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Binds <see cref="AlvoDeliveryOptions"/> from its section and refuses a concurrency no pool can run.</summary>
internal sealed class AlvoDeliveryOptionsConfiguration(IConfiguration? configuration)
    : IConfigureOptions<AlvoDeliveryOptions>, IValidateOptions<AlvoDeliveryOptions>
{
    internal const string MaxConcurrencyKey = $"{AlvoDeliveryOptions.SectionName}:{nameof(AlvoDeliveryOptions.MaxConcurrency)}";

    private const int MostConcurrency = 256;

    public void Configure(AlvoDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(AlvoDeliveryOptions.SectionName).Bind(options);
    }

    public ValidateOptionsResult Validate(string? name, AlvoDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.MaxConcurrency is >= 1 and <= MostConcurrency
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"'{options.MaxConcurrency}' is not a usable {MaxConcurrencyKey}. Set it (as an environment variable, "
                + $"{MaxConcurrencyKey.Replace(":", "__", StringComparison.Ordinal)}) to a number from 1 to {MostConcurrency}: "
                + "how many event-handler deliveries this process runs at once.");
    }
}
```

In `src/MMLib.Alvo/Extensions/Setup.cs` add, after the existing `AlvoExtensionOptions` lines:

```csharp
        services.AddOptions<AlvoDeliveryOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoDeliveryOptions>, AlvoDeliveryOptionsConfiguration>(Deliveries));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoDeliveryOptions>, AlvoDeliveryOptionsConfiguration>(Deliveries));
```

and the local function beside `Create`:

```csharp
        static AlvoDeliveryOptionsConfiguration Deliveries(IServiceProvider provider) =>
            new(provider.GetService<IConfiguration>());
```

`src/MMLib.Alvo/Events/IAlvoEventHandler.cs`:

```csharp
namespace MMLib.Alvo.Events;

/// <summary>Reacts to one committed event, after the write that caused it, at least once.</summary>
/// <typeparam name="TMessage">
/// What the handler is handed: <see cref="AlvoCreated{T}"/>, <see cref="AlvoUpdated{T}"/>, <see cref="AlvoDeleted{T}"/> or
/// <see cref="AlvoSaved{T}"/> over <c>AlvoRecord</c> for an entity's events, or <c>AlvoRecord</c> — the event's payload —
/// for an application event.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>At least once.</b> A delivery can run again — after a failure, a timeout, or a process that stopped mid-run — and
/// always with the same <see cref="AlvoEventContext.EventId"/>: deduplicate on it before an effect you cannot repeat.
/// </para>
/// <para>
/// The handler is created per delivery in its own dependency-injection scope. Throwing fails the attempt; it is retried on
/// the handler's schedule and dead after the last attempt. Its exception's message is never stored or logged.
/// </para>
/// </remarks>
public interface IAlvoEventHandler<in TMessage>
    where TMessage : class
{
    /// <summary>Handles one delivery.</summary>
    /// <param name="message">The event's images, or its payload.</param>
    /// <param name="context">The event, the attempt, and data access as the handler's caller.</param>
    /// <param name="cancellationToken">Cancelled at the handler's timeout or when the host stops.</param>
    /// <returns>A task that completes when the delivery is handled.</returns>
    Task HandleAsync(TMessage message, AlvoEventContext context, CancellationToken cancellationToken);
}
```

`src/MMLib.Alvo/Events/AlvoEventMessages.cs`:

```csharp
namespace MMLib.Alvo.Events;

/// <summary>A record was created.</summary>
/// <typeparam name="T">The record's shape: <c>AlvoRecord</c> in the untyped core.</typeparam>
public sealed record AlvoCreated<T>
    where T : class
{
    /// <summary>Gets the record as it was stored.</summary>
    public required T New { get; init; }
}

/// <summary>A record was updated or replaced.</summary>
/// <typeparam name="T">The record's shape: <c>AlvoRecord</c> in the untyped core.</typeparam>
public sealed record AlvoUpdated<T>
    where T : class
{
    /// <summary>Gets the record before the write.</summary>
    public required T Old { get; init; }

    /// <summary>Gets the record as it was stored.</summary>
    public required T New { get; init; }
}

/// <summary>A record was deleted.</summary>
/// <typeparam name="T">The record's shape: <c>AlvoRecord</c> in the untyped core.</typeparam>
public sealed record AlvoDeleted<T>
    where T : class
{
    /// <summary>Gets the record as it was before the delete.</summary>
    public required T Old { get; init; }
}

/// <summary>A record was created or updated.</summary>
/// <typeparam name="T">The record's shape: <c>AlvoRecord</c> in the untyped core.</typeparam>
public sealed record AlvoSaved<T>
    where T : class
{
    /// <summary>Gets the record as it was stored.</summary>
    public required T New { get; init; }

    /// <summary>Gets the record before the write, or <see langword="null"/> when it was created.</summary>
    public T? Old { get; init; }
}
```

`src/MMLib.Alvo/Events/AlvoEventContext.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events;

/// <summary>What one delivery knows besides its message: the event, the attempt, and data access as the handler's caller.</summary>
public sealed class AlvoEventContext
{
    internal AlvoEventContext(AlvoEvent @event, string subscriber, int attempt, IAlvoSession data)
    {
        Event = @event;
        Subscriber = subscriber;
        Attempt = attempt;
        Data = data;
    }

    /// <summary>Gets the event's id — the same on every attempt, so the key to deduplicate on.</summary>
    public Guid EventId => Event.Id;

    /// <summary>Gets the whole envelope, as it was stored.</summary>
    public AlvoEvent Event { get; }

    /// <summary>Gets the handler's registered id.</summary>
    public string Subscriber { get; }

    /// <summary>Gets which attempt this is, starting at 1.</summary>
    public int Attempt { get; }

    /// <summary>Gets the caller the handler acts as.</summary>
    public AlvoContext Caller => Data.Context;

    /// <summary>
    /// Gets data access as <see cref="Caller"/>: every call goes through the same rules and tenancy, and writes made here
    /// carry this event as their cause, one hop deeper in the causation chain.
    /// </summary>
    public IAlvoSession Data { get; }
}
```

- [ ] **Step 4: Run, accept the core baseline**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/AlvoRunAs.cs src/MMLib.Alvo/Extensions/AlvoOrdering.cs src/MMLib.Alvo/Extensions/AlvoRetirement.cs src/MMLib.Alvo/Extensions/AlvoRetryPolicy.cs src/MMLib.Alvo/Extensions/AlvoHandlerOptions.cs src/MMLib.Alvo/Extensions/AlvoDeliveryOptions.cs src/MMLib.Alvo/Extensions/Internal/AlvoDeliveryOptionsConfiguration.cs src/MMLib.Alvo/Extensions/Setup.cs src/MMLib.Alvo/Events/IAlvoEventHandler.cs src/MMLib.Alvo/Events/AlvoEventMessages.cs src/MMLib.Alvo/Events/AlvoEventContext.cs test/MMLib.Alvo.Tests/Extensions/AlvoRetryPolicyTests.cs test/MMLib.Alvo.Tests/Extensions/AlvoHandlerOptionsTests.cs test/MMLib.Alvo.Tests/Extensions/AlvoDeliveryOptionsTests.cs`, then the three classes (PASS) and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests' --filter-class '*SqlTextConfinedToRendererArchitectureTests'`: the core diff adds exactly the types of this task's *Produces* line; accept, re-run, PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo/Extensions/AlvoRunAs.cs src/MMLib.Alvo/Extensions/AlvoOrdering.cs src/MMLib.Alvo/Extensions/AlvoRetirement.cs src/MMLib.Alvo/Extensions/AlvoRetryPolicy.cs src/MMLib.Alvo/Extensions/AlvoHandlerOptions.cs src/MMLib.Alvo/Extensions/AlvoDeliveryOptions.cs src/MMLib.Alvo/Extensions/Internal/AlvoDeliveryOptionsConfiguration.cs src/MMLib.Alvo/Extensions/Setup.cs src/MMLib.Alvo/Events/IAlvoEventHandler.cs src/MMLib.Alvo/Events/AlvoEventMessages.cs src/MMLib.Alvo/Events/AlvoEventContext.cs test/MMLib.Alvo.Tests/Extensions/AlvoRetryPolicyTests.cs test/MMLib.Alvo.Tests/Extensions/AlvoHandlerOptionsTests.cs test/MMLib.Alvo.Tests/Extensions/AlvoDeliveryOptionsTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(extensions): handler options, retry policy, messages and the handler port

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 6: Registration verbs, the registry and the handler invoker (X14)

**Files:**
- Create: `src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs`, `ExtensionRegistrations.cs`, `ExtensionRegistry.cs`, `ExtensionIds.cs`, `HandledEventType.cs`, `HandlerInvoker.cs`
- Create: `src/MMLib.Alvo/Extensions/AlvoEntityBuilder.cs`, `src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs`
- Modify: `src/MMLib.Alvo/Extensions/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/ExtensionRegistrationTests.cs`, `test/MMLib.Alvo.Tests/Extensions/HandlerInvokerTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: Task 5's options and messages; `AlvoEventName.IsWellFormed`, `AlvoEventName.FrameworkNamespace` (Task 1).
- Produces (internal, `MMLib.Alvo.Extensions.Internal`): `abstract record ExtensionRegistration(string Id, int Sequence)`; `sealed record HandlerRegistration(string Id, int Sequence, HandlerSource Source, string? When, HandlerSettings Settings, HandlerInvoker Invoker) : ExtensionRegistration`; `sealed record RetiredExtension(string Id, int Sequence, AlvoRetirement Retirement) : ExtensionRegistration`; `abstract record HandlerSource`; `sealed record EntityEvents(IReadOnlyList<string> Entities, HandlerVerb Verb) : HandlerSource { bool IsAll }`; `sealed record NamedEvent(string Type) : HandlerSource`; `enum HandlerVerb { Created, Updated, Deleted, Saved }`; `sealed record HandlerSettings(string? Description, int Order, bool OrderStated, TimeSpan Timeout, AlvoRunAs RunAs, AlvoOrdering Ordering, AlvoRetryPolicy Retry) { static From(AlvoHandlerOptions) }`; `sealed class ExtensionRegistry(IEnumerable<ExtensionRegistration>) { All; Handlers; Retired; static Empty }`; `static class ExtensionIds { IsExtensionId(string); IsRetirableId(string); DerivedSegment(string) }`; `sealed class HandlerInvoker { Type MessageType; Task InvokeAsync(IServiceProvider, object message, AlvoEventContext, CancellationToken); IReadOnlyList<Type> Unresolvable(IServiceProviderIsService); static FromDelegate(Delegate, Type messageType); static FromClass<THandler, TMessage>() }`.
- Produces (public): `AlvoEntityBuilder` with `OnCreated/OnUpdated/OnDeleted/OnSaved(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null)` and `On…<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)`; `AlvoExtensionBuilderExtensions.Entity`, `Entities`, `AddEventHandler` ×2, `AddRetiredExtension`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Extensions/ExtensionRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// The registration verbs (spec 2026-10-14 §4.2, X14): each call adds one internal registration; shape errors a compiler
/// cannot represent are refused at the call, everything that needs the schema is refused at boot (Task 7).
/// </summary>
public sealed class ExtensionRegistrationTests
{
    [Fact]
    public void An_entity_handler_is_one_registration_bound_to_its_entity_verb_and_condition()
    {
        var registry = Registered(alvo => alvo.Entity("tickets", tickets => tickets.OnUpdated(
            "ticket-resolved", (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask,
            when: "new.status == 'resolved'", configure: options => options.Timeout = TimeSpan.FromSeconds(3))));

        var handler = registry.Handlers.ShouldHaveSingleItem();
        handler.Id.ShouldBe("ticket-resolved");
        handler.Source.ShouldBe(new EntityEvents(["tickets"], HandlerVerb.Updated));
        handler.When.ShouldBe("new.status == 'resolved'");
        handler.Settings.Timeout.ShouldBe(TimeSpan.FromSeconds(3));
        handler.Invoker.MessageType.ShouldBe(typeof(AlvoUpdated<AlvoRecord>));
    }

    [Fact]
    public void Entities_binds_one_registration_to_every_named_entity()
    {
        var registry = Registered(alvo => alvo.Entities(["orders", "invoices"], entities => entities.OnCreated<NoOpCreated>("audit")));

        ((EntityEvents)registry.Handlers.ShouldHaveSingleItem().Source).Entities.ShouldBe(["orders", "invoices"]);
    }

    [Fact]
    public void The_star_binds_every_entity() =>
        ((EntityEvents)Registered(alvo => alvo.Entity("*", all => all.OnSaved<NoOpSaved>("audit-all")))
            .Handlers.Single().Source).IsAll.ShouldBeTrue();

    [Fact]
    public void Registrations_keep_the_order_they_were_made_in()
    {
        var registry = Registered(alvo => alvo
            .Entity("tickets", tickets => tickets.OnCreated<NoOpCreated>("first").OnDeleted("second", (AlvoDeleted<AlvoRecord> _) => { }))
            .AddRetiredExtension("old-sync", AlvoRetirement.Discard)
            .AddEventHandler("third", "orders.approved", (AlvoRecord _) => Task.CompletedTask));

        registry.All.Select(registration => registration.Id).ShouldBe(["first", "second", "old-sync", "third"]);
        registry.Handlers.Select(handler => handler.Sequence).ShouldBe([0, 1, 3]);
        registry.Retired.ShouldHaveSingleItem().Retirement.ShouldBe(AlvoRetirement.Discard);
    }

    [Theory]
    [InlineData("orders.approved")]
    [InlineData("alvo.command.issue_invoice.completed")]
    public void An_application_or_framework_event_can_be_handled(string type) =>
        Registered(alvo => alvo.AddEventHandler<NoOpRecord>("handle-it", type)).Handlers.Single().Source.ShouldBe(new NamedEvent(type));

    [Fact]
    public void An_entity_event_is_handled_through_its_entity_not_by_name()
    {
        var refusal = Should.Throw<ArgumentException>(() =>
            Registered(alvo => alvo.AddEventHandler<NoOpRecord>("by-name", "entity.tickets.updated")));

        refusal.Message.ShouldContain("Entity(\"tickets\"");
    }

    [Theory]
    [InlineData("auth.user.login")]
    [InlineData("Orders.Approved")]
    [InlineData("approved")]
    public void A_name_no_event_can_carry_is_refused(string type) =>
        Should.Throw<ArgumentException>(() => Registered(alvo => alvo.AddEventHandler<NoOpRecord>("by-name", type)));

    [Fact]
    public void A_blank_id_or_condition_is_refused_at_the_call()
    {
        Should.Throw<ArgumentException>(() => Registered(alvo => alvo.Entity("tickets", t => t.OnCreated<NoOpCreated>(" "))));
        Should.Throw<ArgumentException>(() => Registered(alvo => alvo.Entity("tickets", t => t.OnCreated<NoOpCreated>("id", when: " "))));
    }

    [Fact]
    public void An_undefined_retirement_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Registered(alvo => alvo.AddRetiredExtension("old", (AlvoRetirement)7)));

    [Theory]
    [InlineData("send-confirmation", true)]
    [InlineData("a_b", false)]
    [InlineData("Send", false)]
    [InlineData("9lives", false)]
    public void An_extension_id_is_lower_case_letters_digits_and_hyphens(string id, bool valid) =>
        ExtensionIds.IsExtensionId(id).ShouldBe(valid);

    [Theory]
    [InlineData("a_b")]
    [InlineData("issue-invoice")]
    [InlineData("billing.issue_invoice")]
    public void A_retired_id_may_be_any_catalog_id(string id) => ExtensionIds.IsRetirableId(id).ShouldBeTrue();

    [Fact]
    public void Hyphens_and_underscores_derive_one_event_name_segment() =>
        ExtensionIds.DerivedSegment("a-b").ShouldBe(ExtensionIds.DerivedSegment("a_b"));

    private static ExtensionRegistry Registered(Action<IAlvoBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure);
        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<ExtensionRegistry>();
    }

    private sealed class NoOpCreated : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
    {
        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpSaved : IAlvoEventHandler<AlvoSaved<AlvoRecord>>
    {
        public Task HandleAsync(AlvoSaved<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpRecord : IAlvoEventHandler<AlvoRecord>
    {
        public Task HandleAsync(AlvoRecord message, AlvoEventContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
```

`test/MMLib.Alvo.Tests/Extensions/HandlerInvokerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;

using NSubstitute;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>A handler delegate's parameters bind to the message, the context, the token, or a service — checked at the call and at boot.</summary>
public sealed class HandlerInvokerTests
{
    private static readonly AlvoCreated<AlvoRecord> _message = new() { New = AlvoRecord.Empty };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_delegate_receives_the_message_the_context_the_token_and_its_services()
    {
        object? seen = null;
        var invoker = HandlerInvoker.FromDelegate(
            (AlvoCreated<AlvoRecord> message, AlvoEventContext context, Clock clock, CancellationToken token) =>
            {
                seen = (message, context.Subscriber, clock, token);
                return Task.CompletedTask;
            },
            typeof(AlvoCreated<AlvoRecord>));
        using var services = new ServiceCollection().AddSingleton<Clock>().BuildServiceProvider();

        await invoker.InvokeAsync(services, _message, Context(), Ct);

        seen.ShouldBe((_message, "on-created", services.GetRequiredService<Clock>(), Ct));
    }

    [Fact]
    public async Task A_value_task_and_a_void_delegate_are_both_handlers()
    {
        var calls = 0;
        using var services = new ServiceCollection().BuildServiceProvider();

        await HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => { calls++; return ValueTask.CompletedTask; }, typeof(AlvoCreated<AlvoRecord>))
            .InvokeAsync(services, _message, Context(), Ct);
        await HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => { calls++; }, typeof(AlvoCreated<AlvoRecord>))
            .InvokeAsync(services, _message, Context(), Ct);

        calls.ShouldBe(2);
    }

    [Fact]
    public void A_delegate_that_returns_a_value_is_refused() =>
        Should.Throw<ArgumentException>(() => HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _) => 42, typeof(AlvoCreated<AlvoRecord>)))
            .Message.ShouldContain("Task");

    [Fact]
    public void A_delegate_asking_for_another_verb_s_message_is_refused() =>
        Should.Throw<ArgumentException>(() => HandlerInvoker.FromDelegate((AlvoUpdated<AlvoRecord> _) => Task.CompletedTask, typeof(AlvoCreated<AlvoRecord>)))
            .Message.ShouldContain("AlvoCreated");

    [Fact]
    public async Task The_handler_s_own_exception_escapes_unwrapped()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        Func<AlvoCreated<AlvoRecord>, Task> throwsBeforeItsTask = _ => throw new TimeoutException("upstream");
        var invoker = HandlerInvoker.FromDelegate(throwsBeforeItsTask, typeof(AlvoCreated<AlvoRecord>));

        await Should.ThrowAsync<TimeoutException>(() => invoker.InvokeAsync(services, _message, Context(), Ct));
    }

    [Fact]
    public void A_service_the_container_cannot_resolve_is_reported()
    {
        var invoker = HandlerInvoker.FromDelegate((AlvoCreated<AlvoRecord> _, Clock clock) => Task.CompletedTask, typeof(AlvoCreated<AlvoRecord>));

        invoker.Unresolvable(IsService()).ShouldBe([typeof(Clock)]);
    }

    [Fact]
    public async Task A_handler_class_is_built_per_delivery_and_disposed_when_it_was_not_registered()
    {
        using var services = new ServiceCollection().AddSingleton<Clock>().BuildServiceProvider();
        var invoker = HandlerInvoker.FromClass<CountingHandler, AlvoCreated<AlvoRecord>>();

        await invoker.InvokeAsync(services, _message, Context(), Ct);

        CountingHandler.Handled.ShouldBeGreaterThanOrEqualTo(1);
        CountingHandler.Disposed.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void A_handler_class_reports_the_constructor_dependencies_the_container_cannot_resolve() =>
        HandlerInvoker.FromClass<CountingHandler, AlvoCreated<AlvoRecord>>().Unresolvable(IsService()).ShouldBe([typeof(Clock)]);

    private static AlvoEventContext Context() => new(
        new AlvoEvent
        {
            Id = AlvoEventId.Create(DateTimeOffset.UtcNow),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.tickets.created",
            Time = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero),
            Subject = "tickets/1",
            PartitionKey = "tickets:1",
            AuthType = AlvoEventAuthType.ApiKey,
            CorrelationId = "c",
            Data = new AlvoEventData { Record = AlvoRecord.Empty },
        },
        "on-created",
        attempt: 1,
        Substitute.For<IAlvoSession>());

    private static IServiceProviderIsService IsService()
    {
        var isService = Substitute.For<IServiceProviderIsService>();
        isService.IsService(Arg.Any<Type>()).Returns(false);

        return isService;
    }

    internal sealed class Clock;

    private sealed class CountingHandler(Clock clock) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>, IDisposable
    {
        private static int _handled;
        private static int _disposed;

        internal static int Handled => Volatile.Read(ref _handled);

        internal static int Disposed => Volatile.Read(ref _disposed);

        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(clock);
            Interlocked.Increment(ref _handled);
            return Task.CompletedTask;
        }

        public void Dispose() => Interlocked.Increment(ref _disposed);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionRegistrationTests' --filter-class '*HandlerInvokerTests'`
Expected: FAIL (build error).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs`:

```csharp
namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// One piece of host code registered with Alvo — the <c>CelFunctionRegistration</c> precedent: an internal singleton per
/// call, read back as a set when the boot binds them.
/// </summary>
/// <param name="Id">The id its durable state is keyed by.</param>
/// <param name="Sequence">Its place among every registration of this host, in call order.</param>
internal abstract record ExtensionRegistration(string Id, int Sequence);

/// <summary>An event handler.</summary>
internal sealed record HandlerRegistration(
    string Id, int Sequence, HandlerSource Source, string? When, HandlerSettings Settings, HandlerInvoker Invoker)
    : ExtensionRegistration(Id, Sequence);

/// <summary>An id that is no longer registered, and what happens to its unfinished work.</summary>
internal sealed record RetiredExtension(string Id, int Sequence, AlvoRetirement Retirement) : ExtensionRegistration(Id, Sequence);

/// <summary>Which events a handler is subscribed to.</summary>
internal abstract record HandlerSource;

/// <summary>One verb of one or more entities' data events; <c>*</c> alone is every entity.</summary>
internal sealed record EntityEvents(IReadOnlyList<string> Entities, HandlerVerb Verb) : HandlerSource
{
    internal const string AllEntities = "*";

    internal bool IsAll => Entities is [AllEntities];

    public bool Equals(EntityEvents? other) =>
        other is not null && Verb == other.Verb && Entities.SequenceEqual(other.Entities, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Verb, string.Join('\u001f', Entities));
}

/// <summary>One application or framework event, by its exact type.</summary>
internal sealed record NamedEvent(string Type) : HandlerSource;

/// <summary>The data-event verbs a handler is registered for.</summary>
internal enum HandlerVerb
{
    Created,
    Updated,
    Deleted,
    Saved,
}

/// <summary>A handler's options, frozen at registration.</summary>
internal sealed record HandlerSettings(
    string? Description,
    int Order,
    bool OrderStated,
    TimeSpan Timeout,
    AlvoRunAs RunAs,
    AlvoOrdering Ordering,
    AlvoRetryPolicy Retry)
{
    internal static HandlerSettings From(AlvoHandlerOptions options) => new(
        options.Description, options.Order, options.OrderStated, options.Timeout, options.RunAs, options.Ordering, options.Retry);
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionRegistrations.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Adds registrations to the container, numbering them in call order.</summary>
internal static class ExtensionRegistrations
{
    internal static void AddHandler(
        IServiceCollection services,
        string id,
        HandlerSource source,
        string? when,
        Action<AlvoHandlerOptions>? configure,
        HandlerInvoker invoker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (when is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(when);
        }

        var options = new AlvoHandlerOptions();
        configure?.Invoke(options);
        Add(services, sequence => new HandlerRegistration(id, sequence, source, when, HandlerSettings.From(options), invoker));
    }

    internal static void Add(IServiceCollection services, Func<int, ExtensionRegistration> create)
    {
        var sequence = services.Count(descriptor => descriptor.ServiceType == typeof(ExtensionRegistration));
        services.AddSingleton(create(sequence));
    }
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionRegistry.cs`:

```csharp
namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Every registration of this host, in call order.</summary>
internal sealed class ExtensionRegistry
{
    internal ExtensionRegistry(IEnumerable<ExtensionRegistration> registrations)
    {
        All = [.. registrations.OrderBy(registration => registration.Sequence)];
        Handlers = [.. All.OfType<HandlerRegistration>()];
        Retired = [.. All.OfType<RetiredExtension>()];
    }

    internal static ExtensionRegistry Empty { get; } = new([]);

    internal IReadOnlyList<ExtensionRegistration> All { get; }

    internal IReadOnlyList<HandlerRegistration> Handlers { get; }

    internal IReadOnlyList<RetiredExtension> Retired { get; }
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionIds.cs`:

```csharp
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// The id grammars of the one catalog namespace (spec 2026-10-14 E3): extension ids, and — for a retired id, which may
/// have been any kind — command and function names too. Event names derived from ids map <c>-</c> to <c>_</c>.
/// </summary>
internal static partial class ExtensionIds
{
    internal static bool IsExtensionId(string id) => Extension().IsMatch(id);

    internal static bool IsRetirableId(string id) => Extension().IsMatch(id) || Function().IsMatch(id) || Command().IsMatch(id);

    internal static string DerivedSegment(string id) => id.Replace('-', '_');

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Extension();

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Function();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Command();
}
```

`src/MMLib.Alvo/Extensions/Internal/HandledEventType.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Which event names <c>AddEventHandler</c> subscribes to: an application event, or a framework one.</summary>
internal static class HandledEventType
{
    internal static void Ensure(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        var first = eventType.Split(AlvoEventName.Separator)[0];
        if (AlvoEventName.IsReservedNamespace(first) && first != AlvoEventName.FrameworkNamespace)
        {
            throw new ArgumentException(DataNamespace(eventType, first), nameof(eventType));
        }

        if (!AlvoEventName.IsWellFormed(eventType))
        {
            throw new ArgumentException(
                $"'{eventType}' is not an event name: use two or more lower-case dot-separated segments, such as "
                + "'orders.approved'.",
                nameof(eventType));
        }
    }

    private static string DataNamespace(string eventType, string first) =>
        first == "entity"
            ? $"'{eventType}' is an entity's data event; subscribe to it through its entity — Entity(\"{EntityOf(eventType)}\", "
              + "e => e.OnUpdated(…)) — which checks the entity exists and gives the handler the row's images."
            : $"Nothing emits '{first}.' events in this build, so a handler of '{eventType}' would never run.";

    private static string EntityOf(string eventType) =>
        eventType.Split(AlvoEventName.Separator) is [_, var entity, ..] ? entity : "name";
}
```

`src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Reflection;
using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// How one handler is called: a delegate whose parameters bind to the message, the context, the token or a service, or a
/// class built per delivery from the delivery's scope.
/// </summary>
internal sealed class HandlerInvoker
{
    private static readonly Type[] _messageDefinitions =
        [typeof(AlvoCreated<>), typeof(AlvoUpdated<>), typeof(AlvoDeleted<>), typeof(AlvoSaved<>)];

    private readonly Func<IServiceProvider, object, AlvoEventContext, CancellationToken, Task> _invoke;
    private readonly Func<IServiceProviderIsService, IReadOnlyList<Type>> _unresolvable;

    private HandlerInvoker(
        Type messageType,
        Func<IServiceProvider, object, AlvoEventContext, CancellationToken, Task> invoke,
        Func<IServiceProviderIsService, IReadOnlyList<Type>> unresolvable)
    {
        MessageType = messageType;
        _invoke = invoke;
        _unresolvable = unresolvable;
    }

    internal Type MessageType { get; }

    internal Task InvokeAsync(IServiceProvider services, object message, AlvoEventContext context, CancellationToken cancellationToken) =>
        _invoke(services, message, context, cancellationToken);

    internal IReadOnlyList<Type> Unresolvable(IServiceProviderIsService services) => _unresolvable(services);

    internal static HandlerInvoker FromDelegate(Delegate handler, Type messageType)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureReturnsTask(handler.Method);
        var parameters = handler.Method.GetParameters();
        EnsureOnlyItsOwnMessage(parameters, messageType);
        IReadOnlyList<Type> services = [.. parameters.Select(parameter => parameter.ParameterType).Where(type => !IsSupplied(type, messageType))];

        return new HandlerInvoker(
            messageType,
            (provider, message, context, ct) => InvokeDelegateAsync(handler, parameters, provider, message, context, ct),
            isService => [.. services.Where(type => !isService.IsService(type))]);
    }

    internal static HandlerInvoker FromClass<THandler, TMessage>()
        where THandler : class, IAlvoEventHandler<TMessage>
        where TMessage : class =>
        new(typeof(TMessage), InvokeClassAsync<THandler, TMessage>, isService => ClassUnresolvable(typeof(THandler), isService));

    private static void EnsureReturnsTask(MethodInfo method)
    {
        var returns = method.ReturnType;
        if (returns == typeof(void) || returns == typeof(ValueTask) || typeof(Task).IsAssignableFrom(returns))
        {
            return;
        }

        throw new ArgumentException(
            $"A handler delegate returns Task, ValueTask or nothing; this one returns {returns.Name}, which Alvo would discard.",
            nameof(method));
    }

    private static void EnsureOnlyItsOwnMessage(ParameterInfo[] parameters, Type messageType)
    {
        var other = parameters.FirstOrDefault(parameter => IsMessage(parameter.ParameterType) && parameter.ParameterType != messageType);
        if (other is not null)
        {
            throw new ArgumentException(
                $"This registration hands its delegate {Display(messageType)}; parameter '{other.Name}' asks for "
                + $"{Display(other.ParameterType)}, which it would never be given.",
                nameof(parameters));
        }
    }

    private static bool IsSupplied(Type type, Type messageType) =>
        type == messageType || type == typeof(AlvoEventContext) || type == typeof(CancellationToken);

    private static bool IsMessage(Type type) =>
        type == typeof(AlvoRecord) || (type.IsGenericType && _messageDefinitions.Contains(type.GetGenericTypeDefinition()));

    private static string Display(Type type) =>
        type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>" : type.Name;

    private static async Task InvokeDelegateAsync(
        Delegate handler, ParameterInfo[] parameters, IServiceProvider services, object message, AlvoEventContext context, CancellationToken ct)
    {
        var arguments = parameters.Select(parameter => Argument(parameter.ParameterType, services, message, context, ct)).ToArray();
        switch (Invoke(handler, arguments))
        {
            case Task task:
                await task.ConfigureAwait(false);
                break;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                break;
        }
    }

    private static object? Argument(Type type, IServiceProvider services, object message, AlvoEventContext context, CancellationToken ct) =>
        type == typeof(CancellationToken) ? ct
        : type == typeof(AlvoEventContext) ? context
        : type.IsInstanceOfType(message) && IsMessage(type) ? message
        : services.GetRequiredService(type);

    private static object? Invoke(Delegate handler, object?[] arguments)
    {
        try
        {
            return handler.DynamicInvoke(arguments);
        }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
            throw;
        }
    }

    private static async Task InvokeClassAsync<THandler, TMessage>(
        IServiceProvider services, object message, AlvoEventContext context, CancellationToken ct)
        where THandler : class, IAlvoEventHandler<TMessage>
        where TMessage : class
    {
        var registered = services.GetService<THandler>();
        var handler = registered ?? ActivatorUtilities.CreateInstance<THandler>(services);
        try
        {
            await handler.HandleAsync((TMessage)message, context, ct).ConfigureAwait(false);
        }
        finally
        {
            if (registered is null)
            {
                await DisposeAsync(handler).ConfigureAwait(false);
            }
        }
    }

    private static ValueTask DisposeAsync(object handler)
    {
        switch (handler)
        {
            case IAsyncDisposable asyncDisposable:
                return asyncDisposable.DisposeAsync();
            case IDisposable disposable:
                disposable.Dispose();
                return ValueTask.CompletedTask;
            default:
                return ValueTask.CompletedTask;
        }
    }

    private static IReadOnlyList<Type> ClassUnresolvable(Type handler, IServiceProviderIsService isService)
    {
        if (isService.IsService(handler))
        {
            return [];
        }

        var constructor = handler.GetConstructors().OrderByDescending(candidate => candidate.GetParameters().Length).FirstOrDefault();

        return constructor is null
            ? [handler]
            : [.. constructor.GetParameters()
                .Where(parameter => !parameter.HasDefaultValue && !isService.IsService(parameter.ParameterType))
                .Select(parameter => parameter.ParameterType)];
    }
}
```

`src/MMLib.Alvo/Extensions/AlvoEntityBuilder.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Extensions;

/// <summary>
/// Registers host code for the entities named in <c>Entity(…)</c> or <c>Entities(…)</c>; every registration is checked
/// against the descriptor when the host starts, and all problems are reported at once.
/// </summary>
/// <remarks>
/// A handler's <c>when</c> is a CEL condition over <c>new.</c> and <c>old.</c> (as the verb's event carries them),
/// <c>changed(field)</c> on updates, <c>@user</c> and <c>@tenant</c>. It is judged after the write commits, before any
/// delivery exists: an event it does not match costs no delivery.
/// </remarks>
public sealed class AlvoEntityBuilder
{
    private readonly IServiceCollection _services;
    private readonly IReadOnlyList<string> _entities;

    internal AlvoEntityBuilder(IServiceCollection services, IReadOnlyList<string> entities)
    {
        _services = services;
        _entities = entities;
    }

    /// <summary>Runs <paramref name="handler"/> after a record is created.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoCreated&lt;AlvoRecord&gt;</c>, and optionally the <see cref="AlvoEventContext"/>, a <see cref="CancellationToken"/> and services.</param>
    /// <param name="when">An optional CEL condition the created record must meet.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnCreated(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromDelegate(handler, typeof(AlvoCreated<AlvoRecord>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is created.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition the created record must meet.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnCreated<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoCreated<AlvoRecord>> =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromClass<THandler, AlvoCreated<AlvoRecord>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is updated or replaced.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoUpdated&lt;AlvoRecord&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over both images.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnUpdated(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Updated, HandlerInvoker.FromDelegate(handler, typeof(AlvoUpdated<AlvoRecord>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/> after a record is updated or replaced.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over both images.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnUpdated<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoUpdated<AlvoRecord>> =>
        Add(id, HandlerVerb.Updated, HandlerInvoker.FromClass<THandler, AlvoUpdated<AlvoRecord>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is deleted.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoDeleted&lt;AlvoRecord&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over the deleted record (<c>old.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnDeleted(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Deleted, HandlerInvoker.FromDelegate(handler, typeof(AlvoDeleted<AlvoRecord>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/> after a record is deleted.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over the deleted record (<c>old.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnDeleted<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoDeleted<AlvoRecord>> =>
        Add(id, HandlerVerb.Deleted, HandlerInvoker.FromClass<THandler, AlvoDeleted<AlvoRecord>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is created or updated.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoSaved&lt;AlvoRecord&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over the stored record (<c>new.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnSaved(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Saved, HandlerInvoker.FromDelegate(handler, typeof(AlvoSaved<AlvoRecord>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/> after a record is created or updated.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over the stored record (<c>new.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnSaved<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoSaved<AlvoRecord>> =>
        Add(id, HandlerVerb.Saved, HandlerInvoker.FromClass<THandler, AlvoSaved<AlvoRecord>>(), when, configure);

    private AlvoEntityBuilder Add(
        string id, HandlerVerb verb, HandlerInvoker invoker, string? when, Action<AlvoHandlerOptions>? configure)
    {
        ExtensionRegistrations.AddHandler(_services, id, new EntityEvents(_entities, verb), when, configure, invoker);

        return this;
    }
}
```

`src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs`:

```csharp
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers host code with Alvo: <c>Entity</c> and <c>Entities</c> group what one or more entities run, and the
/// <c>Add*</c> verbs register the rest.
/// </summary>
public static class AlvoExtensionBuilderExtensions
{
    /// <summary>Registers host code for one entity, or for every entity with <c>"*"</c>.</summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="entity">The entity's name, or <c>"*"</c>: every entity, whose conditions may test only <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="configure">Registers the entity's handlers.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder Entity(this IAlvoBuilder builder, string entity, Action<AlvoEntityBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(configure);
        configure(new AlvoEntityBuilder(builder.Services, [entity]));

        return builder;
    }

    /// <summary>Registers the same host code for several entities; each registration is one id bound to all of them.</summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="entities">The entities' names.</param>
    /// <param name="configure">Registers the entities' handlers.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder Entities(this IAlvoBuilder builder, IEnumerable<string> entities, Action<AlvoEntityBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(configure);
        IReadOnlyList<string> names = [.. entities];
        if (names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Name at least one entity, and no blank one.", nameof(entities));
        }

        configure(new AlvoEntityBuilder(builder.Services, names));

        return builder;
    }

    /// <summary>Runs <paramref name="handler"/> for every application or framework event named <paramref name="eventType"/>.</summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="eventType">The exact event name, such as <c>orders.approved</c>; an entity's events are handled through <c>Entity</c>.</param>
    /// <param name="handler">A delegate taking the payload as an <see cref="AlvoRecord"/>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder AddEventHandler(
        this IAlvoBuilder builder,
        string id,
        string eventType,
        Delegate handler,
        string? when = null,
        Action<AlvoHandlerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        HandledEventType.Ensure(eventType);
        ExtensionRegistrations.AddHandler(
            builder.Services, id, new NamedEvent(eventType), when, configure, HandlerInvoker.FromDelegate(handler, typeof(AlvoRecord)));

        return builder;
    }

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, for every event named <paramref name="eventType"/>.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="eventType">The exact event name.</param>
    /// <param name="when">An optional CEL condition over <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder AddEventHandler<THandler>(
        this IAlvoBuilder builder, string id, string eventType, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoRecord>
    {
        ArgumentNullException.ThrowIfNull(builder);
        HandledEventType.Ensure(eventType);
        ExtensionRegistrations.AddHandler(
            builder.Services, id, new NamedEvent(eventType), when, configure, HandlerInvoker.FromClass<THandler, AlvoRecord>());

        return builder;
    }

    /// <summary>
    /// Declares an id this host no longer registers, and what happens at start-up to the work it left behind; without it
    /// a host whose store holds such work refuses to start.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">The retired id.</param>
    /// <param name="retirement">Dead-letter its unfinished deliveries, or discard them.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder AddRetiredExtension(this IAlvoBuilder builder, string id, AlvoRetirement retirement)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Enum.IsDefined(retirement))
        {
            throw new ArgumentOutOfRangeException(nameof(retirement), retirement, "Choose AlvoRetirement.DeadLetter or AlvoRetirement.Discard.");
        }

        ExtensionRegistrations.Add(builder.Services, sequence => new RetiredExtension(id, sequence, retirement));

        return builder;
    }
}
```

`src/MMLib.Alvo/Extensions/Setup.cs` — add before `return services;`:

```csharp
        services.TryAddSingleton(provider => new ExtensionRegistry(provider.GetServices<ExtensionRegistration>()));
```

- [ ] **Step 4: Run, accept the core baseline**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistrations.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistry.cs src/MMLib.Alvo/Extensions/Internal/ExtensionIds.cs src/MMLib.Alvo/Extensions/Internal/HandledEventType.cs src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs src/MMLib.Alvo/Extensions/AlvoEntityBuilder.cs src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/ExtensionRegistrationTests.cs test/MMLib.Alvo.Tests/Extensions/HandlerInvokerTests.cs`, then the two classes (PASS) and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`: the diff adds `AlvoEntityBuilder` and `AlvoExtensionBuilderExtensions` with exactly the members above; accept, re-run, PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo/Extensions/Internal/ExtensionRegistration.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistrations.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRegistry.cs src/MMLib.Alvo/Extensions/Internal/ExtensionIds.cs src/MMLib.Alvo/Extensions/Internal/HandledEventType.cs src/MMLib.Alvo/Extensions/Internal/HandlerInvoker.cs src/MMLib.Alvo/Extensions/AlvoEntityBuilder.cs src/MMLib.Alvo/Extensions/AlvoExtensionBuilderExtensions.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/ExtensionRegistrationTests.cs test/MMLib.Alvo.Tests/Extensions/HandlerInvokerTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(extensions): Entity, Entities, AddEventHandler and AddRetiredExtension

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: `ExtensionBindingCompiler`, `ExtensionBinder` and the boot refusal

**Files:**
- Create: `src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs`, `ExtensionBindingCompiler.cs`, `ExtensionBinder.cs`, `ExtensionRefusal.cs`, `ExtensionLog.cs`
- Modify: `src/MMLib.Alvo/Extensions/Setup.cs`, `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (`AddBootPlan`), `src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs`, `src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs` (`TryPlanTheStoredDescriptor`)
- Modify: `test/MMLib.Alvo.Tests/Migrations/BootServiceWorld.cs` (an optional binder), `test/MMLib.Alvo.Tests/Migrations/AlvoBootServiceStoredDescriptorTests.cs` (two facts)
- Modify: `test/MMLib.Alvo.Tests/CapturingLogger.cs` (record the event name)
- Create: `test/MMLib.Alvo.Tests/Extensions/HandlerCatalog.cs`, `ExtensionBindingCompilerTests.cs`, `ExtensionBinderTests.cs`; `test/MMLib.Alvo.TestHost.Tests/ExtensionBootRefusalTests.cs`

**Interfaces:**
- Consumes: `ExtensionRegistry`, `HandlerRegistration`, `HandlerInvoker.Unresolvable`, `ExtensionIds` (Task 6); `AfterImagePhase.Refusals(CelNode, AfterImages, string, string)`, `AfterImages(bool Pre, bool Post)`, `PolicyCatalogBuilder.ReferencesContextValue`, `RequiredContext`, `NameSuggestion.Closest`, `CelTree.Children` (existing).
- Produces (internal): `sealed record ExtensionProblem(string Id, string Kind, string Locator, string Message, string Fix)`; `sealed record OrderTie(string Target, int Order, IReadOnlyList<string> Ids)`; `sealed record BoundCondition(CompiledExpression? Expression, RequiredContext Required, bool ReadsRoles) { static Always; static Of(CompiledExpression) }`; `sealed record BoundHandler(HandlerRegistration Registration, IReadOnlyDictionary<string, BoundCondition> Conditions, BoundCondition Fallback) { string Id; HandlerSettings Settings }`; `sealed class ExtensionBindings(IEnumerable<BoundHandler>) { static None; IReadOnlyList<BoundHandler> Handlers; BoundHandler? Handler(string id); TimeSpan LongestTimeout }`; `sealed record ExtensionBindingResult(ExtensionBindings Bindings, IReadOnlyList<ExtensionProblem> Problems, IReadOnlyList<OrderTie> Ties)`; `static class ExtensionBindingCompiler { const HandlerKind = "eventHandler"; const RetiredKind = "retired"; static ExtensionBindingResult Compile(ExtensionRegistry, PolicyCatalog, ICelCompiler, IServiceProviderIsService) }`; `sealed class ExtensionBinder(ExtensionRegistry, ICelCompiler, IServiceProviderIsService, ILogger<ExtensionBinder>) { void EnsureBindable(PolicyCatalog); ExtensionBindings For(PolicyCatalog) }`; `ExtensionLog.ExtensionOrderTie / ExtensionUnbound / ExtensionRetired`; `DescriptorBootPlan(…, ExtensionBinder? extensions = null)`; `CapturedLogEntry.EventName`.

**Rulings this task implements** (recorded in the spec amendment, Task 14): an entity handler's `when` compiles in `CelProfile.Condition` against each bound entity; the #325 image rule applies per verb — `OnCreated` and `OnSaved` carry only the post-image (no `old.`, no `changed()`), `OnDeleted` only the pre-image; a `when` reading an `events.exclude` field — through `new.`, `old.` **or `changed()`** — is refused (stricter than after-hooks, so #398 cannot reach a handler); a `"*"` or application-event handler's `when` compiles against a fieldless schema, so it can test only `@user`/`@tenant`; `"*"` mixed with names is refused; ids follow E3 (retired ids may be any catalog grammar), duplicates — including a handler re-using a retired id — and ids deriving one event-name segment are refused; a non-positive timeout is refused; `ExtensionOrderTie` warns only when two registrations on one entity+operation (or one event) share an `Order` and at least one of them **stated** it. At boot every problem is refused at once. **A code-first boot refuses to start; a stored-descriptor (dashboard-first) boot records the project `Failed` — readiness 503, the process up, the reason published — like every other stored-descriptor refusal** (I7). On any catalog the boot plan did not bind — a runtime apply (`RuntimeSchemaService`), `ApplyAlvoDescriptorAsync` (`SchemaMigrationRunner`), and the *first* catalog of a dashboard-first host that booted `Awaiting` — a handler with a problem is **unbound**: never bound against a stale catalog, logged `ExtensionUnbound` at Error once per catalog, and kept in `ExtensionBindings.Unbound` so the fan-out records a **dead `unbound` delivery** for every event its source selects (Task 8) — visible, and replayable once HX-06 ships; never a silent drop. Until HX-04's apply guard refuses such an apply, acceptance (7) holds for a dashboard-first host only in that form.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/CapturingLogger.cs` — change the record and its one construction:

```csharp
internal sealed record CapturedLogEntry(LogLevel Level, string Message, Exception? Exception)
{
    internal string? EventName { get; init; }
}
```

and in `Log`: `var entry = new CapturedLogEntry(logLevel, formatter(state, exception), exception) { EventName = eventId.Name };`.

`test/MMLib.Alvo.Tests/Extensions/HandlerCatalog.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using NSubstitute;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>A primed catalog of two entities and the registrations the binder facts compile against it.</summary>
internal static class HandlerCatalog
{
    internal static SchemaModel Schema { get; } = new([
        new EntitySchema
        {
            Name = "tickets",
            Tenancy = TenancyMode.Global,
            EventImageExclusions = ["secret"],
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid },
                new FieldSchema { Name = "title", Type = FieldType.String },
                new FieldSchema { Name = "code", Type = FieldType.String, Nullable = true },
                new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "resolved"] },
                new FieldSchema { Name = "secret", Type = FieldType.String, Nullable = true },
            ],
        },
        new EntitySchema
        {
            Name = "notes",
            Tenancy = TenancyMode.Global,
            Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }, new FieldSchema { Name = "text", Type = FieldType.String }],
        },
    ]);

    internal static PolicyCatalog Catalog() =>
        PolicyCatalog.Build(
            new AlvoDescriptor
            {
                ApiVersion = "alvo.dev/v1",
                Name = "handlers",
                Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
                {
                    ["tickets"] = new() { Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) },
                    ["notes"] = new() { Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal) },
                },
            },
            Schema,
            CelFixtures.Compiler);

    internal static ExtensionRegistry Registry(Action<IAlvoBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure);
        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<ExtensionRegistry>();
    }

    internal static ExtensionBindingResult Compile(Action<IAlvoBuilder> configure, IServiceProviderIsService? services = null) =>
        ExtensionBindingCompiler.Compile(Registry(configure), Catalog(), CelFixtures.Compiler, services ?? EveryService());

    internal static IServiceProviderIsService EveryService()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(true);

        return services;
    }

    internal static Func<AlvoCreated<AlvoRecord>, Task> OnCreated { get; } = _ => Task.CompletedTask;

    internal static Func<AlvoUpdated<AlvoRecord>, Task> OnUpdated { get; } = _ => Task.CompletedTask;

    internal static Func<AlvoDeleted<AlvoRecord>, Task> OnDeleted { get; } = _ => Task.CompletedTask;

    internal static Func<AlvoSaved<AlvoRecord>, Task> OnSaved { get; } = _ => Task.CompletedTask;

    internal static Func<AlvoRecord, Task> OnRecord { get; } = _ => Task.CompletedTask;
}
```

`test/MMLib.Alvo.Tests/Extensions/ExtensionBindingCompilerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// Boot validation of handler registrations (spec 2026-10-14 §4.2): every problem is found, each with id, kind, locator
/// and fix, and a handler with a problem is never bound.
/// </summary>
public sealed class ExtensionBindingCompilerTests
{
    [Fact]
    public void A_handler_on_a_known_entity_binds_with_its_compiled_condition()
    {
        var result = Compile(alvo => alvo.Entity("tickets", t => t.OnUpdated("ticket-resolved", OnUpdated, when: "changed(status) && new.status == 'resolved'")));

        result.Problems.ShouldBeEmpty();
        var bound = result.Bindings.Handler("ticket-resolved").ShouldNotBeNull();
        bound.Conditions["tickets"].Expression.ShouldNotBeNull();
    }

    [Fact]
    public void A_when_typo_is_refused_at_its_locator()
    {
        var problems = Compile(alvo => alvo.Entity("tickets", t => t.OnUpdated("ticket-resolved", OnUpdated, when: "new.stauts == 'resolved'")))
            .Problems;

        problems.ShouldNotBeEmpty();
        problems.ShouldAllBe(problem =>
            problem.Id == "ticket-resolved" && problem.Kind == "eventHandler" && problem.Locator == "extensions/ticket-resolved/when");
    }

    [Fact]
    public void Every_problem_is_reported_at_once_and_none_of_their_handlers_binds()
    {
        var result = Compile(
            alvo => alvo
                .Entity("tickets", t => t.OnUpdated("typo", OnUpdated, when: "new.stauts == 'x'"))
                .Entity("tikets", t => t.OnCreated("misspelled", OnCreated))
                .Entity("notes", n => n.OnCreated("needs-a-clock", (AlvoCreated<AlvoRecord> _, TimeProvider clock) => Task.CompletedTask))
                .Entity("notes", n => n.OnCreated("fine", OnCreated)),
            NothingButTheBasics());

        result.Problems.Select(problem => problem.Id).Distinct().ShouldBe(["typo", "misspelled", "needs-a-clock"], ignoreOrder: true);
        result.Problems.Single(problem => problem.Id == "misspelled").Fix.ShouldContain("Did you mean 'tickets'?");
        result.Bindings.Handlers.Select(handler => handler.Id).ShouldBe(["fine"]);
    }

    [Theory]
    [InlineData("created", "old.status == 'open'")]
    [InlineData("deleted", "new.status == 'open'")]
    [InlineData("saved", "old.status == 'open'")]
    [InlineData("saved", "changed(status)")]
    public void A_condition_reading_an_image_the_verb_s_event_lacks_is_refused(string verb, string when) =>
        Compile(alvo => alvo.Entity("tickets", t => Register(t, verb, when))).Problems.ShouldHaveSingleItem().Locator
            .ShouldBe("extensions/on-it/when");

    [Theory]
    [InlineData("new.secret == 'x'")]
    [InlineData("changed(secret)")]
    public void A_condition_reading_a_field_left_out_of_the_event_images_is_refused(string when) =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnUpdated("on-it", OnUpdated, when: when))).Problems.ShouldHaveSingleItem()
            .Message.ShouldContain("events.exclude");

    [Fact]
    public void A_star_registration_whose_condition_reads_a_field_is_refused()
    {
        var problems = Compile(alvo => alvo.Entity("*", all => all.OnSaved("audit-all", OnSaved, when: "new.title == 'x'"))).Problems;

        problems.ShouldNotBeEmpty();
        problems.ShouldAllBe(problem => problem.Locator == "extensions/audit-all/when" && problem.Fix.Contains("@user and @tenant"));
    }

    [Fact]
    public void A_star_registration_may_test_the_caller() =>
        Compile(alvo => alvo.Entity("*", all => all.OnSaved("audit-all", OnSaved, when: "'admin' in @user.roles"))).Problems.ShouldBeEmpty();

    [Fact]
    public void An_application_event_handler_s_condition_cannot_read_the_payload()
    {
        var problems = Compile(alvo => alvo.AddEventHandler("on-approved", "orders.approved", OnRecord, when: "new.total > 1")).Problems;

        problems.ShouldNotBeEmpty();
        problems.ShouldAllBe(problem => problem.Fix.Contains("inside the handler"));
    }

    [Fact]
    public void The_star_among_entity_names_is_refused() =>
        Compile(alvo => alvo.Entities(["tickets", "*"], e => e.OnCreated("mixed", OnCreated))).Problems.ShouldHaveSingleItem()
            .Locator.ShouldBe("extensions/mixed/entity");

    [Theory]
    [InlineData("Send_Confirmation")]
    [InlineData("a_b")]
    public void An_id_outside_the_extension_grammar_is_refused(string id) =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated(id, OnCreated))).Problems.ShouldHaveSingleItem()
            .Locator.ShouldBe($"extensions/{id}/id");

    [Fact]
    public void An_id_registered_twice_is_refused_even_as_a_retirement() =>
        Compile(alvo => alvo
                .Entity("tickets", t => t.OnCreated("sync", OnCreated))
                .AddRetiredExtension("sync", AlvoRetirement.Discard))
            .Problems.ShouldHaveSingleItem().Message.ShouldContain("registered 2 times");

    [Fact]
    public void Two_ids_deriving_one_event_name_segment_are_refused()
    {
        var problem = Compile(alvo => alvo
                .Entity("tickets", t => t.OnCreated("a-b", OnCreated))
                .AddRetiredExtension("a_b", AlvoRetirement.Discard))
            .Problems.ShouldHaveSingleItem();

        problem.Message.ShouldContain("'a-b' and 'a_b'");
    }

    [Fact]
    public void A_timeout_that_is_not_positive_is_refused() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("instant", OnCreated, configure: o => o.Timeout = TimeSpan.Zero)))
            .Problems.ShouldHaveSingleItem().Locator.ShouldBe("extensions/instant/timeout");

    [Fact]
    public void Two_handlers_stating_one_order_on_one_operation_tie()
    {
        var tie = Compile(alvo => alvo.Entity("tickets", t => t
                .OnCreated("first", OnCreated, configure: o => o.Order = 5)
                .OnSaved("second", OnSaved, configure: o => o.Order = 5)))
            .Ties.ShouldHaveSingleItem();

        tie.Target.ShouldBe("tickets created");
        tie.Ids.ShouldBe(["first", "second"]);
    }

    [Fact]
    public void Handlers_that_never_stated_an_order_do_not_tie() =>
        Compile(alvo => alvo.Entity("tickets", t => t.OnCreated("first", OnCreated).OnCreated("second", OnCreated))).Ties.ShouldBeEmpty();

    private static AlvoEntityBuilder Register(AlvoEntityBuilder tickets, string verb, string when) => verb switch
    {
        "created" => tickets.OnCreated("on-it", OnCreated, when: when),
        "deleted" => tickets.OnDeleted("on-it", OnDeleted, when: when),
        _ => tickets.OnSaved("on-it", OnSaved, when: when),
    };

    private static IServiceProviderIsService NothingButTheBasics()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => call.Arg<Type>() != typeof(TimeProvider));

        return services;
    }
}
```

`test/MMLib.Alvo.Tests/Extensions/ExtensionBinderTests.cs`:

```csharp
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>The binder: strict at boot, lenient — and loud — on a catalog a runtime apply primed later.</summary>
public sealed class ExtensionBinderTests : IDisposable
{
    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;

    public ExtensionBinderTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public void The_boot_is_refused_with_every_problem_and_its_fix()
    {
        var binder = Binder(alvo => alvo
            .Entity("tickets", t => t.OnUpdated("typo", OnUpdated, when: "new.stauts == 'x'"))
            .Entity("tikets", t => t.OnCreated("misspelled", OnCreated)));

        var refusal = Should.Throw<AlvoStartupRefusedException>(() => binder.EnsureBindable(Catalog()));

        refusal.Message.ShouldContain("'typo' (eventHandler) at extensions/typo/when");
        refusal.Message.ShouldContain("'misspelled' (eventHandler) at extensions/misspelled/entity");
        refusal.FixSuggestion.ShouldContain("Did you mean 'tickets'?");
    }

    [Fact]
    public void A_tie_is_logged_at_boot_by_name()
    {
        var binder = Binder(alvo => alvo.Entity("tickets", t => t
            .OnCreated("first", OnCreated, configure: o => o.Order = 1)
            .OnCreated("second", OnCreated, configure: o => o.Order = 1)));

        binder.EnsureBindable(Catalog());

        _logs.Entries.ShouldContain(entry => entry.EventName == "ExtensionOrderTie" && entry.Message.Contains("first, second"));
    }

    [Fact]
    public void One_catalog_is_bound_once()
    {
        var binder = Binder(alvo => alvo.Entity("tickets", t => t.OnCreated("first", OnCreated)));
        var catalog = Catalog();

        binder.For(catalog).ShouldBeSameAs(binder.For(catalog));
    }

    [Fact]
    public void A_catalog_that_unbinds_a_handler_logs_it_and_binds_the_rest()
    {
        var binder = Binder(alvo => alvo
            .Entity("tickets", t => t.OnCreated("on-tickets", OnCreated))
            .Entity("notes", n => n.OnCreated("on-notes", OnCreated)));
        var withoutNotes = PolicyCatalog.Build(
            new() { ApiVersion = "alvo.dev/v1", Name = "handlers", Entities = new Dictionary<string, MMLib.Alvo.Descriptor.EntityDescriptor>(StringComparer.Ordinal) },
            new SchemaModel([Schema.Entities.Single(entity => entity.Name == "tickets")]),
            CelFixtures.Compiler);

        var bindings = binder.For(withoutNotes);
        binder.For(withoutNotes);

        bindings.Handlers.Select(handler => handler.Id).ShouldBe(["on-tickets"]);
        bindings.Unbound.Select(handler => handler.Id).ShouldBe(["on-notes"], "kept, so its events are recorded dead rather than dropped");
        _logs.Entries.Count(entry => entry.EventName == "ExtensionUnbound" && entry.Level == LogLevel.Error).ShouldBe(1);
    }

    private ExtensionBinder Binder(Action<IAlvoBuilder> configure) =>
        new(Registry(configure), CelFixtures.Compiler, EveryService(), _loggers.CreateLogger<ExtensionBinder>());
}
```

`test/MMLib.Alvo.TestHost.Tests/ExtensionBootRefusalTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// A host whose handler registrations do not fit its descriptor never starts (spec 2026-10-14 §7 HX-03 acceptance (7),
/// (14), (15)): one refusal, every problem in it, each naming the extension.
/// </summary>
public sealed class ExtensionBootRefusalTests
{
    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_when_typo_refuses_the_boot_naming_the_id_beside_every_other_problem()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(
            alvo => alvo.FromDescriptor(HelpDesk)
                .Entity("tickets", t => t.OnUpdated("ticket-closed", (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask, when: "new.stauts == 'closed'"))
                .Entity("tikets", t => t.OnCreated("misspelled-entity", (AlvoCreated<AlvoRecord> _) => Task.CompletedTask)),
            cancellationToken: Ct));

        refusal.Message.ShouldContain("'ticket-closed'");
        refusal.Message.ShouldContain("extensions/ticket-closed/when");
        refusal.Message.ShouldContain("'misspelled-entity'");
        refusal.FixSuggestion.ShouldContain("Did you mean 'tickets'?");
    }

    [Fact]
    public async Task A_star_handler_whose_condition_reads_a_field_refuses_the_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(
            alvo => alvo.FromDescriptor(HelpDesk)
                .Entity("*", all => all.OnSaved("audit-all", (AlvoSaved<AlvoRecord> _) => Task.CompletedTask, when: "new.title == 'x'")),
            cancellationToken: Ct));

        refusal.Message.ShouldContain("extensions/audit-all/when");
    }

    [Fact]
    public async Task Two_ids_deriving_one_event_name_refuse_the_boot()
    {
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => AlvoTestHost.StartAsync(
            alvo => alvo.FromDescriptor(HelpDesk)
                .Entity("tickets", t => t.OnCreated("a-b", (AlvoCreated<AlvoRecord> _) => Task.CompletedTask))
                .AddRetiredExtension("a_b", AlvoRetirement.Discard),
            cancellationToken: Ct));

        refusal.Message.ShouldContain("'a-b' and 'a_b'");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionBindingCompilerTests' --filter-class '*ExtensionBinderTests'`
Expected: FAIL (build error).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>One problem a registration has against the primed schema.</summary>
internal sealed record ExtensionProblem(string Id, string Kind, string Locator, string Message, string Fix);

/// <summary>Two or more registrations stating one <c>Order</c> on one target.</summary>
internal sealed record OrderTie(string Target, int Order, IReadOnlyList<string> Ids);

/// <summary>What binding a registry against one catalog produced.</summary>
internal sealed record ExtensionBindingResult(
    ExtensionBindings Bindings, IReadOnlyList<ExtensionProblem> Problems, IReadOnlyList<OrderTie> Ties);

/// <summary>A compiled <c>when</c> and the caller values it reads that an envelope may lack.</summary>
internal sealed record BoundCondition(CompiledExpression? Expression, RequiredContext Required, bool ReadsRoles)
{
    internal static BoundCondition Always { get; } = new(null, RequiredContext.None, false);

    internal static BoundCondition Of(CompiledExpression expression) => new(
        expression,
        new RequiredContext(
            TenantId: PolicyCatalogBuilder.ReferencesContextValue(expression.Root, CelContextValue.TenantId),
            UserId: PolicyCatalogBuilder.ReferencesContextValue(expression.Root, CelContextValue.UserId)),
        PolicyCatalogBuilder.ReferencesContextValue(expression.Root, CelContextValue.UserRoles));
}

/// <summary>A registration bound to one catalog: its condition per entity, or one for every event it selects.</summary>
internal sealed record BoundHandler(
    HandlerRegistration Registration, IReadOnlyDictionary<string, BoundCondition> Conditions, BoundCondition Fallback)
{
    internal string Id => Registration.Id;

    internal HandlerSettings Settings => Registration.Settings;
}

/// <summary>Every handler bound to one catalog, by <c>Order</c> and then registration order.</summary>
internal sealed class ExtensionBindings
{
    private readonly Dictionary<string, BoundHandler> _byId;

    internal ExtensionBindings(IEnumerable<BoundHandler> handlers, IEnumerable<HandlerRegistration>? unbound = null)
    {
        Unbound = [.. unbound ?? []];
        Handlers = [.. handlers.OrderBy(handler => handler.Settings.Order).ThenBy(handler => handler.Registration.Sequence)];
        _byId = Handlers.ToDictionary(handler => handler.Id, StringComparer.Ordinal);
    }

    internal static ExtensionBindings None { get; } = new([]);

    internal IReadOnlyList<BoundHandler> Handlers { get; }

    /// <summary>The registered handlers that do not fit this catalog; the fan-out records their events dead (<c>unbound</c>).</summary>
    internal IReadOnlyList<HandlerRegistration> Unbound { get; }

    internal TimeSpan LongestTimeout => Handlers.Count == 0 ? TimeSpan.Zero : Handlers.Max(handler => handler.Settings.Timeout);

    internal BoundHandler? Handler(string id) => _byId.GetValueOrDefault(id);
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionBindingCompiler.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Binds every registration of this host to one primed catalog and collects every problem at once (spec 2026-10-14
/// §4.2): unknown entities, conditions that do not compile or read what the verb's event cannot answer, ids, services.
/// </summary>
internal static class ExtensionBindingCompiler
{
    internal const string HandlerKind = "eventHandler";
    internal const string RetiredKind = "retired";

    private const string IdFix =
        "Use lower-case letters, digits and hyphens, starting with a letter, at most 63 characters — 'send-confirmation'.";

    internal static ExtensionBindingResult Compile(
        ExtensionRegistry registry, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services)
    {
        var problems = new List<ExtensionProblem>(IdProblems(registry));
        var bound = new List<BoundHandler>();
        foreach (var handler in registry.Handlers)
        {
            problems.AddRange(new HandlerBinding(handler, catalog, compiler, services).Bind(out var boundHandler));
            if (boundHandler is not null)
            {
                bound.Add(boundHandler);
            }
        }

        var refused = problems.Select(problem => problem.Id).ToHashSet(StringComparer.Ordinal);

        var bindings = new ExtensionBindings(
            bound.Where(handler => !refused.Contains(handler.Id)),
            registry.Handlers.Where(handler => refused.Contains(handler.Id)));

        return new(bindings, problems, Ties(registry));
    }

    private static IEnumerable<ExtensionProblem> IdProblems(ExtensionRegistry registry) =>
        [.. registry.All.Where(registration => !Grammatical(registration)).Select(BadId), .. Duplicates(registry), .. Collisions(registry)];

    private static bool Grammatical(ExtensionRegistration registration) =>
        registration is RetiredExtension ? ExtensionIds.IsRetirableId(registration.Id) : ExtensionIds.IsExtensionId(registration.Id);

    private static ExtensionProblem BadId(ExtensionRegistration registration) =>
        new(registration.Id, KindOf(registration), IdLocator(registration.Id), $"'{registration.Id}' is not an extension id.", IdFix);

    private static IEnumerable<ExtensionProblem> Duplicates(ExtensionRegistry registry) =>
        registry.All.GroupBy(registration => registration.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new ExtensionProblem(
                group.Key,
                KindOf(group.First()),
                IdLocator(group.Key),
                $"'{group.Key}' is registered {group.Count()} times ({string.Join(", ", group.Select(KindOf))}).",
                "Give each registration its own id: an id is how its deliveries are found again after a restart, and a retired id may not be registered."));

    private static IEnumerable<ExtensionProblem> Collisions(ExtensionRegistry registry) =>
        registry.All.Select(registration => registration.Id).Distinct(StringComparer.Ordinal)
            .GroupBy(ExtensionIds.DerivedSegment, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => Collision([.. group], group.Key, registry));

    private static ExtensionProblem Collision(IReadOnlyList<string> ids, string segment, ExtensionRegistry registry) => new(
        ids[0],
        KindOf(registry.All.First(registration => registration.Id == ids[0])),
        IdLocator(ids[0]),
        $"'{string.Join("' and '", ids)}' derive one event-name segment, '{segment}', so the events Alvo mints for them could not be told apart.",
        "Rename one of them.");

    private static string KindOf(ExtensionRegistration registration) => registration is RetiredExtension ? RetiredKind : HandlerKind;

    private static string IdLocator(string id) => $"extensions/{id}/id";

    private static IReadOnlyList<OrderTie> Ties(ExtensionRegistry registry) =>
        [.. registry.Handlers
            .SelectMany(handler => Targets(handler.Source).Select(target => (Target: target, Handler: handler)))
            .GroupBy(entry => (entry.Target, entry.Handler.Settings.Order))
            .Where(group => group.Count() > 1 && group.Any(entry => entry.Handler.Settings.OrderStated))
            .Select(group => new OrderTie(group.Key.Target, group.Key.Order, [.. group.Select(entry => entry.Handler.Id)]))];

    private static IEnumerable<string> Targets(HandlerSource source) => source switch
    {
        EntityEvents entities => entities.Entities.SelectMany(entity => Operations(entities.Verb).Select(operation => $"{entity} {operation}")),
        NamedEvent named => [named.Type],
        _ => [],
    };

    private static string[] Operations(HandlerVerb verb) => verb switch
    {
        HandlerVerb.Created => ["created"],
        HandlerVerb.Updated => ["updated"],
        HandlerVerb.Deleted => ["deleted"],
        _ => ["created", "updated"],
    };

    /// <summary>One handler's binding, collecting its own problems.</summary>
    private sealed class HandlerBinding(
        HandlerRegistration handler, PolicyCatalog catalog, ICelCompiler compiler, IServiceProviderIsService services)
    {
        private const string WhenFix = "Correct the condition: it is CEL over new., old., changed(field), @user and @tenant.";

        private readonly List<ExtensionProblem> _problems = [];

        internal IReadOnlyList<ExtensionProblem> Bind(out BoundHandler? bound)
        {
            ResolveServices();
            CheckTimeout();
            var named = handler.Source is EntityEvents { IsAll: false } entities ? entities : null;
            var conditions = named is null ? new Dictionary<string, BoundCondition>(StringComparer.Ordinal) : EntityConditions(named);
            var fallback = named is null ? FieldlessCondition() : BoundCondition.Always;
            bound = _problems.Count == 0 ? new BoundHandler(handler, conditions, fallback) : null;

            return _problems;
        }

        private void ResolveServices()
        {
            foreach (var type in handler.Invoker.Unresolvable(services))
            {
                Add("handler", $"The handler needs a {type.FullName} that the host's services cannot provide.",
                    $"Register {type.Name} in the host's services, or take what the handler needs from AlvoEventContext.");
            }
        }

        private void CheckTimeout()
        {
            if (handler.Settings.Timeout <= TimeSpan.Zero)
            {
                Add("timeout", $"A timeout of {handler.Settings.Timeout} lets no attempt run.", "Set Timeout to a positive duration, or leave the default 30 seconds.");
            }
        }

        private Dictionary<string, BoundCondition> EntityConditions(EntityEvents source)
        {
            var conditions = new Dictionary<string, BoundCondition>(StringComparer.Ordinal);
            if (source.Entities.Contains(EntityEvents.AllEntities))
            {
                Add("entity", "'*' binds every entity, so naming entities beside it says nothing.", "Register Entity(\"*\", …) on its own, or name the entities.");
                return conditions;
            }

            foreach (var name in source.Entities)
            {
                if (EntityNamed(name) is not { } entity)
                {
                    UnknownEntity(name);
                }
                else if (Condition(entity, source.Verb) is { } condition)
                {
                    conditions[name] = condition;
                }
            }

            return conditions;
        }

        private EntitySchema? EntityNamed(string name) =>
            catalog.Schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

        private void UnknownEntity(string name)
        {
            var known = catalog.Schema.Entities.Select(entity => entity.Name).Order(StringComparer.Ordinal).ToList();
            var closest = NameSuggestion.Closest(name, known);
            var list = $"Entities: {string.Join(", ", known)}.";
            Add("entity", $"'{name}' is not an entity of this project, so the handler would never run.",
                closest is null ? list : $"Did you mean '{closest}'? {list}");
        }

        private BoundCondition? Condition(EntitySchema entity, HandlerVerb verb)
        {
            if (handler.When is null)
            {
                return BoundCondition.Always;
            }

            var result = compiler.Compile(handler.When, CelProfile.Condition, entity);
            if (!result.IsSuccess)
            {
                result.Errors.ToList().ForEach(error => Add("when", $"{error.Message} (entity '{entity.Name}')", error.FixSuggestion ?? WhenFix));
                return null;
            }

            var before = _problems.Count;
            ImageRefusals(result.Expression!, verb);
            ExcludedReads(result.Expression!, entity);

            return _problems.Count == before ? BoundCondition.Of(result.Expression!) : null;
        }

        private void ImageRefusals(CompiledExpression expression, HandlerVerb verb)
        {
            foreach (var refusal in AfterImagePhase.Refusals(expression.Root, Images(verb), VerbName(verb), Locator("when")))
            {
                Add("when", refusal.Message, refusal.FixSuggestion ?? WhenFix);
            }
        }

        private void ExcludedReads(CompiledExpression expression, EntitySchema entity)
        {
            var excluded = FieldsRead(expression.Root)
                .Where(field => entity.EventImageExclusions.Contains(field, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal);
            foreach (var field in excluded)
            {
                Add("when",
                    $"The condition reads '{field}', which '{entity.Name}' leaves out of its event images (events.exclude): it would be decided by the field's absence on every event.",
                    $"Remove '{field}' from the condition, or from entities.{entity.Name}.events.exclude.");
            }
        }

        private BoundCondition FieldlessCondition()
        {
            if (handler.When is null)
            {
                return BoundCondition.Always;
            }

            var result = compiler.Compile(handler.When, CelProfile.Condition, new EntitySchema { Name = handler.Id, Fields = [] });
            if (result.IsSuccess)
            {
                return BoundCondition.Of(result.Expression!);
            }

            result.Errors.ToList().ForEach(error => Add("when", error.Message, FieldlessFix));
            return BoundCondition.Always;
        }

        private string FieldlessFix => handler.Source is NamedEvent
            ? "An application event's payload declares no fields to check a condition against: test only @user and @tenant here, and check the payload inside the handler."
            : "A '*' registration binds every entity, including ones added later: test only @user and @tenant here, or register the entities by name.";

        private void Add(string slot, string message, string fix) =>
            _problems.Add(new ExtensionProblem(handler.Id, HandlerKind, Locator(slot), message, fix));

        private string Locator(string slot) => $"extensions/{handler.Id}/{slot}";

        private static AfterImages Images(HandlerVerb verb) => verb switch
        {
            HandlerVerb.Updated => new(Pre: true, Post: true),
            HandlerVerb.Deleted => new(Pre: true, Post: false),
            _ => new(Pre: false, Post: true),
        };

        private static string VerbName(HandlerVerb verb) => $"On{verb}";

        private static IEnumerable<string> FieldsRead(CelNode node) =>
            OwnFields(node).Concat(CelTree.Children(node).SelectMany(FieldsRead));

        private static string[] OwnFields(CelNode node) => node switch
        {
            CelFieldRef reference => [reference.FieldName],
            CelChanged changed => [changed.FieldName],
            _ => [],
        };
    }
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionRefusal.cs`:

```csharp
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>The one start-up refusal listing every extension problem (spec 2026-10-14 §4.2: all problems at once).</summary>
internal static class ExtensionRefusal
{
    internal static AlvoStartupRefusedException Of(IReadOnlyList<ExtensionProblem> problems) => new(
        $"Alvo cannot start: {problems.Count} problem(s) in the extensions this host registers, all listed here:"
        + string.Concat(problems.Select(problem => $"{Environment.NewLine}- '{problem.Id}' ({problem.Kind}) at {problem.Locator}: {problem.Message}")),
        string.Join(Environment.NewLine, problems.Select(problem => $"'{problem.Id}': {problem.Fix}")));
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionLog.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>What the extension subsystem logs — ids and locators, never a URL, a body or an exception message.</summary>
internal static partial class ExtensionLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo found extensions stating one order {Order} on {Target}: {Ids}. They run in registration order; "
            + "give each its own Order if the order matters.")]
    internal static partial void ExtensionOrderTie(ILogger logger, string target, int order, string ids);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo could not bind extension {Id} to the current schema ({Locator}: {Problem}). Every event it would "
            + "handle is recorded as a dead delivery (unbound) until the descriptor or the registration is fixed.")]
    internal static partial void ExtensionUnbound(ILogger logger, string id, string locator, string problem);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo retired extension {Id} ({Retirement}): {Deliveries} unfinished deliveries were ended.")]
    internal static partial void ExtensionRetired(ILogger logger, string id, string retirement, int deliveries);
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;

using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Binds the registry to each primed catalog once: strictly at boot (<see cref="EnsureBindable"/> refuses every problem
/// at once), leniently afterwards (<see cref="For"/> binds what fits and logs the rest once per catalog).
/// </summary>
internal sealed class ExtensionBinder(
    ExtensionRegistry registry, ICelCompiler compiler, IServiceProviderIsService services, ILogger<ExtensionBinder> logger)
{
    private static readonly object _reportedMark = new();

    private readonly ConditionalWeakTable<PolicyCatalog, ExtensionBindingResult> _results = new();
    private readonly ConditionalWeakTable<PolicyCatalog, object> _reported = new();
    private readonly Lock _gate = new();

    internal void EnsureBindable(PolicyCatalog catalog)
    {
        var result = Result(catalog);
        foreach (var tie in result.Ties)
        {
            ExtensionLog.ExtensionOrderTie(logger, tie.Target, tie.Order, string.Join(", ", tie.Ids));
        }

        if (result.Problems.Count > 0)
        {
            throw ExtensionRefusal.Of(result.Problems);
        }

        MarkReported(catalog);
    }

    internal ExtensionBindings For(PolicyCatalog catalog)
    {
        var result = Result(catalog);
        if (MarkReported(catalog))
        {
            foreach (var problem in result.Problems)
            {
                ExtensionLog.ExtensionUnbound(logger, problem.Id, problem.Locator, problem.Message);
            }
        }

        return result.Bindings;
    }

    private ExtensionBindingResult Result(PolicyCatalog catalog)
    {
        lock (_gate)
        {
            if (!_results.TryGetValue(catalog, out var result))
            {
                result = ExtensionBindingCompiler.Compile(registry, catalog, compiler, services);
                _results.Add(catalog, result);
            }

            return result;
        }
    }

    private bool MarkReported(PolicyCatalog catalog)
    {
        lock (_gate)
        {
            if (_reported.TryGetValue(catalog, out _))
            {
                return false;
            }

            _reported.Add(catalog, _reportedMark);

            return true;
        }
    }
}
```

`src/MMLib.Alvo/Extensions/Setup.cs` — add before `return services;`: `services.TryAddSingleton<ExtensionBinder>();`

`src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs` — add the optional constructor parameter `ExtensionBinder? extensions = null` after `logger` (store it in `private readonly ExtensionBinder? _extensions;`, `using MMLib.Alvo.Extensions.Internal;`), and replace the last line of `Plan` with:

```csharp
        var catalog = PolicyCatalog.Build(descriptor, desired, _compiler);
        _extensions?.EnsureBindable(catalog);

        return new BootPlan(descriptor, desired, descriptorJson, catalog);
```

`src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` — in `AddBootPlan`, pass `provider.GetRequiredService<ExtensionBinder>()` as the fifth argument (`using MMLib.Alvo.Extensions.Internal;`).

`src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs`, `TryPlanTheStoredDescriptor` — add a catch **before** the existing one, so an extension refusal on the stored-descriptor path records `Failed` and stands the process up not ready, instead of escaping `StartingAsync` unrecorded (its `catch (AlvoStartupRefusedException) { throw; }` assumes the stage recorded it):

```csharp
        catch (AlvoStartupRefusedException refused)
        {
            boot = null;
            var reason =
                $"Alvo cannot serve project '{project}': the extensions this host registers do not fit the descriptor stored "
                + $"at revision {stored.Revision} — {refused.Message} The process is staying up, reporting not ready and "
                + "serving nothing; fix the registrations and redeploy the host, or apply a descriptor they fit.";

            _state.Failed(project, reason);
            BootRefused(_logger, reason);

            return false;
        }
```

`test/MMLib.Alvo.Tests/Migrations/BootServiceWorld.cs` — add `internal ExtensionBinder? Extensions { get; set; }` and pass it as the last argument of `Plan()`'s `new(…)` (`using MMLib.Alvo.Extensions.Internal;`).

`test/MMLib.Alvo.Tests/Migrations/AlvoBootServiceStoredDescriptorTests.cs` — add:

```csharp
    /// <summary>A host whose handlers do not fit the stored descriptor stays up, not ready, with the reason published.</summary>
    [Fact]
    public async Task Handlers_that_do_not_fit_a_stored_descriptor_stand_the_host_up_not_ready()
    {
        using var world = Dashboard().StoredVerbatim(BootServiceWorld.City);
        world.Extensions = MisfitHandlers();

        (await world.BootAsync()).ShouldBeNull("a stored-descriptor refusal never stops the process");

        world.State.Phase.ShouldBe(AlvoBootPhase.Failed);
        world.State.Failure.ShouldNotBeNull().ShouldContain("'orphan-handler'");
        world.PrimedEntities.ShouldBeEmpty();
        world.Log.ShouldContain(entry => entry.Level == LogLevel.Critical);
    }

    /// <summary>The same handlers on a code-first boot refuse the start: the file is the host's, and so is the fix.</summary>
    [Fact]
    public async Task Handlers_that_do_not_fit_a_descriptor_file_refuse_the_start()
    {
        using var world = new BootServiceWorld { Extensions = MisfitHandlers() }.BootingFrom(BootServiceWorld.City);

        (await world.BootAsync()).ShouldBeOfType<AlvoStartupRefusedException>().Message.ShouldContain("'orphan-handler'");
    }

    private static ExtensionBinder MisfitHandlers() => new(
        HandlerCatalog.Registry(alvo => alvo.Entity("no-such-entity", e => e.OnCreated("orphan-handler", HandlerCatalog.OnCreated))),
        new CelCompiler(),
        HandlerCatalog.EveryService(),
        NullLogger<ExtensionBinder>.Instance);
```

(with `using MMLib.Alvo.Extensions.Internal;`, `using MMLib.Alvo.Tests.Extensions;`, `using MMLib.Alvo.Expressions.Internal;` and `using Microsoft.Extensions.Logging.Abstractions;` as the file lacks them). If `BootServiceWorld`'s constructor is not usable with an object initializer, set `Extensions` after construction.

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindingCompiler.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRefusal.cs src/MMLib.Alvo/Extensions/Internal/ExtensionLog.cs src/MMLib.Alvo/Extensions/Setup.cs src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs test/MMLib.Alvo.Tests/Migrations/BootServiceWorld.cs test/MMLib.Alvo.Tests/Migrations/AlvoBootServiceStoredDescriptorTests.cs test/MMLib.Alvo.Tests/CapturingLogger.cs test/MMLib.Alvo.Tests/Extensions/HandlerCatalog.cs test/MMLib.Alvo.Tests/Extensions/ExtensionBindingCompilerTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionBinderTests.cs test/MMLib.Alvo.TestHost.Tests/ExtensionBootRefusalTests.cs`, then:
- `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionBindingCompilerTests' --filter-class '*ExtensionBinderTests' --filter-class '*DescriptorBootPlanTests' --filter-class '*AlvoBootServiceStoredDescriptorTests' --filter-class '*AlvoBootServiceTests'` — PASS
- `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*ExtensionBootRefusalTests'` — PASS (acceptance (7), (14), (15))

If a CEL compile error's wording differs from what a fact expects, assert the locator and id (which this task owns), never the compiler's sentence.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindingCompiler.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBinder.cs src/MMLib.Alvo/Extensions/Internal/ExtensionRefusal.cs src/MMLib.Alvo/Extensions/Internal/ExtensionLog.cs src/MMLib.Alvo/Extensions/Setup.cs src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs src/MMLib.Alvo/Migrations/Internal/AlvoBootService.cs test/MMLib.Alvo.Tests/Migrations/BootServiceWorld.cs test/MMLib.Alvo.Tests/Migrations/AlvoBootServiceStoredDescriptorTests.cs test/MMLib.Alvo.Tests/CapturingLogger.cs test/MMLib.Alvo.Tests/Extensions/HandlerCatalog.cs test/MMLib.Alvo.Tests/Extensions/ExtensionBindingCompilerTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionBinderTests.cs test/MMLib.Alvo.TestHost.Tests/ExtensionBootRefusalTests.cs
git commit -m "feat(extensions): bind handlers at boot and refuse every problem at once

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: Fan-out first — handler selection on the outbox dispatcher (M1, X6, R-M5, R-M6) **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Events/Internal/EnvelopeReading.cs`, `HandlerCondition.cs`, `DeliveryVocabulary.cs`, `DeliveryLog.cs`, `AlvoDeliveryMetrics.cs`, `DeliveryFanOut.cs`
- Modify: `src/MMLib.Alvo/Events/Internal/EventSubscriptions.cs` (members move to `EnvelopeReading`), `src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs` (selection), `src/MMLib.Alvo/Events/Internal/OutboxDispatcher.cs`, `src/MMLib.Alvo/Events/Setup.cs`
- Modify (I6, the hold-back release): `src/MMLib.Alvo.Abstractions/Events/IOutboxStore.cs` (`HoldBackAsync`), `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreOutboxStore.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxTable.cs`, `src/MMLib.Alvo.Testing/Events/OutboxStoreContractTests.cs` (one fact), and the three test doubles implementing the port: `test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs` (`RecordingOutboxStore`), `test/MMLib.Alvo.Tests/Events/EventSubscriptionsTests.cs` (`CapturingOutboxStore`), `FakeOutboxStore` in `OutboxDispatcherTests.cs`
- Modify: `test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.cs` (`Subject` takes a fan-out), `test/MMLib.Alvo.Tests/Events/EventPumpRegistrationTests.cs` (a delivery-store substitute)
- Create: `test/MMLib.Alvo.Tests/Events/DeliveryFanOutTests.cs`, `test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.FanOut.cs`
- Baselines: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` (`IOutboxStore.HoldBackAsync`)

**Interfaces:**
- Consumes: `ExtensionBinder.For`, `BoundHandler`, `BoundCondition` (Task 7); `IDeliveryStore`, `DeliveryRequest` (Task 3); `FakeDeliveryStore`, `ManualClock` (Task 3 tests); `HandlerCatalog` (Task 7 tests).
- Produces (internal): `static class EnvelopeReading { bool TryReadDataType(string type, out string entity, out DataOperation operation); EnvelopeCaller CallerOf(AlvoEvent); bool Holds(CompiledExpression, AlvoEventData, IPredicateEvaluator, AlvoContext) }`; `readonly record struct EnvelopeCaller(AlvoContext Context, bool CarriesRoles)`; `enum HandlerVerdict { Match, NoMatch, Failed }`; `static HandlerCondition.Judge(BoundCondition, AlvoEvent, IPredicateEvaluator, EnvelopeCaller)`; `BoundHandler.Selects(AlvoEvent) → BoundCondition?`; `ExtensionBindings.Selecting(AlvoEvent) → IEnumerable<(BoundHandler Handler, BoundCondition Condition)>`; `DeliveryKind.Handler = "handler"`; `DeliveryErrors { WhenFailed, ChainDepthExceeded, Timeout, EventMissing, EventUnreadable, LeaseExpired, Retired, Unbound }`; `static HandlerSources.Selects(HandlerSource, string eventType) → (bool Selected, string? Entity)`; `ExtensionBindings.UnboundSelecting(AlvoEvent) → IEnumerable<HandlerRegistration>`; `IOutboxStore.HoldBackAsync(Guid id, TimeSpan retryAfter, CancellationToken)`; `DeliveryLog.HandlerConditionFailed / ChainDepthExceeded`; `AlvoDeliveryMetrics { DeadName; DeadRecorded(string subscriber, string reason) }`; `sealed class DeliveryFanOut(IDeliveryStore, ExtensionBinder, IPredicateEvaluator, AlvoChainDepthLimit, ILogger<DeliveryFanOut>) { Task<int> FanOutAsync(AlvoEvent, PolicyCatalog, CancellationToken) }`; `OutboxDispatcher(…, EventActionExecutor executor, DeliveryFanOut fanOut, …)`.

**Behaviour (spec §4.8 *Fan-out*):** for each claimed entry, in id order, fan-out runs **before** the declarative after-hook actions. A handler whose `when` does not hold gets no row (acceptance (4)); one whose `when` reads a caller value the envelope lacks gets no row (fail closed, the after-hook rule); one whose `when` throws gets a `dead` `when-failed` row (R-M6); a matching one on an envelope whose `chaindepth + 1 > MaxDepth` gets a `dead` `chain-depth-exceeded` row and an Error `ChainDepthExceeded` (R-M5). A **storage** failure of the insert releases the entry like any failed attempt and **holds back** every later entry of the batch with the same partition key — handed back unprocessed, with the same backoff, **without consuming an outbox attempt** (`IOutboxStore.HoldBackAsync`, I6: an innocent entry must not walk to the outbox ceiling behind a failing one); other keys proceed. Across batches, rounds and a dispatcher that died between claiming an entry and fanning it out, order is kept by the delivery side (Task 4's lane condition): an ordered delivery is not claimable while an earlier entry of its key is undispatched below the outbox ceiling — so event 2 of a record can be fanned out first, but never delivered first (acceptance (5), B1). An event a registered-but-**unbound** handler would select by its source (entity and verb, or event name — no `when` is judged) gets a `dead` `unbound` delivery (I3), so nothing is dropped silently. A failing declarative action no longer touches the deliveries already recorded (acceptance (6)); its retry re-runs the actions, and re-fan-out is a no-op. An event that produced a delivery — even a dead one — counts as `alvo.events.dispatched`, never `filtered`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Events/DeliveryFanOutTests.cs`:

```csharp
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Events;

/// <summary>Which handlers one committed event fans out to, and how each delivery starts life (spec 2026-10-14 §4.8).</summary>
public sealed class DeliveryFanOutTests : IDisposable
{
    private static readonly PolicyCatalog _catalog = HandlerCatalog.Catalog();

    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeDeliveryStore _store = new(new ManualClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)));

    public DeliveryFanOutTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_matching_handler_gets_one_pending_delivery_and_an_unmatched_one_none()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t
            .OnUpdated("on-resolved", OnUpdated, when: "new.status == 'resolved'")
            .OnUpdated("on-open", OnUpdated, when: "new.status == 'open'")));

        (await fanOut.FanOutAsync(Event("entity.tickets.updated", Record(("status", "resolved")), Record(("status", "open"))), _catalog, Ct)).ShouldBe(1);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Subscriber.ShouldBe("on-resolved");
        row.Status.ShouldBe("pending");
        row.PartitionKey.ShouldBe("tickets:1");
    }

    [Fact]
    public async Task A_condition_that_throws_is_a_dead_when_failed_delivery()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("by-code", OnCreated, when: "int(new.code) > 0")));

        await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("code", "x"))), _catalog, Ct);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("when-failed");
        _logs.Entries.ShouldContain(entry => entry.EventName == "HandlerConditionFailed" && entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(7, "pending")]
    [InlineData(8, "dead")]
    public async Task An_event_at_the_depth_limit_fans_out_dead(int chainDepth, string status)
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("echo", OnCreated)));

        await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("code", "1")), depth: chainDepth), _catalog, Ct);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe(status);
        if (status == "dead")
        {
            row.LastErrorType.ShouldBe("chain-depth-exceeded");
            _logs.Entries.ShouldContain(entry => entry.EventName == "ChainDepthExceeded" && entry.Level == LogLevel.Error);
        }
    }

    [Fact]
    public async Task A_condition_reading_a_tenant_the_event_lacks_fans_out_nothing()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("tenant-only", OnCreated, when: "new.id != @tenant.id")));

        (await fanOut.FanOutAsync(Event("entity.tickets.created", Record(("id", Guid.NewGuid()))), _catalog, Ct)).ShouldBe(0);

        _store.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_storage_failure_escapes_so_the_dispatcher_can_hold_the_record_back()
    {
        var fanOut = FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("any", OnCreated)));
        _store.FanOutFailures = 1;

        await Should.ThrowAsync<InvalidOperationException>(() => fanOut.FanOutAsync(Event("entity.tickets.created", Record()), _catalog, Ct));

        _store.Rows.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("entity.tickets.created", 1)]
    [InlineData("entity.notes.updated", 1)]
    [InlineData("entity.tickets.deleted", 0)]
    public async Task A_star_on_saved_covers_every_entity_s_creates_and_updates(string type, int expected) =>
        (await FanOut(alvo => alvo.Entity("*", all => all.OnSaved("audit-all", OnSaved)))
            .FanOutAsync(Event(type, Record()), _catalog, Ct)).ShouldBe(expected);

    [Theory]
    [InlineData("orders.approved", 1)]
    [InlineData("orders.rejected", 0)]
    public async Task An_application_event_reaches_the_handlers_of_its_exact_name(string type, int expected) =>
        (await FanOut(alvo => alvo.AddEventHandler("on-approved", "orders.approved", OnRecord))
            .FanOutAsync(Event(type, Record()), _catalog, Ct)).ShouldBe(expected);

    /// <summary>
    /// A registered handler that does not fit the current catalog is never silently skipped (I3): every event its source
    /// selects gets a dead <c>unbound</c> delivery, visible and replayable.
    /// </summary>
    [Fact]
    public async Task An_event_a_handler_unbound_from_the_catalog_would_select_records_a_dead_unbound_delivery()
    {
        var fanOut = FanOut(alvo => alvo.Entity("notes", n => n.OnCreated("broken", OnCreated, when: "new.missing == 'x'")));

        (await fanOut.FanOutAsync(Event("entity.notes.created", Record()), _catalog, Ct)).ShouldBe(1);
        (await fanOut.FanOutAsync(Event("entity.tickets.created", Record()), _catalog, Ct)).ShouldBe(0, "its source does not select tickets");

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("unbound");
        _logs.Entries.Count(entry => entry.EventName == "ExtensionUnbound").ShouldBe(1, "logged once per catalog, not per event");
    }

    [Fact]
    public async Task A_delivery_carries_the_event_s_tenant()
    {
        var tenant = Guid.NewGuid().ToString();
        await FanOut(alvo => alvo.Entity("tickets", t => t.OnCreated("any", OnCreated)))
            .FanOutAsync(Event("entity.tickets.created", Record()) with { TenantId = tenant }, _catalog, Ct);

        _store.Rows.ShouldHaveSingleItem().TenantId.ShouldBe(tenant);
    }

    private DeliveryFanOut FanOut(Action<IAlvoBuilder> configure) => new(
        _store,
        new ExtensionBinder(Registry(configure), CelFixtures.Compiler, EveryService(), _loggers.CreateLogger<ExtensionBinder>()),
        CelFixtures.Evaluator,
        AlvoChainDepthLimit.Default,
        _loggers.CreateLogger<DeliveryFanOut>());

    private static AlvoEvent Event(string type, AlvoRecord record, AlvoRecord? old = null, int depth = 0) => new()
    {
        Id = AlvoEventId.Create(DateTimeOffset.UtcNow),
        Source = AlvoEvent.DefaultSource,
        Type = type,
        Time = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero),
        Subject = "tickets/1",
        PartitionKey = "tickets:1",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "c",
        ChainDepth = depth,
        Data = new AlvoEventData { Record = record, OldRecord = old, Changed = ["status"] },
    };

    private static AlvoRecord Record(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal));
}
```

`test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.FanOut.cs`:

```csharp
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using System.Net;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Fan-out runs first (spec 2026-10-14 §4.8, Appendix A M1): a failing after-hook action no longer keeps a handler from
/// its delivery, and a failed fan-out holds back the later events of its record.
/// </summary>
public sealed partial class OutboxDispatcherTests
{
    private const string DealHandler = "deal-won";

    [Fact]
    public async Task Fan_out_runs_first_and_a_failing_after_hook_action_keeps_the_handler_s_delivery()
    {
        var deliveries = new FakeDeliveryStore(TimeProvider.System);
        var store = StoreWith(WonDeal());
        var pump = Subject(store, Ready, status: HttpStatusCode.InternalServerError, fanOut: FanOutTo(deliveries));

        await pump.PumpOneBatchAsync(Cancellation);

        store.DispatchedIds.ShouldBeEmpty("the webhook failed, so the entry is handed back");
        deliveries.Rows.ShouldHaveSingleItem().Subscriber.ShouldBe(DealHandler);
    }

    [Fact]
    public async Task A_fan_out_that_fails_holds_back_the_later_events_of_its_record()
    {
        var deliveries = new FakeDeliveryStore(TimeProvider.System) { FanOutFailures = 1 };
        var first = WonDeal();
        var second = WonDeal();
        var store = StoreWith(first, second);
        var pump = Subject(store, Ready, fanOut: FanOutTo(deliveries));

        await pump.PumpOneBatchAsync(Cancellation);

        deliveries.Rows.ShouldBeEmpty();
        store.ReleasedIds.ShouldBe([first.Id], "only the entry whose fan-out failed used an attempt");
        store.HeldBackIds.ShouldBe([second.Id]);
        store.EntryOf(second.Id).Attempts.ShouldBe(0, "a held-back entry was not tried, so its claim is not counted");
        store.HeldBackFor.ShouldBe([store.Backoffs[0]], "the later event waits exactly as long as the one it must follow");

        await pump.PumpOneBatchAsync(Cancellation);

        var claim = new DeliveryClaim { Claimant = "worker", Ordered = [DealHandler], BatchSize = 10, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10 };
        (await deliveries.ClaimAsync(claim, Cancellation)).ShouldHaveSingleItem().EventId
            .ShouldBe(first.Id, "event 2 of the same record is not delivered before event 1");
    }

    [Fact]
    public async Task An_event_that_fans_out_is_dispatched_rather_than_filtered()
    {
        var deliveries = new FakeDeliveryStore(TimeProvider.System);
        var store = StoreWith(Event("entity.deals.updated", Record(("stage", "lead")), Record(("stage", "lead"))));
        var pump = Subject(store, Ready, fanOut: FanOutTo(deliveries));

        await pump.PumpOneBatchAsync(Cancellation);

        store.DispatchedIds.ShouldHaveSingleItem();
        deliveries.Rows.ShouldHaveSingleItem();
    }

    private DeliveryFanOut FanOutTo(FakeDeliveryStore deliveries) => new(
        deliveries,
        new ExtensionBinder(
            HandlerCatalog.Registry(alvo => alvo.Entity("deals", d => d.OnUpdated(DealHandler, (AlvoUpdated<AlvoRecord> _) => Task.CompletedTask))),
            CelFixtures.Compiler,
            HandlerCatalog.EveryService(),
            _loggers.CreateLogger<ExtensionBinder>()),
        CelFixtures.Evaluator,
        AlvoChainDepthLimit.Default,
        _loggers.CreateLogger<DeliveryFanOut>());
}
```

In `test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.cs`, give `Subject` a last parameter `DeliveryFanOut? fanOut = null`, pass it to the constructor after the `EventActionExecutor` argument as `fanOut ?? NoHandlers()`, and add:

```csharp
    /// <summary>A fan-out with no handler registered: every pre-HX-03 fact here keeps its meaning.</summary>
    private DeliveryFanOut NoHandlers() => new(
        new FakeDeliveryStore(TimeProvider.System),
        new ExtensionBinder(ExtensionRegistry.Empty, CelFixtures.Compiler, HandlerCatalog.EveryService(), _loggers.CreateLogger<ExtensionBinder>()),
        CelFixtures.Evaluator,
        AlvoChainDepthLimit.Default,
        _loggers.CreateLogger<DeliveryFanOut>());
```

(with `using MMLib.Alvo.Extensions.Internal;` and `using MMLib.Alvo.Tests.Extensions;`). In `EventPumpRegistrationTests.Registered`, add `collection.AddSingleton(Substitute.For<IDeliveryStore>());` beside the outbox substitute.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DeliveryFanOutTests' --filter-class '*OutboxDispatcherTests'`
Expected: FAIL (build error: `DeliveryFanOut` does not exist).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Events/Internal/EnvelopeReading.cs` — the members move here from `EventSubscriptions`, unchanged in behaviour, so after-hooks and handlers read an envelope one way (and #398/#399's fixes, when they land, cover both):

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;

using System.Globalization;

namespace MMLib.Alvo.Events.Internal;

/// <summary>How a stored envelope is read for a subscription: its data-event type, its caller as at the event, and a condition over its images.</summary>
/// <remarks>
/// Shared by <see cref="EventSubscriptions"/> (descriptor after-hooks) and <see cref="HandlerCondition"/> (host handlers),
/// so the two subscription kinds cannot drift. The caller is built from the envelope — <c>authid</c>, <c>tenantid</c>,
/// <c>authclaims</c> — never from whoever dispatches; what the envelope does not carry is left absent.
/// </remarks>
internal static class EnvelopeReading
{
    private const char TypeSeparator = '.';
    private const int TypeSegments = 3;
    private const int PrefixSegment = 0;
    private const int EntitySegment = 1;
    private const int OperationSegment = 2;
    private const string DataEventPrefix = "entity";

    private static readonly IReadOnlySet<Role> _noRoles = new HashSet<Role> { Role.Anon };

    private static readonly Dictionary<string, DataOperation> _operations = new(StringComparer.Ordinal)
    {
        ["created"] = DataOperation.Create,
        ["updated"] = DataOperation.Update,
        ["deleted"] = DataOperation.Delete,
    };

    /// <summary>Reads the entity and the operation out of a data event's type; anything else answers <see langword="false"/>.</summary>
    internal static bool TryReadDataType(string type, out string entity, out DataOperation operation)
    {
        entity = string.Empty;
        operation = default;
        var segments = type.Split(TypeSeparator);
        if (segments.Length != TypeSegments || !string.Equals(segments[PrefixSegment], DataEventPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        entity = segments[EntitySegment];

        return _operations.TryGetValue(segments[OperationSegment], out operation);
    }

    /// <summary>The caller one event's conditions resolve against: the credential, tenant and roles the envelope records.</summary>
    internal static EnvelopeCaller CallerOf(AlvoEvent @event)
    {
        var actor = UserId.TryParse(@event.AuthId, CultureInfo.InvariantCulture, out var user) ? user : default;
        TenantId? tenant = TenantId.TryParse(@event.TenantId, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        var names = AlvoEventProvenance.RolesIn(@event.AuthClaims);
        var roles = names is { Count: > 0 } ? RoleCatalog.Create(names).Resolve(names) : null;

        return new EnvelopeCaller(new AlvoContext { User = actor, Roles = roles ?? _noRoles, Tenant = tenant }, roles is not null);
    }

    /// <summary>
    /// Evaluates <paramref name="condition"/> over the event's images, answering <c>changed(f)</c> from the envelope's
    /// own change list whenever the evaluator can (spec E7); a host's own evaluator compares the images.
    /// </summary>
    internal static bool Holds(CompiledExpression condition, AlvoEventData data, IPredicateEvaluator evaluator, AlvoContext context) =>
        evaluator is IChangeListPredicateEvaluator changeAware
            ? changeAware.Evaluate(condition, data.Record ?? AlvoRecord.Empty, data.OldRecord, data.Changed, context)
            : evaluator.Evaluate(condition, data.Record ?? AlvoRecord.Empty, data.OldRecord, context);
}

/// <summary>One event's caller, and whether its <c>authclaims</c> named any role — parsed once per event.</summary>
/// <param name="Context">The caller conditions are evaluated against.</param>
/// <param name="CarriesRoles">Whether <see cref="AlvoContext.Roles"/> came from the envelope rather than standing in for none.</param>
internal readonly record struct EnvelopeCaller(AlvoContext Context, bool CarriesRoles);
```

`src/MMLib.Alvo/Events/Internal/EventSubscriptions.cs`: delete `CallerOf`, the nested `EventCaller`, `_noRoles`, `Holds`, `TryReadSubscription`, the six `const` fields after it and `_operations` (their remarks now live on `EnvelopeReading`); in what remains replace `TryReadSubscription(` with `EnvelopeReading.TryReadDataType(`, `CallerOf(@event)` with `EnvelopeReading.CallerOf(@event)`, `Holds(` with `EnvelopeReading.Holds(`, and the parameter type `EventCaller` with `EnvelopeCaller` (in `UnreadProvenance`, `Selects`, `CarriesWhatItReads`). `EventSubscriptionsTests` must pass unchanged — that is this refactor's proof.

`src/MMLib.Alvo/Events/Internal/HandlerCondition.cs`:

```csharp
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Events.Internal;

/// <summary>What one handler's <c>when</c> says about one event.</summary>
internal enum HandlerVerdict
{
    Match,
    NoMatch,
    Failed,
}

/// <summary>
/// Judges a handler's condition at fan-out, after the commit and before any delivery exists (spec X6). A condition reading
/// a caller value the envelope lacks matches nothing, as an after-hook's does; one that throws is an outcome — the
/// fan-out records it dead — never a failure of the batch (R-M6).
/// </summary>
internal static class HandlerCondition
{
    internal static HandlerVerdict Judge(BoundCondition condition, AlvoEvent @event, IPredicateEvaluator evaluator, EnvelopeCaller caller)
    {
        if (condition.Expression is null)
        {
            return HandlerVerdict.Match;
        }

        if (condition.Required.IsMissingFrom(caller.Context) || (condition.ReadsRoles && !caller.CarriesRoles))
        {
            return HandlerVerdict.NoMatch;
        }

        try
        {
            return EnvelopeReading.Holds(condition.Expression, @event.Data, evaluator, caller.Context) ? HandlerVerdict.Match : HandlerVerdict.NoMatch;
        }
        catch (Exception)
        {
            return HandlerVerdict.Failed;
        }
    }
}
```

`src/MMLib.Alvo/Events/Internal/DeliveryVocabulary.cs`:

```csharp
namespace MMLib.Alvo.Events.Internal;

/// <summary>What runs a delivery.</summary>
internal static class DeliveryKind
{
    internal const string Handler = "handler";
}

/// <summary>The fixed reasons a delivery records; any other failure records its exception's type name, never its message.</summary>
internal static class DeliveryErrors
{
    internal const string WhenFailed = "when-failed";
    internal const string ChainDepthExceeded = "chain-depth-exceeded";
    internal const string Timeout = "timeout";
    internal const string EventMissing = "event-missing";
    internal const string LeaseExpired = "lease-expired";
    internal const string Retired = "retired";
    internal const string Unbound = "unbound";
    internal const string EventUnreadable = "event-unreadable";
}
```

`src/MMLib.Alvo/Events/Internal/DeliveryLog.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Events.Internal;

/// <summary>What deliveries log — ids, counts and type names; never a URL, a body or an exception message.</summary>
internal static partial class DeliveryLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo recorded a dead delivery of event {EventId} for {Subscriber}: its when condition failed while it "
            + "was judged. Fix the condition, then redeliver it.")]
    internal static partial void HandlerConditionFailed(ILogger logger, string subscriber, Guid eventId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo recorded a dead delivery of event {EventId} for {Subscriber}: the event is {ChainDepth} hops deep, "
            + "running the handler would make it {Depth}, past the limit of {MaxDepth}. Something reacts to its own effects.")]
    internal static partial void ChainDepthExceeded(ILogger logger, string subscriber, Guid eventId, int chainDepth, int depth, int maxDepth);
}
```

`src/MMLib.Alvo/Events/Internal/AlvoDeliveryMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Events.Internal;

/// <summary>The delivery instruments, on the events meter (spec 2026-10-14 §4.8); tags are ids and fixed words only.</summary>
internal static class AlvoDeliveryMetrics
{
    internal const string DeadName = "alvo.delivery.dead";
    internal const string SubscriberTag = "alvo.subscriber";
    internal const string ReasonTag = "alvo.reason";

    private static readonly Meter _meter = new(AlvoEventMetrics.MeterName);

    internal static Counter<long> Dead { get; } = _meter.CreateCounter<long>(
        DeadName, "{delivery}", "Deliveries that ended dead: fanned out dead, or failed at their last attempt.");

    internal static void DeadRecorded(string subscriber, string reason) =>
        Dead.Add(1, new KeyValuePair<string, object?>(SubscriberTag, subscriber), new KeyValuePair<string, object?>(ReasonTag, reason));
}
```

`src/MMLib.Alvo/Events/Internal/DeliveryFanOut.cs`:

```csharp
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Records, for one committed event, one delivery per bound handler whose condition holds — before the event's declarative
/// actions run, so their failure cannot keep a handler from its work (spec 2026-10-14 §4.8, M1).
/// </summary>
/// <remarks>
/// The store's uniqueness on (event, subscriber) makes a second fan-out of one event a no-op, which is what lets the outbox
/// retry an entry whose actions failed. Only a storage failure escapes; the dispatcher then holds the record's later events
/// back.
/// </remarks>
internal sealed class DeliveryFanOut(
    IDeliveryStore store,
    ExtensionBinder binder,
    IPredicateEvaluator evaluator,
    AlvoChainDepthLimit depth,
    ILogger<DeliveryFanOut> logger)
{
    internal async Task<int> FanOutAsync(AlvoEvent @event, PolicyCatalog catalog, CancellationToken cancellationToken)
    {
        var requests = RequestsFor(@event, binder.For(catalog));
        if (requests.Count > 0)
        {
            await store.FanOutAsync(requests, cancellationToken).ConfigureAwait(false);
        }

        return requests.Count;
    }

    private List<DeliveryRequest> RequestsFor(AlvoEvent @event, ExtensionBindings bindings)
    {
        var caller = EnvelopeReading.CallerOf(@event);
        var requests = new List<DeliveryRequest>();
        foreach (var (handler, condition) in bindings.Selecting(@event))
        {
            if (Request(@event, handler, HandlerCondition.Judge(condition, @event, evaluator, caller)) is { } request)
            {
                requests.Add(request);
            }
        }

        requests.AddRange(bindings.UnboundSelecting(@event).Select(handler => Unbound(@event, handler)));

        return requests;
    }

    private DeliveryRequest? Request(AlvoEvent @event, BoundHandler handler, HandlerVerdict verdict) => verdict switch
    {
        HandlerVerdict.NoMatch => null,
        HandlerVerdict.Failed => WhenFailed(@event, handler),
        _ when @event.ChainDepth + 1 > depth.MaxDepth => TooDeep(@event, handler),
        _ => Pending(@event, handler),
    };

    private DeliveryRequest WhenFailed(AlvoEvent @event, BoundHandler handler)
    {
        DeliveryLog.HandlerConditionFailed(logger, handler.Id, @event.Id);
        AlvoDeliveryMetrics.DeadRecorded(handler.Id, DeliveryErrors.WhenFailed);

        return Pending(@event, handler) with { DeadReason = DeliveryErrors.WhenFailed };
    }

    private DeliveryRequest TooDeep(AlvoEvent @event, BoundHandler handler)
    {
        DeliveryLog.ChainDepthExceeded(logger, handler.Id, @event.Id, @event.ChainDepth, @event.ChainDepth + 1, depth.MaxDepth);
        AlvoDeliveryMetrics.DeadRecorded(handler.Id, DeliveryErrors.ChainDepthExceeded);

        return Pending(@event, handler) with { DeadReason = DeliveryErrors.ChainDepthExceeded };
    }

    private static DeliveryRequest Pending(AlvoEvent @event, BoundHandler handler) =>
        Request(@event, handler.Id);

    private static DeliveryRequest Request(AlvoEvent @event, string subscriber) => new()
    {
        EventId = @event.Id,
        Subscriber = subscriber,
        PartitionKey = @event.PartitionKey,
        TenantId = @event.TenantId,
        Kind = DeliveryKind.Handler,
    };

    private DeliveryRequest Unbound(AlvoEvent @event, HandlerRegistration handler)
    {
        AlvoDeliveryMetrics.DeadRecorded(handler.Id, DeliveryErrors.Unbound);

        return Request(@event, handler.Id) with { DeadReason = DeliveryErrors.Unbound };
    }
}
```

`src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs` — add to `BoundHandler` (with `using MMLib.Alvo.Events;` and `using MMLib.Alvo.Events.Internal;`):

```csharp
    internal BoundCondition? Selects(AlvoEvent @event) => HandlerSources.Selects(Registration.Source, @event.Type) switch
    {
        (false, _) => null,
        (true, { } entity) when Registration.Source is EntityEvents { IsAll: false } => Conditions.GetValueOrDefault(entity),
        _ => Fallback,
    };
```

and a new file `src/MMLib.Alvo/Extensions/Internal/HandlerSources.cs` — which events a handler's source selects, `when` aside (shared by bound selection and the unbound dead-letter):

```csharp
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Extensions.Internal;

internal static class HandlerSources
{
    /// <summary>Whether <paramref name="source"/> selects an event of <paramref name="eventType"/>, and for which entity.</summary>
    internal static (bool Selected, string? Entity) Selects(HandlerSource source, string eventType) => source switch
    {
        NamedEvent named => (string.Equals(named.Type, eventType, StringComparison.Ordinal), null),
        EntityEvents entities => SelectsDataEvent(entities, eventType),
        _ => (false, null),
    };

    private static (bool Selected, string? Entity) SelectsDataEvent(EntityEvents source, string eventType)
    {
        if (!EnvelopeReading.TryReadDataType(eventType, out var entity, out var operation) || !Covers(source.Verb, operation))
        {
            return (false, null);
        }

        return (source.IsAll || source.Entities.Contains(entity, StringComparer.Ordinal), entity);
    }

    private static bool Covers(HandlerVerb verb, DataOperation operation) => verb switch
    {
        HandlerVerb.Created => operation == DataOperation.Create,
        HandlerVerb.Updated => operation == DataOperation.Update,
        HandlerVerb.Deleted => operation == DataOperation.Delete,
        _ => operation is DataOperation.Create or DataOperation.Update,
    };
}
```

and to `ExtensionBindings`:

```csharp
    internal IEnumerable<(BoundHandler Handler, BoundCondition Condition)> Selecting(AlvoEvent @event)
    {
        foreach (var handler in Handlers)
        {
            if (handler.Selects(@event) is { } condition)
            {
                yield return (handler, condition);
            }
        }
    }

    internal IEnumerable<HandlerRegistration> UnboundSelecting(AlvoEvent @event) =>
        Unbound.Where(handler => HandlerSources.Selects(handler.Source, @event.Type).Selected);
```

`src/MMLib.Alvo/Events/Internal/OutboxDispatcher.cs`:
- Constructor: add `DeliveryFanOut fanOut,` after `EventActionExecutor executor,` (and its `<param name="fanOut">Records the deliveries each claimed event fans out to, before its actions run.</param>`).
- `RoundAsync`: create `var stopped = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);` after `round` and pass it: `await DispatchAsync(entry, round, stopped, stoppingToken).ConfigureAwait(false);`.
- Replace `DispatchAsync` and `DeliverAsync` with the following (keep `DeliverAsync`'s remarks, and add to them: "**Fan-out runs first.** A failing action re-runs the actions on the next attempt; the deliveries already recorded are not touched, and a second fan-out of the event records nothing."):

```csharp
    private async Task DispatchAsync(
        OutboxEntry entry, DrainRound round, Dictionary<string, TimeSpan> stopped, CancellationToken stoppingToken)
    {
        if (stopped.TryGetValue(entry.PartitionKey, out var heldFor))
        {
            await store.HoldBackAsync(entry.Id, heldFor, stoppingToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await DeliverAsync(entry, round, stopped, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
        {
            round.Failed();
            await AbandonAttemptAsync(entry, failure, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DeliverAsync(
        OutboxEntry entry, DrainRound round, Dictionary<string, TimeSpan> stopped, CancellationToken stoppingToken)
    {
        var @event = AlvoEventJson.Read(entry.Payload);
        var deliveries = await FanOutAsync(entry, @event, stopped, stoppingToken).ConfigureAwait(false);
        var hooks = EventSubscriptions.Matching(Catalog, @event, evaluator, logger);

        foreach (var hook in hooks)
        {
            round.Ran(hook.Path);
            await RunAsync(hook, @event, stoppingToken).ConfigureAwait(false);
        }

        await store.MarkDispatchedAsync(entry.Id, stoppingToken).ConfigureAwait(false);

        Counted(hooks.Count + deliveries).Add(1);
    }

    /// <summary>
    /// Fans the event out; a storage failure holds back every later entry of the batch with the same partition key, for
    /// as long as this one waits, so no later event of the record can be fanned out first (spec 2026-10-14 §4.8).
    /// </summary>
    private async Task<int> FanOutAsync(
        OutboxEntry entry, AlvoEvent @event, Dictionary<string, TimeSpan> stopped, CancellationToken stoppingToken)
    {
        try
        {
            return await fanOut.FanOutAsync(@event, Catalog, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            stopped[entry.PartitionKey] = RetryAfter(entry);
            throw;
        }
    }
```

`src/MMLib.Alvo/Events/Setup.cs`: add `services.TryAddSingleton<DeliveryFanOut>();` before `services.TryAddSingleton<OutboxDispatcher>();`.

**The hold-back release (I6).** `src/MMLib.Alvo.Abstractions/Events/IOutboxStore.cs` — add after `ReleaseAsync`:

```csharp
    /// <summary>
    /// Hands <paramref name="id"/> back <b>without counting its claim as an attempt</b>: it was held behind an earlier entry
    /// of its partition key whose processing failed, and was never tried itself.
    /// </summary>
    /// <param name="id">The entry's id.</param>
    /// <param name="retryAfter">How long the entry stays unclaimable — the backoff of the entry it waits behind.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the entry is handed back.</returns>
    /// <remarks>
    /// <see cref="ReleaseAsync"/> keeps the attempt, which is right for an entry that failed. An entry that only waited
    /// must not walk to the attempt ceiling behind one that keeps failing: past the ceiling it is never claimed again.
    /// One statement, on the port's standing rule.
    /// </remarks>
    Task HoldBackAsync(Guid id, TimeSpan retryAfter, CancellationToken cancellationToken = default);
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxTable.cs` — add:

```csharp
    internal static string HoldBackSql(string tableName) =>
        $"UPDATE {tableName} SET claimed_at = @claimed_at, claimed_by = NULL, attempts = attempts - 1 WHERE id = @id AND attempts > 0";
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreOutboxStore.cs` — add beside `ReleaseAsync`:

```csharp
    /// <inheritdoc/>
    public Task HoldBackAsync(Guid id, TimeSpan retryAfter, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryAfter.Ticks);

        return ExecuteAsync(
            OutboxTable.HoldBackSql(_tableName),
            command => RelationalSqlBatch.AddParameter(
                command, "@claimed_at", StoredInstant.Text(_time.GetUtcNow() + retryAfter)),
            id,
            cancellationToken);
    }
```

`src/MMLib.Alvo.Testing/Events/OutboxStoreContractTests.cs` — add:

```csharp
    /// <summary>A held-back entry is handed back without its claim counting as an attempt, and waits its backoff.</summary>
    [Fact]
    public async Task A_held_back_entry_does_not_spend_an_attempt()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var ids = await world.SeedAsync(count: 1);
        await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct);

        await world.Store.HoldBackAsync(ids[0], TimeSpan.FromSeconds(5), Ct);

        (await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct)).ShouldBeEmpty("it waits its backoff");
        world.Advance(TimeSpan.FromSeconds(6));
        (await world.Store.ClaimAsync(Claimant, batchSize: 1, MaxAttempts, _lease, Ct)).ShouldHaveSingleItem().Attempts.ShouldBe(1);
    }
```

(use the suite's existing `Claimant`, `MaxAttempts`, `_lease` and `Ct` members; if a name differs, use the suite's own). The three doubles: `RecordingOutboxStore` and `CapturingOutboxStore` add `public Task HoldBackAsync(Guid id, TimeSpan retryAfter, CancellationToken cancellationToken = default) => Task.CompletedTask;`; `FakeOutboxStore` adds two lists beside `_released`/`_backoffs` and the member:

```csharp
        private readonly List<Guid> _heldBack = [];
        private readonly List<TimeSpan> _heldBackFor = [];

        internal IReadOnlyList<Guid> HeldBackIds => Snapshot(_heldBack);

        internal IReadOnlyList<TimeSpan> HeldBackFor => Snapshot(_heldBackFor);

        public Task HoldBackAsync(Guid id, TimeSpan retryAfter, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var entry = _entries.Single(candidate => candidate.Id == id);
                entry.Claimed = false;
                entry.Attempts--;
                _heldBack.Add(id);
                _heldBackFor.Add(retryAfter);
            }

            return Task.CompletedTask;
        }
```

Accept the Abstractions public-API diff (exactly `IOutboxStore.HoldBackAsync`); the interface member breaks third-party implementers of a pre-1.0 port — CHANGELOG *Changed* (Task 14) says so.

- [ ] **Step 4: Run them to verify they pass**

Run (the hold-back files first): `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Events/IOutboxStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreOutboxStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxTable.cs src/MMLib.Alvo.Testing/Events/OutboxStoreContractTests.cs src/MMLib.Alvo/Extensions/Internal/HandlerSources.cs test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs test/MMLib.Alvo.Tests/Events/EventSubscriptionsTests.cs`, `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteOutboxStoreTests'` (PASS, incl. `A_held_back_entry_does_not_spend_an_attempt`), and `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'` (accept the one member). Then: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Events/Internal/EnvelopeReading.cs src/MMLib.Alvo/Events/Internal/HandlerCondition.cs src/MMLib.Alvo/Events/Internal/DeliveryVocabulary.cs src/MMLib.Alvo/Events/Internal/DeliveryLog.cs src/MMLib.Alvo/Events/Internal/AlvoDeliveryMetrics.cs src/MMLib.Alvo/Events/Internal/DeliveryFanOut.cs src/MMLib.Alvo/Events/Internal/EventSubscriptions.cs src/MMLib.Alvo/Events/Internal/OutboxDispatcher.cs src/MMLib.Alvo/Events/Setup.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs test/MMLib.Alvo.Tests/Events/DeliveryFanOutTests.cs test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.FanOut.cs test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.cs test/MMLib.Alvo.Tests/Events/EventPumpRegistrationTests.cs`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DeliveryFanOutTests' --filter-class '*OutboxDispatcherTests' --filter-class '*EventSubscriptionsTests' --filter-class '*EventPumpRegistrationTests' --filter-class '*SqlTextConfinedToRendererArchitectureTests'`
Expected: PASS — every pre-existing `OutboxDispatcherTests` and `EventSubscriptionsTests` fact unchanged.

- [ ] **Step 5: Rings and commit**

Run `scripts/test-ring0` and `scripts/test-ring1` (slice end; the outbox chaos criterion in ring2 is untouched until Task 13).

```bash
git add src/MMLib.Alvo.Abstractions/Events/IOutboxStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreOutboxStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxTable.cs src/MMLib.Alvo.Testing/Events/OutboxStoreContractTests.cs src/MMLib.Alvo/Extensions/Internal/HandlerSources.cs test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs test/MMLib.Alvo.Tests/Events/EventSubscriptionsTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt src/MMLib.Alvo/Events/Internal/EnvelopeReading.cs src/MMLib.Alvo/Events/Internal/HandlerCondition.cs src/MMLib.Alvo/Events/Internal/DeliveryVocabulary.cs src/MMLib.Alvo/Events/Internal/DeliveryLog.cs src/MMLib.Alvo/Events/Internal/AlvoDeliveryMetrics.cs src/MMLib.Alvo/Events/Internal/DeliveryFanOut.cs src/MMLib.Alvo/Events/Internal/EventSubscriptions.cs src/MMLib.Alvo/Events/Internal/OutboxDispatcher.cs src/MMLib.Alvo/Events/Setup.cs src/MMLib.Alvo/Extensions/Internal/ExtensionBindings.cs test/MMLib.Alvo.Tests/Events/DeliveryFanOutTests.cs test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.FanOut.cs test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.cs test/MMLib.Alvo.Tests/Events/EventPumpRegistrationTests.cs
git commit -m "feat(events): fan out to handler deliveries before the after-hook actions

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 9: `DeliveryRunner` — one delivery: flow, caller, outcome, retry, span link, metrics (B2, R-M2, X5, R-6)

**Files:**
- Create: `src/MMLib.Alvo/Events/Internal/EventActor.cs`, `HandlerMessage.cs`, `DeliveryBackoff.cs`, `DeliveryRunner.cs`
- Modify: `src/MMLib.Alvo/Events/Internal/AlvoHookTelemetry.cs` (link + attempt tag), `AlvoDeliveryMetrics.cs` (three instruments), `DeliveryLog.cs` (three lines), `src/MMLib.Alvo/Events/Setup.cs`
- Create: `test/MMLib.Alvo.Tests/Events/EventActorTests.cs`, `DeliveryBackoffTests.cs`, `DeliveryRunnerTests.cs`
- Modify: `test/MMLib.Alvo.Tests/InternalMemberReferencesTests.cs` (the core is the flow's production setter)

**Interfaces:**
- Consumes: `ClaimedDelivery`, `DeliveryAttempt`, `DeliveryFailure`, `IDeliveryStore` (Task 3); `BoundHandler`, `HandlerSettings`, `HandlerSource` (Tasks 6–7); `IAlvoSessions` (Task 2); `AlvoEventContext` (Task 5); `DeliveryErrors`, `DeliveryLog`, `AlvoDeliveryMetrics` (Task 8); `AlvoFlow.Enter` (HX-01, internal to Abstractions).
- Produces (internal): `static class EventActor { TenantId? TenantOf(AlvoEvent); AlvoContext Of(AlvoEvent, RoleCatalog declared) }`; `static class HandlerMessage { object For(AlvoEvent, HandlerSource) }`; `sealed class DeliveryBackoff(Func<double> sample) { DeliveryBackoff(); const double Jitter = 0.2; TimeSpan? After(int attempt, AlvoRetryPolicy policy) }`; `enum DeliveryRunOutcome { Succeeded, Failed, Dead, LeaseLost, Shutdown }`; `sealed class DeliveryRunner(IDeliveryStore, IServiceScopeFactory, IAlvoSessions, IPolicyCatalogProvider, DeliveryBackoff, TimeProvider, ILogger<DeliveryRunner>) { static readonly TimeSpan LeaseGrace = 30 s; TimeSpan SettleGrace { get; init; }; Task<DeliveryRunOutcome> RunAsync(ClaimedDelivery, BoundHandler, string claimant, CancellationToken stopping) }`; `AlvoHookTelemetry.StartDelivery(string subscriber, AlvoEvent, int? attempt = null)` (links the envelope's `traceparent`), `AlvoHookTelemetry.AttemptTag = "alvo.delivery.attempt"`; `AlvoDeliveryMetrics.Recorded(DeliveryAttempt, DeliveryRunOutcome)`, `AlvoDeliveryMetrics.Lagged(string subscriber, TimeSpan)`; `DeliveryLog.DeliveryLeaseLost / DeliveryFailed / DeliveryDead`.

**Behaviour:** the runner enters `AlvoFlow` with `{ CorrelationId = event's, CausationId = event id, Depth = chaindepth + 1 }` — so a handler writing through `ctx.Data` **or** a DI-injected `IAlvoData` with a fresh `AlvoContext.System(tenant)` writes one hop deeper (B2, R-M2) — and with **no** `TraceParent`, so writes made in the handler are traced under the delivery span. The span keeps the ambient parent and links the envelope's `traceparent`. The handler runs in its own DI scope as `AlvoContext.System(event tenant)` (`AlvoRunAs.System`) or the envelope's actor (`AlvoRunAs.EventActor`, E8: `authid`, `tenantid`, the `authclaims` roles still declared — an undeclared name is dropped, none left means `anon`). Outcomes: success → fenced complete; `AlvoChainDepthExceededException` — thrown, or wrapped as an inner exception — → dead `chain-depth-exceeded`, never retried; an envelope whose `tenantid` (or, for `EventActor`, `authid`) is present but unreadable → dead `event-unreadable`, never run as a tenantless or anonymous caller (M10); timeout → failed `timeout`, **enforced, not cooperative** (I4): the runner awaits the handler with `WaitAsync(Timeout)`, then cancels its token and waits up to `LeaseGrace` (30 s) for it to stop; any other exception → failed with its type's full name; a retry waits `AlvoRetryPolicy` ±20 %; a failure at the policy's last attempt is dead; the host stopping → nothing recorded, the claim left for its lease; a delivery whose envelope is gone → dead `event-missing`; a reclaimed delivery already past the ceiling (a process that died in it every time) → dead `lease-expired`, not run; a completion whose fence no longer holds → `LeaseLost`, logged, nothing changed.

**The fencing rule (I4).** The runner answers for an attempt exactly once, and no later than `Timeout + LeaseGrace` after it started — inside the claim's own lease (`longest Timeout + LeaseGrace` from the claim), so no other claimant can start the same delivery while this attempt is still unanswered. A handler still running when the runner answers — it ignored its token for 30 s — is abandoned to the background (.NET has no thread abort, spec X15): it may overlap the retry, its completion is never recorded, and **a handler must therefore be idempotent on `EventId`**, which at-least-once requires anyway. A handler that blocks its thread synchronously holds that thread until it returns. The pool slot is freed when the runner answers, so an ignoring handler cannot starve the dispatcher.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Events/EventActorTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Tests.Events;

/// <summary>The caller as at the event (spec 2026-10-14 E8), rebuilt from the envelope for <c>AlvoRunAs.EventActor</c>.</summary>
public sealed class EventActorTests
{
    private static readonly Guid _user = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _tenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void The_actor_is_the_recorded_caller_with_the_roles_it_still_holds_by_declaration()
    {
        var actor = EventActor.Of(Event(_user.ToString(), "{\"roles\":[\"agent\",\"authenticated\",\"retired-role\"]}"), RoleCatalog.Create(["agent"]));

        actor.User.Value.ShouldBe(_user);
        actor.Tenant.ShouldBe(new TenantId(_tenant));
        actor.Roles.Select(role => role.Name).Order(StringComparer.Ordinal).ShouldBe(["agent", "authenticated"]);
        actor.ActorKind.ShouldBe(AlvoActorKind.User);
    }

    [Fact]
    public void An_actor_without_a_readable_role_holds_only_anon() =>
        EventActor.Of(Event(_user.ToString(), authClaims: null), RoleCatalog.BuiltInOnly).Roles.ShouldBe([Role.Anon]);

    [Fact]
    public void An_anonymous_change_has_an_anonymous_actor_in_its_tenant()
    {
        var actor = EventActor.Of(Event(authId: null, authClaims: null), RoleCatalog.BuiltInOnly);

        actor.User.ShouldBe(default);
        actor.Tenant.ShouldBe(new TenantId(_tenant));
    }

    private static AlvoEvent Event(string? authId, string? authClaims) => new()
    {
        Id = AlvoEventId.Create(DateTimeOffset.UtcNow),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.created",
        Time = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero),
        Subject = "tickets/1",
        PartitionKey = "tickets:1",
        AuthType = AlvoEventAuthType.User,
        AuthId = authId,
        AuthClaims = authClaims,
        TenantId = _tenant.ToString(),
        CorrelationId = "c",
        Data = new AlvoEventData { Record = AlvoRecord.Empty },
    };
}
```

`test/MMLib.Alvo.Tests/Events/DeliveryBackoffTests.cs`:

```csharp
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Events;

public sealed class DeliveryBackoffTests
{
    [Theory]
    [InlineData(0.0, 4.0)]
    [InlineData(0.5, 5.0)]
    [InlineData(1.0, 6.0)]
    public void A_wait_is_spread_by_a_fifth_either_way(double sample, double seconds) =>
        new DeliveryBackoff(() => sample).After(1, AlvoRetryPolicy.Default).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void There_is_no_wait_after_the_last_attempt() =>
        new DeliveryBackoff(() => 0.5).After(7, AlvoRetryPolicy.Default).ShouldBeNull();
}
```

`test/MMLib.Alvo.Tests/Events/DeliveryRunnerTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;

using System.Collections.Concurrent;
using System.Diagnostics;

namespace MMLib.Alvo.Tests.Events;

/// <summary>One delivery, end to end over the in-memory store: who it runs as, how deep, and how each outcome is recorded.</summary>
public sealed class DeliveryRunnerTests : IDisposable
{
    private const string Handler = "on-created";
    private const string Claimant = "worker-1";
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    private static readonly Guid _tenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly PolicyCatalog _catalog = HandlerCatalog.Catalog();

    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeDeliveryStore _store;

    public DeliveryRunnerTests()
    {
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        _store = new FakeDeliveryStore(_clock);
    }

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_handler_that_returns_completes_its_delivery_and_records_the_attempt()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        var delivery = await ClaimedAsync();

        (await runner.RunAsync(delivery, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Succeeded);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("succeeded");
        _store.Attempts.ShouldBe([(delivery.Id, 1, "succeeded")]);
    }

    [Fact]
    public async Task The_handler_runs_one_hop_deeper_than_its_event_and_a_fresh_system_context_inherits_it()
    {
        AlvoProvenance? ambient = null;
        AlvoProvenance? effective = null;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) =>
        {
            ambient = AlvoFlow.Provenance;
            effective = AlvoEventProvenance.Effective(AlvoContext.System(null));
            return Task.CompletedTask;
        });
        var @event = await ClaimedEventAsync(chainDepth: 3);

        await runner.RunAsync(@event.Delivery, handler, Claimant, Ct);

        ambient.ShouldNotBeNull().Depth.ShouldBe(4);
        ambient.CausationId.ShouldBe(@event.Event.Id.ToString());
        ambient.CorrelationId.ShouldBe(@event.Event.CorrelationId);
        ambient.TraceParent.ShouldBeNull("writes in a handler are traced under the delivery span, not the original request");
        effective.ShouldNotBeNull().Depth.ShouldBe(4);
        AlvoFlow.Provenance.ShouldBeNull("the flow ends with the delivery");
    }

    [Fact]
    public async Task A_system_handler_acts_as_the_system_in_the_event_s_tenant()
    {
        AlvoContext? caller = null;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
        {
            caller = context.Caller;
            return Task.CompletedTask;
        });

        await runner.RunAsync((await ClaimedEventAsync()).Delivery, handler, Claimant, Ct);

        caller.ShouldNotBeNull().User.ShouldBe(AlvoContext.System(null).User);
        caller.Tenant.ShouldBe(new TenantId(_tenant));
        caller.Roles.ShouldBe([Role.Admin]);
    }

    [Fact]
    public async Task An_event_actor_handler_acts_as_the_caller_who_made_the_change()
    {
        AlvoContext? caller = null;
        var user = Guid.NewGuid();
        var (runner, handler) = Arrange(
            (AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
            {
                caller = context.Caller;
                return Task.CompletedTask;
            },
            options => options.RunAs = AlvoRunAs.EventActor);

        await runner.RunAsync((await ClaimedEventAsync(authId: user.ToString())).Delivery, handler, Claimant, Ct);

        caller.ShouldNotBeNull().User.Value.ShouldBe(user);
        caller.Tenant.ShouldBe(new TenantId(_tenant));
    }

    [Fact]
    public async Task A_failure_waits_for_its_retry_and_records_the_exception_s_type_never_its_message()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException("card 4111-1111")));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Failed);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("retrying");
        row.LastErrorType.ShouldBe(typeof(InvalidOperationException).FullName);
        row.NotBefore.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(5));
        _logs.Entries.ShouldAllBe(entry => !entry.Message.Contains("4111"));
    }

    [Fact]
    public async Task The_last_attempt_s_failure_is_dead()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException()), options => options.Retry = AlvoRetryPolicy.None);

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("dead");
        _logs.Entries.ShouldContain(entry => entry.EventName == "DeliveryDead" && entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_write_refused_for_depth_is_dead_at_once_and_never_retried()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.FromException(AlvoChainDepthExceededException.At(9, 8)));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    [Fact]
    public async Task A_handler_that_outlives_its_timeout_is_cancelled_and_counted_as_failed()
    {
        var (runner, handler) = Arrange(
            async (AlvoCreated<AlvoRecord> _, CancellationToken ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct),
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            time: TimeProvider.System);

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Failed);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("timeout");
    }

    [Fact]
    public async Task A_handler_that_ignores_its_token_is_answered_at_its_timeout_and_only_once()
    {
        var never = new TaskCompletionSource();
        var (runner, handler) = Arrange(
            (AlvoCreated<AlvoRecord> _) => never.Task,
            options => options.Timeout = TimeSpan.FromMilliseconds(50),
            time: TimeProvider.System,
            settle: TimeSpan.FromMilliseconds(50));

        var outcome = await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        never.SetResult();

        outcome.ShouldBe(DeliveryRunOutcome.Failed, "the runner answers at the timeout whatever the handler does");
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("timeout");
        _store.Attempts.Count.ShouldBe(1, "the attempt is answered once; the handler finishing late records nothing");
    }

    [Fact]
    public async Task A_depth_refusal_wrapped_in_another_exception_is_still_dead_at_once()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) =>
            Task.FromException(new InvalidOperationException("wrapped", AlvoChainDepthExceededException.At(9, 8))));

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    [Fact]
    public async Task An_envelope_with_an_unreadable_tenant_is_dead_and_never_runs_as_a_tenantless_system()
    {
        var ran = false;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => { ran = true; return Task.CompletedTask; });

        var claimed = await ClaimedEventAsync(tenantText: "not-a-tenant");
        (await runner.RunAsync(claimed.Delivery, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        ran.ShouldBeFalse();
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("event-unreadable");
    }

    [Fact]
    public async Task A_shutdown_mid_handler_records_nothing_and_leaves_the_claim()
    {
        using var stopping = new CancellationTokenSource();
        var (runner, handler) = Arrange(async (AlvoCreated<AlvoRecord> _, CancellationToken ct) =>
        {
            await stopping.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        (await runner.RunAsync(await ClaimedAsync(), handler, Claimant, stopping.Token)).ShouldBe(DeliveryRunOutcome.Shutdown);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("running");
        _store.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_late_finisher_whose_lease_was_reclaimed_changes_nothing_and_says_so()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        var late = await ClaimedAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _store.ClaimAsync(Claim(), Ct);

        (await runner.RunAsync(late, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.LeaseLost);

        _store.Rows.ShouldHaveSingleItem().Attempts.ShouldBe(2);
        _store.Attempts.ShouldBeEmpty();
        _logs.Entries.ShouldContain(entry => entry.EventName == "DeliveryLeaseLost");
    }

    [Fact]
    public async Task A_delivery_reclaimed_past_its_last_attempt_is_dead_without_running()
    {
        var ran = false;
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => { ran = true; return Task.CompletedTask; }, options => options.Retry = AlvoRetryPolicy.None);
        await ClaimedAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = (await _store.ClaimAsync(Claim(), Ct)).Single();

        (await runner.RunAsync(reclaimed, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        ran.ShouldBeFalse();
        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("lease-expired");
    }

    [Fact]
    public async Task A_delivery_whose_envelope_is_gone_is_dead()
    {
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);

        (await runner.RunAsync(await ClaimedAsync() with { Payload = null }, handler, Claimant, Ct)).ShouldBe(DeliveryRunOutcome.Dead);

        _store.Rows.ShouldHaveSingleItem().LastErrorType.ShouldBe("event-missing");
    }

    [Fact]
    public async Task The_delivery_span_links_the_envelope_trace_and_keeps_the_ambient_parent()
    {
        using var spans = new Spans();
        var (runner, handler) = Arrange((AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        var claimed = await ClaimedEventAsync(traceParent: TraceParent);

        var root = await spans.WithinAsync(() => runner.RunAsync(claimed.Delivery, handler, Claimant, Ct));

        var span = spans.Recorded.ShouldHaveSingleItem();
        span.TraceId.ShouldBe(root, "the parent stays the drain or loop that ran it, which is what the test host's recorder scopes by");
        span.Links.ShouldHaveSingleItem().Context.TraceId.ToString().ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        span.GetTagItem("alvo.subscriber").ShouldBe(Handler);
        span.GetTagItem("alvo.delivery.attempt").ShouldBe(1);
        span.GetTagItem("alvo.outcome").ShouldBe("succeeded");
    }

    private (DeliveryRunner Runner, BoundHandler Handler) Arrange(
        Delegate handle, Action<AlvoHandlerOptions>? configure = null, TimeProvider? time = null, TimeSpan? settle = null)
    {
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t.OnCreated(Handler, handle, configure: configure)));
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), _loggers.CreateLogger<ExtensionBinder>());
        var catalogs = new PolicyCatalogProvider();
        catalogs.SetCurrent("handlers", _catalog);
        var runner = new DeliveryRunner(
            _store,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new AlvoSessions(Substitute.For<IAlvoData>()),
            catalogs,
            new DeliveryBackoff(() => 0.5),
            time ?? _clock,
            _loggers.CreateLogger<DeliveryRunner>())
        {
            SettleGrace = settle ?? DeliveryRunner.LeaseGrace,
        };

        return (runner, binder.For(_catalog).Handler(Handler).ShouldNotBeNull());
    }

    private async Task<ClaimedDelivery> ClaimedAsync() => (await ClaimedEventAsync()).Delivery;

    private async Task<(AlvoEvent Event, ClaimedDelivery Delivery)> ClaimedEventAsync(
        int chainDepth = 0, string? authId = null, string? traceParent = null, string? tenantText = null)
    {
        var @event = new AlvoEvent
        {
            Id = AlvoEventId.Create(_clock.GetUtcNow()),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.tickets.created",
            Time = _clock.GetUtcNow(),
            Subject = "tickets/1",
            PartitionKey = "tickets:1",
            AuthType = AlvoEventAuthType.User,
            AuthId = authId,
            AuthClaims = "{\"roles\":[\"authenticated\"]}",
            TenantId = tenantText ?? _tenant.ToString(),
            CorrelationId = "corr-1",
            ChainDepth = chainDepth,
            TraceParent = traceParent,
            Data = new AlvoEventData { Record = AlvoRecord.Empty },
        };
        _store.Publish(@event);
        await _store.FanOutAsync(
            [new DeliveryRequest { EventId = @event.Id, Subscriber = Handler, PartitionKey = @event.PartitionKey, TenantId = @event.TenantId, Kind = DeliveryKind.Handler }],
            Ct);

        return (@event, (await _store.ClaimAsync(Claim(), Ct)).Single());
    }

    private static DeliveryClaim Claim() =>
        new() { Claimant = Claimant, Ordered = [Handler], BatchSize = 10, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10 };

    /// <summary>Listens to the hook source under one fact's own root trace.</summary>
    private sealed class Spans : IDisposable
    {
        private const string TestSource = "MMLib.Alvo.Tests.DeliveryRunnerSpans";

        private readonly ActivitySource _test = new(TestSource);
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;
        private ActivityTraceId _trace;

        internal Spans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is AlvoHookTelemetry.SourceName or TestSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.Source.Name == AlvoHookTelemetry.SourceName && activity.TraceId == _trace)
                    {
                        _stopped.Enqueue(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal IReadOnlyList<Activity> Recorded => [.. _stopped];

        internal async Task<ActivityTraceId> WithinAsync(Func<Task> act)
        {
            var root = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
            _trace = root.TraceId;
            using (_test.StartActivity("fact", ActivityKind.Internal, root))
            {
                await act();
            }

            return root.TraceId;
        }

        public void Dispose()
        {
            _listener.Dispose();
            _test.Dispose();
        }
    }
}
```

In `test/MMLib.Alvo.Tests/InternalMemberReferencesTests.cs` add:

```csharp
    /// <summary>HX-03 is the flow's first production setter: the delivery runner, in the core, which the ban allows.</summary>
    [Fact]
    public void The_scan_sees_the_core_reach_the_flow_setter()
        => InternalMemberReferences.Of(typeof(PolicyEngine).Assembly).ShouldContain("MMLib.Alvo.AlvoFlow::Enter");
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EventActorTests' --filter-class '*DeliveryBackoffTests' --filter-class '*DeliveryRunnerTests' --filter-class '*InternalMemberReferencesTests'`
Expected: FAIL (build error; once it builds, the flow-setter fact fails until the runner calls `AlvoFlow.Enter`).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Events/Internal/EventActor.cs`:

```csharp
using System.Globalization;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The caller as at the event (spec 2026-10-14 E8): its id, tenant and the roles its envelope recorded, resolved against
/// the roles the descriptor still declares — a role that is no longer declared is not held.
/// </summary>
internal static class EventActor
{
    internal static TenantId? TenantOf(AlvoEvent @event) =>
        TenantId.TryParse(@event.TenantId, CultureInfo.InvariantCulture, out var tenant) ? tenant : null;

    internal static AlvoContext Of(AlvoEvent @event, RoleCatalog declared)
    {
        if (!UserId.TryParse(@event.AuthId, CultureInfo.InvariantCulture, out var user))
        {
            return AlvoContext.Anonymous with { Tenant = TenantOf(@event) };
        }

        return new AlvoContext { User = user, Roles = RolesOf(@event, declared), Tenant = TenantOf(@event), ActorKind = KindOf(@event.AuthType) };
    }

    private static HashSet<Role> RolesOf(AlvoEvent @event, RoleCatalog declared)
    {
        var roles = (AlvoEventProvenance.RolesIn(@event.AuthClaims) ?? [])
            .SelectMany(name => declared.TryGet(name, out var role) ? new[] { role } : [])
            .ToHashSet();

        return roles.Count > 0 ? roles : [Role.Anon];
    }

    private static AlvoActorKind KindOf(string authType) => authType switch
    {
        AlvoEventAuthType.ApiKey => AlvoActorKind.ApiKey,
        AlvoEventAuthType.User => AlvoActorKind.User,
        AlvoEventAuthType.System => AlvoActorKind.System,
        AlvoEventAuthType.Unauthenticated => AlvoActorKind.Anonymous,
        _ => AlvoActorKind.Unknown,
    };
}
```

`src/MMLib.Alvo/Events/Internal/HandlerMessage.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Events.Internal;

/// <summary>What a handler is handed for one event: the verb's images over <see cref="AlvoRecord"/>, or the payload.</summary>
internal static class HandlerMessage
{
    internal static object For(AlvoEvent @event, HandlerSource source) => source is EntityEvents entities
        ? ForVerb(@event.Data, entities.Verb)
        : @event.Data.Record ?? AlvoRecord.Empty;

    private static object ForVerb(AlvoEventData data, HandlerVerb verb) => verb switch
    {
        HandlerVerb.Created => new AlvoCreated<AlvoRecord> { New = data.Record ?? AlvoRecord.Empty },
        HandlerVerb.Updated => new AlvoUpdated<AlvoRecord> { Old = data.OldRecord ?? AlvoRecord.Empty, New = data.Record ?? AlvoRecord.Empty },
        HandlerVerb.Deleted => new AlvoDeleted<AlvoRecord> { Old = data.OldRecord ?? AlvoRecord.Empty },
        _ => new AlvoSaved<AlvoRecord> { New = data.Record ?? AlvoRecord.Empty, Old = data.OldRecord },
    };
}
```

`src/MMLib.Alvo/Events/Internal/DeliveryBackoff.cs`:

```csharp
using MMLib.Alvo.Extensions;

using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Events.Internal;

/// <summary>The wait before a failed delivery's next attempt: the policy's delay, spread by up to a fifth either way (spec X5).</summary>
internal sealed class DeliveryBackoff(Func<double> sample)
{
    internal const double Jitter = 0.2;

    [SuppressMessage("Security", "CA5394", Justification = "Retry jitter spreads load; it guards nothing.")]
    internal DeliveryBackoff()
        : this(Random.Shared.NextDouble)
    {
    }

    internal TimeSpan? After(int attempt, AlvoRetryPolicy policy) =>
        policy.DelayAfter(attempt) is { } delay ? delay * (1 - Jitter + (2 * Jitter * sample())) : null;
}
```

`src/MMLib.Alvo/Events/Internal/AlvoHookTelemetry.cs` — replace `StartDelivery` and add the tag constant and the link:

```csharp
    internal const string AttemptTag = "alvo.delivery.attempt";

    /// <summary>
    /// Starts the span for one run of <paramref name="subscriber"/> over <paramref name="event"/>, under whatever is ambient,
    /// linked — never parented — to the envelope's own trace context.
    /// </summary>
    internal static Activity? StartDelivery(string subscriber, AlvoEvent @event, int? attempt = null)
    {
        var delivery = _source.StartActivity(DeliveryName, ActivityKind.Consumer, parentContext: default, links: LinkTo(@event));
        if (delivery is not { IsAllDataRequested: true })
        {
            return delivery;
        }

        delivery.DisplayName = $"{DeliveryName} {subscriber}";
        delivery.SetTag(SubscriberTag, subscriber);
        delivery.SetTag(EventIdTag, @event.Id.ToString());
        delivery.SetTag(EventTypeTag, @event.Type);
        if (attempt is { } number)
        {
            delivery.SetTag(AttemptTag, number);
        }

        return delivery;
    }

    private static ActivityLink[]? LinkTo(AlvoEvent @event) =>
        @event.TraceParent is { } traceParent && ActivityContext.TryParse(traceParent, @event.TraceState, isRemote: true, out var origin)
            ? [new ActivityLink(origin)]
            : null;
```

Add to the class remarks: "Every `alvo.delivery` span keeps the ambient parent — the test host's drain, or nothing under the background loop — and carries the envelope's `traceparent` as a **link**, the messaging convention for a consumer that runs later than its producer. Parenting it on the envelope would move a test host's runs out of the drain trace its recorder scopes by."

`src/MMLib.Alvo/Events/Internal/AlvoDeliveryMetrics.cs` — add after `Dead`:

```csharp
    internal const string DurationName = "alvo.delivery.duration";
    internal const string AttemptsName = "alvo.delivery.attempts";
    internal const string LagName = "alvo.delivery.lag";
    internal const string OutcomeTag = "alvo.outcome";

    internal static Histogram<double> Durations { get; } = _meter.CreateHistogram<double>(
        DurationName, "ms", "How long one delivery attempt ran.");

    internal static Counter<long> AttemptCount { get; } = _meter.CreateCounter<long>(
        AttemptsName, "{attempt}", "Delivery attempts, by outcome.");

    internal static Histogram<double> Lags { get; } = _meter.CreateHistogram<double>(
        LagName, "ms", "How long after its event a delivery attempt started.");

    internal static void Recorded(DeliveryAttempt attempt, DeliveryRunOutcome outcome)
    {
        var subscriber = new KeyValuePair<string, object?>(SubscriberTag, attempt.Delivery.Subscriber);
        var ended = new KeyValuePair<string, object?>(OutcomeTag, OutcomeName(outcome));
        Durations.Record(attempt.Duration.TotalMilliseconds, subscriber, ended);
        AttemptCount.Add(1, subscriber, ended);
    }

    internal static void Lagged(string subscriber, TimeSpan lag) =>
        Lags.Record(Math.Max(0, lag.TotalMilliseconds), new KeyValuePair<string, object?>(SubscriberTag, subscriber));

    private static string OutcomeName(DeliveryRunOutcome outcome) => outcome switch
    {
        DeliveryRunOutcome.Succeeded => "succeeded",
        DeliveryRunOutcome.Dead => "dead",
        DeliveryRunOutcome.LeaseLost => "lease-lost",
        _ => "failed",
    };
```

`src/MMLib.Alvo/Events/Internal/DeliveryLog.cs` — add:

```csharp
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo's attempt {Attempt} of delivery {DeliveryId} for {Subscriber} finished after its lease was taken "
            + "over; nothing was recorded, and the claimant now holding the delivery answers for it.")]
    internal static partial void DeliveryLeaseLost(ILogger logger, Guid deliveryId, string subscriber, int attempt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo's attempt {Attempt} of delivery {DeliveryId} for {Subscriber} failed ({ErrorType}); it is retried "
            + "on the handler's schedule.")]
    internal static partial void DeliveryFailed(ILogger logger, Guid deliveryId, string subscriber, int attempt, string errorType);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo gave up on delivery {DeliveryId} for {Subscriber} after attempt {Attempt} ({ErrorType}). It is dead; "
            + "with per-record order it holds back this handler's later events of the same record until it is redelivered "
            + "or retired.")]
    internal static partial void DeliveryDead(ILogger logger, Guid deliveryId, string subscriber, int attempt, string errorType);
```

`src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;

using System.Diagnostics;
using System.Globalization;

namespace MMLib.Alvo.Events.Internal;

/// <summary>How one claimed delivery ended, for the dispatcher's tally.</summary>
internal enum DeliveryRunOutcome
{
    Succeeded,
    Failed,
    Dead,
    LeaseLost,
    Shutdown,
}

/// <summary>
/// Runs one claimed delivery: in its event's flow one hop deeper, in its own DI scope, as its handler's caller, under its
/// handler's timeout — then answers for the attempt, fenced, so a late finisher changes nothing (spec 2026-10-14 §4.6,
/// §4.8).
/// </summary>
internal sealed class DeliveryRunner(
    IDeliveryStore store,
    IServiceScopeFactory scopes,
    IAlvoSessions sessions,
    IPolicyCatalogProvider catalogs,
    DeliveryBackoff backoff,
    TimeProvider time,
    ILogger<DeliveryRunner> logger)
{
    /// <summary>What a claim's lease adds to the longest handler timeout, and how long a timed-out handler is given to stop.</summary>
    internal static readonly TimeSpan LeaseGrace = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long a timed-out handler is given to observe its token; <see cref="LeaseGrace"/> except in tests.</summary>
    internal TimeSpan SettleGrace { get; init; } = LeaseGrace;

    internal async Task<DeliveryRunOutcome> RunAsync(
        ClaimedDelivery delivery, BoundHandler handler, string claimant, CancellationToken stopping)
    {
        var startedAt = time.GetUtcNow();
        var started = time.GetTimestamp();
        if (await AttemptAsync(delivery, handler, stopping).ConfigureAwait(false) is not { } verdict)
        {
            return DeliveryRunOutcome.Shutdown;
        }

        var attempt = new DeliveryAttempt { Delivery = delivery, Claimant = claimant, StartedAt = startedAt, Duration = time.GetElapsedTime(started) };
        var outcome = await RecordAsync(attempt, handler.Settings.Retry, verdict).ConfigureAwait(false);
        AlvoDeliveryMetrics.Recorded(attempt, outcome);

        return outcome;
    }

    private async Task<AttemptVerdict?> AttemptAsync(ClaimedDelivery delivery, BoundHandler handler, CancellationToken stopping)
    {
        if (delivery.Payload is null)
        {
            return AttemptVerdict.Dead(DeliveryErrors.EventMissing);
        }

        if (delivery.Attempts > handler.Settings.Retry.MaxAttempts)
        {
            return AttemptVerdict.Dead(DeliveryErrors.LeaseExpired);
        }

        var @event = AlvoEventJson.Read(delivery.Payload);
        if (!Readable(@event, handler.Settings.RunAs))
        {
            return AttemptVerdict.Dead(DeliveryErrors.EventUnreadable);
        }

        using var flow = AlvoFlow.Enter(FlowOf(@event));
        using var span = AlvoHookTelemetry.StartDelivery(delivery.Subscriber, @event, delivery.Attempts);
        AlvoDeliveryMetrics.Lagged(delivery.Subscriber, time.GetUtcNow() - @event.Time);

        return await InvokeObservedAsync(@event, delivery, handler, span, stopping).ConfigureAwait(false);
    }

    private async Task<AttemptVerdict?> InvokeObservedAsync(
        AlvoEvent @event, ClaimedDelivery delivery, BoundHandler handler, Activity? span, CancellationToken stopping)
    {
        try
        {
            var verdict = await InvokeAsync(@event, delivery, handler, stopping).ConfigureAwait(false);
            AlvoHookTelemetry.Finish(span, verdict.Succeeded ? AlvoHookTelemetry.Succeeded : AlvoHookTelemetry.Failed);

            return verdict;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<AttemptVerdict> InvokeAsync(AlvoEvent @event, ClaimedDelivery delivery, BoundHandler handler, CancellationToken stopping)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var invocation = RunHandlerAsync(@event, delivery, handler, cancel.Token);
        try
        {
            await invocation.WaitAsync(handler.Settings.Timeout, time, stopping).ConfigureAwait(false);
            return AttemptVerdict.Success;
        }
        catch (TimeoutException) when (!invocation.IsCompleted && !stopping.IsCancellationRequested)
        {
            await cancel.CancelAsync().ConfigureAwait(false);
            await SettleAsync(invocation, stopping).ConfigureAwait(false);
            return AttemptVerdict.Retry(DeliveryErrors.Timeout);
        }
        catch (Exception failure) when (!stopping.IsCancellationRequested && IsDepthRefusal(failure))
        {
            return AttemptVerdict.Dead(DeliveryErrors.ChainDepthExceeded);
        }
        catch (Exception failure) when (!stopping.IsCancellationRequested)
        {
            return AttemptVerdict.Retry(failure.GetType().FullName ?? failure.GetType().Name);
        }
    }

    /// <summary>Gives a timed-out handler <see cref="SettleGrace"/> to observe its token; past that it is abandoned.</summary>
    private async Task SettleAsync(Task invocation, CancellationToken stopping)
    {
        try
        {
            await invocation.WaitAsync(SettleGrace, time, stopping).ConfigureAwait(false);
        }
        catch (Exception) when (!stopping.IsCancellationRequested)
        {
        }
    }

    private static bool IsDepthRefusal(Exception failure) => failure switch
    {
        AlvoChainDepthExceededException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsDepthRefusal),
        { InnerException: { } inner } => IsDepthRefusal(inner),
        _ => false,
    };

    /// <summary>
    /// A present but unreadable <c>tenantid</c> — or, for an actor run, <c>authid</c> — must never become a tenantless
    /// system caller or an anonymous one: the delivery is dead instead (fail closed).
    /// </summary>
    private static bool Readable(AlvoEvent @event, AlvoRunAs runAs) =>
        (@event.TenantId is null || EventActor.TenantOf(@event) is not null)
        && (runAs is not AlvoRunAs.EventActor || @event.AuthId is null || UserId.TryParse(@event.AuthId, CultureInfo.InvariantCulture, out _));

    private async Task RunHandlerAsync(AlvoEvent @event, ClaimedDelivery delivery, BoundHandler handler, CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = new AlvoEventContext(@event, delivery.Subscriber, delivery.Attempts, sessions.For(CallerFor(@event, handler.Settings.RunAs)));
            var message = HandlerMessage.For(@event, handler.Registration.Source);
            await handler.Registration.Invoker.InvokeAsync(scope.ServiceProvider, message, context, ct).ConfigureAwait(false);
        }
    }

    private AlvoContext CallerFor(AlvoEvent @event, AlvoRunAs runAs) =>
        runAs is AlvoRunAs.EventActor
            ? EventActor.Of(@event, catalogs.Current?.Roles ?? RoleCatalog.BuiltInOnly)
            : AlvoContext.System(EventActor.TenantOf(@event));

    private static AlvoProvenance FlowOf(AlvoEvent @event) => new()
    {
        CorrelationId = @event.CorrelationId,
        CausationId = @event.Id.ToString(),
        Depth = @event.ChainDepth + 1,
    };

    private async Task<DeliveryRunOutcome> RecordAsync(DeliveryAttempt attempt, AlvoRetryPolicy policy, AttemptVerdict verdict)
    {
        if (verdict.Succeeded)
        {
            return await store.CompleteAsync(attempt, CancellationToken.None).ConfigureAwait(false)
                ? DeliveryRunOutcome.Succeeded
                : LeaseLost(attempt);
        }

        var retryAfter = verdict.Retryable ? backoff.After(attempt.Delivery.Attempts, policy) : null;
        var failure = new DeliveryFailure { ErrorType = verdict.ErrorType!, RetryAfter = retryAfter, MaxAttempts = policy.MaxAttempts };

        return await store.FailAsync(attempt, failure, CancellationToken.None).ConfigureAwait(false)
            ? Failed(attempt.Delivery, failure)
            : LeaseLost(attempt);
    }

    private DeliveryRunOutcome Failed(ClaimedDelivery delivery, DeliveryFailure failure)
    {
        if (failure.RetryAfter is null || delivery.Attempts >= failure.MaxAttempts)
        {
            DeliveryLog.DeliveryDead(logger, delivery.Id, delivery.Subscriber, delivery.Attempts, failure.ErrorType);
            AlvoDeliveryMetrics.DeadRecorded(delivery.Subscriber, failure.ErrorType);
            return DeliveryRunOutcome.Dead;
        }

        DeliveryLog.DeliveryFailed(logger, delivery.Id, delivery.Subscriber, delivery.Attempts, failure.ErrorType);
        return DeliveryRunOutcome.Failed;
    }

    private DeliveryRunOutcome LeaseLost(DeliveryAttempt attempt)
    {
        DeliveryLog.DeliveryLeaseLost(logger, attempt.Delivery.Id, attempt.Delivery.Subscriber, attempt.Delivery.Attempts);
        return DeliveryRunOutcome.LeaseLost;
    }

    private readonly record struct AttemptVerdict(string? ErrorType, bool Retryable)
    {
        internal static AttemptVerdict Success => new(null, false);

        internal bool Succeeded => ErrorType is null;

        internal static AttemptVerdict Retry(string errorType) => new(errorType, true);

        internal static AttemptVerdict Dead(string errorType) => new(errorType, false);
    }
}
```

`src/MMLib.Alvo/Events/Setup.cs` — add after `services.TryAddSingleton<DeliveryFanOut>();`:

```csharp
        services.TryAddSingleton(new DeliveryBackoff());
        services.TryAddSingleton<DeliveryRunner>();
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Events/Internal/EventActor.cs src/MMLib.Alvo/Events/Internal/HandlerMessage.cs src/MMLib.Alvo/Events/Internal/DeliveryBackoff.cs src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs src/MMLib.Alvo/Events/Internal/AlvoHookTelemetry.cs src/MMLib.Alvo/Events/Internal/AlvoDeliveryMetrics.cs src/MMLib.Alvo/Events/Internal/DeliveryLog.cs src/MMLib.Alvo/Events/Setup.cs test/MMLib.Alvo.Tests/Events/EventActorTests.cs test/MMLib.Alvo.Tests/Events/DeliveryBackoffTests.cs test/MMLib.Alvo.Tests/Events/DeliveryRunnerTests.cs test/MMLib.Alvo.Tests/InternalMemberReferencesTests.cs`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EventActorTests' --filter-class '*DeliveryBackoffTests' --filter-class '*DeliveryRunnerTests' --filter-class '*InternalMemberReferencesTests' --filter-class '*OutboxDispatcherTests' --filter-class '*SqlTextConfinedToRendererArchitectureTests' --filter-class '*TestingInternalGrantArchitectureTests'`
Expected: PASS — including `OutboxDispatcherTests.Spans` (after-hook spans with no envelope trace carry no link) and every granted assembly's `InternalGrantArchitectureTests` (only the core calls `AlvoFlow.Enter`).

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo/Events/Internal/EventActor.cs src/MMLib.Alvo/Events/Internal/HandlerMessage.cs src/MMLib.Alvo/Events/Internal/DeliveryBackoff.cs src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs src/MMLib.Alvo/Events/Internal/AlvoHookTelemetry.cs src/MMLib.Alvo/Events/Internal/AlvoDeliveryMetrics.cs src/MMLib.Alvo/Events/Internal/DeliveryLog.cs src/MMLib.Alvo/Events/Setup.cs test/MMLib.Alvo.Tests/Events/EventActorTests.cs test/MMLib.Alvo.Tests/Events/DeliveryBackoffTests.cs test/MMLib.Alvo.Tests/Events/DeliveryRunnerTests.cs test/MMLib.Alvo.Tests/InternalMemberReferencesTests.cs
git commit -m "feat(events): run one delivery in its event's flow, fenced, retried and traced

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 10: `DeliveryDispatcher`, the pump's delivery step, and the re-entrant drain refusal **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs`
- Modify: `src/MMLib.Alvo/Events/Internal/EventPump.cs`, `DrainRound.cs` (thread-safe), `DeliveryLog.cs` (three lines), `src/MMLib.Alvo/Events/Setup.cs`, `src/MMLib.Alvo/Events/IAlvoEventPump.cs` and `AlvoDrainResult.cs` (docs only)
- Modify: `test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.Manual.cs` (its `Pump` helper builds `EventPump` — I2) and `test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.cs` (a `NoDeliveries()` helper)
- Create: `test/MMLib.Alvo.Tests/Events/DeliveryDispatcherTests.cs`, `test/MMLib.Alvo.TestHost.Tests/AlvoTestHostHandlerTests.cs`
- Modify: `test/MMLib.Alvo.Tests/Events/EventPumpRegistrationTests.cs`

**Interfaces:**
- Consumes: `DeliveryRunner.RunAsync`, `DeliveryRunOutcome` (Task 9); `ExtensionBinder.For`, `ExtensionBindings.Handlers/Handler/LongestTimeout` (Task 7); `AlvoDeliveryOptions.MaxConcurrency` (Task 5); `IDeliveryStore.ClaimAsync`, `DeliveryClaim` (Task 3).
- Produces (internal): `sealed class DeliveryDispatcher(IDeliveryStore, AlvoBootState, IPolicyCatalogProvider, ExtensionBinder, DeliveryRunner, IOptions<AlvoEventOptions>, IOptions<AlvoDeliveryOptions>, TimeProvider, ILogger<DeliveryDispatcher>) : BackgroundService { Task<AlvoDrainResult> ManualRoundAsync(CancellationToken) }` (the lease grace is `DeliveryRunner.LeaseGrace`, Task 9); `EventPump(OutboxDispatcher, DeliveryDispatcher, IHostApplicationLifetime)` with `Deliveries`; `DeliveryLog.DeliveryDispatcherManual / DeliveryDispatcherStopped / DeliveryRunFailed`.

**Behaviour (spec §4.8 *Claim*, *Worker model*):** the claim names exactly the handlers bound to the current catalog (`subscriber IN`), ordered or not by their `Ordering`, with a lease of the longest registered timeout + 30 s (one statement claims a batch, so the lease is the batch's — never shorter than any row's `timeout + 30 s`). The background loop keeps at most `MaxConcurrency` deliveries running, claims only free slots, and idles on the poll interval or the first finishing delivery; it keeps the outbox loop's gates (enabled, background mode, boot `Ready`, primed catalog). A manual round claims up to `Alvo:Events:BatchSize` lane heads (M5: a fixed 16 would make the test host report a false loop on a backlog of more than 1 600 deliveries — 100 rounds × 16), runs them at most `MaxConcurrency` at a time and returns when all have ended; rounds are serialised, so nothing else in the process claims while a queued delivery waits for a slot. The claimant is `{machine}:{pid}:{instance}`, so two dispatchers in one process — two test hosts over one database — are two claimants. **A round called from inside a round is refused** (the controller's carry): `EventPump` marks its flow and a nested `DrainAsync` throws `InvalidOperationException` at once instead of waiting forever on its own gate; code that suppresses flow escapes the mark, and the docs say so.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Events/DeliveryDispatcherTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>The delivery dispatcher: which deliveries a claim takes, how many run at once, and how a round reports.</summary>
public sealed class DeliveryDispatcherTests : IDisposable
{
    private const string Handler = "on-created";

    private static readonly PolicyCatalog _catalog = HandlerCatalog.Catalog();
    private static readonly TimeSpan _waitBudget = TimeSpan.FromSeconds(10);

    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));

    public DeliveryDispatcherTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manual_round_runs_every_due_lane_head_and_reports_it()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2");

        var result = await dispatcher.ManualRoundAsync(Ct);

        result.Claimed.ShouldBe(2);
        result.Failed.ShouldBe(0);
        result.Subscribers.ShouldBe([Handler]);
        store.Rows.ShouldAllBe(row => row.Status == "succeeded");
    }

    [Fact]
    public async Task A_round_leaves_the_work_of_a_handler_this_host_does_not_register()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        await FanOutAsync(store, "someone-else", "tickets:1");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_attempt_is_counted_once()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException()));
        await FanOutAsync(store, Handler, "tickets:1");

        (await dispatcher.ManualRoundAsync(Ct)).Failed.ShouldBe(1);
    }

    [Fact]
    public async Task An_unordered_handler_is_not_held_back_by_a_dead_earlier_delivery()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, options => options.Ordering = AlvoOrdering.None);
        var dead = await PublishAsync(store, "tickets:1");
        await store.FanOutAsync([Request(dead, Handler, "tickets:1") with { DeadReason = "when-failed" }], Ct);
        await FanOutAsync(store, Handler, "tickets:1");

        (await dispatcher.ManualRoundAsync(Ct)).Claimed.ShouldBe(1);
    }

    [Fact]
    public async Task A_claim_holds_for_the_longest_handler_timeout_and_thirty_seconds()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct), options => options.Timeout = TimeSpan.FromSeconds(20));
        await FanOutAsync(store, Handler, "tickets:1");
        using var stop = new CancellationTokenSource();

        var round = dispatcher.ManualRoundAsync(stop.Token);
        await UntilAsync(() => store.Rows.Single().Status == "running", "the delivery to be claimed");

        store.Rows.Single().LeaseUntil.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromSeconds(50));
        await stop.CancelAsync();
        await round;
    }

    [Fact]
    public async Task The_background_loop_runs_no_more_deliveries_at_once_than_its_concurrency()
    {
        var store = new FakeDeliveryStore(TimeProvider.System);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        var dispatcher = Dispatcher(
            store,
            async (AlvoCreated<AlvoRecord> _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);
                await gate.Task;
                Interlocked.Decrement(ref running);
            },
            concurrency: 2,
            mode: AlvoEventDispatchMode.Background,
            time: TimeProvider.System);
        await FanOutAsync(store, Handler, "tickets:1", "tickets:2", "tickets:3", "tickets:4", "tickets:5");

        await dispatcher.StartAsync(Ct);
        await UntilAsync(() => Volatile.Read(ref running) == 2, "two deliveries to be running");
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        gate.SetResult();
        await UntilAsync(() => store.Rows.All(row => row.Status == "succeeded"), "every delivery to finish");
        await dispatcher.StopAsync(Ct);

        Volatile.Read(ref most).ShouldBe(2);
    }

    [Fact]
    public async Task In_the_manual_mode_the_loop_stands_down_and_says_so()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);
        await FanOutAsync(store, Handler, "tickets:1");

        await dispatcher.StartAsync(Ct);
        await dispatcher.StopAsync(Ct);

        store.Rows.Single().Status.ShouldBe("pending");
        _logs.Entries.ShouldContain(entry => entry.EventName == "DeliveryDispatcherManual");
    }

    private DeliveryDispatcher Dispatcher(
        FakeDeliveryStore store,
        Delegate handle,
        Action<AlvoHandlerOptions>? configure = null,
        int concurrency = 16,
        AlvoEventDispatchMode mode = AlvoEventDispatchMode.Manual,
        TimeProvider? time = null)
    {
        var clock = time ?? _clock;
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t.OnCreated(Handler, handle, configure: configure)));
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), _loggers.CreateLogger<ExtensionBinder>());
        var catalogs = new PolicyCatalogProvider();
        catalogs.SetCurrent("handlers", _catalog);
        var boot = new AlvoBootState();
        boot.Ready("handlers", appliedRevision: 1);
        var runner = new DeliveryRunner(
            store,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new AlvoSessions(Substitute.For<IAlvoData>()),
            catalogs,
            new DeliveryBackoff(() => 0.5),
            clock,
            _loggers.CreateLogger<DeliveryRunner>());

        return new DeliveryDispatcher(
            store,
            boot,
            catalogs,
            binder,
            runner,
            Options.Create(new AlvoEventOptions { DispatchMode = mode, PollInterval = TimeSpan.FromMilliseconds(20) }),
            Options.Create(new AlvoDeliveryOptions { MaxConcurrency = concurrency }),
            clock,
            _loggers.CreateLogger<DeliveryDispatcher>());
    }

    private async Task FanOutAsync(FakeDeliveryStore store, string subscriber, params string[] keys)
    {
        foreach (var key in keys)
        {
            var @event = await PublishAsync(store, key);
            await store.FanOutAsync([Request(@event, subscriber, key)], Ct);
        }
    }

    private static DeliveryRequest Request(Guid @event, string subscriber, string key) =>
        new() { EventId = @event, Subscriber = subscriber, PartitionKey = key, Kind = DeliveryKind.Handler };

    private Task<Guid> PublishAsync(FakeDeliveryStore store, string key)
    {
        var @event = new AlvoEvent
        {
            Id = AlvoEventId.Create(_clock.GetUtcNow()),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.tickets.created",
            Time = _clock.GetUtcNow(),
            Subject = key.Replace(':', '/'),
            PartitionKey = key,
            AuthType = AlvoEventAuthType.ApiKey,
            CorrelationId = "c",
            Data = new AlvoEventData { Record = AlvoRecord.Empty },
        };
        store.Publish(@event);

        return Task.FromResult(@event.Id);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
            seen = Volatile.Read(ref target);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + _waitBudget;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {_waitBudget} for {what}, and it never happened.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), Ct);
        }
    }
}
```

`test/MMLib.Alvo.TestHost.Tests/AlvoTestHostHandlerTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Handler facts that only the package's own leg needs — engine-agnostic by construction.</summary>
public sealed class AlvoTestHostHandlerTests
{
    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_handler_that_drains_is_refused_instead_of_hanging()
    {
        Exception? refused = null;
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(HelpDesk).Entity("tickets", tickets => tickets.OnCreated(
                "drains-from-inside",
                async (AlvoCreated<AlvoRecord> _, IAlvoEventPump pump, CancellationToken ct) =>
                    refused = await Record.ExceptionAsync(() => pump.DrainAsync(ct)))),
            cancellationToken: Ct);
        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        refused.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("inside a round");
        alvo.Hooks.AssertRan("drains-from-inside", times: 1);
    }
}
```

`test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.Manual.cs` — its `Pump` helper builds `new(dispatcher, lifetime)`; make it an instance method (drop `static`; make any static caller an instance method too) returning `new(dispatcher, NoDeliveries(), lifetime)`, and add to `OutboxDispatcherTests.cs` (with `using Microsoft.Extensions.DependencyInjection;`, `using MMLib.Alvo.Data;`, `using MMLib.Alvo.Extensions;`, `using NSubstitute;`):

```csharp
    /// <summary>A delivery dispatcher with no handler registered, for the pump the manual-round facts drive.</summary>
    private DeliveryDispatcher NoDeliveries()
    {
        var store = new FakeDeliveryStore(TimeProvider.System);
        var runner = new DeliveryRunner(
            store,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new AlvoSessions(Substitute.For<IAlvoData>()),
            PrimedCatalogs,
            new DeliveryBackoff(() => 0.5),
            TimeProvider.System,
            _loggers.CreateLogger<DeliveryRunner>());

        return new DeliveryDispatcher(
            store,
            new AlvoBootState(),
            PrimedCatalogs,
            new ExtensionBinder(ExtensionRegistry.Empty, CelFixtures.Compiler, HandlerCatalog.EveryService(), _loggers.CreateLogger<ExtensionBinder>()),
            runner,
            Options.Create(new AlvoEventOptions { DispatchMode = AlvoEventDispatchMode.Manual }),
            Options.Create(new AlvoDeliveryOptions()),
            TimeProvider.System,
            _loggers.CreateLogger<DeliveryDispatcher>());
    }
```

The manual facts' assertions (no Warning log, the outbox entry left claimed on a cut-short round) hold unchanged: the delivery step claims nothing with no handler registered.

`test/MMLib.Alvo.Tests/Events/EventPumpRegistrationTests.cs` — the pump now resolves the delivery runner, whose sessions need a data port: in `Registered` add `collection.AddSingleton(Substitute.For<IAlvoData>());` (with `using MMLib.Alvo.Data;`) beside the two store substitutes, then add:

```csharp
    [Fact]
    public void The_pump_drives_the_one_hosted_delivery_dispatcher()
    {
        var collection = Registered(times: 2);
        using var services = collection.BuildServiceProvider();

        var hosted = collection.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType == typeof(DeliveryDispatcher)).ToList();

        hosted.Count.ShouldBe(1);
        services.GetRequiredService<IAlvoEventPump>().ShouldBeOfType<EventPump>().Deliveries
            .ShouldBeSameAs(hosted[0].ImplementationFactory!(services));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DeliveryDispatcherTests' --filter-class '*EventPumpRegistrationTests'`
Expected: FAIL (build error).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Claims and runs event-handler deliveries: the lane heads of the handlers this process registers, at most
/// <see cref="AlvoDeliveryOptions.MaxConcurrency"/> at once (spec 2026-10-14 §4.8).
/// </summary>
/// <remarks>
/// <para>
/// It keeps the outbox dispatcher's gates — events enabled, the background mode, a <c>Ready</c> boot with a primed catalog —
/// for the same reason: a pump that runs before the schema is primed has nothing to bind a handler to. Nothing escapes
/// <see cref="ExecuteAsync"/>; a delivery that fails is the delivery's outcome, not the loop's.
/// </para>
/// <para>
/// <b>The claimant is per instance</b> (<c>{machine}:{pid}:{instance}</c>): two dispatchers in one process — two test hosts
/// over one database — are two claimants, and completion is fenced on the attempt count besides, so a reclaimed lease's
/// late finisher changes nothing whoever it is.
/// </para>
/// </remarks>
internal sealed class DeliveryDispatcher(
    IDeliveryStore store,
    AlvoBootState boot,
    IPolicyCatalogProvider catalogs,
    ExtensionBinder binder,
    DeliveryRunner runner,
    IOptions<AlvoEventOptions> events,
    IOptions<AlvoDeliveryOptions> deliveries,
    TimeProvider time,
    ILogger<DeliveryDispatcher> logger) : BackgroundService
{
    private static readonly AlvoDrainResult _quiet = new() { Claimed = 0, Failed = 0, Subscribers = [] };

    private readonly string _claimant = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private bool _storeReady;

    /// <summary>One round on the caller's schedule; the pump calls it after the outbox step passed the shared gates.</summary>
    internal async Task<AlvoDrainResult> ManualRoundAsync(CancellationToken cancellationToken)
    {
        if (catalogs.Current is not { } catalog)
        {
            return _quiet;
        }

        await EnsureStoreAsync(cancellationToken).ConfigureAwait(false);
        var bindings = binder.For(catalog);
        var claimed = await ClaimAsync(bindings, events.Value.BatchSize, cancellationToken).ConfigureAwait(false);
        var round = new DrainRound();
        var slots = new ParallelOptions { MaxDegreeOfParallelism = deliveries.Value.MaxConcurrency };
        await Parallel.ForEachAsync(claimed, slots, async (delivery, _) =>
            await RunContainedAsync(delivery, bindings, round, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        return round.Result(claimed.Count);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await PumpUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            DeliveryLog.DeliveryDispatcherStopped(logger, failure);
        }
    }

    private async Task PumpUntilStoppedAsync(CancellationToken stop)
    {
        if (!await GatesOpenAsync(stop).ConfigureAwait(false))
        {
            return;
        }

        await EnsureStoreAsync(stop).ConfigureAwait(false);
        var running = new List<Task>();
        while (!stop.IsCancellationRequested)
        {
            running.RemoveAll(task => task.IsCompleted);
            var bindings = binder.For(catalogs.Current!);
            var claimed = await ClaimAsync(bindings, deliveries.Value.MaxConcurrency - running.Count, stop).ConfigureAwait(false);
            running.AddRange(claimed.Select(delivery => RunContainedAsync(delivery, bindings, round: null, stop)));
            if (claimed.Count == 0)
            {
                await Task.WhenAny([Task.Delay(events.Value.PollInterval, time, stop), .. running]).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task<bool> GatesOpenAsync(CancellationToken stop)
    {
        if (!events.Value.Enabled)
        {
            return false;
        }

        if (events.Value.DispatchMode is AlvoEventDispatchMode.Manual)
        {
            DeliveryLog.DeliveryDispatcherManual(logger);
            return false;
        }

        return await boot.SettledAsync(stop).ConfigureAwait(false) is AlvoBootPhase.Ready && catalogs.Current is not null;
    }

    private async Task EnsureStoreAsync(CancellationToken ct)
    {
        if (!_storeReady)
        {
            await store.EnsureAsync(ct).ConfigureAwait(false);
            _storeReady = true;
        }
    }

    private async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(ExtensionBindings bindings, int batchSize, CancellationToken ct) =>
        batchSize <= 0 || bindings.Handlers.Count == 0
            ? []
            : await store.ClaimAsync(ClaimOf(bindings, batchSize), ct).ConfigureAwait(false);

    private DeliveryClaim ClaimOf(ExtensionBindings bindings, int batchSize) => new()
    {
        Claimant = _claimant,
        Ordered = [.. bindings.Handlers.Where(handler => handler.Settings.Ordering is AlvoOrdering.PerRecord).Select(handler => handler.Id)],
        Unordered = [.. bindings.Handlers.Where(handler => handler.Settings.Ordering is AlvoOrdering.None).Select(handler => handler.Id)],
        BatchSize = batchSize,
        Lease = bindings.LongestTimeout + DeliveryRunner.LeaseGrace,
        OutboxMaxAttempts = events.Value.MaxAttempts,
    };

    private async Task RunContainedAsync(ClaimedDelivery delivery, ExtensionBindings bindings, DrainRound? round, CancellationToken stop)
    {
        if (bindings.Handler(delivery.Subscriber) is not { } handler)
        {
            return;
        }

        round?.Ran(delivery.Subscriber);
        try
        {
            var outcome = await runner.RunAsync(delivery, handler, _claimant, stop).ConfigureAwait(false);
            if (outcome is DeliveryRunOutcome.Failed or DeliveryRunOutcome.Dead)
            {
                round?.Failed();
            }
        }
        catch (Exception failure) when (!stop.IsCancellationRequested)
        {
            round?.Failed();
            DeliveryLog.DeliveryRunFailed(logger, delivery.Id, delivery.Subscriber, failure.GetType().FullName ?? failure.GetType().Name);
        }
    }
}
```

`src/MMLib.Alvo/Events/Internal/DeliveryLog.cs` — add:

```csharp
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo's delivery dispatcher is in the manual dispatch mode (Alvo:Events:DispatchMode = Manual): its "
            + "background loop does not run, and deliveries run only in IAlvoEventPump.DrainAsync rounds.")]
    internal static partial void DeliveryDispatcherManual(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo's delivery dispatcher stopped and will not restart in this process; no event handler runs until it is restarted.")]
    internal static partial void DeliveryDispatcherStopped(ILogger logger, Exception failure);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo could not answer for delivery {DeliveryId} of {Subscriber} ({ErrorType}); its lease returns it to the queue.")]
    internal static partial void DeliveryRunFailed(ILogger logger, Guid deliveryId, string subscriber, string errorType);
```

`src/MMLib.Alvo/Events/Internal/DrainRound.cs` — make it safe for concurrent deliveries: add `private readonly Lock _gate = new();` and wrap the bodies of `Ran`, `Failed` and `Result` in `lock (_gate) { … }` (the summary gains: "Deliveries of one round run concurrently, so the tally locks."). `_failed++` stays inside the lock.

`src/MMLib.Alvo/Events/Internal/EventPump.cs` — replace the class body:

```csharp
internal sealed class EventPump(OutboxDispatcher outbox, DeliveryDispatcher deliveries, IHostApplicationLifetime lifetime)
    : IAlvoEventPump, IDisposable
{
    private readonly SemaphoreSlim _rounds = new(1, 1);
    private readonly AsyncLocal<bool> _inRound = new();

    /// <summary>Gets the outbox dispatcher this pump drives — the same instance the host runs.</summary>
    internal OutboxDispatcher Outbox => outbox;

    /// <summary>Gets the delivery dispatcher this pump drives — the same instance the host runs.</summary>
    internal DeliveryDispatcher Deliveries => deliveries;

    /// <inheritdoc/>
    public async Task<AlvoDrainResult> DrainAsync(CancellationToken cancellationToken = default)
    {
        if (_inRound.Value)
        {
            throw ReEntered();
        }

        using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        await _rounds.WaitAsync(round.Token).ConfigureAwait(false);
        try
        {
            _inRound.Value = true;
            var fannedOut = await outbox.ManualRoundAsync(round.Token).ConfigureAwait(false);
            var delivered = await deliveries.ManualRoundAsync(round.Token).ConfigureAwait(false);

            return Sum(fannedOut, delivered);
        }
        finally
        {
            _inRound.Value = false;
            EndRound();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _rounds.Dispose();

    private static AlvoDrainResult Sum(AlvoDrainResult first, AlvoDrainResult second) => new()
    {
        Claimed = first.Claimed + second.Claimed,
        Failed = first.Failed + second.Failed,
        Subscribers = [.. first.Subscribers.Concat(second.Subscribers).Distinct(StringComparer.Ordinal)],
    };

    private static InvalidOperationException ReEntered() => new(
        "A dispatch round was refused: it was called from inside a round — a subscriber (an event handler, or a webhook "
        + "receiver running in this process) drained the pump. A round waits for the round it is in, so this would never "
        + "return. Return from the subscriber and drain from the code that wrote the event.");

    private void EndRound()
    {
        try
        {
            _rounds.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
```

Keep `EndRound`'s existing XML remarks; replace the class remarks' first paragraph with "A round is the outbox dispatcher's tick (claim, fan out, run the declarative actions) followed by the delivery dispatcher's (claim the lane heads now due, run them); HX-13 adds the timer and schedule steps. The public port and its result do not change; the steps' results are summed." and the second with "The gate serialises rounds. A round called from inside a round — a subscriber that drains — is **refused** rather than left to wait for itself: the round marks its execution flow, and a nested call finds the mark. Code that suppresses execution-context flow escapes the mark and would wait; nothing can see it."

`src/MMLib.Alvo/Events/IAlvoEventPump.cs` — in the remarks replace "a round called from inside a round would wait for itself, so a subscriber must never drain." with "a round called from inside a round — by a subscriber that drains — is refused with an <see cref=\"InvalidOperationException\"/>, because it would wait for itself." and add to the `<exception>` text: "…, or the call was made from inside a round."

`src/MMLib.Alvo/Events/AlvoDrainResult.cs` — in `Subscribers`' summary append: "An event handler is named by its registered id."

`src/MMLib.Alvo/Events/Setup.cs` — after the outbox's hosted registration add:

```csharp
        services.TryAddSingleton<DeliveryDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DeliveryDispatcher>(
            provider => provider.GetRequiredService<DeliveryDispatcher>()));
```

and extend the class remarks' dispatcher paragraph: "The delivery dispatcher is registered the same way: one singleton, hosted and driven by the pump as the same instance."

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs src/MMLib.Alvo/Events/Internal/EventPump.cs src/MMLib.Alvo/Events/Internal/DrainRound.cs src/MMLib.Alvo/Events/Internal/DeliveryLog.cs src/MMLib.Alvo/Events/Setup.cs src/MMLib.Alvo/Events/IAlvoEventPump.cs src/MMLib.Alvo/Events/AlvoDrainResult.cs test/MMLib.Alvo.Tests/Events/DeliveryDispatcherTests.cs test/MMLib.Alvo.Tests/Events/EventPumpRegistrationTests.cs test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.Manual.cs test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.cs test/MMLib.Alvo.TestHost.Tests/AlvoTestHostHandlerTests.cs`, then:
- `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DeliveryDispatcherTests' --filter-class '*EventPumpRegistrationTests' --filter-class '*OutboxDispatcherTests' --filter-class '*SqlTextConfinedToRendererArchitectureTests'` — PASS
- `dotnet test --project test/MMLib.Alvo.TestHost.Tests` — PASS: the new fact, and every HX-02 fact unchanged — `A_receiver_that_writes_back_is_reported_as_a_loop` still throws `AlvoDrainNotQuiescentException` (its receiver keeps `SuppressFlow`; a round now also runs the empty delivery step).

- [ ] **Step 5: Rings and commit**

Run `scripts/test-ring0` and `scripts/test-ring1` (slice end).

```bash
git add test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.Manual.cs test/MMLib.Alvo.Tests/Events/OutboxDispatcherTests.cs src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs src/MMLib.Alvo/Events/Internal/EventPump.cs src/MMLib.Alvo/Events/Internal/DrainRound.cs src/MMLib.Alvo/Events/Internal/DeliveryLog.cs src/MMLib.Alvo/Events/Setup.cs src/MMLib.Alvo/Events/IAlvoEventPump.cs src/MMLib.Alvo/Events/AlvoDrainResult.cs test/MMLib.Alvo.Tests/Events/DeliveryDispatcherTests.cs test/MMLib.Alvo.Tests/Events/EventPumpRegistrationTests.cs test/MMLib.Alvo.TestHost.Tests/AlvoTestHostHandlerTests.cs
git commit -m "feat(events): the delivery dispatcher, its pump step, and a refused re-entrant drain

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 11: Pending work at boot — orphans refused, retirements applied (E3)

**Files:**
- Create: `src/MMLib.Alvo/Extensions/Internal/ExtensionStateGuard.cs`
- Modify: `src/MMLib.Alvo/Extensions/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/ExtensionStateGuardTests.cs`

**Interfaces:**
- Consumes: `ExtensionRegistry.All/Retired`, `RetiredExtension`, `AlvoRetirement` (Tasks 5–6); `IDeliveryStore.PendingSubscribersAsync/DeadLetterPendingAsync/CancelPendingAsync` (Task 3); `DeliveryErrors.Retired`, `ExtensionLog.ExtensionRetired` (Tasks 7–8).
- Produces: `internal sealed class ExtensionStateGuard(AlvoBootState, ExtensionRegistry, IDeliveryStore, ILogger<ExtensionStateGuard>) : IHostedLifecycleService`, registered with `TryAddEnumerable` after the boot service.

**Behaviour:** once the boot settles `Ready` (it is registered after `AlvoBootService`, and awaits the settlement in case the host starts services concurrently), the guard applies each `AddRetiredExtension` — `DeadLetter` ends the id's pending, running and retrying deliveries as `dead` `retired`, `Discard` cancels them — and then refuses the start, naming every id, if the store holds unfinished work for a subscriber this host neither registers nor retires. "Unfinished" is pending, running or retrying; a `dead` delivery is history and does not block a start.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Extensions/ExtensionStateGuardTests.cs`:

```csharp
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Tests.Events;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Durable work keyed by an id outlives the registration; the boot decides what becomes of it (spec 2026-10-14 E3).</summary>
public sealed class ExtensionStateGuardTests : IDisposable
{
    private readonly CapturingLogger _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly FakeDeliveryStore _store = new(new ManualClock(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)));

    public ExtensionStateGuardTests() => _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    public void Dispose()
    {
        _loggers.Dispose();
        _logs.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unfinished_work_of_an_id_nobody_registers_refuses_the_start_naming_it_and_the_fix()
    {
        await PendingAsync("old-sync");

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => Guard(alvo => { }).StartingAsync(Ct));

        refusal.Message.ShouldContain("'old-sync'");
        refusal.FixSuggestion.ShouldContain("AddRetiredExtension(\"old-sync\", AlvoRetirement.DeadLetter)");
        refusal.FixSuggestion.ShouldContain("AlvoRetirement.Discard");
    }

    [Fact]
    public async Task Work_of_a_registered_handler_starts_normally()
    {
        await PendingAsync("on-created");

        await Guard(alvo => alvo.Entity("tickets", t => t.OnCreated("on-created", OnCreated))).StartingAsync(Ct);
    }

    [Fact]
    public async Task A_dead_letter_retirement_ends_the_work_as_dead_and_says_how_much()
    {
        await PendingAsync("old-sync");

        await Guard(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.DeadLetter)).StartingAsync(Ct);

        var row = _store.Rows.ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("retired");
        _logs.Entries.ShouldContain(entry => entry.EventName == "ExtensionRetired" && entry.Message.Contains("old-sync"));
    }

    [Fact]
    public async Task A_discard_retirement_cancels_the_work()
    {
        await PendingAsync("old-sync");

        await Guard(alvo => alvo.AddRetiredExtension("old-sync", AlvoRetirement.Discard)).StartingAsync(Ct);

        _store.Rows.ShouldHaveSingleItem().Status.ShouldBe("cancelled");
    }

    [Fact]
    public async Task A_dead_delivery_is_history_and_does_not_block_a_start()
    {
        await _store.FanOutAsync(
            [new DeliveryRequest { EventId = Guid.NewGuid(), Subscriber = "old-sync", PartitionKey = "tickets:1", Kind = "handler", DeadReason = "when-failed" }],
            Ct);

        await Guard(alvo => { }).StartingAsync(Ct);
    }

    [Fact]
    public async Task A_boot_that_did_not_reach_ready_checks_nothing()
    {
        await PendingAsync("old-sync");
        var boot = new AlvoBootState();
        boot.Failed("refused for another reason");

        await new ExtensionStateGuard(boot, Registry(alvo => { }), _store, _loggers.CreateLogger<ExtensionStateGuard>()).StartingAsync(Ct);
    }

    private ExtensionStateGuard Guard(Action<IAlvoBuilder> configure)
    {
        var boot = new AlvoBootState();
        boot.Ready("handlers", appliedRevision: 1);

        return new ExtensionStateGuard(boot, Registry(configure), _store, _loggers.CreateLogger<ExtensionStateGuard>());
    }

    private Task PendingAsync(string subscriber) =>
        _store.FanOutAsync([new DeliveryRequest { EventId = Guid.NewGuid(), Subscriber = subscriber, PartitionKey = "tickets:1", Kind = "handler" }], Ct);
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionStateGuardTests'`
Expected: FAIL (build error).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Extensions/Internal/ExtensionStateGuard.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// At start-up, decides what becomes of durable work keyed by an id this host no longer registers (spec 2026-10-14 E3):
/// a declared retirement dead-letters or discards it; anything else refuses the start, because that work would never run
/// and, with per-record order, the lanes behind it would wait forever.
/// </summary>
/// <remarks>Registered after the boot service and awaiting its settlement, so it runs against the primed, migrated database.</remarks>
internal sealed class ExtensionStateGuard(
    AlvoBootState boot, ExtensionRegistry registry, IDeliveryStore store, ILogger<ExtensionStateGuard> logger)
    : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (await boot.SettledAsync(cancellationToken).ConfigureAwait(false) is not AlvoBootPhase.Ready)
        {
            return;
        }

        await store.EnsureAsync(cancellationToken).ConfigureAwait(false);
        await RetireAsync(cancellationToken).ConfigureAwait(false);
        await RefuseOrphansAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RetireAsync(CancellationToken ct)
    {
        foreach (var retired in registry.Retired)
        {
            var ended = retired.Retirement is AlvoRetirement.Discard
                ? await store.CancelPendingAsync(retired.Id, ct).ConfigureAwait(false)
                : await store.DeadLetterPendingAsync(retired.Id, DeliveryErrors.Retired, ct).ConfigureAwait(false);
            ExtensionLog.ExtensionRetired(logger, retired.Id, retired.Retirement.ToString(), ended);
        }
    }

    private async Task RefuseOrphansAsync(CancellationToken ct)
    {
        var known = registry.All.Select(registration => registration.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = (await store.PendingSubscribersAsync(ct).ConfigureAwait(false)).Where(id => !known.Contains(id)).ToList();
        if (orphans.Count > 0)
        {
            throw Orphaned(orphans);
        }
    }

    private static AlvoStartupRefusedException Orphaned(IReadOnlyList<string> ids) => new(
        $"Alvo cannot start: the delivery store holds unfinished work for '{string.Join("', '", ids)}', which this host "
        + "neither registers nor retires. It would never run, and with per-record order the work queued behind it would "
        + "wait forever.",
        string.Join(Environment.NewLine, ids.Select(id =>
            $"Register '{id}' again, or declare what becomes of its work: AddRetiredExtension(\"{id}\", AlvoRetirement.DeadLetter) "
            + "keeps it inspectable as dead; AddRetiredExtension(\"" + id + "\", AlvoRetirement.Discard) cancels it.")));
}
```

`src/MMLib.Alvo/Extensions/Setup.cs` — add `using Microsoft.Extensions.Hosting;` and before `return services;`:

```csharp
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExtensionStateGuard>());
```

`AddAlvoExtensions` runs after `AddBoot` in `AddAlvo`, so the guard's `StartingAsync` follows the boot's.

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/Internal/ExtensionStateGuard.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/ExtensionStateGuardTests.cs`, then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionStateGuardTests' --filter-class '*AlvoExtensionOptionsTests'` and `dotnet test --project test/MMLib.Alvo.TestHost.Tests` (every host now runs the guard over an empty store).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo/Extensions/Internal/ExtensionStateGuard.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/ExtensionStateGuardTests.cs
git commit -m "feat(extensions): refuse a start over orphaned deliveries, apply retirements

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 12: Event handlers end to end, on both engines **[slice end]**

**Files:**
- Create: `test/_shared/testhost/EventHandlerFacts.cs`, `test/_shared/testhost/DeliveryRows.cs`, `test/_shared/testhost/FailingFirstFanOut.cs`, `test/_shared/testhost/descriptors/handler-desk.alvo.json`, `test/_shared/testhost/descriptors/tenant-desk.alvo.json` (the `_shared/testhost` globs already link them into both legs)
- Create: `test/MMLib.Alvo.TestHost.Tests/SqliteEventHandlerTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlEventHandlerTests.cs`

**Interfaces:**
- Consumes: the whole public surface of Tasks 2, 5, 6; `AlvoTestHost` (HX-02); `IAlvoEvents`, `IAlvoData`, `AlvoStartupRefusedException`.
- Produces (test-only): `abstract class EventHandlerFacts { protected sealed record HandlerDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect); protected abstract Task<HandlerDatabase> DatabaseAsync(); }`; `DeliveryRows.ReadAsync(Func<DbConnection>, CancellationToken) → IReadOnlyList<DeliveryRow>`, `DeliveryRows.AttemptCountAsync(…) → int`; `record DeliveryRow(Guid EventId, string Subscriber, string Status, int Attempts, string? LastErrorType)`.

These are the acceptance criteria that need the real pipeline — a write, its outbox row, the fan-out, the claim, the handler, its own writes — on SQLite (ring0, a temp file per fact, so two hosts can share one) and PostgreSQL (ring2, a database per fact). **(2) is proven in process, deliberately:** a SIGKILL harness exists only for the standalone image (`ChildHostHarness`), which cannot carry a C# handler; what a killed process leaves behind is a delivery `running` under a lease nobody will answer for, and that is exactly what host A leaves here — read off the database before host B starts — while host B, a separate container over the same database on a clock past the lease, is the restart.

- [ ] **Step 1: Write the descriptors and the helper**

`test/_shared/testhost/descriptors/handler-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "handler-desk",
  "auth": { "roles": ["agent"] },
  "webhooks": {
    "endpoints": {
      "audit": { "url": "https://audit.example.test/hooks/alvo", "secretRef": "audit-signing-key" }
    }
  },
  "entities": {
    "tickets": {
      "fields": {
        "title": { "type": "string", "maxLength": 120 },
        "code": { "type": "string", "maxLength": 20 }
      },
      "rules": {
        "list": "'agent' in @user.roles || 'admin' in @user.roles",
        "get": "'agent' in @user.roles || 'admin' in @user.roles",
        "create": "'agent' in @user.roles || 'admin' in @user.roles",
        "update": "'agent' in @user.roles || 'admin' in @user.roles"
      },
      "hooks": {
        "afterCreate": [
          { "condition": "new.title == 'audit me'", "action": { "type": "webhook", "endpoint": "audit" } }
        ]
      }
    }
  }
}
```

`test/_shared/testhost/descriptors/tenant-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "tenant-desk",
  "tenancy": { "enabled": true },
  "auth": { "roles": ["agent"] },
  "entities": {
    "notes": {
      "tenancy": "scoped",
      "fields": {
        "text": { "type": "string", "maxLength": 80 }
      },
      "rules": {
        "list": "'agent' in @user.roles || 'admin' in @user.roles",
        "get": "'agent' in @user.roles || 'admin' in @user.roles",
        "create": "'agent' in @user.roles || 'admin' in @user.roles"
      }
    }
  }
}
```

`test/_shared/testhost/DeliveryRows.cs`:

```csharp
using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>One row of <c>alvo_deliveries</c>, as a fact reads it.</summary>
internal sealed record DeliveryRow(Guid EventId, string Subscriber, string Status, int Attempts, string? LastErrorType);

/// <summary>Reads the delivery tables straight off the database — the port has no read member until HX-06.</summary>
internal static class DeliveryRows
{
    internal static async Task<IReadOnlyList<DeliveryRow>> ReadAsync(Func<DbConnection> connect, CancellationToken ct)
    {
        await using var connection = connect();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, subscriber, status, attempts, last_error_type FROM alvo_deliveries ORDER BY event_id, subscriber";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<DeliveryRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DeliveryRow(
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    internal static async Task<int> AttemptCountAsync(Func<DbConnection> connect, CancellationToken ct)
    {
        await using var connection = connect();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alvo_delivery_attempts";

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}
```

`test/_shared/testhost/FailingFirstFanOut.cs` — decorates whatever `IDeliveryStore` the provider registered, failing the first fan-out (the only public way to put a storage failure at that exact point):

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The provider's delivery store, except that the first fan-out throws, as a database that dropped one insert would.</summary>
internal sealed class FailingFirstFanOut(IDeliveryStore inner) : IDeliveryStore
{
    private int _failed;

    internal static void Install(IServiceCollection services)
    {
        var original = services.Last(descriptor => descriptor.ServiceType == typeof(IDeliveryStore));
        services.AddSingleton<IDeliveryStore>(provider => new FailingFirstFanOut((IDeliveryStore)original.ImplementationFactory!(provider)));
    }

    public Task<int> FanOutAsync(IReadOnlyList<DeliveryRequest> requests, CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _failed, 1) == 0
            ? Task.FromException<int>(new InvalidOperationException("injected: the first fan-out fails"))
            : inner.FanOutAsync(requests, cancellationToken);

    public Task EnsureAsync(CancellationToken cancellationToken = default) => inner.EnsureAsync(cancellationToken);

    public Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(DeliveryClaim claim, CancellationToken cancellationToken = default) =>
        inner.ClaimAsync(claim, cancellationToken);

    public Task<bool> CompleteAsync(DeliveryAttempt attempt, CancellationToken cancellationToken = default) =>
        inner.CompleteAsync(attempt, cancellationToken);

    public Task<bool> FailAsync(DeliveryAttempt attempt, DeliveryFailure failure, CancellationToken cancellationToken = default) =>
        inner.FailAsync(attempt, failure, cancellationToken);

    public Task<IReadOnlyList<string>> PendingSubscribersAsync(CancellationToken cancellationToken = default) =>
        inner.PendingSubscribersAsync(cancellationToken);

    public Task<int> DeadLetterPendingAsync(string subscriber, string reason, CancellationToken cancellationToken = default) =>
        inner.DeadLetterPendingAsync(subscriber, reason, cancellationToken);

    public Task<int> CancelPendingAsync(string subscriber, CancellationToken cancellationToken = default) =>
        inner.CancelPendingAsync(subscriber, cancellationToken);
}
```

- [ ] **Step 2: Write the facts (failing until the legs exist)**

`test/_shared/testhost/EventHandlerFacts.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

using System.Collections.Concurrent;
using System.Data.Common;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Event handlers over the real pipeline (spec 2026-10-14 §7 HX-03), on whichever engine the deriving class supplies — the
/// SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class EventHandlerFacts
{
    protected const string HandlerDesk = "handler-desk.alvo.json";
    protected const string TenantDesk = "tenant-desk.alvo.json";
    protected const string Tickets = "tickets";

    private const string ForeignTrace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static AlvoContext Agent { get; } = AlvoTestHost.Caller("agent");

    /// <summary>(4) An event the condition does not match costs no delivery row.</summary>
    [Fact]
    public async Task An_unmatched_condition_records_no_delivery()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t
            .OnCreated("matches", Done, when: "new.title == 'match'")
            .OnCreated("misses", Done, when: "new.title == 'other'")));
        await CreateTicketAsync(alvo, "match");

        await alvo.DrainEventsAsync(Ct);

        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Subscriber.ShouldBe("matches");
        alvo.Hooks.AssertRan("matches", times: 1);
        alvo.Hooks.AssertRan("misses", times: 0);
    }

    /// <summary>(6) A webhook action that never succeeds does not keep a handler of the same event from running.</summary>
    [Fact]
    public async Task A_failing_webhook_action_does_not_keep_the_handler_from_its_delivery()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated("audits", Done)));
        await CreateTicketAsync(alvo, "audit me");

        var drained = await alvo.DrainEventsAsync(Ct);

        drained.Failed.ShouldBeGreaterThanOrEqualTo(1, "the webhook has no receiver, so its attempt fails");
        alvo.Hooks.AssertRan("audits", times: 1);
        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Status.ShouldBe("succeeded");
    }

    /// <summary>(8) A handler writing what it subscribes to, through its own data access, stops dead at the depth limit.</summary>
    [Fact]
    public async Task A_handler_writing_what_it_subscribes_to_through_its_context_stops_dead_at_the_depth_limit()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "echo", (AlvoCreated<AlvoRecord> _, AlvoEventContext context, CancellationToken ct) => context.Data.CreateAsync(Tickets, Values(("title", "echo")), ct))));
        await CreateTicketAsync(alvo, "first");

        await alvo.DrainEventsAsync(Ct);

        await ShouldHaveStoppedAtTheLimitAsync(alvo, database, "echo");
    }

    /// <summary>(8) The same loop through a DI-injected data port and a fresh system context: the flow still carries the depth.</summary>
    [Fact]
    public async Task A_handler_writing_through_an_injected_data_port_with_a_fresh_system_context_stops_dead_too()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "echo-injected", (AlvoCreated<AlvoRecord> _, IAlvoData data, CancellationToken ct) =>
                data.CreateAsync(Tickets, Values(("title", "echo")), AlvoContext.System(null), cancellationToken: ct))));
        await CreateTicketAsync(alvo, "first");

        await alvo.DrainEventsAsync(Ct);

        await ShouldHaveStoppedAtTheLimitAsync(alvo, database, "echo-injected");
    }

    /// <summary>(16) A handler of an application event that publishes the same event stops at fan-out, with no write involved.</summary>
    [Fact]
    public async Task A_handler_republishing_its_own_event_stops_dead_at_the_depth_limit()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.AddEventHandler(
            "echo-approved",
            "orders.approved",
            (AlvoRecord _, AlvoEventContext context, IAlvoEvents events, CancellationToken ct) =>
                events.PublishAsync("orders.approved", "orders/1", null, context.Caller, ct)));
        await alvo.Services.GetRequiredService<IAlvoEvents>().PublishAsync("orders.approved", "orders/1", null, Agent, Ct);

        await alvo.DrainEventsAsync(Ct);

        var rows = await DeliveryRows.ReadAsync(database.Connect, Ct);
        rows.Count(row => row.Status == "succeeded").ShouldBe(8);
        rows.Single(row => row.Status == "dead").LastErrorType.ShouldBe("chain-depth-exceeded");
    }

    /// <summary>(17) A condition that throws on one record's event is a dead delivery, and another record's event is not delayed.</summary>
    [Fact]
    public async Task A_condition_that_throws_is_dead_and_delays_no_other_record()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated("by-code", Done, when: "int(new.code) > 0")));
        await alvo.Data.CreateAsync(Tickets, Values(("title", "A"), ("code", "x")), Agent, cancellationToken: Ct);
        await alvo.Data.CreateAsync(Tickets, Values(("title", "B"), ("code", "5")), Agent, cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        var rows = await DeliveryRows.ReadAsync(database.Connect, Ct);
        rows[0].Status.ShouldBe("dead");
        rows[0].LastErrorType.ShouldBe("when-failed");
        rows[1].Status.ShouldBe("succeeded");
        alvo.Hooks.AssertRan("by-code", times: 1);
    }

    /// <summary>(19) A dead delivery holds back its record's later events for an ordered handler only.</summary>
    [Theory]
    [InlineData(AlvoOrdering.None, 1)]
    [InlineData(AlvoOrdering.PerRecord, 0)]
    public async Task A_dead_delivery_holds_back_its_record_only_for_an_ordered_handler(AlvoOrdering ordering, int laterRuns)
    {
        var database = await DatabaseAsync();
        var runs = 0;
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnSaved(
            "fragile",
            (AlvoSaved<AlvoRecord> saved) =>
            {
                if (Equals(saved.New["title"], "boom"))
                {
                    throw new InvalidOperationException("boom");
                }

                Interlocked.Increment(ref runs);
            },
            configure: options =>
            {
                options.Ordering = ordering;
                options.Retry = AlvoRetryPolicy.None;
            })));
        var ticket = await CreateTicketAsync(alvo, "boom");
        await alvo.DrainEventsAsync(Ct);

        await alvo.Data.UpdateAsync(Tickets, (Guid)ticket[AlvoManagedColumns.Id]!, Values(("title", "fine")), Agent, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        Volatile.Read(ref runs).ShouldBe(laterRuns);
    }

    /// <summary>(12) A system handler acts in its event's tenant and cannot read another tenant's row.</summary>
    [Fact]
    public async Task A_system_handler_cannot_read_another_tenant_s_row()
    {
        var database = await DatabaseAsync();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var foreign = Guid.Empty;
        var seen = new ConcurrentDictionary<Guid, (bool ReadForeign, int Listed)>();
        await using var alvo = await StartAsync(database, TenantDesk, builder => builder.Entity("notes", n => n.OnCreated(
            "tenant-peek",
            async (AlvoCreated<AlvoRecord> _, AlvoEventContext context, CancellationToken ct) =>
            {
                var read = await context.Data.GetAsync("notes", foreign, ct);
                var listed = await context.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, ct);
                seen[context.Caller.Tenant!.Value.Value] = (read is not null, listed.Items.Count);
            })));
        var noteB = await alvo.Data.CreateAsync("notes", Values(("text", "b"), ("tenant_id", tenantB)), Agent with { Tenant = new TenantId(tenantB) }, cancellationToken: Ct);
        foreign = (Guid)noteB[AlvoManagedColumns.Id]!;
        await alvo.Data.CreateAsync("notes", Values(("text", "a"), ("tenant_id", tenantA)), Agent with { Tenant = new TenantId(tenantA) }, cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        seen[tenantA].ShouldBe((false, 1), "tenant A's handler sees tenant A's one note, and tenant B's note is not found");
    }

    /// <summary>(13) The span links the envelope's trace, so the test host's recorder still counts the run under its drain.</summary>
    [Fact]
    public async Task A_handler_on_an_event_with_a_foreign_trace_still_counts_in_the_recorder()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated("traced", Done)));
        var traced = Agent with { Provenance = new AlvoProvenance { TraceParent = ForeignTrace } };
        await alvo.Data.CreateAsync(Tickets, Values(("title", "traced")), traced, cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        alvo.Hooks.AssertRan("traced", times: 1);
    }

    /// <summary>
    /// (2) and (18): a process that stops answering mid-delivery leaves the claim to its lease; a restart delivers the same
    /// event again; and the first process finishing late changes nothing.
    /// </summary>
    [Fact]
    public async Task A_handler_killed_mid_delivery_runs_again_with_the_same_event_and_the_late_finisher_changes_nothing()
    {
        var database = await DatabaseAsync();
        var start = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid? rerun = null;
        var first = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "slow",
            async (AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
            {
                entered.TrySetResult(context.EventId);
                await release.Task;
            })), new FakeTimeProvider(start));
        await CreateTicketAsync(first, "Printer jam");
        var abandoned = first.DrainEventsAsync(Ct);
        var original = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Status.ShouldBe("running", "the first process holds the claim it will never answer for in time");

        await using (var restarted = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "slow", (AlvoCreated<AlvoRecord> _, AlvoEventContext context) => { rerun = context.EventId; })), new FakeTimeProvider(start + TimeSpan.FromSeconds(61))))
        {
            await restarted.DrainEventsAsync(Ct);
        }

        release.SetResult();
        await abandoned;
        await first.DisposeAsync();

        rerun.ShouldBe(original, "at least once, and with the same event id every time");
        var row = (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem();
        row.Status.ShouldBe("succeeded");
        row.Attempts.ShouldBe(2);
        (await DeliveryRows.AttemptCountAsync(database.Connect, Ct)).ShouldBe(1, "the late finisher was fenced out and recorded nothing");
    }

    /// <summary>
    /// (5) across batches (B1): with one outbox entry per batch, event 1's fan-out fails and events 2 and 3 of the same
    /// record are fanned out first — yet none of them is delivered before event 1, and all arrive in order.
    /// </summary>
    [Fact]
    public async Task A_record_s_events_reach_its_handler_in_order_when_the_first_fan_out_fails_alone_in_its_batch()
    {
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var seen = new ConcurrentQueue<string>();
        await using var alvo = await StartAsync(database, HandlerDesk, builder =>
        {
            builder.Entity(Tickets, t => t.OnSaved("in-order", (AlvoSaved<AlvoRecord> saved) => seen.Enqueue((string)saved.New["title"]!)));
            builder.Services.Configure<AlvoEventOptions>(options => options.BatchSize = 1);
            FailingFirstFanOut.Install(builder.Services);
        }, clock);
        await WriteThreeVersionsAsync(alvo);

        await alvo.DrainEventsAsync(Ct);
        seen.ShouldBeEmpty("event 1 is not fanned out yet, so its record's later deliveries wait");
        clock.Advance(TimeSpan.FromMinutes(1));
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe(["first", "second", "third"]);
    }

    /// <summary>
    /// (5) across a crash (B1): an outbox claimant that took event 1 and died before fanning it out holds the record's
    /// later deliveries back until its claim's lease returns event 1 to the queue.
    /// </summary>
    [Fact]
    public async Task A_record_s_events_wait_for_an_event_an_outbox_claimant_abandoned_before_its_fan_out()
    {
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var seen = new ConcurrentQueue<string>();
        await using var alvo = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnSaved(
            "in-order", (AlvoSaved<AlvoRecord> saved) => seen.Enqueue((string)saved.New["title"]!))), clock);
        var ticket = await CreateTicketAsync(alvo, "first");
        await alvo.Services.GetRequiredService<IOutboxStore>().ClaimAsync("dead-dispatcher", batchSize: 1, maxAttempts: 10, TimeSpan.FromMinutes(5), Ct);
        await UpdateTitlesAsync(alvo, (Guid)ticket[AlvoManagedColumns.Id]!, "second", "third");

        await alvo.DrainEventsAsync(Ct);
        seen.ShouldBeEmpty("event 1 is held by a claimant that will never fan it out");
        clock.Advance(TimeSpan.FromMinutes(6));
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe(["first", "second", "third"]);
    }

    /// <summary>(9) Unfinished work of a handler no longer registered refuses the start; a dead-letter retirement ends it as dead.</summary>
    [Fact]
    public async Task Work_left_by_a_dropped_handler_refuses_the_start_until_it_is_retired_as_dead()
    {
        var database = await DatabaseAsync();
        await LeaveRetryingWorkAsync(database);

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartAsync(database, HandlerDesk, _ => { }));
        refusal.Message.ShouldContain("'old-handler'");

        await using (await StartAsync(database, HandlerDesk, builder => builder.AddRetiredExtension("old-handler", AlvoRetirement.DeadLetter)))
        {
        }

        var row = (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem();
        row.Status.ShouldBe("dead");
        row.LastErrorType.ShouldBe("retired");
    }

    /// <summary>(9) A discard retirement cancels the work instead.</summary>
    [Fact]
    public async Task A_discard_retirement_cancels_the_work_a_dropped_handler_left()
    {
        var database = await DatabaseAsync();
        await LeaveRetryingWorkAsync(database);

        await using (await StartAsync(database, HandlerDesk, builder => builder.AddRetiredExtension("old-handler", AlvoRetirement.Discard)))
        {
        }

        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Status.ShouldBe("cancelled");
    }

    /// <summary>Registers this leg's database; both legs give every fact a database of its own that two hosts can share.</summary>
    protected abstract Task<HandlerDatabase> DatabaseAsync();

    protected static async Task<AlvoTestHost> StartAsync(
        HandlerDatabase database, string descriptor, Action<IAlvoBuilder> configure, TimeProvider? time = null) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", descriptor));
                configure(alvo);
            },
            time,
            webhooks: null,
            Ct);

    protected static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal);

    private static Func<AlvoCreated<AlvoRecord>, Task> Done { get; } = _ => Task.CompletedTask;

    private static Task<AlvoRecord> CreateTicketAsync(AlvoTestHost alvo, string title) =>
        alvo.Data.CreateAsync(Tickets, Values(("title", title)), Agent, cancellationToken: Ct);

    private static async Task WriteThreeVersionsAsync(AlvoTestHost alvo)
    {
        var ticket = await CreateTicketAsync(alvo, "first");
        await UpdateTitlesAsync(alvo, (Guid)ticket[AlvoManagedColumns.Id]!, "second", "third");
    }

    private static async Task UpdateTitlesAsync(AlvoTestHost alvo, Guid id, params string[] titles)
    {
        foreach (var title in titles)
        {
            await alvo.Data.UpdateAsync(Tickets, id, Values(("title", title)), Agent, cancellationToken: Ct);
        }
    }

    private static async Task ShouldHaveStoppedAtTheLimitAsync(AlvoTestHost alvo, HandlerDatabase database, string subscriber)
    {
        var rows = (await DeliveryRows.ReadAsync(database.Connect, Ct)).Where(row => row.Subscriber == subscriber).ToList();
        rows.Count(row => row.Status == "succeeded").ShouldBe(8, "events at depth 0 to 7 ran their handler, each writing one hop deeper");
        rows.Single(row => row.Status == "dead").LastErrorType.ShouldBe("chain-depth-exceeded");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = Tickets }, Agent, Ct)).Items.Count.ShouldBe(9);
    }

    private static async Task LeaveRetryingWorkAsync(HandlerDatabase database)
    {
        await using var before = await StartAsync(database, HandlerDesk, builder => builder.Entity(Tickets, t => t.OnCreated(
            "old-handler", (AlvoCreated<AlvoRecord> _) => Task.FromException(new InvalidOperationException()))));
        await CreateTicketAsync(before, "left behind");
        await before.DrainEventsAsync(Ct);
        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldHaveSingleItem().Status.ShouldBe("retrying");
    }

    /// <summary>How a leg hands a fact its database: the builder registration, and a raw connection to read the tables.</summary>
    protected sealed record HandlerDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
```

`test/MMLib.Alvo.TestHost.Tests/SqliteEventHandlerTests.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The handler facts on SQLite — a temp file per fact, so two hosts can share one database.</summary>
public sealed class SqliteEventHandlerTests : EventHandlerFacts, IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    protected override Task<HandlerDatabase> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-handlers-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = $"Data Source={path}";

        return Task.FromResult(new HandlerDatabase(alvo => alvo.UseSqlite(connectionString), () => new SqliteConnection(connectionString)));
    }
}
```

`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlEventHandlerTests.cs`:

```csharp
using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The handler facts with <c>UsePostgreSql</c> — a fresh database per fact on one container, dropped when the class ends.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlEventHandlerTests(PostgresFixture postgres)
    : EventHandlerFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly List<string> _databases = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    protected override async Task<HandlerDatabase> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_handlers_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();

        return new HandlerDatabase(alvo => alvo.UsePostgreSql(connectionString), () => new NpgsqlConnection(connectionString));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 3: Run the SQLite leg, then the PostgreSQL leg**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include test/_shared/testhost/EventHandlerFacts.cs test/_shared/testhost/DeliveryRows.cs test/_shared/testhost/FailingFirstFanOut.cs test/MMLib.Alvo.TestHost.Tests/SqliteEventHandlerTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlEventHandlerTests.cs`, then:
- `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteEventHandlerTests'` — every fact PASS (a fact that fails here is a defect in Tasks 3–11; fix the code, not the fact — e.g. a depth fact counting 9 succeeded rows means fan-out is not refusing at `chaindepth + 1 > 8`)
- `dotnet test --project test/MMLib.Alvo.TestHost.Tests` — the HX-02 facts, the R-12 loop fact included, unchanged
- `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlEventHandlerTests' --filter-class '*PostgreSqlAlvoTestHostTests'` — PASS (Docker)

- [ ] **Step 4: Rings**

Run `scripts/test-ring0` and `scripts/test-ring1` (slice end). Expected: green.

- [ ] **Step 5: Commit**

```bash
git add test/_shared/testhost/EventHandlerFacts.cs test/_shared/testhost/DeliveryRows.cs test/_shared/testhost/FailingFirstFanOut.cs test/_shared/testhost/descriptors/handler-desk.alvo.json test/_shared/testhost/descriptors/tenant-desk.alvo.json test/MMLib.Alvo.TestHost.Tests/SqliteEventHandlerTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlEventHandlerTests.cs
git commit -m "test(events): event handlers end to end on SQLite and PostgreSQL

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 13: The 10 000-event delivery chaos run, on both engines (acceptance (3))

**Files:**
- Create: `test/_shared/deliveries/DeliveryChaosCriteriaTests.cs`, `test/_shared/deliveries/chaos-desk.alvo.json`
- Create: `test/MMLib.Alvo.Data.Sqlite.Tests.Integration/SqliteDeliveryChaosTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryChaosTests.cs`
- Modify: `test/MMLib.Alvo.Data.Sqlite.Tests.Integration/MMLib.Alvo.Data.Sqlite.Tests.Integration.csproj` (reference the test host, link the suite, `DeliveryRows.cs` and the descriptor), `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/MMLib.Alvo.Data.PostgreSql.Tests.Integration.csproj` (link the suite and the descriptor)

**Interfaces:**
- Consumes: `AlvoTestHost`, `IAlvoEventPump`, `IDeliveryStore`, `DeliveryClaim`, `AlvoRetryPolicy`, `AlvoEventOptions.BatchSize`, `AlvoDeliveryOptions.MaxConcurrency`; `DeliveryRows` (Task 12); `DispatchedEventCollection`, `RepositoryRoot` (existing).
- Produces: `abstract class DeliveryChaosCriteriaTests { protected abstract Task<(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)> DatabaseAsync(); protected abstract string EngineDescription { get; } }`.

Modelled on `OutboxChaosCriteriaTests` (`baas-analyza.md:676`): the queue is asserted full before anything runs, delivery is measured as **sets of ids**, **every record carries four events (a create and three updates) and the sink asserts each record's steps arrived as 0, 1, 2, 3** (M7 — the ordering defect B1 found is invisible with one event per record), the chaos is asserted to have happened, and one line per run goes to `artifacts/criteria/events.md`. One delivery in twenty throws; two claims are taken by a claimant that never answers (`dead-worker`), so the lease is what recovers them; the clock moves only when a round is quiet, past every retry and lease. Ring2: the legs live in the two `.Tests.Integration` projects, in `DispatchedEventCollection`, because the outbox counters are process-wide.

- [ ] **Step 1: Write the descriptor and the suite**

`test/_shared/deliveries/chaos-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "chaos-desk",
  "auth": { "roles": ["agent"] },
  "entities": {
    "orders": {
      "fields": {
        "step": { "type": "integer" }
      },
      "rules": {
        "list": "'agent' in @user.roles",
        "get": "'agent' in @user.roles",
        "create": "'agent' in @user.roles",
        "update": "'agent' in @user.roles"
      }
    }
  }
}
```

`test/_shared/deliveries/DeliveryChaosCriteriaTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.TestHost;
using MMLib.Alvo.TestHost.Tests;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

using Xunit;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// <c>baas-analyza.md:676</c> for event handlers: a <b>10 000-event chaos run loses nothing and keeps every record's
/// order</b> — 2 500 records with four events each reach their handler, through refused attempts and abandoned
/// claims, over the real outbox, fan-out and delivery store.
/// </summary>
public abstract class DeliveryChaosCriteriaTests
{
    private const int EventCount = 10_000;
    private const int EventsPerRecord = 4;
    private const int RecordCount = EventCount / EventsPerRecord;
    private const int FailEvery = 20;
    private const int AbandonedClaims = 2;
    private const int AbandonEveryRounds = 5;
    private const int SeedBatch = 100;
    private const int MaxRounds = 5_000;
    private const string Sink = "chaos-sink";
    private const string Orders = "orders";

    private static readonly AlvoContext _agent = AlvoTestHost.Caller("agent");

    /// <summary>The engine and build configuration a reported number was measured under.</summary>
    protected abstract string EngineDescription { get; }

    /// <summary>Registers this leg's database on the builder, and a raw connection to read the delivery table.</summary>
    protected abstract Task<(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)> DatabaseAsync();

    [Fact]
    public async Task Ten_thousand_events_reach_their_handler_in_order_and_none_is_lost()
    {
        EventCount.ShouldBe(10_000, "the criterion is ten thousand events; a smaller number is a decision made here");
        var database = await DatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var sink = new ChaosSink();
        await using var alvo = await StartAsync(database.Use, clock, sink);

        var seeded = await SeedAsync(alvo);
        var stopwatch = Stopwatch.StartNew();
        var run = await RunWithChaosAsync(alvo, clock, sink);
        var rows = await DeliveryRows.ReadAsync(database.Connect, Ct);

        await ReportAsync($"§3 delivery chaos ({EventCount} events on {RecordCount} records, fail 1-in-{FailEvery}, "
            + $"{AbandonedClaims} abandoned claims, {EngineDescription}): {run.Rounds} rounds, {sink.AcceptedEvents.Count} accepted, "
            + $"{sink.Refused.Count} refused, {stopwatch.Elapsed.TotalSeconds:F1}s");

        AssertNothingIsLost(seeded, sink, rows, run);
        AssertEveryRecordKeptItsOrder(sink);
        AssertTheChaosHappened(sink, run);
    }

    private static async Task<AlvoTestHost> StartAsync(Action<IAlvoBuilder> use, FakeTimeProvider clock, ChaosSink sink) =>
        await AlvoTestHost.StartAsync(
            builder =>
            {
                use(builder);
                builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "chaos-desk.alvo.json"));
                builder.Entity(Orders, orders => orders.OnSaved(
                    Sink, sink.HandleAsync, configure: options => options.Retry = AlvoRetryPolicy.From([.. Enumerable.Repeat(TimeSpan.FromSeconds(1), 12)])));
                builder.Services.Configure<AlvoEventOptions>(options => options.BatchSize = 500);
                builder.Services.Configure<AlvoDeliveryOptions>(options => options.MaxConcurrency = 64);
            },
            clock,
            cancellationToken: Ct);

    /// <summary>Creates every record (step 0), then writes steps 1 to 3 to each — four events per record, ten thousand in all.</summary>
    private static async Task<HashSet<string>> SeedAsync(AlvoTestHost alvo)
    {
        var ids = await CreateRecordsAsync(alvo);
        for (var step = 1; step < EventsPerRecord; step++)
        {
            foreach (var batch in ids.Chunk(SeedBatch))
            {
                IReadOnlyList<AlvoRowPatch> patches = [.. batch.Select(id => new AlvoRowPatch(id, Reference(id, step)))];
                await alvo.Data.UpdateManyAsync(Orders, patches, _agent, cancellationToken: Ct);
            }
        }

        var seeded = ids.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        seeded.Count.ShouldBe(RecordCount, "'nothing was lost' is trivially true of an empty queue");

        return seeded;
    }

    private static async Task<List<Guid>> CreateRecordsAsync(AlvoTestHost alvo)
    {
        var ids = new List<Guid>();
        for (var batch = 0; batch < RecordCount / SeedBatch; batch++)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
                [.. Enumerable.Range(0, SeedBatch).Select(_ => new Dictionary<string, object?> { ["step"] = 0L })];
            var result = await alvo.Data.CreateManyAsync(Orders, rows, _agent, cancellationToken: Ct);
            ids.AddRange(result.Rows.Select(row => (Guid)row[AlvoManagedColumns.Id]!));
        }

        return ids;
    }

    private static Dictionary<string, object?> Reference(Guid id, int step) => new() { ["step"] = (long)step };

    private static async Task<ChaosRun> RunWithChaosAsync(AlvoTestHost alvo, FakeTimeProvider clock, ChaosSink sink)
    {
        var pump = alvo.Services.GetRequiredService<IAlvoEventPump>();
        var store = alvo.Services.GetRequiredService<IDeliveryStore>();
        var abandoned = new List<Guid>();
        var rounds = 0;
        while (sink.AcceptedEvents.Count < EventCount && rounds < MaxRounds)
        {
            var result = await pump.DrainAsync(Ct);
            rounds++;
            if (rounds % AbandonEveryRounds == 0 && abandoned.Count < AbandonedClaims)
            {
                abandoned.AddRange((await store.ClaimAsync(DeadWorkerClaim(), Ct)).Select(delivery => delivery.EventId).Take(1));
            }

            if (result.IsQuiet)
            {
                clock.Advance(TimeSpan.FromMinutes(2));
            }
        }

        return new ChaosRun(rounds, abandoned);
    }

    private static DeliveryClaim DeadWorkerClaim() => new()
    {
        Claimant = "dead-worker",
        Ordered = [Sink],
        BatchSize = 1,
        Lease = TimeSpan.FromMinutes(1),
        OutboxMaxAttempts = 10,
    };

    private static void AssertNothingIsLost(HashSet<string> seeded, ChaosSink sink, IReadOnlyList<DeliveryRow> rows, ChaosRun run)
    {
        run.Rounds.ShouldBeLessThan(MaxRounds, "the pump ran out of rounds instead of draining the queue");
        sink.AcceptedEvents.Count.ShouldBe(EventCount, "every event reaches the handler at least once");
        sink.Steps.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(seeded).ShouldBeTrue("every seeded record's events reached the handler");
        rows.Count.ShouldBe(EventCount, "one delivery per event, never two: the fan-out is unique on (event, subscriber)");
        rows.ShouldAllBe(row => row.Status == "succeeded", "nothing is left pending, retrying or dead");
    }

    /// <summary>Q7: one record's events reach an ordered handler in order, whatever failed or was abandoned between them (M7).</summary>
    private static void AssertEveryRecordKeptItsOrder(ChaosSink sink)
    {
        var outOfOrder = sink.Steps.Where(record => !record.Value.SequenceEqual(Enumerable.Range(0, EventsPerRecord))).Select(record => record.Key).ToList();
        outOfOrder.ShouldBeEmpty($"{outOfOrder.Count} records saw their events out of order");
    }

    private static void AssertTheChaosHappened(ChaosSink sink, ChaosRun run)
    {
        sink.Refused.Count.ShouldBeGreaterThan(EventCount / (FailEvery * 2), "the chaos must have happened, not only been configured");
        sink.Refused.Keys.ShouldAllBe(id => sink.AcceptedEvents.ContainsKey(id), "a refused delivery is retried until it lands");
        run.Abandoned.Count.ShouldBe(AbandonedClaims, "a claim must have been abandoned mid-run");
        run.Abandoned.ShouldAllBe(id => sink.AcceptedEvents.ContainsKey(id), "an abandoned claim is recovered by its lease and nothing else");
    }

    private static async Task ReportAsync(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.AddAttachment("events-criteria", line);
        var path = Path.Combine(RepositoryRoot.Find(), "artifacts", "criteria", "events.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(
            path, string.Create(CultureInfo.InvariantCulture, $"- {DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}"), Ct);
    }

#if DEBUG
    /// <summary>The build configuration a reported number was measured under.</summary>
    protected const string BuildConfiguration = "Debug";
#else
    /// <summary>The build configuration a reported number was measured under.</summary>
    protected const string BuildConfiguration = "Release";
#endif

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ChaosRun(int Rounds, IReadOnlyList<Guid> Abandoned);

    /// <summary>
    /// The handler: refuses one call in <see cref="FailEvery"/>; remembers every event it accepted and, per record, the
    /// order in which that record's steps arrived.
    /// </summary>
    private sealed class ChaosSink
    {
        private int _calls;

        internal ConcurrentDictionary<Guid, byte> AcceptedEvents { get; } = new();

        internal ConcurrentDictionary<string, ConcurrentQueue<int>> Steps { get; } = new(StringComparer.Ordinal);

        internal ConcurrentDictionary<Guid, byte> Refused { get; } = new();

        internal Task HandleAsync(AlvoSaved<AlvoRecord> saved, AlvoEventContext context)
        {
            if (Interlocked.Increment(ref _calls) % FailEvery == 0)
            {
                Refused.TryAdd(context.EventId, 0);
                return Task.FromException(new InvalidOperationException("chaos"));
            }

            if (AcceptedEvents.TryAdd(context.EventId, 0))
            {
                Steps.GetOrAdd(saved.New[AlvoManagedColumns.Id]!.ToString()!, _ => new()).Enqueue(Convert.ToInt32(saved.New["step"], CultureInfo.InvariantCulture));
            }

            return Task.CompletedTask;
        }
    }
}
```

`test/MMLib.Alvo.Data.Sqlite.Tests.Integration/SqliteDeliveryChaosTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Tests.Events;

using System.Data.Common;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests.Integration;

/// <summary>The SQLite leg of the delivery chaos criterion, over a real database file.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class SqliteDeliveryChaosTests : DeliveryChaosCriteriaTests, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"alvo-delivery-chaos-{Guid.NewGuid():N}.db");

    protected override string EngineDescription => "sqlite (file), " + BuildConfiguration;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    protected override Task<(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)> DatabaseAsync()
    {
        var connectionString = $"Data Source={_path}";

        return Task.FromResult<(Action<IAlvoBuilder>, Func<DbConnection>)>((alvo => alvo.UseSqlite(connectionString), () => new SqliteConnection(connectionString)));
    }
}
```

`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryChaosTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Tests.Events;

using Npgsql;

using System.Data.Common;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The PostgreSQL leg of the delivery chaos criterion, in a database of its own.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlDeliveryChaosTests(PostgresFixture postgres)
    : DeliveryChaosCriteriaTests, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _name = $"alvo_delivery_chaos_{Guid.NewGuid():N}";

    protected override string EngineDescription => "postgres:16-alpine, " + BuildConfiguration;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await ExecuteAsync($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");

    protected override async Task<(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");
        await ExecuteAsync($"CREATE DATABASE \"{_name}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = _name }.ToString();

        return (alvo => alvo.UsePostgreSql(connectionString), () => new NpgsqlConnection(connectionString));
    }

    private async Task ExecuteAsync(string sql)
    {
        if (string.IsNullOrEmpty(postgres.ConnectionString))
        {
            return;
        }

        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
```

`MMLib.Alvo.Data.Sqlite.Tests.Integration.csproj`: add `<ProjectReference Include="../../src/MMLib.Alvo.TestHost/MMLib.Alvo.TestHost.csproj" />` to the project references, and to the chaos item group:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)../_shared/deliveries/DeliveryChaosCriteriaTests.cs" Link="_shared/DeliveryChaosCriteriaTests.cs" />
    <Compile Include="$(MSBuildThisFileDirectory)../_shared/testhost/DeliveryRows.cs" Link="_shared/DeliveryRows.cs" />
    <None Include="$(MSBuildThisFileDirectory)../_shared/deliveries/chaos-desk.alvo.json" Link="chaos-desk.alvo.json" CopyToOutputDirectory="PreserveNewest" />
```

`MMLib.Alvo.Data.PostgreSql.Tests.Integration.csproj`: beside the outbox chaos links add the first and third of those lines (it already compiles `_shared/testhost/*.cs`, `DeliveryRows.cs` included).

- [ ] **Step 2: Run both legs**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include test/_shared/deliveries/DeliveryChaosCriteriaTests.cs test/MMLib.Alvo.Data.Sqlite.Tests.Integration/SqliteDeliveryChaosTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryChaosTests.cs`, then:
- `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests.Integration` — both chaos criteria PASS; read the appended line in `artifacts/criteria/events.md` (rounds, refused, seconds)
- `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlDeliveryChaosTests' --filter-class '*PostgreSqlOutboxChaosTests'` — PASS

If the SQLite leg takes more than ~3 minutes, record the number in the PR report rather than lowering `EventCount` — the constant is the criterion. A failing set comparison is a lost event: find it with `DeliveryRows` before touching the run.

- [ ] **Step 3: Commit**

```bash
git add test/_shared/deliveries/DeliveryChaosCriteriaTests.cs test/_shared/deliveries/chaos-desk.alvo.json test/MMLib.Alvo.Data.Sqlite.Tests.Integration/SqliteDeliveryChaosTests.cs test/MMLib.Alvo.Data.Sqlite.Tests.Integration/MMLib.Alvo.Data.Sqlite.Tests.Integration.csproj test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryChaosTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/MMLib.Alvo.Data.PostgreSql.Tests.Integration.csproj
git commit -m "test(events): a 10 000-event delivery chaos run loses nothing, on both engines

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 14: Docs, snippets, DocsGen, architecture docs, rule 4, CHANGELOG, PLAN, spec amendment

**Files:**
- Create: `website/src/content/docs/extending/event-handlers.mdx`; `website/src/snippets/extending/handlers/ticket-desk.alvo.json`, `TicketDeskHandlers.cs`, `TicketDeskHandlersTests.cs`
- Modify: `website/astro.config.mjs`, `website/src/content/docs/extending/index.mdx`, `where-logic-goes.mdx`, `testing.mdx`
- Modify: `tools/MMLib.Alvo.DocsGen/CSharp/ApiScope.cs`, `test/MMLib.Alvo.DocsGen.Tests/CSharp/CSharpApiTests.cs`
- Modify: `docs/architecture/events.md`, `docs/architecture/extensibility.md`, `CHANGELOG.md`, `docs/PLAN.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`

- [ ] **Step 1: The snippets (compiled and run by `MMLib.Alvo.DocsSnippets.Tests`)**

`website/src/snippets/extending/handlers/ticket-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "ticket-desk",
  "auth": { "roles": ["agent"] },
  "entities": {
    "tickets": {
      "fields": {
        "title": { "type": "string", "required": true, "maxLength": 120 },
        "priority": { "type": "enum", "values": ["low", "high"] },
        "status": { "type": "enum", "values": ["open", "resolved"] }
      },
      "rules": {
        "list": "'agent' in @user.roles || 'admin' in @user.roles",
        "get": "'agent' in @user.roles || 'admin' in @user.roles",
        "create": "'agent' in @user.roles",
        "update": "'agent' in @user.roles"
      }
    },
    "follow_ups": {
      "fields": {
        "ticket_id": { "type": "string", "maxLength": 36 },
        "note": { "type": "string", "maxLength": 200 }
      },
      "rules": {
        "list": "'agent' in @user.roles",
        "get": "'agent' in @user.roles",
        "create": "'admin' in @user.roles"
      }
    }
  }
}
```

`website/src/snippets/extending/handlers/TicketDeskHandlers.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace TicketDesk;

public static class TicketDeskHandlers
{
    public static IAlvoBuilder AddTicketDeskHandlers(this IAlvoBuilder alvo) => alvo
        .Entity("tickets", tickets => tickets
            .OnUpdated(
                "ticket-resolved-to-chat",
                when: "changed(status) && new.status == 'resolved'",
                handler: (AlvoUpdated<AlvoRecord> resolved, IChatNotifier chat, CancellationToken ct) =>
                    chat.PostAsync($"Ticket '{resolved.New["title"]}' resolved", ct))
            .OnCreated<OpenFollowUp>(
                "open-follow-up",
                when: "new.priority == 'high'",
                configure: options => options.Timeout = TimeSpan.FromSeconds(10)));
}

public interface IChatNotifier
{
    Task PostAsync(string message, CancellationToken cancellationToken);
}

public sealed class OpenFollowUp : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
{
    public Task HandleAsync(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken) =>
        context.Data.CreateAsync(
            "follow_ups",
            new Dictionary<string, object?> { ["ticket_id"] = created.New["id"]?.ToString(), ["note"] = "Call the customer back" },
            cancellationToken);
}
```

`website/src/snippets/extending/handlers/TicketDeskHandlersTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using TicketDesk;
using Xunit;

namespace TicketDesk.Tests;

public sealed class TicketDeskHandlersTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "ticket-desk.alvo.json");

    [Fact]
    public async Task Resolving_a_ticket_posts_to_the_chat_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new RecordingChat();
        await using var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddTicketDeskHandlers();
            builder.Services.AddSingleton<IChatNotifier>(chat);
        }, cancellationToken: ct);
        var agent = AlvoTestHost.Caller("agent");

        var ticket = await alvo.Data.CreateAsync(
            "tickets", new Dictionary<string, object?> { ["title"] = "Printer jam", ["priority"] = "low", ["status"] = "open" }, agent, cancellationToken: ct);
        await alvo.Data.UpdateAsync(
            "tickets", (Guid)ticket["id"]!, new Dictionary<string, object?> { ["status"] = "resolved" }, agent, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        chat.Posted.ShouldBe(["Ticket 'Printer jam' resolved"]);
        alvo.Hooks.AssertRan("ticket-resolved-to-chat", times: 1);
        alvo.Hooks.AssertRan("open-follow-up", times: 0);

        await alvo.Data.CreateAsync(
            "tickets", new Dictionary<string, object?> { ["title"] = "Server down", ["priority"] = "high", ["status"] = "open" }, agent, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        alvo.Hooks.AssertRan("open-follow-up", times: 1);
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "follow_ups" }, agent, ct)).Items.ShouldHaveSingleItem();
    }

    private sealed class RecordingChat : IChatNotifier
    {
        private readonly List<string> _posted = [];

        public IReadOnlyList<string> Posted => _posted;

        public Task PostAsync(string message, CancellationToken cancellationToken)
        {
            _posted.Add(message);
            return Task.CompletedTask;
        }
    }
}
```

Run: `dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/handlers/TicketDeskHandlers.cs website/src/snippets/extending/handlers/TicketDeskHandlersTests.cs`, then `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests` — PASS (the coverage fact sees both new types; the descriptor name is page-unique).

- [ ] **Step 2: The page**

`website/src/content/docs/extending/event-handlers.mdx`:

```mdx
---
title: React to changes (event handlers)
description: Run C# after a write commits — once per matching event, at least once, retried, in order per record, and visible in your tests.
sidebar:
  order: 3
---

import { Code } from '@astrojs/starlight/components';
import SourceExcerpt from '../../../components/SourceExcerpt.astro';
import handlers from '../../../snippets/extending/handlers/TicketDeskHandlers.cs?raw';
import tests from '../../../snippets/extending/handlers/TicketDeskHandlersTests.cs?raw';
import descriptor from '../../../snippets/extending/handlers/ticket-desk.alvo.json?raw';

An event handler is C# your host runs **after** a write commits: post to a chat, call an ERP, open a follow-up. It runs
on Alvo's transactional outbox, so it never undoes the write and is never lost with it — each matching event becomes a
**delivery** that is retried until it succeeds or is dead-lettered.

<Code code={descriptor} lang="json" title="ticket-desk.alvo.json" />

## Register a handler

Group registrations by entity with `Entity("name", …)` — or `Entities(["orders", "invoices"], …)`, or `Entity("*", …)`
for every entity — and pick the verb: `OnCreated`, `OnUpdated` (also a replace), `OnDeleted`, `OnSaved` (created or
updated).

<SourceExcerpt code={handlers} from="public static IAlvoBuilder AddTicketDeskHandlers" to="TimeSpan.FromSeconds(10)));" title="TicketDeskHandlers.cs" />

- **The id is the handler's identity.** Lower-case letters, digits and hyphens. Its deliveries are found by it after a
  restart, so renaming a handler is retiring the old id (below).
- **A delegate** takes the message — `AlvoCreated<AlvoRecord>` (`New`), `AlvoUpdated<AlvoRecord>` (`Old`, `New`),
  `AlvoDeleted<AlvoRecord>` (`Old`), `AlvoSaved<AlvoRecord>` (`New`, `Old` is `null` on create) — and any of
  `AlvoEventContext`, a `CancellationToken` and your own services. **A class** implementing
  `IAlvoEventHandler<TMessage>` is built per delivery from the delivery's own DI scope.
- **`when`** is a CEL condition over the images the verb's event carries: `new.` and `old.` on updates, `new.` on
  creates and saves, `old.` on deletes, `changed(field)` on updates, `@user` and `@tenant`. It is judged after the commit
  and before any delivery exists: an event it does not match costs nothing. A `"*"` registration's condition tests only
  `@user` and `@tenant`.
- **Everything is checked when the host starts**, against your descriptor: an unknown entity, a typo in a condition, a
  condition reading what the event cannot carry or a field left out of the event images, a service your container
  cannot provide. All problems are reported in one refusal, each naming the handler and its slot —
  `extensions/ticket-resolved-to-chat/when`.

<SourceExcerpt code={handlers} from="public sealed class OpenFollowUp" title="TicketDeskHandlers.cs" />

## What a delivery guarantees

- **At least once, with the same `EventId`.** A failure, a timeout or a process that stops mid-run means the handler
  runs again. Deduplicate on `context.EventId` before anything you cannot repeat.
- **Retries.** 5 s, 30 s, 2 min, 15 min, 1 h, 6 h (each spread by a fifth either way); after the seventh attempt the
  delivery is **dead**. Change it per handler with `options.Retry = AlvoRetryPolicy.From(…)` or `AlvoRetryPolicy.None`.
  A timeout (`options.Timeout`, 30 s by default) ends the attempt as failed whether or not the handler observes its
  token: Alvo cancels the token and waits up to 30 s more, then records the attempt and moves on. A handler that ignores
  its token keeps running in the background and can overlap its own retry — another reason to be idempotent on
  `EventId`.
- **In order per record.** One record's events reach a handler in order — also when an earlier event of the record
  has not been fanned out yet because its processing failed or its dispatcher died — and a dead delivery holds that
  record's later events back, for that handler only. `options.Ordering = AlvoOrdering.None` opts out. Order is by event
  id, so two processes writing one record in the same millisecond can still interleave (#150). An earlier event whose
  descriptor after-hook action keeps failing holds the record's later handler deliveries back until it succeeds or the
  outbox gives up on it (`Alvo:Events:MaxAttempts`).
- **A dead delivery blocks its record until it is cleared.** Redeliver, replay and pause arrive in a later release; until
  then an operator unblocks a lane by setting that row of `alvo_deliveries` to `cancelled` (or back to `pending` to
  retry it), or retires the whole handler with `AddRetiredExtension`.
- **Who the handler is.** By default the system, in the event's tenant (`'admin' in @user.roles` is true, another
  tenant's rows are invisible); `options.RunAs = AlvoRunAs.EventActor` acts as the caller who made the change, with the
  roles it held then. `context.Data` reads and writes as that caller.
- **Loops stop.** A handler's writes and published events sit one hop deeper in the causation chain than the event that
  ran it — even through a DI-injected `IAlvoData` with a fresh `AlvoContext.System`. Past
  [`Alvo:Extensions:MaxDepth`](/reference/configuration/#alvoextensions) (8) the next delivery is recorded dead with
  `chain-depth-exceeded`, never run. The chain follows .NET's execution context: work you hand to your own queue
  leaves it.
- **A handler that no longer fits the descriptor** — after an apply removed what its condition reads, say — is not
  skipped silently: every event it would have handled is recorded as a dead delivery with the reason `unbound`.
- **`EventActor` uses today's roles by name.** A role the descriptor redefined since the event grants its rules as they
  are now; a role it dropped is not held. A handler on an event the system made runs as `admin`, as the event did.
- **Nothing sensitive is stored.** A delivery records ids, statuses, times and an error **type** — never a body, a URL
  or an exception message.

## Application events

`AddEventHandler(id, "orders.approved", (AlvoRecord payload, …) => …)` handles an event your code publishes with
`IAlvoEvents.PublishAsync`. Its condition tests only `@user` and `@tenant`; check the payload inside the handler. The
`alvo.` namespace is reserved for events Alvo mints itself, and a host cannot publish into it.

## Rename or remove a handler

A host whose database holds unfinished deliveries for an id it no longer registers refuses to start, naming the id.
Say what becomes of them: `AddRetiredExtension("old-id", AlvoRetirement.DeadLetter)` keeps them as dead deliveries;
`AlvoRetirement.Discard` cancels them.

## Test it

The test host runs handlers in the same rounds as after-hooks, and `Hooks.AssertRan` counts them by id:

<SourceExcerpt code={tests} from="Resolving_a_ticket_posts_to_the_chat_once()" to='Items.ShouldHaveSingleItem();' title="TicketDeskHandlersTests.cs" />

Never drain from inside a handler: the call is refused, because a round would wait for itself.

## Configuration

`Alvo:Deliveries:MaxConcurrency` (16) bounds how many deliveries one process runs at once. Deliveries share
`Alvo:Events:Enabled`, `DispatchMode` and `PollInterval` with the outbox.

## Not yet

Typed handlers over your own classes (`Entity<T>`), the extension catalog and dashboard, and redeliver, replay and
pause arrive in the next releases.
```

`website/astro.config.mjs`: after `page('The event envelope', 'extending/event-envelope'),` add `page('React to changes', 'extending/event-handlers'),`. In `testing.mdx` set `sidebar.order` to 4.

`website/src/content/docs/extending/index.mdx`, *What exists in this build*, add before the testing bullet:

```mdx
- [React to changes (event handlers)](/extending/event-handlers/) — C# after a write commits: at least once, retried,
  in order per record.
```

and in *What lands next* replace "Event handlers, write hooks in three phases, …" with "Typed handlers, write hooks in three phases, …".

`where-logic-goes.mdx`: in the `IAlvoEventHandler` row replace the last cell with `available — untyped (\`Entity("name", e => e.OnCreated(…))\`); typed planned`.

`testing.mdx`: in *Deliver events when you say so*, replace "Never drain from inside a receiver: a round waits for the round it is in." with "Never drain from inside a receiver or a handler: the call is refused, because a round would wait for the round it is in."; add a bullet: "- **Event handlers run in the same rounds.** A round fans each event out to the handlers whose condition holds and runs the deliveries that are due; `Hooks.AssertRan` counts a handler by its id. A failed delivery waits for its retry, so pass a `FakeTimeProvider` and advance it to retry." In *What it is not* replace the last bullet with "- Write hooks and timers in C# arrive one release at a time; each joins the test host — and `Hooks.AssertRan` — as it ships."

- [ ] **Step 3: DocsGen scope**

`tools/MMLib.Alvo.DocsGen/CSharp/ApiScope.cs`, `Ports`: add `"AlvoEntityBuilder"`, `"AlvoEventContext"`, `"IAlvoEventHandler`1"`, `"IAlvoSession"`, `"IAlvoSessions"`. `test/MMLib.Alvo.DocsGen.Tests/CSharp/CSharpApiTests.cs` — add:

```csharp
    [Fact]
    public void The_event_handler_surface_is_in_scope()
    {
        var core = ApiScope.TypesOf(ShippedPackages.All.Single(p => p.Package == "MMLib.Alvo").Assembly).Select(type => type.Name).ToList();
        var abstractions = ApiScope.TypesOf(ShippedPackages.All.Single(p => p.Package == "MMLib.Alvo.Abstractions").Assembly)
            .Select(type => type.Name).ToList();

        core.ShouldContain("AlvoEntityBuilder");
        core.ShouldContain("AlvoEventContext");
        core.ShouldContain("IAlvoEventHandler`1");
        core.ShouldContain("AlvoExtensionBuilderExtensions");
        abstractions.ShouldContain("IAlvoSession");
    }
```

Run: `dotnet format whitespace MMLib.Alvo.slnx --include tools/MMLib.Alvo.DocsGen/CSharp/ApiScope.cs test/MMLib.Alvo.DocsGen.Tests/CSharp/CSharpApiTests.cs`, `dotnet test --project test/MMLib.Alvo.DocsGen.Tests` (PASS; a `RemarksFilterTests` failure means a public XML doc cites `§` or `#` — fix the doc), then `cd website && npm ci && npm run build` — build, link validation and DocsGen succeed; the C# reference lists the new types.

- [ ] **Step 4: Architecture docs**

`docs/architecture/extensibility.md`, rule 4 — after the `Apply{Thing}` bullet add:

```markdown
   - `Entity` / `Entities` — a **grouping verb** (HX-03, spec 2026-10-14 X14): it registers nothing itself; the
     builder it hands out does, one registration per call — `Entity("tickets", t => t.OnUpdated(…))`,
     `Entities(["orders", "invoices"], …)`, `Entity("*", …)`. The precedent is EF Core's `modelBuilder.Entity<T>(…)`.
     Every other registration of host code is an `Add{Thing}`: `AddEventHandler` and `AddRetiredExtension` from HX-03;
     `AddSchedule`, `AddFunction`, `AddCommand`, `AddDataMigration`, `AddRequiredSecret` as they ship.
```

and after *Registering a CEL function* add a section:

```markdown
## Registering an event handler

`Entity("tickets", t => t.OnUpdated("ticket-resolved", handler, when: "…"))` adds one internal `HandlerRegistration`
singleton — the `CelFunctionRegistration` precedent — and nothing else: the boot binds every registration to the primed
catalog (`ExtensionBinder`) and refuses all problems at once. The mechanism — fan-out, deliveries, lanes, retries — is
[events.md](events.md#event-handlers-and-deliveries); the guide is *React to changes (event handlers)* on the docs site.
```

`docs/architecture/events.md`:
- Add a section `## Event handlers and deliveries` before `## Chain depth`, with these paragraphs:
  - **Fan-out runs first.** For each claimed outbox entry, in id order, `DeliveryFanOut` judges every handler bound to the current catalog (`ExtensionBinder.For`) and inserts one `alvo_deliveries` row per match before the declarative actions run; `UNIQUE (event_id, subscriber)` makes a second fan-out a no-op. A `when` that throws is a `dead` `when-failed` row; a matching handler on an envelope whose `chaindepth + 1 > MaxDepth` is a `dead` `chain-depth-exceeded` row and an Error `ChainDepthExceeded`; a condition reading a caller value the envelope lacks matches nothing. A storage failure of the insert releases the entry and every later entry of the batch with the same partition key, unprocessed, with the same backoff. A failing action no longer touches deliveries already recorded.
  - **The tables.** `alvo_deliveries` and `alvo_delivery_attempts` (identical DDL, `SystemSchemaInitializer`, names from `AlvoFrameworkTables`); no body, URL or exception message is stored; the envelope stays in `alvo_outbox`, which a claim reads it from — so `IDeliveryStore` and `IOutboxStore` share one database.
  - **The claim.** One `UPDATE … RETURNING` with the outbox's EvalPlanQual-safe shape, restricted to the handlers this process binds, due (`pending`, `retrying` past `not_before`, or `running` past its lease), and — for an ordered handler — no earlier delivery of the same subscriber and partition key pending, running, retrying or dead, **and no earlier outbox entry of the same partition key still undispatched below `Alvo:Events:MaxAttempts`** (an index on the outbox's `(partition_key, id)` keeps that a seek). The second half is what keeps a record's order across outbox batches, a failed fan-out and a dispatcher that died between claiming an entry and fanning it out; it also means a descriptor action that keeps failing on event 1 holds the record's later handler deliveries back until it succeeds or reaches the outbox ceiling (HX-15 moves actions onto deliveries and removes that coupling). A same-key entry held back behind a failed fan-out is handed back with `IOutboxStore.HoldBackAsync`, which does not spend its attempt. The lease is the longest registered timeout + 30 s; the runner answers for an attempt within it (it waits a timed-out handler at most 30 s), so no second claim starts a delivery whose attempt is still unanswered. Completion and failure are fenced on `(claimed_by, attempts)`: a late finisher changes nothing and logs `DeliveryLeaseLost`. The claimant is per dispatcher instance.
  - **A delivery.** `DeliveryRunner` enters `AlvoFlow` (`Depth = chaindepth + 1`, `CausationId = event id`, no trace parent) — the first production caller of `AlvoFlow.Enter` — and runs the handler in its own DI scope, as `System(event tenant)` or the event's actor, under its timeout. Retries follow `AlvoRetryPolicy` ±20 % (default X5: dead after attempt 7); `AlvoChainDepthExceededException` is dead at once; a host stopping records nothing; a delivery reclaimed past its ceiling is dead `lease-expired` without running; an envelope gone from the outbox is dead `event-missing`.
  - **Worker model.** `DeliveryDispatcher` keeps at most `Alvo:Deliveries:MaxConcurrency` (16) deliveries running; in the manual mode it is the second step of a pump round, and a round called from inside a round is refused. The job pool for schedules and migrations arrives with them (HX-13).
  - **Boot.** Every registration is bound when the boot plan compiles the catalog; all problems refuse the start at once. `ExtensionStateGuard` then applies `AddRetiredExtension` and refuses a start over unfinished work of an id nobody registers.
  - **Observability.** `alvo.delivery {subscriber}` spans keep the ambient parent and **link** the envelope's `traceparent`; tags add `alvo.delivery.attempt`. Metrics on `MMLib.Alvo.Events`: `alvo.delivery.duration`, `alvo.delivery.attempts{alvo.outcome}`, `alvo.delivery.lag`, `alvo.delivery.dead{alvo.reason}`.
  - **Unbound handlers.** A registered handler that does not fit a catalog the boot plan did not bind (a runtime apply, `ApplyAlvoDescriptorAsync`, a dashboard-first host's first catalog) is logged `ExtensionUnbound` once and gets a dead `unbound` delivery for every event its source selects — never a silent drop, never a binding against a stale catalog.
  - **Inherited, stated.** Order is event-id order, which is per-record order across batches, failed fan-outs and dispatcher crashes; what remains of #150 is its id condition — two processes writing one record within one millisecond can interleave. A subscriber receives events fanned out after its registration on the fanning replica. E4 holds by construction: `PolicyCatalogProvider` primes one project per host, and the binder binds to that catalog only. A handler's `when` reading an `events.exclude` field through `changed()` is refused at boot (stricter than an after-hook's), so #398 cannot reach a handler; an envelope with no `data.changed` (#399) reads `changed()` as false for a handler exactly as for an after-hook, through the shared `EnvelopeReading`.
- In *Chain depth*, replace "HX-01 ships no production caller of `AlvoFlow.Enter` — HX-03 (deliveries) and HX-07 (the write unit) add them." with "`DeliveryRunner` (HX-03) is its first production caller — a handler writes one hop deeper than its event — and HX-07's write unit the second."
- In *Manual dispatch*, replace "HX-03's delivery dispatcher and HX-13's pumps become further steps of the same round." with "HX-03's delivery dispatcher is the second step of the same round, and HX-13's pumps follow." and "a subscriber must never drain" with "a round called from inside a round is refused".
- *Where the code lives*: add rows `MMLib.Alvo.Abstractions/Events/` `IDeliveryStore` and its records; `MMLib.Alvo.Abstractions/Data/` `IAlvoSession`; `MMLib.Alvo.Data.EntityFrameworkCore/Internal/` `DeliveryTable`, `EfCoreDeliveryStore`; `MMLib.Alvo/Events/Internal/` `EnvelopeReading`, `HandlerCondition`, `DeliveryFanOut`, `DeliveryRunner`, `DeliveryDispatcher`, `EventActor`, `HandlerMessage`, `DeliveryBackoff`, `AlvoDeliveryMetrics`, `DeliveryLog`; `MMLib.Alvo/Extensions/` the builder surface and `Internal/` registrations, `ExtensionBindingCompiler`, `ExtensionBinder`, `ExtensionStateGuard`; `MMLib.Alvo.Testing/Events/` `DeliveryStoreContractTests` + `IDeliveryStoreWorld`.

- [ ] **Step 5: CHANGELOG, PLAN, spec**

`CHANGELOG.md`, `## [Unreleased]` → `### Added`, first entry:

```markdown
- **Event handlers on a delivery engine** (HX-03, #380). An embedded host reacts to committed changes in C#:
  `Entity("tickets", t => t.OnUpdated("id", handler, when: "changed(status) && new.status == 'resolved'"))`, also
  `Entities([...])`, `Entity("*")`, `OnCreated`/`OnDeleted`/`OnSaved`, class handlers (`IAlvoEventHandler<T>`) and
  `AddEventHandler(id, "orders.approved", …)` for application events. Every registration is checked at start-up against
  the descriptor, all problems in one refusal. Each matching event becomes a row in the new `alvo_deliveries` table:
  at least once with the same `EventId`, retried 5 s → 6 h and dead after attempt 7, in order per record (a dead
  delivery holds that record back; `AlvoOrdering.None` opts out), fenced against late finishers, one hop deeper in the
  causation chain (loops are recorded dead with `chain-depth-exceeded`), run as the system in the event's tenant or as
  the event's actor, traced (`alvo.delivery`, linked to the envelope's trace) and metered. `AddRetiredExtension`
  retires an id; a start over another id's unfinished work is refused. `IAlvoSession` binds data access to one caller.
  `Alvo:Deliveries:MaxConcurrency` (16). Docs: *React to changes (event handlers)*.
```

and under `### Changed` (create the heading if absent):

```markdown
- **The `alvo.` event namespace is reserved** (HX-03). `IAlvoEvents.PublishAsync("alvo.…")` is refused, as
  `entity.…` already was: Alvo mints schedule runs and command completions there.
- **A database provider must supply `IDeliveryStore`** over the same database as `IOutboxStore` (HX-03); both shipped
  providers do. The outbox dispatcher fans each event out to its handler deliveries before running the descriptor's
  after-hook actions. Expect `IDeliveryStore` to grow before 1.0 (reads, job kinds, steps).
- **`IOutboxStore` gains `HoldBackAsync`** (HX-03): an entry held behind a failed earlier entry of its key is handed back
  without spending an attempt. A third-party implementation of the port must add it.
```

`docs/PLAN.md`, the *Host extensibility* line: replace "HX-02 (`MMLib.Alvo.TestHost` and the docs-snippet harness) the second." with "HX-02 (`MMLib.Alvo.TestHost` and the docs-snippet harness) the second; HX-03 (event handlers on the delivery engine) the third."

Spec amendment — `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`:
- After §4.8's *Operations (HX-06)* table add a paragraph **"HX-03 settled details."** with one bullet per ruling, verbatim from this plan's *Rulings and deviations* section below (items 1–31); in §4.8 *Fan-out* replace "later entries of the same key are released with it" with "later entries of the same key are held back without spending an attempt, and an ordered delivery is not claimable while an earlier entry of its key is undispatched below the outbox ceiling", and replace "Per-record order holds under the outbox's own condition: one outbox dispatcher and one emitting process per millisecond (#150 still applies)" with "Per-record order holds across batches, failed fan-outs and dispatcher crashes; #150's id condition still applies (two emitting processes within one millisecond)" — a recorded deviation from S:557-560, whose in-batch stop alone did not keep order (review B1).
- §5: in the HX-03 core/`MMLib.Alvo.Extensions` row remove `AlvoHookOptions` (it ships with its first user, HX-09 — add it to the HX-09 row); in the HX-03 core/Events row remove `EventCondition<T>`, `AlvoEventAttribute` and `AddEventHandler<TMessage,THandler>` from the DI row (typed layer — add them to the HX-05 row) and add `AddEventHandler<THandler>(id, eventType, …)` over `AlvoRecord`; in the `IDeliveryStore` row add "with `DeliveryRequest`, `DeliveryClaim`, `ClaimedDelivery`, `DeliveryAttempt`, `DeliveryFailure`; the EF implementation is internal".
- §7 *HX-03* scope: after "`DeliveryDispatcher` (pools, …)" add "(the handler pool; the job pool arrives with its first job kind, HX-13)".

- [ ] **Step 6: Issue notes**

```bash
gh issue comment 150 --repo Burgyn/MMLib.Alvo --body "HX-03 (#380): event-handler deliveries inherit this condition. A handler's lane order is event-id order, which is per-record order only with one outbox dispatcher and one emitting process per millisecond — the same condition the outbox states. Recorded in docs/architecture/events.md, *Event handlers and deliveries*."
gh issue comment 154 --repo Burgyn/MMLib.Alvo --body "HX-03 (#380) adds alvo_deliveries and alvo_delivery_attempts. Retention must cover both, and must keep an alvo_outbox row while any delivery of its event is pending, running or retrying: a claim reads the envelope from the outbox, and a delivery whose envelope is gone is dead-lettered as event-missing."
gh issue comment 398 --repo Burgyn/MMLib.Alvo --body "HX-03 (#380): handler conditions share the after-hook evaluation (EnvelopeReading.Holds), but cannot reach this defect — a handler's when that reads an events.exclude field, through changed() included, is refused at boot. Fixing it for after-hooks needs no handler change."
gh issue comment 399 --repo Burgyn/MMLib.Alvo --body "HX-03 (#380): handler conditions read data.changed through the same EnvelopeReading.Holds as after-hooks, so they inherit this behaviour and its fix."
```

- [ ] **Step 7: Commit**

```bash
git add website/src/content/docs/extending/event-handlers.mdx website/src/snippets/extending/handlers/ticket-desk.alvo.json website/src/snippets/extending/handlers/TicketDeskHandlers.cs website/src/snippets/extending/handlers/TicketDeskHandlersTests.cs website/astro.config.mjs website/src/content/docs/extending/index.mdx website/src/content/docs/extending/where-logic-goes.mdx website/src/content/docs/extending/testing.mdx tools/MMLib.Alvo.DocsGen/CSharp/ApiScope.cs test/MMLib.Alvo.DocsGen.Tests/CSharp/CSharpApiTests.cs docs/architecture/events.md docs/architecture/extensibility.md CHANGELOG.md docs/PLAN.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: react to changes with event handlers, and HX-03's record

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 15: Reviews, rings, pre-PR gates

- [ ] **Step 1: Security-core review** (spec §6.2 lists HX-03). Run the `alvo-security-core-review` checklist over the diff and record each answer in the PR report; label the PR `needs-deep-review`. The points it must cover:
  - *Cross-tenant*: a handler runs as `AlvoContext.System(envelope tenantid)` — never another tenant (pinned by `A_system_handler_cannot_read_another_tenant_s_row`, both engines); a global row's event has no tenant, so a system handler writing a scoped entity is refused by the tenant guard; `AlvoRunAs.EventActor` keeps only roles the descriptor still declares.
  - *Forgery*: `alvo.` joins the reserved namespaces, so a host cannot mint a framework event; `AddEventHandler` refuses `entity.`/`auth.`/`storage.` names.
  - *Loops*: the depth hop is set by the core only (`AlvoFlow.Enter`, arch facts in every granted assembly), fan-out refuses `chaindepth + 1 > MaxDepth` with no write involved, and a refused write is dead at once.
  - *Fail-closed conditions*: a `when` reading a caller value the envelope lacks matches nothing; one that throws is dead, never "selected"; an excluded field is refused at boot.
  - *Malformed envelopes*: a present but unreadable `tenantid` (or `authid` for an actor run) is dead `event-unreadable`, never a tenantless system or anonymous caller (`An_envelope_with_an_unreadable_tenant_is_dead_and_never_runs_as_a_tenantless_system`).
  - *No silent drops*: a handler a later catalog unbinds gets a dead `unbound` delivery per event it would select; a held-back outbox entry keeps its attempts; order holds across batches and crashes.
  - *Timeouts*: enforced by the runner; a handler ignoring its token cannot hold a pool slot or have its attempt answered twice.
  - *Data minimisation*: no body, URL or exception message in the tables, logs or span tags (`A_failure_waits_for_its_retry_and_records_the_exception_s_type_never_its_message`).
  - *Availability*: a dead delivery blocks only its own (handler, record) lane; a reclaimed poison delivery stops at its ceiling; a start over orphaned work is refused rather than silently stranding lanes.
- [ ] **Step 2: Rings** — `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (both chaos criteria, `PostgreSqlDeliveryStoreTests` with the race, `PostgreSqlEventHandlerTests`), then `cd website && npm run build`. Expected: green. Run `scripts/test-admin-e2e` once (every host now runs the delivery dispatcher and the state guard).
- [ ] **Step 3: Reviews** — freeze the tree (no edits while a reviewer reads it). `/code-review` and `/security-review` are user-only: dispatch a reviewer subagent over `git diff origin/main...HEAD` labelled "substitute for /code-review high" and one labelled "substitute for /security-review". Point them at: `DeliveryTable.ClaimSql` (outer `WHERE` repeats claimability; `NOT EXISTS` correlation resolves to the outer row on both engines), `EfCoreDeliveryStore.AnswerAsync` (write-first transaction, fence), `DeliveryRunner.InvokeAsync`'s catch order (shutdown vs enforced timeout vs wrapped depth refusal) and its answer-once fencing rule, `DeliveryTable.ClaimSql`'s outbox lane condition and the `HoldBackAsync` statement, `DeliveryDispatcher.PumpUntilStoppedAsync` (free slots, shutdown awaits), `EventPump`'s re-entrancy mark, and `OutboxDispatcher`'s per-key stop. Fix findings, re-run ring0.
- [ ] **Step 4: `alvo-plan-guard`** — dispatch last (read-only); then the `alvo-pr-report` skill builds the PR report (evidence: the chaos lines from `artifacts/criteria/events.md`, the PostgreSQL race, the public-API diffs against the public-surface line, the security checklist). Verify `git branch --show-current` is `hx/03-event-handlers` before pushing (memory *Subagents switch branches*); open the PR **against `main`** with the five-line pointer body carrying `Closes #380` on its own line and ending with the session URL.

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 14 Step 5) so a later reader can tell a decision from an oversight.

1. **Kill mid-delivery (2) is proven in process.** Host A's handler never returns, leaving a `running` delivery under an unanswered lease (read off the database); host B — a separate container over the same database, on a clock past the lease — is the restart. A SIGKILL child harness exists only for the standalone image, which cannot carry a C# handler. What this cannot catch is a defect in a real process exit (something on `ApplicationStopping` recording an attempt); `A_shutdown_mid_handler_records_nothing_and_leaves_the_claim` covers that half.
2. **(8)'s "write reaches depth 9"** is the delivery that would run at `chaindepth + 1 = 9`: fan-out records it `dead` `chain-depth-exceeded` and never runs it. A handler write refused by the driver (reachable with an explicitly deeper context) is dead at once, never retried — a separate fact — also when the refusal arrives wrapped as an inner exception.
3. **(15) is reachable in HX-03 through `AddRetiredExtension`,** whose id may follow any catalog grammar (a retired command or function name may hold `_`); handler ids cannot, so `a-b` beside a retired `a_b` is the live collision. The injective check runs over every id in the namespace; in HX-03 no event name is minted from an id yet, so it protects what HX-08 (command completions) and HX-13 (schedule runs) mint.
4. **A re-entrant drain is refused**, not documented: `EventPump` marks its execution flow, and a nested `DrainAsync` throws `InvalidOperationException` at once. Code that suppresses flow escapes the mark (stated).
5. **Delivery spans keep the ambient parent and link the envelope's trace;** the delivery flow carries no `TraceParent`, so a handler's writes are traced under its delivery span.
6. **Binding is strict at boot, visible after.** The pass runs where the boot plan compiles the catalog (`DescriptorBootPlan`, through `ExtensionBinder`), not inside the public `PolicyCatalog.Build`, which keeps its seven call sites. A code-first boot refuses to start; a stored-descriptor boot records the project `Failed` and stays up (I7). A catalog the boot plan did not bind — a runtime apply, `ApplyAlvoDescriptorAsync` (`SchemaMigrationRunner`), the first catalog of a dashboard-first host that booted `Awaiting` — binds what fits, never a stale catalog, logs `ExtensionUnbound` once, and records a **dead `unbound` delivery** for every event an unbound handler's source selects (I3): visible, replayable by HX-06, never a silent drop. HX-04's apply guard then refuses such an apply; until it does, acceptance (7) holds for a dashboard-first host only in that form.
7. **`changed()` on an excluded field is refused in a handler's `when`** (stricter than after-hooks), so #398 cannot reach handlers; #399 is inherited through the shared `EnvelopeReading` and noted on the issue.
8. **`OnSaved` carries only the post-image:** its `when` may not read `old.` or call `changed()` (the #325 rule); use `OnUpdated` for a condition on the change.
9. **A `"*"` or application-event handler's `when` tests only `@user`/`@tenant`** — compiled against a fieldless schema; an application event's payload declares no fields to check.
10. **`ExtensionOrderTie`** warns when two registrations on one entity+operation (or one event name) share an `Order` and at least one **stated** it; untouched defaults never tie. For handlers `Order` orders fan-out and listing only — deliveries run independently.
11. **The lease is per claim:** the longest registered timeout + 30 s, never shorter than any row's `timeout + 30 s`. The cost: crash recovery for a short handler waits out the longest handler's timeout.
12. **Pools:** the handler pool ships; the job pool (`MaxJobConcurrency`) arrives with its first job kind (HX-13) — a pool with nothing to run is configuration that does nothing.
13. **Deferred public types:** `AlvoHookOptions` → HX-09; `EventCondition<T>`, `AlvoEventAttribute`, `AddEventHandler<TMessage,THandler>` → HX-05. HX-03 ships the untyped `AddEventHandler(id, eventType, Delegate)` and `AddEventHandler<THandler>(id, eventType)` over `AlvoRecord`.
14. **`EfCoreDeliveryStore` is internal;** the port is the contract.
15. **A poison crash is bounded:** a delivery reclaimed past its policy's last attempt is dead `lease-expired` without running. The cost: a claim a graceful shutdown left behind also counts an attempt, so repeated rolling restarts can dead-letter a healthy delivery — bounded by the policy, and redeliverable in HX-06.
16. **Declarative after-hook actions do not enter `AlvoFlow` in HX-03** (they move onto deliveries in HX-15); the stub receiver's `SuppressFlow` and the R-12 loop fact stay.
17. **`AlvoRunAs.EventActor`** resolves the envelope's role names against the roles the descriptor still declares; an undeclared name is dropped, none left is `anon`.
18. **"Pending state" for the E3 boot check** is pending, running or retrying; a dead delivery is history and does not block a start. A handler re-using a retired id is a duplicate.
19. **`ON CONFLICT … DO NOTHING`** is SQLite's and PostgreSQL's spelling, like the outbox's `LIMIT`/`RETURNING`; Alvo's own tables on T-SQL belong to #92.
20. **Lane order is event-id order, kept across batches and crashes** (B1): an ordered delivery is not claimable while an earlier outbox entry of its key is undispatched below the outbox ceiling, so a failed or abandoned fan-out holds the record's later deliveries back; an entry past the ceiling stops blocking (it is lost to after-hooks as well — the outbox's own DLQ stand-in). The outbox claim is unchanged. A same-key entry held behind a failed fan-out is handed back with `HoldBackAsync`, **without spending an attempt** (I6). What remains of #150 is its id condition.
21. **An event that produced only dead deliveries** counts as `alvo.events.dispatched`, not `filtered`.
22. **`IAlvoSession` writes carry no idempotency key** (a handler deduplicates on `EventId`) and no batch verbs (HX-07's `IAlvoTransaction` has them).
23. **The port's records are `required init`, never positional** (I5), so HX-06/13/14 can add members without breaking a provider.
24. **A timeout is enforced** (I4): the runner awaits the handler with `WaitAsync(Timeout)`, cancels, waits at most 30 s, then answers — exactly once, inside the claim's lease, so no second claim runs the delivery while its attempt is unanswered. A handler that ignores its token is abandoned to the background (X15), may overlap its retry, and must be idempotent.
25. **A malformed `tenantid` (or, for `EventActor`, `authid`) fails closed** (M10): the delivery is dead `event-unreadable`, never run as a tenantless system or an anonymous caller.
26. **A manual round claims up to `Alvo:Events:BatchSize` deliveries** and runs at most `MaxConcurrency` at a time (M5), so the test host does not report a false loop on a backlog.
27. **E4 holds by construction** (M4): `PolicyCatalogProvider` primes one project per host, and handlers bind to that catalog only.
28. **`DeliveryRunFailed` logs the exception's type name, not the exception** (M9).
29. **A dead delivery blocks its (handler, record) lane until HX-06's redeliver** (M11); the docs give the manual remedy — set the row `cancelled`, or retire the handler.
30. **`EventActor` resolves roles by name against today's declaration** (M16): a role redefined since grants today's rules; a system-originated event runs its actor handler as `admin`.
31. **Rejected: a contract fact for fan-out atomicity** (M8, one item of four) — a partial insert cannot be forced through the port portably; the EF store's single multi-row statement is atomic by construction. The other three M8 facts ship.

---

## Acceptance criteria → tasks

| HX-03 criterion | Task(s) | Test |
|---|---|---|
| (1) contract suite, both engines: claim, lane order, re-fan-out no-op, `not_before`, dead at attempt 7, lease-expiry reclaim | 3, 4 | `DeliveryStoreContractTests` (21 facts, incl. the cross-batch lane facts) via `FakeDeliveryStoreTests`, `SqliteDeliveryStoreTests`, `PostgreSqlDeliveryStoreTests` (+ the two-claimant race) |
| (2) kill mid-delivery: runs again after restart with the same `EventId` | 12 | `A_handler_killed_mid_delivery_runs_again_with_the_same_event_and_the_late_finisher_changes_nothing` (both engines; ruling 1) |
| (3) a 10 000-event chaos run loses nothing | 13 | `DeliveryChaosCriteriaTests.Ten_thousand_events_reach_their_handler_in_order_and_none_is_lost` (SQLite + PostgreSQL, ring2) |
| (4) an unmatched `when` creates no delivery row | 8, 12 | `A_matching_handler_gets_one_pending_delivery_and_an_unmatched_one_none`; `An_unmatched_condition_records_no_delivery` |
| (5) event 1's fan-out fails once; event 2 of the same record is not delivered before it | 3, 4, 8, 12, 13 | in-batch `A_fan_out_that_fails_holds_back_the_later_events_of_its_record`; across batches and crashes `An_ordered_delivery_waits_for_an_earlier_event_of_its_record_that_is_not_fanned_out_yet` (contract, both engines), `A_record_s_events_reach_its_handler_in_order_when_the_first_fan_out_fails_alone_in_its_batch`, `A_record_s_events_wait_for_an_event_an_outbox_claimant_abandoned_before_its_fan_out` (both engines); per-record order over 10 000 events (chaos) |
| (6) a permanently failing webhook action does not keep the handler from its delivery | 8, 12 | `Fan_out_runs_first_and_a_failing_after_hook_action_keeps_the_handler_s_delivery`; `A_failing_webhook_action_does_not_keep_the_handler_from_its_delivery` |
| (7) a `when` typo refuses boot naming the id; all problems at once | 7 | `Every_problem_is_reported_at_once_and_none_of_their_handlers_binds`, `The_boot_is_refused_with_every_problem_and_its_fix`, `A_when_typo_refuses_the_boot_naming_the_id_beside_every_other_problem`; stored-descriptor path `Handlers_that_do_not_fit_a_stored_descriptor_stand_the_host_up_not_ready` (ruling 6) |
| (8) handler writing its own entity via `ctx.Data` and via injected `IAlvoData` + fresh `System(tenant)` → dead `chain-depth-exceeded`, both engines | 9, 12 | `The_handler_runs_one_hop_deeper_than_its_event_and_a_fresh_system_context_inherits_it`, `A_write_refused_for_depth_is_dead_at_once_and_never_retried`; the two `…stops_dead_at_the_depth_limit` facts (ruling 2) |
| (9) unknown id with pending deliveries refuses boot; `DeadLetter` dead-letters, `Discard` cancels | 11, 12 | `ExtensionStateGuardTests`; `Work_left_by_a_dropped_handler_refuses_the_start_until_it_is_retired_as_dead`, `A_discard_retirement_cancels_the_work_a_dropped_handler_left` |
| (10) equal `Order` logs `ExtensionOrderTie` | 7 | `Two_handlers_stating_one_order_on_one_operation_tie`, `A_tie_is_logged_at_boot_by_name` |
| (11) `PublishAsync("alvo.schedule.x")` is refused | 1 | `Publish_refuses_a_reserved_namespace` (alvo rows), `Publish_refuses_a_framework_event_naming_what_it_would_forge` |
| (12) a `System` handler cannot read another tenant's row | 9, 12 | `A_system_handler_acts_as_the_system_in_the_event_s_tenant`; `A_system_handler_cannot_read_another_tenant_s_row` (both engines) |
| (13) the delivery span links to the envelope's `traceparent` | 9, 12 | `The_delivery_span_links_the_envelope_trace_and_keeps_the_ambient_parent`; `A_handler_on_an_event_with_a_foreign_trace_still_counts_in_the_recorder` |
| (14) a `"*"` registration with a field-referencing `when` refuses boot | 7 | `A_star_registration_whose_condition_reads_a_field_is_refused`, `A_star_handler_whose_condition_reads_a_field_refuses_the_boot` |
| (15) a colliding derived event name (`a-b` vs `a_b`) refuses boot | 6, 7 | `Hyphens_and_underscores_derive_one_event_name_segment`, `Two_ids_deriving_one_event_name_segment_are_refused`, `Two_ids_deriving_one_event_name_refuse_the_boot` (ruling 3) |
| (16) a handler on custom event `X` publishing `X` stops at envelope depth 8 | 8, 12 | `An_event_at_the_depth_limit_fans_out_dead`; `A_handler_republishing_its_own_event_stops_dead_at_the_depth_limit` |
| (17) a throwing `when` on key A is dead `when-failed` and does not delay key B | 3, 8, 12 | `A_dead_delivery_blocks_its_own_lane_and_no_other`; `A_condition_that_throws_is_a_dead_when_failed_delivery`; `A_condition_that_throws_is_dead_and_delays_no_other_record` |
| (18) a reclaimed lease's late finisher changes nothing | 3, 9, 12 | `A_late_finisher_whose_lease_was_reclaimed_changes_nothing` (contract); `A_late_finisher_whose_lease_was_reclaimed_changes_nothing_and_says_so`; the kill fact |
| (19) an `Ordering.None` handler is not blocked by a dead earlier delivery | 3, 10, 12 | `An_unordered_subscriber_is_not_blocked_by_a_dead_earlier_delivery`; `An_unordered_handler_is_not_held_back_by_a_dead_earlier_delivery`; `A_dead_delivery_holds_back_its_record_only_for_an_ordered_handler` |
| Scope: `IAlvoSession` | 2 | `AlvoSessionsTests` |
| Scope: `alvo` reserved namespace (E12) | 1 | `EventPatternTests` |
| Scope: metrics and spans | 8, 9 | `DeliveryRunnerTests` (span), `AlvoDeliveryMetrics` exercised by every runner fact |
| Scope: `extensibility.md` rule 4 amended (X14) | 14 | doc review |
| Docs: *React to changes (event handlers)* | 14 | `TicketDeskHandlersTests` in `MMLib.Alvo.DocsSnippets.Tests`; website build |
| Dashboard | — | none in this PR (spec §7: HX-04 renders it) |
| Controller carries: link not parent; re-entrant drain; first `AlvoFlow` setter; #398/#399 | 9, 10, 14 | span facts; `A_handler_that_drains_is_refused_instead_of_hanging`; `The_scan_sees_the_core_reach_the_flow_setter`; issue notes |
