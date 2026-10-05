using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// cel-go's math functions are namespaced globals — <c>math.abs(x)</c> — and the catalog, not the grammar, decides
/// whether <c>a.b(</c> is one (spec §6.1, E2). Everything that was a field or a refusal before stays one.
/// </summary>
public sealed class CelQualifiedNameParsingTests
{
    private static readonly EntitySchema _withMath = TestCelFunctions.Items with
    {
        Fields = [.. TestCelFunctions.Items.Fields, new FieldSchema { Name = "math", Type = FieldType.Decimal, Precision = 18, Scale = 2, Nullable = true }],
    };

    [Theory]
    [InlineData("math.abs(qty)", "math.abs", 1)]
    [InlineData("math.round(price)", "math.round", 1)]
    [InlineData("math .round ( price )", "math.round", 1)]
    [InlineData("math.abs(math.round(price))", "math.abs", 1)]
    public void A_catalogued_qualified_name_parses_as_one_call(string source, string name, int arguments)
    {
        var call = CelParser.Parse(source).ShouldBeOfType<CelCall>();

        call.Name.ShouldBe(name);
        call.Arguments.Count.ShouldBe(arguments);
    }

    [Fact]
    public void A_field_named_like_the_namespace_is_still_a_field() =>
        new CelCompiler().Compile("math.round(math)", CelProfile.Mutate, _withMath).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("abs(qty)", "'abs' is not a recognized function.", "math.abs")]
    [InlineData("round(price)", "'round' is not a recognized function.", "math.round")]
    public void The_bare_names_are_gone_and_say_where_they_went(string source, string message, string suggestion)
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));

        refused.Message.ShouldBe(message);
        refused.FixSuggestion.ShouldNotBeNull().ShouldContain(suggestion);
    }

    [Fact]
    public void An_unknown_member_of_the_namespace_gets_did_you_mean()
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse("math.rond(price)"));

        refused.Message.ShouldStartWith("Alvo has no nested field access");
        refused.FixSuggestion.ShouldNotBeNull().ShouldContain("Did you mean 'math.round'?");
    }

    [Fact]
    public void Old_and_new_followed_by_a_function_name_are_still_field_paths() =>
        Should.Throw<CelSyntaxException>(() => CelParser.Parse("old.trim(qty)"))
            .Message.ShouldBe("Expected EndOfInput but found LeftParen.");

    [Theory]
    [InlineData("new.name.trim()", "Write trim(new.name)")]
    [InlineData("old.name.upperAscii()", null)]
    public void A_receiver_call_after_an_image_is_told_the_global_form(string source, string? fix)
    {
        var refused = Should.Throw<CelSyntaxException>(() => CelParser.Parse(source));

        refused.Message.ShouldBe("Alvo has no nested field access beyond old./new.; use a single field name.");
        if (fix is null)
        {
            refused.FixSuggestion.ShouldBeNull("upperAscii is not catalogued until Task 2");
        }
        else
        {
            refused.FixSuggestion.ShouldNotBeNull().ShouldStartWith(fix);
        }
    }

    [Fact]
    public void A_host_cannot_take_the_namespace_word() =>
        Should.Throw<ArgumentException>(() => HostCelFunction.Create("math", (string s) => s, null))
            .Message.ShouldContain("namespace of CEL's math functions");

    [Fact]
    public void A_host_may_take_a_bare_math_name_and_its_own_function_wins()
    {
        var catalog = CelFunctionCatalog.BuiltIns.With([HostCelFunction.Create("abs", (string s) => s, null)]);

        CelParser.Parse("abs(name)", catalog).ShouldBeOfType<CelCall>().Name.ShouldBe("abs");
    }
}
