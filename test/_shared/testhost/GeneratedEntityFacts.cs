using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Schema;
using MMLib.Alvo.TestHost.Tests.Generated;

using System.Collections.Concurrent;

using V1 = MMLib.Alvo.TestHost.Tests.Generated.V1;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// Classes the typed-entity generator emitted from <c>generated/desk-live.alvo.json</c> (and an older copy without
/// <c>legacy_code</c>) over the real pipeline and the real boot, on whichever engine the deriving class supplies — the SQLite leg in
/// <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>.
/// </summary>
public abstract class GeneratedEntityFacts
{
    private const string DeskLive = "desk-live.alvo.json";

    private const string DeskV1 = "desk-v1.alvo.json";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_generated_wrapper_round_trips_a_field_its_descriptor_did_not_declare()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var session = Session(alvo);
        var created = await session.CreateAsync("orders", new Dictionary<string, object?> { ["reference"] = "R-1", ["legacy_code"] = "L-7" }, Ct);
        var id = (Guid)created[AlvoManagedColumns.Id]!;

        var older = (await session.GetAsync<V1.Order>(id, Ct)).ShouldNotBeNull();
        await session.ReplaceAsync(id, older, cancellationToken: Ct);

        var stored = (await session.GetAsync("orders", id, Ct)).ShouldNotBeNull();
        (stored["legacy_code"], stored["reference"]).ShouldBe(("L-7", "R-1"));
    }

    [Fact]
    public async Task A_generated_create_leaves_out_null_and_store_maintained_fields()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var session = Session(alvo);

        var order = (await session.CreateAsync(Order.Create(reference: "A-1"), Ct)).ShouldNotBeNull();
        await session.CreateAsync(OrderLine.Create(orderId: order.Id, note: null), Ct);

        order.Id.ShouldNotBe(Guid.Empty);
        (order.Reference, order.Status, order.LineCount.GetValueOrDefault()).ShouldBe(("A-1", OrderStatus.Draft, 0L));
        (await session.GetAsync<Order>(order.Id, Ct)).ShouldNotBeNull().LineCount.ShouldBe(1L);
    }

    [Fact]
    public async Task A_generated_patch_updates_through_its_typed_setters()
    {
        await using var alvo = await StartAsync(await DatabaseAsync(), _ => { });
        var session = Session(alvo);
        var order = (await session.CreateAsync(Order.Create(reference: "A-1"), Ct)).ShouldNotBeNull();

        await session.UpdateAsync(order.Id, Order.Patch().SetStatus(OrderStatus.Ok), cancellationToken: Ct);

        (await session.GetAsync("orders", order.Id, Ct)).ShouldNotBeNull()["status"].ShouldBe("ok");
    }

    [Fact]
    public async Task OnSaved_on_a_generated_type_sees_a_null_Old_on_create_and_the_previous_row_on_update()
    {
        var seen = new ConcurrentQueue<(bool Created, OrderStatus? Status)>();
        await using var alvo = await StartAsync(await DatabaseAsync(), builder => builder.Entity<Order>(orders => orders.OnSaved(
            "order-saved", (AlvoSaved<Order> e) => seen.Enqueue((e.Old is null, e.New.Status)))));
        var session = Session(alvo);
        var order = (await session.CreateAsync(Order.Create(reference: "A-1"), Ct)).ShouldNotBeNull();
        await session.UpdateAsync(order.Id, Order.Patch().SetStatus(OrderStatus.Ok), cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe([(true, OrderStatus.Draft), (false, OrderStatus.Ok)]);
    }

    [Fact]
    public async Task A_typed_condition_on_a_generated_enum_translates_to_its_declared_value()
    {
        var seen = new ConcurrentQueue<OrderStatus?>();
        await using var alvo = await StartAsync(await DatabaseAsync(), builder => builder.Entity<Order>(orders => orders.OnUpdated(
            "order-in-review",
            (AlvoUpdated<Order> e) => seen.Enqueue(e.New.Status),
            when: e => e.New.Status == OrderStatus.InReview)));
        var session = Session(alvo);
        var order = (await session.CreateAsync(Order.Create(reference: "A-1"), Ct)).ShouldNotBeNull();
        await session.UpdateAsync(order.Id, Order.Patch().SetStatus(OrderStatus.InReview), cancellationToken: Ct);
        await session.UpdateAsync(order.Id, Order.Patch().SetStatus(OrderStatus.Ok), cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct);

        seen.ShouldBe([OrderStatus.InReview]);
        alvo.Hooks.AssertRan("order-in-review", times: 1);
    }

    [Fact]
    public async Task A_boot_over_a_schema_without_a_generated_field_warns_once_naming_the_class_and_the_field()
    {
        var stale = new WarningCapture("GeneratedEntityStale");

        await using var alvo = await StartAsync(await DatabaseAsync(), DeskV1, builder => builder.Services.AddSingleton<ILoggerProvider>(stale));

        var line = stale.Lines.ShouldHaveSingleItem();
        line.ShouldContain("MMLib.Alvo.TestHost.Tests.Generated.Order");
        line.ShouldContain("legacy_code");
    }

    protected abstract Task<GeneratedDatabase> DatabaseAsync();

    private static IAlvoSession Session(AlvoTestHost alvo) =>
        alvo.Services.GetRequiredService<IAlvoSessions>().For(AlvoTestHost.Caller(DeskRoles.Agent));

    private static Task<AlvoTestHost> StartAsync(GeneratedDatabase database, Action<IAlvoBuilder> configure) =>
        StartAsync(database, DeskLive, configure);

    private static async Task<AlvoTestHost> StartAsync(GeneratedDatabase database, string descriptor, Action<IAlvoBuilder> configure) =>
        await AlvoTestHost.StartAsync(
            alvo =>
            {
                database.Use(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "generated", descriptor));
                configure(alvo);
            },
            cancellationToken: Ct);

    protected sealed record GeneratedDatabase(Action<IAlvoBuilder> Use);
}
