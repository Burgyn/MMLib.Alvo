using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace BookingDesk;

public enum BookingStatus
{
    Requested,
    Confirmed,
    Cancelled,
}

[AlvoEntity("bookings")]
public sealed record Booking(Guid Id, string Guest, BookingStatus Status, long Nights, decimal? Total);

public static class BookingDeskHandlers
{
    public static IAlvoBuilder AddBookingDesk(this IAlvoBuilder alvo) => alvo
        .Entity<Booking>(bookings => bookings
            .OnUpdated<SendConfirmation>(
                "send-confirmation",
                when: e => e.Changed(x => x.Status) && e.New.Status == BookingStatus.Confirmed)
            .OnCreated(
                "long-stay",
                (AlvoCreated<Booking> created, IGuestMail mail, CancellationToken ct) => mail.SendAsync(created.New.Guest, "Enjoy your long stay", ct),
                when: "new.nights >= 7"));
}

public interface IGuestMail
{
    Task SendAsync(string guest, string text, CancellationToken cancellationToken);
}

public sealed class SendConfirmation(IGuestMail mail) : IAlvoEventHandler<AlvoUpdated<Booking>>
{
    public async Task HandleAsync(AlvoUpdated<Booking> confirmed, AlvoEventContext context, CancellationToken cancellationToken)
    {
        var guest = confirmed.New.Guest;
        var stays = await context.Data.QueryAsync<Booking>(x => x.Guest == guest && x.Status == BookingStatus.Confirmed, limit: 50, cancellationToken);
        await mail.SendAsync(guest, $"Booking confirmed: {stays.Count} confirmed stay(s) with us", cancellationToken);
    }
}
