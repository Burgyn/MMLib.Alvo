using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The test host's facts with <c>UsePostgreSql</c> in its builder — a fresh database per fact on one container
/// (spec 2026-10-14 §7 HX-02, acceptance (4)), dropped when the fact ends.
/// </summary>
/// <remarks>
/// <para>
/// In <see cref="DispatchedEventCollection"/>, because the facts dispatch events and this assembly's criteria suites
/// assert process-wide event counters.
/// </para>
/// <para>
/// Disposing a test host stops it but leaves a database the builder registered, as the docs tell a reader to expect,
/// so this leg drops what it created itself — after the fact has disposed its host, with <c>FORCE</c> for the
/// connections the host's pool still holds.
/// </para>
/// </remarks>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlAlvoTestHostTests(PostgresFixture postgres)
    : AlvoTestHostFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly List<string> _databases = [];

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await ExecuteAsync(postgres.ConnectionString, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    /// <inheritdoc/>
    protected override async Task<Action<IAlvoBuilder>> DatabaseAsync()
    {
        Assert.SkipWhen(
            string.IsNullOrEmpty(postgres.ConnectionString),
            "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var name = $"alvo_testhost_{Guid.NewGuid():N}";
        await ExecuteAsync(postgres.ConnectionString, $"CREATE DATABASE \"{name}\"");
        _databases.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();

        return alvo => alvo.UsePostgreSql(connectionString);
    }

    private static async Task ExecuteAsync(string adminConnectionString, string sql)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
