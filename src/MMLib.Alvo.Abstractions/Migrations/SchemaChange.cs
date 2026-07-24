namespace MMLib.Alvo.Migrations;

/// <summary>Describes a single schema change.</summary>
public sealed record SchemaChange
{
    /// <summary>Gets the kind of change.</summary>
    public required SchemaChangeKind Kind { get; init; }

    /// <summary>Gets the entity name affected by this change.</summary>
    public required string Entity { get; init; }

    /// <summary>Gets the field name, if applicable.</summary>
    public string? Field { get; init; }

    /// <summary>Gets the original name (for renames).</summary>
    public string? FromName { get; init; }

    /// <summary>Gets a value indicating whether this change is destructive.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>Gets additional details about the change.</summary>
    public string? Detail { get; init; }
}
