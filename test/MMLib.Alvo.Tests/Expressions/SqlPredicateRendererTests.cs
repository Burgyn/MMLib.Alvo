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

    /// <summary>
    /// Defaults to <c>"p"</c> explicitly: the prefix is incidental to what these facts pin (a
    /// rendering shape), not the default itself — see
    /// <see cref="The_default_parameter_prefix_cannot_collide_with_an_orms_own_parameter_names"/> for
    /// the one fact that exercises the real default.
    /// </summary>
    private static SqlPredicate Render(string source, AlvoContext context, string parameterPrefix = "p") =>
        _renderer.Render(CelFixtures.CompileRule(source), context, _fields, parameterPrefix);

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

    /// <summary>
    /// The default prefix must be one no ORM would mint, and this fact exists to stop it being
    /// "simplified" back to <c>p</c>. PR2's spike proved why with a real query: EF Core names its own
    /// parameters <c>p0</c>, <c>p1</c>, …, so a <c>p</c>-prefixed render composed into an EF command
    /// collides — and EF resolves the collision by keeping its own <c>p0</c> and renaming ours to
    /// <c>p00</c> while both occurrences in the SQL text still read <c>@p0</c>. The caller's value is
    /// then substituted into the <b>security predicate</b>: wrong rows and no error at all on SQLite,
    /// and on PostgreSQL an error only when the two values' types happen to differ.
    /// </summary>
    [Fact]
    public void The_default_parameter_prefix_cannot_collide_with_an_orms_own_parameter_names()
    {
        // Calls the renderer directly (not the local Render helper, which pins "p" explicitly for the
        // shape facts above) so this fact exercises the real, un-overridden default.
        var parameters = _renderer.Render(
            CelFixtures.CompileRule("owner_id == @user.id"), CelFixtures.Alice, _fields).Parameters;

        parameters.Keys.ShouldBe(["alvo_p0"]);
        parameters.Keys.ShouldAllBe(name => !name.StartsWith('p'));
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

    /// <summary>
    /// Every comparison renders both operands through the dialect's value repair. Pinned in the core's own
    /// suite, against a renderer whose repair is <em>visible</em>: with an identity repair, deleting the call
    /// would leave every assertion here and the golden baseline byte-identical while a decimal rule silently
    /// became a lexical comparison on SQLite — a fail-open for any rule gating on an amount.
    /// </summary>
    [Fact]
    public void A_decimal_comparisons_operands_are_both_repaired_by_the_dialect()
        => Render("total > 100", CelFixtures.Alice).Sql
            .ShouldBe("COALESCE(CAST(\"total\" AS numeric) > CAST(@p0 AS numeric), FALSE)");

    /// <summary>
    /// Equality too, not only the relational operators: under a lexical comparison <c>total != 100</c> matches
    /// a row whose total <em>is</em> 100, so a rule that excludes the row admits it.
    /// </summary>
    [Fact]
    public void An_equality_comparisons_operands_are_repaired_as_well()
        => Render("total != 100", CelFixtures.Alice).Sql
            .ShouldBe("COALESCE(CAST(\"total\" AS numeric) <> CAST(@p0 AS numeric), FALSE)");

    /// <summary>
    /// The promotion the repair depends on: a whole-number literal against a decimal column makes a
    /// <c>Decimal</c> comparison, on either side of the operator. Without it the literal — the operand most
    /// likely to be an <c>Int64</c> in a <c>TEXT</c> column's comparison — is the one that escapes the repair.
    /// </summary>
    [Theory]
    [InlineData("total > 100", "CAST(\"total\" AS numeric) > CAST(@p0 AS numeric)")]
    [InlineData("100 < total", "CAST(@p0 AS numeric) < CAST(\"total\" AS numeric)")]
    [InlineData("total > 100.5", "CAST(\"total\" AS numeric) > CAST(@p0 AS numeric)")]
    public void A_mixed_numeric_comparison_is_promoted_to_decimal_on_either_side(string rule, string expected)
        => Render(rule, CelFixtures.Alice).Sql.ShouldBe($"COALESCE({expected}, FALSE)");

    /// <summary>
    /// A comparison over two non-numeric operands is left alone — the repair costs an index, and is only
    /// correct where the storage does not order the way the type does.
    /// </summary>
    [Theory]
    [InlineData("status == 'approved'", "COALESCE(\"status\" = @p0, FALSE)")]
    [InlineData("owner_id == @user.id", "COALESCE(\"owner_id\" = @p0, FALSE)")]
    [InlineData("created_at == approved_at", "COALESCE(\"created_at\" = \"approved_at\", FALSE)")]
    public void A_comparison_of_another_type_is_left_unrepaired(string rule, string expected)
        => Render(rule, CelFixtures.Alice).Sql.ShouldBe(expected);

    /// <summary>
    /// The <c>Computed</c> profile's own comparison rendering is a second call site of the same repair, and it
    /// has to be pinned separately — deleting the repair there would leave every predicate-path fact green.
    /// </summary>
    [Fact]
    public void The_scalar_paths_comparison_repairs_its_operands_too()
        => RenderScalar("total > 100 ? 1 : 0").Sql.ShouldStartWith(
            "(CASE WHEN COALESCE(CAST(\"total\" AS numeric) > CAST(@p0 AS numeric), FALSE) THEN ");

    /// <summary>
    /// The promotion walks into an operator or conditional node, which is what would make
    /// <c>(price + 1) &gt; 100</c> a decimal comparison — but no comparison operand can <em>be</em> one:
    /// both operand renderers accept a literal, a field reference and (on the predicate path) a context
    /// value, and refuse anything else. So those arms of the promotion are unreachable defence-in-depth
    /// rather than untested logic, and this fact says so instead of leaving it to be rediscovered.
    /// </summary>
    [Theory]
    [InlineData("total + 1 > 100 ? 1 : 0")]
    [InlineData("-total > 100 ? 1 : 0")]
    public void An_operator_node_cannot_be_a_comparison_operand_at_all(string rule)
        => Should.Throw<NotSupportedException>(() => RenderScalar(rule));

    /// <summary>
    /// A conditional operand is refused one layer earlier still, by the type checker, so the promotion's
    /// conditional arm is unreachable from two directions.
    /// </summary>
    [Fact]
    public void A_conditional_cannot_be_a_comparison_operand_either()
        => Should.Throw<InvalidOperationException>(() => RenderScalar("(total > 0 ? total : 0) > 100 ? 1 : 0"));

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
            "(CASE WHEN (COALESCE(CAST(\"total\" AS numeric) > CAST(@p0 AS numeric), FALSE) AND "
            + "COALESCE(CAST(\"total\" AS numeric) < CAST(@p1 AS numeric), FALSE)) THEN @p2 ELSE @p3 END)");
    }

    [Fact]
    public void A_ternary_renders_as_a_case_expression_with_an_unwrapped_comparison_condition()
    {
        var scalar = RenderScalar("total > 5 ? 1 : 2");

        scalar.Sql.ShouldBe(
            "(CASE WHEN COALESCE(CAST(\"total\" AS numeric) > CAST(@p0 AS numeric), FALSE) THEN @p1 ELSE @p2 END)");
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

        scalar.Sql.ShouldBe(
            "(CASE WHEN (NOT COALESCE(CAST(\"total\" AS numeric) > CAST(@p0 AS numeric), FALSE)) THEN @p1 ELSE @p2 END)");
    }

    /// <summary>
    /// The <see cref="CelProfile.Mutate"/> profile's interpreter-only guarantee, asserted from the
    /// renderer's side: a function call is refused <em>by name</em>, with a message naming the profile and
    /// saying why, never by the generic "cannot be rendered by this entry point" default arm. Deleting the
    /// explicit arm leaves a <see cref="NotSupportedException"/> of the same shape, so these two facts
    /// assert the message and not merely the exception type — otherwise the guarantee would survive its own
    /// removal.
    /// </summary>
    /// <remarks>
    /// Both trees are assembled by hand rather than compiled from source, deliberately: the refusal has to
    /// hold for any <see cref="CelCall"/> that reaches the renderer through any seam a provider may drive
    /// directly, not only for the source shapes today's parser happens to produce.
    /// </remarks>
    [Fact]
    public void A_mutate_function_call_is_refused_by_name_with_the_profile_and_the_reason()
    {
        var refused = Should.Throw<NotSupportedException>(
            () => _renderer.Render(MutateExpression(LowerAsciiOfTitle), CelFixtures.Alice, _fields));

        refused.Message.ShouldContain(nameof(CelProfile.Mutate));
        refused.Message.ShouldContain(CelCall.LowerAscii);
        refused.Message.ShouldContain("never rendered to SQL");
    }

    [Fact]
    public void A_mutate_function_call_is_refused_in_an_operand_position_too_not_only_at_the_root()
    {
        var comparison = new CelBinary(
            CelBinaryOperator.Equal, LowerAsciiOfTitle, new CelLiteral(CelValueType.String, "alvo"));

        var refused = Should.Throw<NotSupportedException>(
            () => _renderer.Render(MutateExpression(comparison, CelValueType.Bool), CelFixtures.Alice, _fields));

        refused.Message.ShouldContain(nameof(CelProfile.Mutate));
    }

    private static CelCall LowerAsciiOfTitle =>
        new(CelCall.LowerAscii, new CelFieldRef("title", CelValueType.String, CelRecordState.Current));

    private static CompiledExpression MutateExpression(CelNode root, CelValueType resultType = CelValueType.String) =>
        new(root, CelProfile.Mutate, resultType, "lowerAscii(title)", CelFixtures.Orders);
}
