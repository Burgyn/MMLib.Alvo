using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The read-extension facts with <c>UsePostgreSql</c> — a fresh database per fact on one container, its name the capture's marker.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlReadExtensionTests(PostgresFixture postgres)
    : ReadExtensionFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
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

    protected override async Task<ReadsDatabase> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_reads_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();

        return new ReadsDatabase(alvo => alvo.UsePostgreSql(connectionString), name);
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
