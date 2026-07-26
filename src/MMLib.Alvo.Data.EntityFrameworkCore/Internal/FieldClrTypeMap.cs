using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The single <see cref="FieldSchema"/> → CLR type mapping in the framework, in the two shapes the two
/// EF models need: <see cref="Exact"/> for the migration model, whose column nullability must match the
/// schema, and <see cref="Optional"/> for the read model, where every property is nullable so a masked
/// field can be projected as a typed SQL <c>NULL</c> without the shaper throwing.
/// </summary>
/// <remarks>
/// One mapping, not two, because it is also the contract <c>IAlvoData</c> publishes to callers — a
/// <c>uuid</c> field reads back as a <see cref="Guid"/>, a timestamp as a
/// <see cref="DateTimeOffset"/>, a decimal as a <see cref="decimal"/> — and a second copy is how the
/// read path and the migration path come to disagree about what a column holds.
/// </remarks>
internal static class FieldClrTypeMap
{
    internal static Type Exact(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Wrap(Bare(field.Type), field.Nullable);
    }

    internal static Type Optional(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Wrap(Bare(field.Type), nullable: true);
    }

    private static Type Bare(FieldType type) => type switch
    {
        FieldType.Uuid or FieldType.Ref => typeof(Guid),
        FieldType.String or FieldType.Text or FieldType.Json or FieldType.Enum => typeof(string),
        FieldType.Integer => typeof(long),
        FieldType.Decimal => typeof(decimal),
        FieldType.Boolean => typeof(bool),
        FieldType.Date => typeof(DateOnly),
        FieldType.DateTime => typeof(DateTimeOffset),
        _ => throw new NotSupportedException($"Unsupported field type '{type}'."),
    };

    private static Type Wrap(Type type, bool nullable) =>
        nullable && type.IsValueType ? typeof(Nullable<>).MakeGenericType(type) : type;
}
