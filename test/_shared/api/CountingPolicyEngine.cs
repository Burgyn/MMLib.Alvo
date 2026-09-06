using MMLib.Alvo.Data;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Counts every <see cref="IPolicyEngine.Resolve"/> call one request makes, forwarding each verbatim to the
/// real engine.
/// </summary>
/// <remarks>
/// The decision it returns is the inner engine's own object, not a copy: a fact about how many times the
/// policy is resolved must not also change what any of those resolutions decided.
/// </remarks>
/// <param name="inner">The engine Alvo registered, which remains the authority on every decision.</param>
internal sealed class CountingPolicyEngine(IPolicyEngine inner) : IPolicyEngine
{
    private readonly List<(string Entity, DataOperation Operation)> _resolved = [];

    /// <summary>Every resolution so far, in order.</summary>
    internal IReadOnlyList<(string Entity, DataOperation Operation)> Resolved
    {
        get
        {
            lock (_resolved)
            {
                return [.. _resolved];
            }
        }
    }

    /// <summary>Forgets what has been recorded, so one fact can measure one request.</summary>
    /// <remarks>
    /// Priming a world applies a descriptor and starts the host, and the read paths that runs resolve
    /// policies of their own. Without this, every count would carry that setup with it.
    /// </remarks>
    internal void Clear()
    {
        lock (_resolved)
        {
            _resolved.Clear();
        }
    }

    /// <summary>The recorded calls as <c>entity:operation</c>, for a failure message worth reading.</summary>
    /// <remarks>
    /// A count assertion that fails with "expected 2, was 3" cannot be acted on without a debugger; the same
    /// failure naming <c>owners:List, owners:List, owners:Get</c> says which resolution is the new one.
    /// </remarks>
    internal string Trace() => Resolved.Count == 0
        ? "(no policy resolutions recorded)"
        : string.Join(", ", Resolved.Select(call => $"{call.Entity}:{call.Operation}"));

    /// <inheritdoc/>
    public PolicyDecision Resolve(string entity, DataOperation operation, AlvoContext context)
    {
        lock (_resolved)
        {
            _resolved.Add((entity, operation));
        }

        return inner.Resolve(entity, operation, context);
    }
}
