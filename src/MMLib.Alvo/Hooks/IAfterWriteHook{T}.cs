namespace MMLib.Alvo.Hooks;

/// <summary>
/// An after-write hook over typed records: runs inside the transaction after the row and its rollups; may refuse — rolling
/// the write back — write other rows and publish; never its own row.
/// </summary>
/// <typeparam name="T">The record type it is registered for.</typeparam>
public interface IAfterWriteHook<T>
    where T : class
{
    /// <summary>Runs after the row is written; refusing on <paramref name="write"/> rolls the write back.</summary>
    /// <param name="write">The write.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask AfterWriteAsync(AfterWrite<T> write, CancellationToken cancellationToken);
}
