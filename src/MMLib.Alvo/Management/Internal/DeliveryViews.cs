using MMLib.Alvo.Events;

namespace MMLib.Alvo.Management.Internal;

/// <summary>The store's delivery records as the wire's.</summary>
internal static class DeliveryViews
{
    internal static ManagementDelivery Of(StoredDelivery row) => new()
    {
        Id = row.Id,
        EventId = row.EventId,
        Subscriber = row.Subscriber,
        PartitionKey = row.PartitionKey,
        TenantId = row.TenantId,
        Status = row.Status,
        Attempts = row.Attempts,
        NotBefore = row.NotBefore,
        CompletedAt = row.CompletedAt,
        LastErrorType = row.LastErrorType,
    };

    internal static ManagementDeliveryAttempt Of(StoredDeliveryAttempt attempt) => new()
    {
        Attempt = attempt.Attempt,
        StartedAt = attempt.StartedAt,
        Duration = attempt.Duration,
        Outcome = attempt.Outcome,
        ErrorType = attempt.ErrorType,
    };

    /// <summary>A completed step by name and time; its result never leaves the store, since it can hold personal data.</summary>
    internal static ManagementDeliveryStep Of(DeliveryStep step) => new() { Name = step.Name, CompletedAt = step.CompletedAt };
}
