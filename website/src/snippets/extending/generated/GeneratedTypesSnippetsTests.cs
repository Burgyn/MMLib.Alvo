using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Snippets.Generated;
using System.Collections.Concurrent;
using Xunit;

namespace FrontDesk.Tests;

public sealed class GeneratedTypesSnippetsTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "booking.alvo.json");

    [Fact]
    public async Task Generated_wrapper_creates_reads_and_patches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor), cancellationToken: ct);
        var session = alvo.Services.GetRequiredService<IAlvoSessions>().For(AlvoTestHost.Caller(BookingRoles.Staff));

        var booking = await GeneratedTypesSnippets.BookAndConfirmAsync(session, ct);

        booking.ShouldNotBeNull();
        (booking.Guest, booking.Nights, booking.Status).ShouldBe(("Ada", 2L, BookingStatus.Confirmed));
        booking.Record.Values.ShouldNotContainKey("internal_note");
    }

    [Fact]
    public async Task A_typed_handler_on_a_generated_class_sees_Old_null_on_create()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new ConcurrentQueue<string>();
        await using var alvo = await AlvoTestHost.StartAsync(builder => builder.FromDescriptor(Descriptor).AddBookingLog(log), cancellationToken: ct);
        var session = alvo.Services.GetRequiredService<IAlvoSessions>().For(AlvoTestHost.Caller(BookingRoles.Staff));

        await GeneratedTypesSnippets.BookAndConfirmAsync(session, ct);
        await alvo.DrainEventsAsync(ct);

        log.ShouldBe(["new booking for Ada", "Ada: Requested -> Confirmed"]);
    }

    [Fact]
    public void Roles_are_constants()
    {
        GeneratedTypesSnippets.FrontDeskRole.ShouldBe("staff");
        (BookingRoles.Anon, BookingRoles.Authenticated, BookingRoles.Admin).ShouldBe(("anon", "authenticated", "admin"));
    }
}
