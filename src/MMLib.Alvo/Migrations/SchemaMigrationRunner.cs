using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Migrations.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// Orchestrates a code-first schema migration: load the descriptor, resolve the desired schema,
/// diff it against the currently applied (or introspected, on first run) schema, guard against
/// unapproved destructive changes, apply, and persist the new snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Invoked by the code-first builder (<c>FromDescriptor()</c>) and, later, a Management-API
/// migration endpoint — both compose the same ports rather than duplicating this flow. Every
/// branch that accepts the descriptor as authoritative for the schema actually in the database (an
/// empty plan, or a genuinely applied one) also (re)primes <see cref="IPolicyCatalogProvider"/>
/// from the same parsed descriptor, so <c>IPolicyEngine</c> is never left serving a stale or
/// never-primed catalog after a successful run.
/// </para>
/// <para>
/// Loading, validating, mapping and compiling are <see cref="DescriptorBootPlan"/>'s — the boot's stage
/// 0, which touches no database and which the boot service runs on every start. This runner is what
/// happens <em>after</em> it: read the applied snapshot, plan, guard, apply, save. The catalog therefore
/// arrives already compiled, which keeps the property the two priming branches were built around —
/// an uncompilable rule set rejects the run before <see cref="ISchemaMigrator.ApplyAsync"/> makes
/// anything durable — and strengthens it, because the compilation now precedes even the store read.
/// Publishing is still per branch: the empty plan (nothing to durably change) publishes at once, and
/// the genuinely-applied branch publishes via <see cref="IPolicyCatalogProvider.SetCurrent"/> only
/// after the applied snapshot is saved.
/// </para>
/// <para>
/// Guarded like every apply: a descriptor that would unbind a handler the current catalog binds is refused
/// (<see cref="Descriptor.DescriptorValidationException"/>) before anything is planned. A handler that never bound
/// stays unbound, logged, with its events recorded dead.
/// </para>
/// </remarks>
internal sealed class SchemaMigrationRunner
{
    private readonly DescriptorBootPlan _bootPlan;
    private readonly ISchemaMigrator _migrator;
    private readonly ISchemaIntrospector _introspector;
    private readonly IAppliedSchemaStore _store;
    private readonly IPolicyCatalogProvider _policyCatalogProvider;
    private readonly ExtensionApplyGuard? _extensions;
    private readonly DescriptorActionSweep? _sweep;

    public SchemaMigrationRunner(
        DescriptorBootPlan bootPlan,
        ISchemaMigrator migrator,
        ISchemaIntrospector introspector,
        IAppliedSchemaStore store,
        IPolicyCatalogProvider policyCatalogProvider,
        ExtensionApplyGuard? extensions = null,
        DescriptorActionSweep? sweep = null)
    {
        ArgumentNullException.ThrowIfNull(bootPlan);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(introspector);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policyCatalogProvider);

        _bootPlan = bootPlan;
        _migrator = migrator;
        _introspector = introspector;
        _store = store;
        _policyCatalogProvider = policyCatalogProvider;
        _extensions = extensions;
        _sweep = sweep;
    }

    /// <summary>Runs the code-first migration flow described in the type's remarks.</summary>
    /// <param name="options">Migration options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The migration result. An empty plan (the applied/introspected schema already matches the
    /// descriptor) is a true no-op: it returns un-applied without touching
    /// <see cref="ISchemaMigrator.ApplyAsync"/> or <see cref="IAppliedSchemaStore.SaveAsync"/>, so
    /// the applied snapshot and its revision are left untouched. When the plan contains destructive
    /// changes and <see cref="MigrationOptions.AllowDestructive"/> is <see langword="false"/>, or
    /// when <see cref="MigrationOptions.DryRun"/> is <see langword="true"/>, the plan is likewise
    /// returned un-applied (<c>Applied == false</c>) — inspect <c>Plan.Steps</c>, or pass the plan
    /// to <see cref="DestructiveChangeGuard.Describe"/>, for a readable summary of what was refused.
    /// A caller that needs the descriptor to be <em>serving</em> rather than merely planned calls
    /// <see cref="MigrationResult.EnsureApplied"/> on the result, which turns the destructive refusal —
    /// and only that one, not the empty plan or the dry run — into a throw.
    /// </returns>
    public async Task<MigrationResult> RunAsync(MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (descriptor, desired, descriptorJson, catalog) = await _bootPlan.LoadAsync(ct).ConfigureAwait(false);
        _extensions?.Check(catalog);

        var appliedSnapshot = await _store.GetCurrentAsync(descriptor.Name, ct).ConfigureAwait(false);
        var current = appliedSnapshot?.Schema
            ?? await _introspector.IntrospectAsync(ct).ConfigureAwait(false);

        RollupValidationOrderCheck.ThrowIfAny(current, desired);
        var plan = await _migrator.PlanAsync(current, desired, options, ct).ConfigureAwait(false);

        if (plan.IsEmpty)
        {
            var stored = appliedSnapshot is not null && DescriptorContent.IsSame(descriptor, appliedSnapshot.DescriptorJson);
            await ServeAsync(descriptor.Name, catalog, appliedSnapshot?.Revision, sweep: stored).ConfigureAwait(false);
            return new MigrationResult(Applied: false, plan, WasDryRun: options.DryRun);
        }

        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            return new MigrationResult(Applied: false, plan, WasDryRun: options.DryRun);
        }

        if (options.DryRun)
        {
            return new MigrationResult(Applied: false, plan, WasDryRun: true);
        }

        var result = await _migrator.ApplyAsync(plan, options, ct).ConfigureAwait(false);

        if (result.Applied)
        {
            var revision = (appliedSnapshot?.Revision ?? 0) + 1;
            var snapshot = new AppliedSchema(desired, descriptorJson, revision, DateTimeOffset.UtcNow);
            await _store.SaveAsync(descriptor.Name, snapshot, ct).ConfigureAwait(false);
            await ServeAsync(descriptor.Name, catalog, revision, sweep: true).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Publishes <paramref name="catalog"/>, then — when it is the stored revision — ends the work of the descriptor actions it
    /// no longer binds (issue 480): after the snapshot is saved, and not cancelled with the call, as
    /// <see cref="RuntimeSchemaService"/> does.
    /// </summary>
    /// <remarks>
    /// An empty plan saves nothing, so a descriptor that differs from the stored one only in hooks or rules is served by
    /// this process alone; the sweep runs only against the database's own revision (<paramref name="sweep"/>), never a
    /// process-local catalog another process sharing the database does not serve.
    /// </remarks>
    private async Task ServeAsync(string project, PolicyCatalog catalog, int? revision, bool sweep)
    {
        _policyCatalogProvider.SetCurrent(project, catalog);
        if (sweep && _sweep is not null)
        {
            await _sweep.SweepAsync(catalog, revision, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
