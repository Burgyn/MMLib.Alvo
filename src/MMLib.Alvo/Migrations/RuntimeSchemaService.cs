using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations.Internal;
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
/// <para>
/// Every branch that accepts a descriptor as the project's current, authoritative one (a genuine
/// apply, a rules-only change, a re-apply of the descriptor already stored, or a rollback) also
/// (re)primes <see cref="IPolicyCatalogProvider"/> from that same descriptor, so a tightened or
/// revoked rule takes effect for the very next <c>IPolicyEngine.Resolve</c> call, not merely after a
/// process restart. On the branches that write something durable the catalog is always built — a step
/// that can throw <see cref="DescriptorValidationException"/> when a rule fails to compile —
/// <em>before</em> <see cref="IRuntimeSchemaWriter.ApplyAndAppendAsync"/> commits the schema/version
/// change, and published via <see cref="IPolicyCatalogProvider.SetCurrent"/> only <em>after</em> that
/// commit succeeds: an uncompilable rule set rejects the whole apply rather than leaving a committed
/// schema paired with a stale (possibly too-permissive) catalog. The re-apply of an identical
/// descriptor writes nothing at all, so it builds and publishes in one step
/// (<c>PolicyCatalogPriming</c>) — and rejects the call if a rule no longer compiles, rather than
/// reporting success while leaving the catalog as it was.
/// </para>
/// <para>
/// <strong>Nothing primes at startup.</strong> This service is driven by a request, so a host that
/// only ever applies descriptors at runtime comes back from a restart with an unprimed provider,
/// which <c>IPolicyEngine</c> treats as deny-everything until something applies. That is the safe
/// direction, but it is a real gap rather than a design intent: re-applying the stored descriptor is
/// what closes it (which is why the branch above primes), and the HTTP/host wiring that will do so on
/// startup is not part of this milestone.
/// </para>
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
    /// <returns>
    /// The appended <see cref="DescriptorVersion"/> — or the unchanged current one when
    /// <paramref name="descriptorJson"/> is, in canonical form, byte-identical to what is already
    /// stored. A rules-only change (same fields, different CEL) plans empty exactly like such a
    /// resubmission does, so the plan cannot tell the two apart and the descriptors' own content is
    /// what does: a rules-only change <b>does</b> append a version, an identical resubmission does
    /// not. Both (re)prime the policy catalog.
    /// </returns>
    /// <exception cref="DescriptorValidationException"><paramref name="descriptorJson"/> is invalid, or one of its rules no longer compiles.</exception>
    /// <exception cref="DestructiveChangeNotAllowedException">The plan is destructive and <see cref="MigrationOptions.AllowDestructive"/> is <see langword="false"/>.</exception>
    /// <exception cref="DescriptorConcurrencyException">
    /// <paramref name="expectedRevision"/> is not the latest revision. Checked <em>before</em>
    /// planning, deliberately: planning against a base the caller never saw can misclassify the diff
    /// as destructive (two unrelated field additions read as a drop plus an add), surfacing the wrong
    /// exception — or, with <see cref="MigrationOptions.AllowDestructive"/> set, silently applying a
    /// diff nobody asked for. The writer's own optimistic-lock check still guards the narrower race
    /// between this check and the atomic append.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="MigrationOptions.DryRun"/> is <see langword="true"/>. The runtime path has no
    /// dry-run: <see cref="IRuntimeSchemaWriter"/> applies and appends in one atomic step, so there is
    /// no seam to preview from without mutating. It is refused rather than ignored, so a caller
    /// expecting a no-op preview does not get a real apply.
    /// </exception>
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
            throw new DescriptorConcurrencyException(project, expectedRevision, currentRevision);
        }

        var plan = await _migrator.PlanAsync(currentSchema, desired, options, ct).ConfigureAwait(false);
        Guard(project, plan, options);

        if (IsUnchangedReapply(plan, current, descriptor))
        {
            PolicyCatalogPriming.Prime(_policyCatalogProvider, _compiler, project, descriptor, desired);
            return current!;
        }

        var catalog = PolicyCatalog.Build(descriptor, desired, _compiler);
        var candidate = new DescriptorVersion(desired, descriptorJson, 0, DateTimeOffset.UtcNow, options.Author, options.Reason);
        var applied = await _writer.ApplyAndAppendAsync(project, plan, candidate, expectedRevision, options, ct).ConfigureAwait(false);
        _policyCatalogProvider.SetCurrent(project, catalog);
        return applied;
    }

    private static bool IsUnchangedReapply(MigrationPlan plan, DescriptorVersion? current, AlvoDescriptor descriptor) =>
        plan.IsEmpty && current is not null && DescriptorContent.IsSame(descriptor, current.DescriptorJson);

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

        var catalog = PolicyCatalog.Build(AlvoDescriptor.Parse(target.DescriptorJson), target.Schema, _compiler);
        var candidate = new DescriptorVersion(
            target.Schema, target.DescriptorJson, 0, DateTimeOffset.UtcNow,
            options.Author, options.Reason ?? $"Rollback to revision {targetRevision}", RolledBackFrom: targetRevision);
        var reverted = await _writer.ApplyAndAppendAsync(project, plan, candidate, currentVersion.Revision, options, ct).ConfigureAwait(false);
        _policyCatalogProvider.SetCurrent(project, catalog);
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
