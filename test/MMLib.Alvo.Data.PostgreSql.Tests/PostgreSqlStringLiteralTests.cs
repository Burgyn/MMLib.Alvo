using CsCheck;
using MMLib.Alvo.Expressions;
using System.Text;

namespace MMLib.Alvo.Data.PostgreSql.Tests;

/// <summary>
/// PostgreSQL's text constants in a computed field's DDL are <b>escape strings</b> (<c>E'…'</c>), whose reading does
/// not depend on the server's <c>standard_conforming_strings</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the standard literal, which SQLite gets.</b> A plain <c>'…'</c> means one thing with
/// <c>standard_conforming_strings = on</c> (the default since 9.1: a backslash is an ordinary character) and another
/// with it <c>off</c> (a backslash escapes the next character). With it off, the standard spelling of
/// <c>\'); DROP TABLE t; --</c> is <c>'\''); DROP TABLE t; --'</c>, and the <c>\'</c> ends nothing while the next
/// quote ends the literal — the DDL would run the payload. An escape string is read by the same rules under either
/// setting (PostgreSQL docs §4.1.2.2), so doubling every backslash and every quote inside <c>E'…'</c> is correct
/// whatever the server is configured with, rather than correct under a documented assumption.
/// </para>
/// <para>
/// The decoder below is §4.1.2.2's, restricted to the two escapes this dialect emits; the engine's own reading, under
/// both settings, is asserted in the integration suite.
/// </para>
/// </remarks>
public class PostgreSqlStringLiteralTests
{
    private static readonly Gen<string> _hostileText =
        Gen.OneOf(
            Gen.Char["abcXYZ01_ '\"%;-()\\/*$ENé中ž"],
            Gen.Char[' ', '퟿'],
            Gen.Const('\''),
            Gen.Const('\\')).Array[0, 32].Select(characters => new string(characters));

    private readonly IFieldSqlRenderer _fields = new PostgreSqlFieldSqlRenderer();

    [Fact]
    public void Every_text_decodes_back_to_itself_from_exactly_one_escape_string()
    {
        long quoted = 0;

        _hostileText.Sample(
            value =>
            {
                if (_fields.RenderStringLiteral(value) is not { } literal)
                {
                    return false;
                }

                Interlocked.Increment(ref quoted);
                return DecodeEscapeString(literal) == value;
            },
            iter: 10_000);

        quoted.ShouldBe(10_000, "every generated value is text a literal carries, so none may be declined");
    }

    [Theory]
    [InlineData("Jana Nováková", "E'Jana Nováková'")]
    [InlineData("O'Brien", "E'O''Brien'")]
    [InlineData("a\\", "E'a\\\\'")]
    [InlineData("\\'); DROP TABLE t; --", "E'\\\\''); DROP TABLE t; --'")]
    public void A_literal_is_an_escape_string_doubling_quotes_and_backslashes(string value, string expected)
        => _fields.RenderStringLiteral(value).ShouldBe(expected);

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x0A)]
    [InlineData(0x7F)]
    [InlineData(0xD800)]
    public void Text_a_literal_cannot_carry_is_declined(int codeUnit)
        => _fields.RenderStringLiteral($"a{(char)codeUnit}b").ShouldBeNull();

    [Fact]
    public void A_concatenation_is_the_standards_operator()
        => _fields.RenderStringConcatenation("\"first_name\"", "E' '").ShouldBe("(\"first_name\" || E' ')");

    /// <summary>
    /// §4.1.2.2 for the two escapes this dialect writes (<c>''</c> and <c>\\</c>), failing — <see langword="null"/> —
    /// on anything that is not one escape string, including any other backslash sequence, which this dialect must
    /// never produce.
    /// </summary>
    private static string? DecodeEscapeString(string literal)
    {
        if (!literal.StartsWith("E'", StringComparison.Ordinal) || literal.Length < 3 || literal[^1] != '\'')
        {
            return null;
        }

        var decoded = new StringBuilder();
        for (var index = 2; index < literal.Length - 1; index++)
        {
            var current = literal[index];
            if (current is '\'' or '\\')
            {
                if (index + 1 >= literal.Length - 1 || literal[index + 1] != current)
                {
                    return null;
                }

                index++;
            }

            decoded.Append(current);
        }

        return decoded.ToString();
    }
}
