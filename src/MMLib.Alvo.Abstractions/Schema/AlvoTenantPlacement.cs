using MMLib.Alvo.Data;

namespace MMLib.Alvo.Schema;

/// <summary>
/// Which tenant a created row on a <see cref="TenancyMode.Scoped"/> entity lands in: the caller's own, whether the
/// payload names it or leaves it out — the one authority every <see cref="IAlvoData"/> implementation places a row
/// through (#538).
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller's tenant is the only tenant a create can ever reach, so it is the default.</b> The synthesized tenant
/// scope is <c>tenant_id == @tenant.id</c>, and the policy engine denies a create on a scoped entity outright when the
/// caller has no tenant. A payload <c>tenant_id</c> therefore had exactly one value that could succeed — the one the
/// caller already named by acting in a tenant — and leaving it out answered a bare "rejected by policy" that said
/// nothing about what to fix. Filling it in removes a field the caller could only ever get wrong.
/// </para>
/// <para>
/// <b>A value that differs is refused, never overwritten.</b> Silently replacing it would store a row somewhere other
/// than where the caller asked, which is the wrong-stored-value failure this port refuses everywhere else. The refusal
/// is decided from the payload and the caller alone — no row is consulted — so it says nothing about stored data, and
/// it never echoes the value the caller sent. It is the same verdict the tenant scope reaches over the candidate,
/// moved ahead of it so the answer can name the field and the fix.
/// </para>
/// <para>
/// <b>A caller with no tenant is left to the policy engine.</b> Nothing is placed and nothing is refused here: the
/// tenant guard denies that caller before any payload is read, and this type must not invent a tenant for anyone.
/// </para>
/// <para>
/// <see langword="internal"/>, reached by both shipped implementations through Abstractions' <c>InternalsVisibleTo</c>,
/// for the reason <see cref="AlvoManagedColumns"/> keeps its own name list in one place: two implementations deciding
/// "is this the caller's tenant" separately is how they come to answer it differently.
/// </para>
/// </remarks>
internal static class AlvoTenantPlacement
{
    /// <summary>
    /// Why <paramref name="values"/> may not be created in <paramref name="context"/>'s tenant, or
    /// <see langword="null"/> when it may.
    /// </summary>
    /// <param name="values">The caller's own create payload.</param>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="context">The caller the write is performed as.</param>
    internal static string? Refusal(
        IReadOnlyDictionary<string, object?> values, EntitySchema entity, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(values);

        return CallerTenant(entity, context) is { } tenant
            && values.TryGetValue(AlvoManagedColumns.TenantId, out var named)
            && !Names(named, tenant)
            ? AlvoAuthorizationException.ForeignTenant
            : null;
    }

    /// <summary>Throws the <see cref="Refusal"/> for <paramref name="values"/>, if it has one.</summary>
    /// <param name="values">The caller's own create payload.</param>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="context">The caller the write is performed as.</param>
    /// <exception cref="AlvoAuthorizationException">The payload names a tenant other than the caller's.</exception>
    internal static void EnsurePlaceable(
        IReadOnlyDictionary<string, object?> values, EntitySchema entity, AlvoContext context)
    {
        if (Refusal(values, entity, context) is { } refusal)
        {
            throw new AlvoAuthorizationException(refusal);
        }
    }

    /// <summary>
    /// <paramref name="values"/> carrying the caller's tenant as <c>tenant_id</c>, or <paramref name="values"/>
    /// itself when the entity is not scoped or the caller has no tenant.
    /// </summary>
    /// <remarks>
    /// Written whether or not the payload named it: a payload that reached here has passed <see cref="Refusal"/>, so
    /// the value it named is the caller's tenant already, and writing the <see cref="Guid"/> leaves the row one
    /// representation of it whatever spelling the caller used.
    /// </remarks>
    /// <param name="values">The row being created.</param>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="context">The caller the write is performed as.</param>
    internal static IReadOnlyDictionary<string, object?> Placed(
        IReadOnlyDictionary<string, object?> values, EntitySchema entity, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(values);

        return CallerTenant(entity, context) is { } tenant
            ? new Dictionary<string, object?>(values, StringComparer.Ordinal) { [AlvoManagedColumns.TenantId] = tenant.Value }
            : values;
    }

    /// <summary>The caller's tenant when <paramref name="entity"/> is scoped, otherwise <see langword="null"/>.</summary>
    private static TenantId? CallerTenant(EntitySchema entity, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);

        return AlvoManagedColumns.For(entity).Contains(AlvoManagedColumns.TenantId) ? context.Tenant : null;
    }

    /// <summary>
    /// Whether a payload value names <paramref name="tenant"/>: the <see cref="Guid"/> itself, or text that parses as
    /// it — the two shapes a uuid travels through this port as.
    /// </summary>
    private static bool Names(object? value, TenantId tenant) => value switch
    {
        Guid id => id == tenant.Value,
        string text => Guid.TryParse(text, out var id) && id == tenant.Value,
        _ => false,
    };
}
