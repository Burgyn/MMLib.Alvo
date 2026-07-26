using Shouldly;
using Xunit;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The differential proof that a comparison over a <c>decimal</c> answers by <b>value</b> on every engine —
/// one rule table, inherited, so the two backends cannot come to test different things.
/// </summary>
/// <remarks>
/// <para>
/// EF stores a <c>decimal</c> in a <c>TEXT</c> column on SQLite, and SQLite compares <c>TEXT</c>
/// lexicographically, so an unguarded <c>price &gt; 100</c> matches a row whose price is <c>12.34</c> while
/// PostgreSQL's <c>numeric</c> answers correctly. That is not imprecision but an inverted answer, and on a
/// <c>USING</c> rule gating access on an amount it is a fail-open authorization outcome on one engine —
/// §0 principle 3's exact prohibition. Because the defect <em>is</em> a disagreement between engines, the
/// proof that it is fixed is inherently differential: a per-engine copy of this table is how the two engines
/// would drift again, which is the same argument that made
/// <see cref="AlvoDataSqlSnapshotTests"/> a shipped base rather than a per-driver file.
/// </para>
/// <para>
/// A subclass supplies one seam: <see cref="MatchesAsync"/> must run the <em>whole</em> path a rule really
/// takes — compile the CEL against <see cref="AlvoDataFixtures.Vehicle"/>, render it through the driver's own
/// <c>IFieldSqlRenderer</c>, bind through EF's type mapping, and execute against a real database — and return
/// how many rows matched. Anything less (an in-memory evaluator, a hand-built parameter) would prove
/// something other than what the two engines actually do.
/// </para>
/// </remarks>
public abstract class AlvoDataComparisonTests
{
    /// <summary>
    /// Seeds exactly one <c>vehicle</c> row with <paramref name="price"/> and <paramref name="mileage"/>, then
    /// answers <paramref name="rule"/> against it through the driver's real rendering, binding and execution
    /// path.
    /// </summary>
    /// <param name="rule">The CEL rule to compile, render and execute.</param>
    /// <param name="price">The row's <c>price</c>, or <see langword="null"/> to leave it unset.</param>
    /// <param name="mileage">The row's <c>mileage</c>, or <see langword="null"/> to leave it unset.</param>
    /// <returns>The number of rows the rendered predicate matched — <c>0</c> or <c>1</c>.</returns>
    protected abstract Task<int> MatchesAsync(string rule, decimal? price, long? mileage);

    /// <summary>
    /// The relational operators over a stored decimal, at magnitudes where a lexical comparison and a numeric
    /// one disagree — <c>'12.34' &gt; '100'</c> is true as text and false as a number.
    /// </summary>
    /// <param name="rule">The CEL rule to answer.</param>
    /// <param name="expected">How many rows must match.</param>
    [Theory]
    [InlineData("price > 100", 0)]
    [InlineData("price > 2", 1)]
    [InlineData("price >= 12.34", 1)]
    [InlineData("price < 100", 1)]
    [InlineData("price <= 2", 0)]
    [InlineData("price > 12.34", 0)]
    public async Task A_decimal_comparison_answers_numerically_not_lexicographically(string rule, int expected)
        => (await MatchesAsync(rule, 12.34m, null)).ShouldBe(expected);

    /// <summary>
    /// The same defect reached through a whole-number literal, which the type checker accepts against a
    /// decimal field. On SQLite the bound parameter is then an <c>INTEGER</c> against a <c>TEXT</c> column, so
    /// storage-class ordering alone — every <c>TEXT</c> value sorts above every <c>INTEGER</c> — produces the
    /// wrong answer before collation is even involved.
    /// </summary>
    /// <param name="rule">The CEL rule to answer.</param>
    /// <param name="expected">How many rows must match.</param>
    [Theory]
    [InlineData("price > 3", 0)]
    [InlineData("price < 3", 1)]
    public async Task A_whole_number_literal_against_a_decimal_column_answers_numerically(string rule, int expected)
        => (await MatchesAsync(rule, 2.50m, null)).ShouldBe(expected);

    /// <summary>
    /// Equality is the shape that fails <em>open</em>: with the column stored as <c>'100.00'</c> and the
    /// literal bound as an <c>INTEGER</c> <c>100</c>, a textual comparison makes <c>price == 100</c> miss and
    /// therefore <c>price != 100</c> match — a rule meant to exclude the row admits it.
    /// </summary>
    /// <param name="rule">The CEL rule to answer.</param>
    /// <param name="expected">How many rows must match.</param>
    [Theory]
    [InlineData("price == 100", 1)]
    [InlineData("price != 100", 0)]
    public async Task Equality_against_a_decimal_column_answers_numerically(string rule, int expected)
        => (await MatchesAsync(rule, 100m, null)).ShouldBe(expected);

    /// <summary>
    /// A <see langword="null"/> decimal satisfies neither a comparison nor its negation — a repair must not
    /// disturb the three-valued fold every predicate goes through.
    /// </summary>
    /// <param name="rule">The CEL rule to answer.</param>
    /// <param name="expected">How many rows must match.</param>
    [Theory]
    [InlineData("price > 1", 0)]
    [InlineData("!(price > 1)", 1)]
    [InlineData("has(price)", 0)]
    public async Task A_null_decimal_keeps_its_three_valued_answer(string rule, int expected)
        => (await MatchesAsync(rule, null, null)).ShouldBe(expected);

    /// <summary>
    /// An integer column is ordered correctly by every engine Alvo supports, so its comparison must answer the
    /// same with or without a repair. This is a correctness fact, not a proof that no repair was applied — a
    /// cast on both sides would give the same counts. What pins "untouched" is the golden CEL→SQL baseline,
    /// where an integer comparison appears unwrapped next to a wrapped decimal one.
    /// </summary>
    /// <param name="rule">The CEL rule to answer.</param>
    /// <param name="expected">How many rows must match.</param>
    [Theory]
    [InlineData("mileage > 100", 1)]
    [InlineData("mileage > 100000", 0)]
    public async Task An_integer_comparison_answers_the_same_on_every_engine(string rule, int expected)
        => (await MatchesAsync(rule, null, 5000L)).ShouldBe(expected);
}
