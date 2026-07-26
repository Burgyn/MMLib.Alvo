using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Proves <see cref="IFieldSqlRenderer"/> is a sufficient seam for a dialect with no boolean type —
/// SQL Server / Azure SQL, which §0 principle 3 requires the engine-agnostic core to support. The
/// PostgreSQL/SQLite shape <c>COALESCE(&lt;predicate&gt;, FALSE)</c> is not parseable in T-SQL's
/// boolean position at all, and a driver author who could only implement field/parameter/literal
/// rendering had no way to fix that without forking the structural renderer. Every fact here renders
/// through <see cref="TSqlFieldSqlRenderer"/>, which overrides nothing but the two-valued members.
/// </summary>
public class TSqlSeamTests
{
    private static readonly IFieldSqlRenderer _tsql = new TSqlFieldSqlRenderer();
    private static readonly SqlPredicateRenderer _renderer = new();

    private static SqlPredicate Render(string source, AlvoContext context) =>
        _renderer.Render(CelFixtures.CompileRule(source), context, _tsql);

    [Fact]
    public void A_comparison_collapses_through_the_dialects_own_two_valued_shape()
    {
        Render("owner_id == @user.id", CelFixtures.Alice).Sql
            .ShouldBe("(CASE WHEN [owner_id] = @p0 THEN 1 ELSE 0 END = 1)");
    }

    [Fact]
    public void A_nullable_boolean_column_is_defaulted_in_value_position_then_compared()
    {
        Render("is_public", CelFixtures.Alice).Sql.ShouldBe("(COALESCE([is_public], 0) = 1)");
    }

    [Fact]
    public void Negation_composes_over_the_dialects_predicate_shape()
    {
        Render("!is_public", CelFixtures.Alice).Sql.ShouldBe("(NOT (COALESCE([is_public], 0) = 1))");
    }

    [Fact]
    public void A_boolean_literal_root_renders_as_a_predicate_not_a_bare_bit()
    {
        Render("true", CelFixtures.Alice).Sql.ShouldBe("(1 = 1)");
        Render("false", CelFixtures.Alice).Sql.ShouldBe("(1 = 0)");
    }

    [Fact]
    public void Render_time_role_membership_renders_as_a_predicate_not_a_bare_bit()
    {
        Render("'editor' in @user.roles", CelFixtures.Editor).Sql.ShouldBe("(1 = 1)");
        Render("'editor' in @user.roles", CelFixtures.Alice).Sql.ShouldBe("(1 = 0)");
    }

    [Fact]
    public void A_parameterized_in_list_collapses_through_the_dialects_two_valued_shape()
    {
        Render("status in @user.roles", CelFixtures.Editor).Sql
            .ShouldBe("(CASE WHEN [status] IN (@p0, @p1) THEN 1 ELSE 0 END = 1)");
    }

    [Fact]
    public void A_presence_test_is_already_a_predicate_in_every_dialect()
    {
        Render("has(owner_id)", CelFixtures.Alice).Sql.ShouldBe("([owner_id] IS NOT NULL)");
    }

    [Fact]
    public void An_always_false_predicate_renders_as_a_predicate_not_a_bare_bit()
    {
        SqlPredicate.AlwaysFalse(_tsql).Sql.ShouldBe("(1 = 0)");
    }

    /// <summary>
    /// The structural claim behind the individual shapes: across the renderer's whole rule matrix,
    /// nothing reaches the root as a bare <c>COALESCE(...)</c> value — the exact fragment T-SQL cannot
    /// evaluate where a predicate is expected.
    /// </summary>
    [Fact]
    public void No_rendered_predicate_leaves_a_bare_coalesce_value_in_boolean_position()
    {
        string[] rules =
        [
            "owner_id == @user.id",
            "status != 'draft'",
            "total > 100",
            "!(owner_id == @user.id)",
            "has(owner_id)",
            "!has(owner_id)",
            "is_public",
            "!is_public",
            "is_public && owner_id == @user.id",
            "owner_id == @user.id || status == 'approved'",
            "true",
            "status in @user.roles",
        ];

        foreach (var rule in rules)
        {
            var sql = Render(rule, CelFixtures.Editor).Sql;

            sql.ShouldNotStartWith("COALESCE(", Case.Sensitive, $"'{rule}' rendered '{sql}', a value where T-SQL needs a predicate.");
        }
    }
}
