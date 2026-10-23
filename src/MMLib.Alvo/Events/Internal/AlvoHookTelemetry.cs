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
/// <see langword="null"/> without a sampling listener, and both members accept that.
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
    internal const string Succeeded = "succeeded";
    internal const string Failed = "failed";

    private static readonly ActivitySource _source = new(SourceName);

    /// <summary>Starts the span for one run of <paramref name="subscriber"/> over <paramref name="event"/>.</summary>
    internal static Activity? StartDelivery(string subscriber, AlvoEvent @event)
    {
        var delivery = _source.StartActivity(DeliveryName, ActivityKind.Consumer);
        if (delivery is null)
        {
            return null;
        }

        delivery.DisplayName = $"{DeliveryName} {subscriber}";
        delivery.SetTag(SubscriberTag, subscriber);
        delivery.SetTag(EventIdTag, @event.Id.ToString());
        delivery.SetTag(EventTypeTag, @event.Type);

        return delivery;
    }

    /// <summary>Records how the run ended; a failed run also marks the span's status as an error.</summary>
    internal static void Finish(Activity? delivery, string outcome)
    {
        if (delivery is null)
        {
            return;
        }

        delivery.SetTag(OutcomeTag, outcome);
        if (outcome == Failed)
        {
            delivery.SetStatus(ActivityStatusCode.Error);
        }
    }
}
