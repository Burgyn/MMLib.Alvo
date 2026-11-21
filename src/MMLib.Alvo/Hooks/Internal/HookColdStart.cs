using MMLib.Alvo.Extensions;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// The cold-start allowance (#519): until a hook's first run in this host has ended, its runs get <see cref="Allowance"/>
/// instead of the hook's own timeout, because that first run also pays for the JIT, the type loading, the DI activation and
/// the first query compilation of its path — work no later run repeats. Once one run has ended — succeeded, refused or
/// failed, a timeout included — the hook is warm and every run after it has its own timeout.
/// </summary>
/// <remarks>
/// Held by the runner, so it lives as long as the host's service provider: one process in production, one host in a test
/// process that starts several. A timed-out run is never retried — it would run the host's code twice.
/// </remarks>
internal sealed class HookColdStart
{
    internal const int Factor = 10;

    private readonly ConcurrentDictionary<string, bool> _warm = new(StringComparer.Ordinal);

    /// <summary>Gets the longest a cold run may take: the validate stage's default, the longest a stage's default trusts a run with.</summary>
    internal static TimeSpan Cap => AlvoHookOptions.DefaultValidateTimeout;

    /// <summary>The cold allowance for <paramref name="timeout"/>: <see cref="Factor"/> times it, at most <see cref="Cap"/>, never less than it.</summary>
    internal static TimeSpan Allowance(TimeSpan timeout) => TimeSpan.FromTicks(Math.Max(timeout.Ticks, Math.Min(timeout.Ticks * Factor, Cap.Ticks)));

    /// <summary>Whether <paramref name="hook"/> is still cold: no run of it has ended in this host yet.</summary>
    internal bool IsCold(string hook) => !_warm.ContainsKey(hook);

    /// <summary>The time a run of <paramref name="hook"/> starting now may take.</summary>
    internal TimeSpan Budget(string hook, TimeSpan timeout) => IsCold(hook) ? Allowance(timeout) : timeout;

    /// <summary>Records that a run of <paramref name="hook"/> has ended, so every run that starts after it is warm.</summary>
    internal void Ended(string hook) => _warm.TryAdd(hook, true);
}
