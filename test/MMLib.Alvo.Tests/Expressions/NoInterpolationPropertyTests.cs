using CsCheck;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Proves the DoD "no interpolation" property (<c>baas-analyza.md</c> §2.1): no literal from a
/// rule's source ever appears in the rendered SQL text, for arbitrary generated literals and for an
/// injection payload attempted through every operator the grammar allows (<c>== != &lt; &lt;= &gt;
/// &gt;= in has</c>). Where an operator's type rules make it structurally impossible for attacker
/// text to reach a literal at all, that impossibility is asserted explicitly rather than silently
/// skipped, so the coverage gap does not hide behind a passing test.
/// </summary>
public class NoInterpolationPropertyTests
{
    private const string Payload = "x'; DROP TABLE orders; --";

    private static readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();

#pragma warning disable CA1859
    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();
#pragma warning restore CA1859

    private static readonly Gen<string> _literals =
        Gen.Char["abcXYZ01_ '\"%;-()"].Array[1, 12].Select(characters => new string(characters));

    /// <summary>
    /// CEL escapes a quote inside a single-quoted string literal with a backslash (see
    /// <c>CelLexer.ReadEscape</c>: <c>\'</c> → <c>'</c>), not SQL's doubled-quote convention. Using
    /// SQL-style doubling here would make most quote-bearing generated literals fail to parse (the
    /// lexer would read the doubled quote as "end of string" immediately followed by a new,
    /// unterminated string), so the property would vacuously skip exactly the inputs its own
    /// character set was designed to exercise.
    /// </summary>
    private static string EscapeForCel(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    [Fact]
    public void No_literal_from_a_rule_ever_appears_in_the_rendered_sql()
    {
        _literals.Sample(literal =>
        {
            var result = CelFixtures.Compiler.Compile(
                $"status == '{EscapeForCel(literal)}'", CelProfile.Rule, CelFixtures.Orders);
            if (!result.IsSuccess)
            {
                return true;
            }

            var predicate = _renderer.Render(result.Expression!, CelFixtures.Alice, _fields);

            return !predicate.Sql.Contains(literal, StringComparison.Ordinal)
                && predicate.Parameters.Values.Contains(literal);
        },
        iter: 10_000);
    }

    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    public void An_equality_injection_attempt_against_a_string_field_stays_inside_a_parameter(string op)
    {
        var result = CelFixtures.Compiler.Compile(
            $"title {op} '{EscapeForCel(Payload)}'", CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeTrue($"'{op}' against a String field must compile so the injection attempt is actually exercised.");

        var predicate = _renderer.Render(result.Expression!, CelFixtures.Alice, _fields);

        predicate.Sql.ShouldNotContain("DROP", Case.Insensitive);
        predicate.Parameters.Values.ShouldContain(Payload);
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    public void A_relational_injection_attempt_against_a_string_field_is_rejected_at_compile_time(string op)
    {
        // IMPORTANT 6 (Task 8): relational operators on a string are collation-dependent and
        // rejected outside the Computed profile — so there is no way for a string literal, let
        // alone one carrying a payload, to reach a relational comparison in a Rule at all. The
        // defense here is the type checker, not the renderer.
        var result = CelFixtures.Compiler.Compile(
            $"title {op} '{EscapeForCel(Payload)}'", CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse("relational operators on a string must be rejected in the Rule profile.");
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    public void A_relational_operator_against_a_decimal_field_cannot_carry_a_text_payload(string op)
    {
        // total (Decimal) is exactly the field type that makes a relational operator legal, but the
        // CEL lexer's decimal-literal grammar only accepts digits and a single '.' — it has no
        // syntax through which arbitrary text could ever occupy this literal's slot, so an
        // injection attempt here fails to *parse*, not merely to type-check.
        var result = CelFixtures.Compiler.Compile(
            $"total {op} 5; DROP TABLE orders; --", CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse("a decimal literal has no grammar slot for injected text.");

        var legitimate = CelFixtures.Compiler.Compile($"total {op} 5", CelProfile.Rule, CelFixtures.Orders);
        legitimate.IsSuccess.ShouldBeTrue("the same operator against a bare decimal literal must still compile.");
    }

    [Fact]
    public void A_timestamp_field_has_no_literal_syntax_so_no_comparison_can_carry_a_payload()
    {
        // created_at (Timestamp) is comparable with every relational operator too, but CEL has no
        // timestamp literal syntax at all — the only way to populate one side of such a comparison
        // is another Timestamp-typed field. A string literal (even a well-formed ISO-8601 one)
        // cannot be compared against it (Timestamp vs String is rejected as a type mismatch), so
        // there is no literal slot here for attacker text to occupy, injection payload or not.
        var result = CelFixtures.Compiler.Compile(
            "created_at == '2024-01-01T00:00:00Z'; DROP TABLE orders; --", CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse("a string literal cannot be compared against a Timestamp field.");

        var fieldToField = CelFixtures.Compiler.Compile("created_at == approved_at", CelProfile.Rule, CelFixtures.Orders);
        fieldToField.IsSuccess.ShouldBeTrue("a Timestamp field compared against another Timestamp field must still compile.");
    }

    [Fact]
    public void A_role_membership_literal_is_resolved_at_render_time_and_never_reaches_the_sql_or_a_parameter()
    {
        // 'x' in @user.roles is decided entirely by the renderer, from the known AlvoContext, so
        // the literal never becomes a bind parameter either — there is nothing left to leak.
        var escaped = EscapeForCel(Payload);
        var result = CelFixtures.Compiler.Compile($"'{escaped}' in @user.roles", CelProfile.Rule, CelFixtures.Orders);
        result.IsSuccess.ShouldBeTrue();

        var predicate = _renderer.Render(result.Expression!, CelFixtures.Alice, _fields);

        predicate.Sql.ShouldNotContain("DROP", Case.Insensitive);
        predicate.Sql.ShouldBe("FALSE");
        predicate.Parameters.Values.ShouldNotContain(Payload);
    }

    [Fact]
    public void Has_takes_no_literal_operand_so_it_cannot_carry_an_injection_payload()
    {
        // has(field) only ever accepts a schema-resolved field name — there is no literal slot for
        // attacker-controlled text to occupy in the first place, so this documents the coverage
        // rather than exercising an injection attempt that cannot syntactically exist.
        var result = CelFixtures.Compiler.Compile("has(title)", CelProfile.Rule, CelFixtures.Orders);
        result.IsSuccess.ShouldBeTrue();

        var predicate = _renderer.Render(result.Expression!, CelFixtures.Alice, _fields);

        predicate.Sql.ShouldBe("(\"title\" IS NOT NULL)");
        predicate.Parameters.ShouldBeEmpty();
    }
}
