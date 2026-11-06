using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>Durations and <c>now()</c> in SQL (HX-11 Rulings 4, 11): folded where no field is read, integer microseconds where one is.</summary>
public sealed class SqlPredicateRendererTemporalTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly SqlPredicateRenderer _renderer = new();

    private static SqlPredicate Render(string rule)
    {
        using var instant = AlvoFlow.EnterInstant(_t);
        return _renderer.Render(CelFixtures.CompileRule(rule), CelFixtures.Alice, EpochFields.Instance, "p");
    }

    [Fact]
    public void A_constant_window_is_folded_into_one_bound_instant_and_stays_sargable()
    {
        var predicate = Render("created_at > now() - duration('24h')");
        predicate.Sql.ShouldBe("COALESCE(\"created_at\" > @p0, FALSE)");
        predicate.Parameters["p0"].ShouldBe(_t.AddHours(-24));
    }

    [Fact]
    public void Arithmetic_over_a_field_enters_the_microsecond_domain()
    {
        var predicate = Render("approved_at - created_at <= duration('8h')");
        predicate.Sql.ShouldBe("COALESCE((EPOCH_US(\"approved_at\") - EPOCH_US(\"created_at\")) <= @p0, FALSE)");
        predicate.Parameters["p0"].ShouldBe(28_800_000_000L);
    }

    [Fact]
    public void A_field_shifted_by_a_duration_is_compared_with_the_bound_instant_in_microseconds()
    {
        var predicate = Render("created_at + duration('1h') < now()");
        predicate.Sql.ShouldBe("COALESCE((EPOCH_US(\"created_at\") + @p0) < @p1, FALSE)");
        predicate.Parameters["p0"].ShouldBe(3_600_000_000L);
        predicate.Parameters["p1"].ShouldBe((_t - DateTimeOffset.UnixEpoch).Ticks / 10);
    }

    [Fact]
    public void A_date_field_never_takes_the_leaf_path()
    {
        var predicate = Render("due_on > now() - duration('24h')");
        predicate.Sql.ShouldBe("COALESCE(EPOCH_US(\"due_on\") > @p0, FALSE)");
        predicate.Parameters["p0"].ShouldBe((_t.AddHours(-24) - DateTimeOffset.UnixEpoch).Ticks / 10);
    }

    /// <summary>
    /// Two duration constants compare as integers, never as a bound <see cref="TimeSpan"/> whose text SQLite would order
    /// (<c>'1.00:00:00' &lt; '23:00:00'</c>) — the guard Task 1 placed until this rendering existed.
    /// </summary>
    [Fact]
    public void Two_duration_constants_compare_as_microseconds()
    {
        var predicate = Render("duration('24h') < duration('23h')");
        predicate.Sql.ShouldBe("COALESCE(@p0 < @p1, FALSE)");
        predicate.Parameters["p0"].ShouldBe(86_400_000_000L);
        predicate.Parameters["p1"].ShouldBe(82_800_000_000L);
    }

    [Fact]
    public void A_bare_now_comparison_keeps_its_bound_instant()
    {
        var predicate = Render("created_at <= now()");
        predicate.Sql.ShouldBe("COALESCE(\"created_at\" <= @p0, FALSE)");
        predicate.Parameters["p0"].ShouldBe(_t);
    }

    /// <summary>
    /// A constant shift of the bound instant past CEL's range denies the whole predicate (Ruling 57), as the interpreter's
    /// <c>WITH CHECK</c> does — never an unhandled render failure, and never a <c>FALSE</c> a <c>!</c> above it could invert.
    /// </summary>
    [Fact]
    public void A_fold_past_the_range_denies_the_whole_predicate()
    {
        using var instant = AlvoFlow.EnterInstant(new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero));

        var predicate = _renderer.Render(
            CelFixtures.CompileRule("!(created_at < now() + duration('48h'))"), CelFixtures.Alice, EpochFields.Instance, "p");

        predicate.Sql.ShouldBe("FALSE");
        predicate.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void A_dialect_with_no_epoch_refuses_by_name()
    {
        using var instant = AlvoFlow.EnterInstant(_t);
        Should.Throw<NotSupportedException>(() => _renderer.Render(
            CelFixtures.CompileRule("approved_at - created_at <= duration('8h')"), CelFixtures.Alice, NoEpochFields.Instance, "p"))
            .Message.ShouldContain(nameof(NoEpochFields));
    }

    [Fact]
    public void Now_with_no_bound_instant_refuses_to_render() =>
        Should.Throw<InvalidOperationException>(() =>
            _renderer.Render(CelFixtures.CompileRule("created_at <= now()"), CelFixtures.Alice, EpochFields.Instance, "p"));
}
