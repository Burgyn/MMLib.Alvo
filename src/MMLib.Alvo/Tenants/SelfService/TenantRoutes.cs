using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The person's tenants (spec §6.3): <c>GET {prefix}/tenants</c> lists them, <c>POST {prefix}/tenants</c> creates a shared
/// one, <c>PATCH {prefix}/tenants/{id}</c> renames one the person owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server chooses a new tenant's id</b> — a client-chosen one would let a caller probe for, or squat on, an id
/// some API-key workflow already writes rows under (spec §6.6). The creator becomes its owner with
/// <c>createTenants.creatorRoles</c>, and the registry holds the creator's account row while it counts, so parallel
/// creates never pass <c>maxPerAccount</c> (S3 plan D3, D9).
/// </para>
/// <para>
/// <b>An answer about a membership is computed from the applied descriptor</b> (D5): the roles shown are the ones that
/// count on the person's next request in that tenant, never a stored name the descriptor no longer lists.
/// </para>
/// </remarks>
internal static class TenantRoutes
{
    private const string InvalidToken = "invalid_token";

    /// <summary><c>GET {prefix}/tenants</c>: the person's memberships, the personal one first.</summary>
    /// <param name="http">The request.</param>
    internal static async Task<IResult> ListAsync(HttpContext http)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.SelfServiceOff).ConfigureAwait(false);
        return call is null
            ? refusal!
            : SelfServiceCall.Json(
                await SelfServiceMemberships.ListAsync(call.Store, call.Policy, call.Person, call.Ct).ConfigureAwait(false));
    }

    /// <summary><c>POST {prefix}/tenants</c>: a new shared tenant, owned by the person.</summary>
    /// <param name="http">The request.</param>
    /// <exception cref="InvalidOperationException">
    /// The registry answered a status this route does not know — a broken invariant, the host's 500, never a guessed
    /// refusal.
    /// </exception>
    internal static async Task<IResult> CreateAsync(HttpContext http)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.CreatingOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var (name, invalid) = await call.ReadAsync(TenantName.ReadAsync).ConfigureAwait(false);
        if (name is null)
        {
            return invalid!;
        }

        var outcome = await call.Store.CreateSharedAsync(Creation(call, name), call.Ct).ConfigureAwait(false);
        return outcome.Status switch
        {
            CreateTenantStatus.Created => Created(call, outcome.Membership!),
            CreateTenantStatus.LimitReached => ProblemResultFactory.TenantLimit(call.Policy.MaxPerAccount),
            CreateTenantStatus.NoAccount => ProblemResultFactory.BearerUnauthenticated(InvalidToken),
            _ => throw new InvalidOperationException(
                $"The tenant registry answered a creation with {outcome.Status}, which this route does not know."),
        };
    }

    /// <summary><c>PATCH {prefix}/tenants/{tenantId}</c>: renames a tenant the person owns.</summary>
    /// <param name="http">The request.</param>
    /// <param name="tenantId">The tenant, from the route.</param>
    internal static async Task<IResult> RenameAsync(HttpContext http, Guid tenantId)
    {
        var (call, refusal) = await SelfServiceCall.EnterAsync(http, SelfServiceProblems.SelfServiceOff).ConfigureAwait(false);
        if (call is null)
        {
            return refusal!;
        }

        var (name, invalid) = await call.ReadAsync(TenantName.ReadAsync).ConfigureAwait(false);
        if (name is null)
        {
            return invalid!;
        }

        var tenant = new TenantId(tenantId);
        if (await call.MembershipAsync(tenant).ConfigureAwait(false) is null)
        {
            return SelfServiceProblems.NoSuchTenant();
        }

        var status = await call.Store.RenameAsync(tenant, call.Person, name, call.Ct).ConfigureAwait(false);
        return status == MemberChangeStatus.Changed
            ? await MembershipAsync(call, tenant).ConfigureAwait(false)
            : SelfServiceProblems.Refused(status);
    }

    private static CreateTenantRequest Creation(SelfServiceCall call, string name) => new(
        TenantId.New(),
        name,
        call.Person,
        [.. call.Policy.CreatorRoles!.Select(role => role.Name).Order(StringComparer.Ordinal)],
        call.Policy.MaxPerAccount,
        call.Now);

    private static IResult Created(SelfServiceCall call, StoredMembership membership)
    {
        var request = call.Http.Request;
        var collection = request.PathBase.Add(request.Path).Value!.TrimEnd('/');
        call.Http.Response.Headers.Location = $"{collection}/{membership.Tenant.Value:D}";
        return SelfServiceCall.Json(SelfServiceMemberships.Response(membership, call.Policy), StatusCodes.Status201Created);
    }

    private static async Task<IResult> MembershipAsync(SelfServiceCall call, TenantId tenant) =>
        await call.Store.FindMembershipAsync(call.Person, tenant, call.Ct).ConfigureAwait(false) is { } membership
            ? SelfServiceCall.Json(SelfServiceMemberships.Response(membership, call.Policy))
            : SelfServiceProblems.NoSuchTenant();
}
