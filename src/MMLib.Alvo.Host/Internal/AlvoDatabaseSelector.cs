using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.PostgreSql;

namespace MMLib.Alvo.Host.Internal;

/// <summary>Registers exactly one database driver, named by configuration.</summary>
internal static class AlvoDatabaseSelector
{
    /// <summary>Registers the driver <paramref name="database"/> names, or refuses the name.</summary>
    /// <remarks>
    /// A missing <paramref name="connectionString"/> is defaulted only for SQLite. PostgreSQL is handed the
    /// null through, so the driver's own crafted refusal fires — a PostgreSQL host that quietly wrote to a
    /// container-local file would lose every row with the container.
    /// </remarks>
    /// <param name="builder">The Alvo builder being configured.</param>
    /// <param name="database">The host's database options.</param>
    /// <param name="connectionString">The resolved <c>ConnectionStrings:Alvo</c> entry, if there is one.</param>
    /// <exception cref="InvalidOperationException"><paramref name="database"/> names no driver this host ships.</exception>
    internal static void Select(IAlvoBuilder builder, AlvoHostDatabaseOptions database, string? connectionString)
    {
        if (Is(database.Provider, AlvoHostDatabaseOptions.Sqlite))
        {
            builder.UseSqlite(connectionString ?? database.SqliteConnectionString);
            return;
        }

        if (Is(database.Provider, AlvoHostDatabaseOptions.PostgreSql))
        {
            builder.UsePostgreSql(options => options.ConnectionString = connectionString);
            return;
        }

        throw new InvalidOperationException(UnknownProviderMessage(database.Provider));
    }

    private static bool Is(string configured, string known) =>
        string.Equals(configured, known, StringComparison.OrdinalIgnoreCase);

    private static string UnknownProviderMessage(string configured) =>
        $"'{configured}' is not a database provider this host can register. Set Alvo:Database:Provider "
        + $"(env Alvo__Database__Provider) to '{AlvoHostDatabaseOptions.Sqlite}' or "
        + $"'{AlvoHostDatabaseOptions.PostgreSql}'.";
}
