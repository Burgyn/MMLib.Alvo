using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Schema;

using System.Collections.Concurrent;
using System.Globalization;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>What a flow-control fact's handler saw: the seats of each booking it was delivered.</summary>
public sealed class SeatLog
{
    public ConcurrentQueue<int> Seats { get; } = new();
}

/// <summary>Flow control over the real pipeline: a debounced handler runs once, with the latest of a burst.</summary>
public abstract partial class StepFacts
{
    [Fact]
    public async Task Ten_rapid_updates_with_a_five_second_debounce_deliver_once_the_latest()
    {
        var seats = new SeatLog();
        await using var alvo = await StartOnUpdatedAsync(seats, options => options.Debounce = TimeSpan.FromSeconds(5));
        var booking = await alvo.Data.CreateAsync("bookings", Booking("b1"), Staff(Tenant), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        for (var count = 1; count <= 10; count++)
        {
            await alvo.Data.UpdateAsync("bookings", (Guid)booking[AlvoManagedColumns.Id]!, Seats(count), Staff(Tenant), cancellationToken: Ct);
            Time.Advance(TimeSpan.FromMilliseconds(200));
            await alvo.DrainEventsAsync(Ct);
        }

        seats.Seats.ShouldBeEmpty();
        Time.Advance(TimeSpan.FromSeconds(5));
        await alvo.DrainEventsAsync(Ct);

        seats.Seats.ShouldBe([10]);
        (await DeliveriesAsync(alvo)).Count(delivery => delivery.Status == "superseded").ShouldBe(9);
    }

    private async Task<AlvoTestHost> StartOnUpdatedAsync(SeatLog seats, Action<AlvoHandlerOptions> configure)
    {
        var database = await DatabaseAsync();

        return await AlvoTestHost.StartAsync(alvo =>
        {
            database.Use(alvo);
            alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "booking-desk.alvo.json"));
            alvo.Entity("bookings", bookings => bookings.OnUpdated(Handler, RecordSeats(seats), configure: configure));
        }, Time, cancellationToken: Ct);
    }

    private static Func<AlvoUpdated<AlvoRecord>, AlvoEventContext, CancellationToken, Task> RecordSeats(SeatLog log) =>
        (updated, _, _) =>
        {
            log.Seats.Enqueue(Convert.ToInt32(updated.New["seats"], CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        };

    private static Dictionary<string, object?> Seats(int count) => new() { ["seats"] = count };
}
