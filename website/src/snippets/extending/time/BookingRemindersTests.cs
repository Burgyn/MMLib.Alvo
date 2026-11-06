using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace BookingReminders.Tests;

public sealed class BookingRemindersTests
{
    private static readonly DateTimeOffset _monday = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "booking-reminders.alvo.json");

    [Fact]
    public async Task The_reminder_arrives_a_day_before_and_the_nightly_roll_runs_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(_monday);
        var sent = new RecordingSender();
        await using var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddBookingReminders();
            builder.Services.AddSingleton<IReminderSender>(sent);
        }, time, cancellationToken: ct);
        var staff = AlvoTestHost.Caller("staff");

        var booking = await alvo.Data.CreateAsync(
            "bookings", new Dictionary<string, object?> { ["customer"] = "Jana", ["starts_at"] = _monday.AddDays(1).AddHours(4) }, staff, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);
        await alvo.Data.UpdateAsync(
            "bookings", (Guid)booking["id"]!, new Dictionary<string, object?> { ["starts_at"] = _monday.AddDays(1).AddHours(10) }, staff, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        time.Advance(TimeSpan.FromHours(4));
        await alvo.DrainEventsAsync(ct);
        sent.Reminders.ShouldBeEmpty("the booking moved; its reminder moved with it, under the same key");

        time.Advance(TimeSpan.FromHours(6));
        await alvo.DrainEventsAsync(ct);
        sent.Reminders.ShouldHaveSingleItem().StartsAt.ShouldBe(_monday.AddDays(1).AddHours(10));

        time.Advance(TimeSpan.FromHours(7));
        await alvo.DrainEventsAsync(ct);
        await alvo.DrainEventsAsync(ct);
        var rolled = await alvo.Data.QueryAsync(new AlvoQuery { Entity = "slot_days" }, staff, ct);
        rolled.Items.ShouldHaveSingleItem()["day"].ShouldBe("2026-11-12");
    }

    private sealed class RecordingSender : IReminderSender
    {
        public List<BookingReminderDue> Reminders { get; } = [];

        public Task SendAsync(BookingReminderDue reminder, CancellationToken cancellationToken)
        {
            Reminders.Add(reminder);
            return Task.CompletedTask;
        }
    }
}
