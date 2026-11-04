using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Shouldly;

using System.Globalization;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The write-hook facts with <c>UsePostgreSql</c> — a fresh database per fact on one container; a validate hook proves no
/// transaction of its write is open by finding no other backend of the database <c>idle in transaction</c> (Ruling 44).
/// </summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlWriteHookTests(PostgresFixture postgres)
    : WriteHookFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly List<string> _databases = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    protected override async Task<HookDatabase> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_hooks_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();

        return new HookDatabase(alvo => alvo.UsePostgreSql(connectionString), () => new NpgsqlConnection(connectionString));
    }

    protected override async Task AssertNoOpenTransactionAsync(HookDatabase database)
    {
        await using var connection = database.Connect();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state LIKE 'idle in transaction%'";

        Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture)
            .ShouldBe(0, "a validate hook runs with no transaction of the write open");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
