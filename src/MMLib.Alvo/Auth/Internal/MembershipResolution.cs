using System.Collections.Frozen;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// Which tenant a signed-in person acts in, and with which tenant roles — membership resolution, the core service spec
/// §5.7 keeps apart from any one credential, so a person's session, a personal token and a later host-federated
/// identity reach tenants exactly the same way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consulted before the session is read</b> (S2 D13): a refusal here costs the caller no store round trip, and the one
/// read a person's request makes stays account + session (spec §14 AC2). The tenant slice (S3) folds the membership into
/// that same read and hands it here, rather than reading it again.
/// </para>
/// <para>
/// <b>A refusal refuses the whole principal</b> — never a principal without a tenant, which would turn "you may not act in
/// tenant X" into "you are a caller with no tenant", the bool trap <see cref="TenantResolver"/>'s remarks name.
/// </para>
/// </remarks>
internal interface IMembershipResolution
{
    /// <summary>The tenant <paramref name="user"/> acts in when they asked for <paramref name="requested"/>.</summary>
    /// <param name="user">The signed-in person.</param>
    /// <param name="requested">The tenant the request named, or <see langword="null"/> when it named none.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The tenant and its roles, no tenant, or the refusal of the whole principal.</returns>
    ValueTask<MembershipResolution> ResolveAsync(UserId user, TenantId? requested, CancellationToken ct);
}

/// <summary>What membership resolution decided.</summary>
/// <param name="Refused">Whether the whole principal is refused.</param>
/// <param name="Tenant">The tenant to act in, or <see langword="null"/> for none.</param>
/// <param name="TenantRoles">The person's roles inside <paramref name="Tenant"/>; empty without one.</param>
internal sealed record MembershipResolution(bool Refused, TenantId? Tenant, IReadOnlySet<Role> TenantRoles)
{
    /// <summary>Gets the refusal of the whole principal.</summary>
    internal static MembershipResolution Refusal { get; } = new(true, null, FrozenSet<Role>.Empty);

    /// <summary>Gets a caller with no tenant: valid for <c>global</c> entities and for level 1.</summary>
    internal static MembershipResolution NoTenant { get; } = new(false, null, FrozenSet<Role>.Empty);
}

/// <summary>
/// Membership resolution before tenants exist (S2): no requested tenant is no tenant, and any requested tenant is refused —
/// there is no membership it could name. No I/O. The tenant slice replaces this registration.
/// </summary>
internal sealed class NoTenantMembershipResolution : IMembershipResolution
{
    /// <inheritdoc/>
    public ValueTask<MembershipResolution> ResolveAsync(UserId user, TenantId? requested, CancellationToken ct)
        => ValueTask.FromResult(requested is null ? MembershipResolution.NoTenant : MembershipResolution.Refusal);
}
