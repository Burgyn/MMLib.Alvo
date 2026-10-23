using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Events.Internal;

/// <summary>The delivery instruments, on the events meter (spec 2026-10-14 §4.8); tags are ids and fixed words only.</summary>
internal static class AlvoDeliveryMetrics
{
    internal const string DeadName = "alvo.delivery.dead";
    internal const string SubscriberTag = "alvo.subscriber";
    internal const string ReasonTag = "alvo.reason";
    internal const string DurationName = "alvo.delivery.duration";
    internal const string AttemptsName = "alvo.delivery.attempts";
    internal const string LagName = "alvo.delivery.lag";
    internal const string OutcomeTag = "alvo.outcome";

    private static readonly Meter _meter = new(AlvoEventMetrics.MeterName);

    internal static Counter<long> Dead { get; } = _meter.CreateCounter<long>(
        DeadName, "{delivery}", "Deliveries that ended dead: fanned out dead, or failed at their last attempt.");

    internal static void DeadRecorded(string subscriber, string reason) =>
        Dead.Add(1, new KeyValuePair<string, object?>(SubscriberTag, subscriber), new KeyValuePair<string, object?>(ReasonTag, reason));

    internal static Histogram<double> Durations { get; } = _meter.CreateHistogram<double>(
        DurationName, "ms", "How long one delivery attempt ran.");

    internal static Counter<long> AttemptCount { get; } = _meter.CreateCounter<long>(
        AttemptsName, "{attempt}", "Delivery attempts, by outcome.");

    internal static Histogram<double> Lags { get; } = _meter.CreateHistogram<double>(
        LagName, "ms", "How long after its event a delivery attempt started.");

    internal static void Recorded(DeliveryAttempt attempt, DeliveryRunOutcome outcome)
    {
        var subscriber = new KeyValuePair<string, object?>(SubscriberTag, attempt.Delivery.Subscriber);
        var ended = new KeyValuePair<string, object?>(OutcomeTag, OutcomeName(outcome));
        Durations.Record(attempt.Duration.TotalMilliseconds, subscriber, ended);
        AttemptCount.Add(1, subscriber, ended);
    }

    internal static void Lagged(string subscriber, TimeSpan lag) =>
        Lags.Record(Math.Max(0, lag.TotalMilliseconds), new KeyValuePair<string, object?>(SubscriberTag, subscriber));

    private static string OutcomeName(DeliveryRunOutcome outcome) => outcome switch
    {
        DeliveryRunOutcome.Succeeded => "succeeded",
        DeliveryRunOutcome.Dead => "dead",
        DeliveryRunOutcome.LeaseLost => "lease-lost",
        _ => "failed",
    };
}
