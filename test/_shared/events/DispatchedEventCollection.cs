namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The xUnit collection every suite that drives the outbox dispatcher belongs to, so that no two of them ever
/// run at the same time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The event counters are process-wide statics</b> (<c>AlvoEventMetrics</c> owns one <c>Meter</c> for the
/// whole process, deliberately), and every event criterion asserts a counter's <em>value</em>. Two suites
/// dispatching concurrently in one assembly therefore sum into each other's totals: the transition fact's
/// <c>dispatched == 1</c> would read the chaos run's ten thousand, and the chaos run's
/// <c>dispatched == 10 000</c> would read three more than it wrote. Neither failure is reproducible, which is
/// the worst possible shape for a criterion.
/// </para>
/// <para>
/// xUnit parallelises across classes and never inside one, so one shared collection name is the whole fix —
/// and it is a narrower instrument than
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>, which
/// <c>MMLib.Alvo.Api.Tests.Integration</c> needs for a different reason (a latency budget measured under
/// contention it does not control) and which would serialise every unrelated fact in these two assemblies.
/// </para>
/// </remarks>
internal static class DispatchedEventCollection
{
    /// <summary>The collection name; it needs no definition class, because no fixture is shared.</summary>
    internal const string Name = "alvo-dispatched-events";
}
