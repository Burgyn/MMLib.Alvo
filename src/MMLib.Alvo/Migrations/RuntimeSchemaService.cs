using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// Orchestrates a runtime (dashboard-first) schema change: validate untrusted descriptor input,
/// plan against the latest applied version, enforce the destructive guardrail, then atomically apply
/// and append a new version. The service-level operation the Management-API runtime-apply endpoint
/// will call; it owns no DB connection (the atomic transaction lives behind <see cref="IRuntimeSchemaWriter"/>).
/// </summary>
public sealed class RuntimeSchemaService
{
    private readonly IDescriptorValidator _validator;
    private readonly ISchemaMigrator _migrator;
    private readonly IDescriptorVersionStore _store;
    private readonly IRuntimeSchemaWriter _writer;

    /// <summary>Initializes a new instance of the <see cref="RuntimeSchemaService"/> class.</summary>
    /// <param name="validator">Validates untrusted descriptor JSON before it is parsed.</param>
    /// <param name="migrator">Plans the migration between the current and desired schema.</param>
    /// <param name="store">The append-only descriptor version history, read for the current/rollback source.</param>
    /// <param name="writer">The atomic apply-plan-and-append-version seam.</param>
    public RuntimeSchemaService(
        IDescriptorValidator validator, ISchemaMigrator migrator,
        IDescriptorVersionStore store, IRuntimeSchemaWriter writer)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        _validator = validator;
        _migrator = migrator;
        _store = store;
        _writer = writer;
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
    public async Task<DescriptorVersion> ApplyAsync(string project, string descriptorJson, int expectedRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(options);

        Validate(descriptorJson);
        var desired = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptorJson));
        var current = await CurrentSchemaAsync(project, ct).ConfigureAwait(false);
        var plan = await _migrator.PlanAsync(current, desired, options, ct).ConfigureAwait(false);
        Guard(project, plan, options);

        var candidate = new DescriptorVersion(desired, descriptorJson, 0, DateTimeOffset.UtcNow, options.Author, options.Reason);
        return await _writer.ApplyAndAppendAsync(project, plan, candidate, expectedRevision, options, ct).ConfigureAwait(false);
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
    public async Task<DescriptorVersion> RollbackAsync(string project, int targetRevision, MigrationOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(options);

        var target = await _store.GetAsync(project, targetRevision, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{project}' has no revision {targetRevision} to roll back to.");
        var currentVersion = await _store.GetCurrentAsync(project, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{project}' has no applied schema to roll back.");

        var plan = await _migrator.PlanAsync(currentVersion.Schema, target.Schema, options, ct).ConfigureAwait(false);
        Guard(project, plan, options);

        var candidate = new DescriptorVersion(
            target.Schema, target.DescriptorJson, 0, DateTimeOffset.UtcNow,
            options.Author, options.Reason ?? $"Rollback to revision {targetRevision}", RolledBackFrom: targetRevision);
        return await _writer.ApplyAndAppendAsync(project, plan, candidate, currentVersion.Revision, options, ct).ConfigureAwait(false);
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

    private async Task<SchemaModel> CurrentSchemaAsync(string project, CancellationToken ct)
    {
        var current = await _store.GetCurrentAsync(project, ct).ConfigureAwait(false);
        return current?.Schema ?? new SchemaModel([]);
    }
}
