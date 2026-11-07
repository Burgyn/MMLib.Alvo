using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// One piece of host code registered with Alvo — the <c>CelFunctionRegistration</c> precedent: an internal singleton per
/// call, read back as a set when the boot binds them.
/// </summary>
/// <param name="Id">The id its durable state is keyed by.</param>
/// <param name="Sequence">Its place among every registration of this host, in call order.</param>
internal abstract record ExtensionRegistration(string Id, int Sequence);

/// <summary>
/// An event handler; <paramref name="Typed"/> is set when it was registered over a host type, and <paramref name="Job"/> when
/// it is a scheduled job or a data migration — a handler of the framework event Alvo mints for each of its runs.
/// <paramref name="Kind"/> overrides the delivery kind its deliveries record — a descriptor action's <c>action</c> or
/// <c>function</c> — and <paramref name="TriggerOnly"/> marks a schedule that only mints its event: it selects no event, so
/// no delivery is ever recorded for it.
/// </summary>
internal sealed record HandlerRegistration(
    string Id,
    int Sequence,
    HandlerSource Source,
    string? When,
    HandlerSettings Settings,
    HandlerInvoker Invoker,
    TypedHandler? Typed = null,
    JobSpec? Job = null,
    string? Kind = null,
    bool TriggerOnly = false)
    : ExtensionRegistration(Id, Sequence)
{
    /// <summary>The delivery kind this registration's deliveries record.</summary>
    internal string DeliveryKind => Kind ?? Job?.DeliveryKind ?? Events.Internal.DeliveryKind.Handler;
}

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
    AlvoRetryPolicy Retry,
    TimeSpan? Debounce = null,
    AlvoConcurrency? Concurrency = null)
{
    internal static HandlerSettings From(AlvoHandlerOptions options) => new(
        options.Description,
        options.Order,
        options.OrderStated,
        options.Timeout,
        options.RunAs,
        options.Ordering,
        options.Retry,
        options.Debounce,
        options.Concurrency);
}
