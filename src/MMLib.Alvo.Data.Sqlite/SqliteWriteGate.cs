using Microsoft.Data.Sqlite;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>
/// One registration's view of a SQLite database's write line: the database's shared
/// <see cref="SerialRelationalWriteGate"/>, waited at for no longer than the connection string's busy timeout.
/// </summary>
/// <remarks>
/// <para>
/// <b>A wait that outlasts the timeout fails as the engine itself would have</b> — <c>SQLITE_BUSY</c>, "database is
/// locked" — because that is what a writer met before the gate, after <c>Microsoft.Data.Sqlite</c>'s retry ran out the
/// same <c>Default Timeout</c>. The data path's replay loop and the API already answer that error, so overload stays a
/// fast failure, not a hang. The caller's cancellation still wins over the timeout.
/// </para>
/// <para>
/// <b>A zero busy timeout is an unbounded wait</b>, as it is for the engine's own lock wait. Alvo answers for a delivery
/// without a cancellation token — a handler that finished is answered even while the host stops — so with
/// <c>Default Timeout=0</c> a writer wedged ahead in line can hold a delivery's answer, and with it the delivery
/// dispatcher's shutdown, until the host's shutdown timeout ends the process. The provider's default of 30 s bounds that
/// wait.
/// </para>
/// </remarks>
/// <param name="line">The database's write line, shared by every registration over it.</param>
/// <param name="wait">How long a writer waits for its turn; <see cref="Timeout.InfiniteTimeSpan"/> for no bound.</param>
internal sealed class SqliteWriteGate(SerialRelationalWriteGate line, TimeSpan wait) : IRelationalWriteGate
{
    private const int SqliteBusy = 5;

    /// <summary>Gets the database's shared write line.</summary>
    internal SerialRelationalWriteGate Line => line;

    /// <summary>Gets how long a writer waits for its turn before it fails as busy.</summary>
    internal TimeSpan Wait => wait;

    /// <inheritdoc/>
    /// <exception cref="SqliteException">The turn did not come within <see cref="Wait"/>: <c>SQLITE_BUSY</c>.</exception>
    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken) =>
        await line.TryEnterAsync(wait, cancellationToken).ConfigureAwait(false) ?? throw Busy();

    private static SqliteException Busy() => new("SQLite Error 5: 'database is locked'.", SqliteBusy, SqliteBusy);
}
