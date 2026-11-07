using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using Snippets.Generated;

using System.Collections.Concurrent;

namespace FrontDesk;

/// <summary>A generated wrapper, its patch, a typed handler over it and its role constants, as the Generated types page shows them.</summary>
public static class GeneratedTypesSnippets
{
    /// <summary>The role a front-desk caller has: a constant, so it fits an attribute or a <c>switch</c>.</summary>
    public const string FrontDeskRole = BookingRoles.Staff;

    /// <summary>Creates a booking, confirms it through the generated patch, and reads it back typed.</summary>
    public static async Task<Booking?> BookAndConfirmAsync(IAlvoSession session, CancellationToken cancellationToken)
    {
        var booking = await session.CreateAsync(Booking.Create(guest: "Ada", nights: 2), cancellationToken)
            ?? throw new InvalidOperationException("The caller may not read the booking back.");

        await session.UpdateAsync(booking.Id, Booking.Patch().SetStatus(BookingStatus.Confirmed), cancellationToken: cancellationToken);

        return await session.GetAsync<Booking>(booking.Id, cancellationToken);
    }

    /// <summary>Registers a typed handler on the generated class: <c>e.Old</c> is null on a create.</summary>
    public static IAlvoBuilder AddBookingLog(this IAlvoBuilder alvo, ConcurrentQueue<string> log) =>
        alvo.Entity<Booking>(bookings => bookings.OnSaved("booking-log", (AlvoSaved<Booking> e) =>
            log.Enqueue(e.Old is null ? $"new booking for {e.New.Guest}" : $"{e.New.Guest}: {e.Old.Status} -> {e.New.Status}")));
}
