namespace MMLib.Alvo.Migrations.Internal;

/// <summary>What stage 2 of the boot decided to do about the project schema.</summary>
internal enum SchemaStartupOutcome
{
    /// <summary>
    /// Change nothing and serve: the plan is empty, or the mode is <see cref="AlvoSchemaStartupMode.Skip"/> over
    /// a schema Alvo has recorded and the drift is somebody else's business.
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

    /// <summary>
    /// Serve nothing and report not ready, without stopping the process: this descriptor is older than the one
    /// the database is on, so applying it would rewrite a newer schema with an older one (#145).
    /// </summary>
    /// <remarks>
    /// <b>Distinct from <see cref="Refuse"/> because the two failures are different in kind, and the right
    /// response to them differs.</b> A destructive or <see cref="AlvoSchemaStartupMode.Verify"/> refusal is an
    /// authoring or configuration error that only a human changes, so failing the start loudly — and exiting
    /// 78 — is the fastest feedback. An out-of-order boot is a <em>position in a deployment</em>: the pod is
    /// not misconfigured, it is behind, which is exactly what a readiness probe expresses. Standing down
    /// publishes <see cref="AlvoBootPhase.Failed"/> and lets an orchestrator drain the pod instead of
    /// restart-looping a container no restart can fix — the same reasoning as the startup design's deviation
    /// 65. Nothing is primed, so the process can answer 403 and 404 and nothing else.
    /// </remarks>
    StandDown,
}

/// <summary>Stage 2's verdict: what to do, the plan to do it with, and why not, if the answer is "not".</summary>
/// <param name="Outcome">What the boot should do next.</param>
/// <param name="Plan">
/// The plan the verdict was reached about — carried so the caller applies exactly what was judged rather than
/// re-planning against a database that may have moved underneath it.
/// </param>
/// <param name="Refusal">
/// The operator-readable refusal, naming the steps and the setting that would allow them. Non-<c>null</c> if
/// and only if <paramref name="Outcome"/> is <see cref="SchemaStartupOutcome.Refuse"/> or
/// <see cref="SchemaStartupOutcome.StandDown"/>.
/// </param>
/// <param name="Fix">
/// The actionable lines of <paramref name="Refusal"/> on their own — what
/// <see cref="AlvoStartupRefusedException.FixSuggestion"/> publishes, so a caller that presents failures
/// structurally reaches the fix without parsing the refusal's prose. Non-<c>null</c> on exactly the same
/// condition as <paramref name="Refusal"/>.
/// </param>
internal readonly record struct SchemaStartupDecision(
    SchemaStartupOutcome Outcome, MigrationPlan Plan, string? Refusal, string? Fix = null);

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
/// <b><see cref="AlvoSchemaStartupMode.Skip"/> ignores drift, but it may not report a schema nothing
/// verified.</b> Skip's contract is "never touch the project schema", and reading plus diffing touches
/// nothing — so when Alvo has recorded no snapshot <em>and</em> the live schema does not match the descriptor,
/// the boot is refused rather than primed. That is the one Skip state in which nothing at all has confirmed
/// the schema exists: the migration job that owns it has not run, and priming anyway would publish
/// <see cref="AlvoBootPhase.Ready"/> — routing traffic to a process whose every request fails at the database,
/// which is precisely what readiness was added to prevent. An <em>adopted</em> database whose live schema
/// already matches the descriptor produces an empty plan and still serves under Skip, so a host whose schema
/// is genuinely somebody else's business is unaffected.
/// </para>
/// <para>
/// <b>The ordering verdict is an <em>input</em>, not something this decides.</b> Answering "is my descriptor
/// older than the one the database is on?" needs the append-only history, i.e. a store call — so
/// <see cref="DescriptorHistoryOrder"/> answers it and the answer is passed in. That keeps the whole decision
/// table a unit test, which is the property the rest of this type exists to preserve.
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
    /// <param name="outOfOrder">
    /// Why this descriptor is older than the one the database is on, or <see langword="null"/> when it is not —
    /// see <see cref="DescriptorHistoryOrder"/>. <see langword="null"/> is also what a caller that has not
    /// asked passes, which is deliberate: the ordering gate protects the <em>apply</em>, and a caller with
    /// nothing to apply is not required to pay an O(N) history read to be told so.
    /// </param>
    /// <returns>The verdict, carrying <paramref name="plan"/> and a refusal when the answer is no.</returns>
    internal static SchemaStartupDecision Decide(
        AppliedSchema? applied,
        MigrationPlan plan,
        AlvoSchemaOptions options,
        OutOfOrderBoot? outOfOrder = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        if (plan.IsEmpty)
        {
            return Unchanged(plan);
        }

        if (options.Startup is AlvoSchemaStartupMode.Skip)
        {
            return applied is null ? RefusedForAnUnverifiableSkip(plan) : Unchanged(plan);
        }

        if (outOfOrder is not null)
        {
            return StoodDown(plan, outOfOrder);
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

    private static SchemaStartupDecision Unchanged(MigrationPlan plan)
        => new(SchemaStartupOutcome.Unchanged, plan, Refusal: null);

    /// <summary>
    /// The verdict for a descriptor the database has already moved on from: do not apply, do not serve, and do
    /// not stop the process.
    /// </summary>
    /// <remarks>
    /// <b>Decided before the destructive gate, which narrows the startup design's deviation 57 without
    /// weakening it.</b> Both gates refuse the same boot — the plan back from a newer schema is a drop — so
    /// nothing becomes appliable that was not; what changes is which of the two true things the operator is
    /// told. "You are running an older descriptor than the database (revision 1 versus revision 2)" is the
    /// diagnosis for a rollback crash-loop that "destructive change refused" has been failing to give, and it
    /// is the sole reason the ordering gate is worth having ahead of a gate that would refuse anyway.
    /// </remarks>
    /// <param name="plan">The plan that would have run.</param>
    /// <param name="outOfOrder">Why this descriptor is older than the applied one.</param>
    private static SchemaStartupDecision StoodDown(MigrationPlan plan, OutOfOrderBoot outOfOrder)
    {
        var fix = string.Join(Environment.NewLine, outOfOrder.Fixes);

        return new SchemaStartupDecision(
            SchemaStartupOutcome.StandDown, plan, BuildRefusal(outOfOrder.Headline, plan, fix), fix);
    }

    private static SchemaStartupDecision Refused(
        AppliedSchema? applied, MigrationPlan plan, AlvoSchemaOptions options)
        => Refused(plan, Headline(applied), Fixes(plan, options));

    /// <summary>
    /// The refusal for the one <see cref="AlvoSchemaStartupMode.Skip"/> state nothing has verified: no recorded
    /// snapshot, and a live schema that does not match the descriptor.
    /// </summary>
    /// <param name="plan">The plan that would bring the live schema to the descriptor — the steps that are missing.</param>
    private static SchemaStartupDecision RefusedForAnUnverifiableSkip(MigrationPlan plan)
        => Refused(plan, UnverifiableSkipHeadline, UnverifiableSkipFixes);

    private static SchemaStartupDecision Refused(
        MigrationPlan plan, string headline, IEnumerable<string> fixes)
    {
        var fix = string.Join(Environment.NewLine, fixes);

        return new SchemaStartupDecision(
            SchemaStartupOutcome.Refuse, plan, BuildRefusal(headline, plan, fix), fix);
    }

    private static string BuildRefusal(string headline, MigrationPlan plan, string fix)
        => string.Join(
            Environment.NewLine,
            [
                headline,
                string.Empty,
                Indent(DestructiveChangeGuard.DescribeAllSteps(plan)),
                string.Empty,
                fix,
            ]);

    private static string UnverifiableSkipHeadline =>
        $"Alvo cannot start: {StartupSkipSetting} is set, but Alvo has recorded no schema for this database and "
        + "the live schema does not match the descriptor. Under Skip nothing else checks it, so reporting this "
        + "process ready would route traffic to a backend whose every request fails at the database.";

    /// <summary>
    /// The two ways out of an unverifiable <see cref="AlvoSchemaStartupMode.Skip"/>: let whoever owns the schema
    /// bring it up, or stop skipping.
    /// </summary>
    private static IEnumerable<string> UnverifiableSkipFixes
    {
        get
        {
            yield return "  Apply this descriptor with the migration job that owns the schema, then restart.";
            yield return $"  Or set {StartupVerifySetting} to have the boot check the live schema itself, or "
                + $"{StartupApplySetting} to bring it up.";
        }
    }

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

    private static string StartupVerifySetting =>
        $"{AlvoSchemaOptions.StartupEnvironmentVariable}={nameof(AlvoSchemaStartupMode.Verify)}";

    private static string StartupSkipSetting =>
        $"{AlvoSchemaOptions.StartupEnvironmentVariable}={nameof(AlvoSchemaStartupMode.Skip)}";

    private static string Indent(string block) => string.Join(
        Environment.NewLine,
        block.Split(Environment.NewLine).Select(line => $"  {line}"));
}
