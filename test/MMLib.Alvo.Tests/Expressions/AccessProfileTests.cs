using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// The <see cref="CelProfile.Access"/> profile, one fact per construct.
/// </summary>
/// <remarks>
/// <b>The point is that the profile is closed, not that these particular constructs were thought
/// of.</b> <c>docs/architecture/cel.md</c> states the rule the whole table rests on: a construct kind
/// missing from <c>_allowedProfiles</c> compiles in <em>no</em> profile rather than in every profile. So
/// each refusal below is one cell of that table asserted from outside it, and a widening — a later PR
/// adding <see cref="CelProfile.Access"/> to a row — fails exactly the fact that named the cell.
/// </remarks>
public class AccessProfileTests
{
    private static readonly EntitySchema _project = new() { Name = "<project>", Fields = [] };

    private static readonly EntitySchema _deals = new()
    {
        Name = "deals",
        Fields =
        [
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid },
            new FieldSchema { Name = "amount", Type = FieldType.Integer },
            new FieldSchema { Name = "stage", Type = FieldType.String },
        ],
    };

    // ---- what the profile admits -------------------------------------------------------------

    /// <summary>
    /// Every construct the fifth column admits.
    /// </summary>
    /// <remarks>
    /// <b>The two equality cases compare literals, and that is the whole of what equality can do here
    /// today</b> — <c>@user.id</c> has no <c>Uuid</c>-typed operand to meet (see
    /// <see cref="A_level_cannot_yet_compare_the_callers_id_to_a_literal"/>) and there is no row. It is
    /// admitted anyway, deliberately: the frozen schema promises a level may read <c>@user.id</c>, and
    /// widening <c>@user</c> is additive, so admitting the operator now is what makes a level written
    /// against a future typed claim compile without the table changing. Deny-by-default costs nothing
    /// here because the operator can express nothing a role membership could not.
    /// </remarks>
    /// <param name="source">The access-level source that must compile.</param>
    [Theory]
    [InlineData("true")]
    [InlineData("'manager' in @user.roles")]
    [InlineData("!('guest' in @user.roles)")]
    [InlineData("'manager' in @user.roles && 'finance' in @user.roles")]
    [InlineData("'sales' in @user.roles || 'manager' in @user.roles")]
    [InlineData("true == true")]
    [InlineData("true != false")]
    public void The_profile_admits_the_constructs_a_level_is_written_from(string source)
    {
        var result = Compile(source, _project);

        result.IsSuccess.ShouldBeTrue(Report(result));
        result.Expression.ShouldNotBeNull().ResultType.ShouldBe(CelValueType.Bool);
    }

    /// <summary>
    /// <b><c>@user.id</c> is admitted by the profile and has nothing to compare against — a grammar gap,
    /// not a profile refusal, and the distinction is the point.</b> The frozen schema names
    /// <c>@user.id</c> as one of the two members a level may read, so the table admits it; but Alvo's
    /// grammar has no <c>uuid</c> literal (a quoted value is always <c>String</c>), and an access level
    /// sees no row, so there is no <c>Uuid</c>-typed operand in scope to compare it to. The refusal an
    /// author gets therefore comes from the type checker's comparison rule, not from the profile table,
    /// and it names both types.
    /// </summary>
    /// <remarks>
    /// Recorded as a fact rather than as a comment because the fix is additive — a <c>uuid</c> literal, or
    /// typed claims — and on the day either lands this fact fails, which is exactly when the limitation
    /// should be re-read. Refusing <c>@user.id</c> in the profile instead would have been the wrong
    /// closure: the schema promises it, and a level would then be refused for naming something the schema
    /// says it may name.
    /// </remarks>
    [Fact]
    public void A_level_cannot_yet_compare_the_callers_id_to_a_literal()
    {
        var result = Compile("@user.id == '00000000-0000-0000-0000-000000000001'", _project);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(
            message => message.Contains("Cannot compare Uuid to String", StringComparison.Ordinal),
            "the refusal must come from the comparison rule, naming both types — not from the profile "
            + "table, which admits @user.id because the frozen schema names it");
    }

    // ---- what the profile refuses, one fact per construct ------------------------------------

    [Fact]
    public void A_current_row_field_reference_does_not_compile()
    {
        var result = Compile("owner_id == @user.id", _deals);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(
            message => message.Contains("field reference", StringComparison.OrdinalIgnoreCase),
            "an access level has no row to read, so a field reference would compile and then have "
            + "nothing to evaluate against — the silent failure the profile table exists to prevent");
    }

    [Fact]
    public void An_old_or_new_qualified_field_reference_does_not_compile()
    {
        Compile("new.stage == 'won'", _deals).IsSuccess.ShouldBeFalse();
        Compile("old.stage == 'won'", _deals).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Has_does_not_compile()
    {
        var result = Compile("has(owner_id)", _deals);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(message => message.Contains("has(", StringComparison.Ordinal));
    }

    [Fact]
    public void Changed_does_not_compile()
    {
        var result = Compile("changed(stage)", _deals);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(message => message.Contains("changed(", StringComparison.Ordinal));
    }

    [Fact]
    public void Arithmetic_does_not_compile()
    {
        Compile("1 + 1 == 2", _project).IsSuccess.ShouldBeFalse();
        Compile("-1 == -1", _project).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void The_ternary_does_not_compile()
    {
        var result = Compile("'manager' in @user.roles ? true : false", _project);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(message => message.Contains("ternary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_allow_listed_function_call_does_not_compile()
    {
        Compile("lowerAscii('X') == 'x'", _project).IsSuccess.ShouldBeFalse();
        Compile("now() == now()", _project).IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// <b><c>@tenant</c> is the one refusal that is a decision rather than an absence.</b> An access
    /// level is project-scoped by the frozen schema's own description, so admitting <c>@tenant</c>
    /// would make a project-level predicate answer differently per request — which is neither what the
    /// block says nor something an operator could reason about.
    /// </summary>
    [Fact]
    public void A_tenant_context_reference_does_not_compile()
    {
        var result = Compile("@tenant.id == @user.id", _project);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(
            message => message.Contains("project-scoped", StringComparison.Ordinal),
            "the refusal has to say why, or an author reads it as an oversight and files an issue");
    }

    [Fact]
    public void A_level_that_is_not_a_predicate_does_not_compile()
    {
        var result = Compile("'manager'", _project);

        result.IsSuccess.ShouldBeFalse();
        Messages(result).ShouldContain(message => message.Contains("boolean", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The profile is interpreter-only, exactly as <see cref="CelProfile.Mutate"/> is, and the renderer
    /// refuses it <b>by profile</b> rather than by node kind — every construct an access level can
    /// contain is one the renderer would otherwise render perfectly well, into SQL over a row that does
    /// not exist.
    /// </summary>
    [Fact]
    public void The_sql_renderer_refuses_an_access_expression_by_name()
    {
        var expression = Compile("'manager' in @user.roles", _project).Expression.ShouldNotBeNull();
        var renderer = new SqlPredicateRenderer();

        var refusal = Should.Throw<NotSupportedException>(
            () => renderer.Render(expression, AlvoContext.Anonymous, new TestFieldSqlRenderer()));

        refusal.Message.ShouldContain(nameof(CelProfile.Access));
    }

    /// <summary>
    /// And the scalar entry point refuses it too, for the reason its own guard's message gives: that
    /// message sends a caller to the predicate entry point, which would then refuse them again.
    /// </summary>
    [Fact]
    public void The_scalar_entry_point_refuses_an_access_expression_by_name()
    {
        var expression = Compile("'manager' in @user.roles", _project).Expression.ShouldNotBeNull();
        var renderer = new SqlPredicateRenderer();

        var refusal = Should.Throw<NotSupportedException>(
            () => renderer.Render(expression, new TestFieldSqlRenderer()));

        refusal.Message.ShouldContain(nameof(CelProfile.Access));
    }

    private static CelCompilationResult Compile(string source, EntitySchema entity) =>
        new CelCompiler().Compile(source, CelProfile.Access, entity);

    private static IReadOnlyList<string> Messages(CelCompilationResult result) =>
        [.. result.Errors.Select(error => error.Message)];

    private static string Report(CelCompilationResult result) =>
        string.Join(" | ", Messages(result));
}
