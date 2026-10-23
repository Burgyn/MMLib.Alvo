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
    /// Gets data access as <see cref="Caller"/>: every call goes through the same rules and tenancy, and writes made here
    /// carry this event as their cause, one hop deeper in the causation chain.
    /// </summary>
    public IAlvoSession Data { get; }
}
