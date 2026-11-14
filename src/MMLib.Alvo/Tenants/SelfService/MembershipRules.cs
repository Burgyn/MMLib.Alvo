using MMLib.Alvo.Accounts;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>What membership resolution has to do for one request, decided before any I/O.</summary>
internal enum MembershipPlanKind
{
    /// <summary>Refuse the whole principal: a tenant was named that no membership can answer.</summary>
    Refuse,

    /// <summary>No tenant: none was named and the descriptor gives no personal tenant.</summary>
    NoTenant,

    /// <summary>Read the membership <see cref="MembershipPlan.Query"/> names, then decide.</summary>
    Read,
}

/// <summary>The plan for one request's membership.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Query">The membership to read, for <see cref="MembershipPlanKind.Read"/>; otherwise <see langword="null"/>.</param>
internal readonly record struct MembershipPlan(MembershipPlanKind Kind, MembershipQuery? Query);

/// <summary>
/// Which tenant a person acts in, and with which tenant roles — one pure decision (S3 plan D4, D5) shared by the person
/// resolver, which reads the membership in its one session read, and by <see cref="RegistryMembershipResolution"/>, which
/// reads it from the registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-deny</b> (D5): with <c>tenancy.selfService</c> absent no membership is honoured — a named tenant refuses the
/// whole principal and none is no tenant; with <c>personalTenant</c> absent a personal membership is honoured neither.
/// </para>
/// <para>
/// <b>Effective roles are recomputed on every request</b> (D5): a shared membership's stored names ∩ the applied
/// <c>assignableRoles</c>, a personal one's the applied <c>personalTenant.roles</c> whatever its row holds — so a
/// descriptor change counts on the next request and no stored name outlives its reason.
/// </para>
/// <para>
/// <b>Only a personal tenant's owner holds <c>personalTenant.roles</c></b> — defence in depth: a personal tenant has exactly
/// one member, its owner (spec §6.1), so a personal row that is not the owner's is one the registry should never hold, and it
/// is honoured as no membership at all rather than lending the personal roles to whoever it names.
/// </para>
/// <para>
/// <b>Never a principal without the tenant it named</b> (spec §5.7 step 3): every path that has a named tenant and no
/// membership answering it is <see cref="MembershipResolution.Refusal"/>.
/// </para>
/// </remarks>
internal static class MembershipRules
{
    private static readonly MembershipPlan _refuse = new(MembershipPlanKind.Refuse, null);

    private static readonly MembershipPlan _noTenant = new(MembershipPlanKind.NoTenant, null);

    private static readonly MembershipPlan _personal = new(MembershipPlanKind.Read, new MembershipQuery(null));

    /// <summary>What to read, if anything, for a request that named <paramref name="requested"/>.</summary>
    /// <param name="requested">The tenant the request named, or <see langword="null"/>.</param>
    /// <param name="policy">The applied self-service policy.</param>
    internal static MembershipPlan Plan(TenantId? requested, SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
        {
            return requested is null ? _noTenant : _refuse;
        }

        return requested is { } tenant ? new MembershipPlan(MembershipPlanKind.Read, new MembershipQuery(tenant))
            : policy.HasPersonalTenant ? _personal
            : _noTenant;
    }

    /// <summary>What the membership <paramref name="found"/> for a request that named <paramref name="requested"/> means.</summary>
    /// <param name="requested">The tenant the request named, or <see langword="null"/> for the personal one.</param>
    /// <param name="found">The membership read, or <see langword="null"/> when there is none.</param>
    /// <param name="policy">The applied self-service policy.</param>
    internal static MembershipResolution Decide(TenantId? requested, StoredMembership? found, SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Answers(requested, found, policy))
        {
            return requested is null ? MembershipResolution.NoTenant : MembershipResolution.Refusal;
        }

        return found!.Kind == TenantKind.Personal
            ? new MembershipResolution(false, found.Tenant, policy.PersonalTenantRoles!)
            : new MembershipResolution(false, found.Tenant, policy.Effective(found.Roles));
    }

    /// <summary>
    /// Whether <paramref name="policy"/> honours a registry tenant of <paramref name="kind"/> at all: a shared one whenever
    /// <c>selfService</c> is on, a personal one only while the descriptor gives personal tenants. The one test a membership
    /// (<see cref="Honours(StoredMembership, SelfServicePolicy)"/>) and the default tenant list
    /// (<see cref="RegistryTenantCatalog"/>) both pass through, so a tenant nobody can act in gets no run either.
    /// </summary>
    /// <param name="kind">The tenant's kind.</param>
    /// <param name="policy">The applied self-service policy.</param>
    internal static bool Honours(TenantKind kind, SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Enabled && (kind == TenantKind.Shared || policy.HasPersonalTenant);
    }

    /// <summary>
    /// Whether <paramref name="policy"/> honours <paramref name="membership"/> — its tenant's kind, and a personal one only
    /// as its owner's (the remarks' defence in depth). Every membership a request, a route or a listing counts passes it.
    /// </summary>
    /// <param name="membership">The stored membership.</param>
    /// <param name="policy">The applied self-service policy.</param>
    internal static bool Honours(StoredMembership membership, SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return Honours(membership.Kind, policy) && (membership.Kind == TenantKind.Shared || membership.IsOwner);
    }

    /// <summary>
    /// Whether <paramref name="found"/> is a membership the policy honours for this request: one it honours at all, of the
    /// tenant named — or, with none named, a personal one.
    /// </summary>
    private static bool Answers(TenantId? requested, StoredMembership? found, SelfServicePolicy policy) =>
        found is not null
        && Honours(found, policy)
        && (requested is { } tenant ? found.Tenant == tenant : found.Kind == TenantKind.Personal);
}
