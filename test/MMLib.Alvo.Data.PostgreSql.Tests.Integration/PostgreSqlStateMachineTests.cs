using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Tests.Events;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>The state-machine facts with <c>UsePostgreSql</c> — a fresh database per fact on one container.</summary>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlStateMachineTests(PostgresFixture postgres)
    : StateMachineFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFactDatabases _databases = new(postgres, "alvo_states");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _databases.DisposeAsync();

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    protected override async Task<LifecycleDatabase> DatabaseAsync()
    {
        EnsureEngineAvailable();
        var connectionString = await _databases.CreateAsync();

        return new LifecycleDatabase(alvo => alvo.UsePostgreSql(connectionString), () => new NpgsqlConnection(connectionString));
    }
}
