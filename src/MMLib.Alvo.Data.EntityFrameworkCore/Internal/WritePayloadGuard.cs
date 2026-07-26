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
/// The two framework columns are handled asymmetrically on purpose. <c>id</c> is assigned once, by this
/// provider, and rewriting it would corrupt row identity — two rows sharing one id, and the row whose id
/// was taken becoming unreachable. <c>tenant_id</c> is legitimately caller-supplied on a create, where
/// the synthesized tenant scope over the candidate row decides whether that tenant is allowed; on an
/// update it is refused outright, because a row can never move to another tenant once created. Neither
/// column is ever a descriptor-declared field, so neither can appear in
/// <see cref="PolicyDecision.ReadOnlyFields"/> — the read-only check alone would let both through.
/// </para>
/// <para>
/// A <c>hidden</c> field is deliberately still writable: <c>hidden</c> restricts reading, and refusing a
/// write to one would tell the caller the field exists.
/// </para>
/// <para>
/// The messages are word for word <c>InMemoryAlvoData</c>'s, so the reference implementation and this one
/// answer the same refusal with the same text — the adversarial suite asserts on the read-only message
/// naming its field, and a divergence there would be a real inconsistency between two implementations of
/// one port.
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
        Refuse(values, AlvoDataContext.IdColumn, IdReason(isUpdate));
        if (isUpdate)
        {
            Refuse(values, AlvoDataContext.TenantIdColumn, TenantReason);
        }

        EnsureNoReadOnlyWrite(values, decision.ReadOnlyFields);
    }

    private static void EnsureNoReadOnlyWrite(
        IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> readOnlyFields)
    {
        foreach (var field in values.Keys.Where(readOnlyFields.Contains))
        {
            throw new AlvoAuthorizationException($"Field '{field}' is read-only and cannot be written.");
        }
    }

    private static void Refuse(IReadOnlyDictionary<string, object?> values, string field, string reason)
    {
        if (values.ContainsKey(field))
        {
            throw new AlvoAuthorizationException($"Field '{field}' {reason}.");
        }
    }

    private static string IdReason(bool isUpdate) => isUpdate
        ? "is assigned once at creation and can never be rewritten"
        : "is assigned by the store and cannot be supplied on create";

    private const string TenantReason = "is fixed at creation and a row can never move to another tenant";
}
