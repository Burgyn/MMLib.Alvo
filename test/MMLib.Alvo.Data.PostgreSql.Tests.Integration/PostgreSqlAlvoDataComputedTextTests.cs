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
/// PostgreSQL's leg of the computed-text suite — the engine that reads this dialect's escape strings and adds a stored
/// generated column in place, backfilling it.
/// </summary>
public sealed class PostgreSqlAlvoDataComputedTextTests : AlvoDataComputedTextTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();
    private PostgreSqlAlvoDataHost? _host;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Data;
    }

    /// <inheritdoc/>
    protected override Task<Exception?> ExecuteOutOfBandAsync(string sql) =>
        OutOfBandStatement.ExecuteAsync(
            _host!.Services.GetRequiredService<AlvoDataContextFactory>(), sql, TestContext.Current.CancellationToken);

    /// <inheritdoc/>
    protected override async Task<MigrationResult> MigrateAsync(SchemaModel current, SchemaModel desired)
    {
        var migrator = _host!.Services.GetRequiredService<ISchemaMigrator>();
        var options = new MigrationOptions();
        var plan = await migrator.PlanAsync(current, desired, options, TestContext.Current.CancellationToken);
        var result = await migrator.ApplyAsync(plan, options, TestContext.Current.CancellationToken);
        _host.RePrime(desired);

        return result;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
