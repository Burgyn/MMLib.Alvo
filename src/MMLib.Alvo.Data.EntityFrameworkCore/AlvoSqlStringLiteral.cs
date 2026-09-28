using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The one implementation of the SQL standard's string-literal quoting, shared by every Alvo storage driver — the
/// text-constant counterpart of <see cref="AlvoSqlIdentifier"/>, and what a driver's
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer.RenderStringLiteral"/> is built on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The standard's rule and nothing else</b> (SQL:2016 Part 2, §5.3 <c>&lt;character string literal&gt;</c>): the
/// text between single quotes, with each quote inside written as two. SQLite reads exactly that, and treats a
/// backslash as an ordinary character. A driver whose engine reads more — PostgreSQL, whose plain literal honours
/// backslash escapes when <c>standard_conforming_strings</c> is off — builds its own spelling on top of this rather
/// than beside it, so there is still one place quotes are doubled.
/// </para>
/// <para>
/// <b>It declines what a literal must not carry</b> instead of approximating it: a control character (C0, DEL and
/// C1 — a line break or a tab included), which DDL a schema persists has no business holding, and an unpaired UTF-16
/// surrogate, which has no UTF-8 encoding — the engine would store a replacement character and the value read back
/// would not be the value authored. The CEL compiler refuses both in a computed constant already, so declining here
/// is a belt, not a second rule.
/// </para>
/// <para>
/// <b>Never for a value that can be bound.</b> Its one job is a computed field's text constant, in DDL, which has no
/// bind-parameter form; every value a statement can bind is bound. That is why it is <see langword="internal"/>,
/// shared with the two driver assemblies by <c>InternalsVisibleTo</c>, rather than public like
/// <see cref="AlvoSqlIdentifier"/>: an identifier has no bind form anywhere, a value almost always does, and a public
/// quoter would be the easy wrong path for a host's own SQL. The standard literal alone is not safe on an engine that
/// honours backslash escapes — see PostgreSQL's renderer.
/// </para>
/// </remarks>
internal static class AlvoSqlStringLiteral
{
    /// <summary>Quotes <paramref name="value"/> as one standard SQL string literal, unless it holds text a literal must not carry.</summary>
    /// <param name="value">The text to quote.</param>
    /// <param name="literal">The quoted literal, or <see langword="null"/> when <paramref name="value"/> was declined.</param>
    /// <returns><see langword="true"/> when <paramref name="literal"/> was produced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static bool TryQuote(string value, [NotNullWhen(true)] out string? literal)
    {
        ArgumentNullException.ThrowIfNull(value);

        literal = IsLiteralText(value) ? $"'{value.Replace("'", "''", StringComparison.Ordinal)}'" : null;
        return literal is not null;
    }

    private static bool IsLiteralText(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]) || IsUnpairedSurrogate(value, index))
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
