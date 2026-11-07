using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>One entity of a descriptor as an <see cref="EntityModel"/>: managed columns first, then its fields in order.</summary>
internal static class EntityReader
{
    internal static EntityModel Read(string name, JsonObject entity, bool tenancyEnabled)
    {
        var fields = ImmutableArray.CreateBuilder<FieldModel>();
        var untyped = ImmutableArray.CreateBuilder<string>();
        fields.AddRange(ManagedColumns(entity, tenancyEnabled));
        var excluded = new HashSet<string>(entity.Object("events")?.Strings("exclude") ?? [], StringComparer.Ordinal);
        foreach (var member in LastOfEachKey(entity.Object("fields")?.Members ?? default))
        {
            if (member.Value is JsonObject field)
            {
                AddField(member.Key, field, excluded, fields, untyped);
            }
        }

        return EntityNames.Named(new EntityModel(name, name, entity.String("description"), fields.ToImmutable(), untyped.ToImmutable()));
    }

    private static List<JsonMember> LastOfEachKey(EquatableArray<JsonMember> members)
    {
        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < members.Count; i++)
        {
            last[members[i].Key] = i;
        }

        var kept = new List<JsonMember>(last.Count);
        for (var i = 0; i < members.Count; i++)
        {
            if (last[members[i].Key] == i)
            {
                kept.Add(members[i]);
            }
        }

        return kept;
    }

    private static void AddField(
        string name, JsonObject field, HashSet<string> excluded, ImmutableArray<FieldModel>.Builder fields, ImmutableArray<string>.Builder untyped)
    {
        if (Kind(field.String("type")) is { } kind && !IsHidden(field) && !excluded.Contains(name))
        {
            fields.Add(Field(name, field, kind));
        }
        else
        {
            untyped.Add(name);
        }
    }

    private static IEnumerable<FieldModel> ManagedColumns(JsonObject entity, bool tenancyEnabled)
    {
        yield return Managed("id", FieldKind.Uuid, nullable: false);
        if (IsScoped(entity, tenancyEnabled))
        {
            yield return Managed("tenant_id", FieldKind.Uuid, nullable: false);
        }

        if (entity.Bool("audit") == true)
        {
            yield return Managed("created_at", FieldKind.DateTime, nullable: false);
            yield return Managed("created_by", FieldKind.Uuid, nullable: true);
            yield return Managed("updated_at", FieldKind.DateTime, nullable: false);
            yield return Managed("updated_by", FieldKind.Uuid, nullable: true);
        }
    }

    private static bool IsScoped(JsonObject entity, bool tenancyEnabled) => entity.String("tenancy") switch
    {
        "scoped" => true,
        "global" => false,
        _ => tenancyEnabled,
    };

    private static FieldModel Managed(string name, FieldKind kind, bool nullable) =>
        new(name, name, name, kind, nullable, Required: !nullable, HasDefault: false, CallerWritable: false, Description: null, default, null, FieldOrigin.Managed);

    private static FieldModel Field(string name, JsonObject field, FieldKind kind)
    {
        var isVirtual = field.Bool("virtual") == true;
        var required = field.Bool("required") == true;

        return new FieldModel(
            name,
            name,
            name,
            kind,
            Nullable: isVirtual || (field.Bool("nullable") ?? !required),
            required,
            HasDefault: field.Get("default") is not null,
            CallerWritable: IsCallerWritable(field, isVirtual),
            field.String("description"),
            EnumValues(field, kind),
            kind == FieldKind.Enum ? name : null,
            Origin(field, isVirtual));
    }

    private static FieldOrigin Origin(JsonObject field, bool isVirtual) =>
        isVirtual ? FieldOrigin.Virtual
        : field.Get("computed") is not null ? FieldOrigin.Computed
        : field.Get("rollup") is not null ? FieldOrigin.Rollup
        : field.Get("sequence") is not null ? FieldOrigin.Sequence
        : FieldOrigin.Declared;

    private static bool IsCallerWritable(JsonObject field, bool isVirtual) =>
        !isVirtual
        && field.Get("computed") is null
        && field.Get("rollup") is null
        && field.Get("sequence") is null
        && field.Bool("readOnly") != true;

    private static bool IsHidden(JsonObject field) => field.Get("hidden") is { } hidden && !hidden.Equals(new JsonBool(false));

    private static ImmutableArray<EnumValueModel> EnumValues(JsonObject field, FieldKind kind) => kind == FieldKind.Enum
        ? field.Strings("values").Select(value => new EnumValueModel(value, value)).ToImmutableArray()
        : ImmutableArray<EnumValueModel>.Empty;

    private static FieldKind? Kind(string? type) => type switch
    {
        "string" or "text" => FieldKind.String,
        "integer" => FieldKind.Integer,
        "decimal" => FieldKind.Decimal,
        "boolean" => FieldKind.Boolean,
        "date" => FieldKind.Date,
        "datetime" => FieldKind.DateTime,
        "uuid" or "ref" => FieldKind.Uuid,
        "enum" => FieldKind.Enum,
        _ => null,
    };
}
