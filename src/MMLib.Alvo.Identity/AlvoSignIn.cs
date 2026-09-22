using Microsoft.AspNetCore.Identity;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity;

/// <summary>
/// Signing a human in and out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than the host using <see cref="SignInManager{TUser}"/> directly.</b>
/// The stored user type is <c>internal</c> to this package — deliberately, because it is an EF
/// entity and making it public would make its columns a contract. A host cannot name
/// <c>SignInManager&lt;AlvoIdentityUser&gt;</c> without it, so the two operations a sign-in screen
/// needs are published here and the type stays where it belongs.
/// </para>
/// <para>
/// <b>It is two methods and no more.</b> Everything else a sign-in page is tempted to add —
/// registration, password reset, external providers — is either the administrator's job through
/// user administration, or a provider this build does not have. A method here that did not work
/// would be a control somebody draws a button for.
/// </para>
/// </remarks>
public sealed class AlvoSignIn
{
    private readonly SignInManager<AlvoIdentityUser> _signIn;

    /// <summary>
    /// Constructs the service over Identity's sign-in manager.
    /// </summary>
    /// <remarks>
    /// <b>Internal, and the registration is therefore a factory lambda.</b> The parameter's type is
    /// closed over <c>AlvoIdentityUser</c>, which is internal — so a public constructor would not
    /// compile, and making the user type public to satisfy it would turn an EF entity's columns
    /// into a contract. The lambda in
    /// <see cref="AlvoIdentityAuthenticationExtensions.AddAlvoIdentityCookieSignIn"/> lives inside
    /// this package and can see what a container cannot.
    /// </remarks>
    /// <param name="signIn">ASP.NET Core Identity's sign-in manager over the Alvo identity store.</param>
    internal AlvoSignIn(SignInManager<AlvoIdentityUser> signIn) => _signIn = signIn;

    /// <summary>
    /// Signs a person in with an email address and a password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The answer is a <see langword="bool"/>, and that is a security decision rather than a
    /// lazy signature.</b> Identity distinguishes "no such user" from "wrong password" from "locked
    /// out", and a screen that reported the difference would tell anyone which addresses have
    /// accounts. A disabled account is refused for the same reason as a wrong password and looks
    /// identical from outside.
    /// </para>
    /// <para>
    /// <c>isPersistent: false</c> — the session ends with the browser. A configuration tool that
    /// stayed signed in on a shared machine is a worse default than one that asks again.
    /// </para>
    /// </remarks>
    /// <param name="email">The address the person signs in with.</param>
    /// <param name="password">Their password.</param>
    /// <returns><see langword="true"/> when the cookie was written.</returns>
    public async Task<bool> PasswordSignInAsync(string email, string password)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        var user = await _signIn.UserManager.FindByEmailAsync(email).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        var result = await _signIn
            .PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true)
            .ConfigureAwait(false);

        return result.Succeeded;
    }

    /// <summary>Signs the current person out, clearing the cookie.</summary>
    /// <returns>A task that completes when the cookie has been cleared.</returns>
    public Task SignOutAsync() => _signIn.SignOutAsync();
}
