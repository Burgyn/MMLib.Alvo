# HX-07 — Write unit, `TransactAsync`, savepoints — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Host code can make several writes, batches and custom events **one unit** — `IAlvoTransactions.TransactAsync(caller, (unit, ct) => …)` — that commits together or not at all on SQLite and PostgreSQL alike: every call through the unit runs as the caller under the same rules as outside, under its own savepoint (so a caught unique violation does not end the unit on PostgreSQL), a unit past `AlvoTransactionOptions.Timeout` (30 s) rolls back and lets go of the database at once, a timeout, chain-depth or misuse failure **poisons** the unit so that catching it cannot commit, any other data-port call made while the unit is open (`IAlvoData`, `IAlvoSession`, `IAlvoEvents.PublishAsync`, a nested `TransactAsync`) is refused at once with `AlvoUnitInProgressException`, nothing is ever retried, typed writes (`CreateAsync<T>`, `UpdateAsync<T>(id, IAlvoPatch<T>)`, `ReplaceAsync<T>`, `ExistsAsync<T>`) work on a unit and on a session, and three literal text operators (`AlvoFilterOperator.StartsWith/EndsWith/Contains`) let a typed query's `StartsWith(captured)` match a captured `%`, `_`, `\` or `[` literally on every engine.

**Architecture:** Abstractions gains the port (`IAlvoTransactions`, `IAlvoTransaction : IAlvoSession`, `AlvoTransactionOptions`, `AlvoUnitInProgressException`) and an internal `IAlvoFlowUnit` that `AlvoFlow` now carries beside provenance, with one refusal helper every data-port entry calls. The EF driver implements the port: an internal `WriteUnit` owns one connection, one `DbTransaction`, the write turn, a DI scope, a deadline on the host's `TimeProvider`, a one-call-at-a-time gate and the poisoned flag; every `EfAlvoData` write site moves onto one seam (`BeginAsync(db, unit)`) that either opens the top-level transaction exactly as today or joins the unit under a savepoint (`UnitSavepoint`), and internal `…InUnitAsync` entries run each call through `WriteUnit.RunAsync`. The custom-event envelope moves into Abstractions so `IAlvoTransaction.PublishAsync` writes the same envelope into the unit's transaction that `IAlvoEvents.PublishAsync` appends autocommit. The literal text operators are rendered by the shared filter renderer as `field LIKE @pattern ESCAPE @escape` with the value escaped and both bound, kept off the HTTP wire, mirrored by the reference evaluator, held by a new shipped contract suite on SQLite, PostgreSQL and the in-memory reference, and reached from typed queries by lifting HX-05's refusal.

**Tech Stack:** .NET 10 (C# 14), EF Core 10 relational (`IDbContextTransaction` savepoints, `UseTransactionAsync`, `SetDbConnection`), ADO.NET `DbTransaction.Save/Rollback/Release`, Microsoft.Data.Sqlite, Npgsql, `Microsoft.Extensions.DependencyInjection` (`AsyncServiceScope`), `Microsoft.Extensions.TimeProvider.Testing`, xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, CsCheck, Verify + PublicApiGenerator, Testcontainers (PostgreSQL leg), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §7 *HX-07* (scope, docs, dashboard "none", acceptance (1)–(11)); §4.5 (the write unit: connection + `DbTransaction` + DI scope + depth + timeout + poisoned flag; one short-lived `DbContext` per nested write enlisted with `UseTransactionAsync`, R-M1; savepoint per nested write; refuse-not-join on the flow, R-B1; unit timeout; poisoning; no retry; idempotency inside → `ArgumentException`; one DI scope per unit; self-write); §4.1 (stage map, `BEGIN (write unit)` is HX-07's; "top-level" = no unit on the flow); §4.3 (literal operators row, R-M8, N-7) and *HX-05 settled details* (6), (7), (20), (25); §4.4.4–§4.4.6 (budgets and poisoning, the boot ban list naming `IAlvoTransactions`, `AsSystem()`, `SuppressHostHooks`); §4.6 (provenance, `AlvoFlow` carries the open unit, R-M2 "who adds +1", R-M3 enforcement sites); §4.10 (`IAlvoScheduler` the one joining exception — HX-13); §2.3 X7, X13; §5 rows `07`; §6.1 *nested write failure in a unit* parity row; §6.2 (HX-07 is security core); Appendix A B3, M8, M24, R-B1, R-M1, R-M8, R-13. Prior art read: PostgreSQL *Transactions* and `SAVEPOINT`/`ROLLBACK TO SAVEPOINT` (an error inside a transaction block aborts it — `25P02 in_failed_sql_transaction` — until a rollback to a savepoint), SQLite *SAVEPOINT* and *Response To Errors Within A Transaction* (a constraint error rolls back only the statement; `SQLITE_FULL`, `IOERR`, `BUSY`, `NOMEM`, `INTERRUPT` may roll back the whole transaction), SQLite and PostgreSQL `LIKE … ESCAPE` (any character after the escape matches itself), EF Core's `StartsWith`/`Contains` translation (an escaped pattern parameter with `ESCAPE '\'`), EF Core savepoints (`CreateSavepointAsync`, automatic `__EFSavePoint` around `SaveChanges` inside a user transaction, `UseTransactionAsync`, `SetDbConnection(connection, contextOwnsConnection)`), `Microsoft.Data.Sqlite` (`BEGIN IMMEDIATE` by default, `sqlite3_interrupt` on cancellation, 30 s busy timeout), `CancellationTokenSource(TimeSpan, TimeProvider)`, `Task.Run(Func<Task>)`/`Task.Run(Func<Task<T>>)` overload resolution.

## Global Constraints

- Branch `hx/07-transactions` in the worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-hx07`, based on `hx/06-delivery-operations` at `2e75ea32` (PR #422, open against `main`). Issue **#384**, epic #370, blocked by #383. The PR's base is `hx/06-delivery-operations` while #422 is open; once #422 merges, merge `origin/main` into this branch (merge-commit style) and retarget the PR to `main`. One writer per worktree; never `git add -A` (stage each task's files by path); verify `git branch --show-current` prints `hx/07-transactions` before every commit and before the push.
- **Scope is §7 HX-07 only.** No write hooks, no `IWriteExtensionRunner`, no budgets per hook, no self-write detection, no `ExtensionSystemAccess` log (HX-09); no commands, `AlvoCommandContext.TransactAsync`, command idempotency or completion events (HX-08); no `IAlvoScheduler` joining a unit (HX-13); no boot ban of `IAlvoTransactions` in hook constructors (HX-09's §4.4.5 check). **Dashboard: none** (§7). No change to the HTTP query syntax, the OpenAPI document, the Vacuum ruleset or the TeaPie path set.
- **Public surface added is exactly:**
  - Abstractions `MMLib.Alvo.Data`: `IAlvoTransactions` (`TransactAsync<T>` + one non-generic default member), `IAlvoTransaction : IAlvoSession` (`CreateManyAsync`, `UpdateManyAsync`, `DeleteManyAsync`, `PublishAsync`, `AsSystem`), `AlvoTransactionOptions` (`Timeout`, `SuppressHostHooks`, `Reason`, all `init`), `AlvoUnitInProgressException`; `AlvoFilterOperator.StartsWith`, `.EndsWith`, `.Contains` (appended after `Is`).
  - Core `MMLib.Alvo.Data`: `IAlvoPatch<T>`, `AlvoPatch` (`For<T>()`), `AlvoTypedWriteExtensions` (`CreateAsync<T>`, `UpdateAsync<T>`, `ReplaceAsync<T>`, `ExistsAsync<T>` on `IAlvoSession`).
  - Testing `MMLib.Alvo.Testing.Data`: `AlvoDataLiteralTextTests` (abstract contract suite).
  Everything else is `internal`. Every addition §5 does not list is a ruling below (Rulings 3, 4, 19, 25).
- **Wire and naming constants (verbatim):** default `Timeout` 30 s; `Timeout` must be greater than zero and at most 1 hour; `Reason` at most 500 characters, no control character, required when `SuppressHostHooks` is set; savepoint names `alvo_unit_<n>` (n from 1 per unit); the LIKE escape character `\`; escaped characters `\`, `%`, `_`, `[`; the HTTP operator allow-list stays exactly `eq neq gt gte lt lte like ilike in is`.
- **Engine parity:** every unit fact runs on SQLite (`MMLib.Alvo.TestHost.Tests`) and PostgreSQL (`MMLib.Alvo.Data.PostgreSql.Tests.Integration`) through one shared facts class in `test/_shared/testhost`; every literal-operator contract row runs on SQLite, PostgreSQL and the in-memory reference. **Top-level writes keep their exact statements** — no savepoint, no extra statement — so `AlvoDataSqlSnapshotTests`, `AlvoDataStatementTests` and every existing `AlvoData*Tests` row stay byte-identical (acceptance (7)).
- **Data-package allow-lists:** `ChangeTrackerReachTests._sqlComposingFiles` gains exactly `EfAlvoData.Units.cs` (it opens the unit's connection and writes the unit's outbox row). No other new data-package file may contain `CreateCommand`, `CommandText`, `ExecuteNonQuery`/`Scalar`/`Reader`, `ExecuteSqlRaw`, `FromSql` or `GetDbConnection`; `WriteUnit.cs`, `UnitSavepoint.cs`, `TurnHeldTransaction.cs`, `EfAlvoTransactions.cs` and `EfAlvoTransaction.cs` receive their connection and transaction as parameters.
- **SQL text scan:** `SqlTextConfinedToRendererArchitectureTests` scans `src/MMLib.Alvo/{Expressions,Rules,Events}`; the one file this plan edits there, `Events/Internal/AlvoEvents.cs`, may not contain `SELECT `, `WHERE `, `ILIKE`, `COALESCE(`, `IS NOT NULL`, `CASE WHEN `, ` IN (` or the upper-case words `AND`, `OR`, `NOT` (code, strings or XML docs).
- **`AlvoFlow` setters:** only `MMLib.Alvo` and `MMLib.Alvo.Data.EntityFrameworkCore` may call `AlvoFlow.Enter` or the new `AlvoFlow.EnterUnit`; `InternalMemberReferences.FlowSetters` names both.
- Public XML docs cite no `§` and no `#issue` (DocsGen's `RemarksFilterTests`). XML docs on every public member.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, English only, `ConfigureAwait(false)` in `src/`, never in tests (xUnit1030); every test call passes `TestContext.Current.CancellationToken` (xUnit1051). `.cs` files are **UTF-8 with BOM + CRLF**: after creating or editing `.cs` files run `dotnet format whitespace MMLib.Alvo.slnx --include <file> <file> …` (file paths, never a directory).
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Public-API baselines: `*PublicApiApprovalTests` in `test/MMLib.Alvo.Abstractions.Tests` (Abstractions) and `test/MMLib.Alvo.Tests` (core), `*TestingLibraryPublicApiTests` in `test/MMLib.Alvo.Tests` (Testing). To accept a baseline: run the approval test, read the `*.received.txt` diff — it must add exactly the symbols this task lists — then move it over the `*.verified.txt`. Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each task marked **[slice end]**, `scripts/test-ring2` before the PR.
- A grown `PublicApi.*.verified.txt` trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added public symbol against `alvo-architecture-rules` (*public is the contract*) and the public-surface list above.
- **CI parity:** rings are Debug on the local SDK; CI builds Release with `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`. Task 12 runs the Release build in the CI SDK container before anything is pushed.
- **Security core:** §6.2 lists HX-07. Task 12 runs the `alvo-security-core-review` checklist, a labelled `/security-review` substitute, and labels the PR `needs-deep-review`.
- Commits: Conventional Commits, one per task, each message ending with `Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. No `Closes` keyword in a commit message; the PR body carries `Closes #384` once.

## Review Focus

1. **A batch that fails at the engine inside a unit** must leave none of its rows while the unit's earlier writes still commit — the batch path returns a refused result *without* throwing, so only the savepoint's dispose-without-commit rollback stands between it and half a batch committed — `EfAlvoTransactionsTests.A_batch_failing_at_the_engine_inside_a_unit_leaves_none_of_its_rows` and `A_refused_batch_leaves_none_of_its_rows_and_the_unit_goes_on` (Task 8).
2. **Two calls on one transaction at once** (`Task.WhenAll(unit.CreateAsync(…), unit.CreateAsync(…))`) must be refused, not interleaved on one `DbConnection`, and the unit must roll back — `WriteUnitTests.A_second_call_while_one_runs_is_refused_and_poisons_the_unit` (Task 6), which holds the first call open with a `TaskCompletionSource`: over SQLite in memory two real writes complete synchronously and never overlap, so a fact built on them would pass without testing anything.
3. **An `IAlvoTransaction` kept and used after `TransactAsync` returned** must be refused with an "ended" error, never write on a connection the pool has handed to someone else — `WriteUnitTests.A_unit_used_after_it_was_released_is_refused` (Task 6), `EfAlvoTransactionsTests.A_transaction_used_after_its_unit_ended_is_refused` (Task 8).
4. **Work started inside the delegate that outlives it** (`_ = Task.Run(…)` calling `IAlvoData` later) must not be refused once the unit is over, though it inherited the unit's execution context — `AlvoFlowUnitTests.A_unit_that_has_ended_is_not_on_the_flow` (Task 1), `EfAlvoTransactionsTests.A_data_call_from_work_that_outlives_the_unit_is_not_refused` (Task 8).
5. **The caller's own token cancelled mid-unit** must roll back and surface `OperationCanceledException`, not `TimeoutException` — `WriteUnitTests.A_cancelled_call_poisons_the_unit_with_its_cancellation` (Task 6), `EfAlvoTransactionsTests.A_cancelled_caller_rolls_the_unit_back_and_sees_its_cancellation` (Task 8).

---

## File structure

Created:

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Data/IAlvoTransactions.cs` | the port: `TransactAsync<T>` and its non-generic default |
| `src/MMLib.Alvo.Abstractions/Data/IAlvoTransaction.cs` | the unit's data access: a session plus batches, publish, `AsSystem` |
| `src/MMLib.Alvo.Abstractions/Data/AlvoTransactionOptions.cs` | `Timeout`, `SuppressHostHooks`, `Reason`, and their internal validation |
| `src/MMLib.Alvo.Abstractions/Data/AlvoUnitInProgressException.cs` | the R-B1 refusal |
| `src/MMLib.Alvo.Abstractions/Identity/IAlvoFlowUnit.cs` | what `AlvoFlow` knows of an open unit: open, poison |
| `src/MMLib.Alvo.Abstractions/Events/AlvoCustomEnvelope.cs` | the one builder of a custom event's envelope (moved from `AlvoEvents`) |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/LiteralTextPattern.cs` | escaping a literal text value into a `LIKE` pattern |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs` | one open unit |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UnitSavepoint.cs` | a call joined to a unit, under its own savepoint |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TurnHeldTransaction.cs` | a top-level transaction that releases its write turn after itself |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs` | `OpenUnitAsync`, the `…InUnitAsync` entries, the transactional publish, `Deeper` |
| `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransactions.cs`, `EfAlvoTransaction.cs` | the port's implementation |
| `src/MMLib.Alvo/Data/IAlvoPatch.cs`, `AlvoPatch.cs`, `AlvoTypedWriteExtensions.cs` | typed writes |
| `src/MMLib.Alvo/Data/Internal/TypedPatch.cs`, `RecordWriter.cs` | a typed patch, and a typed record as field values |
| `src/MMLib.Alvo.Testing/Data/AlvoDataLiteralTextTests.cs` | the literal-operator contract rows |
| `test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs`, `AlvoFlowUnitTests.cs`, `AlvoTransactionOptionsTests.cs`, `TypedWriteTests.cs`, `InMemoryAlvoDataLiteralTextTests.cs`; `test/MMLib.Alvo.Tests/Events/AlvoEventsUnitTests.cs` | flow, options, typed writes, in-memory leg, publish refusal |
| `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/LiteralTextPatternTests.cs`, `WriteUnitTests.cs`, `EfAlvoDataUnitRefusalTests.cs`, `EfAlvoTransactionsTests.cs` | escaping, the unit, the refusals, the port over SQLite in memory |
| `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataLiteralTextTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataLiteralTextTests.cs` | the contract's engine legs |
| `test/_shared/testhost/TransactionFacts.cs`, `test/_shared/testhost/descriptors/ledger-desk.alvo.json`, `test/MMLib.Alvo.TestHost.Tests/SqliteTransactionTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTransactionTests.cs` | acceptance (1)–(6), (9), (10) on both engines |
| `website/src/content/docs/extending/transactions.mdx`; `website/src/snippets/extending/transactions/{InvoiceDesk.cs,InvoiceDeskTests.cs,invoice-desk.alvo.json}` | *Transactions in your own code* |

Modified: `src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs`, `Data/AlvoFilter.cs` (enum), `Data/IAlvoData.cs`, `Data/IAlvoSession.cs`, `Events/IAlvoEvents.cs` (docs); `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (partial, the seam, the refusals), `Internal/FilterSqlRenderer.cs`, `AlvoEfCoreProvider.cs`; `src/MMLib.Alvo/Events/Internal/AlvoEvents.cs`, `Api/Internal/FilterOperators.cs`, `Data/Internal/FilterTranslator.Calls.cs`, `Data/AlvoTypedReadExtensions.cs` (remarks); `src/MMLib.Alvo.Testing/Data/AlvoFilterEvaluator.cs`; tests `test/_shared/ai/InternalMemberReferences.cs`, `test/_shared/testhost/TypedLayerFacts.cs`, `test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/{FilterSqlRendererTests.cs,FilterSqlRendererPropertyTests.cs,ChangeTrackerReachTests.cs,WritePathWorld.cs,MMLib.Alvo.Data.EntityFrameworkCore.Tests.csproj}`, `test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs`; the three `PublicApi.*.verified.txt` named above; docs per Task 11.

## Task 1: The port, its refusal, and the unit on the flow

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoUnitInProgressException.cs`, `Data/AlvoTransactionOptions.cs`, `Data/IAlvoTransaction.cs`, `Data/IAlvoTransactions.cs`, `Identity/IAlvoFlowUnit.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs`, `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` (class remarks), `src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs` (remarks)
- Modify (tests): `test/_shared/ai/InternalMemberReferences.cs`, `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`
- Create (tests): `test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs`, `test/MMLib.Alvo.Tests/Data/AlvoFlowUnitTests.cs`, `test/MMLib.Alvo.Tests/Data/AlvoTransactionOptionsTests.cs`

**Interfaces:**
- Consumes: HX-01's internal `AlvoFlow` (`Provenance`, `Enter`), `AlvoProvenance`, `AlvoContext.ActorKind`, `AlvoActorKind.System`.
- Produces: `public interface IAlvoTransactions { Task<T> TransactAsync<T>(AlvoContext context, Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoTransactionOptions? options = null, CancellationToken cancellationToken = default); Task TransactAsync(AlvoContext context, Func<IAlvoTransaction, CancellationToken, Task> work, AlvoTransactionOptions? options = null, CancellationToken cancellationToken = default) /* default member */; }`; `public interface IAlvoTransaction : IAlvoSession { Task<AlvoBatchResult> CreateManyAsync(string, IReadOnlyList<IReadOnlyDictionary<string, object?>>, CancellationToken = default); Task<AlvoBatchResult> UpdateManyAsync(string, IReadOnlyList<AlvoRowPatch>, CancellationToken = default); Task<AlvoBatchResult> DeleteManyAsync(string, IReadOnlyList<Guid>, CancellationToken = default); Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken = default); IAlvoTransaction AsSystem(); }`; `public sealed class AlvoTransactionOptions { TimeSpan Timeout {get; init;} = 30 s; bool SuppressHostHooks {get; init;}; string? Reason {get; init;}; internal const int MaxReasonLength = 500; internal static TimeSpan DefaultTimeout; internal static TimeSpan MaxTimeout; internal static AlvoTransactionOptions Validated(AlvoTransactionOptions? options, AlvoContext context); }`; `public sealed class AlvoUnitInProgressException : InvalidOperationException { internal static AlvoUnitInProgressException For(string call); }`; `internal interface IAlvoFlowUnit { bool IsOpen { get; } void Poison(Exception failure); }`; `AlvoFlow.Unit` (`IAlvoFlowUnit?`, only an open one), `AlvoFlow.EnterUnit(IAlvoFlowUnit unit, AlvoProvenance provenance) : IDisposable`, `AlvoFlow.RefuseInsideUnit(string call, bool keyed = false)`; test double `FakeFlowUnit` (`IsOpen` settable, `Poisoned` list).

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs`:

```csharp
namespace MMLib.Alvo.Tests.Data;

/// <summary>An open unit as <c>AlvoFlow</c> sees it, recording what poisoned it.</summary>
internal sealed class FakeFlowUnit : IAlvoFlowUnit
{
    private readonly List<Exception> _poisoned = [];

    public bool IsOpen { get; set; } = true;

    internal IReadOnlyList<Exception> Poisoned => _poisoned;

    public void Poison(Exception failure) => _poisoned.Add(failure);
}
```

`test/MMLib.Alvo.Tests/Data/AlvoFlowUnitTests.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>The open unit on the flow (spec 2026-10-14 §4.5, R-B1): carried beside provenance, refused by every port entry.</summary>
public sealed class AlvoFlowUnitTests
{
    [Fact]
    public void No_unit_is_on_a_flow_nothing_entered()
        => AlvoFlow.Unit.ShouldBeNull();

    [Fact]
    public void An_entered_unit_is_on_the_flow_with_its_provenance_until_the_scope_ends()
    {
        var unit = new FakeFlowUnit();

        using (AlvoFlow.EnterUnit(unit, new AlvoProvenance { Depth = 3, CorrelationId = "c-1" }))
        {
            AlvoFlow.Unit.ShouldBeSameAs(unit);
            AlvoFlow.Provenance!.Depth.ShouldBe(3);
        }

        AlvoFlow.Unit.ShouldBeNull();
        AlvoFlow.Provenance.ShouldBeNull();
    }

    [Fact]
    public void Entering_a_delivery_s_provenance_inside_a_unit_keeps_the_unit()
    {
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, new AlvoProvenance { Depth = 1 });

        using (AlvoFlow.Enter(new AlvoProvenance { Depth = 4 }))
        {
            AlvoFlow.Unit.ShouldBeSameAs(unit);
        }

        AlvoFlow.Provenance!.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task A_unit_that_has_ended_is_not_on_the_flow()
    {
        var unit = new FakeFlowUnit();
        Task<IAlvoFlowUnit?> outliving;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            outliving = Task.Run(async () =>
            {
                await release.Task;
                return AlvoFlow.Unit;
            }, TestContext.Current.CancellationToken);
        }

        unit.IsOpen = false;
        release.SetResult();

        (await outliving).ShouldBeNull("work that outlives its unit inherits the context but not the refusal");
    }

    [Fact]
    public void A_call_inside_an_open_unit_is_refused_and_poisons_it()
    {
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = Should.Throw<AlvoUnitInProgressException>(() => AlvoFlow.RefuseInsideUnit("IAlvoData.CreateAsync"));

        refusal.Message.ShouldStartWith("IAlvoData.CreateAsync was called while a transaction unit is open on this flow");
        refusal.ShouldBeAssignableTo<InvalidOperationException>();
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
    }

    [Fact]
    public void A_keyed_call_inside_an_open_unit_is_an_argument_exception_and_poisons_it()
    {
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = Should.Throw<ArgumentException>(() => AlvoFlow.RefuseInsideUnit("IAlvoData.CreateAsync", keyed: true));

        refusal.ParamName.ShouldBe("idempotency");
        refusal.Message.ShouldContain("idempotency key");
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
    }

    [Fact]
    public void A_call_outside_any_unit_is_let_through()
        => Should.NotThrow(() => AlvoFlow.RefuseInsideUnit("IAlvoData.CreateAsync", keyed: true));
}
```

`test/MMLib.Alvo.Tests/Data/AlvoTransactionOptionsTests.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary><see cref="AlvoTransactionOptions"/>: its defaults and what <c>TransactAsync</c> refuses before opening a unit.</summary>
public sealed class AlvoTransactionOptionsTests
{
    private static readonly AlvoContext _caller = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };
    private static readonly AlvoContext _system = AlvoContext.System(tenant: null);

    [Fact]
    public void The_default_unit_runs_for_thirty_seconds_and_suppresses_nothing()
    {
        var options = AlvoTransactionOptions.Validated(null, _caller);

        (options.Timeout, options.SuppressHostHooks, options.Reason).ShouldBe((TimeSpan.FromSeconds(30), false, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3_600_001)]
    public void A_timeout_outside_a_millisecond_to_an_hour_is_refused(long milliseconds)
    {
        var refusal = Should.Throw<ArgumentOutOfRangeException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { Timeout = TimeSpan.FromMilliseconds(milliseconds) }, _caller));

        refusal.ParamName.ShouldBe("Timeout");
    }

    [Fact]
    public void An_hour_is_the_longest_timeout()
        => AlvoTransactionOptions.Validated(new AlvoTransactionOptions { Timeout = TimeSpan.FromHours(1) }, _caller).Timeout.ShouldBe(TimeSpan.FromHours(1));

    [Fact]
    public void Suppressing_host_hooks_is_refused_to_a_caller_who_is_not_the_system()
        => Should.Throw<ArgumentException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true, Reason = "backfill" }, _caller))
            .Message.ShouldContain("system context");

    [Fact]
    public void Suppressing_host_hooks_needs_a_reason()
        => Should.Throw<ArgumentException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true }, _system))
            .ParamName.ShouldBe("Reason");

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    public void A_reason_with_a_control_character_is_refused(string reason)
        => Should.Throw<ArgumentException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true, Reason = reason }, _system))
            .ParamName.ShouldBe("Reason");

    [Fact]
    public void A_reason_past_five_hundred_characters_is_refused()
        => Should.Throw<ArgumentException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true, Reason = new string('r', 501) }, _system))
            .ParamName.ShouldBe("Reason");

    [Fact]
    public void The_system_may_suppress_host_hooks_with_a_reason()
        => AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true, Reason = "nightly backfill" }, _system)
            .SuppressHostHooks.ShouldBeTrue();
}
```

In `test/_shared/ai/InternalMemberReferences.cs`, replace the `FlowSetters` line with:

```csharp
    internal static IReadOnlyList<string> FlowSetters { get; } = ["MMLib.Alvo.AlvoFlow::Enter", "MMLib.Alvo.AlvoFlow::EnterUnit"];
```

and in its summary, after "only <c>MMLib.Alvo</c> and the EF driver may call it", add ", and the EF driver alone enters a unit".

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoFlowUnitTests' --filter-class '*AlvoTransactionOptionsTests'`
Expected: FAIL to compile — `IAlvoFlowUnit`, `AlvoFlow.Unit`, `AlvoFlow.EnterUnit`, `AlvoFlow.RefuseInsideUnit`, `AlvoUnitInProgressException`, `AlvoTransactionOptions` do not exist.

- [ ] **Step 3: Write the types**

`src/MMLib.Alvo.Abstractions/Identity/IAlvoFlowUnit.cs`:

```csharp
namespace MMLib.Alvo;

/// <summary>What the ambient flow knows of the transaction unit open on it: whether it is still open, and how to fail it.</summary>
/// <remarks>
/// Internal and in Abstractions because the core refuses a publish inside a unit the EF driver opened, and neither may
/// reference the other. Implemented by the driver's write unit only.
/// </remarks>
internal interface IAlvoFlowUnit
{
    /// <summary>Gets a value indicating whether the <c>TransactAsync</c> that opened the unit has not returned yet.</summary>
    bool IsOpen { get; }

    /// <summary>Fails the unit: it will roll back however its delegate ends, and its commit throws <paramref name="failure"/>.</summary>
    /// <param name="failure">The first failure that makes the unit's work untrustworthy.</param>
    void Poison(Exception failure);
}
```

`src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs` — replace the class body (keep the file's summary; extend its remarks' first paragraph with: "HX-07 adds the open transaction unit beside the provenance: <see cref=\"EnterUnit\"/> is called by the EF driver's <c>TransactAsync</c> alone, and every data-port entry calls <see cref=\"RefuseInsideUnit\"/>."):

```csharp
internal static class AlvoFlow
{
    private static readonly AsyncLocal<AlvoProvenance?> _provenance = new();
    private static readonly AsyncLocal<IAlvoFlowUnit?> _unit = new();

    /// <summary>The ambient provenance, or <see langword="null"/> outside any framework flow.</summary>
    internal static AlvoProvenance? Provenance => _provenance.Value;

    /// <summary>The transaction unit open on this flow, or <see langword="null"/> when none is — including one whose <c>TransactAsync</c> has returned.</summary>
    /// <remarks>
    /// An ended unit is not reported, although work its delegate started may still carry it in its execution context:
    /// such work runs after the unit and may use the data port.
    /// </remarks>
    internal static IAlvoFlowUnit? Unit => _unit.Value is { IsOpen: true } open ? open : null;

    /// <summary>Makes <paramref name="provenance"/> ambient until the returned scope is disposed; the open unit, if any, stays.</summary>
    /// <param name="provenance">The provenance of the flow being entered.</param>
    /// <returns>A scope whose disposal restores whatever was ambient before.</returns>
    internal static IDisposable Enter(AlvoProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var restore = new Restore(_provenance.Value, _unit.Value);
        _provenance.Value = provenance;

        return restore;
    }

    /// <summary>Makes <paramref name="unit"/> the open unit, with <paramref name="provenance"/>, until the returned scope is disposed.</summary>
    /// <param name="unit">The unit being opened.</param>
    /// <param name="provenance">The unit's provenance, which every write inside it reports.</param>
    /// <returns>A scope whose disposal restores whatever was ambient before.</returns>
    internal static IDisposable EnterUnit(IAlvoFlowUnit unit, AlvoProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(provenance);
        var restore = new Restore(_provenance.Value, _unit.Value);
        _provenance.Value = provenance;
        _unit.Value = unit;

        return restore;
    }

    /// <summary>Refuses a data-port call made while a unit is open on this flow, and fails that unit.</summary>
    /// <param name="call">The refused member, as <c>IAlvoData.CreateAsync</c>.</param>
    /// <param name="keyed">Whether the call carried an idempotency key, which is refused as an argument instead.</param>
    /// <exception cref="Data.AlvoUnitInProgressException">A unit is open and the call carried no key.</exception>
    /// <exception cref="ArgumentException">A unit is open and the call carried a key.</exception>
    internal static void RefuseInsideUnit(string call, bool keyed = false)
    {
        if (Unit is not { } unit)
        {
            return;
        }

        Exception refusal = keyed ? KeyInsideUnit(call) : Data.AlvoUnitInProgressException.For(call);
        unit.Poison(refusal);
        throw refusal;
    }

    private static ArgumentException KeyInsideUnit(string call) => new(
        $"{call} carried an idempotency key while a transaction unit is open on this flow, so it was refused and the unit "
        + "will roll back. A unit is never replayed, so a key inside it guards nothing; a command's idempotency belongs to "
        + "the command that opens the unit. Fix: drop the key and make the call through the unit's IAlvoTransaction.",
        "idempotency");

    private sealed class Restore(AlvoProvenance? provenance, IAlvoFlowUnit? unit) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _provenance.Value = provenance;
            _unit.Value = unit;
        }
    }
}
```

`src/MMLib.Alvo.Abstractions/Data/AlvoUnitInProgressException.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// A data call was made while a transaction unit is open on the current flow, through a port that would run outside the
/// unit — <see cref="IAlvoData"/>, <see cref="IAlvoSession"/>, <c>IAlvoEvents.PublishAsync</c> or a nested
/// <see cref="IAlvoTransactions.TransactAsync{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Such a call would run on a second connection: it could not see the unit's writes, would not roll back with them, could
/// carry a different caller into the middle of this one's work, and on an engine with one writer would wait for the
/// unit's own lock. It is refused at once instead. Make the call through the unit's <see cref="IAlvoTransaction"/>, or
/// move it before or after <c>TransactAsync</c>.
/// </para>
/// <para>
/// The refusal also fails the unit: it rolls back however the delegate ends, so a caught refusal cannot commit work its
/// author believed included the refused call. An <see cref="InvalidOperationException"/>, so <see cref="IAlvoData"/>'s
/// invariant family.
/// </para>
/// </remarks>
public sealed class AlvoUnitInProgressException : InvalidOperationException
{
    private const string DefaultMessage =
        "A data call was made while a transaction unit is open on this flow; use the unit's IAlvoTransaction instead.";

    /// <summary>Initializes a new instance with the default message.</summary>
    public AlvoUnitInProgressException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/>.</summary>
    /// <param name="message">The message.</param>
    public AlvoUnitInProgressException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/> and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoUnitInProgressException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal of <paramref name="call"/>.</summary>
    /// <param name="call">The refused member, as <c>IAlvoData.CreateAsync</c>.</param>
    /// <returns>The exception to throw.</returns>
    internal static AlvoUnitInProgressException For(string call) => new(
        $"{call} was called while a transaction unit is open on this flow, so it was refused and the unit will roll back. "
        + "A call through the data port would run outside the unit's transaction, on a second connection. Fix: make the "
        + "call through the unit's IAlvoTransaction (the delegate's parameter), or move it before or after TransactAsync.");
}
```

`src/MMLib.Alvo.Abstractions/Data/AlvoTransactionOptions.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>How one transaction unit runs: how long it may hold the database, and whether host write hooks are skipped.</summary>
public sealed class AlvoTransactionOptions
{
    internal const int MaxReasonLength = 500;

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);

    private static readonly AlvoTransactionOptions _default = new();

    /// <summary>Gets how long the unit may run before it is cancelled and rolled back; 30 seconds unless set, at most one hour.</summary>
    /// <remarks>
    /// Measured on the host's <see cref="TimeProvider"/>. At the deadline the unit's token is cancelled, the unit rolls back
    /// as soon as no call through it is running, and the database is free again — work the delegate does after that is
    /// refused, and <c>TransactAsync</c> throws <see cref="TimeoutException"/>.
    /// </remarks>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>Gets a value indicating whether host write hooks are skipped for every write in the unit; declarative hooks and events never are.</summary>
    /// <remarks>Accepted only with a system context and a <see cref="Reason"/>.</remarks>
    public bool SuppressHostHooks { get; init; }

    /// <summary>Gets why host write hooks are suppressed: at most 500 characters, no control character.</summary>
    public string? Reason { get; init; }

    /// <summary>The options a unit runs with: <paramref name="options"/>, or the defaults, once checked against <paramref name="context"/>.</summary>
    /// <param name="options">The caller's options, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The caller opening the unit.</param>
    /// <returns>The checked options.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not greater than zero and at most one hour.</exception>
    /// <exception cref="ArgumentException">Hooks are suppressed for a caller who is not the system, or without a usable reason.</exception>
    internal static AlvoTransactionOptions Validated(AlvoTransactionOptions? options, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var checkedOptions = options ?? _default;
        EnsureTimeout(checkedOptions.Timeout);
        EnsureReason(checkedOptions.Reason);
        EnsureSuppression(checkedOptions, context);

        return checkedOptions;
    }

    private static void EnsureTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Timeout), timeout, "A unit's Timeout must be greater than zero and at most one hour: a unit holds the database's write lock while it runs.");
        }
    }

    private static void EnsureReason(string? reason)
    {
        if (reason is not null && (reason.Length > MaxReasonLength || reason.Any(char.IsControl)))
        {
            throw new ArgumentException(
                $"A unit's Reason is at most {MaxReasonLength} characters with no control character: it is written to one log field.", nameof(Reason));
        }
    }

    private static void EnsureSuppression(AlvoTransactionOptions options, AlvoContext context)
    {
        if (!options.SuppressHostHooks)
        {
            return;
        }

        if (context.ActorKind != AlvoActorKind.System)
        {
            throw new ArgumentException(
                "SuppressHostHooks is accepted only with a system context (AlvoContext.System): host write hooks may be skipped by the framework's own maintenance, never on a caller's behalf.",
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Reason))
        {
            throw new ArgumentException("SuppressHostHooks needs a Reason: skipping host write hooks is audited.", nameof(Reason));
        }
    }
}
```

`src/MMLib.Alvo.Abstractions/Data/IAlvoTransaction.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// The data access of one transaction unit: every call runs as <see cref="IAlvoSession.Context"/>, through the same rules,
/// tenancy and chain-depth checks as <see cref="IAlvoData"/>, inside the unit's one database transaction.
/// </summary>
/// <remarks>
/// <para>
/// Unlike a session from <see cref="IAlvoSessions"/>, nothing commits on its own: the unit commits when its delegate
/// returns, and rolls back if it throws. Reads see the unit's own writes.
/// </para>
/// <para>
/// <b>A refused or failed call does not end the unit.</b> Each call runs under its own savepoint, so an authorization
/// refusal, a constraint violation or a failed precondition undoes that call alone, on every engine; catch it and go on.
/// <b>A few failures end it</b> — the unit's timeout, its chain-depth limit, a cancelled call, two calls at once, or a call
/// through another data port while it is open: catching one of those cannot make the unit commit.
/// </para>
/// <para>
/// One call at a time: await each call before making the next. No call takes an idempotency key — a unit is never
/// replayed. Alvo implements this interface and may add members to it; it is not meant to be implemented by host code.
/// </para>
/// </remarks>
public interface IAlvoTransaction : IAlvoSession
{
    /// <summary>Creates every row of a batch as one all-or-nothing write inside the unit.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="rows">The rows to create.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The written rows, or every row's refusal and nothing written.</returns>
    Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default);

    /// <summary>Updates every row of a batch as one all-or-nothing write inside the unit.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="rows">The rows and the fields to change on each.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The written rows, or every row's refusal and nothing written.</returns>
    Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default);

    /// <summary>Deletes every row of a batch as one all-or-nothing write inside the unit.</summary>
    /// <param name="entity">The entity name.</param>
    /// <param name="ids">The rows to delete.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>How many rows were deleted, or every row's refusal and nothing deleted.</returns>
    Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>Publishes one custom application event that commits with the unit, or not at all.</summary>
    /// <param name="type">The event's name, as <c>IAlvoEvents.PublishAsync</c> accepts it.</param>
    /// <param name="subject">What the event is about, and its partition key.</param>
    /// <param name="data">The payload — flat scalars — or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the event is written into the unit.</returns>
    /// <exception cref="ArgumentException">The name is reserved or malformed, or the payload carries a value the envelope cannot express.</exception>
    Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default);

    /// <summary>The same unit, acting as the system in the caller's tenant, with the caller's provenance.</summary>
    /// <returns>A view whose calls run as <c>AlvoContext.System(tenant)</c> — never in another tenant.</returns>
    IAlvoTransaction AsSystem();
}
```

`src/MMLib.Alvo.Abstractions/Data/IAlvoTransactions.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>Opens transaction units: several reads, writes, batches and custom events that commit together or not at all.</summary>
/// <remarks>
/// <para>
/// The delegate receives the unit's <see cref="IAlvoTransaction"/> and a token that ends with the unit's
/// <see cref="AlvoTransactionOptions.Timeout"/>. Returning commits; throwing rolls back and rethrows. A unit is
/// <b>never retried</b>: the delegate runs once, so a deterministic failure surfaces at once.
/// </para>
/// <para>
/// While the unit is open, every other data-port call on its flow — <see cref="IAlvoData"/>, <see cref="IAlvoSession"/>,
/// <c>IAlvoEvents.PublishAsync</c>, a nested <c>TransactAsync</c> — throws <see cref="AlvoUnitInProgressException"/>. Make a
/// network call before <c>TransactAsync</c>, never inside it: the unit holds row locks, and on SQLite the database's one
/// write lock, while it runs.
/// </para>
/// <para>
/// Implemented by the EF Core driver; the in-memory reference <c>InMemoryAlvoData</c> does not implement it.
/// </para>
/// </remarks>
public interface IAlvoTransactions
{
    /// <summary>Runs <paramref name="work"/> in one transaction unit as <paramref name="context"/>, and commits what it did.</summary>
    /// <typeparam name="T">What the delegate answers.</typeparam>
    /// <param name="context">The caller every call through the unit runs as.</param>
    /// <param name="work">The unit's work: its data access, and a token cancelled at the unit's deadline.</param>
    /// <param name="options">The unit's options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">A token to cancel the unit; it rolls back.</param>
    /// <returns>What <paramref name="work"/> answered, once the unit committed.</returns>
    /// <exception cref="TimeoutException">The unit ran past its timeout; nothing was committed.</exception>
    /// <exception cref="AlvoUnitInProgressException">A unit is already open on this flow, or the delegate called another data port.</exception>
    /// <exception cref="AlvoChainDepthExceededException">The caller is deeper in one causation chain than the limit allows.</exception>
    Task<T> TransactAsync<T>(
        AlvoContext context,
        Func<IAlvoTransaction, CancellationToken, Task<T>> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="work"/> in one transaction unit as <paramref name="context"/>, and commits what it did.</summary>
    /// <param name="context">The caller every call through the unit runs as.</param>
    /// <param name="work">The unit's work: its data access, and a token cancelled at the unit's deadline.</param>
    /// <param name="options">The unit's options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">A token to cancel the unit; it rolls back.</param>
    /// <returns>A task that completes once the unit committed.</returns>
    /// <exception cref="TimeoutException">The unit ran past its timeout; nothing was committed.</exception>
    /// <exception cref="AlvoUnitInProgressException">A unit is already open on this flow, or the delegate called another data port.</exception>
    Task TransactAsync(
        AlvoContext context,
        Func<IAlvoTransaction, CancellationToken, Task> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return TransactAsync(
            context,
            async (unit, token) =>
            {
                await work(unit, token).ConfigureAwait(false);
                return true;
            },
            options,
            cancellationToken);
    }
}
```

`src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs` — in the class remarks' first paragraph, replace "There is no transaction spanning two calls, and no call takes an idempotency key: a handler deduplicates on its event's id." with: "A session from <see cref=\"IAlvoSessions\"/> commits each call on its own, and no call takes an idempotency key: a handler deduplicates on its event's id. An <see cref=\"IAlvoTransaction\"/> is the session of a transaction unit, whose calls commit together."

`src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` — append to the type's `<remarks>` a paragraph: "<b>Inside a transaction unit every member throws <see cref=\"AlvoUnitInProgressException\"/></b> (the invariant family, an <see cref=\"InvalidOperationException\"/>) and fails the unit, before reading or writing anything; a write carrying an idempotency key throws <see cref=\"ArgumentException\"/> instead. Use the unit's <see cref=\"IAlvoTransaction\"/>."

- [ ] **Step 4: Run the tests to verify they pass; accept the Abstractions baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoFlowUnitTests' --filter-class '*AlvoTransactionOptionsTests' --filter-class '*InternalMemberReferencesTests' --filter-class '*EffectiveProvenanceTests' --filter-class '*DeliveryRunnerTests'`
Expected: PASS.

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL with a received file adding exactly `AlvoTransactionOptions` (3 properties), `AlvoUnitInProgressException` (3 constructors), `IAlvoTransaction` (5 members), `IAlvoTransactions` (2 members). Move the received file over `PublicApi.MMLib.Alvo.Abstractions.verified.txt`, re-run: PASS. Dispatch `alvo-snapshot-judge` when the gate asks; the justification for each symbol is the public-surface list (Global Constraints) and Rulings 3–4.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Identity/IAlvoFlowUnit.cs src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs src/MMLib.Alvo.Abstractions/Data/AlvoUnitInProgressException.cs src/MMLib.Alvo.Abstractions/Data/AlvoTransactionOptions.cs src/MMLib.Alvo.Abstractions/Data/IAlvoTransaction.cs src/MMLib.Alvo.Abstractions/Data/IAlvoTransactions.cs src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs test/MMLib.Alvo.Tests/Data/AlvoFlowUnitTests.cs test/MMLib.Alvo.Tests/Data/AlvoTransactionOptionsTests.cs test/_shared/ai/InternalMemberReferences.cs
scripts/test-ring0
git branch --show-current
git add src/MMLib.Alvo.Abstractions/Identity/IAlvoFlowUnit.cs src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs src/MMLib.Alvo.Abstractions/Data/AlvoUnitInProgressException.cs src/MMLib.Alvo.Abstractions/Data/AlvoTransactionOptions.cs src/MMLib.Alvo.Abstractions/Data/IAlvoTransaction.cs src/MMLib.Alvo.Abstractions/Data/IAlvoTransactions.cs src/MMLib.Alvo.Abstractions/Data/IAlvoSession.cs src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs test/MMLib.Alvo.Tests/Data/FakeFlowUnit.cs test/MMLib.Alvo.Tests/Data/AlvoFlowUnitTests.cs test/MMLib.Alvo.Tests/Data/AlvoTransactionOptionsTests.cs test/_shared/ai/InternalMemberReferences.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(data): the transaction port, its refusal, and the unit on the flow

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 2: Three literal text operators, rendered with a bound escape and kept off the wire

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Data/AlvoFilter.cs` (enum `AlvoFilterOperator`, appended members + remarks)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/LiteralTextPattern.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/FilterSqlRenderer.cs`, `src/MMLib.Alvo/Api/Internal/FilterOperators.cs`, `src/MMLib.Alvo.Testing/Data/AlvoFilterEvaluator.cs`
- Create (tests): `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/LiteralTextPatternTests.cs`
- Modify (tests): `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererPropertyTests.cs`, `test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs`, `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Consumes: `FilterSqlRenderer.Comparison`, `QueryFieldGuard.DeclaredField`, `BoundValue.ForColumn`/`FromFramework`, `FilterOperators`, `AlvoFilterEvaluator.Compare`.
- Produces: `AlvoFilterOperator.StartsWith`, `.EndsWith`, `.Contains` (appended after `Is`); `internal static class LiteralTextPattern { internal const string Escape = "\\"; internal static bool IsLiteralTextTest(AlvoFilterOperator op); internal static string For(AlvoFilterOperator op, string value); internal static string Escaped(string value); }`; the rendered shape `{field} LIKE {pattern parameter} ESCAPE {escape parameter}`; `FilterOperators.WireNames` unchanged.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/LiteralTextPatternTests.cs`:

```csharp
namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>A literal text value as a <c>LIKE</c> pattern: every character that means something to <c>LIKE</c> is escaped.</summary>
public sealed class LiteralTextPatternTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("C:\\tmp", "C:\\\\tmp")]
    [InlineData("[x]", "\\[x]")]
    [InlineData("%_\\[", "\\%\\_\\\\\\[")]
    [InlineData("", "")]
    public void Every_pattern_character_is_escaped_and_nothing_else(string value, string escaped)
        => Internal.LiteralTextPattern.Escaped(value).ShouldBe(escaped);

    [Theory]
    [InlineData(AlvoFilterOperator.StartsWith, "a%", "a\\%%")]
    [InlineData(AlvoFilterOperator.EndsWith, "a%", "%a\\%")]
    [InlineData(AlvoFilterOperator.Contains, "a%", "%a\\%%")]
    public void Each_operator_places_the_wildcard_on_its_open_side(AlvoFilterOperator op, string value, string pattern)
        => Internal.LiteralTextPattern.For(op, value).ShouldBe(pattern);

    [Fact]
    public void Only_the_three_literal_operators_are_literal_text_tests()
        => Enum.GetValues<AlvoFilterOperator>().Where(Internal.LiteralTextPattern.IsLiteralTextTest)
            .ShouldBe([AlvoFilterOperator.StartsWith, AlvoFilterOperator.EndsWith, AlvoFilterOperator.Contains]);
}
```

Append to `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererTests.cs` (before the private `Render` helper):

```csharp
    /// <summary>
    /// A literal text test is a <c>LIKE</c> whose pattern and escape character are both parameters: the statement text
    /// carries no quote and no backslash, and the value reaches the engine escaped.
    /// </summary>
    [Theory]
    [InlineData(AlvoFilterOperator.StartsWith, "a%_\\[", "a\\%\\_\\\\\\[%")]
    [InlineData(AlvoFilterOperator.EndsWith, "x", "%x")]
    [InlineData(AlvoFilterOperator.Contains, "x", "%x%")]
    public void A_literal_text_test_binds_its_escaped_pattern_and_its_escape(AlvoFilterOperator op, string value, string pattern)
    {
        var rendered = Render(new AlvoComparison("status", op, value));

        rendered.Sql.ShouldBe("\"status\" LIKE @alvo_f0 ESCAPE @alvo_f1");
        rendered.Parameters["alvo_f0"].Value.ShouldBe(pattern);
        rendered.Parameters["alvo_f1"].Value.ShouldBe("\\");
    }

    [Fact]
    public void A_literal_text_test_on_a_field_that_is_not_text_is_refused()
        => Should.Throw<ArgumentException>(() => Render(new AlvoComparison("price", AlvoFilterOperator.StartsWith, "1")))
            .Message.ShouldContain("'price', which is not a text field");

    [Theory]
    [InlineData(null)]
    [InlineData(5L)]
    public void A_literal_text_test_with_a_value_that_is_not_a_string_is_refused(object? value)
        => Should.Throw<ArgumentException>(() => Render(new AlvoComparison("status", AlvoFilterOperator.Contains, value)))
            .Message.ShouldContain("a value that is not a string");
```

In `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererPropertyTests.cs`:

1. In `No_filter_value_ever_appears_in_the_rendered_sql`, replace `return sql == baselines[op] && parameters!.Values.Contains(value);` with `return sql == baselines[op] && Carries(parameters!, op, value);`.
2. In `An_injection_attempt_through_every_operator_stays_inside_a_parameter`, replace `parameters!.Values.ShouldContain(Payload);` with `Carries(parameters!, op, Payload).ShouldBeTrue($"'{op}' must bind the payload.");`.
3. Add, before `Baselines()`:

```csharp
    /// <summary>
    /// Whether a rendered parameter carries <paramref name="value"/>: the value itself, or — for a literal text test — a
    /// pattern that unescapes to it. The unescaping is this test's own, so the renderer's escaping is checked, not reused.
    /// </summary>
    private static bool Carries(IReadOnlyDictionary<string, object?> parameters, AlvoFilterOperator op, string value) =>
        op is AlvoFilterOperator.StartsWith or AlvoFilterOperator.EndsWith or AlvoFilterOperator.Contains
            ? parameters.Values.OfType<string>().Any(bound => Unescaped(op, bound) == value)
            : parameters.Values.Contains(value);

    private static string? Unescaped(AlvoFilterOperator op, string pattern)
    {
        var open = op is AlvoFilterOperator.EndsWith or AlvoFilterOperator.Contains;
        var close = op is AlvoFilterOperator.StartsWith or AlvoFilterOperator.Contains;
        if ((open && !pattern.StartsWith('%')) || (close && !pattern.EndsWith('%')))
        {
            return null;
        }

        var body = pattern[(open ? 1 : 0)..(pattern.Length - (close ? 1 : 0))];
        var text = new System.Text.StringBuilder();
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] == '\\' && index + 1 < body.Length)
            {
                index++;
            }

            text.Append(body[index]);
        }

        return text.ToString();
    }
```

Append to `test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs`, after `An_operator_spelled_in_a_different_case_is_not_an_operator`:

```csharp
    /// <summary>
    /// The literal text operators are the port's, reached from C#; the wire keeps PostgREST's operators, so their lower-cased
    /// names are unknown operators rather than a dialect nobody chose.
    /// </summary>
    [Theory]
    [InlineData("make=startswith.Sko")]
    [InlineData("make=endswith.da")]
    [InlineData("make=contains.kod")]
    public void The_literal_text_operators_have_no_wire_spelling(string query) =>
        OnlyViolation(query).Code.ShouldBe("unknown-operator");
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*LiteralTextPatternTests' --filter-class '*FilterSqlRendererTests' --filter-class '*FilterSqlRendererPropertyTests'`
Expected: FAIL to compile — `AlvoFilterOperator.StartsWith` and `LiteralTextPattern` do not exist.

- [ ] **Step 3: Append the members, render them, keep them off the wire, evaluate them in the reference**

In `src/MMLib.Alvo.Abstractions/Data/AlvoFilter.cs`, after the `Is` member of `AlvoFilterOperator` (keep `Is` last of the existing ones; append — never reorder, the values are persisted nowhere but a reorder breaks every compiled caller):

```csharp

    /// <summary>The text starts with the value, character for character (<c>startsWith</c>).</summary>
    /// <remarks>
    /// <b>A literal test, never a pattern</b>: a <c>%</c>, <c>_</c>, <c>\</c> or <c>[</c> in the value matches itself on every
    /// engine, unlike <see cref="Like"/>, whose value is a pattern. Case-sensitive and ordinal. Applies to a <c>string</c> or
    /// <c>text</c> field, with a string value; a <see langword="null"/> field never matches. Not part of the HTTP query
    /// syntax, which keeps PostgREST's operators.
    /// </remarks>
    StartsWith,

    /// <summary>The text ends with the value, character for character (<c>endsWith</c>).</summary>
    /// <remarks>A literal test, as <see cref="StartsWith"/>.</remarks>
    EndsWith,

    /// <summary>The text contains the value, character for character (<c>contains</c>).</summary>
    /// <remarks>A literal test, as <see cref="StartsWith"/>.</remarks>
    Contains,
```

and add to the enum's own `<remarks>` a closing paragraph: "<b>The three literal text tests have no wire spelling.</b> <see cref=\"StartsWith\"/>, <see cref=\"EndsWith\"/> and <see cref=\"Contains\"/> are reached from C# — a typed query's <c>StartsWith</c>, or a filter built in code — and are not query-string operators: PostgREST has none, and inventing a spelling would be a dialect."

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/LiteralTextPattern.cs`:

```csharp
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>A literal text test's value as a <c>LIKE</c> pattern, escaped so that every character in it matches itself.</summary>
/// <remarks>
/// <para>
/// The escape character is <c>\</c>, bound as a parameter beside the pattern — never written into the statement — and
/// <c>\</c>, <c>%</c>, <c>_</c> and <c>[</c> are escaped. <c>[</c> means nothing to SQLite's or PostgreSQL's <c>LIKE</c>, where
/// any character after the escape matches itself, so escaping it costs nothing there and closes T-SQL's character class
/// before a T-SQL driver exists. The prior art is EF Core's own <c>StartsWith</c> translation: an escaped pattern parameter
/// with <c>ESCAPE '\'</c>.
/// </para>
/// <para>
/// Case-sensitivity is <c>LIKE</c>'s: case-sensitive on PostgreSQL, and on SQLite because its driver turns
/// <c>case_sensitive_like</c> on for every connection — the same guarantee <see cref="AlvoFilterOperator.Like"/> rests on.
/// </para>
/// </remarks>
internal static class LiteralTextPattern
{
    /// <summary>The escape character, bound as the <c>ESCAPE</c> operand.</summary>
    internal const string Escape = "\\";

    /// <summary>Whether <paramref name="op"/> is one of the three literal text tests.</summary>
    /// <param name="op">The operator.</param>
    internal static bool IsLiteralTextTest(AlvoFilterOperator op) =>
        op is AlvoFilterOperator.StartsWith or AlvoFilterOperator.EndsWith or AlvoFilterOperator.Contains;

    /// <summary>The pattern <paramref name="op"/> matches <paramref name="value"/> with: the escaped value, a <c>%</c> on its open side.</summary>
    /// <param name="op">A literal text test.</param>
    /// <param name="value">The caller's text.</param>
    internal static string For(AlvoFilterOperator op, string value)
    {
        var escaped = Escaped(value);

        return op switch
        {
            AlvoFilterOperator.StartsWith => escaped + "%",
            AlvoFilterOperator.EndsWith => "%" + escaped,
            AlvoFilterOperator.Contains => "%" + escaped + "%",
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Only a literal text test has a literal pattern."),
        };
    }

    /// <summary><paramref name="value"/> with every <c>\</c>, <c>%</c>, <c>_</c> and <c>[</c> preceded by the escape character.</summary>
    /// <param name="value">The caller's text.</param>
    internal static string Escaped(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (character is '\\' or '%' or '_' or '[')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/FilterSqlRenderer.cs`:

1. In `Comparison`, before the `_ => throw Malformed(` arm, add:

```csharp
            AlvoFilterOperator.StartsWith or AlvoFilterOperator.EndsWith or AlvoFilterOperator.Contains
                => LiteralText(declared, target, comparison, fields, bag),
```

2. Add after `Comparison`:

```csharp
    /// <summary>
    /// A literal text test: <c>LIKE</c> over the escaped value, with the escape character bound beside it, on a text field
    /// only — so neither a value nor a structural quote ever reaches the statement text.
    /// </summary>
    private static string LiteralText(
        FieldSchema declared, ComparisonTarget target, AlvoComparison comparison, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var name = comparison.Operator.ToString();
        if (declared.Type is not (FieldType.String or FieldType.Text))
        {
            throw Malformed(
                $"uses '{name}' on '{declared.Name}', which is not a text field",
                "Test a string or text field; compare any other type with eq or in.");
        }

        if (comparison.Value is not string value)
        {
            throw Malformed(
                $"uses '{name}' with a value that is not a string",
                "Pass the text to look for; a null field never matches, so test for it with is.");
        }

        var pattern = bag.Add(fields, target.Column, LiteralTextPattern.For(comparison.Operator, value));
        var escape = bag.AddFramework(fields, LiteralTextPattern.Escape);

        return $"{target.Sql} LIKE {pattern} ESCAPE {escape}";
    }
```

3. In `ParameterBag`, add after `Add`:

```csharp
        /// <summary>Records one value the framework itself supplies — never a caller's — and returns the marker that references it.</summary>
        internal string AddFramework(IFieldSqlRenderer fields, object value)
        {
            var name = prefix + _values.Count.ToString(CultureInfo.InvariantCulture);
            _values[name] = BoundValue.FromFramework(value);
            return fields.RenderParameter(name);
        }
```

4. Add `using MMLib.Alvo.Data.EntityFrameworkCore.Internal;` if the file's namespace does not already see it (`FilterSqlRenderer` is in `MMLib.Alvo.Data.EntityFrameworkCore`; `LiteralTextPattern` in `.Internal`), and in the class remarks' second paragraph append: "The three literal text tests are the exception that proves it: their value is escaped and bound, so <c>%</c> and <c>_</c> in it match themselves."

`src/MMLib.Alvo/Api/Internal/FilterOperators.cs` — add as the first member of the class, and route both derivations through it:

```csharp
    /// <summary>
    /// The port's operators with no wire spelling: the literal text tests are reached from C#, and PostgREST has no
    /// spelling for them, so the allow-list leaves them out rather than invent one.
    /// </summary>
    private static readonly AlvoFilterOperator[] _onTheWire =
        [.. Enum.GetValues<AlvoFilterOperator>().Where(op => op is not (AlvoFilterOperator.StartsWith or AlvoFilterOperator.EndsWith or AlvoFilterOperator.Contains))];
```

replace `[.. Enum.GetValues<AlvoFilterOperator>().Select(WireName)];` with `[.. _onTheWire.Select(WireName)];`, and `Enum.GetValues<AlvoFilterOperator>().ToFrozenDictionary(WireName, StringComparer.Ordinal);` with `_onTheWire.ToFrozenDictionary(WireName, StringComparer.Ordinal);`. (Static initializers run in textual order; `_onTheWire` must stay first.) In the class remarks' first paragraph append: "The literal text tests are the one exception, left off the wire (`_onTheWire`)."

`src/MMLib.Alvo.Testing/Data/AlvoFilterEvaluator.cs` — in `Compare`, before the `_ => throw Malformed(` arm, add:

```csharp
        AlvoFilterOperator.StartsWith => Literal(fieldValue, operand, static (text, value) => text.StartsWith(value, StringComparison.Ordinal)),
        AlvoFilterOperator.EndsWith => Literal(fieldValue, operand, static (text, value) => text.EndsWith(value, StringComparison.Ordinal)),
        AlvoFilterOperator.Contains => Literal(fieldValue, operand, static (text, value) => text.Contains(value, StringComparison.Ordinal)),
```

and after `Like`:

```csharp
    /// <summary>
    /// A literal text test, ordinal and case-sensitive as the shipped backends answer it. A non-text field is
    /// <c>UNKNOWN</c>; a value that is not a string is refused, as a shipped backend refuses it.
    /// </summary>
    private static bool? Literal(object? fieldValue, object? operand, Func<string, string, bool> test)
    {
        if (operand is not string value)
        {
            throw Malformed(
                "uses a literal text test with a value that is not a string",
                "Pass the text to look for; a null field never matches, so test for it with is.");
        }

        return fieldValue is string text ? test(text, value) : null;
    }
```

- [ ] **Step 4: Run the tests to verify they pass; accept the baseline**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*LiteralTextPatternTests' --filter-class '*FilterSqlRendererTests' --filter-class '*FilterSqlRendererPropertyTests'` and `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*QueryStringParserTests'`
Expected: PASS — including the unchanged `The_operator_allow_list_is_exactly_postgrests_spellings` and the three new wire rows.

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL with a received file adding exactly `StartsWith = 10`, `EndsWith = 11`, `Contains = 12` to `AlvoFilterOperator`. Accept it as in Task 1 Step 4.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Data/AlvoFilter.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/LiteralTextPattern.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/FilterSqlRenderer.cs src/MMLib.Alvo/Api/Internal/FilterOperators.cs src/MMLib.Alvo.Testing/Data/AlvoFilterEvaluator.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/LiteralTextPatternTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererPropertyTests.cs test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Data/AlvoFilter.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/LiteralTextPattern.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/FilterSqlRenderer.cs src/MMLib.Alvo/Api/Internal/FilterOperators.cs src/MMLib.Alvo.Testing/Data/AlvoFilterEvaluator.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/LiteralTextPatternTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/FilterSqlRendererPropertyTests.cs test/MMLib.Alvo.Api.Tests/QueryStringParserTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(data): literal startsWith, endsWith and contains filter operators

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 3: The literal operators' contract rows on SQLite, PostgreSQL and the reference **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo.Testing/Data/AlvoDataLiteralTextTests.cs`
- Create (tests): `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataLiteralTextTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataLiteralTextTests.cs`, `test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataLiteralTextTests.cs`
- Modify (tests): `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt`

**Interfaces:**
- Consumes: Task 2's operators; the `CreateAsync(SchemaModel, AlvoDescriptor, seed)` seam shape `AlvoDataOrderingTests` defines; `SqliteAlvoDataFixture`, `PostgreSqlAlvoDataFixture`, `AlvoDataSeed.SeedAsync`, `InMemoryAlvoData`.
- Produces: `public abstract class AlvoDataLiteralTextTests` with `protected abstract Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);` and seven facts — acceptance (11)'s contract rows.

- [ ] **Step 1: Write the contract suite and its three legs**

`src/MMLib.Alvo.Testing/Data/AlvoDataLiteralTextTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Shouldly;
using Xunit;
using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The literal text tests — <see cref="AlvoFilterOperator.StartsWith"/>, <see cref="AlvoFilterOperator.EndsWith"/>,
/// <see cref="AlvoFilterOperator.Contains"/> — answer the same on every engine: a <c>%</c>, <c>_</c>, <c>\</c> or <c>[</c> in the
/// value matches itself, case counts, and a missing value never matches.
/// </summary>
/// <remarks>
/// One shipped suite, inherited by every engine and by the in-memory reference, because the defect it guards is a
/// disagreement between them: a <c>LIKE</c> written without escaping answers <c>startsWith '50%'</c> with every row starting
/// with <c>50</c>, and the bracket class only T-SQL knows would make <c>contains '[x]'</c> a character class there.
/// </remarks>
public abstract class AlvoDataLiteralTextTests
{
    private const string Entity = "codes";

    private static readonly string[] _codes = ["50%_off", "50XYoff", "C:\\tmp", "C:Xtmp", "a_b", "axb", "[x]y", "xy", "Abc", "abc"];

    /// <summary>
    /// Builds a fresh <see cref="IAlvoData"/> over <paramref name="descriptor"/>/<paramref name="schema"/>, seeded out of band
    /// with <paramref name="seed"/>'s rows — the seam <c>AlvoDataOrderingTests</c> defines.
    /// </summary>
    /// <param name="schema">The schema every entity in <paramref name="descriptor"/> maps to.</param>
    /// <param name="descriptor">The project descriptor whose rules apply.</param>
    /// <param name="seed">The initial rows to insert, keyed by entity name.</param>
    protected abstract Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed);

    /// <summary>A percent sign in a prefix matches a percent sign, not "anything".</summary>
    [Fact]
    public async Task A_percent_in_a_starts_with_value_matches_only_itself()
        => (await CodesAsync(Code(AlvoFilterOperator.StartsWith, "50%"))).ShouldBe(["50%_off"], ignoreOrder: true);

    /// <summary>An underscore matches an underscore, not "any one character".</summary>
    [Fact]
    public async Task An_underscore_in_a_contains_value_matches_only_itself()
        => (await CodesAsync(Code(AlvoFilterOperator.Contains, "_"))).ShouldBe(["50%_off", "a_b"], ignoreOrder: true);

    /// <summary>A backslash — the escape character itself — matches a backslash.</summary>
    [Fact]
    public async Task A_backslash_in_an_ends_with_value_matches_only_itself()
        => (await CodesAsync(Code(AlvoFilterOperator.EndsWith, "\\tmp"))).ShouldBe(["C:\\tmp"], ignoreOrder: true);

    /// <summary>A bracket matches a bracket, on every engine — never a character class.</summary>
    [Fact]
    public async Task A_bracket_in_a_contains_value_matches_only_itself()
        => (await CodesAsync(Code(AlvoFilterOperator.Contains, "[x]"))).ShouldBe(["[x]y"], ignoreOrder: true);

    /// <summary>The tests are case-sensitive and ordinal, as the C# they translate from.</summary>
    [Fact]
    public async Task A_literal_text_test_is_case_sensitive()
    {
        (await CodesAsync(Code(AlvoFilterOperator.StartsWith, "abc"))).ShouldBe(["abc"], ignoreOrder: true);
        (await CodesAsync(Code(AlvoFilterOperator.EndsWith, "BC"))).ShouldBeEmpty();
    }

    /// <summary>An empty value matches every text, and a missing value never matches — SQL's three-valued answer.</summary>
    [Fact]
    public async Task An_empty_value_matches_every_text_and_never_a_missing_one()
        => (await CodesAsync(new AlvoComparison("label", AlvoFilterOperator.StartsWith, string.Empty))).ShouldBe(["xy", "abc"], ignoreOrder: true);

    /// <summary>A value that is not a string is the port's malformed-filter refusal on every engine.</summary>
    [Fact]
    public async Task A_value_that_is_not_a_string_is_refused()
    {
        var data = await SeededAsync();

        await Should.ThrowAsync<ArgumentException>(() => data.QueryAsync(
            new AlvoQuery { Entity = Entity, Filter = Code(AlvoFilterOperator.Contains, 5L) }, Caller, TestContext.Current.CancellationToken));
    }

    private static AlvoComparison Code(AlvoFilterOperator op, object? value) => new("code", op, value);

    private async Task<IReadOnlyList<string>> CodesAsync(AlvoFilter filter)
    {
        var data = await SeededAsync();
        var page = await data.QueryAsync(new AlvoQuery { Entity = Entity, Filter = filter }, Caller, TestContext.Current.CancellationToken);

        return [.. page.Items.Select(row => (string)row["code"]!)];
    }

    private Task<IAlvoData> SeededAsync()
    {
        var (descriptor, schema) = Fixture();
        var rows = _codes.Select(code => new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid(),
            ["code"] = code,
            ["label"] = code is "xy" or "abc" ? code : null,
        }));

        return CreateAsync(schema, descriptor, new Dictionary<string, IReadOnlyList<AlvoRecord>>(StringComparer.Ordinal) { [Entity] = [.. rows] });
    }

    private static (AlvoDescriptor Descriptor, SchemaModel Schema) Fixture()
    {
        var descriptor = new AlvoDescriptor
        {
            ApiVersion = "alvo.dev/v1",
            Name = "literal-text-fixture",
            Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
            {
                [Entity] = new EntityDescriptor
                {
                    Tenancy = EntityTenancy.Global,
                    Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                    {
                        ["code"] = new() { Type = DescField.String, Required = true, MaxLength = 40 },
                        ["label"] = new() { Type = DescField.String, MaxLength = 40 },
                    },
                    Rules = new AccessRules { List = "true", Get = "true" },
                },
            },
        };

        var schema = new SchemaModel([
            new EntitySchema
            {
                Name = Entity,
                Tenancy = TenancyMode.Global,
                Fields =
                [
                    new FieldSchema { Name = "id", Type = SchemaField.Uuid, Required = true },
                    new FieldSchema { Name = "code", Type = SchemaField.String, Required = true, MaxLength = 40 },
                    new FieldSchema { Name = "label", Type = SchemaField.String, Nullable = true, MaxLength = 40 },
                ],
            },
        ]);

        return (descriptor, schema);
    }

    private static AlvoContext Caller => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = null,
    };
}
```

`test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataLiteralTextTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of the literal text tests — the engine whose <c>LIKE</c> folds case unless its driver says otherwise.</summary>
public sealed class SqliteAlvoDataLiteralTextTests : AlvoDataLiteralTextTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var host = await _fixture.StartAsync(schema, descriptor);
        await AlvoDataSeed.SeedAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), seed, TestContext.Current.CancellationToken);
        return host.Data;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataLiteralTextTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>PostgreSQL's leg of the literal text tests — the reference for case and escaping.</summary>
public sealed class PostgreSqlAlvoDataLiteralTextTests : AlvoDataLiteralTextTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var host = await _fixture.StartAsync(schema, descriptor);
        await AlvoDataSeed.SeedAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), seed, TestContext.Current.CancellationToken);
        return host.Data;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

`test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataLiteralTextTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary>The in-memory reference's leg of the literal text tests: ordinal string tests, the answer the engines are held to.</summary>
public sealed class InMemoryAlvoDataLiteralTextTests : AlvoDataLiteralTextTests
{
    protected override Task<IAlvoData> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, MMLib.Alvo.Tests.Expressions.CelFixtures.Compiler);
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent(descriptor.Name, catalog);

        var data = new InMemoryAlvoData(new PolicyEngine(provider), new PredicateEvaluator(), schema);
        foreach (var (entity, rows) in seed)
        {
            data.Seed(entity, [.. rows]);
        }

        return Task.FromResult<IAlvoData>(data);
    }
}
```

- [ ] **Step 2: Run the three legs**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*InMemoryAlvoDataLiteralTextTests'`, `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteAlvoDataLiteralTextTests'`, `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlAlvoDataLiteralTextTests'` (Docker required; the facts skip when it is unavailable)
Expected: PASS, seven facts per leg. A SQLite failure on `A_literal_text_test_is_case_sensitive` means `case_sensitive_like` is not on for the connection that ran it — fix the driver, not the row.

- [ ] **Step 3: Accept the Testing baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TestingLibraryPublicApiTests'`
Expected: FAIL with a received file adding exactly `AlvoDataLiteralTextTests` (its constructor, the abstract `CreateAsync` and seven public facts). Accept it.

- [ ] **Step 4: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Testing/Data/AlvoDataLiteralTextTests.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataLiteralTextTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataLiteralTextTests.cs test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataLiteralTextTests.cs
scripts/test-ring1
git add src/MMLib.Alvo.Testing/Data/AlvoDataLiteralTextTests.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataLiteralTextTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataLiteralTextTests.cs test/MMLib.Alvo.Tests/Data/InMemoryAlvoDataLiteralTextTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "test(data): literal text operator contract rows on every engine

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 4: Typed queries' text tests translate to the literal operators **[slice end]**

**Files:**
- Modify: `src/MMLib.Alvo/Data/Internal/FilterTranslator.Calls.cs`, `src/MMLib.Alvo/Data/AlvoTypedReadExtensions.cs` (remarks)
- Modify (tests): `test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs`, `test/_shared/testhost/TypedLayerFacts.cs`

**Interfaces:**
- Consumes: Task 2's operators; HX-05's `FilterTranslator.Field`, `Value`, `Refuse`, `LambdaShape.Touches`, `LambdaValues.Evaluate`.
- Produces: `x.Text.StartsWith(v)` / `EndsWith(v)` / `Contains(v)` (one-argument, or with `StringComparison.Ordinal`) → `AlvoComparison(field, StartsWith|EndsWith|Contains, v)`; every other text-test form refused with `AlvoTranslationException`.

- [ ] **Step 1: Write the failing tests**

In `test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs`:

1. In `Translated()`, add `var wildcard = "50%_";` beside `var note = "it's";`, and these rows before `"or chain"`:

```csharp
            { "starts with", x => x.Note.StartsWith("ab"), "note startswith 'ab'" },
            { "ordinal ends with", x => x.Note.EndsWith("b", StringComparison.Ordinal), "note endswith 'b'" },
            { "contains a captured wildcard", x => x.Note.Contains(wildcard), "note contains '50%_'" },
            { "text test on a nullable text", x => x.Code!.StartsWith("C-"), "code startswith 'C-'" },
```

2. In `Refused()`, replace the rows `"text operator"` and `"text contains"` with:

```csharp
            { "culture-folding comparison", x => x.Note.StartsWith("ab", StringComparison.OrdinalIgnoreCase), "translates only in its one-argument form or with StringComparison.Ordinal" },
            { "char overload", x => x.Note.StartsWith('a'), "translates only in its one-argument form or with StringComparison.Ordinal" },
            { "text against a field", x => x.Note.EndsWith(x.Code!), "it compares a field with a field" },
            { "null text", x => x.Note.Contains(missingText!), "the text is null" },
```

and add `string? missingText = null;` beside `string[]? missing = null;`.

In `test/_shared/testhost/TypedLayerFacts.cs`, add `using System.Linq.Expressions;` and, after `No_captured_string_reaches_the_sql_text`:

```csharp
    [Fact]
    public async Task A_captured_wildcard_in_a_text_test_matches_literally()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var agent = Agent(Guid.NewGuid());
        foreach (var reference in new[] { "50%_off", "50XYoff", "C:\\tmp", "C:Xtmp", "[x]y", "xy" })
        {
            await CreateAsync(alvo, agent, reference, "draft");
        }

        var percent = "50%";
        var underscore = "_";
        var backslash = "\\tmp";
        var bracket = "[x]";

        (await ReferencesAsync(alvo, agent, x => x.Reference.StartsWith(percent))).ShouldBe(["50%_off"]);
        (await ReferencesAsync(alvo, agent, x => x.Reference.Contains(underscore))).ShouldBe(["50%_off"]);
        (await ReferencesAsync(alvo, agent, x => x.Reference.EndsWith(backslash, StringComparison.Ordinal))).ShouldBe(["C:\\tmp"]);
        (await ReferencesAsync(alvo, agent, x => x.Reference.Contains(bracket))).ShouldBe(["[x]y"]);
    }

    [Fact]
    public async Task No_captured_text_test_value_reaches_the_sql_text()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, _ => { });
        var agent = Agent(Guid.NewGuid());
        using var capture = new SqlCapture(database.Marker);
        var baseline = await TextTestStatementsAsync(alvo, agent, capture, "benign");
        var sampled = 0;

        await _hostile.SampleAsync(
            async captured =>
            {
                (await TextTestStatementsAsync(alvo, agent, capture, captured)).ShouldBe(baseline, $"captured {captured}");
                Interlocked.Increment(ref sampled);
            },
            iter: 200,
            threads: 1);

        baseline.ShouldNotBeEmpty();
        sampled.ShouldBe(200);
    }
```

and, beside `StatementsAsync`:

```csharp
    private static async Task<IReadOnlyList<string>> TextTestStatementsAsync(AlvoTestHost alvo, AlvoContext agent, SqlCapture capture, string captured)
    {
        capture.Clear();
        await alvo.Data.QueryAsync<DeskOrder>(x => x.Reference.StartsWith(captured) || x.Reference.Contains(captured), 10, agent, Ct);

        return capture.Statements;
    }

    private static async Task<IReadOnlyList<string>> ReferencesAsync(AlvoTestHost alvo, AlvoContext agent, Expression<Func<DeskOrder, bool>> where) =>
        [.. (await alvo.Data.QueryAsync(where, 10, agent, Ct)).Select(order => order.Reference)];
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FilterTranslationTests'`
Expected: FAIL — the four new `Translated` rows are refused with "'StartsWith' has no filter operator yet", and the new `Refused` rows' reasons do not match.

- [ ] **Step 3: Translate the text tests**

Replace `src/MMLib.Alvo/Data/Internal/FilterTranslator.Calls.cs`'s summary, its first two constants, `_textTests` and `Call` with:

```csharp
/// <summary>
/// The calls a typed query may make: a list's <c>Contains</c> of a field, as <c>in</c> (an empty list matches nothing), and
/// a text field's <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> of a value, as the literal text operators — in their
/// one-argument form or with <see cref="StringComparison.Ordinal"/>, because a filter compares characters exactly.
/// </summary>
internal sealed partial class FilterTranslator
{
    private const string TextFix = "pass the text alone, or StringComparison.Ordinal; for any other comparison, filter the rows after reading them.";
    private const string ListFix = "copy the values into an array or a List<T>.";

    private static readonly FrozenDictionary<string, AlvoFilterOperator> _textTests = new Dictionary<string, AlvoFilterOperator>
    {
        [nameof(string.StartsWith)] = AlvoFilterOperator.StartsWith,
        [nameof(string.EndsWith)] = AlvoFilterOperator.EndsWith,
        [nameof(string.Contains)] = AlvoFilterOperator.Contains,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private AlvoFilter Call(MethodCallExpression call)
    {
        if (call.Object?.Type == typeof(string) && _textTests.TryGetValue(call.Method.Name, out var op))
        {
            return TextTest(call, op);
        }

        if (!LambdaShape.IsContains(call, out var source, out var value))
        {
            throw Refuse(call, LambdaShape.IsClock(call) ? "a query has no clock" : $"method '{call.Method.Name}' has no filter equivalent");
        }

        return !LambdaShape.Touches(source, _row) && value.Type != typeof(object)
            ? Membership(call, source, value)
            : throw Refuse(call, "Contains translates only as a list of values containing a field");
    }

    private AlvoComparison TextTest(MethodCallExpression call, AlvoFilterOperator op)
    {
        if (!IsOrdinalText(call))
        {
            throw Refuse(call, $"'{call.Method.Name}' translates only in its one-argument form or with StringComparison.Ordinal: a filter compares characters exactly", TextFix);
        }

        var argument = call.Arguments[0];
        if (LambdaShape.Touches(argument, _row))
        {
            throw Refuse(call, "it compares a field with a field", "compare the field with a value.");
        }

        var text = Value(argument, enumType: null) as string
            ?? throw Refuse(argument, "the text is null, and a text test never matches a missing value", "test for null with == null.");

        return new AlvoComparison(Field(call.Object!), op, text);
    }

    private static bool IsOrdinalText(MethodCallExpression call)
    {
        var parameters = call.Method.GetParameters();
        if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
        {
            return false;
        }

        return parameters.Length == 1
            || (parameters.Length == 2 && parameters[1].ParameterType == typeof(StringComparison) && LambdaValues.Evaluate(call.Arguments[1]) is StringComparison.Ordinal);
    }
```

(Keep `Membership`, `Candidates` and the rest of the file as they are; delete the `NoTextOperatorFix` constant, which nothing uses now.)

In `src/MMLib.Alvo/Data/AlvoTypedReadExtensions.cs`, in the class remarks, after "and <c>Contains</c> over a list of values" add: ", and a text field's <c>StartsWith</c>, <c>EndsWith</c> and <c>Contains</c> of a value — one-argument or <see cref=\"StringComparison.Ordinal\"/> — which match the value literally, so a <c>%</c> or <c>_</c> in it matches itself".

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*FilterTranslationTests' --filter-class '*TypedReadTests'`, `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteTypedLayerTests'`, `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlTypedLayerTests'`
Expected: PASS, including `A_captured_wildcard_in_a_text_test_matches_literally` and `No_captured_text_test_value_reaches_the_sql_text` on both engines (acceptance (11), typed half).

- [ ] **Step 5: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Data/Internal/FilterTranslator.Calls.cs src/MMLib.Alvo/Data/AlvoTypedReadExtensions.cs test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs test/_shared/testhost/TypedLayerFacts.cs
scripts/test-ring1
git add src/MMLib.Alvo/Data/Internal/FilterTranslator.Calls.cs src/MMLib.Alvo/Data/AlvoTypedReadExtensions.cs test/MMLib.Alvo.Tests/Data/FilterTranslationTests.cs test/_shared/testhost/TypedLayerFacts.cs
git commit -m "feat(typed): StartsWith, EndsWith and Contains in typed queries match literally

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 5: One custom-event envelope, and the autocommit publish refused inside a unit

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/AlvoCustomEnvelope.cs`
- Modify: `src/MMLib.Alvo/Events/Internal/AlvoEvents.cs`, `src/MMLib.Alvo.Abstractions/Events/IAlvoEvents.cs` (remarks, exception doc)
- Create (tests): `test/MMLib.Alvo.Tests/Events/AlvoEventsUnitTests.cs`

**Interfaces:**
- Consumes: Task 1's `AlvoFlow.RefuseInsideUnit`, `FakeFlowUnit`; `AlvoEventName.EnsureCustom`, `AlvoEventJson.Write`, `AlvoEventId.Create`, `AlvoEnvelopeStamp.Of`, `AlvoCustomEvent.Create`.
- Produces: `internal static class AlvoCustomEnvelope { internal static void Ensure(string type, string subject, IReadOnlyDictionary<string, object?>? data); internal static AlvoCustomEvent Build(string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context, DateTimeOffset now); }` (Abstractions, `MMLib.Alvo.Events`) — Task 8's transactional publish builds through it.

- [ ] **Step 1: Write the failing test**

`test/MMLib.Alvo.Tests/Events/AlvoEventsUnitTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Tests.Data;
using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary><c>IAlvoEvents.PublishAsync</c> stays autocommit (spec 2026-10-14 X13), so inside an open unit it is refused (R-B1).</summary>
public sealed class AlvoEventsUnitTests
{
    private static readonly AlvoContext _caller = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };

    [Fact]
    public async Task Publishing_inside_an_open_unit_is_refused_and_fails_the_unit()
    {
        var outbox = Substitute.For<IOutboxStore>();
        var events = new AlvoEvents(outbox, TimeProvider.System, AlvoChainDepthLimit.Default);
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(() => events.PublishAsync(
            "orders.approved", "orders/1", null, _caller, TestContext.Current.CancellationToken));

        refusal.Message.ShouldStartWith("IAlvoEvents.PublishAsync was called while a transaction unit is open");
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
        await outbox.DidNotReceiveWithAnyArgs().AppendAsync(default!, default);
    }

    [Fact]
    public async Task A_malformed_name_is_still_refused_first_inside_a_unit()
    {
        var events = new AlvoEvents(Substitute.For<IOutboxStore>(), TimeProvider.System, AlvoChainDepthLimit.Default);
        var unit = new FakeFlowUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        await Should.ThrowAsync<ArgumentException>(() => events.PublishAsync(
            "entity.orders.created", "orders/1", null, _caller, TestContext.Current.CancellationToken));
        unit.Poisoned.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_publish_outside_any_unit_appends_one_envelope_built_by_the_shared_builder()
    {
        var outbox = Substitute.For<IOutboxStore>();
        var events = new AlvoEvents(outbox, TimeProvider.System, AlvoChainDepthLimit.Default);

        await events.PublishAsync("orders.approved", "orders/1", new Dictionary<string, object?> { ["total"] = 12.5m }, _caller, TestContext.Current.CancellationToken);

        await outbox.Received(1).AppendAsync(
            Arg.Is<AlvoCustomEvent>(custom => custom.Envelope.Type == "orders.approved" && custom.Envelope.PartitionKey == "custom.event:orders/1"),
            Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoEventsUnitTests'`
Expected: FAIL — `Publishing_inside_an_open_unit_is_refused_and_fails_the_unit` sees the event appended and no exception.

- [ ] **Step 3: Move the envelope into Abstractions and refuse inside a unit**

`src/MMLib.Alvo.Abstractions/Events/AlvoCustomEnvelope.cs` — the guards and builder moved verbatim from `AlvoEvents` (`EnsureWritable`, `Probe`, `ProbeType`, `ProbeSubject`, `PartitionKeyFor`, `CustomPartitionMarker` with **their XML docs unchanged**, and the body of `Envelope`), so the transactional publish and the autocommit one build one envelope:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events;

/// <summary>
/// The one builder of a custom application event's envelope: the name and payload guards, then the envelope through the
/// same authorities a data event's is built by.
/// </summary>
/// <remarks>
/// Internal and in Abstractions because two publishers build it: the core's autocommit <c>IAlvoEvents</c>, and the EF
/// driver's transactional <c>IAlvoTransaction.PublishAsync</c>. One builder, so the two cannot drift.
/// </remarks>
internal static class AlvoCustomEnvelope
{
    /// <summary>Refuses a reserved or malformed name, a blank subject, or a payload the envelope cannot express — before anything is minted.</summary>
    /// <param name="type">The event's name.</param>
    /// <param name="subject">What the event is about.</param>
    /// <param name="data">The payload, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">The name, subject or payload is refused.</exception>
    internal static void Ensure(string type, string subject, IReadOnlyDictionary<string, object?>? data)
    {
        AlvoEventName.EnsureCustom(type, nameof(type));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        EnsureWritable(data);
    }

    /// <summary>The envelope one checked event becomes, at <paramref name="now"/>.</summary>
    /// <param name="type">The guarded event name.</param>
    /// <param name="subject">What the event is about, and therefore its partition key.</param>
    /// <param name="data">The payload, or <see langword="null"/>.</param>
    /// <param name="context">The caller, as provenance.</param>
    /// <param name="now">The event's instant, from the publisher's clock.</param>
    /// <returns>The guarded custom event.</returns>
    internal static AlvoCustomEvent Build(
        string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = AlvoEventId.Create(now);
        var stamp = AlvoEnvelopeStamp.Of(context, id, context.Tenant);

        return AlvoCustomEvent.Create(stamp.Onto(new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = now,
            Subject = subject,
            PartitionKey = PartitionKeyFor(subject),
            AuthType = stamp.AuthType,
            CorrelationId = stamp.CorrelationId,
            Data = new AlvoEventData { Record = data is null ? null : new AlvoRecord(data) },
        }));
    }


    /// <summary>Refuses a payload value the envelope cannot express, here rather than at serialization.</summary>
    /// <param name="data">The payload the host supplied.</param>
    /// <remarks>
    /// <para>
    /// <b>The declared parameter type is wider than the wire format, so the gap is closed by a refusal at the
    /// call.</b> <c>IReadOnlyDictionary&lt;string, object?&gt;</c> accepts a nested dictionary, an array or a
    /// <c>JsonElement</c> — the obvious things to relay from inbound JSON — and
    /// <see cref="AlvoEventJson"/> refuses all of them. Without this, that refusal surfaced from deep inside
    /// <see cref="IOutboxStore.AppendAsync"/> as a <see cref="NotSupportedException"/> advising the caller to
    /// use "one of the field types the schema allows", which is meaningless for a custom event that has no
    /// schema. Found by review; the path had no test because nothing round-tripped a payload.
    /// </para>
    /// <para>
    /// <b>It writes the payload rather than pattern-matching a type list</b>, so this cannot drift from what
    /// <see cref="AlvoEventJson"/> really accepts — a second copy of that list is how one comes to admit a
    /// type the other refuses. The cost is one throwaway serialization per publish, on a path that is already
    /// about to serialize the same payload for real.
    /// </para>
    /// </remarks>
    private static void EnsureWritable(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null)
        {
            return;
        }

        try
        {
            _ = AlvoEventJson.Write(Probe(data));
        }
        catch (NotSupportedException failure)
        {
            throw new ArgumentException(
                "This event's payload carries a value the envelope cannot express. An event payload is a flat "
                + "record of scalars — text, boolean, uuid, a number, or a date/timestamp — so a nested "
                + "dictionary, an array or a JsonElement has nothing to become on the wire. Flatten it, or "
                + $"serialize that value to a string yourself. {failure.Message}",
                nameof(data),
                failure);
        }
    }

    /// <summary>The cheapest envelope that carries <paramref name="data"/> through the real writer.</summary>
    /// <param name="data">The payload being checked.</param>
    private static AlvoEvent Probe(IReadOnlyDictionary<string, object?> data) => new()
    {
        Id = Guid.Empty,
        Source = AlvoEvent.DefaultSource,
        Type = ProbeType,
        Time = DateTimeOffset.UnixEpoch,
        Subject = ProbeSubject,
        PartitionKey = ProbeSubject,
        AuthType = AlvoEventAuthType.Unauthenticated,
        CorrelationId = string.Empty,
        Data = new AlvoEventData { Record = new AlvoRecord(data) },
    };

    private const string ProbeType = "alvo.payload.probe";
    private const string ProbeSubject = "probe";

    /// <summary>
    /// The partition a custom event orders within: its type and its subject, joined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fixed marker, not the event's type — because the type broke the ordering this API promises.</b>
    /// The first draft used <c>{type}:{subject}</c>, which put <c>orders.approved</c> and
    /// <c>orders.shipped</c> for one <c>orders/42</c> into <em>different</em> partitions while
    /// <see cref="IAlvoEvents"/> promised ordering per subject. Caught in review. A fixed marker satisfies
    /// both: every event about one subject shares a key, and the marker's dot keeps the key out of any data
    /// entity's partition.
    /// </para>
    /// <para>
    /// <b>Why staying out of a data entity's partition matters.</b> A data
    /// event's key is <c>{entity}:{rowId}</c>, and <c>OutboxTable</c>'s <c>partition_key</c> column exists for
    /// F7's partitioned claim (<b>#150</b>) to index — so a host publishing <c>subject: "deals:&lt;guid&gt;"</c>
    /// would, on the day that claim reads the column, be ordering itself into a real entity's partition. That
    /// is the same "meaning silently widens when the feature lands, with nobody re-reading the artifact"
    /// hazard the wildcard refusal exists for, and it is closed by shape rather than by a warning.
    /// </para>
    /// <para>
    /// <b>Enforced at the door, not only produced here.</b> <see cref="AlvoCustomEvent.Create"/> refuses an
    /// envelope whose partition key's first segment carries no dot, so a host appending through
    /// <see cref="IOutboxStore.AppendAsync"/> directly cannot claim a data entity's partition either. This
    /// method is where a well-formed key is <em>built</em>; that check is where the guarantee <em>holds</em>.
    /// </para>
    /// <para>
    /// <b>The disjointness is provable, not probable.</b> An entity name is
    /// <c>^[a-z][a-z0-9_]{0,62}$</c> (<c>schema/project.schema.json</c>, <c>entities</c>' <c>propertyNames</c>)
    /// and so contains no dot; <see cref="AlvoEventName"/> requires a custom type to contain <em>at least
    /// one</em>. So the two key spaces cannot collide, whatever subject a host supplies.
    /// </para>
    /// <para>
    /// Ordering is still per subject, because the type is fixed for a given subject's stream — under the same
    /// one-dispatcher, one-millisecond conditions as everything else on this queue.
    /// </para>
    /// </remarks>
    /// <param name="subject">What the event is about, in the host's own vocabulary.</param>
    private static string PartitionKeyFor(string subject) => $"{CustomPartitionMarker}:{subject}";

    /// <summary>
    /// The fixed first segment of every custom event's partition key.
    /// </summary>
    /// <remarks>
    /// <b>It contains a dot, and that is the entire mechanism.</b> An entity name is
    /// <c>^[a-z][a-z0-9_]{0,62}$</c> (<c>schema/project.schema.json</c>, <c>entities</c>'
    /// <c>propertyNames</c>) and therefore never contains one, so no custom event's key can equal a data
    /// event's <c>{entity}:{rowId}</c> — whatever subject a host supplies.
    /// </remarks>
    private const string CustomPartitionMarker = "custom.event";
}
```

(The six members after `Build` are `AlvoEvents.cs` lines 47–156 as they stand on `hx/06-delivery-operations`, moved unchanged.)

`src/MMLib.Alvo/Events/Internal/AlvoEvents.cs` — delete the six moved members and `Envelope`, and replace `PublishAsync` with:

```csharp
    /// <inheritdoc/>
    public Task PublishAsync(
        string type,
        string subject,
        IReadOnlyDictionary<string, object?>? data,
        AlvoContext context,
        CancellationToken cancellationToken = default)
    {
        AlvoCustomEnvelope.Ensure(type, subject, data);
        ArgumentNullException.ThrowIfNull(context);
        AlvoFlow.RefuseInsideUnit("IAlvoEvents.PublishAsync");
        depthLimit.EnsureWithin(context);

        return outbox.AppendAsync(AlvoCustomEnvelope.Build(type, subject, data, context, clock.GetUtcNow()), cancellationToken);
    }
```

and in the class remarks' first paragraph replace "The name guard runs first, then the depth refusal, then the clock." with "The name guard runs first, then the refusal inside a transaction unit, then the depth refusal, then the clock." and replace the second paragraph's first sentence with "The envelope is built by <see cref=\"AlvoCustomEnvelope\"/>, the one builder the transactional publish shares —". (Keep the words `and`/`or`/`not` lower-case in this file: the SQL text scan.)

`src/MMLib.Alvo.Abstractions/Events/IAlvoEvents.cs` — replace the remarks paragraph that begins "<b>Not transactional with anything.</b>" with:

```csharp
/// <para>
/// <b>Autocommit.</b> This appends with one statement of its own, so it is not atomic with any write. A host whose own
/// writes and event must commit together publishes through the unit instead — <c>IAlvoTransaction.PublishAsync</c> inside
/// <c>IAlvoTransactions.TransactAsync</c> — and calling this method while a unit is open on the same flow is refused with
/// <see cref="Data.AlvoUnitInProgressException"/>.
/// </para>
```

and add to `PublishAsync`'s exceptions:

```csharp
    /// <exception cref="Data.AlvoUnitInProgressException">A transaction unit is open on this flow; publish through its <c>IAlvoTransaction</c>.</exception>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoEventsUnitTests' --filter-class '*AlvoEventsTests' --filter-class '*SqlTextConfinedToRendererArchitectureTests'`
Expected: PASS — the existing `AlvoEventsTests` unchanged (messages and order of refusals are the same).

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Events/AlvoCustomEnvelope.cs src/MMLib.Alvo/Events/Internal/AlvoEvents.cs src/MMLib.Alvo.Abstractions/Events/IAlvoEvents.cs test/MMLib.Alvo.Tests/Events/AlvoEventsUnitTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Abstractions/Events/AlvoCustomEnvelope.cs src/MMLib.Alvo/Events/Internal/AlvoEvents.cs src/MMLib.Alvo.Abstractions/Events/IAlvoEvents.cs test/MMLib.Alvo.Tests/Events/AlvoEventsUnitTests.cs
git commit -m "feat(events): one custom envelope builder; the autocommit publish is refused inside a unit

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 6: `WriteUnit` — one connection, one transaction, one turn, one scope, a deadline and a poisoned flag

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (one word: `partial`)
- Modify (tests): `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/MMLib.Alvo.Data.EntityFrameworkCore.Tests.csproj`
- Create (tests): `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WriteUnitTests.cs`

**Interfaces:**
- Consumes: Task 1's `IAlvoFlowUnit`, `AlvoTransactionOptions`; `EfAlvoData`'s private `_contexts`, `_time`, `_depthLimit`, `EnsureOutboxTableAsync`, `WriteTurnAsync`; `AlvoEventProvenance.Effective`/`CorrelationIdOf`.
- Produces: `internal sealed record WriteUnitParts(AlvoDataContext Root, DbConnection Connection, IDbContextTransaction Transaction, IDisposable Turn, AsyncServiceScope Scope)`; `internal sealed class WriteUnit : IAlvoFlowUnit, IAsyncDisposable` with `DbConnection Connection`, `DbTransaction Transaction`, `AlvoContext Caller`, `AlvoProvenance Provenance`, `IServiceProvider Services`, `IBeforeHookRunner Hooks`, `CancellationToken Token`, `bool TimedOut`, `bool HasEnded`, `bool IsOpen`, `string NextSavepoint()`, `Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)`, `Task CommitAsync(CancellationToken)`, `TimeoutException TimedOutBecause(Exception? cause)`, `void Poison(Exception)`, `ValueTask DisposeAsync()`; `EfAlvoData` becomes `internal sealed partial class`, with `internal Task<WriteUnit> OpenUnitAsync(AlvoContext context, AlvoTransactionOptions options, IServiceScopeFactory scopes, CancellationToken cancellationToken)`; `WritePathWorld.StartAsync(descriptor, schema, Action<IServiceCollection>? services = null, TimeProvider? time = null)`, `WritePathWorld.Scopes`.

- [ ] **Step 1: Give the test world services and a clock**

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/MMLib.Alvo.Data.EntityFrameworkCore.Tests.csproj` — beside `CsCheck`, add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` (already in `Directory.Packages.props`: the TestHost uses it).

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs` — replace `StartAsync`, `BuildAsync` and `Port` with the versions below, add the `_time` field and the `Scopes` property:

```csharp
    private readonly TimeProvider _time;

    private WritePathWorld(SqliteConnection connection, ServiceProvider services, TimeProvider time)
    {
        _connection = connection;
        _services = services;
        _time = time;
    }

    /// <summary>The scope factory of this world's container, which a unit opens its scope from.</summary>
    internal IServiceScopeFactory Scopes => _services.GetRequiredService<IServiceScopeFactory>();

    /// <summary>Boots a world over <paramref name="schema"/>, with <paramref name="descriptor"/>'s rules.</summary>
    /// <param name="descriptor">The descriptor whose rules the policy engine is primed with.</param>
    /// <param name="schema">The applied schema the read model and the write path are built from.</param>
    /// <param name="services">Registrations a test adds after <c>AddAlvo</c>, such as a scoped probe.</param>
    /// <param name="time">The port's clock, which a unit's deadline also runs on; the system clock unless set.</param>
    internal static async Task<WritePathWorld> StartAsync(
        AlvoDescriptor descriptor, SchemaModel schema, Action<IServiceCollection>? services = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);

        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var collection = new ServiceCollection().AddAlvo().Services;
        services?.Invoke(collection);

        return await BuildAsync(descriptor, schema, connection, collection.BuildServiceProvider(), time ?? TimeProvider.System);
    }

    private static async Task<WritePathWorld> BuildAsync(
        AlvoDescriptor descriptor, SchemaModel schema, SqliteConnection connection, ServiceProvider services, TimeProvider time)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, services.GetRequiredService<ICelCompiler>());
        services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(descriptor.Name, catalog);

        var world = new WritePathWorld(connection, services, time);
        var contexts = new AlvoDataContextFactory(
            new FixedSchemaRegistry(schema),
            options => options
                .UseSqlite(connection, sqlite => sqlite.UseRelationalNulls())
                .AddInterceptors(world.Statements));

        await CreateTablesAsync(contexts);
        world._contexts = contexts;
        world.Data = Port(services, world, contexts);

        return world;
    }

    private static EfAlvoData Port(ServiceProvider services, WritePathWorld world, AlvoDataContextFactory contexts) =>
        new(
            services.GetRequiredService<IPolicyEngine>(),
            services.GetRequiredService<IPredicateEvaluator>(),
            world.Hooks,
            services.GetRequiredService<IPredicateRenderer>(),
            new TestFieldSqlRenderer(),
            new SqliteTestDialect(),
            contexts,
            world._time,
            new AlvoOptions(),
            AlvoChainDepthLimit.Default);
```

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs` — add `"EfAlvoData.Units.cs",` to `_sqlComposingFiles` after `"EfAlvoData.cs",` (it reads the unit's connection with `GetDbConnection` and, in Task 8, writes the unit's outbox row).

- [ ] **Step 2: Write the failing tests**

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WriteUnitTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// One transaction unit's life (spec 2026-10-14 §4.5): its provenance, one call at a time, poisoning, its deadline, its
/// scope, and what is refused after it ends — over SQLite in memory, with no write inside it yet.
/// </summary>
public sealed class WriteUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_unit_reports_its_caller_s_depth_and_one_correlation_id_of_its_own()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { Depth = 3 } };

        await using var unit = await world.Data.OpenUnitAsync(caller, new AlvoTransactionOptions(), world.Scopes, Ct);

        unit.Provenance.Depth.ShouldBe(3);
        unit.Provenance.CorrelationId.ShouldNotBeNullOrWhiteSpace();
        unit.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task A_unit_keeps_the_correlation_id_its_caller_carries()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { CorrelationId = "request-7" } };

        await using var unit = await world.Data.OpenUnitAsync(caller, new AlvoTransactionOptions(), world.Scopes, Ct);

        unit.Provenance.CorrelationId.ShouldBe("request-7");
    }

    [Fact]
    public async Task A_caller_past_the_depth_limit_opens_no_unit()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { Depth = AlvoChainDepthLimit.DefaultMaxDepth + 1 } };

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => world.Data.OpenUnitAsync(caller, new AlvoTransactionOptions(), world.Scopes, Ct));
    }

    [Fact]
    public async Task A_committed_unit_has_ended_and_is_off_the_flow_once_released()
    {
        await using var world = await StartAsync();
        var unit = await OpenAsync(world);

        (await unit.RunAsync(_ => Task.FromResult(7), Ct)).ShouldBe(7);
        await unit.CommitAsync(Ct);

        unit.HasEnded.ShouldBeTrue();
        unit.IsOpen.ShouldBeTrue("the TransactAsync that opened it has not returned yet");
        await unit.DisposeAsync();
        unit.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task A_poisoned_unit_refuses_further_calls_and_its_commit_throws_the_original_failure()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        var original = new AlvoUnitInProgressException("refused");

        unit.Poison(original);

        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(1), Ct))).InnerException.ShouldBeSameAs(original);
        (await Should.ThrowAsync<AlvoUnitInProgressException>(() => unit.CommitAsync(Ct))).ShouldBeSameAs(original);
        unit.HasEnded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_call_while_one_runs_is_refused_and_poisons_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = unit.RunAsync(_ => release.Task, Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(2), Ct)))
            .Message.ShouldContain("one call at a time");
        release.SetResult(1);

        (await first).ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(() => unit.CommitAsync(Ct));
    }

    [Fact]
    public async Task The_deadline_cancels_a_running_call_and_rolls_the_unit_back_without_waiting_for_its_caller()
    {
        var clock = new FakeTimeProvider();
        await using var world = await StartAsync(time: clock);
        await using var unit = await OpenAsync(world, new AlvoTransactionOptions { Timeout = TimeSpan.FromSeconds(1) });
        var running = unit.RunAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            Ct);

        clock.Advance(TimeSpan.FromSeconds(2));

        (await Should.ThrowAsync<TimeoutException>(() => running)).Message.ShouldContain("Timeout of 1 s");
        await EventuallyAsync(() => unit.HasEnded);
        unit.TimedOut.ShouldBeTrue();
        await Should.ThrowAsync<TimeoutException>(() => unit.RunAsync(_ => Task.FromResult(1), Ct));
        await Should.ThrowAsync<TimeoutException>(() => unit.CommitAsync(Ct));
    }

    [Fact]
    public async Task A_unit_used_after_it_was_released_is_refused()
    {
        await using var world = await StartAsync();
        var unit = await OpenAsync(world);
        await unit.DisposeAsync();

        (await Should.ThrowAsync<InvalidOperationException>(() => unit.RunAsync(_ => Task.FromResult(1), Ct)))
            .Message.ShouldContain("has ended");
    }

    [Fact]
    public async Task A_cancelled_call_poisons_the_unit_with_its_cancellation()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);
        using var caller = new CancellationTokenSource();
        var running = unit.RunAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            caller.Token);

        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => running);
        await Should.ThrowAsync<OperationCanceledException>(() => unit.CommitAsync(Ct));
        unit.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chain_depth_refusal_inside_a_call_poisons_the_unit()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() =>
            unit.RunAsync<int>(_ => throw new AlvoChainDepthExceededException("loop"), Ct));

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => unit.CommitAsync(Ct));
    }

    [Fact]
    public async Task A_refused_call_that_is_not_a_poison_leaves_the_unit_usable()
    {
        await using var world = await StartAsync();
        await using var unit = await OpenAsync(world);

        await Should.ThrowAsync<AlvoAuthorizationException>(() =>
            unit.RunAsync<int>(_ => throw new AlvoAuthorizationException("no"), Ct));

        (await unit.RunAsync(_ => Task.FromResult(3), Ct)).ShouldBe(3);
        await unit.CommitAsync(Ct);
    }

    [Fact]
    public async Task A_unit_s_scope_is_one_per_unit_and_ends_with_it()
    {
        await using var world = await StartAsync(services => services.AddScoped<DisposalProbe>());
        var unit = await OpenAsync(world);
        var probe = unit.Services.GetRequiredService<DisposalProbe>();

        unit.Services.GetRequiredService<DisposalProbe>().ShouldBeSameAs(probe);
        await unit.DisposeAsync();

        probe.Disposed.ShouldBeTrue();
    }

    private static Task<WritePathWorld> StartAsync(Action<IServiceCollection>? services = null, TimeProvider? time = null) =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema(), services, time);

    private static Task<WriteUnit> OpenAsync(WritePathWorld world, AlvoTransactionOptions? options = null) =>
        world.Data.OpenUnitAsync(world.Caller, options ?? new AlvoTransactionOptions(), world.Scopes, Ct);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }

        condition().ShouldBeTrue();
    }

    private sealed class DisposalProbe : IAsyncDisposable
    {
        internal bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*WriteUnitTests'`
Expected: FAIL to compile — `WriteUnit` and `EfAlvoData.OpenUnitAsync` do not exist.

- [ ] **Step 4: Write the unit and open it**

In `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`, change `internal sealed class EfAlvoData : IAlvoData` to `internal sealed partial class EfAlvoData : IAlvoData`, and in `WriteTurnAsync`'s remarks replace the paragraph that begins "<b>A unit of several writes</b>" with: "<b>A transaction unit</b> takes the turn once when it opens (<c>OpenUnitAsync</c>), and the writes inside it run on the unit's connection under savepoints without asking again — this method is never called inside a unit."

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Rules;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>What one open unit is made of: the context that opened its connection, the connection, the transaction, the write turn and the DI scope.</summary>
/// <param name="Root">The context that opened the connection and began the transaction; disposed when the unit ends.</param>
/// <param name="Connection">The one connection every call of the unit runs on.</param>
/// <param name="Transaction">The one transaction every call of the unit joins.</param>
/// <param name="Turn">This database's write turn, held from the unit's <c>BEGIN</c> to its end.</param>
/// <param name="Scope">The unit's DI scope — not the request's.</param>
internal sealed record WriteUnitParts(
    AlvoDataContext Root, DbConnection Connection, IDbContextTransaction Transaction, IDisposable Turn, AsyncServiceScope Scope);

/// <summary>
/// One transaction unit (spec 2026-10-14 §4.5): one connection and transaction that every call joins under its own
/// savepoint, the write turn held from <c>BEGIN</c> to the end, one DI scope, a deadline, and the first failure that
/// poisons it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One call at a time.</b> A <see cref="DbConnection"/> runs one command at a time, so a second call made while one is
/// running is refused, and poisons the unit: the delegate's result can no longer be the one it meant.
/// </para>
/// <para>
/// <b>Poisoned means it cannot commit.</b> <see cref="Poison"/> records the first failure; every later call is refused, and
/// <see cref="CommitAsync"/> rolls back and throws that failure — so a delegate that catches it still cannot commit.
/// </para>
/// <para>
/// <b>The deadline ends the unit, not the delegate.</b> At <see cref="AlvoTransactionOptions.Timeout"/> the unit's token is
/// cancelled and the unit is poisoned with a <see cref="TimeoutException"/>; as soon as no call is running it rolls back
/// and lets go of the connection and the write turn — a delegate that ignores its token no longer holds the database. The
/// deadline runs on the host's <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// <b>Open until released.</b> <see cref="IsOpen"/> stays true from opening until <see cref="DisposeAsync"/> — the end of
/// <c>TransactAsync</c> — so the flow refuses other data-port calls for the delegate's whole run, even after a deadline
/// rolled the transaction back; work that outlives <c>TransactAsync</c> is not refused.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2213:Disposable fields should be disposed",
    Justification = "SemaphoreSlim holds no handle unless AvailableWaitHandle is read, which this type never does; disposing it would race a deadline still waiting for the unit's turn to roll it back.")]
internal sealed class WriteUnit : IAlvoFlowUnit, IAsyncDisposable
{
    private const string SavepointPrefix = "alvo_unit_";

    private readonly WriteUnitParts _parts;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _onDeadline;
    private readonly SemaphoreSlim _calls = new(1, 1);
    private readonly Lock _gate = new();
    private Exception? _failure;
    private bool _ended;
    private bool _released;
    private int _savepoints;

    /// <summary>Initializes an open unit over <paramref name="parts"/>.</summary>
    /// <param name="parts">The opened connection, the begun transaction, the turn and the scope.</param>
    /// <param name="caller">The caller who opened it.</param>
    /// <param name="provenance">The unit's provenance, which every write inside it reports.</param>
    /// <param name="timeout">How long the unit may run.</param>
    /// <param name="time">The clock the deadline runs on.</param>
    /// <param name="cancellationToken">The caller's token; cancelling it cancels every call of the unit.</param>
    internal WriteUnit(
        WriteUnitParts parts, AlvoContext caller, AlvoProvenance provenance, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        _parts = parts;
        Caller = caller;
        Provenance = provenance;
        _timeout = timeout;
        _deadline = new CancellationTokenSource(timeout, time);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        _onDeadline = _deadline.Token.Register(static state => ((WriteUnit)state!).OnDeadline(), this);
    }

    /// <summary>Gets the connection every call of the unit runs on.</summary>
    internal DbConnection Connection => _parts.Connection;

    /// <summary>Gets the transaction every call of the unit joins.</summary>
    internal DbTransaction Transaction => _parts.Transaction.GetDbTransaction();

    /// <summary>Gets the caller who opened the unit.</summary>
    internal AlvoContext Caller { get; }

    /// <summary>Gets the unit's provenance: the caller's effective one, with a correlation id of the unit's own when it had none.</summary>
    internal AlvoProvenance Provenance { get; }

    /// <summary>Gets the unit's DI scope, shared by everything the unit resolves.</summary>
    internal IServiceProvider Services => _parts.Scope.ServiceProvider;

    /// <summary>Gets the declarative hook pipeline a write inside the unit runs, resolved from the unit's scope.</summary>
    internal IBeforeHookRunner Hooks => Services.GetRequiredService<IBeforeHookRunner>();

    /// <summary>Gets the token every call of the unit runs under: the caller's, ended at the deadline.</summary>
    internal CancellationToken Token => _lifetime.Token;

    /// <summary>Gets a value indicating whether the unit's deadline has passed.</summary>
    internal bool TimedOut => _deadline.IsCancellationRequested;

    /// <summary>Gets a value indicating whether the unit's transaction has committed or rolled back.</summary>
    internal bool HasEnded
    {
        get
        {
            lock (_gate)
            {
                return _ended;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !_released;
            }
        }
    }

    /// <inheritdoc/>
    public void Poison(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (!_ended)
            {
                _failure ??= failure;
            }
        }
    }

    /// <summary>The next savepoint name of this unit: <c>alvo_unit_1</c>, <c>alvo_unit_2</c>, …</summary>
    internal string NextSavepoint() =>
        SavepointPrefix + Interlocked.Increment(ref _savepoints).ToString(CultureInfo.InvariantCulture);

    /// <summary>Runs one call of the unit: alone, under the unit's token, refused once the unit failed or ended.</summary>
    /// <typeparam name="T">What the call answers.</typeparam>
    /// <param name="call">The call, given the token it must pass to every statement.</param>
    /// <param name="cancellationToken">The call's own token.</param>
    /// <returns>What <paramref name="call"/> answered.</returns>
    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        EnsureUsable();
        if (!_calls.Wait(0))
        {
            var refusal = Concurrent();
            Poison(refusal);
            throw refusal;
        }

        try
        {
            EnsureUsable();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            return await GuardedAsync(call, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _calls.Release();
        }
    }

    /// <summary>Commits the unit — or, when it was poisoned, rolls it back and throws the poison.</summary>
    /// <param name="cancellationToken">A token to cancel the commit.</param>
    internal async Task CommitAsync(CancellationToken cancellationToken)
    {
        await _calls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FailureOrEnd() is { } failure)
            {
                await EndAsync(commit: false).ConfigureAwait(false);
                ExceptionDispatchInfo.Throw(failure);
            }

            await EndAsync(commit: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _calls.Release();
        }
    }

    /// <summary>The unit's timeout, as the exception every caller of a timed-out unit sees.</summary>
    /// <param name="cause">What the delegate or the call failed with at the deadline, if anything.</param>
    internal TimeoutException TimedOutBecause(Exception? cause) => new(
        $"The transaction unit ran longer than its Timeout of {_timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, so it "
        + "was cancelled and rolled back; nothing it wrote was committed. Fix: keep the work inside TransactAsync short — make any "
        + "network call before it — or raise AlvoTransactionOptions.Timeout.",
        cause);

    /// <summary>Releases the unit: rolls back what it did not commit, ends its scope, and takes it off the flow.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
        }

        await _onDeadline.DisposeAsync().ConfigureAwait(false);
        await _calls.WaitAsync().ConfigureAwait(false);
        try
        {
            await EndAsync(commit: false).ConfigureAwait(false);
        }
        finally
        {
            _calls.Release();
        }

        await _parts.Scope.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _deadline.Dispose();
    }

    private async Task<T> GuardedAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken token)
    {
        try
        {
            return await call(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancelled) when (TimedOut)
        {
            throw TimedOutBecause(cancelled);
        }
        catch (Exception failure) when (failure is OperationCanceledException or AlvoChainDepthExceededException)
        {
            Poison(failure);
            throw;
        }
    }

    private void EnsureUsable()
    {
        lock (_gate)
        {
            if (_failure is TimeoutException)
            {
                throw TimedOutBecause(null);
            }

            if (_failure is { } failure)
            {
                throw Failed(failure);
            }

            if (_ended || _released)
            {
                throw Ended();
            }
        }
    }

    private Exception? FailureOrEnd()
    {
        lock (_gate)
        {
            return _failure ?? (_ended ? Ended() : null);
        }
    }

    private async Task EndAsync(bool commit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
        }

        try
        {
            await (commit ? _parts.Transaction.CommitAsync(cancellationToken) : RollBackAsync()).ConfigureAwait(false);
        }
        finally
        {
            await _parts.Transaction.DisposeAsync().ConfigureAwait(false);
            await _parts.Root.DisposeAsync().ConfigureAwait(false);
            _parts.Turn.Dispose();
        }
    }

    private async Task RollBackAsync()
    {
        try
        {
            await _parts.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
    }

    private void OnDeadline()
    {
        Poison(TimedOutBecause(null));
        _ = AbandonAsync();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Runs unobserved off the deadline's timer; the unit's failure is already recorded and surfaces from TransactAsync.")]
    private async Task AbandonAsync()
    {
        try
        {
            await _calls.WaitAsync().ConfigureAwait(false);
            try
            {
                await EndAsync(commit: false).ConfigureAwait(false);
            }
            finally
            {
                _calls.Release();
            }
        }
        catch (Exception)
        {
            return;
        }
    }

    private static InvalidOperationException Failed(Exception failure) => new(
        $"This transaction unit has already failed with {failure.GetType().Name}, so it will roll back and accepts no more calls. "
        + "Fix: let the failure end the delegate, or make the call in a new TransactAsync.",
        failure);

    private static InvalidOperationException Ended() => new(
        "This transaction unit has ended: its TransactAsync has returned, so it committed or rolled back. Fix: make every call "
        + "through the IAlvoTransaction inside the delegate, and await it there.");

    private static InvalidOperationException Concurrent() => new(
        "Another call on this transaction is still running. A unit runs one call at a time, on one connection — it is now "
        + "failed and will roll back. Fix: await each call before making the next.");
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The data port's transaction units (spec 2026-10-14 §4.5): opening one, and — from Task 8 on — running every read,
/// write, batch and publish of <c>IAlvoTransaction</c> inside it.
/// </summary>
/// <remarks>
/// A partial file of its own because it is the one place the data port reaches a unit's connection: it is allow-listed in
/// <c>ChangeTrackerReachTests</c> beside <c>EfAlvoData.cs</c> for exactly that.
/// </remarks>
internal sealed partial class EfAlvoData
{
    /// <summary>Opens one unit as <paramref name="context"/>: the outbox ensured, the write turn taken, the transaction begun.</summary>
    /// <param name="context">The caller every call of the unit runs as.</param>
    /// <param name="options">The checked options.</param>
    /// <param name="scopes">Where the unit's DI scope comes from.</param>
    /// <param name="cancellationToken">The caller's token, which every call of the unit also honours.</param>
    /// <returns>The open unit; the caller disposes it.</returns>
    /// <exception cref="AlvoChainDepthExceededException">The caller is deeper in one causation chain than the limit allows.</exception>
    /// <remarks>
    /// The outbox table is ensured here, before <c>BEGIN</c>, so no write inside the unit ever runs DDL on its connection —
    /// which SQLite would refuse inside a pending transaction.
    /// </remarks>
    internal async Task<WriteUnit> OpenUnitAsync(
        AlvoContext context, AlvoTransactionOptions options, IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopes);
        _depthLimit.EnsureWithin(context);

        var root = _contexts.Create();
        try
        {
            await EnsureOutboxTableAsync(root, cancellationToken).ConfigureAwait(false);
            return await BegunAsync(root, context, options, scopes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await root.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<WriteUnit> BegunAsync(
        AlvoDataContext root, AlvoContext context, AlvoTransactionOptions options, IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        var turn = await WriteTurnAsync(root, cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await root.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var parts = new WriteUnitParts(root, root.Database.GetDbConnection(), transaction, turn, scopes.CreateAsyncScope());

            return new WriteUnit(parts, context, UnitProvenance(context), options.Timeout, _time, cancellationToken);
        }
        catch
        {
            turn.Dispose();
            throw;
        }
    }

    /// <summary>The caller's effective provenance — with a correlation id of the unit's own when it carries none, so every event of one unit shares one.</summary>
    private static AlvoProvenance UnitProvenance(AlvoContext context)
    {
        var effective = AlvoEventProvenance.Effective(context);

        return effective.CorrelationId is null
            ? effective with { CorrelationId = AlvoEventProvenance.CorrelationIdOf(Guid.CreateVersion7()) }
            : effective;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*WriteUnitTests' --filter-class '*ChangeTrackerReachTests' --filter-class '*EfAlvoDataEnsureOnceTests'`
Expected: PASS. `ChangeTrackerReachTests.Every_allow_listed_file_still_exists` finds `EfAlvoData.Units.cs`.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WriteUnitTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/WriteUnit.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WriteUnitTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/ChangeTrackerReachTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/MMLib.Alvo.Data.EntityFrameworkCore.Tests.csproj
git commit -m "feat(ef): the write unit — one transaction, one turn, one scope, a deadline and poisoning

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 7: Every write site on one seam, and the data port refused inside a unit

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TurnHeldTransaction.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UnitSavepoint.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs`
- Create (tests): `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoDataUnitRefusalTests.cs`

**Interfaces:**
- Consumes: Task 6's `WriteUnit`; Task 1's `AlvoFlow.RefuseInsideUnit`, `AlvoFlow.Unit`.
- Produces: `internal sealed class TurnHeldTransaction(IDbContextTransaction transaction, IDisposable turn) : IDbContextTransaction, IInfrastructure<DbTransaction>`; `internal sealed class UnitSavepoint : IDbContextTransaction, IInfrastructure<DbTransaction> { internal static Task<UnitSavepoint> JoinAsync(AlvoDataContext db, WriteUnit unit, CancellationToken cancellationToken); }`; in `EfAlvoData`: `private AlvoDataContext ContextFor(WriteUnit? unit)`, `private Task<IDbContextTransaction> BeginAsync(AlvoDataContext db, WriteUnit? unit, CancellationToken)`, `private static Task<IDbContextTransaction?> ReadScopeAsync(AlvoDataContext db, WriteUnit? unit, CancellationToken)`, `private IBeforeHookRunner Hooks`; the private write methods `CreatedAsync`, `UpdatedAsync`, `ReplacedAsync`, `ErasedAsync`, `BatchAsync`, `OneBatchAsync` take `WriteUnit? unit` before their token; new private `QueriedAsync(AlvoQuery, AlvoContext, WriteUnit?, CancellationToken)` and `GotAsync(string, Guid, AlvoContext, WriteUnit?, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoDataUnitRefusalTests.cs`:

```csharp
namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// R-B1 (spec 2026-10-14 §4.5): every member of the data port, reads included, is refused while a unit is open on the
/// flow — at once, before any statement — and the refusal fails the unit.
/// </summary>
public sealed class EfAlvoDataUnitRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, Func<EfAlvoData, AlvoContext, Task>> EveryMember() => new()
    {
        { "IAlvoData.QueryAsync", (data, caller) => data.QueryAsync(new AlvoQuery { Entity = WritePathFixture.Entity }, caller, Ct) },
        { "IAlvoData.GetAsync", (data, caller) => data.GetAsync(WritePathFixture.Entity, Guid.NewGuid(), caller, Ct) },
        { "IAlvoData.CreateAsync", (data, caller) => data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("x"), caller, cancellationToken: Ct) },
        { "IAlvoData.UpdateAsync", (data, caller) => data.UpdateAsync(WritePathFixture.Entity, Guid.NewGuid(), Title("x"), caller, cancellationToken: Ct) },
        { "IAlvoData.ReplaceAsync", (data, caller) => data.ReplaceAsync(WritePathFixture.Entity, Guid.NewGuid(), WritePathFixture.CreatePayload("x"), caller, cancellationToken: Ct) },
        { "IAlvoData.DeleteAsync", (data, caller) => data.DeleteAsync(WritePathFixture.Entity, Guid.NewGuid(), caller, cancellationToken: Ct) },
        { "IAlvoData.CreateManyAsync", (data, caller) => data.CreateManyAsync(WritePathFixture.Entity, [WritePathFixture.CreatePayload("x")], caller, cancellationToken: Ct) },
        { "IAlvoData.UpdateManyAsync", (data, caller) => data.UpdateManyAsync(WritePathFixture.Entity, [new AlvoRowPatch(Guid.NewGuid(), Title("x"))], caller, cancellationToken: Ct) },
        { "IAlvoData.DeleteManyAsync", (data, caller) => data.DeleteManyAsync(WritePathFixture.Entity, [Guid.NewGuid()], caller, cancellationToken: Ct) },
    };

    [Theory]
    [MemberData(nameof(EveryMember))]
    public async Task Every_member_is_refused_inside_an_open_unit_before_any_statement(string member, Func<EfAlvoData, AlvoContext, Task> call)
    {
        await using var world = await StartAsync();
        var unit = new OpenUnit();
        var seen = world.Statements.Seen.Count;

        using (AlvoFlow.EnterUnit(unit, AlvoProvenance.None))
        {
            var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(() => call(world.Data, world.Caller));

            refusal.Message.ShouldStartWith($"{member} was called while a transaction unit is open on this flow");
            unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
        }

        world.Statements.Seen.Count.ShouldBe(seen, "a refusal reaches no statement");
    }

    [Fact]
    public async Task A_keyed_write_inside_an_open_unit_is_an_argument_exception()
    {
        await using var world = await StartAsync();
        var unit = new OpenUnit();
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var refusal = await Should.ThrowAsync<ArgumentException>(() => world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("x"), world.Caller, new AlvoIdempotency("key-1", "fp-1"), Ct));

        refusal.ParamName.ShouldBe("idempotency");
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(refusal);
    }

    [Fact]
    public async Task A_unit_that_has_ended_refuses_nothing()
    {
        await using var world = await StartAsync();
        var unit = new OpenUnit { IsOpen = false };
        using var _ = AlvoFlow.EnterUnit(unit, AlvoProvenance.None);

        var created = await world.Data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("after"), world.Caller, cancellationToken: Ct);

        created["title"].ShouldBe("after");
        unit.Poisoned.ShouldBeEmpty();
    }

    private static Task<WritePathWorld> StartAsync() =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema());

    private static Dictionary<string, object?> Title(string title) => new(StringComparer.Ordinal) { ["title"] = title };

    private sealed class OpenUnit : IAlvoFlowUnit
    {
        private readonly List<Exception> _poisoned = [];

        public bool IsOpen { get; set; } = true;

        internal IReadOnlyList<Exception> Poisoned => _poisoned;

        public void Poison(Exception failure) => _poisoned.Add(failure);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*EfAlvoDataUnitRefusalTests'`
Expected: FAIL — every member runs (or fails on a missing row) instead of throwing `AlvoUnitInProgressException`.

- [ ] **Step 3: Write the two transaction shapes**

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TurnHeldTransaction.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>A top-level write's own transaction, holding this database's write turn until after it has ended.</summary>
/// <remarks>
/// Exactly what every write site spelled out before the seam — the turn taken, then <c>BEGIN</c>, and the turn released
/// after the transaction commits or rolls back, so the engine's lock is gone before the next writer is let in — kept as
/// one object so the seam can hand a site either this or a unit's savepoint.
/// </remarks>
/// <param name="transaction">The write's transaction.</param>
/// <param name="turn">The write turn taken before it began.</param>
internal sealed class TurnHeldTransaction(IDbContextTransaction transaction, IDisposable turn)
    : IDbContextTransaction, IInfrastructure<DbTransaction>
{
    /// <inheritdoc/>
    public Guid TransactionId => transaction.TransactionId;

    /// <inheritdoc/>
    DbTransaction IInfrastructure<DbTransaction>.Instance => transaction.GetDbTransaction();

    /// <inheritdoc/>
    public void Commit() => transaction.Commit();

    /// <inheritdoc/>
    public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);

    /// <inheritdoc/>
    public void Rollback() => transaction.Rollback();

    /// <inheritdoc/>
    public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        transaction.Dispose();
        turn.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync().ConfigureAwait(false);
        turn.Dispose();
    }
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UnitSavepoint.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// One call joined to a unit's transaction under a savepoint of its own: committing it releases the savepoint, and ending it
/// any other way — an exception, or a refused batch that returns without committing — rolls back to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every call gets one (spec 2026-10-14 §4.5, §6.1).</b> On PostgreSQL a failed statement aborts the whole
/// transaction (<c>25P02</c>) until a rollback to a savepoint; on SQLite a constraint error rolls back only its statement.
/// A savepoint per call makes "a failed call undoes itself and the unit goes on" true on both. EF's own
/// <c>__EFSavePoint</c> around <c>SaveChanges</c> does not cover the <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> and raw
/// statements of the update and delete paths, so it is not enough on its own.
/// </para>
/// <para>
/// <b>Committing never commits the unit.</b> The joined EF transaction does not own the unit's <see cref="DbTransaction"/>;
/// <see cref="CommitAsync"/> only releases the savepoint. A rollback to the savepoint that itself fails — SQLite may roll the
/// whole transaction back after an interrupt, a full disk or a busy error — poisons the unit.
/// </para>
/// </remarks>
internal sealed class UnitSavepoint : IDbContextTransaction, IInfrastructure<DbTransaction>
{
    private readonly IDbContextTransaction _enlisted;
    private readonly WriteUnit _unit;
    private readonly string _name;
    private bool _settled;

    private UnitSavepoint(IDbContextTransaction enlisted, WriteUnit unit, string name)
    {
        _enlisted = enlisted;
        _unit = unit;
        _name = name;
    }

    /// <inheritdoc/>
    public Guid TransactionId => _enlisted.TransactionId;

    /// <inheritdoc/>
    DbTransaction IInfrastructure<DbTransaction>.Instance => _unit.Transaction;

    /// <summary>Joins <paramref name="db"/> — already on the unit's connection — to the unit's transaction, under a new savepoint.</summary>
    /// <param name="db">A context created on the unit's connection.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="cancellationToken">A token to cancel the join.</param>
    internal static async Task<UnitSavepoint> JoinAsync(AlvoDataContext db, WriteUnit unit, CancellationToken cancellationToken)
    {
        var enlisted = await db.Database.UseTransactionAsync(unit.Transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The unit's transaction could not be joined: EF answered no transaction for it.");
        var name = unit.NextSavepoint();
        try
        {
            await enlisted.CreateSavepointAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await enlisted.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new UnitSavepoint(enlisted, unit, name);
    }

    /// <inheritdoc/>
    public void Commit()
    {
        _enlisted.ReleaseSavepoint(_name);
        _settled = true;
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await _enlisted.ReleaseSavepointAsync(_name, cancellationToken).ConfigureAwait(false);
        _settled = true;
    }

    /// <inheritdoc/>
    public void Rollback()
    {
        _enlisted.RollbackToSavepoint(_name);
        _settled = true;
    }

    /// <inheritdoc/>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _enlisted.RollbackToSavepointAsync(_name, cancellationToken).ConfigureAwait(false);
        _settled = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_settled)
        {
            Undo();
        }

        _enlisted.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_settled)
        {
            await UndoAsync().ConfigureAwait(false);
        }

        await _enlisted.DisposeAsync().ConfigureAwait(false);
    }

    private void Undo()
    {
        try
        {
            _enlisted.RollbackToSavepoint(_name);
        }
        catch (DbException failure)
        {
            _unit.Poison(failure);
        }
        catch (InvalidOperationException failure)
        {
            _unit.Poison(failure);
        }
    }

    private async Task UndoAsync()
    {
        try
        {
            await _enlisted.RollbackToSavepointAsync(_name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException failure)
        {
            _unit.Poison(failure);
        }
        catch (InvalidOperationException failure)
        {
            _unit.Poison(failure);
        }
    }
}
```

- [ ] **Step 4: Put the seam into `EfAlvoData`**

Append to `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs` (inside the class; add `using Microsoft.EntityFrameworkCore.Storage;` and `using MMLib.Alvo.Rules;`):

```csharp
    /// <summary>The declarative hook pipeline this write runs: its unit's, resolved from the unit's scope, or the driver's own outside a unit.</summary>
    /// <remarks>
    /// Read from the flow rather than threaded through every write method: a write inside a unit only ever runs on that
    /// unit's flow, and outside one the data port refuses nothing reaches this with a unit open.
    /// </remarks>
    private IBeforeHookRunner Hooks => AlvoFlow.Unit is WriteUnit unit ? unit.Hooks : _hooks;

    /// <summary>A context for one data operation — on the unit's connection when it runs inside <paramref name="unit"/>.</summary>
    /// <param name="unit">The unit the operation runs in, or <see langword="null"/> for a top-level one.</param>
    private AlvoDataContext ContextFor(WriteUnit? unit)
    {
        var db = _contexts.Create();
        if (unit is not null)
        {
            db.Database.SetDbConnection(unit.Connection, contextOwnsConnection: false);
        }

        return db;
    }

    /// <summary>
    /// The one place a write's transaction begins: a top-level write takes the turn and its own transaction, exactly as before;
    /// a write inside a unit joins the unit's transaction under a savepoint of its own.
    /// </summary>
    /// <param name="db">The write's context.</param>
    /// <param name="unit">The unit the write runs in, or <see langword="null"/> for a top-level write.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    private async Task<IDbContextTransaction> BeginAsync(AlvoDataContext db, WriteUnit? unit, CancellationToken cancellationToken)
    {
        if (unit is not null)
        {
            return await UnitSavepoint.JoinAsync(db, unit, cancellationToken).ConfigureAwait(false);
        }

        var turn = await WriteTurnAsync(db, cancellationToken).ConfigureAwait(false);
        try
        {
            return new TurnHeldTransaction(await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), turn);
        }
        catch
        {
            turn.Dispose();
            throw;
        }
    }

    /// <summary>A read inside a unit joins its transaction — so it sees the unit's writes — under a savepoint; a top-level read joins nothing.</summary>
    /// <param name="db">The read's context.</param>
    /// <param name="unit">The unit the read runs in, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    private static async Task<IDbContextTransaction?> ReadScopeAsync(AlvoDataContext db, WriteUnit? unit, CancellationToken cancellationToken) =>
        unit is null ? null : await UnitSavepoint.JoinAsync(db, unit, cancellationToken).ConfigureAwait(false);
```

Then edit `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs`:

1. **Hooks.** Replace every `_hooks.Run(` with `Hooks.Run(` (four occurrences: `RunBeforeCreate`, `RunBeforeDelete`, `RunBeforeUpdate`, `RunBeforeCreateOrRefuse`).

2. **The seam.** Replace every occurrence (ten) of

```csharp
        using var turn = await WriteTurnAsync(db, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
```

with

```csharp
        await using var transaction = await BeginAsync(db, unit: null, cancellationToken);
```

The five idempotent or replay sites (`CreatedOrReplayedAsync`, `UpdatedOrReplayedAsync`, `ReplacedOrReplayedAsync`, `ReplayedAfterKeyRaceAsync`, `ErasedOrReplayedAsync`) stay `unit: null` — they are top-level by construction. The other five are replaced whole in step 3.

3. **The five write bodies that can run in a unit.** Replace each method with:

```csharp
    /// <summary>
    /// One ordinary create: the authorized candidate, inserted, re-read and emitted inside one transaction — its own, or
    /// <paramref name="unit"/>'s under a savepoint.
    /// </summary>
    private async Task<AlvoRecord> CreatedAsync(
        string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoContext context,
        WriteUnit? unit, CancellationToken cancellationToken)
    {
        using var db = ContextFor(unit);
        var now = WriteInstantNow();
        var (schema, candidate) = AuthorizedCandidate(db, entity, values, decision, context, now);
        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await BeginAsync(db, unit, cancellationToken);
        candidate = RunBeforeCreate(db, schema, decision, context, candidate, now);
        var stored = await InsertAsync(db, schema, decision, context, candidate, cancellationToken);
        await RecomputeRollupsAsync(db, schema, [stored!], cancellationToken);
        await EmitAsync(
            db, transaction, schema, OutboxOperation.Created, context, now, Unmasked(stored), preImage: null,
            cancellationToken);
        var echoed = await EchoedAsync(db, schema, context, RowIdOf(stored), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return echoed;
    }
```

```csharp
    /// <summary>One ordinary update: the write and its event, inside one transaction — its own, or <paramref name="unit"/>'s.</summary>
    private async Task<AlvoRecord> UpdatedAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, PolicyDecision decision,
        AlvoContext context, AlvoPrecondition? precondition, WriteUnit? unit, CancellationToken cancellationToken)
    {
        using var db = ContextFor(unit);
        var schema = UpdatableEntity(db, entity, values, decision, precondition);
        var now = WriteInstantNow();
        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await BeginAsync(db, unit, cancellationToken);
        await WrittenAsync(db, transaction, schema, decision, context, id, values, precondition, now, cancellationToken);
        var echoed = await EchoedAsync(db, schema, context, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return echoed;
    }
```

```csharp
    /// <summary>One ordinary create-or-replace: the write and its event, inside one transaction — its own, or <paramref name="unit"/>'s.</summary>
    private async Task<AlvoReplaceResult> ReplacedAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, ReplaceDecisions branches,
        AlvoContext context, AlvoPrecondition? precondition, WriteUnit? unit, CancellationToken cancellationToken)
    {
        using var db = ContextFor(unit);
        var schema = ReplaceableEntity(db, entity, values, branches, precondition);
        var now = WriteInstantNow();
        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await BeginAsync(db, unit, cancellationToken);
        var result = await ReplaceWrittenAsync(
            db, transaction, schema, branches, context, id, values, precondition, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }
```

```csharp
    /// <summary>One ordinary delete: the erase and its event, inside one transaction — its own, or <paramref name="unit"/>'s.</summary>
    private async Task ErasedAsync(
        string entity, Guid id, PolicyDecision decision, AlvoContext context, AlvoPrecondition? precondition,
        WriteUnit? unit, CancellationToken cancellationToken)
    {
        using var db = ContextFor(unit);
        var schema = ErasableEntity(db, entity, precondition);
        var now = WriteInstantNow();
        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await BeginAsync(db, unit, cancellationToken);
        await ErasedAndEmittedAsync(
            db, transaction, schema, decision, context, id, precondition, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
```

`BatchAsync` and `OneBatchAsync` (keep their XML docs; the batch's refused result returns **without** committing, which inside a unit rolls back to the batch's savepoint):

```csharp
    private Task<AlvoBatchResult> BatchAsync(
        string entity,
        AlvoContext context,
        AlvoIdempotency? idempotency,
        Func<AlvoDataContext, IDbContextTransaction, EntitySchema, DateTimeOffset, Task<BatchOutcome>> attempt,
        bool producesRows,
        WriteUnit? unit,
        CancellationToken cancellationToken)
    {
        Task<AlvoBatchResult> Once() =>
            OneBatchAsync(entity, context, idempotency, attempt, producesRows, unit, cancellationToken);

        return idempotency is null
            ? Once()
            : ReplayableWriteAsync(Once, ContendedBatchAttempts, cancellationToken);
    }

    private async Task<AlvoBatchResult> OneBatchAsync(
        string entity,
        AlvoContext context,
        AlvoIdempotency? idempotency,
        Func<AlvoDataContext, IDbContextTransaction, EntitySchema, DateTimeOffset, Task<BatchOutcome>> attempt,
        bool producesRows,
        WriteUnit? unit,
        CancellationToken cancellationToken)
    {
        using var db = ContextFor(unit);
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var now = WriteInstantNow();
        if (idempotency is not null)
        {
            await EnsureIdempotencyTableAsync(db, cancellationToken);
        }

        await EnsureOutboxTableAsync(db, cancellationToken);

        await using var transaction = await BeginAsync(db, unit, cancellationToken);
        var records = idempotency is { } token
            ? new IdempotencyScope(
                db.Database.GetDbConnection(), transaction.GetDbTransaction(), _idempotencyTable, token, context)
            : (IdempotencyScope?)null;

        if (records is { } scope && await scope.FindAsync(cancellationToken) is { } record)
        {
            EnsureSameRequest(record, idempotency!.Value);

            return await ReplayedBatchAsync(db, schema, context, record, producesRows, cancellationToken);
        }

        var (result, rowIds) = await attempt(db, transaction, schema, now);
        if (!result.Succeeded)
        {
            return result;
        }

        if (records is { } spent)
        {
            await spent.InsertAsync(rowIds, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return result;
    }
```

4. **Reads.** Replace `QueryAsync` with the public entry and its body, and `GetAsync` likewise (keep the moved inline comment in `QueriedAsync` exactly as it stands — it is existing code):

```csharp
    /// <inheritdoc/>
    public async Task<AlvoPage> QueryAsync(
        AlvoQuery query, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        AlvoFlow.RefuseInsideUnit("IAlvoData.QueryAsync");

        return await QueriedAsync(query, context, unit: null, cancellationToken);
    }

    /// <summary>One page of a query — top-level, or inside <paramref name="unit"/>, where it sees the unit's writes.</summary>
    private async Task<AlvoPage> QueriedAsync(
        AlvoQuery query, AlvoContext context, WriteUnit? unit, CancellationToken cancellationToken)
    {
        var decision = Resolve(query.Entity, DataOperation.List, context);
        AlvoFilter.EnsureWithinLimits(query.Filter);
        AlvoQuery.EnsurePagingWindowIsSane(query);
        AlvoQuery.EnsureProjectionIsSane(query);

        using var db = ContextFor(unit);
        await using var joined = await ReadScopeAsync(db, unit, cancellationToken);
        var entity = Entity(db, query.Entity);
        QueryFieldGuard.EnsureAvailable(QueryFields(query), entity, decision.HiddenFields);

        var anchor = await AnchorAsync(db, entity, decision, context, query, cancellationToken);
        var options = ReadOptions(query, anchor, entity);
        var total = await TotalCountAsync(db, entity, decision, context, query, options, cancellationToken);

        // A cursor this provider never issued — stale, forged, or from another tenant — finds no anchor and
        // is answered with an empty page rather than the first one. The count still comes back, because it is
        // a property of the query's visible set and not of the window the cursor failed to open: answering
        // null there would make the HTTP layer's `Preference-Applied: count=exact` a lie.
        if (query.After is not null && anchor is null)
        {
            return AlvoPage.Empty with { TotalCount = total };
        }

        var fetched = await PageAsync(db, entity, decision, context, options, cancellationToken);
        var (kept, nextCursor) = Paginated(fetched, query.Limit);
        return new AlvoPage
        {
            Items = [.. kept.Select(row => RecordMaterializer.ToRecord(row, decision.HiddenFields, options.Unselected))],
            NextCursor = nextCursor,
            TotalCount = total,
        };
    }

    /// <inheritdoc/>
    public async Task<AlvoRecord?> GetAsync(
        string entity, Guid id, AlvoContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(context);
        AlvoFlow.RefuseInsideUnit("IAlvoData.GetAsync");

        return await GotAsync(entity, id, context, unit: null, cancellationToken);
    }

    /// <summary>One record — top-level, or inside <paramref name="unit"/>, where it sees the unit's writes.</summary>
    private async Task<AlvoRecord?> GotAsync(
        string entity, Guid id, AlvoContext context, WriteUnit? unit, CancellationToken cancellationToken)
    {
        var decision = Resolve(entity, DataOperation.Get, context);

        using var db = ContextFor(unit);
        await using var joined = await ReadScopeAsync(db, unit, cancellationToken);
        var row = await SingleAsync(db, Entity(db, entity), decision, context, id, lockFor: null, cancellationToken);
        return row is null ? null : RecordMaterializer.ToRecord(row, decision.HiddenFields, FrozenSet<string>.Empty);
    }
```

5. **The write entries refuse inside a unit and pass `unit: null`.** In each public member, insert the refusal right after its `ArgumentNullException.ThrowIfNull(context);` line and pass `unit: null` to the body:
   - `CreateAsync`: `AlvoFlow.RefuseInsideUnit("IAlvoData.CreateAsync", keyed: idempotency is not null);` and `: await CreatedAsync(entity, values, decision, context, unit: null, cancellationToken);`
   - `UpdateAsync`: `AlvoFlow.RefuseInsideUnit("IAlvoData.UpdateAsync", keyed: idempotency is not null);` and `: await UpdatedAsync(entity, id, values, decision, context, precondition, unit: null, cancellationToken);`
   - `ReplaceAsync`: `AlvoFlow.RefuseInsideUnit("IAlvoData.ReplaceAsync", keyed: idempotency is not null);` and `return await ReplacedAsync(entity, id, values, branches, context, precondition, unit: null, cancellationToken);`
   - `DeleteAsync`: `AlvoFlow.RefuseInsideUnit("IAlvoData.DeleteAsync", keyed: idempotency is not null);` and `await ErasedAsync(entity, id, decision, context, precondition, unit: null, cancellationToken);`
   - `CreateManyAsync`, `UpdateManyAsync`, `DeleteManyAsync`: as the first line after `ArgumentNullException.ThrowIfNull(rows);` / `(ids);` insert `AlvoFlow.RefuseInsideUnit("IAlvoData.CreateManyAsync", keyed: idempotency is not null);` (respectively `UpdateManyAsync`, `DeleteManyAsync`), and pass `producesRows: …, unit: null, cancellationToken)` to `BatchAsync`.

   `BatchDecision` takes no refusal: Task 8's unit entries call it from inside the unit.

- [ ] **Step 5: Run the tests to verify they pass, and that nothing outside a unit moved**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests` (the whole module: `EfAlvoDataUnitRefusalTests`, `WriteUnitTests`, `ChangeTrackerReachTests`, the statement and write-path suites) and `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests` (every `SqliteAlvoData*Tests` leg, `SqliteAlvoDataStatementTests`, the SQL snapshot)
Expected: PASS, with **no** `*.received.*` file anywhere: a top-level write's statements are byte-identical.

- [ ] **Step 6: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TurnHeldTransaction.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UnitSavepoint.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoDataUnitRefusalTests.cs
scripts/test-ring0
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TurnHeldTransaction.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/UnitSavepoint.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoDataUnitRefusalTests.cs
git commit -m "refactor(ef): every write site on one seam; the data port is refused inside a unit

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 8: `IAlvoTransactions` on the EF driver — every call through the unit, the publish in its transaction **[slice end]**

**Files:**
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransactions.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransaction.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs`
- Modify (tests): `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs`
- Create (tests): `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoTransactionsTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 5, 6, 7 (`AlvoFlow.EnterUnit`/`RefuseInsideUnit`, `AlvoTransactionOptions.Validated`, `AlvoCustomEnvelope`, `WriteUnit`, `OpenUnitAsync`, `ContextFor`, `BeginAsync`, `QueriedAsync`, `GotAsync`, `CreatedAsync`, `UpdatedAsync`, `ReplacedAsync`, `ErasedAsync`, `BatchAsync`, `BatchDecision`, `ReplaceDecisions`, `CreatedRowsAsync`, `UpdatedRowsAsync`, `RemovedRowsAsync`, `OutboxTable.InsertAsync`).
- Produces: `EfAlvoData.QueryInUnitAsync`, `GetInUnitAsync`, `CreateInUnitAsync`, `UpdateInUnitAsync`, `ReplaceInUnitAsync`, `DeleteInUnitAsync`, `CreateManyInUnitAsync`, `UpdateManyInUnitAsync`, `DeleteManyInUnitAsync`, `PublishInUnitAsync` (each `(WriteUnit unit, …, AlvoContext context, …, CancellationToken)`), `EfAlvoData.Deeper(WriteUnit unit, AlvoContext context) : AlvoContext`; `internal sealed class EfAlvoTransactions(EfAlvoData data, IServiceScopeFactory scopes) : IAlvoTransactions`; `internal sealed class EfAlvoTransaction(EfAlvoData port, WriteUnit unit, AlvoContext context) : IAlvoTransaction` with `internal EfAlvoTransaction Deeper()`; `IAlvoTransactions` registered by `AddRelationalProvider`; `WritePathWorld.Transactions` (`IAlvoTransactions`).

- [ ] **Step 1: Write the failing tests**

In `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs` add (with `using MMLib.Alvo.Data.EntityFrameworkCore.Internal;`):

```csharp
    /// <summary>The transaction port over this world's data port and container — typed as the port, so its default members are reachable.</summary>
    internal IAlvoTransactions Transactions => new EfAlvoTransactions(Data, Scopes);
```

`test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoTransactionsTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// <see cref="EfAlvoTransactions"/> over SQLite in memory (spec 2026-10-14 §4.5): what commits together, what a savepoint
/// undoes, what poisons, what is refused, and the unit's scope. The both-engine facts are <c>TransactionFacts</c>.
/// </summary>
public sealed class EfAlvoTransactionsTests
{
    private static readonly string _outbox = OutboxTable.NameFor(new AlvoOptions().SchemaPrefix);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_writes_and_an_event_commit_together()
    {
        await using var world = await StartAsync();

        await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("first"), ct);
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("second"), ct);
            await unit.PublishAsync("notes.filed", "notes/1", null, ct);
        }, cancellationToken: Ct);

        (await NotesAsync(world)).ShouldBe(2L);
        (await world.ScalarAsync($"SELECT COUNT(*) FROM {_outbox}")).ShouldBe(3L);
    }

    [Fact]
    public async Task A_delegate_that_throws_leaves_no_row_and_no_event()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("first"), ct);
            await unit.PublishAsync("notes.filed", "notes/1", null, ct);
            throw new InvalidOperationException("the host changed its mind");
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
        (await world.ScalarAsync($"SELECT COUNT(*) FROM {_outbox}")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_read_inside_a_unit_sees_the_unit_s_own_writes()
    {
        await using var world = await StartAsync();

        var seen = await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            var created = await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("mine"), ct);
            var read = await unit.GetAsync(WritePathFixture.Entity, (Guid)created["id"]!, ct);
            var page = await unit.QueryAsync(new AlvoQuery { Entity = WritePathFixture.Entity }, ct);
            return (read?["title"], page.Items.Count);
        }, cancellationToken: Ct);

        seen.ShouldBe(("mine", 1));
    }

    [Fact]
    public async Task An_engine_failure_after_a_write_rolls_that_write_back_and_the_unit_goes_on()
    {
        await using var world = await StartAsync();

        await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("kept"), ct);
            world.Statements.FailWhenContains = "SELECT";
            world.Statements.FailuresLeft = 1;
            await Should.ThrowAsync<SqliteException>(() => unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("undone"), ct));
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("after"), ct);
        }, cancellationToken: Ct);

        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" WHERE "title" = 'undone'""")).ShouldBe(0L);
        (await NotesAsync(world)).ShouldBe(2L);
    }

    [Fact]
    public async Task A_batch_failing_at_the_engine_inside_a_unit_leaves_none_of_its_rows()
    {
        await using var world = await StartAsync();

        await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("kept"), ct);
            world.Statements.FailWhenContains = "SELECT";
            world.Statements.FailuresLeft = 1;
            await Should.ThrowAsync<SqliteException>(() => unit.CreateManyAsync(
                WritePathFixture.Entity, [WritePathFixture.CreatePayload("b1"), WritePathFixture.CreatePayload("b2")], ct));
        }, cancellationToken: Ct);

        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" WHERE "title" LIKE 'b%'""")).ShouldBe(0L);
        (await NotesAsync(world)).ShouldBe(1L);
    }

    [Fact]
    public async Task A_refused_batch_leaves_none_of_its_rows_and_the_unit_goes_on()
    {
        await using var world = await StartAsync(WritePathFixture.Descriptor(create: "title != 'blocked'"));

        var refused = await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            var result = await unit.CreateManyAsync(
                WritePathFixture.Entity, [WritePathFixture.CreatePayload("fine"), WritePathFixture.CreatePayload("blocked")], ct);
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("after"), ct);
            return result;
        }, cancellationToken: Ct);

        refused.Succeeded.ShouldBeFalse();
        (await NotesAsync(world)).ShouldBe(1L);
    }

    [Fact]
    public async Task A_data_port_call_inside_a_unit_is_refused_at_once_and_rolls_the_unit_back()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("unit"), ct);
            await world.Data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("side"), world.Caller, cancellationToken: ct);
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_caught_refusal_still_rolls_the_unit_back()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("unit"), ct);
            try
            {
                await world.Data.GetAsync(WritePathFixture.Entity, Guid.NewGuid(), world.Caller, ct);
            }
            catch (AlvoUnitInProgressException)
            {
            }
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_nested_TransactAsync_is_refused()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => world.Transactions.TransactAsync(world.Caller, (_, ct) =>
            world.Transactions.TransactAsync(world.Caller, (_, _) => Task.CompletedTask, cancellationToken: ct), cancellationToken: Ct));
    }

    [Fact]
    public async Task An_idempotency_key_inside_a_unit_is_an_argument_exception()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<ArgumentException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("unit"), ct);
            await world.Data.CreateAsync(
                WritePathFixture.Entity, WritePathFixture.CreatePayload("keyed"), world.Caller, new AlvoIdempotency("key-1", "fp-1"), ct);
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_write_one_hop_deeper_past_the_limit_is_refused_and_a_caught_refusal_still_rolls_back()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { Depth = AlvoChainDepthLimit.DefaultMaxDepth } };

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => world.Transactions.TransactAsync(caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("at the limit"), ct);
            Should.Throw<AlvoChainDepthExceededException>(() => ((EfAlvoTransaction)unit).Deeper());
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task AsSystem_acts_as_the_system_in_the_caller_s_tenant_with_the_caller_s_provenance()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { CorrelationId = "req-1", Depth = 2 } };

        var system = await world.Transactions.TransactAsync(caller, (unit, _) => Task.FromResult(unit.AsSystem().Context), cancellationToken: Ct);

        (system.ActorKind, system.Tenant, system.Provenance).ShouldBe((AlvoActorKind.System, caller.Tenant, caller.Provenance));
    }

    [Fact]
    public async Task Two_writes_in_one_unit_resolve_one_scope_distinct_from_the_request_s_and_the_next_unit_s()
    {
        var log = new HookRunnerLog();
        await using var world = await StartAsync(services: services =>
        {
            services.AddSingleton(log);
            services.AddScoped<IBeforeHookRunner, ScopeProbeHookRunner>();
        });
        await using var request = world.Scopes.CreateAsyncScope();
        var requestRunner = request.ServiceProvider.GetRequiredService<IBeforeHookRunner>();

        await TwoWritesAsync(world);
        var first = log.Runners.Distinct().ShouldHaveSingleItem();
        log.Runners.Count.ShouldBe(2);
        await TwoWritesAsync(world);

        first.ShouldNotBeSameAs(requestRunner);
        log.Runners.Distinct().Count().ShouldBe(2, "the next unit opens a scope of its own");
    }

    [Fact]
    public async Task A_transaction_used_after_its_unit_ended_is_refused()
    {
        await using var world = await StartAsync();
        IAlvoTransaction? kept = null;

        await world.Transactions.TransactAsync(world.Caller, (unit, _) =>
        {
            kept = unit;
            return Task.CompletedTask;
        }, cancellationToken: Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => kept!.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("late"), Ct)))
            .Message.ShouldContain("has ended");
        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_data_call_from_work_that_outlives_the_unit_is_not_refused()
    {
        await using var world = await StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AlvoRecord>? outliving = null;

        await world.Transactions.TransactAsync(world.Caller, (_, _) =>
        {
            outliving = Task.Run(async () =>
            {
                await release.Task;
                return await world.Data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("later"), world.Caller, cancellationToken: Ct);
            }, Ct);
            return Task.CompletedTask;
        }, cancellationToken: Ct);
        release.SetResult();

        (await outliving!)["title"].ShouldBe("later");
    }

    [Fact]
    public async Task A_cancelled_caller_rolls_the_unit_back_and_sees_its_cancellation()
    {
        await using var world = await StartAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Should.ThrowAsync<OperationCanceledException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("cancelled"), ct);
            await caller.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, cancellationToken: caller.Token));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task Options_are_checked_before_a_unit_opens()
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
        {
            await using var world = await StartAsync();
            await world.Transactions.TransactAsync(
                world.Caller, (_, _) => Task.CompletedTask, new AlvoTransactionOptions { Timeout = TimeSpan.Zero }, Ct);
        });

    private static Task<WritePathWorld> StartAsync(AlvoDescriptor? descriptor = null, Action<IServiceCollection>? services = null) =>
        WritePathWorld.StartAsync(descriptor ?? WritePathFixture.Descriptor(), WritePathFixture.Schema(), services);

    private static async Task<long> NotesAsync(WritePathWorld world) =>
        (long)(await world.ScalarAsync("""SELECT COUNT(*) FROM "note" """))!;

    private static Task TwoWritesAsync(WritePathWorld world) =>
        world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("one"), ct);
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("two"), ct);
        }, cancellationToken: Ct);

    private sealed class HookRunnerLog
    {
        internal List<IBeforeHookRunner> Runners { get; } = [];
    }

    private sealed class ScopeProbeHookRunner(HookRunnerLog log) : IBeforeHookRunner
    {
        public IReadOnlyDictionary<string, object?> Run(
            string entity, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext context, DateTimeOffset now)
        {
            log.Runners.Add(this);
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*EfAlvoTransactionsTests'`
Expected: FAIL to compile — `EfAlvoTransactions` and `EfAlvoTransaction` do not exist.

- [ ] **Step 3: The unit entries, the transactional publish and `Deeper`**

Append to `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs` (inside the class):

```csharp
    /// <summary>One page of a query inside <paramref name="unit"/>.</summary>
    internal Task<AlvoPage> QueryInUnitAsync(WriteUnit unit, AlvoQuery query, AlvoContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return unit.RunAsync(token => QueriedAsync(query, context, unit, token), cancellationToken);
    }

    /// <summary>One record inside <paramref name="unit"/>.</summary>
    internal Task<AlvoRecord?> GetInUnitAsync(WriteUnit unit, string entity, Guid id, AlvoContext context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        return unit.RunAsync(token => GotAsync(entity, id, context, unit, token), cancellationToken);
    }

    /// <summary>One create inside <paramref name="unit"/>, as <paramref name="context"/>, under its own savepoint.</summary>
    internal Task<AlvoRecord> CreateInUnitAsync(
        WriteUnit unit, string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken)
    {
        EnsureUnitWrite(entity, values);
        return unit.RunAsync(
            token => CreatedAsync(entity, values, Decided(entity, DataOperation.Create, context), context, unit, token), cancellationToken);
    }

    /// <summary>One update inside <paramref name="unit"/>, as <paramref name="context"/>, under its own savepoint.</summary>
    internal Task<AlvoRecord> UpdateInUnitAsync(
        WriteUnit unit, string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        AlvoPrecondition? precondition, CancellationToken cancellationToken)
    {
        EnsureUnitWrite(entity, values);
        return unit.RunAsync(
            token => UpdatedAsync(entity, id, values, Decided(entity, DataOperation.Update, context), context, precondition, unit, token),
            cancellationToken);
    }

    /// <summary>One create-or-replace inside <paramref name="unit"/>, as <paramref name="context"/>, under its own savepoint.</summary>
    internal Task<AlvoReplaceResult> ReplaceInUnitAsync(
        WriteUnit unit, string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        AlvoPrecondition? precondition, CancellationToken cancellationToken)
    {
        EnsureUnitWrite(entity, values);
        return unit.RunAsync(
            token => ReplacedAsync(entity, id, values, Branches(entity, context), context, precondition, unit, token), cancellationToken);
    }

    /// <summary>One delete inside <paramref name="unit"/>, as <paramref name="context"/>, under its own savepoint.</summary>
    internal Task DeleteInUnitAsync(
        WriteUnit unit, string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        return unit.RunAsync(
            async token =>
            {
                await ErasedAsync(entity, id, Decided(entity, DataOperation.Delete, context), context, precondition, unit, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>One create batch inside <paramref name="unit"/>: all its rows or none, under its own savepoint.</summary>
    internal Task<AlvoBatchResult> CreateManyInUnitAsync(
        WriteUnit unit, string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return unit.RunAsync(
            token =>
            {
                var decision = BatchDecision(entity, DataOperation.Create, rows.Count, context, idempotency: null);
                return BatchAsync(
                    entity, context, idempotency: null,
                    (db, transaction, schema, now) => CreatedRowsAsync(db, transaction, schema, decision, context, rows, now, token),
                    producesRows: true, unit, token);
            },
            cancellationToken);
    }

    /// <summary>One update batch inside <paramref name="unit"/>: all its rows or none, under its own savepoint.</summary>
    internal Task<AlvoBatchResult> UpdateManyInUnitAsync(
        WriteUnit unit, string entity, IReadOnlyList<AlvoRowPatch> rows, AlvoContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return unit.RunAsync(
            token =>
            {
                var decision = BatchDecision(entity, DataOperation.Update, rows.Count, context, idempotency: null);
                return BatchAsync(
                    entity, context, idempotency: null,
                    (db, transaction, schema, now) => UpdatedRowsAsync(db, transaction, schema, decision, context, rows, now, token),
                    producesRows: true, unit, token);
            },
            cancellationToken);
    }

    /// <summary>One delete batch inside <paramref name="unit"/>: all its rows or none, under its own savepoint.</summary>
    internal Task<AlvoBatchResult> DeleteManyInUnitAsync(
        WriteUnit unit, string entity, IReadOnlyList<Guid> ids, AlvoContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return unit.RunAsync(
            token =>
            {
                var decision = BatchDecision(entity, DataOperation.Delete, ids.Count, context, idempotency: null);
                return BatchAsync(
                    entity, context, idempotency: null,
                    (db, transaction, schema, now) => RemovedRowsAsync(db, transaction, schema, decision, context, ids, now, token),
                    producesRows: false, unit, token);
            },
            cancellationToken);
    }

    /// <summary>One custom event written into <paramref name="unit"/>'s transaction: it commits with the unit, or not at all (spec 2026-10-14 X13).</summary>
    internal Task PublishInUnitAsync(
        WriteUnit unit, string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context,
        CancellationToken cancellationToken)
    {
        AlvoCustomEnvelope.Ensure(type, subject, data);
        return unit.RunAsync(token => PublishedAsync(unit, type, subject, data, context, token), cancellationToken);
    }

    /// <summary>
    /// <paramref name="context"/> one hop deeper in its causation chain — the context of a write made under another write's
    /// stage (HX-09's hooks) — refused, and the unit failed, past the limit.
    /// </summary>
    /// <exception cref="AlvoChainDepthExceededException">The deeper context is past the limit; the unit is poisoned.</exception>
    internal AlvoContext Deeper(WriteUnit unit, AlvoContext context)
    {
        var deeper = context with { Provenance = context.Provenance with { Depth = AlvoEventProvenance.Effective(context).Depth + 1 } };
        try
        {
            _depthLimit.EnsureWithin(deeper);
        }
        catch (AlvoChainDepthExceededException refusal)
        {
            unit.Poison(refusal);
            throw;
        }

        return deeper;
    }

    private async Task<bool> PublishedAsync(
        WriteUnit unit, string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context,
        CancellationToken cancellationToken)
    {
        _depthLimit.EnsureWithin(context);
        var custom = AlvoCustomEnvelope.Build(type, subject, data, context, _time.GetUtcNow());
        using var db = ContextFor(unit);
        await using var transaction = await BeginAsync(db, unit, cancellationToken).ConfigureAwait(false);
        await OutboxTable.InsertAsync(unit.Connection, transaction.GetDbTransaction(), _outboxTable, custom.Envelope, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private PolicyDecision Decided(string entity, DataOperation operation, AlvoContext context)
    {
        _depthLimit.EnsureWithin(context);
        return Resolve(entity, operation, context);
    }

    private ReplaceDecisions Branches(string entity, AlvoContext context)
    {
        _depthLimit.EnsureWithin(context);
        return new ReplaceDecisions(Resolve(entity, DataOperation.Create, context), Resolve(entity, DataOperation.Update, context));
    }

    private static void EnsureUnitWrite(string entity, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
    }
```

- [ ] **Step 4: The port**

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransactions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <see cref="IAlvoTransactions"/> over the EF data port: open a unit, put it on the flow, run the delegate once, commit —
/// or roll back and rethrow, as a <see cref="TimeoutException"/> whatever the delegate threw once the deadline passed.
/// </summary>
/// <remarks>
/// <b>Never retried</b> (spec 2026-10-14 §4.5): the delegate runs once. SQLite's writer contention is absorbed before it
/// runs — at the unit's single <c>BEGIN</c>, by this process's write gate and the provider's busy timeout — and any other
/// failure surfaces to the caller, so a deterministic violation never re-runs host code.
/// </remarks>
/// <param name="data">The data port the unit's calls run through.</param>
/// <param name="scopes">Where each unit's DI scope comes from.</param>
internal sealed class EfAlvoTransactions(EfAlvoData data, IServiceScopeFactory scopes) : IAlvoTransactions
{
    /// <inheritdoc/>
    public async Task<T> TransactAsync<T>(
        AlvoContext context,
        Func<IAlvoTransaction, CancellationToken, Task<T>> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        AlvoFlow.RefuseInsideUnit("IAlvoTransactions.TransactAsync");
        var settings = AlvoTransactionOptions.Validated(options, context);

        await using var unit = await data.OpenUnitAsync(context, settings, scopes, cancellationToken).ConfigureAwait(false);
        using var flow = AlvoFlow.EnterUnit(unit, unit.Provenance);
        var result = await WorkedAsync(unit, work, context).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task<T> WorkedAsync<T>(WriteUnit unit, Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoContext context)
    {
        try
        {
            return await work(new EfAlvoTransaction(data, unit, context), unit.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (unit.TimedOut && failure is not TimeoutException)
        {
            throw unit.TimedOutBecause(failure);
        }
    }
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransaction.cs`:

```csharp
namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>One unit's <see cref="IAlvoTransaction"/>: every call goes through the unit as <see cref="Context"/>.</summary>
/// <param name="port">The data port the calls run through.</param>
/// <param name="unit">The unit they run in.</param>
/// <param name="context">The caller they run as.</param>
internal sealed class EfAlvoTransaction(EfAlvoData port, WriteUnit unit, AlvoContext context) : IAlvoTransaction
{
    /// <inheritdoc/>
    public AlvoContext Context => context;

    /// <inheritdoc/>
    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) =>
        port.QueryInUnitAsync(unit, query, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) =>
        port.GetInUnitAsync(unit, entity, id, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoRecord> CreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        port.CreateInUnitAsync(unit, entity, values, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default) =>
        port.UpdateInUnitAsync(unit, entity, id, values, context, precondition, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoReplaceResult> ReplaceAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null,
        CancellationToken cancellationToken = default) =>
        port.ReplaceInUnitAsync(unit, entity, id, values, context, precondition, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteAsync(string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        port.DeleteInUnitAsync(unit, entity, id, context, precondition, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
        port.CreateManyInUnitAsync(unit, entity, rows, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default) =>
        port.UpdateManyInUnitAsync(unit, entity, rows, context, cancellationToken);

    /// <inheritdoc/>
    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        port.DeleteManyInUnitAsync(unit, entity, ids, context, cancellationToken);

    /// <inheritdoc/>
    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        port.PublishInUnitAsync(unit, type, subject, data, context, cancellationToken);

    /// <inheritdoc/>
    public IAlvoTransaction AsSystem() =>
        new EfAlvoTransaction(port, unit, AlvoContext.System(context.Tenant) with { Provenance = context.Provenance });

    /// <summary>The same unit as the same caller, one hop deeper — what a write made under another write's stage acts as.</summary>
    /// <exception cref="AlvoChainDepthExceededException">One hop deeper is past the limit; the unit is poisoned.</exception>
    internal EfAlvoTransaction Deeper() => new(port, unit, port.Deeper(unit, context));
}
```

`src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` — in `AddRelationalProvider`, replace `builder.Services.TryAddSingleton<IAlvoData>(CreateData);` with:

```csharp
        builder.Services.TryAddSingleton(CreateData);
        builder.Services.TryAddSingleton<IAlvoData>(services => services.GetRequiredService<EfAlvoData>());
        builder.Services.TryAddSingleton<IAlvoTransactions>(services => new EfAlvoTransactions(
            services.GetRequiredService<EfAlvoData>(), services.GetRequiredService<IServiceScopeFactory>()));
```

and append to the method's second `<remarks>`: "<see cref=\"Data.IAlvoTransactions\"/> is registered over the same <c>EfAlvoData</c> singleton — the concrete type, not whatever <see cref=\"Data.IAlvoData\"/> resolves to — because a unit's calls must reach this driver's connection: a host that decorates <see cref=\"Data.IAlvoData\"/> does not decorate the calls of a unit."

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*EfAlvoTransactionsTests' --filter-class '*WriteUnitTests' --filter-class '*EfAlvoDataUnitRefusalTests'`
Expected: PASS. If `An_engine_failure_after_a_write_rolls_that_write_back_and_the_unit_goes_on` sees the row `undone`, the savepoint did not roll back — check that `CreatedAsync` reached `BeginAsync(db, unit, …)` and not `unit: null`.

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests` and `dotnet test --project test/MMLib.Alvo.TestHost.Tests`
Expected: PASS — the forwarding `IAlvoData` registration changes nothing a host resolves.

- [ ] **Step 6: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransactions.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransaction.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoTransactionsTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs
scripts/test-ring1
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransactions.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoTransaction.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.Units.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/EfAlvoTransactionsTests.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/WritePathWorld.cs
git commit -m "feat(ef): IAlvoTransactions — writes, batches and events committed as one unit

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 9: Typed writes — `CreateAsync<T>`, `UpdateAsync<T>(id, IAlvoPatch<T>)`, `ReplaceAsync<T>`, `ExistsAsync<T>`

**Files:**
- Create: `src/MMLib.Alvo/Data/IAlvoPatch.cs`, `src/MMLib.Alvo/Data/AlvoPatch.cs`, `src/MMLib.Alvo/Data/AlvoTypedWriteExtensions.cs`, `src/MMLib.Alvo/Data/Internal/TypedPatch.cs`, `src/MMLib.Alvo/Data/Internal/RecordWriter.cs`
- Create (tests): `test/MMLib.Alvo.Tests/Data/TypedWriteTests.cs`
- Modify (tests): `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: HX-05's `RecordShape`, `MappedProperty`, `EnumMap.ValueOf`, `LambdaShape.Unwrapped`/`RecordKey`, `TypedEntity.NameOf<T>`/`Query<T>`, `AlvoRecordExtensions.As<T>()`; Task 1's `IAlvoTransaction : IAlvoSession` (so every extension works on a unit).
- Produces: `public interface IAlvoPatch<T> where T : class { IReadOnlyDictionary<string, object?> Values { get; } IAlvoPatch<T> Set<TValue>(Expression<Func<T, TValue>> field, TValue value); }`; `public static class AlvoPatch { public static IAlvoPatch<T> For<T>() where T : class; }`; `public static class AlvoTypedWriteExtensions` with `Task<T?> CreateAsync<T>(this IAlvoSession, T record, CancellationToken = default)`, `Task<T?> UpdateAsync<T>(this IAlvoSession, Guid id, IAlvoPatch<T> patch, AlvoPrecondition? precondition = null, CancellationToken = default)`, `Task<T?> ReplaceAsync<T>(this IAlvoSession, Guid id, T record, AlvoPrecondition? precondition = null, CancellationToken = default)`, `Task<bool> ExistsAsync<T>(this IAlvoSession, Expression<Func<T, bool>> where, CancellationToken = default)`; internal `RecordWriter.Values<T>(T)`, `RecordWriter.Field(MappedProperty, object?)`, `RecordWriter.IsWritten(string)`, `RecordWriter.Readable<T>(AlvoRecord)`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Data/TypedWriteTests.cs`:

```csharp
using MMLib.Alvo.Data;

using NSubstitute;

using static MMLib.Alvo.Tests.Data.TypedFixtures;

namespace MMLib.Alvo.Tests.Data;

/// <summary>Typed writes (spec 2026-10-14 §5, HX-07): the entity the type names, its mapped fields as the fields hold them, and the stored record back as the type.</summary>
public sealed class TypedWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_typed_create_writes_its_entity_s_mapped_fields_as_the_fields_hold_them()
    {
        var session = Substitute.For<IAlvoSession>();
        IReadOnlyDictionary<string, object?>? sent = null;
        session.CreateAsync("orders", Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), Ct)
            .Returns(Task.FromResult(OrderRecord()));

        var order = await session.CreateAsync(
            new Order { Id = Guid.NewGuid(), Note = "fragile", Status = OrderStatus.Approved, Rank = 2, Reference = "R-1", Total = 12.5m, Quantity = 3 }, Ct);

        order.ShouldNotBeNull().Note.ShouldBe("fragile");
        sent.ShouldNotBeNull();
        sent["status"].ShouldBe("ok");
        sent["rank"].ShouldBe(2L);
        sent["ref_no"].ShouldBe("R-1");
        sent["quantity"].ShouldBe(3L);
        sent["total"].ShouldBe(12.5m);
        sent.ShouldNotContainKey("id");
        sent.ShouldNotContainKey("summary");
    }

    [Fact]
    public async Task A_typed_create_answers_null_when_the_caller_cannot_read_the_row_back()
    {
        var session = Substitute.For<IAlvoSession>();
        session.CreateAsync("orders", Arg.Any<IReadOnlyDictionary<string, object?>>(), Ct)
            .Returns(Task.FromResult(new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid() })));

        (await session.CreateAsync(new Order { Note = "hidden" }, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_wrapper_writes_its_record_without_the_fields_alvo_manages()
    {
        var session = Substitute.For<IAlvoSession>();
        IReadOnlyDictionary<string, object?>? sent = null;
        session.CreateAsync("orders", Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), Ct)
            .Returns(Task.FromResult(OrderRecord()));
        var record = new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["note"] = "n", ["legacy_code"] = "L-1", ["created_at"] = "2026-01-01T00:00:00Z" });

        (await session.CreateAsync(new OrderView(record), Ct)).ShouldNotBeNull();

        sent.ShouldNotBeNull().Keys.ShouldBe(["note", "legacy_code"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_patch_sends_only_the_fields_it_sets_by_their_field_names()
    {
        var session = Substitute.For<IAlvoSession>();
        var id = Guid.NewGuid();
        IReadOnlyDictionary<string, object?>? sent = null;
        session.UpdateAsync("orders", id, Arg.Do<IReadOnlyDictionary<string, object?>>(values => sent = values), null, Ct)
            .Returns(Task.FromResult(OrderRecord()));

        var order = await session.UpdateAsync(id, AlvoPatch.For<Order>().Set(x => x.Status, OrderStatus.Approved).Set(x => x.Reference, "R-2").Set(x => x.Rank, (int?)null), cancellationToken: Ct);

        order.ShouldNotBeNull();
        sent.ShouldNotBeNull().Count.ShouldBe(3);
        sent["status"].ShouldBe("ok");
        sent["ref_no"].ShouldBe("R-2");
        sent["rank"].ShouldBeNull();
    }

    [Fact]
    public void A_patch_of_a_wrapper_names_its_record_keys()
        => AlvoPatch.For<OrderView>().Set(x => x.Record["legacy_code"], "L-2").Values.ShouldBe(new Dictionary<string, object?> { ["legacy_code"] = "L-2" });

    [Theory]
    [InlineData("summary")]
    [InlineData("id")]
    public void A_patch_naming_no_writable_field_is_refused(string which)
    {
        var refusal = Should.Throw<ArgumentException>(() => _ = which == "id"
            ? AlvoPatch.For<Order>().Set(x => x.Id, Guid.Empty)
            : AlvoPatch.For<Order>().Set(x => x.Summary, "s"));

        refusal.ParamName.ShouldBe("field");
    }

    [Fact]
    public async Task An_empty_patch_is_refused_before_anything_is_sent()
    {
        var session = Substitute.For<IAlvoSession>();

        await Should.ThrowAsync<ArgumentException>(() => session.UpdateAsync(Guid.NewGuid(), AlvoPatch.For<Order>(), cancellationToken: Ct));
        await session.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default!, default, default);
    }

    [Fact]
    public async Task A_typed_replace_writes_the_whole_record_under_the_id()
    {
        var session = Substitute.For<IAlvoSession>();
        var id = Guid.NewGuid();
        session.ReplaceAsync("orders", id, Arg.Any<IReadOnlyDictionary<string, object?>>(), null, Ct)
            .Returns(Task.FromResult(AlvoReplaceResult.ReplacedRow(OrderRecord())));

        (await session.ReplaceAsync(id, new Order { Note = "fragile" }, cancellationToken: Ct)).ShouldNotBeNull().Status.ShouldBe(OrderStatus.Approved);
        await session.Received(1).ReplaceAsync("orders", id, Arg.Is<IReadOnlyDictionary<string, object?>>(values => !values.ContainsKey("id")), null, Ct);
    }

    [Fact]
    public async Task Exists_asks_for_one_row_through_the_translated_filter()
    {
        var session = Substitute.For<IAlvoSession>();
        AlvoQuery? sent = null;
        session.QueryAsync(Arg.Do<AlvoQuery>(query => sent = query), Ct)
            .Returns(Task.FromResult(new AlvoPage { Items = [OrderRecord()] }));

        (await session.ExistsAsync<Order>(x => x.Reference == "R-1", Ct)).ShouldBeTrue();

        (sent!.Entity, sent.Limit).ShouldBe(("orders", 1));
        FilterText.Of(sent.Filter!).ShouldBe("ref_no eq 'R-1'");
    }
}
```

(`OrderView` in `TypedFixtures.cs` maps only `note`; this test adds a `legacy_code` key to its record, which a wrapper carries through untouched — HX-05 (13).)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TypedWriteTests'`
Expected: FAIL to compile — `AlvoPatch` and the typed write extensions do not exist.

- [ ] **Step 3: Write the typed writes**

`src/MMLib.Alvo/Data/IAlvoPatch.cs`:

```csharp
using System.Linq.Expressions;

namespace MMLib.Alvo.Data;

/// <summary>The fields a typed update changes, named by the record type's properties, with their new values.</summary>
/// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
/// <remarks>
/// Built with <see cref="AlvoPatch.For{T}"/> and handed to <c>UpdateAsync&lt;T&gt;</c>; a field the patch does not set keeps
/// its stored value, and setting one to <see langword="null"/> clears it. Alvo implements this interface; it is not meant to
/// be implemented by host code.
/// </remarks>
public interface IAlvoPatch<T>
    where T : class
{
    /// <summary>Gets the fields the patch sets, by field name, with the values they are written as.</summary>
    IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>Sets one mapped property — or, on a wrapper, one <c>Record["field"]</c> — to <paramref name="value"/>.</summary>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="field">The property, such as <c>x =&gt; x.Status</c>.</param>
    /// <param name="value">Its new value; an enum member is written as its declared value.</param>
    /// <returns>This patch, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="field"/> names no mapped field, or a field Alvo manages.</exception>
    IAlvoPatch<T> Set<TValue>(Expression<Func<T, TValue>> field, TValue value);
}
```

`src/MMLib.Alvo/Data/AlvoPatch.cs`:

```csharp
using MMLib.Alvo.Data.Internal;

namespace MMLib.Alvo.Data;

/// <summary>Starts a typed patch: <c>AlvoPatch.For&lt;Invoice&gt;().Set(x =&gt; x.Paid, true)</c>.</summary>
public static class AlvoPatch
{
    /// <summary>An empty patch of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <returns>A patch that sets nothing yet.</returns>
    public static IAlvoPatch<T> For<T>()
        where T : class => new TypedPatch<T>();
}
```

`src/MMLib.Alvo/Data/Internal/RecordWriter.cs`:

```csharp
using System.Collections.Frozen;
using System.Globalization;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// A typed record as the field values a write sends — the read direction of <see cref="RecordValue"/> reversed: an enum
/// member as its declared value, a whole number as a <see cref="long"/>, every other value as it is — and the fields Alvo
/// manages left out.
/// </summary>
internal static class RecordWriter
{
    private static readonly FrozenSet<string> _managed = new[]
    {
        AlvoManagedColumns.Id, AlvoManagedColumns.CreatedAt, AlvoManagedColumns.CreatedBy,
        AlvoManagedColumns.UpdatedAt, AlvoManagedColumns.UpdatedBy, AlvoManagedColumns.DeletedAt,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether a write sends <paramref name="field"/>: every field but the id and the audit and soft-delete columns.</summary>
    internal static bool IsWritten(string field) => !_managed.Contains(field);

    /// <summary>The fields <paramref name="record"/> writes: a wrapper's record, or a plain type's mapped properties.</summary>
    /// <exception cref="InvalidOperationException">The type has a mapping problem no descriptor can cure.</exception>
    internal static Dictionary<string, object?> Values<T>(T record)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        var shape = RecordShape.Of(typeof(T));
        if (shape.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{shape.Name} cannot be written: {string.Join(" ", shape.Problems.Select(problem => problem.Message))}");
        }

        return record is IAlvoEntity wrapper
            ? wrapper.Record.Values.Where(pair => IsWritten(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : shape.Properties.Where(property => IsWritten(property.Field))
                .ToDictionary(property => property.Field, property => Field(property, property.Property.GetValue(record)), StringComparer.Ordinal);
    }

    /// <summary>One property's value as its field holds it.</summary>
    /// <exception cref="ArgumentException">An enum member names no declared value.</exception>
    internal static object? Field(MappedProperty property, object? value) => value switch
    {
        null => null,
        Enum member when property.Enum is { } map => map.ValueOf(member)
            ?? throw new ArgumentException($"{member.GetType().Name}.{member} names no value of field '{property.Field}': name it with [AlvoEnumValue(\"…\")].", nameof(value)),
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        _ => value,
    };

    /// <summary>The stored record as <typeparamref name="T"/> — or <see langword="null"/> when the caller's read-back lacks a field <typeparamref name="T"/> maps.</summary>
    /// <remarks>
    /// A write answers the row as the caller's own <c>get</c> would — only its id when the rule does not return the row, and
    /// without a hidden field. The write has happened; answering <see langword="null"/> rather than throwing keeps it from
    /// looking failed, and inside a unit from being rolled back by an exception the host did not cause.
    /// </remarks>
    internal static T? Readable<T>(AlvoRecord stored)
        where T : class
    {
        var shape = RecordShape.Of(typeof(T));

        return shape.IsWrapper || shape.Properties.All(property => stored.Values.ContainsKey(property.Field)) ? stored.As<T>() : null;
    }
}
```

`src/MMLib.Alvo/Data/Internal/TypedPatch.cs`:

```csharp
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>The one <see cref="IAlvoPatch{T}"/>: field names from the type's mapping, values as the fields hold them.</summary>
/// <typeparam name="T">The record type.</typeparam>
internal sealed class TypedPatch<T> : IAlvoPatch<T>
    where T : class
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Values => _values;

    /// <inheritdoc/>
    public IAlvoPatch<T> Set<TValue>(Expression<Func<T, TValue>> field, TValue value)
    {
        ArgumentNullException.ThrowIfNull(field);
        var (name, property) = Target(field);
        _values[name] = property is null ? value : RecordWriter.Field(property, value);

        return this;
    }

    private static (string Field, MappedProperty? Property) Target<TValue>(Expression<Func<T, TValue>> field)
    {
        var shape = RecordShape.Of(typeof(T));
        var row = field.Parameters[0];
        var body = LambdaShape.Unwrapped(field.Body);
        if (body is MemberExpression { Expression: ParameterExpression owner } member && owner == row && shape.Mapped(member.Member) is { } mapped)
        {
            return (Writable(mapped.Field), mapped);
        }

        if (body is MethodCallExpression call && LambdaShape.RecordKey(call) is { } key && shape.IsWrapper
            && call.Object is MemberExpression { Member.Name: nameof(IAlvoEntity.Record), Expression: ParameterExpression wrapper } && wrapper == row)
        {
            return (Writable(key), null);
        }

        throw new ArgumentException(
            $"'{field.Body}' names no field of {typeof(T).Name}: set a mapped property, such as x => x.Status, or a wrapper's x.Record[\"field\"].",
            nameof(field));
    }

    private static string Writable(string name) => RecordWriter.IsWritten(name)
        ? name
        : throw new ArgumentException($"'{name}' is managed by Alvo and cannot be patched.", "field");
}
```

`src/MMLib.Alvo/Data/AlvoTypedWriteExtensions.cs`:

```csharp
using MMLib.Alvo.Data.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Data;

/// <summary>
/// Writes typed records through a session or a transaction unit: the entity a type's <see cref="AlvoEntityAttribute"/> names,
/// its mapped properties as field values, and the same rules, tenancy and hooks as an untyped write.
/// </summary>
/// <remarks>
/// <para>
/// A write sends every mapped property except the fields Alvo manages (<c>id</c>, the audit columns, <c>deleted_at</c>): an
/// enum member as its declared value, a whole number as a <see cref="long"/>. A wrapper (<see cref="IAlvoEntity"/>) sends its
/// record. An <see cref="IAlvoTransaction"/> is a session, so each of these runs inside its unit too.
/// </para>
/// <para>
/// Each write answers the stored record as the caller's own read would return it, as <typeparamref name="T"/> — or
/// <see langword="null"/> when that read does not return every field <typeparamref name="T"/> maps (a <c>get</c> rule that
/// excludes the row, a hidden field). The write happened either way.
/// </para>
/// </remarks>
public static class AlvoTypedWriteExtensions
{
    /// <summary>Creates <paramref name="record"/> as a row of the entity <typeparamref name="T"/> names.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="record">The record; its id is assigned by Alvo.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored record, or <see langword="null"/> when the caller may not read every field of it back.</returns>
    public static async Task<T?> CreateAsync<T>(this IAlvoSession session, T record, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var stored = await session.CreateAsync(TypedEntity.NameOf<T>(), RecordWriter.Values(record), cancellationToken).ConfigureAwait(false);

        return RecordWriter.Readable<T>(stored);
    }

    /// <summary>Changes the fields <paramref name="patch"/> sets on one row.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="id">The row's id.</param>
    /// <param name="patch">The fields to change; at least one.</param>
    /// <param name="precondition">An optional version the row must still have.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored record, or <see langword="null"/> when the caller may not read every field of it back.</returns>
    /// <exception cref="ArgumentException"><paramref name="patch"/> sets no field.</exception>
    public static async Task<T?> UpdateAsync<T>(
        this IAlvoSession session, Guid id, IAlvoPatch<T> patch, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Values.Count == 0)
        {
            throw new ArgumentException("The patch sets no field, so there is nothing to update. Set at least one with Set(x => x.Field, value).", nameof(patch));
        }

        var stored = await session.UpdateAsync(TypedEntity.NameOf<T>(), id, patch.Values, precondition, cancellationToken).ConfigureAwait(false);

        return RecordWriter.Readable<T>(stored);
    }

    /// <summary>Replaces one row with <paramref name="record"/>, or creates it under <paramref name="id"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="id">The row's id; the record's own id is not sent.</param>
    /// <param name="record">The whole record.</param>
    /// <param name="precondition">An optional version the row must still have.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored record, or <see langword="null"/> when the caller may not read every field of it back; use the untyped <c>ReplaceAsync</c> to learn whether it created the row.</returns>
    public static async Task<T?> ReplaceAsync<T>(
        this IAlvoSession session, Guid id, T record, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = await session.ReplaceAsync(TypedEntity.NameOf<T>(), id, RecordWriter.Values(record), precondition, cancellationToken).ConfigureAwait(false);

        return RecordWriter.Readable<T>(result.Row);
    }

    /// <summary>Whether the caller can see at least one row of <typeparamref name="T"/>'s entity matching <paramref name="where"/>.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>.</typeparam>
    /// <param name="session">Data access as the session's caller.</param>
    /// <param name="where">The predicate, translated into a filter as a typed query's is.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when one row matches.</returns>
    /// <exception cref="AlvoTranslationException"><paramref name="where"/> holds a construct with no filter equivalent.</exception>
    public static async Task<bool> ExistsAsync<T>(this IAlvoSession session, Expression<Func<T, bool>> where, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        var page = await session.QueryAsync(TypedEntity.Query(where, limit: 1), cancellationToken).ConfigureAwait(false);

        return page.Items.Count > 0;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass; accept the core baseline**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TypedWriteTests' --filter-class '*TypedReadTests'`
Expected: PASS.

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`
Expected: FAIL with a received file adding exactly `IAlvoPatch<T>`, `AlvoPatch`, `AlvoTypedWriteExtensions` (four methods). Accept it; Ruling 19 justifies `AlvoPatch`, which §5 does not list.

- [ ] **Step 5: Format, ring0, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Data/IAlvoPatch.cs src/MMLib.Alvo/Data/AlvoPatch.cs src/MMLib.Alvo/Data/AlvoTypedWriteExtensions.cs src/MMLib.Alvo/Data/Internal/TypedPatch.cs src/MMLib.Alvo/Data/Internal/RecordWriter.cs test/MMLib.Alvo.Tests/Data/TypedWriteTests.cs
scripts/test-ring0
git add src/MMLib.Alvo/Data/IAlvoPatch.cs src/MMLib.Alvo/Data/AlvoPatch.cs src/MMLib.Alvo/Data/AlvoTypedWriteExtensions.cs src/MMLib.Alvo/Data/Internal/TypedPatch.cs src/MMLib.Alvo/Data/Internal/RecordWriter.cs test/MMLib.Alvo.Tests/Data/TypedWriteTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(typed): typed create, patch, replace and exists on a session or a unit

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 10: Transactions on both engines **[slice end]**

**Files:**
- Create (tests): `test/_shared/testhost/TransactionFacts.cs`, `test/_shared/testhost/descriptors/ledger-desk.alvo.json`, `test/MMLib.Alvo.TestHost.Tests/SqliteTransactionTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTransactionTests.cs`

**Interfaces:**
- Consumes: everything above, through the public surface only (`IAlvoTransactions`, `IAlvoTransaction`, `AlvoTransactionOptions`, `AlvoUnitInProgressException`, typed writes, `AlvoTestHost`); the `test/_shared/testhost/*.cs` glob both legs compile; `PostgresFixture`, `DispatchedEventCollection`.
- Produces: `public abstract class TransactionFacts` with `protected abstract Task<TransactionDatabase> DatabaseAsync();` and `protected sealed record TransactionDatabase(Action<IAlvoBuilder> Use)` — acceptance (1), (2), (3), (4), (5), (6), (9), (10) on SQLite and PostgreSQL.

- [ ] **Step 1: Write the descriptor and the facts**

`test/_shared/testhost/descriptors/ledger-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "ledger-desk",
  "tenancy": { "enabled": true },
  "auth": { "roles": ["clerk", "auditor"] },
  "entities": {
    "invoices": {
      "tenancy": "scoped",
      "fields": {
        "number": { "type": "string", "required": true, "unique": true, "maxLength": 20 },
        "total": { "type": "decimal", "precision": 12, "scale": 2 },
        "note": { "type": "string", "maxLength": 40 }
      },
      "rules": {
        "list": "'clerk' in @user.roles || 'auditor' in @user.roles || 'admin' in @user.roles",
        "get": "'clerk' in @user.roles || 'auditor' in @user.roles || 'admin' in @user.roles",
        "create": "'clerk' in @user.roles || 'admin' in @user.roles",
        "update": "'clerk' in @user.roles || 'admin' in @user.roles"
      }
    }
  }
}
```

`test/_shared/testhost/TransactionFacts.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

[AlvoEntity("invoices")]
public sealed class LedgerInvoice
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string Number { get; init; }

    public decimal? Total { get; init; }

    public string? Note { get; init; }
}

public sealed class LedgerEvents
{
    public ConcurrentQueue<AlvoEvent> Created { get; } = new();

    public ConcurrentQueue<AlvoEvent> Closed { get; } = new();
}

/// <summary>
/// Transaction units over the real pipeline (spec 2026-10-14 §7 HX-07), on whichever engine the deriving class supplies — the
/// SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class TransactionFacts
{
    private const string LedgerDesk = "ledger-desk.alvo.json";
    private static readonly TimeSpan _immediately = TimeSpan.FromSeconds(10);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_writes_and_a_custom_event_commit_together_with_one_correlation_id()
    {
        var events = new LedgerEvents();
        await using var alvo = await StartAsync(events);
        var clerk = Clerk(Guid.NewGuid());

        await Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "C-1"), ct);
            await unit.CreateAsync("invoices", Invoice(clerk, "C-2"), ct);
            await unit.PublishAsync("ledger.closed", "ledgers/1", new Dictionary<string, object?> { ["count"] = 2L }, ct);
        }, cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        (await NumbersAsync(alvo, clerk)).ShouldBe(["C-1", "C-2"], ignoreOrder: true);
        (events.Created.Count, events.Closed.Count).ShouldBe((2, 1));
        events.Created.Concat(events.Closed).Select(e => e.CorrelationId).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_failed_unit_commits_no_write_and_no_event()
    {
        var events = new LedgerEvents();
        await using var alvo = await StartAsync(events);
        var clerk = Clerk(Guid.NewGuid());

        await Should.ThrowAsync<InvalidOperationException>(() => Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "F-1"), ct);
            await unit.PublishAsync("ledger.closed", "ledgers/1", null, ct);
            throw new InvalidOperationException("the host changed its mind");
        }, cancellationToken: Ct));
        await alvo.DrainEventsAsync(Ct);

        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
        (events.Created.Count, events.Closed.Count).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_caught_unique_violation_lets_the_unit_go_on_and_commit()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());
        await alvo.Data.CreateAsync("invoices", Invoice(clerk, "U-1"), clerk, cancellationToken: Ct);

        var number = await Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "U-0"), ct);
            try
            {
                await unit.CreateAsync("invoices", Invoice(clerk, "U-1"), ct);
                return "U-1";
            }
            catch (AlvoConstraintViolationException taken) when (taken.Kind == AlvoConstraintKind.Unique)
            {
                await unit.CreateAsync("invoices", Invoice(clerk, "U-2"), ct);
                return "U-2";
            }
        }, cancellationToken: Ct);

        number.ShouldBe("U-2");
        (await NumbersAsync(alvo, clerk)).ShouldBe(["U-0", "U-1", "U-2"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_caught_data_port_refusal_still_rolls_the_unit_back()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "P-1"), ct);
            try
            {
                await alvo.Data.CreateAsync("invoices", Invoice(clerk, "P-2"), clerk, cancellationToken: ct);
            }
            catch (AlvoUnitInProgressException)
            {
            }
        }, cancellationToken: Ct));

        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caught_timeout_still_rolls_the_unit_back()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());

        await Should.ThrowAsync<TimeoutException>(() => Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "P-3"), ct);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }, Short(), Ct));

        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caller_without_create_rights_is_refused_inside_a_unit_as_outside()
    {
        await using var alvo = await StartAsync();
        var tenant = Guid.NewGuid();
        var auditor = Caller("auditor", tenant);

        var outside = await Should.ThrowAsync<AlvoAuthorizationException>(() =>
            alvo.Data.CreateAsync("invoices", Invoice(auditor, "A-1"), auditor, cancellationToken: Ct));
        var inside = await Should.ThrowAsync<AlvoAuthorizationException>(() => Transactions(alvo).TransactAsync(auditor, (unit, ct) =>
            unit.CreateAsync("invoices", Invoice(auditor, "A-1"), ct), cancellationToken: Ct));

        inside.Message.ShouldBe(outside.Message);
        (await NumbersAsync(alvo, Clerk(tenant))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_unit_never_writes_into_another_tenant_not_even_as_the_system()
    {
        await using var alvo = await StartAsync();
        var clerkA = Clerk(Guid.NewGuid());
        var clerkB = Clerk(Guid.NewGuid());
        var smuggled = Invoice(clerkB, "X-1");

        var outside = await Should.ThrowAsync<AlvoAuthorizationException>(() =>
            alvo.Data.CreateAsync("invoices", smuggled, clerkA, cancellationToken: Ct));
        var (asCaller, asSystem) = await Transactions(alvo).TransactAsync(clerkA, async (unit, ct) =>
            (await RefusalAsync(() => unit.CreateAsync("invoices", smuggled, ct)),
             await RefusalAsync(() => unit.AsSystem().CreateAsync("invoices", smuggled, ct))), cancellationToken: Ct);

        asCaller.ShouldBeOfType(outside.GetType());
        asSystem.ShouldBeOfType(outside.GetType());
        (await NumbersAsync(alvo, clerkB)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_idempotency_key_inside_a_unit_is_an_argument_exception()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());

        var refusal = await Should.ThrowAsync<ArgumentException>(() => Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "K-1"), ct);
            await alvo.Data.CreateAsync("invoices", Invoice(clerk, "K-2"), clerk, new AlvoIdempotency("key-1", "fp-1"), ct);
        }, cancellationToken: Ct));

        refusal.ParamName.ShouldBe("idempotency");
        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deterministic_violation_runs_the_delegate_once()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());
        await alvo.Data.CreateAsync("invoices", Invoice(clerk, "D-1"), clerk, cancellationToken: Ct);
        var runs = 0;

        await Should.ThrowAsync<AlvoConstraintViolationException>(() => Transactions(alvo).TransactAsync(clerk, (unit, ct) =>
        {
            Interlocked.Increment(ref runs);
            return unit.CreateAsync("invoices", Invoice(clerk, "D-1"), ct);
        }, cancellationToken: Ct));

        runs.ShouldBe(1);
        (await NumbersAsync(alvo, clerk)).ShouldBe(["D-1"]);
    }

    [Fact]
    public async Task A_data_port_write_inside_a_unit_is_refused_at_once_with_no_side_write()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "R-1"), ct);
            await alvo.Data.CreateAsync("invoices", Invoice(clerk, "R-2"), clerk, cancellationToken: ct);
        }, cancellationToken: Ct).WaitAsync(_immediately, Ct));

        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_write_inside_a_unit_is_refused_at_once()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());
        var session = alvo.Services.GetRequiredService<IAlvoSessions>().For(clerk);

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => Transactions(alvo).TransactAsync(clerk, (_, ct) =>
            session.CreateAsync("invoices", Invoice(clerk, "S-1"), ct), cancellationToken: Ct).WaitAsync(_immediately, Ct));

        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_autocommit_publish_inside_a_unit_is_refused_at_once_with_no_event()
    {
        var events = new LedgerEvents();
        await using var alvo = await StartAsync(events);
        var clerk = Clerk(Guid.NewGuid());
        var publisher = alvo.Services.GetRequiredService<IAlvoEvents>();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "E-1"), ct);
            await publisher.PublishAsync("ledger.closed", "ledgers/1", null, clerk, ct);
        }, cancellationToken: Ct).WaitAsync(_immediately, Ct));
        await alvo.DrainEventsAsync(Ct);

        (events.Created.Count, events.Closed.Count).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_unit_past_its_timeout_rolls_back()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());

        await Should.ThrowAsync<TimeoutException>(() => Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "T-1"), ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, Short(), Ct));

        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_timed_out_unit_lets_go_of_the_database_before_its_delegate_returns()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var unitTask = Transactions(alvo).TransactAsync(clerk, async (unit, _) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "T-1"), Ct);
            wrote.SetResult();
            await stuck.Task;
        }, Short(), Ct);
        await wrote.Task.WaitAsync(_immediately, Ct);

        await alvo.Data.CreateAsync("invoices", Invoice(clerk, "T-1"), clerk, cancellationToken: Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        stuck.SetResult();

        await Should.ThrowAsync<TimeoutException>(() => unitTask);
        (await NumbersAsync(alvo, clerk)).ShouldBe(["T-1"]);
    }

    [Fact]
    public async Task Typed_writes_inside_a_unit_commit_with_it()
    {
        await using var alvo = await StartAsync();
        var tenant = Guid.NewGuid();
        var clerk = Clerk(tenant);

        var paid = await Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            var created = await unit.CreateAsync(new LedgerInvoice { TenantId = tenant, Number = "Y-1", Total = 40m }, ct);
            await unit.UpdateAsync(created!.Id, AlvoPatch.For<LedgerInvoice>().Set(x => x.Note, "paid"), cancellationToken: ct);
            return await unit.ExistsAsync<LedgerInvoice>(x => x.Number == "Y-1" && x.Note == "paid", ct);
        }, cancellationToken: Ct);

        paid.ShouldBeTrue();
        (await alvo.Services.GetRequiredService<IAlvoSessions>().For(clerk).ExistsAsync<LedgerInvoice>(x => x.Note == "paid", Ct)).ShouldBeTrue();
    }

    protected abstract Task<TransactionDatabase> DatabaseAsync();

    private async Task<AlvoTestHost> StartAsync(LedgerEvents? events = null)
    {
        var database = await DatabaseAsync();
        var log = events ?? new LedgerEvents();

        return await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", LedgerDesk));
                alvo.Entity("invoices", invoices => invoices.OnCreated(
                    "seen-invoices", (AlvoCreated<AlvoRecord> _, AlvoEventContext context) => log.Created.Enqueue(context.Event)));
                alvo.AddEventHandler(
                    "seen-closings", "ledger.closed", (AlvoRecord _, AlvoEventContext context) => log.Closed.Enqueue(context.Event));
            },
            cancellationToken: Ct);
    }

    private static IAlvoTransactions Transactions(AlvoTestHost alvo) => alvo.Services.GetRequiredService<IAlvoTransactions>();

    private static AlvoTransactionOptions Short() => new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    private static AlvoContext Clerk(Guid tenant) => Caller("clerk", tenant);

    private static AlvoContext Caller(string role, Guid tenant) => AlvoTestHost.Caller(role) with { Tenant = new TenantId(tenant) };

    private static Dictionary<string, object?> Invoice(AlvoContext caller, string number) => new()
    {
        ["number"] = number,
        ["total"] = 10m,
        ["tenant_id"] = caller.Tenant!.Value.Value,
    };

    private static async Task<IReadOnlyList<string>> NumbersAsync(AlvoTestHost alvo, AlvoContext caller) =>
        [.. (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoices" }, caller, Ct)).Items.Select(row => (string)row["number"]!)];

    private static async Task<Exception> RefusalAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (AlvoAuthorizationException refusal)
        {
            return refusal;
        }

        throw new ShouldAssertException("The write was expected to be refused.");
    }

    protected sealed record TransactionDatabase(Action<IAlvoBuilder> Use);
}
```

`test/MMLib.Alvo.TestHost.Tests/SqliteTransactionTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The transaction facts on SQLite — a temp file per fact, so a unit and a second writer meet the engine's one writer lock.</summary>
public sealed class SqliteTransactionTests : TransactionFacts, IDisposable
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

    protected override Task<TransactionDatabase> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-unit-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = $"Data Source={path}";

        return Task.FromResult(new TransactionDatabase(alvo => alvo.UseSqlite(connectionString)));
    }
}
```

`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTransactionTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The transaction facts with <c>UsePostgreSql</c> — a fresh database per fact on one container; the engine where a failed statement aborts the whole transaction unless a savepoint undoes it.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlTransactionTests(PostgresFixture postgres)
    : TransactionFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
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

    protected override async Task<TransactionDatabase> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_unit_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();

        return new TransactionDatabase(alvo => alvo.UsePostgreSql(connectionString));
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

- [ ] **Step 2: Run both legs**

Run: `dotnet test --project test/MMLib.Alvo.TestHost.Tests --filter-class '*SqliteTransactionTests'` and `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlTransactionTests'` (Docker)
Expected: PASS, sixteen facts per engine. The two that only PostgreSQL can break: `A_caught_unique_violation_lets_the_unit_go_on_and_commit` (without the savepoint: `25P02 current transaction is aborted`) and `A_timed_out_unit_lets_go_of_the_database_before_its_delegate_returns` (the outside insert of the same number waits on the unit's uncommitted unique key until the deadline rolls it back). On SQLite the latter's outside write waits on the process write gate instead. A `WaitAsync` timeout in any `…refused_at_once…` fact means a refused call reached the database — that is R-B1 broken, not a slow machine.

- [ ] **Step 3: Format, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include test/_shared/testhost/TransactionFacts.cs test/MMLib.Alvo.TestHost.Tests/SqliteTransactionTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTransactionTests.cs
scripts/test-ring1
git add test/_shared/testhost/TransactionFacts.cs test/_shared/testhost/descriptors/ledger-desk.alvo.json test/MMLib.Alvo.TestHost.Tests/SqliteTransactionTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTransactionTests.cs
git commit -m "test(transactions): units on SQLite and PostgreSQL

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

---

## Task 11: Docs — *Transactions in your own code*, architecture docs, CHANGELOG, PLAN, spec amendment **[slice end]**

**Files:**
- Create: `website/src/content/docs/extending/transactions.mdx`; `website/src/snippets/extending/transactions/InvoiceDesk.cs`, `InvoiceDeskTests.cs`, `invoice-desk.alvo.json`
- Modify: `website/astro.config.mjs`, `website/src/content/docs/extending/index.mdx`, `extending/typed-or-cel.mdx`, `extending/testing.mdx` (sidebar order 8), `guides/call-from-endpoints.mdx`
- Modify: `docs/architecture/data-path.md`, `docs/architecture/events.md`, `CHANGELOG.md`, `docs/PLAN.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`

**Interfaces:**
- Consumes: everything above.
- Produces: the page, run by `InvoiceDeskTests` in `MMLib.Alvo.DocsSnippets.Tests` (ring0).

- [ ] **Step 1: Write the snippets and their facts**

`website/src/snippets/extending/transactions/invoice-desk.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "invoice-desk",
  "auth": { "roles": ["clerk"] },
  "entities": {
    "invoices": {
      "fields": {
        "number": { "type": "string", "required": true, "unique": true, "maxLength": 20 },
        "total": { "type": "decimal", "precision": 12, "scale": 2 },
        "paid": { "type": "boolean" }
      },
      "rules": {
        "list": "'clerk' in @user.roles",
        "get": "'clerk' in @user.roles",
        "create": "'clerk' in @user.roles",
        "update": "'clerk' in @user.roles"
      }
    },
    "invoice_lines": {
      "fields": {
        "invoice_id": { "type": "uuid", "required": true },
        "label": { "type": "string", "required": true, "maxLength": 80 },
        "amount": { "type": "decimal", "required": true, "precision": 12, "scale": 2 }
      },
      "rules": {
        "list": "'clerk' in @user.roles",
        "get": "'clerk' in @user.roles",
        "create": "'clerk' in @user.roles && amount > 0"
      }
    }
  }
}
```

`website/src/snippets/extending/transactions/InvoiceDesk.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;

namespace InvoiceDesk;

public static class InvoiceDeskSetup
{
    public static IAlvoBuilder AddInvoiceDesk(this IAlvoBuilder alvo)
    {
        alvo.Services.AddSingleton<InvoiceIssuer>();
        return alvo;
    }
}

[AlvoEntity("invoices")]
public sealed class Invoice
{
    public Guid Id { get; init; }

    public required string Number { get; init; }

    public decimal? Total { get; init; }

    public bool? Paid { get; init; }
}

public sealed record InvoiceLine(string Label, decimal Amount);

public sealed class InvoiceIssuer(IAlvoTransactions transactions)
{
    public Task<Guid> IssueAsync(AlvoContext clerk, string number, IReadOnlyList<InvoiceLine> lines, CancellationToken cancellationToken) =>
        transactions.TransactAsync(clerk, async (unit, ct) =>
        {
            var invoice = await CreateInvoiceAsync(unit, number, lines.Sum(line => line.Amount), ct);
            var id = (Guid)invoice["id"]!;

            var written = await unit.CreateManyAsync("invoice_lines", [.. lines.Select(line => Line(id, line))], ct);
            if (!written.Succeeded)
            {
                throw new InvalidOperationException($"Invoice {number} has a line the rules refuse.");
            }

            await unit.PublishAsync("billing.invoiced", $"invoices/{id}", new Dictionary<string, object?> { ["number"] = invoice["number"] }, ct);
            return id;
        }, cancellationToken: cancellationToken);

    public Task MarkPaidAsync(AlvoContext clerk, Guid invoice, CancellationToken cancellationToken) =>
        transactions.TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.UpdateAsync(invoice, AlvoPatch.For<Invoice>().Set(x => x.Paid, true), cancellationToken: ct);
            await unit.PublishAsync("billing.paid", $"invoices/{invoice}", null, ct);
        }, cancellationToken: cancellationToken);

    private static async Task<AlvoRecord> CreateInvoiceAsync(IAlvoTransaction unit, string number, decimal total, CancellationToken ct)
    {
        try
        {
            return await unit.CreateAsync("invoices", InvoiceValues(number, total), ct);
        }
        catch (AlvoConstraintViolationException taken) when (taken.Kind == AlvoConstraintKind.Unique)
        {
            return await unit.CreateAsync("invoices", InvoiceValues(number + "-B", total), ct);
        }
    }

    private static Dictionary<string, object?> InvoiceValues(string number, decimal total) =>
        new() { ["number"] = number, ["total"] = total };

    private static IReadOnlyDictionary<string, object?> Line(Guid invoice, InvoiceLine line) =>
        new Dictionary<string, object?> { ["invoice_id"] = invoice, ["label"] = line.Label, ["amount"] = line.Amount };
}
```

`website/src/snippets/extending/transactions/InvoiceDeskTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace InvoiceDesk.Tests;

public sealed class InvoiceDeskTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "invoice-desk.alvo.json");

    [Fact]
    public async Task An_invoice_its_lines_and_its_event_commit_together()
    {
        var ct = TestContext.Current.CancellationToken;
        var billing = new Billing();
        await using var alvo = await StartAsync(billing, ct);
        var clerk = AlvoTestHost.Caller("clerk");

        var id = await alvo.Services.GetRequiredService<InvoiceIssuer>().IssueAsync(
            clerk, "2026-0001", [new("Brake pads", 24.90m), new("Labour", 30m)], ct);
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.GetAsync<Invoice>(id, clerk, ct))!.Total.ShouldBe(54.90m);
        var lines = await alvo.Data.QueryAsync(
            new AlvoQuery { Entity = "invoice_lines", Filter = new AlvoComparison("invoice_id", AlvoFilterOperator.Eq, id) }, clerk, ct);
        lines.Items.Count.ShouldBe(2);
        billing.Invoiced.ShouldBe(["2026-0001"]);
    }

    [Fact]
    public async Task A_taken_number_is_caught_inside_the_unit_and_the_invoice_still_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var billing = new Billing();
        await using var alvo = await StartAsync(billing, ct);
        var clerk = AlvoTestHost.Caller("clerk");
        var issuer = alvo.Services.GetRequiredService<InvoiceIssuer>();

        await issuer.IssueAsync(clerk, "2026-0002", [new("Chain", 19m)], ct);
        var second = await issuer.IssueAsync(clerk, "2026-0002", [new("Tyre", 35m)], ct);
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.GetAsync<Invoice>(second, clerk, ct))!.Number.ShouldBe("2026-0002-B");
        billing.Invoiced.ShouldBe(["2026-0002", "2026-0002-B"]);
    }

    [Fact]
    public async Task A_refused_line_leaves_no_invoice_no_line_and_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var billing = new Billing();
        await using var alvo = await StartAsync(billing, ct);
        var clerk = AlvoTestHost.Caller("clerk");

        await Should.ThrowAsync<InvalidOperationException>(() => alvo.Services.GetRequiredService<InvoiceIssuer>().IssueAsync(
            clerk, "2026-0003", [new("Brake pads", 24.90m), new("Goodwill", 0m)], ct));
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoices" }, clerk, ct)).Items.ShouldBeEmpty();
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoice_lines" }, clerk, ct)).Items.ShouldBeEmpty();
        billing.Invoiced.ShouldBeEmpty();
    }

    [Fact]
    public async Task Paying_is_a_typed_update_and_its_event_in_one_unit()
    {
        var ct = TestContext.Current.CancellationToken;
        var billing = new Billing();
        await using var alvo = await StartAsync(billing, ct);
        var clerk = AlvoTestHost.Caller("clerk");
        var issuer = alvo.Services.GetRequiredService<InvoiceIssuer>();
        var id = await issuer.IssueAsync(clerk, "2026-0004", [new("Labour", 30m)], ct);

        await issuer.MarkPaidAsync(clerk, id, ct);
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.GetAsync<Invoice>(id, clerk, ct))!.Paid.ShouldBe(true);
        billing.Paid.ShouldBe(1);
    }

    [Fact]
    public async Task The_data_port_is_refused_inside_a_unit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(new Billing(), ct);
        var clerk = AlvoTestHost.Caller("clerk");
        var transactions = alvo.Services.GetRequiredService<IAlvoTransactions>();

        var refusal = await Should.ThrowAsync<AlvoUnitInProgressException>(() => transactions.TransactAsync(clerk, async (unit, token) =>
        {
            await unit.CreateAsync("invoices", new Dictionary<string, object?> { ["number"] = "2026-0009" }, token);
            await alvo.Data.CreateAsync("invoices", new Dictionary<string, object?> { ["number"] = "2026-0010" }, clerk, cancellationToken: token);
        }, cancellationToken: ct));

        refusal.Message.ShouldStartWith("IAlvoData.CreateAsync was called while a transaction unit is open");
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoices" }, clerk, ct)).Items.ShouldBeEmpty();
    }

    private static Task<AlvoTestHost> StartAsync(Billing billing, CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddInvoiceDesk();
            builder.AddEventHandler("record-invoice", "billing.invoiced", (AlvoRecord invoice) => billing.Invoice((string)invoice["number"]!));
            builder.AddEventHandler("record-payment", "billing.paid", (AlvoRecord _) => billing.Pay());
        }, cancellationToken: ct);

    private sealed class Billing
    {
        private readonly List<string> _invoiced = [];
        private int _paid;

        public IReadOnlyList<string> Invoiced => _invoiced;

        public int Paid => _paid;

        public void Invoice(string number) => _invoiced.Add(number);

        public void Pay() => Interlocked.Increment(ref _paid);
    }
}
```

Run: `dotnet test --project test/MMLib.Alvo.DocsSnippets.Tests`
Expected: PASS (`SnippetCoverageTests` finds `InvoiceDesk` and `InvoiceDeskTests`). A failing `A_refused_line_…` that finds an invoice means the batch's refusal did not end the delegate — keep the `throw` in `IssueAsync`, it is what the page teaches.

- [ ] **Step 2: Write the page**

`website/src/content/docs/extending/transactions.mdx` — frontmatter `title: Transactions in your own code`, `description: Make several writes, batches and events one unit that commits together or not at all — as the caller, under the same rules, on SQLite and PostgreSQL alike.`, `sidebar:` / `order: 7`. Imports as `operate-deliveries.mdx` does (`Code`, `SourceExcerpt`, the three snippets as `descriptor`, `desk`, `tests`). Sections, each a few sentences:

1. *When you need one* — an invoice and its lines and the event that says it was issued must exist together or not at all; `IAlvoData` commits each call on its own, so a failure between two calls leaves half. `IAlvoTransactions.TransactAsync(caller, (unit, ct) => …)` runs the delegate in one transaction unit; returning commits, throwing rolls back and rethrows. Resolve `IAlvoTransactions` from DI (the EF Core drivers register it; the in-memory reference does not). `<Code code={descriptor} lang="json" title="invoice-desk.alvo.json" />` and `<SourceExcerpt code={desk} from="public Task<Guid> IssueAsync" to="}, cancellationToken: cancellationToken);" title="InvoiceDesk.cs" />`.
2. *What runs inside* — every call through `unit` runs as the caller: the same `create`/`update` rules and `WITH CHECK`, the same tenant scope, the declarative before-hooks and rollups, the same events. Reads (`GetAsync`, `QueryAsync`, typed `QueryAsync<T>`, `ExistsAsync<T>`) see the unit's own writes. Batches (`CreateManyAsync`, …) are all-or-nothing inside the unit too. `unit.AsSystem()` is the same unit acting as the system — in the caller's tenant only, never another.
3. *A refused write does not end the unit* — each call runs under its own savepoint, so a refusal (403-kind authorization, a unique or reference violation, a failed precondition) undoes that call alone, on PostgreSQL as on SQLite; catch it and go on. `<SourceExcerpt code={desk} from="private static async Task<AlvoRecord> CreateInvoiceAsync" to="return await unit.CreateAsync(\"invoices\", InvoiceValues(number + \"-B\", total), ct);" title="InvoiceDesk.cs" />` (if the excerpt component cannot take the escaped quotes, end it at `catch (AlvoConstraintViolationException taken)`). A refused batch returns its refusals without throwing — decide yourself whether the unit goes on; `IssueAsync` throws, and the invoice goes with it.
4. *What ends it* — an exception out of the delegate; and five failures that end it even when you catch them, because the unit's work can no longer be trusted: the unit's `Timeout`, the chain-depth limit, a cancelled call, two calls at once on one unit (await each before the next), and a call through another data port while it is open. Catching one of those does not let the unit commit: `TransactAsync` throws it.
5. *Not inside a unit* — `IAlvoData`, an `IAlvoSession`, `IAlvoEvents.PublishAsync` and a nested `TransactAsync` throw `AlvoUnitInProgressException` at once while a unit is open on the same flow — they would run on a second connection, outside the unit (and on SQLite wait for the unit's own write lock). Use `unit` instead. `<SourceExcerpt code={tests} from="public async Task The_data_port_is_refused_inside_a_unit" to="refusal.Message.ShouldStartWith" title="InvoiceDeskTests.cs" />`. **No network inside a unit**: it holds row locks — on SQLite the database's one write lock — while it runs; call the payment provider or the VAT registry *before* `TransactAsync`, and act on its answer inside.
6. *Events that commit with the writes* — `unit.PublishAsync(type, subject, data)` writes the event into the unit's transaction, so handlers see it only if the unit commits; every event of one unit carries one correlation id. `IAlvoEvents.PublishAsync` stays a single autocommit append, for an event that is not about a write.
7. *Typed writes* — `unit.CreateAsync(new Invoice { … })`, `unit.UpdateAsync(id, AlvoPatch.For<Invoice>().Set(x => x.Paid, true))`, `unit.ReplaceAsync(id, record)`, `unit.ExistsAsync<Invoice>(x => …)` — on a unit and on any `IAlvoSession`. Alvo-managed fields (`id`, the audit columns) are never sent; an enum is written as its declared value. Each answers the record as the caller may read it back, or `null` when the caller's `get` rule does not return every field the class maps — the write happened either way. `<SourceExcerpt code={desk} from="public Task MarkPaidAsync" to="}, cancellationToken: cancellationToken);" title="InvoiceDesk.cs" />`.
8. *Never retried, and no idempotency key* — the delegate runs once; a unique violation you do not catch surfaces at once. No call in a unit takes an idempotency key (a unit is never replayed); an `IAlvoData` write that carries one inside a unit is an `ArgumentException`.
9. *Time limit* — `new AlvoTransactionOptions { Timeout = … }` (30 s unless set, at most one hour). At the deadline the delegate's token is cancelled, the unit rolls back as soon as no call is running and lets go of the database, and `TransactAsync` throws `TimeoutException` — even if the delegate ignores its token and goes on. The deadline runs on the host's `TimeProvider`, so a test with a fake clock decides when it passes. `SuppressHostHooks` (system context and a `Reason` only) is for maintenance that must skip host write hooks, which arrive with write hooks.
10. *Text tests in typed queries* — `x.Number.StartsWith(prefix)`, `EndsWith`, `Contains(text)` match the text literally on every engine: a `%`, `_` or `\` in `prefix` matches itself. One-argument or `StringComparison.Ordinal` only. Link [Typed or CEL](/extending/typed-or-cel/#typed-reads).
11. *Not yet* — write hooks receive the same unit as `write.Transaction`; commands get `ctx.TransactAsync`; a timer scheduled inside a unit joins it. Each arrives with its feature.

`website/astro.config.mjs`: `page('Transactions in your own code', 'extending/transactions'),` after the *Operate deliveries* entry. `testing.mdx` frontmatter `order: 8`. `extending/index.mdx`: a bullet `- [Transactions in your own code](/extending/transactions/) — several writes, batches and events as one unit, with savepoints and a time limit.` after the *Operate deliveries* bullet. `typed-or-cel.mdx`, in *Typed reads*: replace "`StartsWith`/`EndsWith`/`Contains` on a text (no filter operator for them yet)," with nothing (drop it from the refused list), and add a bullet before *Null follows SQL*: "- **`StartsWith`, `EndsWith` and `Contains` on a text** match literally — a `%`, `_`, `\` or `[` in the value matches itself — case-sensitively, on every engine; one-argument or `StringComparison.Ordinal` only. Any other comparison is refused." `guides/call-from-endpoints.mdx`, at the end of section 1: "Several writes that must commit together go through [`IAlvoTransactions`](/extending/transactions/) instead — inside one, `IAlvoData` is refused."

- [ ] **Step 3: Architecture docs, CHANGELOG, PLAN**

`docs/architecture/data-path.md`, a new section **A transaction unit spans several writes (HX-07)** after *How `IAlvoData` reaches a host, and why it is a singleton*: the seam (`BeginAsync`: a top-level write takes the turn and its own transaction exactly as before — no savepoint, no extra statement; a unit's call joins the unit's `DbTransaction` with `UseTransactionAsync` on a context created on the unit's connection with `SetDbConnection`, under `alvo_unit_<n>`); why a savepoint per call (PostgreSQL `25P02`; SQLite statement-level rollback for constraints, whole-transaction rollback after `INTERRUPT`/`BUSY`/`IOERR`/`FULL`/`NOMEM`, so a failing rollback-to poisons); one call at a time; poisoning and its set; the deadline (host `TimeProvider`, abandon without waiting for the delegate, write turn released); R-B1 refuse-not-join and why (second connection, privilege mix, self-deadlock on PostgreSQL / 30 s busy wait on SQLite); the unit's DI scope and the hook runner resolved from it; `IAlvoTransactions` registered over the concrete `EfAlvoData`; the allow-list entry `EfAlvoData.Units.cs`. A second new section **Literal text tests escape `LIKE` and bind the escape**: the three operators, `LIKE @pattern ESCAPE @escape`, the escape set `\ % _ [` and why `[`, text fields only, off the wire, contract suite `AlvoDataLiteralTextTests` on three implementations. In *How `IAlvoData` reaches a host*, add a sentence: "`IAlvoTransactions` is the second singleton over the same instance."

`docs/architecture/events.md`: in the custom-events section, replace the bullet ending "**Deviation 88.**" with: "- **It is autocommit.** `IAlvoEvents.PublishAsync` appends with one statement of its own. Since HX-07 a host that needs its own writes and its event to commit together publishes through the unit — `IAlvoTransaction.PublishAsync` inside `TransactAsync` writes the same envelope (one builder, `AlvoCustomEnvelope`) into the unit's transaction — and `IAlvoEvents.PublishAsync` is refused while a unit is open on the flow. **Deviation 88, narrowed by spec 2026-10-14 X13.**" and in the line that cites "deviations 87 and 88", append "(88 narrowed by HX-07)".

`CHANGELOG.md`, `## [Unreleased]` → `### Added`, first entry: **Transactions in your own code** (HX-07, #384). `IAlvoTransactions.TransactAsync` runs reads, writes, batches and custom events (`IAlvoTransaction.PublishAsync`) as one unit that commits together or not at all, as the caller, on SQLite and PostgreSQL; every call runs under its own savepoint, so a caught refusal does not end the unit; `AlvoTransactionOptions.Timeout` (30 s) rolls a unit back and frees the database; a timeout, chain-depth or misuse failure cannot be caught into a commit; `AlvoUnitInProgressException` refuses `IAlvoData`, `IAlvoSession`, `IAlvoEvents.PublishAsync` and a nested unit while one is open. Typed writes `CreateAsync<T>`, `UpdateAsync<T>(id, AlvoPatch.For<T>()…)`, `ReplaceAsync<T>`, `ExistsAsync<T>` on any session. `AlvoFilterOperator.StartsWith/EndsWith/Contains` — literal text tests, also from a typed query's `StartsWith`/`EndsWith`/`Contains`. Docs: *Transactions in your own code*. **Behaviour change:** inside a unit, the data port and `IAlvoEvents.PublishAsync` throw instead of running outside it. **For third-party `IAlvoData` providers:** `AlvoFilterOperator` gained three members; a provider must render them literally (the shipped `AlvoDataLiteralTextTests` hold it) or refuse them as a malformed filter.

`docs/PLAN.md`, *Host extensibility* line: "…; HX-06 (delivery operations) the sixth; HX-07 (write unit, `TransactAsync`, savepoints) the seventh."

- [ ] **Step 4: Spec amendment**

In `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`:

1. After §4.5's last bullet (*Self-write*), add **HX-07 settled details.** — "Rulings and deviations taken while planning (1–27, verbatim from `docs/superpowers/plans/2026-10-29-hx-07-transactions.md`), so a later reader can tell a decision from an oversight. Where a ruling and §4.5 or §4.6 disagree, the ruling is what shipped." — followed by this plan's *Rulings and deviations* (1)–(27), numbered, verbatim.
2. In §4.3 *HX-05 settled details* (7), append: "— **Superseded by HX-07:** the three literal operators shipped (§4.5 *HX-07 settled details* (16)–(18))."
3. In §5, append to the `IAlvoTransactions.TransactAsync<T>` row's notes: "`IAlvoTransaction : IAlvoSession`; a non-generic `TransactAsync` default member (§4.5 *HX-07 settled details* (3), (4))." and to the typed-writes row: "`AlvoPatch.For<T>()` added; the writes are `IAlvoSession` extensions returning `T?`, and `ExistsAsync<T>` takes a predicate (§4.5 *HX-07 settled details* (19))."
4. In §4.6 *Who adds +1 (R-M2)*, after "A unit's nested write's depth = the unit's depth + 1." insert: "(A write through `TransactAsync`'s own `IAlvoTransaction` is at the unit's depth; "nested" is a write made under another write's stage — §4.5 *HX-07 settled details* (1).)"

- [ ] **Step 5: Build the site, ring1, commit**

```bash
dotnet format whitespace MMLib.Alvo.slnx --include website/src/snippets/extending/transactions/InvoiceDesk.cs website/src/snippets/extending/transactions/InvoiceDeskTests.cs
scripts/test-ring1
(cd website && npm ci && npm run build)
git add website/src/content/docs/extending/transactions.mdx website/src/snippets/extending/transactions website/astro.config.mjs website/src/content/docs/extending/index.mdx website/src/content/docs/extending/typed-or-cel.mdx website/src/content/docs/extending/testing.mdx website/src/content/docs/guides/call-from-endpoints.mdx docs/architecture/data-path.md docs/architecture/events.md CHANGELOG.md docs/PLAN.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: transactions in your own code

Claude-Session: https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB"
```

Expected: the site builds with link validation green (the new public types appear in the generated C# reference; `/extending/transactions/` and `/extending/typed-or-cel/#typed-reads` resolve).

---

## Task 12: Rings, reviews, security core, plan-guard, PR report, PR

- [ ] **Step 1: CI-parity Release build, rings and the out-of-ring suites**

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet build MMLib.Alvo.slnx -c Release -p:HUSKY=0 2>&1 | grep -E "error [A-Z]+[0-9]+" | sort -u'
git clean -fdx -e .superpowers
```

Expected: no output (any `error CAxxxx`/`IDExxxx`/`RCSxxxx` is fixed in a `fix(…): …` commit and this step re-run — watch CA1031 on `WriteUnit.AbandonAsync` (suppressed with a justification), CA2213 on `WriteUnit._calls` (suppressed), CA1068 on token-last parameter order of the `…InUnitAsync` entries, CA2007 in the snippets, and CA1001 on `WriteUnit` should the analyzer not count `IAsyncDisposable` — then suppress it on the class with the justification "disposed by DisposeAsync: ending a unit rolls back a database transaction, which has no synchronous form here"). Then `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (both engines: `PostgreSqlTransactionTests`, `PostgreSqlAlvoDataLiteralTextTests`, `PostgreSqlTypedLayerTests`, every `PostgreSqlAlvoData*Tests`), `scripts/test-admin-e2e` (whole — the dashboard writes through `IAlvoData`, whose registration now forwards), `(cd website && npm run build)`. Expected: all green. The OpenAPI document, the Vacuum ruleset and the TeaPie path set do not move (the wire allow-list is unchanged). The assistant eval is not run: no skill, tool or instruction changed.

**Acceptance (7) check:** `git diff --stat origin/hx/06-delivery-operations...HEAD -- 'src/MMLib.Alvo.Testing/Data/AlvoData*Tests.cs' 'test/**/*AlvoData*Tests.cs'` lists only the **new** files `AlvoDataLiteralTextTests.cs` and its three legs — no existing `AlvoData*Tests` file changed — and ring2 ran every existing one green on both engines.

- [ ] **Step 2: Reviews (substitutes) and the security-core checklist**

Freeze the tree. `/code-review` and `/security-review` are user-only: dispatch two reviewer subagents over `git diff origin/hx/06-delivery-operations...HEAD`, labelled "substitute for /code-review high" and "substitute for /security-review". Point the code reviewer at: `WriteUnit`'s locking (`_gate` vs `_calls`), the deadline's `AbandonAsync` racing `CommitAsync` and `DisposeAsync`, `ExceptionDispatchInfo.Throw` of a never-thrown poison; `UnitSavepoint` never committing the unit's `DbTransaction` and rolling back on dispose-without-commit (the refused-batch path); the ten write sites — the five top-level-only ones still `unit: null`, the five unit-capable ones passing `unit`; `ContextFor` and `SetDbConnection` on a context whose options name a connection string; `EnsureOutboxTableAsync` never running DDL on a unit's connection; the `Hooks` property read from the flow; `IAlvoData` forwarding to the concrete singleton. Point the security reviewer at: R-B1 at every `IAlvoData` member and at `IAlvoEvents.PublishAsync` (a refused call reaches no statement — `EfAlvoDataUnitRefusalTests`); every call through a unit authorized as outside (`A_caller_without_create_rights_is_refused_inside_a_unit_as_outside`) and tenant-bound, `AsSystem()` included (`A_unit_never_writes_into_another_tenant_not_even_as_the_system`); a poisoned unit never committing; the literal operators binding both pattern and escape (`No_captured_text_test_value_reaches_the_sql_text`, the renderer's property suite); `RecordWriter` never sending `id` or an audit column; `IsOpen` semantics (work outliving a unit is not refused, a timed-out unit still refuses its delegate). Then run the `alvo-security-core-review` checklist — §6.2 lists HX-07 — and record each item's evidence for the PR report: *SQL predicate* (the literal operators, the property and typed CsCheck facts), *authorization in the WHERE* (unit calls take the same `ReadStatementComposer` path; no in-memory filter added), *fail-fast compile* (unchanged; a typed text test refused when the read is called, as HX-05's other refusals), *cross-tenant isolation* (the two-tenant fact on both engines, `AsSystem` included; two-user: the auditor/clerk fact), *dynamic entities* (not built, #41 — no new path), *default-deny* (a unit's call resolves the same `PolicyDecision`; no call bypasses `Resolve`), *before-hooks in-transaction, budgeted, network-free* (the declarative runner unchanged, now resolved from the unit's scope inside a unit; no host hook yet — HX-09). Close it with: **mark this change `needs-deep-review`.** Fix findings, re-run ring0, one `fix(…): …` commit per finding.

- [ ] **Step 3: `alvo-plan-guard`**

Dispatch `alvo-plan-guard` last (read-only); act on drift it reports. The PLAN marker stays where it is.

- [ ] **Step 4: PR report**

Use the `alvo-pr-report` skill (it dispatches `alvo-pr-reporter` and publishes the 8-section page). Evidence: acceptance (1)–(11) → tests (table below), the three public-API diffs against the public-surface list, the both-engine runs of `TransactionFacts`, `AlvoDataLiteralTextTests` and `TypedLayerFacts`, the acceptance (7) diff check, the Release build, the security-core checklist and both substitute reviews, and the rulings — calling out Ruling 1 (delegate writes at the unit's depth), 2 (top-level writes not yet units), 5 (reads refused too; the key refusal), 8 (savepoint per call), 10 (the deadline abandons the delegate) and 16 (the escape is bound, `[` escaped).

- [ ] **Step 5: Push and open the PR**

```bash
git branch --show-current   # hx/07-transactions
git push -u origin hx/07-transactions
```

If #422 (HX-06) is still open: `gh pr create --base hx/06-delivery-operations`. If it has merged: `git fetch origin && git merge origin/main` (merge-commit style), re-run ring0, push, then `gh pr create --base main`. The body is the skill's five-line pointer, carries `Closes #384` once, and ends with `https://claude.ai/code/session_01NJAtafM29iddLwfhb5gnmB`. Then `gh pr edit --add-label needs-deep-review`. After the merge, read #384 back (`gh issue view 384`) to confirm it — and only it — was closed, and comment on #377 that HX-07 shipped literal operators beside `like` rather than changing it (N-7 stays open).

---

## Rulings and deviations this plan takes

Each is recorded in the spec amendment (Task 11 Step 4) so a later reader can tell a decision from an oversight.

1. **A write through `TransactAsync`'s own `IAlvoTransaction` is at the unit's depth, not one deeper.** §4.6 says "a unit's nested write's depth = the unit's depth + 1". Read literally for the delegate's writes, wrapping two writes in a unit would report their events one hop deeper than the same writes made one by one, and a command at HTTP depth 0 would emit `chaindepth` 1 — a chain grows because it was made atomic. The unit's depth is the caller's effective depth (`max(explicit, ambient)`), set on the flow for the delegate, and "nested" means a write made under another write's stage: `EfAlvoTransaction.Deeper()` (internal) is that view, one hop deeper, refused and poisoning past `MaxDepth` — the seam HX-09 hands hooks as `write.Transaction`. Tested now (`A_write_one_hop_deeper_past_the_limit_is_refused_and_a_caught_refusal_still_rolls_back`).
2. **Top-level writes move onto the seam but are not yet flow-visible units.** §4.5 says every write opens its own unit and "top-level units use the same default" timeout. Here every `EfAlvoData` write site goes through one `BeginAsync(db, unit)`, whose top-level branch is today's turn + transaction, byte for byte; it puts nothing on the flow and has no deadline. Reason: acceptance (7) — a 30 s cap on every existing write, and refusing a host CEL function's data call during a top-level write (R-B1 on its flow), change shipped behaviour while nothing async runs inside a top-level write yet. HX-09, which first runs host hooks inside one, turns that branch into a unit (the one place to change).
3. **`IAlvoTransaction` derives from `IAlvoSession`.** §5 lists its writes with `AlvoPrecondition?`, batch verbs, `PublishAsync`, `AsSystem()`; the session already has the reads and writes in that shape, and deriving makes HX-05's typed reads and this PR's typed writes work on a unit with no second copy. A session's "each call commits on its own" is restated as the property of `IAlvoSessions`' sessions. Reads are on the unit because R-B1 refuses every other read while it is open.
4. **`IAlvoTransactions` has a non-generic `TransactAsync`**, a default interface member over `TransactAsync<T>` (§5 lists only the generic one): a unit that answers nothing is the common case, and a `Task`-returning lambda must not have to `return true`. Overload resolution follows `Task.Run(Func<Task>)`/`Task.Run(Func<Task<T>>)`.
5. **The refusal covers every data-port member, reads included, and fails the unit.** R-B1 names `IAlvoData`, `IAlvoSession`, nested `TransactAsync` and `IAlvoEvents.PublishAsync`; all nine `IAlvoData` members refuse before any statement, the session through `IAlvoData`, and the refusal poisons the unit so a caught one cannot commit. **Acceptance (5)'s idempotency refusal**: no `IAlvoTransaction` member takes a key, so the only key that can reach a unit is an `IAlvoData` write's; inside a unit it is refused as `ArgumentException` (`ParamName` `idempotency`) instead of `AlvoUnitInProgressException`, naming the more specific mistake, and it poisons too.
6. **"On the flow" means "its `TransactAsync` has not returned."** `IAlvoFlowUnit.IsOpen` stays true until the unit is released, so a delegate whose unit timed out and rolled back is still refused; work that outlives `TransactAsync` (a `Task.Run` the delegate started) carries the unit in its execution context but is not refused — it runs after the unit.
7. **One call at a time.** A `DbConnection` runs one command at a time; a second call while one runs is refused (`InvalidOperationException`) and poisons the unit, rather than queued — queueing would hide a missing `await` and make the delegate's result depend on scheduling.
8. **Every call through a unit runs under its own savepoint** (`alvo_unit_<n>`, EF `IDbContextTransaction` savepoints over ADO.NET `DbTransaction.Save/Rollback/Release`), reads included. An exception, or a refused batch that returns without committing, rolls back to it; the unit goes on. A rollback-to that itself fails poisons the unit — SQLite may roll the whole transaction back after `INTERRUPT`, `BUSY`, `IOERR`, `FULL` or `NOMEM` (*Response To Errors Within A Transaction*), and PostgreSQL's aborted state (`25P02`) is exactly what the savepoint exists to leave. Top-level writes take none (Ruling 2).
9. **What poisons.** The unit's timeout; any `OperationCanceledException` from a call (a cancelled SQLite command may have rolled the transaction back); `AlvoChainDepthExceededException`; a data-port refusal (Ruling 5); a concurrent call (Ruling 7); a failed savepoint rollback (Ruling 8). Not poisoning: a call whose own savepoint rolled it back — authorization, constraint, precondition, not-found, validation, a raw `DbException`. Hook exceptions and the per-hook operation budget join the set with HX-09.
10. **The deadline.** `Timeout` is greater than zero and at most one hour (a unit holds the write lock while it runs), 30 s by default, on the host's `TimeProvider`. At the deadline the unit's token is cancelled and the unit poisoned; as soon as no call is running it rolls back and releases the connection and the write turn — **without waiting for a delegate that ignores its token** — and every later call is refused. `TransactAsync` then throws `TimeoutException` whatever the delegate threw or returned (`A_timed_out_unit_lets_go_of_the_database_before_its_delegate_returns`).
11. **The caller's own cancellation is not a timeout**: it rolls back and surfaces `OperationCanceledException`.
12. **One correlation id per unit.** When the caller's effective provenance carries none, the unit mints one (`AlvoEventProvenance.CorrelationIdOf(Guid.CreateVersion7())` — the ambient W3C trace id when there is one), so every data event and custom event of one unit shares it.
13. **One DI scope per unit, created when it opens, disposed when it ends.** In HX-07 a write inside a unit resolves the declarative before-hook runner from it (`WriteUnit.Hooks`, read through the flow) — the seam HX-09's `IWriteExtensionRunner` joins — while top-level writes keep the runner the driver was built with (Ruling 2). Acceptance (8) is proven on that resolution.
14. **Deferred to HX-09, where they first have something to act on:** in-flight rows and the `self-write` refusal, the per-hook operation counter, `AsSystem()`'s `ExtensionSystemAccess` log and `alvo.hook.system_access` counter (both name an extension; a `TransactAsync` caller already chose its own context, so elevating its own unit grants nothing it lacked), and `SuppressHostHooks`'s effect and audit. `SuppressHostHooks` is validated now — system context and a `Reason` of at most 500 characters with no control character — so its contract does not change when its effect arrives.
15. **No retry.** `TransactAsync` runs the delegate once; SQLite contention is absorbed before it runs, at the unit's single `BEGIN IMMEDIATE`, by the process write gate and the provider's busy timeout. `ReplayableWriteAsync` stays the idempotency-key race loop of top-level keyed writes (C14).
16. **Literal text operators render `field LIKE @pattern ESCAPE @escape`, both bound.** The escape character (`\`) is a parameter, never a literal in the statement, so the renderer's property suite still finds no quote or backslash in any statement text; the value's `\`, `%`, `_` and `[` are escaped — `[` means nothing to SQLite's or PostgreSQL's `LIKE`, where any character after the escape matches itself, and escaping it closes T-SQL's character class before a T-SQL driver (#92) exists. Prior art: EF Core's `StartsWith`/`Contains` translation. Text fields (`string`, `text`) only, a string value only, case-sensitive and ordinal on both engines (SQLite's `case_sensitive_like`, on per connection already). The escaping lives in the shared EF renderer, not in a new `IAlvoSqlDialect`/`IFieldSqlRenderer` member: both are public, and an ANSI `ESCAPE` serves both shipped engines.
17. **Not on the wire.** The three members have no HTTP query-string spelling: `FilterOperators` derives the allow-list from the enum minus them, so it stays PostgREST's ten; `QueryStringParserTests` pins it and the new members' lower-cased names answer `unknown-operator`.
18. **Typed queries' text tests** translate in their one-argument form or with `StringComparison.Ordinal` (HX-05 (6)'s rule for `when`); a culture-folding comparison, a `char` overload, a field-to-field test and a null value are refused when the read is called. Supersedes HX-05 (7).
19. **Typed writes are `IAlvoSession` extensions** (so they work on any session and on a unit), not `IAlvoData` ones: a host endpoint gets a session with `IAlvoSessions.For(caller)`. They return `T?` — `null` when the caller's own read-back lacks a field the class maps (a `get` rule excluding the row, a hidden field), because the write happened and an exception would look like a failure (and inside a unit, roll it back). `ExistsAsync<T>` takes a predicate (a by-id existence is `GetAsync<T>(id) is not null`). `AlvoPatch.For<T>()` is a public static factory §5 does not list — an interface needs a way in, and the house precedent for a guarded factory is `AlvoCustomEvent.Create`. A write never sends `id`, the audit columns or `deleted_at`; an enum is written as its declared value, a whole number as `long`. `ReplaceAsync<T>` with a type that maps `tenant_id` is refused by the existing replace rule, as an untyped replace is.
20. **One custom-event envelope builder.** `AlvoEvents`' guards and envelope move, unchanged, into Abstractions (`AlvoCustomEnvelope`, internal) so the transactional publish and the autocommit one cannot drift. The autocommit publish keeps its order of refusals, now name → unit → depth → clock.
21. **`EfAlvoData` becomes `partial`**; `EfAlvoData.Units.cs` joins `ChangeTrackerReachTests`' allow-list (it reads the unit's connection and writes the unit's outbox row). `WriteUnit`, `UnitSavepoint`, `TurnHeldTransaction`, `EfAlvoTransactions` and `EfAlvoTransaction` receive their connection and transaction as parameters and need no entry.
22. **`InMemoryAlvoData` implements the three literal operators** (the reference evaluator, ordinal) — the contract suite runs on it — **but not `IAlvoTransactions`** (§5).
23. **`AlvoUnitInProgressException` is an `InvalidOperationException`** — `IAlvoData`'s invariant family, rendered 500 by the HTTP layer: it is a programming error in host code, not a request the client can fix.
24. **`IAlvoTransactions` is registered over the concrete `EfAlvoData` singleton**, and `IAlvoData` now forwards to the same instance. A host decorating `IAlvoData` therefore does not decorate a unit's calls — a unit must reach this driver's connection — and the docs say so.
25. **Public types §5 does not list:** `AlvoPatch` (Ruling 19), the non-generic `TransactAsync` (Ruling 4), and `AlvoDataLiteralTextTests` (the "contract rows" §5 assigns to HX-07, as a shipped suite every provider inherits).
26. **`IAlvoScheduler` joining a unit is HX-13's** (§4.10): nothing here touches `ITimerStore`; the unit's connection and transaction are exposed internally for it.
27. **Dashboard: none** (§7). No admin page shows units; the operations page's history is unchanged — a unit's events reach it as any event.

---

## Acceptance criteria → tasks

| HX-07 criterion | Task(s) | Test |
|---|---|---|
| (1) two writes + a custom event commit together or not at all, both engines | 8, 10 | `TransactionFacts.Two_writes_and_a_custom_event_commit_together_with_one_correlation_id`, `A_failed_unit_commits_no_write_and_no_event` (`SqliteTransactionTests`, `PostgreSqlTransactionTests`); `EfAlvoTransactionsTests.Two_writes_and_an_event_commit_together`, `A_delegate_that_throws_leaves_no_row_and_no_event` |
| (2) a caught unique violation, then a different key, commits on both engines | 7, 8, 10 | `TransactionFacts.A_caught_unique_violation_lets_the_unit_go_on_and_commit` (both engines); `EfAlvoTransactionsTests.An_engine_failure_after_a_write_rolls_that_write_back_and_the_unit_goes_on` |
| (3) a delegate that catches a poisoning failure still rolls back | 6, 8, 10 | `TransactionFacts.A_caught_data_port_refusal_still_rolls_the_unit_back`, `A_caught_timeout_still_rolls_the_unit_back` (both engines); `EfAlvoTransactionsTests.A_caught_refusal_still_rolls_the_unit_back`, `A_write_one_hop_deeper_past_the_limit_is_refused_and_a_caught_refusal_still_rolls_back`; `WriteUnitTests.A_poisoned_unit_refuses_further_calls_and_its_commit_throws_the_original_failure`, `A_chain_depth_refusal_inside_a_call_poisons_the_unit` |
| (4) a caller without `create` rights is refused inside as outside (adversarial) | 10 | `TransactionFacts.A_caller_without_create_rights_is_refused_inside_a_unit_as_outside`, `A_unit_never_writes_into_another_tenant_not_even_as_the_system` (both engines) |
| (5) an idempotency key inside → `ArgumentException` | 1, 7, 8, 10 | `AlvoFlowUnitTests.A_keyed_call_inside_an_open_unit_is_an_argument_exception_and_poisons_it`; `EfAlvoDataUnitRefusalTests.A_keyed_write_inside_an_open_unit_is_an_argument_exception`; `EfAlvoTransactionsTests.An_idempotency_key_inside_a_unit_is_an_argument_exception`; `TransactionFacts.An_idempotency_key_inside_a_unit_is_an_argument_exception` (both engines) |
| (6) a deterministic violation runs the delegate once | 8, 10 | `TransactionFacts.A_deterministic_violation_runs_the_delegate_once` (both engines) |
| (7) existing `AlvoData*Tests` unchanged and green | 7, 12 | every existing `AlvoData*Tests` suite and leg (InMemory, SQLite, PostgreSQL) in ring2; `AlvoDataSqlSnapshotTests` and `AlvoDataStatementTests` with no received file; the Task 12 Step 1 diff check |
| (8) two nested writes in one unit resolve one DI scope, distinct from the request scope | 6, 8 | `EfAlvoTransactionsTests.Two_writes_in_one_unit_resolve_one_scope_distinct_from_the_request_s_and_the_next_unit_s`; `WriteUnitTests.A_unit_s_scope_is_one_per_unit_and_ends_with_it` |
| (9) R-B1: a DI-injected `IAlvoData.CreateAsync` (and `IAlvoEvents.PublishAsync`) is refused at once on both engines, no hang, no committed side write | 1, 5, 7, 8, 10 | `TransactionFacts.A_data_port_write_inside_a_unit_is_refused_at_once_with_no_side_write`, `An_autocommit_publish_inside_a_unit_is_refused_at_once_with_no_event`, `A_session_write_inside_a_unit_is_refused_at_once` (both engines, bounded by `WaitAsync(10 s)`); `EfAlvoDataUnitRefusalTests.Every_member_is_refused_inside_an_open_unit_before_any_statement`; `AlvoEventsUnitTests.Publishing_inside_an_open_unit_is_refused_and_fails_the_unit`; `EfAlvoTransactionsTests.A_nested_TransactAsync_is_refused` |
| (10) a unit exceeding `AlvoTransactionOptions.Timeout` rolls back | 6, 10 | `TransactionFacts.A_unit_past_its_timeout_rolls_back`, `A_timed_out_unit_lets_go_of_the_database_before_its_delegate_returns` (both engines); `WriteUnitTests.The_deadline_cancels_a_running_call_and_rolls_the_unit_back_without_waiting_for_its_caller` |
| (11) contract rows for the three literal operators: a captured `%`/`_`/`\` matches literally on both engines | 2, 3, 4 | `AlvoDataLiteralTextTests` (seven rows: `%`, `_`, `\`, `[`, case, empty/missing, non-string) on `SqliteAlvoDataLiteralTextTests`, `PostgreSqlAlvoDataLiteralTextTests`, `InMemoryAlvoDataLiteralTextTests`; `TypedLayerFacts.A_captured_wildcard_in_a_text_test_matches_literally`, `No_captured_text_test_value_reaches_the_sql_text` (both engines); `FilterSqlRendererTests.A_literal_text_test_binds_its_escaped_pattern_and_its_escape`; `FilterSqlRendererPropertyTests` |
| Scope: typed writes | 9, 10, 11 | `TypedWriteTests`; `TransactionFacts.Typed_writes_inside_a_unit_commit_with_it`; snippet `Paying_is_a_typed_update_and_its_event_in_one_unit` |
| Scope: transactional `PublishAsync` (X13) | 5, 8, 10 | as (1) and (9) |
| Scope: savepoints, poisoning, nested depth via `AlvoFlow` | 1, 6, 7, 8 | `AlvoFlowUnitTests`, `WriteUnitTests`, `EfAlvoTransactionsTests` |
| Docs: *Transactions in your own code* | 11 | `InvoiceDeskTests` in `MMLib.Alvo.DocsSnippets.Tests`; website build |
| Security core (§6.2) | 12 | `alvo-security-core-review` checklist, `/security-review` substitute, `needs-deep-review` |
