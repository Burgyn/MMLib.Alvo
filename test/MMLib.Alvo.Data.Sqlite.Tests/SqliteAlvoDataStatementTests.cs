using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of the "predicate in the <c>WHERE</c>, never a post-filter" criterion.</summary>
public sealed class SqliteAlvoDataStatementTests : AlvoDataStatementTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IStatementProbe> CreateAsync(
        SchemaModel schema, AlvoDescriptor descriptor, IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> seed)
    {
        var host = await _fixture.StartAsync(schema, descriptor);
        await AlvoDataSeed.SeedAsync(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), seed, TestContext.Current.CancellationToken);
        return new Probe(host);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private sealed class Probe(AlvoDataHost host) : IStatementProbe
    {
        public IAlvoData Data => host.Data;

        public IReadOnlyList<string> Statements => host.Statements;

        public void ClearStatements() => host.ClearStatements();
    }
}
