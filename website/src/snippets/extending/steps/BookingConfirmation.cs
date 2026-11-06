using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;

namespace BookingConfirmation;

[AlvoEntity("bookings")]
public sealed class Booking
{
    public Guid Id { get; init; }

    public required string Customer { get; init; }

    public DateTimeOffset StartsAt { get; init; }
}

public static class BookingConfirmation
{
    public static IAlvoBuilder AddBookingConfirmation(this IAlvoBuilder alvo) => alvo
        .Entity<Booking>(bookings => bookings
            .OnCreated(
                "confirm-booking",
                ConfirmAsync,
                configure: options => options.Concurrency = AlvoConcurrency.PerKey("tenant", 2))
            .OnUpdated(
                "sync-calendar",
                (AlvoUpdated<Booking> changed, ICalendar calendar, CancellationToken ct) => calendar.SyncAsync(changed.New, ct),
                configure: options => options.Debounce = TimeSpan.FromSeconds(5)));

    private static async Task ConfirmAsync(
        AlvoCreated<Booking> created, AlvoEventContext context, IPayments payments, IMailer mailer, CancellationToken ct)
    {
        var booking = created.New;
        var receipt = await context.Steps.RunAsync("charge-deposit", token => payments.ChargeDepositAsync(booking.Id, token), ct);
        await context.Steps.RunAsync("send-confirmation", token => mailer.SendConfirmationAsync(booking, receipt, token), ct);
        await context.Steps.SleepUntilAsync("day-before", booking.StartsAt.AddDays(-1), ct);
        await context.Steps.RunAsync("send-reminder", token => mailer.SendReminderAsync(booking, token), ct);
    }
}

public interface IPayments
{
    Task<string> ChargeDepositAsync(Guid booking, CancellationToken cancellationToken);
}

public interface IMailer
{
    Task SendConfirmationAsync(Booking booking, string receipt, CancellationToken cancellationToken);

    Task SendReminderAsync(Booking booking, CancellationToken cancellationToken);
}

public interface ICalendar
{
    Task SyncAsync(Booking booking, CancellationToken cancellationToken);
}
