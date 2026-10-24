# HX-06 — Delivery operations — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An operator — a human on the dashboard's new **Operations** page, an agent over the Management API, or host code through the new port `IAlvoOperations` — can see every handler delivery (a keyset page, then one delivery with its attempt history), **redeliver** a dead one (which unblocks its lane), **replay** a window of finished deliveries (a dry run first: the exact count and the first 20 ids, changing nothing; the real run under an optional `Idempotency-Key`), **pause and resume** a handler (its deliveries accumulate and drain in order after resume), download a **fixture** of a delivery with every non-key scalar redacted, reproduce that delivery locally with `AlvoTestHost.DeliverAsync(fixture)`, and read **per-instance stats** (`scope: "this-instance"`, invocations, failures, rejections, p50/p95 over 24 h); every pause, resume, redeliver and replay writes exactly one `alvo_operations_log` row and one `ManagementAudit` log event (E11), and `PUT …/extensions/{id}/enabled` answers 409 for a handler.

**Architecture:** The public delivery port `IDeliveryStore` (unreleased — no tag carries it) gains the operator's members — keyset reads, an exact selection, a redeliver and a replay that put finished deliveries back to pending with a fresh attempt budget, the pause control and the operations log — each written in **one write-first transaction together with its log row**, identically on both engines (`DeliveryOperationsTable`), and held to new rows of `DeliveryStoreContractTests` by the EF store and the core's in-memory fake. The claim gains one `NOT EXISTS` over `alvo_subscriber_controls`, so a pause holds on every replica and survives a restart. A core `AlvoOperationsService` implements the new Abstractions port `IAlvoOperations` behind the same management gate as `IAlvoManagement` (nine new rows in the `ManagementOperations` level table), redacts fixtures against the current schema (`DeliveryFixtures`), answers stats from an in-process `MeterListener` over `alvo.delivery.duration` (`ExtensionStats`), and writes `ManagementAudit` after each committed change. The HTTP routes are thin delegates in `ManagementEndpoints.Operations.cs`. `IAlvoEventPump.DeliverAsync` (manual mode only) runs one handler once over a fixture exactly as the dispatcher would, which `AlvoTestHost.DeliverAsync` wraps. The dashboard reads the port through an `OperationsGateway` composed over `ManagementGateway`.

**Tech Stack:** .NET 10 (C# 14), ASP.NET Core minimal APIs, `System.Diagnostics.Metrics.MeterListener`, `System.Text.Json.Nodes`, xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, Verify + PublicApiGenerator, Testcontainers (PostgreSQL leg), Blazor (server-interactive) + MudBlazor, Microsoft.Playwright (admin e2e), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-06* (scope, docs, dashboard, acceptance (1)–(10)); §4.8 *Tables* (`alvo_subscriber_controls`), *Claim* ("no pause control"), *Operations (HX-06)* (the route/level/effect table) and *HX-03 settled details* (6) (unbound rows "replayable by HX-06"), (15) ("redeliverable in HX-06"), (19), (23), (29) (the by-hand retry this replaces), (34), (35), (36), (49); §3.3 E3 (pending state refuses boot), E10 (framework tables, identical DDL, `AlvoFrameworkTables`), E11 (audited = `ManagementAudit` + `alvo_operations_log`); §4.13 dashboard row *Operations page* and *HX-04 settled details* (1) (`Enabled`/`Paused` appended by the PR that first fills them), (13) (p95/24 h rejections need HX-06's stats); §4.14 (meters); §4.15 (`DeliverAsync(DeliveryFixture)` arrives in HX-06) and *HX-02 settled details* (the recorder, no `InternalsVisibleTo` into the TestHost); §5 row `06`; §6.1 (identical SQL, contract suites on both engines); §6.2 (HX-06 is **not** listed — see Global Constraints); §8.1 (#33 advanced, #154 retention). Prior art read: `draft-ietf-httpapi-idempotency-key-header` (key scoping, fingerprint, replay of the stored answer, reuse with a different payload refused), Standard Webhooks / Svix (resend one message; recover failed messages since a time — order not guaranteed), the transactional-outbox DLQ operations of MassTransit and Hangfire (requeue resets the retry count, keeps the history), and `System.Diagnostics.Metrics.MeterListener` (subscribe per instrument, `SetMeasurementEventCallback<T>`, process-wide).

## Global Constraints

- Branch `hx/06-delivery-operations` in the worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx06`, stacked on `hx/05-typed-layer` (PR #416, not yet merged). Issue **#383**, epic #370. One writer per worktree; never `git add -A` (stage each task's files by path); verify `git branch --show-current` prints `hx/06-delivery-operations` before every commit and before the push.
- **Scope is §7 HX-06 only.** No validate hooks and no `ManagementExtension.Enabled` (HX-09 — the enabled route ships and refuses every kind that exists); no schedules (HX-13 — the run route ships and answers 404); no steps in a delivery's detail (HX-14); no timers on the Operations page (HX-13); no assistant tool or skill change (the assistant stays read-only and gains nothing to operate with); no retention of the new log (#154, commented at merge).
- **Public surface added is exactly:**
  - Abstractions `MMLib.Alvo.Events`: `IDeliveryStore.ListAsync`, `.FindAsync`, `.AttemptsAsync`, `.SelectAsync`, `.RedeliverAsync`, `.ReplayAsync`, `.SetPausedAsync`, `.ControlsAsync`, `.FindOperationAsync` (abstract); records `StoredDelivery`, `StoredDeliveryAttempt`, `DeliveryFilter`, `DeliverySelection`, `DeliveryOperationEntry`, `SubscriberControl` (all `required init`, HX-03 (23)).
  - Abstractions `MMLib.Alvo.Management`: `IAlvoOperations` (nine members, below); records `ManagementDelivery`, `ManagementDeliveryAttempt`, `ManagementDeliveryQuery`, `ManagementDeliveryPage`, `ManagementReplayRequest`, `ManagementReplayPlan`, `ManagementExtensionStats`, `ManagementExtensionStat`, `DeliveryFixture`; exceptions `ManagementDeliveryNotFoundException`, `ManagementExtensionNotFoundException`, `ManagementConflictException`; `ManagementExtension.Paused` (init).
  - Core `MMLib.Alvo.Events`: `IAlvoEventPump.DeliverAsync(DeliveryFixture, CancellationToken)`.
  - Testing `MMLib.Alvo.Testing.Events`: the new contract facts on `DeliveryStoreContractTests` (made `partial`), `IDeliveryStoreWorld.OperationsAsync()`.
  - TestHost: `AlvoTestHost.DeliverAsync(DeliveryFixture, CancellationToken)`.
  - Admin: the routable `Operations` component (Razor components are public by construction, HX-04 (19)).
  Everything else is `internal`. Every addition §5 does not list is a ruling below.
- **Wire constants (verbatim):** route prefix `{m}/projects/{project}/`; statuses `pending`, `running`, `succeeded`, `retrying`, `dead`, `cancelled`; log operations `pause`, `resume`, `redeliver`, `replay`; stats scope `this-instance`, window 24 h; fixture placeholder `<redacted:FIELD>`; replay sample 20 ids; replay ceiling 10 000 deliveries per real run; page size default 50, at most 200; reason at most 500 characters, no control characters.
- **Storage:** both new tables are created by `IDeliveryStore.EnsureAsync` (as HX-03's are) with **identical DDL** on both engines, named through `AlvoFrameworkTables`; `ON CONFLICT … DO NOTHING`, `ON CONFLICT (…) DO UPDATE … WHERE`, `RETURNING` and partial indexes are SQLite's and PostgreSQL's shared spelling (HX-03 (19)). Every member that writes more than once opens its transaction **with a write** (the port's rule) and waits at `RelationalConnectionFactory.WriteGate` first (HX-03 (35)). No `ALTER` of any existing table.
- **SQL text scan.** `SqlTextConfinedToRendererArchitectureTests` scans `src/MMLib.Alvo/{Expressions,Rules,Events}`: the files this plan creates or edits there — `Events/IAlvoEventPump.cs`, `Events/Internal/EventPump.cs`, `Events/Internal/DeliveryDispatcher.cs`, `Events/Internal/DeliveryRunner.cs` — may not contain `SELECT `, `WHERE `, `ILIKE`, `COALESCE(`, `IS NOT NULL`, `CASE WHEN `, ` IN (` or the upper-case words `AND`, `OR`, `NOT` (code, strings or XML docs). All SQL lives in the EF driver.
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). `.cs` files are **UTF-8 with BOM + CRLF**: after creating or editing `.cs` files run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` (file paths, never a directory).
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Public-API baselines: `*PublicApiApprovalTests` (`test/MMLib.Alvo.Abstractions.Tests`, `test/MMLib.Alvo.Tests` for the core, `test/MMLib.Alvo.TestHost.Tests`, `test/MMLib.Alvo.Admin.Tests`); the Testing library's is `*TestingLibraryPublicApiTests` (`test/MMLib.Alvo.Tests`). Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR.
- A grown `PublicApi.*.verified.txt` or a new `*.verified.*` trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added public symbol against `alvo-architecture-rules` (*public is the contract*) and the public-surface list above.
- **CI parity:** rings are Debug on the local SDK; CI builds Release with `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`. Task 19 runs the Release build in the CI SDK container before anything is pushed.
- **Security core:** §6.2's list (HX-01, 03, 05, 07, 08, 09, 11, 12, 13, 15, 16, 18, 19) does **not** name HX-06. This plan nevertheless adds nine rows to the management authorization table, a second gated port, and a payload-disclosure surface (the fixture), and the `alvo-security-core-review` skill triggers on authorization changes — so Task 19 runs that checklist (the authorization and disclosure items), a labelled `/security-review` substitute, and labels the PR `needs-deep-review` (Ruling 20).
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes` keyword in a commit message; the PR body carries `Closes #383` once.

## Review Focus

1. **A delivery that died at its last attempt, redelivered,** must run again with a fresh attempt budget — not be reclaimed dead `lease-expired` at once (`DeliveryRunner.Admit`, HX-03 (15)/(29)) — and its history must keep the earlier attempts, numbered on, without a primary-key collision on `(delivery_id, attempt)` — contract fact `A_redelivered_delivery_runs_again_with_a_fresh_budget_and_keeps_its_history` (Task 3) and SQLite fact `An_attempt_after_a_reset_is_numbered_after_the_last_one` (Task 1).
2. **A replay whose window also holds another handler's rows, or rows still pending/running/retrying,** must touch only the named subscriber's finished rows — contract fact `A_replay_resets_only_the_named_subscriber_s_finished_deliveries` (Task 3) and service fact `A_replay_of_an_unfinished_status_is_refused` (Task 11).
3. **A redeliver or replay for a handler this host does not register** (renamed or removed since) must be refused 409/404 rather than leave a pending row that refuses the next start (E3) — `Redelivering_for_a_handler_this_host_does_not_register_is_refused` (Task 10) and `A_replay_names_a_handler_this_host_registers` (Task 11).
4. **A retried replay — same `Idempotency-Key`, same body — after the first committed** must answer the first's count and ids with `replayed: true` and reset nothing again; the same key with a different body is 409 `idempotency-conflict` — `A_retried_replay_answers_the_first_and_resets_nothing` and `A_key_spent_on_a_different_replay_is_a_conflict` (Task 11), contract `A_replay_under_a_spent_key_changes_nothing` (Task 3), HTTP `A_replay_reads_its_idempotency_key_from_the_header` (Task 13).
5. **A fixture of an application event, or of an entity the current schema no longer declares,** must redact every scalar but `id` and never echo a value — rows `an application event keeps only id` and `an entity no longer declared keeps only id` in `DeliveryFixturesTests` (Task 7).

---

## File structure

Created:

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs` | the port's operator records: `StoredDelivery`, `StoredDeliveryAttempt`, `DeliveryFilter`, `DeliverySelection`, `DeliveryOperationEntry`, `SubscriberControl` |
| `src/MMLib.Alvo.Abstractions/Management/IAlvoOperations.cs` | the operations port |
| `src/MMLib.Alvo.Abstractions/Management/ManagementOperationsModels.cs` | the wire records of the port |
| `src/MMLib.Alvo.Abstractions/Management/DeliveryFixture.cs` | the redacted fixture |
| `src/MMLib.Alvo.Abstractions/Management/ManagementDeliveryNotFoundException.cs`, `ManagementExtensionNotFoundException.cs`, `ManagementConflictException.cs` | the three refusals the port adds |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs` | DDL of the two new tables and every operator statement |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs` | the EF store's operator members (partial) |
| `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs` | the operator contract rows (partial) |
| `src/MMLib.Alvo/Management/Internal/ManagementGate.cs` | the level gate and the project check, shared by the operations service |
| `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.cs`, `AlvoOperationsService.Writes.cs`, `AlvoOperationsService.Replay.cs` | `IAlvoOperations` (reads; redeliver, pause and the refusing members; replay) |
| `src/MMLib.Alvo/Management/Internal/DeliveryViews.cs`, `DeliveryQueries.cs`, `OperationReasons.cs`, `OperationsAudit.cs` | wire mapping, query validation, reason rules, the `ManagementAudit` log event |
| `src/MMLib.Alvo/Management/Internal/DeliveryFixtures.cs` | redaction against the schema |
| `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.Operations.cs`, `ManagementOperationsBodies.cs` | the nine routes and their request bodies |
| `src/MMLib.Alvo/Extensions/Internal/ExtensionStats.cs`, `LatencyHistogram.cs` | the `MeterListener` and its hourly buckets |
| `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs` | DDL and statement shape |
| `test/MMLib.Alvo.Tests/Management/OperationsTestWorld.cs`, `ManagementGateTests.cs`, `OperationsLevelTests.cs`, `DeliveryFixturesTests.cs`, `AlvoOperationsServiceTests.cs`, `AlvoOperationsWritesTests.cs`, `AlvoOperationsReplayTests.cs`, `PausedCatalogTests.cs`; `test/MMLib.Alvo.Abstractions.Tests/Management/ManagementOperationsModelsTests.cs`, `test/MMLib.Alvo.Abstractions.Tests/AlvoEventIdTests.cs` | the in-memory world, gate, level rows, models, redaction (acceptance (5)), service reads and writes (acceptance (9) in-process), the paused catalog |
| `test/MMLib.Alvo.Tests/Extensions/LatencyHistogramTests.cs`, `ExtensionStatsTests.cs` | percentiles, buckets, the listener (acceptance (8) unit) |
| `test/MMLib.Alvo.Api.Tests/Management/OperationsRouteTests.cs`, `test/MMLib.Alvo.Api.Tests/descriptors/operated-notes.alvo.json` | over HTTP: acceptance (4), (8), (9), the idempotency header |
| `test/_shared/testhost/DeliveryOperationsFacts.cs`, `test/_shared/testhost/AuditCapture.cs`, `test/_shared/testhost/descriptors/ops-desk.alvo.json`, `test/MMLib.Alvo.TestHost.Tests/SqliteDeliveryOperationsTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryOperationsTests.cs` | both engines: acceptance (1), (2), (3), (6), (7) |
| `test/MMLib.Alvo.TestHost.Tests/AlvoTestHostDeliverTests.cs` | `DeliverAsync` (acceptance (6) unit), stats over a real host (acceptance (8)) |
| `src/MMLib.Alvo.Admin/Internal/OperationsGateway.cs`, `src/MMLib.Alvo.Admin/Components/Operations/OperationsView.cs`, `Operations.razor`, `Operations.razor.cs` | the dashboard slice |
| `test/MMLib.Alvo.Admin.Tests/Operations/OperationsViewTests.cs`, `OperationsGatewayTests.cs`; `test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsWorld.cs`, `OperationsScenarios.cs` | its logic and acceptance (10) |
| `website/src/content/docs/extending/operate-deliveries.mdx`; `website/src/snippets/extending/operations/{RepairDeskHandlers.cs,RepairDeskOperationsTests.cs,repair-desk.alvo.json}` | *Operate deliveries* |

Modified: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs`, `Events/IDeliveryStore.cs`, `Events/AlvoEventId.cs`, `Management/ManagementExtension.cs`; `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/{DeliveryTable.cs,EfCoreDeliveryStore.cs}`; `src/MMLib.Alvo.Testing/Events/{DeliveryStoreContractTests.cs,IDeliveryStoreWorld.cs}`; `src/MMLib.Alvo/Management/Access/{ManagementOperation.cs,ManagementOperations.cs}`, `Management/Setup.cs`, `Management/Internal/{AlvoManagementService.cs,ManagementEndpoints.cs}`, `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`, `src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs`, `src/MMLib.Alvo/Extensions/Setup.cs`; `src/MMLib.Alvo/Events/{IAlvoEventPump.cs,Internal/EventPump.cs,Internal/DeliveryDispatcher.cs,Internal/DeliveryRunner.cs}`; `src/MMLib.Alvo.TestHost/AlvoTestHost.cs`; `src/MMLib.Alvo.Admin/{AlvoAdminServiceCollectionExtensions.cs,Internal/ManagementGateway.cs,Internal/AdminProblem.cs,Internal/AdminPaths.cs,Components/Shell/AdminSection.cs,Components/DesignSystem/Icons.cs}`; tests `test/MMLib.Alvo.Tests/Events/{FakeDeliveryStore.cs,FakeDeliveryStoreTests.cs}`, `test/_shared/ef/DeliveryStoreWorld.cs`, `test/_shared/testhost/FailingFirstFanOut.cs`, `test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs`, `test/MMLib.Alvo.Data.Sqlite.Tests/{AddAlvoIntegrationTests.cs,SqliteDeliveryStoreTests.cs}`, `test/MMLib.Alvo.Api.Tests/Management/ManagementContractTests.cs`, `test/_shared/api/AlvoApiWorld.cs`; every `PublicApi.*.verified.txt` named above; docs per Task 18.

## Task 1: The operator's two tables, reserved, and attempts numbered over a delivery's whole life

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs` (`AttemptSql`), `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs` (`partial`, the two table names, `EnsureAsync`, `InsertAttemptAsync`), `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs`
- Modify (tests): `test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs`, `test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs`, `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDeliveryStoreTests.cs`, `test/_shared/ef/DeliveryStoreWorld.cs`
- Create (test): `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs`

**Interfaces:**
- Consumes: HX-03's `DeliveryTable`, `EfCoreDeliveryStore`, `SystemSchemaInitializer.CreateIfMissingAsync`/`CreateIndexIfMissingAsync`, `RelationalSqlBatch.ExecuteUntransactedAsync`.
- Produces: `AlvoFrameworkTables.SubscriberControlsSuffix` (`"_subscriber_controls"`), `AlvoFrameworkTables.OperationsLogSuffix` (`"_operations_log"`); `internal static class DeliveryOperationsTable` with `ControlsNameFor(string)`, `LogNameFor(string)`, `ControlsDdl(string)`, `LogDdl(string)`, `LogKeyIndexDdl(string)`, `Statements(string controls, string log)`; `EfCoreDeliveryStore` is `internal sealed partial class` with fields `_controls` and `_log`; `DeliveryStoreWorld.ResetAsync(Guid)` and `DeliveryStoreWorld.AttemptNumbersAsync(Guid)`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs` — replace the first fact and extend the second:

```csharp
    [Fact]
    public void The_reserved_set_is_the_eight_bookkeeping_tables_plus_the_seven_identity_ones()
    {
        AlvoFrameworkTables.NamesFor("alvo").Count.ShouldBe(15);
        AlvoFrameworkTables.IdentitySuffixes.Count.ShouldBe(7);
    }

    [Fact]
    public void The_delivery_tables_are_reserved()
    {
        var names = AlvoFrameworkTables.NamesFor("alvo");

        names.ShouldContain("alvo_deliveries");
        names.ShouldContain("alvo_delivery_attempts");
        names.ShouldContain("alvo_subscriber_controls");
        names.ShouldContain("alvo_operations_log");
    }
```

Create `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs`:

```csharp
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The operator's two tables (spec 2026-10-14 §4.8 <c>alvo_subscriber_controls</c>, E11 <c>alvo_operations_log</c>): named
/// from the framework table list, created with the spec's columns, and the log's idempotency key unique only when present.
/// </summary>
public sealed class DeliveryOperationsTableTests
{
    private const string Controls = "alvo_subscriber_controls";
    private const string Log = "alvo_operations_log";

    [Fact]
    public void The_tables_are_named_from_the_framework_table_list()
    {
        DeliveryOperationsTable.ControlsNameFor("alvo").ShouldBe(Controls);
        DeliveryOperationsTable.LogNameFor("alvo").ShouldBe(Log);
        SystemSchemaInitializer.FrameworkTableNames("alvo").ShouldContain(Controls);
        SystemSchemaInitializer.FrameworkTableNames("alvo").ShouldContain(Log);
    }

    [Fact]
    public void The_control_table_carries_the_spec_s_columns_keyed_by_subscriber()
    {
        var ddl = DeliveryOperationsTable.ControlsDdl(Controls);

        ddl.ShouldContain("subscriber TEXT NOT NULL PRIMARY KEY");
        foreach (var column in new[] { "paused_at", "paused_by", "reason", "disabled_at", "disabled_by" })
        {
            ddl.ShouldContain($"{column} TEXT NULL");
        }
    }

    [Fact]
    public void The_log_carries_e11_s_columns_and_the_replay_s_record()
    {
        var ddl = DeliveryOperationsTable.LogDdl(Log);

        foreach (var column in new[] { "id TEXT NOT NULL PRIMARY KEY", "at TEXT NOT NULL", "actor TEXT NOT NULL", "operation TEXT NOT NULL", "target TEXT NOT NULL", "reason TEXT NULL" })
        {
            ddl.ShouldContain(column);
        }

        foreach (var column in new[] { "affected INTEGER NULL", "sample TEXT NULL", "idempotency_scope TEXT NULL", "idempotency_key TEXT NULL", "request_fingerprint TEXT NULL" })
        {
            ddl.ShouldContain(column);
        }
    }

    [Fact]
    public void A_key_is_unique_per_caller_only_when_one_was_sent() =>
        DeliveryOperationsTable.LogKeyIndexDdl(Log).ShouldBe(
            $"CREATE UNIQUE INDEX IF NOT EXISTS ux_{Log}_key ON {Log} (idempotency_scope, idempotency_key) WHERE idempotency_key IS NOT NULL");

    [Fact]
    public void Bringing_the_tables_up_is_idempotent() =>
        DeliveryOperationsTable.Statements(Controls, Log).ShouldAllBe(statement => statement.Contains("IF NOT EXISTS"));
}
```

Add to `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs`:

```csharp
    /// <summary>
    /// An attempt is numbered after the delivery's last recorded one, not by the row's claim count: a redelivered row
    /// restarts its count at one while its history keeps every earlier attempt (spec 2026-10-14 HX-06, Ruling 2).
    /// </summary>
    [Fact]
    public void An_attempt_is_numbered_after_the_delivery_s_last_recorded_one()
    {
        var sql = DeliveryTable.AttemptSql("alvo_delivery_attempts");

        sql.ShouldContain("COALESCE(MAX(attempt), 0) + 1");
        sql.ShouldNotContain("@attempts");
    }
```

Add to `test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs`, beside the two delivery-table facts:

```csharp
    /// <summary>The pause control survives introspection like every framework table (HX-06).</summary>
    [Fact]
    public Task A_framework_bookkeeping_table_is_never_planned_for_a_drop_subscriber_controls() =>
        AFrameworkTableSurvivesIntrospectionAsync("alvo_subscriber_controls");

    /// <summary>The operations log survives introspection like every framework table (HX-06, E11).</summary>
    [Fact]
    public Task A_framework_bookkeeping_table_is_never_planned_for_a_drop_operations_log() =>
        AFrameworkTableSurvivesIntrospectionAsync("alvo_operations_log");
```

Add to `test/_shared/ef/DeliveryStoreWorld.cs` a field `private readonly string _attempts = DeliveryTable.AttemptsNameFor(SchemaPrefix);` beside `_table`, and these two members after `MarkDispatchedAsync`:

```csharp
    /// <summary>Puts a delivery back to pending with its claim count at zero, as an operator's redeliver does.</summary>
    internal async Task ResetAsync(Guid delivery)
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            await ExecuteAsync(
                connection,
                $"UPDATE {_table} SET status = 'pending', attempts = @value, claimed_by = NULL, lease_until = NULL WHERE id = @id",
                delivery,
                0);
        }
    }

    /// <summary>The attempt numbers the history holds for one delivery, ascending.</summary>
    internal async Task<IReadOnlyList<int>> AttemptNumbersAsync(Guid delivery)
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);

            return await RelationalSqlBatch.ReadRowsAsync(
                connection,
                transaction: null,
                $"SELECT attempt FROM {_attempts} WHERE delivery_id = @id ORDER BY attempt",
                command => RelationalSqlBatch.AddParameter(command, "@id", delivery.ToString()),
                reader => Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Ct);
        }
    }
```

Add to `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDeliveryStoreTests.cs`:

```csharp
    /// <summary>
    /// A delivery put back to pending restarts its claim count at one, and the attempt that follows is recorded after the
    /// ones already in its history — not as attempt 1 again, which the history's primary key would refuse.
    /// </summary>
    [Fact]
    public async Task An_attempt_after_a_reset_is_numbered_after_the_last_one()
    {
        await using var world = await DeliveryStoreWorld.StartAsync(Connect);
        var @event = await world.AppendEventAsync("tickets:a");
        await world.Store.FanOutAsync([new DeliveryRequest { EventId = @event, Subscriber = Subscriber, PartitionKey = "tickets:a", Kind = "handler" }], Ct);
        var first = (await world.Store.ClaimAsync(ClaimFor("worker-1"), Ct)).ShouldHaveSingleItem();
        (await world.Store.FailAsync(AttemptOf(first, "worker-1"), new DeliveryFailure { ErrorType = "Boom", MaxAttempts = 1 }, Ct)).ShouldBeTrue();

        await world.ResetAsync(first.Id);
        var second = (await world.Store.ClaimAsync(ClaimFor("worker-1"), Ct)).ShouldHaveSingleItem();

        second.Attempts.ShouldBe(1, "the claim count restarts with the reset");
        (await world.Store.CompleteAsync(AttemptOf(second, "worker-1"), Ct)).ShouldBeTrue();
        (await world.AttemptNumbersAsync(first.Id)).ShouldBe([1, 2]);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*DeliveryOperationsTableTests'`
Expected: build FAIL — `DeliveryOperationsTable` does not exist.
Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteDeliveryStoreTests'`
Expected: `An_attempt_after_a_reset_is_numbered_after_the_last_one` FAILS with `SqliteException` "UNIQUE constraint failed: alvo_delivery_attempts.delivery_id, alvo_delivery_attempts.attempt".

- [ ] **Step 3: Reserve the names**

In `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs`, after `DeliveryAttemptsSuffix`:

```csharp
    /// <summary>The suffix of the table holding one row per subscriber an operator has paused.</summary>
    internal const string SubscriberControlsSuffix = "_subscriber_controls";

    /// <summary>The suffix of the table holding one row per audited delivery operation.</summary>
    internal const string OperationsLogSuffix = "_operations_log";
```

and in `NamesFor`, after `schemaPrefix + DeliveryAttemptsSuffix,`:

```csharp
            schemaPrefix + SubscriberControlsSuffix,
            schemaPrefix + OperationsLogSuffix,
```

- [ ] **Step 4: The DDL**

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs`:

```csharp
namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The operator's two tables — the pause control and the operations log — and, from the next task on, every statement the
/// operator's members of <c>IDeliveryStore</c> run; identical on both shipped engines, as the delivery tables' are.
/// </summary>
/// <remarks>
/// <para>
/// <b>The control table carries the spec's whole row now</b>, <c>disabled_at</c>/<c>disabled_by</c> included, although
/// nothing writes them until validate hooks can be switched off: a column added later is an <c>ALTER</c> of a table every
/// running database already holds, which the framework's tables never take (spec E10).
/// </para>
/// <para>
/// <b>The log carries the replay's idempotency record.</b> Beside E11's six columns, a replay files how many deliveries it
/// reset, the first ids, and the caller's key, scope and request fingerprint in the same row, written in the same
/// transaction as the reset — so a retried replay is answered from the row, and a crash cannot leave a reset without its
/// record. The key is unique per caller only when one was sent (a partial index).
/// </para>
/// </remarks>
internal static class DeliveryOperationsTable
{
    internal static string ControlsNameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.SubscriberControlsSuffix;

    internal static string LogNameFor(string schemaPrefix) => schemaPrefix + AlvoFrameworkTables.OperationsLogSuffix;

    internal static string ControlsDdl(string controls) =>
        $"""
        CREATE TABLE IF NOT EXISTS {controls} (
            subscriber TEXT NOT NULL PRIMARY KEY,
            paused_at TEXT NULL,
            paused_by TEXT NULL,
            reason TEXT NULL,
            disabled_at TEXT NULL,
            disabled_by TEXT NULL
        )
        """;

    internal static string LogDdl(string log) =>
        $"""
        CREATE TABLE IF NOT EXISTS {log} (
            id TEXT NOT NULL PRIMARY KEY,
            at TEXT NOT NULL,
            actor TEXT NOT NULL,
            operation TEXT NOT NULL,
            target TEXT NOT NULL,
            reason TEXT NULL,
            affected INTEGER NULL,
            sample TEXT NULL,
            idempotency_scope TEXT NULL,
            idempotency_key TEXT NULL,
            request_fingerprint TEXT NULL
        )
        """;

    internal static string LogKeyIndexDdl(string log) =>
        $"CREATE UNIQUE INDEX IF NOT EXISTS ux_{log}_key ON {log} (idempotency_scope, idempotency_key) WHERE idempotency_key IS NOT NULL";

    internal static IReadOnlyList<string> Statements(string controls, string log) =>
        [ControlsDdl(controls), LogDdl(log), LogKeyIndexDdl(log)];
}
```

- [ ] **Step 5: Number attempts over a delivery's life; bring the tables up**

In `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs` replace `AttemptSql`:

```csharp
    internal static string AttemptSql(string attempts) =>
        $"""
        INSERT INTO {attempts} (delivery_id, attempt, started_at, duration_ms, outcome, error_type)
        SELECT @id, COALESCE(MAX(attempt), 0) + 1, @started_at, @duration_ms, @outcome, @error_type FROM {attempts} WHERE delivery_id = @id
        """;
```

and extend the class remarks with a paragraph: `<b>An attempt is numbered after the delivery's last recorded one.</b> The row's <c>attempts</c> counts claims since the delivery was last put back to pending — what the retry policy and the attempt ceiling read — while the history numbers every attempt the delivery ever had, so a redelivered row neither collides with its own history nor loses it.`

In `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs`: declare `internal sealed partial class EfCoreDeliveryStore : IDeliveryStore`; add fields `private readonly string _controls;` and `private readonly string _log;`, assigned in the constructor from `DeliveryOperationsTable.ControlsNameFor(options.SchemaPrefix)` and `DeliveryOperationsTable.LogNameFor(options.SchemaPrefix)`; in `EnsureAsync`, after `DeliveryTable.EnsureAsync(...)`:

```csharp
                await RelationalSqlBatch.ExecuteAsync(
                    connection, DeliveryOperationsTable.Statements(_controls, _log), transaction: null, cancellationToken).ConfigureAwait(false);
```

and in `InsertAttemptAsync` delete the line binding `@attempts` (the statement no longer reads it).

In `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs`: add fields `_subscriberControlsTableName` and `_operationsLogTableName` assigned from `DeliveryOperationsTable.ControlsNameFor(schemaPrefix)` / `LogNameFor(schemaPrefix)`, and at the end of `EnsureAsync`:

```csharp
        await CreateIfMissingAsync(
            _subscriberControlsTableName, DeliveryOperationsTable.ControlsDdl(_subscriberControlsTableName), ct).ConfigureAwait(false);
        await CreateIfMissingAsync(
            _operationsLogTableName, DeliveryOperationsTable.LogDdl(_operationsLogTableName), ct).ConfigureAwait(false);
        await CreateIndexIfMissingAsync(DeliveryOperationsTable.LogKeyIndexDdl(_operationsLogTableName), ct).ConfigureAwait(false);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*DeliveryOperationsTableTests'`, then `--filter-class '*DeliveryTableTests'`; `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteDeliveryStoreTests'` and `--filter-class '*AddAlvoIntegrationTests'`; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FrameworkTableReservationTests'`.
Expected: PASS. (`DescriptorValidatorTests` reserves names through `NamesFor`, so an entity called `alvo_operations_log` is refused with no further change — run `--filter-class '*DescriptorValidatorTests'` to confirm.)

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDeliveryStoreTests.cs test/_shared/ef/DeliveryStoreWorld.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/SystemSchemaInitializer.cs test/MMLib.Alvo.Tests/Schema/FrameworkTableReservationTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs test/MMLib.Alvo.Data.Sqlite.Tests/AddAlvoIntegrationTests.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteDeliveryStoreTests.cs test/_shared/ef/DeliveryStoreWorld.cs
git commit -m "feat(events): reserve the pause control and operations log, and number attempts over a delivery's life

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: The port's reads — a keyset page, one delivery with its history, an exact selection

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs`, `src/MMLib.Alvo.Abstractions/Events/AlvoEventId.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs`
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs`
- Modify: `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.cs` (`partial`)
- Create: `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs`
- Modify (tests): `test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs`, `test/_shared/testhost/FailingFirstFanOut.cs`, `test/MMLib.Alvo.Abstractions.Tests/AlvoEventIdTests.cs` (create if absent), `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`, `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Consumes: Task 1's `EfCoreDeliveryStore` (partial), `DeliveryOperationsTable`.
- Produces (public, Abstractions `MMLib.Alvo.Events`):
  - `StoredDelivery { required Guid Id; required Guid EventId; required string Subscriber; required string PartitionKey; string? TenantId; required string Kind; required string Status; required int Attempts; required DateTimeOffset NotBefore; DateTimeOffset? CompletedAt; string? LastErrorType; string? Payload }`
  - `StoredDeliveryAttempt { required int Attempt; required DateTimeOffset StartedAt; required TimeSpan Duration; required string Outcome; string? ErrorType }`
  - `DeliveryFilter { string? Subscriber; string? Status; Guid? FromEventId; Guid? ToEventId; Guid? After; required int Limit }`
  - `DeliverySelection { required int Count; required IReadOnlyList<Guid> Sample }`
  - `IDeliveryStore.ListAsync(DeliveryFilter, CancellationToken) → Task<IReadOnlyList<StoredDelivery>>` (newest first by delivery id, strictly older than `After`, at most `Limit`, `Payload` null); `.FindAsync(Guid, CancellationToken) → Task<StoredDelivery?>` (with `Payload`); `.AttemptsAsync(Guid, CancellationToken) → Task<IReadOnlyList<StoredDeliveryAttempt>>` (ascending); `.SelectAsync(DeliveryFilter, CancellationToken) → Task<DeliverySelection>` (exact count, first `Limit` ids in event order, `After` ignored). A `Limit` of zero or less is refused with `ArgumentOutOfRangeException`.
  - internal `AlvoEventId.Floor(DateTimeOffset instant) → Guid` (the smallest event id minted in `instant`'s millisecond).
  - `FakeDeliveryStore.Row.Kind` (init, default `"handler"`); `FakeDeliveryStore` keeps a per-attempt history (`_history`).

- [ ] **Step 1: Write the failing contract rows**

In `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.cs` change the declaration to `public abstract partial class DeliveryStoreContractTests`.

Create `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs`:

```csharp
using MMLib.Alvo.Events;

using Shouldly;

using Xunit;

namespace MMLib.Alvo.Testing.Events;

/// <summary>The operator's half of the delivery protocol: reads, redeliver, replay, pause and the operations log.</summary>
public abstract partial class DeliveryStoreContractTests
{
    /// <summary>A page lists deliveries newest first and continues strictly after the last id it returned.</summary>
    [Fact]
    public async Task A_page_lists_deliveries_newest_first_and_continues_after_the_last_one()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var events = new List<Guid>();
        foreach (var key in new[] { KeyA, KeyB, "tickets:c" })
        {
            var @event = await world.AppendEventAsync(key);
            await world.Store.FanOutAsync([Request(@event, Confirm, key)], Ct);
            events.Add(@event);
        }

        var first = await world.Store.ListAsync(new DeliveryFilter { Limit = 2 }, Ct);
        var rest = await world.Store.ListAsync(new DeliveryFilter { Limit = 2, After = first[^1].Id }, Ct);

        first.Select(delivery => delivery.EventId).ShouldBe([events[2], events[1]]);
        rest.ShouldHaveSingleItem().EventId.ShouldBe(events[0]);
        first.ShouldAllBe(delivery => delivery.Payload == null && delivery.Status == "pending" && delivery.Kind == Kind);
    }

    /// <summary>A page filters by subscriber, by status and by a half-open window of event ids.</summary>
    [Fact]
    public async Task A_page_filters_by_subscriber_status_and_event_window()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var early = await world.AppendEventAsync(KeyA);
        var middle = await world.AppendEventAsync(KeyB);
        var late = await world.AppendEventAsync("tickets:c");
        await world.Store.FanOutAsync([Request(early, Confirm, KeyA) with { DeadReason = "when-failed" }, Request(early, Audit, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(middle, Confirm, KeyB), Request(middle, Audit, KeyB)], Ct);
        await world.Store.FanOutAsync([Request(late, Confirm, "tickets:c")], Ct);

        (await world.Store.ListAsync(new DeliveryFilter { Subscriber = Audit, Limit = 10 }, Ct)).Count.ShouldBe(2);
        (await world.Store.ListAsync(new DeliveryFilter { Status = "dead", Limit = 10 }, Ct)).ShouldHaveSingleItem().EventId.ShouldBe(early);
        (await world.Store.ListAsync(new DeliveryFilter { Subscriber = Confirm, FromEventId = middle, ToEventId = late, Limit = 10 }, Ct))
            .ShouldHaveSingleItem().EventId.ShouldBe(middle, "the window includes its start and excludes its end");
    }

    /// <summary>One delivery is found with its envelope, and its attempts read back in order with what each ended as.</summary>
    [Fact]
    public async Task A_delivery_is_found_with_its_envelope_and_its_attempts_in_order()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var first = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.FailAsync(Attempt(first), Failure("System.TimeoutException", TimeSpan.Zero), Ct);
        var second = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.CompleteAsync(Attempt(second), Ct);

        var found = (await world.Store.FindAsync(first.Id, Ct)).ShouldNotBeNull();
        var attempts = await world.Store.AttemptsAsync(first.Id, Ct);

        found.Status.ShouldBe("succeeded");
        found.Attempts.ShouldBe(2);
        found.Payload.ShouldNotBeNull().ShouldContain(@event.ToString());
        attempts.Select(attempt => (attempt.Attempt, attempt.Outcome, attempt.ErrorType)).ShouldBe(
            [(1, "failed", "System.TimeoutException"), (2, "succeeded", null)]);
    }

    /// <summary>An id no fan-out recorded is not found and has no attempts.</summary>
    [Fact]
    public async Task An_unknown_delivery_is_not_found_and_has_no_attempts()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        (await world.Store.FindAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
        (await world.Store.AttemptsAsync(Guid.NewGuid(), Ct)).ShouldBeEmpty();
    }

    /// <summary>A selection counts exactly, samples the first ids in event order, and changes nothing.</summary>
    [Fact]
    public async Task A_selection_counts_exactly_samples_in_event_order_and_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var dead = new List<Guid>();
        foreach (var key in new[] { KeyA, KeyB, "tickets:c" })
        {
            var @event = await world.AppendEventAsync(key);
            await world.Store.FanOutAsync([Request(@event, Confirm, key) with { DeadReason = "when-failed" }, Request(@event, Audit, key)], Ct);
            dead.Add(@event);
        }

        var before = await world.Store.ListAsync(new DeliveryFilter { Limit = 50 }, Ct);
        var selection = await world.Store.SelectAsync(new DeliveryFilter { Subscriber = Confirm, Status = "dead", Limit = 2 }, Ct);
        var after = await world.Store.ListAsync(new DeliveryFilter { Limit = 50 }, Ct);

        selection.Count.ShouldBe(3);
        selection.Sample.Count.ShouldBe(2);
        (await world.Store.FindAsync(selection.Sample[0], Ct)).ShouldNotBeNull().EventId.ShouldBe(dead[0]);
        after.ShouldBe(before);
    }

    /// <summary>A page or a selection asking for nothing is refused rather than answered empty.</summary>
    [Fact]
    public async Task A_read_asking_for_no_rows_is_refused()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.Store.ListAsync(new DeliveryFilter { Limit = 0 }, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.Store.SelectAsync(new DeliveryFilter { Limit = 0 }, Ct));
    }
}
```

Add to `test/MMLib.Alvo.Abstractions.Tests/AlvoEventIdTests.cs` (create the file with the namespace `MMLib.Alvo.Abstractions.Tests.Events` and class `AlvoEventIdTests` if it does not exist):

```csharp
    /// <summary>
    /// The floor of an instant is at or below every id minted in its millisecond and above every id minted before it, in
    /// the text order the stores compare ids by.
    /// </summary>
    [Fact]
    public void The_floor_of_an_instant_bounds_the_ids_minted_in_it()
    {
        var instant = new DateTimeOffset(2026, 10, 24, 9, 0, 0, 123, TimeSpan.Zero);
        var earlier = AlvoEventId.Create(instant.AddMilliseconds(-1)).ToString();
        var within = AlvoEventId.Create(instant).ToString();
        var floor = AlvoEventId.Floor(instant).ToString();

        string.CompareOrdinal(earlier, floor).ShouldBeLessThan(0);
        string.CompareOrdinal(floor, within).ShouldBeLessThanOrEqualTo(0);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FakeDeliveryStoreTests'`
Expected: build FAIL — `DeliveryFilter`, `IDeliveryStore.ListAsync` … do not exist.

- [ ] **Step 3: The records and the port's read members**

Create `src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs`:

```csharp
namespace MMLib.Alvo.Events;

/// <summary>One delivery as the store holds it, for an operator to read.</summary>
public sealed record StoredDelivery
{
    /// <summary>Gets the delivery's id; ids order by the time the delivery was fanned out.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the outbox entry it delivers.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the registration that runs it.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets its lane.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Gets the event's tenant, or <see langword="null"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets what runs it: <c>handler</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets where it is: <c>pending</c>, <c>running</c>, <c>succeeded</c>, <c>retrying</c>, <c>dead</c> or <c>cancelled</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Gets how many times it was claimed since it was last put back to pending.</summary>
    public required int Attempts { get; init; }

    /// <summary>Gets the earliest instant it may be claimed.</summary>
    public required DateTimeOffset NotBefore { get; init; }

    /// <summary>Gets when it ended — succeeded, dead or cancelled — or <see langword="null"/> while it has not.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Gets the type name or fixed reason of its last failure — never an exception message.</summary>
    public string? LastErrorType { get; init; }

    /// <summary>Gets the stored envelope; filled only by <see cref="IDeliveryStore.FindAsync"/>, and <see langword="null"/> once the outbox no longer holds it.</summary>
    public string? Payload { get; init; }
}

/// <summary>One recorded attempt of a delivery.</summary>
public sealed record StoredDeliveryAttempt
{
    /// <summary>Gets its number in the delivery's whole history, from 1 — it keeps counting across a redeliver.</summary>
    public required int Attempt { get; init; }

    /// <summary>Gets when it started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets how long it ran.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets how it ended: <c>succeeded</c>, <c>failed</c> or <c>dead</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Gets the type name or fixed reason it failed with, or <see langword="null"/>.</summary>
    public string? ErrorType { get; init; }
}

/// <summary>Which deliveries an operator's read or reset is about.</summary>
public sealed record DeliveryFilter
{
    /// <summary>Gets the one subscriber to read, or <see langword="null"/> for all.</summary>
    public string? Subscriber { get; init; }

    /// <summary>Gets the one status to read, or <see langword="null"/> for all.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the smallest event id included, compared in text order as lanes are, or <see langword="null"/>.</summary>
    public Guid? FromEventId { get; init; }

    /// <summary>Gets the event id the window ends before — excluded — or <see langword="null"/>.</summary>
    public Guid? ToEventId { get; init; }

    /// <summary>Gets the delivery id a page continues after: only strictly older deliveries are listed; ignored by a selection.</summary>
    public Guid? After { get; init; }

    /// <summary>Gets a page's size, or a selection's and a replay's sample size; a store refuses zero or less with <see cref="ArgumentOutOfRangeException"/>.</summary>
    public required int Limit { get; init; }
}

/// <summary>What a filter selects, counted exactly, with the first ids it would reset.</summary>
public sealed record DeliverySelection
{
    /// <summary>Gets how many deliveries match.</summary>
    public required int Count { get; init; }

    /// <summary>Gets the first <see cref="DeliveryFilter.Limit"/> matching ids, in event order — the order they would run.</summary>
    public required IReadOnlyList<Guid> Sample { get; init; }
}
```

In `src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs` extend the interface remarks with a paragraph — `<b>The operator's members</b> read deliveries, put finished ones back to pending, pause a subscriber and record each of those changes in the operations log in the same transaction as the change; a member that changes nothing records nothing. They are abstract: a store that could not serve them would leave a dead delivery's lane blocked forever.` — and append the four read members:

```csharp
    /// <summary>A page of deliveries, newest first — by delivery id — strictly after <see cref="DeliveryFilter.After"/>, without payloads.</summary>
    /// <param name="filter">Which deliveries, and how many.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>At most <see cref="DeliveryFilter.Limit"/> deliveries.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="DeliveryFilter.Limit"/> is zero or less.</exception>
    Task<IReadOnlyList<StoredDelivery>> ListAsync(DeliveryFilter filter, CancellationToken cancellationToken = default);

    /// <summary>One delivery, with its stored envelope.</summary>
    /// <param name="id">The delivery id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The delivery, or <see langword="null"/> when no fan-out recorded it.</returns>
    Task<StoredDelivery?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>A delivery's recorded attempts, ascending.</summary>
    /// <param name="id">The delivery id.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The attempts; none for an unknown id.</returns>
    Task<IReadOnlyList<StoredDeliveryAttempt>> AttemptsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Counts what <paramref name="filter"/> selects, exactly, and samples the first ids in event order; changes nothing.</summary>
    /// <param name="filter">Which deliveries; <see cref="DeliveryFilter.After"/> is ignored.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The count and the sample.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="DeliveryFilter.Limit"/> is zero or less.</exception>
    Task<DeliverySelection> SelectAsync(DeliveryFilter filter, CancellationToken cancellationToken = default);
```

In `src/MMLib.Alvo.Abstractions/Events/AlvoEventId.cs`, after `Create(DateTimeOffset)`:

```csharp
    /// <summary>The smallest id <see cref="Create(DateTimeOffset)"/> can mint in <paramref name="instant"/>'s millisecond.</summary>
    /// <remarks>An operator's window over event time is a window over ids: every id minted at or after the instant sorts at or above it.</remarks>
    /// <param name="instant">The instant.</param>
    /// <returns>The floor id.</returns>
    internal static Guid Floor(DateTimeOffset instant) => Compose(instant.ToUnixTimeMilliseconds(), UInt128.Zero);
```

- [ ] **Step 4: The EF statements and members**

Append to `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs`:

```csharp
    internal const string DeliveryColumns =
        "id, event_id, subscriber, partition_key, tenant_id, kind, status, attempts, not_before, completed_at, last_error_type";

    internal static string ListSql(string table, DeliveryFilter filter) =>
        $"SELECT {DeliveryColumns} FROM {table}{Where(filter, keyset: true)} ORDER BY id DESC LIMIT @limit";

    internal static string FindSql(string table) => $"SELECT {DeliveryColumns} FROM {table} WHERE id = @id";

    internal static string AttemptsSql(string attempts) =>
        $"SELECT attempt, started_at, duration_ms, outcome, error_type FROM {attempts} WHERE delivery_id = @id ORDER BY attempt";

    /// <summary>
    /// The count and the sample in one statement: the window count is taken before the limit, so the two cannot disagree,
    /// and a filter matching nothing returns no row — a count of zero.
    /// </summary>
    internal static string SelectSql(string table, DeliveryFilter filter) =>
        $"SELECT id, COUNT(*) OVER () FROM {table}{Where(filter, keyset: false)} ORDER BY event_id, id LIMIT @limit";

    internal static string Conditions(DeliveryFilter filter, bool keyset)
    {
        var terms = new List<string>();
        AddWhen(terms, filter.Subscriber is not null, "subscriber = @subscriber");
        AddWhen(terms, filter.Status is not null, "status = @status");
        AddWhen(terms, filter.FromEventId is not null, "event_id >= @from_event");
        AddWhen(terms, filter.ToEventId is not null, "event_id < @to_event");
        AddWhen(terms, keyset && filter.After is not null, "id < @after");

        return string.Join(" AND ", terms);
    }

    private static string Where(DeliveryFilter filter, bool keyset) =>
        Conditions(filter, keyset) is { Length: > 0 } terms ? $" WHERE {terms}" : string.Empty;

    private static void AddWhen(List<string> terms, bool present, string term)
    {
        if (present)
        {
            terms.Add(term);
        }
    }
```

(add `using MMLib.Alvo.Events;` at the top of that file).

Create `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs`:

```csharp
using MMLib.Alvo.Events;

using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>The operator's members of the relational <see cref="IDeliveryStore"/>, over <see cref="DeliveryOperationsTable"/>'s SQL.</summary>
internal sealed partial class EfCoreDeliveryStore
{
    public async Task<IReadOnlyList<StoredDelivery>> ListAsync(DeliveryFilter filter, CancellationToken cancellationToken = default)
    {
        EnsureRows(filter);

        return await ReadAsync(
            DeliveryOperationsTable.ListSql(_table, filter),
            command => BindFilter(command, filter, keyset: true, limit: true),
            ReadDelivery,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredDelivery?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync(DeliveryOperationsTable.FindSql(_table), command => BindId(command, id), ReadDelivery, cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var payloads = await PayloadsAsync([rows[0].EventId], cancellationToken).ConfigureAwait(false);

        return rows[0] with { Payload = payloads.GetValueOrDefault(rows[0].EventId) };
    }

    public async Task<IReadOnlyList<StoredDeliveryAttempt>> AttemptsAsync(Guid id, CancellationToken cancellationToken = default) =>
        await ReadAsync(DeliveryOperationsTable.AttemptsSql(_attempts), command => BindId(command, id), ReadAttempt, cancellationToken)
            .ConfigureAwait(false);

    public async Task<DeliverySelection> SelectAsync(DeliveryFilter filter, CancellationToken cancellationToken = default)
    {
        EnsureRows(filter);
        var rows = await ReadAsync(
            DeliveryOperationsTable.SelectSql(_table, filter),
            command => BindFilter(command, filter, keyset: false, limit: true),
            reader => (Id: Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture), Total: Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);

        return new DeliverySelection { Count = rows.Count == 0 ? 0 : rows[0].Total, Sample = [.. rows.Select(row => row.Id)] };
    }

    private static void EnsureRows(DeliveryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.Limit);
    }

    private static void BindId(DbCommand command, Guid id) => RelationalSqlBatch.AddParameter(command, "@id", id.ToString());

    private static void BindFilter(DbCommand command, DeliveryFilter filter, bool keyset, bool limit)
    {
        AddWhenSet(command, "@subscriber", filter.Subscriber);
        AddWhenSet(command, "@status", filter.Status);
        AddWhenSet(command, "@from_event", filter.FromEventId?.ToString());
        AddWhenSet(command, "@to_event", filter.ToEventId?.ToString());
        AddWhenSet(command, "@after", keyset ? filter.After?.ToString() : null);
        if (limit)
        {
            RelationalSqlBatch.AddParameter(command, "@limit", filter.Limit);
        }
    }

    private static void AddWhenSet(DbCommand command, string name, string? value)
    {
        if (value is not null)
        {
            RelationalSqlBatch.AddParameter(command, name, value);
        }
    }

    private static StoredDelivery ReadDelivery(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        EventId = Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        Subscriber = reader.GetString(2),
        PartitionKey = reader.GetString(3),
        TenantId = reader.IsDBNull(4) ? null : reader.GetString(4),
        Kind = reader.GetString(5),
        Status = reader.GetString(6),
        Attempts = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
        NotBefore = StoredInstant.Of(reader.GetValue(8)),
        CompletedAt = reader.IsDBNull(9) ? null : StoredInstant.Of(reader.GetValue(9)),
        LastErrorType = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static StoredDeliveryAttempt ReadAttempt(DbDataReader reader) => new()
    {
        Attempt = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
        StartedAt = StoredInstant.Of(reader.GetValue(1)),
        Duration = TimeSpan.FromMilliseconds(Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)),
        Outcome = reader.GetString(3),
        ErrorType = reader.IsDBNull(4) ? null : reader.GetString(4),
    };
}
```

- [ ] **Step 5: The fake store and the decorator keep up**

`test/_shared/testhost/FailingFirstFanOut.cs` — delegate the four members to `inner`:

```csharp
    public Task<IReadOnlyList<StoredDelivery>> ListAsync(DeliveryFilter filter, CancellationToken cancellationToken = default) =>
        inner.ListAsync(filter, cancellationToken);

    public Task<StoredDelivery?> FindAsync(Guid id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);

    public Task<IReadOnlyList<StoredDeliveryAttempt>> AttemptsAsync(Guid id, CancellationToken cancellationToken = default) =>
        inner.AttemptsAsync(id, cancellationToken);

    public Task<DeliverySelection> SelectAsync(DeliveryFilter filter, CancellationToken cancellationToken = default) =>
        inner.SelectAsync(filter, cancellationToken);
```

`test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs`:
- add `public string Kind { get; init; } = "handler";` to `Row`, set `Kind = request.Kind` in `NewRow`;
- add `private readonly List<(Guid Delivery, DateTimeOffset StartedAt, TimeSpan Duration, string Outcome, string? ErrorType)> _history = [];`;
- give `Answer` a fourth parameter `string? errorType` and, after `_attempts.Add(...)`, `_history.Add((row.Id, attempt.StartedAt, attempt.Duration, outcome, errorType));` — `CompleteAsync` passes `null`, `FailAsync` passes `failure.ErrorType`;
- add the members:

```csharp
    public Task<IReadOnlyList<StoredDelivery>> ListAsync(DeliveryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.Limit);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<StoredDelivery>>([.. Matching(filter)
                .Where(row => filter.After is not { } after || string.CompareOrdinal(row.Id.ToString(), after.ToString()) < 0)
                .OrderByDescending(row => row.Id.ToString(), StringComparer.Ordinal)
                .Take(filter.Limit)
                .Select(Stored)]);
        }
    }

    public Task<StoredDelivery?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.SingleOrDefault(row => row.Id == id) is { } row
                ? Stored(row) with { Payload = _payloads.GetValueOrDefault(row.EventId) }
                : null);
        }
    }

    public Task<IReadOnlyList<StoredDeliveryAttempt>> AttemptsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<StoredDeliveryAttempt>>([.. _history
                .Where(entry => entry.Delivery == id)
                .Select((entry, index) => new StoredDeliveryAttempt
                {
                    Attempt = index + 1,
                    StartedAt = entry.StartedAt,
                    Duration = entry.Duration,
                    Outcome = entry.Outcome,
                    ErrorType = entry.ErrorType,
                })]);
        }
    }

    public Task<DeliverySelection> SelectAsync(DeliveryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.Limit);
        lock (_gate)
        {
            var matched = InEventOrder(Matching(filter)).ToList();

            return Task.FromResult(new DeliverySelection { Count = matched.Count, Sample = [.. matched.Take(filter.Limit).Select(row => row.Id)] });
        }
    }

    private IEnumerable<Row> Matching(DeliveryFilter filter) => _rows.Where(row =>
        (filter.Subscriber is null || row.Subscriber == filter.Subscriber)
        && (filter.Status is null || row.Status == filter.Status)
        && (filter.FromEventId is not { } from || !Before(row.EventId, from))
        && (filter.ToEventId is not { } to || Before(row.EventId, to)));

    private static IEnumerable<Row> InEventOrder(IEnumerable<Row> rows) => rows
        .OrderBy(row => row.EventId.ToString(), StringComparer.Ordinal)
        .ThenBy(row => row.Id.ToString(), StringComparer.Ordinal);

    private static StoredDelivery Stored(Row row) => new()
    {
        Id = row.Id,
        EventId = row.EventId,
        Subscriber = row.Subscriber,
        PartitionKey = row.PartitionKey,
        TenantId = row.TenantId,
        Kind = row.Kind,
        Status = row.Status,
        Attempts = row.Attempts,
        NotBefore = row.NotBefore,
        CompletedAt = row.CompletedAt,
        LastErrorType = row.LastErrorType,
    };
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FakeDeliveryStoreTests'`; `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteDeliveryStoreTests'`; `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*AlvoEventIdTests'`.
Expected: PASS (the PostgreSQL leg runs the same rows in ring2).

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'` and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TestingLibraryPublicApiTests'`.
Expected: FAIL — accept the received baselines (`mv *.received.txt` over the `.verified.txt`): Abstractions grows by the four records and four interface members; Testing by the six contract facts. Dispatch `alvo-snapshot-judge` when the turn-review gate asks.

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs src/MMLib.Alvo.Abstractions/Events/AlvoEventId.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/_shared/testhost/FailingFirstFanOut.cs test/MMLib.Alvo.Abstractions.Tests/AlvoEventIdTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs src/MMLib.Alvo.Abstractions/Events/AlvoEventId.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/_shared/testhost/FailingFirstFanOut.cs test/MMLib.Alvo.Abstractions.Tests/AlvoEventIdTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "feat(events): read deliveries, one delivery's history and an exact selection through the store

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 3: The port's writes — redeliver and replay, each with its log row in one transaction

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs`, `src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs`, `EfCoreDeliveryStore.Operations.cs`
- Modify: `src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs`, `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs`
- Modify (tests): `test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs`, `test/MMLib.Alvo.Tests/Events/FakeDeliveryStoreTests.cs` (its `World`), `test/_shared/ef/DeliveryStoreWorld.cs`, `test/_shared/testhost/FailingFirstFanOut.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs`, both public-API baselines of Task 2

**Interfaces:**
- Consumes: Task 2's records and reads; `RelationalConnectionFactory.WriteGate`.
- Produces (public, Abstractions `MMLib.Alvo.Events`):
  - `DeliveryOperationEntry { required Guid Id; required DateTimeOffset At; required string Actor; required string Operation; required string Target; string? Reason; int? Affected; IReadOnlyList<Guid> Sample = []; string? IdempotencyScope; string? IdempotencyKey; string? Fingerprint }`
  - `IDeliveryStore.RedeliverAsync(Guid id, DeliveryOperationEntry entry, CancellationToken) → Task<bool>` — a `succeeded`, `dead` or `cancelled` delivery back to `pending` with its claim count at zero, due now, its last error cleared; one log row (`Affected = 1`) only when it changed; `false` changes and records nothing.
  - `.ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken) → Task<DeliveryOperationEntry?>` — every finished delivery the filter selects reset as above, and one log row with `Affected` and `Sample` (first `filter.Limit` ids in event order) — always, even for zero; `null`, changing nothing, when `entry`'s `IdempotencyKey` is already recorded for its `IdempotencyScope`.
  - `.FindOperationAsync(string scope, string key, CancellationToken) → Task<DeliveryOperationEntry?>`.
  - Testing: `IDeliveryStoreWorld.OperationsAsync() → Task<IReadOnlyList<DeliveryOperationEntry>>` (every log row, by `At` then `Id`).
  - internal: `EfCoreDeliveryStore.ReadEntry(DbDataReader)` (internal static), `InTransactionAsync<T>`, `ExecuteInAsync`, `LogAsync`, `Nullable`; `DeliveryOperationsTable.LogColumns`, `Finished`, `Reset`, `AllOperationsSql(string log)`.

- [ ] **Step 1: Write the failing contract rows**

Append to `src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs`:

```csharp
    /// <summary>Every row of the operations log, by when it was written and then by id.</summary>
    /// <returns>The rows, as the store would find them.</returns>
    Task<IReadOnlyList<DeliveryOperationEntry>> OperationsAsync();
```

Append to `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs`, inside the class:

```csharp
    private static readonly DateTimeOffset _operatedAt = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private const string Operator = "ops-key:00000000-0000-0000-0000-000000000001";

    /// <summary>Redelivering a dead delivery makes it the claimable head of its lane again, and the lane drains after it.</summary>
    [Fact]
    public async Task Redelivering_a_dead_delivery_makes_it_the_head_of_its_lane_again()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var dead = await world.AppendEventAsync(KeyA);
        var later = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(dead, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(later, Confirm, KeyA)], Ct);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldBeEmpty("the dead head blocks its lane");
        var id = (await world.Store.ListAsync(new DeliveryFilter { Status = "dead", Limit = 1 }, Ct)).ShouldHaveSingleItem().Id;

        (await world.Store.RedeliverAsync(id, Entry("redeliver", id.ToString()), Ct)).ShouldBeTrue();
        var head = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.CompleteAsync(Attempt(head), Ct);

        head.EventId.ShouldBe(dead);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(later);
    }

    /// <summary>
    /// A delivery that died at its last attempt runs again with a fresh attempt budget, and its history keeps the earlier
    /// attempts, numbered on.
    /// </summary>
    [Fact]
    public async Task A_redelivered_delivery_runs_again_with_a_fresh_budget_and_keeps_its_history()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var first = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        await world.Store.FailAsync(Attempt(first), Failure("System.InvalidOperationException", retryAfter: null), Ct);

        await world.Store.RedeliverAsync(first.Id, Entry("redeliver", first.Id.ToString()), Ct);
        var again = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        (await world.Store.CompleteAsync(Attempt(again), Ct)).ShouldBeTrue();

        again.Attempts.ShouldBe(1);
        (await world.Store.AttemptsAsync(first.Id, Ct)).Select(attempt => (attempt.Attempt, attempt.Outcome)).ShouldBe([(1, "dead"), (2, "succeeded")]);
        (await world.Store.FindAsync(first.Id, Ct)).ShouldNotBeNull().LastErrorType.ShouldBeNull();
    }

    /// <summary>A delivery still pending, running or retrying is not redelivered, and nothing is logged.</summary>
    [Fact]
    public async Task Redelivering_an_unfinished_delivery_changes_nothing_and_logs_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA)], Ct);
        var pending = (await world.Store.ListAsync(new DeliveryFilter { Limit = 1 }, Ct)).ShouldHaveSingleItem();
        (await world.Store.RedeliverAsync(pending.Id, Entry("redeliver", pending.Id.ToString()), Ct)).ShouldBeFalse();
        var running = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();

        (await world.Store.RedeliverAsync(running.Id, Entry("redeliver", running.Id.ToString()), Ct)).ShouldBeFalse();
        (await world.Store.RedeliverAsync(Guid.NewGuid(), Entry("redeliver", "unknown"), Ct)).ShouldBeFalse();

        running.Attempts.ShouldBe(1, "the pending redeliver did not reset a claim count it never had");
        (await world.Store.FindAsync(running.Id, Ct)).ShouldNotBeNull().Status.ShouldBe("running");
        (await world.OperationsAsync()).ShouldBeEmpty();
    }

    /// <summary>A redeliver that changed the delivery writes exactly one log row, with who, what and why.</summary>
    [Fact]
    public async Task A_redeliver_writes_one_log_row()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var @event = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(@event, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        var id = (await world.Store.ListAsync(new DeliveryFilter { Limit = 1 }, Ct)).ShouldHaveSingleItem().Id;
        var entry = Entry("redeliver", id.ToString());

        await world.Store.RedeliverAsync(id, entry, Ct);

        var row = (await world.OperationsAsync()).ShouldHaveSingleItem();
        (row.Id, row.At, row.Actor, row.Operation, row.Target, row.Reason, row.Affected)
            .ShouldBe((entry.Id, entry.At, entry.Actor, "redeliver", id.ToString(), "contract", (int?)1));
    }

    /// <summary>A replay resets only the named subscriber's finished deliveries and records one row with its count and sample.</summary>
    [Fact]
    public async Task A_replay_resets_only_the_named_subscriber_s_finished_deliveries()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        var second = await world.AppendEventAsync(KeyB);
        var third = await world.AppendEventAsync("tickets:c");
        await world.Store.FanOutAsync([Request(first, Confirm, KeyA) with { DeadReason = "when-failed" }, Request(first, Audit, KeyA) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(second, Confirm, KeyB) with { DeadReason = "when-failed" }], Ct);
        await world.Store.FanOutAsync([Request(third, Confirm, "tickets:c")], Ct);

        var recorded = (await world.Store.ReplayAsync(new DeliveryFilter { Subscriber = Confirm, Status = "dead", Limit = 20 }, Entry("replay", Confirm), Ct)).ShouldNotBeNull();

        recorded.Affected.ShouldBe(2);
        recorded.Sample.Count.ShouldBe(2);
        (await world.Store.FindAsync(recorded.Sample[0], Ct)).ShouldNotBeNull().EventId.ShouldBe(first, "the sample is in event order");
        (await world.Store.ListAsync(new DeliveryFilter { Subscriber = Audit, Status = "dead", Limit = 10 }, Ct)).ShouldHaveSingleItem("another subscriber's dead row is untouched");
        (await world.Store.ListAsync(new DeliveryFilter { Subscriber = Confirm, Status = "pending", Limit = 10 }, Ct)).Count.ShouldBe(3);
        (await world.OperationsAsync()).ShouldHaveSingleItem().Affected.ShouldBe(2);
    }

    /// <summary>A replay selecting nothing still records its row — the request happened — with a count of zero.</summary>
    [Fact]
    public async Task A_replay_that_selects_nothing_still_records_its_row()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        var recorded = await world.Store.ReplayAsync(new DeliveryFilter { Subscriber = Confirm, Status = "dead", Limit = 20 }, Entry("replay", Confirm), Ct);

        recorded.ShouldNotBeNull().Affected.ShouldBe(0);
        recorded.Sample.ShouldBeEmpty();
        (await world.OperationsAsync()).ShouldHaveSingleItem();
    }

    /// <summary>A replay under a key the caller already spent changes nothing and answers null; the first row is found by the key.</summary>
    [Fact]
    public async Task A_replay_under_a_spent_key_changes_nothing()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(first, Confirm, KeyA) with { DeadReason = "when-failed" }], Ct);
        var filter = new DeliveryFilter { Subscriber = Confirm, Status = "dead", Limit = 20 };
        await world.Store.ReplayAsync(filter, Entry("replay", Confirm, key: "replay-1"), Ct);
        var second = await world.AppendEventAsync(KeyB);
        await world.Store.FanOutAsync([Request(second, Confirm, KeyB) with { DeadReason = "when-failed" }], Ct);

        (await world.Store.ReplayAsync(filter, Entry("replay", Confirm, key: "replay-1"), Ct)).ShouldBeNull();

        (await world.Store.ListAsync(filter, Ct)).ShouldHaveSingleItem().EventId.ShouldBe(second, "the second replay reset nothing");
        var spent = (await world.Store.FindOperationAsync("global/ops", "replay-1", Ct)).ShouldNotBeNull();
        spent.Affected.ShouldBe(1);
        spent.Fingerprint.ShouldBe("fingerprint");
        (await world.Store.FindOperationAsync("global/ops", "replay-2", Ct)).ShouldBeNull();
    }

    private static DeliveryOperationEntry Entry(string operation, string target, string? key = null) => new()
    {
        Id = AlvoEventId.Create(_operatedAt),
        At = _operatedAt,
        Actor = Operator,
        Operation = operation,
        Target = target,
        Reason = "contract",
        IdempotencyScope = key is null ? null : "global/ops",
        IdempotencyKey = key,
        Fingerprint = key is null ? null : "fingerprint",
    };
```

(Log rows are compared member by member: a record's `Sample` list compares by reference.)

Add to `DeliveryOperationsTableTests`:

```csharp
    [Fact]
    public void A_replay_resets_only_finished_rows_and_reads_back_what_it_reset()
    {
        var sql = DeliveryOperationsTable.ReplaySql("alvo_deliveries", new MMLib.Alvo.Events.DeliveryFilter { Subscriber = "a", Status = "dead", Limit = 20 });

        sql.ShouldContain("WHERE status IN ('succeeded', 'dead', 'cancelled') AND subscriber = @subscriber AND status = @status");
        sql.ShouldContain("attempts = 0");
        sql.ShouldEndWith("RETURNING id, event_id");
    }

    [Fact]
    public void A_log_row_under_a_spent_key_is_skipped_not_failed() =>
        DeliveryOperationsTable.InsertLogSql(Log).ShouldEndWith("ON CONFLICT DO NOTHING");
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FakeDeliveryStoreTests'`
Expected: build FAIL — `DeliveryOperationEntry`, `RedeliverAsync` … do not exist.

- [ ] **Step 3: The records and the write members**

Append to `src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs`:

```csharp
/// <summary>One audited delivery operation — a pause, a resume, a redeliver, a replay — as the log records it.</summary>
public sealed record DeliveryOperationEntry
{
    /// <summary>Gets the row's id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets when the operation was made.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Gets who made it: the credential and the user it acted for.</summary>
    public required string Actor { get; init; }

    /// <summary>Gets what was done: <c>pause</c>, <c>resume</c>, <c>redeliver</c> or <c>replay</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>Gets what it was done to: a subscriber id, or a delivery id.</summary>
    public required string Target { get; init; }

    /// <summary>Gets why, in the operator's words, or <see langword="null"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets how many deliveries or controls it changed; the store fills it.</summary>
    public int? Affected { get; init; }

    /// <summary>Gets a replay's first reset ids, in event order; the store fills it.</summary>
    public IReadOnlyList<Guid> Sample { get; init; } = [];

    /// <summary>Gets the caller's identity a key is scoped to, or <see langword="null"/> when no key was sent.</summary>
    public string? IdempotencyScope { get; init; }

    /// <summary>Gets the caller's idempotency key, or <see langword="null"/>.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Gets what makes a retry the same request, or <see langword="null"/>.</summary>
    public string? Fingerprint { get; init; }
}

```

Append to `IDeliveryStore`:

```csharp
    /// <summary>
    /// Puts a finished delivery — succeeded, dead or cancelled — back to pending, due now, with its claim count at zero and
    /// its last error cleared, and records <paramref name="entry"/> in the same transaction; its attempt history is kept.
    /// </summary>
    /// <param name="id">The delivery id.</param>
    /// <param name="entry">The log row to write when the delivery changed.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether it changed; <see langword="false"/> for an unfinished or unknown delivery, which records nothing.</returns>
    Task<bool> RedeliverAsync(Guid id, DeliveryOperationEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts every finished delivery <paramref name="filter"/> selects back to pending, as <see cref="RedeliverAsync"/> does, and
    /// records <paramref name="entry"/> with how many it reset and the first <see cref="DeliveryFilter.Limit"/> of them in
    /// event order — in one transaction, whatever the count.
    /// </summary>
    /// <param name="filter">Which deliveries; <see cref="DeliveryFilter.After"/> is ignored.</param>
    /// <param name="entry">The log row; its key, when present, may be recorded once per scope.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The recorded row, or <see langword="null"/> — nothing reset — when the key is already recorded.</returns>
    Task<DeliveryOperationEntry?> ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The log row a caller's key was recorded under.</summary>
    /// <param name="scope">The caller's identity.</param>
    /// <param name="key">The caller's key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    Task<DeliveryOperationEntry?> FindOperationAsync(string scope, string key, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: The EF statements and members**

Append to `DeliveryOperationsTable.cs`:

```csharp
    internal const string LogColumns =
        "id, at, actor, operation, target, reason, affected, sample, idempotency_scope, idempotency_key, request_fingerprint";

    private const string Finished = "status IN ('succeeded', 'dead', 'cancelled')";

    private const string Reset =
        "status = 'pending', attempts = 0, not_before = @now, lease_until = NULL, claimed_by = NULL, completed_at = NULL, last_error_type = NULL";

    internal static string RedeliverSql(string table) => $"UPDATE {table} SET {Reset} WHERE id = @id AND {Finished}";

    internal static string ReplaySql(string table, DeliveryFilter filter) =>
        Conditions(filter, keyset: false) is { Length: > 0 } terms
            ? $"UPDATE {table} SET {Reset} WHERE {Finished} AND {terms} RETURNING id, event_id"
            : $"UPDATE {table} SET {Reset} WHERE {Finished} RETURNING id, event_id";

    internal static string InsertLogSql(string log) =>
        $"""
        INSERT INTO {log} ({LogColumns})
        VALUES (@log_id, @log_at, @log_actor, @log_operation, @log_target, @log_reason, @log_affected, @log_sample, @log_scope, @log_key, @log_fingerprint)
        ON CONFLICT DO NOTHING
        """;

    internal static string LogOutcomeSql(string log) => $"UPDATE {log} SET affected = @log_affected, sample = @log_sample WHERE id = @log_id";

    internal static string FindLogSql(string log) =>
        $"SELECT {LogColumns} FROM {log} WHERE idempotency_scope = @scope AND idempotency_key = @key";

    internal static string AllOperationsSql(string log) => $"SELECT {LogColumns} FROM {log} ORDER BY at, id";
```

Append to `EfCoreDeliveryStore.Operations.cs`:

```csharp
    public Task<bool> RedeliverAsync(Guid id, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return InTransactionAsync(async (connection, transaction) =>
        {
            var reset = await ExecuteInAsync(connection, transaction, DeliveryOperationsTable.RedeliverSql(_table), command =>
            {
                BindId(command, id);
                RelationalSqlBatch.AddParameter(command, "@now", Now);
            }, cancellationToken).ConfigureAwait(false) == 1;

            return reset && await LogAsync(connection, transaction, entry with { Affected = 1 }, cancellationToken).ConfigureAwait(false) == 1;
        }, cancellationToken);
    }

    public Task<DeliveryOperationEntry?> ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        EnsureRows(filter);
        ArgumentNullException.ThrowIfNull(entry);

        return InTransactionAsync(async (connection, transaction) =>
            await LogAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false) == 0
                ? null
                : await ResetSelectedAsync(connection, transaction, filter, entry, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    public async Task<DeliveryOperationEntry?> FindOperationAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var rows = await ReadAsync(DeliveryOperationsTable.FindLogSql(_log), command =>
        {
            RelationalSqlBatch.AddParameter(command, "@scope", scope);
            RelationalSqlBatch.AddParameter(command, "@key", key);
        }, ReadEntry, cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    internal static DeliveryOperationEntry ReadEntry(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
        At = StoredInstant.Of(reader.GetValue(1)),
        Actor = reader.GetString(2),
        Operation = reader.GetString(3),
        Target = reader.GetString(4),
        Reason = reader.IsDBNull(5) ? null : reader.GetString(5),
        Affected = reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
        Sample = reader.IsDBNull(7) ? [] : [.. reader.GetString(7).Split(',').Select(id => Guid.Parse(id, CultureInfo.InvariantCulture))],
        IdempotencyScope = reader.IsDBNull(8) ? null : reader.GetString(8),
        IdempotencyKey = reader.IsDBNull(9) ? null : reader.GetString(9),
        Fingerprint = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private async Task<DeliveryOperationEntry> ResetSelectedAsync(
        DbConnection connection, DbTransaction transaction, DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken ct)
    {
        var reset = await RelationalSqlBatch.ReadRowsAsync(connection, transaction, DeliveryOperationsTable.ReplaySql(_table, filter), command =>
        {
            BindFilter(command, filter, keyset: false, limit: false);
            RelationalSqlBatch.AddParameter(command, "@now", Now);
        }, reader => (Id: reader.GetString(0), EventId: reader.GetString(1)), ct).ConfigureAwait(false);
        var recorded = entry with
        {
            Affected = reset.Count,
            Sample = [.. reset.OrderBy(row => row.EventId, StringComparer.Ordinal).ThenBy(row => row.Id, StringComparer.Ordinal)
                .Take(filter.Limit).Select(row => Guid.Parse(row.Id, CultureInfo.InvariantCulture))],
        };
        await ExecuteInAsync(connection, transaction, DeliveryOperationsTable.LogOutcomeSql(_log), command => BindOutcome(command, recorded), ct)
            .ConfigureAwait(false);

        return recorded;
    }

    private Task<int> LogAsync(DbConnection connection, DbTransaction transaction, DeliveryOperationEntry entry, CancellationToken ct) =>
        ExecuteInAsync(connection, transaction, DeliveryOperationsTable.InsertLogSql(_log), command => BindEntry(command, entry), ct);

    private static void BindEntry(DbCommand command, DeliveryOperationEntry entry)
    {
        RelationalSqlBatch.AddParameter(command, "@log_at", StoredInstant.Text(entry.At));
        RelationalSqlBatch.AddParameter(command, "@log_actor", entry.Actor);
        RelationalSqlBatch.AddParameter(command, "@log_operation", entry.Operation);
        RelationalSqlBatch.AddParameter(command, "@log_target", entry.Target);
        RelationalSqlBatch.AddParameter(command, "@log_reason", Nullable(entry.Reason));
        RelationalSqlBatch.AddParameter(command, "@log_scope", Nullable(entry.IdempotencyScope));
        RelationalSqlBatch.AddParameter(command, "@log_key", Nullable(entry.IdempotencyKey));
        RelationalSqlBatch.AddParameter(command, "@log_fingerprint", Nullable(entry.Fingerprint));
        BindOutcome(command, entry);
    }

    private static void BindOutcome(DbCommand command, DeliveryOperationEntry entry)
    {
        RelationalSqlBatch.AddParameter(command, "@log_id", entry.Id.ToString());
        RelationalSqlBatch.AddParameter(command, "@log_affected", (object?)entry.Affected ?? DBNull.Value);
        RelationalSqlBatch.AddParameter(command, "@log_sample", entry.Sample.Count == 0 ? DBNull.Value : string.Join(',', entry.Sample));
    }

    private static object Nullable(string? value) => (object?)value ?? DBNull.Value;

    private async Task<T> InTransactionAsync<T>(Func<DbConnection, DbTransaction, Task<T>> work, CancellationToken ct)
    {
        var connection = _connections.Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, ct).ConfigureAwait(false);
            using (await _connections.WriteGate.EnterAsync(ct).ConfigureAwait(false))
            {
                var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var result = await work(connection, transaction).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);

                    return result;
                }
            }
        }
    }

    private static async Task<int> ExecuteInAsync(DbConnection connection, DbTransaction transaction, string sql, Action<DbCommand> bind, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            bind(command);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
```

Each transaction opens with a write (the `UPDATE` or the log `INSERT`); a replay whose log insert is skipped commits a transaction that changed nothing. A redeliver whose log insert would be skipped cannot happen (a fresh id), so its `== 1` reads as "changed".

In `test/_shared/ef/DeliveryStoreWorld.cs`, implement the world member:

```csharp
    public async Task<IReadOnlyList<DeliveryOperationEntry>> OperationsAsync()
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);

            return await RelationalSqlBatch.ReadRowsAsync(
                connection, transaction: null, DeliveryOperationsTable.AllOperationsSql(DeliveryOperationsTable.LogNameFor(SchemaPrefix)),
                _ => { }, EfCoreDeliveryStore.ReadEntry, Ct);
        }
    }
```

- [ ] **Step 5: The fake store and the decorator keep up**

`FailingFirstFanOut.cs` — delegate `RedeliverAsync`, `ReplayAsync`, `FindOperationAsync` to `inner` as in Task 2.

`FakeDeliveryStore.cs`:

```csharp
    private static readonly string[] _finished = ["succeeded", "dead", "cancelled"];
    private readonly List<DeliveryOperationEntry> _operations = [];

    internal IReadOnlyList<DeliveryOperationEntry> Operations
    {
        get
        {
            lock (_gate)
            {
                return [.. _operations];
            }
        }
    }

    public Task<bool> RedeliverAsync(Guid id, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_rows.SingleOrDefault(row => row.Id == id && _finished.Contains(row.Status)) is not { } row)
            {
                return Task.FromResult(false);
            }

            Reset(row);
            _operations.Add(entry with { Affected = 1 });

            return Task.FromResult(true);
        }
    }

    public Task<DeliveryOperationEntry?> ReplayAsync(DeliveryFilter filter, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.Limit);
        lock (_gate)
        {
            if (entry.IdempotencyKey is { } key && _operations.Exists(row => row.IdempotencyScope == entry.IdempotencyScope && row.IdempotencyKey == key))
            {
                return Task.FromResult<DeliveryOperationEntry?>(null);
            }

            var reset = InEventOrder(Matching(filter).Where(row => _finished.Contains(row.Status))).ToList();
            reset.ForEach(Reset);
            var recorded = entry with { Affected = reset.Count, Sample = [.. reset.Take(filter.Limit).Select(row => row.Id)] };
            _operations.Add(recorded);

            return Task.FromResult<DeliveryOperationEntry?>(recorded);
        }
    }

    public Task<DeliveryOperationEntry?> FindOperationAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_operations.LastOrDefault(row => row.IdempotencyScope == scope && row.IdempotencyKey == key));
        }
    }

    private void Reset(Row row)
    {
        row.Status = "pending";
        row.Attempts = 0;
        row.NotBefore = time.GetUtcNow();
        row.LeaseUntil = null;
        row.ClaimedBy = null;
        row.CompletedAt = null;
        row.LastErrorType = null;
    }
```

In `FakeDeliveryStoreTests.World` add `public Task<IReadOnlyList<DeliveryOperationEntry>> OperationsAsync() => Task.FromResult(_store.Operations);`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FakeDeliveryStoreTests'`; `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteDeliveryStoreTests'`; `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*DeliveryOperationsTableTests'`.
Expected: PASS. Then re-accept the Abstractions and Testing baselines (one record, three members, eight contract facts, `OperationsAsync`) and dispatch `alvo-snapshot-judge`.

- [ ] **Step 7: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStoreTests.cs test/_shared/ef/DeliveryStoreWorld.cs test/_shared/testhost/FailingFirstFanOut.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs src/MMLib.Alvo.Testing/Events/IDeliveryStoreWorld.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStoreTests.cs test/_shared/ef/DeliveryStoreWorld.cs test/_shared/testhost/FailingFirstFanOut.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "feat(events): redeliver and replay through the store, each logged in its own transaction

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 4: Pause — the control the claim reads **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs`, `src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs` (`ClaimSql` gains the pause term), `DeliveryOperationsTable.cs`, `EfCoreDeliveryStore.cs` (`ClaimRowsAsync` passes `_controls`), `EfCoreDeliveryStore.Operations.cs`
- Modify: `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs`
- Modify (tests): `test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs`, `test/_shared/ef/DeliveryStoreWorld.cs`, `test/_shared/testhost/FailingFirstFanOut.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs`, both public-API baselines of Task 2

**Interfaces:**
- Consumes: Task 3's `DeliveryOperationEntry`, `InTransactionAsync`, `ExecuteInAsync`, `LogAsync`, `Nullable`, the contract's `Entry(…)`, `_operatedAt`, `Operator`; Task 1's `_controls` field.
- Produces (public, Abstractions `MMLib.Alvo.Events`):
  - `SubscriberControl { required string Subscriber; required DateTimeOffset PausedAt; required string PausedBy; string? Reason }`
  - `IDeliveryStore.SetPausedAsync(string subscriber, bool paused, DeliveryOperationEntry entry, CancellationToken) → Task<bool>` — `true` and one log row only when the state changed; the pause records `entry.At`, `entry.Actor` and `entry.Reason`.
  - `.ControlsAsync(CancellationToken) → Task<IReadOnlyList<SubscriberControl>>` — the paused subscribers, by id.
  - `ClaimAsync` never claims a paused subscriber's delivery.
  - internal: `DeliveryTable.ClaimSql(string table, string outbox, string controls, int ordered, int unordered)`; `DeliveryOperationsTable.PauseSql`, `ResumeSql`, `ControlsSql`.

- [ ] **Step 1: Write the failing contract rows**

Append to `src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs`, inside the class:

```csharp
    /// <summary>A paused subscriber's deliveries are not claimed — another subscriber's are — and its lane drains in order after resume.</summary>
    [Fact]
    public async Task A_paused_subscriber_is_not_claimed_and_its_lane_drains_in_order_after_resume()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        var first = await world.AppendEventAsync(KeyA);
        var second = await world.AppendEventAsync(KeyA);
        await world.Store.FanOutAsync([Request(first, Confirm, KeyA), Request(first, Audit, KeyA)], Ct);
        await world.Store.FanOutAsync([Request(second, Confirm, KeyA)], Ct);

        (await world.Store.SetPausedAsync(Confirm, paused: true, Entry("pause", Confirm), Ct)).ShouldBeTrue();
        (await world.Store.ClaimAsync(Claim(Confirm, Audit), Ct)).ShouldHaveSingleItem().Subscriber.ShouldBe(Audit);
        (await world.Store.SetPausedAsync(Confirm, paused: false, Entry("resume", Confirm), Ct)).ShouldBeTrue();

        var head = (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem();
        head.EventId.ShouldBe(first);
        await world.Store.CompleteAsync(Attempt(head), Ct);
        (await world.Store.ClaimAsync(Claim(Confirm), Ct)).ShouldHaveSingleItem().EventId.ShouldBe(second);
    }

    /// <summary>Pausing a paused subscriber, or resuming a running one, changes nothing and logs nothing.</summary>
    [Fact]
    public async Task Pausing_twice_changes_nothing_the_second_time()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();

        (await world.Store.SetPausedAsync(Confirm, paused: false, Entry("resume", Confirm), Ct)).ShouldBeFalse();
        (await world.Store.SetPausedAsync(Confirm, paused: true, Entry("pause", Confirm), Ct)).ShouldBeTrue();
        (await world.Store.SetPausedAsync(Confirm, paused: true, Entry("pause", Confirm), Ct)).ShouldBeFalse();

        (await world.OperationsAsync()).Select(row => row.Operation).ShouldBe(["pause"]);
    }

    /// <summary>The paused subscribers are listed with who paused them, when and why, and no longer once resumed.</summary>
    [Fact]
    public async Task A_paused_subscriber_is_listed_with_who_when_and_why()
    {
        EnsureEngineAvailable();
        await using var world = await WorldAsync();
        await world.Store.SetPausedAsync(Confirm, paused: true, Entry("pause", Confirm), Ct);

        var control = (await world.Store.ControlsAsync(Ct)).ShouldHaveSingleItem();
        await world.Store.SetPausedAsync(Confirm, paused: false, Entry("resume", Confirm), Ct);

        control.ShouldBe(new SubscriberControl { Subscriber = Confirm, PausedAt = _operatedAt, PausedBy = Operator, Reason = "contract" });
        (await world.Store.ControlsAsync(Ct)).ShouldBeEmpty();
    }
```

Update `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs`: every `ClaimSql(Table, Outbox, …)` call becomes `ClaimSql(Table, Outbox, Controls, …)` with `private const string Controls = "alvo_subscriber_controls";`, the count in `The_claim_repeats_its_claimability_outside_the_subquery` becomes `ShouldBe(6, "two lane checks and the pause check in each half")`, and add:

```csharp
    [Fact]
    public void A_paused_subscriber_is_never_claimed() =>
        DeliveryTable.ClaimSql(Table, Outbox, Controls, ordered: 1, unordered: 1)
            .ShouldContain($"NOT EXISTS (SELECT 1 FROM {Controls} paused WHERE paused.subscriber = {Table}.subscriber AND paused.paused_at IS NOT NULL)");
```

Add to `DeliveryOperationsTableTests`:

```csharp
    [Fact]
    public void A_pause_changes_only_a_running_subscriber() =>
        DeliveryOperationsTable.PauseSql(Controls).ShouldContain($"WHERE {Controls}.paused_at IS NULL");
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FakeDeliveryStoreTests'`
Expected: build FAIL — `SetPausedAsync`, `SubscriberControl` do not exist.

- [ ] **Step 3: The record and the members**

Append to `src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs`:

```csharp
/// <summary>A subscriber an operator has paused.</summary>
public sealed record SubscriberControl
{
    /// <summary>Gets the subscriber id.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets when it was paused.</summary>
    public required DateTimeOffset PausedAt { get; init; }

    /// <summary>Gets who paused it.</summary>
    public required string PausedBy { get; init; }

    /// <summary>Gets why, or <see langword="null"/>.</summary>
    public string? Reason { get; init; }
}
```

Append to `IDeliveryStore`, and extend the *Lanes* paragraph of its remarks — after "its subscriber is one the claim names" — with "and is not paused":

```csharp
    /// <summary>Pauses or resumes a subscriber, recording <paramref name="entry"/> in the same transaction when the state changed.</summary>
    /// <param name="subscriber">The subscriber id.</param>
    /// <param name="paused">Whether it should be paused.</param>
    /// <param name="entry">The log row; a pause also stores its instant, actor and reason on the control.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the state changed.</returns>
    Task<bool> SetPausedAsync(string subscriber, bool paused, DeliveryOperationEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The paused subscribers, by id.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The controls.</returns>
    Task<IReadOnlyList<SubscriberControl>> ControlsAsync(CancellationToken cancellationToken = default);
```

- [ ] **Step 4: The claim's pause term, the statements and the members**

In `DeliveryTable.cs`, change `ClaimSql` to take `string controls` third and add the term:

```csharp
    internal static string ClaimSql(string table, string outbox, string controls, int ordered, int unordered)
    {
        var condition =
            $"{Claimable} AND {Registered(ordered + unordered)} AND {NotPaused(table, controls)} AND {LaneHead(table, outbox, ordered, unordered)}";

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

    private static string NotPaused(string table, string controls) =>
        $"NOT EXISTS (SELECT 1 FROM {controls} paused WHERE paused.subscriber = {table}.subscriber AND paused.paused_at IS NOT NULL)";
```

In `EfCoreDeliveryStore.ClaimRowsAsync` pass `_controls`: `DeliveryTable.ClaimSql(_table, _outbox, _controls, claim.Ordered.Count, claim.Unordered.Count)`. In `test/_shared/ef/DeliveryStoreWorld.cs` both `ClaimSql(_table, _outbox, …)` calls gain `DeliveryOperationsTable.ControlsNameFor(SchemaPrefix)` as the third argument.

Append to `DeliveryOperationsTable.cs`:

```csharp
    internal static string PauseSql(string controls) =>
        $"""
        INSERT INTO {controls} (subscriber, paused_at, paused_by, reason) VALUES (@subscriber, @at, @actor, @reason)
        ON CONFLICT (subscriber) DO UPDATE SET paused_at = excluded.paused_at, paused_by = excluded.paused_by, reason = excluded.reason
        WHERE {controls}.paused_at IS NULL
        """;

    internal static string ResumeSql(string controls) =>
        $"UPDATE {controls} SET paused_at = NULL, paused_by = NULL, reason = NULL WHERE subscriber = @subscriber AND paused_at IS NOT NULL";

    internal static string ControlsSql(string controls) =>
        $"SELECT subscriber, paused_at, paused_by, reason FROM {controls} WHERE paused_at IS NOT NULL ORDER BY subscriber";
```

Append to `EfCoreDeliveryStore.Operations.cs`:

```csharp
    public Task<bool> SetPausedAsync(string subscriber, bool paused, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        ArgumentNullException.ThrowIfNull(entry);

        return InTransactionAsync(async (connection, transaction) =>
        {
            var sql = paused ? DeliveryOperationsTable.PauseSql(_controls) : DeliveryOperationsTable.ResumeSql(_controls);
            var changed = await ExecuteInAsync(connection, transaction, sql, command => BindControl(command, subscriber, paused, entry), cancellationToken)
                .ConfigureAwait(false) == 1;

            return changed && await LogAsync(connection, transaction, entry with { Affected = 1 }, cancellationToken).ConfigureAwait(false) == 1;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SubscriberControl>> ControlsAsync(CancellationToken cancellationToken = default) =>
        await ReadAsync(DeliveryOperationsTable.ControlsSql(_controls), _ => { }, ReadControl, cancellationToken).ConfigureAwait(false);

    private static void BindControl(DbCommand command, string subscriber, bool paused, DeliveryOperationEntry entry)
    {
        RelationalSqlBatch.AddParameter(command, "@subscriber", subscriber);
        if (paused)
        {
            RelationalSqlBatch.AddParameter(command, "@at", StoredInstant.Text(entry.At));
            RelationalSqlBatch.AddParameter(command, "@actor", entry.Actor);
            RelationalSqlBatch.AddParameter(command, "@reason", Nullable(entry.Reason));
        }
    }

    private static SubscriberControl ReadControl(DbDataReader reader) => new()
    {
        Subscriber = reader.GetString(0),
        PausedAt = StoredInstant.Of(reader.GetValue(1)),
        PausedBy = reader.GetString(2),
        Reason = reader.IsDBNull(3) ? null : reader.GetString(3),
    };
```

The pause's upsert opens its transaction with a write and changes a row only from running to paused, so a second pause answers `false` and writes no log row; a resume changes only a paused row.

- [ ] **Step 5: The fake store and the decorator keep up**

`FailingFirstFanOut.cs` — delegate `SetPausedAsync` and `ControlsAsync` to `inner`.

`FakeDeliveryStore.cs` — add the field and the members:

```csharp
    private readonly Dictionary<string, SubscriberControl> _paused = new(StringComparer.Ordinal);

    public Task<bool> SetPausedAsync(string subscriber, bool paused, DeliveryOperationEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var changed = paused
                ? _paused.TryAdd(subscriber, new SubscriberControl { Subscriber = subscriber, PausedAt = entry.At, PausedBy = entry.Actor, Reason = entry.Reason })
                : _paused.Remove(subscriber);
            if (changed)
            {
                _operations.Add(entry with { Affected = 1 });
            }

            return Task.FromResult(changed);
        }
    }

    public Task<IReadOnlyList<SubscriberControl>> ControlsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SubscriberControl>>([.. _paused.Values.OrderBy(control => control.Subscriber, StringComparer.Ordinal)]);
        }
    }
```

and in `Claimable` add `&& !_paused.ContainsKey(row.Subscriber)` right after the subscriber test.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FakeDeliveryStoreTests'` and `--filter-class '*DeliveryDispatcherTests'` (the fake's claim change must not move a dispatcher fact); `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteDeliveryStoreTests'` (incl. `The_lane_check_seeks_the_partial_index_over_unfinished_deliveries`: the pause term must not cost the claim its index); `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*DeliveryTableTests'` and `--filter-class '*DeliveryOperationsTableTests'`.
Expected: PASS. Then re-accept the Abstractions and Testing baselines (one record, two members, three contract facts) and dispatch `alvo-snapshot-judge`.

- [ ] **Step 7: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/_shared/ef/DeliveryStoreWorld.cs test/_shared/testhost/FailingFirstFanOut.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs
scripts/test-ring1
git add src/MMLib.Alvo.Abstractions/Events/DeliveryOperations.cs src/MMLib.Alvo.Abstractions/Events/IDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/DeliveryOperationsTable.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfCoreDeliveryStore.Operations.cs src/MMLib.Alvo.Testing/Events/DeliveryStoreContractTests.Operations.cs test/MMLib.Alvo.Tests/Events/FakeDeliveryStore.cs test/_shared/ef/DeliveryStoreWorld.cs test/_shared/testhost/FailingFirstFanOut.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryTableTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/DeliveryOperationsTableTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "feat(events): pause a subscriber in the claim, logged in its own transaction

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 5: The operations wire records, the fixture and the refusals

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Management/ManagementOperationsModels.cs`, `DeliveryFixture.cs`, `ManagementDeliveryNotFoundException.cs`, `ManagementExtensionNotFoundException.cs`, `ManagementConflictException.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs` (`Paused`)
- Test: `test/MMLib.Alvo.Abstractions.Tests/Management/ManagementOperationsModelsTests.cs`; baseline `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Consumes: nothing new.
- Produces (public, Abstractions `MMLib.Alvo.Management`):
  - `ManagementDelivery { required Guid Id; required Guid EventId; required string Subscriber; required string PartitionKey; string? TenantId; required string Status; required int Attempts; required DateTimeOffset NotBefore; DateTimeOffset? CompletedAt; string? LastErrorType; IReadOnlyList<ManagementDeliveryAttempt> History = [] }`; `ManagementDeliveryAttempt { required int Attempt; required DateTimeOffset StartedAt; required TimeSpan Duration; required string Outcome; string? ErrorType }`; `ManagementDeliveryQuery { string? Subscriber; string? Status; DateTimeOffset? From; DateTimeOffset? To; Guid? After; int Limit = 50 }`; `ManagementDeliveryPage { required IReadOnlyList<ManagementDelivery> Items; Guid? Next }`; `ManagementReplayRequest { required string Subscriber; string Status = "dead"; DateTimeOffset? From; DateTimeOffset? To; bool DryRun; string? Reason; string? IdempotencyKey }`; `ManagementReplayPlan { required bool DryRun; required int Count; required IReadOnlyList<Guid> Ids; bool Replayed }`; `ManagementExtensionStats { required string Scope; required TimeSpan Window; required DateTimeOffset Since; required IReadOnlyList<ManagementExtensionStat> Items }`; `ManagementExtensionStat { required string Id; long Invocations; long Failures; long Rejections; TimeSpan? P50; TimeSpan? P95 }`; `DeliveryFixture { required string Subscriber; required Guid Delivery; required int Attempt; required JsonElement Envelope; IReadOnlyList<string> Redacted = [] }`.
  - `ManagementDeliveryNotFoundException(Guid delivery)` (`Delivery`), `ManagementExtensionNotFoundException(string extension, IReadOnlyList<string> registered)` (`Extension`), `ManagementConflictException(string message)` — each with the three standard constructors.
  - `ManagementExtension.Paused` (`bool`, init).

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Abstractions.Tests/Management/ManagementOperationsModelsTests.cs`:

```csharp
using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Abstractions.Tests.Management;

/// <summary>The defaults an operator's request starts from, and the shape a fixture travels in.</summary>
public sealed class ManagementOperationsModelsTests
{
    [Fact]
    public void A_replay_replays_dead_deliveries_for_real_unless_told_otherwise()
    {
        var request = new ManagementReplayRequest { Subscriber = "send-confirmation" };

        request.Status.ShouldBe("dead");
        request.DryRun.ShouldBeFalse();
    }

    [Fact]
    public void A_page_holds_fifty_deliveries_unless_asked_for_another_size() =>
        new ManagementDeliveryQuery().Limit.ShouldBe(50);

    [Fact]
    public void A_fixture_reads_back_from_the_json_it_is_downloaded_as()
    {
        var fixture = new DeliveryFixture
        {
            Subscriber = "send-confirmation",
            Delivery = Guid.Parse("0192f1a4-0000-7000-8000-000000000001"),
            Attempt = 3,
            Envelope = JsonDocument.Parse("""{"id":"x","data":{"record":{"title":"<redacted:title>"}}}""").RootElement,
            Redacted = ["/data/record/title"],
        };

        var read = JsonSerializer.Deserialize<DeliveryFixture>(JsonSerializer.Serialize(fixture, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;

        read.Subscriber.ShouldBe(fixture.Subscriber);
        read.Envelope.GetProperty("data").GetProperty("record").GetProperty("title").GetString().ShouldBe("<redacted:title>");
        read.Redacted.ShouldBe(["/data/record/title"]);
    }

    [Fact]
    public void A_refusal_names_what_was_asked_for_and_what_exists()
    {
        new ManagementExtensionNotFoundException("send-confirmaton", ["send-confirmation"]).Message
            .ShouldBe("This host registers no extension 'send-confirmaton'. It registers: send-confirmation.");
        new ManagementDeliveryNotFoundException(Guid.Empty).Message
            .ShouldBe("No delivery '00000000-0000-0000-0000-000000000000' was recorded. List the deliveries to find its id.");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*ManagementOperationsModelsTests'`
Expected: build FAIL — the types do not exist.

- [ ] **Step 3: The records, the fixture and the refusals**

Create `src/MMLib.Alvo.Abstractions/Management/ManagementOperationsModels.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>One handler delivery, as an operator reads it: where it is and how its attempts went — never its payload.</summary>
public sealed record ManagementDelivery
{
    /// <summary>Gets the delivery's id; ids order by the time the delivery was fanned out.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the event it delivers.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the handler that runs it.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets its lane: the record or key its events are ordered by.</summary>
    public required string PartitionKey { get; init; }

    /// <summary>Gets the event's tenant, or <see langword="null"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets where it is: <c>pending</c>, <c>running</c>, <c>succeeded</c>, <c>retrying</c>, <c>dead</c> or <c>cancelled</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Gets how many times it was attempted since it was last put back to pending.</summary>
    public required int Attempts { get; init; }

    /// <summary>Gets the earliest instant it runs.</summary>
    public required DateTimeOffset NotBefore { get; init; }

    /// <summary>Gets when it ended, or <see langword="null"/>.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Gets the type name or fixed reason of its last failure — never an exception message.</summary>
    public string? LastErrorType { get; init; }

    /// <summary>Gets its attempts, oldest first; filled only when one delivery is read.</summary>
    public IReadOnlyList<ManagementDeliveryAttempt> History { get; init; } = [];
}

/// <summary>One attempt of a delivery.</summary>
public sealed record ManagementDeliveryAttempt
{
    /// <summary>Gets its number in the delivery's whole history, from 1.</summary>
    public required int Attempt { get; init; }

    /// <summary>Gets when it started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets how long it ran.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets how it ended: <c>succeeded</c>, <c>failed</c> or <c>dead</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Gets the type name or fixed reason it failed with, or <see langword="null"/>.</summary>
    public string? ErrorType { get; init; }
}

/// <summary>Which deliveries a page lists.</summary>
public sealed record ManagementDeliveryQuery
{
    /// <summary>Gets the one handler to list, or <see langword="null"/> for all.</summary>
    public string? Subscriber { get; init; }

    /// <summary>Gets the one status to list, or <see langword="null"/> for all.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the earliest event time included, or <see langword="null"/>.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Gets the event time the window ends before, or <see langword="null"/>.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Gets the delivery id the previous page ended with — its <see cref="ManagementDeliveryPage.Next"/> — or <see langword="null"/>.</summary>
    public Guid? After { get; init; }

    /// <summary>Gets the page size, from 1 to 200; 50 unless set.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>One page of deliveries, newest first.</summary>
public sealed record ManagementDeliveryPage
{
    /// <summary>Gets the deliveries.</summary>
    public required IReadOnlyList<ManagementDelivery> Items { get; init; }

    /// <summary>Gets the cursor of the next page, or <see langword="null"/> when this page was the last.</summary>
    public Guid? Next { get; init; }
}

/// <summary>Which finished deliveries of one handler to run again.</summary>
public sealed record ManagementReplayRequest
{
    /// <summary>Gets the handler whose deliveries are replayed.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets which finished deliveries: <c>dead</c> (unless set), <c>succeeded</c> or <c>cancelled</c>.</summary>
    public string Status { get; init; } = "dead";

    /// <summary>Gets the earliest event time included, or <see langword="null"/>.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Gets the event time the window ends before, or <see langword="null"/>.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Gets whether to count and list without changing anything.</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets why, in the operator's words — kept in the audit; at most 500 characters.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the caller's idempotency key for a real replay, or <see langword="null"/>; refused on a dry run.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>What a replay selected — counted on a dry run, reset on a real one.</summary>
public sealed record ManagementReplayPlan
{
    /// <summary>Gets whether nothing was changed.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Gets how many deliveries were selected (a dry run) or reset (a real run).</summary>
    public required int Count { get; init; }

    /// <summary>Gets the first 20 of them, in the order they run.</summary>
    public required IReadOnlyList<Guid> Ids { get; init; }

    /// <summary>Gets whether this answers an earlier request made under the same idempotency key, resetting nothing now.</summary>
    public bool Replayed { get; init; }
}

/// <summary>How this instance's extensions ran over the last day.</summary>
public sealed record ManagementExtensionStats
{
    /// <summary>Gets what the numbers cover: <c>this-instance</c> — this process, not the deployment.</summary>
    public required string Scope { get; init; }

    /// <summary>Gets the window the numbers cover: 24 hours, in hourly steps.</summary>
    public required TimeSpan Window { get; init; }

    /// <summary>Gets the earliest instant counted — later than a day ago when this process started since.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>Gets one entry per registered extension, in catalog order.</summary>
    public required IReadOnlyList<ManagementExtensionStat> Items { get; init; }
}

/// <summary>How one extension ran.</summary>
public sealed record ManagementExtensionStat
{
    /// <summary>Gets the extension id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets how many times it ran.</summary>
    public long Invocations { get; init; }

    /// <summary>Gets how many runs failed.</summary>
    public long Failures { get; init; }

    /// <summary>Gets how many runs refused a write; an event handler refuses none.</summary>
    public long Rejections { get; init; }

    /// <summary>Gets the median run time, within about a fifth, or <see langword="null"/> before any run.</summary>
    public TimeSpan? P50 { get; init; }

    /// <summary>Gets the 95th-percentile run time, within about a fifth, or <see langword="null"/> before any run.</summary>
    public TimeSpan? P95 { get; init; }
}
```

Create `src/MMLib.Alvo.Abstractions/Management/DeliveryFixture.cs`:

```csharp
using System.Text.Json;

namespace MMLib.Alvo.Management;

/// <summary>
/// A delivery to reproduce outside the instance it ran on: the handler, the attempt, and the event's envelope with every
/// field value that is not a key replaced by <c>&lt;redacted:FIELD&gt;</c>.
/// </summary>
/// <remarks>
/// Kept: the envelope's attributes (id, type, subject, time, tenant, caller, chain depth), the record's <c>id</c> and
/// <c>tenant_id</c>, fields of type <c>ref</c> and <c>enum</c>, the changed-field names, and empty values. Everything else is
/// redacted — an application event's payload keeps only <c>id</c>. Fill in what a reproduction needs before delivering it.
/// </remarks>
public sealed record DeliveryFixture
{
    /// <summary>Gets the handler the delivery is for.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets the delivery it was taken from.</summary>
    public required Guid Delivery { get; init; }

    /// <summary>Gets the attempt the handler is told it is on, from 1.</summary>
    public required int Attempt { get; init; }

    /// <summary>Gets the redacted CloudEvents envelope.</summary>
    public required JsonElement Envelope { get; init; }

    /// <summary>Gets the JSON pointers, into <see cref="Envelope"/>, of every value that was redacted.</summary>
    public IReadOnlyList<string> Redacted { get; init; } = [];
}
```

Create `src/MMLib.Alvo.Abstractions/Management/ManagementDeliveryNotFoundException.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>No delivery with that id was recorded.</summary>
public sealed class ManagementDeliveryNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementDeliveryNotFoundException"/> class.</summary>
    public ManagementDeliveryNotFoundException()
        : base("No delivery with that id was recorded.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementDeliveryNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ManagementDeliveryNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementDeliveryNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementDeliveryNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance naming the delivery that was asked for.</summary>
    /// <param name="delivery">The delivery id.</param>
    public ManagementDeliveryNotFoundException(Guid delivery)
        : base($"No delivery '{delivery}' was recorded. List the deliveries to find its id.") => Delivery = delivery;

    /// <summary>Gets the delivery id that was asked for.</summary>
    public Guid Delivery { get; }
}
```

Create `src/MMLib.Alvo.Abstractions/Management/ManagementExtensionNotFoundException.cs` — the same shape: default message `"This host registers no extension by that name."`, `Extension` initialised to `string.Empty` in the three standard constructors, and:

```csharp
    /// <summary>Initializes a new instance naming the extension that was asked for and the ones that exist.</summary>
    /// <param name="extension">The id asked for.</param>
    /// <param name="registered">The ids this host registers.</param>
    public ManagementExtensionNotFoundException(string extension, IReadOnlyList<string> registered)
        : base($"This host registers no extension '{extension}'. It registers: {(registered is { Count: > 0 } ? string.Join(", ", registered) : "none")}.")
        => Extension = extension;

    /// <summary>Gets the id asked for.</summary>
    public string Extension { get; }
```

Create `src/MMLib.Alvo.Abstractions/Management/ManagementConflictException.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// The operation is well-formed and cannot be applied to what it names in the state it is in — a delivery that is running,
/// an extension of a kind that cannot be switched off. The message says what to do instead.
/// </summary>
public sealed class ManagementConflictException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementConflictException"/> class.</summary>
    public ManagementConflictException()
        : base("The operation cannot be applied in the state its target is in.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementConflictException"/> class.</summary>
    /// <param name="message">What stands in the way, and what to do instead.</param>
    public ManagementConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementConflictException"/> class.</summary>
    /// <param name="message">What stands in the way, and what to do instead.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

Append to `src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs`:

```csharp
    /// <summary>
    /// Gets whether an operator paused it: its deliveries are still recorded, wait, and run in order once it is resumed.
    /// </summary>
    public bool Paused { get; init; }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*ManagementOperationsModelsTests'`.
Expected: PASS. Accept the grown Abstractions baseline (nine records, three exceptions, `ManagementExtension.Paused`) and dispatch `alvo-snapshot-judge`.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Management/ManagementOperationsModels.cs src/MMLib.Alvo.Abstractions/Management/DeliveryFixture.cs src/MMLib.Alvo.Abstractions/Management/ManagementDeliveryNotFoundException.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtensionNotFoundException.cs src/MMLib.Alvo.Abstractions/Management/ManagementConflictException.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs test/MMLib.Alvo.Abstractions.Tests/Management/ManagementOperationsModelsTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management/ManagementOperationsModels.cs src/MMLib.Alvo.Abstractions/Management/DeliveryFixture.cs src/MMLib.Alvo.Abstractions/Management/ManagementDeliveryNotFoundException.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtensionNotFoundException.cs src/MMLib.Alvo.Abstractions/Management/ManagementConflictException.cs src/MMLib.Alvo.Abstractions/Management/ManagementExtension.cs test/MMLib.Alvo.Abstractions.Tests/Management/ManagementOperationsModelsTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): the operations port's records, its fixture and its refusals

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 6: The operations port and its level rows

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Management/IAlvoOperations.cs`
- Modify: `src/MMLib.Alvo/Management/Access/ManagementOperation.cs`, `src/MMLib.Alvo/Management/Access/ManagementOperations.cs`
- Test: `test/MMLib.Alvo.Tests/Management/OperationsLevelTests.cs`; baseline `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Consumes: the records and refusals of Task 5.
- Produces:
  - public `interface IAlvoOperations` (Abstractions `MMLib.Alvo.Management`) — `Task<ManagementDeliveryPage> ListDeliveriesAsync(string project, ManagementDeliveryQuery query, CancellationToken ct = default)`; `Task<ManagementDelivery> GetDeliveryAsync(string project, Guid delivery, CancellationToken ct = default)`; `Task<ManagementDelivery> RedeliverAsync(string project, Guid delivery, string? reason, CancellationToken ct = default)`; `Task<ManagementReplayPlan> ReplayAsync(string project, ManagementReplayRequest request, CancellationToken ct = default)`; `Task<DeliveryFixture> GetFixtureAsync(string project, Guid delivery, CancellationToken ct = default)`; `Task<ManagementExtension> SetPausedAsync(string project, string subscriber, bool paused, string? reason, CancellationToken ct = default)`; `Task<ManagementExtension> SetEnabledAsync(string project, string extension, bool enabled, string? reason, CancellationToken ct = default)`; `Task<ManagementDelivery> RunScheduleAsync(string project, string schedule, CancellationToken ct = default)`; `Task<ManagementExtensionStats> GetExtensionStatsAsync(string project, CancellationToken ct = default)`.
  - internal `ManagementOperation.ListDeliveries`, `GetDelivery`, `RedeliverDelivery`, `ReplayDeliveries`, `GetDeliveryFixture`, `PauseSubscriber`, `SetExtensionEnabled`, `RunSchedule`, `GetExtensionStats`, with levels Viewer, Viewer, Developer, Developer, Developer, Developer, Admin, Developer, Viewer.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Tests/Management/OperationsLevelTests.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The operations routes' levels, as spec 2026-10-14 §4.8's table writes them: reads Viewer, changes Developer, switching code off Admin.</summary>
public sealed class OperationsLevelTests
{
    [Theory]
    [InlineData("ListDeliveries", "Viewer")]
    [InlineData("GetDelivery", "Viewer")]
    [InlineData("GetExtensionStats", "Viewer")]
    [InlineData("RedeliverDelivery", "Developer")]
    [InlineData("ReplayDeliveries", "Developer")]
    [InlineData("GetDeliveryFixture", "Developer")]
    [InlineData("PauseSubscriber", "Developer")]
    [InlineData("RunSchedule", "Developer")]
    [InlineData("SetExtensionEnabled", "Admin")]
    public void An_operations_route_requires_its_level(string operation, string level) =>
        ManagementOperations.RequiredLevel(Enum.Parse<ManagementOperation>(operation)).ToString().ShouldBe(level);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*OperationsLevelTests'`
Expected: FAIL — `Enum.Parse` finds no `ListDeliveries` member.

- [ ] **Step 3: The port**

Create `src/MMLib.Alvo.Abstractions/Management/IAlvoOperations.cs`:

```csharp
namespace MMLib.Alvo.Management;

/// <summary>
/// <b>The operator's surface over host code that runs after a commit</b>: read handler deliveries, run a dead one again,
/// replay a window, pause a handler, take a delivery away as a redacted fixture, and see how each extension ran. The admin
/// dashboard calls it in-process; an agent and the CLI reach the same members over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member gates itself, as <see cref="IAlvoManagement"/>'s do</b>, against the same level table: reads need
/// <c>viewer</c>, every change and the fixture need <c>developer</c>, switching an extension off needs <c>admin</c>. An
/// in-process caller that publishes no principal is refused.
/// </para>
/// <para>
/// <b>Every change is audited and idempotent.</b> A pause, resume, redeliver or replay that changed something writes one
/// row to the operations log and one <c>ManagementAudit</c> log event; one that changed nothing — a second pause, a
/// redeliver of a delivery already pending — writes neither and answers the current state. A real replay may carry an
/// idempotency key: a retry under it answers the first replay's count again, with <see cref="ManagementReplayPlan.Replayed"/>.
/// </para>
/// <para>
/// <b>A redelivered or replayed delivery runs its handler again with a fresh attempt budget</b>, its history kept; its
/// condition is not evaluated again — it was decided when the event was fanned out.
/// </para>
/// </remarks>
public interface IAlvoOperations
{
    /// <summary>A page of deliveries, newest first, without payloads.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="query">Which deliveries, and the page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The page and the next page's cursor.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>viewer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementRequestException">An unknown status, a page size outside 1–200, or a window that ends before it starts.</exception>
    Task<ManagementDeliveryPage> ListDeliveriesAsync(string project, ManagementDeliveryQuery query, CancellationToken ct = default);

    /// <summary>One delivery and its attempts.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="delivery">The delivery id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The delivery with <see cref="ManagementDelivery.History"/> filled.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>viewer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementDeliveryNotFoundException">No such delivery was recorded.</exception>
    Task<ManagementDelivery> GetDeliveryAsync(string project, Guid delivery, CancellationToken ct = default);

    /// <summary>
    /// Runs a finished delivery — dead, succeeded or cancelled — again: back to pending, due now, with a fresh attempt
    /// budget. A dead delivery that held its record's later deliveries back stops holding them once it succeeds.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="delivery">The delivery id.</param>
    /// <param name="reason">Why, kept in the audit; at most 500 characters, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The delivery as it is now; one already pending or retrying is returned unchanged.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementDeliveryNotFoundException">No such delivery was recorded.</exception>
    /// <exception cref="ManagementConflictException">It is running, or its handler is not registered in this host.</exception>
    /// <exception cref="ManagementRequestException">The reason is too long or holds a control character.</exception>
    Task<ManagementDelivery> RedeliverAsync(string project, Guid delivery, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Counts (a dry run) or runs again (a real run) one handler's finished deliveries of one status within a window of event
    /// time. A real run resets at most 10 000 deliveries.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="request">The handler, the status, the window, whether to only count, the reason and the key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count and the first 20 ids.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementExtensionNotFoundException">This host registers no such handler.</exception>
    /// <exception cref="ManagementRequestException">
    /// A status that is not finished, a window that ends before it starts, more than 10 000 deliveries, a key on a dry run or
    /// one the caller cannot hold, or a bad reason.
    /// </exception>
    /// <exception cref="Data.AlvoIdempotencyConflictException">The key was spent by this caller on a different replay.</exception>
    Task<ManagementReplayPlan> ReplayAsync(string project, ManagementReplayRequest request, CancellationToken ct = default);

    /// <summary>A delivery as a fixture: its envelope with every non-key value redacted, to reproduce elsewhere.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="delivery">The delivery id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fixture.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementDeliveryNotFoundException">No such delivery was recorded.</exception>
    /// <exception cref="ManagementConflictException">The outbox no longer holds its event.</exception>
    Task<DeliveryFixture> GetFixtureAsync(string project, Guid delivery, CancellationToken ct = default);

    /// <summary>
    /// Pauses or resumes a handler on every instance: while paused its deliveries are recorded and wait; once resumed they
    /// run in order.
    /// </summary>
    /// <param name="project">The project name.</param>
    /// <param name="subscriber">The handler id.</param>
    /// <param name="paused">Whether it should be paused.</param>
    /// <param name="reason">Why, kept on the pause and in the audit; at most 500 characters, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The handler's catalog entry as it is now.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementExtensionNotFoundException">This host registers no such handler.</exception>
    /// <exception cref="ManagementRequestException">The reason is too long or holds a control character.</exception>
    Task<ManagementExtension> SetPausedAsync(string project, string subscriber, bool paused, string? reason, CancellationToken ct = default);

    /// <summary>Switches an extension that may be switched off — a validate hook — off or on.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="extension">The extension id.</param>
    /// <param name="enabled">Whether it should run.</param>
    /// <param name="reason">Why, kept in the audit, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The extension's catalog entry as it is now.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>admin</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementExtensionNotFoundException">This host registers no such extension.</exception>
    /// <exception cref="ManagementConflictException">Its kind cannot be switched off — an event handler is paused instead.</exception>
    Task<ManagementExtension> SetEnabledAsync(string project, string extension, bool enabled, string? reason, CancellationToken ct = default);

    /// <summary>Runs a schedule now, as a delivery of its own.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="schedule">The schedule id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The delivery that will run it.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>developer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    /// <exception cref="ManagementExtensionNotFoundException">This host registers no such schedule.</exception>
    Task<ManagementDelivery> RunScheduleAsync(string project, string schedule, CancellationToken ct = default);

    /// <summary>How each extension ran on this instance over the last day.</summary>
    /// <param name="project">The project name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The numbers, scoped to <c>this-instance</c>.</returns>
    /// <exception cref="ManagementForbiddenException">The caller does not reach <c>viewer</c>.</exception>
    /// <exception cref="ManagementProjectNotFoundException">This instance does not serve that project.</exception>
    Task<ManagementExtensionStats> GetExtensionStatsAsync(string project, CancellationToken ct = default);
}
```

- [ ] **Step 4: The level rows**

Append to `src/MMLib.Alvo/Management/Access/ManagementOperation.cs`, before the closing brace:

```csharp
    /// <summary>List handler deliveries, without payloads.</summary>
    ListDeliveries,

    /// <summary>Read one delivery and its attempts.</summary>
    GetDelivery,

    /// <summary>Run a finished delivery again.</summary>
    RedeliverDelivery,

    /// <summary>Count or run again a window of finished deliveries.</summary>
    ReplayDeliveries,

    /// <summary>Take a delivery away as a redacted fixture — the shape of a payload, if none of its values.</summary>
    GetDeliveryFixture,

    /// <summary>Pause or resume a handler.</summary>
    PauseSubscriber,

    /// <summary>Switch an extension off or on.</summary>
    SetExtensionEnabled,

    /// <summary>Run a schedule now.</summary>
    RunSchedule,

    /// <summary>Read how each extension ran on this instance.</summary>
    GetExtensionStats,
```

In `src/MMLib.Alvo/Management/Access/ManagementOperations.cs` add, before `CheckExpression`:

```csharp
        [ManagementOperation.ListDeliveries] = ManagementLevel.Viewer,
        [ManagementOperation.GetDelivery] = ManagementLevel.Viewer,
        [ManagementOperation.GetExtensionStats] = ManagementLevel.Viewer,
```

and before `SetAiConnection`:

```csharp
        [ManagementOperation.RedeliverDelivery] = ManagementLevel.Developer,
        [ManagementOperation.ReplayDeliveries] = ManagementLevel.Developer,
        [ManagementOperation.GetDeliveryFixture] = ManagementLevel.Developer,
        [ManagementOperation.PauseSubscriber] = ManagementLevel.Developer,
        [ManagementOperation.RunSchedule] = ManagementLevel.Developer,
        [ManagementOperation.SetExtensionEnabled] = ManagementLevel.Admin,
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*OperationsLevelTests'`, then `--filter-class '*ManagementOperationsTests'` (the existing "every member is listed" fact).
Expected: PASS. Accept the grown Abstractions baseline (the port) and dispatch `alvo-snapshot-judge`. `ManagementContractTests.Every_member_of_IAlvoManagement_has_an_http_route` is untouched (the routes arrive later, and that task widens it to both ports).

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Management/IAlvoOperations.cs src/MMLib.Alvo/Management/Access/ManagementOperation.cs src/MMLib.Alvo/Management/Access/ManagementOperations.cs test/MMLib.Alvo.Tests/Management/OperationsLevelTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Management/IAlvoOperations.cs src/MMLib.Alvo/Management/Access/ManagementOperation.cs src/MMLib.Alvo/Management/Access/ManagementOperations.cs test/MMLib.Alvo.Tests/Management/OperationsLevelTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(management): define the operations port and its levels

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: `DeliveryFixtures` — a fixture with every non-key scalar redacted

**Files:**
- Create: `src/MMLib.Alvo/Management/Internal/DeliveryFixtures.cs`
- Test: `test/MMLib.Alvo.Tests/Management/DeliveryFixturesTests.cs`

**Interfaces:**
- Consumes: `StoredDelivery` (Task 2), `DeliveryFixture` (Task 5), `EnvelopeReading.TryReadDataType`, `AlvoEventAttributes.Data`/`.Type`, `AlvoManagedColumns.Id`/`.TenantId`, `SchemaModel`.
- Produces: `internal static class DeliveryFixtures` with `DeliveryFixture Of(StoredDelivery delivery, string payload, SchemaModel schema)` and `string Placeholder(string field)` (`"<redacted:" + field + ">"`).

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Management/DeliveryFixturesTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;

namespace MMLib.Alvo.Tests.Management;

/// <summary>A fixture contains no non-key scalar (spec 2026-10-14 HX-06 acceptance (5)): keys, references and enums stay, every other value is redacted.</summary>
public sealed class DeliveryFixturesTests
{
    private static readonly Guid _ticket = Guid.Parse("0192f1a4-0000-7000-8000-00000000000a");
    private static readonly Guid _customer = Guid.Parse("0192f1a4-0000-7000-8000-00000000000b");
    private static readonly Guid _tenant = Guid.Parse("0192f1a4-0000-7000-8000-00000000000c");

    private static readonly SchemaModel _schema = new([new EntitySchema
    {
        Name = "tickets",
        Fields =
        [
            new FieldSchema { Name = "title", Type = FieldType.String },
            new FieldSchema { Name = "total", Type = FieldType.Decimal },
            new FieldSchema { Name = "is_urgent", Type = FieldType.Boolean },
            new FieldSchema { Name = "external_ref", Type = FieldType.Uuid },
            new FieldSchema { Name = "metadata", Type = FieldType.Json },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "closed"] },
            new FieldSchema { Name = "customer_id", Type = FieldType.Ref },
            new FieldSchema { Name = "note", Type = FieldType.Text },
        ],
    }]);

    [Fact]
    public void A_fixture_keeps_keys_references_and_enums_and_redacts_every_other_value()
    {
        var fixture = DeliveryFixtures.Of(Delivery(), Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), _schema);

        var record = fixture.Envelope.GetProperty("data").GetProperty("record");
        record.GetProperty("id").GetString().ShouldBe(_ticket.ToString());
        record.GetProperty("tenant_id").GetString().ShouldBe(_tenant.ToString());
        record.GetProperty("customer_id").GetString().ShouldBe(_customer.ToString());
        record.GetProperty("status").GetString().ShouldBe("open");
        foreach (var field in new[] { "title", "total", "is_urgent", "external_ref", "metadata" })
        {
            record.GetProperty(field).GetString().ShouldBe($"<redacted:{field}>");
        }

        record.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null, "an empty value carries nothing to hide and stays empty");
        fixture.Redacted.ShouldContain("/data/record/title");
        fixture.Redacted.ShouldContain("/data/old_record/title");
        fixture.Redacted.ShouldNotContain("/data/record/note");
    }

    [Fact]
    public void No_original_non_key_value_survives_anywhere_in_the_fixture()
    {
        var text = DeliveryFixtures.Of(Delivery(), Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), _schema).Envelope.GetRawText();

        text.ShouldNotContain("Printer jam");
        text.ShouldNotContain("12.5");
        text.ShouldNotContain("legacy-system");
        text.ShouldNotContain("true", Case.Sensitive);
    }

    [Theory]
    [InlineData("orders.shipped", "an application event keeps only id")]
    [InlineData("entity.retired_tickets.updated", "an entity no longer declared keeps only id")]
    public void An_event_with_no_declared_entity_keeps_only_id(string type, string because)
    {
        var record = DeliveryFixtures.Of(Delivery(), Envelope(type, Row("Printer jam", 12.5m)), _schema)
            .Envelope.GetProperty("data").GetProperty("record");

        record.GetProperty("id").GetString().ShouldBe(_ticket.ToString(), because);
        record.GetProperty("status").GetString().ShouldBe("<redacted:status>", because);
        record.GetProperty("customer_id").GetString().ShouldBe("<redacted:customer_id>", because);
        record.GetProperty("tenant_id").GetString().ShouldBe("<redacted:tenant_id>", because);
    }

    [Fact]
    public void The_envelope_s_attributes_and_the_changed_names_stay()
    {
        var fixture = DeliveryFixtures.Of(Delivery(), Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), _schema);

        fixture.Envelope.GetProperty("type").GetString().ShouldBe("entity.tickets.updated");
        fixture.Envelope.GetProperty("tenantid").GetString().ShouldBe(_tenant.ToString());
        fixture.Envelope.GetProperty("data").GetProperty("changed").EnumerateArray().Select(name => name.GetString()).ShouldContain("title");
        fixture.Subscriber.ShouldBe("send-confirmation");
        fixture.Attempt.ShouldBe(3);
    }

    private static StoredDelivery Delivery() => new()
    {
        Id = Guid.Parse("0192f1a4-0000-7000-8000-000000000001"),
        EventId = Guid.Parse("0192f1a4-0000-7000-8000-000000000002"),
        Subscriber = "send-confirmation",
        PartitionKey = $"tickets:{_ticket}",
        Kind = "handler",
        Status = "dead",
        Attempts = 3,
        NotBefore = DateTimeOffset.UnixEpoch,
    };

    private static AlvoRecord Row(string title, decimal total) => new(new Dictionary<string, object?>
    {
        ["id"] = _ticket,
        ["tenant_id"] = _tenant,
        ["title"] = title,
        ["total"] = total,
        ["is_urgent"] = true,
        ["external_ref"] = "legacy-system",
        ["metadata"] = JsonDocument.Parse("""{"source":"legacy-system"}""").RootElement,
        ["status"] = "open",
        ["customer_id"] = _customer,
        ["note"] = null,
    });

    private static string Envelope(string type, AlvoRecord record) => AlvoEventJson.Write(new AlvoEvent
    {
        Id = Guid.Parse("0192f1a4-0000-7000-8000-000000000002"),
        Source = AlvoEvent.DefaultSource,
        Type = type,
        Time = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
        Subject = $"tickets/{_ticket}",
        PartitionKey = type.StartsWith("entity.", StringComparison.Ordinal) ? $"tickets:{_ticket}" : $"orders.shipped:{_ticket}",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        TenantId = _tenant.ToString(),
        Data = new AlvoEventData { Record = record, OldRecord = record, Changed = ["title"] },
    });
}
```

(If `AlvoEventJson.Write` refuses a `JsonElement` value in a record, store `metadata` as its JSON text instead — the fact is about redaction, not the writer.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DeliveryFixturesTests'`
Expected: build FAIL — `DeliveryFixtures` does not exist.

- [ ] **Step 3: Implement**

Create `src/MMLib.Alvo/Management/Internal/DeliveryFixtures.cs`:

```csharp
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// A delivery's envelope as a fixture: the record images keep their keys, references and enum values, empty values stay
/// empty, and every other value becomes <c>&lt;redacted:FIELD&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Redaction is decided against the schema the instance runs now.</b> A data event of an entity the schema still
/// declares keeps <c>id</c>, <c>tenant_id</c> and its <c>ref</c> and <c>enum</c> fields; an application event, or an entity
/// the schema no longer declares, has no declared keys and keeps only <c>id</c>. When in doubt a value is redacted.
/// </para>
/// <para>
/// <b>A <c>uuid</c> field is redacted</b>: it may be a key into another system, or a person's identifier, and nothing in
/// the schema says which. A <c>json</c> field is replaced whole. The envelope's attributes stay — they identify, they carry
/// no field values — and so do the changed-field names.
/// </para>
/// </remarks>
internal static class DeliveryFixtures
{
    private static readonly string[] _images = ["record", "old_record"];

    internal static DeliveryFixture Of(StoredDelivery delivery, string payload, SchemaModel schema)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(schema);
        var envelope = JsonNode.Parse(payload) as JsonObject ?? throw new JsonException("The stored envelope is not a JSON object.");
        var kept = KeptFields(envelope, schema);
        List<string> redacted = envelope[AlvoEventAttributes.Data] is JsonObject data
            ? [.. _images.SelectMany(image => Redact(data, image, kept))]
            : [];

        return new DeliveryFixture
        {
            Subscriber = delivery.Subscriber,
            Delivery = delivery.Id,
            Attempt = Math.Max(1, delivery.Attempts),
            Envelope = JsonSerializer.SerializeToElement(envelope),
            Redacted = redacted,
        };
    }

    internal static string Placeholder(string field) => $"<redacted:{field}>";

    private static HashSet<string> KeptFields(JsonObject envelope, SchemaModel schema)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal) { AlvoManagedColumns.Id };
        if (envelope[AlvoEventAttributes.Type] is JsonValue type
            && type.TryGetValue<string>(out var name)
            && EnvelopeReading.TryReadDataType(name, out var entity, out _)
            && schema.Entities.FirstOrDefault(candidate => candidate.Name == entity) is { } declared)
        {
            kept.Add(AlvoManagedColumns.TenantId);
            kept.UnionWith(declared.Fields.Where(field => field.Type is FieldType.Ref or FieldType.Enum).Select(field => field.Name));
        }

        return kept;
    }

    private static List<string> Redact(JsonObject data, string image, HashSet<string> kept)
    {
        if (data[image] is not JsonObject row)
        {
            return [];
        }

        var redacted = new List<string>();
        foreach (var field in row.Select(member => member.Key).ToList())
        {
            if (!kept.Contains(field) && row[field] is not null)
            {
                row[field] = Placeholder(field);
                redacted.Add($"/{AlvoEventAttributes.Data}/{image}/{Pointer(field)}");
            }
        }

        return redacted;
    }

    private static string Pointer(string field) =>
        field.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DeliveryFixturesTests'`
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Management/Internal/DeliveryFixtures.cs test/MMLib.Alvo.Tests/Management/DeliveryFixturesTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Management/Internal/DeliveryFixtures.cs test/MMLib.Alvo.Tests/Management/DeliveryFixturesTests.cs
git commit -m "feat(management): redact a delivery into a fixture that keeps only keys

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: `ExtensionStats` — per-instance numbers from a `MeterListener`

**Files:**
- Create: `src/MMLib.Alvo/Extensions/Internal/LatencyHistogram.cs`, `src/MMLib.Alvo/Extensions/Internal/ExtensionStats.cs`
- Modify: `src/MMLib.Alvo/Extensions/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/LatencyHistogramTests.cs`, `test/MMLib.Alvo.Tests/Extensions/ExtensionStatsTests.cs`

**Interfaces:**
- Consumes: `AlvoEventMetrics.MeterName` (`"MMLib.Alvo.Events"`), `AlvoDeliveryMetrics.DurationName` (`"alvo.delivery.duration"`), `.SubscriberTag`, `.OutcomeTag`, `.Durations`; `ManagementExtensionStats`/`ManagementExtensionStat` (Task 5).
- Produces: `internal static class LatencyHistogram` (`const int Bins = 128`, `int Bin(double milliseconds)`, `double UpperBound(int bin)`, `TimeSpan? Percentile(long[] bins, long count, double quantile)`); `internal sealed class ExtensionStats : IHostedService, IDisposable` (ctor `(TimeProvider time)`, `const string Scope = "this-instance"`, `static TimeSpan Window` = 24 h, `void Record(string subscriber, double milliseconds, bool failed)`, `ManagementExtensionStats Report(IReadOnlyList<string> ids)`), registered as a singleton and as a hosted service.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Extensions/LatencyHistogramTests.cs`:

```csharp
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Log-scale bins a quarter-octave wide: any duration is reported within about a fifth of itself, in fixed memory.</summary>
public sealed class LatencyHistogramTests
{
    [Theory]
    [InlineData(0.05)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(1_500)]
    [InlineData(3_600_000)]
    public void A_duration_falls_in_a_bin_whose_upper_bound_is_within_a_fifth_of_it(double milliseconds)
    {
        var upper = LatencyHistogram.UpperBound(LatencyHistogram.Bin(milliseconds));

        upper.ShouldBeGreaterThanOrEqualTo(Math.Min(milliseconds, LatencyHistogram.UpperBound(0)));
        if (milliseconds > LatencyHistogram.UpperBound(0))
        {
            (upper / milliseconds).ShouldBeLessThanOrEqualTo(1.19 + 1e-9);
        }
    }

    [Fact]
    public void A_percentile_is_the_bound_of_the_bin_that_holds_its_rank()
    {
        var bins = new long[LatencyHistogram.Bins];
        foreach (var duration in Enumerable.Range(1, 100))
        {
            bins[LatencyHistogram.Bin(duration)]++;
        }

        var median = LatencyHistogram.Percentile(bins, 100, 0.5)!.Value.TotalMilliseconds;
        var p95 = LatencyHistogram.Percentile(bins, 100, 0.95)!.Value.TotalMilliseconds;

        median.ShouldBeInRange(50, 60);
        p95.ShouldBeInRange(95, 114);
    }

    [Fact]
    public void Nothing_recorded_has_no_percentile() =>
        LatencyHistogram.Percentile(new long[LatencyHistogram.Bins], 0, 0.5).ShouldBeNull();
}
```

Create `test/MMLib.Alvo.Tests/Extensions/ExtensionStatsTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Stats state their scope (spec 2026-10-14 HX-06 acceptance (8)) and count the last 24 hours of this process.</summary>
public sealed class ExtensionStatsTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 24, 9, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// Every fact names an extension of its own: Alvo's meters are process-wide, so a handler id another test's deliveries
    /// also use would be counted here too.
    /// </summary>
    private readonly string _id = $"stats-{Guid.NewGuid():N}";

    [Fact]
    public void A_report_says_it_covers_this_instance_over_a_day()
    {
        using var stats = new ExtensionStats(new FakeTimeProvider(_start));

        var report = stats.Report([_id]);

        report.Scope.ShouldBe("this-instance");
        report.Window.ShouldBe(TimeSpan.FromHours(24));
        report.Since.ShouldBe(_start, "nothing earlier than this process's start is counted");
        report.Items.ShouldHaveSingleItem().ShouldBe(new() { Id = _id });
    }

    [Fact]
    public void Runs_and_failures_are_counted_per_extension_with_their_percentiles()
    {
        using var stats = new ExtensionStats(new FakeTimeProvider(_start));
        foreach (var duration in Enumerable.Range(1, 20))
        {
            stats.Record(_id, duration, failed: duration > 18);
        }

        var item = stats.Report([_id, $"{_id}-other"]).Items[0];

        item.Invocations.ShouldBe(20);
        item.Failures.ShouldBe(2);
        item.Rejections.ShouldBe(0);
        item.P50!.Value.TotalMilliseconds.ShouldBeInRange(10, 12);
        item.P95!.Value.TotalMilliseconds.ShouldBeInRange(19, 23);
    }

    [Fact]
    public void A_run_older_than_a_day_is_no_longer_counted()
    {
        var clock = new FakeTimeProvider(_start);
        using var stats = new ExtensionStats(clock);
        stats.Record(_id, 5, failed: false);

        clock.Advance(TimeSpan.FromHours(23));
        stats.Report([_id]).Items[0].Invocations.ShouldBe(1);
        clock.Advance(TimeSpan.FromHours(2));
        stats.Report([_id]).Items[0].Invocations.ShouldBe(0);
    }

    [Fact]
    public void A_delivery_s_duration_measurement_reaches_the_report()
    {
        var subscriber = _id;
        using var stats = new ExtensionStats(new FakeTimeProvider(_start));

        AlvoDeliveryMetrics.Durations.Record(
            42,
            new KeyValuePair<string, object?>(AlvoDeliveryMetrics.SubscriberTag, subscriber),
            new KeyValuePair<string, object?>(AlvoDeliveryMetrics.OutcomeTag, "dead"));

        var item = stats.Report([subscriber]).Items[0];
        item.Invocations.ShouldBe(1);
        item.Failures.ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ExtensionStatsTests'`
Expected: build FAIL — `ExtensionStats` does not exist.

- [ ] **Step 3: Implement**

Create `src/MMLib.Alvo/Extensions/Internal/LatencyHistogram.cs`:

```csharp
namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// A fixed-memory latency histogram: 128 log-scale bins a quarter-octave wide from 0.1 ms, so a reported percentile — the
/// upper bound of the bin holding its rank — is within 19 % of the true value, up to about five days.
/// </summary>
internal static class LatencyHistogram
{
    internal const int Bins = 128;

    private const double Smallest = 0.1;
    private const double StepsPerOctave = 4;

    internal static int Bin(double milliseconds) =>
        milliseconds <= Smallest
            ? 0
            : Math.Clamp((int)Math.Ceiling(Math.Log2(milliseconds / Smallest) * StepsPerOctave) - 1, 0, Bins - 1);

    internal static double UpperBound(int bin) => Smallest * Math.Pow(2, (bin + 1) / StepsPerOctave);

    internal static TimeSpan? Percentile(long[] bins, long count, double quantile)
    {
        if (count == 0)
        {
            return null;
        }

        var rank = (long)Math.Ceiling(quantile * count);
        long seen = 0;
        for (var bin = 0; bin < bins.Length; bin++)
        {
            seen += bins[bin];
            if (seen >= rank)
            {
                return TimeSpan.FromMilliseconds(UpperBound(bin));
            }
        }

        return TimeSpan.FromMilliseconds(UpperBound(Bins - 1));
    }
}
```

Create `src/MMLib.Alvo/Extensions/Internal/ExtensionStats.cs`:

```csharp
using Microsoft.Extensions.Hosting;

using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Management;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// How each extension ran in this process over the last day, read off the delivery duration measurements with a
/// <see cref="MeterListener"/> and kept in 24 hourly buckets per extension.
/// </summary>
/// <remarks>
/// <para>
/// <b>"This instance" is this process.</b> Alvo's meters are process-wide, so two hosts in one process — a test run — see
/// each other's runs; another replica's are never seen. Nothing is stored: a restart starts the day over, and
/// <see cref="ManagementExtensionStats.Since"/> says from when.
/// </para>
/// <para>
/// <b>One measurement carries everything:</b> <c>alvo.delivery.duration</c> is recorded once per answered attempt with the
/// subscriber and the outcome, so a run is a measurement, a failure is an outcome of <c>failed</c> or <c>dead</c>, and the
/// percentiles come from the same values. Write hooks' rejections join when write hooks exist; a handler refuses nothing.
/// </para>
/// </remarks>
internal sealed class ExtensionStats : IHostedService, IDisposable
{
    internal const string Scope = "this-instance";

    private const int Hours = 24;

    private readonly TimeProvider _time;
    private readonly DateTimeOffset _started;
    private readonly ConcurrentDictionary<string, Buckets> _extensions = new(StringComparer.Ordinal);
    private readonly MeterListener _listener;

    public ExtensionStats(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _started = time.GetUtcNow();
        _listener = new MeterListener { InstrumentPublished = Publish };
        _listener.SetMeasurementEventCallback<double>(OnDuration);
        _listener.Start();
    }

    internal static TimeSpan Window { get; } = TimeSpan.FromHours(Hours);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _listener.Dispose();

    internal void Record(string subscriber, double milliseconds, bool failed) =>
        _extensions.GetOrAdd(subscriber, _ => new Buckets()).Record(HourOf(_time.GetUtcNow()), milliseconds, failed);

    internal ManagementExtensionStats Report(IReadOnlyList<string> ids)
    {
        var hour = HourOf(_time.GetUtcNow());
        var oldest = DateTimeOffset.FromUnixTimeSeconds((hour - Hours + 1) * 3600);

        return new ManagementExtensionStats
        {
            Scope = Scope,
            Window = Window,
            Since = oldest > _started ? oldest : _started,
            Items = [.. ids.Select(id => _extensions.TryGetValue(id, out var buckets) ? buckets.Stat(id, hour) : new ManagementExtensionStat { Id = id })],
        };
    }

    private static void Publish(Instrument instrument, MeterListener listener)
    {
        if (instrument.Meter.Name == AlvoEventMetrics.MeterName && instrument.Name == AlvoDeliveryMetrics.DurationName)
        {
            listener.EnableMeasurementEvents(instrument);
        }
    }

    private void OnDuration(Instrument instrument, double milliseconds, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        string? subscriber = null;
        string? outcome = null;
        foreach (var tag in tags)
        {
            subscriber = tag.Key == AlvoDeliveryMetrics.SubscriberTag ? tag.Value as string : subscriber;
            outcome = tag.Key == AlvoDeliveryMetrics.OutcomeTag ? tag.Value as string : outcome;
        }

        if (subscriber is not null)
        {
            Record(subscriber, milliseconds, outcome is "failed" or "dead");
        }
    }

    private static long HourOf(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / 3600;

    private sealed class Buckets
    {
        private readonly Lock _gate = new();
        private readonly long[] _hours = Enumerable.Repeat(long.MinValue, Hours).ToArray();
        private readonly long[] _runs = new long[Hours];
        private readonly long[] _failures = new long[Hours];
        private readonly long[][] _bins = [.. Enumerable.Range(0, Hours).Select(_ => new long[LatencyHistogram.Bins])];

        internal void Record(long hour, double milliseconds, bool failed)
        {
            lock (_gate)
            {
                var slot = Slot(hour);
                _runs[slot]++;
                _failures[slot] += failed ? 1 : 0;
                _bins[slot][LatencyHistogram.Bin(milliseconds)]++;
            }
        }

        internal ManagementExtensionStat Stat(string id, long hour)
        {
            lock (_gate)
            {
                var live = Enumerable.Range(0, Hours).Where(slot => _hours[slot] > hour - Hours).ToList();
                var bins = new long[LatencyHistogram.Bins];
                live.ForEach(slot => Add(bins, _bins[slot]));
                var runs = live.Sum(slot => _runs[slot]);

                return new ManagementExtensionStat
                {
                    Id = id,
                    Invocations = runs,
                    Failures = live.Sum(slot => _failures[slot]),
                    P50 = LatencyHistogram.Percentile(bins, runs, 0.5),
                    P95 = LatencyHistogram.Percentile(bins, runs, 0.95),
                };
            }
        }

        private int Slot(long hour)
        {
            var slot = (int)(hour % Hours);
            if (_hours[slot] != hour)
            {
                _hours[slot] = hour;
                _runs[slot] = 0;
                _failures[slot] = 0;
                Array.Clear(_bins[slot]);
            }

            return slot;
        }

        private static void Add(long[] total, long[] bins)
        {
            for (var bin = 0; bin < bins.Length; bin++)
            {
                total[bin] += bins[bin];
            }
        }
    }
}
```

In `src/MMLib.Alvo/Extensions/Setup.cs`, after `services.TryAddSingleton<ExtensionStateGuard>();`:

```csharp
        services.TryAddSingleton(provider => new ExtensionStats(provider.GetRequiredService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExtensionStats>(provider => provider.GetRequiredService<ExtensionStats>()));
```

(add `using Microsoft.Extensions.Hosting;`; `TimeProvider` is registered by the events and auth setups — if `AddAlvoExtensions` can run before them, add `services.TryAddSingleton(TimeProvider.System);` first, the house's idempotent pattern).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*LatencyHistogramTests'` and `--filter-class '*ExtensionStatsTests'`.
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/Internal/LatencyHistogram.cs src/MMLib.Alvo/Extensions/Internal/ExtensionStats.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/LatencyHistogramTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionStatsTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Extensions/Internal/LatencyHistogram.cs src/MMLib.Alvo/Extensions/Internal/ExtensionStats.cs src/MMLib.Alvo/Extensions/Setup.cs test/MMLib.Alvo.Tests/Extensions/LatencyHistogramTests.cs test/MMLib.Alvo.Tests/Extensions/ExtensionStatsTests.cs
git commit -m "feat(extensions): count each extension's runs on this instance from the delivery meter

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 9: `AlvoOperationsService` reads — the gate, a page, one delivery, a fixture, stats

**Files:**
- Create: `src/MMLib.Alvo/Management/Internal/ManagementGate.cs`, `AlvoOperationsService.cs`, `DeliveryViews.cs`, `DeliveryQueries.cs`
- Modify: `src/MMLib.Alvo/Management/Setup.cs`
- Create (tests): `test/MMLib.Alvo.Tests/Management/OperationsTestWorld.cs`, `test/MMLib.Alvo.Tests/Management/ManagementGateTests.cs`, `test/MMLib.Alvo.Tests/Management/AlvoOperationsServiceTests.cs`, `test/_shared/testhost/AuditCapture.cs`
- Modify (tests): `test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj` (link `AuditCapture.cs`)

**Interfaces:**
- Consumes: `IDeliveryStore` reads (Task 2), the records of Task 5, `DeliveryFixtures` (Task 7), `ExtensionStats` (Task 8), `ExtensionCatalog.Items`, `AlvoBootState.Projects`, `ManagementAccessEvaluator.Allows(ManagementOperation, AlvoContext)`, `IAlvoContextAccessor.Principal`, `AlvoEventId.Floor`.
- Produces:
  - `internal sealed class ManagementGate(AlvoBootState boot, IAlvoContextAccessor callers, ManagementAccessEvaluator access)` — `AlvoContext Caller`, `string Actor` (`"<key id>:<user id>"`, `"anonymous"` with no principal), `void Admit(ManagementOperation operation, string project)` (level first, then project — `ManagementForbiddenException`, then `ManagementProjectNotFoundException`).
  - `internal sealed partial class AlvoOperationsService(ManagementGate gate, ExtensionCatalog catalog, ExtensionStats stats, ISchemaRegistry schemas, IDeliveryStore? store = null)` (Task 10 inserts `TimeProvider time, ILogger<AlvoOperationsService> logger` before `store`) with the public read members `ListDeliveriesAsync`, `GetDeliveryAsync`, `GetFixtureAsync`, `GetExtensionStatsAsync` (signatures of `IAlvoOperations`), `internal const int SampleSize = 20`, private `DetailAsync(Guid, CancellationToken)`, `FoundAsync(Guid, CancellationToken)`, `Store`.
  - `internal static class DeliveryViews` — `ManagementDelivery Of(StoredDelivery)`, `ManagementDeliveryAttempt Of(StoredDeliveryAttempt)`.
  - `internal static class DeliveryQueries` — `const int MaxPage = 200`, `FrozenSet<string> Statuses`, `FrozenSet<string> Finished`, `DeliveryFilter Filter(ManagementDeliveryQuery)`, `DeliveryFilter Window(DateTimeOffset? from, DateTimeOffset? to)`, `void EnsureStatus(string? status, FrozenSet<string> allowed, string verb)`.
  - Test-side `OperationsTestWorld` (below) and `AuditCapture : ILoggerProvider` (`IReadOnlyList<string> Lines` — the rendered `ManagementAudit` lines).
  - DI: `ManagementGate` and `AlvoOperationsService` registered as singletons (the port is registered in Task 11).

- [ ] **Step 1: The test world and the audit capture**

Create `test/_shared/testhost/AuditCapture.cs`:

```csharp
using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Keeps every <c>ManagementAudit</c> line a host logs at Warning (spec 2026-10-14 E11), and nothing else.</summary>
internal sealed class AuditCapture : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    internal IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (eventId.Name == "ManagementAudit" && logLevel == LogLevel.Warning)
        {
            _lines.Enqueue(formatter(state, exception));
        }
    }

    public void Dispose()
    {
    }
}
```

In `test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj`, beside the other `_shared` links:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)../_shared/testhost/AuditCapture.cs" Link="_shared/AuditCapture.cs" />
```

Create `test/MMLib.Alvo.Tests/Management/OperationsTestWorld.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// A host registered the way a deployment registers one, over the in-memory delivery store, with one handler on
/// <c>tickets</c>, the project <c>desk</c> booted, and — unless told otherwise — the bootstrap administrator published.
/// </summary>
internal sealed class OperationsTestWorld : IDisposable
{
    internal const string Project = "desk";
    internal const string Handler = "send-confirmation";

    private readonly ServiceProvider _provider;

    internal OperationsTestWorld(bool admitted = true)
    {
        Store = new FakeDeliveryStore(Clock);
        var services = new ServiceCollection();
        services.AddSingleton(ManagementCallers.Bootstrapped(user => user == ManagementCallers.Bootstrap));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddLogging(logging => logging.AddProvider(Audit));
        services.AddAlvo(alvo =>
        {
            alvo.Services.AddSingleton<ISchemaMigrator>(new InMemorySchemaMigrator());
            alvo.Services.AddSingleton<IDeliveryStore>(Store);
            alvo.Entity("tickets", tickets => tickets.OnCreated(Handler, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask));
        });
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<AlvoBootState>().Ready(Project, appliedRevision: 1);
        if (admitted)
        {
            Publish();
        }
    }

    internal ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

    internal FakeDeliveryStore Store { get; }

    internal AuditCapture Audit { get; } = new();

    internal AlvoOperationsService Service => _provider.GetRequiredService<AlvoOperationsService>();

    internal IServiceProvider Services => _provider;

    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fans one event of <paramref name="key"/> out to <paramref name="subscriber"/>, dead when a reason is given, and returns the delivery.</summary>
    internal async Task<StoredDelivery> DeliveryAsync(string key, string? deadReason = null, string subscriber = Handler, bool keepEnvelope = true)
    {
        var @event = Event(key);
        if (keepEnvelope)
        {
            Store.Publish(@event);
        }

        await Store.FanOutAsync([new DeliveryRequest { EventId = @event.Id, Subscriber = subscriber, PartitionKey = key, Kind = "handler", DeadReason = deadReason }], Ct);

        return (await Store.ListAsync(new DeliveryFilter { Limit = 1 }, Ct))[0];
    }

    public void Dispose() => _provider.Dispose();

    private void Publish() =>
        _provider.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = new AlvoContext { User = ManagementCallers.Bootstrap, Roles = new HashSet<Role> { Role.Authenticated } },
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

    private AlvoEvent Event(string key) => new()
    {
        Id = AlvoEventId.Create(Clock.GetUtcNow()),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.created",
        Time = Clock.GetUtcNow(),
        Subject = key.Replace(':', '/'),
        PartitionKey = key,
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData { Record = new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["title"] = "Printer jam" }) },
    };
}
```

- [ ] **Step 2: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Management/ManagementGateTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The operations port's gate is the management surface's own: the level first, then the project.</summary>
public sealed class ManagementGateTests
{
    [Fact]
    public void An_unpublished_caller_is_refused_before_the_project_is_looked_at()
    {
        using var world = new OperationsTestWorld(admitted: false);
        var gate = world.Services.GetRequiredService<ManagementGate>();

        Should.Throw<ManagementForbiddenException>(() => gate.Admit(ManagementOperation.ListDeliveries, "no-such-project"));
    }

    [Fact]
    public void An_admitted_caller_naming_a_project_this_instance_does_not_serve_is_told_which_it_serves()
    {
        using var world = new OperationsTestWorld();
        var gate = world.Services.GetRequiredService<ManagementGate>();

        Should.Throw<ManagementProjectNotFoundException>(() => gate.Admit(ManagementOperation.ListDeliveries, "no-such-project"))
            .Message.ShouldContain(OperationsTestWorld.Project);
    }

    [Fact]
    public void The_actor_names_the_credential_and_the_user()
    {
        using var world = new OperationsTestWorld();

        world.Services.GetRequiredService<ManagementGate>().Actor.ShouldBe($"ops-key:{ManagementCallers.Bootstrap.Value}");
    }
}
```

Create `test/MMLib.Alvo.Tests/Management/AlvoOperationsServiceTests.cs`:

```csharp
using MMLib.Alvo.Events;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The operations port's reads: a page, one delivery, a fixture and the stats.</summary>
public sealed class AlvoOperationsServiceTests
{
    private const string Project = OperationsTestWorld.Project;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_lists_newest_first_and_hands_over_the_next_cursor()
    {
        using var world = new OperationsTestWorld();
        var first = await world.DeliveryAsync("tickets:a");
        var second = await world.DeliveryAsync("tickets:b");

        var page = await world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Limit = 1 }, Ct);
        var rest = await world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Limit = 1, After = page.Next }, Ct);

        page.Items.ShouldHaveSingleItem().Id.ShouldBe(second.Id);
        page.Next.ShouldBe(second.Id);
        rest.Items.ShouldHaveSingleItem().Id.ShouldBe(first.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task A_page_size_outside_one_to_two_hundred_is_refused(int limit)
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Limit = limit }, Ct)))
            .Message.ShouldContain("1 to 200");
    }

    [Fact]
    public async Task An_unknown_status_is_refused_naming_the_known_ones()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Status = "failed" }, Ct)))
            .Message.ShouldContain("pending, running, succeeded, retrying, dead, cancelled");
    }

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_refused()
    {
        using var world = new OperationsTestWorld();
        var now = world.Clock.GetUtcNow();

        await Should.ThrowAsync<ManagementRequestException>(() =>
            world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { From = now, To = now.AddMinutes(-1) }, Ct));
    }

    [Fact]
    public async Task One_delivery_reads_with_its_attempts()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a");
        var claimed = (await world.Store.ClaimAsync(new DeliveryClaim { Claimant = "w", Ordered = [OperationsTestWorld.Handler], BatchSize = 1, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10, OutboxClaimLease = TimeSpan.FromMinutes(5) }, Ct)).Single();
        await world.Store.CompleteAsync(new DeliveryAttempt { Delivery = claimed, Claimant = "w", StartedAt = world.Clock.GetUtcNow(), Duration = TimeSpan.FromMilliseconds(12) }, Ct);

        var read = await world.Service.GetDeliveryAsync(Project, delivery.Id, Ct);

        read.Status.ShouldBe("succeeded");
        read.History.ShouldHaveSingleItem().Outcome.ShouldBe("succeeded");
        read.History[0].Duration.ShouldBe(TimeSpan.FromMilliseconds(12));
    }

    [Fact]
    public async Task An_unknown_delivery_is_not_found()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementDeliveryNotFoundException>(() => world.Service.GetDeliveryAsync(Project, Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task A_fixture_redacts_the_delivery_s_envelope()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a", deadReason: "when-failed");

        var fixture = await world.Service.GetFixtureAsync(Project, delivery.Id, Ct);

        fixture.Subscriber.ShouldBe(OperationsTestWorld.Handler);
        fixture.Envelope.GetRawText().ShouldNotContain("Printer jam");
    }

    [Fact]
    public async Task A_fixture_of_a_delivery_whose_event_is_gone_is_a_conflict()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a", keepEnvelope: false);

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.GetFixtureAsync(Project, delivery.Id, Ct)))
            .Message.ShouldContain("no longer holds");
    }

    [Fact]
    public async Task Stats_list_every_registered_extension_scoped_to_this_instance()
    {
        using var world = new OperationsTestWorld();

        var stats = await world.Service.GetExtensionStatsAsync(Project, Ct);

        stats.Scope.ShouldBe("this-instance");
        stats.Items.ShouldHaveSingleItem().Id.ShouldBe(OperationsTestWorld.Handler);
    }

    [Fact]
    public async Task Every_read_refuses_an_unpublished_caller()
    {
        using var world = new OperationsTestWorld(admitted: false);

        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery(), Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.GetDeliveryAsync(Project, Guid.NewGuid(), Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.GetFixtureAsync(Project, Guid.NewGuid(), Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.GetExtensionStatsAsync(Project, Ct));
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoOperationsServiceTests'`
Expected: build FAIL — `AlvoOperationsService`, `ManagementGate` do not exist.

- [ ] **Step 4: Implement**

Create `src/MMLib.Alvo/Management/Internal/ManagementGate.cs`:

```csharp
using MMLib.Alvo.Auth;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The two admission checks every management member makes — the caller's level for the operation, then the project — and
/// who the caller is, for the audit.
/// </summary>
/// <remarks>
/// <c>AlvoManagementService</c> holds the same two checks privately; they are not folded into this class in this change, so
/// that the surface every existing management fact pins is not touched to serve a second port.
/// </remarks>
internal sealed class ManagementGate(AlvoBootState boot, IAlvoContextAccessor callers, ManagementAccessEvaluator access)
{
    internal AlvoContext Caller => callers.Principal?.Context ?? AlvoContext.Anonymous;

    internal string Actor => callers.Principal is { } principal ? $"{principal.KeyId}:{principal.Context.User.Value}" : "anonymous";

    internal void Admit(ManagementOperation operation, string project)
    {
        if (!access.Allows(operation, Caller))
        {
            throw new ManagementForbiddenException();
        }

        ArgumentNullException.ThrowIfNull(project);
        if (!boot.Projects.ContainsKey(project))
        {
            throw new ManagementProjectNotFoundException(project, [.. boot.Projects.Keys]);
        }
    }
}
```

Create `src/MMLib.Alvo/Management/Internal/DeliveryQueries.cs`:

```csharp
using MMLib.Alvo.Events;

using System.Collections.Frozen;

namespace MMLib.Alvo.Management.Internal;

/// <summary>An operator's query or replay, checked and turned into the store's filter.</summary>
internal static class DeliveryQueries
{
    internal const int MaxPage = 200;

    internal static FrozenSet<string> Statuses { get; } = new[] { "pending", "running", "succeeded", "retrying", "dead", "cancelled" }.ToFrozenSet(StringComparer.Ordinal);

    internal static FrozenSet<string> Finished { get; } = new[] { "succeeded", "dead", "cancelled" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] _statusOrder = ["pending", "running", "succeeded", "retrying", "dead", "cancelled"];

    internal static DeliveryFilter Filter(ManagementDeliveryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureStatus(query.Status, Statuses, "listed");
        if (query.Limit is < 1 or > MaxPage)
        {
            throw new ManagementRequestException($"'limit' is {query.Limit}; a page holds 1 to {MaxPage} deliveries.");
        }

        return Window(query.From, query.To) with { Subscriber = query.Subscriber, Status = query.Status, After = query.After, Limit = query.Limit };
    }

    internal static DeliveryFilter Window(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is { } start && to is { } end && end <= start)
        {
            throw new ManagementRequestException(
                $"The window ends at {end:O}, which is not after its start {start:O}. Send 'to' later than 'from', or leave one of them out.");
        }

        return new DeliveryFilter
        {
            Limit = 1,
            FromEventId = from is { } floor ? AlvoEventId.Floor(floor) : null,
            ToEventId = to is { } ceiling ? AlvoEventId.Floor(ceiling) : null,
        };
    }

    internal static void EnsureStatus(string? status, FrozenSet<string> allowed, string verb)
    {
        if (status is not null && !allowed.Contains(status))
        {
            throw new ManagementRequestException(
                $"'{status}' is not a status deliveries can be {verb} by. Use one of: {string.Join(", ", _statusOrder.Where(allowed.Contains))}.");
        }
    }
}
```

Create `src/MMLib.Alvo/Management/Internal/DeliveryViews.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Management.Internal;

/// <summary>The store's delivery records as the wire's.</summary>
internal static class DeliveryViews
{
    internal static ManagementDelivery Of(StoredDelivery row) => new()
    {
        Id = row.Id,
        EventId = row.EventId,
        Subscriber = row.Subscriber,
        PartitionKey = row.PartitionKey,
        TenantId = row.TenantId,
        Status = row.Status,
        Attempts = row.Attempts,
        NotBefore = row.NotBefore,
        CompletedAt = row.CompletedAt,
        LastErrorType = row.LastErrorType,
    };

    internal static ManagementDeliveryAttempt Of(StoredDeliveryAttempt attempt) => new()
    {
        Attempt = attempt.Attempt,
        StartedAt = attempt.StartedAt,
        Duration = attempt.Duration,
        Outcome = attempt.Outcome,
        ErrorType = attempt.ErrorType,
    };
}
```

Create `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.cs`:

```csharp
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The operator's port over handler deliveries: reads here, changes in <c>AlvoOperationsService.Writes.cs</c>. Every member
/// is admitted by <see cref="ManagementGate"/> first, exactly as the management surface's members are.
/// </summary>
internal sealed partial class AlvoOperationsService(
    ManagementGate gate,
    ExtensionCatalog catalog,
    ExtensionStats stats,
    ISchemaRegistry schemas,
    IDeliveryStore? store = null)
{
    internal const int SampleSize = 20;

    private const string NoStore =
        "No delivery store is registered, so there are no deliveries to operate on. Register a data provider — UseSqlite or "
        + "UsePostgreSql — which brings the delivery store with it.";

    public async Task<ManagementDeliveryPage> ListDeliveriesAsync(string project, ManagementDeliveryQuery query, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.ListDeliveries, project);
        var filter = DeliveryQueries.Filter(query);
        var rows = await Store.ListAsync(filter, ct).ConfigureAwait(false);

        return new ManagementDeliveryPage { Items = [.. rows.Select(DeliveryViews.Of)], Next = rows.Count == filter.Limit ? rows[^1].Id : null };
    }

    public async Task<ManagementDelivery> GetDeliveryAsync(string project, Guid delivery, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.GetDelivery, project);

        return await DetailAsync(delivery, ct).ConfigureAwait(false);
    }

    public async Task<DeliveryFixture> GetFixtureAsync(string project, Guid delivery, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.GetDeliveryFixture, project);
        var row = await FoundAsync(delivery, ct).ConfigureAwait(false);

        return row.Payload is { } payload
            ? DeliveryFixtures.Of(row, payload, schemas.GetSchema())
            : throw new ManagementConflictException(
                $"The outbox no longer holds the event of delivery '{delivery}', so there is no envelope to make a fixture of.");
    }

    public Task<ManagementExtensionStats> GetExtensionStatsAsync(string project, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.GetExtensionStats, project);

        return Task.FromResult(stats.Report([.. catalog.Items.Select(item => item.Id)]));
    }

    private IDeliveryStore Store => store ?? throw new ManagementRequestException(NoStore);

    private async Task<ManagementDelivery> DetailAsync(Guid delivery, CancellationToken ct)
    {
        var row = await FoundAsync(delivery, ct).ConfigureAwait(false);
        var attempts = await Store.AttemptsAsync(delivery, ct).ConfigureAwait(false);

        return DeliveryViews.Of(row) with { History = [.. attempts.Select(DeliveryViews.Of)] };
    }

    private async Task<StoredDelivery> FoundAsync(Guid delivery, CancellationToken ct) =>
        await Store.FindAsync(delivery, ct).ConfigureAwait(false) ?? throw new ManagementDeliveryNotFoundException(delivery);
}
```

In `src/MMLib.Alvo/Management/Setup.cs`, in `AddAlvoManagement` after `services.TryAddSingleton<ManagementAccessEvaluator>();`:

```csharp
        services.TryAddSingleton<ManagementGate>();
        services.TryAddSingleton<AlvoOperationsService>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ManagementGateTests'` and `--filter-class '*AlvoOperationsServiceTests'`.
Expected: PASS.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Management/Internal/ManagementGate.cs src/MMLib.Alvo/Management/Internal/AlvoOperationsService.cs src/MMLib.Alvo/Management/Internal/DeliveryViews.cs src/MMLib.Alvo/Management/Internal/DeliveryQueries.cs src/MMLib.Alvo/Management/Setup.cs test/MMLib.Alvo.Tests/Management/OperationsTestWorld.cs test/MMLib.Alvo.Tests/Management/ManagementGateTests.cs test/MMLib.Alvo.Tests/Management/AlvoOperationsServiceTests.cs test/_shared/testhost/AuditCapture.cs
scripts/test-ring0
git add src/MMLib.Alvo/Management/Internal/ManagementGate.cs src/MMLib.Alvo/Management/Internal/AlvoOperationsService.cs src/MMLib.Alvo/Management/Internal/DeliveryViews.cs src/MMLib.Alvo/Management/Internal/DeliveryQueries.cs src/MMLib.Alvo/Management/Setup.cs test/MMLib.Alvo.Tests/Management/OperationsTestWorld.cs test/MMLib.Alvo.Tests/Management/ManagementGateTests.cs test/MMLib.Alvo.Tests/Management/AlvoOperationsServiceTests.cs test/_shared/testhost/AuditCapture.cs test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj
git commit -m "feat(management): read deliveries, fixtures and stats behind the management gate

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 10: `AlvoOperationsService` writes — redeliver, pause, the two refusing routes, the audit

**Files:**
- Create: `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Writes.cs`, `OperationReasons.cs`, `OperationsAudit.cs`
- Modify: `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.cs` (the clock and the logger join the constructor)
- Test: `test/MMLib.Alvo.Tests/Management/AlvoOperationsWritesTests.cs`

**Interfaces:**
- Consumes: Task 9's service, gate and helpers; the store's `RedeliverAsync` and `SetPausedAsync` (Tasks 3–4).
- Produces: the service's public `RedeliverAsync`, `SetPausedAsync`, `SetEnabledAsync`, `RunScheduleAsync` (signatures of `IAlvoOperations`); private `EnsureRedeliverable(StoredDelivery)`, `Handler(string)` (a registered event handler, else `ManagementExtensionNotFoundException`), `Registered(string)`, `Entry(string operation, string target, string? reason) → DeliveryOperationEntry`; `internal static class OperationReasons` (`const int MaxLength = 500`, `void Ensure(string? reason)`); `internal static partial class OperationsAudit` (`void Write(ILogger logger, DeliveryOperationEntry entry)` → a `LoggerMessage` named `ManagementAudit` at Warning: `Management audit: {Actor} made '{Operation}' on '{Target}' ({Affected} changed). Reason: {Reason}`).

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Management/AlvoOperationsWritesTests.cs`:

```csharp
using MMLib.Alvo.Events;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The operations port's changes (spec 2026-10-14 HX-06): each one that changed something writes one log row and one
/// <c>ManagementAudit</c> line (acceptance (7)); one that changed nothing writes neither; switching a handler off is 409 (9).
/// </summary>
public sealed class AlvoOperationsWritesTests
{
    private const string Project = OperationsTestWorld.Project;
    private const string Handler = OperationsTestWorld.Handler;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Redelivering_a_dead_delivery_puts_it_back_and_audits_once()
    {
        using var world = new OperationsTestWorld();
        var dead = await world.DeliveryAsync("tickets:a", deadReason: "when-failed");

        var redelivered = await world.Service.RedeliverAsync(Project, dead.Id, "fixed the template", Ct);

        redelivered.Status.ShouldBe("pending");
        redelivered.Attempts.ShouldBe(0);
        world.Store.Operations.ShouldHaveSingleItem().Operation.ShouldBe("redeliver");
        world.Audit.Lines.ShouldHaveSingleItem().ShouldBe(
            $"Management audit: ops-key:{ManagementCallers.Bootstrap.Value} made 'redeliver' on '{dead.Id}' (1 changed). Reason: fixed the template");
    }

    [Fact]
    public async Task Redelivering_a_pending_delivery_answers_it_unchanged_and_audits_nothing()
    {
        using var world = new OperationsTestWorld();
        var pending = await world.DeliveryAsync("tickets:a");

        (await world.Service.RedeliverAsync(Project, pending.Id, reason: null, Ct)).Status.ShouldBe("pending");

        world.Store.Operations.ShouldBeEmpty();
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Redelivering_a_running_delivery_is_a_conflict()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a");
        await world.Store.ClaimAsync(new DeliveryClaim { Claimant = "w", Ordered = [Handler], BatchSize = 1, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10, OutboxClaimLease = TimeSpan.FromMinutes(5) }, Ct);

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.RedeliverAsync(Project, delivery.Id, reason: null, Ct)))
            .Message.ShouldContain("running");
    }

    [Fact]
    public async Task Redelivering_for_a_handler_this_host_does_not_register_is_refused()
    {
        using var world = new OperationsTestWorld();
        var orphan = await world.DeliveryAsync("tickets:a", deadReason: "when-failed", subscriber: "renamed-away");

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.RedeliverAsync(Project, orphan.Id, reason: null, Ct)))
            .Message.ShouldContain("AddRetiredExtension");
        world.Store.Rows.ShouldHaveSingleItem().Status.ShouldBe("dead", "nothing was put back that the next start would refuse");
    }

    [Fact]
    public async Task Pausing_twice_audits_once_and_answers_paused()
    {
        using var world = new OperationsTestWorld();

        (await world.Service.SetPausedAsync(Project, Handler, paused: true, "mail provider down", Ct)).Paused.ShouldBeTrue();
        (await world.Service.SetPausedAsync(Project, Handler, paused: true, "mail provider down", Ct)).Paused.ShouldBeTrue();
        (await world.Service.SetPausedAsync(Project, Handler, paused: false, reason: null, Ct)).Paused.ShouldBeFalse();

        world.Store.Operations.Select(row => row.Operation).ShouldBe(["pause", "resume"]);
        world.Audit.Lines.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Pausing_an_unknown_handler_is_not_found()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementExtensionNotFoundException>(() => world.Service.SetPausedAsync(Project, "nobody", paused: true, reason: null, Ct));
    }

    [Fact]
    public async Task Switching_a_handler_off_is_a_conflict_that_names_pausing()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.SetEnabledAsync(Project, Handler, enabled: false, reason: null, Ct)))
            .Message.ShouldContain($"subscribers/{Handler}/paused");
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Running_a_schedule_this_host_lacks_is_not_found()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementExtensionNotFoundException>(() => world.Service.RunScheduleAsync(Project, "nightly-report", Ct));
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("tab\there")]
    public async Task A_reason_with_a_control_character_is_refused_before_anything_is_written(string reason)
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementRequestException>(() => world.Service.SetPausedAsync(Project, Handler, paused: true, reason, Ct));

        world.Store.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reason_over_500_characters_is_refused()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.SetPausedAsync(Project, Handler, paused: true, new string('x', 501), Ct)))
            .Message.ShouldContain("500");
    }

    [Fact]
    public async Task Every_write_refuses_an_unpublished_caller()
    {
        using var world = new OperationsTestWorld(admitted: false);

        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.RedeliverAsync(Project, Guid.NewGuid(), null, Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.SetPausedAsync(Project, Handler, true, null, Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.SetEnabledAsync(Project, Handler, false, null, Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.RunScheduleAsync(Project, "nightly-report", Ct));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoOperationsWritesTests'`
Expected: build FAIL — `RedeliverAsync`, `SetPausedAsync` … do not exist on the service.

- [ ] **Step 3: Reasons and the audit line**

Create `src/MMLib.Alvo/Management/Internal/OperationReasons.cs`:

```csharp
namespace MMLib.Alvo.Management.Internal;

/// <summary>What an operator's reason may hold: one line of at most 500 characters, because it becomes one audit field.</summary>
internal static class OperationReasons
{
    internal const int MaxLength = 500;

    internal static void Ensure(string? reason)
    {
        if (reason is null)
        {
            return;
        }

        if (reason.Length > MaxLength)
        {
            throw new ManagementRequestException(
                $"A reason holds at most {MaxLength} characters; this one has {reason.Length}. Shorten it — point at the ticket that tells the whole story.");
        }

        if (reason.Any(char.IsControl))
        {
            throw new ManagementRequestException(
                "A reason is one line of text: it may not hold a line break, a tab or another control character, because it is written into the audit log as one field.");
        }
    }
}
```

Create `src/MMLib.Alvo/Management/Internal/OperationsAudit.cs`:

```csharp
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Events;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The <c>ManagementAudit</c> log event (spec E11): who made which operation on what, how much it changed, and why —
/// written once per committed change, beside the operations log row the store wrote in the change's own transaction.
/// </summary>
/// <remarks>
/// Warning, so it survives a production log level that drops information. Ids and the operator's own one-line reason only —
/// never a payload, a URL or an exception message.
/// </remarks>
internal static partial class OperationsAudit
{
    internal static void Write(ILogger logger, DeliveryOperationEntry entry) =>
        ManagementAudit(logger, entry.Actor, entry.Operation, entry.Target, entry.Affected ?? 0, entry.Reason ?? string.Empty);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Management audit: {Actor} made '{Operation}' on '{Target}' ({Affected} changed). Reason: {Reason}")]
    private static partial void ManagementAudit(ILogger logger, string actor, string operation, string target, int affected, string reason);
}
```

- [ ] **Step 4: The writes**

In `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.cs` insert two constructor parameters before `IDeliveryStore? store = null` — `TimeProvider time,` and `ILogger<AlvoOperationsService> logger,` — and add `using Microsoft.Extensions.Logging;`.

Create `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Writes.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Management.Internal;

/// <summary>The operator's changes: each audited once when it changed something, none when it did not.</summary>
internal sealed partial class AlvoOperationsService
{
    private const string RedeliverOperation = "redeliver";
    private const string PauseOperation = "pause";
    private const string ResumeOperation = "resume";

    public async Task<ManagementDelivery> RedeliverAsync(string project, Guid delivery, string? reason, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.RedeliverDelivery, project);
        OperationReasons.Ensure(reason);
        var row = await FoundAsync(delivery, ct).ConfigureAwait(false);
        EnsureRedeliverable(row);
        var entry = Entry(RedeliverOperation, delivery.ToString(), reason);
        if (DeliveryQueries.Finished.Contains(row.Status) && await Store.RedeliverAsync(delivery, entry, ct).ConfigureAwait(false))
        {
            OperationsAudit.Write(logger, entry with { Affected = 1 });
        }

        return await DetailAsync(delivery, ct).ConfigureAwait(false);
    }

    public async Task<ManagementExtension> SetPausedAsync(string project, string subscriber, bool paused, string? reason, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.PauseSubscriber, project);
        OperationReasons.Ensure(reason);
        var handler = Handler(subscriber);
        var entry = Entry(paused ? PauseOperation : ResumeOperation, subscriber, reason);
        if (await Store.SetPausedAsync(subscriber, paused, entry, ct).ConfigureAwait(false))
        {
            OperationsAudit.Write(logger, entry with { Affected = 1 });
        }

        return handler with { Paused = paused };
    }

    public Task<ManagementExtension> SetEnabledAsync(string project, string extension, bool enabled, string? reason, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.SetExtensionEnabled, project);
        OperationReasons.Ensure(reason);
        var found = Registered(extension);

        throw new ManagementConflictException(
            $"'{found.Id}' is an event handler, and only a validate hook can be switched off. To stop a handler for a while, "
            + $"pause it instead: PUT {{m}}/projects/{project}/subscribers/{found.Id}/paused with {{\"paused\": true}}.");
    }

    public Task<ManagementDelivery> RunScheduleAsync(string project, string schedule, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.RunSchedule, project);

        throw new ManagementExtensionNotFoundException(
            $"This host registers no schedule '{schedule}'. Schedules are registered in the host with AddSchedule; it has none.");
    }

    private void EnsureRedeliverable(StoredDelivery row)
    {
        if (!catalog.Items.Any(item => item.Id == row.Subscriber))
        {
            throw new ManagementConflictException(
                $"Delivery '{row.Id}' is for '{row.Subscriber}', which this host does not register, so a redelivered delivery would never "
                + "run — and would stop the next start as unknown work. Register the handler again, or retire it with AddRetiredExtension.");
        }

        if (row.Status == "running")
        {
            throw new ManagementConflictException(
                $"Delivery '{row.Id}' is running under a claim. Wait for its attempt to be answered, then redeliver it if it ends dead.");
        }
    }

    private ManagementExtension Handler(string subscriber) =>
        catalog.Items.FirstOrDefault(item => item.Id == subscriber && item.Kind == AlvoExtensionKind.EventHandler)
        ?? throw new ManagementExtensionNotFoundException(subscriber, [.. catalog.Items.Select(item => item.Id)]);

    private ManagementExtension Registered(string extension) =>
        catalog.Items.FirstOrDefault(item => item.Id == extension)
        ?? throw new ManagementExtensionNotFoundException(extension, [.. catalog.Items.Select(item => item.Id)]);

    private DeliveryOperationEntry Entry(string operation, string target, string? reason)
    {
        var now = time.GetUtcNow();

        return new DeliveryOperationEntry
        {
            Id = AlvoEventId.Create(now),
            At = now,
            Actor = gate.Actor,
            Operation = operation,
            Target = target,
            Reason = reason,
        };
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoOperationsWritesTests'`, then `--filter-class '*AlvoOperationsServiceTests'`.
Expected: PASS.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Management/Internal/AlvoOperationsService.cs src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Writes.cs src/MMLib.Alvo/Management/Internal/OperationReasons.cs src/MMLib.Alvo/Management/Internal/OperationsAudit.cs test/MMLib.Alvo.Tests/Management/AlvoOperationsWritesTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Management/Internal/AlvoOperationsService.cs src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Writes.cs src/MMLib.Alvo/Management/Internal/OperationReasons.cs src/MMLib.Alvo/Management/Internal/OperationsAudit.cs test/MMLib.Alvo.Tests/Management/AlvoOperationsWritesTests.cs
git commit -m "feat(management): redeliver and pause through the operations service, audited once per change

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 11: Replay with its idempotency key, and the port registered

**Files:**
- Create: `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Replay.cs`
- Modify: `src/MMLib.Alvo/Management/Setup.cs` (register `IAlvoOperations`)
- Test: `test/MMLib.Alvo.Tests/Management/AlvoOperationsReplayTests.cs`

**Interfaces:**
- Consumes: Task 10's `Handler`, `Entry`, `OperationReasons`, `OperationsAudit`; the store's `SelectAsync`, `ReplayAsync`, `FindOperationAsync`; `ManagementIdempotency.FingerprintOf`, `ManagementIdempotencyToken`, `AlvoIdempotency.EnsureUsableKey`/`IdentityOf`; `DeliveryQueries.Window`/`EnsureStatus`/`Finished`.
- Produces: `AlvoOperationsService : IAlvoOperations` (this partial declares the interface) with `ReplayAsync`; `internal const int MaxReplay = 10_000`; `internal static string ReplayFingerprint(string project, ManagementReplayRequest request)`; DI `IAlvoOperations` → the `AlvoOperationsService` singleton.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Tests/Management/AlvoOperationsReplayTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// A replay (spec 2026-10-14 HX-06): a dry run counts exactly and changes nothing (acceptance (1)); a real run resets what it
/// selects and audits once (7); its idempotency key answers a retry with the first replay's count.
/// </summary>
public sealed class AlvoOperationsReplayTests
{
    private const string Project = OperationsTestWorld.Project;
    private const string Handler = OperationsTestWorld.Handler;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_replay_dry_run_counts_exactly_and_changes_nothing()
    {
        using var world = new OperationsTestWorld();
        foreach (var key in new[] { "tickets:a", "tickets:b", "tickets:c" })
        {
            await world.DeliveryAsync(key, deadReason: "when-failed");
        }

        var plan = await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, DryRun = true }, Ct);

        plan.DryRun.ShouldBeTrue();
        plan.Count.ShouldBe(3);
        plan.Ids.Count.ShouldBe(3);
        world.Store.Rows.ShouldAllBe(row => row.Status == "dead");
        world.Store.Operations.ShouldBeEmpty();
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replay_resets_what_it_selects_and_audits_once()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        await world.DeliveryAsync("tickets:b", deadReason: "when-failed");

        var plan = await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Reason = "endpoint is back" }, Ct);

        plan.Count.ShouldBe(2);
        plan.Replayed.ShouldBeFalse();
        world.Store.Rows.ShouldAllBe(row => row.Status == "pending");
        world.Store.Operations.ShouldHaveSingleItem().Affected.ShouldBe(2);
        world.Audit.Lines.ShouldHaveSingleItem().ShouldContain("made 'replay' on 'send-confirmation' (2 changed)");
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    [InlineData("retrying")]
    public async Task A_replay_of_an_unfinished_status_is_refused(string status)
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Status = status }, Ct)))
            .Message.ShouldContain("succeeded, dead, cancelled");
    }

    [Fact]
    public async Task A_replay_names_a_handler_this_host_registers()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementExtensionNotFoundException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = "send-confirmaton" }, Ct)))
            .Message.ShouldContain(Handler);
    }

    [Fact]
    public async Task A_key_on_a_dry_run_is_refused()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, DryRun = true, IdempotencyKey = "k-1" }, Ct)))
            .Message.ShouldContain("dry run");
    }

    [Fact]
    public async Task A_retried_replay_answers_the_first_and_resets_nothing()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        var request = new ManagementReplayRequest { Subscriber = Handler, IdempotencyKey = "replay-7" };
        var first = await world.Service.ReplayAsync(Project, request, Ct);
        await world.DeliveryAsync("tickets:b", deadReason: "when-failed");

        var retried = await world.Service.ReplayAsync(Project, request, Ct);

        (retried.Count, retried.Replayed, retried.DryRun).ShouldBe((first.Count, true, false));
        retried.Ids.ShouldBe(first.Ids);
        world.Store.Rows.Count(row => row.Status == "dead").ShouldBe(1, "the retry reset nothing");
        world.Audit.Lines.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_key_spent_on_a_different_replay_is_a_conflict()
    {
        using var world = new OperationsTestWorld();
        await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, IdempotencyKey = "replay-8" }, Ct);

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(() =>
            world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Status = "succeeded", IdempotencyKey = "replay-8" }, Ct));
    }

    [Fact]
    public async Task A_replay_refuses_an_unpublished_caller()
    {
        using var world = new OperationsTestWorld(admitted: false);

        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler }, Ct));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoOperationsReplayTests'`
Expected: build FAIL — `ReplayAsync` does not exist on the service.

- [ ] **Step 3: The replay**

Create `src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Replay.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Globalization;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// A replay: a dry run counts, a real run resets under an optional idempotency key whose record is the operations log row
/// the store writes in the reset's own transaction. With it the service is the whole <see cref="IAlvoOperations"/>.
/// </summary>
internal sealed partial class AlvoOperationsService : IAlvoOperations
{
    internal const int MaxReplay = 10_000;

    private const string ReplayOperation = "replay";

    private const string KeyOnDryRun =
        "A dry run may not carry an 'Idempotency-Key'. It resets nothing, so there is nothing to record — and a record filed "
        + "here would turn your real replay into a replay of a preview. Send the key with the real replay.";

    private const string KeySpentElsewhere =
        "This 'Idempotency-Key' was already spent on a different replay. Send a fresh key with this body, or resend the original body to read the answer it recorded.";

    public async Task<ManagementReplayPlan> ReplayAsync(string project, ManagementReplayRequest request, CancellationToken ct = default)
    {
        gate.Admit(ManagementOperation.ReplayDeliveries, project);
        var filter = ReplayFilter(request);
        var token = ReplayToken(project, request);
        if (request.DryRun)
        {
            return await PlanAsync(filter, ct).ConfigureAwait(false);
        }

        return await ReplayedAsync(token, ct).ConfigureAwait(false)
            ?? await ResetAsync(filter, request, token, ct).ConfigureAwait(false);
    }

    internal static string ReplayFingerprint(string project, ManagementReplayRequest request) =>
        ManagementIdempotency.FingerprintOf(
            nameof(ReplayAsync),
            project,
            expectedRevision: 0,
            allowDestructive: false,
            string.Join('\n', request.Subscriber, request.Status, Instant(request.From), Instant(request.To)));

    private static string Instant(DateTimeOffset? instant) => instant?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    private DeliveryFilter ReplayFilter(ManagementReplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OperationReasons.Ensure(request.Reason);
        Handler(request.Subscriber);
        DeliveryQueries.EnsureStatus(request.Status, DeliveryQueries.Finished, "replayed");

        return DeliveryQueries.Window(request.From, request.To) with { Subscriber = request.Subscriber, Status = request.Status, Limit = SampleSize };
    }

    private ManagementIdempotencyToken? ReplayToken(string project, ManagementReplayRequest request)
    {
        if (request.IdempotencyKey is not { } key)
        {
            return null;
        }

        if (request.DryRun)
        {
            throw new ManagementRequestException(KeyOnDryRun);
        }

        var caller = gate.Caller;
        try
        {
            AlvoIdempotency.EnsureUsableKey(key, caller);
        }
        catch (ArgumentException refusal)
        {
            throw new ManagementRequestException(refusal.Message, refusal);
        }

        return new ManagementIdempotencyToken(key, AlvoIdempotency.IdentityOf(caller), ReplayFingerprint(project, request));
    }

    private async Task<ManagementReplayPlan> PlanAsync(DeliveryFilter filter, CancellationToken ct)
    {
        var selection = await Store.SelectAsync(filter, ct).ConfigureAwait(false);

        return new ManagementReplayPlan { DryRun = true, Count = selection.Count, Ids = selection.Sample };
    }

    private async Task<ManagementReplayPlan?> ReplayedAsync(ManagementIdempotencyToken? token, CancellationToken ct)
    {
        if (token is not { } spent || await Store.FindOperationAsync(spent.Scope, spent.Key, ct).ConfigureAwait(false) is not { } recorded)
        {
            return null;
        }

        return string.Equals(recorded.Fingerprint, spent.Fingerprint, StringComparison.Ordinal)
            ? new ManagementReplayPlan { DryRun = false, Count = recorded.Affected ?? 0, Ids = recorded.Sample, Replayed = true }
            : throw new AlvoIdempotencyConflictException(KeySpentElsewhere);
    }

    private async Task<ManagementReplayPlan> ResetAsync(DeliveryFilter filter, ManagementReplayRequest request, ManagementIdempotencyToken? token, CancellationToken ct)
    {
        var selected = await Store.SelectAsync(filter, ct).ConfigureAwait(false);
        if (selected.Count > MaxReplay)
        {
            throw new ManagementRequestException(
                $"The replay selects {selected.Count} deliveries; one replay resets at most {MaxReplay}. Narrow 'from' and 'to', and replay the window in parts.");
        }

        var entry = Entry(ReplayOperation, request.Subscriber, request.Reason) with
        {
            IdempotencyScope = token?.Scope,
            IdempotencyKey = token?.Key,
            Fingerprint = token?.Fingerprint,
        };
        var recorded = await Store.ReplayAsync(filter, entry, ct).ConfigureAwait(false);
        if (recorded is null)
        {
            return await ReplayedAsync(token, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The delivery store refused the replay's key and then did not find it under that key — a store that breaks IDeliveryStore's contract.");
        }

        OperationsAudit.Write(logger, recorded);

        return new ManagementReplayPlan { DryRun = false, Count = recorded.Affected ?? 0, Ids = recorded.Sample };
    }
}
```

In `src/MMLib.Alvo/Management/Setup.cs`, after the two singletons of Task 9:

```csharp
        services.TryAddSingleton<IAlvoOperations>(provider => provider.GetRequiredService<AlvoOperationsService>());
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoOperationsReplayTests'`, then `--filter-class '*AlvoOperationsWritesTests'` and `--filter-class '*AlvoOperationsServiceTests'`.
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Replay.cs src/MMLib.Alvo/Management/Setup.cs test/MMLib.Alvo.Tests/Management/AlvoOperationsReplayTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Management/Internal/AlvoOperationsService.Replay.cs src/MMLib.Alvo/Management/Setup.cs test/MMLib.Alvo.Tests/Management/AlvoOperationsReplayTests.cs
git commit -m "feat(management): replay deliveries under an idempotency key, and register the operations port

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 12: The catalog says which handlers are paused

**Files:**
- Modify: `src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs`, `src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs` (`GetExtensionsAsync`, constructor), `src/MMLib.Alvo/Management/Setup.cs`
- Test: `test/MMLib.Alvo.Tests/Management/PausedCatalogTests.cs`

**Interfaces:**
- Consumes: `IDeliveryStore.ControlsAsync` (Task 4), `ManagementExtension.Paused` (Task 5).
- Produces: `ExtensionCatalog.CatalogAsync(IDeliveryStore? store, CancellationToken ct) → Task<ManagementExtensionCatalog>` (every item with `Paused` set from the controls; without a store nothing is paused); `AlvoManagementService` gains a trailing constructor parameter `IDeliveryStore? deliveries` and its `GetExtensionsAsync` serves `CatalogAsync`.

- [ ] **Step 1: Write the failing test**

Create `test/MMLib.Alvo.Tests/Management/PausedCatalogTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>The extension catalog says which handlers an operator paused (spec 2026-10-14 §4.13 <c>Paused</c>), so every reader of it — the dashboard, the assistant — sees it.</summary>
public sealed class PausedCatalogTests
{
    [Fact]
    public async Task A_paused_handler_is_listed_paused_and_a_resumed_one_is_not()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new OperationsTestWorld();
        var management = world.Services.GetRequiredService<IAlvoManagement>();

        await world.Service.SetPausedAsync(OperationsTestWorld.Project, OperationsTestWorld.Handler, paused: true, reason: null, ct);
        (await management.GetExtensionsAsync(OperationsTestWorld.Project, ct)).Items.ShouldHaveSingleItem().Paused.ShouldBeTrue();

        await world.Service.SetPausedAsync(OperationsTestWorld.Project, OperationsTestWorld.Handler, paused: false, reason: null, ct);
        (await management.GetExtensionsAsync(OperationsTestWorld.Project, ct)).Items.ShouldHaveSingleItem().Paused.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PausedCatalogTests'`
Expected: FAIL — `Paused` stays `false` (the catalog does not read the controls).

- [ ] **Step 3: Implement**

In `src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs` add (with `using MMLib.Alvo.Events;`):

```csharp
    internal async Task<ManagementExtensionCatalog> CatalogAsync(IDeliveryStore? store, CancellationToken ct)
    {
        if (store is null || Items.Count == 0)
        {
            return Catalog();
        }

        var paused = (await store.ControlsAsync(ct).ConfigureAwait(false)).Select(control => control.Subscriber).ToHashSet(StringComparer.Ordinal);

        return new ManagementExtensionCatalog { Items = [.. Items.Select(item => paused.Contains(item.Id) ? item with { Paused = true } : item)] };
    }
```

In `AlvoManagementService`, add a trailing primary-constructor parameter `IDeliveryStore? deliveries` (with its `<param>` doc: "The delivery store, read for which handlers are paused; none without a driver.") and change `GetExtensionsAsync`:

```csharp
    /// <inheritdoc/>
    public async Task<ManagementExtensionCatalog> GetExtensionsAsync(string project, CancellationToken ct = default)
    {
        EnsureMayPerform(ManagementOperation.GetExtensions);
        EnsureServed(project);

        return await extensions.CatalogAsync(deliveries, ct).ConfigureAwait(false);
    }
```

In `src/MMLib.Alvo/Management/Setup.cs`, `AddManagementService`, append `provider.GetService<Events.IDeliveryStore>()` as the last constructor argument.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PausedCatalogTests'`; `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ManagementAccessTests'` (the catalog route still answers); `dotnet test --project test/MMLib.Alvo.Ai.Tests --filter-class '*ManagementToolsTests'` (the assistant reads the same member).
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs src/MMLib.Alvo/Management/Setup.cs test/MMLib.Alvo.Tests/Management/PausedCatalogTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Extensions/Internal/ExtensionCatalog.cs src/MMLib.Alvo/Management/Internal/AlvoManagementService.cs src/MMLib.Alvo/Management/Setup.cs test/MMLib.Alvo.Tests/Management/PausedCatalogTests.cs
git commit -m "feat(management): list paused handlers as paused in the extension catalog

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 13: The nine routes over HTTP **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (`partial`, `MapOperations(group)` in `Map`, three `catch` clauses in `Answer`)
- Create: `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.Operations.cs`, `src/MMLib.Alvo/Management/Internal/ManagementOperationsBodies.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (`ManagementConflict`)
- Modify (tests): `test/_shared/api/AlvoApiWorld.cs` (route parameter values), `test/MMLib.Alvo.Api.Tests/Management/ManagementContractTests.cs`
- Create (tests): `test/MMLib.Alvo.Api.Tests/descriptors/operated-notes.alvo.json`, `test/MMLib.Alvo.Api.Tests/Management/OperationsRouteTests.cs`

**Interfaces:**
- Consumes: `IAlvoOperations` (registered in Task 11), `ManagementEndpoints.Gate`, `.Answer`, `.Refused`, `.TryReadIdempotencyKey`, `RepeatedIdempotencyKey`.
- Produces: routes under `{m}/projects/{project}/`: `GET deliveries` (query `subscriber`, `status`, `from`, `to`, `after`, `limit`), `GET deliveries/{delivery:guid}`, `POST deliveries/{delivery:guid}/redeliver` (`{reason}`), `POST deliveries/replay` (`{subscriber, status, from, to, dryRun, reason}` + header `Idempotency-Key`), `GET deliveries/{delivery:guid}/fixture`, `PUT subscribers/{subscriber}/paused` (`{paused, reason}`), `PUT extensions/{extension}/enabled` (`{enabled, reason}`), `POST schedules/{schedule}/run`, `GET extensions/stats`; each with `ManagementRoute(nameof(IAlvoOperations.<Member>), ManagementOperation.<Row>)`; `ProblemResultFactory.ManagementConflict(string detail)` → 409 `conflict`.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Api.Tests/descriptors/operated-notes.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "operated-notes",
  "description": "The management levels the operations routes are measured against: ops reads, dev operates, admin switches code off. One handler is registered on notes by the fact itself.",
  "auth": {
    "providers": ["local"],
    "roles": ["ops", "dev"]
  },
  "access": {
    "viewer": "'ops' in @user.roles",
    "developer": "'dev' in @user.roles",
    "admin": "'admin' in @user.roles"
  },
  "entities": {
    "notes": {
      "fields": {
        "title": { "type": "string", "required": true, "maxLength": 120 }
      },
      "rules": {
        "list": "false",
        "get": "false",
        "create": "false",
        "update": "false",
        "delete": "false"
      }
    }
  }
}
```

(Copy it to the test output like `managed-notes.alvo.json` is — the project's `descriptors/*.alvo.json` glob already does.)

Create `test/MMLib.Alvo.Api.Tests/Management/OperationsRouteTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>The operations routes over the wire (spec 2026-10-14 HX-06): levels (acceptance (4)), the handler refusal (9), the key header, stats (8).</summary>
public sealed class OperationsRouteTests
{
    private const string Prefix = "/management/projects/operated-notes";
    private const string Handler = "note-audit";

    private static readonly TestApiKey _viewer = new("ops-viewer", ["ops"], ["*:read"]);
    private static readonly TestApiKey _developer = new("ops-dev", ["dev"], ["*:write"]);
    private static readonly TestApiKey _admin = new("ops-admin", ["admin"], ["*:write"]);

    [Fact]
    public async Task A_viewer_is_refused_a_redeliver()
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/{Guid.NewGuid()}/redeliver", _viewer);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
    }

    [Fact]
    public async Task A_viewer_lists_deliveries_and_reads_the_stats()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Get, $"{Prefix}/deliveries?limit=10", _viewer)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var stats = await world.SendAsync(HttpMethod.Get, $"{Prefix}/extensions/stats", _viewer);

        stats.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await stats.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        body["scope"]!.GetValue<string>().ShouldBe("this-instance");
        body["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ShouldBe([Handler]);
    }

    [Fact]
    public async Task Switching_a_handler_off_is_409_naming_pausing()
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Put, $"{Prefix}/extensions/{Handler}/enabled", _admin, body: new JsonObject { ["enabled"] = false });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"subscribers/{Handler}/paused");
    }

    [Fact]
    public async Task A_developer_may_not_switch_code_off()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Put, $"{Prefix}/extensions/{Handler}/enabled", _developer, body: new JsonObject { ["enabled"] = false }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_developer_pausing_a_handler_gets_its_catalog_entry_back()
    {
        await using var world = await WorldAsync();

        var response = await world.SendAsync(HttpMethod.Put, $"{Prefix}/subscribers/{Handler}/paused", _developer, body: new JsonObject { ["paused"] = true, ["reason"] = "mail provider down" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["paused"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_pause_that_does_not_say_paused_or_resumed_is_422()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Put, $"{Prefix}/subscribers/{Handler}/paused", _developer, body: new JsonObject { ["reason"] = "?" }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_replay_reads_its_idempotency_key_from_the_header()
    {
        await using var world = await WorldAsync();
        KeyValuePair<string, string>[] key = [new("Idempotency-Key", "replay-http-1")];
        KeyValuePair<string, string>[] twoKeys = [new("Idempotency-Key", "a"), new("Idempotency-Key", "b")];

        var first = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler }, headers: key);
        var again = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler }, headers: key);
        var ambiguous = await world.SendAsync(HttpMethod.Post, $"{Prefix}/deliveries/replay", _developer, body: new JsonObject { ["subscriber"] = Handler }, headers: twoKeys);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["replayed"]!.GetValue<bool>().ShouldBeTrue();
        ambiguous.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "two keys are two requests, and a replay is recorded under one");
    }

    [Fact]
    public async Task An_unknown_delivery_is_404()
    {
        await using var world = await WorldAsync();

        (await world.SendAsync(HttpMethod.Get, $"{Prefix}/deliveries/{Guid.NewGuid()}", _viewer)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static Task<AlvoApiWorld> WorldAsync() => AlvoApiWorld.FromDescriptorAsync(
        "operated-notes.alvo.json",
        [_viewer, _developer, _admin],
        new AlvoApiWorldSetup(
            MapManagementApi: true,
            ConfigureServices: services => services.AddAlvo(alvo =>
                alvo.Entity("notes", notes => notes.OnCreated(Handler, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask)))));
}
```

In `test/MMLib.Alvo.Api.Tests/Management/ManagementContractTests.cs`, rename the first fact to `Every_member_of_the_management_ports_has_an_http_route` and change `ContractMembers()`:

```csharp
    /// <summary>Every member both management ports declare, read off the interfaces rather than off a list.</summary>
    private static IEnumerable<string> ContractMembers() =>
        typeof(IAlvoManagement).GetMethods().Concat(typeof(IAlvoOperations).GetMethods()).Select(method => method.Name);
```

In `test/_shared/api/AlvoApiWorld.cs`, extend `_routeParameterValues`:

```csharp
        new(StringComparer.Ordinal)
        {
            ["project"] = "no-such-project",
            ["revision"] = "0",
            ["delivery"] = Guid.Empty.ToString(),
            ["subscriber"] = "no-such-handler",
            ["extension"] = "no-such-extension",
            ["schedule"] = "no-such-schedule",
        };
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*OperationsRouteTests'`
Expected: FAIL — every route answers 404 (none is mapped); `ManagementContractTests` FAILS listing the nine members with no route.

- [ ] **Step 3: The routes**

Create `src/MMLib.Alvo/Management/Internal/ManagementOperationsBodies.cs`:

```csharp
namespace MMLib.Alvo.Management.Internal;

/// <summary>The body of a redeliver: why, if the caller says.</summary>
/// <param name="Reason">Kept in the audit.</param>
internal sealed record ManagementReasonBody(string? Reason);

/// <summary>The body of a pause or a resume.</summary>
/// <param name="Paused">Whether the handler should be paused; required.</param>
/// <param name="Reason">Kept on the pause and in the audit.</param>
internal sealed record ManagementPausedBody(bool? Paused, string? Reason);

/// <summary>The body of switching an extension off or on.</summary>
/// <param name="Enabled">Whether it should run; required.</param>
/// <param name="Reason">Kept in the audit.</param>
internal sealed record ManagementEnabledBody(bool? Enabled, string? Reason);

/// <summary>The body of a replay; the key travels in the <c>Idempotency-Key</c> header.</summary>
/// <param name="Subscriber">The handler; required.</param>
/// <param name="Status">Which finished deliveries; <c>dead</c> when absent.</param>
/// <param name="From">The earliest event time included.</param>
/// <param name="To">The event time the window ends before.</param>
/// <param name="DryRun">Whether to only count.</param>
/// <param name="Reason">Kept in the audit.</param>
internal sealed record ManagementReplayBody(string? Subscriber, string? Status, DateTimeOffset? From, DateTimeOffset? To, bool DryRun, string? Reason)
{
    internal ManagementReplayRequest ToRequest(string? key) => new()
    {
        Subscriber = Subscriber!,
        Status = Status ?? "dead",
        From = From,
        To = To,
        DryRun = DryRun,
        Reason = Reason,
        IdempotencyKey = key,
    };
}
```

Create `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.Operations.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Management.Internal;

/// <summary>One minimal-API delegate per <see cref="IAlvoOperations"/> member, gated exactly as the management routes are.</summary>
internal static partial class ManagementEndpoints
{
    private const string PausedRequired =
        "Send whether the handler should be paused: {\"paused\": true} pauses it, {\"paused\": false} resumes it; 'reason' is optional.";

    private const string EnabledRequired =
        "Send whether the extension should run: {\"enabled\": false} switches it off, {\"enabled\": true} on; 'reason' is optional.";

    private const string ReplayBodyRequired =
        "A replay needs a JSON body naming the handler: {\"subscriber\": \"…\"}; 'status' (dead unless set), 'from', 'to', 'dryRun' and 'reason' are optional.";

    private static void MapOperations(RouteGroupBuilder group)
    {
        MapDeliveryReads(group);
        MapDeliveryChanges(group);
        MapCodeControls(group);
    }

    private static void MapDeliveryReads(RouteGroupBuilder group)
    {
        Gate(
            group.MapGet(
                "/projects/{project}/deliveries",
                (string project, string? subscriber, string? status, DateTimeOffset? from, DateTimeOffset? to, Guid? after, int? limit,
                    IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.ListDeliveriesAsync(
                        project,
                        new ManagementDeliveryQuery { Subscriber = subscriber, Status = status, From = from, To = to, After = after, Limit = limit ?? 50 },
                        ct))),
            new ManagementRoute(nameof(IAlvoOperations.ListDeliveriesAsync), ManagementOperation.ListDeliveries));

        Gate(
            group.MapGet(
                "/projects/{project}/deliveries/{delivery:guid}",
                (string project, Guid delivery, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.GetDeliveryAsync(project, delivery, ct))),
            new ManagementRoute(nameof(IAlvoOperations.GetDeliveryAsync), ManagementOperation.GetDelivery));

        Gate(
            group.MapGet(
                "/projects/{project}/deliveries/{delivery:guid}/fixture",
                (string project, Guid delivery, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.GetFixtureAsync(project, delivery, ct))),
            new ManagementRoute(nameof(IAlvoOperations.GetFixtureAsync), ManagementOperation.GetDeliveryFixture));

        Gate(
            group.MapGet(
                "/projects/{project}/extensions/stats",
                (string project, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.GetExtensionStatsAsync(project, ct))),
            new ManagementRoute(nameof(IAlvoOperations.GetExtensionStatsAsync), ManagementOperation.GetExtensionStats));
    }

    private static void MapDeliveryChanges(RouteGroupBuilder group)
    {
        Gate(
            group.MapPost(
                "/projects/{project}/deliveries/{delivery:guid}/redeliver",
                (string project, Guid delivery, ManagementReasonBody? body, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.RedeliverAsync(project, delivery, body?.Reason, ct))),
            new ManagementRoute(nameof(IAlvoOperations.RedeliverAsync), ManagementOperation.RedeliverDelivery));

        Gate(
            group.MapPost(
                "/projects/{project}/deliveries/replay",
                (string project, ManagementReplayBody? body, HttpRequest request, IAlvoOperations operations, CancellationToken ct) =>
                    ReplayAsync(project, body, request, operations, ct)),
            new ManagementRoute(nameof(IAlvoOperations.ReplayAsync), ManagementOperation.ReplayDeliveries));
    }

    private static void MapCodeControls(RouteGroupBuilder group)
    {
        Gate(
            group.MapPut(
                "/projects/{project}/subscribers/{subscriber}/paused",
                (string project, string subscriber, ManagementPausedBody? body, IAlvoOperations operations, CancellationToken ct) =>
                    body?.Paused is { } paused
                        ? Answer(() => operations.SetPausedAsync(project, subscriber, paused, body.Reason, ct))
                        : Refused(ProblemResultFactory.ManagementValidation(PausedRequired))),
            new ManagementRoute(nameof(IAlvoOperations.SetPausedAsync), ManagementOperation.PauseSubscriber));

        Gate(
            group.MapPut(
                "/projects/{project}/extensions/{extension}/enabled",
                (string project, string extension, ManagementEnabledBody? body, IAlvoOperations operations, CancellationToken ct) =>
                    body?.Enabled is { } enabled
                        ? Answer(() => operations.SetEnabledAsync(project, extension, enabled, body.Reason, ct))
                        : Refused(ProblemResultFactory.ManagementValidation(EnabledRequired))),
            new ManagementRoute(nameof(IAlvoOperations.SetEnabledAsync), ManagementOperation.SetExtensionEnabled));

        Gate(
            group.MapPost(
                "/projects/{project}/schedules/{schedule}/run",
                (string project, string schedule, IAlvoOperations operations, CancellationToken ct) =>
                    Answer(() => operations.RunScheduleAsync(project, schedule, ct))),
            new ManagementRoute(nameof(IAlvoOperations.RunScheduleAsync), ManagementOperation.RunSchedule));
    }

    private static Task<IResult> ReplayAsync(
        string project, ManagementReplayBody? body, HttpRequest request, IAlvoOperations operations, CancellationToken ct)
    {
        if (!TryReadIdempotencyKey(request, out var key))
        {
            return Refused(ProblemResultFactory.ManagementValidation(RepeatedIdempotencyKey));
        }

        return string.IsNullOrWhiteSpace(body?.Subscriber)
            ? Refused(ProblemResultFactory.ManagementValidation(ReplayBodyRequired))
            : Answer(() => operations.ReplayAsync(project, body.ToRequest(key), ct));
    }
}
```

In `ManagementEndpoints.cs`: declare `internal static partial class ManagementEndpoints`; in `Map`, after `MapRollback(group);`, call `MapOperations(group);`; in `Answer`, after the `ManagementRevisionNotFoundException` clause:

```csharp
        catch (ManagementDeliveryNotFoundException refusal)
        {
            return ProblemResultFactory.ManagementNotFound(refusal.Message);
        }
        catch (ManagementExtensionNotFoundException refusal)
        {
            return ProblemResultFactory.ManagementNotFound(refusal.Message);
        }
        catch (ManagementConflictException refusal)
        {
            return ProblemResultFactory.ManagementConflict(refusal.Message);
        }
```

and extend the class summary to say the operations port's delegates live in `ManagementEndpoints.Operations.cs`.

In `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`, after `ManagementNotFound`:

```csharp
    /// <summary>
    /// The 409 for an operation its target's state does not allow — a running delivery, an extension that cannot be
    /// switched off — with the refusal's own sentence, which says what to do instead.
    /// </summary>
    /// <param name="detail">What stands in the way, and the way round it.</param>
    internal static IResult ManagementConflict(string detail) => Problem(
        StatusCodes.Status409Conflict, AlvoProblemTypes.Conflict, detail);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*OperationsRouteTests'`, `--filter-class '*ManagementContractTests'`, `--filter-class '*ManagementAccessTests'` (the sweep reaches the nine new routes and each refuses 403).
Expected: PASS.

- [ ] **Step 5: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs src/MMLib.Alvo/Management/Internal/ManagementEndpoints.Operations.cs src/MMLib.Alvo/Management/Internal/ManagementOperationsBodies.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs test/_shared/api/AlvoApiWorld.cs test/MMLib.Alvo.Api.Tests/Management/ManagementContractTests.cs test/MMLib.Alvo.Api.Tests/Management/OperationsRouteTests.cs
scripts/test-ring1
git add src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs src/MMLib.Alvo/Management/Internal/ManagementEndpoints.Operations.cs src/MMLib.Alvo/Management/Internal/ManagementOperationsBodies.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs test/_shared/api/AlvoApiWorld.cs test/MMLib.Alvo.Api.Tests/Management/ManagementContractTests.cs test/MMLib.Alvo.Api.Tests/Management/OperationsRouteTests.cs test/MMLib.Alvo.Api.Tests/descriptors/operated-notes.alvo.json
git commit -m "feat(management): serve the operations port over HTTP behind the management gate

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 14: `DeliverAsync` — one fixture through one handler, as the dispatcher would run it

**Files:**
- Modify: `src/MMLib.Alvo/Events/IAlvoEventPump.cs`, `src/MMLib.Alvo/Events/Internal/EventPump.cs`, `src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs`, `src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs`
- Modify: `src/MMLib.Alvo.TestHost/AlvoTestHost.cs`
- Create (test): `test/_shared/testhost/descriptors/ops-desk.alvo.json`, `test/MMLib.Alvo.TestHost.Tests/AlvoTestHostDeliverTests.cs`
- Modify (tests): `test/MMLib.Alvo.Tests/Events/DeliveryDispatcherTests.cs`; baselines `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`, `test/MMLib.Alvo.TestHost.Tests/PublicApi.MMLib.Alvo.TestHost.verified.txt`

**Interfaces:**
- Consumes: `DeliveryFixture` (Task 5); `IAlvoOperations.GetFixtureAsync` (Task 9); HX-03's `DeliveryRunner.RunHandlerAsync`, `FlowOf`, `ReadableEvent`, `AlvoHookTelemetry.StartDelivery`/`Finish`, `DeliveryDispatcher.EnsureManualGates`, `ExtensionBinder.For`.
- Produces: public `IAlvoEventPump.DeliverAsync(DeliveryFixture fixture, CancellationToken cancellationToken = default) → Task` (manual mode only; the handler's own exception propagates; records nothing); public `AlvoTestHost.DeliverAsync(DeliveryFixture fixture, CancellationToken cancellationToken = default) → Task` (inside a drain activity, so `Hooks.AssertRan` counts it); internal `DeliveryDispatcher.DeliverFixtureAsync(DeliveryFixture, CancellationToken)`, `DeliveryRunner.InvokeOnceAsync(AlvoEvent, BoundHandler, int attempt, CancellationToken)`, `DeliveryRunner.ReadableEvent` made `internal static`; the test descriptor `ops-desk.alvo.json` (entity `tickets` with `title` and enum `status`; roles `agent`, `ops`; `access.developer = 'ops' in @user.roles`, `access.admin = 'admin' in @user.roles`).

- [ ] **Step 1: Write the failing tests**

Create `test/_shared/testhost/descriptors/ops-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "ops-desk",
  "auth": { "roles": ["agent", "ops"] },
  "access": {
    "developer": "'ops' in @user.roles",
    "admin": "'admin' in @user.roles"
  },
  "entities": {
    "tickets": {
      "fields": {
        "title": { "type": "string", "maxLength": 120 },
        "status": { "type": "enum", "values": ["open", "closed"] }
      },
      "rules": {
        "list": "'agent' in @user.roles || 'admin' in @user.roles",
        "get": "'agent' in @user.roles || 'admin' in @user.roles",
        "create": "'agent' in @user.roles || 'admin' in @user.roles",
        "update": "'agent' in @user.roles || 'admin' in @user.roles"
      }
    }
  }
}
```

Add to `test/MMLib.Alvo.Tests/Events/DeliveryDispatcherTests.cs` (with `using MMLib.Alvo.Management;` and `using System.Text.Json;`):

```csharp
    [Fact]
    public async Task A_fixture_runs_its_handler_once_and_records_nothing()
    {
        var store = new FakeDeliveryStore(_clock);
        var seen = new List<(Guid Event, int Attempt)>();
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _, AlvoEventContext context) => seen.Add((context.EventId, context.Attempt)));
        var (fixture, eventId) = Fixture(Handler, attempt: 3);

        await dispatcher.DeliverFixtureAsync(fixture, Ct);

        seen.ShouldBe([(eventId, 3)]);
        store.Rows.ShouldBeEmpty();
        store.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_fixture_hands_back_the_handler_s_own_exception()
    {
        var store = new FakeDeliveryStore(_clock);
        var dispatcher = Dispatcher(store, (AlvoCreated<AlvoRecord> _) => throw new InvalidOperationException("template missing"));

        (await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.DeliverFixtureAsync(Fixture(Handler, attempt: 1).Fixture, Ct)))
            .Message.ShouldBe("template missing");
    }

    [Fact]
    public async Task A_fixture_is_refused_in_the_background_mode()
    {
        var dispatcher = Dispatcher(new FakeDeliveryStore(_clock), (AlvoCreated<AlvoRecord> _) => Task.CompletedTask, mode: AlvoEventDispatchMode.Background);

        await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.DeliverFixtureAsync(Fixture(Handler, attempt: 1).Fixture, Ct));
    }

    [Fact]
    public async Task A_fixture_for_a_handler_this_host_does_not_bind_is_refused_naming_the_bound_ones()
    {
        var dispatcher = Dispatcher(new FakeDeliveryStore(_clock), (AlvoCreated<AlvoRecord> _) => Task.CompletedTask);

        (await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.DeliverFixtureAsync(Fixture("someone-else", attempt: 1).Fixture, Ct)))
            .Message.ShouldContain(Handler);
    }

    private (DeliveryFixture Fixture, Guid EventId) Fixture(string subscriber, int attempt)
    {
        var @event = new AlvoEvent
        {
            Id = AlvoEventId.Create(_clock.GetUtcNow()),
            Source = AlvoEvent.DefaultSource,
            Type = "entity.tickets.created",
            Time = _clock.GetUtcNow(),
            Subject = "tickets/1",
            PartitionKey = "tickets:1",
            AuthType = AlvoEventAuthType.ApiKey,
            CorrelationId = "c",
            Data = new AlvoEventData { Record = AlvoRecord.Empty },
        };

        return (new DeliveryFixture
        {
            Subscriber = subscriber,
            Delivery = Guid.NewGuid(),
            Attempt = attempt,
            Envelope = JsonDocument.Parse(AlvoEventJson.Write(@event)).RootElement,
        }, @event.Id);
    }
```

Create `test/MMLib.Alvo.TestHost.Tests/AlvoTestHostDeliverTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// A delivery taken from one host as a fixture reproduces its failure in another (spec 2026-10-14 HX-06 acceptance (6)), and
/// the stats a host reports are its own (acceptance (8)).
/// </summary>
public sealed class AlvoTestHostDeliverTests
{
    private const string Handler = "send-confirmation";

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "descriptors", "ops-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fixture_from_one_host_reproduces_the_failure_in_another()
    {
        DeliveryFixture fixture;
        await using (var production = await StartAsync(Failing))
        {
            await production.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam", ["status"] = "open" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
            await production.DrainEventsAsync(Ct);
            var operations = AsOperator(production);
            var dead = (await operations.ListDeliveriesAsync("ops-desk", new ManagementDeliveryQuery { Status = "dead" }, Ct)).Items.ShouldHaveSingleItem();
            fixture = await operations.GetFixtureAsync("ops-desk", dead.Id, Ct);
        }

        await using var local = await StartAsync(Failing);

        (await Should.ThrowAsync<InvalidOperationException>(() => local.DeliverAsync(fixture, Ct))).Message.ShouldBe("template missing");
        fixture.Envelope.GetRawText().ShouldNotContain("Printer jam");
    }

    [Fact]
    public async Task A_delivered_fixture_runs_the_handler_as_the_dispatcher_would_and_is_counted_as_a_run()
    {
        var seen = new List<(Guid Event, int Attempt, string Subscriber, bool System)>();
        await using var alvo = await StartAsync((AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
            seen.Add((context.EventId, context.Attempt, context.Subscriber, context.Caller.ActorKind == AlvoActorKind.System)));
        var envelope = System.Text.Json.JsonDocument.Parse(AlvoEventJson.Write(Event())).RootElement;

        await alvo.DeliverAsync(new DeliveryFixture { Subscriber = Handler, Delivery = Guid.NewGuid(), Attempt = 4, Envelope = envelope }, Ct);

        seen.ShouldHaveSingleItem().ShouldBe((envelope.GetProperty("id").GetGuid(), 4, Handler, true), "as a delivery: its attempt, its id, its identity");
        alvo.Hooks.AssertRan(Handler, times: 1);
    }

    [Fact]
    public async Task Stats_count_the_runs_this_instance_made()
    {
        var id = $"counted-{Guid.NewGuid():N}";
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Descriptor).Entity("tickets", t => t.OnCreated(id, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask)),
            cancellationToken: Ct);
        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "a" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        var stats = await AsOperator(alvo).GetExtensionStatsAsync("ops-desk", Ct);

        stats.Scope.ShouldBe("this-instance");
        var item = stats.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(id);
        item.Invocations.ShouldBe(1);
        item.P95.ShouldNotBeNull();
    }

    private static Func<AlvoCreated<AlvoRecord>, Task> Failing { get; } = _ => throw new InvalidOperationException("template missing");

    private static Task<AlvoTestHost> StartAsync(Delegate handler) => AlvoTestHost.StartAsync(
        builder => builder.FromDescriptor(Descriptor).Entity("tickets", t => t.OnCreated(Handler, handler, configure: options => options.Retry = AlvoRetryPolicy.None)),
        time: new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)),
        cancellationToken: Ct);

    private static IAlvoOperations AsOperator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("ops"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    private static AlvoEvent Event() => new()
    {
        Id = AlvoEventId.Create(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.created",
        Time = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
        Subject = "tickets/1",
        PartitionKey = "tickets:1",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData { Record = new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["title"] = "x" }) },
    };
}
```

(`AlvoRunAs.System` is the default identity: the fixture runs as the system in the event's tenant, never as the test's caller.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DeliveryDispatcherTests'`
Expected: build FAIL — `DeliverFixtureAsync` does not exist.

- [ ] **Step 3: The runner and the dispatcher**

In `src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs`: change `private static AlvoEvent? ReadableEvent(` to `internal static AlvoEvent? ReadableEvent(`, and add after `RunAsync`:

```csharp
    /// <summary>
    /// Runs <paramref name="handler"/> once over <paramref name="event"/> exactly as an attempt would — its own scope, its
    /// identity, one hop deeper in the chain, its timeout, one delivery span — and records nothing; the handler's exception
    /// propagates as it was thrown.
    /// </summary>
    internal async Task InvokeOnceAsync(AlvoEvent @event, BoundHandler handler, int attempt, CancellationToken cancellationToken)
    {
        var delivery = new ClaimedDelivery
        {
            Id = Guid.Empty,
            EventId = @event.Id,
            Subscriber = handler.Id,
            PartitionKey = @event.PartitionKey,
            Attempts = attempt,
        };
        using var flow = AlvoFlow.Enter(FlowOf(@event));
        using var span = AlvoHookTelemetry.StartDelivery(handler.Id, @event, attempt);
        try
        {
            await RunHandlerAsync(@event, delivery, handler, cancellationToken).WaitAsync(handler.Settings.Timeout, time, cancellationToken).ConfigureAwait(false);
            AlvoHookTelemetry.Finish(span, AlvoHookTelemetry.Succeeded);
        }
        catch (Exception)
        {
            AlvoHookTelemetry.Finish(span, AlvoHookTelemetry.Failed);
            throw;
        }
    }
```

In `src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs`, after `ManualRoundAsync`:

```csharp
    internal async Task DeliverFixtureAsync(DeliveryFixture fixture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var bindings = binder.For(EnsureManualGates());
        var handler = bindings.Handler(fixture.Subscriber) ?? throw UnboundFixture(fixture.Subscriber, bindings);
        var @event = DeliveryRunner.ReadableEvent(fixture.Envelope.GetRawText(), handler.Settings.RunAs)
            ?? throw new InvalidOperationException(
                $"The fixture's envelope cannot be read as an event '{handler.Id}' can run: it is not a CloudEvents envelope Alvo wrote, "
                + "or its tenant or caller id is malformed. Restore the envelope's attributes as they were downloaded.");

        await runner.InvokeOnceAsync(@event, handler, Math.Max(1, fixture.Attempt), cancellationToken).ConfigureAwait(false);
    }

    private static InvalidOperationException UnboundFixture(string subscriber, ExtensionBindings bindings) => new(
        $"The fixture is for '{subscriber}', which this host does not bind. Register the handler under that id, or edit the "
        + $"fixture's subscriber. This host binds: {(bindings.Handlers.Count == 0 ? "none" : string.Join(", ", bindings.Handlers.Select(handler => handler.Id)))}.");
```

(add `using MMLib.Alvo.Management;`).

- [ ] **Step 4: The pump and the test host**

In `src/MMLib.Alvo/Events/IAlvoEventPump.cs` add:

```csharp
    /// <summary>
    /// Runs the handler a fixture names, once, over the fixture's envelope — in its own scope, as its configured identity,
    /// one hop deeper in the chain, under its timeout — exactly as a delivery attempt would, and records nothing.
    /// </summary>
    /// <remarks>
    /// The way to reproduce a delivery taken from another instance: the handler's own exception reaches the caller as it was
    /// thrown. Refused unless the dispatchers are manual — a fixture is an event nobody wrote, and only a test host may run one.
    /// </remarks>
    /// <param name="fixture">The fixture, with whatever redacted values the reproduction needs filled in.</param>
    /// <param name="cancellationToken">Cancels the run, as the host stopping does.</param>
    /// <returns>A task that completes when the handler returns.</returns>
    /// <exception cref="InvalidOperationException">
    /// The dispatch mode is not <see cref="AlvoEventDispatchMode.Manual"/>, the boot is not ready, the handler is not bound,
    /// the envelope cannot be read, or the call was made from inside a round.
    /// </exception>
    /// <exception cref="TimeoutException">The handler ran past its timeout.</exception>
    Task DeliverAsync(Management.DeliveryFixture fixture, CancellationToken cancellationToken = default);
```

In `src/MMLib.Alvo/Events/Internal/EventPump.cs`, extract the round's guard and add the member:

```csharp
    public Task<AlvoDrainResult> DrainAsync(CancellationToken cancellationToken = default) =>
        InRoundAsync(async round => Sum(
            await outbox.ManualRoundAsync(round).ConfigureAwait(false),
            await deliveries.ManualRoundAsync(round).ConfigureAwait(false)), cancellationToken);

    public Task DeliverAsync(DeliveryFixture fixture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return InRoundAsync(async round =>
        {
            await deliveries.DeliverFixtureAsync(fixture, round).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private async Task<T> InRoundAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
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
            return await work(round.Token).ConfigureAwait(false);
        }
        finally
        {
            _inRound.Value = false;
            EndRound();
        }
    }
```

(add `using MMLib.Alvo.Management;`; the existing `DrainAsync` body is replaced by the expression above).

In `src/MMLib.Alvo.TestHost/AlvoTestHost.cs`, after `DrainEventsAsync`:

```csharp
    /// <summary>
    /// Runs the handler a fixture names, once, over its envelope — as a delivery attempt on this host would — and records
    /// nothing; the handler's exception reaches you as it was thrown. Take the fixture from <c>IAlvoOperations.GetFixtureAsync</c>
    /// (or the Management API's <c>…/deliveries/{id}/fixture</c>) on the instance where the delivery failed.
    /// </summary>
    /// <remarks>
    /// The fixture's non-key values arrive as <c>&lt;redacted:FIELD&gt;</c>; fill in what the reproduction needs before calling
    /// this. The run counts for <see cref="Hooks"/> like a delivered one.
    /// </remarks>
    /// <param name="fixture">The fixture.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task that completes when the handler returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The handler is not registered here, or the envelope cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async Task DeliverAsync(DeliveryFixture fixture, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(fixture);

        using var drain = StartDrain();
        await _pump.DeliverAsync(fixture, cancellationToken).ConfigureAwait(false);
    }
```

(add `using MMLib.Alvo.Management;`).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*DeliveryDispatcherTests'`, `--filter-class '*EventPumpRegistrationTests'`; `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*AlvoTestHostDeliverTests'`.
Expected: PASS. Then the core and TestHost public-API baselines grow by `IAlvoEventPump.DeliverAsync` and `AlvoTestHost.DeliverAsync` — accept them and dispatch `alvo-snapshot-judge`.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Events/IAlvoEventPump.cs src/MMLib.Alvo/Events/Internal/EventPump.cs src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs src/MMLib.Alvo.TestHost/AlvoTestHost.cs test/MMLib.Alvo.Tests/Events/DeliveryDispatcherTests.cs test/MMLib.Alvo.TestHost.Tests/AlvoTestHostDeliverTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Events/IAlvoEventPump.cs src/MMLib.Alvo/Events/Internal/EventPump.cs src/MMLib.Alvo/Events/Internal/DeliveryDispatcher.cs src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs src/MMLib.Alvo.TestHost/AlvoTestHost.cs test/MMLib.Alvo.Tests/Events/DeliveryDispatcherTests.cs test/MMLib.Alvo.TestHost.Tests/AlvoTestHostDeliverTests.cs test/_shared/testhost/descriptors/ops-desk.alvo.json test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt test/MMLib.Alvo.TestHost.Tests/PublicApi.MMLib.Alvo.TestHost.verified.txt
git commit -m "feat(testhost): reproduce a delivery locally from its redacted fixture

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 15: Delivery operations on both engines **[slice end]**

**Files:**
- Create: `test/_shared/testhost/DeliveryOperationsFacts.cs`, `test/MMLib.Alvo.TestHost.Tests/SqliteDeliveryOperationsTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryOperationsTests.cs`
- Modify: `test/_shared/testhost/DeliveryRows.cs` (`OperationsAsync`)

**Interfaces:**
- Consumes: everything above; `ops-desk.alvo.json` (Task 14); `AuditCapture` (Task 9); HX-03's `DispatchedEventCollection` and `PostgresFixture`.
- Produces: `DeliveryRows.OperationsAsync(Func<DbConnection>, CancellationToken) → Task<IReadOnlyList<string>>` (the log's `operation` column, by `at` then `id`); the abstract `DeliveryOperationsFacts` with `protected abstract Task<OperationsDatabase> DatabaseAsync()` and `protected sealed record OperationsDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)`.

- [ ] **Step 1: Write the facts**

Append to `test/_shared/testhost/DeliveryRows.cs` (and change its summary to "Reads the delivery and operations tables straight off the database, so a fact sees what the engine holds, not what the port reports."):

```csharp
    internal static async Task<IReadOnlyList<string>> OperationsAsync(Func<DbConnection> connect, CancellationToken ct)
    {
        await using var connection = connect();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation FROM alvo_operations_log ORDER BY at, id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var operations = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            operations.Add(reader.GetString(0));
        }

        return operations;
    }
```

Create `test/_shared/testhost/DeliveryOperationsFacts.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Management;

using System.Collections.Concurrent;
using System.Data.Common;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Delivery operations over the real pipeline (spec 2026-10-14 §7 HX-06) on whichever engine the deriving class supplies —
/// SQLite in <c>MMLib.Alvo.TestHost.Tests</c>, PostgreSQL in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class DeliveryOperationsFacts
{
    private const string Project = "ops-desk";
    private const string Tickets = "tickets";
    private const string Handler = "send-confirmation";

    private static readonly DateTimeOffset _start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AlvoContext Agent { get; } = AlvoTestHost.Caller("agent");

    /// <summary>(1) A replay dry run returns the exact count and changes nothing.</summary>
    [Fact]
    public async Task A_replay_dry_run_returns_the_exact_count_and_changes_nothing()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, (AlvoCreated<AlvoRecord> _) => throw new InvalidOperationException("down"));
        foreach (var title in new[] { "one", "two", "three" })
        {
            await alvo.Data.CreateAsync(Tickets, Values(("title", title)), Agent, cancellationToken: Ct);
        }

        await alvo.DrainEventsAsync(Ct);
        var before = await DeliveryRows.ReadAsync(database.Connect, Ct);
        var attempts = await DeliveryRows.AttemptCountAsync(database.Connect, Ct);

        var plan = await AsOperator(alvo).ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, DryRun = true }, Ct);

        plan.Count.ShouldBe(3);
        plan.Ids.Count.ShouldBe(3);
        before.ShouldAllBe(row => row.Status == "dead");
        (await DeliveryRows.ReadAsync(database.Connect, Ct)).ShouldBe(before);
        (await DeliveryRows.AttemptCountAsync(database.Connect, Ct)).ShouldBe(attempts);
        (await DeliveryRows.OperationsAsync(database.Connect, Ct)).ShouldBeEmpty();
    }

    /// <summary>(2) A paused handler accumulates its deliveries and drains them in order after resume.</summary>
    [Fact]
    public async Task A_paused_handler_accumulates_and_drains_in_order_after_resume()
    {
        var database = await DatabaseAsync();
        var seen = new ConcurrentQueue<string>();
        await using var alvo = await StartSavedAsync(database, (AlvoSaved<AlvoRecord> saved) => seen.Enqueue((string)saved.New["title"]!));
        var operations = AsOperator(alvo);
        await operations.SetPausedAsync(Project, Handler, paused: true, "mail provider down", Ct);

        var ticket = await alvo.Data.CreateAsync(Tickets, Values(("title", "first")), Agent, cancellationToken: Ct);
        foreach (var title in new[] { "second", "third" })
        {
            await alvo.Data.UpdateAsync(Tickets, (Guid)ticket[AlvoManagedColumns.Id]!, Values(("title", title)), Agent, cancellationToken: Ct);
        }

        await alvo.DrainEventsAsync(Ct);
        seen.ShouldBeEmpty("a paused handler runs nothing");
        (await DeliveryRows.ReadAsync(database.Connect, Ct)).Select(row => row.Status).ShouldBe(["pending", "pending", "pending"]);

        await operations.SetPausedAsync(Project, Handler, paused: false, reason: null, Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe(["first", "second", "third"]);
    }

    /// <summary>(3) Redelivering a dead delivery unblocks its record's later deliveries.</summary>
    [Fact]
    public async Task Redelivering_a_dead_delivery_unblocks_its_lane()
    {
        var database = await DatabaseAsync();
        var failing = true;
        var seen = new ConcurrentQueue<string>();
        await using var alvo = await StartSavedAsync(database, (AlvoSaved<AlvoRecord> saved) =>
        {
            if (Volatile.Read(ref failing))
            {
                throw new InvalidOperationException("template missing");
            }

            seen.Enqueue((string)saved.New["title"]!);
        });
        var ticket = await alvo.Data.CreateAsync(Tickets, Values(("title", "first")), Agent, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);
        Volatile.Write(ref failing, false);
        await alvo.Data.UpdateAsync(Tickets, (Guid)ticket[AlvoManagedColumns.Id]!, Values(("title", "second")), Agent, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);
        seen.ShouldBeEmpty("the dead first delivery holds the record's lane");
        var operations = AsOperator(alvo);
        var dead = (await operations.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Status = "dead" }, Ct)).Items.ShouldHaveSingleItem();

        await operations.RedeliverAsync(Project, dead.Id, "template restored", Ct);
        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe(["first", "second"]);
        var history = (await operations.GetDeliveryAsync(Project, dead.Id, Ct)).History;
        history.Select(attempt => (attempt.Attempt, attempt.Outcome)).ShouldBe([(1, "dead"), (2, "succeeded")]);
    }

    /// <summary>(7) Pause, redeliver and replay each write one operations-log row and one <c>ManagementAudit</c> line.</summary>
    [Fact]
    public async Task Pause_redeliver_and_replay_each_write_one_log_row_and_one_audit_line()
    {
        var database = await DatabaseAsync();
        var audit = new AuditCapture();
        await using var alvo = await StartAsync(
            database,
            (AlvoCreated<AlvoRecord> _) => throw new InvalidOperationException("down"),
            services => services.AddSingleton<ILoggerProvider>(audit));
        await alvo.Data.CreateAsync(Tickets, Values(("title", "one")), Agent, cancellationToken: Ct);
        await alvo.Data.CreateAsync(Tickets, Values(("title", "two")), Agent, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);
        var operations = AsOperator(alvo);
        var dead = (await operations.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Status = "dead", Limit = 1 }, Ct)).Items.Single();

        await operations.SetPausedAsync(Project, Handler, paused: true, "maintenance", Ct);
        await operations.SetPausedAsync(Project, Handler, paused: true, "maintenance", Ct);
        await operations.RedeliverAsync(Project, dead.Id, "retry one", Ct);
        await operations.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Reason = "retry the rest" }, Ct);

        (await DeliveryRows.OperationsAsync(database.Connect, Ct)).ShouldBe(["pause", "redeliver", "replay"]);
        audit.Lines.Count.ShouldBe(3);
        audit.Lines.ShouldContain(line => line.Contains("made 'replay' on 'send-confirmation' (1 changed)", StringComparison.Ordinal));
    }

    /// <summary>Registers this leg's database; each fact gets a database of its own.</summary>
    protected abstract Task<OperationsDatabase> DatabaseAsync();

    private static Task<AlvoTestHost> StartAsync(OperationsDatabase database, Delegate handler, Action<IServiceCollection>? services = null) =>
        AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "ops-desk.alvo.json"));
                alvo.Entity(Tickets, t => t.OnCreated(Handler, handler, configure: options => options.Retry = AlvoRetryPolicy.None));
                services?.Invoke(alvo.Services);
            },
            new FakeTimeProvider(_start),
            webhooks: null,
            Ct);

    private static Task<AlvoTestHost> StartSavedAsync(OperationsDatabase database, Delegate handler) =>
        AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "ops-desk.alvo.json"));
                alvo.Entity(Tickets, t => t.OnSaved(Handler, handler, configure: options => options.Retry = AlvoRetryPolicy.None));
            },
            new FakeTimeProvider(_start),
            webhooks: null,
            Ct);

    private static IAlvoOperations AsOperator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("ops"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    private static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal);

    /// <summary>One leg's database: how to point a host at it, and how to read it directly.</summary>
    protected sealed record OperationsDatabase(Action<IAlvoBuilder> Use, Func<DbConnection> Connect);
}
```

In (7) the second pause changes nothing and so writes nothing; the redeliver resets one dead delivery; the replay then resets the other (the first is pending again, no longer dead), so its count is 1.

Create `test/MMLib.Alvo.TestHost.Tests/SqliteDeliveryOperationsTests.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The delivery-operations facts on SQLite — a temp file per fact.</summary>
public sealed class SqliteDeliveryOperationsTests : DeliveryOperationsFacts, IDisposable
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

    protected override Task<OperationsDatabase> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-operations-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = $"Data Source={path}";

        return Task.FromResult(new OperationsDatabase(alvo => alvo.UseSqlite(connectionString), () => new SqliteConnection(connectionString)));
    }
}
```

Create `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryOperationsTests.cs`:

```csharp
using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The delivery-operations facts with <c>UsePostgreSql</c> — a fresh database per fact on one container, dropped when the class ends.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlDeliveryOperationsTests(PostgresFixture postgres)
    : DeliveryOperationsFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
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

    protected override async Task<OperationsDatabase> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_operations_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();

        return new OperationsDatabase(alvo => alvo.UsePostgreSql(connectionString), () => new NpgsqlConnection(connectionString));
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

- [ ] **Step 2: Run the SQLite leg**

Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteDeliveryOperationsTests'`
Expected: PASS (all four facts) — the behaviour arrived with Tasks 1–14; a failure here is a real defect in one of them, fixed where it lives, never in the fact.

- [ ] **Step 3: Run the PostgreSQL leg**

Run: `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlDeliveryOperationsTests'` and `--filter-class '*PostgreSqlDeliveryStoreTests'` (the contract rows of Tasks 2–4 on PostgreSQL).
Expected: PASS (skipped only where Docker is unavailable). Watch for a PostgreSQL-only defect in the upsert's `WHERE`, the partial unique index's `ON CONFLICT DO NOTHING`, or a `DBNull` parameter whose type the server cannot infer; a defect is fixed in `DeliveryOperationsTable` with identical text for both engines.

- [ ] **Step 4: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include test/_shared/testhost/DeliveryOperationsFacts.cs test/_shared/testhost/DeliveryRows.cs test/MMLib.Alvo.TestHost.Tests/SqliteDeliveryOperationsTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryOperationsTests.cs
scripts/test-ring1
git add test/_shared/testhost/DeliveryOperationsFacts.cs test/_shared/testhost/DeliveryRows.cs test/MMLib.Alvo.TestHost.Tests/SqliteDeliveryOperationsTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlDeliveryOperationsTests.cs
git commit -m "test(events): prove replay, pause, redeliver and the audit on both engines

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 16: The dashboard's operations reads and writes — `OperationsGateway` and `OperationsView`

**Files:**
- Create: `src/MMLib.Alvo.Admin/Internal/OperationsGateway.cs`, `src/MMLib.Alvo.Admin/Components/Operations/OperationsView.cs`
- Modify: `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs` (`ForProjectAsync`), `src/MMLib.Alvo.Admin/Internal/AdminProblem.cs` (three refusal titles), `src/MMLib.Alvo.Admin/AlvoAdminServiceCollectionExtensions.cs` (register the gateway)
- Test: `test/MMLib.Alvo.Admin.Tests/Operations/OperationsViewTests.cs`, `test/MMLib.Alvo.Admin.Tests/Operations/OperationsGatewayTests.cs`

**Interfaces:**
- Consumes: `IAlvoOperations` (Tasks 6, 11), `ManagementGateway.ExtensionsAsync`, `.Invalidate()`, private `ProjectAsync`/`AsOperatorAsync`.
- Produces:
  - `ManagementGateway.ForProjectAsync<T>(Func<string, Task<T>> call, CancellationToken ct) → Task<T>` (internal: the project, then the call with the operator published).
  - `internal sealed class OperationsGateway(IAlvoOperations operations, ManagementGateway management)` — `Task<IReadOnlyList<ManagementExtension>> HandlersAsync(CancellationToken)`, `Task<ManagementExtensionStats> StatsAsync(CancellationToken)`, `Task<ManagementDeliveryPage> DeliveriesAsync(string? status, CancellationToken)`, `Task<ManagementDelivery> DeliveryAsync(Guid, CancellationToken)`, `Task<ManagementDelivery> RedeliverAsync(Guid, string? reason, CancellationToken)`, `Task<ManagementReplayPlan> PlanReplayAsync(string subscriber, CancellationToken)`, `Task<ManagementReplayPlan> ReplayAsync(string subscriber, string? reason, string key, CancellationToken)`, `Task<DeliveryFixture> FixtureAsync(Guid, CancellationToken)`, `Task<ManagementExtension> SetPausedAsync(string subscriber, bool paused, string? reason, CancellationToken)` (drops the catalog cache).
  - `internal static class OperationsView` — `IReadOnlyList<string> StatusFilters` (`dead`, `retrying`, `pending`, `running`, `succeeded`, `cancelled`), `string BadgeClass(string status)`, `string Duration(TimeSpan? value)`, `string RunsLine(ManagementExtensionStat? stat)`, `string AttemptLine(ManagementDeliveryAttempt attempt)`, `string ReplayQuestion(ManagementReplayPlan plan, string subscriber)`, `string FixtureJson(DeliveryFixture fixture)`, `bool CanRedeliver(ManagementDelivery delivery)`.

- [ ] **Step 1: Write the failing tests**

Create `test/MMLib.Alvo.Admin.Tests/Operations/OperationsViewTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Operations;
using MMLib.Alvo.Management;

using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Operations;

/// <summary>The Operations page's words and tones, as logic.</summary>
public sealed class OperationsViewTests
{
    [Theory]
    [InlineData("succeeded", "a-badge a-badge--ok")]
    [InlineData("dead", "a-badge a-badge--danger")]
    [InlineData("retrying", "a-badge a-badge--warn")]
    [InlineData("pending", "a-badge a-badge--accent")]
    [InlineData("running", "a-badge a-badge--accent")]
    [InlineData("cancelled", "a-badge")]
    public void A_status_has_its_tone(string status, string css) => OperationsView.BadgeClass(status).ShouldBe(css);

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0.4, "0.4 ms")]
    [InlineData(12, "12 ms")]
    [InlineData(1_250, "1.25 s")]
    [InlineData(90_000, "1.5 min")]
    public void A_duration_reads_in_its_own_unit(double? milliseconds, string text) =>
        OperationsView.Duration(milliseconds is { } value ? TimeSpan.FromMilliseconds(value) : null).ShouldBe(text);

    [Fact]
    public void A_handler_s_runs_say_how_many_how_many_failed_and_how_long_on_this_instance()
    {
        OperationsView.RunsLine(null).ShouldBe("no runs on this instance in the last 24 hours");
        OperationsView.RunsLine(new ManagementExtensionStat { Id = "x", Invocations = 42, Failures = 3, P50 = TimeSpan.FromMilliseconds(12), P95 = TimeSpan.FromMilliseconds(80) })
            .ShouldBe("42 runs · 3 failed · p50 12 ms · p95 80 ms — this instance, last 24 hours");
    }

    [Fact]
    public void An_attempt_line_names_its_number_outcome_error_and_duration()
    {
        OperationsView.AttemptLine(new ManagementDeliveryAttempt { Attempt = 1, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromSeconds(30), Outcome = "failed", ErrorType = "timeout" })
            .ShouldBe("#1 · failed · timeout · 30 s");
        OperationsView.AttemptLine(new ManagementDeliveryAttempt { Attempt = 2, StartedAt = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMilliseconds(12), Outcome = "succeeded" })
            .ShouldBe("#2 · succeeded · 12 ms");
    }

    [Fact]
    public void A_replay_asks_with_the_count_it_will_run()
    {
        OperationsView.ReplayQuestion(new ManagementReplayPlan { DryRun = true, Count = 3, Ids = [] }, "send-confirmation")
            .ShouldBe("Run 3 dead deliveries of send-confirmation again?");
        OperationsView.ReplayQuestion(new ManagementReplayPlan { DryRun = true, Count = 1, Ids = [] }, "send-confirmation")
            .ShouldBe("Run 1 dead delivery of send-confirmation again?");
    }

    [Theory]
    [InlineData("dead", true)]
    [InlineData("succeeded", true)]
    [InlineData("cancelled", true)]
    [InlineData("pending", false)]
    [InlineData("running", false)]
    [InlineData("retrying", false)]
    public void Only_a_finished_delivery_offers_a_redeliver(string status, bool offered) =>
        OperationsView.CanRedeliver(Delivery(status)).ShouldBe(offered);

    [Fact]
    public void A_fixture_is_shown_as_the_json_it_downloads_as()
    {
        var fixture = new DeliveryFixture
        {
            Subscriber = "send-confirmation",
            Delivery = Guid.Empty,
            Attempt = 1,
            Envelope = JsonDocument.Parse("""{"id":"e"}""").RootElement,
        };

        var json = OperationsView.FixtureJson(fixture);

        json.ShouldContain("\"subscriber\": \"send-confirmation\"");
        JsonSerializer.Deserialize<DeliveryFixture>(json, JsonSerializerOptions.Web)!.Subscriber.ShouldBe("send-confirmation");
    }

    private static ManagementDelivery Delivery(string status) => new()
    {
        Id = Guid.Empty,
        EventId = Guid.Empty,
        Subscriber = "send-confirmation",
        PartitionKey = "tickets:1",
        Status = status,
        Attempts = 1,
        NotBefore = DateTimeOffset.UnixEpoch,
    };
}
```

Create `test/MMLib.Alvo.Admin.Tests/Operations/OperationsGatewayTests.cs`:

```csharp
using Microsoft.AspNetCore.Components.Authorization;

using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

using NSubstitute;

using System.Security.Claims;

namespace MMLib.Alvo.Admin.Tests.Operations;

/// <summary>The dashboard asks the operations port for the project it is looking at, and drops the catalog after a pause.</summary>
public sealed class OperationsGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_of_deliveries_is_asked_for_the_dashboard_s_project()
    {
        var operations = Substitute.For<IAlvoOperations>();
        operations.ListDeliveriesAsync(Arg.Any<string>(), Arg.Any<ManagementDeliveryQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementDeliveryPage { Items = [] });
        var gateway = new OperationsGateway(operations, Management(out _));

        await gateway.DeliveriesAsync("dead", Ct);

        await operations.Received(1).ListDeliveriesAsync("default", Arg.Is<ManagementDeliveryQuery>(query => query.Status == "dead"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pause_drops_the_cached_catalog_so_the_handler_reads_paused()
    {
        var operations = Substitute.For<IAlvoOperations>();
        var management = Management(out var contract);
        contract.GetExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ManagementExtensionCatalog { Items = [] });
        var gateway = new OperationsGateway(operations, management);
        await gateway.HandlersAsync(Ct);

        await gateway.SetPausedAsync("send-confirmation", paused: true, "down", Ct);
        await gateway.HandlersAsync(Ct);

        await operations.Received(1).SetPausedAsync("default", "send-confirmation", true, "down", Arg.Any<CancellationToken>());
        await contract.Received(2).GetExtensionsAsync("default", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_replay_dry_run_carries_no_key_and_the_real_run_carries_the_one_it_was_given()
    {
        var operations = Substitute.For<IAlvoOperations>();
        operations.ReplayAsync(Arg.Any<string>(), Arg.Any<ManagementReplayRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ManagementReplayPlan { DryRun = true, Count = 0, Ids = [] });
        var gateway = new OperationsGateway(operations, Management(out _));

        await gateway.PlanReplayAsync("send-confirmation", Ct);
        await gateway.ReplayAsync("send-confirmation", "endpoint back", "key-1", Ct);

        await operations.Received(1).ReplayAsync("default", Arg.Is<ManagementReplayRequest>(request => request.DryRun && request.IdempotencyKey == null), Arg.Any<CancellationToken>());
        await operations.Received(1).ReplayAsync("default", Arg.Is<ManagementReplayRequest>(request => !request.DryRun && request.IdempotencyKey == "key-1" && request.Reason == "endpoint back"), Arg.Any<CancellationToken>());
    }

    private static ManagementGateway Management(out IAlvoManagement contract)
    {
        contract = Substitute.For<IAlvoManagement>();
        contract.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns([new ManagementProject("default", 1, "applied")]);
        var authentication = Substitute.For<AuthenticationStateProvider>();
        authentication.GetAuthenticationStateAsync().Returns(Task.FromResult(new AuthenticationState(new ClaimsPrincipal())));

        return new ManagementGateway(contract, people: null, Substitute.For<IAlvoAdminCallerResolver>(), authentication, Substitute.For<IAlvoContextAccessor>());
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*OperationsViewTests'`
Expected: build FAIL — `OperationsView`, `OperationsGateway` do not exist.

- [ ] **Step 3: Implement**

In `src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs`, beside `ExtensionsAsync`:

```csharp
    /// <summary>Runs one call of another management port for this dashboard's project, with the operator published.</summary>
    /// <remarks>The operations port gates itself exactly as this contract does; the dashboard only says who is asking.</remarks>
    internal async Task<T> ForProjectAsync<T>(Func<string, Task<T>> call, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);

        return await AsOperatorAsync(() => call(project), ct).ConfigureAwait(false);
    }
```

Create `src/MMLib.Alvo.Admin/Internal/OperationsGateway.cs`:

```csharp
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The dashboard's one caller of <see cref="IAlvoOperations"/>: uncached — deliveries move under the operator's eyes — and
/// authorizing nothing; every call runs as the signed-in operator through <see cref="ManagementGateway.ForProjectAsync"/>.
/// </summary>
/// <param name="operations">The operations port, resolved in-process.</param>
/// <param name="management">The management gateway: the project, the operator, and the extension catalog's cache.</param>
internal sealed class OperationsGateway(IAlvoOperations operations, ManagementGateway management)
{
    private const int PageSize = 50;

    public async Task<IReadOnlyList<ManagementExtension>> HandlersAsync(CancellationToken ct) =>
        [.. (await management.ExtensionsAsync(ct).ConfigureAwait(false)).Where(extension => extension.Kind == AlvoExtensionKind.EventHandler)];

    public Task<ManagementExtensionStats> StatsAsync(CancellationToken ct) =>
        management.ForProjectAsync(project => operations.GetExtensionStatsAsync(project, ct), ct);

    public Task<ManagementDeliveryPage> DeliveriesAsync(string? status, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.ListDeliveriesAsync(project, new ManagementDeliveryQuery { Status = status, Limit = PageSize }, ct), ct);

    public Task<ManagementDelivery> DeliveryAsync(Guid delivery, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.GetDeliveryAsync(project, delivery, ct), ct);

    public Task<ManagementDelivery> RedeliverAsync(Guid delivery, string? reason, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.RedeliverAsync(project, delivery, reason, ct), ct);

    public Task<ManagementReplayPlan> PlanReplayAsync(string subscriber, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.ReplayAsync(project, new ManagementReplayRequest { Subscriber = subscriber, DryRun = true }, ct), ct);

    public Task<ManagementReplayPlan> ReplayAsync(string subscriber, string? reason, string key, CancellationToken ct) =>
        management.ForProjectAsync(
            project => operations.ReplayAsync(project, new ManagementReplayRequest { Subscriber = subscriber, Reason = reason, IdempotencyKey = key }, ct),
            ct);

    public Task<DeliveryFixture> FixtureAsync(Guid delivery, CancellationToken ct) =>
        management.ForProjectAsync(project => operations.GetFixtureAsync(project, delivery, ct), ct);

    public async Task<ManagementExtension> SetPausedAsync(string subscriber, bool paused, string? reason, CancellationToken ct)
    {
        var handler = await management.ForProjectAsync(project => operations.SetPausedAsync(project, subscriber, paused, reason, ct), ct).ConfigureAwait(false);
        management.Invalidate();

        return handler;
    }
}
```

Create `src/MMLib.Alvo.Admin/Components/Operations/OperationsView.cs`:

```csharp
using MMLib.Alvo.Management;

using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Operations;

/// <summary>The Operations page's words and tones, kept out of the markup so they can be tested.</summary>
internal static class OperationsView
{
    private const string Separator = " · ";

    private static readonly JsonSerializerOptions _fixtureJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The statuses the delivery list filters by, in the order an operator looks for trouble.</summary>
    public static IReadOnlyList<string> StatusFilters { get; } = ["dead", "retrying", "pending", "running", "succeeded", "cancelled"];

    public static string BadgeClass(string status) => status switch
    {
        "succeeded" => "a-badge a-badge--ok",
        "dead" => "a-badge a-badge--danger",
        "retrying" => "a-badge a-badge--warn",
        "pending" or "running" => "a-badge a-badge--accent",
        _ => "a-badge",
    };

    public static string Duration(TimeSpan? value) => value switch
    {
        null => "—",
        { TotalSeconds: < 1 } short => Number(short.TotalMilliseconds, "ms"),
        { TotalMinutes: < 1 } medium => Number(medium.TotalSeconds, "s"),
        { TotalHours: < 1 } longer => Number(longer.TotalMinutes, "min"),
        { } longest => Number(longest.TotalHours, "h"),
    };

    public static string RunsLine(ManagementExtensionStat? stat) => stat is not { Invocations: > 0 } runs
        ? "no runs on this instance in the last 24 hours"
        : string.Create(CultureInfo.InvariantCulture, $"{runs.Invocations} runs{Separator}{runs.Failures} failed{Separator}p50 {Duration(runs.P50)}{Separator}p95 {Duration(runs.P95)} — this instance, last 24 hours");

    public static string AttemptLine(ManagementDeliveryAttempt attempt) => string.Join(Separator, AttemptParts(attempt));

    public static string ReplayQuestion(ManagementReplayPlan plan, string subscriber) =>
        string.Create(CultureInfo.InvariantCulture, $"Run {plan.Count} dead {(plan.Count == 1 ? "delivery" : "deliveries")} of {subscriber} again?");

    public static string FixtureJson(DeliveryFixture fixture) => JsonSerializer.Serialize(fixture, _fixtureJson);

    public static bool CanRedeliver(ManagementDelivery delivery) => delivery.Status is "dead" or "succeeded" or "cancelled";

    private static IEnumerable<string> AttemptParts(ManagementDeliveryAttempt attempt)
    {
        yield return string.Create(CultureInfo.InvariantCulture, $"#{attempt.Attempt}");
        yield return attempt.Outcome;
        if (attempt.ErrorType is { } error)
        {
            yield return error;
        }

        yield return Duration(attempt.Duration);
    }

    private static string Number(double amount, string unit) => string.Create(CultureInfo.InvariantCulture, $"{amount:0.##} {unit}");
}
```

In `src/MMLib.Alvo.Admin/Internal/AdminProblem.cs`, `RefusalTitle`, after the `ManagementRevisionNotFoundException` arm:

```csharp
        ManagementDeliveryNotFoundException => "That delivery is not there",
        ManagementExtensionNotFoundException => "This host registers no such extension",
        ManagementConflictException => "That cannot be done in the state it is in",
```

In `src/MMLib.Alvo.Admin/AlvoAdminServiceCollectionExtensions.cs`, after `services.TryAddScoped<DataGateway>();`:

```csharp
        /* Scoped like the management gateway it composes: the operator, the project and the catalog cache are this circuit's. */
        services.TryAddScoped(provider => new OperationsGateway(
            provider.GetRequiredService<IAlvoOperations>(),
            provider.GetRequiredService<ManagementGateway>()));
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*OperationsViewTests'` and `--filter-class '*OperationsGatewayTests'`; `--filter-class '*BoundaryArchitectureTests'` (the Admin package still references no core type).
Expected: PASS.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Admin/Internal/OperationsGateway.cs src/MMLib.Alvo.Admin/Components/Operations/OperationsView.cs src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs src/MMLib.Alvo.Admin/Internal/AdminProblem.cs src/MMLib.Alvo.Admin/AlvoAdminServiceCollectionExtensions.cs test/MMLib.Alvo.Admin.Tests/Operations/OperationsViewTests.cs test/MMLib.Alvo.Admin.Tests/Operations/OperationsGatewayTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Admin/Internal/OperationsGateway.cs src/MMLib.Alvo.Admin/Components/Operations/OperationsView.cs src/MMLib.Alvo.Admin/Internal/ManagementGateway.cs src/MMLib.Alvo.Admin/Internal/AdminProblem.cs src/MMLib.Alvo.Admin/AlvoAdminServiceCollectionExtensions.cs test/MMLib.Alvo.Admin.Tests/Operations/OperationsViewTests.cs test/MMLib.Alvo.Admin.Tests/Operations/OperationsGatewayTests.cs
git commit -m "feat(admin): read and operate deliveries through the operations port

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 17: The Operations page and its end-to-end scenario **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Operations/Operations.razor`, `Operations.razor.cs`
- Modify: `src/MMLib.Alvo.Admin/Internal/AdminPaths.cs` (`Operations`), `src/MMLib.Alvo.Admin/Components/DesignSystem/Icons.cs` (`Operations`), `src/MMLib.Alvo.Admin/Components/Shell/AdminSection.cs` (the nav entry)
- Create (tests): `test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsWorld.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsScenarios.cs`
- Modify (tests): `test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt`

**Interfaces:**
- Consumes: Task 16's gateway and view; the design system (`PageHeader`, `Panel`, `ListRow`, `AlvoButton`, `AlvoConfirm`, `EmptyState`, `Skeleton`, `RefreshBar`, `RefusalPanel`, `RefusalState<AdminProblem>`, `CodeBlock`), `AdminProblem.From`.
- Produces: the routable `/admin/operations` page (`Operations`), `AdminPaths.Operations` (`$"{AlvoAdmin.BasePath}/operations"`), `Icons.Operations`, the nav section *Operations* (go key `p`); test ids `ops-handler-{id}`, `ops-handler-paused-{id}`, `ops-runs-{id}`, `ops-pause-{id}`, `ops-resume-{id}`, `ops-replay-{id}`, `ops-reason`, `ops-confirm`/`ops-confirm-run`/`ops-confirm-cancel`, `ops-refresh`, `ops-status-filter-{status}`, `ops-delivery-list`, `ops-row-{id}`, `ops-delivery-status`, `ops-delivery-history`, `ops-redeliver`, `ops-fixture`, `ops-fixture-json`.

- [ ] **Step 1: Write the failing scenario**

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsWorld.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The field-service world with one handler on <c>regions</c> that records the code of every region it is handed.</summary>
/// <remarks>Registered through <see cref="AdminWorld"/>'s test-side builder seam; the dispatcher runs in the background, as in production.</remarks>
public sealed class OperationsWorld : AdminWorld
{
    internal const string RegionSync = "region-sync";

    internal ConcurrentQueue<string> Synced { get; } = new();

    /// <inheritdoc/>
    protected override void Configure(IAlvoBuilder alvo) => alvo
        .Entity("regions", regions => regions.OnCreated(
            RegionSync,
            (AlvoCreated<AlvoRecord> created) => Synced.Enqueue((string)created.New["code"]!),
            configure: options => options.Description = "Copies every new region to the dispatch system."));
}
```

Create `test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsScenarios.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>Operating a handler from the dashboard (spec 2026-10-14 HX-06 acceptance (10)): pause → write → resume → delivered.</summary>
/// <param name="world">The field-service world with one region handler, and a browser.</param>
public sealed class OperationsScenarios(OperationsWorld world) : IClassFixture<OperationsWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_paused_handler_holds_its_delivery_until_it_is_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await world.SignInAsync(ct);
        await session.GoAsync("/operations");
        var handler = session.Page.GetByTestId($"ops-handler-{OperationsWorld.RegionSync}");
        await handler.WaitForAsync();

        await handler.GetByTestId($"ops-pause-{OperationsWorld.RegionSync}").ClickAsync();
        var confirm = session.Dialog("ops-confirm");
        await confirm.GetByTestId("ops-reason").Locator("input").FillAsync("dispatch system down");
        await confirm.GetByTestId("ops-confirm-run").ClickAsync();
        await Assertions.Expect(session.Page.GetByTestId($"ops-handler-paused-{OperationsWorld.RegionSync}")).ToBeVisibleAsync();

        await world.Services.GetRequiredService<IAlvoData>().CreateAsync(
            "regions", new Dictionary<string, object?> { ["code"] = "NORTH-9", ["name"] = "North nine" }, AlvoContext.System(null), cancellationToken: ct);
        await session.Page.GetByTestId("ops-refresh").ClickAsync();
        var row = session.Page.GetByTestId("ops-delivery-list").Locator("[data-testid^='ops-row-']").First;
        await Assertions.Expect(row.GetByTestId("ops-delivery-status")).ToHaveTextAsync("pending");
        world.Synced.ShouldNotContain("NORTH-9", "a paused handler runs nothing");

        await handler.GetByTestId($"ops-resume-{OperationsWorld.RegionSync}").ClickAsync();
        await Assertions.Expect(session.Page.GetByTestId($"ops-handler-paused-{OperationsWorld.RegionSync}")).ToHaveCountAsync(0);
        await WaitUntilDeliveredAsync(session, row);

        world.Synced.ShouldContain("NORTH-9");
        session.AssertConsoleClean();
    }

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_page_fits_a_phone_and_names_the_scope_of_its_numbers()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken, width: 375);
        await session.GoAsync("/operations");
        var runs = session.Page.GetByTestId($"ops-runs-{OperationsWorld.RegionSync}");
        await runs.WaitForAsync();

        (await runs.InnerTextAsync()).ShouldContain("this instance");
        await session.AssertNoHorizontalScrollAsync();
        session.AssertConsoleClean();
    }

    private static async Task WaitUntilDeliveredAsync(AdminSession session, ILocator row)
    {
        for (var poll = 0; poll < 30; poll++)
        {
            if ((await row.GetByTestId("ops-delivery-status").InnerTextAsync()).Trim() == "succeeded")
            {
                return;
            }

            await session.Page.WaitForTimeoutAsync(500);
            await session.Page.GetByTestId("ops-refresh").ClickAsync();
        }

        (await row.GetByTestId("ops-delivery-status").InnerTextAsync()).Trim().ShouldBe("succeeded", "the resumed handler ran within 15 seconds");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `scripts/test-admin-e2e --filter-class '*OperationsScenarios'` (or `dotnet test --project test/MMLib.Alvo.Admin.Tests.EndToEnd --filter-class '*OperationsScenarios'` after `pwsh test/MMLib.Alvo.Admin.Tests.EndToEnd/bin/Debug/net10.0/playwright.ps1 install chromium` as the script does).
Expected: FAIL — `/operations` draws "There is no screen at this address".

- [ ] **Step 3: The navigation entry**

`src/MMLib.Alvo.Admin/Internal/AdminPaths.cs`, after `Integrations`:

```csharp
    /// <summary>Deliveries of the host's handlers: history, pause, redeliver, replay, fixtures.</summary>
    public const string Operations = $"{AlvoAdmin.BasePath}/operations";
```

`src/MMLib.Alvo.Admin/Components/DesignSystem/Icons.cs`, after `Integrations`:

```csharp
    /// <summary>Operations: a pulse line.</summary>
    public const string Operations = """<path d="M2 10h4l2-5 4 10 2-5h4"/>""";
```

`src/MMLib.Alvo.Admin/Components/Shell/AdminSection.cs`, in `Live`, after the *Integrations* entry (outside the five the bar shows):

```csharp
        new("Operations", AdminPaths.Operations, Icons.Operations, 'p'),
```

- [ ] **Step 4: The page**

Create `src/MMLib.Alvo.Admin/Components/Operations/Operations.razor`:

```razor
@page "/admin/operations"
@attribute [Microsoft.AspNetCore.Authorization.Authorize]
@inject OperationsGateway Gateway
@inject ILogger<Operations> Logger

<PageTitle>Operations · Alvo</PageTitle>

<div class="a-stack">
    <PageHeader Title="Operations"
                Subtitle="What the handlers this host registers did after each commit: pause one, run a dead delivery again, replay what failed. Run counts are this instance's, over the last 24 hours.">
        <Primary>
            <AlvoButton Small="true" data-testid="ops-refresh" Busy="_loading" OnClick="_ => LoadAsync()">Refresh</AlvoButton>
        </Primary>
    </PageHeader>

    @if (_problem.Shown)
    {
        @RefusalPanel.Of(_problem)
    }

    @if (_handlers is null)
    {
        <Skeleton Size="Skeleton.SkeletonSize.Lg" />
    }
    else if (_handlers.Count == 0)
    {
        <EmptyState Title="This host registers no event handlers"
                    Body="Handlers are registered in the host's code with Entity(…).OnCreated and their kin; the standalone image registers none." />
    }
    else
    {
        <Panel Title="Handlers">
            @foreach (var handler in _handlers)
            {
                <div class="a-listrow a-listrow--stacked a-listrow--flush" data-testid="@($"ops-handler-{handler.Id}")">
                    <div class="a-row">
                        <span class="a-mono">@handler.Id</span>
                        <span class="a-badge a-badge--accent">⟨/⟩ from code</span>
                        @if (handler.Paused)
                        {
                            <span class="a-badge a-badge--warn" data-testid="@($"ops-handler-paused-{handler.Id}")">paused</span>
                        }
                    </div>
                    @if (handler.Description is { } description)
                    {
                        <span class="a-section__sub">@description</span>
                    }
                    <span class="a-section__sub" data-testid="@($"ops-runs-{handler.Id}")">@OperationsView.RunsLine(Stat(handler.Id))</span>
                    <div class="a-row a-actions">
                        @if (handler.Paused)
                        {
                            <AlvoButton Small="true" data-testid="@($"ops-resume-{handler.Id}")" Busy="_busy" OnClick="_ => ResumeAsync(handler.Id)">Resume</AlvoButton>
                        }
                        else
                        {
                            <AlvoButton Small="true" data-testid="@($"ops-pause-{handler.Id}")" Busy="_busy" OnClick="_ => AskPause(handler.Id)">Pause…</AlvoButton>
                        }
                        <AlvoButton Small="true" data-testid="@($"ops-replay-{handler.Id}")" Busy="_busy" OnClick="_ => PlanReplayAsync(handler.Id)">Replay dead…</AlvoButton>
                    </div>
                </div>
            }
        </Panel>

        <div class="a-split">
            <Panel class="a-split__main a-grow" Title="Deliveries">
                <div class="a-row a-wrap" role="group" aria-label="Filter by status">
                    <AlvoButton Small="true" Tone="@FilterTone(null)" data-testid="ops-status-filter-all" OnClick="_ => FilterAsync(null)">All</AlvoButton>
                    @foreach (var status in OperationsView.StatusFilters)
                    {
                        <AlvoButton Small="true" Tone="@FilterTone(status)" data-testid="@($"ops-status-filter-{status}")" OnClick="_ => FilterAsync(status)">@status</AlvoButton>
                    }
                </div>
                <div data-testid="ops-delivery-list">
                    @if (_deliveries is { Count: 0 })
                    {
                        <span class="a-section__sub">No deliveries@(_status is null ? string.Empty : $" {_status}") yet.</span>
                    }
                    @foreach (var delivery in _deliveries ?? [])
                    {
                        <div data-testid="@($"ops-row-{delivery.Id}")">
                        <ListRow class="a-listrow--stacked a-listrow--flush" OnClick="() => OpenAsync(delivery.Id)">
                            <div class="a-row">
                                <span class="@OperationsView.BadgeClass(delivery.Status)" data-testid="ops-delivery-status">@delivery.Status</span>
                                <span class="a-mono">@delivery.Subscriber</span>
                                <span class="a-section__sub a-mono">@delivery.PartitionKey</span>
                            </div>
                            <span class="a-section__sub">attempts @delivery.Attempts@(delivery.LastErrorType is { } error ? $" · {error}" : string.Empty)</span>
                        </ListRow>
                        </div>
                    }
                </div>
            </Panel>

            <div class="a-split__aside a-stack" id="ops-delivery">
                @if (_selected is null)
                {
                    <div class="a-card">
                        <span class="a-section__title">Pick a delivery</span>
                        <span class="a-section__sub">Its attempts are shown here; a finished one can be run again or taken away as a redacted fixture.</span>
                    </div>
                }
                else
                {
                    <div class="a-card a-card--roomy">
                        <div class="a-row">
                            <span class="@OperationsView.BadgeClass(_selected.Status)">@_selected.Status</span>
                            <span class="a-mono">@_selected.Subscriber</span>
                        </div>
                        <ol class="a-stack" data-testid="ops-delivery-history">
                            @foreach (var attempt in _selected.History)
                            {
                                <li class="a-section__sub">@OperationsView.AttemptLine(attempt)</li>
                            }
                        </ol>
                        <div class="a-row a-actions">
                            @if (OperationsView.CanRedeliver(_selected))
                            {
                                <AlvoButton Small="true" data-testid="ops-redeliver" Busy="_busy" OnClick="_ => RedeliverAsync(_selected.Id)">Run again</AlvoButton>
                            }
                            <AlvoButton Small="true" data-testid="ops-fixture" Busy="_busy" OnClick="_ => FixtureAsync(_selected.Id)">Fixture</AlvoButton>
                        </div>
                        @if (_fixture is { } fixture)
                        {
                            <p class="a-section__sub">
                                Every value that is not a key is redacted. Save it as JSON and pass it to
                                <code class="a-mono">AlvoTestHost.DeliverAsync</code> to reproduce this delivery.
                            </p>
                            <div data-testid="ops-fixture-json"><CodeBlock Json="@fixture" /></div>
                        }
                    </div>
                }
            </div>
        </div>
    }
</div>

<AlvoConfirm Open="_confirming is not null" TestId="ops-confirm" ConfirmTestId="ops-confirm-run" CancelTestId="ops-confirm-cancel"
             Title="@(_confirming?.Title ?? string.Empty)" Consequence="@(_confirming?.Consequence ?? string.Empty)"
             Verb="@(_confirming?.Verb ?? string.Empty)" Busy="_busy" OnConfirm="ConfirmedAsync" OnCancel="() => _confirming = null">
    <MudTextField T="string" @bind-Value="_reason" Label="Reason (kept in the audit)" MaxLength="500" data-testid="ops-reason" />
</AlvoConfirm>
```

Create its code-behind `src/MMLib.Alvo.Admin/Components/Operations/Operations.razor.cs`:

```csharp
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Components.Operations;

/// <summary>The Operations page: handlers with their run counts and their pause, the delivery list, one delivery's attempts.</summary>
public partial class Operations
{
    private readonly RefusalState<AdminProblem> _problem = new();
    private IReadOnlyList<ManagementExtension>? _handlers;
    private ManagementExtensionStats? _stats;
    private IReadOnlyList<ManagementDelivery>? _deliveries;
    private ManagementDelivery? _selected;
    private string? _fixture;
    private string? _status;
    private string? _reason;
    private Question? _confirming;
    private bool _loading;
    private bool _busy;

    protected override Task OnInitializedAsync() => LoadAsync();

    private ManagementExtensionStat? Stat(string id) => _stats?.Items.FirstOrDefault(item => item.Id == id);

    private AlvoButton.ButtonTone FilterTone(string? status) =>
        status == _status ? AlvoButton.ButtonTone.Primary : AlvoButton.ButtonTone.Secondary;

    private async Task LoadAsync()
    {
        _loading = true;
        await GuardedAsync(async () =>
        {
            _handlers = await Gateway.HandlersAsync(default);
            _stats = await Gateway.StatsAsync(default);
            _deliveries = (await Gateway.DeliveriesAsync(_status, default)).Items;
            _selected = _selected is null ? null : await Gateway.DeliveryAsync(_selected.Id, default);
        });
        _loading = false;
    }

    private Task FilterAsync(string? status)
    {
        _status = status;
        return LoadAsync();
    }

    private Task OpenAsync(Guid delivery) => GuardedAsync(async () =>
    {
        _fixture = null;
        _selected = await Gateway.DeliveryAsync(delivery, default);
    });

    private void AskPause(string subscriber)
    {
        _reason = null;
        _confirming = new Question(
            $"Pause {subscriber}?",
            "Its deliveries keep being recorded and wait. Nothing new runs on any instance until you resume it; then they run in order.",
            "Pause",
            () => Gateway.SetPausedAsync(subscriber, paused: true, _reason, default));
    }

    private Task ResumeAsync(string subscriber) => GuardedAsync(async () =>
    {
        await Gateway.SetPausedAsync(subscriber, paused: false, reason: null, default);
        await LoadAsync();
    });

    private Task PlanReplayAsync(string subscriber) => GuardedAsync(async () =>
    {
        var plan = await Gateway.PlanReplayAsync(subscriber, default);
        var key = Guid.NewGuid().ToString("N");
        _reason = null;
        _confirming = plan.Count == 0
            ? null
            : new Question(
                OperationsView.ReplayQuestion(plan, subscriber),
                "They run again in the order of their events, after newer events of the same records that already ran. Their conditions are not evaluated again.",
                $"Replay {plan.Count}",
                () => Gateway.ReplayAsync(subscriber, _reason, key, default));
        if (plan.Count == 0)
        {
            _problem.Show(AdminProblem.From(new ManagementConflictException($"{subscriber} has no dead deliveries to run again.")), fromPress: true);
        }
    });

    private Task RedeliverAsync(Guid delivery) => GuardedAsync(async () =>
    {
        _selected = await Gateway.RedeliverAsync(delivery, reason: null, default);
        await LoadAsync();
    });

    private Task FixtureAsync(Guid delivery) => GuardedAsync(async () =>
        _fixture = OperationsView.FixtureJson(await Gateway.FixtureAsync(delivery, default)));

    private async Task ConfirmedAsync()
    {
        if (_confirming is not { } question)
        {
            return;
        }

        await GuardedAsync(async () =>
        {
            await question.Run();
            _confirming = null;
            await LoadAsync();
        });
    }

    private async Task GuardedAsync(Func<Task> step)
    {
        _busy = true;
        try
        {
            _problem.Clear();
            await step();
        }
        catch (Exception exception) when (AdminProblem.From(exception, Logger) is { } problem)
        {
            _confirming = null;
            _problem.Show(problem, fromPress: true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>A change waiting for the operator's yes.</summary>
    private sealed record Question(string Title, string Consequence, string Verb, Func<Task> Run);
}
```

(The page follows *History*'s shapes: a refusal is drawn above the lists, which stay where they were; `CodeBlock` takes the fixture's JSON text; a replay's key is minted when the dry run is drawn, so a second press of the confirm replays the first answer instead of running twice. Add `using MudBlazor;` if `_Imports.razor` does not already bring it.)

- [ ] **Step 5: Run the scenario and the Admin suite**

Run: `scripts/test-admin-e2e --filter-class '*OperationsScenarios'`, then `dotnet test --project test/MMLib.Alvo.Admin.Tests` (the public-API baseline grows by the routable `Operations` component — accept it and dispatch `alvo-snapshot-judge`; `GotoShortcutTests` covers the new go key; `EndToEndSelectorTests` checks the new test ids exist in markup).
Expected: PASS.

- [ ] **Step 6: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Admin/Components/Operations/Operations.razor.cs src/MMLib.Alvo.Admin/Internal/AdminPaths.cs src/MMLib.Alvo.Admin/Components/DesignSystem/Icons.cs src/MMLib.Alvo.Admin/Components/Shell/AdminSection.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsScenarios.cs
scripts/test-ring1
git add src/MMLib.Alvo.Admin/Components/Operations/Operations.razor src/MMLib.Alvo.Admin/Components/Operations/Operations.razor.cs src/MMLib.Alvo.Admin/Internal/AdminPaths.cs src/MMLib.Alvo.Admin/Components/DesignSystem/Icons.cs src/MMLib.Alvo.Admin/Components/Shell/AdminSection.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsWorld.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/OperationsScenarios.cs test/MMLib.Alvo.Admin.Tests/PublicApi.MMLib.Alvo.Admin.verified.txt
git commit -m "feat(admin): an Operations page to pause, redeliver and replay handler deliveries

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 18: Docs — *Operate deliveries*, architecture docs, CHANGELOG, PLAN, spec amendment **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/operate-deliveries.mdx`; `website/src/snippets/extending/operations/RepairDeskHandlers.cs`, `RepairDeskOperationsTests.cs`, `repair-desk.alvo.json`
- Modify: `website/astro.config.mjs`, `website/src/content/docs/extending/index.mdx`, `extending/event-handlers.mdx`, `extending/extension-catalog.mdx`, `extending/testing.mdx` (sidebar order 7)
- Modify: `docs/architecture/events.md`, `docs/architecture/management-api.md`, `CHANGELOG.md`, `docs/PLAN.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the page, run by `RepairDeskOperationsTests` in `MMLib.Alvo.DocsSnippets.Tests` (ring0).

- [ ] **Step 1: Write the snippets and their facts**

`website/src/snippets/extending/operations/repair-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "repair-desk",
  "auth": { "roles": ["mechanic", "lead"] },
  "access": {
    "viewer": "'mechanic' in @user.roles",
    "developer": "'lead' in @user.roles"
  },
  "entities": {
    "repairs": {
      "fields": {
        "bike": { "type": "string", "required": true, "maxLength": 20 },
        "part": { "type": "string", "required": true, "maxLength": 80 }
      },
      "rules": {
        "list": "'mechanic' in @user.roles || 'admin' in @user.roles",
        "get": "'mechanic' in @user.roles || 'admin' in @user.roles",
        "create": "'mechanic' in @user.roles"
      }
    }
  }
}
```

`website/src/snippets/extending/operations/RepairDeskHandlers.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;

namespace RepairDesk;

public static class RepairDeskHandlers
{
    public static IAlvoBuilder AddRepairDesk(this IAlvoBuilder alvo) =>
        alvo.Entity("repairs", repairs => repairs.OnCreated<OrderParts>(
            "order-parts",
            configure: options =>
            {
                options.Description = "Orders the part a new repair needs.";
                options.Retry = AlvoRetryPolicy.From(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
            }));
}

public interface IPartsSupplier
{
    Task OrderAsync(string part, CancellationToken cancellationToken);
}

public sealed class OrderParts(IPartsSupplier supplier) : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
{
    public Task HandleAsync(AlvoCreated<AlvoRecord> created, AlvoEventContext context, CancellationToken cancellationToken) =>
        supplier.OrderAsync((string)created.New["part"]!, cancellationToken);
}
```

`website/src/snippets/extending/operations/RepairDeskOperationsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace RepairDesk.Tests;

public sealed class RepairDeskOperationsTests
{
    private const string Project = "repair-desk";

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "repair-desk.alvo.json");

    [Fact]
    public async Task A_dead_delivery_is_reproduced_from_its_fixture_and_redelivered_once_fixed()
    {
        var ct = TestContext.Current.CancellationToken;
        var supplier = new Supplier { Down = true };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        await using var alvo = await StartAsync(supplier, clock, ct);
        await alvo.Data.CreateAsync("repairs", new Dictionary<string, object?> { ["bike"] = "BK-7", ["part"] = "brake pads" }, AlvoTestHost.Caller("mechanic"), cancellationToken: ct);
        foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(36) })
        {
            clock.Advance(wait);
            await alvo.DrainEventsAsync(ct);
        }

        var operations = AsLead(alvo);
        var dead = (await operations.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Status = "dead" }, ct)).Items.ShouldHaveSingleItem();
        dead.LastErrorType.ShouldBe("System.Net.Http.HttpRequestException");

        var fixture = await operations.GetFixtureAsync(Project, dead.Id, ct);
        fixture.Envelope.GetProperty("data").GetProperty("record").GetProperty("part").GetString().ShouldBe("<redacted:part>");
        await Should.ThrowAsync<HttpRequestException>(() => alvo.DeliverAsync(fixture, ct));

        supplier.Down = false;
        await operations.RedeliverAsync(Project, dead.Id, "supplier API is back", ct);
        await alvo.DrainEventsAsync(ct);
        supplier.Ordered.ShouldBe(["brake pads"]);
    }

    [Fact]
    public async Task A_paused_handler_catches_up_in_order_once_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var supplier = new Supplier();
        await using var alvo = await StartAsync(supplier, new FakeTimeProvider(), ct);
        var operations = AsLead(alvo);

        await operations.SetPausedAsync(Project, "order-parts", paused: true, "supplier maintenance window", ct);
        foreach (var part in new[] { "chain", "brake pads" })
        {
            await alvo.Data.CreateAsync("repairs", new Dictionary<string, object?> { ["bike"] = "BK-7", ["part"] = part }, AlvoTestHost.Caller("mechanic"), cancellationToken: ct);
        }

        await alvo.DrainEventsAsync(ct);
        supplier.Ordered.ShouldBeEmpty();

        await operations.SetPausedAsync(Project, "order-parts", paused: false, reason: null, ct);
        await alvo.DrainEventsAsync(ct);
        supplier.Ordered.ShouldBe(["chain", "brake pads"]);
    }

    private static Task<AlvoTestHost> StartAsync(Supplier supplier, TimeProvider clock, CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddRepairDesk();
            builder.Services.AddSingleton<IPartsSupplier>(supplier);
        }, clock, cancellationToken: ct);

    private static IAlvoOperations AsLead(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("lead"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "docs",
        };
        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    private sealed class Supplier : IPartsSupplier
    {
        private readonly List<string> _ordered = [];

        public bool Down { get; set; }

        public IReadOnlyList<string> Ordered => _ordered;

        public Task OrderAsync(string part, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("The parts supplier did not answer.");
            }

            _ordered.Add(part);
            return Task.CompletedTask;
        }
    }
}
```

(The waits are 6 s and 36 s, not 5 s and 30 s: every retry wait carries ±20 % jitter, X5.)

Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests`
Expected: PASS (`SnippetCoverageTests` finds `RepairDeskHandlers` and `RepairDeskOperationsTests`). A `ManagementForbiddenException` means the `access` block did not admit `lead` — keep the descriptor honest, not the test.

- [ ] **Step 2: Write the page**

`website/src/content/docs/extending/operate-deliveries.mdx` — frontmatter `title: Operate deliveries`, `description: See what your handlers did after each commit, pause one, run a dead delivery again, replay what failed, and take a delivery home as a redacted fixture to reproduce it in a test.`, `sidebar:` / `order: 6`. Imports as `extension-catalog.mdx` does (`Code`, `SourceExcerpt`, the three snippets as `descriptor`, `handlers`, `tests`). Excerpt markers carry no quotes. Sections, each a few sentences:

1. *A delivery, and where it can end* — one row per (event, handler); statuses `pending`, `running`, `succeeded`, `retrying`, `dead`, `cancelled`; a dead delivery holds its record's later deliveries for that handler back (unless `Ordering = None`). `<Code code={descriptor} lang="json" title="repair-desk.alvo.json" />` and `<SourceExcerpt code={handlers} from="public static IAlvoBuilder AddRepairDesk" to="Orders the part a new repair needs." title="RepairDeskHandlers.cs" />`.
2. *Who may do what* — the table: list, read one, stats → `viewer`; redeliver, replay, pause, fixture, run a schedule → `developer`; switch an extension off → `admin`; the levels come from the descriptor's `access` block, as for every management route. A viewer sees ids, statuses, tenants and error types — never a payload.
3. *Look* — `GET {m}/projects/{p}/deliveries?subscriber=&status=&from=&to=&after=&limit=` (newest first; `next` is the cursor; `from`/`to` are event time, the end excluded), `GET …/deliveries/{id}` with its attempts (numbered across the delivery's whole life), and the same through `IAlvoOperations` in-process. `GET …/extensions/stats`: runs, failures, p50/p95 over the last 24 hours, **for this instance only** (`scope: "this-instance"`): another replica's runs are not in it, and a restart starts the day over (`since`).
4. *Run a dead delivery again* — `POST …/deliveries/{id}/redeliver` `{reason}`: back to pending with a fresh attempt budget; its history is kept; its lane drains after it. A delivery still pending or retrying is answered as it is; a running one is 409; one whose handler this host no longer registers is 409 (retire it with `AddRetiredExtension` instead). `<SourceExcerpt code={tests} from="supplier.Down = false;" to="supplier.Ordered.ShouldBe" title="RepairDeskOperationsTests.cs" />`. Replace the old by-hand SQL: it is no longer needed.
5. *Replay a window* — `POST …/deliveries/replay` `{subscriber, status, from, to, dryRun, reason}`: always ask with `"dryRun": true` first — it answers the exact count and the first 20 ids and changes nothing; the real run resets at most 10 000; send `Idempotency-Key` so a retried request answers the first one's count (`"replayed": true`) instead of running them twice (the same key with a different body is 409 `idempotency-conflict`; a key on a dry run is refused). Replayed deliveries run after newer events of the same records that already ran, and **their conditions are not evaluated again** — a delivery recorded `unbound` was recorded without its condition, so a replay of `unbound` rows can run the handler for records its condition would not have matched; check `when` in the handler body if that matters.
6. *Pause and resume* — `PUT …/subscribers/{id}/paused` `{paused, reason}`: on every instance, until resumed — it survives a restart; new deliveries are still recorded and wait; a delivery already running finishes; after resume they run in order. The catalog and the dashboard show `paused`. `<SourceExcerpt code={tests} from="public async Task A_paused_handler_catches_up_in_order_once_resumed" to="supplier.Ordered.ShouldBe([\"chain\"" title="RepairDeskOperationsTests.cs" />` (if the excerpt component cannot take the escaped quote, end the excerpt at `await operations.SetPausedAsync(Project, "order-parts", paused: false`).
7. *Reproduce a delivery locally* — `GET …/deliveries/{id}/fixture` (developer): the envelope with every value that is not a key replaced by `<redacted:FIELD>` (kept: `id`, `tenant_id`, `ref` and `enum` fields, empty values, the envelope's attributes and the changed-field names; an application event keeps only `id`); save it, fill in what the failure depends on, and run it with `AlvoTestHost.DeliverAsync(fixture)`: the handler runs once as a delivery would — its scope, its identity, its timeout — and its exception reaches your test as it was thrown. `<SourceExcerpt code={tests} from="var fixture = await operations.GetFixtureAsync" to="alvo.DeliverAsync(fixture, ct)" title="RepairDeskOperationsTests.cs" />`.
8. *Audit* — every pause, resume, redeliver and replay that changed something writes one row to `<prefix>_operations_log` (who, when, what, on what, why, how many) and one `ManagementAudit` log line at Warning; one that changed nothing writes neither. The log is not pruned yet.
9. *On the dashboard* — the **Operations** page (`g p`): each handler with its runs on this instance, Pause/Resume, Replay dead (the dry run's count first); the delivery list by status; a delivery's attempts, **Run again** and **Fixture**.
10. *Switching code off, and schedules* — `PUT …/extensions/{id}/enabled` exists for validate hooks, which arrive with write hooks; for an event handler it answers 409 naming the pause route. `POST …/schedules/{id}/run` answers 404 until schedules exist.

`website/astro.config.mjs`: `page('Operate deliveries', 'extending/operate-deliveries'),` after the *See your extensions in the dashboard* entry. `testing.mdx` frontmatter `order: 7`. `extending/index.mdx`: a bullet `- [Operate deliveries](/extending/operate-deliveries/) — history, pause, redeliver, replay and fixtures for your handlers.` after the extension-catalog bullet. `event-handlers.mdx`: the bullet *A dead delivery blocks its record until it is cleared* becomes "A dead delivery blocks its record until it is redelivered, replayed or cancelled — [operate deliveries](/extending/operate-deliveries/)."; the *A typed message that cannot be built* bullet links "redeliver it" to `/extending/operate-deliveries/#run-a-dead-delivery-again`; delete the section *Retry a dead delivery by hand*; *Not yet* is deleted (nothing listed there is missing any more). `extension-catalog.mdx`: *What is listed* gains "`paused` — whether an operator paused the handler ([operate deliveries](/extending/operate-deliveries/#pause-and-resume))"; its *Not yet* drops "Delivery history, pause and replay arrive with the operations page."

- [ ] **Step 3: Architecture docs, CHANGELOG, PLAN**

`docs/architecture/events.md`, inside *Event handlers and deliveries*, a section **Operating deliveries (HX-06)**: the store's operator members and their transaction shape (the change and its log row together, write-first; a no-op writes nothing); redeliver/replay reset `attempts` to zero and the history keeps numbering (`COALESCE(MAX(attempt), 0) + 1`), replacing the by-hand recipe; the claim's pause term and why it lives in the claim (every replica, survives restart, one statement); `unbound` and `when-failed` rows replay without their condition; stats from a process-wide `MeterListener`; `DeliverAsync` (manual mode only). Point at *Operate deliveries*.

`docs/architecture/management-api.md`: add the nine rows to the table in *The surface* (path, member, level), and a section **Operations** after *Extensions*: the second port and why it is separate from `IAlvoManagement` (descriptor-shaped vs. runtime state; one gate, one level table); the replay's `Idempotency-Key` and where its record lives (the operations log row, in the reset's transaction — unlike the apply's post-commit record, *The post-commit window, stated plainly*); the 409s; `RunScheduleAsync`/`SetEnabledAsync` shipped refusing.

`CHANGELOG.md`, `## [Unreleased]` → `### Added`, first entry: **Delivery operations** (HX-06, #383). `IAlvoOperations` and nine Management routes: list deliveries, read one with its attempts, redeliver, replay (dry run, `Idempotency-Key`), redacted fixtures, pause/resume a handler, per-instance extension stats (`scope: "this-instance"`), and the enable and run-schedule routes (refusing until validate hooks and schedules exist); `ManagementExtension.Paused`; `IAlvoEventPump.DeliverAsync` and `AlvoTestHost.DeliverAsync`; the dashboard's **Operations** page; every change audited in `alvo_operations_log` and a `ManagementAudit` log event. Docs: *Operate deliveries*. **Breaking for delivery-store implementers:** `IDeliveryStore` gains nine abstract members and `ClaimAsync` must skip paused subscribers; an attempt's number in the history now counts across redeliveries. Upgrade: the two tables are created on start.

`docs/PLAN.md`, *Host extensibility* line: "…; HX-05 (typed layer and lambda translation) the fifth; HX-06 (delivery operations) the sixth."

- [ ] **Step 4: Spec amendment**

In `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`, after *HX-03 settled details* in §4.8 (before `### 4.9`), add **HX-06 settled details.** — "Rulings and deviations taken while planning (1–25, verbatim from `docs/superpowers/plans/2026-10-24-hx-06-delivery-operations.md`), so a later reader can tell a decision from an oversight. Where a ruling and §4.8's *Operations* table disagree, the ruling is what shipped." — followed by this plan's *Rulings and deviations* (1)–(25), numbered, verbatim. In §5, append to the `06` row: "`ManagementDeliveryAttempt`, `ManagementDeliveryPage`, `ManagementExtensionStat`, `ManagementDeliveryNotFoundException`, `ManagementExtensionNotFoundException`, `ManagementConflictException`, `ManagementExtension.Paused`, `IAlvoEventPump.DeliverAsync`, and nine `IDeliveryStore` members with their records added (§4.8 *HX-06 settled details* (1), (11), (12))." In §4.8 *HX-03 settled details* (29), append: "— **Superseded by HX-06:** redeliver (§4.8 *HX-06 settled details* (2))."

- [ ] **Step 5: Build the site, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/operations/RepairDeskHandlers.cs website/src/snippets/extending/operations/RepairDeskOperationsTests.cs
scripts/test-ring1
(cd website && npm ci && npm run build)
git add website/src/content/docs/extending/operate-deliveries.mdx website/src/snippets/extending/operations website/astro.config.mjs website/src/content/docs/extending/index.mdx website/src/content/docs/extending/event-handlers.mdx website/src/content/docs/extending/extension-catalog.mdx website/src/content/docs/extending/testing.mdx docs/architecture/events.md docs/architecture/management-api.md CHANGELOG.md docs/PLAN.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: operate deliveries

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

Expected: the site builds with link validation green (the new public types appear in the generated C# reference; the anchors `#run-a-dead-delivery-again` and `#pause-and-resume` resolve).

---

## Task 19: Rings, reviews, plan-guard, PR report, PR

- [ ] **Step 1: CI-parity Release build, rings and the out-of-ring suites**

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet build MMLib.Alvo.slnx -c Release -p:HUSKY=0 2>&1 | grep -E "error [A-Z]+[0-9]+" | sort -u'
git clean -fdx -e .superpowers
```

Expected: no output (any `error CAxxxx`/`IDExxxx`/`RCSxxxx` is fixed in a `fix(…): …` commit and this step re-run — watch CA1822 on members that read no instance state, CA1031 on the runner's rethrowing catch, CA2007 in the snippets). Then `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (both engines: `PostgreSqlDeliveryStoreTests` with the operator rows, `PostgreSqlDeliveryOperationsTests`), `scripts/test-admin-e2e` (whole), `(cd website && npm run build)`. Expected: all green. The OpenAPI document, the Vacuum ruleset and the TeaPie path set do not move (management routes are excluded from the document). The assistant eval is not run: no skill, tool or instruction changed.

- [ ] **Step 2: Reviews (substitutes) and the security-core checklist**

Freeze the tree. `/code-review` and `/security-review` are user-only: dispatch two reviewer subagents over `git diff hx/05-typed-layer...HEAD`, labelled "substitute for /code-review high" and "substitute for /security-review". Point the code reviewer at: every operator statement being identical on both engines and opening its transaction with a write; the attempt numbering (`MAX + 1`) and the reset's `attempts = 0` against `DeliveryRunner.Admit`'s ceiling; the claim's pause term in both halves of the EvalPlanQual-safe statement; the fake store matching the EF store row for row; `ReplayAsync`'s order (key replay before the count, the 10 000 ceiling, the `null` path); the no-op paths writing neither row nor audit; `EventPump.InRoundAsync` keeping the re-entrancy and stop semantics `DrainAsync` had. Point the security reviewer at: the nine rows in `ManagementOperations` and every route's `Gate` (the sweep in `ManagementAccessTests` reaches all nine); `ManagementGate.Admit`'s order (level before project); the fixture's redaction (no value but keys, `DeliveryFixturesTests`) and its Developer level; the reason's bounds against log injection; `IdempotencyKey` scoping by `AlvoIdempotency.IdentityOf`; `DeliverAsync` refused outside the manual mode (a fixture is a forged event); the viewer's list disclosing no payload. Then run the `alvo-security-core-review` checklist — §6.2 does not list HX-06, and the plan runs it anyway for its authorization and disclosure items (Ruling 20) — and record each item's evidence for the PR report. Fix findings, re-run ring0, one `fix(…): …` commit per finding.

- [ ] **Step 3: `alvo-plan-guard`**

Dispatch `alvo-plan-guard` last (read-only); act on drift it reports. The PLAN marker stays where it is.

- [ ] **Step 4: PR report**

Use the `alvo-pr-report` skill (it dispatches `alvo-pr-reporter` and publishes the 8-section page). Evidence: acceptance (1)–(10) → tests (table below), the five public-API diffs against the public-surface list, the both-engine runs of the contract rows and `DeliveryOperationsFacts`, the admin e2e run, the Release build, the security-core checklist and both substitute reviews, and the rulings — calling out Ruling 1 (the store port grows), 2 (attempt numbering), 4 (the log carries the replay's key), 8 (conditions not re-evaluated) and 10 (two members that only refuse until HX-09/HX-13).

- [ ] **Step 5: Push and open the PR**

```bash
git branch --show-current   # hx/06-delivery-operations
git push -u origin hx/06-delivery-operations
```

If #416 (HX-05) is merged: `git fetch origin && git merge origin/main` first (merge-commit style), re-run ring0, push, then `gh pr create --base main`. If #416 is still open: `gh pr create --base main --draft`, the body's first line "Stacked on #416 — review `hx/05-typed-layer...hx/06-delivery-operations`; ready once #416 merges." The body is the skill's five-line pointer, carries `Closes #383` once, and ends with `https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Then `gh pr edit --add-label needs-deep-review` (Ruling 20). After the merge, read #383 back (`gh issue view 383`) to confirm it — and only it — was closed, comment on #33 that HX-06 delivered its DLQ and redelivery UI, and on #154 that retention must now cover `alvo_operations_log` and `alvo_subscriber_controls` is not to be pruned.

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 18 Step 4) so a later reader can tell a decision from an oversight.

1. **The delivery port grows instead of a second store port.** §5 lists no store additions for HX-06, but operations need storage a provider implements: `IDeliveryStore` gains nine **abstract** members (`ListAsync`, `FindAsync`, `AttemptsAsync`, `SelectAsync`, `RedeliverAsync`, `ReplayAsync`, `SetPausedAsync`, `ControlsAsync`, `FindOperationAsync`) and six `required init` records, and `ClaimAsync`'s contract gains "never a paused subscriber". One port because the claim must read the pause control in the same statement and the log row must commit with the change — one database, one port (the port's own *One database with the outbox* rule). Abstract, not default members, for `HoldBackAsync`'s reason (HX-03 (20), (33)): a default that did nothing would leave a dead lane blocked forever on a third-party store. No release tag carries `IDeliveryStore`, so the break reaches no published consumer; the CHANGELOG names it for anyone building from `main` or `:edge`. Held by eleven new rows of `DeliveryStoreContractTests`, on the EF store (both engines) and the core's fake.
2. **A redelivered or replayed delivery restarts its attempt budget; its history keeps numbering.** The reset sets `attempts = 0` — what the retry policy, the backoff and `DeliveryRunner.Admit`'s ceiling read — so a delivery that died at its last attempt runs again instead of being reclaimed dead `lease-expired` at once; the history numbers an attempt `COALESCE(MAX(attempt), 0) + 1` per delivery, so it neither collides with the `(delivery_id, attempt)` key nor loses earlier attempts. The handler sees `AlvoEventContext.Attempt` start again at 1. Prior art: Hangfire's and MassTransit's requeue reset the retry count and keep the history. Supersedes HX-03 (29)'s by-hand recipe (which deleted the history).
3. **The pause lives in the claim.** A `NOT EXISTS` over `alvo_subscriber_controls` in both halves of the claim, not a filter of the dispatcher's subscriber lists: it holds on every replica and across restarts, with no read-then-claim window (spec §4.8 *Claim*, "no pause control"). Fan-out is untouched, so a paused handler's deliveries accumulate; a delivery already running finishes; a drain over a paused handler is quiet, not a loop.
4. **The operations log carries the replay's idempotency record.** E11 names six columns; the log adds `affected`, `sample`, `idempotency_scope`, `idempotency_key`, `request_fingerprint`, and a partial unique index on the key. The record is written in the reset's own transaction — the log insert is the transaction's first write and `ON CONFLICT DO NOTHING` makes a concurrent duplicate a no-op — so a crash cannot leave a reset without its record (the window `IManagementIdempotencyStore`'s remarks state for the apply). `IManagementIdempotencyStore` is not reused: it records a revision number, and a replay's answer is a count and ids.
5. **A change that changed nothing writes nothing.** Pausing a paused handler, resuming a running one and redelivering a delivery already pending or retrying answer the current state with no log row and no audit line — idempotent operations (§0 *agent-first*); acceptance (7) is read as "each change". A replay always writes its row, even for a count of zero: the row is its key's record.
6. **`Idempotency-Key` follows the house rules, which differ from the IETF draft in one answer.** Optional (as on apply and rollback); read from one header line (two lines are 422); refused on a dry run; scoped by `AlvoIdempotency.IdentityOf(caller)`; the fingerprint is the operation, the project, the subscriber, the status and the window — not the reason, so the same replay retried with a reworded note is the same request. A retry answers the stored count and ids with `replayed: true` (the draft's "return the stored response"). The same key with a different request is 409 `idempotency-conflict`, the slug the data path and the apply already send, where the draft suggests 422.
7. **What a replay selects.** One registered handler (required), one finished status (`dead` unless set; `succeeded` and `cancelled` allowed; an unfinished status is 422), and a half-open window over **event time**, mapped onto event ids through an internal `AlvoEventId.Floor` (UUIDv7 ids sort by time; the stores already compare them for lane order). The sample is the first 20 ids in event order — the order they run. A real run resets at most 10 000 deliveries, checked by a count before the reset; a concurrent fan-out can push one replay slightly past it, which is stated rather than locked against.
8. **Conditions are not evaluated again.** A redelivered or replayed delivery runs its handler; `when` was decided at fan-out (X6). A delivery recorded `when-failed` or `unbound` was recorded without a decided condition, so replaying it runs the handler even for a record the condition would not match — HX-03 (6)'s stated trade-off, now in the operator's hands: the docs and the dashboard's replay confirm say so. Re-evaluating at claim time would change every delivery's semantics and is left to a measured need.
9. **Redeliver's guards.** Running → 409 (the fence would make a reset racy); a handler this host does not register → 409 (a pending row for an unknown id refuses the next start, E3); pending or retrying → answered unchanged. A handler registered but not bound to the current schema may be redelivered: the delivery waits until it binds.
10. **`IAlvoOperations` is complete now, including two members that only refuse.** §4.8 defines the port complete in HX-06: `SetEnabledAsync` answers 404 or 409 for every kind that exists until validate hooks (HX-09), and `RunScheduleAsync` 404 until schedules (HX-13); it returns the `ManagementDelivery` that will run the schedule. Unlike HX-04 (1), which defers *init members* nothing fills, an *interface member* added later breaks every implementer and every test double of the port (the dashboard's), so the spec's "complete" stands.
11. **`ManagementExtension.Paused` ships; `Enabled` waits.** HX-04 (1) appends `Enabled`/`Paused` "by the PR that first fills them": `Paused` is filled here (the catalog reads the controls); `Enabled` can be false for no kind before HX-09, so it waits for it.
12. **Public types §5 does not list.** `ManagementDeliveryAttempt` (the detail's history), `ManagementDeliveryPage` (a keyset page needs its cursor), `ManagementExtensionStat` (the stats envelope's items, the house's envelope-with-items shape), three exceptions (the HTTP layer maps only types: 404 delivery, 404 extension, 409 conflict), the store records of Ruling 1, `IDeliveryStoreWorld.OperationsAsync` and the contract rows (Testing), and `IAlvoEventPump.DeliverAsync` — the TestHost holds no `InternalsVisibleTo` (HX-02, HX-05 (12)), so its `DeliverAsync` needs a public core seam, and the pump is the manual-mode surface it already drives.
13. **Statuses and operations are wire strings**, not public enums — as HX-04 (4)'s `runAs`/`ordering`: the values are the store's own words, append-safe for a tolerant client.
14. **What a fixture keeps.** The record images keep `id`, `tenant_id`, `ref` and `enum` fields and empty values; every other value — `uuid` and `json` included — becomes the string `<redacted:FIELD>`, also in a numeric or boolean field (the developer fills in what a reproduction needs; a typed handler names the field it cannot read). An application event, or an entity the current schema no longer declares, keeps only `id`. The envelope's attributes stay — ids, type, time, tenant, `authid`, the role names in `authclaims` — because they identify rather than describe, and a fixture must run as the event's tenant and actor. `Redacted` lists the JSON pointers. Developer level: it discloses a payload's shape.
15. **`DeliverAsync` runs as a delivery and records nothing.** Manual mode only (a fixture is an event nobody wrote); the handler's own scope, its `RunAs`, one hop deeper in the chain, its timeout and one delivery span, so `Hooks.AssertRan` counts it; its exception propagates unwrapped; a handler the host does not bind, or an unreadable envelope, is `InvalidOperationException`.
16. **Stats are this process's, from a `MeterListener`.** On `alvo.delivery.duration` (one measurement per answered attempt, with subscriber and outcome): runs, failures (`failed`, `dead`), and p50/p95 from 128 quarter-octave bins (within 19 %), in 24 hourly buckets. Alvo's meters are process-wide, so `this-instance` means this process — two hosts in one test process see each other's runs, another replica's are never seen; `since` is the later of a day ago and the process start. Every registered extension is listed, zeros included; `rejections` is 0 until write hooks (HX-09) record them.
17. **The audit line.** A source-generated `ManagementAudit` event at Warning (event name = the method's), written after the change commits, carrying actor (`<key id>:<user id>`), operation, target, count and reason — ids and the operator's own words only. A reason is at most 500 characters with no control character, refused 422 before anything is written (it is one log field). A crash between the commit and the log line loses the line, never the row.
18. **A gate class beside the management service.** `ManagementGate` holds the level check and the project check for the operations port; `AlvoManagementService` keeps its two private copies unchanged, so the surface every existing management fact pins is not edited to serve a second port. Folding them is a later touch's.
19. **A viewer sees every tenant's deliveries.** Ids, statuses, partition keys, tenant ids and error types — never a payload: the Management API is project-wide by design ("data is deliberately absent", `IAlvoManagement`'s remarks) and a delivery row holds no data. The fixture, which holds a payload's shape, is Developer.
20. **Security core by trigger, not by list.** §6.2 does not list HX-06; it adds nine authorization rows, a second gated port and a disclosure surface, and `alvo-security-core-review` triggers on authorization — so the checklist, a labelled `/security-review` substitute and `needs-deep-review` apply (Task 19).
21. **The two tables** are created by both `SystemSchemaInitializer` and `IDeliveryStore.EnsureAsync` (HX-03's shape, E10), reserved in `AlvoFrameworkTables` (an entity cannot take the name; introspection never plans a drop), with no `ALTER`; the control table carries `disabled_at`/`disabled_by` now (Ruling 11's later need) because a column added later would be one.
22. **A page is newest first by delivery id** (fan-out time), continued with `after`, 1–200 rows (50 unless set); `next` is set whenever a page is full, so the last page may be empty — the cost of not counting.
23. **The dashboard slice.** One *Operations* page (`g p`, after *Integrations*): handlers with their runs on this instance, Pause/Resume (a reason asked for on pause), Replay dead (the dry run's count in the confirm; the key minted when it is drawn, so a double press replays rather than runs twice); the delivery list by status; a delivery's attempts, **Run again** and **Fixture**. No level-aware hiding: the port refuses and the page draws the refusal, as every other page does. Schedules and timers join with HX-13; the *Lifecycle* tab's p95 and 24 h columns remain HX-09's row (HX-04 (13)).
24. **The docs retire the by-hand SQL.** *React to changes* loses *Retry a dead delivery by hand* and its *Not yet*; *Operate deliveries* carries the operator's story with executed snippets.
25. **Retention is not built.** `alvo_operations_log` grows without bound until #154; the merge comments there.

---

## Acceptance criteria → tasks

| HX-06 criterion | Task(s) | Test |
|---|---|---|
| (1) replay dry run returns the exact count and changes nothing, both engines | 2, 11, 15 | contract `A_selection_counts_exactly_samples_in_event_order_and_changes_nothing` (SQLite, PostgreSQL, fake); `AlvoOperationsReplayTests.A_replay_dry_run_counts_exactly_and_changes_nothing`; `DeliveryOperationsFacts.A_replay_dry_run_returns_the_exact_count_and_changes_nothing` (`SqliteDeliveryOperationsTests`, `PostgreSqlDeliveryOperationsTests`) |
| (2) a paused subscriber accumulates and drains in order after resume | 4, 15, 18 | contract `A_paused_subscriber_is_not_claimed_and_its_lane_drains_in_order_after_resume`; `DeliveryOperationsFacts.A_paused_handler_accumulates_and_drains_in_order_after_resume` (both engines); snippet `A_paused_handler_catches_up_in_order_once_resumed` |
| (3) redeliver of a dead delivery unblocks its lane | 1, 3, 15 | contract `Redelivering_a_dead_delivery_makes_it_the_head_of_its_lane_again`, `A_redelivered_delivery_runs_again_with_a_fresh_budget_and_keeps_its_history`; SQLite `An_attempt_after_a_reset_is_numbered_after_the_last_one`; `DeliveryOperationsFacts.Redelivering_a_dead_delivery_unblocks_its_lane` (both engines) |
| (4) Viewer gets 403 on redeliver | 6, 13 | `OperationsLevelTests`; `OperationsRouteTests.A_viewer_is_refused_a_redeliver`; `ManagementAccessTests.Every_mapped_management_route_refuses_a_caller_the_project_names_nowhere` |
| (5) a fixture contains no non-key scalar | 7, 9, 14 | `DeliveryFixturesTests` (all rows, incl. `No_original_non_key_value_survives_anywhere_in_the_fixture`); `AlvoOperationsServiceTests.A_fixture_redacts_the_delivery_s_envelope`; `AlvoTestHostDeliverTests.A_fixture_from_one_host_reproduces_the_failure_in_another` |
| (6) `DeliverAsync` reproduces a failure locally | 14, 18 | `AlvoTestHostDeliverTests.A_fixture_from_one_host_reproduces_the_failure_in_another`, `A_delivered_fixture_runs_the_handler_as_the_dispatcher_would_and_is_counted_as_a_run`; `DeliveryDispatcherTests.A_fixture_hands_back_the_handler_s_own_exception`; snippet `A_dead_delivery_is_reproduced_from_its_fixture_and_redelivered_once_fixed` |
| (7) pause, redeliver and replay each write one log row and one `ManagementAudit` event | 3, 4, 10, 11, 15 | contract `A_redeliver_writes_one_log_row`, `A_replay_resets_only_the_named_subscriber_s_finished_deliveries`, `Pausing_twice_changes_nothing_the_second_time`; `AlvoOperationsWritesTests.Redelivering_a_dead_delivery_puts_it_back_and_audits_once`, `Pausing_twice_audits_once_and_answers_paused`; `AlvoOperationsReplayTests.A_replay_resets_what_it_selects_and_audits_once`; `DeliveryOperationsFacts.Pause_redeliver_and_replay_each_write_one_log_row_and_one_audit_line` (both engines) |
| (8) stats states `scope: "this-instance"` | 8, 9, 13, 14 | `ExtensionStatsTests`; `AlvoOperationsServiceTests.Stats_list_every_registered_extension_scoped_to_this_instance`; `OperationsRouteTests.A_viewer_lists_deliveries_and_reads_the_stats`; `AlvoTestHostDeliverTests.Stats_count_the_runs_this_instance_made` |
| (9) `PUT …/extensions/{id}/enabled` on a handler → 409 | 10, 13 | `AlvoOperationsWritesTests.Switching_a_handler_off_is_a_conflict_that_names_pausing`; `OperationsRouteTests.Switching_a_handler_off_is_409_naming_pausing`, `A_developer_may_not_switch_code_off` |
| (10) admin e2e: pause → write → resume → delivered | 16, 17 | `OperationsScenarios.A_paused_handler_holds_its_delivery_until_it_is_resumed`; `OperationsGatewayTests`, `OperationsViewTests` |
| Scope: `Idempotency-Key` | 3, 11, 13 | contract `A_replay_under_a_spent_key_changes_nothing`; `AlvoOperationsReplayTests.A_retried_replay_answers_the_first_and_resets_nothing`, `A_key_spent_on_a_different_replay_is_a_conflict`, `A_key_on_a_dry_run_is_refused`; `OperationsRouteTests.A_replay_reads_its_idempotency_key_from_the_header` |
| Scope: `alvo_subscriber_controls`, `alvo_operations_log` | 1, 3, 4, 12 | `DeliveryOperationsTableTests`, `FrameworkTableReservationTests`, `AddAlvoIntegrationTests` (two survive-introspection facts), `PausedCatalogTests` |
| Scope: routes and levels | 6, 13 | `OperationsLevelTests`, `ManagementContractTests.Every_member_of_the_management_ports_has_an_http_route`, `OperationsRouteTests` |
| Docs: *Operate deliveries*; Dashboard: Operations page | 17, 18 | `RepairDeskOperationsTests` in `MMLib.Alvo.DocsSnippets.Tests`; website build; admin e2e |
| Security (authorization and disclosure) | 19 | `alvo-security-core-review` checklist, `/security-review` substitute, `needs-deep-review` (Ruling 20) |
