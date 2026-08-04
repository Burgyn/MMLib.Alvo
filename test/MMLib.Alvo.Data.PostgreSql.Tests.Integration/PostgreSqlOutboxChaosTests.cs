using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The 10 000-event chaos criterion over a real PostgreSQL engine — "no event is lost on SQLite <em>and</em>
/// PostgreSQL", proved rather than assumed. Inherits every assertion unchanged.
/// </summary>
/// <remarks>
/// It belongs to <see cref="DispatchedEventCollection"/> together with
/// <see cref="PostgreSqlAlvoEventCriteriaTests"/>: both assert process-wide event counters by value, and
/// serialising them also means one container at a time rather than two.
/// </remarks>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlOutboxChaosTests : OutboxChaosCriteriaTests, IAsyncLifetime
{
    /// <inheritdoc/>
    protected override string EngineDescription => "postgres:16-alpine, " + BuildConfiguration;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartDatabaseAsync(Action<IServiceCollection> install)
    {
        var project = Project;
        var host = await _fixture.StartAsync(project.Schema, project.Descriptor, configure: install);

        return host.Services;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private readonly PostgreSqlAlvoDataFixture _fixture = new();
}
