using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Internal;
using System.Globalization;

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
/// One record the panel draws and the page copies from, so what is on screen and what reaches the clipboard cannot
/// come apart.
/// </para>
/// </remarks>
/// <param name="Link">The set-password link.</param>
/// <param name="Token">The bare token.</param>
/// <param name="Instruction">How to hand it over, and until when it works.</param>
/// <param name="DisabledNote">For a person who is disabled, that it waits for them to be let back in.</param>
internal sealed record CredentialHandover(string Link, string Token, string Instruction, string? DisabledNote)
{
    /// <summary>What Copy link puts on the clipboard.</summary>
    public string Clipboard => Link;

    /// <summary>The handover of <paramref name="token"/>, issued for <paramref name="person"/>.</summary>
    /// <param name="navigation">Where the dashboard is served, which the link is built under.</param>
    /// <param name="person">The person it was issued for.</param>
    /// <param name="token">The token.</param>
    /// <returns>The handover.</returns>
    public static CredentialHandover For(NavigationManager navigation, AlvoUser person, AlvoCredentialToken token)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(token);

        var until = token.ExpiresAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return new CredentialHandover(
            SetPasswordLink.For(navigation, person.Email, token.Token),
            token.Token,
            $"Hand this over out of band: this build sends no email. It works once, until {until}.",
            person.IsDisabled ? "They cannot use it until you let them back in." : null);
    }
}
