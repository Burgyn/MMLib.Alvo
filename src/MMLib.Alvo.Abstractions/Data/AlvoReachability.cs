namespace MMLib.Alvo.Data;

/// <summary>What <see cref="IAlvoDataReachability.ProbeAsync"/> answered: reachable, or not and why.</summary>
/// <remarks>
/// <para>
/// <b><see cref="Failure"/> is for the log and never for a response.</b> An unreachable store's exception is
/// the driver's own message and can carry a connection string or a filesystem path, while the readiness
/// endpoint that consumes this is anonymous by construction — a container probe presents nothing to
/// authenticate with. So the reason travels to the operator's log and the probe learns only that the pod is
/// not ready, which is the same split <c>AlvoProblemTypes.Internal</c> makes for a 500 (design deviation 59).
/// </para>
/// <para>
/// <b>Two states, and there is no third.</b> See <see cref="IAlvoDataReachability"/> for why "cannot answer"
/// is expressed by not registering the port rather than by a value here.
/// </para>
/// </remarks>
public sealed class AlvoReachability
{
    private AlvoReachability(bool isReachable, Exception? failure)
    {
        IsReachable = isReachable;
        Failure = failure;
    }

    /// <summary>The store answered.</summary>
    public static AlvoReachability Reachable { get; } = new(isReachable: true, failure: null);

    /// <summary>Whether the store answered.</summary>
    public bool IsReachable { get; }

    /// <summary>
    /// Why the store could not be reached, or <see langword="null"/> when it could. For logging only — see
    /// this type's remarks.
    /// </summary>
    public Exception? Failure { get; }

    /// <summary>The store could not be reached, for the reason an operator has to read.</summary>
    /// <remarks>
    /// The failure is required rather than optional: an implementation that has determined unreachability has
    /// something that told it so, and a probe reporting "not reachable" with no reason leaves an operator with
    /// a drained pod and nothing to act on.
    /// </remarks>
    /// <param name="failure">Why the store could not be reached.</param>
    /// <returns>An unreachable answer carrying <paramref name="failure"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    public static AlvoReachability Unreachable(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new AlvoReachability(isReachable: false, failure);
    }
}
