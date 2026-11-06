using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Scheduling;

/// <summary>How one scheduled job runs.</summary>
public sealed class AlvoScheduleOptions
{
    private AlvoRetryPolicy _retry = AlvoRetryPolicy.Default;

    /// <summary>Gets or sets where an occurrence runs; default <see cref="AlvoScheduleScope.Global"/>.</summary>
    public AlvoScheduleScope Scope { get; set; } = AlvoScheduleScope.Global;

    /// <summary>
    /// Gets or sets how long one attempt may run before it is cancelled and counted as failed; default 15 minutes, at most
    /// 1 hour.
    /// </summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Gets or sets the retry schedule of a failed run; default <see cref="AlvoRetryPolicy.Default"/>.</summary>
    public AlvoRetryPolicy Retry
    {
        get => _retry;
        set => _retry = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets or sets what the job does, in a sentence, for the dashboard and the docs.</summary>
    public string? Description { get; set; }

    internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(15);
}
