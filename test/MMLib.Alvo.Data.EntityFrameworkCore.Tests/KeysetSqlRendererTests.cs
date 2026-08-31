using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class KeysetSqlRendererTests
{
    private static readonly Guid _anchorId = Guid.NewGuid();

    [Fact]
    public void With_no_sort_key_the_cursor_is_the_primary_key_alone()
        => Render([], []).Sql.ShouldBe("\"id\" > @alvo_k0");

    /// <summary>
    /// Row-value tuple comparison has no portable form, so the nested-OR expansion is what ships: a
    /// strictly greater leading key, or an equal leading key and a greater tail.
    /// </summary>
    [Fact]
    public void One_ascending_sort_key_expands_to_the_nested_or_form()
        => Render([new AlvoSort("plate")], ["ACME-001"]).Sql
            .ShouldBe("(\"plate\" > @alvo_k0 OR (\"plate\" = @alvo_k0 AND \"id\" > @alvo_k1))");

    [Fact]
    public void A_descending_sort_key_reverses_only_the_strict_comparison()
        => Render([new AlvoSort("plate", Descending: true)], ["ACME-001"]).Sql
            .ShouldBe("(\"plate\" < @alvo_k0 OR (\"plate\" = @alvo_k0 AND \"id\" > @alvo_k1))");

    /// <summary>
    /// Two keys nest left to right, and the outer one being nullable does not change where the inner one
    /// goes: the null arm is added <em>beside</em> the comparison, never around the tail.
    /// </summary>
    [Fact]
    public void Two_sort_keys_nest_left_to_right()
        => Render([new AlvoSort("status"), new AlvoSort("plate")], ["open", "ACME-001"]).Sql
            .ShouldBe(
                "(\"status\" IS NULL OR \"status\" > @alvo_k0 OR (\"status\" = @alvo_k0 AND "
                + "(\"plate\" > @alvo_k1 OR (\"plate\" = @alvo_k1 AND \"id\" > @alvo_k2))))");

    /// <summary>
    /// A nullable key whose nulls sort <b>last</b> and whose anchor row has a value: every null-keyed row is
    /// past the boundary, so the arm admitting them carries no comparison at all.
    /// </summary>
    [Fact]
    public void A_nulls_last_key_past_a_valued_anchor_admits_every_null_keyed_row()
        => Render([new AlvoSort("status")], ["open"]).Sql.ShouldBe(
            "(\"status\" IS NULL OR \"status\" > @alvo_k0 OR (\"status\" = @alvo_k0 AND \"id\" > @alvo_k1))");

    /// <summary>
    /// The same key under <c>nullsfirst</c> adds nothing: the nulls sort before the anchor, and
    /// <c>col &gt; @v</c> is already <see langword="null"/> — and therefore false — for them.
    /// </summary>
    [Fact]
    public void A_nulls_first_key_past_a_valued_anchor_renders_exactly_the_non_nullable_form()
        => Render([new AlvoSort("status", Nulls: AlvoNullPlacement.First)], ["open"]).Sql.ShouldBe(
            "(\"status\" > @alvo_k0 OR (\"status\" = @alvo_k0 AND \"id\" > @alvo_k1))");

    /// <summary>
    /// Where nulls sort is decided by the rank term, which <see cref="SortSqlRenderer"/> always orders
    /// ascending — so the direction reverses the value comparison and leaves the null arm untouched.
    /// </summary>
    [Fact]
    public void A_descending_nulls_last_key_reverses_the_comparison_and_not_the_null_arm()
        => Render([new AlvoSort("status", Descending: true)], ["open"]).Sql.ShouldBe(
            "(\"status\" IS NULL OR \"status\" < @alvo_k0 OR (\"status\" = @alvo_k0 AND \"id\" > @alvo_k1))");

    /// <summary>
    /// An anchor row whose key <em>is</em> null under <c>nullslast</c>: it sits in the last bucket, so the
    /// rows after it are the other null-keyed ones, separated by the tie-breaker alone. <c>status = @v</c> is
    /// never rendered — against a null anchor it is the three-valued trap that made a page stop silently.
    /// </summary>
    [Fact]
    public void A_nulls_last_key_past_a_null_anchor_continues_within_the_null_bucket()
        => Render([new AlvoSort("status")], [null]).Sql
            .ShouldBe("(\"status\" IS NULL AND \"id\" > @alvo_k0)");

    /// <summary>
    /// The same anchor under <c>nullsfirst</c> sits in the <em>first</em> bucket, so every valued row is past
    /// it unconditionally and the null-keyed ones are separated by the tie-breaker.
    /// </summary>
    [Fact]
    public void A_nulls_first_key_past_a_null_anchor_admits_every_valued_row()
        => Render([new AlvoSort("status", Nulls: AlvoNullPlacement.First)], [null]).Sql
            .ShouldBe("(\"status\" IS NOT NULL OR \"id\" > @alvo_k0)");

    /// <summary>
    /// A null anchor binds no parameter for its own key: there is no value to compare against, and an
    /// unreferenced name in the bag is a parameter the statement text never mentions.
    /// </summary>
    [Fact]
    public void A_null_anchor_value_binds_no_parameter_of_its_own()
    {
        var rendered = Render([new AlvoSort("status")], [null]);

        rendered.Parameters.Count.ShouldBe(1);
        rendered.Parameters[PolicyParameterPrefix.Keyset + "0"].Value.ShouldBe(_anchorId);
    }

    /// <summary>
    /// The null arms are chosen by the field's declared nullability — the same condition
    /// <see cref="SortSqlRenderer"/> emits its rank term on — so a <b>required</b> key renders the plain form
    /// whatever its <see cref="AlvoSort.Nulls"/> says. Sorting a required column by null placement is
    /// meaningless, and honouring it here would put an arm in the boundary that the <c>ORDER BY</c> has no
    /// counterpart for.
    /// </summary>
    [Fact]
    public void A_required_key_renders_the_plain_form_whatever_its_null_placement_says()
    {
        var last = Render([new AlvoSort("plate")], ["ACME-001"]).Sql;
        var first = Render([new AlvoSort("plate", Nulls: AlvoNullPlacement.First)], ["ACME-001"]).Sql;

        last.ShouldBe("(\"plate\" > @alvo_k0 OR (\"plate\" = @alvo_k0 AND \"id\" > @alvo_k1))");
        first.ShouldBe(last);
    }

    [Fact]
    public void Every_anchor_value_is_a_bound_parameter()
        => Render([new AlvoSort("plate")], ["ACME-001"]).Parameters.Values
            .Select(bound => bound.Value).ShouldContain("ACME-001");

    [Fact]
    public void An_anchor_value_is_bound_once_and_referenced_twice()
    {
        var rendered = Render([new AlvoSort("plate")], ["ACME-001"]);

        rendered.Parameters.Count.ShouldBe(2);
        rendered.Parameters[PolicyParameterPrefix.Keyset + "0"].Value.ShouldBe("ACME-001");
        rendered.Parameters[PolicyParameterPrefix.Keyset + "1"].Value.ShouldBe(_anchorId);
    }

    /// <summary>
    /// The tie-breaker is always ascending, on both directions of the user key: it exists to make the order
    /// total, and flipping it with the last user key would make two pages of one query disagree about where
    /// the boundary is.
    /// </summary>
    [Fact]
    public void The_tie_breaking_key_stays_ascending_under_a_descending_sort()
        => Render([new AlvoSort("plate", Descending: true)], ["ACME-001"]).Sql.ShouldContain("\"id\" > @alvo_k1");

    /// <summary>
    /// The same repair the caller filter and the CEL predicate apply: a decimal sort key is compared
    /// lexicographically on SQLite unless both operands are cast, which would make a page skip or repeat rows
    /// rather than merely mis-order them.
    /// </summary>
    [Fact]
    public void Both_operands_of_every_cursor_comparison_go_through_the_dialects_value_repair()
        => Render([new AlvoSort("price")], [12.34m], new TypeMarkingFieldSqlRenderer()).Sql.ShouldBe(
            "(\"price\" IS NULL OR CMP<Decimal>(\"price\") > CMP<Decimal>(@alvo_k0) "
            + "OR (CMP<Decimal>(\"price\") = CMP<Decimal>(@alvo_k0) "
            + "AND CMP<Uuid>(\"id\") > CMP<Uuid>(@alvo_k1)))");

    [Fact]
    public void An_undeclared_sort_key_never_reaches_the_sql_text()
        => Should.Throw<AlvoAuthorizationException>(
            () => Render([new AlvoSort("plate\"; DROP TABLE vehicle; --")], ["x"]));

    /// <summary>
    /// A cursor whose value list does not line up with its sort keys would silently compare a key against
    /// another key's value, so it is refused as the programming error it is rather than rendered.
    /// </summary>
    [Fact]
    public void An_anchor_whose_values_do_not_match_its_sort_keys_is_refused()
    {
        Should.Throw<ArgumentException>(() => Render([new AlvoSort("plate")], []));
        Should.Throw<ArgumentException>(() => Render([], ["ACME-001"]));
    }

    [Fact]
    public void Every_argument_is_required()
    {
        var anchor = new KeysetAnchor([new AlvoSort("plate")], ["ACME-001"], _anchorId);

        Should.Throw<ArgumentNullException>(() => KeysetSqlRenderer.Render(
            null!, AlvoDataFixtures.Vehicle, new TestFieldSqlRenderer(), PolicyParameterPrefix.Keyset));
        Should.Throw<ArgumentNullException>(() => KeysetSqlRenderer.Render(
            anchor, null!, new TestFieldSqlRenderer(), PolicyParameterPrefix.Keyset));
        Should.Throw<ArgumentNullException>(() => KeysetSqlRenderer.Render(
            anchor, AlvoDataFixtures.Vehicle, null!, PolicyParameterPrefix.Keyset));
        Should.Throw<ArgumentException>(() => KeysetSqlRenderer.Render(
            anchor, AlvoDataFixtures.Vehicle, new TestFieldSqlRenderer(), "  "));
    }

    private static RenderedSql Render(
        IReadOnlyList<AlvoSort> sort, IReadOnlyList<object?> values, IFieldSqlRenderer? fields = null) =>
        KeysetSqlRenderer.Render(
            new KeysetAnchor(sort, values, _anchorId),
            AlvoDataFixtures.Vehicle,
            fields ?? new TestFieldSqlRenderer(),
            PolicyParameterPrefix.Keyset);
}
