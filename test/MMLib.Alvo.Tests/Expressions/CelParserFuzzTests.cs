using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public class CelParserFuzzTests
{
    private const string Alphabet = "abc_ 01'\"()[]{}.,:;?!<>=&|+-*/%@\\\n\t";

    private static readonly string[] _keywordFragments =
    [
        "has(", "changed(", "@user.id", "@user.roles", "@user.role", "@user.claims['x']",
        "@tenant.id", "old.status", "new.status", " in ", "true", "false", "null",
        "owner_id", "'x'", "(", ")", "&&", "==", "?", ":",
    ];

    [Fact]
    public void Arbitrary_text_either_parses_or_raises_a_cel_syntax_error()
    {
        Gen.Char[Alphabet].Array[0, 60]
            .Select(characters => new string(characters))
            .Sample(source => ParseOrRejectCleanly(source), iter: 20_000);
    }

    [Fact]
    public void Keyword_bearing_fragments_either_parse_or_raise_a_cel_syntax_error()
    {
        Gen.OneOfConst(_keywordFragments).Array[1, 20]
            .Select(string.Concat)
            .Sample(source => ParseOrRejectCleanly(source), iter: 20_000);
    }

    [Fact]
    public void Deeply_nested_generated_input_never_stack_overflows()
    {
        Gen.Int[1, 400].Sample(
            depth => ParseOrRejectCleanly(new string('!', depth) + "a"),
            iter: 200);
    }

    [Fact]
    public void Five_thousand_nested_parens_reject_cleanly()
    {
        Should.Throw<CelSyntaxException>(
            () => CelParser.Parse(new string('(', 5000) + "a" + new string(')', 5000)));
    }

    [Fact]
    public void Five_thousand_chained_negations_reject_cleanly()
    {
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(new string('!', 5000) + "a"));
    }

    [Fact]
    public void Nesting_over_the_depth_cap_but_under_the_length_cap_rejects_independently()
    {
        var source = new string('(', 50) + "a" + new string(')', 50);
        source.Length.ShouldBeLessThan(CelParser.MaxSourceLength);

        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));
    }

    private static void ParseOrRejectCleanly(string source)
    {
        try
        {
            CelParser.Parse(source);
        }
        catch (CelSyntaxException)
        {
        }
    }
}
