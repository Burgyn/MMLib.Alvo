using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the exclusion suite — one <c>EXCLUDE USING gist</c> constraint over <c>btree_gist</c>, and the engine
/// whose writers really run concurrently.
/// </summary>
/// <remarks>Its own container with headroom, so twenty concurrent writers neither queue on nor exhaust the pool.</remarks>
public sealed class PostgreSqlExclusionTests : AlvoDataExclusionTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new() { MaxConnections = 200, MaxPoolSize = 150 };
    private PostgreSqlAlvoDataHost? _host;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override ISchemaMigrator Migrator => _host!.Services.GetRequiredService<ISchemaMigrator>();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Data;
    }

    protected override void RePrime(SchemaModel schema, AlvoDescriptor descriptor) => _host!.RePrime(schema, descriptor);

    protected override Task<Exception?> ExecuteOutOfBandAsync(string sql) =>
        OutOfBandStatement.ExecuteAsync(
            _host!.Services.GetRequiredService<AlvoDataContextFactory>(), sql, TestContext.Current.CancellationToken);

    protected override Task<IReadOnlyList<string>> ExclusionNamesAsync(string table) =>
        CatalogQuery.NamesAsync(
            _host!.Services,
            "SELECT conname::text FROM pg_constraint WHERE contype = 'x' AND conrelid = (SELECT oid FROM pg_class WHERE relname = @table)",
            table);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
