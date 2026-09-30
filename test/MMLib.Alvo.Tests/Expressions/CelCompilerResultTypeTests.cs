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

    /// <summary>
    /// A rule wrapped whole in quotes is a string, and the refusal says to remove the quotes — not to add a comparison it
    /// already has (spec §8.1 step 3; the three shapes the RCA's model sent).
    /// </summary>
    [Theory]
    [InlineData("'owner_id == @user.id'", "owner_id == @user.id")]
    [InlineData("\"owner_id == @user.id\"", "owner_id == @user.id")]
    [InlineData("'true'", "true")]
    public void A_predicate_wrapped_whole_in_quotes_is_refused_with_the_quotes_named(string source, string content)
    {
        var result = _compiler.Compile(source, CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Message.ShouldStartWith("A Rule expression must evaluate to a boolean");
        error.Message.ShouldContain(source);
        error.FixSuggestion.ShouldBe(CelCompiler.QuotedFixLead + content);
    }

    /// <summary>A quoted string whose content is no predicate keeps the generic fix: there is nothing to unwrap to.</summary>
    /// <remarks>
    /// Regression pins: both pass before D42 too. The second is a quoted predicate nested in quotes — its content
    /// <c>'true'</c> is itself only a string, and the inner compile runs with the quote check off, so it is never
    /// unwrapped twice. That bound is by construction (<c>detectQuoted: false</c>), and this pins its visible half.
    /// </remarks>
    [Theory]
    [InlineData("'admin'")]
    [InlineData("\"'true'\"")]
    public void A_quoted_string_that_is_no_quoted_predicate_keeps_the_generic_fix(string source) =>
        _compiler.Compile(source, CelProfile.Rule, CelFixtures.Orders).Errors.ShouldHaveSingleItem()
            .FixSuggestion.ShouldNotStartWith("Remove the outer quotes");

    /// <summary>Access and Condition are predicates too, and get the same refusal.</summary>
    [Theory]
    [InlineData(CelProfile.Access)]
    [InlineData(CelProfile.Condition)]
    public void Every_predicate_profile_names_a_quoted_predicate(CelProfile profile) =>
        _compiler.Compile("'true'", profile, CelFixtures.Orders).Errors.ShouldHaveSingleItem()
            .FixSuggestion.ShouldBe(CelCompiler.QuotedFixLead + "true");

    /// <summary>Every result-type refusal quotes the source it refused, cut at a bound so a long one cannot flood the answer.</summary>
    [Fact]
    public void A_result_type_refusal_echoes_its_source_and_cuts_a_long_one()
    {
        var longSource = "'" + new string('x', 300) + "'";

        _compiler.Compile("total", CelProfile.Rule, CelFixtures.Orders).Errors.Single().Message.ShouldContain("total");
        var echoed = _compiler.Compile(longSource, CelProfile.Condition, CelFixtures.Orders).Errors.Single().Message;
        echoed.ShouldContain(longSource[..CelCompiler.EchoLength] + "…");
        echoed.ShouldNotContain(longSource);
    }

    /// <summary>The unwrapped content in the fix is capped like the echo (pre-flight M3).</summary>
    [Fact]
    public void A_long_quoted_predicate_is_capped_in_the_fix()
    {
        var content = string.Concat(Enumerable.Repeat("owner_id == @user.id || ", 6)) + "true";

        var fix = _compiler.Compile("'" + content + "'", CelProfile.Rule, CelFixtures.Orders).Errors.ShouldHaveSingleItem().FixSuggestion;

        fix.ShouldBe(CelCompiler.QuotedFixLead + content[..CelCompiler.EchoLength] + "…");
    }

    /// <summary>The echo turns a control character into a space, so a refusal is one line of plain text (pre-flight L1).</summary>
    [Fact]
    public void The_echo_replaces_control_characters_with_a_space() =>
        _compiler.Compile("title\n+\t'a\tb'", CelProfile.Condition, CelFixtures.Orders).Errors
            .ShouldContain(error => error.Message.EndsWith("The expression: title + 'a b'.", StringComparison.Ordinal));

    /// <summary>A cut never splits a surrogate pair: the echo ends on a whole character (pre-flight L1).</summary>
    [Fact]
    public void The_echo_never_ends_on_half_a_surrogate_pair()
    {
        var source = "'" + new string('x', CelCompiler.EchoLength - 2) + "\U0001F600" + new string('y', 20) + "'";

        var message = _compiler.Compile(source, CelProfile.Condition, CelFixtures.Orders).Errors.ShouldHaveSingleItem().Message;

        message.ShouldContain(source[..(CelCompiler.EchoLength - 1)] + "…");
        message.Where((character, index) => char.IsHighSurrogate(character)
            && (index + 1 == message.Length || !char.IsLowSurrogate(message[index + 1]))).ShouldBeEmpty();
    }

    /// <summary>A quoted string stays a legitimate Computed or Mutate value: no unwrap is offered there.</summary>
    /// <remarks>A regression pin: it passes before D42 too, and holds the accepted set still under Mutate.</remarks>
    [Fact]
    public void A_quoted_string_is_never_refused_under_mutate()
    {
        var result = _compiler.Compile("'owner_id == @user.id'", CelProfile.Mutate, CelFixtures.Orders);

        result.IsSuccess.ShouldBeTrue();
    }
}
