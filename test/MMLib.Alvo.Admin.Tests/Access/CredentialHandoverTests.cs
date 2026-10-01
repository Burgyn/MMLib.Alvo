using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Components.Access;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Access;

/// <summary>
/// What the token panel shows and what its Copy link puts on the clipboard: the set-password link, the
/// out-of-band sentence with its expiry, and the note for a person who is disabled.
/// </summary>
public sealed class CredentialHandoverTests
{
    private static readonly NavigationManager _navigation = new FixedNavigation("https://alvo.example/");
    private static readonly DateTimeOffset _expires = new(2026, 9, 28, 14, 5, 0, TimeSpan.Zero);

    /// <summary>When the token was issued: a day before it expires, so the expiry is on another day.</summary>
    private static readonly DateTimeOffset _issued = _expires.AddDays(-1);

    [Fact]
    public void The_panel_shows_the_set_password_link_and_copy_puts_the_link_on_the_clipboard()
    {
        var handover = For(Person(disabled: false), TimeSpan.Zero);

        handover.Link.ShouldBe(SetPasswordLink.For(_navigation, "eva@alvo.test", "CfDJ8+a/b="));
        handover.Clipboard.ShouldBe(handover.Link, "Copy link copies the link, not the bare token");
        handover.Token.ShouldBe("CfDJ8+a/b=");
    }

    /// <summary>
    /// The expiry is in the operator's own time, as the lockout above it in the same editor is (final branch review,
    /// item 3): one editor, one clock.
    /// </summary>
    [Fact]
    public void The_panel_says_how_to_hand_it_over_and_until_when_in_the_operators_time()
        => For(Person(disabled: false), TimeSpan.FromHours(2)).Instruction
            .ShouldBe("Hand this over out of band: this build sends no email. It works once, until 2026-09-28 16:05.");

    /// <summary>Before the browser has said its offset, the expiry is UTC and says so, like the lockout.</summary>
    [Fact]
    public void An_operator_whose_offset_is_not_known_yet_reads_the_expiry_in_UTC_said_as_such()
        => For(Person(disabled: false), offset: null).Instruction
            .ShouldBe(
                "Hand this over out of band: this build sends no email. It works once, until 2026-09-28 14:05 UTC.");

    [Fact]
    public void A_disabled_person_is_told_they_cannot_use_it_until_they_are_let_back_in()
    {
        For(Person(disabled: true), TimeSpan.Zero).DisabledNote
            .ShouldBe("They cannot use it until you let them back in.");
        For(Person(disabled: false), TimeSpan.Zero).DisabledNote.ShouldBeNull();
    }

    /// <summary>
    /// A host that maps no set-password post (an embedded one that did not map its own) gets the bare token, and one
    /// sentence saying why: a link would open a form that posts nowhere (final branch review, item 15).
    /// </summary>
    [Fact]
    public void Without_a_set_password_page_the_handover_is_the_bare_token_and_says_why()
    {
        var handover = CredentialHandover.For(
            _navigation, Person(disabled: false), Token(), _issued, TimeSpan.Zero, hasSetPasswordPage: false);

        handover.Link.ShouldBeNull();
        handover.Clipboard.ShouldBe("CfDJ8+a/b=", "Copy copies the token when there is no page to link to");
        handover.NoPageNote.ShouldBe("This host has no set-password page — hand the token to your own flow.");
    }

    [Fact]
    public void With_a_set_password_page_there_is_no_such_note()
        => For(Person(disabled: false), TimeSpan.Zero).NoPageNote.ShouldBeNull();

    private static CredentialHandover For(AlvoUser person, TimeSpan? offset)
        => CredentialHandover.For(_navigation, person, Token(), _issued, offset, hasSetPasswordPage: true);

    private static AlvoUser Person(bool disabled) => new()
    {
        Id = new UserId(Guid.Parse("0198f0c1-5f3a-7b2e-9a1d-000000000001")),
        Email = "eva@alvo.test",
        RoleNames = [],
        IsDisabled = disabled,
    };

    private static AlvoCredentialToken Token()
        => new(new UserId(Guid.Parse("0198f0c1-5f3a-7b2e-9a1d-000000000001")), "CfDJ8+a/b=", _expires);

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string baseUri) => Initialize(baseUri, baseUri);
    }
}
