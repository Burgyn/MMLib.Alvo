using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Commands;

/// <summary>A typed access lambda becomes the CEL a string rule would be (spec 2026-10-14 §4.9: "translated like when").</summary>
public sealed class AccessTranslatorTests
{
    [Fact]
    public void A_caller_only_rule_translates_to_a_role_test() =>
        AccessTranslator.Translate(caller => caller.HasRole("staff"), "quote_stay").ShouldBe("'staff' in @user.roles");

    [Fact]
    public void A_row_rule_reads_the_row_as_its_current_image() =>
        AccessTranslator.Translate<Booking>((booking, caller) => booking.Status == BookingStatus.Requested && caller.HasRole("staff"), "confirm_booking")
            .ShouldBe("new.status == 'requested' && 'staff' in @user.roles");

    [Fact]
    public void A_rule_comparing_the_row_with_the_caller_translates_both_sides() =>
        AccessTranslator.Translate<Booking>((booking, caller) => booking.GuestId == caller.User, "cancel_booking")
            .ShouldBe("new.guest_id == @user.id");

    [Fact]
    public void An_untranslatable_rule_is_refused_naming_the_access_slot()
    {
        var refusal = Should.Throw<AlvoTranslationException>(() =>
            AccessTranslator.Translate<Booking>((booking, _) => booking.Room.GetHashCode() == 7, "confirm_booking"));

        refusal.Message.ShouldContain("'confirm_booking' (access)");
        refusal.FixSuggestion.ShouldContain("access: \"…\"");
    }

    [AlvoEntity("bookings")]
    public sealed class Booking
    {
        public Guid GuestId { get; init; }

        public required string Room { get; init; }

        public BookingStatus Status { get; init; }
    }

    public enum BookingStatus
    {
        Requested,
        Confirmed,
        Cancelled,
    }
}
