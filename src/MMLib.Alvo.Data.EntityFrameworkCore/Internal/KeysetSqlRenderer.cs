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
/// The nested-OR form rather than SQL's <c>(a, b) &gt; (x, y)</c> row constructor, which has no portable
/// LINQ or SQLite equivalent. The tie-breaking <c>id</c> comparison is always ascending: it exists to
/// make the order deterministic, not to be sorted by, and flipping it with the last user key would make
/// two pages of the same query disagree about where the boundary is.
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

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
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
        string prefix, Dictionary<string, object?> parameters)
    {
        if (index == anchor.Sort.Count)
        {
            return TieBreaker(anchor, entity, fields, prefix, parameters);
        }

        var key = anchor.Sort[index];
        var declared = QueryFieldGuard.DeclaredField(entity, key.Field);
        var (column, parameter) = fields.RenderComparableOperands(
            fields.RenderField(entity, declared.Name),
            Bind(anchor.Values[index], fields, prefix, parameters),
            CelFieldType.Of(declared));
        var strict = key.Descending ? "<" : ">";
        var tail = Level(index + 1, anchor, entity, fields, prefix, parameters);

        return $"({column} {strict} {parameter} OR ({column} = {parameter} AND {tail}))";
    }

    private static string TieBreaker(
        KeysetAnchor anchor, EntitySchema entity, IFieldSqlRenderer fields,
        string prefix, Dictionary<string, object?> parameters)
    {
        var declared = QueryFieldGuard.DeclaredField(entity, AlvoDataContext.IdColumn);
        var (column, parameter) = fields.RenderComparableOperands(
            fields.RenderField(entity, declared.Name),
            Bind(anchor.RowId, fields, prefix, parameters),
            CelFieldType.Of(declared));

        return $"{column} > {parameter}";
    }

    private static string Bind(object? value, IFieldSqlRenderer fields, string prefix, Dictionary<string, object?> parameters)
    {
        var name = prefix + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters[name] = value;
        return fields.RenderParameter(name);
    }
}
