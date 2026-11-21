using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The <c>Idempotency-Key</c> of the two creations whose answer carries a secret shown once — minting a token and creating
/// an invitation — which do not honour it (#542 item 5, a recorded deviation) and so <b>refuse</b> it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refused, not ignored</b> (ruled 2026-11-21): a retried mint carrying a key it silently ignored would mint a second
/// live secret while the client believed it replayed the first. <see cref="TenantCreationKey"/>'s own rule — a key that
/// cannot be recorded is never silently ignored — applies here too, so a key is 422 <c>malformed-query</c>, the Data API's
/// answer for a header it cannot honour, with the way out.
/// </para>
/// <para>
/// <b>Why it cannot be honoured</b>: the secret is stored only as its hash (spec §14 AC6), so a replay could not return it
/// without storing it. A client whose answer was lost lists, revokes the one it cannot account for, and creates again.
/// </para>
/// </remarks>
internal static class ShownOnceKey
{
    private const string Header = DataApiEndpoints.IdempotencyKeyHeader;

    /// <summary>The fix for a lost mint, in the refusal.</summary>
    internal const string TokensDetail =
        $"The '{Header}' header is not honoured when minting a token: its secret is shown once and kept only as a hash, so a "
        + "retry could not return it. Send the mint without the header. If an answer was lost, list your tokens, revoke the "
        + "one you cannot account for, and mint again.";

    /// <summary>The fix for a lost invitation, in the refusal.</summary>
    internal const string InvitationsDetail =
        $"The '{Header}' header is not honoured when creating an invitation: its code is shown once and kept only as a hash, "
        + "so a retry could not return it. Send the request without the header. If an answer was lost, list the tenant's "
        + "invitations, revoke the one you cannot account for, and create a new one.";

    /// <summary>The 422 a request carrying the header earns, or <see langword="null"/> when it carries none.</summary>
    /// <param name="request">The request.</param>
    /// <param name="detail">The route's own sentence: <see cref="TokensDetail"/> or <see cref="InvitationsDetail"/>.</param>
    internal static IResult? Refusal(HttpRequest request, string detail)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.ContainsKey(Header) ? ProblemResultFactory.Malformed(detail) : null;
    }
}
