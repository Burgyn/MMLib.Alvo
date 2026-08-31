using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// #133's port for every EF-backed driver at once: open a connection this probe owns, make the dialect's one
/// round trip, and dispose it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation rather than one per provider package.</b> The issue's scope said "implemented once
/// per <c>MMLib.Alvo.Data.*</c> package"; it was written before the shared EF path became the place
/// <see cref="IAlvoData"/>, <see cref="MMLib.Alvo.Events.IOutboxStore"/> and the three schema services are
/// all composed. Two identical implementations are the drift that seam exists to prevent, and one means a
/// third relational driver inherits a correct probe instead of owing one. The engine-specific half is
/// <see cref="IAlvoSqlDialect.ReachabilityProbeStatement"/>.
/// </para>
/// <para>
/// <b>A fresh connection per probe, from the same factory every other store here uses.</b> A held connection
/// would be the one thing a probe must not have: a socket that died silently answers from a cached client
/// object until something writes to it, which is the exact false "reachable" this check exists to refuse.
/// </para>
/// <para>
/// <b>What is caught, and what is deliberately not.</b> Only the engine's own failure to answer — a
/// <see cref="DbException"/> or a <see cref="TimeoutException"/> — becomes
/// <see cref="AlvoReachability.Unreachable"/>. Anything else propagates: a misconfiguration is not
/// unreachability, and the health-check service reports a check that threw as its registration's failure
/// status anyway, with the framework's own log record. So the narrow catch costs no availability signal and
/// keeps a defect from being reported as a database outage.
/// </para>
/// <para>
/// <b>A cancelled probe is never reported as unreachable.</b> The token is re-read inside the catch, because
/// a driver may well surface cancellation as its own <see cref="DbException"/> — and "the caller's bound
/// elapsed" is a different diagnosis from "the store said it is away". Answering the latter for the former
/// would report a probe that is merely too slow as a database outage; throwing
/// <see cref="OperationCanceledException"/> instead is what lets the health-check service report its own
/// timeout, and it is the obligation <c>MMLib.Alvo.Testing.Data.AlvoDataReachabilityContractTests</c> holds
/// every implementation to.
/// </para>
/// </remarks>
/// <param name="connections">The factory every other store in this package opens through.</param>
/// <param name="dialect">The driver whose one probe statement this executes.</param>
internal sealed class RelationalReachability(RelationalConnectionFactory connections, IAlvoSqlDialect dialect)
    : IAlvoDataReachability
{
    /// <inheritdoc/>
    public async ValueTask<AlvoReachability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RoundTripAsync(cancellationToken).ConfigureAwait(false);

            return AlvoReachability.Reachable;
        }
        catch (Exception failure) when (TheStoreDidNotAnswer(failure))
        {
            cancellationToken.ThrowIfCancellationRequested();

            return AlvoReachability.Unreachable(failure);
        }
    }

    private async Task RoundTripAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = dialect.ReachabilityProbeStatement;

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TheStoreDidNotAnswer(Exception failure) =>
        failure is DbException or TimeoutException;
}
