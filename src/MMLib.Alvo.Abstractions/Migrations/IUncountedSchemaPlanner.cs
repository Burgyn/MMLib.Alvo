using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Migrations;

/// <summary>
/// A migrator that can plan without counting the rows a new validation would refuse — the plan
/// <see cref="ISchemaMigrator.PlanAsync"/> answers, minus the pre-apply violation scan (HX-11 Rulings 22, 66).
/// </summary>
/// <remarks>
/// <para>
/// <b>For the boot alone.</b> A boot plans before it knows whether it will act: <c>Skip</c> serves whatever the database
/// holds, and an older pod stands down (#145). Neither applies anything, so neither may be failed by a count over rows it
/// will never touch — the boot plans through this seam, decides, and counts by planning again only when the decision is
/// to initialize or apply.
/// </para>
/// <para>
/// <b>Internal</b>, implemented by the EF migrator through Abstractions' <c>InternalsVisibleTo</c>, so the published
/// migrator port does not grow. A migrator without it is planned through <see cref="ISchemaMigrator.PlanAsync"/>.
/// </para>
/// </remarks>
internal interface IUncountedSchemaPlanner
{
    /// <summary>Plans a migration exactly as <see cref="ISchemaMigrator.PlanAsync"/> does, without the violation scan.</summary>
    /// <param name="current">The current schema.</param>
    /// <param name="desired">The desired schema.</param>
    /// <param name="options">The migration options.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MigrationPlan> PlanWithoutCountingAsync(SchemaModel current, SchemaModel desired, MigrationOptions options, CancellationToken ct);
}
