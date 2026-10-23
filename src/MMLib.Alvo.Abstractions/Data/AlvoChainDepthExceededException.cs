namespace MMLib.Alvo.Data;

/// <summary>
/// A write or a publish was refused because it is deeper in one causation chain than the configured limit allows — a
/// hook, handler or timer is writing in a loop. <c>IAlvoData</c>'s seventh exception family.
/// </summary>
/// <remarks>
/// Rendered as <c>508 Loop Detected</c> with problem type <c>chain-depth-exceeded</c> (RFC 5842's registered meaning)
/// and marked <b>non-retryable</b>: the same request arrives at the same depth, so a retry can only fail again.
/// </remarks>
public sealed class AlvoChainDepthExceededException : Exception
{
    private const string DefaultMessage =
        "This operation is deeper in one causation chain than the limit allows, so it was refused before anything was "
        + "written. Something is writing in a loop; retrying cannot succeed.";

    /// <summary>Initializes a new instance with the default message.</summary>
    public AlvoChainDepthExceededException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/>.</summary>
    /// <param name="message">The message.</param>
    public AlvoChainDepthExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/> and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoChainDepthExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal of an operation at <paramref name="depth"/> under a limit of <paramref name="maxDepth"/>.</summary>
    /// <param name="depth">The effective depth of the refused operation.</param>
    /// <param name="maxDepth">The configured limit.</param>
    /// <returns>The exception to throw.</returns>
    internal static AlvoChainDepthExceededException At(int depth, int maxDepth) => new(
        $"This operation is {depth} hops deep in one causation chain, past the limit of {maxDepth}, so it was refused "
        + "before anything was written. A hook, handler or timer is writing in a loop; retrying cannot succeed. Break "
        + "the loop, or raise Alvo:Extensions:MaxDepth only if a chain this long is intended.");
}
