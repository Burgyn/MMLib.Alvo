namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// A fixed-memory latency histogram: 128 log-scale bins a quarter-octave wide from 0.1 ms, so a reported percentile — the
/// upper bound of the bin holding its rank — is within 19 % of the true value, up to about five days.
/// </summary>
internal static class LatencyHistogram
{
    internal const int Bins = 128;

    private const double Smallest = 0.1;
    private const double StepsPerOctave = 4;

    internal static int Bin(double milliseconds) =>
        milliseconds <= Smallest || double.IsNaN(milliseconds)
            ? 0
            : (int)Math.Clamp(Math.Ceiling(Math.Log2(milliseconds / Smallest) * StepsPerOctave) - 1, 0, Bins - 1);

    internal static double UpperBound(int bin) => Smallest * Math.Pow(2, (bin + 1) / StepsPerOctave);

    internal static TimeSpan? Percentile(long[] bins, long count, double quantile)
    {
        if (count == 0)
        {
            return null;
        }

        var rank = Math.Max(1, (long)Math.Ceiling(quantile * count));
        long seen = 0;
        for (var bin = 0; bin < bins.Length; bin++)
        {
            seen += bins[bin];
            if (seen >= rank)
            {
                return TimeSpan.FromMilliseconds(UpperBound(bin));
            }
        }

        return TimeSpan.FromMilliseconds(UpperBound(Bins - 1));
    }
}
