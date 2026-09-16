using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The scalar (Computed) path's own shapes, pinned separately from the predicate path's because they are
/// separate code: the same <c>AND</c>/<c>OR</c> choice, the same boolean-constant rendering and the unary
/// minus are each composed a second time here, and a defect in one is invisible from the other.
/// </summary>
public class SqlPredicateRendererScalarShapeTests
{
    private readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();
    private readonly SqlPredicateRenderer _renderer = new();

    /// <summary>
    /// The negation has to reach the SQL: a generated column whose expression lost its minus sign stores
    /// the opposite number on every write, and no later read can tell.
    /// </summary>
    [Fact]
    public void Unary_negation_renders_its_operand_under_a_minus_sign()
        => RenderScalar("-total").Sql.ShouldBe("(-\"total\")");

    [Fact]
    public void A_negated_arithmetic_subtree_keeps_the_minus_outside_it()
        => RenderScalar("-(total + 1)").Sql.ShouldBe("(-(\"total\" + @p0))");

    /// <summary>
    /// A boolean constant in a <c>CASE WHEN</c> condition is the dialect's boolean predicate, and the two
    /// constants must not be swapped — a swapped one picks the other branch for every row.
    /// </summary>
    [Theory]
    [InlineData("true ? 1 : 2", "(CASE WHEN TRUE THEN @p0 ELSE @p1 END)")]
    [InlineData("false ? 1 : 2", "(CASE WHEN FALSE THEN @p0 ELSE @p1 END)")]
    public void A_boolean_constant_condition_renders_as_the_dialects_boolean_predicate(string source, string expectedSql)
        => RenderScalar(source).Sql.ShouldBe(expectedSql);

    /// <summary>
    /// The connective comes from the node, not from a default: rendering <c>||</c> as <c>AND</c> narrows a
    /// two-branch computed guard to a conjunction, silently changing which branch every row takes.
    /// </summary>
    [Theory]
    [InlineData("&&", "AND")]
    [InlineData("||", "OR")]
    public void The_scalar_paths_connective_is_rendered_from_the_node(string celOperator, string sqlOperator)
        => RenderScalar($"(total > 5 {celOperator} total < 10) ? 1 : 2").Sql.ShouldBe(
            "(CASE WHEN (COALESCE(CAST(\"total\" AS numeric) > CAST(@p0 AS numeric), FALSE) "
            + $"{sqlOperator} COALESCE(CAST(\"total\" AS numeric) < CAST(@p1 AS numeric), FALSE)) THEN @p2 ELSE @p3 END)");

    private SqlExpression RenderScalar(string source) => _renderer.Render(CelFixtures.CompileComputed(source), _fields);
}
