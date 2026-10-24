using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Which events a handler's source selects, its <c>when</c> aside — shared by bound selection and the unbound dead letter.</summary>
internal static class HandlerSources
{
    /// <summary>Whether <paramref name="source"/> selects an event of <paramref name="eventType"/>, and for which entity.</summary>
    internal static (bool Selected, string? Entity) Selects(HandlerSource source, string eventType) => source switch
    {
        NamedEvent named => (string.Equals(named.Type, eventType, StringComparison.Ordinal), null),
        EntityEvents entities => SelectsDataEvent(entities, eventType),
        _ => (false, null),
    };

    private static (bool Selected, string? Entity) SelectsDataEvent(EntityEvents source, string eventType)
    {
        if (!EnvelopeReading.TryReadDataType(eventType, out var entity, out var operation) || !Covers(source.Verb, operation))
        {
            return (false, null);
        }

        return (source.IsAll || source.Entities.Contains(entity, StringComparer.Ordinal), entity);
    }

    private static bool Covers(HandlerVerb verb, DataOperation operation) => verb switch
    {
        HandlerVerb.Created => operation == DataOperation.Create,
        HandlerVerb.Updated => operation == DataOperation.Update,
        HandlerVerb.Deleted => operation == DataOperation.Delete,
        HandlerVerb.Saved => operation is DataOperation.Create or DataOperation.Update,
        _ => false,
    };
}
