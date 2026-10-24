using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Data.Internal;

/// <summary>One thing wrong with a host type or its mapping, and how to fix it.</summary>
internal sealed record ShapeProblem(string Message, string Fix);

/// <summary>The problems a host type has whatever the descriptor says: no constructor to call, one field mapped twice, a value no field holds.</summary>
internal static class ShapeProblems
{
    private static readonly HashSet<Type> _held =
        [typeof(string), typeof(long), typeof(int), typeof(decimal), typeof(bool), typeof(Guid), typeof(DateOnly), typeof(DateTimeOffset)];

    internal static IReadOnlyList<ShapeProblem> Of(RecordShape shape) =>
        [.. Construction(shape), .. FieldNames(shape), .. SharedFields(shape), .. shape.Properties.SelectMany(property => Values(shape, property))];

    internal static string WithArticle(string word) =>
        $"{("AEIOUaeiou".Contains(word[0], StringComparison.Ordinal) ? "an" : "a")} {word}";

    private static IEnumerable<ShapeProblem> Construction(RecordShape shape)
    {
        if (shape.Type.IsAbstract)
        {
            yield return new($"{shape.Name} is abstract, so Alvo cannot create one.", $"Map a concrete type that derives from {shape.Name}.");
            yield break;
        }

        if (shape.Constructor is not null)
        {
            yield break;
        }

        yield return shape.IsWrapper
            ? new($"{shape.Name} implements IAlvoEntity but has no public constructor taking an AlvoRecord.", $"Add public {shape.Name}(AlvoRecord record).")
            : new($"{shape.Name} has no public constructor Alvo can call: none is parameterless, and none takes only properties it maps.",
                "Add a parameterless constructor, or make it a positional record over the mapped properties.");
    }

    private static IEnumerable<ShapeProblem> FieldNames(RecordShape shape) =>
        shape.Properties.Where(property => !CelText.IsFieldName(property.Field)).Select(property => new ShapeProblem(
            $"{shape.Name}.{property.Property.Name} maps to '{property.Field}', which is not a field name: a field name starts with a letter or '_' and holds only letters, digits and '_'.",
            "Name the field as the descriptor declares it: [AlvoField(\"…\")]."));

    private static IEnumerable<ShapeProblem> SharedFields(RecordShape shape) =>
        shape.Properties
            .GroupBy(property => property.Field, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new ShapeProblem(
                $"{string.Join(" and ", group.Select(property => $"{shape.Name}.{property.Property.Name}"))} map the same field '{group.Key}'.",
                "Map each field once: name one of them with [AlvoField(\"…\")]."));

    private static IEnumerable<ShapeProblem> Values(RecordShape shape, MappedProperty property)
    {
        if (property.Enum is { } map)
        {
            return EnumProblems(map);
        }

        return _held.Contains(property.ValueType) ? [] : [Unheld(shape, property)];
    }

    private static ShapeProblem Unheld(RecordShape shape, MappedProperty property) => new(
        $"{shape.Name}.{property.Property.Name} is a {property.ValueType.Name}, which no field type holds.",
        property.ValueType == typeof(DateTime)
            ? "Declare it as DateTimeOffset: a datetime field holds one instant."
            : "Declare it as string, long, int, decimal, bool, Guid, DateOnly, DateTimeOffset or an enum.");

    private static IEnumerable<ShapeProblem> EnumProblems(EnumMap map)
    {
        if (map.IsFlags)
        {
            yield return new($"{map.Type.Name} is a [Flags] enum, but an enum field holds one value.", "Map a plain enum.");
        }

        foreach (var shared in map.Members.GroupBy(member => member.Value, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            yield return new(
                $"{string.Join(" and ", shared.Select(member => $"{map.Type.Name}.{member.Name}"))} map the same value '{shared.Key}'.",
                "Give one of them [AlvoEnumValue(\"…\")].");
        }
    }
}
