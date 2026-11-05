using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Hooks;
using System.Linq.Expressions;

namespace MMLib.Alvo.Tests.Extensions;

public sealed class ConditionTranslatorClockTests
{
    private static readonly TimeSpan _grace = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan _tick = TimeSpan.FromTicks(1);

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
    };
}
