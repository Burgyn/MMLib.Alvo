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
        store.ChangeTracker.Clear();
        try
        {
            return await work().ConfigureAwait(false);
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
