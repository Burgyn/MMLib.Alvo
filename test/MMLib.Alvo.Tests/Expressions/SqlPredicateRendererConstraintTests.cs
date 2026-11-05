using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

public sealed class SqlPredicateRendererConstraintTests
{
    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();

    private static string Constraint(string rule) => _renderer.RenderConstraint(CelFixtures.CompileRule(rule), EpochFields.Instance);

    [Theory]
    [InlineData("total >= 0", "COALESCE(\"total\" >= 0, FALSE)")]
    [InlineData("total <= 12.5", "COALESCE(\"total\" <= 12.5, FALSE)")]
    [InlineData("title != 'void'", "COALESCE(\"title\" <> 'void', FALSE)")]
    [InlineData("status != 'draft'", "COALESCE(\"status\" <> 'draft', FALSE)")]
    [InlineData("title != \"it's\"", "COALESCE(\"title\" <> 'it''s', FALSE)")]
    [InlineData("is_public == true", "COALESCE(\"is_public\" = TRUE, FALSE)")]
    [InlineData("total >= -40", "COALESCE(\"total\" >= -40, FALSE)")]
    [InlineData("total >= -0.5", "COALESCE(\"total\" >= -0.5, FALSE)")]
    [InlineData("approved_at - created_at <= duration('1h')", "COALESCE((EPOCH_US(\"approved_at\") - EPOCH_US(\"created_at\")) <= 3600000000, FALSE)")]
    [InlineData("has(title) && title == 'open'", "((\"title\" IS NOT NULL) AND COALESCE(\"title\" = 'open', FALSE))")]
    public void Every_literal_is_written_inline_and_nothing_is_bound(string rule, string sql) => Constraint(rule).ShouldBe(sql);

    /// <summary>A negative constant beside a minus keeps its space: <c>--</c> would open a SQL comment.</summary>
    [Theory]
    [InlineData("approved_at - created_at >= duration('-1h')", "COALESCE((EPOCH_US(\"approved_at\") - EPOCH_US(\"created_at\")) >= -3600000000, FALSE)")]
    [InlineData("created_at - duration('-1h') <= approved_at", "COALESCE((EPOCH_US(\"created_at\") - -3600000000) <= EPOCH_US(\"approved_at\"), FALSE)")]
    [InlineData("total >= -9223372036854775808", "COALESCE(\"total\" >= -9223372036854775808, FALSE)")]
    public void A_negative_constant_never_opens_a_comment(string rule, string sql) =>
        Constraint(rule).ShouldSatisfyAllConditions(rendered => rendered.ShouldBe(sql), rendered => rendered.ShouldNotContain("--"));

    /// <summary>A bound instant does not make <c>now()</c> a constant: the stored constraint would freeze it.</summary>
    [Fact]
    public void Now_is_refused_even_with_an_instant_bound()
    {
        using var bound = AlvoFlow.EnterInstant(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));

        Should.Throw<NotSupportedException>(() => Constraint("created_at + duration('1h') <= now()")).Message.ShouldContain("now()");
    }

    [Fact]
    public void Only_a_rule_renders_as_a_constraint() =>
        Should.Throw<InvalidOperationException>(() => _renderer.RenderConstraint(CelFixtures.CompileCondition("new.total >= 0"), EpochFields.Instance));

    [Fact]
    public void A_renderer_that_does_not_implement_constraints_refuses() =>
        Should.Throw<NotSupportedException>(() => ((IPredicateRenderer)new NoConstraints()).RenderConstraint(CelFixtures.CompileRule("total >= 0"), EpochFields.Instance))
            .Message.ShouldContain(nameof(NoConstraints));

    [Theory]
    [InlineData("owner_id == @user.id")]
    [InlineData("tenant_id == @tenant.id")]
    [InlineData("'admin' in @user.roles")]
    [InlineData("created_at <= now()")]
    public void Anything_with_no_constant_value_refuses_to_render(string rule) =>
        Should.Throw<NotSupportedException>(() => Constraint(rule));

    [Fact]
    public void Text_the_dialect_declines_refuses_to_render() =>
        Should.Throw<NotSupportedException>(() => _renderer.RenderConstraint(CelFixtures.CompileRule("title != 'a\u0001b'"), EpochFields.Instance));

    [Theory]
    [InlineData(CelProfile.Rule)]
    [InlineData(CelProfile.Condition)]
    public void A_negative_constant_is_one_literal_in_every_row_profile(CelProfile profile) =>
        CelFixtures.Compiler.Compile("total >= -40", profile, CelFixtures.Orders)
            .Expression.ShouldNotBeNull().Root.ShouldBeOfType<CelBinary>().Right.ShouldBe(new CelLiteral(CelValueType.Int, -40L));

    [Fact]
    public void The_smallest_long_folds_and_one_past_it_is_refused()
    {
        CelFixtures.CompileRule("total >= -9223372036854775808").Root.ShouldBeOfType<CelBinary>().Right.ShouldBe(new CelLiteral(CelValueType.Int, long.MinValue));
        CelFixtures.Compiler.Compile("total >= -9223372036854775809", CelProfile.Rule, CelFixtures.Orders).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("total >= -(-9223372036854775808)")]
    [InlineData("total >= 9223372036854775808")]
    public void A_magnitude_past_the_largest_long_is_refused(string rule) =>
        CelFixtures.Compiler.Compile(rule, CelProfile.Rule, CelFixtures.Orders).IsSuccess.ShouldBeFalse();

    [Theory]
    [InlineData("total >= -(-40)", 40L)]
    [InlineData("total >= -09223372036854775808", long.MinValue)]
    public void A_nested_or_padded_negative_constant_folds_too(string rule, long value) =>
        CelFixtures.CompileRule(rule).Root.ShouldBeOfType<CelBinary>().Right.ShouldBe(new CelLiteral(CelValueType.Int, value));

    private sealed class NoConstraints : IPredicateRenderer
    {
        public SqlPredicate Render(CompiledExpression expression, AlvoContext context, IFieldSqlRenderer fields, string parameterPrefix = "alvo_p") =>
            throw new NotImplementedException();

        public SqlExpression Render(CompiledExpression expression, IFieldSqlRenderer fields) => throw new NotImplementedException();
    }
}
