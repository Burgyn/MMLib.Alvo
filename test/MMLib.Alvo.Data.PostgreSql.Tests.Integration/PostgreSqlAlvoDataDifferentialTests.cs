using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the differential matrix. Its own three-valued logic is the reference SQLite's
/// <c>COALESCE</c> fold is emulating, so a divergence here means the fold is wrong rather than the emulation.
/// </summary>
public sealed class PostgreSqlAlvoDataDifferentialTests : AlvoDataDifferentialTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();
    private readonly ServiceProvider _core = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override ICelCompiler Compiler => _core.GetRequiredService<ICelCompiler>();

    protected override IPredicateRenderer Renderer => _core.GetRequiredService<IPredicateRenderer>();

    protected override IPredicateEvaluator Evaluator => _core.GetRequiredService<IPredicateEvaluator>();

    protected override IFieldSqlRenderer Fields { get; } = new PostgreSqlFieldSqlRenderer();

    protected override async Task<IDifferentialProbe> CreateProbeAsync(EntitySchema entity)
    {
        var host = await _fixture.StartAsync(new SchemaModel([entity]));
        return new DifferentialProbe(
            host.Services.GetRequiredService<AlvoDataContextFactory>(), entity, new PostgreSqlSqlDialect());
    }

    public async ValueTask DisposeAsync()
    {
        await _core.DisposeAsync();
        await _fixture.DisposeAsync();
    }
}
