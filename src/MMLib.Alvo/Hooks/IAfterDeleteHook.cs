using MMLib.Alvo.Data;

namespace MMLib.Alvo.Hooks;

/// <summary>
/// An after-delete hook over untyped records: runs inside the transaction after the row is removed and its rollups
/// recomputed; may refuse — keeping the row — write other rows and publish.
/// </summary>
public interface IAfterDeleteHook
{
    /// <summary>Runs after the row is removed; refusing on <paramref name="deletion"/> rolls the delete back.</summary>
    /// <param name="deletion">The delete.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask AfterDeleteAsync(AfterDelete<AlvoRecord> deletion, CancellationToken cancellationToken);
}
