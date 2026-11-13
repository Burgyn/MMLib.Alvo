using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Npgsql;
using Shouldly;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The tenant guard (#161) on PostgreSQL: a scoped reference spans <c>(tenant_id, id)</c>, and <c>onDelete: setNull</c> must
/// clear only the reference column — PostgreSQL 15's <c>ON DELETE SET NULL (column)</c>.
/// </summary>
/// <remarks>
/// One container for the class, and a database of its own per fact: the migration facts create, alter and drop the same
/// table names, so a shared database would make each fact depend on what the previous one left behind.
/// </remarks>
public sealed class PostgreSqlTenantGuardMigrationTests(PostgresFixture fixture)
    : TenantGuardMigrationFacts, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    protected override IServiceProvider Services => _services;

    public async ValueTask InitializeAsync()
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!Runnable)
        {
            return;
        }

        var name = $"alvo_guard_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        await _services.DisposeAsync();
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ToString());
        builder.Services.AddAlvo();
        _services = builder.Services.BuildServiceProvider();
    }

    protected override bool Runnable => !OperatingSystem.IsWindows();

    [Fact]
    public async Task A_scoped_set_null_reference_names_the_column_it_clears()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");

        var plan = await Migrator.PlanAsync(new SchemaModel([]), TenantGuardSchemas.Pair(OnDelete.SetNull), new MigrationOptions(), Ct);

        var sql = string.Join("\n", plan.Sql);
        sql.ShouldContain("ON DELETE SET NULL (work_order_id)");
        sql.ShouldNotContain("FOREIGN KEY (work_order_id) REFERENCES");
    }

    [Fact]
    public async Task Restrict_and_cascade_guards_are_emitted_unchanged()
    {
        Assert.SkipUnless(Runnable, "PostgreSQL Testcontainers requires a Linux Docker daemon.");

        var plan = await Migrator.PlanAsync(new SchemaModel([]), TenantGuardSchemas.AllActions(), new MigrationOptions(), Ct);

        var sql = string.Join("\n", plan.Sql);
        sql.ShouldContain("ON DELETE CASCADE");
        sql.ShouldContain("ON DELETE RESTRICT");
        System.Text.RegularExpressions.Regex.Count(sql, "SET NULL \\(").ShouldBe(1);
    }

    /// <summary>
    /// After a rename the reference's foreign key, its index and the target's alternate key carry exactly the names the
    /// model gives them — none left behind under an old name, none missing — so every later plan names a constraint the
    /// database really holds. The primary key keeps its old name, which no plan ever drops on its own.
    /// </summary>
    /// <param name="renames"><c>entity</c>, <c>field</c> or <c>both</c>.</param>
    /// <param name="expected">The foreign key, unique constraint and <c>IX_</c> index names, sorted.</param>
    [Theory]
    [InlineData("entity", "AK_jobs_tenant_id_id,FK_visits_jobs_tenant_id_work_order_id,IX_visits_tenant_id_work_order_id")]
    [InlineData("field", "AK_work_orders_tenant_id_id,FK_visits_work_orders_tenant_id_job_id,IX_visits_tenant_id_job_id")]
    [InlineData("both", "AK_jobs_tenant_id_id,FK_visits_jobs_tenant_id_job_id,IX_visits_tenant_id_job_id")]
    public async Task After_a_rename_the_guard_holds_only_the_names_the_model_gives_it(string renames, string expected)
    {
        Assert.SkipUnless(Runnable, "PostgreSQL Testcontainers requires a Linux Docker daemon.");

        await MigrateAsync(new SchemaModel([]), TenantGuardSchemas.Pair(OnDelete.Restrict));
        await MigrateAsync(TenantGuardSchemas.Pair(OnDelete.Restrict), TenantGuardSchemas.Renamed(renames));

        (await ScalarAsync<string>(
            """
            SELECT string_agg(name, ',' ORDER BY name) FROM (
                SELECT conname::text AS name FROM pg_constraint WHERE contype IN ('f', 'u') AND connamespace = 'public'::regnamespace
                UNION ALL
                SELECT indexname::text FROM pg_indexes WHERE schemaname = 'public' AND indexname LIKE 'IX%') AS names
            """)).ShouldBe(expected);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
