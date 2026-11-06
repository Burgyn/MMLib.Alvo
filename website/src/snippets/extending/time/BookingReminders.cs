using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Scheduling;

namespace BookingReminders;

[AlvoEntity("bookings")]
public sealed class Booking
{
    public Guid Id { get; init; }

    public required string Customer { get; init; }

    public DateTimeOffset StartsAt { get; init; }
}

[AlvoEvent("bookings.reminder_due")]
public sealed record BookingReminderDue(Guid BookingId, string Customer, DateTimeOffset StartsAt);

public static class BookingReminders
{
    public static IAlvoBuilder AddBookingReminders(this IAlvoBuilder alvo) => alvo
        .Entity<Booking>(bookings => bookings
            .OnCreated("arm-reminder", (AlvoCreated<Booking> created, IAlvoScheduler scheduler, CancellationToken ct) =>
                ArmAsync(scheduler, created.New, ct))
            .OnUpdated(
                "move-reminder",
                when: e => e.Changed(x => x.StartsAt),
                handler: (AlvoUpdated<Booking> moved, IAlvoScheduler scheduler, CancellationToken ct) => ArmAsync(scheduler, moved.New, ct))
            .OnDeleted("cancel-reminder", (AlvoDeleted<Booking> deleted, IAlvoScheduler scheduler, CancellationToken ct) =>
                DisarmAsync(scheduler, deleted.Old, ct)))
        .AddEventHandler<BookingReminderDue, SendReminder>("send-reminder")
        .AddSchedule<RollSlots>(
            "roll-slots",
            "0 2 * * *",
            "Europe/Bratislava",
            options => options.Description = "Opens the booking slots of the day after tomorrow.");

    private static Task ArmAsync(IAlvoScheduler scheduler, Booking booking, CancellationToken ct) =>
        scheduler.ScheduleAsync(
            Key(booking.Id),
            booking.StartsAt.AddHours(-24),
            new BookingReminderDue(booking.Id, booking.Customer, booking.StartsAt),
            cancellationToken: ct);

    private static async Task DisarmAsync(IAlvoScheduler scheduler, Booking booking, CancellationToken ct) =>
        await scheduler.CancelAsync(Key(booking.Id), cancellationToken: ct);

    private static string Key(Guid booking) => $"reminder:{booking}";
}

public interface IReminderSender
{
    Task SendAsync(BookingReminderDue reminder, CancellationToken cancellationToken);
}

public sealed class SendReminder(IReminderSender sender) : IAlvoEventHandler<BookingReminderDue>
{
    public Task HandleAsync(BookingReminderDue reminder, AlvoEventContext context, CancellationToken cancellationToken) =>
        sender.SendAsync(reminder, cancellationToken);
}

public sealed class RollSlots : IAlvoScheduledJob
{
    private static readonly TimeZoneInfo _bratislava = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    public async Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken)
    {
        var day = TimeZoneInfo.ConvertTime(context.ScheduledFor, _bratislava).AddDays(2);
        await context.Data.CreateAsync(
            "slot_days",
            new Dictionary<string, object?> { ["day"] = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);
    }
}
