namespace MMLib.Alvo.Hooks;

/// <summary>
/// A before-write hook over typed records: runs inside a create, update or replace's transaction before the row is written;
/// may refuse, set fields, write other rows through the write's transaction and publish; no network.
/// </summary>
/// <typeparam name="T">The record type it is registered for.</typeparam>
public interface IBeforeWriteHook<T>
    where T : class
{
    /// <summary>Runs before the row is written; refuse, set fields, or write other rows through <paramref name="write"/>.</summary>
    /// <param name="write">The write.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask BeforeWriteAsync(BeforeWrite<T> write, CancellationToken cancellationToken);
}
