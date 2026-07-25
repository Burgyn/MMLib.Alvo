using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Pins the renderer's exact SQL shapes per node kind, per the minimal-wrapping rule: a comparison
/// wraps itself once in <c>COALESCE(..., FALSE)</c> and is then two-valued; <c>AND</c>/<c>OR</c>/
/// <c>NOT</c>/<c>has(...)</c>/a boolean literal need no further wrap. Every assertion is exact
/// (<c>ShouldBe</c>), never <c>ShouldContain</c>, so a change to this security-critical SQL is a
/// visible test failure rather than a silently-widened assertion.
/// </summary>
public class SqlPredicateRendererTests
{
    private static readonly IFieldSqlRenderer _fields = new TestFieldSqlRenderer();

    // Deliberately typed as the interface — this is the public contract under test, not a
    // performance-sensitive path CA1859 would improve by narrowing to the concrete renderer.
#pragma warning disable CA1859
    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();
#pragma warning restore CA1859

    private static SqlPredicate Render(string source, AlvoContext context) =>
        _renderer.Render(CelFixtures.CompileRule(source), context, _fields);

    [Fact]
    public void A_comparison_is_collapsed_so_null_reads_as_false()
    {
        var predicate = Render("owner_id == @user.id", CelFixtures.Alice);

        predicate.Sql.ShouldBe("COALESCE(\"owner_id\" = @p0, FALSE)");
        predicate.Parameters["p0"].ShouldBe(CelFixtures.Alice.User.Value);
    }

    [Fact]
    public void Negation_is_rendered_over_the_collapsed_value_without_a_second_wrap()
    {
        Render("!(owner_id == @user.id)", CelFixtures.Alice).Sql
            .ShouldBe("(NOT COALESCE(\"owner_id\" = @p0, FALSE))");
    }

    [Fact]
    public void A_string_literal_never_appears_in_the_sql_text()
    {
        var predicate = Render("status == 'approved'", CelFixtures.Alice);

        predicate.Sql.ShouldNotContain("approved");
        predicate.Parameters.Values.ShouldContain("approved");
    }

    [Fact]
    public void Role_membership_is_decided_at_render_time_so_the_role_name_stays_out_of_the_sql()
    {
        Render("'editor' in @user.roles", CelFixtures.Editor).Sql.ShouldNotContain("editor");
        Render("'editor' in @user.roles", CelFixtures.Editor).Sql.ShouldBe("TRUE");
        Render("'editor' in @user.roles", CelFixtures.Alice).Sql.ShouldBe("FALSE");
    }

    [Fact]
    public void A_field_backed_role_membership_check_renders_as_a_parameterized_in_list()
    {
        var predicate = Render("status in @user.roles", CelFixtures.Editor);

        predicate.Sql.ShouldBe("COALESCE(\"status\" IN (@p0, @p1), FALSE)");
        predicate.Parameters["p0"].ShouldBe("authenticated");
        predicate.Parameters["p1"].ShouldBe("editor");
    }

    [Fact]
    public void A_tenantless_context_renders_a_denial_rather_than_an_is_null_comparison()
    {
        var predicate = Render("tenant_id == @tenant.id", CelFixtures.TenantlessAlice);

        predicate.Sql.ShouldNotContain("IS NULL");
        predicate.Sql.ShouldBe("FALSE");
        predicate.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Presence_tests_are_already_two_valued_and_need_no_wrap()
    {
        Render("has(owner_id)", CelFixtures.Alice).Sql.ShouldBe("(\"owner_id\" IS NOT NULL)");
    }

    [Fact]
    public void A_bare_boolean_literal_root_renders_as_the_dialect_literal_with_no_wrap()
    {
        Render("true", CelFixtures.Alice).Sql.ShouldBe("TRUE");
        Render("false", CelFixtures.Alice).Sql.ShouldBe("FALSE");
    }

    [Fact]
    public void Parameter_names_are_generated_not_taken_from_the_source()
    {
        var predicate = Render("title == 'p0' && owner_id == @user.id", CelFixtures.Alice);

        predicate.Parameters.Keys.ShouldBe(["p0", "p1"], ignoreOrder: true);
        predicate.Parameters["p0"].ShouldBe("p0");
        predicate.Parameters["p1"].ShouldBe(CelFixtures.Alice.User.Value);
    }

    [Fact]
    public void An_int_literal_binds_as_a_clr_long()
    {
        var predicate = Render("total == 5", CelFixtures.Alice);

        predicate.Parameters.Values.Single().ShouldBeOfType<long>();
    }

    [Fact]
    public void A_decimal_literal_binds_as_a_clr_decimal()
    {
        var predicate = Render("total == 5.5", CelFixtures.Alice);

        predicate.Parameters.Values.Single().ShouldBeOfType<decimal>();
    }

    [Fact]
    public void A_string_literal_binds_as_a_clr_string()
    {
        var predicate = Render("status == 'approved'", CelFixtures.Alice);

        predicate.Parameters.Values.Single().ShouldBeOfType<string>();
    }

    [Fact]
    public void The_user_id_context_value_binds_as_a_clr_guid_not_a_user_id_wrapper()
    {
        var predicate = Render("owner_id == @user.id", CelFixtures.Alice);

        predicate.Parameters.Values.Single().ShouldBeOfType<Guid>();
        predicate.Parameters.Values.Single().ShouldBe(CelFixtures.Alice.User.Value);
    }

    [Fact]
    public void The_tenant_id_context_value_binds_as_a_clr_guid_not_a_tenant_id_wrapper()
    {
        var predicate = Render("tenant_id == @tenant.id", CelFixtures.Alice);

        predicate.Parameters.Values.Single().ShouldBeOfType<Guid>();
        predicate.Parameters.Values.Single().ShouldBe(CelFixtures.AcmeTenant.Value);
    }

    [Fact]
    public void The_computed_entry_point_rejects_a_rule_expression()
    {
        var expression = CelFixtures.CompileRule("owner_id == @user.id");

        Should.Throw<InvalidOperationException>(() => _renderer.Render(expression, _fields));
    }

    [Fact]
    public void The_predicate_entry_point_rejects_a_computed_expression()
    {
        var expression = CelFixtures.CompileComputed("total + 1");

        Should.Throw<InvalidOperationException>(() => _renderer.Render(expression, CelFixtures.Alice, _fields));
    }
}
