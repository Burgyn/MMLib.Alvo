namespace MMLib.Alvo.Data;

/// <summary>What one batch produced: the rows it wrote, or every reason it wrote none.</summary>
/// <remarks>
/// <para>
/// <b>A batch is one transaction, so it wrote every row or none</b> — there is no partial outcome for this
/// type to express, and a result carrying both rows and refusals would describe one that cannot happen.
/// <see cref="Succeeded"/> is the discriminator.
/// </para>
/// <para>
/// <b><see cref="Affected"/> exists because a delete produces no rows, and this type failed open without
/// it.</b> With only <see cref="Rows"/>, a successful <see cref="IAlvoData.DeleteManyAsync"/> and a refused
/// one are <em>both</em> an empty list — so a caller who checked the rows could not tell a five-row delete
/// from a refusal, in a port where every other refusal throws. A count cannot be confused with an absence.
/// </para>
/// <para>
/// <b><see cref="Refusals"/> is how a refusal names a row.</b> A batch of five hundred that reports only
/// "refused" is one nobody can fix, and a caller cannot bisect it by retrying halves without writing the
/// halves that pass. Every refused row is named, never only the first.
/// </para>
/// </remarks>
/// <param name="Affected">How many rows the batch wrote or removed; zero when it was refused.</param>
/// <param name="Rows">The rows the batch wrote, in request order; empty for a delete and for a refusal.</param>
/// <param name="Refusals">Every reason the batch wrote nothing; empty when it wrote.</param>
public sealed record AlvoBatchResult(
    int Affected, IReadOnlyList<AlvoRecord> Rows, IReadOnlyList<AlvoRowRefusal> Refusals)
{
    /// <summary>Whether the batch wrote. A refused batch wrote nothing at all.</summary>
    public bool Succeeded => Refusals.Count == 0;

    /// <summary>The result of a batch that wrote.</summary>
    /// <param name="rows">The rows it wrote, in request order; empty for a delete.</param>
    /// <param name="affected">How many rows it wrote or removed.</param>
    public static AlvoBatchResult Wrote(IReadOnlyList<AlvoRecord> rows, int affected) => new(affected, rows, []);

    /// <summary>The result of a batch that wrote nothing, and every reason.</summary>
    /// <param name="refusals">Every refused row; never empty, or this would read as a successful write of nothing.</param>
    /// <exception cref="ArgumentException"><paramref name="refusals"/> is empty.</exception>
    public static AlvoBatchResult Refused(IReadOnlyList<AlvoRowRefusal> refusals)
    {
        ArgumentNullException.ThrowIfNull(refusals);

        return refusals.Count > 0
            ? new AlvoBatchResult(0, [], refusals)
            : throw new ArgumentException(
                "A refused batch must name at least one refused row: a result carrying neither rows nor "
                + "refusals reads as a successful write of nothing.",
                nameof(refusals));
    }
}
