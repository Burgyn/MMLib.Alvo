using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events;

/// <summary>
/// The one door through which Alvo mints the events of its reserved <c>alvo</c> namespace — a command's completion
/// (and, later, a schedule's run). A host cannot reach it: the custom-event door refuses the namespace. Its partition key
/// carries the tenant, so one tenant's completions never queue behind another's in an ordered partition (Ruling 48).
/// </summary>
internal static class AlvoFrameworkEvent
{
    private const string PartitionMarker = "framework.event";

    /// <summary>Builds a framework event, wrapped so only a port that takes framework events can append it, stamped with <paramref name="context"/>'s identity and effective provenance.</summary>
    /// <param name="type">The event type, in the <c>alvo.</c> namespace.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="data">The flat payload.</param>
    /// <param name="context">The caller it happened as.</param>
    /// <param name="now">When it happened.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="type"/> is not a well-formed <c>alvo.</c> name, or the payload cannot be written.
    /// </exception>
    internal static AlvoFrameworkEnvelope Create(
        string type, string subject, IReadOnlyDictionary<string, object?> data, AlvoContext context, DateTimeOffset now)
    {
        EnsureFramework(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(context);
        AlvoCustomEnvelope.EnsureWritable(data);

        var id = AlvoEventId.Create(now);
        var stamp = AlvoEnvelopeStamp.Of(context, id, context.Tenant);

        return new AlvoFrameworkEnvelope(stamp.Onto(new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = now,
            Subject = subject,
            PartitionKey = $"{PartitionMarker}:{context.Tenant?.ToString() ?? AlvoIdempotency.TenantlessScope}:{subject}",
            AuthType = stamp.AuthType,
            CorrelationId = stamp.CorrelationId,
            Data = new AlvoEventData { Record = new AlvoRecord(data) },
        }));
    }

    private static void EnsureFramework(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (!type.StartsWith(AlvoEventName.FrameworkNamespace + AlvoEventName.Separator, StringComparison.Ordinal)
            || !AlvoEventName.IsWellFormed(type))
        {
            throw new ArgumentException(
                $"'{type}' is not a framework event name: it must be a well-formed name in the "
                + $"'{AlvoEventName.FrameworkNamespace}.' namespace.",
                nameof(type));
        }
    }
}
