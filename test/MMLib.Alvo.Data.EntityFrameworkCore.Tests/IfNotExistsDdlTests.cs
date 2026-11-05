using Microsoft.Data.Sqlite;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A framework object's create tells "a rival created it a moment ago" from a real failure, by reading the outcome rather
/// than the error. The lost race itself is PostgreSQL's (<c>PostgreSqlColdFrameworkTableTests</c>); here a create the
/// engine refuses because the name is taken stands in for it, deterministically, on any engine.
/// </summary>
public sealed class IfNotExistsDdlTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public IfNotExistsDdlTests() => _connection.Open();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_create_refused_because_a_rival_already_made_the_table_succeeds()
    {
        await ExecuteAsync("CREATE TABLE rival (id TEXT NOT NULL)");

        await Should.NotThrowAsync(() => IfNotExistsDdl.CreateTableAsync(_connection, "rival", "CREATE TABLE rival (id TEXT NOT NULL)", Ct));
    }

    [Fact]
    public async Task A_create_that_failed_and_left_no_table_rethrows_the_engines_error()
    {
        var failure = await Should.ThrowAsync<DbException>(
            () => IfNotExistsDdl.CreateTableAsync(_connection, "never", "CREATE TABLE never (id TEXT NOT NULL, id TEXT NOT NULL)", Ct));

        failure.Message.ShouldContain("duplicate column");
    }

    [Fact]
    public async Task An_index_that_cannot_be_built_rethrows_although_its_table_exists()
    {
        await ExecuteAsync("CREATE TABLE host (id TEXT NOT NULL)");

        var failure = await Should.ThrowAsync<DbException>(
            () => IfNotExistsDdl.CreateIndexAsync(_connection, "CREATE INDEX IF NOT EXISTS ix_host_missing ON host (missing)", Ct));

        failure.Message.ShouldContain("missing");
    }

    [Fact]
    public async Task Both_creates_are_no_ops_when_the_objects_are_in_place()
    {
        await IfNotExistsDdl.CreateTableAsync(_connection, "twice", "CREATE TABLE IF NOT EXISTS twice (id TEXT NOT NULL)", Ct);
        await IfNotExistsDdl.CreateIndexAsync(_connection, "CREATE INDEX IF NOT EXISTS ix_twice ON twice (id)", Ct);

        await Should.NotThrowAsync(async () =>
        {
            await IfNotExistsDdl.CreateTableAsync(_connection, "twice", "CREATE TABLE IF NOT EXISTS twice (id TEXT NOT NULL)", Ct);
            await IfNotExistsDdl.CreateIndexAsync(_connection, "CREATE INDEX IF NOT EXISTS ix_twice ON twice (id)", Ct);
        });
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ExecuteAsync(string sql)
    {
        var command = _connection.CreateCommand();
        await using (command)
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
