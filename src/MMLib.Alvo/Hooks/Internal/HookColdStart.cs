using MMLib.Alvo.Extensions;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// The cold-start allowance (#519): a hook's cold runs get <see cref="Allowance"/> instead of the hook's own timeout,
/// because they also pay for the JIT, the type loading, the DI activation and the first query compilation of their path —
/// work no later run repeats. Two runs are cold, once per hook per host: every run until the hook's first run has ended,
/// and the first run that calls through <c>write.Transaction</c>, which earns the allowance at that first call — the same
/// cost, deferred past a first run that returned early. Every run until such a run has ended shares it, so concurrent
/// cold runs do too. A run ends a cold start however it ends, a throw or a timeout included, except a run the caller
/// cancelled, which leaves it for the next caller.
/// </summary>
/// <remarks>
/// Held by the runner, so it lives as long as the host's service provider: one process in production, one host in a test
/// process that starts several. A timed-out run is never retried — it would run the host's code twice.
/// </remarks>
internal sealed class HookColdStart
{
    internal const int Factor = 10;

    private readonly ConcurrentDictionary<string, bool> _ran = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _called = new(StringComparer.Ordinal);

    /// <summary>Gets the longest a cold run may take: the validate stage's default, the longest a stage's default trusts a run with.</summary>
    internal static TimeSpan Cap => AlvoHookOptions.DefaultValidateTimeout;

    /// <summary>The cold allowance for <paramref name="timeout"/>: <see cref="Factor"/> times it, at most <see cref="Cap"/>, never less than it.</summary>
    internal static TimeSpan Allowance(TimeSpan timeout) => TimeSpan.FromTicks(Math.Max(timeout.Ticks, Math.Min(timeout.Ticks * Factor, Cap.Ticks)));

    /// <summary>The time a run of <paramref name="hook"/> starting now may take: the allowance until its first run has ended.</summary>
    internal TimeSpan Budget(string hook, TimeSpan timeout) => _ran.ContainsKey(hook) ? timeout : Allowance(timeout);

    /// <summary>The time a run that has just made its first call through the transaction may take, from its start.</summary>
    internal TimeSpan AtFirstCall(string hook, TimeSpan timeout) => _called.ContainsKey(hook) ? timeout : Allowance(timeout);

    /// <summary>Records that a run of <paramref name="hook"/> has ended, and whether it called through the transaction.</summary>
    internal void Ended(string hook, bool called)
    {
        _ran.TryAdd(hook, true);
        if (called)
        {
            _called.TryAdd(hook, true);
        }
    }
}
