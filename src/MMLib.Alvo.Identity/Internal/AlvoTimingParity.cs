using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// The hash a refused sign-in verifies against, so it costs what verifying a stored password costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Made by the registered hasher, never a constant.</b> A fixed Identity v3 blob matches the cost of exactly
/// one configuration: the stock <see cref="PasswordHasher{TUser}"/> at its default iteration count. A host that
/// raises <c>PasswordHasherOptions.IterationCount</c> would make every real verification slower than the dummy,
/// and a host that replaces <see cref="IPasswordHasher{TUser}"/> (Argon2id, say — the documented extension
/// point) would reject a v3 blob after a format check, in microseconds, which is the oracle back in full. A hash
/// the registered hasher made itself is verified by the same code, at the same cost, as a stored one.
/// </para>
/// <para>
/// <b>Made once, at start, and lazily as a fallback.</b> <see cref="AlvoIdentityBootstrap"/> makes it in
/// <c>StartingAsync</c>, before the server accepts a request, so no sign-in pays for it. A composition that never
/// runs the hosted services makes it on the first refused sign-in, which then costs one hash more — once per
/// process, which says nothing about any address. Two first uses racing both hash and one result wins; either is
/// a valid dummy. The password behind it is random and discarded, so nobody can sign in with it.
/// </para>
/// </remarks>
internal sealed class AlvoTimingParity
{
    private string? _hash;

    /// <summary>The hash, once it has been made; <see langword="null"/> before that.</summary>
    internal string? Hash => Volatile.Read(ref _hash);

    /// <summary>The hash, made by <paramref name="hasher"/> the first time it is asked for.</summary>
    /// <param name="hasher">The registered password hasher.</param>
    /// <returns>A hash of a random, discarded password.</returns>
    internal string For(IPasswordHasher<AlvoIdentityUser> hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        if (Volatile.Read(ref _hash) is { } made)
        {
            return made;
        }

        var discarded = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var fresh = hasher.HashPassword(new AlvoIdentityUser(), discarded);
        return Interlocked.CompareExchange(ref _hash, fresh, null) ?? fresh;
    }
}
