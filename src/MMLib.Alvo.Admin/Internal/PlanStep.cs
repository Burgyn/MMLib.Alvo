namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// One step of a migration plan as a sentence: a verb, the table or column it touches, and what it costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the dashboard reads the planner's line rather than showing it.</b> The plan arrives as the
/// framework's own summary — <c>DropField customers.notes: Drops the column and all its data.  &lt;- destructive</c>
/// — the line a refused boot prints to a log. Its op name is a C# enum member and its arrow is an ASCII
/// marker for a terminal; on the Preview screen the badge beside the step already says it destroys data.
/// So the line is parsed back into its parts and said as a sentence, and nothing is lost: the target is
/// the planner's, the destructive flag is the planner's marker, and a step this parser does not recognise
/// is shown exactly as it arrived, minus the marker.
/// </para>
/// <para>
/// <b>A deliberate deviation, recorded.</b> <c>ManagementPlanSummary.Steps</c> asks clients not to reword a
/// step, because a second wording is a second spelling of one truth. The design pass (§4.9, "planner steps
/// render as sentences") overrides that for this screen only: what is reworded is the verb, an enum member
/// no operator reads; what the step touches and whether it destroys data are carried through unchanged, and
/// the CLI, the API and a refused boot keep the framework's own line.
/// </para>
/// <para>
/// <b>The line's grammar is <c>DestructiveChangeGuard.DescribeStep</c>'s:</b>
/// <c>{Kind} {entity}[.{field}][: {reason}][  &lt;- destructive]</c>. A reason is present only on a
/// destructive step, and for a drop it repeats what the verb already says, so only an alteration's reason
/// — the one that names <em>how</em> it can lose data — is carried into the sentence.
/// </para>
/// </remarks>
/// <param name="Verb">What the step does, e.g. <c>Add column</c>.</param>
/// <param name="Target">The table or <c>table.column</c> it does it to; empty for an unrecognised step.</param>
/// <param name="Consequence">What it costs, when it costs anything.</param>
/// <param name="Destructive">Whether the planner marked the step as discarding data.</param>
internal sealed record PlanStep(string Verb, string Target, string? Consequence, bool Destructive)
{
    private const string DestructiveMarker = "<- destructive";

    /// <summary>Reads one line of <c>ManagementPlanSummary.Steps</c>.</summary>
    /// <param name="line">The planner's line, verbatim.</param>
    public static PlanStep Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var destructive = line.TrimEnd().EndsWith(DestructiveMarker, StringComparison.Ordinal);
        var body = destructive
            ? line.TrimEnd()[..^DestructiveMarker.Length].TrimEnd()
            : line.Trim();

        var (head, reason) = SplitReason(body);
        var space = head.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0)
        {
            return new PlanStep(body, string.Empty, null, destructive);
        }

        var kind = head[..space];
        var target = head[(space + 1)..].Trim();
        return Describe(kind, target, reason, destructive) ?? new PlanStep(body, string.Empty, null, destructive);
    }

    /// <summary>
    /// A one-line reason for a plan that makes exactly one change, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Offered as the placeholder of Preview's "Why" box, so an operator starts from a sentence about
    /// <em>their</em> change instead of an unrelated example. Only for one step: a summary of five is a list,
    /// and a reason is what the list cannot say.
    /// </remarks>
    /// <param name="lines">The plan's steps, verbatim.</param>
    public static string? SuggestReason(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count == 1 ? Parse(lines[0]).AsReason() : null;
    }

    private string? AsReason()
    {
        var dot = Target.IndexOf('.', StringComparison.Ordinal);
        var (entity, field) = dot > 0 ? (Target[..dot], Target[(dot + 1)..]) : (Target, string.Empty);

        return Verb switch
        {
            "Create table" => $"Add {entity}",
            "Drop table" => $"Remove {entity}",
            "Add column" when field.Length > 0 => $"Add {field} to {entity}",
            "Drop column" when field.Length > 0 => $"Remove {field} from {entity}",
            _ => null,
        };
    }

    private static PlanStep? Describe(string kind, string target, string? reason, bool destructive) => kind switch
    {
        "CreateEntity" => new("Create table", target, null, destructive),
        "DropEntity" => new("Drop table", target, "destroys its rows", destructive),
        "RenameEntity" => new("Rename a table to", target, null, destructive),
        "AddField" => new("Add column", target, null, destructive),
        "DropField" => new("Drop column", target, "destroys its data", destructive),
        "RenameField" => new("Rename a column to", target, null, destructive),
        "AlterField" => new(target.Contains('.', StringComparison.Ordinal) ? "Change column" : "Change table",
            target, Sentence(reason), destructive),
        "AddIndex" => new("Add an index on", target, null, destructive),
        "DropIndex" => new("Drop an index on", target, null, destructive),
        "RenameIndex" => new("Rename an index on", target, null, destructive),
        _ => null,
    };

    /// <summary>
    /// Splits <c>head: reason</c> on the first separator after the target, so a reason's own colons stay in it.
    /// </summary>
    private static (string Head, string? Reason) SplitReason(string body)
    {
        var colon = body.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? (body, null) : (body[..colon], body[(colon + 2)..].Trim());
    }

    /// <summary>The planner's reason as the second half of a sentence: lower-cased first letter, no full stop.</summary>
    private static string? Sentence(string? reason)
    {
        if (reason is not { Length: > 0 })
        {
            return null;
        }

        var trimmed = reason.TrimEnd('.');
        return string.Concat(char.ToLowerInvariant(trimmed[0]).ToString(), trimmed[1..]);
    }
}
