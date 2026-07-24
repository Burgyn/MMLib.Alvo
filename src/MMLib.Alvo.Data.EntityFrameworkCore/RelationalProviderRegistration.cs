using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Scaffolding;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The provider-specific building blocks Alvo needs to stand up an EF Core-backed database
/// provider — everything that differs between SQLite, PostgreSQL, or a hypothetical external
/// engine (e.g. Oracle). Passed to
/// <see cref="AlvoEfCoreProvider.AddRelationalProvider(IAlvoBuilder, RelationalProviderRegistration)"/>,
/// which owns the glue that is identical across every relational provider.
/// </summary>
/// <remarks>
/// This is the public authoring contract for an out-of-repo EF-based provider: a provider package
/// supplies these five callbacks and lets <see cref="AlvoEfCoreProvider"/> resolve EF Core's
/// migrations differ, SQL generator, model-runtime initializer, and scaffolding factory. Nothing
/// here reaches into Alvo internals — the callbacks compose only public EFCore.Relational and
/// provider-package abstractions, exactly as Npgsql or Pomelo build on the public EF base rather
/// than on EF's own <c>InternalsVisibleTo</c>.
/// </remarks>
public sealed class RelationalProviderRegistration
{
    /// <summary>
    /// Resolves the ADO.NET connection string from the host container — typically from the
    /// provider's own <c>IOptions&lt;T&gt;</c>, so the value can be bound from configuration and is
    /// read at provider-build time rather than captured eagerly at registration. Should throw a
    /// clear, actionable error if no connection string was configured.
    /// </summary>
    public required Func<IServiceProvider, string> ConnectionString { get; init; }

    /// <summary>
    /// Applies the provider's own <c>UseXxx(connectionString)</c> (e.g. <c>UseSqlite</c>,
    /// <c>UseNpgsql</c>) to a throwaway <see cref="DbContextOptionsBuilder"/>. Alvo builds a
    /// short-lived <see cref="DbContext"/> from the result purely to reach EF Core's provider-flavored
    /// services; it never persists the context.
    /// </summary>
    public required Action<DbContextOptionsBuilder, string> ConfigureProvider { get; init; }

    /// <summary>
    /// Creates a fresh, conventionless <see cref="ModelBuilder"/> seeded with the provider's
    /// convention set (e.g. <c>new ModelBuilder(SqliteConventionSetBuilder.Build())</c>). Alvo uses
    /// it to translate a descriptor into the runtime EF <c>IModel</c> the differ consumes.
    /// </summary>
    public required Func<ModelBuilder> CreateModelBuilder { get; init; }

    /// <summary>
    /// Builds the provider's <see cref="IDatabaseModelFactory"/> (the reverse-engineering /
    /// scaffolding factory used for introspection) from the throwaway context's internal EF service
    /// provider, passed as the argument. Different providers need different dependencies — SQLite's
    /// factory takes the scaffolding logger and the relational type-mapping source, Npgsql's takes
    /// only the logger — so each provider constructs its own factory here.
    /// </summary>
    public required Func<IServiceProvider, IDatabaseModelFactory> CreateDatabaseModelFactory { get; init; }

    /// <summary>
    /// Creates the provider's concrete ADO.NET <see cref="DbConnection"/> for a connection string
    /// (e.g. <c>new SqliteConnection(cs)</c>). Alvo's migrator, introspector, and applied-schema
    /// store each own one such connection for the container's lifetime.
    /// </summary>
    public required Func<string, DbConnection> CreateConnection { get; init; }
}
