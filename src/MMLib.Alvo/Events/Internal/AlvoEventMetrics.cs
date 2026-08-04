using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The event subsystem's three counters: how many events were delivered, how many matched no subscription,
/// and how many attempts failed.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is half of what "execution log" means in this build</b> (the other half being one log entry per
/// executed action). A durable, queryable execution log with retention and a redelivery UI belongs to the
/// later webhook-management work; the acceptance criterion this pair answers is <em>"an event matching
/// nothing produces no execution log, only a counter"</em>, which is a statement about these counters
/// existing and about the log entry <b>not</b> being written.
/// </para>
/// <para>
/// <b>All three live on one <see cref="Meter"/>, and that is load-bearing.</b> A listener subscribes by meter
/// name, so a counter created on a second meter would be silently unobserved by anything watching this one —
/// a criterion that counts increments would then read zero and be indistinguishable from a counter that was
/// never incremented. <see cref="AllInstrumentNames"/> exists so a fact can hold the whole set at once.
/// </para>
/// <para>
/// <b>Static rather than <c>IMeterFactory</c>-scoped.</b> These are process-wide subsystem counters written
/// from a single background service, and the tests that count them attach a listener by meter name before a
/// host exists. A factory-scoped meter would be per-container, which buys isolation nothing here needs and
/// costs the ability to observe the subsystem without resolving it.
/// </para>
/// </remarks>
internal static class AlvoEventMetrics
{
    /// <summary>The one meter every event instrument is published on.</summary>
    internal const string MeterName = "MMLib.Alvo.Events";

    /// <summary>The instrument name of <see cref="Dispatched"/>.</summary>
    internal const string DispatchedName = "alvo.events.dispatched";

    /// <summary>The instrument name of <see cref="Filtered"/>.</summary>
    internal const string FilteredName = "alvo.events.filtered";

    /// <summary>The instrument name of <see cref="Failed"/>.</summary>
    internal const string FailedName = "alvo.events.failed";

    private const string EventUnit = "{event}";

    private static readonly Meter _meter = new(MeterName);

    /// <summary>Events whose matching actions all ran and which are retired from the outbox.</summary>
    internal static Counter<long> Dispatched { get; } = _meter.CreateCounter<long>(
        DispatchedName,
        EventUnit,
        "Events delivered: every after-hook the event matched ran, and the outbox entry was retired.");

    /// <summary>Events that matched no after-hook, counted once each and never logged.</summary>
    internal static Counter<long> Filtered { get; } = _meter.CreateCounter<long>(
        FilteredName,
        EventUnit,
        "Events that matched no after-hook subscription. Counted once each, with no action log entry.");

    /// <summary>Delivery attempts that threw, counted once per attempt rather than once per event.</summary>
    internal static Counter<long> Failed { get; } = _meter.CreateCounter<long>(
        FailedName,
        EventUnit,
        "Delivery attempts that failed. One increment per attempt, so a retried event counts more than once.");

    /// <summary>Every instrument name this meter publishes, so a fact can hold the whole set.</summary>
    internal static IReadOnlyList<string> AllInstrumentNames { get; } = [DispatchedName, FilteredName, FailedName];
}
