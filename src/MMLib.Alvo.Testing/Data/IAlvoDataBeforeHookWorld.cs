using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// An <see cref="IAlvoData"/> plus the before-hook invocations its writes made — the seam
/// <see cref="AlvoDataBeforeHookTests"/> needs, because <b>how many times</b> a hook ran is invisible in the
/// row a write returns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counting is not a convenience here; it is the only way one fact can be asked at all.</b> A before-hook
/// is pure by construction — the profile it compiles in has no I/O and no state — so a hook that ran twice
/// over the same candidate produces exactly the value a hook that ran once produces. The idempotent create
/// path is where that matters: a replay must run no hook, and nothing about the row it answers with could
/// ever show that it did.
/// </para>
/// <para>
/// The shape mirrors <see cref="IAlvoDataOutboxWorld"/>: one port under test plus one thing only the
/// implementation's own container can answer. It records and never substitutes — the runner the writes go
/// through is the product's own, so a fact here is a fact about the pipeline that ships.
/// </para>
/// </remarks>
public interface IAlvoDataBeforeHookWorld
{
    /// <summary>Gets the data port under test.</summary>
    IAlvoData Data { get; }

    /// <summary>
    /// The operation of every <see cref="IBeforeHookRunner.Run"/> call this store has made, in order — one
    /// entry per invocation, including invocations for an entity that declares no hook at all.
    /// </summary>
    IReadOnlyList<DataOperation> HookRuns { get; }
}
