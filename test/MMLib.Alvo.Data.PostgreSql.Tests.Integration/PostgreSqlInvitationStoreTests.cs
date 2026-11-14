using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of <see cref="InvitationStoreFacts"/> — the leg where a creation racing its creator's demotion, and
/// two accepts racing for one use, really overlap in the engine, since no in-process write gate serialises them.
/// </summary>
public sealed class PostgreSqlInvitationStoreTests : InvitationStoreFacts, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    protected override string? LockWaitsSql =>
        "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'";

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartAsync() => (await _fixture.StartAsync(new SchemaModel([]))).Services;

    /// <inheritdoc/>
    protected override ValueTask DisposeFixtureAsync() => _fixture.DisposeAsync();
}
