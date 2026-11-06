using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>A host narrowing, ready for the driver: one filter, and the extensions it came from (for a failure's ids).</summary>
/// <param name="Filter">The filter ANDed into the read as its own term.</param>
/// <param name="ExtensionIds">Every narrowing registered on the entity, in registration order.</param>
internal sealed record ReadNarrowing(AlvoFilter Filter, IReadOnlyList<string> ExtensionIds);

/// <summary>
/// Runs a host's read extensions at the places only a driver knows. Implemented by the core; a driver without one runs
/// none.
/// </summary>
/// <remarks>
/// <b>Fail closed.</b> A narrowing that is unbound for the current catalog, throws, cannot be translated, exceeds the filter
/// limits or names a field the entity does not declare fails the read with a <see cref="HookFailedException"/> of stage
/// <see cref="HookFailureStage.Read"/> — and, inside a transaction unit, blames the unit.
/// </remarks>
internal interface IReadExtensionRunner
{
    /// <summary>The narrowing of this read, or <see langword="null"/> when none binds the entity or the caller is the system.</summary>
    /// <param name="entity">The entity being read.</param>
    /// <param name="operation"><see cref="DataOperation.List"/> for a page and its count, <see cref="DataOperation.Get"/> for a get or an echo.</param>
    /// <param name="context">The caller the read is for.</param>
    /// <param name="cancellationToken">The read's token.</param>
    /// <returns>The narrowing to AND into the read, or <see langword="null"/>.</returns>
    /// <exception cref="HookFailedException">A narrowing failed; the read answers nothing.</exception>
    ValueTask<ReadNarrowing?> NarrowingAsync(string entity, DataOperation operation, AlvoContext context, CancellationToken cancellationToken);

    /// <summary>
    /// The failure of a narrowing only the driver can see — a value its field cannot hold, a tree the renderer refuses, or a
    /// combination past the limits: logged like every other read-extension failure (ids, reason, exception type), blamed on
    /// the open unit, and returned for the driver to throw.
    /// </summary>
    /// <param name="extensions">The ids of the narrowings the refused term came from.</param>
    /// <param name="reason">One of the reason constants.</param>
    /// <param name="cause">What refused the term; only its type is logged.</param>
    /// <returns>The failure to throw.</returns>
    HookFailedException Refused(IReadOnlyList<string> extensions, string reason, Exception cause);
}
