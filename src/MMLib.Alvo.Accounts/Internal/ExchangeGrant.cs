using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// Signing in by token exchange (RFC 8693, spec §5.2): the provider's ID token is the subject token, and the answer is an
/// Alvo access token and the first refresh token of a new session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refused subject token is one <c>invalid_request</c></b> (RFC 8693 §2.2.2), whatever the verifier found and
/// also when the descriptor does not list the token's provider (S2 D10): the provider must be one this deployment
/// configured <em>and</em> the applied descriptor lists. That check is the verifier's first, before any key or discovery
/// document is fetched (spec §5.2 step 1), so a provider the descriptor dropped costs an anonymous caller no outbound
/// request.
/// </para>
/// <para>
/// <b>A parameter sent empty is a parameter not sent</b> (RFC 6749 §3.2): <c>audience=</c> asks for no audience.
/// </para>
/// <para>
/// <b>The sign-up policy is decided here and applied by the store</b>: the store creates an account only when the
/// verdict admits one, so an existing account signs in under <c>invitationOnly</c> (D14) and a refused new identity writes
/// nothing. The session and refresh ids are chosen here, so the one transaction that starts the session also issues the
/// refresh token whose value this answers with.
/// </para>
/// </remarks>
/// <param name="verifier">Verifies the subject token.</param>
/// <param name="store">Signs the identity in, in one transaction.</param>
/// <param name="signIn">The applied descriptor's providers and sign-up policy.</param>
/// <param name="issuance">Answers a granted request.</param>
/// <param name="options">The accounts options, for the session lifetime.</param>
/// <param name="clock">The clock the session starts on.</param>
internal sealed class ExchangeGrant(
    IIdentityVerifier verifier,
    IAccountStore store,
    DescriptorSignIn signIn,
    TokenIssuance issuance,
    IOptions<AlvoAccountsOptions> options,
    TimeProvider clock)
{
    /// <summary>RFC 8693's <c>grant_type</c>.</summary>
    internal const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>RFC 8693's identifier of an OpenID Connect ID token as a subject token.</summary>
    internal const string IdTokenType = "urn:ietf:params:oauth:token-type:id_token";

    private const string SubjectToken = "subject_token";

    private const string SubjectTokenType = "subject_token_type";

    private const string RequestedTokenType = "requested_token_type";

    /// <summary>Exchanges the subject token <paramref name="form"/> carries.</summary>
    /// <param name="form">The token request's parameters.</param>
    /// <param name="request">The request, for the issuer in Development.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async Task<IResult> ExchangeAsync(TokenForm form, HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (ParameterRefusal(form) is { } refused)
        {
            return refused;
        }

        var policy = signIn.Policy;
        var verification = await verifier.VerifyAsync(form[SubjectToken]!, provider => signIn.Admits(policy, provider), ct)
            .ConfigureAwait(false);
        if (verification is not { Identity: { } identity, SubjectTokenId: { } tokenId })
        {
            return TokenProblems.SubjectTokenRefused();
        }

        var verdict = SignUpDecision.Decide(policy, identity);
        var started = Start(identity, verdict, tokenId);
        var outcome = await store.SignInAsync(started, ct).ConfigureAwait(false);
        return await AnswerAsync(outcome, verdict, started, policy, request, ct).ConfigureAwait(false);
    }

    private static IResult? ParameterRefusal(TokenForm form)
    {
        if (form["audience"] is not null || form["resource"] is not null)
        {
            return TokenProblems.InvalidTarget();
        }

        var requested = form[RequestedTokenType];
        return form[SubjectToken] is null
            || !string.Equals(form[SubjectTokenType], IdTokenType, StringComparison.Ordinal)
            || (requested is not null && !string.Equals(requested, TokenIssuance.AccessTokenType, StringComparison.Ordinal))
                ? TokenProblems.MalformedExchange()
                : null;
    }

    private SignInRequest Start(VerifiedIdentity identity, SignUpVerdict verdict, string subjectTokenId)
    {
        var now = clock.GetUtcNow();
        return new SignInRequest(
            identity,
            verdict == SignUpVerdict.Admitted,
            subjectTokenId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            now,
            now.AddDays(options.Value.SessionLifetimeDays));
    }

    private async Task<IResult> AnswerAsync(
        SignInOutcome outcome, SignUpVerdict verdict, SignInRequest started, SignInPolicy policy, HttpRequest request, CancellationToken ct)
        => outcome switch
        {
            { Status: SignInStatus.Created or SignInStatus.SignedIn, Account: { } account } => await issuance.IssueAsync(
                new AccessTokenSubject(account, started.SessionId, started.Identity.Provider),
                started.RefreshTokenId,
                policy,
                request,
                exchanged: true,
                ct).ConfigureAwait(false),
            { Status: SignInStatus.SignUpRefused } => TokenProblems.SignUpRefused(verdict),
            { Status: SignInStatus.Disabled } => TokenProblems.SignInRefused(),
            { Status: SignInStatus.SubjectTokenReplayed } => TokenProblems.SubjectTokenRefused(),
            _ => throw new InvalidOperationException($"The account store answered a sign-in with {outcome.Status} and no account."),
        };
}
