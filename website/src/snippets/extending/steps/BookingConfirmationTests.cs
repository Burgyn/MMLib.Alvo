using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace BookingConfirmation.Tests;

public sealed class BookingConfirmationTests
{
    private static readonly DateTimeOffset _monday = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "booking-reminders.alvo.json");

    [Fact]
    public async Task The_deposit_is_charged_once_though_the_email_failed_and_the_reminder_waits_for_its_day()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(_monday);
        var desk = new RecordingDesk { FailFirstConfirmation = true };
        await using var alvo = await StartAsync(time, desk, ct);
        var staff = AlvoTestHost.Caller("staff");

        var booking = await alvo.Data.CreateAsync(
            "bookings", new Dictionary<string, object?> { ["customer"] = "Jana", ["starts_at"] = _monday.AddHours(34) }, staff, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);
        (desk.Charges, desk.Confirmations).ShouldBe((1, 0), "the confirmation e-mail failed; the delivery retries");

        time.Advance(TimeSpan.FromMinutes(1));
        await alvo.DrainEventsAsync(ct);
        (desk.Charges, desk.Confirmations, desk.Reminders).ShouldBe((1, 1, 0), "the retry skipped the completed charge and now sleeps");

        time.Advance(TimeSpan.FromHours(10));
        await alvo.DrainEventsAsync(ct);
        (desk.Charges, desk.Confirmations, desk.Reminders).ShouldBe((1, 1, 1), "the reminder went out a day before the booking");

        var id = (Guid)booking["id"]!;
        foreach (var customer in new[] { "Jana N.", "Jana Nová", "Jana Nováková" })
        {
            await alvo.Data.UpdateAsync("bookings", id, new Dictionary<string, object?> { ["customer"] = customer }, staff, cancellationToken: ct);
            await alvo.DrainEventsAsync(ct);
        }

        time.Advance(TimeSpan.FromSeconds(5));
        await alvo.DrainEventsAsync(ct);
        desk.Synced.ShouldBe(["Jana Nováková"], "three edits within the debounce are one sync of the latest");
    }

    private static Task<AlvoTestHost> StartAsync(FakeTimeProvider time, RecordingDesk desk, CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddBookingConfirmation();
            builder.Services.AddSingleton<IPayments>(desk).AddSingleton<IMailer>(desk).AddSingleton<ICalendar>(desk);
        }, time, cancellationToken: ct);

    private sealed class RecordingDesk : IPayments, IMailer, ICalendar
    {
        public bool FailFirstConfirmation { get; set; }

        public int Charges { get; private set; }

        public int Confirmations { get; private set; }

        public int Reminders { get; private set; }

        public List<string> Synced { get; } = [];

        public Task<string> ChargeDepositAsync(Guid booking, CancellationToken cancellationToken)
        {
            Charges++;
            return Task.FromResult($"receipt-{booking:N}");
        }

        public Task SendConfirmationAsync(Booking booking, string receipt, CancellationToken cancellationToken)
        {
            if (FailFirstConfirmation)
            {
                FailFirstConfirmation = false;
                throw new HttpRequestException("the mail provider is down");
            }

            Confirmations++;
            return Task.CompletedTask;
        }

        public Task SendReminderAsync(Booking booking, CancellationToken cancellationToken)
        {
            Reminders++;
            return Task.CompletedTask;
        }

        public Task SyncAsync(Booking booking, CancellationToken cancellationToken)
        {
            Synced.Add(booking.Customer);
            return Task.CompletedTask;
        }
    }
}
