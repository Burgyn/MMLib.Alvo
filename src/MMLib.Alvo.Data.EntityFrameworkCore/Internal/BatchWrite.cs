namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The order a batch takes its row locks in, and the order it reports refusals in — which are not the same
/// order, and keeping them apart is this type's whole job.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locks are taken in id order, because two concurrent batches would otherwise deadlock.</b> Each row's
/// verdict is reached over that row's <em>locked</em> pre-image, so two batches whose id sets overlap take
/// the same locks in whatever order their callers happened to write them — the textbook deadlock, and on
/// PostgreSQL it is a real one rather than a slowdown. A fixed total order removes it for the cost of one
/// sort, and every ordering works as long as both batches use the same one.
/// </para>
/// <para>
/// <b>The request index travels with the row, because a caller's row 3 must be reported as row 3.</b>
/// Reporting the sorted position would be worse than reporting nothing: it looks like an index, so a caller
/// would repair the row it names — a different row from the one that was refused.
/// </para>
/// </remarks>
internal static class BatchWrite
{
    /// <summary>
    /// <paramref name="rows"/> paired with the position the caller sent them in, ordered by the id each one
    /// addresses.
    /// </summary>
    /// <typeparam name="T">The row shape — a patch, or a bare id.</typeparam>
    /// <param name="rows">The rows the caller supplied, in request order.</param>
    /// <param name="id">The row id each element addresses.</param>
    internal static IReadOnlyList<(int Index, T Row)> InLockOrder<T>(IReadOnlyList<T> rows, Func<T, Guid> id) =>
    [
        .. rows
            .Select((row, index) => (Index: index, Row: row))
            .OrderBy(pair => id(pair.Row))
    ];

    /// <summary>
    /// Every row after the first that names an id an earlier row already named, as a refusal each.
    /// </summary>
    /// <remarks>
    /// <b>Checked before anything is judged, because it is a question about the request rather than about a
    /// row.</b> The first occurrence stands and every repeat is named, so a caller removes exactly the rows
    /// the response points at. See <see cref="AlvoAuthorizationException.RowNamedTwice"/> for why this is a
    /// refusal rather than a fold.
    /// </remarks>
    /// <typeparam name="T">The row shape — a patch, or a bare id.</typeparam>
    /// <param name="rows">The rows the caller supplied, in request order.</param>
    /// <param name="id">The row id each element addresses.</param>
    /// <param name="refusal">Builds the refusal for one repeated row.</param>
    internal static List<AlvoRowRefusal> RepeatedRows<T>(
        IReadOnlyList<T> rows, Func<T, Guid> id, Func<int, AlvoRowRefusal> refusal)
    {
        var seen = new HashSet<Guid>();
        var repeats = new List<AlvoRowRefusal>();
        for (var index = 0; index < rows.Count; index++)
        {
            if (!seen.Add(id(rows[index])))
            {
                repeats.Add(refusal(index));
            }
        }

        return repeats;
    }

    /// <summary>The refusals a batch collected, in the order the caller sent the rows they name.</summary>
    /// <remarks>
    /// Sorted on the way out rather than collected in order, because the judging pass runs in lock order.
    /// A caller reading a refusal list out of order would repair the rows in a different order than they
    /// sent them, which is exactly the confusion the index exists to remove.
    /// </remarks>
    /// <param name="refusals">Every refusal the judging pass collected.</param>
    internal static IReadOnlyList<AlvoRowRefusal> InRequestOrder(List<AlvoRowRefusal> refusals)
    {
        refusals.Sort((left, right) => left.Index.CompareTo(right.Index));

        return refusals;
    }
}
