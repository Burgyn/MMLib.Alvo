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
/// </remarks>
internal static class ReadProjection
{
    internal static string Compose(
        EntitySchema entity, IReadOnlySet<string> hiddenFields, IAlvoSqlDialect dialect, IEntityType rows)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(hiddenFields);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(rows);
        QueryFieldGuard.EnsureMaskable(hiddenFields, rows);

        return string.Join(", ", entity.Fields.Select(field => Project(field, hiddenFields, dialect, rows)));
    }

    private static string Project(
        FieldSchema field, IReadOnlySet<string> hiddenFields, IAlvoSqlDialect dialect, IEntityType rows) =>
        hiddenFields.Contains(field.Name)
            ? $"{dialect.RenderNullProjection(StoreTypeOf(field.Name, rows))} AS {dialect.RenderColumn(field.Name)}"
            : dialect.RenderColumn(field.Name);

    private static string StoreTypeOf(string fieldName, IEntityType rows) =>
        rows.FindProperty(fieldName)?.GetColumnType()
        ?? throw new AlvoAuthorizationException(QueryFieldGuard.UnmaskableFieldMessage);
}
