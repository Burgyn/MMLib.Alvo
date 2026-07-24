using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>Port for schema migration operations.</summary>
/// <remarks>
/// Implementations are expected to serialize their own <see cref="ApplyAsync"/> execution
/// internally and are intended for controlled use (startup migrations, a single orchestrator) —
/// not as the concurrency-control mechanism for many independent clients changing the schema at
/// runtime, which is governed by descriptor optimistic locking, not by this port.
/// </remarks>
public interface ISchemaMigrator
{
    /// <summary>Plans a migration from the current schema to the desired schema.</summary>
    /// <param name="current">The current schema.</param>
    /// <param name="desired">The desired schema.</param>
    /// <param name="options">Migration options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A migration plan.</returns>
    Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct = default);

    /// <summary>Applies a migration plan.</summary>
    /// <param name="plan">The plan to apply.</param>
    /// <param name="options">Migration options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the migration.</returns>
    Task<MigrationResult> ApplyAsync(MigrationPlan plan, MigrationOptions options, CancellationToken ct = default);
}
