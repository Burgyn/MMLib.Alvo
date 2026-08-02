using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The field-name checks every EF-backed Alvo driver runs before composing a statement: a filter or sort
/// key must name a field the caller can actually read, a write payload must name a field the entity
/// declares, and a field mask must be one this read model can actually apply. Shared here rather than
/// copied per driver — a per-driver copy of a security check is how two engines come to disagree about
/// what is refused.
/// </summary>
/// <remarks>
/// A field name is the one caller-supplied string that reaches SQL as an <b>identifier</b>, and SQL has
/// no bind-parameter form of a column name, so validating it against the schema here is what makes that
/// interpolation safe; the engine's own unknown-column error arrives after the statement is composed and
/// echoes schema internals. Both caller-facing refusals carry the <em>same</em> message and name neither
/// the field nor the reason: a caller must not be able to tell "exists but hidden from you" from "does not
/// exist", and the name itself is attacker-controlled text this layer will not echo into a log.
/// </remarks>
internal static class QueryFieldGuard
{
    /// <summary>
    /// Names no field, because the mask is authored (or synthesized) backend-side rather than supplied by
    /// the caller whose read is being refused — but it is still an <see cref="AlvoAuthorizationException"/>,
    /// so a caller learns nothing beyond "denied".
    /// </summary>
    internal const string UnmaskableFieldMessage = "The resolved field mask cannot be applied to this entity.";

    private const string UnavailableQueryFieldMessage = "The query references a field that is not available to this caller.";

    private const string UndeclaredPayloadFieldMessage = "The payload names a field that is not writable on this entity.";

    /// <summary>
    /// Refuses a filter or sort key the caller cannot read — one the entity does not declare, or one the
    /// resolved mask hides. Both arms raise the identical message, so the refusal is not an oracle for
    /// "exists but hidden from you".
    /// </summary>
    /// <param name="fields">The caller-supplied field names a statement is about to reference.</param>
    /// <param name="entity">The entity being read, or <see langword="null"/> when the applied schema does not declare it.</param>
    /// <param name="hiddenFields">The resolved field mask.</param>
    /// <exception cref="AlvoAuthorizationException">A name is undeclared or masked.</exception>
    internal static void EnsureAvailable(IEnumerable<string> fields, EntitySchema? entity, IReadOnlySet<string> hiddenFields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(hiddenFields);

        var declared = DeclaredFields(entity);
        foreach (var field in fields)
        {
            if (hiddenFields.Contains(field) || !declared.Contains(field))
            {
                throw new AlvoAuthorizationException(UnavailableQueryFieldMessage);
            }
        }
    }

    /// <summary>
    /// Resolves a caller-supplied field name against the entity's declared fields and returns the
    /// <b>declared</b> field, so the string a renderer interpolates is one the schema owns rather than the
    /// caller's own bytes — and so the comparison's type comes from the schema too.
    /// </summary>
    /// <remarks>
    /// The local half of the same check <see cref="EnsureAvailable"/> makes for a whole statement, raising the
    /// identical message. Both a filter renderer and a keyset renderer need it, and one implementation is the
    /// point: a second copy is how a name refused on one path becomes an identifier on another.
    /// </remarks>
    /// <param name="entity">The entity being queried, as the applied schema declares it.</param>
    /// <param name="field">The caller-supplied field name.</param>
    /// <exception cref="AlvoAuthorizationException"><paramref name="field"/> is not declared.</exception>
    internal static FieldSchema DeclaredField(EntitySchema entity, string field)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return entity.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal))
            ?? throw new AlvoAuthorizationException(UnavailableQueryFieldMessage);
    }

    /// <summary>
    /// Refuses a write payload naming a field the entity does not declare. A masked field is deliberately
    /// <em>allowed</em> here: <c>hidden</c> is a read restriction, and refusing a write to one would tell the
    /// caller the field exists.
    /// </summary>
    /// <param name="values">The caller-supplied payload.</param>
    /// <param name="entity">The entity being written, or <see langword="null"/> when the applied schema does not declare it.</param>
    /// <exception cref="AlvoAuthorizationException">The payload names an undeclared field.</exception>
    internal static void EnsureDeclared(IReadOnlyDictionary<string, object?> values, EntitySchema? entity)
    {
        ArgumentNullException.ThrowIfNull(values);

        var declared = DeclaredFields(entity);
        foreach (var field in values.Keys)
        {
            if (!declared.Contains(field))
            {
                throw new AlvoAuthorizationException(UndeclaredPayloadFieldMessage);
            }
        }
    }

    /// <summary>
    /// An entity the applied schema does not know declares nothing, so every name fails closed. A mismatch
    /// between the policy catalog and the applied schema must not be the one path on which an unvalidated
    /// name reaches storage. Ordinal, like every other field lookup in Alvo — the schema, the CEL type
    /// checker and the rendered SQL all use the exact declared name, and a case-insensitive match here would
    /// admit a name none of them agreed to.
    /// </summary>
    private static HashSet<string> DeclaredFields(EntitySchema? entity) =>
        entity is null ? [] : [.. entity.Fields.Select(field => field.Name)];

    /// <summary>
    /// The fail-closed belt on the read path: a mask that hides the row key, or a field this read model does
    /// not map, is refused rather than projected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>hidden</c>/<c>readOnly</c> flag on the row key is already refused at <em>apply</em> time
    /// (<c>PolicyCatalogBuilder</c>), which is where a bad descriptor belongs — Alvo's rule is that a
    /// descriptor fails at save, never per request. This is the second check for the case that check cannot
    /// see: a <c>SchemaModel</c> and a mask arriving from a source that never ran it, F7's dynamic-entity
    /// registry being the obvious next one.
    /// </para>
    /// <para>
    /// It matters because EF re-marks a key property required whatever <c>IsRequired(false)</c> asked, so a
    /// projected <c>NULL</c> for the key throws at materialization — <see cref="InvalidOperationException"/>
    /// on SQLite, <see cref="InvalidCastException"/> on PostgreSQL. One deterministic denial is strictly
    /// better than two engine-specific crashes, and the key is asked of EF rather than assumed to be
    /// <c>id</c>, so the model itself stays the authority.
    /// </para>
    /// </remarks>
    /// <param name="hiddenFields">The resolved field mask.</param>
    /// <param name="rows">The read model's entity type for the entity being read.</param>
    /// <exception cref="AlvoAuthorizationException">The mask hides a key property, or the model has no key.</exception>
    internal static void EnsureMaskable(IReadOnlySet<string> hiddenFields, IEntityType rows)
    {
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(rows);

        var key = rows.FindPrimaryKey() ?? throw new AlvoAuthorizationException(UnmaskableFieldMessage);
        if (key.Properties.Any(property => hiddenFields.Contains(property.Name)))
        {
            throw new AlvoAuthorizationException(UnmaskableFieldMessage);
        }
    }
}
