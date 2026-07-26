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
    private static readonly SqlPredicateRenderer _renderer = new();

    private static SqlPredicate Render(string source, AlvoContext context) =>
        _renderer.Render(CelFixtures.CompileRule(source), context, _fields);

    private static SqlExpression RenderScalar(string source) =>
        _renderer.Render(CelFixtures.CompileComputed(source), _fields);

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
    public void A_boolean_field_used_as_a_predicate_is_collapsed_so_a_null_flag_reads_as_false()
    {
        Render("is_public", CelFixtures.Alice).Sql.ShouldBe("COALESCE(\"is_public\", FALSE)");
    }

    [Fact]
    public void Negating_a_boolean_field_negates_the_collapsed_value()
    {
        Render("!is_public", CelFixtures.Alice).Sql.ShouldBe("(NOT COALESCE(\"is_public\", FALSE))");
    }

    [Fact]
    public void A_boolean_field_composes_with_a_comparison_over_and()
    {
        var predicate = Render("is_public && owner_id == @user.id", CelFixtures.Alice);

        predicate.Sql.ShouldBe("(COALESCE(\"is_public\", FALSE) AND COALESCE(\"owner_id\" = @p0, FALSE))");
        predicate.Parameters["p0"].ShouldBe(CelFixtures.Alice.User.Value);
    }

    [Fact]
    public void Parameter_names_are_generated_not_taken_from_the_source()
    {
        var predicate = Render("title == 'p0' && owner_id == @user.id", CelFixtures.Alice);

        predicate.Parameters.Keys.ShouldBe(["p0", "p1"], ignoreOrder: true);
        predicate.Parameters["p0"].ShouldBe("p0");
        predicate.Parameters["p1"].ShouldBe(CelFixtures.Alice.User.Value);
    }

    /// <summary>
    /// A <c>PolicyDecision</c> carries up to three predicates a backend composes into one command, and
    /// each render numbers its own parameters from zero — so without a per-render prefix the composed
    /// command has two different values bound to one name, and whichever wins silently changes what the
    /// predicate means. The prefix is the caller's way to keep them disjoint.
    /// </summary>
    [Fact]
    public void Two_renders_with_different_prefixes_produce_disjoint_parameter_names()
    {
        var usingPredicate = _renderer.Render(
            CelFixtures.CompileRule("owner_id == @user.id"), CelFixtures.Alice, _fields, "u");
        var tenantScope = _renderer.Render(
            CelFixtures.CompileRule("tenant_id == @tenant.id"), CelFixtures.Alice, _fields, "t");

        usingPredicate.Parameters.Keys.ShouldBe(["u0"]);
        tenantScope.Parameters.Keys.ShouldBe(["t0"]);
        usingPredicate.Parameters.Keys.Intersect(tenantScope.Parameters.Keys, StringComparer.Ordinal).ShouldBeEmpty();
        usingPredicate.Sql.ShouldContain("@u0");
        tenantScope.Sql.ShouldContain("@t0");
    }

    [Fact]
    public void The_default_parameter_prefix_keeps_the_established_names()
    {
        Render("owner_id == @user.id", CelFixtures.Alice).Parameters.Keys.ShouldBe(["p0"]);
    }

    /// <summary>
    /// The prefix is composed into SQL text unparameterized (there is no bind parameter for a bind
    /// parameter's own name), so it is validated as an identifier rather than trusted — a provider
    /// deriving one from anything caller-influenced must not be able to smuggle SQL through it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("p 0")]
    [InlineData("p; DROP TABLE items --")]
    [InlineData("1p")]
    [InlineData("p-0")]
    public void A_parameter_prefix_that_is_not_a_plain_identifier_is_rejected(string prefix)
    {
        Should.Throw<ArgumentException>(() => _renderer.Render(
            CelFixtures.CompileRule("owner_id == @user.id"), CelFixtures.Alice, _fields, prefix));
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

    [Fact]
    public void The_returned_parameter_dictionary_is_not_the_renderers_mutable_instance()
    {
        var predicate = Render("owner_id == @user.id", CelFixtures.Alice);

        Should.Throw<InvalidCastException>(() => _ = (Dictionary<string, object?>)predicate.Parameters);
    }

    [Fact]
    public void A_condition_referencing_old_and_new_fields_throws_not_supported_since_hooks_are_interpreter_evaluated()
    {
        var expression = CelFixtures.CompileCondition("old.status != new.status");

        Should.Throw<NotSupportedException>(() => _renderer.Render(expression, CelFixtures.Alice, _fields));
    }

    [Fact]
    public void A_condition_using_changed_throws_not_supported_since_hooks_are_interpreter_evaluated()
    {
        var expression = CelFixtures.CompileCondition("changed(status)");

        Should.Throw<NotSupportedException>(() => _renderer.Render(expression, CelFixtures.Alice, _fields));
    }

    [Theory]
    [InlineData("total + 1", "(\"total\" + @p0)")]
    [InlineData("total - 1", "(\"total\" - @p0)")]
    [InlineData("total * 2", "(\"total\" * @p0)")]
    [InlineData("total / 2", "(\"total\" / @p0)")]
    public void Each_arithmetic_operator_renders_unwrapped_over_a_field_and_a_parameter(string source, string expectedSql)
    {
        RenderScalar(source).Sql.ShouldBe(expectedSql);
    }

    [Fact]
    public void Arithmetic_precedence_is_preserved_by_the_parsed_tree_shape_not_the_renderer()
    {
        RenderScalar("total * 2 + 1").Sql.ShouldBe("((\"total\" * @p0) + @p1)");
    }

    [Fact]
    public void A_nested_arithmetic_tree_renders_with_matching_parentheses()
    {
        RenderScalar("(total + 1) * (total - 1)").Sql.ShouldBe("((\"total\" + @p0) * (\"total\" - @p1))");
    }

    [Fact]
    public void A_logical_and_composes_two_collapsed_comparisons_inside_a_ternary_condition()
    {
        var scalar = RenderScalar("(total > 5 && total < 10) ? 1 : 2");

        scalar.Sql.ShouldBe(
            "(CASE WHEN (COALESCE(\"total\" > @p0, FALSE) AND COALESCE(\"total\" < @p1, FALSE)) THEN @p2 ELSE @p3 END)");
    }

    [Fact]
    public void A_ternary_renders_as_a_case_expression_with_an_unwrapped_comparison_condition()
    {
        var scalar = RenderScalar("total > 5 ? 1 : 2");

        scalar.Sql.ShouldBe("(CASE WHEN COALESCE(\"total\" > @p0, FALSE) THEN @p1 ELSE @p2 END)");
    }

    /// <summary>
    /// Without collapsing the comparison first, <c>NOT (total &gt; 5)</c> over a null <c>total</c>
    /// would render <c>NOT UNKNOWN</c> = <c>UNKNOWN</c>, falling to the <c>ELSE</c> branch — but the
    /// interpreter's null rule makes <c>!(total &gt; 5)</c> true for a null <c>total</c>, selecting
    /// the <c>THEN</c> branch. Collapsing first (<c>NOT COALESCE(...)</c>) keeps the two backends in
    /// agreement.
    /// </summary>
    [Fact]
    public void Negating_a_comparison_inside_a_ternary_condition_is_collapsed_so_a_null_field_does_not_diverge()
    {
        var scalar = RenderScalar("!(total > 5) ? 1 : 2");

        scalar.Sql.ShouldBe("(CASE WHEN (NOT COALESCE(\"total\" > @p0, FALSE)) THEN @p1 ELSE @p2 END)");
    }
}
