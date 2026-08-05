using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the computed/rollup suite — the engine that refuses to add a stored generated column to a
/// populated table and that needs no row lock at all.
/// </summary>
/// <remarks>
/// It supplies a store and an out-of-band connection, and nothing else, so no fact can be weakened to make this
/// driver pass.
/// </remarks>
public sealed class SqliteAlvoDataComputedRollupTests : AlvoDataComputedRollupTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private AlvoDataHost? _host;

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Data;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A connection of its own, out of the same container the port uses, so the statement genuinely bypasses
    /// <c>IAlvoData</c> instead of going through it under another name.
    /// </remarks>
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
