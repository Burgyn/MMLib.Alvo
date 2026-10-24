using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>Builds a host type from a record through the constructor its shape chose, then its remaining public setters.</summary>
internal static class RecordMaterializer
{
    internal static object Create(RecordShape shape, AlvoRecord record)
    {
        if (shape.Problems.Count > 0)
        {
            throw new InvalidOperationException($"{shape.Name} cannot be read from a record: {shape.Problems[0].Message} {shape.Problems[0].Fix}");
        }

        return shape.IsWrapper ? Wrapped(shape, record) : Built(shape, record);
    }

    private static object Wrapped(RecordShape shape, AlvoRecord record)
    {
        var missing = shape.Properties.FirstOrDefault(property => !record.TryGetValue(property.Field, out _));

        return missing is null ? shape.Constructor!.Invoke([record]) : throw RecordValue.Missing(shape, missing);
    }

    private static object Built(RecordShape shape, AlvoRecord record)
    {
        var parameters = shape.Constructor!.GetParameters();
        var instance = shape.Constructor.Invoke([.. parameters.Select(parameter => RecordValue.Read(shape, record, RecordShape.ParameterProperty(parameter, shape.Properties)!))]);
        foreach (var property in shape.Properties.Where(property => property.Property.SetMethod is { IsPublic: true } && !Passed(property, parameters)))
        {
            property.Property.SetValue(instance, RecordValue.Read(shape, record, property));
        }

        return instance;
    }

    private static bool Passed(MappedProperty property, ParameterInfo[] parameters) =>
        parameters.Any(parameter => string.Equals(parameter.Name, property.Property.Name, StringComparison.OrdinalIgnoreCase));
}
