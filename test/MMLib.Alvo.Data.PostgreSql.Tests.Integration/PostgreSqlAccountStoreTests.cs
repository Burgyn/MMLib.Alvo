using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of <see cref="AccountStoreFacts"/> — the leg where two concurrent sign-ins, refreshes and first key
/// inserts really overlap in the engine, since no in-process write gate serialises them.
/// </summary>
public sealed class PostgreSqlAccountStoreTests : AccountStoreFacts, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync(Action<IServiceCollection>? configure) =>
        (await _fixture.StartAsync(new SchemaModel([]), configure: configure)).Services;

    /// <inheritdoc/>
    protected override ValueTask DisposeFixtureAsync() => _fixture.DisposeAsync();
}
