using MMLib.Alvo.Testing;

namespace MMLib.Alvo.Tests;

/// <summary>
/// No test or eval world clears every SQLite connection pool in the process; each clears only the pool of the file it
/// made, by a connection built from the very string its host was given.
/// </summary>
/// <remarks>
/// <c>SqliteConnection.ClearAllPools()</c> marks every pooled connection do-not-pool and reclaims those that read as
/// leaked — and a connection another fact is <em>opening</em> (popped from its pool, not yet bound to its owner) reads
/// as leaked in that window, so the clear disposes its <c>sqlite3</c> handle under the thread about to use it. Facts run
/// in parallel, so one fact's teardown fails another's write with an intermittent <see cref="ObjectDisposedException"/>
/// in <c>SqliteConnection.Open</c>. Host.Tests learned this in commit 0a8d0391; TestHost.Tests grew seven copies of
/// the global clear afterwards, which is why this is a scan rather than a note.
/// </remarks>
public class SqlitePoolIsolationArchitectureTests
{
    private const string GlobalClear = "ClearAllPools(";
    private static readonly string[] _scannedDirectories = ["test", "eval"];

    [Fact]
    public void No_test_clears_every_sqlite_pool_in_the_process()
    {
        var root = RepositoryRoot.Find();

        var offenders = _scannedDirectories
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .Where(file => ShippedSources.CodeOf(file).Contains(GlobalClear, StringComparison.Ordinal))
            .Where(file => Path.GetFileName(file) != $"{nameof(SqlitePoolIsolationArchitectureTests)}.cs")
            .Select(file => Path.GetRelativePath(root, file))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("clear only the pool of the fact's own file: SqliteConnection.ClearPool(new SqliteConnection(itsConnectionString))");
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
