using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>
/// A DB-less <see cref="ISchemaMigrator"/> fake for tests: it computes plans via
/// <see cref="SchemaDiff"/> and tracks the applied schema in memory, so tests can
/// exercise migration behavior without a real database. The <see cref="MigrationPlan"/>
/// passed to <see cref="ApplyAsync"/> must be one this instance's <see cref="PlanAsync"/>
/// returned — a foreign plan is rejected rather than silently reported as applied.
/// </summary>
public sealed class InMemorySchemaMigrator : ISchemaMigrator
{
    private readonly ConditionalWeakTable<MigrationPlan, SchemaModel> _projectedModels = new();

    /// <summary>Gets the schema model currently applied by this migrator.</summary>
    public SchemaModel Applied { get; private set; } = new([]);

    /// <inheritdoc/>
    public Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default)
    {
        var plan = new MigrationPlan { Steps = SchemaDiff.Compute(current, desired) };
        _projectedModels.Add(plan, desired);
        return Task.FromResult(plan);
    }

    /// <summary>Applies a migration plan.</summary>
    /// <param name="plan">A plan previously returned by this instance's <see cref="PlanAsync"/>.</param>
    /// <param name="options">Migration options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the migration.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="plan"/> was not returned by this instance's <see cref="PlanAsync"/>.
    /// </exception>
    public Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default)
    {
        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            return Task.FromResult(new MigrationResult(false, plan, options.DryRun));
        }

        if (!_projectedModels.TryGetValue(plan, out var projected))
        {
            throw new InvalidOperationException("The MigrationPlan must be one returned by this migrator's PlanAsync.");
        }

        if (!options.DryRun)
        {
            Applied = projected;
        }

        return Task.FromResult(new MigrationResult(!options.DryRun, plan, options.DryRun));
    }
}
