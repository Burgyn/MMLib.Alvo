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
    /// The total number of rows matching the query, or <see langword="null"/> when the caller did
    /// not ask for one — which is always, in F3. Modelled now because §2.1 requires count to be an
    /// opt-in (<c>Prefer: count=exact</c>) and a page shape without it could not gain one additively.
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>An empty page: no rows, no next cursor.</summary>
    public static AlvoPage Empty { get; } = new() { Items = [] };
}
