using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Creates a fresh, unopened ADO.NET <see cref="DbConnection"/> per call, so callers that need
/// genuinely concurrent work (runtime schema changes by independent clients) each own their own
/// connection and transaction instead of serializing on one shared connection.
/// </summary>
internal sealed class RelationalConnectionFactory(Func<DbConnection> create)
{
    private readonly Func<DbConnection> _create = create ?? throw new ArgumentNullException(nameof(create));

    /// <summary>Creates a new, unopened connection the caller owns and must dispose.</summary>
    public DbConnection Create() => _create();
}
