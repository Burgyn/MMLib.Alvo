using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A catalogued name parses as an N-ary call; anything else is the same syntax-time refusal as before.</summary>
public sealed class CelCatalogCallParsingTests
{
    private static readonly CelFunctionCatalog _catalog = TestCelFunctions.With(
        TestCelFunctions.Echo,
        TestCelFunctions.Pair,
        TestCelFunctions.Host("normalizePhone", CelValueType.String, arguments => arguments[0], TestCelFunctions.Parameter("phone", CelValueType.String)));

    [Fact]
    public void A_catalogued_function_parses_with_every_argument_in_order()
    {
        var call = CelParser.Parse("pair(title, 'x')", _catalog).ShouldBeOfType<CelCall>();

        call.Name.ShouldBe("pair");
        call.Arguments.Count.ShouldBe(2);
        call.Arguments[0].ShouldBeOfType<CelFieldRef>().FieldName.ShouldBe("title");
        call.Arguments[1].ShouldBeOfType<CelLiteral>().Value.ShouldBe("x");
    }

    [Theory]
    [InlineData("echo()", 0)]
    [InlineData("echo (title)", 1)]
    [InlineData("echo(new.title)", 1)]
    [InlineData("pair(echo(title), old.title)", 2)]
    [InlineData("echo(title == 'x')", 1)]
    public void An_argument_is_any_expression_and_arity_is_left_to_the_checker(string source, int arguments) =>
        CelParser.Parse(source, _catalog).ShouldBeOfType<CelCall>().Arguments.Count.ShouldBe(arguments);

    [Theory]
    [InlineData("echo(")]
    [InlineData("echo(title")]
    [InlineData("echo(title,")]
    [InlineData("echo(,)")]
    [InlineData("echo(title,)")]
    [InlineData("echo(title))")]
    public void An_unbalanced_or_empty_argument_list_is_a_syntax_error(string source) =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog));

    [Fact]
    public void Each_call_level_counts_against_the_nesting_cap()
    {
        string Nested(int depth) => string.Concat(Enumerable.Repeat("echo(", depth)) + "title" + new string(')', depth);

        Should.NotThrow(() => CelParser.Parse(Nested(CelParser.MaxDepth), _catalog));
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(Nested(CelParser.MaxDepth + 1), _catalog))
            .Message.ShouldStartWith($"CEL expression nests {CelParser.MaxDepth + 1} levels deep");
    }

    [Fact]
    public void A_source_of_calls_over_the_length_limit_is_refused_before_it_is_read()
    {
        var source = "pair(" + string.Join(", ", Enumerable.Repeat("title", 400)) + ")";

        source.Length.ShouldBeGreaterThan(CelParser.MaxSourceLength);
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog)).Message.ShouldContain("exceeding the maximum of 2000");
    }

    [Fact]
    public void An_unknown_function_keeps_its_message_and_names_the_closest_and_every_known_function()
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse("normalisePhone(title)", _catalog));

        refused.Message.ShouldBe("'normalisePhone' is not a recognized function.");
        refused.FixSuggestion.ShouldNotBeNull().ShouldContain("Did you mean 'normalizePhone'?");
        refused.FixSuggestion.ShouldContain($"Known functions: {string.Join(", ", _catalog.Names)}.");
        refused.FixSuggestion.ShouldContain("echo");
        refused.FixSuggestion.ShouldContain("normalizePhone");
        refused.FixSuggestion.ShouldContain("AddCelFunction");
    }

    [Fact]
    public void The_catalog_free_overload_knows_the_built_ins_only() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("echo(title)")).Message.ShouldBe("'echo' is not a recognized function.");

    [Theory]
    [InlineData("all(f, f > 0)")]
    [InlineData("exists(f)")]
    [InlineData("exists_one(f)")]
    [InlineData("map(f)")]
    [InlineData("filter(f)")]
    public void A_comprehension_macro_keeps_the_hook_fix(string source) =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog)).FixSuggestion.ShouldNotBeNull().ShouldContain("hooks.beforeUpdate");

    [Fact]
    public void Lower_keeps_the_lower_ascii_fix() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("lower(title)", _catalog)).FixSuggestion.ShouldNotBeNull().ShouldContain("lowerAscii(");

    [Theory]
    [InlineData("title.echo()")]
    [InlineData("math.echo(title)")]
    public void A_receiver_or_namespaced_spelling_of_a_function_is_told_the_call_shape(string source)
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog));

        refused.Message.ShouldStartWith("Alvo has no nested field access");
        refused.FixSuggestion.ShouldNotBeNull().ShouldContain("echo(x)");
    }

    /// <summary>
    /// The field-only calls refused a nested call with a bare token mismatch and no fix (final review, M3). The
    /// message and position stay exactly what they were — the corpus pins them — and the fix is now real.
    /// </summary>
    [Theory]
    [InlineData("has(trim(title))", 8, "has takes one field reference, never a call", "has(field)")]
    [InlineData("changed(trim(title))", 12, "changed takes one field reference, never a call", "trim(old.field) != trim(new.field)")]
    public void A_call_inside_a_field_only_call_is_refused_with_a_fix(string source, int position, string lead, string recipe)
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source, _catalog));

        refused.Message.ShouldBe("Expected RightParen but found LeftParen.");
        refused.Position.ShouldBe(position);
        refused.FixSuggestion.ShouldNotBeNull().ShouldStartWith(lead);
        refused.FixSuggestion.ShouldContain(recipe);
    }

    [Fact]
    public void A_name_without_parentheses_is_a_field_even_when_a_function_has_that_name() =>
        CelParser.Parse("echo", _catalog).ShouldBeOfType<CelFieldRef>().FieldName.ShouldBe("echo");

    [Fact]
    public void Now_keeps_its_narrow_grammar() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("now(title)", _catalog));

    [Fact]
    public void A_unicode_name_is_simply_not_a_known_function() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("normalizéPhone(title)", _catalog))
            .Message.ShouldBe("'normalizéPhone' is not a recognized function.");
}
