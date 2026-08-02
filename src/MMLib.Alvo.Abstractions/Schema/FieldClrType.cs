namespace MMLib.Alvo.Schema;

/// <summary>
/// The one mapping from a declared field's <see cref="FieldType"/> to the CLR type a value of that
/// field is carried as through <c>IAlvoData</c>.
/// </summary>
/// <remarks>
/// <para>
/// Shared, and public, for exactly the reason <see cref="Expressions.CelFieldType"/> is: two layers
/// must answer this identically and neither can see the other's copy. A storage driver builds its read
/// model and its bind parameters from it, and the HTTP layer binds a JSON request body with it — those
/// live in different assemblies, and the core cannot reference a provider package by design
/// (<c>SharedArchitectureRules.Core_depends_only_on_Abstractions</c>). It belongs in the ports because
/// it is not one backend's opinion: it is the contract <c>IAlvoData</c> publishes to callers, stated in
/// that port's own remarks — a <c>uuid</c> field reads back as a <see cref="Guid"/>, a timestamp as a
/// <see cref="DateTimeOffset"/>, a decimal as a <see cref="decimal"/>.
/// </para>
/// <para>
/// A second copy is not merely duplication. PR3's first pass had one here and one in the EF package,
/// and <b>they already disagreed on failure mode</b> — the driver threw
/// <see cref="NotSupportedException"/> for a type it does not map while the HTTP copy laundered the
/// same condition into a client 422, telling a caller to fix a request that was fine. That is the
/// defect class this type exists to make unrepresentable.
/// </para>
/// <para>
/// <b>Nullability is deliberately not modelled here.</b> Whether a column is <c>Guid</c> or
/// <c>Guid?</c> is a question about one model (a migration model must match the schema's nullability; a
/// read model wants every property nullable so a masked field can project as a typed SQL
/// <c>NULL</c>), so the wrapping stays with whoever is building that model. This answers the one
/// question they share.
/// </para>
/// </remarks>
public static class FieldClrType
{
    /// <summary>The CLR type a value of <paramref name="field"/> is carried as.</summary>
    /// <param name="field">The declared field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="field"/>'s type has no CLR mapping in this build.</exception>
    public static Type Of(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Of(field.Type);
    }

    /// <summary>The CLR type a value of a field of <paramref name="type"/> is carried as.</summary>
    /// <remarks>
    /// A type this build does not map throws rather than falling back to <see cref="object"/> or
    /// <see cref="string"/>: an unmapped field type is a broken invariant of whoever composed the
    /// schema, never a caller's mistake, and a guessed mapping is how a value silently changes type
    /// between the write path and the read path.
    /// </remarks>
    /// <param name="type">The declared field type.</param>
    /// <exception cref="NotSupportedException"><paramref name="type"/> has no CLR mapping in this build.</exception>
    public static Type Of(FieldType type) => type switch
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
}
