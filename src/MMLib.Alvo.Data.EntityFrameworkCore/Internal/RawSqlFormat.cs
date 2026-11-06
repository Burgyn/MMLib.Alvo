namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Makes text safe to splice into a statement EF formats with its arguments (<c>ExecuteSqlRawAsync(sql, args)</c>): that
/// call runs the statement through <see cref="string.Format(IFormatProvider?, string, object?[])"/>, so a brace in the text
/// would be read as a placeholder (HX-11 Ruling 31).
/// </summary>
/// <remarks>
/// For text the descriptor wrote — a rollup filter's inline constants — never a request's: a value a caller sends is bound,
/// not spliced. Doubling is the format grammar's own escape, so the engine receives the text exactly as rendered.
/// </remarks>
internal static class RawSqlFormat
{
    /// <summary>Doubles every <c>{</c> and <c>}</c> in <paramref name="sql"/>, and changes nothing else.</summary>
    /// <param name="sql">Rendered SQL that carries no placeholder of its own.</param>
    internal static string Escape(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return sql.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
    }
}
