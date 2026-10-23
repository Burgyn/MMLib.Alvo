namespace MMLib.Alvo.Extensions;

/// <summary>How long a failed delivery waits before each retry; one attempt more than there are waits.</summary>
/// <remarks>Each wait is spread by up to a fifth either way, so many deliveries failing together do not retry together.</remarks>
public sealed class AlvoRetryPolicy
{
    private AlvoRetryPolicy(IReadOnlyList<TimeSpan> delays) => Delays = delays;

    /// <summary>Gets the default: 5 s, 30 s, 2 min, 15 min, 1 h and 6 h — seven attempts across about seven hours.</summary>
    public static AlvoRetryPolicy Default { get; } = new([
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)]);

    /// <summary>Gets a policy with no retry: the first failure is final.</summary>
    public static AlvoRetryPolicy None { get; } = new([]);

    /// <summary>Gets the waits, the first after attempt 1.</summary>
    public IReadOnlyList<TimeSpan> Delays { get; }

    /// <summary>Gets the attempt after which a failing delivery is dead.</summary>
    public int MaxAttempts => Delays.Count + 1;

    /// <summary>Builds a policy waiting <paramref name="delays"/> between attempts.</summary>
    /// <param name="delays">The waits, the first after attempt 1; none negative.</param>
    /// <returns>The policy.</returns>
    public static AlvoRetryPolicy From(params TimeSpan[] delays)
    {
        ArgumentNullException.ThrowIfNull(delays);
        foreach (var delay in delays)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero, nameof(delays));
        }

        return new([.. delays]);
    }

    internal TimeSpan? DelayAfter(int attempt) => attempt >= 1 && attempt <= Delays.Count ? Delays[attempt - 1] : null;
}
