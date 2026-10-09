using CsCheck;
using MMLib.Alvo.Expressions.Internal;
using System.Text;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The text built-ins never throw for any string — lone surrogates included — and agree with a reference definition.</summary>
public sealed class CelBuiltInPropertyTests
{
    private const int Iterations = 5_000;
    private const string AsciiWhitespace = " \t\n\r";

    [Fact]
    public void Trim_never_leaves_ascii_whitespace_at_an_end_and_is_idempotent() =>
        Gen.String.Sample(text =>
        {
            var trimmed = CelBuiltInFunctions.TrimText(text);

            (trimmed.Length == 0 || (!AsciiWhitespace.Contains(trimmed[0]) && !AsciiWhitespace.Contains(trimmed[^1]))).ShouldBeTrue();
            text.Contains(trimmed, StringComparison.Ordinal).ShouldBeTrue();
            CelBuiltInFunctions.TrimText(trimmed).ShouldBe(trimmed);
        },
        iter: Iterations);

    [Fact]
    public void Trim_returns_the_input_unchanged_when_neither_end_is_ascii_whitespace() =>
        Gen.Char[" \t\n\r\u00a0\u2003ab"].Array[1, 6].Select(characters => new string(characters))
            .Where(text => text.Length > 0 && !AsciiWhitespace.Contains(text[0]) && !AsciiWhitespace.Contains(text[^1]))
            .Sample(text => CelBuiltInFunctions.TrimText(text).ShouldBe(text), iter: Iterations);

    [Fact]
    public void Trim_removes_exactly_the_ascii_whitespace_run_at_each_end() =>
        Gen.Select(Whitespace, Gen.String, Whitespace).Sample(
            sample =>
            {
                var core = sample.Item2.Trim(' ', '\t', '\n', '\r');

                CelBuiltInFunctions.TrimText(sample.Item1 + core + sample.Item3).ShouldBe(core);
            },
            iter: Iterations);

    private static Gen<string> Whitespace => Gen.Char[AsciiWhitespace].Array[0, 4].Select(characters => new string(characters));

    [Fact]
    public void Size_equals_a_code_point_scan() =>
        Gen.String.Sample(text => CelBuiltInFunctions.SizeOf(text).ShouldBe(CodePoints(text)), iter: Iterations);

    [Fact]
    public void Replace_agrees_with_a_left_to_right_scan() =>
        Gen.Select(Text(0, 40), Text(1, 3), Text(0, 3)).Sample(
            sample => CelBuiltInFunctions.ReplaceText(sample.Item1, sample.Item2, sample.Item3)
                .ShouldBe(Scan(sample.Item1, sample.Item2, sample.Item3)),
            iter: Iterations);

    /// <summary>Text over a three-letter alphabet, so searches actually match.</summary>
    private static Gen<string> Text(int shortest, int longest) =>
        Gen.Char["abc"].Array[shortest, longest].Select(characters => new string(characters));

    private static long CodePoints(string text)
    {
        long count = 0;
        for (var index = 0; index < text.Length; index++, count++)
        {
            if (char.IsSurrogatePair(text, index))
            {
                index++;
            }
        }

        return count;
    }

    private static string Scan(string text, string search, string replacement)
    {
        var built = new StringBuilder();
        for (var at = 0; at < text.Length;)
        {
            var matches = at + search.Length <= text.Length && string.CompareOrdinal(text, at, search, 0, search.Length) == 0;
            built.Append(matches ? replacement : text[at].ToString());
            at += matches ? search.Length : 1;
        }

        return built.ToString();
    }

    [Fact]
    public void Substring_agrees_with_a_code_point_list_for_every_range_inside_the_text() =>
        Gen.String.SelectMany(text =>
            {
                var size = (int)CelBuiltInFunctions.SizeOf(text);
                return Gen.Int[0, size].SelectMany(start => Gen.Int[start, size].Select(end => (text, start, end)));
            })
            .Sample(
                sample =>
                {
                    var runes = sample.text.EnumerateRunes().Select(rune => rune.ToString()).ToList();
                    var expected = string.Concat(runes.Skip(sample.start).Take(sample.end - sample.start));
                    return SameCodePoints(CelBuiltInFunctions.SubstringText(sample.text, sample.start, sample.end), expected);
                },
                iter: Iterations);

    [Fact]
    public void String_then_timestamp_is_the_same_instant() =>
        Gen.DateTimeOffset.Sample(value => CelBuiltInFunctions.TimestampOf(CelBuiltInFunctions.StringOf(value)) == value, iter: 2_000);

    [Fact]
    public void String_then_int_is_the_same_whole_number() =>
        Gen.Long.Sample(value => CelBuiltInFunctions.IntOf(CelBuiltInFunctions.StringOf(value)) == value, iter: 2_000);

    /// <summary>Equal by code point; <c>EnumerateRunes</c> maps a lone surrogate to U+FFFD on both sides, as <c>size</c> counts it.</summary>
    private static bool SameCodePoints(string actual, string expected) =>
        actual.EnumerateRunes().Select(rune => rune.Value).SequenceEqual(expected.EnumerateRunes().Select(rune => rune.Value));
}
