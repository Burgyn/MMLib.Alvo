namespace MMLib.Alvo.Migrations;

/// <summary>
/// Formats the destructive steps of a <see cref="MigrationPlan"/> into an agent-readable
/// "what will happen" summary, one line per destructive step. Pure and I/O-free: callers
/// (<see cref="MigrationResult.EnsureApplied"/>, the core's migration runner, structured error
/// responses, logging) attach the result wherever a human or an agent needs to understand a
/// refused migration at a glance.
/// </summary>
/// <remarks>
/// It lives beside the model rather than in the core because <see cref="MigrationResult.EnsureApplied"/>
/// — the member every host calls to turn a refused plan into a failed start — needs the same summary,
/// and the core is downstream of this assembly. The core still reaches it: Abstractions grants
/// <c>InternalsVisibleTo</c> to <c>MMLib.Alvo</c>.
/// </remarks>
internal static class DestructiveChangeGuard
{
    private const string NoDestructiveChangesSummary = "No destructive changes.";

    /// <summary>Describes the destructive steps of <paramref name="plan"/>, one line each.</summary>
    /// <param name="plan">The plan to describe.</param>
    /// <returns>
    /// A newline-separated summary of the destructive steps, or <see cref="NoDestructiveChangesSummary"/>
    /// when the plan has none.
    /// </returns>
    public static string Describe(MigrationPlan plan)
    {
        var destructiveSteps = plan.Steps.Where(step => step.IsDestructive).ToList();
        return destructiveSteps.Count == 0
            ? NoDestructiveChangesSummary
            : string.Join(Environment.NewLine, destructiveSteps.Select(DescribeStep));
    }

    private static string DescribeStep(MigrationStep step)
    {
        var target = step.Change.Field is { } field
            ? $"{step.Change.Entity}.{field}"
            : step.Change.Entity;
        var reasonSuffix = step.Reason is { } reason ? $": {reason}" : string.Empty;
        return $"{step.Change.Kind} {target}{reasonSuffix}";
    }
}
