using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// Orchestrates a code-first schema migration: load the descriptor, resolve the desired schema,
/// diff it against the currently applied (or introspected, on first run) schema, guard against
/// unapproved destructive changes, apply, and persist the new snapshot.
/// </summary>
/// <remarks>
/// Invoked by the code-first builder (<c>FromDescriptor()</c>) and, later, a Management-API
/// migration endpoint — both compose the same four ports rather than duplicating this flow. Every
/// branch that accepts the descriptor as authoritative for the schema actually in the database (an
/// empty plan, or a genuinely applied one) also (re)primes <see cref="IPolicyCatalogProvider"/>
/// from the same parsed descriptor, so <c>IPolicyEngine</c> is never left serving a stale or
/// never-primed catalog after a successful run — see <see cref="PolicyCatalogPriming"/>.
/// </remarks>
internal sealed class SchemaMigrationRunner
{
    private readonly IDescriptorSource _source;
    private readonly IDescriptorValidator _validator;
    private readonly ISchemaMigrator _migrator;
    private readonly ISchemaIntrospector _introspector;
    private readonly IAppliedSchemaStore _store;
    private readonly ICelCompiler _compiler;
    private readonly IPolicyCatalogProvider _policyCatalogProvider;

    public SchemaMigrationRunner(
        IDescriptorSource source,
        IDescriptorValidator validator,
        ISchemaMigrator migrator,
        ISchemaIntrospector introspector,
        IAppliedSchemaStore store,
        ICelCompiler compiler,
        IPolicyCatalogProvider policyCatalogProvider)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(introspector);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(policyCatalogProvider);

        _source = source;
        _validator = validator;
        _migrator = migrator;
        _introspector = introspector;
        _store = store;
        _compiler = compiler;
        _policyCatalogProvider = policyCatalogProvider;
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
    /// </returns>
    public async Task<MigrationResult> RunAsync(MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var descriptorJson = await _source.LoadAsync(ct).ConfigureAwait(false);
        var validation = _validator.Validate(descriptorJson);
        if (!validation.IsValid)
        {
            throw new DescriptorValidationException(validation);
        }

        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        var desired = DescriptorToSchemaMapper.Map(descriptor);

        var appliedSnapshot = await _store.GetCurrentAsync(descriptor.Name, ct).ConfigureAwait(false);
        var current = appliedSnapshot?.Schema
            ?? await _introspector.IntrospectAsync(ct).ConfigureAwait(false);

        var plan = await _migrator.PlanAsync(current, desired, options, ct).ConfigureAwait(false);

        if (plan.IsEmpty)
        {
            PolicyCatalogPriming.Prime(_policyCatalogProvider, _compiler, descriptor, desired);
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
            PolicyCatalogPriming.Prime(_policyCatalogProvider, _compiler, descriptor, desired);
        }

        return result;
    }
}
