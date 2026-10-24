using Microsoft.Data.Sqlite;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>One <see cref="SerialRelationalWriteGate"/> per SQLite database in this process, found by the database it names.</summary>
/// <remarks>
/// <para>
/// <b>Process-wide, not per container</b>, because SQLite's write lock is the file's: two hosts in one process over one
/// file — two test hosts, a host and a tool — are two writers on the same lock and must queue in the same line.
/// </para>
/// <para>
/// <b>The key is the database, not the connection string's spelling.</b> A file database is keyed by its full path, so
/// a relative and an absolute spelling, or two connection strings that differ only in their other keywords, share one gate.
/// The path compares without case where the platform's default file system does (Windows, macOS). An in-memory database
/// is keyed by its name: a named shared-cache database is one database to every connection that names it. A private
/// <c>:memory:</c> database, a new one per connection, cannot be told apart from the next: they all share one key, which
/// costs queueing they did not need and never misses one they did.
/// </para>
/// <para>
/// The gates are never removed. One per database a process opens is the whole cost, and a gate removed while a store
/// still held it would let the next store start a second line.
/// </para>
/// </remarks>
internal static class SqliteWriteGates
{
    private static readonly ConcurrentDictionary<string, SerialRelationalWriteGate> _gates =
        new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The gate for the database <paramref name="connectionString"/> names: its shared write line, waited at for no
    /// longer than the connection string's <c>Default Timeout</c> (none when it is zero, as for the engine's own wait).
    /// </summary>
    internal static SqliteWriteGate For(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var line = _gates.GetOrAdd(DatabaseKey(builder), static _ => new SerialRelationalWriteGate());

        return new SqliteWriteGate(line, builder.DefaultTimeout > 0 ? TimeSpan.FromSeconds(builder.DefaultTimeout) : Timeout.InfiniteTimeSpan);
    }

    /// <summary>The key a database is found by: its full path, or its name when it lives in memory.</summary>
    internal static string DatabaseKey(string connectionString) => DatabaseKey(new SqliteConnectionStringBuilder(connectionString));

    private static string DatabaseKey(SqliteConnectionStringBuilder builder)
    {
        var source = builder.DataSource;

        return builder.Mode is SqliteOpenMode.Memory || string.IsNullOrEmpty(source) || source == ":memory:"
            ? "memory:" + source
            : "file:" + Path.GetFullPath(source);
    }
}
