using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.TestHost.Internal;

/// <summary>
/// One uniquely named <c>Mode=Memory;Cache=Shared</c> SQLite database, kept alive by one open connection for the
/// lifetime of its host.
/// </summary>
/// <remarks>
/// A bare <c>:memory:</c> gives every connection its own empty database, and Alvo's relational driver opens a fresh
/// connection per unit of work — the migration would create tables no request could see. The keep-alive connection is
/// what makes a named shared cache one database, with no file to clean up.
/// </remarks>
internal sealed class InMemorySqlite : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _keepAlive;

    private InMemorySqlite(SqliteConnection keepAlive) => _keepAlive = keepAlive;

    /// <summary>Opens a fresh database and registers it as Alvo's provider, through the extension a host calls.</summary>
    internal static InMemorySqlite Open(IAlvoBuilder alvo)
    {
        var keepAlive = new SqliteConnection($"Data Source=alvo-testhost-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        keepAlive.Open();
        alvo.UseSqlite(keepAlive.ConnectionString);

        return new InMemorySqlite(keepAlive);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _keepAlive.DisposeAsync();

    /// <inheritdoc/>
    public void Dispose() => _keepAlive.Dispose();
}
