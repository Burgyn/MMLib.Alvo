using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>Fills an entity's generated names: its class, each property and parameter — unique within the class — and its enums' types and members.</summary>
internal static class EntityNames
{
    internal static EntityModel Named(EntityModel entity)
    {
        var className = CSharpNames.Class(entity.Name);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var fields = entity.Fields.Select(field => Named(field, className, used)).ToImmutableArray();

        return entity with { ClassName = className, Fields = fields };
    }

    private static FieldModel Named(FieldModel field, string className, HashSet<string> used)
    {
        var property = CSharpNames.Unique(CSharpNames.Property(field.Name, className), used);

        return field with
        {
            PropertyName = property,
            ParameterName = CSharpNames.Camel(property),
            EnumTypeName = field.Kind == FieldKind.Enum ? CSharpNames.EnumType(className, property) : null,
            EnumValues = Members(field.EnumValues),
        };
    }

    private static ImmutableArray<EnumValueModel> Members(EquatableArray<EnumValueModel> values)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        return values.Select((value, index) => value with { MemberName = CSharpNames.Unique(CSharpNames.EnumMember(value.Value, index), used) })
            .ToImmutableArray();
    }
}
