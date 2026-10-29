using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The transaction facts with <c>UsePostgreSql</c> — a fresh database per fact on one container; the engine where a failed
/// statement aborts the whole transaction (<c>25P02</c>) unless a savepoint undoes it.
/// </summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlTransactionTests(PostgresFixture postgres)
    : TransactionFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
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

    protected override async Task<TransactionDatabase> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_unit_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();

        return new TransactionDatabase(alvo => alvo.UsePostgreSql(connectionString));
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
