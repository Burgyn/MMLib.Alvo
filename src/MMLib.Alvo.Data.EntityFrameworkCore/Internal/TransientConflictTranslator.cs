using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Turns a deadlock the engine broke by failing a write into the data port's retryable
/// <see cref="AlvoTransientConflictException"/> — when, and only when, the driver's own dialect says the failure is one
/// (<see cref="IAlvoSqlDialect.IsDeadlock"/>, issue 443).
/// </summary>
/// <remarks>
/// <para>
/// <b>At the port's boundaries, not around one statement.</b> A deadlock can fail any statement of a write's transaction —
/// an exclusion's advisory lock, the row, a rollup's parent, a counter — so it is mapped where a call leaves the port: each
/// write of <see cref="IAlvoData"/>, each call through a unit, and <c>TransactAsync</c> itself, whose <c>COMMIT</c> can be
/// the statement that fails.
/// </para>
/// <para>
/// <b>The provider's exception wherever it sits in the chain</b>, like <see cref="ConstraintViolationTranslator"/>: EF wraps
/// it in <see cref="DbUpdateException"/> from <c>SaveChanges</c>, and the Npgsql provider's execution strategy wraps a
/// failure it deems transient — a deadlock is one — in an <see cref="InvalidOperationException"/> suggesting a retry. So an
/// idempotent write whose attempt loop ran out on a deadlock answers a transient conflict too, which is what it is. A
/// refusal already answered passes as itself, so a call nested in a call is mapped once, where it failed.
/// </para>
/// <para>
/// Evaluated in the <c>when</c> filter, like <see cref="ConstraintViolationTranslator"/>: a failure that is no deadlock is
/// never caught, and propagates with the provider's own stack.
/// </para>
/// </remarks>
internal static class TransientConflictTranslator
{
    /// <summary>Runs <paramref name="work"/>, answering a deadlock it raised as a transient conflict.</summary>
    /// <typeparam name="T">What the work answers.</typeparam>
    /// <param name="work">The port call.</param>
    /// <param name="dialect">The driver's dialect, which decides what a deadlock is.</param>
    internal static async Task<T> MappedAsync<T>(Func<Task<T>> work, IAlvoSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(dialect);
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception failure) when (Translate(failure, dialect) is { } conflict)
        {
            throw conflict;
        }
    }

    /// <inheritdoc cref="MappedAsync{T}"/>
    internal static async Task MappedAsync(Func<Task> work, IAlvoSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(dialect);
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception failure) when (Translate(failure, dialect) is { } conflict)
        {
            throw conflict;
        }
    }

    /// <summary>The transient conflict <paramref name="failure"/> stands for, or <see langword="null"/> when it is no deadlock.</summary>
    /// <param name="failure">What the call threw.</param>
    /// <param name="dialect">The driver's dialect.</param>
    internal static AlvoTransientConflictException? Translate(Exception failure, IAlvoSqlDialect dialect) =>
        Provider(failure) is { } provider && dialect.IsDeadlock(provider) ? new AlvoTransientConflictException(provider) : null;

    private static DbException? Provider(Exception failure) => failure switch
    {
        AlvoTransientConflictException => null,
        DbException provider => provider,
        { InnerException: { } inner } => Provider(inner),
        _ => null,
    };
}
