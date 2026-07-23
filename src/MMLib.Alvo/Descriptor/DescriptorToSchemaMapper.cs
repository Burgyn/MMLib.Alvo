using MMLib.Alvo.Schema;

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
            .Where(kvp => (kvp.Value.Storage ?? "physical") == "physical")   // dynamic store is F7
            .Select(kvp => MapEntity(kvp.Key, kvp.Value, tenancyEnabled))
            .ToList();
        return new SchemaModel(entities);
    }

    private static EntitySchema MapEntity(string name, EntityDto e, bool tenancyEnabled)
    {
        var fields = new List<FieldSchema>();
        if (!e.Fields.ContainsKey("id"))
        {
            fields.Add(new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true });
        }

        foreach (var (fname, f) in e.Fields)
        {
            fields.Add(MapField(fname, f));
        }

        var tenancy = ResolveTenancy(e.Tenancy, tenancyEnabled);
        if (tenancy == TenancyMode.Scoped)
        {
            fields.Add(new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true });
        }

        if (e.Audit)
        {
            fields.Add(new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Required = true });
            fields.Add(new FieldSchema { Name = "created_by", Type = FieldType.Uuid, Nullable = true });
            fields.Add(new FieldSchema { Name = "updated_at", Type = FieldType.DateTime, Required = true });
            fields.Add(new FieldSchema { Name = "updated_by", Type = FieldType.Uuid, Nullable = true });
        }

        if (e.SoftDelete)
        {
            fields.Add(new FieldSchema { Name = "deleted_at", Type = FieldType.DateTime, Nullable = true });
        }

        var indexes = (e.Indexes ?? [])
            .Select(i => new IndexSchema(i.Fields, i.Unique)).ToList();

        return new EntitySchema
        {
            Name = name,
            RenamedFrom = e.RenamedFrom,
            Storage = EntityStorage.Physical,
            Tenancy = tenancy,
            SoftDelete = e.SoftDelete,
            Audit = e.Audit,
            Fields = fields,
            Indexes = indexes,
        };
    }

    private static TenancyMode? ResolveTenancy(string? entityTenancy, bool tenancyEnabled) => entityTenancy switch
    {
        "global" => TenancyMode.Global,
        "scoped" => TenancyMode.Scoped,
        _ => tenancyEnabled ? TenancyMode.Scoped : null,
    };

    private static FieldSchema MapField(string name, FieldDto f) => new()
    {
        Name = name,
        Type = ParseType(f.Type),
        RenamedFrom = f.RenamedFrom,
        Required = f.Required,
        Unique = f.Unique,
        Nullable = f.Nullable ?? !f.Required,
        MaxLength = f.MaxLength,
        Precision = f.Precision,
        Scale = f.Scale,
        EnumValues = f.Values,
        Reference = f.Entity is null ? null : new RefSchema(f.Entity, ParseOnDelete(f.OnDelete)),
        Indexed = f.Index,
        ComputedExpression = f.Computed,
    };

    private static FieldType ParseType(string t) => t switch
    {
        "string" => FieldType.String,
        "text" => FieldType.Text,
        "integer" => FieldType.Integer,
        "decimal" => FieldType.Decimal,
        "boolean" => FieldType.Boolean,
        "date" => FieldType.Date,
        "datetime" => FieldType.DateTime,
        "uuid" => FieldType.Uuid,
        "json" => FieldType.Json,
        "enum" => FieldType.Enum,
        "ref" => FieldType.Ref,
        _ => throw new InvalidDataException($"Unknown field type '{t}'."),
    };

    private static OnDelete ParseOnDelete(string? od) => od switch
    {
        "cascade" => OnDelete.Cascade,
        "setNull" => OnDelete.SetNull,
        _ => OnDelete.Restrict,
    };
}
