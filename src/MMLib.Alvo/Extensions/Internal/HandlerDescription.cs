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
        RunAs = RunAs(handler.Settings.RunAs),
        Ordering = Ordering(handler.Settings.Ordering),
        Description = handler.Settings.Description,
        TypeName = exposeTypeNames ? handler.Invoker.TypeName : null,
    };

    private static string[] Operations(HandlerVerb verb) => verb switch
    {
        HandlerVerb.Created => ["create"],
        HandlerVerb.Updated => ["update"],
        HandlerVerb.Deleted => ["delete"],
        HandlerVerb.Saved => ["create", "update"],
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "A handler verb the catalog does not describe."),
    };

    private static string RunAs(AlvoRunAs runAs) => runAs switch
    {
        AlvoRunAs.System => "system",
        AlvoRunAs.EventActor => "eventActor",
        _ => throw new ArgumentOutOfRangeException(nameof(runAs), runAs, "An identity the catalog does not describe."),
    };

    private static string Ordering(AlvoOrdering ordering) => ordering switch
    {
        AlvoOrdering.PerRecord => "perRecord",
        AlvoOrdering.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(ordering), ordering, "An ordering the catalog does not describe."),
    };
}
