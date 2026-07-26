using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The <see cref="CelValueType"/> a comparison over a declared field is evaluated at — the type a caller
/// filter and a keyset cursor hand to <see cref="IFieldSqlRenderer.RenderComparableOperand"/> so a dialect
/// whose storage does not order the way the type does can repair the comparison.
/// </summary>
/// <remarks>
/// <para>
/// The <b>column</b> is the authority, not the caller's value. A caller comparing a <c>decimal</c> column
/// against a whole number must still get a numeric comparison, and on SQLite — where a decimal lives in a
/// <c>TEXT</c> column — taking the type from the value would leave that comparison lexicographic: the same
/// fail-open a <c>USING</c> rule gating on an amount had before both operands were repaired.
/// </para>
/// <para>
/// This mirrors the CEL type checker's own field-type mapping, which is <see langword="private"/> to the
/// core (a filter is not CEL, so there is nothing compiled to read a resolved type off). The duplication is
/// deliberate and pinned: <c>FieldCelTypeTests</c> asserts, per field type, that this answers exactly what
/// the real <c>ICelCompiler</c> resolves for a field reference of that type, so the two cannot drift
/// silently. A field type this build does not know is reported untyped — the same answer the type checker
/// gives — which means no repair rather than a wrong one.
/// </para>
/// </remarks>
internal static class FieldCelType
{
    internal static CelValueType Of(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return field.Type switch
        {
            FieldType.String or FieldType.Text or FieldType.Enum => CelValueType.String,
            FieldType.Integer => CelValueType.Int,
            FieldType.Decimal => CelValueType.Decimal,
            FieldType.Boolean => CelValueType.Bool,
            FieldType.Date or FieldType.DateTime => CelValueType.Timestamp,
            FieldType.Uuid or FieldType.Ref => CelValueType.Uuid,
            _ => CelValueType.Json,
        };
    }
}
