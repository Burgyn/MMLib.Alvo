namespace MMLib.Alvo.Management;

/// <summary>No delivery with that id was recorded.</summary>
public sealed class ManagementDeliveryNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ManagementDeliveryNotFoundException"/> class.</summary>
    public ManagementDeliveryNotFoundException()
        : base("No delivery with that id was recorded.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementDeliveryNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ManagementDeliveryNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ManagementDeliveryNotFoundException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ManagementDeliveryNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance naming the delivery that was asked for.</summary>
    /// <param name="delivery">The delivery id.</param>
    public ManagementDeliveryNotFoundException(Guid delivery)
        : base($"No delivery '{delivery}' was recorded. List the deliveries to find its id.")
    {
        Delivery = delivery;
    }

    /// <summary>Gets the delivery id that was asked for.</summary>
    public Guid Delivery { get; }
}
