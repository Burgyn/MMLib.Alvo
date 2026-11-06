namespace MMLib.Alvo.Events;

/// <summary>One completed step of one subscriber's delivery of one event, as the store keeps it.</summary>
/// <remarks>
/// Keyed by (event, subscriber, name): two handlers of one event keep their own steps, and a delivery put back to pending
/// finds the steps it already completed.
/// </remarks>
public sealed record DeliveryStep
{
    /// <summary>Gets the event the step's delivery is for.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Gets the registration whose delivery ran the step.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets the step's name, unique within one run of the handler.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the step's result as JSON text; <c>null</c> for a step with no value.</summary>
    public required string Result { get; init; }

    /// <summary>Gets when the step's work finished.</summary>
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>A cap on how many deliveries of one subscriber run at once per key.</summary>
public sealed record DeliveryLimit
{
    /// <summary>Gets the registration the cap is for.</summary>
    public required string Subscriber { get; init; }

    /// <summary>Gets what the cap counts per: <c>tenant</c> or <c>record</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Gets how many deliveries of one key may run at once.</summary>
    public required int Limit { get; init; }
}
