namespace MMLib.Alvo.Schema;

/// <summary>Describes an entity in the schema model.</summary>
public sealed record EntitySchema
{
    /// <summary>Gets the entity name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the human-readable description of what this entity is, or <see langword="null"/> when the
    /// descriptor declares none.
    /// </summary>
    /// <remarks>
    /// On the applied schema for the reason <see cref="FieldSchema.Description"/> is: the generated OpenAPI
    /// document is the contract an agent reads, and the layer that writes it never sees the descriptor. It
    /// carries no behaviour — nothing validates, stores or compares it — which is why the migration diff
    /// (<c>SchemaDiff.IsUnchanged</c>) does not consult it and a changed description is not a migration.
    /// </remarks>
    public string? Description { get; init; }

    /// <summary>Gets the previous name of the entity (for migrations).</summary>
    public string? RenamedFrom { get; init; }

    /// <summary>Gets the storage mode (physical or dynamic).</summary>
    public EntityStorage Storage { get; init; } = EntityStorage.Physical;

    /// <summary>Gets the tenancy mode (scoped or global).</summary>
    public TenancyMode? Tenancy { get; init; }

    /// <summary>Gets a value indicating whether soft delete is enabled.</summary>
    public bool SoftDelete { get; init; }

    /// <summary>Gets a value indicating whether audit logging is enabled.</summary>
    public bool Audit { get; init; }

    /// <summary>Gets the fields in this entity.</summary>
    public required IReadOnlyList<FieldSchema> Fields { get; init; }

    /// <summary>Gets the indexes on this entity.</summary>
    public IReadOnlyList<IndexSchema> Indexes { get; init; } = [];
}
