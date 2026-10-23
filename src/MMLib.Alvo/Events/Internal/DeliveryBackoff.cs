using MMLib.Alvo.Extensions;

using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Events.Internal;

/// <summary>The wait before a failed delivery's next attempt: the policy's delay, spread by up to a fifth either way (spec X5).</summary>
internal sealed class DeliveryBackoff(Func<double> sample)
{
    internal const double Jitter = 0.2;

    [SuppressMessage("Security", "CA5394", Justification = "Retry jitter spreads load; it guards nothing.")]
    internal DeliveryBackoff()
        : this(Random.Shared.NextDouble)
    {
    }

    internal TimeSpan? After(int attempt, AlvoRetryPolicy policy) =>
        policy.DelayAfter(attempt) is { } delay ? delay * (1 - Jitter + (2 * Jitter * sample())) : null;
}
