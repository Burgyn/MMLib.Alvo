using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Reads the framework tables a write-hook fact checks, outside the port.</summary>
internal static class WriteHookRows
{
    internal static async Task<long> CountAsync(Func<DbConnection> connect, string table, string where = "1 = 1")
    {
        await using var connection = connect();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }
}
