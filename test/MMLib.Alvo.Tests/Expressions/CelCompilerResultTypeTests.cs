using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The compiler's argument contract and what a result-type rejection has to carry. The sentence a
/// rejection is written in is prose; the type the expression actually evaluates to, and the presence of a
/// fix suggestion, are what an agent reads and acts on, so those are what is asserted here.
/// </summary>
public class CelCompilerResultTypeTests
{
    private readonly CelCompiler _compiler = new();

    /// <summary>
    /// A null argument is a caller defect, not an authoring error — the compiler's "no exception ever
    /// escapes for any source string" guarantee is about source strings, and must not swallow this.
    /// </summary>
    [Fact]
    public void A_null_source_is_refused_as_an_argument()
        => Should.Throw<ArgumentNullException>(() => _compiler.Compile(null!, CelProfile.Rule, CelFixtures.Orders));

    [Fact]
    public void A_null_entity_is_refused_as_an_argument()
        => Should.Throw<ArgumentNullException>(() => _compiler.Compile("true", CelProfile.Rule, null!));

    /// <summary>
    /// A result-type rejection names the type the expression evaluates to. That is the fact the author
    /// does not have — they wrote the source, not its inferred type — and the one the surrounding sentence
    /// cannot supply.
    /// </summary>
    [Theory]
    [InlineData("payload", CelProfile.Computed, "Json")]
    [InlineData("payload", CelProfile.Mutate, "Json")]
    [InlineData("total", CelProfile.Rule, "Decimal")]
    [InlineData("title", CelProfile.Condition, "String")]
    public void A_result_type_rejection_names_the_type_the_expression_evaluates_to(
        string source, CelProfile profile, string expectedType)
    {
        var result = _compiler.Compile(source, profile, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Message.Contains(expectedType, StringComparison.Ordinal));
    }

    /// <summary>
    /// And it carries a fix suggestion. Alvo's errors are read by an agent that acts on the suggestion, so
    /// an empty one is a degraded error rather than a cosmetic omission — the content of the sentence is
    /// deliberately not asserted, only that the channel is not empty.
    /// </summary>
    [Theory]
    [InlineData("total > 5", CelProfile.Computed)]
    [InlineData("payload", CelProfile.Computed)]
    [InlineData("payload", CelProfile.Mutate)]
    [InlineData("total", CelProfile.Rule)]
    [InlineData("total", CelProfile.Condition)]
    public void A_result_type_rejection_always_carries_a_fix_suggestion(string source, CelProfile profile)
    {
        var result = _compiler.Compile(source, profile, CelFixtures.Orders);

        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldAllBe(error => !string.IsNullOrWhiteSpace(error.FixSuggestion));
    }
}
