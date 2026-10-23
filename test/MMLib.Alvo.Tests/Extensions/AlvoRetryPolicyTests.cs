using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>The handler retry schedule (spec 2026-10-14 X5): six waits, so the seventh attempt is the last.</summary>
public sealed class AlvoRetryPolicyTests
{
    [Fact]
    public void The_default_waits_are_the_specified_six_and_the_seventh_attempt_is_the_last()
    {
        AlvoRetryPolicy.Default.Delays.ShouldBe([
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)]);
        AlvoRetryPolicy.Default.MaxAttempts.ShouldBe(7);
    }

    [Fact]
    public void No_retry_means_one_attempt() => AlvoRetryPolicy.None.MaxAttempts.ShouldBe(1);

    [Theory]
    [InlineData(1, 5.0)]
    [InlineData(6, 21600.0)]
    public void The_wait_after_an_attempt_is_its_own_delay(int attempt, double seconds) =>
        AlvoRetryPolicy.Default.DelayAfter(attempt).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void There_is_no_wait_before_the_first_attempt() => AlvoRetryPolicy.Default.DelayAfter(0).ShouldBeNull();

    [Fact]
    public void There_is_no_wait_after_the_last_attempt() => AlvoRetryPolicy.Default.DelayAfter(7).ShouldBeNull();

    [Fact]
    public void A_policy_keeps_its_own_copy_of_the_delays()
    {
        TimeSpan[] delays = [TimeSpan.FromSeconds(1)];
        var policy = AlvoRetryPolicy.From(delays);

        delays[0] = TimeSpan.FromHours(1);

        policy.Delays.ShouldBe([TimeSpan.FromSeconds(1)]);
    }

    [Fact]
    public void A_negative_delay_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => AlvoRetryPolicy.From(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void A_delay_of_seven_days_is_the_longest_accepted() =>
        AlvoRetryPolicy.From(TimeSpan.FromDays(7)).Delays.ShouldBe([TimeSpan.FromDays(7)]);

    [Fact]
    public void A_delay_beyond_seven_days_is_refused_naming_the_cap()
    {
        var refused = Should.Throw<ArgumentOutOfRangeException>(() => AlvoRetryPolicy.From(TimeSpan.FromDays(7) + TimeSpan.FromTicks(1)));

        refused.ParamName.ShouldBe("delays");
        refused.Message.ShouldContain("7 days");
    }
}
