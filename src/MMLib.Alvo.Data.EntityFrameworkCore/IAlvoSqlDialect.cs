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
    /// <para>
    /// A driver must not qualify the name with a database schema unless it actually has one: SQLite has
    /// no schemas at all, and <c>AlvoOptions.SchemaPrefix</c> is a framework-<em>table</em> name prefix,
    /// not a schema. Both in-repo drivers return the bare quoted entity name, matching the
    /// <c>ToTable(entity.Name)</c> the migration model already uses.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> The result is interpolated verbatim as the <c>FROM</c> clause's table
    /// source. It must not include the <c>FROM</c> keyword, must not carry an alias, and must not carry a
    /// statement terminator or any surrounding whitespace. A dialect whose table source is a query rather
    /// than a name — F7's dynamic driver, which projects JSON paths out of one shared partitioned store —
    /// must return it <b>parenthesised</b> (<c>(SELECT … FROM …)</c>), because the composer adds no
    /// parentheses of its own; EF then wraps the whole <c>FromSql</c> root in its own derived table and
    /// supplies the alias.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity being read.</param>
    string RenderTable(EntitySchema entity);

    /// <summary>Renders a column reference in a <c>SELECT</c> list or an <c>ORDER BY</c>.</summary>
    /// <remarks>
    /// <b>Return grammar.</b> A bare column reference: quoted per this dialect, with no table or alias
    /// qualifier, no <c>AS</c> alias of its own, and no separating comma — the composer joins the
    /// <c>SELECT</c> list and appends an alias where one is needed (see
    /// <see cref="RenderNullProjection"/>).
    /// </remarks>
    /// <param name="columnName">The column's name.</param>
    string RenderColumn(string columnName);

    /// <summary>
    /// Renders a typed SQL <c>NULL</c> standing in for a masked field's value — the mechanism that keeps
    /// a <c>hidden</c> field's data inside the table. An untyped bare <c>NULL</c> is not enough: the
    /// result set has to satisfy the mapped property's store type, so the cast names
    /// <paramref name="storeType"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dialect decides only the <em>cast syntax</em>; it must not decide the type. The one authority for
    /// "what store type does this column have" is EF's own <c>IRelationalTypeMappingSource</c>, reached
    /// through the mapped property (<c>IProperty.GetColumnType()</c>) — the very thing that produced the
    /// table's DDL, so it honours the <c>HasMaxLength</c>/<c>HasPrecision</c> calls
    /// <c>DescriptorModelBuilder.ConfigureField</c> makes, and it is per provider by construction. A
    /// dialect deriving a type name from a <see cref="FieldSchema"/> instead would be a second,
    /// unreconciled authority, and this port's first revision proved that drifts immediately: it answered
    /// <c>numeric(18,2)</c> for every <c>decimal</c> regardless of its declared precision, <c>jsonb</c>
    /// for a <c>json</c> field whose column the migrator creates as <c>text</c>, and <c>text</c> for a
    /// length-bounded string whose column is <c>character varying(N)</c>.
    /// </para>
    /// <para>
    /// <b>Return grammar.</b> The result is a bare SQL <em>expression</em>, interpolated verbatim into a
    /// <c>SELECT</c> list. It must not carry an <c>AS &lt;column&gt;</c> alias — the composer appends the
    /// masked field's alias itself, through <see cref="RenderColumn"/> — and it must not carry a
    /// separating comma.
    /// </para>
    /// <para>
    /// <paramref name="storeType"/> reaches the SQL text unparameterized, because a type name has no
    /// bind-parameter form. That is safe only because it comes from EF's type mapping; a dialect must never
    /// be handed one assembled from caller input.
    /// </para>
    /// </remarks>
    /// <param name="storeType">
    /// The masked column's EF-resolved store type, exactly as this provider spells it (e.g.
    /// <c>character varying(32)</c>, <c>numeric(10,4)</c>, <c>TEXT</c>).
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="storeType"/> is null, empty or whitespace.</exception>
    string RenderNullProjection(string storeType);

    /// <summary>
    /// Gets the row-locking clause appended to a pre-image read whose result a <c>WITH CHECK</c> decision
    /// will be based on, so a concurrent writer cannot change the row between the check and the write —
    /// <c>FOR NO KEY UPDATE</c> on PostgreSQL, the empty string where the engine has no such clause and
    /// serializes write transactions instead (SQLite).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Return grammar.</b> The clause itself, carrying <b>no separator of its own</b> — no leading
    /// space, no trailing space, no terminator. The composer inserts the separating space, and only when
    /// the value is non-empty. A dialect that shipped its own leading space would double the separator
    /// here and a dialect that omitted one under the opposite convention would produce
    /// <c>… WHERE &lt;predicate&gt;FOR NO KEY UPDATE</c> — a syntax error in the one statement a
    /// <c>WITH CHECK</c> verdict is based on. Return <see cref="string.Empty"/>, not <c>" "</c>, when the
    /// engine has no such clause.
    /// </para>
    /// <para>
    /// <b>Why the "no key" variant on PostgreSQL.</b> PostgreSQL documents <c>FOR NO KEY UPDATE</c> as the
    /// mode for a locking read that precedes an <c>UPDATE</c> not touching the row's key: it takes a
    /// weaker lock than <c>FOR UPDATE</c> and, unlike it, does not block a concurrent transaction that
    /// needs to take a <c>FOR KEY SHARE</c> lock on this row — which is exactly what a foreign-key check
    /// from another table does (see PostgreSQL's <i>SELECT</i> reference, "The Locking Clause", and
    /// <i>Explicit Locking</i> §13.3.2). Alvo's update path provably never changes a key: the row id is
    /// framework-owned and a caller-supplied <c>id</c> in an update payload is rejected before the
    /// pre-image is read. Since the entity's <c>Ref</c> fields carry real foreign keys
    /// (<c>DescriptorModelBuilder.ConfigureReferences</c>), taking the stronger lock would serialize
    /// unrelated inserts against this row for no benefit.
    /// </para>
    /// <para>
    /// Being a property, this cannot vary per operation, which forecloses <c>FOR SHARE</c>,
    /// <c>SKIP LOCKED</c> and <c>NOWAIT</c>. That is accepted for PR2: the one caller is the
    /// <c>WITH CHECK</c> pre-image read, and every mode above is a widening this member can grow into
    /// (an operation argument, or a small option value) without changing what it means today.
    /// </para>
    /// </remarks>
    string RowLockHint { get; }
}
