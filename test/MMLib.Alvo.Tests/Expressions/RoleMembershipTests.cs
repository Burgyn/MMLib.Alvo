using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Both <c>in</c> backends answer role membership from <see cref="AlvoContext.Roles"/> and never look at
/// the expression's right operand at all — correct only because the type checker admits no
/// <see cref="CelValueType.StringList"/> producer other than <c>@user.roles</c>. That is an assumption
/// about a *different* file, so both backends assert it through this one shared guard rather than each
/// leaving it implicit: the day a second string-list context value lands (<c>@user.claims</c>,
/// <c>@user.teams</c> — tracked by #37), membership against it must fail loudly instead of being
/// silently answered from the role set.
/// </summary>
public class RoleMembershipTests
{
    [Fact]
    public void The_role_set_is_the_one_accepted_right_operand()
    {
        Should.NotThrow(() => RoleMembership.RequireUserRolesOperand(
            new CelContextRef(CelContextValue.UserRoles, CelValueType.StringList)));
    }

    [Fact]
    public void Another_context_value_as_the_right_operand_is_refused()
    {
        Should.Throw<NotSupportedException>(() => RoleMembership.RequireUserRolesOperand(
            new CelContextRef(CelContextValue.UserId, CelValueType.Uuid)));
    }

    [Fact]
    public void A_row_field_as_the_right_operand_is_refused()
    {
        Should.Throw<NotSupportedException>(() => RoleMembership.RequireUserRolesOperand(
            new CelFieldRef("status", CelValueType.String, CelRecordState.Current)));
    }

    [Fact]
    public void A_literal_as_the_right_operand_is_refused()
    {
        Should.Throw<NotSupportedException>(() => RoleMembership.RequireUserRolesOperand(
            new CelLiteral(CelValueType.String, "editor")));
    }

    /// <summary>
    /// The guard must not disturb the shape both backends actually see: a role literal decided at render
    /// time, and a row field tested against the role set, still work through the compiler end to end.
    /// </summary>
    [Fact]
    public void The_shapes_the_compiler_can_actually_produce_still_render_and_evaluate()
    {
        var renderer = new SqlPredicateRenderer();
        var fields = new TestFieldSqlRenderer();

        renderer.Render(CelFixtures.CompileRule("'editor' in @user.roles"), CelFixtures.Editor, fields).Sql.ShouldBe("TRUE");
        renderer.Render(CelFixtures.CompileRule("status in @user.roles"), CelFixtures.Editor, fields).Sql
            .ShouldBe("COALESCE(\"status\" IN (@p0, @p1), FALSE)");

        CelInterpreter.EvaluatePredicate(
            CelFixtures.CompileRule("'editor' in @user.roles"), AlvoRecord.Empty, null, CelFixtures.Editor).ShouldBeTrue();
        CelInterpreter.EvaluatePredicate(
            CelFixtures.CompileRule("'editor' in @user.roles"), AlvoRecord.Empty, null, CelFixtures.Alice).ShouldBeFalse();
    }
}
