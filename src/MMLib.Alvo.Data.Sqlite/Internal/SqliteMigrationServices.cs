using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Sqlite.Scaffolding.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using MMLib.Alvo;
using MMLib.Alvo.Data.EntityFrameworkCore;

namespace MMLib.Alvo.Data.Sqlite.Internal;

/// <summary>
/// Resolves the SQLite-flavored EF Core services <see cref="EfCoreSchemaMigrator"/> and
/// <see cref="EfCoreSchemaIntrospector"/> need, without depending on
/// <c>Microsoft.EntityFrameworkCore.Design</c>.
/// </summary>
/// <remarks>
/// Both factory methods spin up a throwaway <see cref="DbContext"/> configured with
/// <c>UseSqlite</c> purely to reach its internal service provider via <c>DbContext.GetService</c>.
/// The context is disposed immediately after — the resolved services (differ, SQL generator,
/// model runtime initializer, type mapping source, scaffolding logger) are already-built object
/// graphs that don't reach back into the disposed provider — so nothing here leaks a second,
/// host-visible container: the throwaway provider lives and dies inside this one call.
///
/// <para>
/// This relies on a version-pinned assumption about EF Core 10: <c>IMigrationsModelDiffer</c>
/// and <c>IMigrationsSqlGenerator</c> are registered Scoped by the SQLite provider, but the code
/// paths actually exercised against them (<c>GetDifferences</c>/<c>Generate</c>) hold or
/// dereference nothing from the now-disposed scope — they only close over already-injected,
/// non-disposable dependencies. A future EF Core version could change that and would surface as
/// an <see cref="ObjectDisposedException"/> from those calls, not from this method.
/// </para>
/// </remarks>
internal static class SqliteMigrationServices
{
    public static EfCoreSchemaMigrator CreateMigrator(string connectionString)
    {
        using var context = new DbContext(BuildOptions(connectionString));

        return new EfCoreSchemaMigrator(
            context.GetService<IMigrationsModelDiffer>(),
            context.GetService<IMigrationsSqlGenerator>(),
            context.GetService<IModelRuntimeInitializer>(),
            () => new ModelBuilder(SqliteConventionSetBuilder.Build()),
            new SqliteConnection(WithoutPooling(connectionString)));
    }

    public static EfCoreSchemaIntrospector CreateIntrospector(string connectionString, string schemaPrefix)
    {
        using var context = new DbContext(BuildOptions(connectionString));

        // SqliteDatabaseModelFactory is [EntityFrameworkInternal]: it is the concrete runtime
        // scaffolding factory EF Core itself constructs internally, but the ordinary
        // Microsoft.EntityFrameworkCore.Sqlite package never registers it as a service (that only
        // happens via the design-time host in Microsoft.EntityFrameworkCore.Design, which this
        // package deliberately does not reference). Constructing it directly from its two
        // runtime-registered dependencies avoids that dependency entirely.
#pragma warning disable EF1001 // SqliteDatabaseModelFactory is EF-internal by design; see remarks above.
        var databaseModelFactory = new SqliteDatabaseModelFactory(
            context.GetService<IDiagnosticsLogger<DbLoggerCategory.Scaffolding>>(),
            context.GetService<IRelationalTypeMappingSource>());
#pragma warning restore EF1001

        return new EfCoreSchemaIntrospector(
            databaseModelFactory,
            new SqliteConnection(WithoutPooling(connectionString)),
            SystemSchemaInitializer.AppliedSchemaTableName(schemaPrefix));
    }

    public static AppliedSchemaStore CreateAppliedSchemaStore(string connectionString, AlvoOptions options) =>
        new(new SqliteConnection(connectionString), options);

    private static DbContextOptions BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder().UseSqlite(connectionString).Options;

    // The migrator and introspector each own one long-lived connection for the container's
    // lifetime and dispose it deterministically (see EfCoreSchemaMigrator.Dispose /
    // EfCoreSchemaIntrospector.Dispose). Microsoft.Data.Sqlite pools connections by default, which
    // keeps the underlying file handle alive even after Dispose() — on Windows that leaves the
    // .db file locked (delete fails with IOException) until the pool itself is cleared. Disabling
    // pooling for these two administrative connections releases the file handle on Dispose(),
    // which is what a caller (e.g. a test deleting the file right after) needs. This is not a
    // hot path, so the per-connection setup cost pooling would normally amortize is irrelevant
    // here.
    private static string WithoutPooling(string connectionString) =>
        new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString();
}
