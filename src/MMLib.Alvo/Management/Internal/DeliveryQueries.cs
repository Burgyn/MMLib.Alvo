using MMLib.Alvo.Events;

using System.Collections.Frozen;

namespace MMLib.Alvo.Management.Internal;

/// <summary>An operator's query or replay, checked and turned into the store's filter.</summary>
internal static class DeliveryQueries
{
    internal const int MaxPage = 200;

    internal static FrozenSet<string> Statuses { get; } = new[] { "pending", "running", "succeeded", "retrying", "dead", "cancelled" }.ToFrozenSet(StringComparer.Ordinal);

    internal static FrozenSet<string> Finished { get; } = new[] { "succeeded", "dead", "cancelled" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] _statusOrder = ["pending", "running", "succeeded", "retrying", "dead", "cancelled"];

    internal static DeliveryFilter Filter(ManagementDeliveryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureStatus(query.Status, Statuses, "listed");
        if (query.Limit is < 1 or > MaxPage)
        {
            throw new ManagementRequestException($"'limit' is {query.Limit}; a page holds 1 to {MaxPage} deliveries.");
        }

        return Window(query.From, query.To) with { Subscriber = query.Subscriber, Status = query.Status, After = query.After, Limit = query.Limit };
    }

    internal static DeliveryFilter Window(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is { } start && to is { } end && end <= start)
        {
            throw new ManagementRequestException(
                $"The window ends at {end:O}, which is not after its start {start:O}. Send 'to' later than 'from', or leave one of them out.");
        }

        return new DeliveryFilter
        {
            Limit = 1,
            FromEventId = from is { } floor ? AlvoEventId.Floor(floor) : null,
            ToEventId = to is { } ceiling ? AlvoEventId.Floor(ceiling) : null,
        };
    }

    internal static void EnsureStatus(string? status, FrozenSet<string> allowed, string verb)
    {
        if (status is not null && !allowed.Contains(status))
        {
            throw new ManagementRequestException(
                $"'{status}' is not a status deliveries can be {verb} by. Use one of: {string.Join(", ", _statusOrder.Where(allowed.Contains))}.");
        }
    }
}
