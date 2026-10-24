using MMLib.Alvo.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// A host type against one entity (spec 2026-10-14 §4.2): every mapped property names a declared field of a type it can
/// hold, every enum member a declared value, a property on an optional field takes null, and — for a handler, whose record
/// is filled from the event images — no mapped field is one <c>events.exclude</c> leaves out.
/// </summary>
internal static class ShapeMapping
{
    internal static IEnumerable<ShapeProblem> Problems(RecordShape shape, EntitySchema entity, bool fromEventImages) =>
        shape.Properties.SelectMany(property => PropertyProblems(shape, property, entity, fromEventImages));

    internal static bool Holds(MappedProperty property, FieldSchema field) => field.Type switch
    {
        FieldType.Enum => property.ValueType == typeof(string) || property.ValueType.IsEnum,
        FieldType.Integer => property.ValueType == typeof(long) || property.ValueType == typeof(int),
        _ => !property.ValueType.IsEnum && property.ValueType == FieldClrType.Of(field.Type),
    };

    private static IEnumerable<ShapeProblem> PropertyProblems(RecordShape shape, MappedProperty property, EntitySchema entity, bool fromEventImages)
    {
        var owner = $"{shape.Name}.{property.Property.Name}";
        if (Field(entity, property.Field) is not { } field)
        {
            return [Missing(owner, property, entity)];
        }

        if (!Holds(property, field))
        {
            return [Mistyped(owner, property, field)];
        }

        return [.. EnumValues(property, field), .. Nullability(owner, property, field), .. Excluded(owner, property, entity, fromEventImages)];
    }

    private static FieldSchema? Field(EntitySchema entity, string name) =>
        entity.Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static ShapeProblem Missing(string owner, MappedProperty property, EntitySchema entity)
    {
        var names = entity.Fields.Select(field => field.Name).Order(StringComparer.Ordinal).ToList();
        var fix = NameSuggestion.Closest(property.Field, names) is { } closest
            ? $"Did you mean '{closest}'? Map it with [AlvoField(\"{closest}\")], or remove the property."
            : $"Map it to a field of '{entity.Name}' with [AlvoField(\"…\")], or remove the property. Fields: {string.Join(", ", names)}.";

        return new($"{owner} maps to '{property.Field}', which '{entity.Name}' does not declare.", fix);
    }

    private static ShapeProblem Mistyped(string owner, MappedProperty property, FieldSchema field) => new(
        $"{owner} is {ShapeProblems.WithArticle(property.ValueType.Name)}, but '{field.Name}' is {ShapeProblems.WithArticle(field.Type.ToString().ToLowerInvariant())} field, which holds {Held(field)}.",
        $"Declare {owner} as {Held(field)}.");

    private static string Held(FieldSchema field) => field.Type switch
    {
        FieldType.Enum => "String or an enum",
        FieldType.Integer => "Int64 or Int32",
        _ => FieldClrType.Of(field.Type).Name,
    };

    private static IEnumerable<ShapeProblem> EnumValues(MappedProperty property, FieldSchema field)
    {
        var declared = field.EnumValues ?? [];

        return property.Enum is not { } map
            ? []
            : map.Members.Where(member => !declared.Contains(member.Value, StringComparer.Ordinal)).Select(member => new ShapeProblem(
                $"{map.Type.Name}.{member.Name} maps to '{member.Value}', which is not a value of '{field.Name}' ({string.Join(", ", declared)}).",
                $"Name its value with [AlvoEnumValue(\"…\")], or add '{member.Value}' to the field's values."));
    }

    private static IEnumerable<ShapeProblem> Nullability(string owner, MappedProperty property, FieldSchema field) =>
        field.Nullable && !property.AcceptsNull
            ? [new($"'{field.Name}' may be empty, but {owner} cannot hold null.", $"Declare {owner} as {property.ValueType.Name}?.")]
            : [];

    private static IEnumerable<ShapeProblem> Excluded(string owner, MappedProperty property, EntitySchema entity, bool fromEventImages) =>
        fromEventImages && entity.EventImageExclusions.Contains(property.Field, StringComparer.Ordinal)
            ? [new(
                $"{owner} maps to '{property.Field}', which '{entity.Name}' leaves out of its event images (events.exclude): the handler's record could never be filled.",
                $"Map a type without {property.Property.Name} for this handler, or remove '{property.Field}' from entities.{entity.Name}.events.exclude.")]
            : [];
}
