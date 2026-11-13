using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Which roles <c>@user.roles</c> holds in one scope (spec §6.2): the caller's project roles everywhere, joined by their
/// tenant roles only in the scope of a <see cref="TenancyMode.Scoped"/> entity — a scoped entity's own rule, or a
/// relation term (and its bound related <c>list</c> rule) into one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The split lives at the fold, not in a decision.</b> A policy decision and a bound relation tree are shared across
/// callers, and role literals fold per caller at render; folding by the scope's own tenancy here is what keeps a tenant
/// role off a global entity's rule, and off a relation term into one, with no per-caller cache anywhere.
/// </para>
/// <para>
/// <b>Tenant roles count only with a tenant.</b> They are a membership's roles <em>inside</em>
/// <see cref="AlvoContext.Tenant"/>; a context carrying them without a tenant is malformed, and they count nowhere.
/// </para>
/// </remarks>
internal static class RoleProvenance
{
    /// <summary>
    /// The role set <c>@user.roles</c> is for <paramref name="caller"/> in a scope of <paramref name="tenancy"/>:
    /// <see cref="AlvoContext.Roles"/> itself — by reference, allocating nothing — unless the scope is tenant-scoped, the
    /// caller has a tenant and the caller carries tenant roles; then the union of both sets.
    /// </summary>
    /// <param name="caller">The caller whose roles fold.</param>
    /// <param name="tenancy">The tenancy of the entity whose scope is evaluated; <see langword="null"/> when untenanted.</param>
    internal static IReadOnlySet<Role> Visible(AlvoContext caller, TenancyMode? tenancy)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (tenancy != TenancyMode.Scoped || caller.Tenant is null || caller.TenantRoles.Count == 0)
        {
            return caller.Roles;
        }

        var union = new HashSet<Role>(caller.Roles);
        union.UnionWith(caller.TenantRoles);
        return union;
    }
}
