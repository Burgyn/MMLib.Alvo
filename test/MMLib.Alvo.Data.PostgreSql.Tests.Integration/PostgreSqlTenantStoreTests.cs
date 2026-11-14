using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of <see cref="TenantStoreFacts"/> — the leg where concurrent creates at the cap really overlap in the
/// engine, since no in-process write gate serialises them, so only the store's row lock keeps them under it.
/// </summary>
public sealed class PostgreSqlTenantStoreTests : TenantStoreFacts, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync() => (await _fixture.StartAsync(new SchemaModel([]))).Services;

    /// <inheritdoc/>
    protected override ValueTask DisposeFixtureAsync() => _fixture.DisposeAsync();
}
