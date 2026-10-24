using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>An <see cref="IRelationalWriteGate"/> that admits one writer at a time, in the order they arrived.</summary>
/// <remarks>
/// One instance stands for one database: a provider hands every store over the same database the same instance, whatever
/// container it was built in, so two hosts in one process over one file queue behind each other too.
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
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Turn(_turn);
    }

    /// <summary>Waits at most <paramref name="timeout"/> for the turn.</summary>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits until the turn comes.</param>
    /// <param name="cancellationToken">Ends the wait; a cancellation wins over the timeout and holds nothing.</param>
    /// <returns>The turn, or <see langword="null"/> when the timeout passed first and nothing was taken.</returns>
    internal async ValueTask<IDisposable?> TryEnterAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _turn.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) ? new Turn(_turn) : null;

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
