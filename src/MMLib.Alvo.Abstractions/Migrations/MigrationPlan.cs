namespace MMLib.Alvo.Migrations;

/// <summary>A complete plan for migrating a schema.</summary>
public sealed record MigrationPlan
{
    /// <summary>Gets the steps in this migration plan.</summary>
    public required IReadOnlyList<MigrationStep> Steps { get; init; }

    /// <summary>Gets a value indicating whether this plan contains any destructive changes.</summary>
    public bool HasDestructiveChanges => Steps.Any(s => s.IsDestructive);

    /// <summary>Gets a value indicating whether this plan has no steps.</summary>
    public bool IsEmpty => Steps.Count == 0;
}
