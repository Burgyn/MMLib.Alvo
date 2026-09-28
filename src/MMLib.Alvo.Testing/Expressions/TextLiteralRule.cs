namespace MMLib.Alvo.Testing;

/// <summary>
/// What the two fakes decline as a text literal — the rule <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer.RenderStringLiteral"/>'s
/// remarks give every dialect: a control character (C0, DEL, C1), a line or paragraph separator (U+2028, U+2029), or
/// an unpaired UTF-16 surrogate.
/// </summary>
internal static class TextLiteralRule
{
    /// <summary>Whether a literal may carry <paramref name="value"/>.</summary>
    public static bool IsCarried(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]) || value[index] is '\u2028' or '\u2029'
                || (char.IsSurrogate(value[index]) && !char.IsSurrogatePair(value, index)))
            {
                return false;
            }

            index += char.IsHighSurrogate(value[index]) ? 1 : 0;
        }

        return true;
    }
}
