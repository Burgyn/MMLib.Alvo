using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of <see cref="AccountStoreFacts"/>.</summary>
public sealed class SqliteAccountStoreTests : AccountStoreFacts
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync(Action<IServiceCollection>? configure) =>
        (await _fixture.StartAsync(new SchemaModel([]), configure: configure)).Services;

    /// <inheritdoc/>
    protected override ValueTask DisposeFixtureAsync() => _fixture.DisposeAsync();
}
