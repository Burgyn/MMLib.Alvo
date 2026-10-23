using System.Diagnostics;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The <c>MMLib.Alvo.Hooks</c> activity source: one consumer span per after-hook action the dispatcher runs.
/// </summary>
/// <remarks>
/// <para>
/// The source spec 2026-10-14 §4.14 names, which host-code hooks and deliveries join as they ship (HX-03, HX-09); a
/// descriptor after-hook's action is the first thing that runs under it. Tags are ids only — the subscriber (the hook's
/// JSON pointer), the event id and type, the outcome — never a URL, a body or an exception message, the same rule the
/// dispatcher's log lines keep.
/// </para>
/// <para>
/// Free when nobody listens: <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> answers
/// <see langword="null"/> without a sampling listener, and both members accept that; a span sampled for propagation
/// only (<see cref="Activity.IsAllDataRequested"/> false) gets no display name or tags either.
/// </para>
/// <para>
/// Every <c>alvo.delivery</c> span keeps the ambient parent — the test host's drain, or nothing under the background
/// loop — and carries the envelope's <c>traceparent</c> as a <b>link</b>, the messaging convention for a consumer that
/// runs later than its producer. Parenting it on the envelope would move a test host's runs out of the drain trace its
/// recorder scopes by. The link is parsed only for a span that records data, so the envelope's trace context costs
/// nothing when nobody listens.
/// </para>
/// </remarks>
internal static class AlvoHookTelemetry
{
    internal const string SourceName = "MMLib.Alvo.Hooks";
    internal const string DeliveryName = "alvo.delivery";
    internal const string SubscriberTag = "alvo.subscriber";
    internal const string EventIdTag = "alvo.event.id";
    internal const string EventTypeTag = "alvo.event.type";
    internal const string OutcomeTag = "alvo.outcome";
    internal const string AttemptTag = "alvo.delivery.attempt";
    internal const string Succeeded = "succeeded";
    internal const string Failed = "failed";
    internal const string LeaseLost = "lease-lost";

    private static readonly ActivitySource _source = new(SourceName);

    /// <summary>
    /// Starts the span for one run of <paramref name="subscriber"/> over <paramref name="event"/>, under whatever is ambient,
    /// linked — never parented — to the envelope's own trace context.
    /// </summary>
    internal static Activity? StartDelivery(string subscriber, AlvoEvent @event, int? attempt = null)
    {
        var delivery = _source.StartActivity(DeliveryName, ActivityKind.Consumer);
        if (delivery is not { IsAllDataRequested: true })
        {
            return delivery;
        }

        if (LinkTo(@event) is { } origin)
        {
            delivery.AddLink(origin);
        }

        delivery.DisplayName = $"{DeliveryName} {subscriber}";
        delivery.SetTag(SubscriberTag, subscriber);
        delivery.SetTag(EventIdTag, @event.Id.ToString());
        delivery.SetTag(EventTypeTag, @event.Type);
        if (attempt is { } number)
        {
            delivery.SetTag(AttemptTag, number);
        }

        return delivery;
    }

    /// <summary>Records how the run ended; a failed run also marks the span's status as an error.</summary>
    internal static void Finish(Activity? delivery, string outcome)
    {
        if (delivery is not { IsAllDataRequested: true })
        {
            return;
        }

        delivery.SetTag(OutcomeTag, outcome);
        if (outcome == Failed)
        {
            delivery.SetStatus(ActivityStatusCode.Error);
        }
    }

    private static ActivityLink? LinkTo(AlvoEvent @event) =>
        @event.TraceParent is { } traceParent && ActivityContext.TryParse(traceParent, @event.TraceState, isRemote: true, out var origin)
            ? new ActivityLink(origin)
            : null;
}
