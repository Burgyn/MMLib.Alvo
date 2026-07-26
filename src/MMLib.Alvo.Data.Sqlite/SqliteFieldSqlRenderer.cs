using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>
/// SQLite's <see cref="IFieldSqlRenderer"/>. The three two-valued members come from the port's default
/// interface members, whose defaults already carry the <c>COALESCE(…, 0)</c> shape SQLite accepts in
/// boolean position — a dialect only overrides them when it has no boolean type (T-SQL).
/// </summary>
public sealed class SqliteFieldSqlRenderer : IFieldSqlRenderer
{
    /// <inheritdoc/>
    public string TrueLiteral => "1";

    /// <inheritdoc/>
    public string FalseLiteral => "0";

    /// <inheritdoc/>
    public string RenderField(EntitySchema entity, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(fieldName);
    }

    /// <inheritdoc/>
    public string RenderParameter(string parameterName) => "@" + parameterName;

    /// <inheritdoc/>
    public string RenderCaseInsensitiveLike(string left, string right) => $"UPPER({left}) LIKE UPPER({right})";

    /// <inheritdoc/>
    /// <remarks>
    /// SQLite has no decimal storage class, so EF maps a decimal field to a <c>TEXT</c> column and every
    /// comparison over it is a string comparison unless both operands are cast: <c>price &gt; 100</c>
    /// matches a price of <c>12.34</c>, and <c>price != 100</c> matches a price that <em>is</em> 100.
    /// <c>REAL</c> is the widest numeric type SQLite offers and is what its own <c>CAST</c> documentation
    /// names for numeric comparison of text.
    /// <para>
    /// Two costs, both accepted rather than hidden. The cast makes the predicate non-sargable, so a decimal
    /// comparison cannot use an index on that column. And <c>REAL</c> is an IEEE-754 double, so the
    /// comparison is exact only while the value fits 53 bits of mantissa — about 9·10^15 minor units, or
    /// ±90 trillion at two decimal places. Beyond that a comparison may answer on a rounded value. Both are
    /// far better than the current alternative, which answers on the <em>lexical</em> value at every
    /// magnitude; a storage change (a scaled integer, exact and orderable) is the real fix and is a schema
    /// decision this port cannot make.
    /// </para>
    /// </remarks>
    public (string Left, string Right) RenderComparableOperands(string left, string right, CelValueType type) =>
        type == CelValueType.Decimal ? (AsReal(left), AsReal(right)) : (left, right);

    private static string AsReal(string sql) => $"CAST({sql} AS REAL)";
}
