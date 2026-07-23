using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>
/// A DB-less <see cref="ISchemaMigrator"/> fake for tests: it computes plans via
/// <see cref="SchemaDiff"/> and tracks the applied schema in memory, so tests can
/// exercise migration behavior without a real database.
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

    /// <inheritdoc/>
    public Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default)
    {
        if (plan.HasDestructiveChanges && !options.AllowDestructive)
        {
            return Task.FromResult(new MigrationResult(false, plan, options.DryRun));
        }

        if (!options.DryRun && _projectedModels.TryGetValue(plan, out var projected))
        {
            Applied = projected;
        }

        return Task.FromResult(new MigrationResult(!options.DryRun, plan, options.DryRun));
    }
}
