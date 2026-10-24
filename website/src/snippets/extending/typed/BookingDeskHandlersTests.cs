using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Management;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace BookingDesk.Tests;

public sealed class BookingDeskHandlersTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "booking-desk.alvo.json");

    [Fact]
    public async Task Confirming_a_booking_sends_one_confirmation_and_the_catalog_shows_its_condition_as_cel()
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = new RecordingMail();
        await using var alvo = await StartAsync(mail, ct);
        var staff = AlvoTestHost.Caller("staff");

        var booking = await alvo.Data.CreateAsync(
            "bookings", new Dictionary<string, object?> { ["guest"] = "Ada", ["status"] = "requested", ["nights"] = 2L }, staff, cancellationToken: ct);
        await alvo.Data.UpdateAsync("bookings", (Guid)booking["id"]!, new Dictionary<string, object?> { ["status"] = "confirmed" }, staff, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        mail.Sent.ShouldBe(["Ada: Booking confirmed: 1 confirmed stay(s) with us"]);
        alvo.Hooks.AssertRan("long-stay", times: 0);
        var catalog = await AsAdministrator(alvo).GetExtensionsAsync("booking-desk", ct);
        catalog.Items.Single(item => item.Id == "send-confirmation").When.ShouldBe("changed(status) && new.status == 'confirmed'");
    }

    [Fact]
    public async Task A_class_handler_can_be_called_directly()
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = new RecordingMail();
        await using var alvo = await StartAsync(mail, ct);
        var staff = AlvoTestHost.Caller("staff");
        var created = await alvo.Data.CreateAsync(
            "bookings", new Dictionary<string, object?> { ["guest"] = "Grace", ["status"] = "confirmed", ["nights"] = 1L }, staff, cancellationToken: ct);
        var stored = (await alvo.Data.GetAsync<Booking>((Guid)created["id"]!, staff, ct))!;

        await new SendConfirmation(mail).HandleAsync(new AlvoUpdated<Booking> { Old = stored, New = stored }, alvo.EventContext(staff), ct);

        mail.Sent.ShouldBe(["Grace: Booking confirmed: 1 confirmed stay(s) with us"]);
    }

    [Fact]
    public void A_condition_with_no_cel_equivalent_is_refused_when_it_is_registered()
    {
        var refusal = Should.Throw<AlvoTranslationException>(() => new ServiceCollection().AddAlvo(alvo => alvo.Entity<Booking>(bookings =>
            bookings.OnUpdated<SendConfirmation>("bad", when: e => e.New.Guest.GetHashCode() == 7))));

        refusal.Message.ShouldStartWith("Cannot translate 'e.New.Guest.GetHashCode()' in extension 'bad' (when)");
    }

    private static Task<AlvoTestHost> StartAsync(RecordingMail mail, CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddBookingDesk();
            builder.Services.AddSingleton<IGuestMail>(mail);
        }, cancellationToken: ct);

    private static IAlvoManagement AsAdministrator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("admin"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "docs",
        };
        return alvo.Services.GetRequiredService<IAlvoManagement>();
    }

    private sealed class RecordingMail : IGuestMail
    {
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent => _sent;

        public Task SendAsync(string guest, string text, CancellationToken cancellationToken)
        {
            _sent.Add($"{guest}: {text}");
            return Task.CompletedTask;
        }
    }
}
