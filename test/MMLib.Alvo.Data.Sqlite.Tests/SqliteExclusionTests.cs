using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the exclusion suite: two <c>AFTER</c> triggers over one overlap join, replayed after every table rebuild.
/// </summary>
public sealed class SqliteExclusionTests : AlvoDataExclusionTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private AlvoDataHost? _host;

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

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
