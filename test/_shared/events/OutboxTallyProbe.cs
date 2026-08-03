using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Testing.Events;

using System.Globalization;

using Xunit;

// EF1001 matches on a namespace ending in ".Internal", so here it flags Alvo's OWN internals — both driver
// test projects are granted them by InternalsVisibleTo — rather than an Entity Framework internal API.
#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The outbox's own state, read straight off the table: how many entries are still undelivered, and how many
/// have been retired.
/// </summary>
/// <remarks>
/// <para>
/// <b>One authority for both counts, shared by every event criterion here.</b> A criterion about absence — no
/// second delivery, no execution-log entry, no lost event — passes just as well when no event was ever written
/// or nothing was ever claimed, and this pair of counts is what rules both out. It is not decoration:
/// measured while Task 10's facts were being proved, dropping <c>MarkDispatchedAsync</c> turns three of those
/// four facts red on the tally alone, with their deliveries and their counters unchanged.
/// </para>
/// <para>
/// It reads on a connection of its own, from the driver's own factory, so nothing here can be mistaken for the
/// write path's transaction or hold a lock the pump then waits on.
/// </para>
/// </remarks>
/// <param name="services">The started container the driver's connection factory is resolved from.</param>
internal sealed class OutboxTallyProbe(IServiceProvider services)
{
    /// <summary>Counts what is still undelivered and what has been retired, in that order.</summary>
    internal async Task<AlvoOutboxTally> TallyAsync() =>
        new(await CountAsync(Undelivered), await CountAsync(Retired));

    /// <summary>
    /// How many outbox rows satisfy <paramref name="predicate"/>.
    /// </summary>
    /// <param name="predicate">
    /// One of the two constants below — the queue's state machine, never anything caller-supplied.
    /// </param>
    private async Task<int> CountAsync(string predicate)
    {
        var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await using (connection.ConfigureAwait(false))
        {
            await RelationalSqlBatch.OpenAsync(connection, Ct);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = $"SELECT COUNT(*) FROM {_tableName} WHERE {predicate}";
                var count = await command.ExecuteScalarAsync(Ct);

                return Convert.ToInt32(count, CultureInfo.InvariantCulture);
            }
        }
    }

    private const string SchemaPrefix = "alvo";

    private const string Undelivered = "dispatched_at IS NULL";

    private const string Retired = "dispatched_at IS NOT NULL";

    private readonly string _tableName = OutboxTable.NameFor(SchemaPrefix);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
