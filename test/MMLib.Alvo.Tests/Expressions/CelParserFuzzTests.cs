using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The parser's robustness property: arbitrary input either parses or is refused with
/// <see cref="CelSyntaxException"/> — never an unexpected exception type and never a
/// <c>StackOverflowException</c>, which no <c>catch</c> could contain. A property whose body only
/// swallows the expected exception asserts nothing about the corpus it ran on, so every arm here
/// carries a <see cref="ProductionCoverage"/> counter, asserted <b>after</b> the loop: it pins that
/// the generator really spells the productions under test and really reaches both outcomes, so a
/// later narrowing of the alphabet or the fragment list fails loudly instead of turning the property
/// vacuous.
/// </summary>
public class CelParserFuzzTests
{
    /// <summary>
    /// Every character CEL's own vocabulary is spelled with — the letters of <c>has</c>,
    /// <c>changed</c>, <c>in</c>, <c>true</c>, <c>false</c>, <c>null</c>, <c>@user</c>,
    /// <c>@tenant</c>, <c>old</c> and <c>new</c> — plus digits, quotes, and every operator and
    /// bracket character the lexer knows, and a backslash and whitespace it does not treat
    /// specially. A narrower letter set (the <c>abc_</c> this started as) cannot spell a single
    /// keyword, context root, or membership operator, so the generated corpus never reaches the
    /// productions the parser is most likely to get wrong.
    /// </summary>
    private const string Alphabet = "acdefghilnorstuw_ 01'\"()[]{}.,:;?!<>=&|+-*/%@\\\n\t";

    private static readonly string[] _keywordFragments =
    [
        "has(", "changed(", "@user.id", "@user.roles", "@user.role", "@user.claims['x']",
        "@tenant.id", "old.status", "new.status", " in ", "true", "false", "null",
        "owner_id", "'x'", "(", ")", "&&", "==", "?", ":",
    ];

    /// <summary>
    /// Productions a <b>character-level</b> generator can reach. Each is two characters, so over this
    /// alphabet it occurs in hundreds of the generated sources; a three-or-more-character production
    /// (<c>has(</c>, <c>@user.</c>) is provably out of reach here — one specific 4-character window
    /// has a chance around 1 in 3 million, against roughly 600,000 windows in a whole run — which is
    /// exactly why <see cref="Keyword_bearing_fragments_either_parse_or_raise_a_cel_syntax_error"/>
    /// exists and owns those counters.
    /// </summary>
    private static readonly string[] _characterReachableProductions = ["in", "@u", "@t", "''"];

    private static readonly string[] _fragmentProductions = ["has(", "changed(", "@user.", " in ", "'x'"];

    [Fact]
    public void Arbitrary_text_either_parses_or_raises_a_cel_syntax_error()
    {
        var coverage = new ProductionCoverage(_characterReachableProductions);

        Gen.Char[Alphabet].Array[0, 60]
            .Select(characters => new string(characters))
            .Sample(source => ParseOrRejectCleanly(source, coverage), iter: 20_000);

        coverage.ShouldHaveExercisedEveryProduction();
    }

    [Fact]
    public void Keyword_bearing_fragments_either_parse_or_raise_a_cel_syntax_error()
    {
        var coverage = new ProductionCoverage(_fragmentProductions);

        Gen.OneOfConst(_keywordFragments).Array[1, 20]
            .Select(string.Concat)
            .Sample(source => ParseOrRejectCleanly(source, coverage), iter: 20_000);

        coverage.ShouldHaveExercisedEveryProduction();
    }

    /// <summary>
    /// The generated depth range must straddle <see cref="CelParser.MaxDepth"/>: without the
    /// both-outcomes counter, a range that had drifted entirely past the cap would prove only that
    /// the cap rejects, and a range entirely below it would prove only that shallow input parses.
    /// </summary>
    [Fact]
    public void Deeply_nested_generated_input_never_stack_overflows()
    {
        var coverage = new ProductionCoverage([]);

        Gen.Int[1, 400].Sample(
            depth => ParseOrRejectCleanly(new string('!', depth) + "a", coverage),
            iter: 200);

        coverage.ShouldHaveExercisedEveryProduction();
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

    private static void ParseOrRejectCleanly(string source, ProductionCoverage coverage) =>
        coverage.Observe(source, ParsesCleanly(source));

    private static bool ParsesCleanly(string source)
    {
        try
        {
            CelParser.Parse(source);
            return true;
        }
        catch (CelSyntaxException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a fuzz run actually covered: how often each production was spelled, and how often the
    /// parser accepted and refused. CsCheck may sample in parallel, so every counter moves through
    /// <see cref="Interlocked"/>.
    /// </summary>
    /// <param name="productions">The substrings the generator must be able to spell.</param>
    private sealed class ProductionCoverage(IReadOnlyList<string> productions)
    {
        private readonly long[] _spelled = new long[productions.Count];
        private long _parsed;
        private long _refused;

        public void Observe(string source, bool parsed)
        {
            if (parsed)
            {
                Interlocked.Increment(ref _parsed);
            }
            else
            {
                Interlocked.Increment(ref _refused);
            }

            CountSpelledProductions(source);
        }

        public void ShouldHaveExercisedEveryProduction()
        {
            _parsed.ShouldBeGreaterThan(0, "no generated source parsed at all, so the property only ever observed the refusal path.");
            _refused.ShouldBeGreaterThan(0, "no generated source was refused, so the property never observed the refusal path.");

            for (var index = 0; index < productions.Count; index++)
            {
                _spelled[index].ShouldBeGreaterThan(
                    0,
                    $"the generator never spelled '{productions[index]}', so no sample can have exercised it.");
            }
        }

        private void CountSpelledProductions(string source)
        {
            for (var index = 0; index < productions.Count; index++)
            {
                if (source.Contains(productions[index], StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _spelled[index]);
                }
            }
        }
    }
}
