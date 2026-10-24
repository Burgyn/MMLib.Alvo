using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Extensions;

public sealed class AlvoHandlerOptionsTests
{
    [Fact]
    public void A_handler_runs_as_the_system_in_order_for_thirty_seconds_with_the_default_retries_unless_told_otherwise()
    {
        var options = new AlvoHandlerOptions();

        options.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.RunAs.ShouldBe(AlvoRunAs.System);
        options.Ordering.ShouldBe(AlvoOrdering.PerRecord);
        options.Retry.ShouldBeSameAs(AlvoRetryPolicy.Default);
        options.Order.ShouldBe(0);
        options.OrderStated.ShouldBeFalse();
    }

    [Fact]
    public void Setting_the_order_states_it_even_when_it_is_zero() =>
        new AlvoHandlerOptions { Order = 0 }.OrderStated.ShouldBeTrue();

    [Fact]
    public void A_handler_cannot_go_without_a_retry_policy() =>
        Should.Throw<ArgumentNullException>(() => new AlvoHandlerOptions { Retry = null! });
}
