using MMLib.Alvo.Events;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The apply-time findings for an entity's <c>timers</c>: each fires an event a host may mint, the entity is physical, and
/// no cascading <c>ref</c> can delete its rows past the write path that cancels their timers.
/// </summary>
/// <remarks>
/// <b>A cascade is refused rather than chased.</b> A row the database deletes on its parent's delete never reaches the write
/// that cancels its timers, so its timer would fire for a record that no longer exists. <c>at</c> and <c>when</c> are
/// compiled with the rules, by <c>LifecycleCompiler</c>.
/// </remarks>
internal static class EntityTimerCheck
{
    /// <summary>Every finding about every declared timer.</summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IReadOnlyList<DescriptorValidationError> Check(AlvoDescriptor descriptor) =>
    [
        .. from entity in descriptor.Entities
           where entity.Value.Timers is { Count: > 0 }
           from error in Errors(entity.Key, entity.Value)
           select error,
    ];

    /// <summary>The event a timer fires: its declared <c>event</c>, else <c>&lt;entity&gt;.&lt;timer&gt;</c> with '-' as '_'.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="timer">The timer's name.</param>
    /// <param name="declared">The timer.</param>
    internal static string EventOf(string entity, string timer, EntityTimer declared) =>
        declared.Event ?? $"{entity}{AlvoEventName.Separator}{timer}".Replace('-', '_');

    private static IEnumerable<DescriptorValidationError> Errors(string entity, EntityDescriptor declared)
    {
        if (!DescriptorToSchemaMapper.IsPhysical(declared))
        {
            return [StateMachineCheck.DynamicError(entity, "timers")];
        }

        IEnumerable<DescriptorValidationError?> findings =
        [
            CascadeError(entity, declared),
            .. declared.Timers!.Select(timer => EventError(entity, timer.Key, timer.Value)),
        ];

        return findings.OfType<DescriptorValidationError>();
    }

    private static DescriptorValidationError? CascadeError(string entity, EntityDescriptor declared)
    {
        var cascading = declared.Fields.Where(field => field.Value.OnDelete == OnDeleteAction.Cascade).Select(field => $"'{field.Key}'").ToList();
        return cascading.Count == 0
            ? null
            : new DescriptorValidationError(
                $"/entities/{entity}/timers",
                $"'{entity}' declares timers, and its ref {string.Join(", ", cascading)} deletes its rows on cascade — a row the "
                + "database deletes never reaches the write that cancels its timers, so they would fire for a record that is gone.",
                "Make the ref 'onDelete: restrict' (delete the rows through the API first), or move the timers to an entity no cascade reaches.",
                DescriptorValidationSeverity.Error);
    }

    private static DescriptorValidationError? EventError(string entity, string timer, EntityTimer declared)
    {
        var type = EventOf(entity, timer, declared);
        var reason = !AlvoEventName.IsWellFormed(type) ? "which is not a well-formed event name"
            : AlvoEventName.IsReservedNamespace(type.Split(AlvoEventName.Separator)[0]) ? "which is in one of the framework's reserved namespaces, where only Alvo mints events"
            : null;

        return reason is null ? null : new DescriptorValidationError(
            declared.Event is null ? $"/entities/{entity}/timers/{timer}" : $"/entities/{entity}/timers/{timer}/event",
            $"Timer '{timer}' of '{entity}' fires '{type}', {reason}.",
            declared.Event is null
                ? $"Declare 'event' with a name of your own, e.g. 'bookings.{timer.Replace('-', '_')}'."
                : $"Name an event of your own, e.g. 'bookings.{timer.Replace('-', '_')}'.",
            DescriptorValidationSeverity.Error);
    }
}
