using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Hooks;
using System.Diagnostics;
using System.Linq.Expressions;

namespace MMLib.Alvo.Tests.Extensions;

public sealed class ConditionTranslatorClockTests
{
    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan _tick = TimeSpan.FromTicks(1);
    private static readonly TimeSpan _back = TimeSpan.FromMinutes(-90);
    private static readonly TimeSpan _forever = TimeSpan.MaxValue;
    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static string When(Expression<Func<EventCondition<Order>, bool>> when) =>
        ConditionTranslator.Translate(when, "clock", HandlerVerb.Updated);

    [Fact]
    public void Utc_now_translates_to_now() =>
        When(e => e.New.DueAt > DateTimeOffset.UtcNow).ShouldBe("new.due_at > now()");

    [Fact]
    public void Now_with_the_local_offset_is_the_same_instant() =>
        When(e => e.New.DueAt > DateTimeOffset.Now).ShouldBe("new.due_at > now()");

    [Fact]
    public void A_static_readonly_time_span_and_the_operators_translate() =>
        When(e => e.New.ClosedAt - e.New.DueAt <= _grace + _grace)
            .ShouldBe("new.closed_at - new.due_at <= duration('5400s') + duration('5400s')");

    [Fact]
    public void A_typed_write_hook_when_translates_utc_now_too() =>
        WriteConditionTranslator.Translate<HookBooking>(w => w.New.StartsAt > DateTimeOffset.UtcNow, "guard", HandlerVerb.Updated)
            .ShouldBe("new.starts_at > now()");

    [Fact]
    public void A_shift_of_now_translates() =>
        When(e => e.New.DueAt < DateTimeOffset.UtcNow - _grace).ShouldBe("new.due_at < now() - duration('5400s')");

    [Fact]
    public void A_time_span_with_no_cel_duration_is_refused() =>
        Should.Throw<AlvoTranslationException>(() => When(e => e.New.ClosedAt - e.New.DueAt <= _tick))
            .Message.ShouldContain("finer than a microsecond");

    /// <summary>
    /// <c>DateTimeOffset.UtcNow</c> inside a value the translator would fold at registration is refused: folding it would bake
    /// the registration's instant into the condition instead of <c>now()</c>.
    /// </summary>
    [Fact]
    public void Utc_now_folded_into_a_constant_is_refused() =>
        Should.Throw<AlvoTranslationException>(() => When(e => e.New.DueAt < DateTimeOffset.UtcNow.AddHours(1)))
            .Message.ShouldContain("translates to now() only as an operand");

    [Fact]
    public void A_negative_time_span_translates_to_a_negative_duration() =>
        When(e => e.New.ClosedAt - e.New.DueAt >= _back).ShouldBe("new.closed_at - new.due_at >= duration('-5400s')");

    [Fact]
    public void A_time_span_past_the_cel_range_is_refused() =>
        Should.Throw<AlvoTranslationException>(() => When(e => e.New.ClosedAt - e.New.DueAt <= _forever))
            .Message.ShouldContain("longer than ten thousand years");

    /// <summary>
    /// The natural spelling, a <see cref="TimeSpan"/> computed inline, is refused with the fix that names the shape Ruling 34
    /// admits — a <c>static readonly</c> field — rather than the generic constant fix (Task 4 review m4).
    /// </summary>
    [Fact]
    public void An_inline_time_span_is_refused_with_the_static_readonly_fix() =>
        Should.Throw<AlvoTranslationException>(() => When(e => e.New.DueAt < DateTimeOffset.UtcNow - TimeSpan.FromHours(1)))
            .Message.ShouldContain("static readonly TimeSpan");

    /// <summary>
    /// Typed–CEL parity (Task 4 review m2): every translated temporal condition compiles in the Condition profile against the
    /// entity, and judges a row exactly as the hand-written CEL does at the same bound instant.
    /// </summary>
    [Theory]
    [MemberData(nameof(Temporal))]
    public void A_translated_temporal_condition_compiles_and_agrees_with_its_cel(
        Expression<Func<EventCondition<Order>, bool>> when, string cel)
    {
        var typed = CompileCondition(When(when));
        var written = CompileCondition(cel);
        using var bound = AlvoFlow.EnterInstant(_t);

        foreach (var row in ParityRows())
        {
            CelInterpreter.EvaluatePredicate(typed, row, null, CelFixtures.Alice)
                .ShouldBe(CelInterpreter.EvaluatePredicate(written, row, null, CelFixtures.Alice));
        }
    }

    [Fact]
    public void The_parity_rows_fall_on_both_sides_of_the_window()
    {
        var window = CompileCondition("new.due_at < now() - duration('5400s')");
        using var bound = AlvoFlow.EnterInstant(_t);

        ParityRows().Select(row => CelInterpreter.EvaluatePredicate(window, row, null, CelFixtures.Alice)).Distinct().Count().ShouldBe(2);
    }

    public static TheoryData<Expression<Func<EventCondition<Order>, bool>>, string> Temporal => new()
    {
        { e => e.New.DueAt > DateTimeOffset.UtcNow, "new.due_at > now()" },
        { e => e.New.DueAt < DateTimeOffset.UtcNow - _grace, "new.due_at < now() - duration('90m')" },
        { e => e.New.ClosedAt - e.New.DueAt <= _grace + _grace, "new.closed_at - new.due_at <= duration('3h')" },
        { e => e.New.ClosedAt - e.New.DueAt >= _back, "new.closed_at - new.due_at >= duration('-90m')" },
    };

    private static CompiledExpression CompileCondition(string cel)
    {
        var compiled = CelFixtures.Compiler.Compile(cel, CelProfile.Condition, TypedFixtures.OrdersSchema);
        compiled.IsSuccess.ShouldBeTrue(string.Join("; ", compiled.Errors.Select(error => error.Message)));
        return compiled.Expression!;
    }

    private static IEnumerable<AlvoRecord> ParityRows() =>
    [
        CelFixtures.Row(("due_at", _t.AddHours(-2)), ("closed_at", _t)),
        CelFixtures.Row(("due_at", _t.AddHours(-1)), ("closed_at", _t.AddHours(-4))),
        CelFixtures.Row(("due_at", _t.AddHours(1)), ("closed_at", _t.AddHours(5))),
        CelFixtures.Row(("due_at", null), ("closed_at", _t)),
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public void Other_clocks_stay_refused_with_the_fix(Expression<Func<EventCondition<Order>, bool>> when, string member) =>
        Should.Throw<AlvoTranslationException>(() => When(when)).Message.ShouldSatisfyAllConditions(
            message => message.ShouldContain(member),
            message => message.ShouldContain("DateTimeOffset.UtcNow"));

    public static TheoryData<Expression<Func<EventCondition<Order>, bool>>, string> Refused => new()
    {
        { e => e.New.DueAt!.Value.UtcDateTime > DateTime.UtcNow, "UtcNow" },
        { e => e.New.ShipOn > DateOnly.FromDateTime(DateTime.Today), "Today" },
        { e => e.New.DueAt > TimeProvider.System.GetUtcNow(), "GetUtcNow" },
        { e => e.New.DueAt!.Value.LocalDateTime > DateTime.Now, "Now" },
        { e => e.New.DueAt > TimeProvider.System.GetLocalNow(), "GetLocalNow" },
        { e => TimeOnly.FromDateTime(DateTime.UtcNow).Hour > 9, "UtcNow" },
        { e => e.New.Quantity < Stopwatch.GetTimestamp(), "GetTimestamp" },
        { e => e.New.Quantity < Environment.TickCount64, "TickCount64" },
    };
}
