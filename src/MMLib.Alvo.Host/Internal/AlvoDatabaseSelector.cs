using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data.PostgreSql;

namespace MMLib.Alvo.Host.Internal;

/// <summary>Registers exactly one database driver, named by configuration.</summary>
internal static class AlvoDatabaseSelector
{
    /// <summary>Registers the driver <paramref name="database"/> names, or refuses the name.</summary>
    /// <remarks>
    /// <para>
    /// A missing <paramref name="connectionString"/> is defaulted only for SQLite. PostgreSQL is handed the
    /// null through, because <see cref="AlvoHostOptionsValidation"/> refuses that case at startup — before the
    /// driver's own lazy refusal, which fires only once the boot resolves a store.
    /// </para>
    /// <para>
    /// <b>The refusal is raised here rather than left to that validation, and its wording comes from the same
    /// place.</b> The driver has to be registered while the container is still being built, so an unknown name
    /// cannot wait for a validator that only runs on the built container: registering nothing would surface as a
    /// missing-service failure naming an internal type instead.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Alvo builder being configured.</param>
    /// <param name="database">The host's database options.</param>
    /// <param name="connectionString">The resolved <c>ConnectionStrings:Alvo</c> entry, if there is one.</param>
    /// <exception cref="OptionsValidationException"><paramref name="database"/> names no driver this host ships.</exception>
    internal static void Select(IAlvoBuilder builder, AlvoHostDatabaseOptions database, string? connectionString)
    {
        if (AlvoHostConfiguration.Is(database.Provider, AlvoHostDatabaseOptions.Sqlite))
        {
            builder.UseSqlite(connectionString ?? database.SqliteConnectionString);
            return;
        }

        if (AlvoHostConfiguration.Is(database.Provider, AlvoHostDatabaseOptions.PostgreSql))
        {
            builder.UsePostgreSql(options => options.ConnectionString = connectionString);
            return;
        }

        throw AlvoHostConfiguration.Refuse(AlvoHostConfiguration.UnknownProvider(database.Provider));
    }
}
