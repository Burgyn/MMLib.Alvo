using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace Inventory.Tests;

public sealed class InventoryIntegrityTests
{
    private static readonly DateTimeOffset _monday = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "inventory.alvo.json");

    [Fact]
    public async Task A_reservation_past_the_stock_is_refused_at_its_quantity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), time: new FakeTimeProvider(_monday), cancellationToken: ct);
        var clerk = AlvoTestHost.Caller("clerk");
        var lamp = await alvo.Data.CreateAsync("products", new Dictionary<string, object?> { ["name"] = "Lamp", ["stock"] = 2L }, clerk, cancellationToken: ct);

        await alvo.Data.CreateAsync("reservations", Reservation(lamp, 2), clerk, cancellationToken: ct);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => alvo.Data.CreateAsync("reservations", Reservation(lamp, 1), clerk, cancellationToken: ct));

        (refusal.Kind, refusal.Fields.Single()).ShouldBe((AlvoConstraintKind.Check, "quantity"));
        (await alvo.Data.GetAsync("products", (Guid)lamp["id"]!, clerk, ct))!["reserved"].ShouldBe(2L);
    }

    [Fact]
    public async Task A_sale_longer_than_thirty_days_is_refused_at_its_end()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), time: new FakeTimeProvider(_monday), cancellationToken: ct);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(() => alvo.Data.CreateAsync("products", new Dictionary<string, object?>
        {
            ["name"] = "Lamp",
            ["stock"] = 1L,
            ["sale_starts_at"] = _monday,
            ["sale_ends_at"] = _monday.AddDays(31),
        }, AlvoTestHost.Caller("clerk"), cancellationToken: ct));

        refusal.Fields.ShouldBe(["sale_ends_at"]);
    }

    private static Dictionary<string, object?> Reservation(AlvoRecord product, long quantity) =>
        new() { ["product_id"] = product["id"], ["quantity"] = quantity, ["status"] = "held" };
}
