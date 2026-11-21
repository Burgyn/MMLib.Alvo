using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// App tokens (spec §7.1, S5 plan D7–D14): <c>POST {prefix}/tokens</c> mints a personal token for the signed-in person, or a
/// service token for a tenant the person owns, and answers the credential — once.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of the gates is the self-service contract</b> (S3 plan Task 7): the person (a token — any key — presented as
/// an API key is 403, never a token minting a token), then <c>auth.accessTokens</c>, then the body, then the kind's own
/// feature, then the person's membership (a personal token naming a tenant), and only then the registry. Right after
/// <c>auth.accessTokens</c>, minting also asks whether this host would ever resolve the token (<see cref="AppTokenReach"/>).
/// </para>
/// <para>
/// <b>The secret is made here and only its hash goes down</b>: the registry stores what it is given and never sees the
/// secret, so no statement, log or refusal below this line can carry it. The registry judges ownership, the tenant kind, the
/// roles the minter holds and the live cap in the mint's own transaction, serialised with every sweep that would revoke the
/// token (D10).
/// </para>
/// </remarks>
internal static class TokenRoutes
{
    /// <summary><c>POST {prefix}/tokens</c>: a new token, answered with its credential.</summary>
    /// <param name="http">The request.</param>
    internal static async Task<IResult> CreateAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var catalog = http.RequestServices.GetRequiredService<IPolicyCatalogProvider>().Current;
        var tokens = catalog?.AppTokens ?? AppTokenPolicy.None;
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, _ => AppTokenProblems.Off(tokens)).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        if (await AppTokenReach.RefusalAsync(http, call.Ct).ConfigureAwait(false) is { } unreachable)
        {
            return unreachable;
        }

        var rules = new TokenRules(tokens, call.Policy, EntitiesOf(catalog), call.Now);
        var (request, invalid) = await call.ReadAsync(
            (body, options, ct) => AppTokenRequest.ReadAsync(body, options, rules, ct)).ConfigureAwait(false);
        if (request is null)
        {
            return invalid!;
        }

        if (AppTokenProblems.KindOff(tokens, request.Kind) is { } disabled)
        {
            return disabled;
        }

        return request is { Kind: ApiKeyKind.Personal, Tenant: { } tenant } && await call.MembershipAsync(tenant).ConfigureAwait(false) is null
            ? SelfServiceProblems.NoSuchTenant()
            : await MintedAsync(call, request).ConfigureAwait(false);
    }

    /// <summary><c>GET {prefix}/tokens</c>: the person's tokens and the service tokens of tenants they own, newest first.</summary>
    /// <param name="http">The request.</param>
    internal static async Task<IResult> ListAsync(HttpContext http)
    {
        var (call, refusal) = await EnterAsync(http).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var tokens = await TokenStore(http).ListAsync(call.Person, call.Ct).ConfigureAwait(false);
        var now = call.Now;
        return SelfServiceCall.Json(
            tokens.Where(token => Honoured(call, token.Kind)).Select(token => AppTokenListItem.From(token, now)).ToList());
    }

    /// <summary><c>DELETE {prefix}/tokens/{keyId}</c>: revokes a token its caller answers for; a second revocation is 204 too.</summary>
    /// <param name="http">The request.</param>
    /// <param name="keyId">The key id, from the route — never the credential.</param>
    internal static async Task<IResult> RevokeAsync(HttpContext http, string keyId)
    {
        var (call, refusal) = await EnterAsync(http).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        if (!Honoured(call, AppTokenSecret.KindOf(keyId)))
        {
            return ProblemResultFactory.NotFound();
        }

        var status = await TokenStore(http).RevokeAsync(new TokenRevocation(keyId, call.Person, call.Now), call.Ct).ConfigureAwait(false);
        return status == RevokeTokenStatus.Revoked ? Results.NoContent() : ProblemResultFactory.NotFound();
    }

    /// <summary>
    /// Whether a token of <paramref name="kind"/> is one the applied descriptor honours: a service token acts in a shared
    /// tenant, so it counts only while that membership does — the one rule the list and the revocation share.
    /// </summary>
    private static bool Honoured(SelfServiceCall call, ApiKeyKind kind) =>
        kind != ApiKeyKind.Service || MembershipRules.Honours(TenantKind.Shared, call.Policy);

    private static ValueTask<(SelfServiceCall? Call, IResult? Refusal)> EnterAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var tokens = http.RequestServices.GetRequiredService<IPolicyCatalogProvider>().Current?.AppTokens ?? AppTokenPolicy.None;
        return SelfServiceCall.EnterAsync(http, _ => AppTokenProblems.Off(tokens));
    }

    private static IAppTokenStore TokenStore(HttpContext http) => http.RequestServices.GetRequiredService<IAppTokenStore>();

    private static HashSet<string> EntitiesOf(PolicyCatalog? catalog) =>
        new(catalog?.Entities.Select(entity => entity.Key) ?? [], StringComparer.Ordinal);

    private static async Task<IResult> MintedAsync(SelfServiceCall call, AppTokenRequest request)
    {
        var secret = AppTokenSecret.New(request.Kind);
        var mint = new MintTokenRequest(
            secret.KeyId,
            request.Kind,
            secret.Hash,
            request.Name,
            call.Person,
            request.Kind == ApiKeyKind.Personal ? call.Person : UserId.New(),
            request.Tenant,
            request.Roles,
            request.ScopeTexts,
            request.ExpiresAt,
            AppTokenProblems.MaxLive,
            call.Now,
            call.Session);
        var outcome = await call.Http.RequestServices.GetRequiredService<IAppTokenStore>().MintAsync(mint, call.Ct).ConfigureAwait(false);
        if (outcome.Status != MintTokenStatus.Minted)
        {
            return AppTokenProblems.Refused(outcome.Status);
        }

        var header = call.Http.RequestServices.GetRequiredService<IOptions<AlvoAuthOptions>>().Value.HeaderName;
        return SelfServiceCall.Json(
            AppTokenResponse.From(outcome.Token!, secret.Presented, header, call.Now), StatusCodes.Status201Created);
    }
}
