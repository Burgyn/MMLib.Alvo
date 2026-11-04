namespace MMLib.Alvo.Hooks;

/// <summary>
/// A before-delete hook over typed records: runs inside a delete's transaction before the row is removed; may refuse,
/// write other rows through the delete's transaction and publish; no network.
/// </summary>
/// <typeparam name="T">The record type it is registered for.</typeparam>
public interface IBeforeDeleteHook<T>
    where T : class
{
    /// <summary>Runs before the row is removed; record a refusal on <paramref name="deletion"/> to stop it.</summary>
    /// <param name="deletion">The delete.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask BeforeDeleteAsync(BeforeDelete<T> deletion, CancellationToken cancellationToken);
}
