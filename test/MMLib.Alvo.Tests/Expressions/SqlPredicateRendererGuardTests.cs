using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// What the renderer refuses, and what each refusal has to say to be actionable: the argument
/// contract both entry points hold callers to, the profile mismatch that names the expression and the
/// entry point to use instead, the parameter prefix that reaches the SQL text unparameterized, and the
/// two constructs (<c>in</c> against something that is not the role set, a non-comparison operator in
/// predicate position) that must fail loudly rather than be answered from the wrong thing. The shapes
/// the compiler cannot produce are built as trees by hand — that is the only way to reach a guard whose
/// whole job is to hold when an earlier layer stops.
/// </summary>
public class SqlPredicateRendererGuardTests
{
    private readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();
    private readonly SqlPredicateRenderer _renderer = new();

    [Fact]
    public void A_null_expression_is_refused_as_an_argument()
        => Should.Throw<ArgumentNullException>(() => _renderer.Render(null!, CelFixtures.Alice, _fields, "p"));

    /// <summary>
    /// The context is guarded at the entry point rather than where it is first read: a predicate that
    /// happens not to reference <c>@user</c>/<c>@tenant</c> would otherwise render happily against no
    /// caller at all, and the next predicate that does reference one would fail somewhere deeper.
    /// </summary>
    [Fact]
    public void A_null_context_is_refused_even_by_a_predicate_that_never_reads_it()
        => Should.Throw<ArgumentNullException>(
            () => _renderer.Render(CelFixtures.CompileRule("true"), null!, _fields, "p"));

    [Fact]
    public void A_null_field_renderer_is_refused_as_an_argument()
        => Should.Throw<ArgumentNullException>(
            () => _renderer.Render(CelFixtures.CompileRule("true"), CelFixtures.Alice, null!, "p"));

    [Fact]
    public void The_scalar_entry_point_refuses_a_null_field_renderer_too()
        => Should.Throw<ArgumentNullException>(
            () => _renderer.Render(CelFixtures.CompileComputed("total + 1"), (IFieldSqlRenderer)null!));

    /// <summary>
    /// A profile mismatch quotes the expression it refused. The sentence around it is prose; which
    /// expression was refused is the part a caller holding several compiled rules acts on.
    /// </summary>
    [Fact]
    public void The_predicate_entry_point_names_the_computed_expression_it_refused()
    {
        var refused = Should.Throw<InvalidOperationException>(
            () => _renderer.Render(CelFixtures.CompileComputed("total + 1"), CelFixtures.Alice, _fields, "p"));

        refused.Message.ShouldContain("total + 1");
    }

    /// <summary>
    /// And the scalar entry point names the other entry point's own signature, not just "the other one" —
    /// the two overloads differ by their parameters, so the parameter list is the fix suggestion.
    /// </summary>
    [Fact]
    public void The_scalar_entry_point_names_the_entry_point_a_rule_expression_belongs_to()
    {
        var refused = Should.Throw<InvalidOperationException>(
            () => _renderer.Render(CelFixtures.CompileRule("owner_id == @user.id"), _fields));

        refused.Message.ShouldContain("owner_id == @user.id");
        refused.Message.ShouldContain("(expression, context, fields)");
    }

    /// <summary>
    /// The rejected prefix is quoted back. It is the one string in this call that reaches the SQL text
    /// unparameterized, so a provider deriving one from caller-influenced input has to be able to see
    /// which value was refused.
    /// </summary>
    [Fact]
    public void A_rejected_parameter_prefix_is_quoted_back_and_bound_to_its_parameter_name()
    {
        var refused = Should.Throw<ArgumentException>(() => _renderer.Render(
            CelFixtures.CompileRule("owner_id == @user.id"), CelFixtures.Alice, _fields, "p; DROP TABLE orders --"));

        refused.ParamName.ShouldBe("parameterPrefix");
        refused.Message.ShouldContain("p; DROP TABLE orders --");
    }

    /// <summary>
    /// A node the predicate path cannot render is refused by its node kind, and the refusal names it:
    /// <c>changed(...)</c> is legal in a hook condition and has no SQL at all, so an operator who sees
    /// the failure has to be told which construct in the rule the renderer could not take.
    /// </summary>
    [Fact]
    public void An_unrenderable_node_is_refused_by_its_node_kind()
    {
        var refused = Should.Throw<NotSupportedException>(
            () => _renderer.Render(CelFixtures.CompileCondition("changed(status)"), CelFixtures.Alice, _fields, "p"));

        refused.Message.ShouldContain(nameof(CelChanged));
    }

    /// <summary>
    /// Every binary that is not <c>&amp;&amp;</c>/<c>||</c>/<c>in</c> falls through to the comparison
    /// renderer, so an arithmetic node arriving there must be refused by operator rather than rendered
    /// with whatever the switch's last arm happens to be. The type checker keeps arithmetic out of the
    /// Rule profile, so the tree is built by hand.
    /// </summary>
    [Fact]
    public void An_arithmetic_operator_in_predicate_position_is_refused_and_names_the_operator()
    {
        var root = new CelBinary(
            CelBinaryOperator.Add,
            new CelFieldRef("total", CelValueType.Decimal, CelRecordState.Current),
            new CelLiteral(CelValueType.Int, 1L));

        var refused = Should.Throw<NotSupportedException>(
            () => _renderer.Render(RuleExpression(root), CelFixtures.Alice, _fields, "p"));

        refused.Message.ShouldContain(nameof(CelBinaryOperator.Add));
    }

    /// <summary>
    /// <c>in</c> is role membership and the renderer answers it from the caller's role set without reading
    /// the right operand at all — so a right operand that is not <c>@user.roles</c> must be refused,
    /// naming the only operand both backends can answer. Held from the renderer's own side: with the guard
    /// gone this exact tree renders <c>TRUE</c> for a caller who holds the named role, which is the failure
    /// mode a second string-list context value (<c>@user.claims</c>) would introduce.
    /// </summary>
    [Fact]
    public void Membership_against_anything_but_the_role_set_is_refused_naming_the_role_set()
    {
        var root = new CelBinary(
            CelBinaryOperator.In,
            new CelLiteral(CelValueType.String, "admin"),
            new CelContextRef(CelContextValue.UserId, CelValueType.Uuid));

        var refused = Should.Throw<NotSupportedException>(
            () => _renderer.Render(RuleExpression(root), CelFixtures.Admin, _fields, "p"));

        refused.Message.ShouldContain("@user.roles");
    }

    /// <summary>
    /// A boolean literal in <em>value</em> position renders as the dialect's own literal rather than a bind
    /// parameter — and the two literals must not be swapped: <c>is_public == false</c> rendered with
    /// <c>TRUE</c> matches exactly the rows the rule excludes.
    /// </summary>
    [Theory]
    [InlineData("is_public == true", "COALESCE(\"is_public\" = TRUE, FALSE)")]
    [InlineData("is_public == false", "COALESCE(\"is_public\" = FALSE, FALSE)")]
    public void A_boolean_literal_operand_renders_as_the_dialects_literal(string source, string expectedSql)
    {
        var predicate = _renderer.Render(CelFixtures.CompileRule(source), CelFixtures.Alice, _fields, "p");

        predicate.Sql.ShouldBe(expectedSql);
        predicate.Parameters.ShouldBeEmpty();
    }

    /// <summary>
    /// The type a comparison is repaired at falls back to the other operand when one side carries no type
    /// of its own; the dialect repairs <em>both</em> sides by that one type, so the fallback decides how
    /// the row is compared. Unreachable through the compiler — it refuses a comparison against a null
    /// literal outright (deviation 10) — so this pins the defence-in-depth arm, with a recording renderer
    /// because the repair a wrong type produces is invisible in the SQL of a non-decimal comparison.
    /// </summary>
    [Fact]
    public void An_untyped_operand_takes_the_other_operands_type_for_the_dialects_repair()
    {
        var recording = new PromotionRecordingRenderer();
        var root = new CelBinary(
            CelBinaryOperator.Equal,
            new CelLiteral(CelValueType.Null, null),
            new CelFieldRef("title", CelValueType.String, CelRecordState.Current));

        _renderer.Render(RuleExpression(root), CelFixtures.Alice, recording, "p");

        recording.PromotedTypes.ShouldHaveSingleItem().ShouldBe(CelValueType.String);
    }

    /// <summary>
    /// The other half of the same fallback: an operand that carries a type of its own is the one the
    /// comparison is repaired at — the fallback to the other side applies only where there is no type to
    /// use, never as the general rule.
    /// </summary>
    [Fact]
    public void A_typed_operand_decides_the_repair_type_rather_than_the_other_side()
    {
        var recording = new PromotionRecordingRenderer();
        var root = new CelBinary(
            CelBinaryOperator.Equal,
            new CelFieldRef("id", CelValueType.Uuid, CelRecordState.Current),
            new CelLiteral(CelValueType.String, "not-a-uuid"));

        _renderer.Render(RuleExpression(root), CelFixtures.Alice, recording, "p");

        recording.PromotedTypes.ShouldHaveSingleItem().ShouldBe(CelValueType.Uuid);
    }

    private static CompiledExpression RuleExpression(CelNode root) =>
        new(root, CelProfile.Rule, CelValueType.Bool, "hand-built tree", CelFixtures.Orders);

    /// <summary>
    /// <see cref="TestFieldSqlRenderer"/>'s behaviour plus a record of the type each comparison was
    /// repaired at — the one thing the rendered text of a non-decimal comparison does not show.
    /// </summary>
    private sealed class PromotionRecordingRenderer : IFieldSqlRenderer
    {
        private readonly TestFieldSqlRenderer _inner = new();

        public List<CelValueType> PromotedTypes { get; } = [];

        public string TrueLiteral => _inner.TrueLiteral;

        public string FalseLiteral => _inner.FalseLiteral;

        public string RenderField(EntitySchema entity, string fieldName) => _inner.RenderField(entity, fieldName);

        public string RenderParameter(string parameterName) => _inner.RenderParameter(parameterName);

        public string RenderCaseInsensitiveLike(string left, string right) => _inner.RenderCaseInsensitiveLike(left, right);

        public (string Left, string Right) RenderComparableOperands(string left, string right, CelValueType type)
        {
            PromotedTypes.Add(type);
            return _inner.RenderComparableOperands(left, right, type);
        }
    }
}
