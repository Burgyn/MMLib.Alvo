using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of the sequence suite: the write gate admits one writer, so the counter row is never contended.</summary>
public sealed class SqliteSequenceTests : AlvoDataSequenceTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private AlvoDataHost? _host;

    protected override IAlvoTransactions Transactions => _host!.Services.GetRequiredService<IAlvoTransactions>();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor, TimeProvider time)
    {
        _host = await _fixture.StartAsync(schema, descriptor, time);
        return _host.Data;
    }

    protected override async Task ApplyAsync(SchemaModel current, SchemaModel desired, AlvoDescriptor descriptor)
    {
        var migrator = _host!.Services.GetRequiredService<ISchemaMigrator>();
        var ct = TestContext.Current.CancellationToken;
        var plan = await migrator.PlanAsync(current, desired, new MigrationOptions(), ct);
        (await migrator.ApplyAsync(plan, new MigrationOptions(), ct)).Applied.ShouldBeTrue();
        _host.RePrime(desired, descriptor);
    }

    /// <summary>
    /// The shipped topology — <c>UseSqlite</c>'s write gate under the provider's 30 s busy timeout — queues the hundred
    /// writers one at a time, and a Windows runner's per-write cost outlasts that timeout (the same skip as
    /// <c>SqliteDataWriteGateTests</c>, #410). Held on Linux and macOS, unweakened.
    /// </summary>
    protected override void EnsureAHundredWritersFitTheBusyTimeout() =>
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "A Windows runner's per-write cost (~1.3 s a write on CI) makes a hundred writers queued at SQLite's write gate "
            + "outlast its busy timeout; tracked by #410, held on Linux and macOS.");

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
