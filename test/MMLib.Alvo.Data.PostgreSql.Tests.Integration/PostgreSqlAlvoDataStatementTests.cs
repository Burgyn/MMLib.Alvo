using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the "predicate in the <c>WHERE</c>, never a post-filter" criterion — the leg that was
/// missing, and the reason it mattered: every outcome-level fact on this engine is equally satisfied by an
/// implementation that fetches the candidate rows and filters them in memory.
/// </summary>
public sealed class PostgreSqlAlvoDataStatementTests : AlvoDataStatementTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IStatementProbe> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var host = await _fixture.StartAsync(schema, descriptor);
        await AlvoDataSeed.SeedAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), seed, TestContext.Current.CancellationToken);
        return new Probe(host);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private sealed class Probe(PostgreSqlAlvoDataHost host) : IStatementProbe
    {
        public IAlvoData Data => host.Data;

        public IReadOnlyList<string> Statements => host.Statements;

        public void ClearStatements() => host.ClearStatements();
    }
}
