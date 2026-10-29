using Microsoft.Data.Sqlite;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The boot's framework-table step tells a lost creation race from a real failure, for indexes as well as tables.
/// </summary>
public sealed class SystemSchemaInitializerTests : IDisposable
{
    private readonly string _connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    public SystemSchemaInitializerTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    /// <summary>
    /// An index that cannot be built fails the boot with the engine's own error. The table it is built on exists, so a
    /// recovery that only asked whether the table is there would swallow it and boot without the index.
    /// </summary>
    [Fact]
    public async Task An_index_that_cannot_be_built_fails_the_boot()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync("CREATE TABLE alvo_outbox (id TEXT NOT NULL PRIMARY KEY)", ct);

        var failure = await Should.ThrowAsync<DbException>(() => new SystemSchemaInitializer(_keepAlive, "alvo").EnsureAsync(ct));

        failure.Message.ShouldContain("partition_key");
    }

    /// <summary>Every framework table and index already in place is a no-op, as on every boot after the first.</summary>
    [Fact]
    public async Task A_boot_over_complete_framework_tables_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await new SystemSchemaInitializer(_keepAlive, "alvo").EnsureAsync(ct);

        await Should.NotThrowAsync(() => new SystemSchemaInitializer(_keepAlive, "alvo").EnsureAsync(ct));
    }

    /// <summary>The boot creates the command and inbound webhook ledgers, each with its sweep index (Ruling 41).</summary>
    [Fact]
    public async Task The_boot_creates_both_ledgers_and_their_sweep_indexes()
    {
        var ct = TestContext.Current.CancellationToken;

        await new SystemSchemaInitializer(_keepAlive, "alvo").EnsureAsync(ct);

        var objects = await SchemaObjectsAsync(ct);

        objects.ShouldContain("alvo_command_requests");
        objects.ShouldContain("alvo_command_requests_claimed");
        objects.ShouldContain("alvo_inbound_webhooks");
        objects.ShouldContain("alvo_inbound_webhooks_received");
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<List<string>> SchemaObjectsAsync(CancellationToken ct)
    {
        var command = _keepAlive.CreateCommand();
        await using (command)
        {
            command.CommandText = "SELECT name FROM sqlite_master";
            var reader = await command.ExecuteReaderAsync(ct);
            await using (reader)
            {
                var names = new List<string>();
                while (await reader.ReadAsync(ct))
                {
                    names.Add(reader.GetString(0));
                }

                return names;
            }
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        var command = _keepAlive.CreateCommand();
        await using (command)
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
