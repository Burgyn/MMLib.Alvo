namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// One piece of host code registered with Alvo — the <c>CelFunctionRegistration</c> precedent: an internal singleton per
/// call, read back as a set when the boot binds them.
/// </summary>
/// <param name="Id">The id its durable state is keyed by.</param>
/// <param name="Sequence">Its place among every registration of this host, in call order.</param>
internal abstract record ExtensionRegistration(string Id, int Sequence);

/// <summary>An event handler; <paramref name="Typed"/> is set when it was registered over a host type.</summary>
internal sealed record HandlerRegistration(
    string Id, int Sequence, HandlerSource Source, string? When, HandlerSettings Settings, HandlerInvoker Invoker, TypedHandler? Typed = null)
    : ExtensionRegistration(Id, Sequence);

/// <summary>An id that is no longer registered, and what happens to its unfinished work.</summary>
internal sealed record RetiredExtension(string Id, int Sequence, AlvoRetirement Retirement) : ExtensionRegistration(Id, Sequence);

/// <summary>Which events a handler is subscribed to.</summary>
internal abstract record HandlerSource;

/// <summary>One verb of one or more entities' data events; <c>*</c> alone is every entity.</summary>
internal sealed record EntityEvents(IReadOnlyList<string> Entities, HandlerVerb Verb) : HandlerSource
{
    internal const string AllEntities = "*";

    internal bool IsAll => Entities is [AllEntities];

    public bool Equals(EntityEvents? other) =>
        other is not null && Verb == other.Verb && Entities.SequenceEqual(other.Entities, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Verb, string.Join('\u001f', Entities));
}

/// <summary>One application or framework event, by its exact type.</summary>
internal sealed record NamedEvent(string Type) : HandlerSource;

/// <summary>The data-event verbs a handler is registered for.</summary>
internal enum HandlerVerb
{
    Created,
    Updated,
    Deleted,
    Saved,
}

/// <summary>A handler's options, frozen at registration.</summary>
internal sealed record HandlerSettings(
    string? Description,
    int Order,
    bool OrderStated,
    TimeSpan Timeout,
    AlvoRunAs RunAs,
    AlvoOrdering Ordering,
    AlvoRetryPolicy Retry)
{
    internal static HandlerSettings From(AlvoHandlerOptions options) => new(
        options.Description, options.Order, options.OrderStated, options.Timeout, options.RunAs, options.Ordering, options.Retry);
}
