using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>What a typed registration adds to the untyped core's: the host type's shape, and how its message is built from an event.</summary>
/// <param name="Shape">The host type's mapping, checked against the entity at boot.</param>
/// <param name="Message">Builds the handler's message from the event's images or payload.</param>
internal sealed record TypedHandler(RecordShape Shape, Func<AlvoEventData, object> Message)
{
    internal static TypedHandler ForEntity<T>(HandlerVerb verb)
        where T : class
    {
        var shape = RecordShape.Of(typeof(T));
        Func<AlvoEventData, object> message = verb switch
        {
            HandlerVerb.Created => data => new AlvoCreated<T> { New = shape.Materialize<T>(data.Record) },
            HandlerVerb.Updated => data => new AlvoUpdated<T> { Old = shape.Materialize<T>(data.OldRecord), New = shape.Materialize<T>(data.Record) },
            HandlerVerb.Deleted => data => new AlvoDeleted<T> { Old = shape.Materialize<T>(data.OldRecord) },
            _ => data => new AlvoSaved<T> { New = shape.Materialize<T>(data.Record), Old = data.OldRecord is { } old ? shape.Materialize<T>(old) : null },
        };

        return new(shape, message);
    }

    internal static TypedHandler ForPayload<T>()
        where T : class
    {
        var shape = RecordShape.Of(typeof(T));

        return new(shape, data => shape.Materialize<T>(data.Record));
    }
}
