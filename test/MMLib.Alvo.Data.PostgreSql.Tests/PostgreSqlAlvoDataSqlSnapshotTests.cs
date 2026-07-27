using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.PostgreSql.Tests;

/// <summary>
/// PostgreSQL's leg of the golden CEL→SQL table. Until this existed the table was frozen on SQLite alone, so
/// a row like <c>price &gt; 100</c> — where the two dialects genuinely differ, because only SQLite needs the
/// value repair — recorded one engine's answer and nothing to compare it against.
/// </summary>
public sealed class PostgreSqlAlvoDataSqlSnapshotTests : AlvoDataSqlSnapshotTests, IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    protected override string EngineName => "postgresql";

    protected override ICelCompiler Compiler => _services.GetRequiredService<ICelCompiler>();

    protected override IPredicateRenderer Renderer => _services.GetRequiredService<IPredicateRenderer>();

    protected override IFieldSqlRenderer Fields { get; } = new PostgreSqlFieldSqlRenderer();

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }
}
