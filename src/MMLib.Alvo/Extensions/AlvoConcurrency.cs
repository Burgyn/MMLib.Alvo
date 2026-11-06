using System.Globalization;

namespace MMLib.Alvo.Extensions;

/// <summary>
/// A cap on how many of one handler's deliveries run at once per key — per tenant, or per record of a tenant — across every
/// replica on one database. A delivery over the cap waits, pending, until one of its key finishes.
/// </summary>
/// <remarks>
/// The cap is per handler: two handlers of one event each have their own. A delivery holds its place until it is answered —
/// succeeded, failed or asleep — or, if its process died, until its claim's lease expires.
/// </remarks>
public sealed class AlvoConcurrency
{
    private const string TenantKey = "tenant";
    private const string RecordKey = "record";
    private const int MaxLimit = 100;

    private AlvoConcurrency(string key, int limit)
    {
        Key = key;
        Limit = limit;
    }

    /// <summary>Gets what the cap counts per: <c>tenant</c> or <c>record</c>.</summary>
    public string Key { get; }

    /// <summary>Gets how many deliveries of one key may run at once.</summary>
    public int Limit { get; }

    /// <summary>
    /// Caps the handler's running deliveries per <paramref name="key"/>: <c>tenant</c> counts every delivery of one tenant,
    /// <c>record</c> every delivery of one record — one partition key — of one tenant.
    /// </summary>
    /// <param name="key"><c>tenant</c> or <c>record</c>.</param>
    /// <param name="limit">How many may run at once, 1 to 100.</param>
    /// <returns>The cap.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is neither <c>tenant</c> nor <c>record</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is below 1 or above 100.</exception>
    public static AlvoConcurrency PerKey(string key, int limit)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key is not (TenantKey or RecordKey))
        {
            throw new ArgumentException($"'{key}' is not a concurrency key; use \"tenant\" or \"record\".", nameof(key));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);

        return new AlvoConcurrency(key, limit);
    }

    /// <summary>The cap as the catalog shows it: <c>tenant:2</c>, <c>record:1</c>.</summary>
    /// <returns>The key, a colon and the limit.</returns>
    public override string ToString() => $"{Key}:{Limit.ToString(CultureInfo.InvariantCulture)}";
}
