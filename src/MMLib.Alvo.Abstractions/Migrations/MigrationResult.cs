namespace MMLib.Alvo.Migrations;

/// <summary>The result of applying a migration plan.</summary>
/// <param name="Applied">Whether the migration was applied successfully.</param>
/// <param name="Plan">The plan that was applied.</param>
/// <param name="WasDryRun">Whether this was a dry run.</param>
public sealed record MigrationResult(bool Applied, MigrationPlan Plan, bool WasDryRun);
