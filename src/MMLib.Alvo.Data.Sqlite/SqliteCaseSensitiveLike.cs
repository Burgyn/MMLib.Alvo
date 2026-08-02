using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>
/// Makes SQLite's <c>LIKE</c> case-sensitive, on every connection this driver opens, by running
/// <c>PRAGMA case_sensitive_like = ON</c>.
/// </summary>
/// <remarks>
/// <para>
/// SQLite's <c>LIKE</c> is ASCII-case-<b>in</b>sensitive by default — <c>'ACME' LIKE 'acme'</c> answers
/// <c>1</c> — while PostgreSQL's is case-sensitive and answers <c>f</c>. So one descriptor, one caller
/// filter and one data set returned different rows per engine, silently, on the channel a caller controls
/// per request: §0 principle 3. Standard SQL's <c>LIKE</c> is case-sensitive, and that is the meaning
/// <see cref="Data.AlvoFilterOperator.Like"/> documents, so SQLite is the engine that has to move.
/// </para>
/// <para>
/// <b>A pragma rather than a rendering change.</b> The alternative was to make case-folding a dialect
/// decision and have SQLite render something case-sensitive — but the only case-sensitive matching SQLite
/// can express in an operator is <c>GLOB</c>, whose wildcards are <c>*</c>/<c>?</c>/<c>[…]</c>, so adopting
/// it means translating and escaping a caller-supplied pattern into a second wildcard language. Rewriting
/// caller text is exactly the class of work this data path refuses to do elsewhere.
/// </para>
/// <para>
/// The pragma does <b>not</b> disturb the <c>ilike</c> emulation: <c>UPPER(a) LIKE UPPER(b)</c> folds both
/// operands explicitly before the comparison, so a case-sensitive <c>LIKE</c> over two upper-cased operands
/// is exactly the ASCII-case-insensitive match <c>ilike</c> guarantees.
/// </para>
/// <para>
/// It is applied on <b>connection open</b> rather than through the connection string, because
/// <c>case_sensitive_like</c> has no connection-string keyword and the setting is per connection — and
/// Microsoft.Data.Sqlite pools connections, so a one-shot setup at registration time would leave later
/// connections at the default.
/// </para>
/// </remarks>
internal sealed class SqliteCaseSensitiveLike : DbConnectionInterceptor
{
    private const string Pragma = "PRAGMA case_sensitive_like = ON;";

    /// <inheritdoc/>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Apply(connection);
    }

    /// <inheritdoc/>
    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        command.ExecuteNonQuery();
    }
}
