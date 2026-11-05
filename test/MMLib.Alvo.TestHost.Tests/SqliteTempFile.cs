using Microsoft.Data.Sqlite;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>A fact's temp SQLite file: the one connection string its host is given, and its teardown.</summary>
/// <remarks>
/// <b>Only this file's pool, never <c>SqliteConnection.ClearAllPools()</c>.</b> Pools are keyed by the exact connection
/// string, so a connection built from <see cref="ConnectionStringFor"/> reaches this file's pool and no other fact's.
/// Clearing every pool in the process is not safe while facts run in parallel: a connection another fact is
/// <em>opening</em> — popped from its pool, not yet bound to its owner — reads as leaked in that window, and the clear
/// disposes its <c>sqlite3</c> handle under the thread about to use it (the intermittent
/// <see cref="ObjectDisposedException"/> in <c>SqliteConnection.Open</c>; Host.Tests' <c>AlvoHostWorld</c> learned the same).
/// </remarks>
internal static class SqliteTempFile
{
    internal static string ConnectionStringFor(string path) => $"Data Source={path}";

    /// <summary>Clears the file's own pool, so no pooled handle keeps it open, then deletes it.</summary>
    internal static void Delete(string path)
    {
        using (var connection = new SqliteConnection(ConnectionStringFor(path)))
        {
            SqliteConnection.ClearPool(connection);
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
