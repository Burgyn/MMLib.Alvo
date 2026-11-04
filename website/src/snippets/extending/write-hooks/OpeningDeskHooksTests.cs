using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace OpeningDesk.Tests;

public sealed class OpeningDeskHooksTests
{
    private static readonly Guid _court = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly DateTimeOffset _monday = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "opening-desk.alvo.json");

    [Fact]
    public async Task A_booking_in_opening_hours_is_stored_audited_and_confirmed_after_the_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = new RecordingMail();
        await using var alvo = await StartAsync(mail, registered: true, ct);
        var staff = AlvoTestHost.Caller("staff");

        var booking = await alvo.Data.CreateAsync("bookings", Booking(10, 12, vatId: "SK2020000000"), staff, cancellationToken: ct);
        await alvo.Data.UpdateAsync("bookings", (Guid)booking["id"]!, new Dictionary<string, object?> { ["status"] = "confirmed" }, staff, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "booking_audits" }, staff, ct)).Items.Select(row => row["note"]).ShouldBe(["booked", "changed"], ignoreOrder: true);
        mail.Sent.ShouldBe(["Ada: Your booking is confirmed."]);
        alvo.Hooks.AssertRan("within-opening-hours", times: 2);
    }

    [Fact]
    public async Task A_booking_outside_opening_hours_is_refused_at_its_field_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(new RecordingMail(), registered: true, ct);
        var staff = AlvoTestHost.Caller("staff");

        var refusal = await Should.ThrowAsync<AlvoWriteRejectedException>(
            () => alvo.Data.CreateAsync("bookings", Booking(19, 21), staff, cancellationToken: ct));

        (refusal.Kind, refusal.Code, refusal.Pointer).ShouldBe((AlvoWriteRejectionKind.Conflict, "outside-opening-hours", "/starts_at"));
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "bookings" }, staff, ct)).Items.ShouldBeEmpty();
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "booking_audits" }, staff, ct)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unregistered_vat_id_is_refused_before_the_write_opens_a_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await StartAsync(new RecordingMail(), registered: false, ct);

        var refusal = await Should.ThrowAsync<AlvoWriteRejectedException>(
            () => alvo.Data.CreateAsync("bookings", Booking(10, 12, vatId: "XX0"), AlvoTestHost.Caller("staff"), cancellationToken: ct));

        (refusal.Kind, refusal.Pointer).ShouldBe((AlvoWriteRejectionKind.Invalid, "/vat_id"));
    }

    private static async Task<AlvoTestHost> StartAsync(RecordingMail mail, bool registered, CancellationToken ct)
    {
        var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddOpeningDesk();
            builder.Services.AddSingleton<IVatRegistry>(new FixedRegistry(registered));
            builder.Services.AddSingleton<IGuestMail>(mail);
        }, time: new FakeTimeProvider(_monday), cancellationToken: ct);
        await alvo.Data.CreateAsync(
            "opening_hours",
            new Dictionary<string, object?> { ["resource_id"] = _court, ["weekday"] = 1L, ["opens_hour"] = 8L, ["closes_hour"] = 18L },
            AlvoTestHost.Caller("manager"),
            cancellationToken: ct);

        return alvo;
    }

    private static Dictionary<string, object?> Booking(int from, int to, string? vatId = null) => new()
    {
        ["resource_id"] = _court,
        ["guest"] = "Ada",
        ["starts_at"] = _monday.AddHours(from),
        ["ends_at"] = _monday.AddHours(to),
        ["vat_id"] = vatId,
    };

    private sealed class FixedRegistry(bool registered) : IVatRegistry
    {
        public Task<bool> IsRegisteredAsync(string vatId, CancellationToken cancellationToken) => Task.FromResult(registered);
    }

    private sealed class RecordingMail : IGuestMail
    {
        private readonly Lock _gate = new();
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        public Task SendAsync(string guest, string text, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _sent.Add($"{guest}: {text}");
            }

            return Task.CompletedTask;
        }
    }
}
