using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The <c>SELECT</c> list a policy-filtered read uses: every field the entity declares, in declaration
/// order, with each masked field replaced by a typed SQL <c>NULL</c> under the field's own alias.
/// </summary>
/// <remarks>
/// <para>
/// Omitting a masked field from the list is not an option — EF requires a <c>FromSql</c> result set to
/// contain every mapped property and fails with "The required column '…' was not present in the results
/// of a 'FromSql' operation", identically on both engines. Projecting the <c>NULL</c> instead means the
/// masked column is never read from the page at all, and the key is dropped again when the
/// <c>AlvoRecord</c> is assembled, so the value never leaves the table by either route.
/// </para>
/// <para>
/// The cast's type comes from the read model's own property (<c>IProperty.GetColumnType()</c>), never
/// from a <see cref="FieldType"/> switch: EF's type mapping is the one authority for what store type a
/// column has, and a second derivation drifts the moment a facet (a <c>MaxLength</c>, a
/// <c>Precision</c>) is involved. A masked field the model does not map therefore has no answer here and
/// is refused rather than guessed.
/// </para>
/// <para>
/// <b>Two sets in, never one.</b> The mask is a security control resolved per caller; the unselected set
/// is the caller's own preference. They are unioned here and nowhere else, and only for one decision —
/// which columns become a projected <c>NULL</c>. <see cref="QueryFieldGuard.EnsureMaskable"/> keeps
/// seeing the mask <em>alone</em>, because it answers with <see cref="AlvoAuthorizationException"/> and a
/// caller's own projection must never produce a 403.
/// </para>
/// </remarks>
internal static class ReadProjection
{
    /// <summary>Composes the <c>SELECT</c> list for one policy-filtered read.</summary>
    /// <param name="entity">The entity being read, as the applied schema declares it.</param>
    /// <param name="hiddenFields">The field mask the caller's policy resolved — a security control.</param>
    /// <param name="unselectedFields">
    /// The declared fields the caller's projection excluded — a caller preference. Empty for a read with no
    /// projection, which is every read but a projected page.
    /// </param>
    /// <param name="dialect">The engine's renderer.</param>
    /// <param name="rows">The read model's entity type, the one authority for a column's store type.</param>
    internal static string Compose(
        EntitySchema entity,
        IReadOnlySet<string> hiddenFields,
        IReadOnlySet<string> unselectedFields,
        IAlvoSqlDialect dialect,
        IEntityType rows)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(unselectedFields);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(rows);
        QueryFieldGuard.EnsureMaskable(hiddenFields, rows);

        return string.Join(
            ", ", entity.Fields.Select(field => Project(field, hiddenFields, unselectedFields, dialect, rows)));
    }

    /// <summary>Renders one field: the column, or a typed <c>NULL</c> aliased to the column's name.</summary>
    /// <remarks>
    /// <b>The mask is tested first, and the order is load-bearing rather than stylistic.</b> The two sets
    /// overlap on every projected read of a masked entity — a hidden field is never selected, never a sort
    /// key and never framework-managed, so it is always unselected too. Testing <paramref name="unselectedFields"/>
    /// first would answer a masked field's unresolvable store type with <see cref="InvalidOperationException"/>
    /// and silently undo the split <see cref="NoStoreType"/> exists for.
    /// </remarks>
    private static string Project(
        FieldSchema field,
        IReadOnlySet<string> hiddenFields,
        IReadOnlySet<string> unselectedFields,
        IAlvoSqlDialect dialect,
        IEntityType rows)
    {
        if (hiddenFields.Contains(field.Name))
        {
            return NullProjection(field.Name, dialect, rows, masked: true);
        }

        return unselectedFields.Contains(field.Name)
            ? NullProjection(field.Name, dialect, rows, masked: false)
            : dialect.RenderColumn(field.Name);
    }

    private static string NullProjection(string field, IAlvoSqlDialect dialect, IEntityType rows, bool masked)
    {
        var storeType = rows.FindProperty(field)?.GetColumnType() ?? throw NoStoreType(field, masked);
        return $"{dialect.RenderNullProjection(storeType)} AS {dialect.RenderColumn(field)}";
    }

    /// <summary>
    /// The two sets fail differently, and that is the point. A <b>mask</b> the read model cannot apply is
    /// the fail-closed case <see cref="QueryFieldGuard"/> exists for — it can arrive from a source that
    /// never ran the apply-time check, F7's dynamic-entity registry being the next one. An
    /// <b>unselected</b> field the model cannot map is unreachable by construction, because that set is
    /// derived from the applied schema's own fields: reaching it means the schema and the read model
    /// disagree, which is an Alvo defect and must not be dressed as a decision about the caller.
    /// </summary>
    private static Exception NoStoreType(string field, bool masked) => masked
        ? new AlvoAuthorizationException(QueryFieldGuard.UnmaskableFieldMessage)
        : new InvalidOperationException(
            $"The applied schema declares '{field}' but the read model maps no such property, so a "
            + "projected NULL has no store type to cast to. The applied schema and the read model disagree.");
}
