using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MMLib.Alvo;
using MMLib.Alvo.Data.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Conventions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Scaffolding.Internal;

namespace MMLib.Alvo.Data.PostgreSql.Internal;

/// <summary>
/// Resolves the PostgreSQL-flavored EF Core services <see cref="EfCoreSchemaMigrator"/> and
/// <see cref="EfCoreSchemaIntrospector"/> need, without depending on
/// <c>Microsoft.EntityFrameworkCore.Design</c>.
/// </summary>
/// <remarks>
/// Both factory methods spin up a throwaway <see cref="DbContext"/> configured with
/// <c>UseNpgsql</c> purely to reach its internal service provider via <c>DbContext.GetService</c>.
/// The context is disposed immediately after — the resolved services (differ, SQL generator,
/// model runtime initializer, scaffolding logger) are already-built object graphs that don't
/// reach back into the disposed provider — so nothing here leaks a second, host-visible container:
/// the throwaway provider lives and dies inside this one call.
///
/// <para>
/// This relies on a version-pinned assumption about EF Core 10 / Npgsql.EntityFrameworkCore.PostgreSQL
/// 10: <c>IMigrationsModelDiffer</c> and <c>IMigrationsSqlGenerator</c> are registered Scoped by the
/// Npgsql provider, but the code paths actually exercised against them (<c>GetDifferences</c>/
/// <c>Generate</c>) hold or dereference nothing from the now-disposed scope — they only close over
/// already-injected, non-disposable dependencies. A future version could change that and would
/// surface as an <see cref="ObjectDisposedException"/> from those calls, not from this method.
/// </para>
/// </remarks>
internal static class PostgreSqlMigrationServices
{
    public static EfCoreSchemaMigrator CreateMigrator(string connectionString)
    {
        using var context = new DbContext(BuildOptions(connectionString));

        return new EfCoreSchemaMigrator(
            context.GetService<IMigrationsModelDiffer>(),
            context.GetService<IMigrationsSqlGenerator>(),
            context.GetService<IModelRuntimeInitializer>(),
            () => new ModelBuilder(NpgsqlConventionSetBuilder.Build()),
            new NpgsqlConnection(connectionString));
    }

    public static EfCoreSchemaIntrospector CreateIntrospector(string connectionString, string schemaPrefix)
    {
        using var context = new DbContext(BuildOptions(connectionString));

        // NpgsqlDatabaseModelFactory is [EntityFrameworkInternal]: it is the concrete runtime
        // scaffolding factory EF Core itself constructs internally, but the ordinary
        // Npgsql.EntityFrameworkCore.PostgreSQL package never registers it as a service (that only
        // happens via the design-time host in Microsoft.EntityFrameworkCore.Design, which this
        // package deliberately does not reference). Constructing it directly from its single
        // runtime-registered dependency avoids that dependency entirely. Unlike SQLite's factory,
        // Npgsql's takes only the scaffolding logger — no type mapping source.
#pragma warning disable EF1001 // NpgsqlDatabaseModelFactory is EF-internal by design; see remarks above.
        var databaseModelFactory = new NpgsqlDatabaseModelFactory(
            context.GetService<IDiagnosticsLogger<DbLoggerCategory.Scaffolding>>());
#pragma warning restore EF1001

        return new EfCoreSchemaIntrospector(
            databaseModelFactory,
            new NpgsqlConnection(connectionString),
            SystemSchemaInitializer.AppliedSchemaTableName(schemaPrefix));
    }

    public static AppliedSchemaStore CreateAppliedSchemaStore(string connectionString, AlvoOptions options) =>
        new(new NpgsqlConnection(connectionString), options);

    private static DbContextOptions BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder().UseNpgsql(connectionString).Options;
}
