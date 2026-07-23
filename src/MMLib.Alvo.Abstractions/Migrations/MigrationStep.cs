namespace MMLib.Alvo.Migrations;

/// <summary>A single step in a migration plan.</summary>
/// <param name="Change">The schema change being applied.</param>
/// <param name="Sql">The SQL statement to execute.</param>
/// <param name="IsDestructive">Whether this step is destructive.</param>
/// <param name="Reason">The reason for the destructive nature, if applicable.</param>
public sealed record MigrationStep(SchemaChange Change, string Sql, bool IsDestructive, string? Reason);
