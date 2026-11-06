using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>The read-extension facts on SQLite — a temp file per fact, its name the marker the SQL capture matches.</summary>
public sealed class SqliteReadExtensionTests : ReadExtensionFacts, IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            SqliteTempFile.Delete(file);
        }

        GC.SuppressFinalize(this);
    }

    protected override Task<ReadsDatabase> DatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-reads-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var connectionString = SqliteTempFile.ConnectionStringFor(path);

        return Task.FromResult(new ReadsDatabase(
            alvo => alvo.UseSqlite(connectionString), Path.GetFileName(path), table => ColumnsAsync(connectionString, table)));
    }

    private static async Task<IReadOnlyList<string>> ColumnsAsync(string connectionString, string table)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);

        return await NamesAsync(command);
    }
}
