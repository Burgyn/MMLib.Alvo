using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests.Integration;

/// <summary>
/// The 10 000-event chaos criterion over a real SQLite database file. Every assertion is inherited unchanged:
/// this class supplies a started backend and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the leg that gates a merge.</b> It needs no Docker, so it runs on both CI runners, while the
/// PostgreSQL leg proves the same number on the other engine and self-skips where Docker runs Windows containers.
/// </para>
/// <para>
/// <b>It runs in ring2, not ring0, and the project name is what puts it there.</b> Measured: the SQLite dispatch
/// costs ~27 s — ~11 000 autocommit writes, one durable commit each — which took its old home
/// <c>MMLib.Alvo.Data.Sqlite.Tests</c> from ~7 s to ~34 s and made it ring0's critical path, in the gate
/// <c>CLAUDE.md</c> describes as "after every small step" and Husky runs on every <c>pre-commit</c>. Its tier is
/// the one <c>PagingPerformanceTests</c> already occupies: before opening a PR. <c>scripts/test-ring0</c> excludes
/// this project by its <c>.Tests.Integration</c> suffix and <c>scripts/test-ring2</c> picks it up by the same
/// suffix, so the move needed no script change — and a rename could only ever put the criterion <em>back</em> in
/// ring0, never stop it running.
/// </para>
/// <para>
/// The collection is kept even though this is now the only dispatching suite in its assembly: the event counters
/// are process-wide statics, and the next criterion added here would otherwise sum into this one's totals.
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
