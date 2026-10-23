namespace MMLib.Alvo.Extensions;

/// <summary>How one event handler runs.</summary>
public sealed class AlvoHandlerOptions
{
    private int _order;
    private AlvoRetryPolicy _retry = AlvoRetryPolicy.Default;

    /// <summary>Gets or sets what the handler does, in a sentence, for the dashboard and the docs.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets where the handler sits among those of the same entity and operation, or of the same application event
    /// name; lower first, then registration order. Deliveries of different handlers still run independently — this orders
    /// their fan-out and their listing.
    /// </summary>
    public int Order
    {
        get => _order;
        set
        {
            _order = value;
            OrderStated = true;
        }
    }

    /// <summary>Gets or sets how long one attempt may run before it is cancelled and counted as failed; default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Gets or sets who the handler acts as; default <see cref="AlvoRunAs.System"/>.</summary>
    public AlvoRunAs RunAs { get; set; } = AlvoRunAs.System;

    /// <summary>Gets or sets whether one record's events reach the handler in order; default <see cref="AlvoOrdering.PerRecord"/>.</summary>
    public AlvoOrdering Ordering { get; set; } = AlvoOrdering.PerRecord;

    /// <summary>Gets or sets the retry schedule; default <see cref="AlvoRetryPolicy.Default"/>.</summary>
    public AlvoRetryPolicy Retry
    {
        get => _retry;
        set => _retry = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal bool OrderStated { get; private set; }

    internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);
}
