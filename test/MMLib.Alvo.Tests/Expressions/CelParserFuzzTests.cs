using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public class CelParserFuzzTests
{
    private const string Alphabet = "abc_ 01'\"()[]{}.,:;?!<>=&|+-*/%@\\\n\t";

    [Fact]
    public void Arbitrary_text_either_parses_or_raises_a_cel_syntax_error()
    {
        Gen.Char[Alphabet].Array[0, 60]
            .Select(characters => new string(characters))
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
        ParseOrRejectCleanly(new string('(', 5000) + "a" + new string(')', 5000));
    }

    [Fact]
    public void Five_thousand_chained_negations_reject_cleanly()
    {
        ParseOrRejectCleanly(new string('!', 5000) + "a");
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
