using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Testing.Scheduling;

using System.Data.Common;
using System.Globalization;

using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// One <see cref="EfCoreTimerStore"/> over a started database, the outbox it appends to, and a hand-moved clock. Linked into
/// both driver test projects.
/// </summary>
internal sealed class TimerStoreWorld : ITimerStoreWorld
{
    private const string SchemaPrefix = "alvo";

    private static readonly DateTimeOffset _start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly Func<DbConnection> _connect;
    private readonly FakeTimeProvider _clock = new(_start);
    private readonly string _outbox = OutboxTable.NameFor(SchemaPrefix);

    private TimerStoreWorld(Func<DbConnection> connect)
    {
        _connect = connect;
        Store = new EfCoreTimerStore(
            new RelationalConnectionFactory(connect), new AlvoOptions { SchemaPrefix = SchemaPrefix }, _clock, NoData);
    }

    public ITimerStore Store { get; }

    public DateTimeOffset Now => _clock.GetUtcNow();

    internal static async Task<TimerStoreWorld> StartAsync(Func<DbConnection> connect)
    {
        var world = new TimerStoreWorld(connect);
        await world.Store.EnsureAsync(Ct);

        return world;
    }

    public void Advance(TimeSpan duration) => _clock.Advance(duration);

    public async Task<IReadOnlyList<string>> OutboxTypesAsync() =>
        await ReadAsync($"SELECT event_type FROM {_outbox} ORDER BY id", reader => reader.GetString(0));

    public async Task BreakOutboxAsync()
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);
            await RelationalSqlBatch.ExecuteAsync(connection, [$"DROP TABLE {_outbox}"], transaction: null, Ct);
        }
    }

    public async Task<IReadOnlyList<(string Id, int Revision)>> MigrationRowsAsync() =>
        await ReadAsync(
            $"SELECT id, revision FROM {SchemaPrefix}{AlvoFrameworkTables.DataMigrationsSuffix} ORDER BY id",
            reader => (reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static EfAlvoData NoData() =>
        throw new InvalidOperationException("The timer-store world has no data port: its store never runs inside a unit.");

    private async Task<List<T>> ReadAsync<T>(string sql, Func<DbDataReader, T> read)
    {
        var connection = _connect();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(Ct);

            return await RelationalSqlBatch.ReadRowsAsync(connection, transaction: null, sql, _ => { }, read, Ct);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
