namespace MMLib.Alvo.Migrations;

/// <summary>
/// The atomic seam for a runtime schema change: applies a migration plan's DDL <em>and</em>
/// appends the resulting <see cref="DescriptorVersion"/> as one indivisible unit, so a lost
/// optimistic-lock race can never leave the schema changed but the change never recorded.
/// </summary>
/// <remarks>
/// This port exists because the core (and the code-first path) treat schema apply and version
/// append as two separate steps — <see cref="ISchemaMigrator.ApplyAsync"/> then
/// <see cref="IDescriptorVersionStore.AppendAsync"/> — which is correct for a single writer but
/// unsafe for independent clients changing the schema at runtime: a loser could apply its DDL and
/// only then be refused the append, leaving a schema mutation that no revision row explains. A
/// provider that can wrap both in one physical transaction implements this port to close that gap;
/// <see cref="IDescriptorVersionStore.AppendAsync"/> remains the non-atomic append used by the
/// code-first path and the contract tests.
///
/// <para>
/// <strong>The destructive guardrail is the caller's responsibility, not this port's.</strong> The
/// runtime schema service (which owns the plan-then-apply flow) refuses a destructive plan unless
/// destructive changes are explicitly allowed, exactly as <see cref="ISchemaMigrator.ApplyAsync"/>
/// does, <em>before</em> it ever calls this writer. By the time
/// <see cref="ApplyAndAppendAsync"/> runs, the plan is already cleared for execution: the writer
/// executes the plan's SQL unconditionally and does not re-evaluate
/// <see cref="MigrationOptions.AllowDestructive"/> or <see cref="MigrationOptions.DryRun"/>. Its one
/// job is atomicity, not policy.
/// </para>
/// </remarks>
public interface IRuntimeSchemaWriter
{
    /// <summary>
    /// Atomically executes <paramref name="plan"/>'s SQL and appends <paramref name="candidate"/> at
    /// revision <paramref name="expectedRevision"/> + 1, iff <paramref name="expectedRevision"/> is
    /// still the current revision. Both the DDL and the version-row insert commit together or not at
    /// all: if the append loses the optimistic-lock race, the whole transaction — including any DDL
    /// executed in it — is rolled back and <see cref="DescriptorConcurrencyException"/> is thrown, so
    /// a concurrent loser never commits a schema change.
    /// </summary>
    /// <param name="project">The project whose schema is changing.</param>
    /// <param name="plan">The migration plan to execute; its <see cref="MigrationPlan.Sql"/> is run verbatim.</param>
    /// <param name="candidate">
    /// The version to append. Its <see cref="DescriptorVersion.Revision"/> is ignored; the inserted
    /// row's revision is always <paramref name="expectedRevision"/> + 1.
    /// </param>
    /// <param name="expectedRevision">The revision the caller expects to currently be latest (0 for a fresh project).</param>
    /// <param name="options">
    /// The migration options. The destructive/dry-run guardrail is the caller's responsibility (see
    /// the type remarks); this writer executes and appends regardless.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended version, with its assigned <see cref="DescriptorVersion.Revision"/>.</returns>
    /// <exception cref="DescriptorConcurrencyException">
    /// <paramref name="expectedRevision"/> no longer matches the store's current revision; both the
    /// DDL and the append have been rolled back.
    /// </exception>
    Task<DescriptorVersion> ApplyAndAppendAsync(
        string project, MigrationPlan plan, DescriptorVersion candidate,
        int expectedRevision, MigrationOptions options, CancellationToken ct = default);
}
