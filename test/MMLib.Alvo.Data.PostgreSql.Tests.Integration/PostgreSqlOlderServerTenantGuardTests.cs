using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// #161: a guard's <c>ON DELETE SET NULL (column)</c> is PostgreSQL 15's, so on postgres:14 a <c>setNull</c> reference between
/// two scoped entities is refused at its <c>onDelete</c> pointer, naming the version that can, rather than failing in DDL; the
/// other actions still apply.
/// </summary>
public sealed class PostgreSqlOlderServerTenantGuardTests : IClassFixture<PostgreSqlOlderServerExclusionTests.Postgres14>
{
    private readonly PostgreSqlAlvoDataFixture _fixture;

    public PostgreSqlOlderServerTenantGuardTests(PostgreSqlOlderServerExclusionTests.Postgres14 server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Docker on Windows runners has no linux/amd64 postgres image, so the PostgreSQL engine cannot be started.");
        _fixture = server.Fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scoped_set_null_reference_is_refused_at_its_on_delete_on_postgresql_14()
    {
        var migrator = await MigratorAsync();

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => migrator.PlanAsync(new SchemaModel([]), TenantGuardSchemas.Pair(OnDelete.SetNull), new MigrationOptions(), Ct));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/visits/fields/work_order_id/onDelete");
        error.Message.ShouldContain("PostgreSQL 15");
    }

    [Fact]
    public async Task A_scoped_restrict_reference_still_applies_on_postgresql_14()
    {
        var migrator = await MigratorAsync();

        var plan = await migrator.PlanAsync(new SchemaModel([]), TenantGuardSchemas.Pair(OnDelete.Restrict), new MigrationOptions(), Ct);
        var applied = await migrator.ApplyAsync(plan, new MigrationOptions(), Ct);

        applied.Applied.ShouldBeTrue();
    }

    private async Task<ISchemaMigrator> MigratorAsync()
    {
        Assert.SkipUnless(_fixture.Available, "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");
        var name = $"alvo_pg14_{Guid.NewGuid():N}"[..30];
        await using (var admin = new NpgsqlConnection(_fixture.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var database = new NpgsqlConnectionStringBuilder(_fixture.AdminConnectionString) { Database = name };
        return _fixture.ServicesFor(database.ToString()).GetRequiredService<ISchemaMigrator>();
    }
}
