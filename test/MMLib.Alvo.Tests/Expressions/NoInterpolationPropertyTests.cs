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
/// skipped, so the coverage gap does not hide behind a passing test. Every case that compiles also
/// asserts <c>IsSuccess</c> (or counts renders against the iteration count) so an escaping bug that
/// made compilation start failing could never turn this suite vacuously green again.
/// </summary>
public class NoInterpolationPropertyTests
{
    private const string Payload = "x'; DROP TABLE orders; --";

    private static readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();
    private static readonly SqlPredicateRenderer _renderer = new();

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

    /// <summary>
    /// Compiles against <c>title</c> (<see cref="MMLib.Alvo.Schema.FieldType.String"/>), not
    /// <c>status</c> (an <c>Enum</c> field whose declared values share no character with this
    /// generator's alphabet) — every generated literal must compile so the renderer is genuinely
    /// exercised on all 10,000 iterations, never silently skipped past an early <c>!IsSuccess</c>
    /// return. The render count is asserted separately from the per-sample property itself, so a
    /// regression that made compilation start failing (and the property vacuously pass by skipping)
    /// would be caught here instead.
    /// </summary>
    /// <remarks>
    /// Asserts the rendered SQL is <b>byte-identical</b> across every generated literal, rather than
    /// checking the literal's absence by substring search: a substring check gives false positives
    /// whenever a short literal happens to coincide with structural SQL text this generator's own
    /// character set can produce (a bare digit inside the auto-generated <c>@p0</c> placeholder, a
    /// bare space or parenthesis that is also part of <c>COALESCE(...)</c>'s own syntax). Proving the
    /// SQL text never changes, no matter what the literal is, is both a stronger and a
    /// false-positive-free proof that the literal never influenced it.
    /// </remarks>
    [Fact]
    public void No_literal_from_a_rule_ever_appears_in_the_rendered_sql()
    {
        const string ExpectedSql = "COALESCE(\"title\" = @p0, FALSE)";
        long rendered = 0;

        _literals.Sample(literal =>
        {
            var result = CelFixtures.Compiler.Compile(
                $"title == '{EscapeForCel(literal)}'", CelProfile.Rule, CelFixtures.Orders);
            result.IsSuccess.ShouldBeTrue($"'{literal}' against a String field must always compile.");

            var predicate = _renderer.Render(result.Expression!, CelFixtures.Alice, _fields);
            Interlocked.Increment(ref rendered);

            return predicate.Sql == ExpectedSql && predicate.Parameters.Values.Contains(literal);
        },
        iter: 10_000);

        rendered.ShouldBe(10_000);
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

    /// <summary>
    /// Relational operators on a string are collation-dependent and rejected outside the Computed
    /// profile (Task 8, IMPORTANT 6) — so there is no way for a string literal, let alone one
    /// carrying a payload, to reach a relational comparison in a Rule at all. The defense here is the
    /// type checker, not the renderer.
    /// </summary>
    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    public void A_relational_injection_attempt_against_a_string_field_is_rejected_at_compile_time(string op)
    {
        var result = CelFixtures.Compiler.Compile(
            $"title {op} '{EscapeForCel(Payload)}'", CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse("relational operators on a string must be rejected in the Rule profile.");
    }

    /// <summary>
    /// <c>total</c> (Decimal) is exactly the field type that makes a relational operator legal, but
    /// the CEL lexer's decimal-literal grammar only accepts digits and a single <c>.</c> — it has no
    /// syntax through which arbitrary text could ever occupy this literal's slot, so an injection
    /// attempt here fails to <i>parse</i>, not merely to type-check.
    /// </summary>
    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    public void A_relational_operator_against_a_decimal_field_cannot_carry_a_text_payload(string op)
    {
        var result = CelFixtures.Compiler.Compile(
            $"total {op} 5; DROP TABLE orders; --", CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse("a decimal literal has no grammar slot for injected text.");

        var legitimate = CelFixtures.Compiler.Compile($"total {op} 5", CelProfile.Rule, CelFixtures.Orders);
        legitimate.IsSuccess.ShouldBeTrue("the same operator against a bare decimal literal must still compile.");
    }

    [Fact]
    public void A_timestamp_field_has_no_literal_syntax_so_no_comparison_can_carry_a_payload()
    {
        var result = CelFixtures.Compiler.Compile(
            "created_at == '2024-01-01T00:00:00Z'; DROP TABLE orders; --", CelProfile.Rule, CelFixtures.Orders);

        result.IsSuccess.ShouldBeFalse("a string literal cannot be compared against a Timestamp field.");

        var fieldToField = CelFixtures.Compiler.Compile("created_at == approved_at", CelProfile.Rule, CelFixtures.Orders);
        fieldToField.IsSuccess.ShouldBeTrue("a Timestamp field compared against another Timestamp field must still compile.");
    }

    /// <summary>
    /// <c>'x' in @user.roles</c> is decided entirely by the renderer, from the known
    /// <see cref="AlvoContext"/>, so the literal never becomes a bind parameter either — there is
    /// nothing left to leak.
    /// </summary>
    [Fact]
    public void A_role_membership_literal_is_resolved_at_render_time_and_never_reaches_the_sql_or_a_parameter()
    {
        var escaped = EscapeForCel(Payload);
        var result = CelFixtures.Compiler.Compile($"'{escaped}' in @user.roles", CelProfile.Rule, CelFixtures.Orders);
        result.IsSuccess.ShouldBeTrue();

        var predicate = _renderer.Render(result.Expression!, CelFixtures.Alice, _fields);

        predicate.Sql.ShouldNotContain("DROP", Case.Insensitive);
        predicate.Sql.ShouldBe("FALSE");
        predicate.Parameters.Values.ShouldNotContain(Payload);
    }

    /// <summary>
    /// <c>has(field)</c> only ever accepts a schema-resolved field name — there is no literal slot
    /// for attacker-controlled text to occupy in the first place, so this documents the coverage
    /// rather than exercising an injection attempt that cannot syntactically exist.
    /// </summary>
    [Fact]
    public void Has_takes_no_literal_operand_so_it_cannot_carry_an_injection_payload()
    {
        var result = CelFixtures.Compiler.Compile("has(title)", CelProfile.Rule, CelFixtures.Orders);
        result.IsSuccess.ShouldBeTrue();

        var predicate = _renderer.Render(result.Expression!, CelFixtures.Alice, _fields);

        predicate.Sql.ShouldBe("(\"title\" IS NOT NULL)");
        predicate.Parameters.ShouldBeEmpty();
    }
}
