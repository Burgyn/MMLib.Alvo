using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class SortSqlRendererTests
{
    /// <summary>
    /// Without a key of its own the order is still total: the row key is appended to every <c>ORDER BY</c>,
    /// ascending, because the keyset cursor's own tie-breaker is exactly that comparison and a page whose
    /// order and whose boundary disagree skips or repeats rows.
    /// </summary>
    [Fact]
    public void An_empty_sort_still_orders_by_the_row_key()
        => Render([]).ShouldBe("\"id\"");

    [Fact]
    public void A_sort_key_is_followed_by_the_row_key_tie_breaker()
        => Render([new AlvoSort("plate")]).ShouldBe("\"plate\", \"id\"");

    /// <summary>
    /// The null-placement rank is emitted <b>only</b> where the key is nullable. It defeats an index on the
    /// sort key, and over a non-nullable key it is a compile-time constant <c>0</c> that cannot change a single
    /// row — which is what it used to be on <em>every paged read</em>, because
    /// <c>EnsureSortKeysCanBePaged</c> refuses a nullable paged key three frames earlier. So the one
    /// index-defeating construct in this data path was unavoidable in exactly the case §2.1's latency criterion
    /// is about.
    /// </summary>
    /// <param name="field">The key to sort by — <c>plate</c> is required, <c>status</c> nullable.</param>
    /// <param name="expected">The rendered term list.</param>
    [Theory]
    [InlineData("plate", "\"plate\", \"id\"")]
    [InlineData("status", "CASE WHEN \"status\" IS NULL THEN 1 ELSE 0 END, \"status\", \"id\"")]
    public void The_null_placement_rank_is_emitted_only_for_a_nullable_key(string field, string expected)
        => Render([new AlvoSort(field)]).ShouldBe(expected);

    [Fact]
    public void A_descending_key_carries_its_direction_while_the_tie_breaker_stays_ascending()
        => Render([new AlvoSort("plate", Descending: true)]).ShouldBe("\"plate\" DESC, \"id\"");

    /// <summary>
    /// SQLite and PostgreSQL disagree on where <c>NULL</c> sorts for a given direction, so the placement is
    /// rendered rather than left to the engine — the portable <c>CASE</c> emulation, which spike <c>Q3c</c>
    /// proved translates identically on both.
    /// </summary>
    [Fact]
    public void Nulls_first_inverts_the_placement_rank_and_nothing_else()
        => Render([new AlvoSort("status", Nulls: AlvoNullPlacement.First)])
            .ShouldBe("CASE WHEN \"status\" IS NULL THEN 0 ELSE 1 END, \"status\", \"id\"");

    [Fact]
    public void Several_keys_are_ordered_outermost_first()
        => Render([new AlvoSort("status"), new AlvoSort("plate", Descending: true)])
            .ShouldBe("CASE WHEN \"status\" IS NULL THEN 1 ELSE 0 END, \"status\", \"plate\" DESC, \"id\"");

    /// <summary>
    /// The load-bearing fact of this renderer. A <c>decimal</c> lives in a SQLite <c>TEXT</c> column, so an
    /// unrepaired <c>ORDER BY</c> over it is lexicographic while the keyset cursor's boundary — repaired
    /// through the very same <see cref="IFieldSqlRenderer.RenderComparableOperands"/>, at the same
    /// <see cref="CelFieldType"/> — is numeric. The two disagreeing is not a mis-sort: the page's order and
    /// the page's boundary then describe different sequences, and a page skips or repeats rows. So the
    /// ordering operand and the tie-breaker both go through that one seam, at the column's own type, and the
    /// null-placement test alone reads the raw column (a cast <c>NULL</c> is still <c>NULL</c>).
    /// </summary>
    [Fact]
    public void The_ordering_operand_and_the_tie_breaker_both_go_through_the_dialects_repair()
        => Render([new AlvoSort("price")], new TypeMarkingFieldSqlRenderer())
            .ShouldBe("CASE WHEN \"price\" IS NULL THEN 1 ELSE 0 END, CMP<Decimal>(\"price\"), CMP<Uuid>(\"id\")");

    [Fact]
    public void A_sort_key_naming_an_undeclared_field_is_refused_rather_than_interpolated()
        => Should.Throw<AlvoAuthorizationException>(
            () => Render([new AlvoSort("plate\"; DROP TABLE vehicle; --")]));

    [Fact]
    public void Every_argument_is_required()
    {
        Should.Throw<ArgumentNullException>(
            () => SortSqlRenderer.Render(null!, AlvoDataFixtures.Vehicle, new TestFieldSqlRenderer()));
        Should.Throw<ArgumentNullException>(() => SortSqlRenderer.Render([], null!, new TestFieldSqlRenderer()));
        Should.Throw<ArgumentNullException>(() => SortSqlRenderer.Render([], AlvoDataFixtures.Vehicle, null!));
    }

    private static string Render(IReadOnlyList<AlvoSort> sort, IFieldSqlRenderer? fields = null) =>
        SortSqlRenderer.Render(sort, AlvoDataFixtures.Vehicle, fields ?? new TestFieldSqlRenderer());
}
