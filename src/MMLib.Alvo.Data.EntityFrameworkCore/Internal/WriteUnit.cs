using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMLib.Alvo.Rules;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>What one open unit is made of: the context that opened its connection, the connection, the transaction, the write turn and the DI scope.</summary>
/// <param name="Root">The context that opened the connection and began the transaction; disposed when the unit ends.</param>
/// <param name="Connection">The one connection every call of the unit runs on.</param>
/// <param name="Transaction">The one transaction every call of the unit joins.</param>
/// <param name="Turn">This database's write turn, held from the unit's <c>BEGIN</c> to its end.</param>
/// <param name="Scope">The unit's DI scope — not the request's.</param>
internal sealed record WriteUnitParts(
    AlvoDataContext Root, DbConnection Connection, IDbContextTransaction Transaction, IDisposable Turn, AsyncServiceScope Scope);

/// <summary>
/// One transaction unit (spec 2026-10-14 §4.5): one connection and transaction that every call joins under its own
/// savepoint, the write turn held from <c>BEGIN</c> to the end, one DI scope, a deadline, and the first failure that
/// poisons it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One call at a time — per level.</b> A <see cref="DbConnection"/> runs one command at a time, so a second call made
/// while one is running is refused, and poisons the unit: the delegate's result can no longer be the one it meant. A call
/// made <em>by</em> the running call — on its own execution flow, as a hook running inside a write will (HX-09) — is let in
/// under its own savepoint, one at a time among its siblings; each call records itself in an <see cref="AsyncLocal{T}"/>
/// frame for exactly that (Ruling 30).
/// </para>
/// <para>
/// <b>Poisoned means it cannot commit.</b> <see cref="Poison"/> records the first failure; every later call is refused, and
/// <see cref="CommitAsync"/> rolls back and throws that failure — so a delegate that catches it still cannot commit.
/// </para>
/// <para>
/// <b>The commit decision is atomic (Ruling 46).</b> <see cref="CommitAsync"/> takes the gate and reads the failure under
/// one lock: from then on no call is admitted and a later <see cref="Poison"/> is ignored, so a decided commit commits. A
/// call still running at that moment — one the delegate started and did not await — fails the unit: it is cancelled,
/// awaited, and the unit rolls back and throws an <see cref="InvalidOperationException"/> naming it.
/// </para>
/// <para>
/// <b>A decided commit is not cancellable (Ruling 50).</b> The <c>COMMIT</c> runs with <see cref="CancellationToken.None"/>:
/// a caller's cancel landing while it runs could otherwise report as cancelled a transaction the engine committed — the
/// outcome Ruling 47 exists to rule out. The caller's cancel still ends the unit at any point before the decision.
/// </para>
/// <para>
/// <b>The deadline ends the unit, not the delegate.</b> At <see cref="AlvoTransactionOptions.Timeout"/> the unit's token is
/// cancelled and the unit is poisoned with a <see cref="TimeoutException"/>; as soon as no call is running it rolls back
/// and lets go of the connection and the write turn — a delegate that ignores its token no longer holds the database. The
/// deadline runs on the host's <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// <b>Every function passed to <see cref="RunAsync{T}"/> honours its token.</b> The deadline's rollback, a commit that
/// found a call still running and <see cref="DisposeAsync"/> all cancel the unit's calls and then wait for them, so that
/// bound is what keeps a unit from holding the write turn past its end (HX-09's hook budget must keep it true). The
/// token is cancelled only while a call is in flight — a unit that ended cleanly fires no cancellation callback — and a
/// callback that throws never stands between the unit and its rollback.
/// </para>
/// <para>
/// <b>A committed unit reports success (Ruling 47).</b> Once <c>COMMIT</c> succeeded, a failure disposing the transaction
/// or the context is logged as a warning after the write turn is released, not thrown: the data is committed, and a
/// caller that retried on the exception would write it twice.
/// </para>
/// <para>
/// <b>Open until released.</b> <see cref="IsOpen"/> stays true from opening until <see cref="DisposeAsync"/> — the end of
/// <c>TransactAsync</c> — so the flow refuses other data-port calls for the delegate's whole run, even after a deadline
/// rolled the transaction back; work that outlives <c>TransactAsync</c> is not refused.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2213:Disposable fields should be disposed",
    Justification = "SemaphoreSlim holds no handle unless AvailableWaitHandle is read, which this type never does; disposing it would race a deadline still waiting for the unit's turn to roll it back.")]
internal sealed partial class WriteUnit : IAlvoFlowUnit, IAsyncDisposable
{
    private const string SavepointPrefix = "alvo_unit_";

    private static readonly AsyncLocal<CallFrame?> _frame = new();

    private readonly WriteUnitParts _parts;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _onDeadline;
    private readonly SemaphoreSlim _calls = new(1, 1);
    private readonly Lock _gate = new();
    private Exception? _failure;
    private bool _ended;
    private bool _ending;
    private bool _finished;
    private bool _released;
    private int _savepoints;

    /// <summary>Initializes an open unit over <paramref name="parts"/>.</summary>
    /// <param name="parts">The opened connection, the begun transaction, the turn and the scope.</param>
    /// <param name="caller">The caller who opened it.</param>
    /// <param name="provenance">The unit's provenance, which every write inside it reports.</param>
    /// <param name="timeout">How long the unit may run.</param>
    /// <param name="time">The clock the deadline runs on.</param>
    /// <param name="cancellationToken">The caller's token; cancelling it cancels every call of the unit.</param>
    internal WriteUnit(
        WriteUnitParts parts, AlvoContext caller, AlvoProvenance provenance, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        _parts = parts;
        Caller = caller;
        Provenance = provenance;
        _timeout = timeout;
        _deadline = new CancellationTokenSource(timeout, time);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        _onDeadline = _deadline.Token.Register(static state => ((WriteUnit)state!).OnDeadline(), this);
    }

    /// <summary>Gets the connection every call of the unit runs on.</summary>
    internal DbConnection Connection => _parts.Connection;

    /// <summary>Gets the transaction every call of the unit joins.</summary>
    internal DbTransaction Transaction => _parts.Transaction.GetDbTransaction();

    /// <summary>Gets the caller who opened the unit.</summary>
    internal AlvoContext Caller { get; }

    /// <summary>Gets the unit's provenance: the caller's effective one, with a correlation id of the unit's own when it had none.</summary>
    internal AlvoProvenance Provenance { get; }

    /// <summary>Gets the unit's DI scope, shared by everything the unit resolves.</summary>
    internal IServiceProvider Services => _parts.Scope.ServiceProvider;

    /// <summary>Gets the declarative hook pipeline a write inside the unit runs, resolved from the unit's scope.</summary>
    internal IBeforeHookRunner Hooks => Services.GetRequiredService<IBeforeHookRunner>();

    /// <summary>Gets the token every call of the unit runs under: the caller's, ended at the deadline.</summary>
    internal CancellationToken Token => _lifetime.Token;

    /// <summary>Gets a value indicating whether the unit's deadline has passed.</summary>
    internal bool TimedOut => _deadline.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether the unit's transaction has committed or rolled back, and the unit has let go of its
    /// connection and write turn.
    /// </summary>
    internal bool HasEnded
    {
        get
        {
            lock (_gate)
            {
                return _finished;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !_released;
            }
        }
    }

    /// <inheritdoc/>
    public void Poison(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (!_ended)
            {
                _failure ??= failure;
            }
        }
    }

    /// <summary>Makes this unit the one open on the current flow, with its provenance, until the returned scope is disposed.</summary>
    /// <remarks>
    /// The one place the driver puts a unit on the flow (<c>AlvoFlow.EnterUnit</c>): a call joins the unit only from a flow
    /// that carries it (<see cref="UnitSavepoint"/>), and the data port refuses every other call there.
    /// </remarks>
    /// <returns>A scope whose disposal restores what was on the flow before.</returns>
    internal IDisposable EnterFlow() => AlvoFlow.EnterUnit(this, Provenance);

    /// <summary>The next savepoint name of this unit: <c>alvo_unit_1</c>, <c>alvo_unit_2</c>, …</summary>
    internal string NextSavepoint() =>
        SavepointPrefix + Interlocked.Increment(ref _savepoints).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs one call of the unit: alone among its siblings, under the unit's token, refused once the unit failed or ended.
    /// A call made from inside a running call of this unit is a nested call: it waits on its parent's gate, not the unit's.
    /// </summary>
    /// <typeparam name="T">What the call answers.</typeparam>
    /// <param name="call">The call, given the token it must pass to every statement.</param>
    /// <param name="cancellationToken">The call's own token.</param>
    /// <returns>What <paramref name="call"/> answered.</returns>
    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        EnsureUsable();
        var gate = _frame.Value is { } parent && parent.Unit == this ? parent.Children : _calls;
        if (!gate.Wait(0, CancellationToken.None))
        {
            EnsureUsable();
            var refusal = Concurrent();
            Poison(refusal);
            throw refusal;
        }

        try
        {
            EnsureUsable();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _frame.Value = new CallFrame(this);
            return await GuardedAsync(call, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Commits the unit — or, when it was poisoned or a call is still running, rolls it back and throws why.
    /// </summary>
    /// <remarks>Takes no token: once decided, the commit runs to its end (Ruling 50).</remarks>
    internal async Task CommitAsync()
    {
        var (failure, holdsGate) = Decide();
        if (!holdsGate)
        {
            await CancelCallsAsync().ConfigureAwait(false);
            await _calls.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            if (failure is not null)
            {
                await EndAsync(commit: false).ConfigureAwait(false);
                ExceptionDispatchInfo.Throw(failure);
            }

            await EndAsync(commit: true).ConfigureAwait(false);
        }
        finally
        {
            _calls.Release();
        }
    }

    /// <summary>The unit's timeout, as the exception every caller of a timed-out unit sees.</summary>
    /// <param name="cause">What the delegate or the call failed with at the deadline, if anything.</param>
    internal TimeoutException TimedOutBecause(Exception? cause) => new(
        $"The transaction unit ran longer than its Timeout of {_timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, so it "
        + "was cancelled and rolled back; nothing it wrote was committed. Fix: keep the work inside TransactAsync short — make any "
        + "network call before it — or raise AlvoTransactionOptions.Timeout.",
        cause);

    /// <summary>Releases the unit: rolls back what it did not commit, ends its scope, and takes it off the flow.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
        }

        try
        {
            await _onDeadline.DisposeAsync().ConfigureAwait(false);
            if (CallInFlight())
            {
                await CancelCallsAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await RollBackWhenFreeAsync().ConfigureAwait(false);
            }
            finally
            {
                await DisposeScopeAndClocksAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether a call holds the unit's gate now; a racy read, so a missed cancel only falls back to the deadline.</summary>
    private bool CallInFlight()
    {
        lock (_gate)
        {
            return _calls.CurrentCount == 0;
        }
    }

    /// <summary>Cancels the unit's calls; a host callback registered on <see cref="Token"/> that throws is ignored.</summary>
    private async Task CancelCallsAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException)
        {
            return;
        }
    }

    private async Task<T> GuardedAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken token)
    {
        try
        {
            return await call(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancelled) when (TimedOut)
        {
            throw TimedOutBecause(cancelled);
        }
        catch (Exception failure) when (failure is OperationCanceledException or AlvoChainDepthExceededException)
        {
            Poison(failure);
            throw;
        }
    }

    private void EnsureUsable()
    {
        lock (_gate)
        {
            if (_failure is TimeoutException)
            {
                throw TimedOutBecause(null);
            }

            if (_failure is { } failure)
            {
                throw Failed(failure);
            }

            if (_ended || _released)
            {
                throw Ended();
            }
        }
    }

    /// <summary>
    /// The commit decision, under one lock: whether the gate is free, and what the unit fails with — its poison, the end it
    /// already reached, or a call still running. From here on no call is admitted and no poison is recorded.
    /// </summary>
    private (Exception? Failure, bool HoldsGate) Decide()
    {
        lock (_gate)
        {
            var holdsGate = _calls.Wait(0, CancellationToken.None);
            if (_ended || _released)
            {
                return (_failure ?? Ended(), holdsGate);
            }

            _ended = true;
            _failure ??= holdsGate ? null : Unawaited();

            return (_failure, holdsGate);
        }
    }

    private async Task EndAsync(bool commit)
    {
        lock (_gate)
        {
            if (_ending)
            {
                return;
            }

            _ending = true;
            _ended = true;
        }

        var committed = false;
        try
        {
            await (commit ? _parts.Transaction.CommitAsync(CancellationToken.None) : RollBackAsync()).ConfigureAwait(false);
            committed = commit;
        }
        finally
        {
            await ReleaseAfterAsync(committed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the parts; after a successful <c>COMMIT</c>, a failure doing so is logged, not thrown (Ruling 47) — the
    /// write turn is released either way.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Only after a successful COMMIT: the data is committed, so any cleanup failure must not read as the unit's failure.")]
    private async Task ReleaseAfterAsync(bool committed)
    {
        try
        {
            await ReleasePartsAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (committed)
        {
            CleanupFailedAfterCommit(Logger(), failure);
        }
    }

    private ILogger Logger() =>
        _parts.Scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(WriteUnit).FullName!) ?? NullLogger.Instance;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A transaction unit committed, but releasing its transaction or context then failed. The data is committed "
            + "and the write turn released; the unit reports success, so do not retry it.")]
    private static partial void CleanupFailedAfterCommit(ILogger logger, Exception failure);

    /// <summary>Lets go of the transaction, the context and then the write turn — each whatever the one before it threw.</summary>
    private async Task ReleasePartsAsync()
    {
        try
        {
            await _parts.Transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _parts.Root.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseTurn();
            }
        }
    }

    private void ReleaseTurn()
    {
        try
        {
            _parts.Turn.Dispose();
        }
        finally
        {
            lock (_gate)
            {
                _finished = true;
            }
        }
    }

    private async Task RollBackWhenFreeAsync()
    {
        await _calls.WaitAsync().ConfigureAwait(false);
        try
        {
            await EndAsync(commit: false).ConfigureAwait(false);
        }
        finally
        {
            _calls.Release();
        }
    }

    private async Task DisposeScopeAndClocksAsync()
    {
        try
        {
            await _parts.Scope.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
            _deadline.Dispose();
        }
    }

    private async Task RollBackAsync()
    {
        try
        {
            await _parts.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
    }

    private void OnDeadline()
    {
        Poison(TimedOutBecause(null));
        _ = AbandonAsync();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Runs unobserved off the deadline's timer; the unit's failure is already recorded and surfaces from TransactAsync.")]
    private async Task AbandonAsync()
    {
        try
        {
            await RollBackWhenFreeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }
    }

    private static InvalidOperationException Failed(Exception failure) => new(
        $"This transaction unit has already failed with {failure.GetType().Name}, so it will roll back and accepts no more calls. "
        + "Fix: let the failure end the delegate, or make the call in a new TransactAsync.",
        failure);

    private static InvalidOperationException Ended() => new(
        "This transaction unit has ended: its TransactAsync has returned, so it committed or rolled back. Fix: make every call "
        + "through the IAlvoTransaction inside the delegate, and await it there.");

    private static InvalidOperationException Unawaited() => new(
        "A call on this transaction was still running when the TransactAsync delegate returned: it was started and not "
        + "awaited, so the unit cannot commit without it or with it. The unit rolled back; nothing it wrote was committed. "
        + "Fix: await every call inside the delegate before it returns.");

    private static InvalidOperationException Concurrent() => new(
        "Another call on this transaction is still running. A unit runs one call at a time, on one connection — it is now "
        + "failed and will roll back. Fix: await each call before making the next.");

    /// <summary>One running call of a unit, as the calls it makes in turn see it: their gate is its <see cref="Children"/>.</summary>
    /// <remarks>
    /// Set in <see cref="AsyncLocal{T}"/> inside <see cref="RunAsync{T}"/>, so it is visible to the call's own flow and to
    /// nothing the delegate does beside it; the async method's return restores the caller's frame.
    /// </remarks>
    /// <param name="unit">The unit the call belongs to.</param>
    [SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "SemaphoreSlim holds no handle unless AvailableWaitHandle is read, which this type never does; a frame lives for one call.")]
    private sealed class CallFrame(WriteUnit unit)
    {
        internal WriteUnit Unit { get; } = unit;

        internal SemaphoreSlim Children { get; } = new(1, 1);
    }
}
