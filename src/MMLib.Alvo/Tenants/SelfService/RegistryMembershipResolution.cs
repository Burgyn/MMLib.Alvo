using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// Membership resolution over the self-service registry (S3 plan D4): <see cref="MembershipRules"/> over
/// <see cref="ITenantStore.FindMembershipAsync"/>, for a caller that has no session read to fold the membership into —
/// event actors and personal tokens (<c>AppTokenPrincipals</c>, S5 plan D6). The person resolver decides by the same rules over its one read.
/// </summary>
/// <remarks>
/// A refusal and a request with no tenant to read cost no I/O; otherwise one registry read. The applied policy is read
/// once per call, so the plan and the decision judge one descriptor.
/// </remarks>
/// <param name="store">The tenant registry.</param>
/// <param name="catalogs">The applied policy catalog, whose self-service policy decides.</param>
internal sealed class RegistryMembershipResolution(ITenantStore store, IPolicyCatalogProvider catalogs) : IMembershipResolution
{
    /// <inheritdoc/>
    public async ValueTask<MembershipResolution> ResolveAsync(UserId user, TenantId? requested, CancellationToken ct)
    {
        var policy = catalogs.Current?.SelfService ?? SelfServicePolicy.None;
        var plan = MembershipRules.Plan(requested, policy);
        return plan switch
        {
            { Kind: MembershipPlanKind.Read, Query: { } query } => MembershipRules.Decide(
                requested, await store.FindMembershipAsync(user, query.Tenant, ct).ConfigureAwait(false), policy),
            { Kind: MembershipPlanKind.NoTenant } => MembershipResolution.NoTenant,
            _ => MembershipResolution.Refusal,
        };
    }
}
