using Consumer.Model;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Events;

namespace PackedConsumer;

/// <summary>Uses what the generator emitted from booking.alvo.json, so the build proves it exists and compiles.</summary>
internal static class GeneratedUse
{
    internal static string Admin => BookingRoles.Admin;

    internal static IServiceCollection Configure(IServiceCollection services)
    {
        services.AddAlvo(alvo => alvo.Entity<Booking>(bookings => bookings.OnSaved(
            "booking-saved", (AlvoSaved<Booking> e) => Console.WriteLine(e.Old is null ? "created" : e.Old.Guest))));

        return services;
    }

    internal static Booking Draft() => Booking.Create(guest: "Ada", nights: 2);

    internal static BookingPatch Confirm() => Booking.Patch().SetStatus(BookingStatus.Confirmed);
}
