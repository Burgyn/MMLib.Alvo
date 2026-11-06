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
    /// <returns>The narrowing to AND into the read, or <see langword="null"/>.</returns>
    /// <exception cref="HookFailedException">A narrowing failed; the read answers nothing.</exception>
    ReadNarrowing? Narrowing(string entity, DataOperation operation, AlvoContext context);
}
