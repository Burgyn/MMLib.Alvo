using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of <see cref="InvitationStoreFacts"/> — the write gate serialises every write, so the races are moot here.</summary>
public sealed class SqliteInvitationStoreTests : InvitationStoreFacts
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync() => (await _fixture.StartAsync(new SchemaModel([]))).Services;

    /// <inheritdoc/>
    protected override ValueTask DisposeFixtureAsync() => _fixture.DisposeAsync();
}
