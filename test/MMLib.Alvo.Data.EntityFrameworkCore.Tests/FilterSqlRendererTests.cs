using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class FilterSqlRendererTests
{
    [Theory]
    [InlineData(AlvoFilterOperator.Eq, "\"status\" = @alvo_f0")]
    [InlineData(AlvoFilterOperator.Neq, "\"status\" <> @alvo_f0")]
    [InlineData(AlvoFilterOperator.Gt, "\"status\" > @alvo_f0")]
    [InlineData(AlvoFilterOperator.Gte, "\"status\" >= @alvo_f0")]
    [InlineData(AlvoFilterOperator.Lt, "\"status\" < @alvo_f0")]
    [InlineData(AlvoFilterOperator.Lte, "\"status\" <= @alvo_f0")]
    [InlineData(AlvoFilterOperator.Like, "\"status\" LIKE @alvo_f0")]
    public void Each_scalar_operator_renders_its_own_sql_operator(AlvoFilterOperator op, string expected)
        => Render(new AlvoComparison("status", op, "open")).Sql.ShouldBe(expected);

    [Fact]
    public void Case_insensitive_like_goes_through_the_drivers_own_seam()
        => Render(new AlvoComparison("status", AlvoFilterOperator.ILike, "op%")).Sql
            .ShouldBe("UPPER(\"status\") LIKE UPPER(@alvo_f0)");

    [Fact]
    public void Membership_renders_one_parameter_per_candidate()
    {
        var rendered = Render(new AlvoComparison("status", AlvoFilterOperator.In, new object?[] { "open", "closed" }));

        rendered.Sql.ShouldBe("\"status\" IN (@alvo_f0, @alvo_f1)");
        rendered.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Membership_in_nothing_matches_nothing_rather_than_rendering_an_empty_list()
    {
        var rendered = Render(new AlvoComparison("status", AlvoFilterOperator.In, Array.Empty<object?>()));

        rendered.Sql.ShouldBe("FALSE");
        rendered.Parameters.ShouldBeEmpty();
    }

    /// <summary>
    /// A bare string is itself an <see cref="System.Collections.IEnumerable"/>, so membership over one would
    /// otherwise silently expand to one parameter per character.
    /// </summary>
    [Fact]
    public void Membership_over_a_bare_string_is_refused_rather_than_expanded_per_character()
        => Should.Throw<AlvoAuthorizationException>(
            () => Render(new AlvoComparison("status", AlvoFilterOperator.In, "open")));

    [Fact]
    public void Membership_over_a_scalar_is_refused()
        => Should.Throw<AlvoAuthorizationException>(
            () => Render(new AlvoComparison("mileage", AlvoFilterOperator.In, 10L)));

    [Fact]
    public void An_identity_test_renders_a_definite_two_valued_predicate_with_no_parameter()
    {
        Render(new AlvoComparison("status", AlvoFilterOperator.Is, null)).Sql.ShouldBe("\"status\" IS NULL");
        Render(new AlvoComparison("is_public", AlvoFilterOperator.Is, true)).Sql.ShouldBe("\"is_public\" IS TRUE");
        Render(new AlvoComparison("is_public", AlvoFilterOperator.Is, false)).Sql.ShouldBe("\"is_public\" IS FALSE");
        Render(new AlvoComparison("status", AlvoFilterOperator.Is, null)).Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void An_identity_test_against_anything_else_is_refused()
        => Should.Throw<AlvoAuthorizationException>(
            () => Render(new AlvoComparison("status", AlvoFilterOperator.Is, "open")));

    [Fact]
    public void Boolean_connectives_are_parenthesised_and_never_flattened_into_the_policy_term()
    {
        var tree = new AlvoNot(new AlvoAnd([
            new AlvoComparison("status", AlvoFilterOperator.Eq, "open"),
            new AlvoOr([
                new AlvoComparison("mileage", AlvoFilterOperator.Gt, 10L),
                new AlvoComparison("mileage", AlvoFilterOperator.Is, null),
            ]),
        ]));

        Render(tree).Sql.ShouldBe(
            "(NOT ((\"status\" = @alvo_f0) AND ((\"mileage\" > @alvo_f1) OR (\"mileage\" IS NULL))))");
    }

    [Fact]
    public void An_empty_conjunction_matches_every_row_and_an_empty_disjunction_matches_none()
    {
        Render(new AlvoAnd([])).Sql.ShouldBe("TRUE");
        Render(new AlvoOr([])).Sql.ShouldBe("FALSE");
    }

    [Fact]
    public void A_tree_deeper_than_the_cap_is_refused_rather_than_walked()
    {
        AlvoFilter node = new AlvoComparison("status", AlvoFilterOperator.Eq, "open");
        for (var level = 1; level <= AlvoFilter.MaxDepth; level++)
        {
            node = new AlvoNot(node);
        }

        Should.Throw<ArgumentException>(() => Render(node));
    }

    [Fact]
    public void A_malformed_tree_is_refused_rather_than_dereferenced()
        => Should.Throw<ArgumentException>(() => Render(new AlvoAnd([null!])));

    [Fact]
    public void An_undeclared_field_never_reaches_the_sql_text()
        => Should.Throw<AlvoAuthorizationException>(
            () => Render(new AlvoComparison("nope\"; DROP TABLE items; --", AlvoFilterOperator.Eq, "x")));

    /// <summary>
    /// The value that reaches the renderer is the <em>schema's</em> declared name, so an ordinal-equal but
    /// differently-cased name is undeclared rather than silently normalised — the same rule the CEL type
    /// checker and <see cref="QueryFieldGuard"/> follow.
    /// </summary>
    [Fact]
    public void A_field_matching_only_case_insensitively_is_undeclared()
        => Should.Throw<AlvoAuthorizationException>(() => Render(new AlvoComparison("Status", AlvoFilterOperator.Eq, "x")));

    /// <summary>
    /// A decimal lives in a <c>TEXT</c> column on SQLite, so a comparison over one is lexicographic unless
    /// <em>both</em> operands are repaired — wrapping only the column would leave the parameter's own storage
    /// class deciding the answer, and SQLite orders every <c>TEXT</c> value above every numeric one.
    /// </summary>
    [Theory]
    [InlineData(AlvoFilterOperator.Eq, "=")]
    [InlineData(AlvoFilterOperator.Neq, "<>")]
    [InlineData(AlvoFilterOperator.Gt, ">")]
    [InlineData(AlvoFilterOperator.Gte, ">=")]
    [InlineData(AlvoFilterOperator.Lt, "<")]
    [InlineData(AlvoFilterOperator.Lte, "<=")]
    public void Both_operands_of_an_ordering_comparison_go_through_the_dialects_value_repair(
        AlvoFilterOperator op, string expected)
        => Render(new AlvoComparison("price", op, 100m), new TypeMarkingFieldSqlRenderer()).Sql
            .ShouldBe($"CMP<Decimal>(\"price\") {expected} CMP<Decimal>(@alvo_f0)");

    [Fact]
    public void Every_membership_candidate_is_repaired_too_because_membership_is_equality()
        => Render(new AlvoComparison("price", AlvoFilterOperator.In, new object?[] { 1m, 2m }), new TypeMarkingFieldSqlRenderer())
            .Sql.ShouldBe("CMP<Decimal>(\"price\") IN (CMP<Decimal>(@alvo_f0), CMP<Decimal>(@alvo_f1))");

    /// <summary>
    /// The comparison type is the <em>column's</em>, not the caller value's: the column is what the row is
    /// stored in, and a caller comparing a decimal column against a whole number must still get a numeric
    /// comparison rather than a lexical one.
    /// </summary>
    [Fact]
    public void The_comparison_type_comes_from_the_column_not_from_the_supplied_value()
        => Render(new AlvoComparison("price", AlvoFilterOperator.Gt, 100L), new TypeMarkingFieldSqlRenderer()).Sql
            .ShouldBe("CMP<Decimal>(\"price\") > CMP<Decimal>(@alvo_f0)");

    /// <summary>
    /// A pattern match is a string operation by definition, so it is not routed through the numeric repair —
    /// <c>UPPER(CAST(x AS REAL))</c> is not a pattern match on any engine.
    /// </summary>
    [Theory]
    [InlineData(AlvoFilterOperator.Like)]
    [InlineData(AlvoFilterOperator.ILike)]
    public void A_pattern_match_is_not_routed_through_the_numeric_repair(AlvoFilterOperator op)
        => Render(new AlvoComparison("status", op, "a%"), new TypeMarkingFieldSqlRenderer()).Sql
            .ShouldNotContain("CMP<");

    [Fact]
    public void An_identity_test_needs_no_repair_because_it_compares_nothing()
        => Render(new AlvoComparison("price", AlvoFilterOperator.Is, null), new TypeMarkingFieldSqlRenderer()).Sql
            .ShouldBe("\"price\" IS NULL");

    [Fact]
    public void Every_argument_is_required()
    {
        var filter = new AlvoComparison("status", AlvoFilterOperator.Eq, "open");

        Should.Throw<ArgumentNullException>(() => FilterSqlRenderer.Render(
            null!, AlvoDataFixtures.Vehicle, new TestFieldSqlRenderer(), PolicyParameterPrefix.Filter));
        Should.Throw<ArgumentNullException>(() => FilterSqlRenderer.Render(
            filter, null!, new TestFieldSqlRenderer(), PolicyParameterPrefix.Filter));
        Should.Throw<ArgumentNullException>(() => FilterSqlRenderer.Render(
            filter, AlvoDataFixtures.Vehicle, null!, PolicyParameterPrefix.Filter));
        Should.Throw<ArgumentException>(() => FilterSqlRenderer.Render(
            filter, AlvoDataFixtures.Vehicle, new TestFieldSqlRenderer(), "  "));
    }

    private static RenderedSql Render(AlvoFilter filter, IFieldSqlRenderer? fields = null) => FilterSqlRenderer.Render(
        filter, AlvoDataFixtures.Vehicle, fields ?? new TestFieldSqlRenderer(), PolicyParameterPrefix.Filter);
}
