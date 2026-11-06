using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// A field renderer that spells nothing a dialect would: the validator's passes over <c>computed</c> and
/// <c>validation</c> ask whether an expression renders and what it binds, never what the SQL text is, so a driver's
/// quoting and casting are not their business.
/// </summary>
/// <remarks>
/// <b>The text-literal decline rule is the one thing it does not neutralise</b> (HX-11 Ruling 55). Every shipped dialect
/// writes text through the EF driver's <c>AlvoSqlStringLiteral</c>, which this assembly cannot reference, so
/// <see cref="RenderStringLiteral"/> mirrors its rule — a C0 or C1 control, U+2028/U+2029 or a lone surrogate is declined —
/// and a constant no engine can carry is refused when the descriptor is saved rather than when the migrator first builds.
/// </remarks>
internal sealed class NeutralFields : IFieldSqlRenderer
{
    /// <summary>The one instance; the renderer holds no state.</summary>
    public static NeutralFields Instance { get; } = new();

    /// <inheritdoc />
    public string TrueLiteral => "TRUE";

    /// <inheritdoc />
    public string FalseLiteral => "FALSE";

    /// <inheritdoc />
    public string RenderField(EntitySchema entity, string fieldName) => fieldName;

    /// <inheritdoc />
    public string RenderParameter(string parameterName) => parameterName;

    /// <inheritdoc />
    public string RenderCaseInsensitiveLike(string left, string right) => $"{left} LIKE {right}";

    /// <summary>Quotes <paramref name="value"/>, or declines exactly what the driver's literal writer declines.</summary>
    /// <param name="value">The text constant.</param>
    /// <returns>The quoted literal, or <see langword="null"/> when no SQL literal can carry it.</returns>
    public string? RenderStringLiteral(string value) =>
        IsLiteralText(value) ? $"'{value.Replace("'", "''", StringComparison.Ordinal)}'" : null;

    /// <inheritdoc />
    public string RenderStringConcatenation(string left, string right) => $"({left} || {right})";

    /// <inheritdoc />
    public string? RenderEpochMicroseconds(string timestampSql) => $"EPOCH_US({timestampSql})";

    private static bool IsLiteralText(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]) || value[index] is '\u2028' or '\u2029' || IsUnpairedSurrogate(value, index))
            {
                return false;
            }

            index += char.IsHighSurrogate(value[index]) ? 1 : 0;
        }

        return true;
    }

    private static bool IsUnpairedSurrogate(string value, int index) =>
        char.IsSurrogate(value[index]) && !char.IsSurrogatePair(value, index);
}
