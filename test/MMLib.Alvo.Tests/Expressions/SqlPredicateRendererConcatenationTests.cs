using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// How the scalar (Computed) path renders CEL's string <c>+</c> and the text constants it joins — through the two
/// <see cref="IFieldSqlRenderer"/> members a dialect owns, never spelled by the core.
/// </summary>
/// <remarks>
/// <b>The one place a value from the source reaches SQL text, and why it is bounded.</b> A generated column is DDL,
/// which has no bind-parameter form, so a text constant in a value position of a computed expression is written
/// inline — only through <see cref="IFieldSqlRenderer.RenderStringLiteral"/>, only on this entry point, and only
/// when the dialect answers one. Every predicate keeps binding its values (the no-interpolation property), and a
/// dialect that declines falls back to a parameter, which the validator then refuses.
/// </remarks>
public class SqlPredicateRendererConcatenationTests
{
    private readonly SqlPredicateRenderer _renderer = new();

    [Fact]
    public void A_concatenation_renders_through_the_dialects_operator_with_the_constant_inline()
    {
        var scalar = Render("first_name + ' ' + last_name", new TestFieldSqlRenderer());

        scalar.Sql.ShouldBe("((\"first_name\" || ' ') || \"last_name\")");
        scalar.Parameters.ShouldBeEmpty("a generated column has no bind parameters to carry");
    }

    /// <summary>
    /// The operator and the literal are the dialect's: a third engine that spells either differently renders its
    /// own, with no branch in the core.
    /// </summary>
    [Fact]
    public void A_dialect_that_spells_concatenation_differently_renders_its_own()
        => Render("first_name + ' ' + last_name", new TSqlFieldSqlRenderer()).Sql
            .ShouldBe("CONCAT(CONCAT([first_name], N' '), [last_name])");

    /// <summary>
    /// A dialect that did not decide its literal quoting is not given the standard's by assumption — the port's
    /// default answers <see langword="null"/>, and the constant is bound, which a generated column cannot carry.
    /// </summary>
    [Fact]
    public void A_dialect_that_declines_the_literal_gets_a_bound_parameter_instead()
    {
        var scalar = Render("first_name + ' ' + last_name", new PortDefaultsRenderer());

        scalar.Sql.ShouldBe("((\"first_name\" || @p0) || \"last_name\")");
        scalar.Parameters.Values.ShouldBe([" "]);
    }

    /// <summary>The explicit fallback the null rule asks for renders as a <c>CASE</c> whose branches are the join's operands.</summary>
    [Fact]
    public void The_explicit_fallback_renders_as_a_case_over_the_presence_test()
        => Render("(has(middle_name) ? middle_name : '') + last_name", new TestFieldSqlRenderer()).Sql.ShouldBe(
            "((CASE WHEN (\"middle_name\" IS NOT NULL) THEN \"middle_name\" ELSE '' END) || \"last_name\")");

    /// <summary>Arithmetic <c>+</c> is untouched: the node is the same, the operand types decide.</summary>
    [Fact]
    public void Numeric_plus_still_renders_as_arithmetic()
        => Render("visits + visits", new TestFieldSqlRenderer()).Sql.ShouldBe("(\"visits\" + \"visits\")");

    /// <summary>
    /// The inline path belongs to the scalar entry point alone: a rule's string constant is still a parameter, and
    /// the literal member is never even asked.
    /// </summary>
    [Fact]
    public void A_predicate_never_asks_for_an_inline_literal()
    {
        var rule = CelFixtures.Compiler.Compile(
            "first_name == 'Jana' || last_name == 'x\\'; DROP TABLE customers; --'",
            CelProfile.Rule,
            CelStringConcatenationTests.Customers);
        rule.IsSuccess.ShouldBeTrue();

        var predicate = _renderer.Render(rule.Expression!, CelFixtures.Alice, new LiteralRefusingRenderer(), "p");

        predicate.Parameters.Count.ShouldBe(2);
        predicate.Sql.ShouldNotContain("Jana");
    }

    private SqlExpression Render(string source, IFieldSqlRenderer fields)
    {
        var result = CelFixtures.Compiler.Compile(source, CelProfile.Computed, CelStringConcatenationTests.Customers);
        result.IsSuccess.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.Message)));
        return _renderer.Render(result.Expression!, fields);
    }

    /// <summary>Implements only what the port requires, so every default interface member answers as shipped.</summary>
    private sealed class PortDefaultsRenderer : IFieldSqlRenderer
    {
        public string TrueLiteral => "TRUE";

        public string FalseLiteral => "FALSE";

        public string RenderField(EntitySchema entity, string fieldName) => $"\"{fieldName}\"";

        public string RenderParameter(string parameterName) => "@" + parameterName;

        public string RenderCaseInsensitiveLike(string left, string right) => $"{left} ILIKE {right}";
    }

    /// <summary><see cref="TestFieldSqlRenderer"/>, except that being asked for an inline literal is a test failure.</summary>
    private sealed class LiteralRefusingRenderer : IFieldSqlRenderer
    {
        private readonly TestFieldSqlRenderer _inner = new();

        public string TrueLiteral => _inner.TrueLiteral;

        public string FalseLiteral => _inner.FalseLiteral;

        public string RenderField(EntitySchema entity, string fieldName) => _inner.RenderField(entity, fieldName);

        public string RenderParameter(string parameterName) => _inner.RenderParameter(parameterName);

        public string RenderCaseInsensitiveLike(string left, string right) => _inner.RenderCaseInsensitiveLike(left, right);

        public string? RenderStringLiteral(string value) =>
            throw new InvalidOperationException("A predicate asked for an inline literal.");
    }
}
