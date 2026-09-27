using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.Access;

/// <summary>
/// A credential token as the person editor hands it over: the set-password link, what to do with it, and until when.
/// </summary>
/// <remarks>
/// <para>
/// <b>The link, not the bare token, is what Copy link copies</b> (design §1.1): the person it is for opens it and
/// sets a password, with nothing to paste. The bare token stays on screen under it for a browser running no
/// JavaScript, where the page shows the token box and it is pasted by hand.
/// </para>
/// <para>
/// <b>The expiry is in the operator's own time</b>, drawn by <see cref="OperatorTime.Clock"/> as the lockout above it
/// in the same editor is (<see cref="LockoutWords"/>), and in UTC saying so until the browser's offset is known: one
/// editor, one clock.
/// </para>
/// <para>
/// <b>Without a set-password page there is no link</b> (<see cref="SetPasswordRoute"/>): a host that maps no post
/// for the form — an embedded one that did not map its own — is handed the bare token, which Copy copies, and one
/// sentence saying so, rather than a link to a form that posts nowhere.
/// </para>
/// <para>
/// One record the panel draws and the page copies from, so what is on screen and what reaches the clipboard cannot
/// come apart.
/// </para>
/// </remarks>
/// <param name="Link">The set-password link, or <see langword="null"/> when this host has no set-password page.</param>
/// <param name="Token">The bare token.</param>
/// <param name="Instruction">How to hand it over, and until when it works.</param>
/// <param name="DisabledNote">For a person who is disabled, that it waits for them to be let back in.</param>
/// <param name="NoPageNote">When this host has no set-password page, that the token goes to the host's own flow.</param>
internal sealed record CredentialHandover(
    string? Link, string Token, string Instruction, string? DisabledNote, string? NoPageNote)
{
    /// <summary>The sentence for a host that maps no set-password post.</summary>
    public const string NoPage = "This host has no set-password page — hand the token to your own flow.";

    /// <summary>What Copy puts on the clipboard: the link, or the token when there is none.</summary>
    public string Clipboard => Link ?? Token;

    /// <summary>What is copied, in the words of its button and its confirmation.</summary>
    public string Copied => Link is null ? "token" : "link";

    /// <summary>The handover of <paramref name="token"/>, issued for <paramref name="person"/>.</summary>
    /// <param name="navigation">Where the dashboard is served, which the link is built under.</param>
    /// <param name="person">The person it was issued for.</param>
    /// <param name="token">The token.</param>
    /// <param name="offset">The operator's UTC offset, or <see langword="null"/> while it is not known.</param>
    /// <param name="hasSetPasswordPage">Whether this host maps the set-password post (<see cref="SetPasswordRoute"/>).</param>
    /// <returns>The handover.</returns>
    public static CredentialHandover For(
        NavigationManager navigation, AlvoUser person, AlvoCredentialToken token, TimeSpan? offset,
        bool hasSetPasswordPage)
        => For(navigation, person, token, DateTimeOffset.UtcNow, offset, hasSetPasswordPage);

    /// <summary>The handover of <paramref name="token"/>, issued for <paramref name="person"/>, as of <paramref name="now"/>.</summary>
    /// <param name="navigation">Where the dashboard is served, which the link is built under.</param>
    /// <param name="person">The person it was issued for.</param>
    /// <param name="token">The token.</param>
    /// <param name="now">The instant "today" is decided at, for the expiry's date.</param>
    /// <param name="offset">The operator's UTC offset, or <see langword="null"/> while it is not known.</param>
    /// <param name="hasSetPasswordPage">Whether this host maps the set-password post (<see cref="SetPasswordRoute"/>).</param>
    /// <returns>The handover.</returns>
    public static CredentialHandover For(
        NavigationManager navigation, AlvoUser person, AlvoCredentialToken token, DateTimeOffset now, TimeSpan? offset,
        bool hasSetPasswordPage)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(token);

        var until = OperatorTime.Clock(token.ExpiresAt, now, offset);
        return new CredentialHandover(
            hasSetPasswordPage ? SetPasswordLink.For(navigation, person.Email, token.Token) : null,
            token.Token,
            $"Hand this over out of band: this build sends no email. It works once, until {until}.",
            person.IsDisabled ? "They cannot use it until you let them back in." : null,
            hasSetPasswordPage ? null : NoPage);
    }
}
