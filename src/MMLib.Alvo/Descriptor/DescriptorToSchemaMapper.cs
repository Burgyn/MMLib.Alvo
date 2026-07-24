using MMLib.Alvo.Schema;
using SchemaFieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// Maps a parsed <see cref="AlvoDescriptor"/> to the public <see cref="SchemaModel"/>,
/// injecting the framework-managed columns (id, tenant_id, audit quartet, deleted_at).
/// </summary>
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
        if (!e.Fields.ContainsKey("id"))
        {
            fields.Add(new FieldSchema { Name = "id", Type = SchemaFieldType.Uuid, Required = true });
        }

        foreach (var (fname, f) in e.Fields)
        {
            fields.Add(MapField(fname, f));
        }

        var tenancy = ResolveTenancy(e.Tenancy, tenancyEnabled);
        bool audit = e.Audit == true;
        bool softDelete = e.SoftDelete == true;
        AddManagedColumns(fields, tenancy, audit, softDelete);

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

    private static void AddManagedColumns(List<FieldSchema> fields, TenancyMode? tenancy, bool audit, bool softDelete)
    {
        if (tenancy == TenancyMode.Scoped)
        {
            fields.Add(new FieldSchema { Name = "tenant_id", Type = SchemaFieldType.Uuid, Required = true, Indexed = true });
        }

        if (audit)
        {
            fields.Add(new FieldSchema { Name = "created_at", Type = SchemaFieldType.DateTime, Required = true });
            fields.Add(new FieldSchema { Name = "created_by", Type = SchemaFieldType.Uuid, Nullable = true });
            fields.Add(new FieldSchema { Name = "updated_at", Type = SchemaFieldType.DateTime, Required = true });
            fields.Add(new FieldSchema { Name = "updated_by", Type = SchemaFieldType.Uuid, Nullable = true });
        }

        if (softDelete)
        {
            fields.Add(new FieldSchema { Name = "deleted_at", Type = SchemaFieldType.DateTime, Nullable = true });
        }
    }

    private static TenancyMode? ResolveTenancy(EntityTenancy? entityTenancy, bool tenancyEnabled) => entityTenancy switch
    {
        EntityTenancy.Global => TenancyMode.Global,
        EntityTenancy.Scoped => TenancyMode.Scoped,
        _ => tenancyEnabled ? TenancyMode.Scoped : null,
    };

    private static FieldSchema MapField(string name, FieldDescriptor f) => new()
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
        ComputedExpression = f.Computed,
    };

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
