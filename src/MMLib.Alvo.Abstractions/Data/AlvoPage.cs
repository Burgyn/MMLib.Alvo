namespace MMLib.Alvo.Data;

/// <summary>
/// One page of an <see cref="IAlvoData.QueryAsync"/> result: the rows themselves, plus enough for the caller
/// to keep paging without knowing how this implementation pages.
/// </summary>
/// <remarks>
/// A record rather than the bare <see cref="IReadOnlyList{T}"/> of <see cref="AlvoRecord"/> PR2 returned,
/// because a page is more than its rows the moment paging has to be <em>honest</em> — a caller who received
/// exactly <see cref="AlvoQuery.Limit"/> rows cannot tell, from the rows alone, whether that was the whole
/// visible set or a page with more to come. <see cref="NextCursor"/> answers that without another round trip.
/// </remarks>
public sealed record AlvoPage
{
    /// <summary>The rows in this page, in the query's sort order.</summary>
    public required IReadOnlyList<AlvoRecord> Items { get; init; }

    /// <summary>
    /// The opaque cursor that reads the page after this one, or <see langword="null"/> when this
    /// page is the last. Only the implementation that issued it may interpret it.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>
    /// The total number of rows the query matches, or <see langword="null"/> when the caller did not ask for
    /// one — see <see cref="AlvoQuery.IncludeTotalCount"/>, which is <see langword="false"/> by default
    /// because an exact count is a second full scan of the filtered set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It counts the <b>policy-filtered</b> set narrowed by the caller's own filter, and <em>not</em> the
    /// page: <see cref="AlvoQuery.Limit"/>, <see cref="AlvoQuery.Offset"/> and <see cref="AlvoQuery.After"/>
    /// do not narrow it. So on a five-row page of a two-hundred-row result this is 200, and a caller can size
    /// a scrollbar without walking every cursor.
    /// </para>
    /// <para>
    /// <b>Exact means "not an estimate", not "atomically consistent with <see cref="Items"/>".</b> The count
    /// cannot be a window function over the page's own statement — that statement carries the keyset
    /// boundary, so the window would count only the rows after the cursor — so it is a second statement, and
    /// a write interleaving the two can make the number disagree with the rows by one. This was modelled in
    /// F3 and always answered <see langword="null"/> then, which is why the shape could gain a count
    /// additively rather than breaking.
    /// </para>
    /// </remarks>
    public long? TotalCount { get; init; }

    /// <summary>An empty page: no rows, no next cursor.</summary>
    public static AlvoPage Empty { get; } = new() { Items = [] };
}
