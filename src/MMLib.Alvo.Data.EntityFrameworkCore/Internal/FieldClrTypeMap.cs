using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// <see cref="FieldClrType"/> in the two shapes the two EF models need: <see cref="Exact"/> for the
/// migration model, whose column nullability must match the schema, and <see cref="Optional"/> for the
/// read model, where every property is nullable so a masked field can be projected as a typed SQL
/// <c>NULL</c> without the shaper throwing.
/// </summary>
/// <remarks>
/// The <see cref="FieldType"/> → CLR type table itself lives in <see cref="FieldClrType"/>, in the
/// ports: it is the contract <c>IAlvoData</c> publishes to callers, not this driver's opinion, and the
/// HTTP layer in the core has to answer it identically while being unable to reference this package.
/// This type adds only the nullability wrapping, which is a question about one model rather than about
/// the port — see <see cref="FieldClrType"/>'s own remarks.
/// </remarks>
internal static class FieldClrTypeMap
{
    internal static Type Exact(FieldSchema field) => Wrap(FieldClrType.Of(field), field.Nullable);

    internal static Type Optional(FieldSchema field) => Wrap(FieldClrType.Of(field), nullable: true);

    private static Type Wrap(Type type, bool nullable) =>
        nullable && type.IsValueType ? typeof(Nullable<>).MakeGenericType(type) : type;
}
