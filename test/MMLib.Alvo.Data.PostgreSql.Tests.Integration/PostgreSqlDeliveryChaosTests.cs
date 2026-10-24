using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Tests.Events;

using Npgsql;

using System.Data.Common;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The PostgreSQL leg of the delivery chaos criterion, in a database of its own.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlDeliveryChaosTests(PostgresFixture postgres)
    : DeliveryChaosCriteriaTests, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _name = $"alvo_delivery_chaos_{Guid.NewGuid():N}";

    protected override string EngineDescription => "postgres:16-alpine, " + BuildConfiguration;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await ExecuteAsync($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");

    protected override async Task<(Action<IAlvoBuilder> Use, Func<DbConnection> Connect)> DatabaseAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");
        await ExecuteAsync($"CREATE DATABASE \"{_name}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = _name }.ToString();

        return (alvo => alvo.UsePostgreSql(connectionString), () => new NpgsqlConnection(connectionString));
    }

    private async Task ExecuteAsync(string sql)
    {
        if (string.IsNullOrEmpty(postgres.ConnectionString))
        {
            return;
        }

        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
