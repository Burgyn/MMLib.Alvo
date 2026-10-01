namespace MMLib.Alvo.Migrations;

/// <summary>
/// A single <em>semantic</em> step in a migration plan: what changes, and whether it destroys
/// data. The executable SQL is not carried here — a provider may emit several SQL commands for one
/// semantic step (e.g. a SQLite table rebuild) or fold several steps into one command, so the
/// ordered SQL to run lives on the plan (<see cref="MigrationPlan.Sql"/>), decoupled from the
/// human/agent-readable step list that drives <see cref="MigrationPlan.HasDestructiveChanges"/> and
/// the dry-run summary.
/// </summary>
/// <param name="Change">The schema change being applied.</param>
/// <param name="IsDestructive">Whether this step is destructive.</param>
/// <param name="Reason">
/// On a destructive step, why it destroys data; on a non-destructive one, what it costs when that is worth saying before
/// it runs (e.g. that the engine rebuilds the table), or <see langword="null"/>.
/// </param>
public sealed record MigrationStep(SchemaChange Change, bool IsDestructive, string? Reason);
