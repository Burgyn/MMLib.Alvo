using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data;

/// <summary>
/// The one causation-chain limit every write and publish entry enforces, built by the core from
/// <c>Alvo:Extensions:MaxDepth</c> and resolved by every driver that implements <c>IAlvoData</c>.
/// </summary>
/// <remarks>
/// Internal and in Abstractions because the EF driver must enforce a limit the core configures and may not reference
/// the core's options type; the grant to the driver already exists for <c>AlvoFrameworkTables</c>.
/// </remarks>
internal sealed class AlvoChainDepthLimit
{
    /// <summary>The default limit (spec 2026-10-14 X3): a legitimate hook→write→hook→event→handler→write chain uses 5.</summary>
    internal const int DefaultMaxDepth = 8;

    /// <summary>Initializes a limit that allows chains up to <paramref name="maxDepth"/> hops deep.</summary>
    /// <param name="maxDepth">The deepest chain still allowed.</param>
    internal AlvoChainDepthLimit(int maxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        MaxDepth = maxDepth;
    }

    /// <summary>The limit at its default, for stores no configuration reaches (<c>InMemoryAlvoData</c>).</summary>
    internal static AlvoChainDepthLimit Default { get; } = new(DefaultMaxDepth);

    /// <summary>The deepest chain still allowed; a depth equal to it is accepted.</summary>
    internal int MaxDepth { get; }

    /// <summary>Refuses <paramref name="context"/> when its effective depth is greater than <see cref="MaxDepth"/>.</summary>
    /// <param name="context">The caller, whose explicit provenance is merged with the ambient flow.</param>
    /// <returns>The effective depth, which the operation then reports.</returns>
    /// <exception cref="AlvoChainDepthExceededException">The effective depth is greater than <see cref="MaxDepth"/>.</exception>
    internal int EnsureWithin(AlvoContext context)
    {
        var depth = AlvoEventProvenance.Effective(context).Depth;

        return depth <= MaxDepth ? depth : throw AlvoChainDepthExceededException.At(depth, MaxDepth);
    }
}
