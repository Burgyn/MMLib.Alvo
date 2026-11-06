using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Scheduling;

/// <summary>Typed timers: the message is a class marked with <see cref="AlvoEventAttribute"/>.</summary>
public static class AlvoSchedulerExtensions
{
    /// <summary>
    /// Arms the timer <paramref name="key"/> to fire <paramref name="message"/> at <paramref name="at"/>: the event type comes
    /// from its <see cref="AlvoEventAttribute"/>, the payload from its properties, as a typed handler reads them back.
    /// </summary>
    /// <typeparam name="TMessage">The message type, marked with <see cref="AlvoEventAttribute"/>.</typeparam>
    /// <param name="scheduler">The scheduler.</param>
    /// <param name="key">The timer's key: 1–200 characters, none of them a control character.</param>
    /// <param name="at">When it fires; a time already past fires on the next round.</param>
    /// <param name="message">The message the fired event carries.</param>
    /// <param name="tenant">The tenant the timer belongs to; defaults to the open unit's tenant.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the timer is armed.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TMessage"/> carries no <see cref="AlvoEventAttribute"/>, or the scheduler refused the timer.
    /// </exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TMessage"/>'s shape cannot be written.</exception>
    public static Task ScheduleAsync<TMessage>(
        this IAlvoScheduler scheduler,
        string key,
        DateTimeOffset at,
        TMessage message,
        TenantId? tenant = null,
        CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(message);
        var shape = RecordShape.Of(typeof(TMessage));
        var eventType = shape.EventType
            ?? throw new ArgumentException($"Mark {typeof(TMessage).Name} with [AlvoEvent(\"<event type>\")], so Alvo knows which event it is.", nameof(TMessage));
        RecordWriter.EnsureWritable(shape);

        return scheduler.ScheduleAsync(key, at, eventType, PayloadOf(shape, message), tenant, cancellationToken);
    }

    private static Dictionary<string, object?> PayloadOf(RecordShape shape, object message) =>
        shape.Properties.ToDictionary(
            property => property.Field, property => RecordWriter.Field(property, property.Property.GetValue(message)), StringComparer.Ordinal);
}
