namespace MMLib.Alvo.Migrations;

/// <summary>A complete plan for migrating a schema.</summary>
public sealed record MigrationPlan
{
    /// <summary>Gets the <em>semantic</em> steps in this migration plan.</summary>
    /// <remarks>
    /// Steps describe <em>what</em> changes and whether each is destructive; they drive
    /// <see cref="HasDestructiveChanges"/> and the dry-run summary. The <em>executable</em> SQL is
    /// carried separately by <see cref="Sql"/>, because one semantic step can require several SQL
    /// commands (a SQLite table rebuild) and several steps can share one command.
    /// </remarks>
    public required IReadOnlyList<MigrationStep> Steps { get; init; }

    /// <summary>
    /// Gets the ordered SQL commands that execute this plan, generated as one unit so that
    /// interdependent operations (e.g. a drop that rebuilds a table which a later add extends)
    /// resolve against each other correctly. Empty for plans built without a SQL backend (the
    /// test fake), which are applied by projecting the desired model rather than by running SQL.
    /// </summary>
    public IReadOnlyList<string> Sql { get; init; } = [];

    /// <summary>Gets a value indicating whether this plan contains any destructive changes.</summary>
    public bool HasDestructiveChanges => Steps.Any(s => s.IsDestructive);

    /// <summary>Gets a value indicating whether this plan has no steps.</summary>
    public bool IsEmpty => Steps.Count == 0;
}
