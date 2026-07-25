using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// Orchestrates a runtime (dashboard-first) schema change: validate untrusted descriptor input,
/// plan against the latest applied version, enforce the destructive guardrail, then atomically apply
/// and append a new version. The service-level operation the Management-API runtime-apply endpoint
/// will call; it owns no DB connection (the atomic transaction lives behind <see cref="IRuntimeSchemaWriter"/>).
/// </summary>
/// <remarks>
/// Every branch that accepts a descriptor as the project's current, authoritative one (an idempotent
/// re-apply, a genuine apply, or a rollback) also (re)primes <see cref="IPolicyCatalogProvider"/> from
/// that same descriptor — see <see cref="PolicyCatalogPriming"/> — so a tightened or revoked rule
/// takes effect for the very next <c>IPolicyEngine.Resolve</c> call, not merely after a process
/// restart.
/// </remarks>
public sealed class RuntimeSchemaService
{
    private readonly IDescriptorValidator _validator;
    private readonly ISchemaMigrator _migrator;
    private readonly IDescriptorVersionStore _store;
    private readonly IRuntimeSchemaWriter _writer;
    private readonly ICelCompiler _compiler;
    private readonly IPolicyCatalogProvider _policyCatalogProvider;

    /// <summary>Initializes a new instance of the <see cref="RuntimeSchemaService"/> class.</summary>
    /// <param name="validator">Validates untrusted descriptor JSON before it is parsed.</param>
    /// <param name="migrator">Plans the migration between the current and desired schema.</param>
    /// <param name="store">The append-only descriptor version history, read for the current/rollback source.</param>
    /// <param name="writer">The atomic apply-plan-and-append-version seam.</param>
    /// <param name="compiler">Compiles the policy catalog primed after every accepted apply/rollback.</param>
    /// <param name="policyCatalogProvider">Holds the currently effective <see cref="PolicyCatalog"/> for <c>IPolicyEngine</c>.</param>
    public RuntimeSchemaService(
        IDescriptorValidator validator, ISchemaMigrator migrator,
        IDescriptorVersionStore store, IRuntimeSchemaWriter writer,
        ICelCompiler compiler, IPolicyCatalogProvider policyCatalogProvider)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(policyCatalogProvider);
        _validator = validator;
        _migrator = migrator;
        _store = store;
        _writer = writer;
        _compiler = compiler;
        _policyCatalogProvider = policyCatalogProvider;
    }

    /// <summary>Validates, plans, guards, and atomically applies + versions a runtime descriptor change.</summary>
    /// <param name="project">The project whose schema is changing.</param>
    /// <param name="descriptorJson">The untrusted descriptor JSON to validate, parse, and apply.</param>
    /// <param name="expectedRevision">The revision the caller expects to currently be latest (0 for a fresh project).</param>
    /// <param name="options">Migration options; <see cref="MigrationOptions.Author"/>/<see cref="MigrationOptions.Reason"/> are carried into the appended version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended <see cref="DescriptorVersion"/>.</returns>
    /// <exception cref="DescriptorValidationException"><paramref name="descriptorJson"/> is invalid.</exception>
    /// <exception cref="DestructiveChangeNotAllowedException">The plan is destructive and <see cref="MigrationOptions.AllowDestructive"/> is <see langword="false"/>.</exception>
    /// <exception cref="DescriptorConcurrencyException"><paramref name="expectedRevision"/> lost the optimistic-lock race.</exception>
    /// <exception cref="NotSupportedException"><see cref="MigrationOptions.DryRun"/> is <see langword="true"/>; the runtime path has no dry-run.</exception>
    public async Task<DescriptorVersion> ApplyAsync(string project, string descriptorJson, int expectedRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(options);
        RejectDryRun(options);

        Validate(descriptorJson);
        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        var desired = DescriptorToSchemaMapper.Map(descriptor);
        var current = await _store.GetCurrentAsync(project, ct).ConfigureAwait(false);
        var currentSchema = current?.Schema ?? new SchemaModel([]);
        var currentRevision = current?.Revision ?? 0;
        if (currentRevision != expectedRevision)
        {
            // Fail fast on staleness BEFORE planning: planning against the store's actual current
            // (which the caller's expectedRevision no longer matches — either a plain stale caller,
            // or the other side of a genuine race that already committed) would diff the desired
            // schema against a base the caller never saw. That diff can misclassify as destructive
            // (e.g. two unrelated single-field additions look like a drop+add) and surface the wrong
            // exception type, or — worse — silently apply an unintended diff when AllowDestructive is
            // set. The writer's own optimistic-lock check still guards the true, narrower race between
            // this read and the atomic append below.
            throw new DescriptorConcurrencyException(project, expectedRevision, currentRevision);
        }

        var plan = await _migrator.PlanAsync(currentSchema, desired, options, ct).ConfigureAwait(false);
        Guard(project, plan, options);

        if (plan.IsEmpty && current is not null)
        {
            // Idempotent re-apply of an unchanged descriptor, mirroring the code-first runner's
            // plan.IsEmpty no-op: nothing to append, and the optimistic-lock head must not advance
            // just because the caller resubmitted the same schema. Only skip the writer when a prior
            // version already exists — a fresh project (current is null) whose desired schema happens
            // to plan empty (e.g. an entity-less descriptor) still gets its rev-1 baseline appended
            // below, so ApplyAsync's Task<DescriptorVersion> contract never has to return null.
            PolicyCatalogPriming.Prime(_policyCatalogProvider, _compiler, descriptor, desired);
            return current;
        }

        var candidate = new DescriptorVersion(desired, descriptorJson, 0, DateTimeOffset.UtcNow, options.Author, options.Reason);
        var applied = await _writer.ApplyAndAppendAsync(project, plan, candidate, expectedRevision, options, ct).ConfigureAwait(false);
        PolicyCatalogPriming.Prime(_policyCatalogProvider, _compiler, descriptor, desired);
        return applied;
    }

    /// <summary>Rolls the project back to <paramref name="targetRevision"/> by appending a git-revert version.</summary>
    /// <param name="project">The project to roll back.</param>
    /// <param name="targetRevision">The historical revision to restore.</param>
    /// <param name="options">Migration options; <see cref="MigrationOptions.Author"/> is carried into the appended version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The appended <see cref="DescriptorVersion"/>, with <see cref="DescriptorVersion.RolledBackFrom"/> set to <paramref name="targetRevision"/>.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="targetRevision"/> does not exist, or the project has no applied schema to roll back.</exception>
    /// <exception cref="DestructiveChangeNotAllowedException">The plan is destructive and <see cref="MigrationOptions.AllowDestructive"/> is <see langword="false"/>.</exception>
    /// <exception cref="DescriptorConcurrencyException">The current revision changed concurrently, losing the optimistic-lock race.</exception>
    /// <exception cref="NotSupportedException"><see cref="MigrationOptions.DryRun"/> is <see langword="true"/>; the runtime path has no dry-run.</exception>
    public async Task<DescriptorVersion> RollbackAsync(string project, int targetRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(options);
        RejectDryRun(options);

        var target = await _store.GetAsync(project, targetRevision, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{project}' has no revision {targetRevision} to roll back to.");
        var currentVersion = await _store.GetCurrentAsync(project, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{project}' has no applied schema to roll back.");

        var plan = await _migrator.PlanAsync(currentVersion.Schema, target.Schema, options, ct).ConfigureAwait(false);
        Guard(project, plan, options);

        var candidate = new DescriptorVersion(
            target.Schema, target.DescriptorJson, 0, DateTimeOffset.UtcNow,
            options.Author, options.Reason ?? $"Rollback to revision {targetRevision}", RolledBackFrom: targetRevision);
        var reverted = await _writer.ApplyAndAppendAsync(project, plan, candidate, currentVersion.Revision, options, ct).ConfigureAwait(false);
        PolicyCatalogPriming.Prime(_policyCatalogProvider, _compiler, AlvoDescriptor.Parse(target.DescriptorJson), target.Schema);
        return reverted;
    }

    private void Validate(string descriptorJson)
    {
        var result = _validator.Validate(descriptorJson);
        if (!result.IsValid)
        {
            throw new DescriptorValidationException(result);
        }
    }

    private static void Guard(string project, MigrationPlan plan, MigrationOptions options)
    {
        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            throw new DestructiveChangeNotAllowedException(project, plan);
        }
    }

    // The runtime path has no dry-run: the atomic IRuntimeSchemaWriter applies and appends in one
    // step, so there is no seam to preview from without actually mutating. Reject up front rather
    // than silently ignoring the flag (which would surprise a caller expecting a no-op preview).
    private static void RejectDryRun(MigrationOptions options)
    {
        if (options.DryRun)
        {
            throw new NotSupportedException(
                "Runtime schema apply does not support dry-run (MigrationOptions.DryRun). " +
                "Preview is not available on the runtime path; inspect the plan via a plan-only " +
                "operation, or use the code-first path for dry-run.");
        }
    }
}
