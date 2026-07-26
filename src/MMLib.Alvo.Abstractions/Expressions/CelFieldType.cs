using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions;

/// <summary>
/// The one mapping from a declared field's <see cref="FieldType"/> to the <see cref="CelValueType"/> a
/// comparison over that field is evaluated at.
/// </summary>
/// <remarks>
/// <para>
/// Shared, and public, because two different layers must answer this identically and neither can see the
/// other's copy. The CEL type checker resolves a field reference's type with it, and a storage driver hands
/// the same type to <see cref="IFieldSqlRenderer.RenderComparableOperands"/> when it renders a <em>caller
/// filter</em> or a keyset cursor — which are not CEL, so there is no compiled expression to read a resolved
/// type off. A second copy would not merely duplicate a table: a divergence changes <b>which comparisons get
/// a dialect's value repair</b>, so a filter calling a decimal column an integer compares it
/// lexicographically on SQLite while the identical rule, going through the compiler, answers correctly. That
/// is a fail-open reintroduced by drift, which is why an agreement test between two copies was not
/// considered sufficient.
/// </para>
/// <para>
/// A <see cref="FieldType"/> this build does not know maps to <see cref="CelValueType.Json"/> — untyped,
/// therefore never repaired and never compared as a number. The type checker refuses such a field earlier,
/// so this arm exists for a caller reaching the mapping directly.
/// </para>
/// </remarks>
public static class CelFieldType
{
    /// <summary>The <see cref="CelValueType"/> a comparison over <paramref name="field"/> is evaluated at.</summary>
    /// <param name="field">The declared field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public static CelValueType Of(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Of(field.Type);
    }

    /// <summary>The <see cref="CelValueType"/> a comparison over a field of <paramref name="type"/> is evaluated at.</summary>
    /// <param name="type">The declared field type.</param>
    public static CelValueType Of(FieldType type) => type switch
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
