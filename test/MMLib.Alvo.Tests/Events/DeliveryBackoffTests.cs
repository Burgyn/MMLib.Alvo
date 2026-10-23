using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Events;

public sealed class DeliveryBackoffTests
{
    [Theory]
    [InlineData(0.0, 4.0)]
    [InlineData(0.5, 5.0)]
    [InlineData(1.0, 6.0)]
    public void A_wait_is_spread_by_a_fifth_either_way(double sample, double seconds) =>
        new DeliveryBackoff(() => sample).After(1, AlvoRetryPolicy.Default).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void There_is_no_wait_after_the_last_attempt() =>
        new DeliveryBackoff(() => 0.5).After(7, AlvoRetryPolicy.Default).ShouldBeNull();

    [Fact]
    public void A_spread_wait_never_passes_the_policy_s_7_day_ceiling() =>
        new DeliveryBackoff(() => 1.0).After(1, AlvoRetryPolicy.From(TimeSpan.FromDays(7))).ShouldBe(TimeSpan.FromDays(7));

    [Fact]
    public void The_shared_sampler_stays_within_a_fifth_of_the_policy_s_wait()
    {
        var backoff = new DeliveryBackoff();

        Enumerable.Range(0, 200).Select(_ => backoff.After(2, AlvoRetryPolicy.Default)!.Value)
            .ShouldAllBe(wait => wait >= TimeSpan.FromSeconds(24) && wait <= TimeSpan.FromSeconds(36));
    }
}
