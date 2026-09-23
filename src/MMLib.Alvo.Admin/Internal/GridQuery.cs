using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>A header sort the operator chose: one field, one direction.</summary>
/// <param name="Field">The field sorted by.</param>
/// <param name="Descending">Whether the order is descending.</param>
internal sealed record GridSort(string Field, bool Descending);

/// <summary>
/// The Data grid's quick search and header sort, as the <see cref="AlvoQuery"/> they become.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through <see cref="AlvoQuery"/>, and nothing else.</b> A search is an <c>ilike</c>
/// <c>%term%</c> over the entity's string fields joined by <c>or</c>, and a sort is a
/// <see cref="AlvoSort"/> — the same port the Data API serves, so the grid can narrow and order rows
/// only in ways <c>/api</c> can, over the same policy predicate. It is a quick filter, not the
/// PostgREST query builder (design pass §4.1): the full syntax stays in the API.
/// </para>
/// <para>
/// <b>A field that may be masked is never a search or sort term.</b> The port refuses a query whose
/// filter or sort names a field hidden from the caller — the whole query, deliberately, so a refusal is
/// not an oracle — and the dashboard cannot evaluate a CEL mask to know in advance. Leaving such a field
/// out costs a search one column; putting it in would turn the search box into a refusal for exactly
/// the callers the mask exists for.
/// </para>
/// <para>
/// <b>Keyset paging survives a sort.</b> The EF driver re-reads the cursor's anchor row and compares the
/// sort keys, with the row id as the tie-breaker, so <see cref="AlvoQuery.After"/> needs nothing from the
/// grid but the same sort on every page — there is no fallback to offsets.
/// </para>
/// </remarks>
internal static class GridQuery
{
    /// <summary>The string fields a quick search looks in.</summary>
    public static IReadOnlyList<string> Searchable(EntitySchema entity, FieldMasks masks)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(masks);

        var managed = AlvoManagedColumns.For(entity);
        return [.. entity.Fields
            .Where(field => field.Type == FieldType.String)
            .Where(field => !managed.Contains(field.Name) && !masks.MayBeHidden(field.Name))
            .Select(field => field.Name)
            .Take(AlvoFilter.MaxTerms - 1)];
    }

    /// <summary>The filter a search term becomes; <see langword="null"/> when there is nothing to search for.</summary>
    public static AlvoFilter? Search(IReadOnlyList<string> fields, string? term)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var trimmed = term?.Trim();
        if (string.IsNullOrEmpty(trimmed) || fields.Count == 0)
        {
            return null;
        }

        var pattern = $"%{trimmed}%";
        return new AlvoOr([.. fields.Select(field => new AlvoComparison(field, AlvoFilterOperator.ILike, pattern))]);
    }

    /// <summary>The order a page is read in: the chosen sort, else newest first on an audited entity.</summary>
    public static IReadOnlyList<AlvoSort> Sort(EntitySchema entity, GridSort? chosen)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (chosen is not null)
        {
            return [new AlvoSort(chosen.Field, chosen.Descending)];
        }

        return entity.Audit ? [new AlvoSort(AlvoManagedColumns.CreatedAt, Descending: true)] : [];
    }

    /// <summary>What one click on <paramref name="field"/>'s header makes of the current sort: asc, desc, none.</summary>
    public static GridSort? Next(GridSort? current, string field)
    {
        if (current is null || !string.Equals(current.Field, field, StringComparison.Ordinal))
        {
            return new GridSort(field, Descending: false);
        }

        return current.Descending ? null : current with { Descending = true };
    }

    /// <summary>Whether a header offers a sort.</summary>
    /// <remarks>
    /// A reference is not sortable: its order is the order of uuids, which is no order a reader can use,
    /// and the label the cell shows lives in another table.
    /// </remarks>
    public static bool Sortable(FieldSchema field, FieldMasks masks)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(masks);

        return field.Type is not (FieldType.Ref or FieldType.Json) && !masks.MayBeHidden(field.Name);
    }

    /// <summary>One page of the grid.</summary>
    public static AlvoQuery Page(
        EntitySchema entity, AlvoFilter? filter, GridSort? sort, int limit, string? after)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new AlvoQuery
        {
            Entity = entity.Name,
            Filter = filter,
            Sort = Sort(entity, sort),
            Limit = limit,
            After = after,
            IncludeTotalCount = true,
        };
    }
}
