using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>What the self-service routes answer when they do not do what was asked (S3 plan D7, D8).</summary>
/// <remarks>
/// <para>
/// <b>A tenant the person is no member of is <c>404 not-found</c>, byte for byte the answer for an id that names nothing</b>
/// — GitHub's rule for a private organisation: the routes never confirm that a tenant exists to someone outside it.
/// </para>
/// <para>
/// <b>Every detail is a fixed sentence.</b> None echoes an id, a name or a role the caller sent, so a log of refusals holds
/// nothing a request supplied.
/// </para>
/// </remarks>
internal static class SelfServiceProblems
{
    /// <summary>The descriptor key that enables every self-service route.</summary>
    internal const string SelfServiceKey = "tenancy.selfService";

    /// <summary>The descriptor key that enables creating shared tenants.</summary>
    internal const string CreateTenantsKey = "tenancy.selfService.createTenants";

    private const string NotOwner = "Only an owner of this tenant makes this change.";

    private const string Self = "Nobody changes their own membership; another owner can, and you can leave.";

    private const string Personal = "A personal tenant has exactly one member, its owner.";

    /// <summary>The 404 <c>feature-disabled</c> while <paramref name="policy"/> leaves self-service off, else <see langword="null"/>.</summary>
    /// <param name="policy">The applied self-service policy.</param>
    internal static IResult? SelfServiceOff(SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Enabled ? null : ProblemResultFactory.FeatureDisabled(SelfServiceKey);
    }

    /// <summary>The 404 <c>feature-disabled</c> while <paramref name="policy"/> lets nobody create a tenant, else <see langword="null"/>.</summary>
    /// <param name="policy">The applied self-service policy.</param>
    internal static IResult? CreatingOff(SelfServicePolicy policy) =>
        SelfServiceOff(policy) ?? (policy.CreatesTenants ? null : ProblemResultFactory.FeatureDisabled(CreateTenantsKey));

    /// <summary>The answer for a tenant or membership write the registry did not make.</summary>
    /// <param name="status">What the registry did; never <see cref="MemberChangeStatus.Changed"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is <see cref="MemberChangeStatus.Changed"/>.</exception>
    internal static IResult Refused(MemberChangeStatus status) => status switch
    {
        MemberChangeStatus.NotFound or MemberChangeStatus.NoSuchMember => ProblemResultFactory.NotFound(),
        MemberChangeStatus.NotOwner => ProblemResultFactory.PersonForbidden(NotOwner),
        MemberChangeStatus.Self => ProblemResultFactory.PersonForbidden(Self),
        MemberChangeStatus.LastOwner => ProblemResultFactory.LastOwner(),
        MemberChangeStatus.PersonalTenant => ProblemResultFactory.MembershipConflict(Personal),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A change that was made is no refusal."),
    };

    /// <summary>The 404 for a tenant the person is no member of, or that does not exist — one answer for both.</summary>
    internal static IResult NoSuchTenant() => ProblemResultFactory.NotFound();
}
