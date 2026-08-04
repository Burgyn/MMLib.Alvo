using Microsoft.Data.Sqlite;

using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Runs the whole <see cref="OutboxStoreContractTests"/> suite against a real SQLite database file.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than <c>Mode=Memory</c>, matching <see cref="SqliteOutboxTableTests"/>: the claim's
/// predicates compare stored <c>TEXT</c>, and a shared-cache in-memory database is the one SQLite
/// configuration a production host never runs.
/// </para>
/// <para>
/// <b>The connection string is the shipped one, deliberately.</b> No <c>journal_mode</c>, no
/// <c>busy_timeout</c> and no <c>Default Timeout</c>: spike Q5 measured that the shipped registration already
/// works because <c>Microsoft.Data.Sqlite</c>'s own <c>DefaultTimeout</c> is 30 s and its retry loop covers
/// <c>BEGIN</c>. A test that added a pragma here would be measuring a configuration nothing deploys.
/// </para>
/// </remarks>
public sealed class SqliteOutboxStoreTests : OutboxStoreContractTests, IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-outbox-store-tests-{Guid.NewGuid():N}.db");

    protected override async Task<IOutboxStoreWorld> WorldAsync() =>
        await OutboxStoreWorld.StartAsync(CreateConnection);

    /// <summary>
    /// Pooling is off so that disposing a connection really releases the OS file handle, which is what lets
    /// <see cref="Dispose"/> delete the file instead of leaving one per test behind.
    /// </summary>
    private SqliteConnection CreateConnection() =>
        new($"Data Source={_databasePath};Pooling=False");

    public void Dispose()
    {
        // Best-effort, for the same reason SqliteOutboxTableTests deletes best-effort: this is a temp file
        // either way, so a stray lock must not fail the test.
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
