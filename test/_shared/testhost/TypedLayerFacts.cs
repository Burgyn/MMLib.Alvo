using CsCheck;

using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;

using System.Collections.Concurrent;

namespace MMLib.Alvo.TestHost.Tests;

[AlvoEntity("orders")]
public sealed class DeskOrder
{
    public Guid Id { get; init; }

    public required string Reference { get; init; }

    public DeskStatus Status { get; init; }

    public decimal? Total { get; init; }

    public long? Quantity { get; init; }

    public bool? IsPaid { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public DateOnly? ShipOn { get; init; }

    public Guid? OwnerId { get; init; }
}

public enum DeskStatus
{
    Draft,
    InReview,
    [AlvoEnumValue("ok")]
    Approved,
}

[AlvoEntity("orders")]
public sealed class DeskOrderWithVat
{
    public string? VatId { get; init; }
}

[AlvoEntity("orders")]
public sealed class DeskOrderMistyped
{
    public string? Quantity { get; init; }
}

[AlvoEntity("orders")]
public sealed class DeskOrderWithNote
{
    public string? InternalNote { get; init; }
}

[AlvoEvent("desk.shipped")]
public sealed record DeskShipped(string Reference, long Boxes);

public sealed class ShipmentLog
{
    public ConcurrentQueue<DeskShipped> Shipped { get; } = new();
}

public sealed class ShipmentRecorder(ShipmentLog log) : IAlvoEventHandler<DeskShipped>
{
    public Task HandleAsync(DeskShipped message, AlvoEventContext context, CancellationToken cancellationToken)
    {
        log.Shipped.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The typed layer over the real pipeline (spec 2026-10-14 §7 HX-05), on whichever engine the deriving class supplies —
/// the SQLite leg in <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class TypedLayerFacts
{
    private const string TypedDesk = "typed-desk.alvo.json";

    private static readonly Gen<string> _hostile = Gen.Char["abcXYZ01_ '\"%;-()\\é中"].Array[1, 24].Select(characters => new string(characters));

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_typed_handler_is_handed_typed_images_and_its_translated_condition_selects()
    {
        var seen = new ConcurrentQueue<AlvoUpdated<DeskOrder>>();
        await using var alvo = await StartAsync(await DatabaseAsync(), builder => builder.Entity<DeskOrder>(orders => orders.OnUpdated(
            "on-approved",
            (AlvoUpdated<DeskOrder> approved) =>
            {
                seen.Enqueue(approved);
                return Task.CompletedTask;
            },
            when: e => e.Changed(x => x.Status) && e.New.Status == DeskStatus.Approved)));
        var agent = Agent(Guid.NewGuid());
        var id = await CreateAsync(alvo, agent, "R-1", "draft");
        await UpdateAsync(alvo, agent, id, "in_review");
        await UpdateAsync(alvo, agent, id, "ok");

        await alvo.DrainEventsAsync(Ct);

        var approved = seen.ShouldHaveSingleItem();
        (approved.Old.Status, approved.New.Status, approved.New.Reference, approved.New.Total).ShouldBe((DeskStatus.InReview, DeskStatus.Approved, "R-1", 12.5m));
        approved.New.ShipOn.ShouldBe(new DateOnly(2026, 11, 3));
        alvo.Hooks.AssertRan("on-approved", times: 1);
    }

    [Fact]
    public async Task A_typed_application_event_handler_is_handed_its_typed_payload()
    {
        var log = new ShipmentLog();
        await using var alvo = await StartAsync(await DatabaseAsync(), builder =>
        {
            builder.AddEventHandler<DeskShipped, ShipmentRecorder>("record-shipment");
            builder.Services.AddSingleton(log);
        });

        await alvo.Services.GetRequiredService<IAlvoEvents>().PublishAsync(
            "desk.shipped", "desk/1", new Dictionary<string, object?> { ["reference"] = "R-9", ["boxes"] = 3L }, Agent(Guid.NewGuid()), Ct);
        await alvo.DrainEventsAsync(Ct);

        log.Shipped.ShouldHaveSingleItem().ShouldBe(new DeskShipped("R-9", 3));
    }

    [Fact]
    public async Task Typed_records_that_do_not_fit_refuse_boot_all_at_once()
    {
        var database = await DatabaseAsync();

        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => StartAsync(database, builder => builder
            .Entity<DeskOrderWithVat>(o => o.OnCreated("typed-missing", (AlvoCreated<DeskOrderWithVat> _) => Task.CompletedTask))
            .Entity<DeskOrderMistyped>(o => o.OnCreated("typed-mistyped", (AlvoCreated<DeskOrderMistyped> _) => Task.CompletedTask))
            .Entity<DeskOrderWithNote>(o => o.OnCreated("typed-excluded", (AlvoCreated<DeskOrderWithNote> _) => Task.CompletedTask))));

        refusal.Message.ShouldContain("'typed-missing'");
        refusal.Message.ShouldContain("'vat_id'");
        refusal.Message.ShouldContain("'typed-mistyped'");
        refusal.Message.ShouldContain("'typed-excluded'");
        refusal.Message.ShouldContain("events.exclude");
    }

    [Fact]
    public async Task A_typed_read_returns_typed_records_through_the_caller_s_rules()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var agent = Agent(Guid.NewGuid());
        var approved = await CreateAsync(alvo, agent, "R-1", "ok");
        await CreateAsync(alvo, agent, "R-2", "draft");

        (await alvo.Data.GetAsync<DeskOrder>(approved, agent, Ct)).ShouldNotBeNull().Reference.ShouldBe("R-1");
        (await alvo.Data.QueryAsync<DeskOrder>(x => x.Status == DeskStatus.Approved && x.Total > 10m, 10, agent, Ct))
            .ShouldHaveSingleItem().Id.ShouldBe(approved);
    }

    [Fact]
    public async Task A_typed_read_never_crosses_tenants()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var tenantA = Agent(Guid.NewGuid());
        var tenantB = Agent(Guid.NewGuid());
        var id = await CreateAsync(alvo, tenantA, "R-1", "ok");

        (await alvo.Data.QueryAsync<DeskOrder>(x => x.Reference == "R-1", 10, tenantB, Ct)).ShouldBeEmpty();
        (await alvo.Data.GetAsync<DeskOrder>(id, tenantB, Ct)).ShouldBeNull();
        (await alvo.Data.QueryAsync<DeskOrder>(x => x.Reference == "R-1", 10, tenantA, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_hostile_captured_string_is_matched_as_a_value()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var agent = Agent(Guid.NewGuid());
        var payload = "x'; DROP TABLE orders; --";
        await CreateAsync(alvo, agent, payload, "draft");

        (await alvo.Data.QueryAsync<DeskOrder>(x => x.Reference == payload, 10, agent, Ct)).ShouldHaveSingleItem().Reference.ShouldBe(payload);
    }

    [Fact]
    public async Task No_captured_string_reaches_the_sql_text()
    {
        var database = await DatabaseAsync();
        await using var alvo = await StartAsync(database, _ => { });
        var agent = Agent(Guid.NewGuid());
        using var capture = new SqlCapture(database.Marker);
        var baseline = await StatementsAsync(alvo, agent, capture, "benign");
        var sampled = 0;

        await _hostile.SampleAsync(
            async captured =>
            {
                (await StatementsAsync(alvo, agent, capture, captured)).ShouldBe(baseline, $"captured {captured}");
                Interlocked.Increment(ref sampled);
            },
            iter: 200,
            threads: 1);

        baseline.ShouldNotBeEmpty();
        sampled.ShouldBe(200);
    }

    protected abstract Task<TypedDatabase> DatabaseAsync();

    private static async Task<IReadOnlyList<string>> StatementsAsync(AlvoTestHost alvo, AlvoContext agent, SqlCapture capture, string captured)
    {
        capture.Clear();
        await alvo.Data.QueryAsync<DeskOrder>(
            x => x.Reference == captured || (x.Reference != captured && new[] { captured, "fixed" }.Contains(x.Reference)), 10, agent, Ct);

        return capture.Statements;
    }

    private static async Task<AlvoTestHost> StartAsync(TypedDatabase database, Action<IAlvoBuilder> configure) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", TypedDesk));
                configure(alvo);
            },
            cancellationToken: Ct);

    private static AlvoContext Agent(Guid tenant) => AlvoTestHost.Caller("agent") with { Tenant = new TenantId(tenant) };

    private static async Task<Guid> CreateAsync(AlvoTestHost alvo, AlvoContext agent, string reference, string status)
    {
        var created = await alvo.Data.CreateAsync("orders", new Dictionary<string, object?>
        {
            ["reference"] = reference,
            ["status"] = status,
            ["total"] = 12.5m,
            ["quantity"] = 3L,
            ["ship_on"] = "2026-11-03",
            ["tenant_id"] = agent.Tenant!.Value.Value,
        }, agent, cancellationToken: Ct);

        return (Guid)created[AlvoManagedColumns.Id]!;
    }

    private static Task<AlvoRecord> UpdateAsync(AlvoTestHost alvo, AlvoContext agent, Guid id, string status) =>
        alvo.Data.UpdateAsync("orders", id, new Dictionary<string, object?> { ["status"] = status }, agent, cancellationToken: Ct);

    protected sealed record TypedDatabase(Action<IAlvoBuilder> Use, string Marker);
}
