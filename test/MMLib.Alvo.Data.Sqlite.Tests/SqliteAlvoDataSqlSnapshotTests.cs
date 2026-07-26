using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

public sealed class SqliteAlvoDataSqlSnapshotTests : AlvoDataSqlSnapshotTests, IDisposable
{
    private readonly ServiceProvider _services = BuildServices();

    protected override string EngineName => "sqlite";

    protected override ICelCompiler Compiler => _services.GetRequiredService<ICelCompiler>();

    protected override IPredicateRenderer Renderer => _services.GetRequiredService<IPredicateRenderer>();

    protected override IFieldSqlRenderer Fields { get; } = new SqliteFieldSqlRenderer();

    private static ServiceProvider BuildServices() => new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }
}
