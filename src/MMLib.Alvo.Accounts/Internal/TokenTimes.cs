using Microsoft.IdentityModel.JsonWebTokens;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Every lifetime check of every token Alvo reads, against the one injected clock (S2 D5): the handler validates the
/// signature, the key, the issuer, the audience, the algorithm and the type, and never a time.
/// </summary>
/// <remarks>
/// Both bounds are inclusive, so a boundary is pinnable to the second: a token whose <c>exp</c> is exactly
/// <c>skew</c> ago is live, and one second later it is not. A missing <c>exp</c> or <c>iat</c> is never live or fresh.
/// </remarks>
internal static class TokenTimes
{
    /// <summary>Whether a token with <paramref name="exp"/> and <paramref name="nbf"/> is valid at <paramref name="now"/>.</summary>
    /// <param name="exp">The token's expiry; required.</param>
    /// <param name="nbf">The token's not-before, if it carries one.</param>
    /// <param name="now">The instant to judge at.</param>
    /// <param name="skew">How far either bound may be passed.</param>
    internal static bool Live(DateTimeOffset? exp, DateTimeOffset? nbf, DateTimeOffset now, TimeSpan skew)
        => exp is { } expires && now <= expires + skew && !NotYetValid(nbf, now, skew);

    /// <summary>Whether <paramref name="nbf"/> is still ahead of <paramref name="now"/> by more than the skew.</summary>
    /// <param name="nbf">The token's not-before, if it carries one.</param>
    /// <param name="now">The instant to judge at.</param>
    /// <param name="skew">How far the bound may be passed.</param>
    internal static bool NotYetValid(DateTimeOffset? nbf, DateTimeOffset now, TimeSpan skew)
        => nbf is { } notBefore && now < notBefore - skew;

    /// <summary>
    /// Whether a token issued at <paramref name="iat"/> is at most <paramref name="maxAge"/> old and not issued further
    /// in the future than <paramref name="skew"/>.
    /// </summary>
    /// <param name="iat">The token's issue instant; required.</param>
    /// <param name="now">The instant to judge at.</param>
    /// <param name="maxAge">The oldest a token may be.</param>
    /// <param name="skew">How far in the future an issue instant may lie.</param>
    internal static bool Fresh(DateTimeOffset? iat, DateTimeOffset now, TimeSpan maxAge, TimeSpan skew)
        => iat is { } issued && now - issued <= maxAge && issued - now <= skew;

    /// <summary>A NumericDate claim (RFC 7519 §2) of <paramref name="token"/>, or <see langword="null"/> when absent.</summary>
    /// <param name="token">The token.</param>
    /// <param name="claim">The claim's name, e.g. <c>exp</c>.</param>
    internal static DateTimeOffset? Instant(JsonWebToken token, string claim)
        => token.TryGetPayloadValue<long>(claim, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
