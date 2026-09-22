namespace MMLib.Alvo.Admin.Internal;

/// <summary>What happened to one line between two documents.</summary>
internal enum LineChange
{
    /// <summary>Present in both, unchanged.</summary>
    Same,

    /// <summary>Only in the new document.</summary>
    Added,

    /// <summary>Only in the old one.</summary>
    Removed,
}

/// <summary>
/// One rendered line of a diff: what happened to it, its text, and the line number it carries in
/// whichever document still has it.
/// </summary>
/// <param name="Change">What happened to it.</param>
/// <param name="Text">The line itself, without its newline.</param>
/// <param name="Number">Its number in the new document, or in the old one for a removal.</param>
internal sealed record DiffLine(LineChange Change, string Text, int Number);

/// <summary>
/// A line diff between two versions of the same document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Design §4.6 binds the editor to showing the document it produces
/// with the changed lines marked, and §6.3's third criterion — everything clickable is exportable
/// as code — is only believable if the operator can see what is about to be applied. Without it the
/// preview offers an Apply button over a plan that says "no migration", which is true and tells
/// nobody what changed.
/// </para>
/// <para>
/// <b>Lines, not a JSON tree walk.</b> Both sides come out of the same writer with the same
/// indentation, so a line is a stable unit; a structural differ would be a second model of the
/// descriptor to keep in step with the first, which is the drift <c>DescriptorLens</c> exists to
/// avoid. The cost is that a re-indentation would read as a whole-file change — and a
/// re-indentation <em>is</em> a whole-file change to the artifact a person commits.
/// </para>
/// </remarks>
internal static class LineDiff
{
    /// <summary>How many unchanged lines are kept either side of a change.</summary>
    private const int Context = 3;

    /// <summary>The marker line that stands in for the stretches nobody needs to read.</summary>
    internal const string Elision = "⋯";

    /// <summary>
    /// The changed lines of <paramref name="after"/> against <paramref name="before"/>, with a few
    /// lines of context around each change and the untouched stretches elided.
    /// </summary>
    /// <param name="before">The document as it stands.</param>
    /// <param name="after">The document as it would stand.</param>
    /// <returns>The lines to render, in order; empty when the two are identical.</returns>
    public static IReadOnlyList<DiffLine> Between(string before, string after)
    {
        var old = Split(before);
        var updated = Split(after);
        var full = Walk(old, updated, LongestCommonSubsequence(old, updated));

        return full.Any(line => line.Change is not LineChange.Same) ? Elide(full) : [];
    }

    private static string[] Split(string document)
        => document.ReplaceLineEndings("\n").Split('\n');

    /// <summary>
    /// The classic length table: <c>[i, j]</c> is how many lines the two documents share from
    /// <c>i</c> and <c>j</c> onwards.
    /// </summary>
    private static int[,] LongestCommonSubsequence(string[] old, string[] updated)
    {
        var lengths = new int[old.Length + 1, updated.Length + 1];

        for (var left = old.Length - 1; left >= 0; left -= 1)
        {
            for (var right = updated.Length - 1; right >= 0; right -= 1)
            {
                lengths[left, right] = string.Equals(old[left], updated[right], StringComparison.Ordinal)
                    ? lengths[left + 1, right + 1] + 1
                    : Math.Max(lengths[left + 1, right], lengths[left, right + 1]);
            }
        }

        return lengths;
    }

    private static List<DiffLine> Walk(string[] old, string[] updated, int[,] lengths)
    {
        var lines = new List<DiffLine>();
        int left = 0, right = 0;

        while (left < old.Length && right < updated.Length)
        {
            if (string.Equals(old[left], updated[right], StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(LineChange.Same, updated[right], right + 1));
                left += 1;
                right += 1;
            }
            else if (lengths[left + 1, right] >= lengths[left, right + 1])
            {
                lines.Add(new DiffLine(LineChange.Removed, old[left], left + 1));
                left += 1;
            }
            else
            {
                lines.Add(new DiffLine(LineChange.Added, updated[right], right + 1));
                right += 1;
            }
        }

        AppendRemainder(lines, old, left, LineChange.Removed);
        AppendRemainder(lines, updated, right, LineChange.Added);

        return lines;
    }

    private static void AppendRemainder(List<DiffLine> lines, string[] document, int from, LineChange change)
    {
        for (var index = from; index < document.Length; index += 1)
        {
            lines.Add(new DiffLine(change, document[index], index + 1));
        }
    }

    /// <summary>
    /// Keeps the changes and <see cref="Context"/> lines around each of them, replacing every other
    /// stretch with one <see cref="Elision"/> line.
    /// </summary>
    private static List<DiffLine> Elide(List<DiffLine> lines)
    {
        var keep = NearAChange(lines);
        var elided = new List<DiffLine>();
        var eliding = false;

        for (var index = 0; index < lines.Count; index += 1)
        {
            if (keep[index])
            {
                elided.Add(lines[index]);
                eliding = false;
                continue;
            }

            if (!eliding)
            {
                elided.Add(new DiffLine(LineChange.Same, Elision, 0));
                eliding = true;
            }
        }

        return elided;
    }

    private static bool[] NearAChange(List<DiffLine> lines)
    {
        var keep = new bool[lines.Count];

        for (var index = 0; index < lines.Count; index += 1)
        {
            if (lines[index].Change is LineChange.Same)
            {
                continue;
            }

            var from = Math.Max(0, index - Context);
            var to = Math.Min(lines.Count - 1, index + Context);
            for (var near = from; near <= to; near += 1)
            {
                keep[near] = true;
            }
        }

        return keep;
    }
}
