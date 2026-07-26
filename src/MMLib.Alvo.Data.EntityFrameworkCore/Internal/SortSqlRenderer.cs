using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Renders the <c>ORDER BY</c> term list of a policy-filtered read: each caller sort key with an explicit
/// null placement and direction, then the row key ascending so the order is total.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is rendered SQL rather than a LINQ ordering chain.</b> A keyset page is only correct while its
/// <em>order</em> and its <em>boundary</em> describe the same sequence, and the boundary is rendered — by
/// <see cref="KeysetSqlRenderer"/>, through <see cref="IFieldSqlRenderer.RenderComparableOperands"/> at the
/// key column's own type. Composing the order in LINQ instead makes EF's translation the second authority
/// for it, and the two provably disagree: EF's SQLite provider orders a <c>decimal</c> column with its own
/// exact <c>EF_DECIMAL</c> collation, while the boundary compares the driver's repaired value, so two
/// decimals the collation separates and the repair rounds together are ordered by one and tied by the other.
/// A page then skips or repeats a row rather than merely mis-sorting it. Rendering both from the same seam
/// makes that disagreement unrepresentable.
/// </para>
/// <para>
/// Null placement is the portable <c>CASE WHEN &lt;key&gt; IS NULL THEN … END</c> emulation spike <c>Q3c</c>
/// proved translates identically on both engines; native <c>NULLS FIRST</c>/<c>NULLS LAST</c> is not adopted
/// (SQLite's support is recent and the emulation is one shape for both). The <c>IS NULL</c> test reads the
/// raw column rather than the repaired one — a cast <c>NULL</c> is still <c>NULL</c>, and the raw column is
/// the form an index can serve. The emulation is known to defeat an index on the sort key; that is the
/// accepted cost recorded against the latency criterion, which #19 owns.
/// </para>
/// <para>
/// The row-key tie-breaker is always ascending and always present: it exists to make the order total, not to
/// be sorted by, and flipping it with the last caller key would make two pages of one query disagree about
/// where the boundary is.
/// </para>
/// </remarks>
internal static class SortSqlRenderer
{
    /// <summary>Renders the term list that follows <c>ORDER BY</c>, without the keyword itself.</summary>
    /// <param name="sort">The caller's sort keys, outermost first; may be empty.</param>
    /// <param name="entity">The entity being read, as the applied schema declares it.</param>
    /// <param name="fields">The driver's field/expression renderer.</param>
    /// <exception cref="AlvoAuthorizationException">A key names a field the entity does not declare.</exception>
    internal static string Render(IReadOnlyList<AlvoSort> sort, EntitySchema entity, IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(fields);

        return string.Join(", ", sort.Select(key => Key(key, entity, fields)).Append(TieBreaker(entity, fields)));
    }

    private static string Key(AlvoSort key, EntitySchema entity, IFieldSqlRenderer fields)
    {
        var declared = QueryFieldGuard.DeclaredField(entity, key.Field);
        var column = fields.RenderField(entity, declared.Name);
        var direction = key.Descending ? " DESC" : string.Empty;

        return $"{NullPlacement(column, key.Nulls)}, {Comparable(column, declared, fields)}{direction}";
    }

    private static string TieBreaker(EntitySchema entity, IFieldSqlRenderer fields)
    {
        var declared = QueryFieldGuard.DeclaredField(entity, AlvoDataContext.IdColumn);
        return Comparable(fields.RenderField(entity, declared.Name), declared, fields);
    }

    /// <summary>
    /// The ordering operand, repaired exactly as a comparison's operands are. The pair-returning port is
    /// asked with the same operand on both sides and either side taken: the repair is symmetric by contract
    /// (<see cref="IFieldSqlRenderer.RenderComparableOperands"/> exists precisely because repairing one side
    /// alone inverts a comparison), so this is the same seam rather than a second reading of it.
    /// </summary>
    private static string Comparable(string column, FieldSchema declared, IFieldSqlRenderer fields) =>
        fields.RenderComparableOperands(column, column, CelFieldType.Of(declared)).Left;

    private static string NullPlacement(string column, AlvoNullPlacement placement)
    {
        var (whenNull, otherwise) = placement == AlvoNullPlacement.First ? (0, 1) : (1, 0);
        return $"CASE WHEN {column} IS NULL THEN {whenNull} ELSE {otherwise} END";
    }
}
