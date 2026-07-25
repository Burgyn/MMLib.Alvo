namespace MMLib.Alvo.Migrations;

/// <summary>Options for controlling migration behavior.</summary>
public sealed record MigrationOptions
{
    /// <summary>Gets a value indicating whether destructive changes are allowed.</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>Gets a value indicating whether this is a dry run (no changes applied).</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets who is applying this change (audit provenance carried into the appended <see cref="DescriptorVersion"/>; null for code-first/system).</summary>
    public string? Author { get; init; }

    /// <summary>Gets an optional human/agent-supplied reason for this change, carried into the appended <see cref="DescriptorVersion"/>.</summary>
    public string? Reason { get; init; }
}
