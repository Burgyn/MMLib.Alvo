using CsCheck;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The SQL standard's string literal (SQL:2016 Part 2, §5.3 <c>&lt;character string literal&gt;</c>: single quotes,
/// a quote inside written as two), which a computed field's text constants are written into DDL with.
/// </summary>
/// <remarks>
/// The property decodes the literal with the standard's own rule and asserts it is exactly one token whose value is
/// the input — no text the generator can produce closes the literal early. The engines' agreement with that rule is
/// proved per driver against a real engine.
/// </remarks>
public class AlvoSqlStringLiteralTests
{
    private static readonly Gen<string> _hostileText =
        Gen.OneOf(
            Gen.Char["abcXYZ01_ '\"%;-()\\/*$ENé中ž"],
            Gen.Char['\u00A0', '\uD7FF'],
            Gen.Const('\'')).Array[0, 32].Select(characters => new string(characters));

    [Fact]
    public void Every_text_decodes_back_to_itself_from_exactly_one_token()
    {
        long quoted = 0;

        _hostileText.Sample(
            value =>
            {
                if (!AlvoSqlStringLiteral.TryQuote(value, out var literal))
                {
                    return false;
                }

                Interlocked.Increment(ref quoted);
                return DecodeStandard(literal) == value;
            },
            iter: 10_000);

        quoted.ShouldBe(10_000, "every generated value is text a literal carries, so none may be declined");
    }

    [Theory]
    [InlineData("Jana Nováková", "'Jana Nováková'")]
    [InlineData("", "''")]
    [InlineData("O'Brien", "'O''Brien'")]
    [InlineData("a\\", "'a\\'")]
    [InlineData("'; DROP TABLE customers; --", "'''; DROP TABLE customers; --'")]
    public void A_literal_is_the_standards_spelling(string value, string expected)
    {
        AlvoSqlStringLiteral.TryQuote(value, out var literal).ShouldBeTrue();
        literal.ShouldBe(expected);
    }

    /// <summary>
    /// A control character or an unpaired surrogate is declined: not text a DDL statement carries, and a surrogate on
    /// its own has no UTF-8 encoding, so the engine would store something that is not the input.
    /// </summary>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x09)]
    [InlineData(0x1F)]
    [InlineData(0x7F)]
    [InlineData(0x9F)]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void Text_a_literal_cannot_carry_is_declined(int codeUnit)
    {
        AlvoSqlStringLiteral.TryQuote($"a{(char)codeUnit}b", out var literal).ShouldBeFalse();
        literal.ShouldBeNull();
    }

    [Fact]
    public void A_surrogate_pair_is_text_and_is_carried()
    {
        AlvoSqlStringLiteral.TryQuote("🚲", out var literal).ShouldBeTrue();
        literal.ShouldBe("'🚲'");
    }

    /// <summary>
    /// The standard's decoder, and the no-breakout check at once: it fails (returns <see langword="null"/>) on
    /// anything that is not one token — a quote that closes the literal before its end included.
    /// </summary>
    private static string? DecodeStandard(string literal)
    {
        if (literal.Length < 2 || literal[0] != '\'' || literal[^1] != '\'')
        {
            return null;
        }

        var decoded = new StringBuilder();
        for (var index = 1; index < literal.Length - 1; index++)
        {
            if (literal[index] == '\'')
            {
                if (literal[index + 1] != '\'' || index + 1 == literal.Length - 1)
                {
                    return null;
                }

                index++;
            }

            decoded.Append(literal[index]);
        }

        return decoded.ToString();
    }
}
