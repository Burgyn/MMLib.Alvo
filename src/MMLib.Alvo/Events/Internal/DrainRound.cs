namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The tally one dispatch round keeps while it runs: which subscribers it attempted, and how many claimed entries failed.
/// </summary>
internal sealed class DrainRound
{
    private readonly List<string> _subscribers = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _failed;

    /// <summary>Records that <paramref name="subscriber"/> ran, keeping first-run order and naming it once.</summary>
    internal void Ran(string subscriber)
    {
        if (_seen.Add(subscriber))
        {
            _subscribers.Add(subscriber);
        }
    }

    /// <summary>Records one claimed entry whose attempt failed, however many of its subscribers it had run.</summary>
    internal void Failed() => _failed++;

    /// <summary>The round's result once every claimed entry has been dispatched.</summary>
    internal AlvoDrainResult Result(int claimed) => new()
    {
        Claimed = claimed,
        Failed = _failed,
        Subscribers = [.. _subscribers],
    };
}
