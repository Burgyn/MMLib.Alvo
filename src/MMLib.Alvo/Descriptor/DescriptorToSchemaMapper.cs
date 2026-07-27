using MMLib.Alvo.Schema;
using SchemaFieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// Maps a parsed <see cref="AlvoDescriptor"/> to the public <see cref="SchemaModel"/>,
/// injecting the framework-managed columns <see cref="AlvoManagedColumns"/> names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which columns are managed is not decided here.</b> <see cref="AlvoManagedColumns"/> is the
/// authority, and this mapper injects exactly what it reports for an entity's traits — because the
/// write guard that <em>refuses</em> a caller-supplied managed column is <see langword="internal"/> to a
/// driver package and cannot see this file. When the two each carried their own list, this one grew to
/// six columns and the guard's stayed at two.
/// </para>
/// <para>
/// <c>src/MMLib.Alvo.Testing/Data/AlvoDataAdversarialTests.cs</c>'s private <c>BuildFixture</c>
/// hand-mirrors this mapper's managed-column injection (it cannot reference this internal
/// type — that project depends only on <c>MMLib.Alvo.Abstractions</c>). Its mirror only covers
/// field type, <c>Required</c>/<c>Nullable</c>, and the managed columns; it does not replicate
/// <c>Indexed</c>, <c>MaxLength</c>, enum values, or a <c>Ref</c> target. A future change here
/// (a different id/tenant_id shape) does not propagate there automatically — check that fixture when
/// changing this method.
/// </para>
/// </remarks>
internal static class DescriptorToSchemaMapper
{
    public static SchemaModel Map(AlvoDescriptor d)
    {
        bool tenancyEnabled = d.Tenancy?.Enabled == true;
        var entities = d.Entities
            .Where(kvp => IsPhysical(kvp.Value))
            .Select(kvp => MapEntity(kvp.Key, kvp.Value, tenancyEnabled))
            .ToList();
        return new SchemaModel(entities);
    }

    private static bool IsPhysical(EntityDescriptor e) => (e.Storage ?? StorageMode.Physical) == StorageMode.Physical;

    private static EntitySchema MapEntity(string name, EntityDescriptor e, bool tenancyEnabled)
    {
        var fields = new List<FieldSchema>();
        AddManagedColumn(fields, e, AlvoManagedColumns.Id, IdColumn);

        foreach (var (fname, f) in e.Fields)
        {
            fields.Add(MapField(fname, f));
        }

        var tenancy = ResolveTenancy(e.Tenancy, tenancyEnabled);
        bool audit = e.Audit == true;
        bool softDelete = e.SoftDelete == true;
        AddManagedColumns(fields, e, tenancy, audit, softDelete);

        var indexes = (e.Indexes ?? [])
            .Select(i => new IndexSchema(i.Fields, i.Unique == true)).ToList();

        return new EntitySchema
        {
            Name = name,
            RenamedFrom = e.RenamedFrom,
            Storage = EntityStorage.Physical,
            Tenancy = tenancy,
            SoftDelete = softDelete,
            Audit = audit,
            Fields = fields,
            Indexes = indexes,
        };
    }

    private static void AddManagedColumns(
        List<FieldSchema> fields, EntityDescriptor e, TenancyMode? tenancy, bool audit, bool softDelete)
    {
        if (tenancy == TenancyMode.Scoped)
        {
            AddManagedColumn(fields, e, AlvoManagedColumns.TenantId, TenantIdColumn);
        }

        if (audit)
        {
            AddManagedColumn(fields, e, AlvoManagedColumns.CreatedAt, RequiredInstantColumn);
            AddManagedColumn(fields, e, AlvoManagedColumns.CreatedBy, ActorColumn);
            AddManagedColumn(fields, e, AlvoManagedColumns.UpdatedAt, RequiredInstantColumn);
            AddManagedColumn(fields, e, AlvoManagedColumns.UpdatedBy, ActorColumn);
        }

        if (softDelete)
        {
            AddManagedColumn(fields, e, AlvoManagedColumns.DeletedAt, OptionalInstantColumn);
        }
    }

    /// <summary>
    /// Appends one framework-managed column <b>unless the descriptor already declares that name</b>.
    /// </summary>
    /// <remarks>
    /// Appending unconditionally is what made a descriptor naming a managed column produce two
    /// <see cref="FieldSchema"/> entries with one name, so every later operation on that entity died with
    /// <c>An item with the same key has already been added</c> from the first code that keyed on the field
    /// list — which made declaring <c>readOnly</c> on a managed column, the documented way to protect one,
    /// break the entity instead of protecting it. <c>id</c> was the only column guarded this way; the guard
    /// is now the shape every managed column goes through.
    /// <para>
    /// This de-duplicates by name only. A descriptor that declares a managed name with a <em>different
    /// type</em> still wins the mapping, which is a reserved-name question the descriptor validator owns
    /// (see <c>docs/architecture/data-path.md</c>), not something a mapper may silently override.
    /// </para>
    /// </remarks>
    private static void AddManagedColumn(
        List<FieldSchema> fields, EntityDescriptor e, string name, Func<string, FieldSchema> column)
    {
        if (!e.Fields.ContainsKey(name))
        {
            fields.Add(column(name));
        }
    }

    private static FieldSchema IdColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.Uuid, Required = true };

    private static FieldSchema TenantIdColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.Uuid, Required = true, Indexed = true };

    private static FieldSchema RequiredInstantColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.DateTime, Required = true };

    private static FieldSchema OptionalInstantColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.DateTime, Nullable = true };

    private static FieldSchema ActorColumn(string name) =>
        new() { Name = name, Type = SchemaFieldType.Uuid, Nullable = true };

    private static TenancyMode? ResolveTenancy(EntityTenancy? entityTenancy, bool tenancyEnabled) => entityTenancy switch
    {
        EntityTenancy.Global => TenancyMode.Global,
        EntityTenancy.Scoped => TenancyMode.Scoped,
        _ => tenancyEnabled ? TenancyMode.Scoped : null,
    };

    private static FieldSchema MapField(string name, FieldDescriptor f)
    {
        if (f.Computed is not null)
        {
            throw new InvalidDataException(
                $"Field '{name}' declares 'computed', which is not supported yet: computed fields " +
                "require the CEL→SQL compiler arriving in #21. Remove 'computed' or track #21.");
        }

        return new()
        {
            Name = name,
            Type = MapType(f.Type),
            RenamedFrom = f.RenamedFrom,
            Required = f.Required == true,
            Unique = f.Unique == true,
            Nullable = f.Nullable ?? f.Required != true,
            MaxLength = f.MaxLength,
            Precision = f.Precision,
            Scale = f.Scale,
            EnumValues = f.Values,
            Reference = f.Entity is null ? null : new RefSchema(f.Entity, MapOnDelete(f.OnDelete)),
            Indexed = f.Index == true,
            // ComputedExpression intentionally not set — revived by #21 (CEL→SQL).
        };
    }

    private static SchemaFieldType MapType(FieldType t) => t switch
    {
        FieldType.String => SchemaFieldType.String,
        FieldType.Text => SchemaFieldType.Text,
        FieldType.Integer => SchemaFieldType.Integer,
        FieldType.Decimal => SchemaFieldType.Decimal,
        FieldType.Boolean => SchemaFieldType.Boolean,
        FieldType.Date => SchemaFieldType.Date,
        FieldType.DateTime => SchemaFieldType.DateTime,
        FieldType.Uuid => SchemaFieldType.Uuid,
        FieldType.Json => SchemaFieldType.Json,
        FieldType.Enum => SchemaFieldType.Enum,
        FieldType.Ref => SchemaFieldType.Ref,
        _ => throw new InvalidDataException($"Unknown field type '{t}'."),
    };

    private static OnDelete MapOnDelete(OnDeleteAction? od) => od switch
    {
        OnDeleteAction.Cascade => OnDelete.Cascade,
        OnDeleteAction.SetNull => OnDelete.SetNull,
        _ => OnDelete.Restrict,
    };
}
