namespace MMLib.Alvo.Hooks;

/// <summary>
/// An after-delete hook over typed records: runs inside the transaction after the row is removed and its rollups
/// recomputed; may refuse — keeping the row — write other rows and publish.
/// </summary>
/// <typeparam name="T">The record type it is registered for.</typeparam>
public interface IAfterDeleteHook<T>
    where T : class
{
    /// <summary>Runs after the row is removed; refusing on <paramref name="deletion"/> rolls the delete back.</summary>
    /// <param name="deletion">The delete.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask AfterDeleteAsync(AfterDelete<T> deletion, CancellationToken cancellationToken);
}
