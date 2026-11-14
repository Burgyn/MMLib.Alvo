using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The signed-in person's own routes (spec §5.5): <c>GET {prefix}/user</c>, <c>PATCH {prefix}/user</c> (the display name
/// only, S2 D8) and <c>POST {prefix}/logout</c> (the bearer's session, S2 D16 — not RFC 7009).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a Bearer token reaches them.</b> The credential is read by <see cref="CallerResolution.Read"/>, the one reading
/// every authenticating site shares, as the Data API reads it: an API key alone is no person (a plain Bearer challenge,
/// RFC 6750 §3.1, since nothing usable was presented); a malformed or ambiguous Bearer is <c>invalid_request</c> before
/// anything is validated; a token that does not hold is <c>invalid_token</c>.
/// </para>
/// <para>
/// <b>Logout identifies, it does not authenticate.</b> It needs the token's signature, claims and lifetime, not a live
/// session — so signing out of a session already revoked (by a second tab, by reuse, by an operator) still answers 204:
/// the outcome the caller asked for holds. A token that is not ours, or has expired, is 401.
/// </para>
/// <para>Every answer carries <c>Cache-Control: no-store</c>: each one is about a person.</para>
/// </remarks>
internal static class UserEndpoints
{
    /// <summary>The operation id of <c>GET {prefix}/user</c>.</summary>
    internal const string GetUserOperationId = "auth.getUser";

    /// <summary>The operation id of <c>PATCH {prefix}/user</c>.</summary>
    internal const string UpdateUserOperationId = "auth.updateUser";

    /// <summary>The operation id of <c>POST {prefix}/logout</c>.</summary>
    internal const string LogoutOperationId = "auth.logout";

    private const string InvalidToken = "invalid_token";

    private const string InvalidRequest = "invalid_request";

    /// <summary>Maps the three routes into the sign-in group.</summary>
    /// <param name="group">The group mapped at <see cref="AlvoAccountsOptions.Prefix"/>.</param>
    /// <param name="cors">The CORS filter each route carries first.</param>
    internal static void Map(RouteGroupBuilder group, AlvoCors cors)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(cors);
        group.MapGet("/user", GetAsync).AddEndpointFilter(cors.Filter).AddEndpointFilter(AccountsEndpoints.NoStore)
            .WithName(GetUserOperationId);
        group.MapPatch("/user", PatchAsync).AddEndpointFilter(cors.Filter).AddEndpointFilter(AccountsEndpoints.NoStore)
            .WithName(UpdateUserOperationId);
        group.MapPost("/logout", LogoutAsync).AddEndpointFilter(cors.Filter).AddEndpointFilter(AccountsEndpoints.NoStore)
            .WithName(LogoutOperationId);
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, PersonSessions sessions, IAccountStore store, IOptions<AlvoAuthOptions> auth)
    {
        var ct = http.RequestAborted;
        var (session, refusal) = await AuthenticateAsync(http.Request, sessions, auth.Value).ConfigureAwait(false);
        return refusal ?? await UserAsync(store, session!.Account, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> PatchAsync(
        HttpContext http,
        PersonSessions sessions,
        IAccountStore store,
        IOptions<AlvoAuthOptions> auth,
        IOptions<AlvoApiOptions> api)
    {
        var ct = http.RequestAborted;
        var (session, refusal) = await AuthenticateAsync(http.Request, sessions, auth.Value).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (JsonContentType.Refuse(http.Request) is { } unsupported)
        {
            return unsupported;
        }

        var (name, violations) = await DisplayName.ReadAsync(http.Request, api.Value, ct).ConfigureAwait(false);
        if (name is null)
        {
            return ProblemResultFactory.Validation(violations);
        }

        return await store.SetDisplayNameAsync(session!.Account, name, ct).ConfigureAwait(false)
            ? await UserAsync(store, session.Account, ct).ConfigureAwait(false)
            : Unusable();
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http, PersonSessions sessions, IAccountStore store, IOptions<AlvoAuthOptions> auth, TimeProvider clock)
    {
        var ct = http.RequestAborted;
        var (token, refusal) = Bearer(http.Request, auth.Value);
        if (token is null)
        {
            return refusal!;
        }

        if (await sessions.IdentifyAsync(token, http.Request, ct).ConfigureAwait(false) is not { } session)
        {
            return Unusable();
        }

        await store.RevokeSessionAsync(session.Session, SessionRevocations.Logout, clock.GetUtcNow(), ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>The live session the request's Bearer token names, or the 401 to answer instead.</summary>
    private static async ValueTask<(PersonSession? Session, IResult? Refusal)> AuthenticateAsync(
        HttpRequest request, PersonSessions sessions, AlvoAuthOptions auth)
    {
        var (token, refusal) = Bearer(request, auth);
        if (token is null)
        {
            return (null, refusal);
        }

        var session = await sessions.AuthenticateAsync(token, request, request.HttpContext.RequestAborted).ConfigureAwait(false);
        return session is null ? (null, Unusable()) : (session, null);
    }

    /// <summary>The Bearer token the request presents, or the 401 a request presenting none (usable) earns.</summary>
    private static (string? Token, IResult? Refusal) Bearer(HttpRequest request, AlvoAuthOptions auth)
    {
        var credential = CallerResolution.Read(request, auth, CredentialSurface.Data, personRegistered: true);
        return credential.Channel switch
        {
            CredentialChannel.Bearer => (credential.Value, null),
            CredentialChannel.MalformedBearer or CredentialChannel.Ambiguous =>
                (null, ProblemResultFactory.BearerUnauthenticated(InvalidRequest)),
            _ => (null, ProblemResultFactory.BearerRequired()),
        };
    }

    private static IResult Unusable() => ProblemResultFactory.BearerUnauthenticated(InvalidToken);

    private static async Task<IResult> UserAsync(IAccountStore store, UserId account, CancellationToken ct)
        => await store.GetAccountAsync(account, ct).ConfigureAwait(false) is { } view
            ? UserResponse.From(view).Ok()
            : Unusable();
}
