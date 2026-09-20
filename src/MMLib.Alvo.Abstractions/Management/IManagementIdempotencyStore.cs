namespace MMLib.Alvo.Management;

/// <summary>
/// Records a management write under a caller-chosen key, so a retry after a lost response is a replay rather
/// than an answer the caller cannot attribute.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this buys that the expected revision does not.</b> A management write already carries its own
/// expected revision, so a retry loses the optimistic-lock race and is refused — the apply is
/// <em>at-most-once</em> without any key. What the revision cannot give is <em>attribution</em>: when the
/// first response is lost, the retry's <c>412</c> means both "my own write landed" and "somebody else
/// changed the descriptor", and those two need opposite recoveries. The key converts the first of them into
/// a replay carrying the revision the first attempt appended. It is the same gap
/// <c>DataApiEndpoints.IdempotencyKeyHeader</c> records for the Data API's own conditional writes.
/// </para>
/// <para>
/// <b>It stores the revision, never a rendered response</b> — the same decision the data path's idempotency
/// record makes. A replay re-reads the revision through the ordinary read path, so it can never hand back a
/// representation the caller's current access would not produce.
/// </para>
/// <para>
/// <b>The scope is part of the key.</b> Callers pass <c>AlvoIdempotency.IdentityOf(context)</c>, so one
/// caller's key can never reach another's record — and an anonymous caller, who has no identity to scope by,
/// cannot hold a key at all.
/// </para>
/// <para>
/// <b>The record is filed after the write commits, and the window is stated rather than hidden.</b>
/// <c>IRuntimeSchemaWriter.ApplyAndAppendAsync</c> owns its transaction and exposes no seam to enlist in, so
/// a crash between that commit and <see cref="RecordAsync"/> leaves the attempt unrecorded and the retry is
/// refused with the unattributable <c>412</c> again. That narrows the window rather than closing it; closing
/// it needs a widened writer.
/// </para>
/// </remarks>
public interface IManagementIdempotencyStore
{
    /// <summary>The revision a previous identical request appended, or null when this key is new.</summary>
    /// <param name="key">The caller's idempotency key.</param>
    /// <param name="scope">The caller's identity, from <c>AlvoIdempotency.IdentityOf</c>.</param>
    /// <param name="fingerprint">A hash of the request this key is being used for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded revision, or <see langword="null"/> when the key has never been spent here.</returns>
    /// <exception cref="Data.AlvoIdempotencyConflictException">
    /// The key exists for this scope under a <em>different</em> fingerprint: it is not a replay, and answering
    /// with the first request's revision would report success for an apply that never happened.
    /// </exception>
    Task<int?> FindAsync(string key, string scope, string fingerprint, CancellationToken ct = default);

    /// <summary>Records that this request appended this revision.</summary>
    /// <remarks>
    /// <b>Recording one key twice throws</b> rather than overwriting: the record's primary key is the
    /// concurrency control, so two requests that both found nothing cannot both file a revision under one
    /// key.
    /// </remarks>
    /// <param name="key">The caller's idempotency key.</param>
    /// <param name="scope">The caller's identity.</param>
    /// <param name="fingerprint">A hash of the request.</param>
    /// <param name="revision">The revision that was appended.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the record is filed.</returns>
    Task RecordAsync(string key, string scope, string fingerprint, int revision, CancellationToken ct = default);
}
