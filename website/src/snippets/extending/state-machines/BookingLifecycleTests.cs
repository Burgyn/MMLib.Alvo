using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace Bookings.Tests;

public sealed class BookingLifecycleTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly TenantId _tenant = TenantId.New();

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "booking-lifecycle.alvo.json");

    [Fact]
    public async Task Skipping_a_step_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), time: new FakeTimeProvider(_now), cancellationToken: ct);
        var booking = await alvo.Data.CreateAsync("bookings", Booking(_now.AddDays(2)), Caller("staff"), cancellationToken: ct);

        var refusal = await Should.ThrowAsync<AlvoConstraintViolationException>(
            () => alvo.Data.UpdateAsync("bookings", (Guid)booking["id"]!, Status("done"), Caller("manager"), cancellationToken: ct));

        (refusal.Kind, refusal.Fields.Single()).ShouldBe((AlvoConstraintKind.Transition, "status"));
    }

    [Fact]
    public async Task Only_a_manager_closes_a_booking()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), time: new FakeTimeProvider(_now), cancellationToken: ct);
        var id = (Guid)(await alvo.Data.CreateAsync("bookings", Booking(_now.AddDays(2)), Caller("staff"), cancellationToken: ct))["id"]!;
        await alvo.Data.UpdateAsync("bookings", id, Status("confirmed"), Caller("staff"), cancellationToken: ct);

        await Should.ThrowAsync<AlvoAuthorizationException>(
            () => alvo.Data.UpdateAsync("bookings", id, Status("done"), Caller("staff"), cancellationToken: ct));
        var closed = await alvo.Data.UpdateAsync("bookings", id, Status("done"), Caller("manager"), cancellationToken: ct);

        closed["status"].ShouldBe("done");
    }

    [Fact]
    public async Task A_confirmed_booking_is_reminded_a_day_before()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(_now);
        var reminded = new List<AlvoEvent>();
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder
                .FromDescriptor(Descriptor)
                .AddEventHandler("remind", "bookings.reminder_due", (Func<AlvoRecord, AlvoEventContext, CancellationToken, Task>)((_, context, _) =>
                {
                    reminded.Add(context.Event);
                    return Task.CompletedTask;
                })),
            time: clock,
            cancellationToken: ct);
        var id = (Guid)(await alvo.Data.CreateAsync("bookings", Booking(_now.AddDays(3)), Caller("staff"), cancellationToken: ct))["id"]!;
        await alvo.Data.UpdateAsync("bookings", id, Status("confirmed"), Caller("staff"), cancellationToken: ct);

        clock.Advance(TimeSpan.FromDays(1));
        await alvo.DrainEventsAsync(ct);
        reminded.ShouldBeEmpty();
        clock.Advance(TimeSpan.FromDays(1));
        await alvo.DrainEventsAsync(ct);

        reminded.ShouldHaveSingleItem().Data.Record.ShouldNotBeNull()["timer"].ShouldBe("reminder");
    }

    private static AlvoContext Caller(string role) => AlvoTestHost.Caller(role) with { Tenant = _tenant };

    private static Dictionary<string, object?> Booking(DateTimeOffset startsAt) => new()
    {
        ["starts_at"] = startsAt,
        ["note"] = "Board meeting",
        ["tenant_id"] = _tenant.Value,
    };

    private static Dictionary<string, object?> Status(string status) => new() { ["status"] = status };
}
