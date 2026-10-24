namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Where a store's write transaction queues behind the other writes this process makes to the same database, before it
/// asks the engine for its write lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>An engine with one writer at a time is the reason it exists.</b> SQLite admits one write transaction per database
/// file. Concurrent writers that reach the engine are refused with <c>SQLITE_BUSY</c>, and <c>Microsoft.Data.Sqlite</c>
/// answers that refusal by sleeping and retrying. Under the delivery dispatcher's concurrency those sleeps, not the work,
/// were the cost of a delivery. Writers that queue here wait exactly as long as the writer ahead of them takes.
/// </para>
/// <para>
/// <b>The engine's busy retry stays the backstop.</b> The gate orders the writes of one process only; another process on
/// the same file, or a write made by a path the gate does not cover, still meets the engine's lock and waits through the
/// provider's retry. A provider whose engine runs writers concurrently registers <see cref="NoRelationalWriteGate"/>,
/// which is the driver's default.
/// </para>
/// <para>
/// <b>A wait is bounded the way the engine's own busy wait is.</b> A provider bounds its gate by the busy timeout its
/// connections already carry and, when it passes, fails the writer with the engine's own busy error — the failure a
/// writer met before the gate existed, which the replay loop and the API already answer. A slow or wedged writer ahead
/// of the line is then an error after that timeout, never an unbounded queue.
/// </para>
/// </remarks>
internal interface IRelationalWriteGate
{
    /// <summary>Waits for this caller's turn to write; disposing the result hands the turn to the next writer.</summary>
    /// <param name="cancellationToken">Ends the wait; a cancelled wait holds nothing and needs no disposal.</param>
    /// <returns>The turn, released exactly once however many times it is disposed.</returns>
    ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken);
}
