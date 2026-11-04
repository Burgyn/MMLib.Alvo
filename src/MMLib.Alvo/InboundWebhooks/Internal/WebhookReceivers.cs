namespace MMLib.Alvo.InboundWebhooks.Internal;

/// <summary>The receivers this host mapped: one per whole route pattern, a group's prefix included.</summary>
internal sealed class WebhookReceivers
{
    private readonly Dictionary<string, object> _patterns = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _patterns.Count;
            }
        }
    }

    /// <summary>
    /// Records <paramref name="receiver"/> at <paramref name="pattern"/>; recording the same receiver there again — its
    /// endpoint built once more — is not a second mapping.
    /// </summary>
    /// <param name="pattern">The receiver's whole route pattern.</param>
    /// <param name="receiver">The receiver mapped there.</param>
    /// <exception cref="InvalidOperationException">Another receiver is already mapped at <paramref name="pattern"/>.</exception>
    internal void Add(string pattern, object receiver)
    {
        lock (_gate)
        {
            if (!_patterns.TryAdd(pattern, receiver) && !ReferenceEquals(_patterns[pattern], receiver))
            {
                throw new InvalidOperationException($"A webhook receiver is already mapped at '{pattern}'; its ids would be deduplicated together. Map each receiver once.");
            }
        }
    }
}
