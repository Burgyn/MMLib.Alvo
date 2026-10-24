using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Events.Internal;

/// <summary>What a handler is handed for one event: the verb's images over <see cref="AlvoRecord"/>, or the payload.</summary>
internal static class HandlerMessage
{
    internal static object For(AlvoEvent @event, HandlerSource source) => source is EntityEvents entities
        ? ForVerb(@event.Data, entities.Verb)
        : @event.Data.Record ?? AlvoRecord.Empty;

    private static object ForVerb(AlvoEventData data, HandlerVerb verb) => verb switch
    {
        HandlerVerb.Created => new AlvoCreated<AlvoRecord> { New = data.Record ?? AlvoRecord.Empty },
        HandlerVerb.Updated => new AlvoUpdated<AlvoRecord> { Old = data.OldRecord ?? AlvoRecord.Empty, New = data.Record ?? AlvoRecord.Empty },
        HandlerVerb.Deleted => new AlvoDeleted<AlvoRecord> { Old = data.OldRecord ?? AlvoRecord.Empty },
        _ => new AlvoSaved<AlvoRecord> { New = data.Record ?? AlvoRecord.Empty, Old = data.OldRecord },
    };
}
