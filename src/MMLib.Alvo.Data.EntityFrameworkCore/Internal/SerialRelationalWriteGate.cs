using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>An <see cref="IRelationalWriteGate"/> that admits one writer at a time.</summary>
/// <remarks>
/// <para>
/// One instance stands for one database: a provider hands every store over the same database the same instance, whatever
/// container it was built in, so two hosts in one process over one file queue behind each other too.
/// </para>
/// <para>
/// <b>Not first come, first served.</b> <see cref="SemaphoreSlim"/> promises no order among its waiters, so a writer may
/// overtake one that waited longer. Nothing depends on the order: a record's event ids are minted inside its write's
/// transaction, under the turn, so the turn's order is the ids' order whoever got it first.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A gate lives as long as the process; SemaphoreSlim holds no handle unless AvailableWaitHandle is read, which this type never does.")]
internal sealed class SerialRelationalWriteGate : IRelationalWriteGate
{
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <inheritdoc/>
    /// <remarks>
    /// A wait that is cancelled throws before it takes the turn, so it leaves nothing to release; the turn a caller did take
    /// is released by the first <see cref="IDisposable.Dispose"/> and never again.
    /// </remarks>
    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        StoreWrites.RefuseInsideUnit();
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Turn(_turn);
    }

    /// <summary>Waits at most <paramref name="timeout"/> for the turn.</summary>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits until the turn comes.</param>
    /// <param name="cancellationToken">Ends the wait; a cancellation wins over the timeout and holds nothing.</param>
    /// <returns>The turn, or <see langword="null"/> when the timeout passed first and nothing was taken.</returns>
    internal async ValueTask<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        StoreWrites.RefuseInsideUnit();
        return await _turn.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) ? new Turn(_turn) : null;
    }

    private sealed class Turn(SemaphoreSlim turn) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                turn.Release();
            }
        }
    }
}
