using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of <see cref="AppTokenStoreFacts"/> — the leg where concurrent mints of one person really overlap in the
/// engine, so only the account-row lock keeps the live cap.
/// </summary>
public sealed class PostgreSqlAppTokenStoreTests : AppTokenStoreFacts, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync() => (await _fixture.StartAsync(new SchemaModel([]))).Services;

    /// <inheritdoc/>
    protected override ValueTask DisposeFixtureAsync() => _fixture.DisposeAsync();
}
