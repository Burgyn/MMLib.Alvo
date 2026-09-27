using Microsoft.AspNetCore.Identity;
using System.Globalization;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// The two rules of the password policy Identity's own validator cannot state: a ceiling, and the
/// context-specific blocklist.
/// </summary>
/// <remarks>
/// <para>
/// <b>NIST SP 800-63B-4 §3.1.1.2, for a password that is the only factor.</b> Identity's validator carries
/// the floor (<see cref="MinimumLength"/>) and, with every composition rule switched off, nothing else. What
/// NIST adds is a maximum of at least 64 (128 here, so a pasted passphrase fits and a megabyte of input does
/// not become a PBKDF2 cost) and a refusal of values "specific to the context", of which the only one this
/// package knows is the address the person signs in with.
/// </para>
/// <para>
/// <b>The local part is a blocklist word only from three characters.</b> A two-letter local part
/// (<c>ab@…</c>) would otherwise refuse every password that happens to contain those two letters, which is
/// a policy nobody could satisfy by intent; the whole address is refused at any length.
/// </para>
/// <para>
/// <b>No breached-password corpus</b>, although NIST says SHALL: the image is offline and ships no list.
/// The deviation is recorded in the design (§8.6) and is a follow-up.
/// </para>
/// </remarks>
internal sealed class AlvoPasswordValidator : IPasswordValidator<AlvoIdentityUser>
{
    /// <summary>The policy's floor: NIST's fifteen for a single-factor password.</summary>
    internal const int MinimumLength = 15;

    /// <summary>The policy's ceiling, and the one source of it.</summary>
    /// <remarks>
    /// The dashboard's page (<c>SetPasswordPolicy.MaximumLength</c>) and the host's endpoint
    /// (<c>AlvoAdminSetPassword.MaximumPasswordLength</c>) cannot reference this package's internals, so they spell
    /// the number and a host test pins both to what this policy does at it and one past it.
    /// </remarks>
    internal const int MaximumLength = 128;

    /// <summary>How long a local part must be before a password containing it is refused.</summary>
    private const int ShortestBlockedLocalPart = 3;

    private static readonly IdentityError _tooLong = new()
    {
        Code = "PasswordTooLong",
        Description = string.Create(
            CultureInfo.InvariantCulture, $"Passwords must be at most {MaximumLength} characters."),
    };

    private static readonly IdentityError _containsAddress = new()
    {
        Code = "PasswordContainsAddress",
        Description = "Passwords must not contain the email address they sign in with.",
    };

    /// <inheritdoc/>
    public Task<IdentityResult> ValidateAsync(
        UserManager<AlvoIdentityUser> manager, AlvoIdentityUser user, string? password)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (password is { Length: > MaximumLength })
        {
            return Task.FromResult(IdentityResult.Failed(_tooLong));
        }

        return Task.FromResult(password is not null && ContainsAddress(password, user.Email ?? user.UserName)
            ? IdentityResult.Failed(_containsAddress)
            : IdentityResult.Success);
    }

    /// <summary>Whether <paramref name="password"/> contains the address or a word-length local part of it.</summary>
    /// <param name="password">The candidate password.</param>
    /// <param name="address">The address the person signs in with, when there is one.</param>
    /// <returns><see langword="true"/> when the password is refused.</returns>
    private static bool ContainsAddress(string password, string? address)
    {
        if (address is not { Length: > 0 })
        {
            return false;
        }

        if (password.Contains(address, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        var local = at < 0 ? address : address[..at];
        return local.Length >= ShortestBlockedLocalPart
            && password.Contains(local, StringComparison.OrdinalIgnoreCase);
    }
}
