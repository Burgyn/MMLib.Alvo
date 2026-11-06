using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>One fresh database per fact on the shared container, each dropped when the test class is disposed.</summary>
/// <param name="postgres">The container.</param>
/// <param name="prefix">The databases' name prefix.</param>
internal sealed class PostgresFactDatabases(PostgresFixture postgres, string prefix) : IAsyncDisposable
{
    private readonly List<string> _names = [];

    /// <summary>Creates a database and answers its connection string; skips the fact where Docker is unavailable.</summary>
    internal async Task<string> CreateAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(postgres.ConnectionString), "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\"");
        _names.Add(name);

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _names)
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
