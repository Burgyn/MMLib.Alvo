using Microsoft.Extensions.Time.Testing;

namespace MMLib.Alvo.Abstractions.Tests;

/// <summary>The one instant a flow's every <c>now()</c> reads (HX-11 Ruling 1): microsecond-truncated, bound once, restored.</summary>
public sealed class AlvoFlowInstantTests
{
    private static readonly DateTimeOffset _t = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_instant_is_bound_to_the_microsecond_and_restored()
    {
        using (AlvoFlow.EnterInstant(_t.AddTicks(7)))
        {
            AlvoFlow.Instant.ShouldBe(_t);
        }

        AlvoFlow.Instant.ShouldBeNull();
    }

    [Fact]
    public void An_instant_is_bound_in_utc()
    {
        using var bound = AlvoFlow.EnterInstant(_t.ToOffset(TimeSpan.FromHours(2)));

        AlvoFlow.Instant!.Value.Offset.ShouldBe(TimeSpan.Zero);
        AlvoFlow.Instant.ShouldBe(_t);
    }

    [Fact]
    public void Ensure_binds_only_when_nothing_is_bound()
    {
        var clock = new FakeTimeProvider(_t);
        using (AlvoFlow.EnterInstant(_t.AddHours(-1)))
        using (AlvoFlow.EnsureInstant(clock))
        {
            AlvoFlow.Instant.ShouldBe(_t.AddHours(-1));
        }

        using (AlvoFlow.EnsureInstant(clock))
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            AlvoFlow.Instant.ShouldBe(_t);
        }

        AlvoFlow.Instant.ShouldBeNull();
    }

    [Fact]
    public async Task The_instant_flows_into_awaited_work()
    {
        using var bound = AlvoFlow.EnterInstant(_t);
        await Task.Yield();
        AlvoFlow.Instant.ShouldBe(_t);
    }

    [Fact]
    public void Entering_a_provenance_keeps_the_bound_instant_and_restores_it()
    {
        using var bound = AlvoFlow.EnterInstant(_t);
        using (AlvoFlow.Enter(new AlvoProvenance { CorrelationId = "flow", Depth = 1 }))
        {
            AlvoFlow.Instant.ShouldBe(_t);
        }

        AlvoFlow.Instant.ShouldBe(_t);
    }

    [Fact]
    public void Leaving_an_instant_restores_the_provenance_it_found()
    {
        var provenance = new AlvoProvenance { CorrelationId = "flow", Depth = 1 };
        using var entered = AlvoFlow.Enter(provenance);
        using (AlvoFlow.EnterInstant(_t))
        {
            AlvoFlow.Provenance.ShouldBe(provenance);
        }

        AlvoFlow.Provenance.ShouldBe(provenance);
    }
}
