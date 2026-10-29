using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

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

/// <summary>An invoice read model that maps the two fields the store maintains — the rollup <c>net</c> and the computed <c>gross</c>.</summary>
[AlvoEntity("invoices")]
public sealed class LedgerInvoiceSheet
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string Number { get; init; }

    public long? Vat { get; init; }

    public long? Net { get; init; }

    public long? Gross { get; init; }
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
/// <remarks>
/// A <c>WaitAsync</c> timeout in any <c>…refused_at_once…</c> fact means a refused call reached the database (R-B1), not a
/// slow machine. The timeout facts advance a <see cref="FakeTimeProvider"/> past a one-second unit (Ruling 35).
/// </remarks>
public abstract class TransactionFacts
{
    private const string LedgerDesk = "ledger-desk.alvo.json";
    private const long Forged = 999;
    private static readonly TimeSpan _immediately = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long an outside write may wait for a timed-out unit to let go: longer than <see cref="_immediately"/>, because it
    /// waits through the unit's rollback on the engine's lock, not a refusal that answers before any statement.
    /// </summary>
    private static readonly TimeSpan _throughTheRollback = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _pastTheDeadline = TimeSpan.FromSeconds(2);

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
        events.Created.Concat(events.Closed).Select(e => e.CorrelationId).Distinct().ShouldHaveSingleItem().ShouldNotBeNullOrWhiteSpace();
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

    /// <summary>Acceptance (2)'s literal row: an insert, recovered by EF's own savepoint around <c>SaveChanges</c> (Ruling 29).</summary>
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

    /// <summary>
    /// The unit savepoint's fact (Ruling 29): a failed <c>ExecuteUpdate</c> runs outside EF's own savepoint, so on PostgreSQL
    /// only the per-call savepoint takes the transaction out of its aborted state (<c>25P02</c>) for the next write.
    /// </summary>
    [Fact]
    public async Task A_caught_unique_violation_on_an_update_lets_the_unit_go_on_and_commit()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());
        await alvo.Data.CreateAsync("invoices", Invoice(clerk, "V-1"), clerk, cancellationToken: Ct);
        var second = await alvo.Data.CreateAsync("invoices", Invoice(clerk, "V-2"), clerk, cancellationToken: Ct);

        await Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            var taken = await Should.ThrowAsync<AlvoConstraintViolationException>(() => unit.UpdateAsync(
                "invoices", (Guid)second["id"]!, new Dictionary<string, object?> { ["number"] = "V-1" }, cancellationToken: ct));
            taken.Kind.ShouldBe(AlvoConstraintKind.Unique);
            await unit.CreateAsync("invoices", Invoice(clerk, "V-3"), ct);
        }, cancellationToken: Ct);

        (await NumbersAsync(alvo, clerk)).ShouldBe(["V-1", "V-2", "V-3"], ignoreOrder: true);
    }

    /// <summary>The unit's connection is opened through EF, so SQLite's <c>case_sensitive_like</c> holds from the first unit (Ruling 32).</summary>
    [Fact]
    public async Task A_text_test_in_the_first_unit_of_a_fresh_host_is_case_sensitive()
    {
        await using var alvo = await StartAsync();
        var clerk = Clerk(Guid.NewGuid());

        var (lower, exact) = await Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "Abc-1"), ct);
            return (await unit.ExistsAsync<LedgerInvoice>(x => x.Number.StartsWith("abc"), ct),
                    await unit.ExistsAsync<LedgerInvoice>(x => x.Number.StartsWith("Abc"), ct));
        }, cancellationToken: Ct);

        (lower, exact).ShouldBe((false, true));
    }

    /// <summary>A framework store's write is refused at the write gate while a unit is open on the flow (Ruling 33).</summary>
    [Fact]
    public async Task A_store_write_inside_a_unit_is_refused_at_once_with_no_side_write()
    {
        var events = new LedgerEvents();
        await using var alvo = await StartAsync(events);
        var clerk = Clerk(Guid.NewGuid());
        var outbox = alvo.Services.GetRequiredService<IOutboxStore>();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => Transactions(alvo).TransactAsync(clerk, (_, ct) =>
            outbox.AppendAsync(ForgedClosing(), ct), cancellationToken: Ct).WaitAsync(_immediately, Ct));
        await alvo.DrainEventsAsync(Ct);

        events.Closed.ShouldBeEmpty();
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
        }, cancellationToken: Ct).WaitAsync(_immediately, Ct));

        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_caught_timeout_still_rolls_the_unit_back()
    {
        var clock = new FakeTimeProvider();
        await using var alvo = await StartAsync(time: clock);
        var clerk = Clerk(Guid.NewGuid());
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var unitTask = Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "P-3"), ct);
            wrote.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }, Short(), Ct);
        await wrote.Task.WaitAsync(_immediately, Ct);
        clock.Advance(_pastTheDeadline);

        await EndedAsync(unitTask);
        await Should.ThrowAsync<TimeoutException>(() => unitTask);
        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    /// <summary>
    /// Acceptance (4), adversarial: the same refusal inside a unit as outside, for a single create and a batch one. The auditor
    /// may update, so a batch create checked as an update would pass here.
    /// </summary>
    [Fact]
    public async Task A_caller_without_create_rights_is_refused_inside_a_unit_as_outside()
    {
        await using var alvo = await StartAsync();
        var tenant = Guid.NewGuid();
        var auditor = Caller("auditor", tenant);

        var outside = (
            (await RefusalAsync(() => alvo.Data.CreateAsync("invoices", Invoice(auditor, "A-1"), auditor, cancellationToken: Ct))).Message,
            (await alvo.Data.CreateManyAsync("invoices", [Invoice(auditor, "A-2")], auditor, cancellationToken: Ct)).Refusals);
        var inside = await Transactions(alvo).TransactAsync(auditor, async (unit, ct) => (
            (await RefusalAsync(() => unit.CreateAsync("invoices", Invoice(auditor, "A-1"), ct))).Message,
            (await unit.CreateManyAsync("invoices", [Invoice(auditor, "A-2")], ct)).Refusals), cancellationToken: Ct);

        outside.Refusals.ShouldHaveSingleItem().ShouldSatisfyAllConditions(row => row.Index.ShouldBe(0), row => row.Code.ShouldBe("forbidden"));
        inside.Message.ShouldBe(outside.Message);
        inside.Refusals.ShouldBe(outside.Refusals);
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

    /// <summary>
    /// <c>AsSystem()</c> elevates the caller's roles and keeps its tenant: a create the auditor is denied commits through it in
    /// the auditor's own tenant, while the same elevation still cannot reach another tenant.
    /// </summary>
    [Fact]
    public async Task A_unit_as_the_system_creates_what_its_caller_may_not_but_only_in_the_caller_s_tenant()
    {
        await using var alvo = await StartAsync();
        var tenant = Guid.NewGuid();
        var auditor = Caller("auditor", tenant);
        var other = Clerk(Guid.NewGuid());

        var outside = await Should.ThrowAsync<AlvoAuthorizationException>(() => alvo.Data.CreateAsync(
            "invoices", Invoice(other, "S-2"), AlvoContext.System(new TenantId(tenant)), cancellationToken: Ct));
        var smuggling = await Transactions(alvo).TransactAsync(auditor, async (unit, ct) =>
        {
            await Should.ThrowAsync<AlvoAuthorizationException>(() => unit.CreateAsync("invoices", Invoice(auditor, "S-0"), ct));
            await unit.AsSystem().CreateAsync("invoices", Invoice(auditor, "S-1"), ct);
            return await RefusalAsync(() => unit.AsSystem().CreateAsync("invoices", Invoice(other, "S-2"), ct));
        }, cancellationToken: Ct);

        smuggling.ShouldBeOfType(outside.GetType());
        (await NumbersAsync(alvo, Clerk(tenant))).ShouldBe(["S-1"]);
        (await NumbersAsync(alvo, other)).ShouldBeEmpty();
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
        var clock = new FakeTimeProvider();
        await using var alvo = await StartAsync(time: clock);
        var clerk = Clerk(Guid.NewGuid());
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var unitTask = Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            await unit.CreateAsync("invoices", Invoice(clerk, "T-1"), ct);
            wrote.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, Short(), Ct);
        await wrote.Task.WaitAsync(_immediately, Ct);
        clock.Advance(_pastTheDeadline);

        await EndedAsync(unitTask);
        await Should.ThrowAsync<TimeoutException>(() => unitTask);
        (await NumbersAsync(alvo, clerk)).ShouldBeEmpty();
    }

    /// <summary>
    /// The outside insert of the same number waits — on PostgreSQL on the unit's uncommitted unique key, on SQLite on the
    /// process write gate — until the advanced clock's deadline rolls the unit back, while the delegate ignores its token.
    /// </summary>
    [Fact]
    public async Task A_timed_out_unit_lets_go_of_the_database_before_its_delegate_returns()
    {
        var clock = new FakeTimeProvider();
        await using var alvo = await StartAsync(time: clock);
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
        clock.Advance(_pastTheDeadline);

        await alvo.Data.CreateAsync("invoices", Invoice(clerk, "T-1"), clerk, cancellationToken: Ct).WaitAsync(_throughTheRollback, Ct);
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

    /// <summary>
    /// Ruling 52 on the real composition: a typed create and replace leave out the rollup and the computed field the type maps,
    /// inside a unit and through the session <c>AddAlvo</c> registers, so the port never sees a write to either and the store's
    /// own values stand: no line yet is a rollup of nothing (<c>NULL</c> on both engines, so <c>gross</c> too), one line of 7
    /// makes <c>net</c> 7 and <c>gross</c> 7 + VAT. Without the ruling every write here is refused.
    /// </summary>
    [Fact]
    public async Task A_typed_record_mapping_a_computed_and_a_rollup_field_creates_and_replaces_in_a_unit_and_outside()
    {
        await using var alvo = await StartAsync();
        var tenant = Guid.NewGuid();
        var clerk = Clerk(tenant);

        var (created, replaced) = await Transactions(alvo).TransactAsync(clerk, async (unit, ct) =>
        {
            var sheet = await unit.CreateAsync(Sheet(tenant, "M-1", vat: 3), ct);
            await unit.CreateAsync("invoice_lines", new Dictionary<string, object?>
            {
                ["invoice_id"] = sheet!.Id,
                ["amount"] = 7L,
                ["tenant_id"] = tenant,
            }, ct);
            var again = await unit.ReplaceAsync(sheet.Id, Sheet(tenant, "M-1", vat: 4), cancellationToken: ct);
            return (sheet, again!);
        }, cancellationToken: Ct);
        var outside = await alvo.Services.GetRequiredService<IAlvoSessions>().For(clerk).CreateAsync(Sheet(tenant, "M-2", vat: 5), Ct);

        (created.Vat, replaced.Vat, outside!.Vat).ShouldBe((3L, 4L, 5L));
        (created.Net, created.Gross, replaced.Net, replaced.Gross, outside.Net, outside.Gross).ShouldBe((null, null, 7L, 11L, null, null));
        (await NumbersAsync(alvo, clerk)).ShouldBe(["M-1", "M-2"], ignoreOrder: true);
    }

    protected abstract Task<TransactionDatabase> DatabaseAsync();

    private async Task<AlvoTestHost> StartAsync(LedgerEvents? events = null, TimeProvider? time = null)
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
            time,
            cancellationToken: Ct);
    }

    private static IAlvoTransactions Transactions(AlvoTestHost alvo) => alvo.Services.GetRequiredService<IAlvoTransactions>();

    /// <summary>A one-second unit on the host's fake clock: the deadline passes when a fact advances it, never on a slow runner (Ruling 35).</summary>
    private static AlvoTransactionOptions Short() => new() { Timeout = TimeSpan.FromSeconds(1) };

    private static AlvoCustomEvent ForgedClosing() => AlvoCustomEvent.Create(new AlvoEvent
    {
        Id = Guid.CreateVersion7(),
        Source = AlvoEvent.DefaultSource,
        Type = "ledger.closed",
        Time = DateTimeOffset.UtcNow,
        Subject = "ledgers/1",
        PartitionKey = "custom.event:ledgers/1",
        AuthType = AlvoEventAuthType.Unauthenticated,
        CorrelationId = "store-write",
        Data = new AlvoEventData { Record = null },
    });

    private static AlvoContext Clerk(Guid tenant) => Caller("clerk", tenant);

    private static AlvoContext Caller(string role, Guid tenant) => AlvoTestHost.Caller(role) with { Tenant = new TenantId(tenant) };

    private static Dictionary<string, object?> Invoice(AlvoContext caller, string number) => new()
    {
        ["number"] = number,
        ["total"] = 10m,
        ["tenant_id"] = caller.Tenant!.Value.Value,
    };

    private static LedgerInvoiceSheet Sheet(Guid tenant, string number, long vat) =>
        new() { TenantId = tenant, Number = number, Vat = vat, Net = Forged, Gross = Forged };

    private static async Task<IReadOnlyList<string>> NumbersAsync(AlvoTestHost alvo, AlvoContext caller) =>
        [.. (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "invoices" }, caller, Ct)).Items.Select(row => (string)row["number"]!)];

    /// <summary>
    /// Waits for a unit the fake clock has timed out, failing — not hanging — when its deadline never fires. Not
    /// <c>WaitAsync</c>: its own <see cref="TimeoutException"/> would pass for the unit's.
    /// </summary>
    private static async Task EndedAsync(Task unit) =>
        (await Task.WhenAny(unit, Task.Delay(_immediately, Ct))).ShouldBeSameAs(unit, "the unit's deadline never ended it");

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
