using System.Text;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>Why a subject token was not accepted as a verified identity (spec §5.2 steps 1–3).</summary>
internal enum IdTokenRefusal
{
    /// <summary>It is not a signed JWT, or it lacks a claim an ID token must carry.</summary>
    Malformed,

    /// <summary>Its <c>iss</c> is no provider this deployment configured.</summary>
    UnknownIssuer,

    /// <summary>Its signature does not verify under a key its provider publishes for its <c>kid</c>.</summary>
    Signature,

    /// <summary>Its <c>alg</c> is not one its provider signs with — <c>none</c> and every <c>HS*</c> never are.</summary>
    Algorithm,

    /// <summary>Its <c>aud</c> names none of the configured client ids.</summary>
    Audience,

    /// <summary>Its <c>exp</c> is more than the skew in the past, or absent.</summary>
    Expired,

    /// <summary>Its <c>nbf</c> is more than the skew in the future.</summary>
    NotYetValid,

    /// <summary>Its <c>iat</c> is older than five minutes, too far in the future, or absent.</summary>
    Stale,

    /// <summary>Its provider's discovery document or keys could not be fetched.</summary>
    Unreachable,
}

/// <summary>What verifying a subject token found: an identity and the token's single-use id, or why not.</summary>
/// <param name="Identity">The verified identity, when the token was accepted.</param>
/// <param name="SubjectTokenId">
/// The token's single-use id (spec §5.2 step 3): base64url(SHA-256(<c>iss</c> + "\n" + (<c>jti</c> ?? base64url(SHA-256(token))))).
/// </param>
/// <param name="Refusal">Why the token was refused, when it was.</param>
internal sealed record IdentityVerification(VerifiedIdentity? Identity, string? SubjectTokenId, IdTokenRefusal? Refusal)
{
    /// <summary>A refusal for <paramref name="refusal"/>.</summary>
    /// <param name="refusal">Why the token was refused.</param>
    internal static IdentityVerification Refused(IdTokenRefusal refusal) => new(null, null, refusal);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Identity = ").Append(Identity);
        builder.Append(", Refusal = ").Append(Refusal);
        return true;
    }
}

/// <summary>
/// Turns a subject token into a verified identity — the seam every way of signing in ends at (spec §5.8). Internal
/// until a second implementation exists: <i>public is the contract</i>.
/// </summary>
internal interface IIdentityVerifier
{
    /// <summary>Verifies <paramref name="subjectToken"/>.</summary>
    /// <param name="subjectToken">The token the client presented; never logged.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask<IdentityVerification> VerifyAsync(string subjectToken, CancellationToken ct);
}
