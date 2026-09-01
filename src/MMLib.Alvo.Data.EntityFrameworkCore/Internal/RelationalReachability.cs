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
/// third relational driver inherits a correct probe instead of owing one.
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
internal sealed class RelationalReachability(RelationalConnectionFactory connections) : IAlvoDataReachability
{
    /// <summary>The round trip: the cheapest statement that proves the engine answered.</summary>
    /// <remarks>
    /// <para>
    /// <b>A constant here rather than a member on <see cref="IAlvoSqlDialect"/>, deliberately reversed from
    /// the first draft of this file.</b> That draft added a default interface member so a dialect for an
    /// engine spelling a bare projection differently (Oracle's <c>SELECT 1 FROM DUAL</c>) could override it —
    /// and then no dialect overrode it: SQLite, PostgreSQL and <c>TSqlSqlDialect</c> all inherited the
    /// default, so the only thing the member bought was one more obligation on a public interface every
    /// out-of-repo dialect author reads. A default interface member can be added on the day a driver needs
    /// it <em>without breaking anyone</em>, which is exactly the asymmetry that says not to add it now.
    /// </para>
    /// <para>
    /// It touches <b>no table</b>, so a schema problem can never be reported as unreachability — that is a
    /// different question with its own health check — and it carries no parameter and nothing a caller could
    /// influence, which is why this file's place on <c>ChangeTrackerReachTests</c>' SQL-composing allow-list
    /// costs nothing.
    /// </para>
    /// </remarks>
    private const string ProbeStatement = "SELECT 1";

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
        command.CommandText = ProbeStatement;

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TheStoreDidNotAnswer(Exception failure) =>
        failure is DbException or TimeoutException;
}
