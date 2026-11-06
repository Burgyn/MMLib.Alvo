using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;

namespace MMLib.Alvo.Tests.Data;

/// <summary>Reads a column of names out of an engine's own catalog, on a connection the data port is not mediating.</summary>
internal static class CatalogQuery
{
    /// <summary>The first column of every row <paramref name="sql"/> answers, its one parameter <c>@table</c> bound to <paramref name="table"/>.</summary>
    /// <param name="services">The host container of the database under test.</param>
    /// <param name="sql">The catalog query.</param>
    /// <param name="table">The table the query is about.</param>
    internal static async Task<IReadOnlyList<string>> NamesAsync(IServiceProvider services, string sql, string table)
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = services.GetRequiredService<AlvoDataContextFactory>().Create();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
