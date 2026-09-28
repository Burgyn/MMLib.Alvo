using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// PostgreSQL's <see cref="IFieldSqlRenderer"/>. The three two-valued members come from the port's
/// default interface members, whose defaults already carry the <c>COALESCE(…, FALSE)</c> shape
/// PostgreSQL accepts in boolean position — a dialect only overrides them when it has no boolean type
/// (T-SQL).
/// </summary>
public sealed class PostgreSqlFieldSqlRenderer : IFieldSqlRenderer
{
    /// <inheritdoc/>
    public string TrueLiteral => "TRUE";

    /// <inheritdoc/>
    public string FalseLiteral => "FALSE";

    /// <inheritdoc/>
    public string RenderField(EntitySchema entity, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(fieldName);
    }

    /// <inheritdoc/>
    public string RenderParameter(string parameterName) => "@" + parameterName;

    /// <inheritdoc/>
    public string RenderCaseInsensitiveLike(string left, string right) => $"{left} ILIKE {right}";

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>An escape string, <c>E'…'</c>, rather than the standard literal — a deliberate deviation, for the one
    /// setting that changes how a plain literal reads.</b> With <c>standard_conforming_strings = on</c> (the default
    /// since 9.1) a backslash inside <c>'…'</c> is an ordinary character; with it <c>off</c> it escapes the next one,
    /// so the standard spelling of <c>\'); DROP TABLE t; --</c> — <c>'\''); DROP TABLE t; --'</c> — ends the literal
    /// at the second quote and runs the rest as DDL. An escape string reads the same under either setting
    /// (PostgreSQL docs §4.1.2.2), so doubling every backslash, and then every quote through
    /// <see cref="AlvoSqlStringLiteral"/>, is correct whatever the server is configured with rather than correct under
    /// an assumption about it.
    /// </para>
    /// <para>
    /// Only those two escapes are ever written; every other character, Unicode included, stands for itself. The
    /// catalogue stores the parsed expression, not this text, so nothing downstream sees the <c>E</c>.
    /// </para>
    /// </remarks>
    string? IFieldSqlRenderer.RenderStringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AlvoSqlStringLiteral.TryQuote(value.Replace("\\", "\\\\", StringComparison.Ordinal), out var literal)
            ? "E" + literal
            : null;
    }
}
