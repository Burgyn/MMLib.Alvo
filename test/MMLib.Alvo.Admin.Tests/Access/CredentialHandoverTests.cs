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

    [Fact]
    public void The_panel_shows_the_set_password_link_and_copy_puts_the_link_on_the_clipboard()
    {
        var handover = CredentialHandover.For(_navigation, Person(disabled: false), Token());

        handover.Link.ShouldBe(SetPasswordLink.For(_navigation, "eva@alvo.test", "CfDJ8+a/b="));
        handover.Clipboard.ShouldBe(handover.Link, "Copy link copies the link, not the bare token");
        handover.Token.ShouldBe("CfDJ8+a/b=");
    }

    [Fact]
    public void The_panel_says_how_to_hand_it_over_and_until_when()
        => CredentialHandover.For(_navigation, Person(disabled: false), Token()).Instruction
            .ShouldBe("Hand this over out of band: this build sends no email. It works once, until 2026-09-28 14:05.");

    [Fact]
    public void A_disabled_person_is_told_they_cannot_use_it_until_they_are_let_back_in()
    {
        CredentialHandover.For(_navigation, Person(disabled: true), Token()).DisabledNote
            .ShouldBe("They cannot use it until you let them back in.");
        CredentialHandover.For(_navigation, Person(disabled: false), Token()).DisabledNote.ShouldBeNull();
    }

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
