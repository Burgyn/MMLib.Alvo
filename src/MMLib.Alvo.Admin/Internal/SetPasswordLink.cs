using Microsoft.AspNetCore.Components;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The link a credential token is handed over as: the set-password page, with the address and the token in its
/// fragment (design §1.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>From <see cref="NavigationManager.BaseUri"/>, not from a configured origin</b>, so the path base and a
/// forwarded origin are the ones the operator's own browser reached the dashboard at.
/// </para>
/// <para>
/// <b>Both values escaped with <see cref="Uri.EscapeDataString(string)"/>.</b> The page reads them back with
/// <c>URLSearchParams</c>, which reads a <c>+</c> as a space, and a base64 token is full of <c>+</c>, <c>/</c> and
/// <c>=</c>; an address may carry a <c>+</c> or an <c>&amp;</c>. Escaping every one of them is what makes the round
/// trip exact. The host's endpoint spells the same fragment when it sends a mismatch back, and a host test pins the
/// two together.
/// </para>
/// </remarks>
internal static class SetPasswordLink
{
    /// <summary>The whole link.</summary>
    /// <param name="navigation">Where the dashboard is served, as this circuit reached it.</param>
    /// <param name="email">The person's address.</param>
    /// <param name="token">The credential token.</param>
    /// <returns>An absolute URL whose fragment carries both values.</returns>
    public static string For(NavigationManager navigation, string email, string token)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        return $"{navigation.BaseUri.TrimEnd('/')}{AlvoAdmin.SetPasswordPath}{Fragment(email, token)}";
    }

    /// <summary>The fragment alone, <c>#</c> included.</summary>
    /// <param name="email">The person's address.</param>
    /// <param name="token">The credential token.</param>
    /// <returns><c>#email=…&amp;token=…</c>, both escaped.</returns>
    public static string Fragment(string email, string token)
        => $"#email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
}
