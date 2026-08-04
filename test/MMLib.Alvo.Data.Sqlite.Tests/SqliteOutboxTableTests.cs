using Microsoft.Data.Sqlite;
using MMLib.Alvo.Tests.Data;

using System.Data.Common;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Runs the whole <see cref="OutboxTableFacts"/> suite against a real SQLite database file — the engine whose
/// dynamic typing is the reason the stored id's ordering is measured rather than assumed.
/// </summary>
/// <remarks>
/// A file rather than <c>Mode=Memory</c>, matching <see cref="SqliteDescriptorVersionStoreTests"/>: the facts
/// are about what the engine stores and how it sorts it, and a shared-cache in-memory database is the one
/// SQLite configuration a production host never runs.
/// </remarks>
public sealed class SqliteOutboxTableTests : OutboxTableFacts, IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-outbox-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Pooling is off so that disposing the world really releases the OS file handle, which is what lets
    /// <see cref="Dispose"/> delete the file instead of leaving one per test behind.
    /// </summary>
    protected override DbConnection CreateConnection() =>
        new SqliteConnection($"Data Source={_databasePath};Pooling=False");

    public void Dispose()
    {
        // Best-effort, for the same reason SqliteDescriptorVersionStoreTests deletes best-effort: this is a
        // temp file either way, so a stray lock (an antivirus scan on Windows, say) must not fail the test.
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
