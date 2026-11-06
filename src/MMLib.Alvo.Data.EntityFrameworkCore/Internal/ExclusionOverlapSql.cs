using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The pairs of rows an exclusion forbids — equal equality columns, both inside its filter, both non-empty ranges, and the
/// two <c>[start, end)</c> ranges overlapping — as the <c>FROM … JOIN …</c> body an engine without exclusion constraints
/// selects from: SQLite's trigger with the written row, and the apply's scan of the rows a table already holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each side filters inside its own single-table subquery</b>, so the unqualified columns of the renderer's filter
/// resolve against exactly one table, and the join's own conditions qualify every column by its side.
/// </para>
/// <para>
/// <b>An empty or inverted range overlaps nothing</b> (PostgreSQL's <c>'[)'</c>): each side must hold <c>start &lt; end</c>.
/// A <c>datetime</c> bound is compared through the engine's epoch rendering (the <c>epoch</c> argument of
/// <see cref="Pairs"/>), a <c>date</c> bound as stored.
/// </para>
/// </remarks>
internal static class ExclusionOverlapSql
{
    /// <summary>
    /// The text an engine without exclusion constraints aborts a write with, followed by the exclusion's name — the tag the
    /// write path decodes a refusal by.
    /// </summary>
    internal const string RaisePrefix = "alvo_exclusion:";

    private const string Written = "n";
    private const string Other = "o";

    /// <summary>The <c>FROM … JOIN …</c> body of the forbidden pairs.</summary>
    /// <param name="definition">The exclusion.</param>
    /// <param name="epoch">Wraps a qualified <c>datetime</c> column into this engine's comparable epoch expression.</param>
    /// <param name="newRowId">
    /// The written row's id expression (a trigger's <c>NEW."id"</c>), or <see langword="null"/> for the scan of every pair,
    /// each counted once.
    /// </param>
    internal static string Pairs(ExclusionConstraintDefinition definition, Func<string, string> epoch, string? newRowId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(epoch);

        var on = definition.EqualityColumns.Select(column => $"{Column(Other, column)} = {Column(Written, column)}")
            .Append(newRowId is null ? $"{Column(Other, "id")} < {Column(Written, "id")}" : $"{Column(Other, "id")} <> {Column(Written, "id")}")
            .Append(Before(definition, epoch, Other, Other))
            .Append(Before(definition, epoch, Written, Written))
            .Append(Before(definition, epoch, Other, Written))
            .Append(Before(definition, epoch, Written, Other));
        var row = newRowId is null ? string.Empty : $" WHERE {Column(Written, "id")} = {newRowId}";
        return $"{Side(definition, Written)} JOIN {Side(definition, Other)} ON {string.Join(" AND ", on)}{row}";
    }

    private static string Before(ExclusionConstraintDefinition definition, Func<string, string> epoch, string starting, string ending) =>
        $"{Bound(definition, epoch, starting, definition.StartColumn)} < {Bound(definition, epoch, ending, definition.EndColumn)}";

    private static string Side(ExclusionConstraintDefinition definition, string alias) =>
        $"(SELECT * FROM {AlvoSqlIdentifier.Quote(definition.Table)}{(definition.Filter is null ? string.Empty : $" WHERE {definition.Filter}")}) AS {AlvoSqlIdentifier.Quote(alias)}";

    private static string Column(string alias, string column) => $"{AlvoSqlIdentifier.Quote(alias)}.{AlvoSqlIdentifier.Quote(column)}";

    private static string Bound(ExclusionConstraintDefinition definition, Func<string, string> epoch, string alias, string column) =>
        definition.RangeType == FieldType.Date ? Column(alias, column) : epoch(Column(alias, column));
}
