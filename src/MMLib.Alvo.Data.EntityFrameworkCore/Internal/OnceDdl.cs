namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Runs a store's <c>CREATE … IF NOT EXISTS</c> once per process, serialising concurrent first callers (Ruling 47): on
/// PostgreSQL two concurrent <c>IF NOT EXISTS</c> creates of one cold table can still collide in <c>pg_type</c>. A failed
/// attempt is forgotten, so the next call tries again. The boot's <c>SystemSchemaInitializer</c> normally created the
/// tables already; this covers a composition that never booted.
/// </summary>
/// <remarks>
/// Once the run succeeded, a call costs one volatile read. A failed run's task is marked observed where it fails, so a
/// failure no other caller waited on is not reported again when the task is collected.
/// </remarks>
/// <param name="ensure">The store's DDL, run on a connection of its own.</param>
internal sealed class OnceDdl(Func<CancellationToken, Task> ensure)
{
    private TaskCompletionSource? _done;

    internal async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _done) is { Task.IsCompletedSuccessfully: true })
        {
            return;
        }

        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _done, mine, null) is { } winner)
        {
            await winner.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await ensure(CancellationToken.None).ConfigureAwait(false);
            mine.SetResult();
        }
        catch (Exception failure)
        {
            Interlocked.Exchange(ref _done, null);
            mine.SetException(failure);
            _ = mine.Task.Exception;
            throw;
        }
    }
}
