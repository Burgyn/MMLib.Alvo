using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the differential matrix: the same rule/row/caller table PR1 replays against an in-process
/// three-valued evaluator, re-answered by a real SQLite database.
/// </summary>
public sealed class SqliteAlvoDataDifferentialTests : AlvoDataDifferentialTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private readonly ServiceProvider _core = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    protected override ICelCompiler Compiler => _core.GetRequiredService<ICelCompiler>();

    protected override IPredicateRenderer Renderer => _core.GetRequiredService<IPredicateRenderer>();

    protected override IPredicateEvaluator Evaluator => _core.GetRequiredService<IPredicateEvaluator>();

    protected override IFieldSqlRenderer Fields { get; } = new SqliteFieldSqlRenderer();

    protected override async Task<IDifferentialProbe> CreateProbeAsync(EntitySchema entity)
    {
        var host = await _fixture.StartAsync(new SchemaModel([entity]));
        return new DifferentialProbe(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), entity, new SqliteSqlDialect());
    }

    public async ValueTask DisposeAsync()
    {
        await _core.DisposeAsync();
        await _fixture.DisposeAsync();
    }
}
