using Microsoft.Data.Sqlite;
using MMLib.Alvo.DocsGen.Host;

namespace MMLib.Alvo.DocsGen.Tests.Host;

public sealed class HostBootTests : IDisposable
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("alvo-docsgen-test-");
    private readonly DirectoryInfo _other = Directory.CreateTempSubdirectory("alvo-docsgen-other-");

    [Fact]
    public void Deleting_a_work_directory_leaves_another_databases_pool_alone()
    {
        var other = $"Data Source={Path.Combine(_other.FullName, "other.db")}";
        var pooled = HandleOfAPooledConnection(other);

        HostBoot.DeleteWorkDirectory(_work);

        HandleOfAPooledConnection(other).ShouldBeSameAs(pooled);
    }

    [Fact]
    public void A_work_directory_a_transient_holder_releases_is_deleted_on_a_later_attempt()
    {
        var attempts = 0;

        HostBoot.DeleteWorkDirectory(_work, directory =>
        {
            if (++attempts < 3)
            {
                throw new IOException("The process cannot access the file 'alvo.db' because it is being used by another process.");
            }

            directory.Delete(recursive: true);
        }, TimeSpan.Zero);

        attempts.ShouldBe(3);
        Directory.Exists(_work.FullName).ShouldBeFalse();
    }

    [Fact]
    public void A_work_directory_that_stays_held_is_refused_after_the_bounded_attempts()
    {
        var attempts = 0;

        Should.Throw<IOException>(() => HostBoot.DeleteWorkDirectory(
            _work, _ => throw new IOException($"held {++attempts}"), TimeSpan.Zero));

        attempts.ShouldBe(HostBoot.DeleteAttempts);
    }

    public void Dispose()
    {
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_other.FullName, "other.db")}"))
        {
            SqliteConnection.ClearPool(connection);
        }
        foreach (var directory in new[] { _work, _other }.Where(directory => Directory.Exists(directory.FullName)))
        {
            directory.Delete(recursive: true);
        }
    }

    private static SQLitePCL.sqlite3 HandleOfAPooledConnection(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection.Handle!;
    }
}
