using MMLib.Alvo.Data;

namespace MMLib.Alvo.Events;

/// <summary>What one delivery knows besides its message: the event, the attempt, and data access as the handler's caller.</summary>
public sealed class AlvoEventContext
{
    internal AlvoEventContext(AlvoEvent @event, string subscriber, int attempt, IAlvoSession data)
    {
        Event = @event;
        Subscriber = subscriber;
        Attempt = attempt;
        Data = data;
    }

    /// <summary>
    /// A context for calling a class handler's <c>HandleAsync</c> yourself — in a unit test, say; deliveries build their own.
    /// </summary>
    /// <remarks>
    /// <paramref name="data"/> is used as it is: writes through <see cref="Data"/> carry no causation from
    /// <paramref name="event"/> and sit no hop deeper in the chain. Only a real delivery runs the handler inside its event's flow.
    /// </remarks>
    /// <param name="event">The event the handler sees.</param>
    /// <param name="data">Data access as the handler's caller, such as <c>IAlvoSessions.For(caller)</c>.</param>
    /// <param name="subscriber">The handler's id.</param>
    /// <param name="attempt">Which attempt this is, from 1.</param>
    /// <returns>The context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="event"/> or <paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subscriber"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempt"/> is less than 1.</exception>
    public static AlvoEventContext Create(AlvoEvent @event, IAlvoSession data, string subscriber, int attempt = 1)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriber);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        return new(@event, subscriber, attempt, data);
    }

    /// <summary>Gets the event's id — the same on every attempt, so the key to deduplicate on.</summary>
    public Guid EventId => Event.Id;

    /// <summary>Gets the whole envelope, as it was stored.</summary>
    public AlvoEvent Event { get; }

    /// <summary>Gets the handler's registered id.</summary>
    public string Subscriber { get; }

    /// <summary>Gets which attempt this is, starting at 1.</summary>
    public int Attempt { get; }

    /// <summary>Gets the caller the handler acts as.</summary>
    public AlvoContext Caller => Data.Context;

    /// <summary>
    /// Gets data access as <see cref="Caller"/>: every call goes through the same rules and tenancy. Inside a delivery,
    /// writes made here carry this event as their cause, one hop deeper in the causation chain; a context from
    /// <see cref="Create"/> carries neither.
    /// </summary>
    public IAlvoSession Data { get; }
}
