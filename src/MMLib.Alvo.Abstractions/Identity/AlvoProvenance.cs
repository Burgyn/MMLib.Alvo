namespace MMLib.Alvo;

/// <summary>
/// Which flow a data operation belongs to: the id every step of one end-to-end flow shares, the immediate cause, how
/// many hops deep the causation chain is, and the W3C trace context it runs under.
/// </summary>
/// <remarks>
/// Carried explicitly on <see cref="AlvoContext.Provenance"/> and ambiently by the framework's own pumps; a port entry
/// merges the two with <c>AlvoEventProvenance.Effective</c>, where depth is the larger of the two, so code can raise
/// the depth it reports and never lower it.
/// </remarks>
public sealed record AlvoProvenance
{
    private readonly int _depth;

    /// <summary>Gets the id shared by everything in one end-to-end flow, or <see langword="null"/> when none is known.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Gets the id of the immediate cause (an event id), or <see langword="null"/> for a direct caller.</summary>
    public string? CausationId { get; init; }

    /// <summary>Gets how many hops deep the causation chain is; <c>0</c> for a caller acting directly.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Depth
    {
        get => _depth;
        init => _depth = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(Depth), value, "A causation chain is never less than zero hops deep; a direct caller is depth 0.");
    }

    /// <summary>Gets the W3C <c>traceparent</c> the flow runs under, or <see langword="null"/>.</summary>
    public string? TraceParent { get; init; }

    /// <summary>Gets the W3C <c>tracestate</c> that accompanies <see cref="TraceParent"/>, or <see langword="null"/>.</summary>
    public string? TraceState { get; init; }

    /// <summary>Gets the provenance of a caller nothing else is known about: depth 0 and no ids.</summary>
    public static AlvoProvenance None { get; } = new();
}
