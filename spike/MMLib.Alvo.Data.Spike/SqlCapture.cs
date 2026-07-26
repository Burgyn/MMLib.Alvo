using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Text;

namespace MMLib.Alvo.Data.Spike;

/// <summary>
/// Records the real <see cref="DbCommand"/> EF (or hand-written ADO.NET) hands to the provider —
/// command text plus the provider's own parameter collection, with names, CLR types and DbTypes.
/// This is the spike's evidence source: nothing here reasons about what SQL "should" be produced.
/// </summary>
public sealed class SqlCapture : DbCommandInterceptor
{
    private readonly List<string> _log = [];

    public IReadOnlyList<string> Log => _log;

    public void Clear() => _log.Clear();

    public void Record(DbCommand command) => _log.Add(Describe(command));

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public static string Describe(DbCommand command)
    {
        var text = new StringBuilder();
        text.AppendLine("  -- SQL ------------------------------------------------------------");
        foreach (var line in command.CommandText.Split('\n'))
        {
            text.AppendLine("  " + line.TrimEnd());
        }

        text.AppendLine("  -- parameters (provider collection) --------------------------------");
        if (command.Parameters.Count == 0)
        {
            text.AppendLine("  (none)");
        }

        foreach (DbParameter parameter in command.Parameters)
        {
            var value = parameter.Value;
            var clr = value is null or DBNull ? "null" : value.GetType().Name;
            text.AppendLine($"  {parameter.ParameterName,-18} DbType={parameter.DbType,-12} clr={clr,-16} value={Format(value)}");
        }

        return text.ToString();
    }

    private static string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => $"'{s}'",
        _ => value.ToString() ?? "?",
    };
}
