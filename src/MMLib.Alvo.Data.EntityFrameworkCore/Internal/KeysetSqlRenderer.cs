using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>The row a page continues after: the sort keys in effect, that row's values for them, and its primary key.</summary>
/// <param name="Sort">The sort keys the page is ordered by, outermost first.</param>
/// <param name="Values">That row's value for each key in <paramref name="Sort"/>, in the same order.</param>
/// <param name="RowId">The anchor row's primary key — the tie-breaker that makes the order total.</param>
internal sealed record KeysetAnchor(IReadOnlyList<AlvoSort> Sort, IReadOnlyList<object?> Values, Guid RowId);

/// <summary>
/// Renders a keyset-pagination predicate as the nested-OR expansion of a row-value tuple comparison:
/// <c>(k &gt; @k0 OR (k = @k0 AND …))</c>, ending in the primary key so the order is total and a page can
/// neither skip nor repeat a row.
/// </summary>
/// <remarks>
/// <para>
/// <b>A nullable key is compared as the pair <c>(rank, value)</c>, because that is what
/// <see cref="SortSqlRenderer"/> orders by.</b> That renderer emits a nullable key as two terms — an always
/// ascending <c>CASE WHEN col IS NULL THEN …</c> rank, then the value with the caller's direction — so a
/// boundary comparing the value alone describes a different sequence than the <c>ORDER BY</c> does, which is
/// how a page comes to skip or repeat a row rather than merely mis-sort one. Expanding
/// <c>(rank, value) &gt; (rank₀, value₀)</c> and folding away the constant arms leaves four shapes, two of
/// which add nothing to the non-nullable form; <see cref="PastAValuedKey"/> and <see cref="PastANullKey"/>
/// carry them and each records its own derivation. <b>Whether a key gets that treatment is read from
/// <c>FieldSchema.Nullable</c> — the same condition <see cref="SortSqlRenderer"/> emits its rank term on</b>,
/// so the two cannot disagree about which keys have a rank at all. That they agree about *where* nulls go is
/// not provable from either file and is pinned behaviourally instead, by the inherited paging fact that walks
/// a nullable-keyed set one row per page and compares the concatenation with the unpaged sorted read.
/// </para>
/// <para>
/// The nested-OR form rather than SQL's <c>(a, b) &gt; (x, y)</c> row constructor. The blocker is T-SQL /
/// Azure SQL, which has no row-value constructor in a comparison predicate at all (only in <c>VALUES</c>) —
/// the one dialect this form has to run on unmodified, and the divergence §0 principle 3 asks an engine seam
/// to carry rather than the core. PostgreSQL and SQLite both support the row constructor and both turn it into
/// an index range scan, where this nested-OR form costs an index scan plus a filter whose cost grows with
/// cursor depth on a multi-term sort — see issue #100 for the measurement and the <c>IAlvoSqlDialect</c> seam
/// that would let a per-engine renderer opt in. The tie-breaking <c>id</c> comparison is always ascending: it
/// exists to make the order deterministic, not to be sorted by, and flipping it with the last user key would
/// make two pages of the same query disagree about where the boundary is.
/// </para>
/// <para>
/// Every comparison here renders both operands through
/// <see cref="IFieldSqlRenderer.RenderComparableOperands"/> at the key column's own type, exactly as the
/// caller filter and the CEL predicate renderer do. A cursor is <em>only</em> comparisons, so a dialect whose
/// storage does not order the way the type does (a <c>decimal</c> in a SQLite <c>TEXT</c> column) would not
/// merely mis-order a page — it would skip or repeat rows across page boundaries.
/// </para>
/// </remarks>
internal static class KeysetSqlRenderer
{
    /// <summary>Renders the cursor predicate for one anchor.</summary>
    /// <param name="anchor">The row the page continues after.</param>
    /// <param name="entity">The entity being paged, as the applied schema declares it.</param>
    /// <param name="fields">The driver's field/expression renderer.</param>
    /// <param name="parameterPrefix">The reserved prefix this fragment's parameters are named from.</param>
    internal static RenderedSql Render(
        KeysetAnchor anchor, EntitySchema entity, IFieldSqlRenderer fields, string parameterPrefix)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);
        EnsureAligned(anchor);

        var parameters = new Dictionary<string, BoundValue>(StringComparer.Ordinal);
        var sql = Level(0, anchor, entity, fields, parameterPrefix, parameters);
        return new RenderedSql(sql, parameters);
    }

    /// <summary>
    /// A cursor whose value list does not line up with its sort keys would compare one key against another
    /// key's value — a page that silently returns the wrong rows. It is the caller's own bug (the anchor is
    /// built inside the data path, never parsed from the wire), so it raises an
    /// <see cref="ArgumentException"/> rather than an authorization refusal.
    /// </summary>
    private static void EnsureAligned(KeysetAnchor anchor)
    {
        if (anchor.Sort.Count != anchor.Values.Count)
        {
            throw new ArgumentException(
                $"The cursor carries {anchor.Values.Count} value(s) for {anchor.Sort.Count} sort key(s); "
                + "a keyset anchor must hold exactly one value per key, in the same order.",
                nameof(anchor));
        }
    }

    private static string Level(
        int index, KeysetAnchor anchor, EntitySchema entity, IFieldSqlRenderer fields,
        string prefix, Dictionary<string, BoundValue> parameters)
    {
        if (index == anchor.Sort.Count)
        {
            return TieBreaker(anchor, entity, fields, prefix, parameters);
        }

        var key = anchor.Sort[index];
        var declared = QueryFieldGuard.DeclaredField(entity, key.Field);
        var rawColumn = fields.RenderField(entity, declared.Name);

        return declared.Nullable && anchor.Values[index] is null
            ? PastANullKey(rawColumn, key, Level(index + 1, anchor, entity, fields, prefix, parameters))
            : PastAValuedKey(index, anchor, entity, fields, prefix, parameters, key, declared, rawColumn);
    }

    /// <summary>
    /// The boundary for one key whose anchor row <em>has</em> a value: today's nested-OR expansion, plus —
    /// where the key is nullable and its nulls sort <b>last</b> — the arm that lets the null-keyed tail
    /// through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Under <c>nullsfirst</c> nothing is added, and that is derived rather than overlooked.</b> The nulls
    /// sort <em>before</em> this anchor, so they are already excluded — <c>col &gt; @v</c> is
    /// <see langword="null"/> for them and a <c>WHERE</c> treats that as false. Under <c>nullslast</c> they
    /// sort after every value, so every null-keyed row is past this boundary whatever the direction, which is
    /// why the added arm carries no comparison and is not flipped by <see cref="AlvoSort.Descending"/>: the
    /// rank term <see cref="SortSqlRenderer"/> orders by is always ascending, and only the value term is
    /// reversed.
    /// </para>
    /// <para>
    /// The <c>IS NULL</c> test reads the <b>raw</b> column, not the repaired one, exactly as
    /// <see cref="SortSqlRenderer"/>'s rank does: a cast <see langword="null"/> is still
    /// <see langword="null"/>, and the raw column is the form an index can serve. Every comparison still runs
    /// on the repaired pair.
    /// </para>
    /// </remarks>
    private static string PastAValuedKey(
        int index, KeysetAnchor anchor, EntitySchema entity, IFieldSqlRenderer fields, string prefix,
        Dictionary<string, BoundValue> parameters, AlvoSort key, FieldSchema declared, string rawColumn)
    {
        var (column, parameter) = fields.RenderComparableOperands(
            rawColumn,
            Bind(anchor.Values[index], declared.Name, fields, prefix, parameters),
            CelFieldType.Of(declared));
        var strict = key.Descending ? "<" : ">";
        var tail = Level(index + 1, anchor, entity, fields, prefix, parameters);
        var compared = $"{column} {strict} {parameter} OR ({column} = {parameter} AND {tail})";

        return declared.Nullable && key.Nulls == AlvoNullPlacement.Last
            ? $"({rawColumn} IS NULL OR {compared})"
            : $"({compared})";
    }

    /// <summary>
    /// The boundary for one key whose anchor row's value is <see langword="null"/> — reachable only for a
    /// nullable key, and the case F3's renderer could not express at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The anchor sits in the null bucket, so every row still in that bucket ties with it on this key and the
    /// answer is whatever the <em>tail</em> says — never <c>col = @v</c>, which against a
    /// <see langword="null"/> anchor is the three-valued trap that made a page stop silently. The non-null
    /// rows are then wholly before the anchor (<c>nullsfirst</c>: excluded) or wholly after it
    /// (<c>nullslast</c>: admitted unconditionally), because a bucket is compared before a value is.
    /// </para>
    /// <para>
    /// No parameter is bound here: there is no value to compare against, and binding an unreferenced one
    /// would put a name in the statement's bag that its text never mentions.
    /// </para>
    /// </remarks>
    private static string PastANullKey(string rawColumn, AlvoSort key, string tail) =>
        key.Nulls == AlvoNullPlacement.First
            ? $"({rawColumn} IS NOT NULL OR {tail})"
            : $"({rawColumn} IS NULL AND {tail})";

    private static string TieBreaker(
        KeysetAnchor anchor, EntitySchema entity, IFieldSqlRenderer fields,
        string prefix, Dictionary<string, BoundValue> parameters)
    {
        var declared = QueryFieldGuard.DeclaredField(entity, AlvoDataContext.IdColumn);
        var (column, parameter) = fields.RenderComparableOperands(
            fields.RenderField(entity, declared.Name),
            Bind(anchor.RowId, declared.Name, fields, prefix, parameters),
            CelFieldType.Of(declared));

        return $"{column} > {parameter}";
    }

    /// <summary>
    /// Records one anchor value against the column it is compared with. The anchor's values came back from a
    /// previous read already shaped by EF's mapping, but the column is still required: a cursor is only
    /// comparisons, and a boundary bound by the value's own CLR type would compare a repaired column against
    /// an unrepaired parameter.
    /// </summary>
    private static string Bind(
        object? value, string column, IFieldSqlRenderer fields, string prefix, Dictionary<string, BoundValue> parameters)
    {
        var name = prefix + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters[name] = BoundValue.ForColumn(column, value);
        return fields.RenderParameter(name);
    }
}
