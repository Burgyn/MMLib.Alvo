using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace Bookings.Tests;

public sealed class BookingIntegrityTests
{
    private static readonly DateTimeOffset _today = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private static readonly AlvoContext _clerk = AlvoTestHost.Caller("clerk") with { Tenant = TenantId.New() };

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "bookings.alvo.json");

    [Fact]
    public async Task Bookings_are_numbered_per_room_and_year_and_never_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), time: new FakeTimeProvider(_today), cancellationToken: ct);
        var room = await alvo.Data.CreateAsync("rooms", Tenanted(new() { ["name"] = "Blue room" }), _clerk, cancellationToken: ct);

        var first = await alvo.Data.CreateAsync("bookings", Booking(room, 9, 10), _clerk, cancellationToken: ct);
        var backToBack = await alvo.Data.CreateAsync("bookings", Booking(room, 10, 11), _clerk, cancellationToken: ct);
        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => alvo.Data.CreateAsync("bookings", Booking(room, 10.5, 12), _clerk, cancellationToken: ct));

        (first["number"], backToBack["number"]).ShouldBe(("B-2026-001", "B-2026-002"));
        (refusal.Kind, refusal.Fields.Single()).ShouldBe((AlvoConstraintKind.Exclusion, "starts_at"));
    }

    [Fact]
    public async Task A_cancelled_booking_may_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), time: new FakeTimeProvider(_today), cancellationToken: ct);
        var room = await alvo.Data.CreateAsync("rooms", Tenanted(new() { ["name"] = "Blue room" }), _clerk, cancellationToken: ct);
        await alvo.Data.CreateAsync("bookings", Booking(room, 9, 10), _clerk, cancellationToken: ct);

        var cancelled = await alvo.Data.CreateAsync("bookings", Booking(room, 9, 10, status: "cancelled"), _clerk, cancellationToken: ct);

        cancelled["number"].ShouldBe("B-2026-002");
    }

    private static Dictionary<string, object?> Booking(AlvoRecord room, double from, double to, string status = "confirmed") => Tenanted(new()
    {
        ["room"] = room["id"],
        ["starts_at"] = _today.Date.AddHours(from),
        ["ends_at"] = _today.Date.AddHours(to),
        ["status"] = status,
    });

    private static Dictionary<string, object?> Tenanted(Dictionary<string, object?> values)
    {
        values["tenant_id"] = _clerk.Tenant!.Value.Value;
        return values;
    }
}
