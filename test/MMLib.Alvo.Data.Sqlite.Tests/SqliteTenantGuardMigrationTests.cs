using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Shouldly;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>The tenant guard (#161) on SQLite: <c>onDelete: setNull</c> is kept as a single-column key beside the guard.</summary>
public sealed class SqliteTenantGuardMigrationTests : TenantGuardMigrationFacts, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-sqlite-guard-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public SqliteTenantGuardMigrationTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        builder.Services.AddAlvo();
        _services = builder.Services.BuildServiceProvider();
    }

    protected override IServiceProvider Services => _services;

    /// <summary>
    /// A guard added over rows that break it is refused at apply by the framing's <c>foreign_key_check</c>, once per row and
    /// at the reference's pointer — never a second time at <c>tenant_id</c>, a field no descriptor declares, although the
    /// guard's key has a row in <c>pragma_foreign_key_list</c> for each of its two columns.
    /// </summary>
    [Fact]
    public async Task A_guard_added_over_rows_that_break_it_is_refused_once_at_the_reference()
    {
        var unguarded = TenantGuardSchemas.Flipped("work_orders");
        await MigrateAsync(new SchemaModel([]), unguarded);
        var workOrder = Guid.NewGuid();
        (await RawAsync($"""INSERT INTO "work_orders" ("id", "title") VALUES ('{workOrder}', 'WO')""")).ShouldBeNull();
        (await RawAsync($"""INSERT INTO "visits" ("id", "tenant_id", "work_order_id") VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', '{workOrder}')"""))
            .ShouldBeNull();

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => MigrateAsync(unguarded, TenantGuardSchemas.Pair(OnDelete.Restrict)));

        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe("/entities/visits/fields/work_order_id");
    }

    public void Dispose()
    {
        _services.Dispose();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
