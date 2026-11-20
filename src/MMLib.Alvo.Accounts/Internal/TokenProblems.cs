using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Every refusal the token endpoint answers with (spec D4): a problem document carrying RFC 6749 §5.2's <c>error</c> and
/// <c>error_description</c>, each description a fixed sentence.
/// </summary>
/// <remarks>
/// <b>Nothing the caller sent is ever echoed</b> — a token endpoint's input is a credential. And every refused credential
/// of one kind answers one description: a subject token refused for its signature, its age, its reuse or its provider
/// reads the same, and so does every dead refresh token, so the answer is no oracle for which check failed.
/// </remarks>
internal static class TokenProblems
{
    /// <summary>The description of every refused subject token (RFC 8693 §2.2.2: <c>invalid_request</c>).</summary>
    internal const string SubjectTokenDescription =
        "The subject token is not a valid, fresh ID token from a provider this backend accepts.";

    /// <summary>The description of every refused refresh token, whatever made it dead.</summary>
    internal const string RefreshTokenDescription = "The refresh token is not valid. Sign in again.";

    /// <summary>
    /// The description of every dead <c>invitation_code</c> on an exchange (S4 plan D3) — unknown, expired, revoked, used up
    /// or bound to another email read the same.
    /// </summary>
    internal const string InvitationDescription = "The invitation is not valid. Sign in without it, or ask for a new one.";

    /// <summary>The description of an <c>invitation_code</c> sent while the descriptor takes no invitations (S4 plan D5).</summary>
    internal const string InvitationsOffDescription = "This backend takes no invitations; send no invitation_code.";

    private const string InvalidRequestError = "invalid_request";

    private const string InvalidGrantError = "invalid_grant";

    private const string AccessDeniedError = "access_denied";

    private const string PolicyMember = "policy";

    /// <summary>A form that is not one urlencoded body of at most 16 KiB with each parameter once.</summary>
    internal static IResult MalformedForm() => InvalidRequest(
        "The token request must be one application/x-www-form-urlencoded body of at most 16 KiB that sends each "
        + "parameter once.");

    /// <summary>A form with no <c>grant_type</c>.</summary>
    internal static IResult MissingGrantType() => InvalidRequest("The token request names no grant_type.");

    /// <summary>A <c>grant_type</c> this endpoint does not grant.</summary>
    internal static IResult UnsupportedGrantType() => OAuth(
        StatusCodes.Status400BadRequest,
        AlvoProblemTypes.TokenRefused,
        "unsupported_grant_type",
        "This token endpoint grants urn:ietf:params:oauth:grant-type:token-exchange and refresh_token only.");

    /// <summary>An exchange whose parameters are missing or name a token type this endpoint does not exchange.</summary>
    internal static IResult MalformedExchange() => InvalidRequest(
        "A token exchange sends subject_token and subject_token_type urn:ietf:params:oauth:token-type:id_token, and "
        + "may request only urn:ietf:params:oauth:token-type:access_token.");

    /// <summary>An exchange naming an <c>audience</c> or a <c>resource</c> (RFC 8693 §2.2.2).</summary>
    internal static IResult InvalidTarget() => OAuth(
        StatusCodes.Status400BadRequest,
        AlvoProblemTypes.TokenRefused,
        "invalid_target",
        "This token endpoint issues tokens for this backend only; send no audience or resource.");

    /// <summary>A subject token refused for any reason, or from a provider the descriptor does not list.</summary>
    internal static IResult SubjectTokenRefused() => InvalidRequest(SubjectTokenDescription);

    /// <summary>A refresh request with no <c>refresh_token</c>.</summary>
    internal static IResult MissingRefreshToken() => InvalidRequest("A refresh sends refresh_token.");

    /// <summary>A refresh token refused for any reason (RFC 6749 §5.2: <c>invalid_grant</c>).</summary>
    internal static IResult InvalidGrant() => OAuth(
        StatusCodes.Status400BadRequest, AlvoProblemTypes.TokenRefused, InvalidGrantError, RefreshTokenDescription);

    /// <summary>
    /// An exchange whose <c>invitation_code</c> admits nobody (RFC 6749 §5.2: <c>invalid_grant</c> — "invalid, expired,
    /// revoked"): nothing was written, and the description names no reason.
    /// </summary>
    internal static IResult InvitationRefused() => OAuth(
        StatusCodes.Status400BadRequest, AlvoProblemTypes.TokenRefused, InvalidGrantError, InvitationDescription);

    /// <summary>An exchange carrying an <c>invitation_code</c> while <c>tenancy.selfService.invitations</c> is off.</summary>
    internal static IResult InvitationsOff() => InvalidRequest(InvitationsOffDescription);

    /// <summary>A new identity the sign-up policy does not admit, naming the policy that refused it.</summary>
    /// <param name="verdict">The refusing verdict.</param>
    internal static IResult SignUpRefused(SignUpVerdict verdict) => verdict switch
    {
        SignUpVerdict.InvitationOnly => SignUpRefused(
            "invitationOnly", "This backend creates new accounts only by invitation."),
        SignUpVerdict.DomainNotAllowed => SignUpRefused(
            "allowedEmailDomains", "This backend creates new accounts only for a trusted email address in an allowed domain."),
        _ => throw new InvalidOperationException($"Sign-up verdict {verdict} refuses nobody, yet the store refused the sign-up."),
    };

    /// <summary>An existing account that may not sign in; the description names no reason.</summary>
    internal static IResult SignInRefused() => OAuth(
        StatusCodes.Status403Forbidden, AlvoProblemTypes.SignInRefused, AccessDeniedError, "This account cannot sign in.");

    private static IResult SignUpRefused(string policy, string description) => OAuth(
        StatusCodes.Status403Forbidden,
        AlvoProblemTypes.SignUpRefused,
        AccessDeniedError,
        description,
        new Dictionary<string, object?>(StringComparer.Ordinal) { [PolicyMember] = policy });

    private static IResult InvalidRequest(string description)
        => OAuth(StatusCodes.Status400BadRequest, AlvoProblemTypes.TokenRefused, InvalidRequestError, description);

    private static IResult OAuth(
        int status, string slug, string error, string description, IReadOnlyDictionary<string, object?>? extensions = null)
        => ProblemResultFactory.OAuthError(status, slug, error, description, extensions);
}
