using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>One run's deadline and its one-way extension (#519).</summary>
public sealed class HookDeadlineTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _allowance = TimeSpan.FromMilliseconds(2500);

    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void An_extension_before_the_deadline_moves_it_to_the_new_budget_from_the_runs_start()
    {
        using var deadline = new HookDeadline(_timeout, _clock);
        _clock.Advance(TimeSpan.FromMilliseconds(100));

        deadline.ExtendTo(_allowance);
        _clock.Advance(TimeSpan.FromMilliseconds(2399));
        var beforeTheNewDeadline = deadline.Passed;
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        (beforeTheNewDeadline, deadline.Passed, deadline.Budget).ShouldBe((false, true, _allowance));
    }

    [Fact]
    public void A_deadline_that_fired_stays_fired_when_extended()
    {
        using var deadline = new HookDeadline(_timeout, _clock);
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        deadline.ExtendTo(_allowance);

        (deadline.Passed, deadline.Budget).ShouldBe((true, _timeout));
    }

    [Fact]
    public void An_extension_after_dispose_changes_nothing_and_does_not_throw()
    {
        var deadline = new HookDeadline(_timeout, _clock);
        deadline.Dispose();

        Should.NotThrow(() => deadline.ExtendTo(_allowance));

        deadline.Budget.ShouldBe(_timeout);
    }

    [Fact]
    public void A_smaller_budget_changes_nothing()
    {
        using var deadline = new HookDeadline(_allowance, _clock);

        deadline.ExtendTo(_timeout);
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        (deadline.Passed, deadline.Budget).ShouldBe((false, _allowance));
    }
}
