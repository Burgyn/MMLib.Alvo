using Testcontainers.PostgreSql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Starts a single, real PostgreSQL container for the lifetime of the test class that shares this
/// fixture, so the contract suite exercises a real engine rather than a fake.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Explicit tag: PostgreSqlBuilder's parameterless ctor and its PostgreSqlImage constant are
    // both obsolete in Testcontainers.PostgreSql 4.13 in favor of an explicit image argument.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
