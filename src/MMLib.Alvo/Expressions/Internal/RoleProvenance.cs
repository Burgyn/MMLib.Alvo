using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Which roles <c>@user.roles</c> holds on one decision's path (spec §6.2): the caller's project roles everywhere, joined
/// by their tenant roles only while <b>every</b> entity on the path is <see cref="TenancyMode.Scoped"/> — a scoped
/// entity's own rule, and each relation term (with its bound related <c>list</c> rule) reached from it through scoped
/// entities only.
/// </summary>
/// <remarks>
/// <para>
/// <b>The split lives at the fold, not in a decision.</b> A policy decision and a bound relation tree are shared across
/// callers, and role literals fold per caller at render; folding by the path here keeps a tenant role off a global
/// entity's rule, off anything reached from one, and off anything past a global hop, with no per-caller cache anywhere.
/// </para>
/// <para>
/// <b>A global entity on the path ends it.</b> Folding by the related entity alone would let a global <c>regions</c>
/// whose <c>update</c> reads <c>jobs.exists(j, true)</c> be updated by anyone who holds a tenant role a scoped
/// <c>jobs</c> list admits: a decision about shared data must never depend on a tenant role, not even transitively.
/// </para>
/// <para>
/// <b>Tenant roles count only with a tenant.</b> They are a membership's roles <em>inside</em>
/// <see cref="AlvoContext.Tenant"/>; a context carrying them without a tenant is malformed, and they count nowhere.
/// </para>
/// </remarks>
internal static class RoleProvenance
{
    /// <summary>The role set on a path of one entity of <paramref name="tenancy"/>: <see cref="OnPath"/> for that path.</summary>
    /// <param name="caller">The caller whose roles fold.</param>
    /// <param name="tenancy">The entity's tenancy; <see langword="null"/> when untenanted.</param>
    internal static IReadOnlySet<Role> Visible(AlvoContext caller, TenancyMode? tenancy) =>
        OnPath(caller, StillScoped(true, tenancy));

    /// <summary>Whether a path that was <paramref name="scopedSoFar"/> stays scoped once it reaches an entity of <paramref name="next"/>.</summary>
    /// <param name="scopedSoFar">Whether every entity on the path before this one is scoped.</param>
    /// <param name="next">The tenancy of the entity the path reaches.</param>
    internal static bool StillScoped(bool scopedSoFar, TenancyMode? next) => scopedSoFar && next == TenancyMode.Scoped;

    /// <summary>
    /// The role set <c>@user.roles</c> is for <paramref name="caller"/> on a path: <see cref="AlvoContext.Roles"/> itself —
    /// by reference, allocating nothing — unless every entity on the path is scoped, the caller has a tenant and the caller
    /// carries tenant roles; then the union of both sets.
    /// </summary>
    /// <param name="caller">The caller whose roles fold.</param>
    /// <param name="everyEntityScoped">Whether every entity on the decision's path, the evaluated one included, is scoped.</param>
    internal static IReadOnlySet<Role> OnPath(AlvoContext caller, bool everyEntityScoped)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!everyEntityScoped || caller.Tenant is null || caller.TenantRoles.Count == 0)
        {
            return caller.Roles;
        }

        var union = new HashSet<Role>(caller.Roles);
        union.UnionWith(caller.TenantRoles);
        return union;
    }
}
