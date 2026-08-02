namespace MMLib.Alvo.Api.Internal;

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
    /// top-level, comma-separated members.
    /// </summary>
    /// <param name="raw">The caller-supplied bracketed text.</param>
    /// <param name="members">The members, in the order written.</param>
    internal static bool TrySplit(string raw, out IReadOnlyList<string> members)
    {
        ArgumentNullException.ThrowIfNull(raw);

        members = [];
        if (raw.Length < 3 || raw[0] != '(' || raw[^1] != ')')
        {
            return false;
        }

        if (SplitTopLevel(raw[1..^1]) is not { } split)
        {
            return false;
        }

        members = split;
        return true;
    }

    /// <summary>
    /// The members of an already-unwrapped list, or <see langword="null"/> when its brackets do not balance —
    /// which is a refusal rather than a best-effort split, because <c>or=(a,b))</c> means nothing and
    /// answering it would be a guess.
    /// </summary>
    private static List<string>? SplitTopLevel(string inner)
    {
        var members = new List<string>();
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
                return null;
            }
            else if (character == ',' && depth == 0)
            {
                members.Add(inner[start..index]);
                start = index + 1;
            }
        }

        if (depth != 0)
        {
            return null;
        }

        members.Add(inner[start..]);
        return members;
    }
}
