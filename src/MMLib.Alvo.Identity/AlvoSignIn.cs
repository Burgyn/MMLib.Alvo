using Microsoft.AspNetCore.Identity;
using MMLib.Alvo.Data;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity;

/// <summary>
/// Signing a human in and out, and letting a person redeem the credential token an administrator issued them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than the host using <see cref="SignInManager{TUser}"/> directly.</b>
/// The stored user type is <c>internal</c> to this package — deliberately, because it is an EF
/// entity and making it public would make its columns a contract. A host cannot name
/// <c>SignInManager&lt;AlvoIdentityUser&gt;</c> without it, so the operations an unauthenticated
/// screen needs are published here and the type stays where it belongs.
/// </para>
/// <para>
/// <b>Three methods, and the third is redemption because its order is a security property.</b> Signing in
/// and out are what a sign-in page needs. Setting a password from a token is what the set-password page
/// needs, and the order it runs Identity's checks in — the policy before anything about the account, then
/// the refusals, then the token — is what keeps a weak password from saying anything about the account and
/// keeps every refusal identical; a host composing it from <c>UserManager</c> would have to get that order
/// right itself, and an embedded host rendering its own screen needs the same guarantee as the dashboard.
/// Everything else a sign-in page is tempted to add — self-service registration, a reset mailed to the
/// address, external providers — is either the administrator's job through user administration or a
/// provider this build does not have. A method here that did not work would be a control somebody draws a
/// button for.
/// </para>
/// </remarks>
public sealed class AlvoSignIn
{
    private readonly SignInManager<AlvoIdentityUser> _signIn;
    private readonly IAlvoBootstrapAdmin _bootstrap;
    private readonly AlvoIdentityDbContext _store;
    private readonly AlvoTimingParity _parity;

    /// <summary>
    /// Constructs the service over Identity's sign-in manager.
    /// </summary>
    /// <remarks>
    /// <b>Internal, and the registration is therefore a factory lambda.</b> The parameters' types are
    /// closed over <c>AlvoIdentityUser</c> or are the package's internal store, so a public constructor would
    /// not compile, and making them public to satisfy it would turn an EF entity's columns into a contract.
    /// The lambda in <see cref="AlvoIdentityAuthenticationExtensions.AddAlvoIdentityCookieSignIn"/> lives
    /// inside this package and can see what a container cannot.
    /// </remarks>
    /// <param name="signIn">ASP.NET Core Identity's sign-in manager over the Alvo identity store.</param>
    /// <param name="bootstrap">Who the bootstrap administrator is, whom redemption refuses.</param>
    /// <param name="store">The identity store, whose unit of work a redemption runs in.</param>
    /// <param name="parity">The hash a refused sign-in verifies against.</param>
    internal AlvoSignIn(
        SignInManager<AlvoIdentityUser> signIn,
        IAlvoBootstrapAdmin bootstrap,
        AlvoIdentityDbContext store,
        AlvoTimingParity parity)
    {
        _signIn = signIn;
        _bootstrap = bootstrap;
        _store = store;
        _parity = parity;
    }

    private UserManager<AlvoIdentityUser> Users => _signIn.UserManager;

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
    /// <b>And it takes as long, to within a write.</b> An address with no account, an account with no password
    /// yet, and a disabled or locked-out one are all refused without Identity hashing anything, which made the
    /// answer fast exactly when the address was not a working account. Each verifies the password against a hash
    /// the registered hasher made (<see cref="AlvoTimingParity"/>), so every refusal costs one verification at the
    /// host's own cost, like a wrong password (design §10.3). One residue remains, deliberately: a wrong password
    /// for a live account also writes its failed-attempt count (and, at the threshold, the lockout), about a
    /// millisecond against a verification's tens; the shared credential rate limit bounds how many samples of it
    /// a client can take. No dummy write is added to match it, because writing to a row that does not exist is
    /// not possible and writing somewhere else would be a cost with no guarantee of equality.
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

        var user = await Users.FindByEmailAsync(email).ConfigureAwait(false);
        if (user?.PasswordHash is null || await Users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            var hasher = Users.PasswordHasher;
            _ = hasher.VerifyHashedPassword(new AlvoIdentityUser(), _parity.For(hasher), password);
            return false;
        }

        var result = await _signIn
            .PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true)
            .ConfigureAwait(false);

        return result.Succeeded;
    }

    /// <summary>
    /// Sets a person's password from the credential token an administrator issued them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is the contract.</b> The policy runs first, against a placeholder carrying only the typed
    /// address, so a weak password is rejected identically whether the address exists or not. Then an address
    /// with no account, a disabled account and the bootstrap administrator are refused, consuming nothing. Only
    /// then is the token verified — its purpose, the user id inside it (another person's token fails here), the
    /// security stamp inside it and its lifetime — and the password hashed and stored. No failure path hashes a
    /// password, so every refusal costs one indexed read and at most one data-protection unprotect.
    /// </para>
    /// <para>
    /// <b>Once.</b> Storing the password rotates the security stamp, which kills this token, every other
    /// outstanding one for the person, and every session they hold. The whole redemption is one unit of work,
    /// so of two racing redemptions exactly one is <see cref="AlvoPasswordSetOutcome.Set"/>.
    /// </para>
    /// <para>
    /// <b>A temporary lockout is cleared on success</b>, which Identity leaves in place: it protected the old
    /// password, and the holder has just proved possession of an administrator-issued token (design §8.8). A
    /// disable is a refusal instead, and survives.
    /// </para>
    /// </remarks>
    /// <param name="email">The address the token was issued for.</param>
    /// <param name="token">The credential token.</param>
    /// <param name="newPassword">The password to set.</param>
    /// <returns>
    /// <see cref="AlvoPasswordSetOutcome.PasswordRejected"/> when the policy refuses the password,
    /// <see cref="AlvoPasswordSetOutcome.Refused"/> for every other failure, and
    /// <see cref="AlvoPasswordSetOutcome.Set"/> when the password is stored.
    /// </returns>
    public async Task<AlvoPasswordSetOutcome> SetPasswordAsync(string email, string token, string newPassword)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(newPassword);

        if (!await PolicyAcceptsAsync(email, newPassword).ConfigureAwait(false))
        {
            return AlvoPasswordSetOutcome.PasswordRejected;
        }

        try
        {
            return await AlvoIdentityUnitOfWork
                .RunAsync(_store, () => RedeemAsync(email, token, newPassword))
                .ConfigureAwait(false);
        }
        catch (AlvoPreconditionFailedException)
        {
            /* A redemption that lost a race to another one: the other one set the password. */
            return AlvoPasswordSetOutcome.Refused;
        }
    }

    /// <summary>Signs the current person out, clearing the cookie.</summary>
    /// <returns>A task that completes when the cookie has been cleared.</returns>
    public Task SignOutAsync() => _signIn.SignOutAsync();

    /// <summary>Runs every registered password validator against a placeholder that carries only the address.</summary>
    /// <remarks>
    /// Never the stored row: a validator that read it would answer differently for an address that exists.
    /// </remarks>
    /// <param name="email">The typed address.</param>
    /// <param name="password">The candidate password.</param>
    /// <returns><see langword="true"/> when every validator accepts it.</returns>
    private async Task<bool> PolicyAcceptsAsync(string email, string password)
    {
        var placeholder = new AlvoIdentityUser { UserName = email, Email = email };
        foreach (var validator in Users.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(Users, placeholder, password).ConfigureAwait(false)).Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The refusals, then the token, then the lockout — inside the unit of work.</summary>
    /// <param name="email">The typed address.</param>
    /// <param name="token">The credential token.</param>
    /// <param name="newPassword">The password, already accepted by the policy.</param>
    /// <returns>What the redemption did.</returns>
    private async Task<AlvoPasswordSetOutcome> RedeemAsync(string email, string token, string newPassword)
    {
        var row = await Users.FindByEmailAsync(email).ConfigureAwait(false);
        if (row is null
            || AlvoIdentityLockout.IsDisabled(row.LockoutEnd)
            || _bootstrap.IsBootstrapAdmin(new UserId(row.Id)))
        {
            return AlvoPasswordSetOutcome.Refused;
        }

        if (!(await Users.ResetPasswordAsync(row, token, newPassword).ConfigureAwait(false)).Succeeded)
        {
            return AlvoPasswordSetOutcome.Refused;
        }

        await ClearLockoutAsync(row).ConfigureAwait(false);
        return AlvoPasswordSetOutcome.Set;
    }

    /// <summary>Resets the failed-attempt count and ends a temporary lockout.</summary>
    /// <remarks>
    /// A failure here throws rather than answering: the unit of work then rolls the new password back with it,
    /// instead of committing a password the person is still locked out of.
    /// </remarks>
    /// <param name="row">The person, whose password was just set.</param>
    /// <returns>A task that completes when the lockout is cleared.</returns>
    private async Task ClearLockoutAsync(AlvoIdentityUser row)
    {
        Written(await Users.ResetAccessFailedCountAsync(row).ConfigureAwait(false));
        if (row.LockoutEnd is not null)
        {
            Written(await Users.SetLockoutEndDateAsync(row, null).ConfigureAwait(false));
        }
    }

    /// <summary>Throws when a write after the password's own failed.</summary>
    /// <param name="result">What Identity said.</param>
    /// <exception cref="AlvoPreconditionFailedException">The write lost a race.</exception>
    /// <exception cref="InvalidOperationException">The write failed for any other reason.</exception>
    private static void Written(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        AlvoIdentityUnitOfWork.ThrowIfRaced(result);
        throw new InvalidOperationException(
            "The lockout could not be cleared after a password was set: "
            + string.Join("; ", result.Errors.Select(error => error.Code)));
    }
}
