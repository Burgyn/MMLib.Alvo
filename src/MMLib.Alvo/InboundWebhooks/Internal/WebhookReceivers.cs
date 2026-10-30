namespace MMLib.Alvo.InboundWebhooks.Internal;

/// <summary>The receivers this host mapped: one per pattern.</summary>
internal sealed class WebhookReceivers
{
    private readonly HashSet<string> _patterns = new(StringComparer.Ordinal);
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

    /// <summary>Records a receiver mapped at <paramref name="pattern"/>.</summary>
    /// <param name="pattern">The receiver's route pattern.</param>
    /// <exception cref="InvalidOperationException">A receiver is already mapped at <paramref name="pattern"/>.</exception>
    internal void Add(string pattern)
    {
        lock (_gate)
        {
            if (!_patterns.Add(pattern))
            {
                throw new InvalidOperationException($"A webhook receiver is already mapped at '{pattern}'; its ids would be deduplicated together. Map each receiver once.");
            }
        }
    }
}
