namespace MMLib.Alvo.Migrations.Internal;

/// <summary>What stage 2 of the boot decided to do about the project schema.</summary>
internal enum SchemaStartupOutcome
{
    /// <summary>
    /// Change nothing and serve: the applied snapshot already matches the descriptor, or the mode is
    /// <see cref="AlvoSchemaStartupMode.Skip"/> and the schema is somebody else's business.
    /// </summary>
    Unchanged,

    /// <summary>
    /// Create the schema, because no applied snapshot exists yet. Allowed in every mode except
    /// <see cref="AlvoSchemaStartupMode.Skip"/>.
    /// </summary>
    Initialize,

    /// <summary>Apply the drift, because the mode asked for it and the plan discards nothing.</summary>
    Apply,

    /// <summary>Refuse to start, printing <see cref="SchemaStartupDecision.Refusal"/>.</summary>
    Refuse,
}

/// <summary>Stage 2's verdict: what to do, the plan to do it with, and why not, if the answer is "not".</summary>
/// <param name="Outcome">What the boot should do next.</param>
/// <param name="Plan">
/// The plan the verdict was reached about — carried so the caller applies exactly what was judged rather than
/// re-planning against a database that may have moved underneath it.
/// </param>
/// <param name="Refusal">
/// The operator-readable refusal, naming the steps and the setting that would allow them. Non-<c>null</c> if
/// and only if <paramref name="Outcome"/> is <see cref="SchemaStartupOutcome.Refuse"/>.
/// </param>
internal readonly record struct SchemaStartupDecision(
    SchemaStartupOutcome Outcome, MigrationPlan Plan, string? Refusal);

/// <summary>
/// Stage 2 of the boot sequence, as a pure function: given the snapshot the database reports, the plan that
/// would bring it to the descriptor, and the configured mode, decide whether the process may start and what
/// it may change on the way.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mode governs drift, not initialization.</b> The hazard the sources and EF Core's own guidance warn
/// about is <em>migrating a database that already holds a schema</em>: replicas race for the DDL, the runtime
/// needs rights it should not have in production, and a bad migration does not roll back. Creating a schema
/// from nothing is a different act with a different risk profile. Conflating the two is what forces the choice
/// between "unsafe in production" and "broken zero-config dev" — so an <em>uninitialized</em> database is
/// initialized in every mode except <see cref="AlvoSchemaStartupMode.Skip"/>, which is what keeps
/// <c>AddAlvo()</c> plus <c>dotnet run</c>, and a bare <c>docker run</c> reaching a working backend inside a
/// minute, true with no configuration at all. Only drift consults the mode.
/// </para>
/// <para>
/// <b><see cref="AlvoSchemaStartupMode.Apply"/> does not weaken the destructive guardrail.</b> A plan that
/// discards data is refused unless <see cref="AlvoSchemaOptions.AllowDestructive"/> is set, whichever mode
/// asked for it, and that includes initialization: an absent snapshot means Alvo has not recorded a schema for
/// this project, not that the database is empty, so the plan is diffed against what was introspected and can
/// legitimately contain drops. That check is the line between "apply on boot" and "lose data on boot".
/// </para>
/// <para>
/// Being pure — no store, no migrator, no clock — is the point: the whole decision table is a unit test, and
/// the boot service is left with nothing to decide, only to carry out.
/// </para>
/// </remarks>
internal static class SchemaStartupPolicy
{
    /// <summary>Decides what the boot does about the project schema.</summary>
    /// <param name="applied">The snapshot the store reports, or <c>null</c> when it has none.</param>
    /// <param name="plan">The plan from the current (or introspected) schema to the descriptor's.</param>
    /// <param name="options">The configured startup mode and destructive allowance.</param>
    /// <returns>The verdict, carrying <paramref name="plan"/> and a refusal when the answer is no.</returns>
    internal static SchemaStartupDecision Decide(
        AppliedSchema? applied, MigrationPlan plan, AlvoSchemaOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Startup is AlvoSchemaStartupMode.Skip || plan.IsEmpty)
        {
            return new SchemaStartupDecision(SchemaStartupOutcome.Unchanged, plan, Refusal: null);
        }

        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            return Refused(applied, plan, options);
        }

        if (applied is null)
        {
            return new SchemaStartupDecision(SchemaStartupOutcome.Initialize, plan, Refusal: null);
        }

        return options.Startup is AlvoSchemaStartupMode.Apply
            ? new SchemaStartupDecision(SchemaStartupOutcome.Apply, plan, Refusal: null)
            : Refused(applied, plan, options);
    }

    private static SchemaStartupDecision Refused(
        AppliedSchema? applied, MigrationPlan plan, AlvoSchemaOptions options)
        => new(SchemaStartupOutcome.Refuse, plan, BuildRefusal(applied, plan, options));

    private static string BuildRefusal(AppliedSchema? applied, MigrationPlan plan, AlvoSchemaOptions options)
        => string.Join(
            Environment.NewLine,
            [
                Headline(applied),
                string.Empty,
                Indent(DestructiveChangeGuard.DescribeAllSteps(plan)),
                string.Empty,
                .. Fixes(plan, options),
            ]);

    private static string Headline(AppliedSchema? applied) => applied is null
        ? "Alvo cannot start: initializing this database from the descriptor would discard data it already "
            + "holds."
        : "Alvo cannot start: the descriptor does not match the schema applied to this database (revision "
            + $"{applied.Revision}).";

    /// <summary>
    /// The fix lines, one per gate the boot is actually held by, so an operator who clears them all starts
    /// rather than meeting a second refusal they were never told about.
    /// </summary>
    private static IEnumerable<string> Fixes(MigrationPlan plan, AlvoSchemaOptions options)
    {
        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            yield return "  These steps are destructive: applying them discards data the descriptor no "
                + "longer declares.";
            yield return $"  Recover what you need first, then set {AllowDestructiveSetting} to allow them "
                + "on boot.";
        }

        if (options.Startup is not AlvoSchemaStartupMode.Apply)
        {
            yield return $"  Apply it with a migration job, or set {StartupApplySetting} to apply it on boot.";
        }
    }

    private static string AllowDestructiveSetting =>
        $"{AlvoSchemaOptions.AllowDestructiveEnvironmentVariable}=true";

    private static string StartupApplySetting =>
        $"{AlvoSchemaOptions.StartupEnvironmentVariable}={nameof(AlvoSchemaStartupMode.Apply)}";

    private static string Indent(string block) => string.Join(
        Environment.NewLine,
        block.Split(Environment.NewLine).Select(line => $"  {line}"));
}
