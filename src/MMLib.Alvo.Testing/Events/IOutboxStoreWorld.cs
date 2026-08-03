using MMLib.Alvo.Events;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// An <see cref="IOutboxStore"/> over live storage, plus the two things the store's own surface cannot do:
/// put entries in the queue, and move time forward.
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors <see cref="Data.IAlvoDataOutboxWorld"/>: one port under test plus only what the facts
/// cannot ask the port itself. <see cref="IOutboxStore"/> deliberately has no way to enqueue — the write path
/// is what appends, and a store that could would be a second writer of a framework table — so seeding is the
/// implementer's job.
/// </para>
/// <para>
/// <b>Time is advanced through this world rather than through a clock this library defines.</b> A lease
/// expires because the store's own clock moved, and how that clock is injected is the implementation's
/// business: a store built on a <see cref="TimeProvider"/> advances a fake one, a store built on the
/// engine's <c>now()</c> would move something else. Exposing a concrete clock type here would make every
/// external provider author adopt Alvo's, for a fact that only needs "time passed".
/// </para>
/// </remarks>
public interface IOutboxStoreWorld : IAsyncDisposable
{
    /// <summary>Gets the store under test, over storage that already exists.</summary>
    IOutboxStore Store { get; }

    /// <summary>Moves the store's clock forward by <paramref name="duration"/>.</summary>
    /// <param name="duration">How far forward time moves.</param>
    void Advance(TimeSpan duration);

    /// <summary>
    /// Appends <paramref name="count"/> undelivered entries and returns their ids in the order a claim must
    /// take them.
    /// </summary>
    /// <param name="count">How many entries to append.</param>
    /// <returns>The appended ids, ascending — the order the queue is claimed in.</returns>
    Task<IReadOnlyList<Guid>> SeedAsync(int count);

    /// <summary>Appends one undelivered entry carrying <paramref name="id"/> exactly.</summary>
    /// <param name="id">The id to store, chosen by the caller rather than minted.</param>
    /// <returns><paramref name="id"/>, so a fact can name the entry it just seeded.</returns>
    /// <remarks>
    /// The one fact that needs it asks whether an entry sorting <em>below</em> an already-delivered one is
    /// still claimed, which cannot be arranged with minted ids: the mint is monotonic by construction.
    /// </remarks>
    Task<Guid> SeedWithExplicitIdAsync(Guid id);
}
