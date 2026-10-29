using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// One call joined to a unit's transaction under a savepoint of its own: committing it releases the savepoint, and ending it
/// any other way — an exception, or a return without committing — rolls back to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every call gets one (spec 2026-10-14 §4.5, §6.1).</b> On PostgreSQL a failed statement aborts the whole
/// transaction (<c>25P02</c>) until a rollback to a savepoint; on SQLite a constraint error rolls back only its statement.
/// A savepoint per call makes "a failed call undoes itself and the unit goes on" true on both. EF's own
/// <c>__EFSavePoint</c> around <c>SaveChanges</c> recovers an insert that fails inside <c>SaveChanges</c>, and nothing
/// else: not the <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> and raw statements of the update and delete paths, and not a
/// statement that fails after a successful <c>SaveChanges</c> — EF has released its savepoint by then (Rulings 29, 31).
/// </para>
/// <para>
/// <b>Committing never commits the unit.</b> The joined EF transaction does not own the unit's <see cref="DbTransaction"/>;
/// <see cref="CommitAsync"/> only releases the savepoint. A rollback to the savepoint that itself fails — SQLite may roll the
/// whole transaction back after an interrupt, a full disk or a busy error — poisons the unit. So does a failure to create
/// the savepoint: the unit's transaction is then in a state nothing can vouch for.
/// </para>
/// <para>
/// <b>Joined only on the unit's own flow.</b> A write inside a unit runs the unit's hook pipeline, which it finds through
/// the flow (<c>AlvoFlow.Unit</c>); a call reaching this unit from a flow that does not carry it — flow suppressed, a
/// host's own queue — would join the unit's transaction and run another pipeline. It is refused instead, and the unit
/// fails.
/// </para>
/// </remarks>
internal sealed class UnitSavepoint : IDbContextTransaction, IInfrastructure<DbTransaction>
{
    private readonly IDbContextTransaction _enlisted;
    private readonly WriteUnit _unit;
    private readonly string _name;
    private bool _settled;

    private UnitSavepoint(IDbContextTransaction enlisted, WriteUnit unit, string name)
    {
        _enlisted = enlisted;
        _unit = unit;
        _name = name;
    }

    /// <inheritdoc/>
    public Guid TransactionId => _enlisted.TransactionId;

    /// <inheritdoc/>
    DbTransaction IInfrastructure<DbTransaction>.Instance => _unit.Transaction;

    /// <summary>Joins <paramref name="db"/> — already on the unit's connection — to the unit's transaction, under a new savepoint.</summary>
    /// <param name="db">A context created on the unit's connection.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="cancellationToken">A token to cancel the join.</param>
    /// <exception cref="InvalidOperationException">The unit is not the one open on this flow; the unit is failed.</exception>
    internal static async Task<UnitSavepoint> JoinAsync(AlvoDataContext db, WriteUnit unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(unit);
        EnsureOnItsFlow(unit);
        var enlisted = await db.Database.UseTransactionAsync(unit.Transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The unit's transaction could not be joined: EF answered no transaction for it.");
        var name = unit.NextSavepoint();
        await SavedAsync(enlisted, unit, name, cancellationToken).ConfigureAwait(false);

        return new UnitSavepoint(enlisted, unit, name);
    }

    /// <summary>Refuses a join from a flow on which <paramref name="unit"/> is not the open unit, and fails the unit.</summary>
    private static void EnsureOnItsFlow(WriteUnit unit)
    {
        if (ReferenceEquals(AlvoFlow.Unit, unit))
        {
            return;
        }

        var refusal = OffItsFlow();
        unit.Poison(refusal);
        throw refusal;
    }

    private static async Task SavedAsync(
        IDbContextTransaction enlisted, WriteUnit unit, string name, CancellationToken cancellationToken)
    {
        try
        {
            await enlisted.CreateSavepointAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is DbException or InvalidOperationException)
        {
            unit.Poison(failure);
            await enlisted.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            await enlisted.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static InvalidOperationException OffItsFlow() => new(
        "A call joined this transaction unit from a flow that does not carry it — with execution-context flow suppressed, or "
        + "from work queued outside the TransactAsync delegate — so it would not run the unit's own hooks. The call was refused "
        + "and the unit will roll back. Fix: make every call through the IAlvoTransaction on the delegate's own flow, and "
        + "await it there.");

    /// <inheritdoc/>
    public void Commit()
    {
        _enlisted.ReleaseSavepoint(_name);
        _settled = true;
    }

    /// <inheritdoc/>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await _enlisted.ReleaseSavepointAsync(_name, cancellationToken).ConfigureAwait(false);
        _settled = true;
    }

    /// <inheritdoc/>
    public void Rollback()
    {
        _enlisted.RollbackToSavepoint(_name);
        _settled = true;
    }

    /// <inheritdoc/>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _enlisted.RollbackToSavepointAsync(_name, cancellationToken).ConfigureAwait(false);
        _settled = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_settled)
        {
            Undo();
        }

        _enlisted.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_settled)
        {
            await UndoAsync().ConfigureAwait(false);
        }

        await _enlisted.DisposeAsync().ConfigureAwait(false);
    }

    private void Undo()
    {
        try
        {
            _enlisted.RollbackToSavepoint(_name);
        }
        catch (DbException failure)
        {
            _unit.Poison(failure);
        }
        catch (InvalidOperationException failure)
        {
            _unit.Poison(failure);
        }
    }

    private async Task UndoAsync()
    {
        try
        {
            await _enlisted.RollbackToSavepointAsync(_name, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException failure)
        {
            _unit.Poison(failure);
        }
        catch (InvalidOperationException failure)
        {
            _unit.Poison(failure);
        }
    }
}
