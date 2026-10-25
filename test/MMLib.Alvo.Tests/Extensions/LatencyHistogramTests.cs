using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>Log-scale bins a quarter-octave wide: any duration is reported within about a fifth of itself, in fixed memory.</summary>
public sealed class LatencyHistogramTests
{
    [Theory]
    [InlineData(0.05)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(1_500)]
    [InlineData(3_600_000)]
    public void A_duration_falls_in_a_bin_whose_upper_bound_is_within_a_fifth_of_it(double milliseconds)
    {
        var upper = LatencyHistogram.UpperBound(LatencyHistogram.Bin(milliseconds));

        upper.ShouldBeGreaterThanOrEqualTo(Math.Min(milliseconds, LatencyHistogram.UpperBound(0)));
        if (milliseconds > LatencyHistogram.UpperBound(0))
        {
            (upper / milliseconds).ShouldBeLessThanOrEqualTo(1.19 + 1e-9);
        }
    }

    [Fact]
    public void A_percentile_is_the_bound_of_the_bin_that_holds_its_rank()
    {
        var bins = new long[LatencyHistogram.Bins];
        foreach (var duration in Enumerable.Range(1, 100))
        {
            bins[LatencyHistogram.Bin(duration)]++;
        }

        var median = LatencyHistogram.Percentile(bins, 100, 0.5)!.Value.TotalMilliseconds;
        var p95 = LatencyHistogram.Percentile(bins, 100, 0.95)!.Value.TotalMilliseconds;

        median.ShouldBeInRange(50, 60);
        p95.ShouldBeInRange(95, 114);
    }

    [Fact]
    public void Nothing_recorded_has_no_percentile() =>
        LatencyHistogram.Percentile(new long[LatencyHistogram.Bins], 0, 0.5).ShouldBeNull();
}
