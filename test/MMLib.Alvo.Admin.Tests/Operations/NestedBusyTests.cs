using MMLib.Alvo.Admin.Components.Operations;

namespace MMLib.Alvo.Admin.Tests.Operations;

/// <summary>
/// The Operations page stays busy until its outermost step ends (final review L2): a confirm's reload, which is a guarded
/// step of its own, must not enable the confirm button while the confirm flow is still running.
/// </summary>
public sealed class NestedBusyTests
{
    [Fact]
    public void A_step_inside_a_step_leaves_the_page_busy_when_it_ends()
    {
        var busy = new NestedBusy();

        using (busy.Enter())
        {
            using (busy.Enter())
            {
                busy.Busy.ShouldBeTrue();
            }

            busy.Busy.ShouldBeTrue("the confirm flow is still focusing and closing after its reload ended");
        }

        busy.Busy.ShouldBeFalse();
    }

    [Fact]
    public void A_page_with_no_step_running_is_not_busy() => new NestedBusy().Busy.ShouldBeFalse();
}
