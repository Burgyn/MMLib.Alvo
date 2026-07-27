using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Refuses a write payload before any row is looked up: a key the entity does not declare, a field the
/// policy marks read-only, and the framework-managed columns a caller may never set.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal here is <see cref="AlvoAuthorizationException"/> and every one of them is decided from
/// the payload alone, so a caller cannot use "was my write rejected" to learn whether a row id exists —
/// the row was never consulted.
/// </para>
/// <para>
/// <b>Which columns are framework-managed is asked, not remembered.</b>
/// <see cref="AlvoManagedColumns.For(EntitySchema)"/> answers it from the entity's own traits — the same
/// question, of the same inputs, that the descriptor mapper injects those columns from. This method used
/// to name two columns while the mapper injected six, and the four it did not know about were
/// caller-writable: a create could assert a victim authored the row and an update could back-date it,
/// with no rule violated on either engine. An enumeration in the guard is exactly what went stale, so
/// there is none.
/// </para>
/// <para>
/// The one asymmetry is <c>tenant_id</c> on a create, and it lives in
/// <see cref="AlvoManagedColumns.IsCallerWritable"/> with its reason: a create legitimately places a row
/// in a tenant, and the synthesized tenant scope over the candidate row decides whether that tenant is
/// allowed. No managed column is ever a descriptor-declared field on an entity the framework manages it
/// for, so none can appear in <see cref="PolicyDecision.ReadOnlyFields"/> — the read-only check alone
/// would let every one of them through.
/// </para>
/// <para>
/// A <c>hidden</c> field is deliberately still writable: <c>hidden</c> restricts reading, and refusing a
/// write to one would tell the caller the field exists.
/// </para>
/// <para>
/// The messages are word for word <c>InMemoryAlvoData</c>'s, because both read them from
/// <see cref="AlvoManagedColumns.RefusalReason"/> — the reference implementation and this one answer the
/// same refusal with the same text, and the adversarial suite asserts on the read-only message naming its
/// field.
/// </para>
/// </remarks>
internal static class WritePayloadGuard
{
    /// <summary>Refuses <paramref name="values"/> if any key is unwritable on this path.</summary>
    /// <param name="values">The caller-supplied payload.</param>
    /// <param name="entity">The entity being written, or <see langword="null"/> when the applied schema does not declare it.</param>
    /// <param name="decision">The verdict <see cref="IPolicyEngine"/> returned for this caller.</param>
    /// <param name="isUpdate">Whether this is an update rather than a create.</param>
    /// <exception cref="AlvoAuthorizationException">A key is undeclared, framework-managed or read-only.</exception>
    internal static void EnsureWritable(
        IReadOnlyDictionary<string, object?> values, EntitySchema? entity, PolicyDecision decision, bool isUpdate)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(decision);

        QueryFieldGuard.EnsureDeclared(values, entity);
        EnsureNoManagedColumnWrite(values, entity, isUpdate);
        EnsureNoReadOnlyWrite(values, decision.ReadOnlyFields);
    }

    /// <summary>
    /// Refuses every column the framework manages for this entity that a caller may not supply on this
    /// path. An entity the applied schema does not declare has already been refused by
    /// <see cref="QueryFieldGuard.EnsureDeclared"/>, so the row key is still covered.
    /// </summary>
    private static void EnsureNoManagedColumnWrite(
        IReadOnlyDictionary<string, object?> values, EntitySchema? entity, bool isUpdate)
    {
        if (entity is null)
        {
            return;
        }

        var refused = AlvoManagedColumns.For(entity)
            .Where(column => !AlvoManagedColumns.IsCallerWritable(column, isUpdate))
            .Where(values.ContainsKey);

        foreach (var column in refused)
        {
            throw new AlvoAuthorizationException(
                $"Field '{column}' {AlvoManagedColumns.RefusalReason(column, isUpdate)}.");
        }
    }

    private static void EnsureNoReadOnlyWrite(
        IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> readOnlyFields)
    {
        foreach (var field in values.Keys.Where(readOnlyFields.Contains))
        {
            throw new AlvoAuthorizationException($"Field '{field}' is read-only and cannot be written.");
        }
    }
}
