using MMLib.Alvo.Data;

namespace MMLib.Alvo.Hooks;

/// <summary>
/// A validate hook over untyped records: runs before a top-level write's transaction opens — never for a write inside a
/// transaction unit — and may call the network; it can only refuse.
/// </summary>
public interface IValidateWriteHook
{
    /// <summary>Judges the write; record a refusal on <paramref name="write"/> to stop it.</summary>
    /// <param name="write">The write.</param>
    /// <param name="cancellationToken">Cancelled at the hook's timeout.</param>
    /// <returns>A task that completes when the hook has judged.</returns>
    ValueTask ValidateAsync(ValidateWrite<AlvoRecord> write, CancellationToken cancellationToken);
}
