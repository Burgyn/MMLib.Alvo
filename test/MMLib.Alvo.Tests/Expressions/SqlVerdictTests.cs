using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Pins <see cref="SqlVerdict"/>'s own three-valued semantics directly — if the evaluator itself
/// cannot tell true, false, and unknown apart, the differential test it backs would pass no matter
/// what the two real backends did, so this is the harness's own proof obligation.
/// </summary>
public class SqlVerdictTests
{
    private static readonly Dictionary<string, object?> _oneParameter = new(StringComparer.Ordinal) { ["p0"] = "irrelevant" };

    [Fact]
    public void A_comparison_against_a_null_field_is_unknown_not_false()
    {
        var row = CelFixtures.Row(("x", null));

        SqlVerdict.EvaluateTri("\"x\" = @p0", row, _oneParameter).ShouldBe(SqlTri.Unknown);
    }

    [Fact]
    public void Coalesce_collapses_an_unknown_inner_value_to_its_fallback()
    {
        var row = CelFixtures.Row(("x", null));

        SqlVerdict.EvaluateTri("COALESCE(\"x\" = @p0, FALSE)", row, _oneParameter).ShouldBe(SqlTri.False);
    }

    [Fact]
    public void Not_of_an_unknown_comparison_is_still_unknown()
    {
        var row = CelFixtures.Row(("x", null));

        SqlVerdict.EvaluateTri("(NOT \"x\" = @p0)", row, _oneParameter).ShouldBe(SqlTri.Unknown);
    }

    [Fact]
    public void Unknown_and_false_is_false()
    {
        SqlTriLogic.And(SqlTri.Unknown, SqlTri.False).ShouldBe(SqlTri.False);
    }

    [Fact]
    public void Unknown_or_true_is_true()
    {
        SqlTriLogic.Or(SqlTri.Unknown, SqlTri.True).ShouldBe(SqlTri.True);
    }

    [Fact]
    public void A_null_field_is_not_not_null()
    {
        var row = CelFixtures.Row(("x", null));

        SqlVerdict.EvaluateTri("(\"x\" IS NOT NULL)", row, new Dictionary<string, object?>()).ShouldBe(SqlTri.False);
    }

    [Fact]
    public void A_present_field_is_not_null()
    {
        var row = CelFixtures.Row(("x", "present"));

        SqlVerdict.EvaluateTri("(\"x\" IS NOT NULL)", row, new Dictionary<string, object?>()).ShouldBe(SqlTri.True);
    }

    [Fact]
    public void A_matching_equality_is_true()
    {
        var row = CelFixtures.Row(("x", "same"));
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["p0"] = "same" };

        SqlVerdict.EvaluateTri("COALESCE(\"x\" = @p0, FALSE)", row, parameters).ShouldBe(SqlTri.True);
    }

    [Fact]
    public void A_bare_boolean_field_reads_its_own_null_as_unknown()
    {
        var row = CelFixtures.Row(("flag", null));

        SqlVerdict.EvaluateTri("COALESCE(\"flag\", FALSE)", row, new Dictionary<string, object?>()).ShouldBe(SqlTri.False);
    }

    [Fact]
    public void An_in_list_match_is_true()
    {
        var row = CelFixtures.Row(("role", "editor"));
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["p0"] = "admin", ["p1"] = "editor" };

        SqlVerdict.EvaluateTri("COALESCE(\"role\" IN (@p0, @p1), FALSE)", row, parameters).ShouldBe(SqlTri.True);
    }

    [Fact]
    public void An_in_list_over_a_null_field_is_unknown_before_the_fallback_collapses_it()
    {
        var row = CelFixtures.Row(("role", null));
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["p0"] = "admin" };

        SqlVerdict.EvaluateTri("\"role\" IN (@p0)", row, parameters).ShouldBe(SqlTri.Unknown);
        SqlVerdict.EvaluateTri("COALESCE(\"role\" IN (@p0), FALSE)", row, parameters).ShouldBe(SqlTri.False);
    }

    [Fact]
    public void An_evaluated_predicate_treats_unknown_the_same_as_false()
    {
        var row = CelFixtures.Row(("x", null));
        var predicate = new SqlPredicate("COALESCE(\"x\" = @p0, FALSE)", _oneParameter);

        SqlVerdict.Evaluate(predicate, row).ShouldBeFalse();
    }
}
