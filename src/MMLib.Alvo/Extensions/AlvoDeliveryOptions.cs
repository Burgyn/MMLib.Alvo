namespace MMLib.Alvo.Extensions;

/// <summary>How this process runs event-handler deliveries (<c>Alvo:Deliveries</c>), validated when the host starts.</summary>
public sealed class AlvoDeliveryOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Alvo:Deliveries";

    /// <summary>Gets or sets how many deliveries this process runs at once; default 16, from 1 to 256.</summary>
    /// <remarks>
    /// It also caps the invocations one handler may leave running past their timeout — abandoned, because they ignored
    /// their cancellation: a handler with this many still alive is not claimed for until one of them ends, while other
    /// handlers keep running.
    /// </remarks>
    public int MaxConcurrency { get; set; } = 16;
}
