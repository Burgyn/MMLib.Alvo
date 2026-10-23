using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The test host's facts with <c>UsePostgreSql</c> in its builder — a fresh database per fact on one container
/// (spec 2026-10-14 §7 HX-02, acceptance (4)).
/// </summary>
/// <remarks>
/// In <see cref="DispatchedEventCollection"/>, because the facts dispatch events and this assembly's criteria suites
/// assert process-wide event counters.
/// </remarks>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlAlvoTestHostTests(PostgresFixture postgres) : AlvoTestHostFacts, IClassFixture<PostgresFixture>
{
    /// <inheritdoc/>
    protected override async Task<Action<IAlvoBuilder>> DatabaseAsync()
    {
        Assert.SkipWhen(
            string.IsNullOrEmpty(postgres.ConnectionString),
            "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var connectionString = await CreateDatabaseAsync(postgres.ConnectionString);

        return alvo => alvo.UsePostgreSql(connectionString);
    }

    private static async Task<string> CreateDatabaseAsync(string adminConnectionString)
    {
        var name = $"alvo_testhost_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{name}\"";
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ToString();
    }
}
