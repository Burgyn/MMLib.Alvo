using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A <c>date</c> column reaches the interpreter as a <see cref="DateOnly"/> on every write path, and a comparison over one
/// must answer as the same calendar day at midnight UTC — the marshaller's rule — never <see langword="false"/> because the
/// operand was not recognised. A <c>false</c> there is a reject that never fires (security risk S-1, fail-open).
/// </summary>
public sealed class CelDateOperandTests
{
    private static readonly EntitySchema _bookings = new()
    {
        Name = "bookings",
        Fields =
        [
            new FieldSchema { Name = "starts_on", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "ends_on", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "stamped_at", Type = FieldType.DateTime, Nullable = true },
        ],
    };

    private static CelFunction Cutoff => TestCelFunctions.Host(
        "cutoff", CelValueType.Timestamp, _ => new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

    private static bool Condition(string source, (string Field, object? Value)[] current, (string Field, object? Value)[]? previous = null)
    {
        var compiled = TestCelFunctions.Compiler(Cutoff).Compile(source, CelProfile.Condition, _bookings);
        compiled.IsSuccess.ShouldBeTrue(string.Join("; ", compiled.Errors.Select(error => error.Message)));
        return CelInterpreter.EvaluatePredicate(
            compiled.Expression!, CelFixtures.Row(current), previous is null ? null : CelFixtures.Row(previous), AlvoContext.Anonymous);
    }

    private static readonly DateOnly _october5 = new(2026, 10, 5);
    private static readonly DateOnly _october9 = new(2026, 10, 9);

    [Fact]
    public void A_moved_date_is_not_equal_to_the_old_one() =>
        Condition("new.starts_on != old.starts_on", [("starts_on", _october9)], [("starts_on", _october5)]).ShouldBeTrue();

    [Fact]
    public void An_unmoved_date_equals_the_old_one() =>
        Condition("new.starts_on == old.starts_on", [("starts_on", _october5)], [("starts_on", _october5)]).ShouldBeTrue();

    [Fact]
    public void Two_date_fields_order_by_calendar_day() =>
        Condition("new.ends_on < new.starts_on", [("starts_on", _october9), ("ends_on", _october5)]).ShouldBeTrue();

    [Fact]
    public void A_date_orders_against_a_datetime_at_midnight_utc()
    {
        var current = new (string, object?)[] { ("starts_on", _october5), ("stamped_at", new DateTimeOffset(2026, 10, 5, 0, 0, 1, TimeSpan.Zero)) };

        Condition("new.starts_on < new.stamped_at", current).ShouldBeTrue();
        Condition("new.starts_on == new.stamped_at", [("starts_on", _october5), ("stamped_at", new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero))])
            .ShouldBeTrue();
    }

    [Fact]
    public void A_date_compares_with_a_host_function_s_instant() =>
        Condition("new.starts_on < cutoff()", [("starts_on", _october5)]).ShouldBeTrue();

    [Fact]
    public void Changed_is_false_for_a_date_left_as_it_was() =>
        Condition("changed(starts_on)", [("starts_on", _october5)], [("starts_on", _october5)]).ShouldBeFalse();

    [Fact]
    public void Changed_is_true_for_a_moved_date() =>
        Condition("changed(starts_on)", [("starts_on", _october9)], [("starts_on", _october5)]).ShouldBeTrue();
}
