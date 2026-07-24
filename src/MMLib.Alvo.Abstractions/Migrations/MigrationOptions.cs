namespace MMLib.Alvo.Migrations;

/// <summary>Options for controlling migration behavior.</summary>
public sealed record MigrationOptions
{
    /// <summary>Gets a value indicating whether destructive changes are allowed.</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>Gets a value indicating whether this is a dry run (no changes applied).</summary>
    public bool DryRun { get; init; }
}
