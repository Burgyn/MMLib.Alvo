using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Runs a schema change's transacted work framed by the statements this engine only honours <b>outside</b> a
/// transaction — the one place both the migrator and the atomic runtime writer get <see cref="MigrationBatchFraming"/>
/// right.
/// </summary>
/// <remarks>
/// It exists as a type of its own because there were two callers and only one had the framing: the dashboard's and
/// the Management API's apply goes through <c>EfCoreRuntimeSchemaWriter</c>, which ran the plan's SQL in its own
/// transaction with SQLite's foreign keys still enforced. A table rebuild on that path then either failed
/// (<c>FOREIGN KEY constraint failed</c>, a restricted reference to the rebuilt parent) or, over a cascading one,
/// deleted the children — the Dev-7 measurement, reachable again from the runtime path.
/// </remarks>
internal static class MigrationFramingScope
{
    /// <summary>Runs <paramref name="work"/> between the framing's <c>Before</c> and <c>After</c> statements.</summary>
    /// <remarks>
    /// <para>
    /// The framing exists because SQLite's <c>PRAGMA foreign_keys</c> is a no-op inside a transaction, which
    /// made a table rebuild cascade away the child rows of every <c>onDelete: "cascade"</c> reference to the
    /// rebuilt table — see <see cref="MigrationBatchFraming"/> for the measurement. Nothing about that is
    /// specific to either caller, so the statements come from the dialect and this method only decides
    /// <em>where</em> they run.
    /// </para>
    /// <para>
    /// <b><c>After</c> always runs, but it is <em>not</em> a bare <c>finally</c>, and the difference is which
    /// exception a caller sees.</b> A suspension that was never restored would ride the connection into whatever
    /// a pool handed it to next, which is a constraint quietly not being enforced — so the restore is attempted
    /// on both paths. But in a <c>finally</c> a throwing restore <em>replaces</em> the failure that caused the
    /// unwind, and "restoring a pragma failed" is a far worse answer than the DDL error that actually broke the
    /// migration. So the two paths are split: on the failing path the restore is best-effort and the original
    /// exception is what propagates; on the succeeding path a failed restore is the only failure there is, and
    /// it propagates, because leaving enforcement off after a migration that otherwise worked is exactly the
    /// state nobody would notice.
    /// </para>
    /// <para>
    /// <b>Neither restore takes the caller's token, and that is the whole point of the pair.</b> The token is
    /// already cancelled on exactly the path the restore exists for: cancel a migration and
    /// <c>ExecuteAsync</c> throws, the catch runs, and a restore passed that same token would abort before
    /// <c>PRAGMA foreign_keys = 1</c> ever reached the connection. The connection then goes back to the pool
    /// with enforcement off and the next borrower writes children against no foreign key at all — the
    /// suspension riding the connection into whatever a pool handed it to next, which the paragraph above
    /// names as the thing this method is here to prevent. Honouring the token would have made the restore
    /// skip itself precisely when it was needed. The cancellation is not swallowed by this: it is
    /// <c>ExecuteAsync</c>'s <see cref="OperationCanceledException"/> that propagates, unchanged, once the
    /// pragma is back.
    /// </para>
    /// </remarks>
    /// <param name="connection">The connection the work runs on; the framing is a property of the connection.</param>
    /// <param name="framing">The dialect's framing.</param>
    /// <param name="work">The transacted work — the plan's SQL, and whatever else must commit with it.</param>
    /// <param name="ct">Cancels the <c>Before</c> statements and the work, never the restore.</param>
    /// <returns>What <paramref name="work"/> answered.</returns>
    public static async Task<T> RunAsync<T>(
        DbConnection connection, MigrationBatchFraming framing, Func<Task<T>> work, CancellationToken ct)
    {
        await RelationalSqlBatch.ExecuteUntransactedAsync(connection, framing.Before, ct).ConfigureAwait(false);

        T result;
        try
        {
            result = await work().ConfigureAwait(false);
        }
        catch
        {
            await TryRestoreAsync(connection, framing).ConfigureAwait(false);
            throw;
        }

        await RelationalSqlBatch.ExecuteUntransactedAsync(connection, framing.After, CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Restores the framing while an exception is already on its way out, swallowing a <em>second</em> failure so
    /// it cannot displace the first.
    /// </summary>
    /// <remarks>
    /// Only the two families a restore can realistically raise are swallowed — the provider's own
    /// (<see cref="DbException"/>, a connection the failed batch left unusable) and
    /// <see cref="InvalidOperationException"/> (a connection that can no longer be opened). Anything else still
    /// propagates, because a restore failing for an unrelated reason is not something to hide. A cancellation
    /// cannot arrive from here at all — see <see cref="RunAsync"/> for why the restore does not
    /// take the caller's token — so there is no cancellation arm to write.
    /// </remarks>
    private static async Task TryRestoreAsync(DbConnection connection, MigrationBatchFraming framing)
    {
        try
        {
            await RelationalSqlBatch.ExecuteUntransactedAsync(connection, framing.After, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception secondary) when (secondary is DbException or InvalidOperationException)
        {
        }
    }
}
