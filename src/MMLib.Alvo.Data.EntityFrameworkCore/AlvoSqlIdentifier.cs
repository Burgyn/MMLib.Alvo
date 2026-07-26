namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The one implementation of SQL's double-quote identifier escaping, shared by every Alvo storage
/// driver. Deliberately <b>not</b> a call to EF's <c>ISqlGenerationHelper.DelimitIdentifier</c>: the
/// Npgsql helper returns an identifier <em>unquoted</em> whenever it judges quoting unnecessary — which
/// PostgreSQL then case-folds, so the same field renders differently per driver — and the SQLite helper
/// silently discards a schema argument. A driver always quotes, because a field or entity name may have
/// been assembled programmatically by a host and is therefore untrusted (see
/// <see cref="MMLib.Alvo.Expressions.IFieldSqlRenderer.RenderField"/>'s own remarks).
/// </summary>
public static class AlvoSqlIdentifier
{
    /// <summary>Quotes <paramref name="identifier"/>, doubling every embedded double quote.</summary>
    /// <param name="identifier">The identifier to quote.</param>
    /// <exception cref="ArgumentException"><paramref name="identifier"/> is null, empty or whitespace.</exception>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
