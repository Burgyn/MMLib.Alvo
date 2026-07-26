using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The storage driver's half of <b>statement</b> shape, as
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/> is its half of <b>expression</b> shape. The
/// shared EF data path composes only structure — a <c>SELECT</c> list, a <c>FROM</c>, the
/// <c>AND</c>-joined <c>WHERE</c> — and asks this interface for everything a dialect owns: how a table
/// is named, how a column is quoted, how a typed SQL <c>NULL</c> is spelled for a masked field, and
/// whether a pre-image read can be locked.
/// </summary>
/// <remarks>
/// <para>
/// This lives beside the EF data path rather than in <c>Abstractions</c> on purpose: statement shape is
/// a relational concern, and <c>Abstractions</c> is required to stay free of one. It is also why
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer"/> was not extended with a table-rendering
/// member — that port renders expressions, and every existing implementation (including
/// <c>MMLib.Alvo.Testing.TestFieldSqlRenderer</c>) would have had to grow a member it has no table for.
/// </para>
/// <para>
/// F7's dynamic-entity driver implements this interface too: there <see cref="RenderTable"/> returns a
/// JSON-projecting sub-select over the one shared partitioned store rather than a table name, which is
/// what makes "the same adversarial suite passes over a physical and a virtual entity" a matter of
/// registering another dialect instead of rewriting the data path.
/// </para>
/// </remarks>
public interface IAlvoSqlDialect
{
    /// <summary>
    /// Renders the table source <paramref name="entity"/>'s rows are read from — a quoted table name on
    /// a physical entity.
    /// </summary>
    /// <remarks>
    /// A driver must not qualify the name with a database schema unless it actually has one: SQLite has
    /// no schemas at all, and <c>AlvoOptions.SchemaPrefix</c> is a framework-<em>table</em> name prefix,
    /// not a schema. Both in-repo drivers return the bare quoted entity name, matching the
    /// <c>ToTable(entity.Name)</c> the migration model already uses.
    /// </remarks>
    /// <param name="entity">The entity being read.</param>
    string RenderTable(EntitySchema entity);

    /// <summary>Renders a column reference in a <c>SELECT</c> list or an <c>ORDER BY</c>.</summary>
    /// <param name="columnName">The column's name.</param>
    string RenderColumn(string columnName);

    /// <summary>
    /// Renders a typed SQL <c>NULL</c> standing in for a masked field's value — the mechanism that keeps
    /// a <c>hidden</c> field's data inside the table. An untyped bare <c>NULL</c> is not enough: the
    /// result set has to satisfy the mapped property's store type, so the cast names this dialect's own
    /// type for <paramref name="field"/>.
    /// </summary>
    /// <param name="field">The masked field.</param>
    string RenderNullProjection(FieldSchema field);

    /// <summary>
    /// Gets the clause appended to a pre-image read whose result a <c>WITH CHECK</c> decision will be
    /// based on, so a concurrent writer cannot change the row between the check and the write —
    /// <c>" FOR UPDATE"</c> on PostgreSQL, the empty string where the engine has no such clause and
    /// serializes write transactions instead (SQLite).
    /// </summary>
    string RowLockHint { get; }
}
