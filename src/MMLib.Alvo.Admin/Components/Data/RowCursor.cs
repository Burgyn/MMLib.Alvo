namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// Where <c>j</c> and <c>k</c> put the data grid's selected row (design §5.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>It stops at the ends rather than wrapping.</b> The grid is one page of a longer list, so the row after the
/// last is on the next page, not the first of this one; a selection that jumped back to the top would read as
/// the list having started again.
/// </para>
/// <para>
/// <b>The first press picks an end.</b> With nothing selected, <c>j</c> starts at the first row and <c>k</c>
/// at the last, so either key lands where it points.
/// </para>
/// </remarks>
internal static class RowCursor
{
    /// <summary>The value alvo.js sends with <c>alvo:move</c> for <c>k</c>; anything else moves down.</summary>
    public const string Previous = "previous";

    /// <summary>The selected row after one move, or <see langword="null"/> when there are no rows.</summary>
    /// <param name="selected">The row selected now, or <see langword="null"/> for none.</param>
    /// <param name="direction">What alvo.js sent: <see cref="Previous"/>, or anything else for the next row.</param>
    /// <param name="count">How many rows the page has.</param>
    public static int? Move(int? selected, string? direction, int count)
    {
        if (count <= 0)
        {
            return null;
        }

        var by = string.Equals(direction, Previous, StringComparison.Ordinal) ? -1 : 1;
        var from = selected is { } index && index < count ? index : by > 0 ? -1 : count;
        return Math.Clamp(from + by, 0, count - 1);
    }
}
