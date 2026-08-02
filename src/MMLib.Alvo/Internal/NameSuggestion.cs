namespace MMLib.Alvo.Internal;

/// <summary>
/// The one "did you mean 'x'?" spelling suggestion Alvo's agent-first errors share. An unknown field
/// name, an undeclared enum value and an undeclared role literal are all the same mistake with a
/// different candidate list, so the edit-distance threshold and the tie-break live here rather than in
/// one copy per call site — an agent reading two of these errors should not see two different notions
/// of "close enough".
/// </summary>
internal static class NameSuggestion
{
    /// <summary>
    /// Two edits: enough for a transposition or a doubled/dropped character (<c>amdin</c> → <c>admin</c>),
    /// tight enough that a genuinely different name is not "suggested" as a fix.
    /// </summary>
    private const int MaxDistance = 2;

    /// <summary>
    /// The candidate closest to <paramref name="value"/> within <see cref="MaxDistance"/> edits, or
    /// <see langword="null"/> when none is close enough. Ties break ordinally by candidate name, so the
    /// suggestion is stable across runs rather than dependent on enumeration order.
    /// </summary>
    /// <param name="value">The name the author actually wrote.</param>
    /// <param name="candidates">The declared names to suggest from.</param>
    public static string? Closest(string value, IEnumerable<string> candidates) => candidates
        .Select(candidate => (Name: candidate, Distance: Distance(value, candidate)))
        .Where(candidate => candidate.Distance <= MaxDistance)
        .OrderBy(candidate => candidate.Distance)
        .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
        .Select(candidate => candidate.Name)
        .FirstOrDefault();

    private static int Distance(string a, string b)
    {
        var distances = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            distances[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            distances[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                distances[i, j] = Math.Min(Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1), distances[i - 1, j - 1] + cost);
            }
        }

        return distances[a.Length, b.Length];
    }
}
