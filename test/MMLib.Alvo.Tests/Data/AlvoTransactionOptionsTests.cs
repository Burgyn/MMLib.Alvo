using MMLib.Alvo.Data;

namespace MMLib.Alvo.Tests.Data;

/// <summary><see cref="AlvoTransactionOptions"/>: its defaults and what <c>TransactAsync</c> refuses before opening a unit.</summary>
public sealed class AlvoTransactionOptionsTests
{
    private static readonly AlvoContext _caller = new() { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } };
    private static readonly AlvoContext _system = AlvoContext.System(tenant: null);

    [Fact]
    public void The_default_unit_runs_for_thirty_seconds_and_suppresses_nothing()
    {
        var options = AlvoTransactionOptions.Validated(null, _caller);

        (options.Timeout, options.SuppressHostHooks, options.Reason).ShouldBe((TimeSpan.FromSeconds(30), false, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3_600_001)]
    public void A_timeout_outside_a_millisecond_to_an_hour_is_refused(long milliseconds)
    {
        var refusal = Should.Throw<ArgumentOutOfRangeException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { Timeout = TimeSpan.FromMilliseconds(milliseconds) }, _caller));

        refusal.ParamName.ShouldBe("Timeout");
    }

    [Fact]
    public void An_hour_is_the_longest_timeout()
        => AlvoTransactionOptions.Validated(new AlvoTransactionOptions { Timeout = TimeSpan.FromHours(1) }, _caller).Timeout.ShouldBe(TimeSpan.FromHours(1));

    [Fact]
    public void Suppressing_host_hooks_is_refused_to_a_caller_who_is_not_the_system()
        => Should.Throw<ArgumentException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true, Reason = "backfill" }, _caller))
            .Message.ShouldContain("system context");

    [Fact]
    public void Suppressing_host_hooks_needs_a_reason()
        => Should.Throw<ArgumentException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true }, _system))
            .ParamName.ShouldBe("Reason");

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    public void A_reason_with_a_control_character_is_refused(string reason)
        => Should.Throw<ArgumentException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true, Reason = reason }, _system))
            .ParamName.ShouldBe("Reason");

    [Fact]
    public void A_reason_past_five_hundred_characters_is_refused()
        => Should.Throw<ArgumentException>(() =>
            AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true, Reason = new string('r', 501) }, _system))
            .ParamName.ShouldBe("Reason");

    [Fact]
    public void The_system_may_suppress_host_hooks_with_a_reason()
        => AlvoTransactionOptions.Validated(new AlvoTransactionOptions { SuppressHostHooks = true, Reason = "nightly backfill" }, _system)
            .SuppressHostHooks.ShouldBeTrue();
}
