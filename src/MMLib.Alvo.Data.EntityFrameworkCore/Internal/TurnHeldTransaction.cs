using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>A top-level write's own transaction, holding this database's write turn until after it has ended.</summary>
/// <remarks>
/// Exactly what every write site spelled out before the seam — the turn taken, then <c>BEGIN</c>, and the turn released
/// after the transaction commits or rolls back, so the engine's lock is gone before the next writer is let in — kept as
/// one object so the seam can hand a site either this or a unit's savepoint.
/// </remarks>
/// <param name="transaction">The write's transaction.</param>
/// <param name="turn">The write turn taken before it began.</param>
internal sealed class TurnHeldTransaction(IDbContextTransaction transaction, IDisposable turn)
    : IDbContextTransaction, IInfrastructure<DbTransaction>
{
    /// <inheritdoc/>
    public Guid TransactionId => transaction.TransactionId;

    /// <inheritdoc/>
    DbTransaction IInfrastructure<DbTransaction>.Instance => transaction.GetDbTransaction();

    /// <inheritdoc/>
    public void Commit() => transaction.Commit();

    /// <inheritdoc/>
    public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);

    /// <inheritdoc/>
    public void Rollback() => transaction.Rollback();

    /// <inheritdoc/>
    public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        transaction.Dispose();
        turn.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync().ConfigureAwait(false);
        turn.Dispose();
    }
}
