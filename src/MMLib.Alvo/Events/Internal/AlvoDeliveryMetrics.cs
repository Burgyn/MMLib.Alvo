using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Events.Internal;

/// <summary>The delivery instruments, on the events meter (spec 2026-10-14 §4.8); tags are ids and fixed words only.</summary>
internal static class AlvoDeliveryMetrics
{
    internal const string DeadName = "alvo.delivery.dead";
    internal const string SubscriberTag = "alvo.subscriber";
    internal const string ReasonTag = "alvo.reason";

    private static readonly Meter _meter = new(AlvoEventMetrics.MeterName);

    internal static Counter<long> Dead { get; } = _meter.CreateCounter<long>(
        DeadName, "{delivery}", "Deliveries that ended dead: fanned out dead, or failed at their last attempt.");

    internal static void DeadRecorded(string subscriber, string reason) =>
        Dead.Add(1, new KeyValuePair<string, object?>(SubscriberTag, subscriber), new KeyValuePair<string, object?>(ReasonTag, reason));
}
