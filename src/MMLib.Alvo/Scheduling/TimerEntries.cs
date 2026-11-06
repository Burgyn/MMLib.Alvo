using MMLib.Alvo.Events;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The one builder of a <see cref="TimerEntry"/>: <see cref="AlvoScheduler"/> and the declarative timers' planner both arm
/// through it, so a host's timer and a descriptor's are the same envelope, subject and stored provenance.
/// </summary>
internal static class TimerEntries
{
    /// <summary>
    /// The key prefix the descriptor's timers own (<c>alvo:&lt;entity&gt;:&lt;timer&gt;:&lt;id&gt;</c>); <see cref="IAlvoScheduler"/>
    /// refuses it, so a host can neither collide with nor cancel a declarative timer.
    /// </summary>
    internal const string ReservedKeyPrefix = "alvo:";

    /// <summary>Builds the timer <paramref name="context"/> arms.</summary>
    /// <param name="scope">The timer's scope: the tenant id text, or empty.</param>
    /// <param name="key">The timer's key.</param>
    /// <param name="at">When it is due.</param>
    /// <param name="eventType">The custom event it fires.</param>
    /// <param name="data">The event's data.</param>
    /// <param name="context">The system context the timer is armed as, whose provenance the fired event resumes.</param>
    /// <param name="now">The instant the envelope is stamped with.</param>
    internal static TimerEntry Of(
        string scope, string key, DateTimeOffset at, string eventType, IReadOnlyDictionary<string, object?>? data, AlvoContext context,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        var subject = $"timers/{key}";
        AlvoCustomEnvelope.Ensure(eventType, subject, data);
        var message = AlvoCustomEnvelope.Build(eventType, subject, data, context, now);

        return TimerEntry.Of(scope, key, at, message, StoredProvenance(context, message.Envelope));
    }

    /// <summary>
    /// The arming flow's effective provenance, with the correlation and trace context the message envelope settled on — so a
    /// timer armed under a W3C trace outside any flow belongs to that trace, as every other emit does.
    /// </summary>
    private static AlvoProvenance StoredProvenance(AlvoContext context, AlvoEvent envelope) =>
        AlvoEventProvenance.Effective(context) with
        {
            CorrelationId = envelope.CorrelationId,
            TraceParent = envelope.TraceParent,
            TraceState = envelope.TraceState,
        };
}
