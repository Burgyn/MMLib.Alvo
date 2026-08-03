using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The 10 000-event chaos criterion over a real SQLite database file. Every assertion is inherited unchanged:
/// this class supplies a started backend and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the leg that gates a merge.</b> It runs in ring0 — no Docker — so the criterion is measured on
/// every run of the fast suite rather than only where a container is reachable; the PostgreSQL leg proves the same
/// number on the other engine and self-skips where Docker runs Windows containers.
/// </para>
/// <para>
/// It belongs to <see cref="DispatchedEventCollection"/> together with
/// <see cref="SqliteAlvoEventCriteriaTests"/>, because both assert process-wide event counters by value.
/// </para>
/// </remarks>
[Collection(DispatchedEventCollection.Name)]
public sealed class SqliteOutboxChaosTests : OutboxChaosCriteriaTests, IAsyncDisposable
{
    /// <inheritdoc/>
    protected override string EngineDescription => "sqlite (file), " + BuildConfiguration;

    /// <inheritdoc/>
    protected override async Task<IServiceProvider> StartDatabaseAsync(Action<IServiceCollection> install)
    {
        var project = Project;
        var host = await _fixture.StartAsync(project.Schema, project.Descriptor, configure: install);

        return host.Services;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private readonly SqliteAlvoDataFixture _fixture = new();
}
