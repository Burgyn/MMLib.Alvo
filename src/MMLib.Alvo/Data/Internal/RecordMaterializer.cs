using System.Reflection;

namespace MMLib.Alvo.Data.Internal;

/// <summary>Builds a host type from a record through the constructor its shape chose, then its remaining public setters.</summary>
/// <remarks>The host's own constructor or setter exception surfaces as itself, not wrapped in a <see cref="TargetInvocationException"/>.</remarks>
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

        return missing is null ? Construct(shape, [record]) : throw RecordValue.Missing(shape, missing);
    }

    private static object Built(RecordShape shape, AlvoRecord record)
    {
        var instance = Construct(shape, [.. shape.Arguments.Select(property => RecordValue.Read(shape, record, property))]);
        foreach (var property in shape.Settable)
        {
            property.Property.SetValue(instance, RecordValue.Read(shape, record, property), BindingFlags.DoNotWrapExceptions, binder: null, index: null, culture: null);
        }

        return instance;
    }

    private static object Construct(RecordShape shape, object?[] arguments) =>
        shape.Constructor!.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null);
}
