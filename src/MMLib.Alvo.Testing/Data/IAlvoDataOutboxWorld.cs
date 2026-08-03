using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// An <see cref="IAlvoData"/> plus the events its writes queued — the seam
/// <see cref="AlvoDataOutboxTests"/> needs, because "no change without an event, and no event without a
/// change" is a claim about a table no store call touches and is invisible in the rows a write returns.
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors <see cref="IStatementProbe"/>: one port under test plus one thing only the
/// implementation can answer. Every event is read back through
/// <see cref="AlvoEventJson.Read(string)"/> from the stored payload, never through a second copy of the
/// serializer, so an implementation that stored something the dispatcher cannot read fails the suite
/// rather than passing it.
/// </para>
/// <para>
/// It carries no way to <em>write</em> or clear the queue on purpose. Every fact in the suite asserts the
/// whole ordered sequence of events one act produced, which is a stronger question than "was there one
/// after I cleared them" and needs no test-only mutation of a framework table to ask.
/// </para>
/// </remarks>
public interface IAlvoDataOutboxWorld
{
    /// <summary>Gets the data port under test.</summary>
    IAlvoData Data { get; }

    /// <summary>
    /// Every event this store has queued, in the order a dispatcher would claim them (<c>ORDER BY id</c>).
    /// </summary>
    Task<IReadOnlyList<AlvoEvent>> EventsAsync();
}
