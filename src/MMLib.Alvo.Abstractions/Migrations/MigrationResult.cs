namespace MMLib.Alvo.Migrations;

/// <summary>The result of applying a migration plan.</summary>
/// <param name="Applied">Whether the migration was applied successfully.</param>
/// <param name="Plan">The plan that was applied.</param>
/// <param name="WasDryRun">Whether this was a dry run.</param>
/// <remarks>
/// A refusal is a <em>return value</em> here, not an exception — a caller that asked for a plan wants
/// to read it. A caller that asked for a running backend wants the opposite, and calls
/// <see cref="EnsureApplied"/>.
/// </remarks>
public sealed record MigrationResult(bool Applied, MigrationPlan Plan, bool WasDryRun)
{
    /// <summary>
    /// Throws unless this result leaves the database matching the descriptor, so a host cannot start
    /// serving nothing on a plan that was refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>Applied == false</c> is not the same as "refused".</b> Two legitimate outcomes report it and
    /// both pass here: an <em>empty</em> plan — the ordinary restart, where the applied schema already
    /// matches the descriptor and nothing needs doing — and a <em>dry run</em>, where the caller asked for
    /// a plan rather than an apply. What does not pass is a non-empty plan that was neither applied nor
    /// asked to be: that is a refusal, and every route generated from the descriptor is missing after it.
    /// </para>
    /// <para>
    /// Named after <c>HttpResponseMessage.EnsureSuccessStatusCode</c> and returns <see langword="this"/>
    /// for the same reason: the guard reads as one link in the call that produced the result.
    /// </para>
    /// </remarks>
    /// <returns>This same result, so the call can be chained onto the apply that produced it.</returns>
    /// <exception cref="DestructiveChangeNotAllowedException">
    /// The plan was refused because it is destructive and <see cref="MigrationOptions.AllowDestructive"/>
    /// was <see langword="false"/>. The message names every destructive step.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The plan was neither applied, empty, nor a dry run, and carries no destructive step to explain it —
    /// an <see cref="ISchemaMigrator"/> reporting an apply it did not perform.
    /// </exception>
    public MigrationResult EnsureApplied() =>
        Applied || Plan.IsEmpty || WasDryRun ? this : throw Refused();

    private Exception Refused() =>
        Plan.HasDestructiveChanges
            ? new DestructiveChangeNotAllowedException(DestructiveRefusalMessage())
            : new InvalidOperationException(UnexplainedRefusalMessage);

    private string DestructiveRefusalMessage() =>
        "The migration plan was refused because it is destructive and AllowDestructive is false, so the "
        + "schema no longer matches the descriptor and nothing generated from it can serve. The steps "
        + $"that were refused:{Environment.NewLine}{DestructiveChangeGuard.Describe(Plan)}{Environment.NewLine}"
        + "Put back what the descriptor removed, or take a backup and re-apply with AllowDestructive = true.";

    private const string UnexplainedRefusalMessage =
        "The migration plan was not applied, and it is neither empty nor a dry run, so the schema does not "
        + "match the descriptor and nothing generated from it can serve. The ISchemaMigrator in use reported "
        + "an apply it did not perform; inspect Plan.Steps.";
}
