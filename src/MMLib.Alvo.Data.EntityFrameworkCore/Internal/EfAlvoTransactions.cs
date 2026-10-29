using Microsoft.Extensions.DependencyInjection;
using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// <see cref="IAlvoTransactions"/> over the EF data port: open a unit, put it on the flow, run the delegate once, commit —
/// or roll back and rethrow, as a <see cref="TimeoutException"/> whatever the delegate threw once the deadline passed, and
/// as the unit's own recorded failure a refusal that only wraps it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never retried</b> (spec 2026-10-14 §4.5): the delegate runs once. SQLite's writer contention is absorbed before it
/// runs — at the unit's single <c>BEGIN</c>, by this process's write gate and the provider's busy timeout — and any other
/// failure surfaces to the caller, so a deterministic violation never re-runs host code.
/// </para>
/// <para>
/// The delegate runs inside <see cref="WriteUnit.EnterFlow"/>, the one way a unit gets onto the flow (Ruling 48): every
/// call through the unit joins it only from that flow.
/// </para>
/// </remarks>
/// <param name="data">The data port the unit's calls run through.</param>
/// <param name="scopes">Where each unit's DI scope comes from.</param>
internal sealed class EfAlvoTransactions(EfAlvoData data, IServiceScopeFactory scopes) : IAlvoTransactions
{
    /// <inheritdoc/>
    public async Task<T> TransactAsync<T>(
        AlvoContext context,
        Func<IAlvoTransaction, CancellationToken, Task<T>> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        AlvoFlow.RefuseInsideUnit("IAlvoTransactions.TransactAsync");
        var settings = AlvoTransactionOptions.Validated(options, context);

        await using var unit = await data.OpenUnitAsync(context, settings, scopes, cancellationToken).ConfigureAwait(false);
        using var flow = unit.EnterFlow();
        var result = await WorkedAsync(unit, work, context).ConfigureAwait(false);
        await unit.CommitAsync().ConfigureAwait(false);

        return result;
    }

    private async Task<T> WorkedAsync<T>(WriteUnit unit, Func<IAlvoTransaction, CancellationToken, Task<T>> work, AlvoContext context)
    {
        try
        {
            return await work(new EfAlvoTransaction(data, unit, context), unit.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (unit.TimedOut && failure is not TimeoutException)
        {
            throw unit.TimedOutBecause(failure);
        }
        catch (Exception failure) when (unit.RecordedFailureBehind(failure) is { } recorded)
        {
            ExceptionDispatchInfo.Throw(recorded);
            throw;
        }
    }
}
