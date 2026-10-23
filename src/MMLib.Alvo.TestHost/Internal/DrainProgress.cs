using MMLib.Alvo.Events;

namespace MMLib.Alvo.TestHost.Internal;

/// <summary>The rounds one drain ran: their total, and the subscribers of the most recent ones.</summary>
internal sealed class DrainProgress
{
    /// <summary>How many of the last rounds name a loop; a cycle of up to this many subscribers is named whole.</summary>
    internal const int LoopWindow = 10;

    private readonly List<AlvoDrainResult> _rounds = [];

    /// <summary>Records one non-quiet round.</summary>
    internal void Add(AlvoDrainResult round) => _rounds.Add(round);

    /// <summary>Every recorded round summed, subscribers distinct in first-run order.</summary>
    internal AlvoDrainResult Total() => new()
    {
        Claimed = _rounds.Sum(round => round.Claimed),
        Failed = _rounds.Sum(round => round.Failed),
        Subscribers = Distinct(_rounds),
    };

    /// <summary>The subscribers of the last <see cref="LoopWindow"/> rounds.</summary>
    internal IReadOnlyList<string> Recent() => Distinct(_rounds.TakeLast(LoopWindow));

    private static string[] Distinct(IEnumerable<AlvoDrainResult> rounds) =>
        [.. rounds.SelectMany(round => round.Subscribers).Distinct(StringComparer.Ordinal)];
}
