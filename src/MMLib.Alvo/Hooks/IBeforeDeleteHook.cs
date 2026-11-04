using MMLib.Alvo.Data;

namespace MMLib.Alvo.Hooks;

/// <summary>
/// A before-delete hook over untyped records: runs inside a delete's transaction before the row is removed; may refuse,
/// write other rows through the delete's transaction and publish; no network.
/// </summary>
public interface IBeforeDeleteHook
{
    /// <summary>Runs before the row is removed; record a refusal on <paramref name="deletion"/> to stop it.</summary>
    /// <param name="deletion">The delete.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    ValueTask BeforeDeleteAsync(BeforeDelete<AlvoRecord> deletion, CancellationToken cancellationToken);
}
