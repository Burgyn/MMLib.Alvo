using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// One unit's run, for <c>TransactAsync</c> and for a top-level write's own unit alike: on the unit's flow, a failure mapped
/// as the unit recorded it — a <see cref="TimeoutException"/> once the deadline passed, the recorded failure behind a refusal
/// that only wraps it — and a commit when the work returned.
/// </summary>
internal static class UnitRun
{
    internal static async Task<T> CommittedAsync<T>(WriteUnit unit, Func<Task<T>> work)
    {
        using var flow = unit.EnterFlow();
        var result = await MappedAsync(unit, work).ConfigureAwait(false);
        await unit.CommitAsync().ConfigureAwait(false);

        return result;
    }

    private static async Task<T> MappedAsync<T>(WriteUnit unit, Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
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
