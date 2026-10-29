using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>A literal text test's value as a <c>LIKE</c> pattern, escaped so that every character in it matches itself.</summary>
/// <remarks>
/// <para>
/// The escape character is <c>\</c>, bound as a parameter beside the pattern — never written into the statement — and
/// <c>\</c>, <c>%</c>, <c>_</c> and <c>[</c> are escaped. <c>[</c> means nothing to SQLite's or PostgreSQL's <c>LIKE</c>, where
/// any character after the escape matches itself, so escaping it costs nothing there and closes T-SQL's character class
/// before a T-SQL driver exists. The prior art is EF Core's own <c>StartsWith</c> translation: an escaped pattern parameter
/// with <c>ESCAPE '\'</c>.
/// </para>
/// <para>
/// Case-sensitivity is <c>LIKE</c>'s: case-sensitive on PostgreSQL, and on SQLite because its driver turns
/// <c>case_sensitive_like</c> on for every connection — the same guarantee <see cref="AlvoFilterOperator.Like"/> rests on.
/// </para>
/// </remarks>
internal static class LiteralTextPattern
{
    /// <summary>The escape character, bound as the <c>ESCAPE</c> operand.</summary>
    internal const string Escape = "\\";

    /// <summary>Whether <paramref name="op"/> is one of the three literal text tests.</summary>
    /// <param name="op">The operator.</param>
    internal static bool IsLiteralTextTest(AlvoFilterOperator op) =>
        op is AlvoFilterOperator.StartsWith or AlvoFilterOperator.EndsWith or AlvoFilterOperator.Contains;

    /// <summary>The pattern <paramref name="op"/> matches <paramref name="value"/> with: the escaped value, a <c>%</c> on its open side.</summary>
    /// <param name="op">A literal text test.</param>
    /// <param name="value">The caller's text.</param>
    internal static string For(AlvoFilterOperator op, string value)
    {
        var escaped = Escaped(value);

        return op switch
        {
            AlvoFilterOperator.StartsWith => escaped + "%",
            AlvoFilterOperator.EndsWith => "%" + escaped,
            AlvoFilterOperator.Contains => "%" + escaped + "%",
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Only a literal text test has a literal pattern."),
        };
    }

    /// <summary><paramref name="value"/> with every <c>\</c>, <c>%</c>, <c>_</c> and <c>[</c> preceded by the escape character.</summary>
    /// <param name="value">The caller's text.</param>
    internal static string Escaped(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var escaped = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (character is '\\' or '%' or '_' or '[')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
