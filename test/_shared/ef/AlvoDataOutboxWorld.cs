using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Testing.Data;

using System.Data.Common;

using Xunit;

// EF1001 matches on a namespace ending in ".Internal", so here it flags Alvo's OWN internals — both driver
// test projects are granted them by InternalsVisibleTo — rather than an Entity Framework internal API.
#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The read side of one started database's outbox, as <see cref="AlvoDataOutboxTests"/> asks for it: the data
/// port plus every queued event, in the order a dispatcher would claim them.
/// </summary>
/// <remarks>
/// <para>
/// Linked into both engine test projects rather than copied, for the reason <see cref="DifferentialProbe"/> is:
/// the question the inherited suite asks is engine-agnostic, and two per-engine copies of the reader are two
/// chances for the engines to stop being asked the same one. Everything engine-specific arrives as the
/// <see cref="IAlvoData"/> and the container the fixture already built.
/// </para>
/// <para>
/// It selects the <c>payload</c> column and hands it to <see cref="AlvoEventJson.Read(string)"/> — the
/// dispatcher's own reader, never a second copy of it — on a connection of its own, so nothing here can be
/// mistaken for the write path's transaction. The <c>ORDER BY id</c> is the claim's own ordering, so a fact
/// asserting a sequence is asserting the order a dispatcher would deliver in.
/// </para>
/// </remarks>
internal sealed class AlvoDataOutboxWorld(IAlvoData data, IServiceProvider services) : IAlvoDataOutboxWorld
{
    private const string SchemaPrefix = "alvo";

    private readonly string _tableName = OutboxTable.NameFor(SchemaPrefix);

    public IAlvoData Data { get; } = data;

    public async Task<IReadOnlyList<AlvoEvent>> EventsAsync()
    {
        using var db = services.GetRequiredService<AlvoDataContextFactory>().Create();
        var connection = db.Database.GetDbConnection();
        await RelationalSqlBatch.OpenAsync(connection, Ct);

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"SELECT payload FROM {_tableName} ORDER BY id";

            return await ReadAllAsync(command);
        }
    }

    private static async Task<IReadOnlyList<AlvoEvent>> ReadAllAsync(DbCommand command)
    {
        var reader = await command.ExecuteReaderAsync(Ct);
        await using (reader.ConfigureAwait(false))
        {
            var events = new List<AlvoEvent>();
            while (await reader.ReadAsync(Ct))
            {
                events.Add(AlvoEventJson.Read(reader.GetString(0)));
            }

            return events;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
