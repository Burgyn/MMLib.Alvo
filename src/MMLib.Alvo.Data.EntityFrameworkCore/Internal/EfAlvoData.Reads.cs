using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>The host's read extensions at the reads only this driver can place.</summary>
internal sealed partial class EfAlvoData
{
    /// <summary>
    /// The host narrowing of a caller-facing read — a page, its count and its cursor's anchor, a get, a write's echo and a
    /// replay's — or <see langword="null"/>. Never asked for a write's target rows, its pre-image or an unmasked read: writes
    /// are not narrowed.
    /// </summary>
    private ValueTask<ReadNarrowing?> NarrowingOfAsync(
        string entity, DataOperation operation, AlvoContext context, CancellationToken cancellationToken) =>
        _reads?.NarrowingAsync(entity, operation, context, cancellationToken) ?? ValueTask.FromResult<ReadNarrowing?>(null);
}
