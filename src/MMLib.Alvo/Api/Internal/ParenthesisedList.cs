using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Internal;

/// <summary>What splitting a bracketed list produced.</summary>
internal enum ListSplit
{
    /// <summary>The members, in the order written.</summary>
    Ok,

    /// <summary>The text is not a balanced, non-empty <c>(…)</c>.</summary>
    Malformed,

    /// <summary>The list carries more members than the caller will accept.</summary>
    TooMany,
}

/// <summary>
/// Splits the one bracketed form PostgREST's grammar uses twice — a group's member list
/// (<c>or=(a,b)</c>) and an <c>in</c> filter's candidate list (<c>in.(skoda,vw)</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The split is nesting-aware, and that is the whole reason this is not a
/// <see cref="string.Split(char[])"/> call.</b> A group member may itself be a group or an <c>in</c> list,
/// both of which carry commas of their own: <c>or=(color.eq.red,make.in.(skoda,vw))</c> has two members, not
/// three, and a flat split silently produces three malformed ones.
/// </para>
/// <para>
/// It scans once, iteratively, so an input with ten thousand brackets costs ten thousand character
/// comparisons rather than ten thousand stack frames — the depth cap that refuses such an input lives in
/// <c>FilterGroupParser</c>, and a splitter that overflowed on the way there would make that cap
/// unreachable.
/// </para>
/// </remarks>
internal static class ParenthesisedList
{
    /// <summary>
    /// Splits <paramref name="raw"/> — which must be a balanced, non-empty <c>(…)</c> — into its
    /// top-level, comma-separated members, stopping as soon as there are more than
    /// <paramref name="maxMembers"/> of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The maximum is taken rather than left to the caller to check afterwards, and that is the whole of
    /// why it is a parameter.</b> Both callers already refused an over-long list one line later — a group
    /// past <see cref="AlvoFilter.MaxTerms"/>, an <c>in</c> list past
    /// <see cref="AlvoFilter.MaxInCandidates"/> — but only after this method had allocated every member. A
    /// request line capped that at a few hundred; a request <em>body</em> does not, so the bound has to be
    /// spent while splitting, exactly as <c>FilterParseScope</c>'s node budget is spent while descending.
    /// </para>
    /// <para>
    /// The refusal is raised after a member is added and only when a separator proves another is coming, so
    /// exactly <paramref name="maxMembers"/> members split cleanly and the first one past it refuses.
    /// Written the other way round — refusing before the add — the trailing member the loop appends
    /// afterwards made the effective bound one higher, which no fact could have seen: the caller's own
    /// budget then produced the same code one line later.
    /// </para>
    /// <para>
    /// <b>Stopping early reorders one refusal, and the reorder is the truer answer.</b> A list that is both
    /// over-wide and unbalanced — three hundred members and a stray bracket at the end — used to be reported
    /// as malformed, because the whole scan ran before anything was counted; it is now reported as too wide,
    /// because the scan stops before it reaches the bracket. Both are the same 422, and a caller told to
    /// narrow a list they must narrow anyway is not told to fix the wrong thing.
    /// </para>
    /// </remarks>
    /// <param name="raw">The caller-supplied bracketed text.</param>
    /// <param name="maxMembers">The most members the caller will accept.</param>
    /// <param name="members">The members, in the order written; empty unless the outcome is <see cref="ListSplit.Ok"/>.</param>
    internal static ListSplit Split(string raw, int maxMembers, out IReadOnlyList<string> members)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMembers);

        members = [];
        if (raw.Length < 3 || raw[0] != '(' || raw[^1] != ')')
        {
            return ListSplit.Malformed;
        }

        var outcome = SplitTopLevel(raw[1..^1], maxMembers, out var split);
        if (outcome == ListSplit.Ok)
        {
            members = split!;
        }

        return outcome;
    }

    /// <summary>
    /// The members of an already-unwrapped list, or why it produced none — an unbalanced bracket is a
    /// refusal rather than a best-effort split, because <c>or=(a,b))</c> means nothing and answering it
    /// would be a guess.
    /// </summary>
    private static ListSplit SplitTopLevel(string inner, int maxMembers, out List<string>? members)
    {
        members = [];
        var depth = 0;
        var start = 0;

        for (var index = 0; index < inner.Length; index++)
        {
            var character = inner[index];
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth < 0)
            {
                members = null;
                return ListSplit.Malformed;
            }
            else if (character == ',' && depth == 0)
            {
                members.Add(inner[start..index]);
                start = index + 1;
                if (members.Count == maxMembers)
                {
                    members = null;
                    return ListSplit.TooMany;
                }
            }
        }

        if (depth != 0)
        {
            members = null;
            return ListSplit.Malformed;
        }

        members.Add(inner[start..]);
        return ListSplit.Ok;
    }
}
