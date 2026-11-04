using MMLib.Alvo.Data;

namespace MMLib.Alvo.Hooks;

/// <summary>
/// A before-write hook over untyped records: runs inside a create, update or replace's transaction before the row is written;
/// may refuse, set fields, write other rows through the write's transaction and publish; no network.
/// </summary>
public interface IBeforeWriteHook
{
    /// <summary>Runs before the row is written; refuse, set fields, or write other rows through <paramref name="write"/>.</summary>
    /// <param name="write">The write.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask BeforeWriteAsync(BeforeWrite<AlvoRecord> write, CancellationToken cancellationToken);
}
