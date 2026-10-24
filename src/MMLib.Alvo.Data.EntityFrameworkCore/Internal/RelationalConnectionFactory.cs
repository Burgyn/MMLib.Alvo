using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Creates a fresh, unopened ADO.NET <see cref="DbConnection"/> per call, so callers that need
/// genuinely concurrent work (runtime schema changes by independent clients) each own their own
/// connection and transaction instead of serializing on one shared connection.
/// </summary>
/// <param name="create">Creates one unopened connection.</param>
/// <param name="writeGate">
/// Where the stores' write transactions over this database queue; <see langword="null"/> is the
/// <see cref="NoRelationalWriteGate"/> of an engine that runs concurrent writers itself.
/// </param>
internal sealed class RelationalConnectionFactory(Func<DbConnection> create, IRelationalWriteGate? writeGate = null)
{
    private readonly Func<DbConnection> _create = create ?? throw new ArgumentNullException(nameof(create));

    /// <summary>Gets where a store's write transaction over this database waits for its turn.</summary>
    public IRelationalWriteGate WriteGate { get; } = writeGate ?? NoRelationalWriteGate.Instance;

    /// <summary>Creates a new, unopened connection the caller owns and must dispose.</summary>
    public DbConnection Create() => _create();
}
