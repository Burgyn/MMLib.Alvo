using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Makes one identity write its own unit of work on a <c>DbContext</c> that may live far longer than it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the change tracker is emptied on both sides of every write.</b> The context is scoped, and in
/// the dashboard a scope is the Blazor circuit, open for as long as the tab. ASP.NET Core Identity writes
/// through tracked entities, so a write left its row in the tracker: the next write from the same tab was
/// made against that copy — with its old concurrency stamp — and a write that <em>failed</em> left its
/// modified user and deleted role rows behind for every later <c>SaveChanges</c> to re-flush, so one lost
/// race stopped the tab from writing anybody at all until it was reloaded. Emptied before, each write
/// reads the row as it is stored now; emptied after, nothing it did survives it, whether it succeeded or
/// not.
/// </para>
/// <para>
/// <b>And it is one transaction.</b> A single administration call is several saves — a role replacement
/// removes the held roles, may create a role row, then adds the new ones — so without one, a race lost on
/// the last save left the person holding no roles while the operator read that nothing was written. Inside
/// one transaction a lost race rolls back whole, which is what makes <see cref="ChangedElsewhere"/> true.
/// On SQLite the transaction also takes the writer lock for its duration, so two units of work serialize
/// rather than race; on an engine with row locks the stamp check is still what catches the race.
/// </para>
/// <para>
/// The core's guarded decorator already resolves the implementation from a scope per call, which makes
/// this unreachable through the public interface. It is kept because the implementation is also
/// resolvable by key, and a rule that holds only because of how its one caller happens to resolve it is
/// a rule the next caller does not get.
/// </para>
/// </remarks>
internal static class AlvoIdentityUnitOfWork
{
    /// <summary>The code ASP.NET Core Identity reports a failed concurrency stamp check with.</summary>
    internal const string ConcurrencyFailure = nameof(IdentityErrorDescriber.ConcurrencyFailure);

    /// <summary>What an operator reads when their write lost a race to somebody else's.</summary>
    internal const string ChangedElsewhere =
        "This person was changed by somebody else while the change was being saved, so nothing was "
        + "written. Reload to see them as they are now, and make the change again.";

    /// <summary>Runs <paramref name="work"/> against an empty change tracker, and empties it again after.</summary>
    /// <typeparam name="T">What the write answers with.</typeparam>
    /// <param name="store">The identity store the write goes through.</param>
    /// <param name="work">The write.</param>
    /// <returns>Whatever the write answered.</returns>
    /// <exception cref="AlvoPreconditionFailedException">The row was written by somebody else in between.</exception>
    internal static async Task<T> RunAsync<T>(AlvoIdentityDbContext store, Func<Task<T>> work)
    {
        try
        {
            /* Through the execution strategy, because a host may configure a retrying one for its
               identity store, and a retrying strategy refuses a transaction opened outside it. A retry
               reruns the whole unit — from an empty tracker, so from a fresh read. */
            return await store.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                store.ChangeTracker.Clear();
                return await InTransactionAsync(store, work).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException raced)
        {
            /* Identity's own stores turn this into a failed result; a path that saves outside them
               (a role row, say) would surface it raw, and the operator is owed the same sentence. */
            throw new AlvoPreconditionFailedException(ChangedElsewhere, raced);
        }
        finally
        {
            store.ChangeTracker.Clear();
        }
    }

    /// <summary>Runs <paramref name="work"/> in one transaction, committed only when it returns.</summary>
    /// <remarks>
    /// Joins a transaction the context already has rather than nesting one — a caller that opened it owns
    /// the commit. Disposing an uncommitted transaction rolls it back, which is the throw path.
    /// </remarks>
    /// <typeparam name="T">What the write answers with.</typeparam>
    /// <param name="store">The identity store the write goes through.</param>
    /// <param name="work">The write.</param>
    /// <returns>Whatever the write answered.</returns>
    private static async Task<T> InTransactionAsync<T>(AlvoIdentityDbContext store, Func<Task<T>> work)
    {
        if (store.Database.CurrentTransaction is not null)
        {
            return await work().ConfigureAwait(false);
        }

        var transaction = await store.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var answer = await work().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
            return answer;
        }
    }

    /// <summary>Throws the named refusal when Identity reported a lost race; otherwise does nothing.</summary>
    /// <remarks>
    /// <b><see cref="AlvoPreconditionFailedException"/>, not a new type.</b> It is the port family's
    /// existing answer to "the row was written since you read it — re-read and retry", which is exactly
    /// this; the dashboard and the Management API already classify it, and a second exception meaning
    /// the same thing would be a public type every caller has to learn twice.
    /// </remarks>
    /// <param name="result">What Identity said.</param>
    /// <exception cref="AlvoPreconditionFailedException">The write failed its concurrency stamp check.</exception>
    internal static void ThrowIfRaced(IdentityResult result)
    {
        if (result.Errors.Any(error => error.Code == ConcurrencyFailure))
        {
            throw new AlvoPreconditionFailedException(ChangedElsewhere);
        }
    }
}
