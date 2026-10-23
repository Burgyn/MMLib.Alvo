using MMLib.Alvo.Events;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Which event names <c>AddEventHandler</c> subscribes to: an application event, or a framework one.</summary>
internal static class HandledEventType
{
    internal static void Ensure(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        var first = eventType.Split(AlvoEventName.Separator)[0];
        if (AlvoEventName.IsReservedNamespace(first) && first != AlvoEventName.FrameworkNamespace)
        {
            throw new ArgumentException(DataNamespace(eventType, first), nameof(eventType));
        }

        if (!AlvoEventName.IsWellFormed(eventType))
        {
            throw new ArgumentException(
                $"'{eventType}' is not an event name: use two or more lower-case dot-separated segments, such as "
                + "'orders.approved'.",
                nameof(eventType));
        }
    }

    private static string DataNamespace(string eventType, string first) =>
        first == "entity"
            ? $"'{eventType}' is an entity's data event; subscribe to it through its entity — Entity(\"{EntityOf(eventType)}\", "
              + "e => e.OnUpdated(…)) — which checks the entity exists and gives the handler the row's images."
            : $"Nothing emits '{first}.' events in this build, so a handler of '{eventType}' would never run.";

    private static string EntityOf(string eventType) =>
        eventType.Split(AlvoEventName.Separator) is [_, var entity, ..] ? entity : "name";
}
