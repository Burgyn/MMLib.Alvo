using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The tenant guard (#161) on PostgreSQL: a scoped reference spans <c>(tenant_id, id)</c>, and <c>onDelete: setNull</c> must
/// clear only the reference column — PostgreSQL 15's <c>ON DELETE SET NULL (column)</c>.
/// </summary>
public sealed class PostgreSqlTenantGuardMigrationTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly ServiceProvider _services;

    public PostgreSqlTenantGuardMigrationTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            _services = new ServiceCollection().BuildServiceProvider();
            return;
        }

        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(fixture.ConnectionString);
        _services = builder.Services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISchemaMigrator Migrator => _services.GetRequiredService<ISchemaMigrator>();

    [Fact]
    public async Task A_scoped_set_null_reference_names_the_column_it_clears()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");

        var plan = await Migrator.PlanAsync(new SchemaModel([]), TenantGuardSchemas.Pair(OnDelete.SetNull), new MigrationOptions(), Ct);

        var sql = string.Join("\n", plan.Sql);
        sql.ShouldContain("ON DELETE SET NULL (work_order_id)");
        sql.ShouldNotContain("FOREIGN KEY (work_order_id) REFERENCES");
    }

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
