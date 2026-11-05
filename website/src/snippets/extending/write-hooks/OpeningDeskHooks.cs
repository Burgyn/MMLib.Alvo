using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;

namespace OpeningDesk;

[AlvoEntity("bookings")]
public sealed class Booking
{
    public Guid Id { get; init; }

    public Guid ResourceId { get; init; }

    public required string Guest { get; init; }

    public DateTimeOffset StartsAt { get; init; }

    public DateTimeOffset EndsAt { get; init; }

    public string? VatId { get; init; }

    public string? Status { get; init; }
}

[AlvoEntity("opening_hours")]
public sealed class OpeningHours
{
    public Guid ResourceId { get; init; }

    public long Weekday { get; init; }

    public long OpensHour { get; init; }

    public long ClosesHour { get; init; }
}

public static class OpeningDeskHooks
{
    public static IAlvoBuilder AddOpeningDesk(this IAlvoBuilder alvo) => alvo
        .Entity<Booking>(bookings => bookings
            .Validate<VatIdIsRegistered>(
                "vat-in-vies",
                when: w => w.New.VatId != null,
                configure: o => o.Timeout = TimeSpan.FromSeconds(3))
            .BeforeWrite<WithinOpeningHours>("within-opening-hours")
            .AfterWrite(
                "audit-booking",
                async (AfterWrite<Booking> write, CancellationToken ct) =>
                {
                    var note = write.Old is null ? "booked" : "changed";
                    await write.Transaction.CreateAsync("booking_audits", new Dictionary<string, object?> { ["booking_id"] = write.Saved.Id, ["note"] = note }, ct);
                })
            .OnUpdated<SendBookingConfirmation>(
                "send-confirmation",
                when: e => e.Changed(x => x.Status) && e.New.Status == "confirmed"));
}

public interface IVatRegistry
{
    Task<bool> IsRegisteredAsync(string vatId, CancellationToken cancellationToken);
}

public interface IGuestMail
{
    Task SendAsync(string guest, string text, CancellationToken cancellationToken);
}

public sealed class VatIdIsRegistered(IVatRegistry registry) : IValidateWriteHook<Booking>
{
    public async ValueTask ValidateAsync(ValidateWrite<Booking> write, CancellationToken cancellationToken)
    {
        if (write.New.VatId is { } vatId && !await registry.IsRegisteredAsync(vatId, cancellationToken))
        {
            write.Invalid("vat-not-registered", "This VAT id is not registered in VIES.", "vat_id");
        }
    }
}

public sealed class WithinOpeningHours : IBeforeWriteHook<Booking>
{
    public async ValueTask BeforeWriteAsync(BeforeWrite<Booking> write, CancellationToken cancellationToken)
    {
        var booking = write.New;
        var resource = booking.ResourceId;
        var weekday = (long)booking.StartsAt.UtcDateTime.DayOfWeek;
        var hours = await write.Transaction.AsSystem()
            .QueryAsync<OpeningHours>(x => x.ResourceId == resource && x.Weekday == weekday, limit: 1, cancellationToken);

        if (hours.Count == 0 || booking.StartsAt.UtcDateTime.Hour < hours[0].OpensHour || booking.EndsAt.UtcDateTime.Hour > hours[0].ClosesHour)
        {
            write.Conflict("outside-opening-hours", "The resource is closed at that time.", "starts_at");
        }
    }
}

public sealed class SendBookingConfirmation(IGuestMail mail) : IAlvoEventHandler<AlvoUpdated<Booking>>
{
    public Task HandleAsync(AlvoUpdated<Booking> confirmed, AlvoEventContext context, CancellationToken cancellationToken) =>
        mail.SendAsync(confirmed.New.Guest, "Your booking is confirmed.", cancellationToken);
}
