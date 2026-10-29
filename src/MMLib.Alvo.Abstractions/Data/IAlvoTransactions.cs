namespace MMLib.Alvo.Data;

/// <summary>Opens transaction units: several reads, writes, batches and custom events that commit together or not at all.</summary>
/// <remarks>
/// <para>
/// The delegate receives the unit's <see cref="IAlvoTransaction"/> and a token that ends with the unit's
/// <see cref="AlvoTransactionOptions.Timeout"/>. Returning commits; throwing rolls back and rethrows. A unit is
/// <b>never retried</b>: the delegate runs once, so a deterministic failure surfaces at once.
/// </para>
/// <para>
/// While the unit is open, every other data-port call on its flow — <see cref="IAlvoData"/>, <see cref="IAlvoSession"/>,
/// <c>IAlvoEvents.PublishAsync</c>, a nested <c>TransactAsync</c> — throws <see cref="AlvoUnitInProgressException"/>. Make a
/// network call before <c>TransactAsync</c>, never inside it: the unit holds row locks, and on SQLite the database's one
/// write lock, while it runs.
/// </para>
/// <para>
/// Implemented by the EF Core driver; the in-memory reference <c>InMemoryAlvoData</c> does not implement it.
/// </para>
/// </remarks>
public interface IAlvoTransactions
{
    /// <summary>Runs <paramref name="work"/> in one transaction unit as <paramref name="context"/>, and commits what it did.</summary>
    /// <typeparam name="T">What the delegate answers.</typeparam>
    /// <param name="context">The caller every call through the unit runs as.</param>
    /// <param name="work">The unit's work: its data access, and a token cancelled at the unit's deadline.</param>
    /// <param name="options">The unit's options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">A token to cancel the unit; it rolls back.</param>
    /// <returns>What <paramref name="work"/> answered, once the unit committed.</returns>
    /// <exception cref="TimeoutException">The unit ran past its timeout; nothing was committed.</exception>
    /// <exception cref="AlvoUnitInProgressException">A unit is already open on this flow, or the delegate called another data port.</exception>
    /// <exception cref="AlvoChainDepthExceededException">The caller is deeper in one causation chain than the limit allows.</exception>
    Task<T> TransactAsync<T>(
        AlvoContext context,
        Func<IAlvoTransaction, CancellationToken, Task<T>> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="work"/> in one transaction unit as <paramref name="context"/>, and commits what it did.</summary>
    /// <param name="context">The caller every call through the unit runs as.</param>
    /// <param name="work">The unit's work: its data access, and a token cancelled at the unit's deadline.</param>
    /// <param name="options">The unit's options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">A token to cancel the unit; it rolls back.</param>
    /// <returns>A task that completes once the unit committed.</returns>
    /// <exception cref="TimeoutException">The unit ran past its timeout; nothing was committed.</exception>
    /// <exception cref="AlvoUnitInProgressException">A unit is already open on this flow, or the delegate called another data port.</exception>
    Task TransactAsync(
        AlvoContext context,
        Func<IAlvoTransaction, CancellationToken, Task> work,
        AlvoTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return TransactAsync(
            context,
            async (unit, token) =>
            {
                await work(unit, token).ConfigureAwait(false);
                return true;
            },
            options,
            cancellationToken);
    }
}
