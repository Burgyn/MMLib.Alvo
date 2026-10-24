using MMLib.Alvo.Management;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>One handler registration as the catalog describes it.</summary>
internal static class HandlerDescription
{
    internal static ManagementExtension Of(HandlerRegistration handler, bool exposeTypeNames) => new()
    {
        Id = handler.Id,
        Kind = AlvoExtensionKind.EventHandler,
        Stage = AlvoExtensionStage.PostCommit,
        Entities = handler.Source is EntityEvents entities ? entities.Entities : [],
        Operations = handler.Source is EntityEvents data ? Operations(data.Verb) : [],
        Event = handler.Source is NamedEvent named ? named.Type : null,
        When = handler.When,
        Order = handler.Settings.Order,
        Timeout = handler.Settings.Timeout,
        RunAs = handler.Settings.RunAs == AlvoRunAs.EventActor ? "eventActor" : "system",
        Ordering = handler.Settings.Ordering == AlvoOrdering.None ? "none" : "perRecord",
        Description = handler.Settings.Description,
        TypeName = exposeTypeNames ? handler.Invoker.TypeName : null,
    };

    private static string[] Operations(HandlerVerb verb) => verb switch
    {
        HandlerVerb.Created => ["create"],
        HandlerVerb.Updated => ["update"],
        HandlerVerb.Deleted => ["delete"],
        _ => ["create", "update"],
    };
}
